#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using HL2RP.V2.Schema;
using HL2RP.V2.Showcase.Scanner;

namespace HL2RP.V2.Tests.Showcase.Scanner;

[TestClass]
public sealed class ScannerShowcaseTests
{
	[TestMethod]
	public async Task BoundPilotInputIsOrderedRateLimitedCappedAndTransactional()
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
		Assert.AreEqual(1, fixture.Body.EnterCount);

		var forged = await service.ApplyInputAsync(pilot.Actor with { ConnectionId = ConnectionId.New() },
			new ScannerInputIntent(entered.Value.SessionId, 1, 1, 0, 0, 0, 0));
		Assert.AreEqual(ErrorCode.Unauthorized, forged.Error!.Code);
		var first = await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(entered.Value.SessionId, 1, 1, 0, 0, 0.5f, -0.5f));
		Assert.IsTrue(first.Succeeded, first.Error?.Message);
		Assert.AreEqual(125f, first.Value.Motion.VelocityX);
		Assert.AreEqual(37.5f, first.Value.Motion.YawRate);
		Assert.AreEqual(240f, first.Value.Motion.MaximumAcceleration);
		Assert.AreEqual(scannerId, fixture.Limits.LastResolvedScannerId);
		var stale = await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(entered.Value.SessionId, 1, 0, 0, 0, 0, 0));
		Assert.AreEqual(ErrorCode.Unauthorized, stale.Error!.Code);
		environment.Clock.Advance(TimeSpan.FromMilliseconds(49));
		var tooFast = await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(entered.Value.SessionId, 2, 0, 0, 0, 0, 0));
		Assert.AreEqual(ErrorCode.PolicyDenied, tooFast.Error!.Code);
		environment.Clock.Advance(TimeSpan.FromMilliseconds(1));
		var second = await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(entered.Value.SessionId, 2, 0, 1, 0, 0, 0));
		Assert.IsTrue(second.Succeeded, second.Error?.Message);
		var uncapped = await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(entered.Value.SessionId, 3, 1, 1, 0, 0, 0));
		Assert.AreEqual(ErrorCode.PolicyDenied, uncapped.Error!.Code);

		environment.Clock.Advance(ScannerPilotService.MinimumInputInterval);
		environment.Provider.FailNextCommit();
		var failed = await service.ApplyInputAsync(pilot.Actor,
			new ScannerInputIntent(entered.Value.SessionId, 3, 0, 0, 1, 0, 0));
		Assert.IsTrue(failed.Failed);
		Assert.AreEqual(2L, environment.ReadScanner(scannerId).LastAcceptedInputSequence);
		Assert.HasCount(2, fixture.Motion.Applied);
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
		var session = (await service.EnterAsync(pilot.Actor, scannerId)).Value;

		var spotlight = await service.ToggleSpotlightAsync(pilot.Actor, session.SessionId);
		Assert.IsTrue(spotlight.Succeeded && spotlight.Value);
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
	public async Task ExitDisconnectAndDestructionAlwaysRestoreBodyIncludingCommitFailure()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(302, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		var service = fixture.CreateService(environment);
		var first = (await service.EnterAsync(pilot.Actor, scannerId)).Value;
		await service.DisconnectAsync(pilot.Actor.ConnectionId);
		Assert.AreEqual(1, fixture.Body.RestoreCount);
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);

		var second = (await service.EnterAsync(pilot.Actor, scannerId)).Value;
		await service.DestroyedAsync(scannerId);
		Assert.AreEqual(2, fixture.Body.RestoreCount);
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);

		var third = (await service.EnterAsync(pilot.Actor, scannerId)).Value;
		environment.Provider.FailNextCommit();
		var failedExit = await service.ExitAsync(pilot.Actor, third.SessionId);
		Assert.IsTrue(failedExit.Succeeded, failedExit.Error?.Message);
		Assert.AreEqual(3, fixture.Body.RestoreCount);
		Assert.IsEmpty(service.ActiveSessions);
		Assert.AreEqual(0, service.PendingCleanupCount);
		Assert.AreEqual(0, service.TerminatingSessionCount);
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
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
	}

	[TestMethod]
	public async Task CleanupRetainsAddressabilityAcrossRetryRoundsThenPrunesItsCompletedTask()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pilot = await environment.SeedCharacterAsync(305, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Scanner);
		var scannerId = SceneEntityId.New();
		await environment.SeedScannerAsync(scannerId);
		var fixture = new ScannerFixture(environment, pilot.Actor, scannerId);
		fixture.CleanupDelay.Block = true;
		var service = fixture.CreateService(environment);
		var entered = (await service.EnterAsync(pilot.Actor, scannerId)).Value;
		environment.Provider.ConflictNextCommits(ScannerPilotService.CleanupMaximumAttempts + 2);

		var exit = service.ExitAsync(pilot.Actor, entered.SessionId).AsTask();

		Assert.AreEqual(pilot.Actor.CharacterId, environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.IsEmpty(service.ActiveSessions);
		Assert.AreEqual(1, service.TerminatingSessionCount);
		Assert.AreEqual(1, service.PendingCleanupCount);
		Assert.AreEqual(1, fixture.Body.RestoreCount);
		Assert.AreEqual(1, fixture.CleanupDelay.CallCount);
		fixture.CleanupDelay.Release();

		var retried = await exit;

		Assert.IsTrue(retried.Succeeded, retried.Error?.Message);
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.AreEqual(0, service.TerminatingSessionCount);
		Assert.AreEqual(0, service.PendingCleanupCount);
		Assert.AreEqual(1, fixture.Body.RestoreCount);
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
		var entered = (await service.EnterAsync(pilot.Actor, scannerId)).Value;
		environment.Provider.FailNextCommit();
		environment.Provider.ForceFatalHealth(true);

		var failed = await service.ExitAsync(pilot.Actor, entered.SessionId);

		Assert.AreEqual(ErrorCode.InternalError, failed.Error!.Code);
		Assert.AreEqual(1, service.TerminatingSessionCount);
		Assert.AreEqual(0, service.PendingCleanupCount);
		Assert.AreEqual(pilot.Actor.CharacterId, environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.AreEqual(1, fixture.Body.RestoreCount);

		environment.Provider.ForceFatalHealth(false);
		var recovered = await service.ExitAsync(pilot.Actor, entered.SessionId);

		Assert.IsTrue(recovered.Succeeded, recovered.Error?.Message);
		Assert.IsNull(environment.ReadScanner(scannerId).PilotCharacterId);
		Assert.AreEqual(0, service.TerminatingSessionCount);
		Assert.AreEqual(1, fixture.Body.RestoreCount);
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
		var session = (await droneService.EnterAsync(pilot.Actor, droneId)).Value;
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
		public FakeEffects Effects { get; } = new();

		public ScannerPilotService CreateService(ShowcaseTestEnvironment environment) => new(
			environment.Repositories, Authority, Sessions, environment.Clock, Body, Motion, Limits, Effects,
			new FakePoses(), () => Guid.NewGuid(), CleanupDelay.DelayAsync);
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
		public void EnterPilot(InventoryActor actor, SceneEntityId scannerId) => EnterCount++;
		public void Restore(InventoryActor actor, SceneEntityId scannerId, string reason) => RestoreCount++;
	}

	private sealed class FakeMotion : IScannerMotionBoundary
	{
		public List<ScannerMotionCommand> Applied { get; } = new();
		public void Apply(SceneEntityId scannerId, ScannerMotionCommand command) => Applied.Add(command);
	}

	private sealed class FakeMotionLimits : IScannerMotionLimitsProvider
	{
		public ScannerMotionLimits Value { get; set; } = new(125f, 240f, 75f);
		public SceneEntityId? LastResolvedScannerId { get; private set; }
		public OperationResult<ScannerMotionLimits> Resolve(SceneEntityId scannerId)
		{
			LastResolvedScannerId = scannerId;
			return OperationResult<ScannerMotionLimits>.Success(Value);
		}
	}

	private sealed class FakeCleanupDelay
	{
		private TaskCompletionSource? _release;
		public bool Block { get; set; }
		public int CallCount { get; private set; }
		public Task DelayAsync(TimeSpan duration)
		{
			Assert.AreEqual(ScannerPilotService.CleanupRetryDelay, duration);
			CallCount++;
			if (!Block) return Task.CompletedTask;
			_release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			return _release.Task;
		}
		public void Release()
		{
			Block = false;
			_release!.TrySetResult();
		}
	}

	private sealed class FakeEffects : IScannerEffectsBoundary
	{
		public int SpotlightCount { get; private set; }
		public int FlashCount { get; private set; }
		public int PhotoCount { get; private set; }
		public void SetSpotlight(SceneEntityId scannerId, bool enabled) => SpotlightCount++;
		public void Flash(SceneEntityId scannerId) => FlashCount++;
		public void PublishPhoto(SceneEntityId scannerId, ScannerPhotoMetadata metadata) => PhotoCount++;
	}

	private sealed class FakePoses : ITrustedScannerPoseProvider
	{
		public OperationResult<TrustedScannerPose> Capture(SceneEntityId scannerId) =>
			OperationResult<TrustedScannerPose>.Success(new TrustedScannerPose(10, 20, 30));
	}
}
