#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Features;

[TestClass]
public sealed class CommerceServiceTests
{
	[TestMethod]
	public async Task VendorBuyCommitsBalanceStockInventoryItemAndReceiptOnce()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var seed = await SeedVendorAsync( environment );
		var events = new RecordingHandler<CommerceReceipt>();
		var service = CreateService( environment, FeatureTestEnvironment.AllowPolicy(), events );

		var result = await service.BuyAsync(
			seed.Actor,
			seed.SessionId,
			seed.Inventory.Id,
			new DefinitionId( HL2RPIds.Items.Water ),
			2 );

		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		Assert.AreEqual( 80L, FindCharacter( environment, seed.Actor.CharacterId ).Balance );
		Assert.AreEqual( 3, FindVendor( environment, seed.VendorId ).Stock[0].Quantity );
		Assert.HasCount( 3, FindInventory( environment, seed.Inventory.Id ).Placements );
		Assert.HasCount( 3, environment.Repositories.Items.All() );
		Assert.HasCount( 1, events.Events );
		Assert.AreEqual( result.Value.CommitSequence, events.Events[0].CommitSequence );
	}

	[TestMethod]
	public async Task VendorSellDerivesCurrentPlacementAndCommitsAllSides()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var seed = await SeedVendorAsync( environment, includeSellItem: true );
		var service = CreateService( environment, FeatureTestEnvironment.AllowPolicy() );

		var result = await service.SellAsync(
			seed.Actor,
			seed.SessionId,
			seed.Inventory.Id,
			seed.SellItem!.Id );

		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		Assert.AreEqual( 105L, FindCharacter( environment, seed.Actor.CharacterId ).Balance );
		Assert.AreEqual( 6, FindVendor( environment, seed.VendorId ).Stock[0].Quantity );
		Assert.IsNull( environment.Repositories.Items.Find( DomainKeys.Item( seed.SellItem.Id ) ) );
		Assert.IsNull( FindInventory( environment, seed.Inventory.Id ).Find( seed.SellItem.Id ) );
	}

	[TestMethod]
	public async Task MissingPermitInsufficientFundsAndStockExhaustionFailWithoutMutation()
	{
		await using var noPermitEnvironment = await FeatureTestEnvironment.CreateAsync();
		var noPermit = await SeedVendorAsync( noPermitEnvironment, withPermit: false );
		var noPermitResult = await CreateService(
			noPermitEnvironment,
			FeatureTestEnvironment.AllowPolicy() ).BuyAsync(
				noPermit.Actor,
				noPermit.SessionId,
				noPermit.Inventory.Id,
				new DefinitionId( HL2RPIds.Items.Water ),
				1 );

		await using var poorEnvironment = await FeatureTestEnvironment.CreateAsync();
		var poor = await SeedVendorAsync( poorEnvironment, balance: 5 );
		var poorResult = await CreateService( poorEnvironment, FeatureTestEnvironment.AllowPolicy() ).BuyAsync(
			poor.Actor,
			poor.SessionId,
			poor.Inventory.Id,
			new DefinitionId( HL2RPIds.Items.Water ),
			1 );

		await using var emptyEnvironment = await FeatureTestEnvironment.CreateAsync();
		var empty = await SeedVendorAsync( emptyEnvironment, stockQuantity: 0 );
		var emptyResult = await CreateService( emptyEnvironment, FeatureTestEnvironment.AllowPolicy() ).BuyAsync(
			empty.Actor,
			empty.SessionId,
			empty.Inventory.Id,
			new DefinitionId( HL2RPIds.Items.Water ),
			1 );

		Assert.AreEqual( ErrorCode.PolicyDenied, noPermitResult.Error!.Code );
		Assert.AreEqual( ErrorCode.Conflict, poorResult.Error!.Code );
		Assert.AreEqual( ErrorCode.Conflict, emptyResult.Error!.Code );
		Assert.AreEqual( 100L, FindCharacter( noPermitEnvironment, noPermit.Actor.CharacterId ).Balance );
		Assert.AreEqual( 5L, FindCharacter( poorEnvironment, poor.Actor.CharacterId ).Balance );
		Assert.AreEqual( 0, FindVendor( emptyEnvironment, empty.VendorId ).Stock[0].Quantity );
	}

	[TestMethod]
	public async Task FullInventoryPriceOverflowAndPolicyVetoLeaveStateUntouched()
	{
		await using var fullEnvironment = await FeatureTestEnvironment.CreateAsync();
		var full = await SeedVendorAsync( fullEnvironment, inventoryWidth: 1, inventoryHeight: 1 );
		var fullResult = await CreateService( fullEnvironment, FeatureTestEnvironment.AllowPolicy() ).BuyAsync(
			full.Actor,
			full.SessionId,
			full.Inventory.Id,
			new DefinitionId( HL2RPIds.Items.Water ),
			1 );

		await using var overflowEnvironment = await FeatureTestEnvironment.CreateAsync();
		var overflow = await SeedVendorAsync(
			overflowEnvironment,
			stockQuantity: 2,
			unitPrice: long.MaxValue );
		var overflowResult = await CreateService(
			overflowEnvironment,
			FeatureTestEnvironment.AllowPolicy() ).BuyAsync(
				overflow.Actor,
				overflow.SessionId,
				overflow.Inventory.Id,
				new DefinitionId( HL2RPIds.Items.Water ),
				2 );

		await using var vetoEnvironment = await FeatureTestEnvironment.CreateAsync();
		var veto = await SeedVendorAsync( vetoEnvironment );
		var vetoResult = await CreateService( vetoEnvironment, FeatureTestEnvironment.DenyPolicy() ).BuyAsync(
			veto.Actor,
			veto.SessionId,
			veto.Inventory.Id,
			new DefinitionId( HL2RPIds.Items.Water ),
			1 );

		Assert.AreEqual( ErrorCode.Conflict, fullResult.Error!.Code );
		Assert.AreEqual( ErrorCode.Conflict, overflowResult.Error!.Code );
		Assert.AreEqual( ErrorCode.PolicyDenied, vetoResult.Error!.Code );
		Assert.AreEqual( 100L, FindCharacter( fullEnvironment, full.Actor.CharacterId ).Balance );
		Assert.AreEqual( 5, FindVendor( fullEnvironment, full.VendorId ).Stock[0].Quantity );
		Assert.AreEqual( 5, FindVendor( vetoEnvironment, veto.VendorId ).Stock[0].Quantity );
	}

	[TestMethod]
	public async Task InjectedCommitFailurePublishesNothingAndLeavesEveryAggregateUnchanged()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var seed = await SeedVendorAsync( environment );
		var events = new RecordingHandler<CommerceReceipt>();
		var service = CreateService( environment, FeatureTestEnvironment.AllowPolicy(), events );
		environment.Provider.FailNextCommit();

		var result = await service.BuyAsync(
			seed.Actor,
			seed.SessionId,
			seed.Inventory.Id,
			new DefinitionId( HL2RPIds.Items.Water ),
			1 );

		Assert.AreEqual( ErrorCode.InternalError, result.Error!.Code );
		Assert.AreEqual( 100L, FindCharacter( environment, seed.Actor.CharacterId ).Balance );
		Assert.AreEqual( 5, FindVendor( environment, seed.VendorId ).Stock[0].Quantity );
		Assert.HasCount( 1, FindInventory( environment, seed.Inventory.Id ).Placements );
		Assert.HasCount( 1, environment.Repositories.Items.All() );
		Assert.IsEmpty( events.Events );
	}

	[TestMethod]
	public async Task InterleavedVendorSessionRevocationRejectsPurchaseWithIndependentInventoryGrant()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var seed = await SeedVendorAsync( environment );
		var events = new RecordingHandler<CommerceReceipt>();
		var service = CreateService( environment, FeatureTestEnvironment.AllowPolicy(), events );
		environment.Provider.InterleaveNextCommit( () =>
		{
			Assert.IsTrue( environment.Sessions.Revoke( seed.SessionId ) );
			return Task.CompletedTask;
		} );

		var result = await service.BuyAsync(
			seed.Actor,
			seed.SessionId,
			seed.Inventory.Id,
			new DefinitionId( HL2RPIds.Items.Water ),
			1 );

		Assert.AreEqual( ErrorCode.Conflict, result.Error!.Code );
		Assert.AreEqual( 100L, FindCharacter( environment, seed.Actor.CharacterId ).Balance );
		Assert.AreEqual( 5, FindVendor( environment, seed.VendorId ).Stock[0].Quantity );
		Assert.HasCount( 1, FindInventory( environment, seed.Inventory.Id ).Placements );
		Assert.HasCount( 1, environment.Repositories.Items.All() );
		Assert.IsEmpty( events.Events );
	}

	[TestMethod]
	public async Task InterleavedPermitRevocationRejectsPurchaseThatConsultedNestedAuthorizationState()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var seed = await SeedVendorAsync( environment );
		var permitId = seed.Inventory.Placements[0].ItemId;
		var events = new RecordingHandler<CommerceReceipt>();
		var service = CreateService( environment, FeatureTestEnvironment.AllowPolicy(), events );
		environment.Provider.InterleaveNextCommit( async () =>
		{
			var permit = environment.Repositories.Items.Find( DomainKeys.Item( permitId ) )!;
			var state = HL2RPPersistence.BusinessPermit.Deserialize(
				permit.Value.Traits["permit"].Data,
				permit.Value.Traits["permit"].TypeVersion );
			var traits = new Dictionary<string, TypedPayload>( permit.Value.Traits, StringComparer.Ordinal )
			{
				["permit"] = HL2RPPersistence.Payload(
					HL2RPPersistence.BusinessPermit, state with { Revoked = true } )
			};
			await using var revoke = environment.Provider.BeginUnitOfWork();
			var editor = revoke.Edit( environment.Repositories.Items, permit )!;
			editor.Replace( permit.Value with { Traits = traits } );
			revoke.Save( editor );
			var committed = await revoke.CommitAsync();
			Assert.IsTrue( committed.Succeeded, committed.Error?.Message );
		} );

		var result = await service.BuyAsync(
			seed.Actor,
			seed.SessionId,
			seed.Inventory.Id,
			new DefinitionId( HL2RPIds.Items.Water ),
			1 );

		Assert.AreEqual( ErrorCode.Conflict, result.Error!.Code );
		Assert.AreEqual( 100L, FindCharacter( environment, seed.Actor.CharacterId ).Balance );
		Assert.AreEqual( 5, FindVendor( environment, seed.VendorId ).Stock[0].Quantity );
		Assert.HasCount( 1, FindInventory( environment, seed.Inventory.Id ).Placements );
		Assert.HasCount( 1, environment.Repositories.Items.All() );
		Assert.IsEmpty( events.Events );
	}

	[TestMethod]
	public async Task MachineDerivesProductFromBoundEntityAndEnforcesCooldown()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor();
		var character = environment.Character( actor, balance: 20 );
		var inventory = environment.Inventory( actor.CharacterId );
		var machineId = SceneEntityId.New();
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unitOfWork.Create( environment.Repositories.Inventories, DomainKeys.Inventory( inventory.Id ), inventory );
			unitOfWork.Create(
				environment.Repositories.SceneEntities,
				DomainKeys.SceneEntity( machineId ),
				new PersistentSceneEntityRecord
				{
					Id = machineId,
					Kind = "ration_dispenser",
					State = HL2RPPersistence.Payload(
						HL2RPPersistence.MachineState,
						new MachineEntityState { Stock = 2, UnitPrice = 5, CooldownSeconds = 4.5, CooldownUntilUtc = null } )
				} );
		} );
		environment.Grant( actor, inventory.Id, InventoryCapability.View | InventoryCapability.TransferIn );
		var session = environment.Sessions.Bind( machineId );
		var service = CreateService( environment, FeatureTestEnvironment.AllowPolicy() );

		var first = await service.PurchaseFromMachineAsync( actor, session, inventory.Id );
		var cooldown = await service.PurchaseFromMachineAsync( actor, session, inventory.Id );

		Assert.IsTrue( first.Succeeded, first.Error?.Message );
		Assert.AreEqual( ErrorCode.Conflict, cooldown.Error!.Code );
		Assert.AreEqual( 15L, FindCharacter( environment, actor.CharacterId ).Balance );
		Assert.AreEqual( 1L, FindMachine( environment, machineId ).Stock );
		Assert.AreEqual(
			environment.Clock.UtcNow + TimeSpan.FromSeconds( 4.5 ),
			FindMachine( environment, machineId ).CooldownUntilUtc );
		var dispensed = environment.Repositories.Items.Find( DomainKeys.Item( first.Value.ItemIds[0] ) )!.Value;
		Assert.AreEqual( HL2RPIds.Items.Ration, dispensed.Definition.Value );
	}

	[TestMethod]
	public async Task VendorAcceptsPermitInsideCycleSafeNestedBagGraph()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor();
		var character = environment.Character( actor );
		var suitcase = environment.Item( HL2RPIds.Items.Suitcase );
		var permit = environment.Item(
			HL2RPIds.Items.BusinessPermit,
			new Dictionary<string, TypedPayload>
			{
				["permit"] = HL2RPPersistence.Payload(
					HL2RPPersistence.BusinessPermit,
					new BusinessPermitItemState
					{
						Kind = BusinessPermitKind.Food,
						OwnerCharacterId = actor.CharacterId,
						IssuedAtUtc = environment.Clock.UtcNow,
						ExpiresAtUtc = null,
						Revoked = false
					} )
			} );
		var main = environment.Inventory(
			actor.CharacterId,
			new[] { new InventoryPlacement( suitcase.Id, 0, 0 ) } );
		var bag = new InventoryRecord
		{
			Id = InventoryId.New(),
			Owner = InventoryOwner.ParentItem( suitcase.Id ),
			Width = 3,
			Height = 3,
			Placements = new[] { new InventoryPlacement( permit.Id, 0, 0 ) }
		};
		var vendorId = SceneEntityId.New();
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unitOfWork.Create( environment.Repositories.Items, DomainKeys.Item( suitcase.Id ), suitcase );
			unitOfWork.Create( environment.Repositories.Items, DomainKeys.Item( permit.Id ), permit );
			unitOfWork.Create( environment.Repositories.Inventories, DomainKeys.Inventory( main.Id ), main );
			unitOfWork.Create( environment.Repositories.Inventories, DomainKeys.Inventory( bag.Id ), bag );
			unitOfWork.Create(
				environment.Repositories.SceneEntities,
				DomainKeys.SceneEntity( vendorId ),
				new PersistentSceneEntityRecord
				{
					Id = vendorId,
					Kind = "vendor",
					State = HL2RPPersistence.Payload(
						HL2RPPersistence.VendorState,
						new VendorEntityState
						{
							RequiredPermit = BusinessPermitKind.Food,
							Stock = new[]
							{
								new VendorStockEntry
								{
									Definition = new DefinitionId( HL2RPIds.Items.Water ),
									Quantity = 1,
									UnitPrice = 10
								}
							}
						} )
				} );
		} );
		environment.Grant( actor, main.Id, InventoryCapability.View | InventoryCapability.TransferIn );
		var session = environment.Sessions.Bind( vendorId );

		var result = await CreateService(
			environment,
			FeatureTestEnvironment.AllowPolicy() ).BuyAsync(
				actor,
				session,
				main.Id,
				new DefinitionId( HL2RPIds.Items.Water ),
				1 );

		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		Assert.AreEqual( 90L, FindCharacter( environment, actor.CharacterId ).Balance );
	}

	private static CommerceService CreateService(
		FeatureTestEnvironment environment,
		PolicyPipeline<HL2RPFeaturePolicyContext> policy,
		RecordingHandler<CommerceReceipt>? events = null ) => new(
		environment.Repositories,
		environment.Schema,
		environment.Access,
		environment.Sessions,
		environment.Ids,
		environment.Layout,
		new HL2RPItemFactory(),
		environment.Clock,
		policy,
		events?.Bus() );

	private static async Task<VendorSeed> SeedVendorAsync(
		FeatureTestEnvironment environment,
		long balance = 100,
		int stockQuantity = 5,
		long unitPrice = 10,
		bool withPermit = true,
		bool includeSellItem = false,
		int inventoryWidth = 4,
		int inventoryHeight = 4 )
	{
		var actor = environment.Actor();
		var character = environment.Character( actor, balance );
		var items = new List<ItemRecord>();
		var placements = new List<InventoryPlacement>();
		if ( withPermit )
		{
			var permit = environment.Item(
				HL2RPIds.Items.BusinessPermit,
				new Dictionary<string, TypedPayload>
				{
					["permit"] = HL2RPPersistence.Payload(
						HL2RPPersistence.BusinessPermit,
						new BusinessPermitItemState
						{
							Kind = BusinessPermitKind.Food,
							OwnerCharacterId = actor.CharacterId,
							IssuedAtUtc = environment.Clock.UtcNow,
							ExpiresAtUtc = null,
							Revoked = false
						} )
				} );
			items.Add( permit );
			placements.Add( new InventoryPlacement( permit.Id, 0, 0 ) );
		}
		ItemRecord? sellItem = null;
		if ( includeSellItem )
		{
			sellItem = environment.Item( HL2RPIds.Items.Water );
			items.Add( sellItem );
			placements.Add( new InventoryPlacement( sellItem.Id, 1, 0 ) );
		}
		var inventory = environment.Inventory(
			actor.CharacterId,
			placements,
			inventoryWidth,
			inventoryHeight );
		var vendorId = SceneEntityId.New();
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unitOfWork.Create( environment.Repositories.Inventories, DomainKeys.Inventory( inventory.Id ), inventory );
			foreach ( var item in items )
				unitOfWork.Create( environment.Repositories.Items, DomainKeys.Item( item.Id ), item );
			unitOfWork.Create(
				environment.Repositories.SceneEntities,
				DomainKeys.SceneEntity( vendorId ),
				new PersistentSceneEntityRecord
				{
					Id = vendorId,
					Kind = "vendor",
					State = HL2RPPersistence.Payload(
						HL2RPPersistence.VendorState,
						new VendorEntityState
						{
							RequiredPermit = BusinessPermitKind.Food,
							Stock = new[]
							{
								new VendorStockEntry
								{
									Definition = new DefinitionId( HL2RPIds.Items.Water ),
									Quantity = stockQuantity,
									UnitPrice = unitPrice
								}
							}
						} )
				} );
		} );
		environment.Grant(
			actor,
			inventory.Id,
			InventoryCapability.View |
			InventoryCapability.TransferIn |
			InventoryCapability.TransferOut |
			InventoryCapability.Sell );
		return new VendorSeed(
			actor,
			inventory,
			vendorId,
			environment.Sessions.Bind( vendorId ),
			sellItem );
	}

	private static CharacterRecord FindCharacter( FeatureTestEnvironment environment, CharacterId id ) =>
		environment.Repositories.Characters.Find( DomainKeys.Character( id ) )!.Value;
	private static InventoryRecord FindInventory( FeatureTestEnvironment environment, InventoryId id ) =>
		environment.Repositories.Inventories.Find( DomainKeys.Inventory( id ) )!.Value;
	private static VendorEntityState FindVendor( FeatureTestEnvironment environment, SceneEntityId id )
	{
		var entity = environment.Repositories.SceneEntities.Find( DomainKeys.SceneEntity( id ) )!.Value;
		return HL2RPPersistence.VendorState.Deserialize( entity.State.Data, entity.State.TypeVersion );
	}
	private static MachineEntityState FindMachine( FeatureTestEnvironment environment, SceneEntityId id )
	{
		var entity = environment.Repositories.SceneEntities.Find( DomainKeys.SceneEntity( id ) )!.Value;
		return HL2RPPersistence.MachineState.Deserialize( entity.State.Data, entity.State.TypeVersion );
	}

	private sealed record VendorSeed(
		InventoryActor Actor,
		InventoryRecord Inventory,
		SceneEntityId VendorId,
		InteractionSessionId SessionId,
		ItemRecord? SellItem );
}
