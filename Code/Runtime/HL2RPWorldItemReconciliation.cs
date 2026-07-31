#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;

namespace HL2RP.V2.Runtime;

public enum WorldItemBoundaryOutcome
{
	Applied = 0,
	TransientFailure = 1,
	PermanentFailure = 2
}

public enum WorldItemReconciliationDisposition
{
	Applied = 0,
	CommittedPendingReconciliation = 1,
	ConfigurationFailed = 2
}

public sealed record WorldItemBoundaryAttempt(
	WorldItemBoundaryOutcome Outcome,
	OperationError? Error = null )
{
	public static WorldItemBoundaryAttempt Applied() => new( WorldItemBoundaryOutcome.Applied );
	public static WorldItemBoundaryAttempt Transient( OperationError error ) =>
		new( WorldItemBoundaryOutcome.TransientFailure, error ?? throw new ArgumentNullException( nameof(error) ) );
	public static WorldItemBoundaryAttempt Permanent( OperationError error ) =>
		new( WorldItemBoundaryOutcome.PermanentFailure, error ?? throw new ArgumentNullException( nameof(error) ) );
}

public sealed record WorldItemReconciliationReceipt(
	ItemId ItemId,
	WorldItemReconciliationDisposition Disposition,
	int Attempt,
	OperationError? Error );

public sealed record WorldItemReconciliationDrainResult(
	IReadOnlyList<WorldItemReconciliationReceipt> Attempts,
	IReadOnlyList<ItemId> PendingItems )
{
	public bool IsClean => PendingItems.Count == 0 &&
		Attempts.All( attempt => attempt.Disposition != WorldItemReconciliationDisposition.ConfigurationFailed );
}

public interface IWorldItemReconciliationBoundary
{
	WorldItemBoundaryAttempt ApplyDesiredState( ItemId itemId );
}

/// <summary>
/// Coalesces durable world-item desired-state reconciliation. No background task
/// is created: the host command, maintenance, startup, and shutdown owners await
/// the single-flight gate explicitly.
/// </summary>
public sealed class HL2RPWorldItemReconciler
{
	private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds( 250 );
	private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds( 30 );
	private readonly object _sync = new();
	private readonly SemaphoreSlim _singleFlight = new( 1, 1 );
	private readonly SemaphoreSlim _admissionQuiesced = new( 0, 1 );
	private readonly Dictionary<ItemId, PendingReconciliation> _pending = new();
	private readonly HashSet<ItemId> _permanentFailures = new();
	private readonly IWorldItemReconciliationBoundary _boundary;
	private readonly IHexClock _clock;
	private int _admittedOperations;
	private bool _admissionSignaled;
	private bool _accepting = true;

	public HL2RPWorldItemReconciler(
		IWorldItemReconciliationBoundary boundary,
		IHexClock clock )
	{
		_boundary = boundary ?? throw new ArgumentNullException( nameof(boundary) );
		_clock = clock ?? throw new ArgumentNullException( nameof(clock) );
	}

	public int PendingCount
	{
		get { lock ( _sync ) return _pending.Keys.Concat( _permanentFailures ).Distinct().Count(); }
	}

	public async ValueTask<OperationResult> ReconcileStartupAsync(
		IEnumerable<ItemId> itemIds,
		CancellationToken cancellationToken = default )
	{
		ArgumentNullException.ThrowIfNull( itemIds );
		if ( !TryReserveAdmission() )
			return OperationResult.Failure(
				ErrorCode.Conflict,
				"World-item startup reconciliation is draining." );
		try
		{
			await _singleFlight.WaitAsync( cancellationToken );
			try
			{
				foreach ( var itemId in itemIds.Distinct().OrderBy( value => value.Value ) )
				{
					var receipt = Apply( itemId );
					if ( receipt.Disposition != WorldItemReconciliationDisposition.Applied )
						return OperationResult.Failure(
							receipt.Error?.Code ?? ErrorCode.InternalError,
							$"Startup world-item reconciliation failed for '{itemId.Value:D}': " +
							(receipt.Error?.Message ?? "unknown boundary failure") );
				}
				return OperationResult.Success();
			}
			finally { _singleFlight.Release(); }
		}
		finally { CompleteAdmission(); }
	}

	public async ValueTask<WorldItemReconciliationReceipt> ReconcileCommittedAsync(
		ItemId itemId,
		CancellationToken cancellationToken = default )
	{
		if ( !TryReserveAdmission() )
			return new WorldItemReconciliationReceipt(
				itemId,
				WorldItemReconciliationDisposition.CommittedPendingReconciliation,
				0,
				new OperationError( ErrorCode.Conflict, "World-item reconciliation is draining." ) );
		try
		{
			await _singleFlight.WaitAsync( cancellationToken );
			try { return Apply( itemId ); }
			finally { _singleFlight.Release(); }
		}
		finally { CompleteAdmission(); }
	}

	public async ValueTask<IReadOnlyList<WorldItemReconciliationReceipt>> ReconcileDueAsync(
		CancellationToken cancellationToken = default )
	{
		ItemId[] due;
		lock ( _sync )
		{
			if ( !_accepting ) return Array.Empty<WorldItemReconciliationReceipt>();
			var now = _clock.UtcNow;
			due = _pending.Values.Where( value => value.NextAttemptAtUtc <= now )
				.Select( value => value.ItemId ).OrderBy( value => value.Value ).ToArray();
			if ( due.Length > 0 ) _admittedOperations++;
		}
		if ( due.Length == 0 ) return Array.Empty<WorldItemReconciliationReceipt>();

		try
		{
			await _singleFlight.WaitAsync( cancellationToken );
			try { return due.Select( Apply ).ToArray(); }
			finally { _singleFlight.Release(); }
		}
		finally { CompleteAdmission(); }
	}

	public async ValueTask<WorldItemReconciliationDrainResult> DrainAsync(
		CancellationToken cancellationToken = default )
	{
		bool waitForAdmission;
		lock ( _sync )
		{
			_accepting = false;
			waitForAdmission = _admittedOperations > 0;
		}
		if ( waitForAdmission ) await _admissionQuiesced.WaitAsync( cancellationToken );
		await _singleFlight.WaitAsync( cancellationToken );
		try
		{
			ItemId[] pending;
			lock ( _sync ) pending = _pending.Keys.Concat( _permanentFailures )
				.Distinct().OrderBy( value => value.Value ).ToArray();
			var attempts = pending.Select( Apply ).ToArray();
			lock ( _sync ) pending = _pending.Keys.Concat( _permanentFailures )
				.Distinct().OrderBy( value => value.Value ).ToArray();
			return new WorldItemReconciliationDrainResult( attempts, pending );
		}
		finally { _singleFlight.Release(); }
	}

	private bool TryReserveAdmission()
	{
		lock ( _sync )
		{
			if ( !_accepting ) return false;
			_admittedOperations++;
			return true;
		}
	}

	private void CompleteAdmission()
	{
		lock ( _sync )
		{
			if ( _admittedOperations <= 0 )
				throw new InvalidOperationException( "World-item reconciliation admission underflow." );
			_admittedOperations--;
			if ( !_accepting && _admittedOperations == 0 && !_admissionSignaled )
			{
				_admissionSignaled = true;
				_admissionQuiesced.Release();
			}
		}
	}

	private WorldItemReconciliationReceipt Apply( ItemId itemId )
	{
		var attempt = 1;
		lock ( _sync )
			if ( _pending.TryGetValue( itemId, out var existing ) ) attempt = checked(existing.Attempt + 1);

		WorldItemBoundaryAttempt boundary;
		try { boundary = _boundary.ApplyDesiredState( itemId ); }
		catch ( Exception exception )
		{
			boundary = WorldItemBoundaryAttempt.Transient( new OperationError(
				ErrorCode.InternalError,
				$"World-item boundary threw: {exception.Message}" ) );
		}

		lock ( _sync )
		{
			switch ( boundary.Outcome )
			{
				case WorldItemBoundaryOutcome.Applied:
					_pending.Remove( itemId );
					_permanentFailures.Remove( itemId );
					return new WorldItemReconciliationReceipt(
						itemId, WorldItemReconciliationDisposition.Applied, attempt, null );
				case WorldItemBoundaryOutcome.PermanentFailure:
					_pending.Remove( itemId );
					_permanentFailures.Add( itemId );
					return new WorldItemReconciliationReceipt(
						itemId, WorldItemReconciliationDisposition.ConfigurationFailed,
						attempt, boundary.Error );
				case WorldItemBoundaryOutcome.TransientFailure:
					_permanentFailures.Remove( itemId );
					var exponent = Math.Min( attempt - 1, 16 );
					var milliseconds = Math.Min(
						InitialRetryDelay.TotalMilliseconds * Math.Pow( 2, exponent ),
						MaximumRetryDelay.TotalMilliseconds );
					_pending[itemId] = new PendingReconciliation(
						itemId,
						attempt,
						_clock.UtcNow + TimeSpan.FromMilliseconds( milliseconds ) );
					return new WorldItemReconciliationReceipt(
						itemId,
						WorldItemReconciliationDisposition.CommittedPendingReconciliation,
						attempt,
						boundary.Error );
				default:
					throw new ArgumentOutOfRangeException( nameof(boundary.Outcome) );
			}
		}
	}

	private sealed record PendingReconciliation(
		ItemId ItemId,
		int Attempt,
		DateTimeOffset NextAttemptAtUtc );
}
