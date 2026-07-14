#nullable enable

using System;
using System.Collections.Generic;
using HL2RP.V2.Runtime;

namespace HL2RP.V2.Tests.Runtime;

[TestClass]
public sealed class RecoverySnapshotTests
{
	private const string DigestA = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
	private const string DigestB = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

	[TestMethod]
	public void SnapshotFormatsTheExactProductionMarkers()
	{
		var snapshot = new HL2RPRecoverySnapshot( 42, DigestA );

		Assert.AreEqual(
			$"HL2RP_RECOVERY_SNAPSHOT phase=post_restart sequence=42 digest={DigestA}",
			snapshot.Format( HL2RPRecoverySnapshotPhase.PostRestart ) );
		Assert.AreEqual(
			$"HL2RP_RECOVERY_SNAPSHOT phase=pre_shutdown sequence=42 digest={DigestA}",
			snapshot.Format( HL2RPRecoverySnapshotPhase.PreShutdown ) );
	}

	[TestMethod]
	public void SnapshotRejectsValuesThatWouldMakeAcceptanceParsingAmbiguous()
	{
		Assert.ThrowsExactly<ArgumentOutOfRangeException>(
			() => new HL2RPRecoverySnapshot( -1, DigestA ) );
		Assert.ThrowsExactly<ArgumentException>(
			() => new HL2RPRecoverySnapshot( 1, DigestA.ToUpperInvariant() ) );
		Assert.ThrowsExactly<ArgumentException>(
			() => new HL2RPRecoverySnapshot( 1, DigestA[..^1] ) );
		Assert.ThrowsExactly<ArgumentOutOfRangeException>(
			() => new HL2RPRecoverySnapshot( 1, DigestA ).Format( (HL2RPRecoverySnapshotPhase)99 ) );
	}

	[TestMethod]
	public void LifecycleEmitsPostRestartThenOneQuiescedPreShutdownMarker()
	{
		var markers = new List<string>();
		var captures = 0;
		var lifecycle = new HL2RPRecoverySnapshotLifecycle( suppressMarkers: false );

		lifecycle.CompleteInitialization(
			() =>
			{
				captures++;
				return new HL2RPRecoverySnapshot( 7, DigestA );
			},
			markers.Add );
		Assert.IsTrue( lifecycle.CompleteQuiescedShutdown(
			() =>
			{
				captures++;
				return new HL2RPRecoverySnapshot( 9, DigestB );
			},
			markers.Add ) );
		Assert.IsFalse( lifecycle.CompleteQuiescedShutdown(
			() => throw new InvalidOperationException( "A second shutdown capture must not run." ),
			markers.Add ) );

		Assert.AreEqual( 2, captures );
		Assert.HasCount( 2, markers );
		StringAssert.Contains( markers[0], "phase=post_restart sequence=7" );
		StringAssert.Contains( markers[1], "phase=pre_shutdown sequence=9" );
	}

	[TestMethod]
	public void FailedOrPartialInitializationCannotProducePreShutdownEvidence()
	{
		var markers = new List<string>();
		var partial = new HL2RPRecoverySnapshotLifecycle( suppressMarkers: false );
		Assert.IsFalse( partial.CompleteQuiescedShutdown(
			() => throw new InvalidOperationException( "Partial initialization must not capture state." ),
			markers.Add ) );

		var failed = new HL2RPRecoverySnapshotLifecycle( suppressMarkers: false );
		Assert.ThrowsExactly<InvalidOperationException>( () => failed.CompleteInitialization(
			() => new HL2RPRecoverySnapshot( 5, DigestA ),
			_ => throw new InvalidOperationException( "Log write failed." ) ) );
		Assert.IsFalse( failed.CompleteQuiescedShutdown(
			() => throw new InvalidOperationException( "Failed initialization must not capture shutdown state." ),
			markers.Add ) );
		Assert.IsEmpty( markers );
	}

	[TestMethod]
	public void SyntheticVerificationLifecycleNeverCapturesOrEmitsProductionMarkers()
	{
		var lifecycle = new HL2RPRecoverySnapshotLifecycle( suppressMarkers: true );
		var captures = 0;
		var markers = new List<string>();

		lifecycle.CompleteInitialization(
			() =>
			{
				captures++;
				return new HL2RPRecoverySnapshot( 1, DigestA );
			},
			markers.Add );
		Assert.IsFalse( lifecycle.CompleteQuiescedShutdown(
			() =>
			{
				captures++;
				return new HL2RPRecoverySnapshot( 1, DigestA );
			},
			markers.Add ) );

		Assert.AreEqual( 0, captures );
		Assert.IsEmpty( markers );
	}
}
