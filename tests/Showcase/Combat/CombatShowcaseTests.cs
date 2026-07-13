#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Schema;
using HL2RP.V2.Showcase.Combat;

namespace HL2RP.V2.Tests.Showcase.Combat;

[TestClass]
public sealed class CombatShowcaseTests
{
	[TestMethod]
	public async Task TypedEquipReloadReplenishAndUnequipActionsCommitThroughNeutralBoundary()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pistol = ShowcaseTestEnvironment.Pistol(6, equipped: false);
		var ammunition = ShowcaseTestEnvironment.Ammunition(12);
		var vest = ShowcaseTestEnvironment.Vest(equipped: false);
		var seeded = await environment.SeedCharacterAsync(100, items: new[] { pistol, ammunition, vest });
		var service = new ItemActionService(
			environment.Repositories,
			environment.Schema,
			environment.Access,
			new SchemaItemShapeCatalog(environment.Schema, environment.Repositories),
			new ItemActionRegistry(new IItemActionHandler[]
			{
				new EquipCombatItemActionHandler(),
				new UnequipCombatItemActionHandler(),
				new ReloadPistolItemActionHandler(),
				new ReplenishPistolAmmunitionItemActionHandler()
			}),
			environment.Schema.CreatePolicyPipeline<ItemActionContext>().Value,
			new PostCommitEventBus<ItemActionCommittedEvent>());

		AssertSuccess(await service.ExecuteAsync(seeded.Actor, seeded.InventoryId, pistol.Id,
			new ActionId(HL2RPIds.Actions.Equip)));
		AssertSuccess(await service.ExecuteAsync(seeded.Actor, seeded.InventoryId, pistol.Id,
			new ActionId(HL2RPIds.Actions.Reload)));
		Assert.AreEqual(PistolItemState.MagazineCapacity, environment.ReadPistol(pistol.Id).MagazineRounds);
		Assert.AreEqual(0, environment.ReadAmmunition(ammunition.Id).Rounds);
		AssertSuccess(await service.ExecuteAsync(seeded.Actor, seeded.InventoryId, ammunition.Id,
			new ActionId(HL2RPIds.Actions.Replenish)));
		Assert.AreEqual(PistolItemState.MagazineCapacity, environment.ReadAmmunition(ammunition.Id).Rounds);
		AssertSuccess(await service.ExecuteAsync(seeded.Actor, seeded.InventoryId, pistol.Id,
			new ActionId(HL2RPIds.Actions.Unequip)));
		Assert.IsFalse(environment.ReadPistol(pistol.Id).Equipped);
		AssertSuccess(await service.ExecuteAsync(seeded.Actor, seeded.InventoryId, vest.Id,
			new ActionId(HL2RPIds.Actions.Equip)));
		Assert.IsTrue(environment.ReadVest(vest.Id).Equipped);
	}

	[TestMethod]
	public async Task FireRequiresCompletedRaiseAndRejectsRateAndActorForgery()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pistol = ShowcaseTestEnvironment.Pistol(equipped: true, raised: false);
		var seeded = await environment.SeedCharacterAsync(101, items: new[] { pistol });
		var raycast = new FakeRaycast();
		var damage = new FakeDamageBoundary();
		var service = new PistolCombatService(environment.Repositories, environment.Access,
			environment.Clock, raycast, damage);

		var ticket = service.BeginRaise(seeded.Actor, seeded.InventoryId, pistol.Id);
		Assert.IsTrue(ticket.Succeeded, ticket.Error?.Message);
		var early = await service.CompleteRaiseAsync(ticket.Value.Id, seeded.Actor);
		Assert.AreEqual(ErrorCode.Conflict, early.Error!.Code);
		environment.Clock.Advance(PistolCombatService.DefaultRaiseDelay);
		AssertSuccess(await service.CompleteRaiseAsync(ticket.Value.Id, seeded.Actor));

		var fired = await service.FireAsync(new PistolFireIntent(seeded.Actor, seeded.InventoryId, pistol.Id));
		Assert.IsTrue(fired.Succeeded, fired.Error?.Message);
		Assert.AreEqual(PistolItemState.MagazineCapacity - 1, fired.Value.RemainingRounds);
		Assert.AreEqual(1, damage.CommitCount);
		Assert.AreEqual(20L, damage.LastDamage);
		var tooFast = await service.FireAsync(new PistolFireIntent(seeded.Actor, seeded.InventoryId, pistol.Id));
		Assert.AreEqual(ErrorCode.PolicyDenied, tooFast.Error!.Code);
		var forged = seeded.Actor with { ConnectionId = ConnectionId.New() };
		var forgedFire = await service.FireAsync(new PistolFireIntent(forged, seeded.InventoryId, pistol.Id));
		Assert.AreEqual(ErrorCode.Unauthorized, forgedFire.Error!.Code);
	}

	[TestMethod]
	public async Task FireAndVestCommitFailuresPublishNoPartialDamageOrTraitMutation()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pistol = ShowcaseTestEnvironment.Pistol(equipped: true, raised: false);
		var vest = ShowcaseTestEnvironment.Vest(100, equipped: true);
		var seeded = await environment.SeedCharacterAsync(102, items: new[] { pistol, vest });
		var damage = new FakeDamageBoundary();
		var combat = new PistolCombatService(environment.Repositories, environment.Access,
			environment.Clock, new FakeRaycast(), damage);
		var ticket = combat.BeginRaise(seeded.Actor, seeded.InventoryId, pistol.Id).Value;
		environment.Clock.Advance(PistolCombatService.DefaultRaiseDelay);
		AssertSuccess(await combat.CompleteRaiseAsync(ticket.Id, seeded.Actor));

		environment.Provider.FailNextCommit();
		var failedFire = await combat.FireAsync(new PistolFireIntent(seeded.Actor, seeded.InventoryId, pistol.Id));
		Assert.IsTrue(failedFire.Failed);
		Assert.AreEqual(PistolItemState.MagazineCapacity, environment.ReadPistol(pistol.Id).MagazineRounds);
		Assert.AreEqual(0, damage.CommitCount);

		damage.StageResult = OperationResult.Failure(ErrorCode.Conflict, "target changed");
		var failedStage = await combat.FireAsync(new PistolFireIntent(seeded.Actor, seeded.InventoryId, pistol.Id));
		Assert.AreEqual(ErrorCode.Conflict, failedStage.Error!.Code);
		Assert.AreEqual(PistolItemState.MagazineCapacity, environment.ReadPistol(pistol.Id).MagazineRounds);
		Assert.AreEqual(0, damage.CommitCount);
		damage.StageResult = OperationResult.Success();

		var vestService = new ProtectiveVestService(environment.Repositories, environment.Access);
		environment.Provider.FailNextCommit();
		var failedVest = await vestService.ApplyAsync(seeded.Actor, seeded.InventoryId, vest.Id, 50);
		Assert.IsTrue(failedVest.Failed);
		Assert.AreEqual(100, environment.ReadVest(vest.Id).Durability);
		var applied = await vestService.ApplyAsync(seeded.Actor, seeded.InventoryId, vest.Id, 50);
		Assert.IsTrue(applied.Succeeded, applied.Error?.Message);
		Assert.AreEqual(15L, applied.Value.AbsorbedDamage);
		Assert.AreEqual(35L, applied.Value.AppliedDamage);
		Assert.AreEqual(85, environment.ReadVest(vest.Id).Durability);
	}

	[TestMethod]
	public async Task DeathDropIsAtomicAndRespawnContractRestoresOnlyAfterDelay()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pistol = ShowcaseTestEnvironment.Pistol(equipped: true, raised: true);
		var seeded = await environment.SeedCharacterAsync(103, items: new[] { pistol });
		var boundary = new FakeLifecycleBoundary();
		var service = new CombatLifecycleService(environment.Repositories, environment.Schema,
			environment.Layout, new AllowWorldModels(), environment.Clock, boundary);
		var transform = new WorldTransformRecord
		{
			PositionX = 1, PositionY = 2, PositionZ = 3,
			RotationX = 0, RotationY = 0, RotationZ = 0, RotationW = 1
		};

		environment.Provider.FailNextCommit();
		var failed = await service.DieAsync(seeded.Actor, seeded.InventoryId, transform, "test");
		Assert.IsTrue(failed.Failed);
		Assert.IsNotNull(environment.Repositories.Inventories.Find(DomainKeys.Inventory(seeded.InventoryId))!.Value.Find(pistol.Id));
		Assert.IsNull(environment.Repositories.WorldItems.Find(DomainKeys.WorldItem(pistol.Id)));
		Assert.AreEqual(0, boundary.ClearCount);

		var died = await service.DieAsync(seeded.Actor, seeded.InventoryId, transform, "test");
		Assert.IsTrue(died.Succeeded, died.Error?.Message);
		Assert.IsNull(environment.Repositories.Inventories.Find(DomainKeys.Inventory(seeded.InventoryId))!.Value.Find(pistol.Id));
		Assert.IsNotNull(environment.Repositories.WorldItems.Find(DomainKeys.WorldItem(pistol.Id)));
		Assert.IsFalse(environment.ReadPistol(pistol.Id).Equipped);
		Assert.AreEqual(1, boundary.ClearCount);
		Assert.AreEqual(ErrorCode.PolicyDenied, service.Respawn(seeded.Actor).Error!.Code);
		environment.Clock.Advance(CombatLifecycleService.DefaultRespawnDelay);
		Assert.IsTrue(service.Respawn(seeded.Actor).Succeeded);
		Assert.AreEqual(1, boundary.RespawnCount);
	}

	private static void AssertSuccess(OperationResult result) =>
		Assert.IsTrue(result.Succeeded, result.Error?.Message);

	private sealed class FakeRaycast : IAuthoritativePistolRaycast
	{
		public OperationResult<AuthoritativeShot> Resolve(PistolFireIntent intent) =>
			OperationResult<AuthoritativeShot>.Success(new AuthoritativeShot(
				new WorldPoint(0, 0, 0), new WorldPoint(100, 0, 0), "target", true));
	}

	private sealed class FakeDamageBoundary : ICombatDamageBoundary
	{
		public int CommitCount { get; private set; }
		public int StageCount { get; private set; }
		public long LastDamage { get; private set; }
		public OperationResult StageResult { get; set; } = OperationResult.Success();

		public OperationResult<PreparedCombatDamage> Prepare(AuthoritativeShot shot, long damage) =>
			OperationResult<PreparedCombatDamage>.Success(new PreparedCombatDamage(shot, damage));

		public OperationResult Stage(IUnitOfWork unitOfWork, PreparedCombatDamage damage)
		{
			StageCount++;
			return StageResult;
		}

		public void Commit(PreparedCombatDamage damage)
		{
			CommitCount++;
			LastDamage = damage.Damage;
		}
	}

	private sealed class AllowWorldModels : IWorldModelCatalog
	{
		public bool IsValidModel(string modelPath) => !string.IsNullOrWhiteSpace(modelPath);
	}

	private sealed class FakeLifecycleBoundary : ICombatLifecycleBoundary
	{
		public int ClearCount { get; private set; }
		public int RespawnCount { get; private set; }

		public void ClearSessions(InventoryActor actor) => ClearCount++;
		public void PublishDeath(DeathTransitionReceipt transition) { }
		public void PublishRespawn(DeathRespawnState state) => RespawnCount++;
	}
}
