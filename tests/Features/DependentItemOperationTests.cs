#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Features;

[TestClass]
public sealed class DependentItemOperationTests
{
	[TestMethod]
	public async Task BagSessionUsesOnlySessionCapabilitiesAndRevokesWhenBagMoves()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor();
		var character = environment.Character( actor );
		var suitcase = environment.Item( HL2RPIds.Items.Suitcase );
		var main = environment.Inventory(
			actor.CharacterId,
			new[] { new InventoryPlacement( suitcase.Id, 0, 0 ) } );
		var bag = new InventoryRecord
		{
			Id = InventoryId.New(),
			Owner = InventoryOwner.ParentItem( suitcase.Id ),
			Width = 3,
			Height = 3
		};
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unitOfWork.Create(
				environment.Repositories.CharacterLifecycleGuards,
				DomainKeys.CharacterLifecycleGuard( character.Id ),
				new CharacterLifecycleGuardRecord { CharacterId = character.Id, ReferenceRevision = 0 } );
			unitOfWork.Create( environment.Repositories.Items, DomainKeys.Item( suitcase.Id ), suitcase );
			unitOfWork.Create( environment.Repositories.Inventories, DomainKeys.Inventory( main.Id ), main );
			unitOfWork.Create( environment.Repositories.Inventories, DomainKeys.Inventory( bag.Id ), bag );
		} );
		environment.Grant( actor, main.Id, InventoryCapability.View | InventoryCapability.Use );
		var sessions = new InteractionSessionService( environment.Clock );
		var service = new BagInteractionService(
			environment.Repositories,
			sessions,
			environment.Access,
			FeatureTestEnvironment.AllowPolicy() );

		var opened = service.Open( actor, main.Id, suitcase.Id );
		var continued = service.Continue( actor, opened.Value.Session.Id );

		Assert.IsTrue( opened.Succeeded, opened.Error?.Message );
		Assert.IsTrue( continued.Succeeded, continued.Error?.Message );
		Assert.IsTrue( environment.Access.Has(
			actor.ConnectionId, actor.CharacterId, bag.Id, InventoryCapability.View | InventoryCapability.TransferOut ) );
		Assert.IsFalse( environment.Access.Has(
			actor.ConnectionId, actor.CharacterId, bag.Id, InventoryCapability.Sell ) );
		service.ItemMoved( suitcase.Id );
		Assert.IsFalse( environment.Access.Has(
			actor.ConnectionId, actor.CharacterId, bag.Id, InventoryCapability.View ) );
		Assert.IsEmpty( sessions.ActiveSessions );
		Assert.AreEqual(
			ErrorCode.Unauthorized,
			service.Continue( actor, opened.Value.Session.Id ).Error!.Code );
	}

	[TestMethod]
	public async Task BagOpenRejectsGloballyKnownItemOutsideClaimedInventory()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor();
		var character = environment.Character( actor );
		var suitcase = environment.Item( HL2RPIds.Items.Suitcase );
		var claimed = environment.Inventory( actor.CharacterId );
		var actual = environment.Inventory(
			actor.CharacterId,
			new[] { new InventoryPlacement( suitcase.Id, 0, 0 ) } );
		var bag = new InventoryRecord
		{
			Id = InventoryId.New(),
			Owner = InventoryOwner.ParentItem( suitcase.Id ),
			Width = 3,
			Height = 3
		};
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unitOfWork.Create( environment.Repositories.Items, DomainKeys.Item( suitcase.Id ), suitcase );
			unitOfWork.Create( environment.Repositories.Inventories, DomainKeys.Inventory( claimed.Id ), claimed );
			unitOfWork.Create( environment.Repositories.Inventories, DomainKeys.Inventory( actual.Id ), actual );
			unitOfWork.Create( environment.Repositories.Inventories, DomainKeys.Inventory( bag.Id ), bag );
		} );
		environment.Grant( actor, claimed.Id, InventoryCapability.View | InventoryCapability.Use );
		var service = new BagInteractionService(
			environment.Repositories,
			new InteractionSessionService( environment.Clock ),
			environment.Access,
			FeatureTestEnvironment.AllowPolicy() );

		var result = service.Open( actor, claimed.Id, suitcase.Id );

		Assert.AreEqual( ErrorCode.Unauthorized, result.Error!.Code );
		Assert.IsFalse( environment.Access.Has(
			actor.ConnectionId, actor.CharacterId, bag.Id, InventoryCapability.View ) );
	}

	[TestMethod]
	public async Task TokenSplitAndCombineCommitExplicitAmountsAndPlacementAtomically()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var seed = await SeedTokensAsync( environment, 10, 5 );
		var service = CreateTokenService( environment );

		var split = await service.SplitAsync(
			seed.Actor, seed.Inventory.Id, seed.Primary.Id, 4 );

		Assert.IsTrue( split.Succeeded, split.Error?.Message );
		Assert.AreEqual( 6L, FindTokenAmount( environment, seed.Primary.Id ) );
		Assert.IsNotNull( split.Value.SecondaryItemId );
		Assert.AreEqual( 4L, FindTokenAmount( environment, split.Value.SecondaryItemId!.Value ) );

		var combined = await service.CombineAsync(
			seed.Actor, seed.Inventory.Id, seed.Primary.Id, seed.Secondary.Id );

		Assert.IsTrue( combined.Succeeded, combined.Error?.Message );
		Assert.AreEqual( 11L, FindTokenAmount( environment, seed.Primary.Id ) );
		Assert.IsNull( environment.Repositories.Items.Find( DomainKeys.Item( seed.Secondary.Id ) ) );
		Assert.IsNull( FindInventory( environment, seed.Inventory.Id ).Find( seed.Secondary.Id ) );
	}

	[TestMethod]
	public async Task InterleavedAccessRevocationRejectsTokenSplitWithoutPartialDocumentsOrEvent()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var seed = await SeedTokensAsync( environment, 10, 5 );
		var events = new RecordingHandler<TokenStackReceipt>();
		var service = new TokenStackService(
			environment.Repositories,
			environment.Access,
			environment.Ids,
			environment.Layout,
			environment.Clock,
			FeatureTestEnvironment.AllowPolicy(),
			events.Bus() );
		var itemCount = environment.Repositories.Items.All().Count;
		environment.Provider.InterleaveNextCommit( () =>
		{
			environment.Access.RevokeConnection( seed.Actor.ConnectionId );
			return Task.CompletedTask;
		} );

		var result = await service.SplitAsync(
			seed.Actor, seed.Inventory.Id, seed.Primary.Id, 4 );

		Assert.AreEqual( ErrorCode.Conflict, result.Error!.Code );
		Assert.AreEqual( 10L, FindTokenAmount( environment, seed.Primary.Id ) );
		Assert.HasCount( 2, FindInventory( environment, seed.Inventory.Id ).Placements );
		Assert.HasCount( itemCount, environment.Repositories.Items.All() );
		Assert.IsEmpty( events.Events );
	}

	[TestMethod]
	public async Task TokenFailureInjectionCapacityOverflowAndWrongItemLeaveStateUntouched()
	{
		await using var failedEnvironment = await FeatureTestEnvironment.CreateAsync();
		var failedSeed = await SeedTokensAsync( failedEnvironment, 10, 5 );
		failedEnvironment.Provider.FailNextCommit();
		var failed = await CreateTokenService( failedEnvironment ).SplitAsync(
			failedSeed.Actor, failedSeed.Inventory.Id, failedSeed.Primary.Id, 4 );

		await using var fullEnvironment = await FeatureTestEnvironment.CreateAsync();
		var fullSeed = await SeedTokensAsync( fullEnvironment, 10, null, width: 1, height: 1 );
		var full = await CreateTokenService( fullEnvironment ).SplitAsync(
			fullSeed.Actor, fullSeed.Inventory.Id, fullSeed.Primary.Id, 4 );

		await using var overflowEnvironment = await FeatureTestEnvironment.CreateAsync();
		var overflowSeed = await SeedTokensAsync( overflowEnvironment, long.MaxValue, 1 );
		var overflow = await CreateTokenService( overflowEnvironment ).CombineAsync(
			overflowSeed.Actor,
			overflowSeed.Inventory.Id,
			overflowSeed.Primary.Id,
			overflowSeed.Secondary.Id );

		await using var wrongEnvironment = await FeatureTestEnvironment.CreateAsync();
		var actor = wrongEnvironment.Actor();
		var character = wrongEnvironment.Character( actor );
		var water = wrongEnvironment.Item( HL2RPIds.Items.Water );
		var inventory = wrongEnvironment.Inventory(
			actor.CharacterId,
			new[] { new InventoryPlacement( water.Id, 0, 0 ) } );
		await wrongEnvironment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create( wrongEnvironment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unitOfWork.Create( wrongEnvironment.Repositories.Items, DomainKeys.Item( water.Id ), water );
			unitOfWork.Create( wrongEnvironment.Repositories.Inventories, DomainKeys.Inventory( inventory.Id ), inventory );
		} );
		wrongEnvironment.Grant(
			actor, inventory.Id, InventoryCapability.View | InventoryCapability.Move | InventoryCapability.Use );
		var wrong = await CreateTokenService( wrongEnvironment ).SplitAsync( actor, inventory.Id, water.Id, 1 );

		Assert.AreEqual( ErrorCode.InternalError, failed.Error!.Code );
		Assert.AreEqual( 10L, FindTokenAmount( failedEnvironment, failedSeed.Primary.Id ) );
		Assert.HasCount( 2, FindInventory( failedEnvironment, failedSeed.Inventory.Id ).Placements );
		Assert.AreEqual( ErrorCode.Conflict, full.Error!.Code );
		Assert.AreEqual( ErrorCode.Conflict, overflow.Error!.Code );
		Assert.AreEqual( ErrorCode.Unauthorized, wrong.Error!.Code );
	}

	[TestMethod]
	public async Task CombineLockUsesDoorSessionRoleMembershipAndOneCommit()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var seed = await SeedLockAsync( environment, HL2RPIds.Factions.CivilProtection );
		var events = new RecordingHandler<CombineLockInstalledReceipt>();
		var service = new CombineLockService(
			environment.Repositories,
			environment.Access,
			environment.Sessions,
			environment.Clock,
			FeatureTestEnvironment.AllowPolicy(),
			events.Bus() );

		var result = await service.InstallAsync(
			seed.Actor, seed.SessionId, seed.Inventory.Id, seed.Kit.Id );

		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		Assert.IsNull( environment.Repositories.Items.Find( DomainKeys.Item( seed.Kit.Id ) ) );
		Assert.IsNull( FindInventory( environment, seed.Inventory.Id ).Find( seed.Kit.Id ) );
		var door = environment.Repositories.SceneEntities.Find( DomainKeys.SceneEntity( seed.DoorId ) )!.Value;
		Assert.IsTrue( HL2RPPersistence.DoorState.Deserialize(
			door.State.Data, door.State.TypeVersion ).CombineLocked );
		Assert.IsEmpty( environment.Repositories.CharacterReferences.All() );
		Assert.HasCount( 1, events.Events );
	}

	[TestMethod]
	public async Task CombineLockRejectsStaleSessionWrongRoleWrongItemAndCommitFailure()
	{
		await using var staleEnvironment = await FeatureTestEnvironment.CreateAsync();
		var staleSeed = await SeedLockAsync( staleEnvironment, HL2RPIds.Factions.CivilProtection );
		var staleService = CreateLockService( staleEnvironment );
		var stale = await staleService.InstallAsync(
			staleSeed.Actor, InteractionSessionId.New(), staleSeed.Inventory.Id, staleSeed.Kit.Id );

		await using var roleEnvironment = await FeatureTestEnvironment.CreateAsync();
		var roleSeed = await SeedLockAsync( roleEnvironment, HL2RPIds.Factions.Citizen );
		var role = await CreateLockService( roleEnvironment ).InstallAsync(
			roleSeed.Actor, roleSeed.SessionId, roleSeed.Inventory.Id, roleSeed.Kit.Id );

		await using var wrongEnvironment = await FeatureTestEnvironment.CreateAsync();
		var wrongSeed = await SeedLockAsync( wrongEnvironment, HL2RPIds.Factions.CivilProtection, wrongItem: true );
		var wrong = await CreateLockService( wrongEnvironment ).InstallAsync(
			wrongSeed.Actor, wrongSeed.SessionId, wrongSeed.Inventory.Id, wrongSeed.Kit.Id );

		await using var failedEnvironment = await FeatureTestEnvironment.CreateAsync();
		var failedSeed = await SeedLockAsync( failedEnvironment, HL2RPIds.Factions.Overwatch );
		failedEnvironment.Provider.FailNextCommit();
		var failed = await CreateLockService( failedEnvironment ).InstallAsync(
			failedSeed.Actor, failedSeed.SessionId, failedSeed.Inventory.Id, failedSeed.Kit.Id );

		Assert.AreEqual( ErrorCode.Unauthorized, stale.Error!.Code );
		Assert.AreEqual( ErrorCode.Unauthorized, role.Error!.Code );
		Assert.AreEqual( ErrorCode.Unauthorized, wrong.Error!.Code );
		Assert.AreEqual( ErrorCode.InternalError, failed.Error!.Code );
		Assert.IsFalse( FindDoorState( failedEnvironment, failedSeed.DoorId ).CombineLocked );
		Assert.IsNotNull( failedEnvironment.Repositories.Items.Find( DomainKeys.Item( failedSeed.Kit.Id ) ) );
		Assert.IsEmpty( failedEnvironment.Repositories.CharacterReferences.All() );
	}

	[TestMethod]
	public async Task CombineLockKitWithRemainingUsesIsDecrementedRatherThanDeleted()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var seed = await SeedLockAsync(
			environment,
			HL2RPIds.Factions.CivilProtection,
			remainingInstallations: 2 );

		var result = await CreateLockService( environment ).InstallAsync(
			seed.Actor, seed.SessionId, seed.Inventory.Id, seed.Kit.Id );

		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		Assert.AreEqual( 1, result.Value.RemainingInstallations );
		var stored = environment.Repositories.Items.Find( DomainKeys.Item( seed.Kit.Id ) )!.Value;
		var state = HL2RPPersistence.CombineLockKit.Deserialize(
			stored.Traits["lock_kit"].Data,
			stored.Traits["lock_kit"].TypeVersion );
		Assert.AreEqual( 1, state.RemainingInstallations );
		Assert.IsNotNull( FindInventory( environment, seed.Inventory.Id ).Find( seed.Kit.Id ) );
	}

	[TestMethod]
	public async Task DoorOwnershipClaimAndReleaseAreIndependentFromDoorLockState()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var seed = await SeedDoorAsync( environment );
		var events = new RecordingHandler<DoorOwnershipReceipt>();
		var service = new DoorOwnershipService(
			environment.Repositories,
			environment.Sessions,
			environment.Clock,
			FeatureTestEnvironment.AllowPolicy(),
			events.Bus() );

		var claimed = await service.ClaimAsync( seed.Actor, seed.SessionId );
		var doorAfterClaim = FindDoorState( environment, seed.DoorId );

		Assert.IsTrue( claimed.Succeeded, claimed.Error?.Message );
		Assert.IsFalse( doorAfterClaim.CombineLocked );
		Assert.HasCount( 1, events.Events );
		Assert.IsTrue( events.Events[0].Claimed );

		var released = await service.ReleaseAsync( seed.Actor, seed.SessionId );

		Assert.IsTrue( released.Succeeded, released.Error?.Message );
		Assert.IsEmpty( environment.Repositories.CharacterReferences.All() );
		Assert.HasCount( 2, events.Events );
		Assert.IsFalse( events.Events[1].Claimed );
	}

	[TestMethod]
	public async Task DoorOwnershipRequiresLiveSessionAndIsRemovedByGenericCharacterDeletion()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var seed = await SeedDoorAsync( environment, includeCharacterSlot: true );
		var service = new DoorOwnershipService(
			environment.Repositories,
			environment.Sessions,
			environment.Clock,
			FeatureTestEnvironment.AllowPolicy() );

		var stale = await service.ClaimAsync( seed.Actor, InteractionSessionId.New() );
		var claimed = await service.ClaimAsync( seed.Actor, seed.SessionId );
		var characterService = new CharacterService(
			environment.Repositories,
			environment.Schema,
			new AllowModels(),
			new HL2RPCharacterStateFactory(),
			HL2RPInitializers.CreateDefault(),
			environment.Ids,
			environment.Clock,
			environment.Layout,
			environment.Schema.CreatePolicyPipeline<CharacterCreationContext>().Value,
			environment.Schema.CreatePolicyPipeline<CharacterDeletionContext>().Value );
		var deleted = await characterService.DeleteAsync( seed.Actor.AccountId, seed.Actor.CharacterId );

		Assert.AreEqual( ErrorCode.Unauthorized, stale.Error!.Code );
		Assert.IsTrue( claimed.Succeeded, claimed.Error?.Message );
		Assert.IsTrue( deleted.Succeeded, deleted.Error?.Message );
		Assert.IsEmpty( environment.Repositories.CharacterReferences.All() );
		Assert.IsNotNull( environment.Repositories.SceneEntities.Find( DomainKeys.SceneEntity( seed.DoorId ) ) );
		Assert.IsFalse( FindDoorState( environment, seed.DoorId ).CombineLocked );
	}

	[TestMethod]
	public async Task RequestDeviceNormalizesFactEnforcesCooldownAndPublishesAfterCommit()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor();
		var character = environment.Character( actor );
		var device = environment.Item(
			HL2RPIds.Items.RequestDevice,
			new Dictionary<string, TypedPayload>
			{
				["request_device"] = HL2RPPersistence.Payload(
					HL2RPPersistence.RequestDevice,
					new RequestDeviceItemState { Powered = true, LastRequestAtUtc = null } )
			} );
		var inventory = environment.Inventory(
			actor.CharacterId,
			new[] { new InventoryPlacement( device.Id, 0, 0 ) } );
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unitOfWork.Create( environment.Repositories.Items, DomainKeys.Item( device.Id ), device );
			unitOfWork.Create( environment.Repositories.Inventories, DomainKeys.Inventory( inventory.Id ), inventory );
		} );
		environment.Grant( actor, inventory.Id, InventoryCapability.View | InventoryCapability.Use );
		var events = new RecordingHandler<RequestFact>();
		var service = new RequestDeviceService(
			environment.Repositories,
			environment.Access,
			environment.Clock,
			FeatureTestEnvironment.AllowPolicy(),
			events.Bus() );

		var first = await service.SendAsync( actor, inventory.Id, device.Id, "  Need assistance  " );
		var cooldown = await service.SendAsync( actor, inventory.Id, device.Id, "Again" );
		environment.Clock.Advance( RequestDeviceService.RequestCooldown );
		environment.Provider.FailNextCommit();
		var failed = await service.SendAsync( actor, inventory.Id, device.Id, "After cooldown" );

		Assert.IsTrue( first.Succeeded, first.Error?.Message );
		Assert.AreEqual( "Need assistance", first.Value.Text );
		Assert.AreEqual( HL2RPIds.Channels.Request, first.Value.ChannelId );
		Assert.AreEqual( ErrorCode.Conflict, cooldown.Error!.Code );
		Assert.AreEqual( ErrorCode.InternalError, failed.Error!.Code );
		Assert.HasCount( 1, events.Events );
		var stored = environment.Repositories.Items.Find( DomainKeys.Item( device.Id ) )!.Value;
		var state = HL2RPPersistence.RequestDevice.Deserialize(
			stored.Traits["request_device"].Data,
			stored.Traits["request_device"].TypeVersion );
		Assert.AreEqual( first.Value.RequestedAtUtc, state.LastRequestAtUtc );
	}

	private static TokenStackService CreateTokenService( FeatureTestEnvironment environment ) => new(
		environment.Repositories,
		environment.Access,
		environment.Ids,
		environment.Layout,
		environment.Clock,
		FeatureTestEnvironment.AllowPolicy() );

	private static CombineLockService CreateLockService( FeatureTestEnvironment environment ) => new(
		environment.Repositories,
		environment.Access,
		environment.Sessions,
		environment.Clock,
		FeatureTestEnvironment.AllowPolicy() );

	private static async Task<TokenSeed> SeedTokensAsync(
		FeatureTestEnvironment environment,
		long primaryAmount,
		long? secondaryAmount,
		int width = 4,
		int height = 4 )
	{
		var actor = environment.Actor();
		var character = environment.Character( actor );
		var primary = Token( environment, primaryAmount );
		var secondary = secondaryAmount is null ? null : Token( environment, secondaryAmount.Value );
		var placements = new List<InventoryPlacement> { new( primary.Id, 0, 0 ) };
		if ( secondary is not null ) placements.Add( new InventoryPlacement( secondary.Id, 1, 0 ) );
		var inventory = environment.Inventory( actor.CharacterId, placements, width, height );
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unitOfWork.Create( environment.Repositories.Items, DomainKeys.Item( primary.Id ), primary );
			if ( secondary is not null )
				unitOfWork.Create( environment.Repositories.Items, DomainKeys.Item( secondary.Id ), secondary );
			unitOfWork.Create( environment.Repositories.Inventories, DomainKeys.Inventory( inventory.Id ), inventory );
		} );
		environment.Grant(
			actor, inventory.Id, InventoryCapability.View | InventoryCapability.Move | InventoryCapability.Use );
		return new TokenSeed( actor, inventory, primary, secondary! );
	}

	private static ItemRecord Token( FeatureTestEnvironment environment, long amount ) => environment.Item(
		HL2RPIds.Items.TokenStack,
		new Dictionary<string, TypedPayload>
		{
			["tokens"] = HL2RPPersistence.Payload(
				HL2RPPersistence.TokenStack,
				new TokenStackItemState { Amount = amount } )
		} );

	private static async Task<LockSeed> SeedLockAsync(
		FeatureTestEnvironment environment,
		string faction,
		bool wrongItem = false,
		int remainingInstallations = 1 )
	{
		var actor = environment.Actor();
		var character = environment.Character( actor ) with
		{
			Faction = new FactionId( faction ),
			Class = faction == HL2RPIds.Factions.CivilProtection
				? new ClassId( HL2RPIds.Classes.Recruit )
				: null
		};
		var kit = wrongItem
			? environment.Item( HL2RPIds.Items.Water )
			: environment.Item(
				HL2RPIds.Items.CombineLockKit,
				new Dictionary<string, TypedPayload>
				{
					["lock_kit"] = HL2RPPersistence.Payload(
						HL2RPPersistence.CombineLockKit,
						new CombineLockKitItemState { RemainingInstallations = remainingInstallations } )
				} );
		var inventory = environment.Inventory(
			actor.CharacterId,
			new[] { new InventoryPlacement( kit.Id, 0, 0 ) } );
		var doorId = SceneEntityId.New();
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unitOfWork.Create( environment.Repositories.Items, DomainKeys.Item( kit.Id ), kit );
			unitOfWork.Create( environment.Repositories.Inventories, DomainKeys.Inventory( inventory.Id ), inventory );
			unitOfWork.Create(
				environment.Repositories.SceneEntities,
				DomainKeys.SceneEntity( doorId ),
				new PersistentSceneEntityRecord
				{
					Id = doorId,
					Kind = "door",
					State = HL2RPPersistence.Payload(
						HL2RPPersistence.DoorState,
						new DoorEntityState { CombineLocked = false, IsOpen = false } )
				} );
		} );
		environment.Grant( actor, inventory.Id, InventoryCapability.View | InventoryCapability.Use );
		return new LockSeed(
			actor,
			inventory,
			kit,
			doorId,
			environment.Sessions.Bind( doorId, InteractionSessionKind.Door ) );
	}

	private static async Task<DoorSeed> SeedDoorAsync(
		FeatureTestEnvironment environment,
		bool includeCharacterSlot = false )
	{
		var actor = environment.Actor();
		var character = environment.Character( actor );
		var doorId = SceneEntityId.New();
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unitOfWork.Create(
				environment.Repositories.CharacterLifecycleGuards,
				DomainKeys.CharacterLifecycleGuard( character.Id ),
				new CharacterLifecycleGuardRecord { CharacterId = character.Id, ReferenceRevision = 0 } );
			if ( includeCharacterSlot )
			{
				unitOfWork.Create(
					environment.Repositories.CharacterSlots,
					DomainKeys.CharacterSlot( character.AccountId, character.Slot ),
					new CharacterSlotRecord
					{
						AccountId = character.AccountId,
						Slot = character.Slot,
						CharacterId = character.Id
					} );
			}
			unitOfWork.Create(
				environment.Repositories.SceneEntities,
				DomainKeys.SceneEntity( doorId ),
				new PersistentSceneEntityRecord
				{
					Id = doorId,
					Kind = "door",
					State = HL2RPPersistence.Payload(
						HL2RPPersistence.DoorState,
						new DoorEntityState { CombineLocked = false, IsOpen = false } )
				} );
		} );
		return new DoorSeed(
			actor,
			doorId,
			environment.Sessions.Bind( doorId, InteractionSessionKind.Door ) );
	}

	private static long FindTokenAmount( FeatureTestEnvironment environment, ItemId id )
	{
		var item = environment.Repositories.Items.Find( DomainKeys.Item( id ) )!.Value;
		return HL2RPPersistence.TokenStack.Deserialize(
			item.Traits["tokens"].Data,
			item.Traits["tokens"].TypeVersion ).Amount;
	}

	private static InventoryRecord FindInventory( FeatureTestEnvironment environment, InventoryId id ) =>
		environment.Repositories.Inventories.Find( DomainKeys.Inventory( id ) )!.Value;

	private static DoorEntityState FindDoorState( FeatureTestEnvironment environment, SceneEntityId id )
	{
		var door = environment.Repositories.SceneEntities.Find( DomainKeys.SceneEntity( id ) )!.Value;
		return HL2RPPersistence.DoorState.Deserialize( door.State.Data, door.State.TypeVersion );
	}

	private sealed record TokenSeed(
		InventoryActor Actor,
		InventoryRecord Inventory,
		ItemRecord Primary,
		ItemRecord Secondary );

	private sealed record LockSeed(
		InventoryActor Actor,
		InventoryRecord Inventory,
		ItemRecord Kit,
		SceneEntityId DoorId,
		InteractionSessionId SessionId );

	private sealed record DoorSeed(
		InventoryActor Actor,
		SceneEntityId DoorId,
		InteractionSessionId SessionId );

	private sealed class AllowModels : ICharacterModelCatalog
	{
		public bool IsAllowed( DefinitionId model, FactionId faction, ClassId? characterClass ) => true;
	}
}
