#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Persistence;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;
using HL2RP.V2.Showcase.Combat;

namespace HL2RP.V2.Tests.Showcase.Combat;

[TestClass]
public sealed class CombatIntentServiceTests
{
	[TestMethod]
	public async Task ProductionRouteRaisesThenFiresThroughVestDeathDropAndRespawn()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var shooterPistol = ShowcaseTestEnvironment.Pistol(equipped: true, raised: false);
		var shooter = await environment.SeedCharacterAsync(201, items: new[] { shooterPistol });
		var targetVest = ShowcaseTestEnvironment.Vest(100, equipped: true);
		var targetPistol = ShowcaseTestEnvironment.Pistol(equipped: true, raised: false);
		var target = await environment.SeedCharacterAsync(202, items: new[] { targetVest, targetPistol });
		var health = new CanonicalCombatHealthDirectory();
		health.Publish(target.Actor.CharacterId, 100, 20);
		var targets = new CanonicalCombatPlayerTargetDirectory();
		targets.Publish(new[] { Target("target-player", target) });
		var playerDamage = new PlayerCombatDamageBoundary(environment.Repositories, targets, health);
		var pistol = new PistolCombatService(environment.Repositories, environment.Access,
			environment.Clock, new TargetRaycast("target-player"), playerDamage);
		var lifecycleBoundary = new RecordingLifecycleBoundary(pistol);
		var lifecycle = new CombatLifecycleService(environment.Repositories, environment.Schema,
			environment.Layout, new AllowWorldModels(), environment.Clock, lifecycleBoundary);
		var delay = new AdvancingDelay(environment.Clock);
		var service = new CombatIntentService(pistol, playerDamage, lifecycle, health, delay, environment.Clock);

		var first = await service.FireAsync(new CombatFireIntent(
			shooter.Actor, shooter.InventoryId, shooterPistol.Id));

		Assert.IsTrue(first.Succeeded, first.Error?.Message);
		Assert.AreEqual(PistolCombatService.DefaultRaiseDelay, delay.LastDelay);
		Assert.AreEqual(1, delay.Count);
		Assert.AreEqual(6L, first.Value.PlayerDamage?.RemainingHealth);
		Assert.AreEqual(6L, first.Value.PlayerDamage?.AbsorbedDamage);
		Assert.AreEqual(94, environment.ReadVest(targetVest.Id).Durability);
		Assert.AreEqual(PistolItemState.MagazineCapacity - 1,
			environment.ReadPistol(shooterPistol.Id).MagazineRounds);
		Assert.IsNull(first.Value.Death);

		environment.Clock.Advance(PistolCombatService.DefaultFireInterval);
		var lethal = await service.FireAsync(new CombatFireIntent(
			shooter.Actor, shooter.InventoryId, shooterPistol.Id));

		Assert.IsTrue(lethal.Succeeded, lethal.Error?.Message);
		Assert.AreEqual(1, delay.Count, "An already host-raised pistol must not pay the delay twice.");
		Assert.IsTrue(lethal.Value.PlayerDamage?.IsLethal);
		Assert.IsNotNull(lethal.Value.Death);
		Assert.AreEqual(0L, health.Require(target.Actor.CharacterId).Value.CurrentHealth);
		Assert.AreEqual(88, environment.ReadVest(targetVest.Id).Durability);
		Assert.AreEqual(PistolItemState.MagazineCapacity - 2,
			environment.ReadPistol(shooterPistol.Id).MagazineRounds);
		Assert.IsNull(environment.Repositories.Inventories.Find(
			DomainKeys.Inventory(target.InventoryId))!.Value.Find(targetPistol.Id));
		Assert.IsNotNull(environment.Repositories.WorldItems.Find(DomainKeys.WorldItem(targetPistol.Id)));
		Assert.AreEqual(1, lifecycleBoundary.ClearCount);
		Assert.AreEqual(ErrorCode.PolicyDenied, service.Respawn(target.Actor).Error!.Code);

		environment.Clock.Advance(CombatLifecycleService.DefaultRespawnDelay);
		Assert.IsTrue(service.Respawn(target.Actor).Succeeded);
		Assert.AreEqual(100L, health.Require(target.Actor.CharacterId).Value.CurrentHealth);
		Assert.AreEqual(1, lifecycleBoundary.RespawnCount);
	}

	[TestMethod]
	public async Task FailedLethalDropSurfacesFailureAndRestoresUsableAliveHealth()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var shooterPistol = ShowcaseTestEnvironment.Pistol(equipped: true, raised: false);
		var shooter = await environment.SeedCharacterAsync(203, items: new[] { shooterPistol });
		var targetPistol = ShowcaseTestEnvironment.Pistol(equipped: true, raised: false);
		var target = await environment.SeedCharacterAsync(204, items: new[] { targetPistol });
		var health = new CanonicalCombatHealthDirectory();
		health.Publish(target.Actor.CharacterId, 100, 10);
		var targets = new CanonicalCombatPlayerTargetDirectory();
		targets.Publish(new[] { Target("target-player", target) });
		var playerDamage = new PlayerCombatDamageBoundary(environment.Repositories, targets, health);
		var failingBoundary = new FailNextDeathBoundary(playerDamage, environment.Provider);
		var pistol = new PistolCombatService(environment.Repositories, environment.Access,
			environment.Clock, new TargetRaycast("target-player"), failingBoundary);
		var lifecycleBoundary = new RecordingLifecycleBoundary(pistol);
		var lifecycle = new CombatLifecycleService(environment.Repositories, environment.Schema,
			environment.Layout, new AllowWorldModels(), environment.Clock, lifecycleBoundary);
		var service = new CombatIntentService(pistol, failingBoundary, lifecycle, health,
			new AdvancingDelay(environment.Clock), environment.Clock);

		var fired = await service.FireAsync(new CombatFireIntent(
			shooter.Actor, shooter.InventoryId, shooterPistol.Id));

		Assert.IsFalse(fired.Succeeded);
		StringAssert.Contains(fired.Error!.Message, "target health was restored");
		Assert.AreEqual(1L, health.Require(target.Actor.CharacterId).Value.CurrentHealth);
		Assert.IsNotNull(environment.Repositories.Inventories.Find(
			DomainKeys.Inventory(target.InventoryId))!.Value.Find(targetPistol.Id));
		Assert.IsNull(environment.Repositories.WorldItems.Find(DomainKeys.WorldItem(targetPistol.Id)));
		Assert.AreEqual(0, lifecycleBoundary.ClearCount);
		Assert.AreEqual(PistolItemState.MagazineCapacity - 1,
			environment.ReadPistol(shooterPistol.Id).MagazineRounds,
			"The committed shot remains visible even when the second death transaction fails.");
	}

	[TestMethod]
	public async Task HealthVialRouteConsumesDurablyAndHealsHostHealth()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var vial = new ItemRecord
		{
			Id = ItemId.New(),
			Definition = new DefinitionId(HL2RPIds.Items.HealthVial),
			Traits = new Dictionary<string, TypedPayload>()
		};
		var seeded = await environment.SeedCharacterAsync(205, items: new[] { vial });
		var health = new CanonicalCombatHealthDirectory();
		health.Publish(seeded.Actor.CharacterId, 100, 50);
		var service = new HealthVialConsumeService(
			environment.Repositories, environment.Access, environment.Layout, health);

		var consumed = await service.ConsumeAsync(seeded.Actor, seeded.InventoryId, vial.Id);

		Assert.IsTrue(consumed.Succeeded, consumed.Error?.Message);
		Assert.AreEqual(50L, consumed.Value.PreviousHealth);
		Assert.AreEqual(75L, consumed.Value.CurrentHealth);
		Assert.IsNull(environment.Repositories.Items.Find(DomainKeys.Item(vial.Id)));
		Assert.IsNull(environment.Repositories.Inventories.Find(
			DomainKeys.Inventory(seeded.InventoryId))!.Value.Find(vial.Id));
	}

	[TestMethod]
	public async Task FailedHealthVialCommitLeavesItemAndHostHealthUntouched()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var vial = new ItemRecord
		{
			Id = ItemId.New(),
			Definition = new DefinitionId(HL2RPIds.Items.HealthVial),
			Traits = new Dictionary<string, TypedPayload>()
		};
		var seeded = await environment.SeedCharacterAsync(206, items: new[] { vial });
		var health = new CanonicalCombatHealthDirectory();
		health.Publish(seeded.Actor.CharacterId, 100, 50);
		var service = new HealthVialConsumeService(
			environment.Repositories, environment.Access, environment.Layout, health);
		environment.Provider.FailNextCommit();

		var consumed = await service.ConsumeAsync(seeded.Actor, seeded.InventoryId, vial.Id);

		Assert.IsFalse(consumed.Succeeded);
		Assert.AreEqual(50L, health.Require(seeded.Actor.CharacterId).Value.CurrentHealth);
		Assert.IsNotNull(environment.Repositories.Items.Find(DomainKeys.Item(vial.Id)));
		Assert.IsNotNull(environment.Repositories.Inventories.Find(
			DomainKeys.Inventory(seeded.InventoryId))!.Value.Find(vial.Id));
	}

	[TestMethod]
	public async Task EveryAdvertisedActionAcrossAllSeventeenItemsHasAnExecutableRoute()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var catalog = HL2RPExecutableItemActionCatalog.CreateDefault();

		Assert.HasCount(17, environment.Schema.Items.All);
		var validation = catalog.Validate(environment.Schema);
		Assert.IsTrue(validation.Succeeded, validation.Error?.Message);
		foreach (var definition in environment.Schema.Items.All)
		foreach (var action in definition.ActionIds)
		{
			var definitionId = new DefinitionId(definition.Id);
			var actionId = new ActionId(action);
			Assert.IsTrue(catalog.TryResolve(definitionId, actionId, out _), $"{definition.Id}/{action}");
			Assert.IsTrue(catalog.CreateSnapshot(definitionId, actionId, action).Enabled, $"{definition.Id}/{action}");
		}

		Assert.AreEqual(ExecutableItemActionRoute.HealthVialConsume,
			RequireRoute(catalog, HL2RPIds.Items.HealthVial, HL2RPIds.Actions.Consume));
		Assert.AreEqual(ExecutableItemActionRoute.CombatFireIntent,
			RequireRoute(catalog, HL2RPIds.Items.Pistol, HL2RPIds.Actions.Fire));
		Assert.AreEqual(ExecutableItemActionRoute.RestraintIntent,
			RequireRoute(catalog, HL2RPIds.Items.ZipTie, HL2RPIds.Actions.Restrain));
		Assert.IsFalse(catalog.CreateSnapshot(new DefinitionId(HL2RPIds.Items.Pistol),
			new ActionId("unknown_action"), "Unknown").Enabled);
	}

	[TestMethod]
	public async Task SameTargetConcurrentDamageAndConsumeVersusDamageFailCleanlyOnHealthReservation()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var vial = new ItemRecord
		{
			Id = ItemId.New(),
			Definition = new DefinitionId(HL2RPIds.Items.HealthVial),
			Traits = new Dictionary<string, TypedPayload>()
		};
		var target = await environment.SeedCharacterAsync(207, items: new[] { vial });
		var health = new CanonicalCombatHealthDirectory();
		health.Publish(target.Actor.CharacterId, 100, 50);
		var targets = new CanonicalCombatPlayerTargetDirectory();
		targets.Publish(new[] { Target("target-player", target) });
		var damage = new PlayerCombatDamageBoundary(environment.Repositories, targets, health);
		var shot = new AuthoritativeShot(
			new WorldPoint(0, 0, 0), new WorldPoint(100, 0, 0), "target-player", true);

		var first = damage.Prepare(shot, 20);
		Assert.IsTrue(first.Succeeded, first.Error?.Message);
		var concurrent = damage.Prepare(shot, 20);
		Assert.IsFalse(concurrent.Succeeded);
		Assert.AreEqual(ErrorCode.Conflict, concurrent.Error!.Code);

		var consume = new HealthVialConsumeService(
			environment.Repositories, environment.Access, environment.Layout, health);
		var duringDamage = await consume.ConsumeAsync(target.Actor, target.InventoryId, vial.Id);
		Assert.IsFalse(duringDamage.Succeeded);
		Assert.AreEqual(ErrorCode.Conflict, duringDamage.Error!.Code);
		Assert.IsNotNull(environment.Repositories.Items.Find(DomainKeys.Item(vial.Id)));
		Assert.AreEqual(50L, health.Require(target.Actor.CharacterId).Value.CurrentHealth);

		damage.Abort(first.Value);
		var healingReservation = health.Reserve(target.Actor.CharacterId);
		Assert.IsTrue(healingReservation.Succeeded, healingReservation.Error?.Message);
		var duringHealing = damage.Prepare(shot, 20);
		Assert.IsFalse(duringHealing.Succeeded);
		Assert.AreEqual(ErrorCode.Conflict, duringHealing.Error!.Code);
		health.Release(healingReservation.Value);
		var afterAbort = await consume.ConsumeAsync(target.Actor, target.InventoryId, vial.Id);
		Assert.IsTrue(afterAbort.Succeeded, afterAbort.Error?.Message);
		Assert.AreEqual(75L, health.Require(target.Actor.CharacterId).Value.CurrentHealth);
	}

	[TestMethod]
	public async Task CancelledRaiseRemovesTicketAndNextIntentCanRaiseAndFire()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pistolItem = ShowcaseTestEnvironment.Pistol(equipped: true, raised: false);
		var shooter = await environment.SeedCharacterAsync(208, items: new[] { pistolItem });
		var damage = new NoOpPlayerDamageBoundary();
		var pistol = new PistolCombatService(environment.Repositories, environment.Access,
			environment.Clock, new MissRaycast(), damage);
		var lifecycle = new CombatLifecycleService(environment.Repositories, environment.Schema,
			environment.Layout, new AllowWorldModels(), environment.Clock,
			new RecordingLifecycleBoundary(pistol));
		var health = new CanonicalCombatHealthDirectory();
		var cancelled = new CombatIntentService(pistol, damage, lifecycle, health,
			new CancelingDelay(), environment.Clock);
		var intent = new CombatFireIntent(shooter.Actor, shooter.InventoryId, pistolItem.Id);

		await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
			await cancelled.FireAsync(intent));

		var retry = new CombatIntentService(pistol, damage, lifecycle, health,
			new AdvancingDelay(environment.Clock), environment.Clock);
		var fired = await retry.FireAsync(intent);
		Assert.IsTrue(fired.Succeeded, fired.Error?.Message);
		Assert.AreEqual(PistolItemState.MagazineCapacity - 1,
			environment.ReadPistol(pistolItem.Id).MagazineRounds);
	}

	[TestMethod]
	public async Task AuthoritativeMissConsumesAmmunitionWithoutCallingWorldDamage()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pistolItem = ShowcaseTestEnvironment.Pistol(equipped: true, raised: false);
		var shooter = await environment.SeedCharacterAsync(209, items: new[] { pistolItem });
		var health = new CanonicalCombatHealthDirectory();
		var targets = new CanonicalCombatPlayerTargetDirectory();
		targets.Publish(Array.Empty<CombatPlayerTarget>());
		var players = new PlayerCombatDamageBoundary(environment.Repositories, targets, health);
		var world = new RecordingWorldDamageBoundary();
		var composite = new CompositeCombatDamageBoundary(players, world);
		var pistol = new PistolCombatService(environment.Repositories, environment.Access,
			environment.Clock, new MissRaycast(), composite);
		var lifecycle = new CombatLifecycleService(environment.Repositories, environment.Schema,
			environment.Layout, new AllowWorldModels(), environment.Clock,
			new RecordingLifecycleBoundary(pistol));
		var service = new CombatIntentService(pistol, players, lifecycle, health,
			new AdvancingDelay(environment.Clock), environment.Clock);

		var fired = await service.FireAsync(new CombatFireIntent(
			shooter.Actor, shooter.InventoryId, pistolItem.Id));

		Assert.IsTrue(fired.Succeeded, fired.Error?.Message);
		Assert.AreEqual(PistolItemState.MagazineCapacity - 1,
			environment.ReadPistol(pistolItem.Id).MagazineRounds);
		Assert.AreEqual(0, world.PrepareCount);
		Assert.AreEqual(0, world.StageCount);
	}

	[TestMethod]
	public void CompositeWorldPlanBookkeepingIsSafeAcrossConcurrentIntents()
	{
		var players = new NoOpPlayerDamageBoundary();
		var world = new RecordingWorldDamageBoundary();
		var composite = new CompositeCombatDamageBoundary(players, world);
		var prepared = new ConcurrentBag<PreparedCombatDamage>();

		Parallel.For(0, 128, index =>
		{
			var result = composite.Prepare(new AuthoritativeShot(
				new WorldPoint(0, 0, 0), new WorldPoint(index + 1, 0, 0), "world-target", true), 20);
			if (result.Succeeded) prepared.Add(result.Value);
		});

		Assert.HasCount(128, prepared);
		Assert.HasCount(128, prepared.Select(plan => plan.PlanId).Distinct());
		Parallel.ForEach(prepared, composite.Abort);
	}

	private static ExecutableItemActionRoute RequireRoute(
		HL2RPExecutableItemActionCatalog catalog,
		string definition,
		string action)
	{
		Assert.IsTrue(catalog.TryResolve(new DefinitionId(definition), new ActionId(action), out var route));
		return route.Route;
	}

	private static CombatPlayerTarget Target(string token, SeededCharacter target) => new(
		token,
		target.Actor,
		target.InventoryId,
		new WorldTransformRecord
		{
			PositionX = 1,
			PositionY = 2,
			PositionZ = 3,
			RotationX = 0,
			RotationY = 0,
			RotationZ = 0,
			RotationW = 1
		});

	private sealed class TargetRaycast : IAuthoritativePistolRaycast
	{
		private readonly string _target;
		public TargetRaycast(string target) => _target = target;
		public OperationResult<AuthoritativeShot> Resolve(PistolFireIntent intent) =>
			OperationResult<AuthoritativeShot>.Success(new AuthoritativeShot(
				new WorldPoint(0, 0, 0), new WorldPoint(100, 0, 0), _target, true));
	}

	private sealed class MissRaycast : IAuthoritativePistolRaycast
	{
		public OperationResult<AuthoritativeShot> Resolve(PistolFireIntent intent) =>
			OperationResult<AuthoritativeShot>.Success(new AuthoritativeShot(
				new WorldPoint(0, 0, 0), new WorldPoint(100, 0, 0), null, false));
	}

	private sealed class AdvancingDelay : ICombatIntentDelay
	{
		private readonly MutableClock _clock;
		public AdvancingDelay(MutableClock clock) => _clock = clock;
		public int Count { get; private set; }
		public TimeSpan LastDelay { get; private set; }
		public ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Count++;
			LastDelay = duration;
			_clock.Advance(duration);
			return ValueTask.CompletedTask;
		}
	}

	private sealed class CancelingDelay : ICombatIntentDelay
	{
		public ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default) =>
			ValueTask.FromException(new OperationCanceledException("cancelled"));
	}

	private sealed class RecordingLifecycleBoundary : ICombatLifecycleBoundary
	{
		private readonly PistolCombatService _pistol;
		public RecordingLifecycleBoundary(PistolCombatService pistol) => _pistol = pistol;
		public int ClearCount { get; private set; }
		public int RespawnCount { get; private set; }
		public void ClearSessions(InventoryActor actor)
		{
			ClearCount++;
			_pistol.ClearCharacter(actor.CharacterId);
		}
		public void PublishDeath(DeathTransitionReceipt transition) { }
		public void PublishRespawn(DeathRespawnState state) => RespawnCount++;
	}

	private sealed class AllowWorldModels : IWorldModelCatalog
	{
		public bool IsValidModel(string modelPath) => !string.IsNullOrWhiteSpace(modelPath);
	}

	private sealed class FailNextDeathBoundary : IPlayerCombatDamageOutcomeBoundary
	{
		private readonly IPlayerCombatDamageOutcomeBoundary _inner;
		private readonly FaultInjectingProvider _provider;
		public FailNextDeathBoundary(IPlayerCombatDamageOutcomeBoundary inner, FaultInjectingProvider provider)
		{
			_inner = inner;
			_provider = provider;
		}
		public bool CanResolve(AuthoritativeShot shot) => _inner.CanResolve(shot);
		public bool Owns(Guid planId) => _inner.Owns(planId);
		public OperationResult<PreparedCombatDamage> Prepare(AuthoritativeShot shot, long damage) =>
			_inner.Prepare(shot, damage);
		public OperationResult Stage(IUnitOfWork unitOfWork, PreparedCombatDamage damage) =>
			_inner.Stage(unitOfWork, damage);
		public void Commit(PreparedCombatDamage damage) => _inner.Commit(damage);
		public void Abort(PreparedCombatDamage damage) => _inner.Abort(damage);
		public bool TryTakeOutcome(Guid planId, out PlayerCombatDamageOutcome outcome)
		{
			var found = _inner.TryTakeOutcome(planId, out outcome!);
			if (found && outcome.IsLethal) _provider.FailNextCommit();
			return found;
		}
		public void RestoreAlive(CharacterId characterId) => _inner.RestoreAlive(characterId);
	}

	private sealed class NoOpPlayerDamageBoundary : IPlayerCombatDamageOutcomeBoundary
	{
		public bool CanResolve(AuthoritativeShot shot) => false;
		public bool Owns(Guid planId) => false;
		public OperationResult<PreparedCombatDamage> Prepare(AuthoritativeShot shot, long damage) =>
			OperationResult<PreparedCombatDamage>.Success(new PreparedCombatDamage(shot, damage));
		public OperationResult Stage(IUnitOfWork unitOfWork, PreparedCombatDamage damage) => OperationResult.Success();
		public void Commit(PreparedCombatDamage damage) { }
		public bool TryTakeOutcome(Guid planId, out PlayerCombatDamageOutcome outcome)
		{
			outcome = null!;
			return false;
		}
		public void RestoreAlive(CharacterId characterId) { }
	}

	private sealed class RecordingWorldDamageBoundary : ICombatDamageBoundary
	{
		private int _prepareCount;
		private int _stageCount;
		public int PrepareCount => _prepareCount;
		public int StageCount => _stageCount;
		public OperationResult<PreparedCombatDamage> Prepare(AuthoritativeShot shot, long damage)
		{
			Interlocked.Increment(ref _prepareCount);
			return damage > 0
				? OperationResult<PreparedCombatDamage>.Success(new PreparedCombatDamage(shot, damage))
				: OperationResult<PreparedCombatDamage>.Failure(ErrorCode.InvalidArgument, "zero damage");
		}
		public OperationResult Stage(IUnitOfWork unitOfWork, PreparedCombatDamage damage)
		{
			Interlocked.Increment(ref _stageCount);
			return OperationResult.Success();
		}
		public void Commit(PreparedCombatDamage damage) { }
	}
}
