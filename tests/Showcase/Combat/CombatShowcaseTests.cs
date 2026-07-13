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
	public async Task InterleavedAccessRevocationRejectsPistolFireWithoutDamageOrAmmunitionMutation()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pistol = ShowcaseTestEnvironment.Pistol(equipped: true, raised: false);
		var seeded = await environment.SeedCharacterAsync(107, items: new[] { pistol });
		var damage = new FakeDamageBoundary();
		var service = new PistolCombatService(environment.Repositories, environment.Access,
			environment.Clock, new FakeRaycast(), damage);
		var ticket = service.BeginRaise(seeded.Actor, seeded.InventoryId, pistol.Id).Value;
		environment.Clock.Advance(PistolCombatService.DefaultRaiseDelay);
		AssertSuccess(await service.CompleteRaiseAsync(ticket.Id, seeded.Actor));
		environment.Provider.InterleaveNextCommit(() =>
		{
			environment.Access.RevokeConnection(seeded.Actor.ConnectionId);
			return Task.CompletedTask;
		});

		var result = await service.FireAsync(
			new PistolFireIntent(seeded.Actor, seeded.InventoryId, pistol.Id));

		Assert.AreEqual(ErrorCode.Conflict, result.Error!.Code);
		Assert.AreEqual(PistolItemState.MagazineCapacity,
			environment.ReadPistol(pistol.Id).MagazineRounds);
		Assert.AreEqual(0, damage.CommitCount);
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
	public async Task PistolTransitionsAndLifecycleClearExposeOnlyDurableReceipts()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var sessionPistol = ShowcaseTestEnvironment.Pistol(equipped: true, raised: false);
		var persistedPistol = ShowcaseTestEnvironment.Pistol(equipped: true, raised: true);
		var seeded = await environment.SeedCharacterAsync(
			104, items: new[] { sessionPistol, persistedPistol });
		var service = new PistolCombatService(
			environment.Repositories,
			environment.Access,
			environment.Clock,
			new FakeRaycast(),
			new FakeDamageBoundary());

		var lowered = await service.LowerAsync(
			seeded.Actor, seeded.InventoryId, persistedPistol.Id);
		Assert.IsTrue(lowered.Succeeded, lowered.Error?.Message);
		Assert.IsTrue(lowered.Value.HasDurableChanges);
		Assert.IsNotNull(lowered.Value.Commit);
		Assert.AreEqual(PistolStateTransitionKind.Lower, lowered.Value.Kind);
		Assert.IsFalse(environment.ReadPistol(persistedPistol.Id).Raised);

		var ticket = service.BeginRaise(seeded.Actor, seeded.InventoryId, sessionPistol.Id);
		Assert.IsTrue(ticket.Succeeded, ticket.Error?.Message);
		environment.Clock.Advance(PistolCombatService.DefaultRaiseDelay);
		var completed = await service.CompleteRaiseAsync(ticket.Value.Id, seeded.Actor);
		Assert.IsTrue(completed.Succeeded, completed.Error?.Message);
		Assert.IsFalse(completed.Value.HasDurableChanges);
		Assert.IsNull(completed.Value.Commit);
		Assert.IsFalse(environment.ReadPistol(sessionPistol.Id).Raised,
			"Raise completion must remain session-only until fire commits.");

		var fired = await service.FireAsync(
			new PistolFireIntent(seeded.Actor, seeded.InventoryId, sessionPistol.Id));
		Assert.IsTrue(fired.Succeeded, fired.Error?.Message);
		Assert.IsTrue(environment.ReadPistol(sessionPistol.Id).Raised);

		environment.Provider.FailNextCommit();
		var failedClear = await service.ClearCharacterAsync(seeded.Actor.CharacterId);
		Assert.IsTrue(failedClear.Failed);
		Assert.IsTrue(environment.ReadPistol(sessionPistol.Id).Raised);
		Assert.IsTrue(service.IsHostRaised(
			seeded.Actor, seeded.InventoryId, sessionPistol.Id).Value,
			"Failed durable clear must retain host-session authority.");

		var cleared = await service.ClearCharacterAsync(seeded.Actor.CharacterId);
		Assert.IsTrue(cleared.Succeeded, cleared.Error?.Message);
		Assert.IsTrue(cleared.Value.HasDurableChanges);
		Assert.IsNotNull(cleared.Value.Commit);
		Assert.HasCount(1, cleared.Value.ChangedPistols);
		Assert.AreEqual(sessionPistol.Id, cleared.Value.ChangedPistols[0]);
		Assert.IsFalse(environment.ReadPistol(sessionPistol.Id).Raised);
		Assert.IsFalse(service.IsHostRaised(
			seeded.Actor, seeded.InventoryId, sessionPistol.Id).Value);
	}

	[TestMethod]
	public async Task PreparedPistolClearDefersSessionCleanupUntilCallerOwnedCommitCompletes()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pistol = ShowcaseTestEnvironment.Pistol(equipped: true, raised: false);
		var seeded = await environment.SeedCharacterAsync(107, items: new[] { pistol });
		var service = new PistolCombatService(
			environment.Repositories,
			environment.Access,
			environment.Clock,
			new FakeRaycast(),
			new FakeDamageBoundary());
		var ticket = service.BeginRaise(seeded.Actor, seeded.InventoryId, pistol.Id).Value;
		environment.Clock.Advance(PistolCombatService.DefaultRaiseDelay);
		AssertSuccess(await service.CompleteRaiseAsync(ticket.Id, seeded.Actor));
		AssertSuccess(await service.FireAsync(
			new PistolFireIntent(seeded.Actor, seeded.InventoryId, pistol.Id)));

		var prepared = service.PrepareCharacterClear(seeded.Actor.CharacterId);
		Assert.IsTrue(prepared.Succeeded, prepared.Error?.Message);
		Assert.HasCount(1, prepared.Value.ChangedPistols);
		var unitOfWork = environment.Repositories.Provider.BeginUnitOfWork();
		AssertSuccess(service.StageCharacterClear(unitOfWork, prepared.Value));
		Assert.IsTrue(environment.ReadPistol(pistol.Id).Raised,
			"Staging must not publish the candidate pistol state.");
		Assert.IsTrue(service.IsHostRaised(
			seeded.Actor, seeded.InventoryId, pistol.Id).Value,
			"Staging must not clear transient raise authority.");

		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork);
		Assert.IsTrue(committed.Succeeded, committed.Error?.Message);
		Assert.IsFalse(environment.ReadPistol(pistol.Id).Raised);
		Assert.IsTrue(service.IsHostRaised(
			seeded.Actor, seeded.InventoryId, pistol.Id).Value,
			"Even a durable shared commit cannot clear session authority before completion.");
		var completed = service.CompleteCharacterClear(prepared.Value, committed.Value);
		Assert.IsTrue(completed.Succeeded, completed.Error?.Message);
		Assert.AreSame(committed.Value, completed.Value.Commit);
		Assert.IsFalse(service.IsHostRaised(
			seeded.Actor, seeded.InventoryId, pistol.Id).Value);
	}

	[TestMethod]
	public async Task PreparedPistolClearPinsEveryPistolAndFailedCommitClearsNoSessionState()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var raised = ShowcaseTestEnvironment.Pistol(equipped: true, raised: false);
		var unchanged = ShowcaseTestEnvironment.Pistol(5, equipped: false, raised: false);
		var seeded = await environment.SeedCharacterAsync(108, items: new[] { raised, unchanged });
		var service = new PistolCombatService(
			environment.Repositories,
			environment.Access,
			environment.Clock,
			new FakeRaycast(),
			new FakeDamageBoundary());
		var ticket = service.BeginRaise(seeded.Actor, seeded.InventoryId, raised.Id).Value;
		environment.Clock.Advance(PistolCombatService.DefaultRaiseDelay);
		AssertSuccess(await service.CompleteRaiseAsync(ticket.Id, seeded.Actor));
		AssertSuccess(await service.FireAsync(
			new PistolFireIntent(seeded.Actor, seeded.InventoryId, raised.Id)));

		var prepared = service.PrepareCharacterClear(seeded.Actor.CharacterId).Value;
		var external = environment.Repositories.Provider.BeginUnitOfWork();
		var unchangedDocument = environment.Repositories.Items.Find(DomainKeys.Item(unchanged.Id))!;
		var unchangedEditor = external.Edit(environment.Repositories.Items, unchangedDocument)!;
		unchangedEditor.Replace(CombatPersistence.ReplaceTrait(
			unchangedEditor.Value,
			CombatTraitNames.Pistol,
			HL2RPPersistence.Pistol,
			environment.ReadPistol(unchanged.Id) with { MagazineRounds = 4 }));
		external.Save(unchangedEditor);
		Assert.IsTrue((await HL2RPUnitOfWork.CommitAndDisposeAsync(external)).Succeeded);

		var sequenceBefore = environment.Provider.Health.Sequence;
		var combined = environment.Repositories.Provider.BeginUnitOfWork();
		AssertSuccess(service.StageCharacterClear(combined, prepared));
		var failed = await HL2RPUnitOfWork.CommitAndDisposeAsync(combined);
		Assert.IsFalse(failed.Succeeded, "An unraised owned pistol must still be pinned by the plan.");
		Assert.AreEqual(sequenceBefore, environment.Provider.Health.Sequence);
		Assert.IsTrue(environment.ReadPistol(raised.Id).Raised);
		Assert.IsTrue(service.IsHostRaised(
			seeded.Actor, seeded.InventoryId, raised.Id).Value);
		var forgedCompletion = service.CompleteCharacterClear(
			prepared, new CommitReceipt(sequenceBefore, Array.Empty<CommittedDocumentVersion>()));
		Assert.IsTrue(forgedCompletion.Failed);
		Assert.IsTrue(service.IsHostRaised(
			seeded.Actor, seeded.InventoryId, raised.Id).Value,
			"A failed or unrelated commit must publish no transient cleanup event.");
	}

	[TestMethod]
	public async Task StartupPistolReconciliationIsOneAtomicReceiptedCommit()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var first = ShowcaseTestEnvironment.Pistol(equipped: true, raised: true);
		var second = ShowcaseTestEnvironment.Pistol(equipped: true, raised: true);
		await environment.SeedCharacterAsync(105, items: new[] { first });
		await environment.SeedCharacterAsync(106, items: new[] { second });
		var service = new PistolCombatService(
			environment.Repositories,
			environment.Access,
			environment.Clock,
			new FakeRaycast(),
			new FakeDamageBoundary());

		environment.Provider.FailNextCommit();
		var failed = await service.ReconcileRaisedPistolsAsync();
		Assert.IsTrue(failed.Failed);
		Assert.IsTrue(environment.ReadPistol(first.Id).Raised);
		Assert.IsTrue(environment.ReadPistol(second.Id).Raised);

		var reconciled = await service.ReconcileRaisedPistolsAsync();
		Assert.IsTrue(reconciled.Succeeded, reconciled.Error?.Message);
		Assert.IsNotNull(reconciled.Value.Commit);
		Assert.HasCount(2, reconciled.Value.ChangedPistols);
		Assert.HasCount(2, reconciled.Value.Commit.Documents);
		Assert.IsFalse(environment.ReadPistol(first.Id).Raised);
		Assert.IsFalse(environment.ReadPistol(second.Id).Raised);

		var noOp = await service.ReconcileRaisedPistolsAsync();
		Assert.IsTrue(noOp.Succeeded, noOp.Error?.Message);
		Assert.IsFalse(noOp.Value.HasDurableChanges);
		Assert.IsNull(noOp.Value.Commit);
	}

	[TestMethod]
	public async Task CharacterPistolGraphFindsNestedPistolWithoutTouchingLargeUnrelatedPopulation()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var suitcase = new ItemRecord
		{
			Id = ItemId.New(),
			Definition = new DefinitionId(HL2RPIds.Items.Suitcase),
			Traits = new Dictionary<string, TypedPayload>()
		};
		var nestedPistol = ShowcaseTestEnvironment.Pistol(equipped: true, raised: true);
		var unrelatedPistol = ShowcaseTestEnvironment.Pistol(equipped: true, raised: true);
		var seeded = await environment.SeedCharacterAsync(108, items: new[] { suitcase });
		var nestedInventory = new InventoryRecord
		{
			Id = InventoryId.New(),
			Owner = InventoryOwner.ParentItem(suitcase.Id),
			Width = 4,
			Height = 4,
			Placements = new[] { new InventoryPlacement(nestedPistol.Id, 0, 0) }
		};
		await using (var seed = environment.Provider.BeginUnitOfWork())
		{
			seed.Create(environment.Repositories.Items, DomainKeys.Item(nestedPistol.Id), nestedPistol);
			seed.Create(environment.Repositories.Items, DomainKeys.Item(unrelatedPistol.Id), unrelatedPistol);
			seed.Create(environment.Repositories.Inventories, DomainKeys.Inventory(nestedInventory.Id), nestedInventory);
			for (var index = 0; index < 256; index++)
			{
				var unrelated = new InventoryRecord
				{
					Id = InventoryId.New(),
					Owner = InventoryOwner.Character(CharacterId.New()),
					Width = 1,
					Height = 1,
					Placements = index == 0
						? new[] { new InventoryPlacement(unrelatedPistol.Id, 0, 0) }
						: Array.Empty<InventoryPlacement>()
				};
				seed.Create(environment.Repositories.Inventories, DomainKeys.Inventory(unrelated.Id), unrelated);
			}
			var committed = await seed.CommitAsync();
			Assert.IsTrue(committed.Succeeded, committed.Error?.Message);
		}
		var service = new PistolCombatService(
			environment.Repositories,
			environment.Access,
			environment.Clock,
			new FakeRaycast(),
			new FakeDamageBoundary());

		var result = await service.ClearCharacterAsync(seeded.Actor.CharacterId);

		Assert.IsTrue(result.Succeeded, result.Error?.Message);
		Assert.HasCount(1, result.Value.ChangedPistols);
		Assert.AreEqual(nestedPistol.Id, result.Value.ChangedPistols[0]);
		Assert.IsFalse(environment.ReadPistol(nestedPistol.Id).Raised);
		Assert.IsTrue(environment.ReadPistol(unrelatedPistol.Id).Raised);
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

	[TestMethod]
	public async Task PostCommitDeathBoundaryFailureStillReturnsTheDurableReceipt()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var pistol = ShowcaseTestEnvironment.Pistol(equipped: true, raised: true);
		var seeded = await environment.SeedCharacterAsync(109, items: new[] { pistol });
		var boundary = new FakeLifecycleBoundary { ThrowOnClear = true, ThrowOnDeath = true };
		var service = new CombatLifecycleService(environment.Repositories, environment.Schema,
			environment.Layout, new AllowWorldModels(), environment.Clock, boundary);
		var transform = new WorldTransformRecord
		{
			PositionX = 1, PositionY = 2, PositionZ = 3,
			RotationX = 0, RotationY = 0, RotationZ = 0, RotationW = 1
		};

		var died = await service.DieAsync(seeded.Actor, seeded.InventoryId, transform, "test");

		Assert.IsTrue(died.Succeeded, died.Error?.Message);
		Assert.IsNotNull(died.Value.Commit);
		Assert.AreEqual(died.Value.Commit.Sequence, died.Value.CommitSequence);
		Assert.IsNotNull(died.Value.BoundaryError);
		Assert.IsNull(environment.Repositories.Inventories
			.Find(DomainKeys.Inventory(seeded.InventoryId))!.Value.Find(pistol.Id));
		Assert.IsNotNull(environment.Repositories.WorldItems.Find(DomainKeys.WorldItem(pistol.Id)));
	}

	private static void AssertSuccess(OperationResult result) =>
		Assert.IsTrue(result.Succeeded, result.Error?.Message);

	private static void AssertSuccess<T>(OperationResult<T> result) =>
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
		public bool ThrowOnClear { get; init; }
		public bool ThrowOnDeath { get; init; }

		public void ClearSessions(InventoryActor actor)
		{
			ClearCount++;
			if (ThrowOnClear) throw new InvalidOperationException("Injected session cleanup failure.");
		}
		public void PublishDeath(DeathTransitionReceipt transition)
		{
			if (ThrowOnDeath) throw new InvalidOperationException("Injected death presentation failure.");
		}
		public void PublishRespawn(DeathRespawnState state) => RespawnCount++;
	}
}
