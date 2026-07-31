#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Features;

[TestClass]
public sealed class PermitPurchaseServiceTests
{
	[TestMethod]
	public async Task PurchaseDebitsAndCreatesOwnedTypedPermitInOneCommit()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var setup = await SeedAsync(environment, balance: 300);
		var events = new RecordingHandler<PermitPurchasedReceipt>();
		var audit = new RecordingHandler<AdminAuditFact>();
		var service = CreateService(environment, FeatureTestEnvironment.AllowPolicy(), events, audit);

		var purchased = await service.PurchaseAsync(
			setup.Actor, setup.Inventory.Id, BusinessPermitKind.General);

		Assert.IsTrue(purchased.Succeeded, purchased.Error?.Message);
		Assert.AreEqual(PermitPurchaseService.DefaultPrice, purchased.Value.Price);
		Assert.AreEqual(50L, purchased.Value.RemainingBalance);
		var character = environment.Repositories.Characters.Find(
			DomainKeys.Character(setup.Actor.CharacterId))!.Value;
		var inventory = environment.Repositories.Inventories.Find(
			DomainKeys.Inventory(setup.Inventory.Id))!.Value;
		var permit = environment.Repositories.Items.Find(
			DomainKeys.Item(purchased.Value.PermitItemId))!.Value;
		Assert.AreEqual(50L, character.Balance);
		Assert.IsNotNull(inventory.Find(permit.Id));
		Assert.AreEqual(HL2RPIds.Items.BusinessPermit, permit.Definition.Value);
		var state = HL2RPPersistence.BusinessPermit.Deserialize(
			permit.Traits["permit"].Data, permit.Traits["permit"].TypeVersion);
		Assert.AreEqual(setup.Actor.CharacterId, state.OwnerCharacterId);
		Assert.AreEqual(BusinessPermitKind.General, state.Kind);
		Assert.IsFalse(state.Revoked);
		Assert.IsNull(state.ExpiresAtUtc);
		Assert.HasCount(1, events.Events);
		Assert.HasCount(1, audit.Events);
		Assert.AreEqual(HL2RPFeatureOperation.PurchasePermit, audit.Events[0].Operation);
		Assert.AreEqual(purchased.Value.CommitSequence, audit.Events[0].CommitSequence);
	}

	[TestMethod]
	public async Task InsufficientFundsLeaveBalanceInventoryAndItemsUnchanged()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var setup = await SeedAsync(environment, balance: PermitPurchaseService.DefaultPrice - 1);
		var beforeItemCount = environment.Repositories.Items.All().Count;

		var purchased = await CreateService(environment).PurchaseAsync(
			setup.Actor, setup.Inventory.Id, BusinessPermitKind.General);

		Assert.IsFalse(purchased.Succeeded);
		Assert.AreEqual(ErrorCode.Conflict, purchased.Error!.Code);
		Assert.AreEqual(PermitPurchaseService.DefaultPrice - 1,
			environment.Repositories.Characters.Find(DomainKeys.Character(setup.Actor.CharacterId))!.Value.Balance);
		Assert.IsEmpty(environment.Repositories.Inventories.Find(
			DomainKeys.Inventory(setup.Inventory.Id))!.Value.Placements);
		Assert.HasCount(beforeItemCount, environment.Repositories.Items.All());
	}

	[TestMethod]
	public async Task FullMainInventoryRejectsPurchaseBeforeDebit()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var setup = await SeedAsync(environment, balance: 500, full: true);
		var beforeItemCount = environment.Repositories.Items.All().Count;

		var purchased = await CreateService(environment).PurchaseAsync(
			setup.Actor, setup.Inventory.Id, BusinessPermitKind.Food);

		Assert.IsFalse(purchased.Succeeded);
		Assert.AreEqual(ErrorCode.Conflict, purchased.Error!.Code);
		Assert.AreEqual(500L, environment.Repositories.Characters.Find(
			DomainKeys.Character(setup.Actor.CharacterId))!.Value.Balance);
		Assert.HasCount(beforeItemCount, environment.Repositories.Items.All());
		Assert.HasCount(1, environment.Repositories.Inventories.Find(
			DomainKeys.Inventory(setup.Inventory.Id))!.Value.Placements);
	}

	[TestMethod]
	public async Task EquivalentActivePermitRejectsDuplicateWithoutDebit()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var setup = await SeedAsync(environment, balance: 500, duplicateKind: BusinessPermitKind.Electronics);
		var beforeItemCount = environment.Repositories.Items.All().Count;

		var purchased = await CreateService(environment).PurchaseAsync(
			setup.Actor, setup.Inventory.Id, BusinessPermitKind.Electronics);

		Assert.IsFalse(purchased.Succeeded);
		Assert.AreEqual(ErrorCode.Conflict, purchased.Error!.Code);
		Assert.AreEqual(500L, environment.Repositories.Characters.Find(
			DomainKeys.Character(setup.Actor.CharacterId))!.Value.Balance);
		Assert.HasCount(beforeItemCount, environment.Repositories.Items.All());
	}

	[TestMethod]
	public async Task PolicyVetoLeavesPurchaseStateUntouched()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var setup = await SeedAsync(environment, balance: 500);
		var events = new RecordingHandler<PermitPurchasedReceipt>();
		var service = CreateService(environment, FeatureTestEnvironment.DenyPolicy(), events);

		var purchased = await service.PurchaseAsync(
			setup.Actor, setup.Inventory.Id, BusinessPermitKind.Literature);

		Assert.IsFalse(purchased.Succeeded);
		Assert.AreEqual(ErrorCode.PolicyDenied, purchased.Error!.Code);
		Assert.AreEqual(500L, environment.Repositories.Characters.Find(
			DomainKeys.Character(setup.Actor.CharacterId))!.Value.Balance);
		Assert.IsEmpty(environment.Repositories.Inventories.Find(
			DomainKeys.Inventory(setup.Inventory.Id))!.Value.Placements);
		Assert.IsEmpty(environment.Repositories.Items.All());
		Assert.IsEmpty(events.Events);
	}

	[TestMethod]
	public async Task CommitFailurePublishesNothingAndLeavesNoPartialMutation()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var setup = await SeedAsync(environment, balance: 500);
		var events = new RecordingHandler<PermitPurchasedReceipt>();
		var audit = new RecordingHandler<AdminAuditFact>();
		var service = CreateService(environment, FeatureTestEnvironment.AllowPolicy(), events, audit);
		environment.Provider.FailNextCommit();

		var purchased = await service.PurchaseAsync(
			setup.Actor, setup.Inventory.Id, BusinessPermitKind.Food);

		Assert.IsFalse(purchased.Succeeded);
		Assert.AreEqual(ErrorCode.InternalError, purchased.Error!.Code);
		Assert.AreEqual(500L, environment.Repositories.Characters.Find(
			DomainKeys.Character(setup.Actor.CharacterId))!.Value.Balance);
		Assert.IsEmpty(environment.Repositories.Inventories.Find(
			DomainKeys.Inventory(setup.Inventory.Id))!.Value.Placements);
		Assert.IsEmpty(environment.Repositories.Items.All());
		Assert.IsEmpty(events.Events);
		Assert.IsEmpty(audit.Events);
	}

	[TestMethod]
	public async Task MissingCapabilityOrNonMainDestinationIsRejected()
	{
		await using var noAccessEnvironment = await FeatureTestEnvironment.CreateAsync();
		var noAccess = await SeedAsync(noAccessEnvironment, balance: 500, grant: false);
		var noAccessResult = await CreateService(noAccessEnvironment).PurchaseAsync(
			noAccess.Actor, noAccess.Inventory.Id);
		Assert.IsFalse(noAccessResult.Succeeded);
		Assert.AreEqual(ErrorCode.Unauthorized, noAccessResult.Error!.Code);

		await using var wrongInventoryEnvironment = await FeatureTestEnvironment.CreateAsync();
		var setup = await SeedAsync(wrongInventoryEnvironment, balance: 500);
		var unrelated = wrongInventoryEnvironment.Inventory(CharacterId.New());
		await wrongInventoryEnvironment.SeedAsync(unitOfWork => unitOfWork.Create(
			wrongInventoryEnvironment.Repositories.Inventories,
			DomainKeys.Inventory(unrelated.Id),
			unrelated));
		wrongInventoryEnvironment.Grant(
			setup.Actor, unrelated.Id, InventoryCapability.View | InventoryCapability.TransferIn);
		var wrongInventoryResult = await CreateService(wrongInventoryEnvironment).PurchaseAsync(
			setup.Actor, unrelated.Id);
		Assert.IsFalse(wrongInventoryResult.Succeeded);
		Assert.AreEqual(ErrorCode.Unauthorized, wrongInventoryResult.Error!.Code);
	}

	[TestMethod]
	public async Task ServerPriceRejectsNegativeConfiguration()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();

		Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PermitPurchaseService(
			environment.Repositories,
			environment.Access,
			environment.Ids,
			environment.Layout,
			environment.Clock,
			FeatureTestEnvironment.AllowPolicy(),
			-1));
	}

	private static PermitPurchaseService CreateService(
		FeatureTestEnvironment environment,
		PolicyPipeline<HL2RPFeaturePolicyContext>? policy = null,
		RecordingHandler<PermitPurchasedReceipt>? events = null,
		RecordingHandler<AdminAuditFact>? audit = null) => new(
			environment.Repositories,
			environment.Access,
			environment.Ids,
			environment.Layout,
			environment.Clock,
			policy ?? FeatureTestEnvironment.AllowPolicy(),
			PermitPurchaseService.DefaultPrice,
			events?.Bus(),
			audit?.Bus());

	private static async Task<PurchaseSetup> SeedAsync(
		FeatureTestEnvironment environment,
		long balance,
		bool full = false,
		BusinessPermitKind? duplicateKind = null,
		bool grant = true)
	{
		var actor = environment.Actor();
		var character = environment.Character(actor, balance);
		var items = new List<ItemRecord>();
		if (full) items.Add(environment.Item(HL2RPIds.Items.Water));
		if (duplicateKind is BusinessPermitKind kind)
		{
			items.Add(environment.Item(
				HL2RPIds.Items.BusinessPermit,
				new Dictionary<string, TypedPayload>(StringComparer.Ordinal)
				{
					["permit"] = HL2RPPersistence.Payload(
						HL2RPPersistence.BusinessPermit,
						new BusinessPermitItemState
						{
							Kind = kind,
							OwnerCharacterId = actor.CharacterId,
							IssuedAtUtc = environment.Clock.UtcNow,
							ExpiresAtUtc = null,
							Revoked = false
						})
				}));
		}
		var placements = items.Select((item, index) => new InventoryPlacement(item.Id, index, 0)).ToArray();
		var inventory = environment.Inventory(
			actor.CharacterId,
			placements,
			width: full ? 1 : 4,
			height: 1);
		await environment.SeedAsync(unitOfWork =>
		{
			unitOfWork.Create(environment.Repositories.Characters,
				DomainKeys.Character(character.Id), character);
			foreach (var item in items)
				unitOfWork.Create(environment.Repositories.Items, DomainKeys.Item(item.Id), item);
			unitOfWork.Create(environment.Repositories.Inventories,
				DomainKeys.Inventory(inventory.Id), inventory);
		});
		if (grant)
			environment.Grant(actor, inventory.Id, InventoryCapability.View | InventoryCapability.TransferIn);
		return new PurchaseSetup(actor, inventory);
	}

	private sealed record PurchaseSetup(InventoryActor Actor, InventoryRecord Inventory);
}
