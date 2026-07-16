#nullable enable

using System;
using Hexagon.V2.Kernel;
using Hexagon.V2.Persistence;

namespace HL2RP.V2.Runtime;

public enum HL2RPRecoverySnapshotPhase
{
	PostRestart = 0,
	PreShutdown
}

/// <summary>
/// Source-bound recovery evidence captured from the validated committed view.
/// The wire representation is intentionally strict so acceptance tooling can
/// reject malformed or ambiguous server-log markers.
/// </summary>
public sealed record HL2RPRecoverySnapshot
{
	public const string Marker = "HL2RP_RECOVERY_SNAPSHOT";

	public HL2RPRecoverySnapshot( long sequence, string digest )
	{
		if ( sequence < 0 ) throw new ArgumentOutOfRangeException( nameof(sequence) );
		if ( !IsLowercaseSha256( digest ) )
			throw new ArgumentException( "Recovery digest must be exactly 64 lowercase hexadecimal characters.", nameof(digest) );
		Sequence = sequence;
		Digest = digest;
	}

	public long Sequence { get; }
	public string Digest { get; }

	public string Format( HL2RPRecoverySnapshotPhase phase ) =>
		$"{Marker} phase={PhaseName( phase )} sequence={Sequence} digest={Digest}";

	private static string PhaseName( HL2RPRecoverySnapshotPhase phase ) => phase switch
	{
		HL2RPRecoverySnapshotPhase.PostRestart => "post_restart",
		HL2RPRecoverySnapshotPhase.PreShutdown => "pre_shutdown",
		_ => throw new ArgumentOutOfRangeException( nameof(phase) )
	};

	private static bool IsLowercaseSha256( string? value )
	{
		if ( value is null || value.Length != 64 ) return false;
		foreach ( var character in value )
			if ( character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f') ) return false;
		return true;
	}
}

/// <summary>
/// Enforces the two lifecycle boundaries for production recovery evidence.
/// Failed or partial initialization cannot emit a shutdown snapshot, and
/// synthetic verification probes never emit production snapshot markers.
/// </summary>
public sealed class HL2RPRecoverySnapshotLifecycle
{
	private readonly bool _suppressMarkers;
	private bool _initializationCompleted;
	private bool _shutdownSnapshotCompleted;

	public HL2RPRecoverySnapshotLifecycle( bool suppressMarkers ) =>
		_suppressMarkers = suppressMarkers;

	public void CompleteInitialization(
		Func<HL2RPRecoverySnapshot> capture,
		Action<string> markerSink )
	{
		ArgumentNullException.ThrowIfNull( capture );
		ArgumentNullException.ThrowIfNull( markerSink );
		if ( _initializationCompleted )
			throw new InvalidOperationException( "Recovery snapshot initialization was already completed." );
		if ( !_suppressMarkers )
			markerSink( capture().Format( HL2RPRecoverySnapshotPhase.PostRestart ) );
		_initializationCompleted = true;
	}

	public bool CompleteQuiescedShutdown(
		Func<HL2RPRecoverySnapshot> capture,
		Action<string> markerSink )
	{
		ArgumentNullException.ThrowIfNull( capture );
		ArgumentNullException.ThrowIfNull( markerSink );
		if ( !_initializationCompleted || _shutdownSnapshotCompleted ) return false;
		if ( !_suppressMarkers )
			markerSink( capture().Format( HL2RPRecoverySnapshotPhase.PreShutdown ) );
		_shutdownSnapshotCompleted = true;
		return !_suppressMarkers;
	}

	public OperationResult CompleteAfterPersistence(
		PersistenceShutdownResult persistenceShutdown,
		HL2RPRecoverySnapshot snapshot,
		Action<string> markerSink )
	{
		ArgumentNullException.ThrowIfNull( persistenceShutdown );
		ArgumentNullException.ThrowIfNull( snapshot );
		ArgumentNullException.ThrowIfNull( markerSink );
		if ( !persistenceShutdown.IsClean )
			return OperationResult.Failure(
				ErrorCode.InternalError,
				"Recovery evidence requires a clean persistence shutdown." );
		try
		{
			_ = CompleteQuiescedShutdown( () => snapshot, markerSink );
			return OperationResult.Success();
		}
		catch ( Exception exception )
		{
			return OperationResult.Failure(
				ErrorCode.InternalError,
				$"Recovery evidence could not be emitted: {exception.Message}" );
		}
	}
}
