#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using HL2RP.V2.Runtime;

namespace HL2RP.V2.Tests.Runtime;

[TestClass]
public sealed class WorldItemReconciliationTests
{
	[TestMethod]
	public async Task TransientFailureIsCoalescedAndConvergesAfterBackoff()
	{
		var clock = new MutableClock( DateTimeOffset.Parse( "2026-07-15T12:00:00Z" ) );
		var boundary = new ScriptedBoundary();
		var item = ItemId.New();
		boundary.Enqueue( item,
			WorldItemBoundaryAttempt.Transient( Error( "spawn false" ) ),
			WorldItemBoundaryAttempt.Applied() );
		var reconciler = new HL2RPWorldItemReconciler( boundary, clock );

		var first = await reconciler.ReconcileCommittedAsync( item );
		var beforeDue = await reconciler.ReconcileDueAsync();
		clock.UtcNow += TimeSpan.FromMilliseconds( 250 );
		var afterDue = await reconciler.ReconcileDueAsync();

		Assert.AreEqual( WorldItemReconciliationDisposition.CommittedPendingReconciliation, first.Disposition );
		Assert.IsEmpty( beforeDue );
		Assert.HasCount( 1, afterDue );
		Assert.AreEqual( WorldItemReconciliationDisposition.Applied, afterDue[0].Disposition );
		Assert.AreEqual( 0, reconciler.PendingCount );
		Assert.AreEqual( 2, boundary.Calls[item] );
	}

	[TestMethod]
	public async Task RepeatedDirtySignalsUseOnePendingItem()
	{
		var clock = new MutableClock( DateTimeOffset.UtcNow );
		var boundary = new ScriptedBoundary();
		var item = ItemId.New();
		boundary.Default = WorldItemBoundaryAttempt.Transient( Error( "network unavailable" ) );
		var reconciler = new HL2RPWorldItemReconciler( boundary, clock );

		_ = await reconciler.ReconcileCommittedAsync( item );
		_ = await reconciler.ReconcileCommittedAsync( item );

		Assert.AreEqual( 1, reconciler.PendingCount );
		Assert.AreEqual( 2, boundary.Calls[item] );
	}

	[TestMethod]
	public async Task StartupFailsReadinessOnPermanentOrTransientBoundaryFailure()
	{
		foreach ( var failure in new[]
		{
			WorldItemBoundaryAttempt.Permanent( new OperationError(
				ErrorCode.ConfigurationInvalid, "missing model" ) ),
			WorldItemBoundaryAttempt.Transient( Error( "spawn false" ) )
		} )
		{
			var boundary = new ScriptedBoundary { Default = failure };
			var reconciler = new HL2RPWorldItemReconciler(
				boundary, new MutableClock( DateTimeOffset.UtcNow ) );
			var result = await reconciler.ReconcileStartupAsync( new[] { ItemId.New() } );
			Assert.IsTrue( result.Failed );
		}
	}

	[TestMethod]
	public async Task DrainAttemptsEveryPendingItemAndClosesAdmission()
	{
		var boundary = new ScriptedBoundary
		{
			Default = WorldItemBoundaryAttempt.Transient( Error( "destroy failed" ) )
		};
		var reconciler = new HL2RPWorldItemReconciler(
			boundary, new MutableClock( DateTimeOffset.UtcNow ) );
		var first = ItemId.New();
		var second = ItemId.New();
		_ = await reconciler.ReconcileCommittedAsync( first );
		_ = await reconciler.ReconcileCommittedAsync( second );

		var drain = await reconciler.DrainAsync();
		var rejected = await reconciler.ReconcileCommittedAsync( ItemId.New() );

		Assert.HasCount( 2, drain.Attempts );
		Assert.HasCount( 2, drain.PendingItems );
		Assert.IsFalse( drain.IsClean );
		Assert.AreEqual( WorldItemReconciliationDisposition.CommittedPendingReconciliation, rejected.Disposition );
		Assert.AreEqual( 0, rejected.Attempt );
	}

	[TestMethod]
	public async Task PermanentPostCommitFailureRemainsUnresolvedForShutdownEvidence()
	{
		var item = ItemId.New();
		var boundary = new ScriptedBoundary
		{
			Default = WorldItemBoundaryAttempt.Permanent( new OperationError(
				ErrorCode.ConfigurationInvalid,
				"missing model" ) )
		};
		var reconciler = new HL2RPWorldItemReconciler(
			boundary,
			new MutableClock( DateTimeOffset.UtcNow ) );

		var committed = await reconciler.ReconcileCommittedAsync( item );
		var due = await reconciler.ReconcileDueAsync();
		var drain = await reconciler.DrainAsync();

		Assert.AreEqual( WorldItemReconciliationDisposition.ConfigurationFailed, committed.Disposition );
		Assert.IsEmpty( due, "Permanent configuration failures must not enter automatic retry." );
		Assert.HasCount( 1, drain.PendingItems );
		Assert.IsFalse( drain.IsClean, "An unresolved desired state must suppress quiesced shutdown evidence." );
	}

	[TestMethod]
	public async Task DrainWaitsForPreviouslyAdmittedReconciliationBeforeSnapshottingPendingWork()
	{
		var boundary = new BlockingBoundary();
		var item = ItemId.New();
		var reconciler = new HL2RPWorldItemReconciler(
			boundary,
			new MutableClock( DateTimeOffset.UtcNow ) );
		var committedTask = Task.Run( async () => await reconciler.ReconcileCommittedAsync( item ) );
		await boundary.Entered.Task;

		var drainTask = reconciler.DrainAsync().AsTask();
		Assert.IsFalse( drainTask.IsCompleted, "Drain returned while admitted world work was still executing." );
		boundary.Release.Set();

		var committed = await committedTask;
		var drain = await drainTask;
		Assert.AreEqual( WorldItemReconciliationDisposition.CommittedPendingReconciliation, committed.Disposition );
		Assert.IsTrue( drain.IsClean );
		Assert.IsEmpty( drain.PendingItems );
		Assert.AreEqual( 2, boundary.Calls );
	}

	private static OperationError Error( string message ) =>
		new( ErrorCode.InternalError, message );

	private sealed class MutableClock : IHexClock
	{
		public MutableClock( DateTimeOffset utcNow ) => UtcNow = utcNow;
		public DateTimeOffset UtcNow { get; set; }
	}

	private sealed class ScriptedBoundary : IWorldItemReconciliationBoundary
	{
		private readonly Dictionary<ItemId, Queue<WorldItemBoundaryAttempt>> _scripts = new();
		public Dictionary<ItemId, int> Calls { get; } = new();
		public WorldItemBoundaryAttempt Default { get; set; } = WorldItemBoundaryAttempt.Applied();

		public void Enqueue( ItemId itemId, params WorldItemBoundaryAttempt[] attempts ) =>
			_scripts[itemId] = new Queue<WorldItemBoundaryAttempt>( attempts );

		public WorldItemBoundaryAttempt ApplyDesiredState( ItemId itemId )
		{
			Calls[itemId] = Calls.GetValueOrDefault( itemId ) + 1;
			return _scripts.TryGetValue( itemId, out var attempts ) && attempts.Count > 0
				? attempts.Dequeue()
				: Default;
		}
	}

	private sealed class BlockingBoundary : IWorldItemReconciliationBoundary
	{
		private int _calls;
		public TaskCompletionSource<bool> Entered { get; } = new(
			TaskCreationOptions.RunContinuationsAsynchronously );
		public ManualResetEventSlim Release { get; } = new( false );
		public int Calls => _calls;

		public WorldItemBoundaryAttempt ApplyDesiredState( ItemId itemId )
		{
			if ( Interlocked.Increment( ref _calls ) == 1 )
			{
				Entered.TrySetResult( true );
				Release.Wait( TimeSpan.FromSeconds( 5 ) );
				return WorldItemBoundaryAttempt.Transient( Error( "publication blocked" ) );
			}
			return WorldItemBoundaryAttempt.Applied();
		}
	}
}
