#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Schema;
using HL2RP.V2.Showcase.Scanner;

namespace HL2RP.V2.Tests.Showcase.Scanner;

[TestClass]
public sealed class ScannerShowcaseTests
{
	[TestMethod]
	public async Task BoundPilotInputIsOrderedRateLimitedCappedAndWrittenBehind()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(300, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var service = fixture.CreateService(environment);
		var entered = await service.EnterAsync(pilot.Actor, scannerId);
		Assert.IsTrue(entered.Succeeded, entered.Error?.Message);
		Assert.AreEqual(entered.Value.Commit.Sequence, entered.Value.CommitSequence);
		Assert.IsTrue(entered.Value.Commit.Documents.Any(value =>
			value.Address == new DocumentAddress(DomainCollections.SceneEntities, DomainKeys.SceneEntity(scannerId))));
		var pilotSession = entered.Value.Session;
		Assert.AreEqual(1, fixture.Body.EnterCount);

		var forged = await service.ApplyInputAsync(pilot.Actor with { ConnectionId = ConnectionId.New() },
			new ScannerInputIntent(pilotSession.SessionId, 1, 1, 0, 0, 0, 0));
		Assert.AreEqual(ErrorCode.Unauthorized, forged.Error!.Code);
		var first = await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(pilotSession.SessionId, 1, 1, 0, 0, 0.5f, -0.5f));
		Assert.IsTrue(first.Succeeded, first.Error?.Message);
		Assert.AreEqual(125f, first.Value.Motion.VelocityX);
		Assert.AreEqual(37.5f, first.Value.Motion.YawRate);
		Assert.AreEqual(240f, first.Value.Motion.MaximumAcceleration);
		Assert.AreEqual(ScannerInputDurability.Pending, first.Value.Durability);
		Assert.IsNull(first.Value.Commit);
		Assert.AreEqual(scannerId, fixture.Limits.LastResolvedScannerId);
		var stale = await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(pilotSession.SessionId, 1, 0, 0, 0, 0, 0));
		Assert.AreEqual(ErrorCode.Unauthorized, stale.Error!.Code);
		environment.Clock.Advance(TimeSpan.FromMilliseconds(49));
		var tooFast = await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(pilotSession.SessionId, 2, 0, 0, 0, 0, 0));
		Assert.AreEqual(ErrorCode.PolicyDenied, tooFast.Error!.Code);
		environment.Clock.Advance(TimeSpan.FromMilliseconds(1));
		var second = await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(pilotSession.SessionId, 2, 0, 1, 0, 0, 0));
		Assert.IsTrue(second.Succeeded, second.Error?.Message);
		Assert.AreEqual(ScannerInputDurability.Pending, second.Value.Durability);
		Assert.AreEqual(0L, environment.ReadScanner(scannerId).LastAcceptedInputSequence,
			"Accepted replay state may lag durable storage by one write-behind window.");
		Assert.AreEqual(1, service.PendingInputWriteCount);
		Assert.AreEqual(1, service.ScheduledInputFlushCount);
		var uncapped = await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(pilotSession.SessionId, 3, 1, 1, 0, 0, 0));
		Assert.AreEqual(ErrorCode.PolicyDenied, uncapped.Error!.Code);

		environment.Provider.FailNextCommit();
		fixture.InputFlushDelay.ReleaseNext();
		await WaitForScheduledInputFlushesAsync(service);
		Assert.AreEqual(0L, environment.ReadScanner(scannerId).LastAcceptedInputSequence);
		Assert.AreEqual(1, service.PendingInputWriteCount,
			"A failed deferred commit must retain the latest accepted sequence.");
		Assert.IsEmpty(fixture.CommitSink.InputReceipts);

		var drained = await service.DrainCleanupAsync();
		Assert.IsTrue(drained.Succeeded);
		Assert.AreEqual(2L, environment.ReadScanner(scannerId).LastAcceptedInputSequence);
		Assert.AreEqual(0, service.PendingInputWriteCount);
		Assert.HasCount(1, fixture.CommitSink.InputReceipts);
		Assert.AreEqual(2L, fixture.CommitSink.InputReceipts[0].Sequence);
		Assert.HasCount(2, fixture.Motion.Applied);
	}

	[TestMethod]
	public async Task TwentyHertzBurstCoalescesToAtMostFourSequenceCommitsPerSecond()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(314, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var service = fixture.CreateService(environment);
		var session = (await service.EnterAsync(pilot.Actor, scannerId)).Value.Session;
		var baselineCommits = environment.Provider.SuccessfulCommitCount;
		long sequence = 0;

		for (var batch = 0; batch < 4; batch++)
		{
			for (var input = 0; input < 5; input++)
			{
				sequence++;
				var accepted = await service.ApplyInputAsync(pilot.Actor,
					new ScannerInputIntent(session.SessionId, sequence, 1, 0, 0, 0, 0));
				Assert.IsTrue(accepted.Succeeded, accepted.Error?.Message);
				environment.Clock.Advance(ScannerPilotService.MinimumInputInterval);
			}

			Assert.AreEqual(1, service.ScheduledInputFlushCount);
			fixture.InputFlushDelay.ReleaseNext();
			await WaitForScheduledInputFlushesAsync(service);
		}

		Assert.AreEqual(4, environment.Provider.SuccessfulCommitCount - baselineCommits);
		Assert.AreEqual(4, fixture.InputFlushDelay.CallCount);
		Assert.AreEqual(20L, environment.ReadScanner(scannerId).LastAcceptedInputSequence);
		CollectionAssert.AreEqual(
			new long[] { 5, 10, 15, 20 },
			fixture.CommitSink.InputReceipts.Select(value => value.Sequence).ToArray());
		Assert.AreEqual(0, service.PendingInputWriteCount);
	}

	[TestMethod]
	public async Task ConcurrentInputsCannotBypassTheImmediateRateLimit()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(317, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var service = fixture.CreateService(environment);
		var session = (await service.EnterAsync(pilot.Actor, scannerId)).Value.Session;
		fixture.Limits.ArmConcurrentResolveRace();

		var first = Task.Run(async () => await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(session.SessionId, 1, 1, 0, 0, 0, 0)));
		Assert.IsTrue(fixture.Limits.FirstRaceResolveEntered.Wait(TimeSpan.FromSeconds(5)),
			"The first input did not reach the coordinated resolution point.");
		var second = Task.Run(async () => await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(session.SessionId, 2, 0, 1, 0, 0, 0)));
		try
		{
			Assert.IsTrue(fixture.Motion.FirstApplyObserved.Wait(TimeSpan.FromSeconds(5)),
				"The first input was not accepted after both callers captured the same session state.");
		}
		finally
		{
			fixture.Limits.ReleaseSecondRaceResolve();
		}

		var firstResult = await first;
		var secondResult = await second;
		Assert.IsTrue(firstResult.Succeeded, firstResult.Error?.Message);
		Assert.AreEqual(ErrorCode.PolicyDenied, secondResult.Error!.Code);
		Assert.AreEqual(1, fixture.Motion.ApplyCount);
		Assert.AreEqual(1L, service.ActiveSessions.Single().LastAcceptedSequence);
		Assert.IsTrue((await service.DrainCleanupAsync()).Succeeded);
	}

	[TestMethod]
	public async Task StaleFlushGenerationCannotClearNewerAcceptedInput()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(315, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var service = fixture.CreateService(environment);
		var session = (await service.EnterAsync(pilot.Actor, scannerId)).Value.Session;
		var flushEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		environment.Provider.InterleaveNextCommit(async () =>
		{
			flushEntered.TrySetResult();
			await releaseFlush.Task;
		});

		Assert.IsTrue((await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(session.SessionId, 1, 1, 0, 0, 0, 0))).Succeeded);
		fixture.InputFlushDelay.ReleaseNext();
		await flushEntered.Task;
		environment.Clock.Advance(ScannerPilotService.MinimumInputInterval);
		Assert.IsTrue((await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(session.SessionId, 2, 0, 1, 0, 0, 0))).Succeeded);
		releaseFlush.TrySetResult();
		await WaitForAsync(
			() => service.ScheduledInputFlushCount == 1 && fixture.InputFlushDelay.PendingCount == 1,
			"A newer generation did not receive a replacement write-behind window.");

		Assert.AreEqual(1L, environment.ReadScanner(scannerId).LastAcceptedInputSequence,
			"The older flush may persist, but its completion must not clear the newer generation.");
		Assert.AreEqual(1, service.PendingInputWriteCount);
		fixture.InputFlushDelay.ReleaseNext();
		await WaitForScheduledInputFlushesAsync(service);

		Assert.AreEqual(2L, environment.ReadScanner(scannerId).LastAcceptedInputSequence);
		Assert.AreEqual(0, service.PendingInputWriteCount);
		Assert.HasCount(2, fixture.CommitSink.InputReceipts);
		CollectionAssert.AreEqual(
			new long[] { 1L, 2L },
			fixture.CommitSink.InputReceipts.Select(static receipt => receipt.Sequence).ToArray());
	}

	[TestMethod]
	public async Task PhotoAndCleanupTransitionsAbsorbLatestPendingSequence()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(316, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var service = fixture.CreateService(environment);
		var session = (await service.EnterAsync(pilot.Actor, scannerId)).Value.Session;

		Assert.IsTrue((await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(session.SessionId, 1, 1, 0, 0, 0, 0))).Succeeded);
		Assert.IsTrue((await service.TakePhotoAsync(pilot.Actor, session.SessionId)).Succeeded);
		Assert.AreEqual(1L, environment.ReadScanner(scannerId).LastAcceptedInputSequence);
		Assert.AreEqual(0, service.PendingInputWriteCount);
		environment.Clock.Advance(ScannerPilotService.MinimumInputInterval);
		Assert.IsTrue((await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(session.SessionId, 2, 0, 1, 0, 0, 0))).Succeeded);

		Assert.IsTrue((await service.ExitAsync(pilot.Actor, session.SessionId)).Succeeded);
		var drained = await service.DrainCleanupAsync();

		Assert.IsTrue(drained.Succeeded);
		Assert.AreEqual(2L, environment.ReadScanner(scannerId).LastAcceptedInputSequence);
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.AreEqual(0, service.PendingInputWriteCount);
		Assert.IsEmpty(fixture.CommitSink.InputReceipts,
			"Sequences absorbed by another durable transition must not publish duplicate commits.");
		Assert.HasCount(1, fixture.CommitSink.Receipts);
	}

	[TestMethod]
	public async Task SpotlightFlashAndTrustedPhotosRevalidateAndHonorCooldown()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(301, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var service = fixture.CreateService(environment);
		var entered = (await service.EnterAsync(pilot.Actor, scannerId)).Value;
		var session = entered.Session;

		var spotlight = await service.ToggleSpotlightAsync(pilot.Actor, session.SessionId);
		Assert.IsTrue(spotlight.Succeeded && spotlight.Value.Enabled);
		Assert.AreEqual(spotlight.Value.Commit.Sequence, spotlight.Value.CommitSequence);
		Assert.AreEqual(scannerId, spotlight.Value.ScannerId);
		Assert.IsTrue(environment.ReadScanner(scannerId).SpotlightEnabled);
		Assert.AreEqual(1, fixture.Effects.SpotlightCount);
		Assert.IsTrue(service.Flash(pilot.Actor, session.SessionId).Succeeded);
		Assert.AreEqual(1, fixture.Effects.FlashCount);
		var photo = await service.TakePhotoAsync(pilot.Actor, session.SessionId);
		Assert.IsTrue(photo.Succeeded, photo.Error?.Message);
		Assert.AreEqual(pilot.Actor.CharacterId, photo.Value.Metadata.PilotCharacterId);
		Assert.AreEqual(10f, photo.Value.Metadata.PositionX);
		Assert.AreEqual(1, fixture.Effects.PhotoCount);
		var coolingDown = await service.TakePhotoAsync(pilot.Actor, session.SessionId);
		Assert.AreEqual(ErrorCode.PolicyDenied, coolingDown.Error!.Code);
		environment.Clock.Advance(ScannerPilotService.PhotoCooldown);
		Assert.IsTrue((await service.TakePhotoAsync(pilot.Actor, session.SessionId)).Succeeded);
		Assert.HasCount(2, environment.ReadScanner(scannerId).Photos);
	}

	[TestMethod]
	public async Task PostCommitEngineBoundaryFailuresReturnEveryDurableScannerReceipt()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(309, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		fixture.Body.ThrowOnEnter = true;
		fixture.Motion.ThrowOnApply = true;
		fixture.Effects.ThrowOnSpotlight = true;
		fixture.Effects.ThrowOnPhoto = true;
		var service = fixture.CreateService(environment);

		var entered = await service.EnterAsync(pilot.Actor, scannerId);
		Assert.IsTrue(entered.Succeeded, entered.Error?.Message);
		Assert.IsNotNull(entered.Value.BoundaryError);
		var input = await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(entered.Value.Session.SessionId, 1, 1, 0, 0, 0, 0));
		Assert.IsTrue(input.Succeeded, input.Error?.Message);
		Assert.IsNotNull(input.Value.BoundaryError);
		Assert.AreEqual(ScannerInputDurability.Pending, input.Value.Durability);
		Assert.IsNull(input.Value.Commit);
		var spotlight = await service.ToggleSpotlightAsync(pilot.Actor, entered.Value.Session.SessionId);
		Assert.IsTrue(spotlight.Succeeded, spotlight.Error?.Message);
		Assert.IsNotNull(spotlight.Value.BoundaryError);
		Assert.AreEqual(1L, environment.ReadScanner(scannerId).LastAcceptedInputSequence,
			"Spotlight commits must absorb the latest pending input sequence.");
		Assert.AreEqual(0, service.PendingInputWriteCount);
		var photo = await service.TakePhotoAsync(pilot.Actor, entered.Value.Session.SessionId);
		Assert.IsTrue(photo.Succeeded, photo.Error?.Message);
		Assert.IsNotNull(photo.Value.BoundaryError);
		Assert.IsTrue(new IHL2RPCommittedOperation[] { entered.Value, spotlight.Value, photo.Value }
			.All(value => value.Commit.Documents.Count > 0));
		fixture.Body.ThrowOnRestore = true;
		var exited = await service.ExitAsync(pilot.Actor, entered.Value.Session.SessionId);
		Assert.IsTrue(exited.Succeeded, exited.Error?.Message);
		Assert.HasCount(1, fixture.CommitSink.Receipts);
		Assert.IsNotNull(fixture.CommitSink.Receipts[0].BoundaryError);
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
	}

	[TestMethod]
	public async Task ExitDisconnectAndDestructionAlwaysRestoreBodyIncludingCommitFailure()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(302, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var service = fixture.CreateService(environment);
		var first = (await service.EnterAsync(pilot.Actor, scannerId)).Value.Session;
		await service.DisconnectAsync(pilot.Actor.ConnectionId);
		Assert.AreEqual(1, fixture.Body.RestoreCount);
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.HasCount(1, fixture.CommitSink.Receipts);
		Assert.AreEqual("disconnect", fixture.CommitSink.Receipts[0].Reason);

		var second = (await service.EnterAsync(pilot.Actor, scannerId)).Value.Session;
		await service.DestroyedAsync(scannerId);
		Assert.AreEqual(2, fixture.Body.RestoreCount);
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.HasCount(2, fixture.CommitSink.Receipts);
		Assert.AreEqual("destroyed", fixture.CommitSink.Receipts[1].Reason);

		var third = (await service.EnterAsync(pilot.Actor, scannerId)).Value.Session;
		environment.Provider.FailNextCommit();
		var failedExit = await service.ExitAsync(pilot.Actor, third.SessionId);
		Assert.IsTrue(failedExit.Succeeded, failedExit.Error?.Message);
		Assert.AreEqual(3, fixture.Body.RestoreCount);
		Assert.IsEmpty(service.ActiveSessions);
		Assert.AreEqual(0, service.PendingCleanupCount);
		Assert.AreEqual(0, service.TerminatingSessionCount);
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.HasCount(3, fixture.CommitSink.Receipts);
		Assert.AreEqual("exit", fixture.CommitSink.Receipts[2].Reason);
		Assert.IsTrue(fixture.CommitSink.Receipts.All(value =>
			value.CommitSequence == value.Commit.Sequence && value.Session.ScannerId == scannerId));
		var staleInput = await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(third.SessionId, 1, 0, 0, 0, 0, 0));
		Assert.AreEqual(ErrorCode.Unauthorized, staleInput.Error!.Code);
	}

	[TestMethod]
	public async Task AutomaticSessionRevocationImmediatelyRestoresBodyAndDrainsDurablePilotClear()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(303, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var service = fixture.CreateService(environment);
		var entered = await service.EnterAsync(pilot.Actor, scannerId);
		Assert.IsTrue(entered.Succeeded);

		environment.Provider.ConflictNextCommits(2);
		fixture.Authority.TargetInvalidated(InteractionTarget.SceneEntity(scannerId), "range_or_los_failed");

		Assert.IsEmpty(service.ActiveSessions);
		Assert.AreEqual(1, fixture.Body.RestoreCount);
		await service.DrainCleanupAsync();
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.AreEqual(0, service.PendingCleanupCount);
		Assert.AreEqual(0, service.TerminatingSessionCount);
		Assert.HasCount(1, fixture.CommitSink.Receipts);
		Assert.AreEqual("range_or_los_failed", fixture.CommitSink.Receipts[0].Reason);
	}

	[TestMethod]
	public async Task RevocationDuringDeferredInputFlushCleansPilotWithoutReplayingAcceptedMotion()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(308, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var service = fixture.CreateService(environment);
		var session = (await service.EnterAsync(pilot.Actor, scannerId)).Value.Session;
		environment.Provider.AfterNextSuccessfulCommit(() =>
			fixture.Authority.TargetInvalidated(InteractionTarget.SceneEntity(scannerId), "post_commit_revoke"));

		var input = await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(session.SessionId, 1, 1, 0, 0, 0, 0));
		await service.DrainCleanupAsync();

		Assert.IsTrue(input.Succeeded, input.Error?.Message);
		Assert.AreEqual(ScannerInputDurability.Pending, input.Value.Durability);
		Assert.IsNull(input.Value.Commit);
		Assert.HasCount(1, fixture.Motion.Applied,
			"Motion accepted while the session was current must run exactly once before deferred persistence.");
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.HasCount(1, fixture.CommitSink.InputReceipts);
		Assert.HasCount(1, fixture.CommitSink.Receipts);
		Assert.AreEqual("post_commit_revoke", fixture.CommitSink.Receipts[0].Reason);
	}

	[TestMethod]
	public async Task RevocationAfterPilotCommitCompensatesDurableEntryWithoutEnteringBody()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(310, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var service = fixture.CreateService(environment);
		environment.Provider.AfterNextSuccessfulCommit(() =>
		{
			var session = fixture.Sessions.ActiveSessions.Single();
			Assert.IsTrue(fixture.Sessions.Revoke(session.Id, "post_commit_entry_revoke"));
		});

		var entered = await service.EnterAsync(pilot.Actor, scannerId);
		await service.DrainCleanupAsync();

		Assert.IsTrue(entered.Succeeded, entered.Error?.Message);
		Assert.AreEqual(entered.Value.Commit.Sequence, entered.Value.CommitSequence);
		Assert.AreEqual(0, fixture.Body.EnterCount,
			"A body transition must not run after the session was revoked.");
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId,
			"The acknowledged pilot write must be compensated after post-commit revocation.");
		Assert.IsEmpty(service.ActiveSessions);
		Assert.AreEqual(0, service.PendingCleanupCount);
		Assert.AreEqual(0, service.TerminatingSessionCount);
		Assert.HasCount(1, fixture.CommitSink.Receipts);
		Assert.AreEqual("post_commit_entry_revoke", fixture.CommitSink.Receipts[0].Reason);
	}

	[TestMethod]
	public async Task RevocationAfterSpotlightCommitSuppressesStaleEffectAndLeavesSpotlightOff()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(311, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var service = fixture.CreateService(environment);
		var session = (await service.EnterAsync(pilot.Actor, scannerId)).Value.Session;
		environment.Provider.AfterNextSuccessfulCommit(() =>
			fixture.Sessions.Revoke(session.SessionId, "post_commit_spotlight_revoke"));

		var spotlight = await service.ToggleSpotlightAsync(pilot.Actor, session.SessionId);
		await service.DrainCleanupAsync();

		Assert.IsTrue(spotlight.Succeeded, spotlight.Error?.Message);
		Assert.AreEqual(spotlight.Value.Commit.Sequence, spotlight.Value.CommitSequence);
		Assert.IsFalse(fixture.Effects.SpotlightEnabled,
			"A stale post-commit continuation must not turn the world spotlight back on.");
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.IsFalse(environment.ReadScanner(scannerId).SpotlightEnabled);
		Assert.IsEmpty(service.ActiveSessions);
		Assert.HasCount(1, fixture.CommitSink.Receipts);
		Assert.AreEqual("post_commit_spotlight_revoke", fixture.CommitSink.Receipts[0].Reason);
	}

	[TestMethod]
	public async Task RevocationBeforePilotCommitRejectsEntryWithoutStateOrWorldEffects()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(309, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var service = fixture.CreateService(environment);
		environment.Provider.InterleaveNextCommit(() =>
		{
			var session = fixture.Sessions.ActiveSessions.Single();
			Assert.IsTrue(fixture.Sessions.Revoke(session.Id, "pre_commit_revoke"));
			return Task.CompletedTask;
		});

		var result = await service.EnterAsync(pilot.Actor, scannerId);

		Assert.AreEqual(ErrorCode.Conflict, result.Error!.Code);
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.AreEqual(0, fixture.Body.EnterCount);
		Assert.IsEmpty(service.ActiveSessions);
		Assert.IsEmpty(fixture.CommitSink.Receipts);
	}

	[TestMethod]
	public async Task CleanupStopsAfterEightAttemptsAndExplicitRecoveryStartsANewBudget()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(305, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var service = fixture.CreateService(environment);
		var entered = (await service.EnterAsync(pilot.Actor, scannerId)).Value.Session;
		environment.Provider.ConflictNextCommits(ScannerPilotService.CleanupMaximumAttempts + 2);

		var failed = await service.ExitAsync(pilot.Actor, entered.SessionId);
		var drained = await service.DrainCleanupAsync();

		Assert.AreEqual(ErrorCode.Conflict, failed.Error!.Code);
		Assert.AreEqual(pilot.Actor.CharacterId, environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.IsEmpty(service.ActiveSessions);
		Assert.AreEqual(1, service.TerminatingSessionCount);
		Assert.AreEqual(0, service.PendingCleanupCount);
		Assert.AreEqual(1, service.PendingRecoveryCount);
		Assert.AreEqual(1, fixture.Body.RestoreCount);
		Assert.AreEqual(ScannerPilotService.CleanupMaximumAttempts - 1, fixture.CleanupDelay.CallCount);
		Assert.IsFalse(drained.Succeeded);
		Assert.HasCount(1, drained.RecoveryHandles);
		var recovery = drained.RecoveryHandles[0];
		Assert.AreEqual(entered.SessionId, recovery.SessionId);
		Assert.AreEqual(pilot.Actor, recovery.Actor);
		Assert.AreEqual(scannerId, recovery.ScannerId);
		Assert.AreEqual("exit", recovery.Reason);
		Assert.AreEqual(ScannerPilotService.CleanupMaximumAttempts, recovery.Attempts);
		Assert.AreEqual(ErrorCode.Conflict, recovery.LastError.Code);
		Assert.AreEqual(environment.Clock.UtcNow, recovery.CreatedAtUtc);
		Assert.AreEqual(ErrorCode.Conflict,
			(await service.ExitAsync(pilot.Actor, entered.SessionId)).Error!.Code,
			"Ordinary exit must not silently start a fresh retry budget.");
		Assert.AreEqual(ScannerPilotService.CleanupMaximumAttempts - 1, fixture.CleanupDelay.CallCount);

		var retried = await service.RetryCleanupAsync(recovery);

		Assert.IsTrue(retried.Succeeded, retried.Error?.Message);
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.AreEqual(0, service.TerminatingSessionCount);
		Assert.AreEqual(0, service.PendingCleanupCount);
		Assert.AreEqual(0, service.PendingRecoveryCount);
		Assert.AreEqual(ScannerPilotService.CleanupMaximumAttempts + 1, fixture.CleanupDelay.CallCount,
			"Two remaining conflicts require two delays before the explicit retry succeeds.");
		Assert.AreEqual(1, fixture.Body.RestoreCount);
		Assert.HasCount(1, fixture.CommitSink.Receipts);
		Assert.IsTrue((await service.EnterAsync(pilot.Actor, scannerId)).Succeeded,
			"Successful retry must release the scanner for a new pilot session.");
	}

	[TestMethod]
	public async Task FatalProviderStopsAutomaticRetryButPreservesAManualRecoveryHandle()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(307, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var service = fixture.CreateService(environment);
		var entered = (await service.EnterAsync(pilot.Actor, scannerId)).Value.Session;
		environment.Provider.FailNextCommit();
		environment.Provider.ForceFatalHealth(true);

		var failed = await service.ExitAsync(pilot.Actor, entered.SessionId);

		Assert.AreEqual(ErrorCode.InternalError, failed.Error!.Code);
		Assert.AreEqual(1, service.TerminatingSessionCount);
		Assert.AreEqual(0, service.PendingCleanupCount);
		Assert.AreEqual(1, service.PendingRecoveryCount);
		Assert.AreEqual(pilot.Actor.CharacterId, environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.AreEqual(1, fixture.Body.RestoreCount);
		Assert.AreEqual(0, fixture.CleanupDelay.CallCount,
			"Fatal provider health must stop the retry round immediately.");
		Assert.IsEmpty(fixture.CommitSink.Receipts);

		var drained = await service.DrainCleanupAsync();
		Assert.HasCount(1, drained.RecoveryHandles);
		var recovery = drained.RecoveryHandles[0];
		Assert.AreEqual(1, recovery.Attempts);
		Assert.AreEqual(ErrorCode.InternalError, recovery.LastError.Code);
		environment.Provider.ForceFatalHealth(false);
		var recovered = await service.RetryCleanupAsync(recovery);

		Assert.IsTrue(recovered.Succeeded, recovered.Error?.Message);
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.AreEqual(0, service.TerminatingSessionCount);
		Assert.AreEqual(0, service.PendingRecoveryCount);
		Assert.AreEqual(1, fixture.Body.RestoreCount);
		Assert.HasCount(1, fixture.CommitSink.Receipts);
	}

	[TestMethod]
	public async Task StorageLimitCleanupFailureIsMappedExactlyAndNeverRetriedAutomatically()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(312, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var service = fixture.CreateService(environment);
		var entered = (await service.EnterAsync(pilot.Actor, scannerId)).Value.Session;
		environment.Provider.FailNextCommit(PersistenceErrorCode.StorageLimitExceeded);

		var failed = await service.ExitAsync(pilot.Actor, entered.SessionId);
		var recovery = service.RecoveryHandles.Single();

		Assert.AreEqual(ErrorCode.StorageLimitExceeded, failed.Error!.Code);
		Assert.AreEqual(ErrorCode.StorageLimitExceeded, recovery.LastError.Code);
		Assert.AreEqual(1, recovery.Attempts);
		Assert.AreEqual(0, fixture.CleanupDelay.CallCount);
		Assert.AreEqual(pilot.Actor.CharacterId, environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.IsTrue((await service.RetryCleanupAsync(recovery)).Succeeded);
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
	}

	[TestMethod]
	public async Task CancellationDuringRetryDelayProducesARecoveryHandleWithoutLosingAddressability()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(313, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		fixture.CleanupDelay.Block = true;
		var service = fixture.CreateService(environment);
		var entered = (await service.EnterAsync(pilot.Actor, scannerId)).Value.Session;
		environment.Provider.ConflictNextCommits(ScannerPilotService.CleanupMaximumAttempts);
		using var cancellation = new CancellationTokenSource();

		var exit = service.ExitAsync(pilot.Actor, entered.SessionId, cancellation.Token).AsTask();
		Assert.AreEqual(1, fixture.CleanupDelay.CallCount);
		cancellation.Cancel();
		var failed = await exit;

		Assert.AreEqual(ErrorCode.InternalError, failed.Error!.Code);
		var recovery = service.RecoveryHandles.Single();
		Assert.AreEqual(1, recovery.Attempts);
		Assert.AreEqual(pilot.Actor.CharacterId, environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.AreEqual(1, fixture.Body.RestoreCount);
		fixture.CleanupDelay.Block = false;
		Assert.IsTrue((await service.RetryCleanupAsync(recovery)).Succeeded);
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
	}

	[TestMethod]
	public async Task ScannerDockIsNotPilotableAndInvalidAuthoredMotionLimitsFailClosed()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(306, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var dockId = SceneEntityId.New();
		await environment.SeedScannerAsync(dockId, "scanner_dock");
		var dockFixture = new ScannerFixture(environment, pilot.Actor, dockId);
		var dockService = dockFixture.CreateService(environment);
		var dock = await dockService.EnterAsync(pilot.Actor, dockId);
		Assert.AreEqual(ErrorCode.PolicyDenied, dock.Error!.Code);
		Assert.AreEqual(0, dockFixture.Body.EnterCount);

		var droneId = SceneEntityId.New();
		await environment.SeedScannerAsync(droneId);
		var droneFixture = new ScannerFixture(environment, pilot.Actor, droneId);
		droneFixture.Limits.Value = new ScannerMotionLimits(125f, float.NaN, 75f);
		var droneService = droneFixture.CreateService(environment);
		var session = (await droneService.EnterAsync(pilot.Actor, droneId)).Value.Session;
		var input = await droneService.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(session.SessionId, 1, 1, 0, 0, 0, 0));

		Assert.AreEqual(ErrorCode.InvalidArgument, input.Error!.Code);
		Assert.IsEmpty(droneFixture.Motion.Applied);
		Assert.AreEqual(0L, environment.ReadScanner(droneId).LastAcceptedInputSequence);
	}

	[TestMethod]
	public async Task StartupReconciliationClearsPersistedPilotWithoutTransientSession()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(304, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var crashedFixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var crashedService = crashedFixture.CreateService(environment);
		Assert.IsTrue((await crashedService.EnterAsync(pilot.Actor, scannerId)).Succeeded);
		Assert.AreEqual(pilot.Actor.CharacterId, environment.ReadScanner(scannerId).PilotCharacterId);

		var recoveredFixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var recoveredService = recoveredFixture.CreateService(environment);
		var reconciled = await recoveredService.ReconcilePersistedPilotsAsync();

		Assert.IsTrue(reconciled.Succeeded);
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.IsEmpty(recoveredService.ActiveSessions);
	}

	private static async Task WaitForScheduledInputFlushesAsync(ScannerPilotService service)
	{
		await WaitForAsync(
			() => service.ScheduledInputFlushCount == 0,
			"Scheduled scanner input flush did not quiesce.");
	}

	private static async Task WaitForAsync(Func<bool> condition, string failureMessage)
	{
		for (var attempt = 0; attempt < 500; attempt++)
		{
			if (condition()) return;
			await Task.Delay(1);
		}
		Assert.Fail(failureMessage);
	}

	private sealed class ScannerFixture
	{
		public ScannerFixture(ShowcaseTestEnvironment environment, InventoryActor actor, SceneEntityId scannerId)
		{
			var target = InteractionTarget.SceneEntity(scannerId);
			World = new FakeWorld(actor, target);
			var interactable = new FakeInteractable(target);
			Sessions = new InteractionSessionService(environment.Clock);
			Authority = new InteractionAuthorityService(World, new FakeDirectory(interactable),
				Sessions, environment.Access,
				ShowcaseTestEnvironment.AllowPolicy<ServerInteractionContext>(), environment.Clock);
		}

		public FakeWorld World { get; }
		public InteractionAuthorityService Authority { get; }
		public InteractionSessionService Sessions { get; }
		public FakeBody Body { get; } = new();
		public FakeMotion Motion { get; } = new();
		public FakeMotionLimits Limits { get; } = new();
		public FakeCleanupDelay CleanupDelay { get; } = new();
		public FakeInputFlushDelay InputFlushDelay { get; } = new();
		public FakeEffects Effects { get; } = new();
		public FakeScannerCommitSink CommitSink { get; } = new();

		public ScannerPilotService CreateService(ShowcaseTestEnvironment environment) => new(
			environment.Repositories, Authority, Sessions, environment.Clock, Body, Motion, Limits, Effects,
			new FakePoses(), CommitSink, () => Guid.NewGuid(), CleanupDelay.DelayAsync,
			InputFlushDelay.DelayAsync);
	}

	private sealed class FakeWorld : IServerInteractionWorld
	{
		private readonly InventoryActor _actor;
		private readonly InteractionTarget _target;
		public FakeWorld(InventoryActor actor, InteractionTarget target) { _actor = actor; _target = target; }
		public bool TryBuildContext(ConnectionId connectionId, CharacterId characterId,
			InteractionTarget target, out ServerInteractionContext context)
		{
			if (connectionId != _actor.ConnectionId || characterId != _actor.CharacterId || target != _target)
			{
				context = default!;
				return false;
			}
			context = new ServerInteractionContext
			{
				ConnectionId = connectionId, AccountId = _actor.AccountId, CharacterId = characterId,
				Target = target, ActorPosition = new WorldPoint(0, 0, 0),
				TargetPosition = new WorldPoint(10, 0, 0), IsAlive = true, IsRestrained = false
			};
			return true;
		}
		public bool HasLineOfSight(ServerInteractionContext context) => true;
	}

	private sealed class FakeDirectory : IInteractionDirectory
	{
		private readonly IHexInteractable _interactable;
		public FakeDirectory(IHexInteractable interactable) => _interactable = interactable;
		public bool TryResolve(InteractionTarget target, out IHexInteractable interactable)
		{
			interactable = _interactable;
			return target == _interactable.Target;
		}
	}

	private sealed class FakeInteractable : IHexInteractable
	{
		public FakeInteractable(InteractionTarget target) => Target = target;
		public InteractionTarget Target { get; }
		public InteractionPolicy Policy { get; } = new() { SessionKind = InteractionSessionKind.Scanner };
		public OperationResult<InteractionOffer> Authorize(ServerInteractionContext context) =>
			OperationResult<InteractionOffer>.Success(new InteractionOffer
			{
				SessionKind = InteractionSessionKind.Scanner
			});
	}

	private sealed class FakeBody : IScannerBodyBoundary
	{
		public int EnterCount { get; private set; }
		public int RestoreCount { get; private set; }
		public bool ThrowOnEnter { get; set; }
		public bool ThrowOnRestore { get; set; }
		public void EnterPilot(InventoryActor actor, SceneEntityId scannerId)
		{
			EnterCount++;
			if (ThrowOnEnter) throw new InvalidOperationException("Injected scanner body failure.");
		}
		public void Restore(InventoryActor actor, SceneEntityId scannerId, string reason)
		{
			RestoreCount++;
			if (ThrowOnRestore) throw new InvalidOperationException("Injected scanner restore failure.");
		}
	}

	private sealed class FakeMotion : IScannerMotionBoundary
	{
		private int _applyCount;
		public List<ScannerMotionCommand> Applied { get; } = new();
		public ManualResetEventSlim FirstApplyObserved { get; } = new(false);
		public int ApplyCount => Volatile.Read(ref _applyCount);
		public bool ThrowOnApply { get; set; }
		public void Apply(SceneEntityId scannerId, ScannerMotionCommand command)
		{
			lock (Applied) Applied.Add(command);
			Interlocked.Increment(ref _applyCount);
			FirstApplyObserved.Set();
			if (ThrowOnApply) throw new InvalidOperationException("Injected scanner motion failure.");
		}
	}

	private sealed class FakeMotionLimits : IScannerMotionLimitsProvider
	{
		private readonly ManualResetEventSlim _secondRaceResolveEntered = new(false);
		private readonly ManualResetEventSlim _releaseSecondRaceResolve = new(false);
		private int _raceResolveCount;
		private bool _coordinateRace;

		public ScannerMotionLimits Value { get; set; } = new(125f, 240f, 75f);
		public SceneEntityId? LastResolvedScannerId { get; private set; }
		public ManualResetEventSlim FirstRaceResolveEntered { get; } = new(false);

		public void ArmConcurrentResolveRace()
		{
			_raceResolveCount = 0;
			FirstRaceResolveEntered.Reset();
			_secondRaceResolveEntered.Reset();
			_releaseSecondRaceResolve.Reset();
			_coordinateRace = true;
		}

		public void ReleaseSecondRaceResolve() => _releaseSecondRaceResolve.Set();

		public OperationResult<ScannerMotionLimits> Resolve(SceneEntityId scannerId)
		{
			LastResolvedScannerId = scannerId;
			if (_coordinateRace)
			{
				var call = Interlocked.Increment(ref _raceResolveCount);
				if (call == 1)
				{
					FirstRaceResolveEntered.Set();
					if (!_secondRaceResolveEntered.Wait(TimeSpan.FromSeconds(5)))
						throw new InvalidOperationException("Second scanner input did not reach motion-limit resolution.");
				}
				else if (call == 2)
				{
					_secondRaceResolveEntered.Set();
					if (!_releaseSecondRaceResolve.Wait(TimeSpan.FromSeconds(5)))
						throw new InvalidOperationException("Second scanner input resolution was not released.");
					_coordinateRace = false;
				}
			}
			return OperationResult<ScannerMotionLimits>.Success(Value);
		}
	}

	private sealed class FakeCleanupDelay
	{
		private TaskCompletionSource? _release;
		public bool Block { get; set; }
		public int CallCount { get; private set; }
		public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
		{
			Assert.AreEqual(ScannerPilotService.CleanupRetryDelay, duration);
			CallCount++;
			if (!Block)
			{
				cancellationToken.ThrowIfCancellationRequested();
				return Task.CompletedTask;
			}
			_release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			if (cancellationToken.CanBeCanceled)
				_ = cancellationToken.Register(
					static state => ((TaskCompletionSource)state!).TrySetCanceled(),
					_release);
			return _release.Task;
		}
	}

	private sealed class FakeInputFlushDelay
	{
		private readonly Queue<TaskCompletionSource> _pending = new();

		public int CallCount { get; private set; }
		public int PendingCount => _pending.Count(value => !value.Task.IsCompleted);

		public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
		{
			Assert.AreEqual(ScannerPilotService.InputWriteBehindInterval, duration);
			CallCount++;
			var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			_pending.Enqueue(completion);
			if (cancellationToken.CanBeCanceled)
				_ = cancellationToken.Register(
					static state => ((TaskCompletionSource)state!).TrySetCanceled(),
					completion);
			return completion.Task;
		}

		public void ReleaseNext()
		{
			while (_pending.Count > 0 && _pending.Peek().Task.IsCompleted) _pending.Dequeue();
			Assert.IsGreaterThan(0, _pending.Count);
			_pending.Dequeue().TrySetResult();
		}
	}

	private sealed class FakeEffects : IScannerEffectsBoundary
	{
		public int SpotlightCount { get; private set; }
		public bool SpotlightEnabled { get; private set; }
		public int FlashCount { get; private set; }
		public int PhotoCount { get; private set; }
		public bool ThrowOnSpotlight { get; set; }
		public bool ThrowOnPhoto { get; set; }
		public void SetSpotlight(SceneEntityId scannerId, bool enabled)
		{
			SpotlightCount++;
			SpotlightEnabled = enabled;
			if (ThrowOnSpotlight) throw new InvalidOperationException("Injected scanner spotlight failure.");
		}
		public void Flash(SceneEntityId scannerId) => FlashCount++;
		public void PublishPhoto(SceneEntityId scannerId, ScannerPhotoMetadata metadata)
		{
			PhotoCount++;
			if (ThrowOnPhoto) throw new InvalidOperationException("Injected scanner photo failure.");
		}
	}

	private sealed class FakeScannerCommitSink : IScannerCommitSink
	{
		public List<ScannerInputPersistenceReceipt> InputReceipts { get; } = new();
		public List<ScannerPilotCleanupReceipt> Receipts { get; } = new();
		public void Observe(ScannerInputPersistenceReceipt receipt) => InputReceipts.Add(receipt);
		public void Observe(ScannerPilotCleanupReceipt receipt) => Receipts.Add(receipt);
	}

	private sealed class FakePoses : ITrustedScannerPoseProvider
	{
		public OperationResult<TrustedScannerPose> Capture(SceneEntityId scannerId) =>
			OperationResult<TrustedScannerPose>.Success(new TrustedScannerPose(10, 20, 30));
	}
}
