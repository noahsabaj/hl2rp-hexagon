#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Features;

[TestClass]
public sealed class SceneEntityStateServiceTests
{
	[TestMethod]
	public void ForcefieldRulesKeepVisibilityCollisionAndRoleAuthorizationConsistent()
	{
		var citizen = new FactionId( HL2RPIds.Factions.Citizen );
		var combine = new FactionId( HL2RPIds.Factions.CivilProtection );
		var disabled = new ForcefieldEntityState { Enabled = false, CombineOnly = true };
		var publicField = new ForcefieldEntityState { Enabled = true, CombineOnly = false };
		var combineField = new ForcefieldEntityState { Enabled = true, CombineOnly = true };

		Assert.IsFalse( ForcefieldEntityRules.IsVisible( disabled ) );
		Assert.IsFalse( ForcefieldEntityRules.IsSolid( disabled ) );
		Assert.IsTrue( ForcefieldEntityRules.IsPassageAuthorized( disabled, citizen ) );
		Assert.IsTrue( ForcefieldEntityRules.IsVisible( publicField ) );
		Assert.IsFalse( ForcefieldEntityRules.IsSolid( publicField ) );
		Assert.IsTrue( ForcefieldEntityRules.IsPassageAuthorized( publicField, citizen ) );
		Assert.IsTrue( ForcefieldEntityRules.IsVisible( combineField ) );
		Assert.IsTrue( ForcefieldEntityRules.IsSolid( combineField ) );
		Assert.IsFalse( ForcefieldEntityRules.IsPassageAuthorized( combineField, citizen ) );
		Assert.IsTrue( ForcefieldEntityRules.IsPassageAuthorized( combineField, combine ) );
	}

	[TestMethod]
	public async Task DoorToggleCommitsBeforePublishingAndFailureLeavesPhysicalEventSilent()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var seed = await SeedDoorAsync( environment, combineLocked: false );
		var events = new RecordingHandler<DoorStateChangedEvent>();
		var service = new HL2RPSceneEntityBehaviorService(
			environment.Repositories, environment.Sessions, environment.Clock,
			FeatureTestEnvironment.AllowPolicy(), doorEvents: events.Bus() );

		var opened = await service.ToggleDoorAsync( seed.Actor, seed.SessionId );

		Assert.IsTrue( opened.Succeeded, opened.Error?.Message );
		Assert.IsTrue( DoorState( environment, seed.EntityId ).IsOpen );
		Assert.HasCount( 1, events.Events );
		Assert.IsTrue( events.Events[0].State.IsOpen );
		Assert.AreEqual( environment.Provider.Health.Sequence, events.Events[0].CommitSequence );

		environment.Provider.FailNextCommit();
		var failed = await service.ToggleDoorAsync( seed.Actor, seed.SessionId );
		Assert.IsTrue( failed.Failed );
		Assert.IsTrue( DoorState( environment, seed.EntityId ).IsOpen );
		Assert.HasCount( 1, events.Events );
	}

	[TestMethod]
	public async Task OneHostSessionCanClaimOperateCloseAndReleaseAnOwnableDoor()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var seed = await SeedDoorAsync( environment, combineLocked: false );
		var policy = FeatureTestEnvironment.AllowPolicy();
		var ownership = new DoorOwnershipService(
			environment.Repositories, environment.Sessions, environment.Clock, policy );
		var behavior = new HL2RPSceneEntityBehaviorService(
			environment.Repositories, environment.Sessions, environment.Clock, policy );

		Assert.IsTrue( (await ownership.ClaimAsync( seed.Actor, seed.SessionId )).Succeeded );
		Assert.IsTrue( (await behavior.ToggleDoorAsync( seed.Actor, seed.SessionId )).Succeeded );
		Assert.IsTrue( DoorState( environment, seed.EntityId ).IsOpen );
		Assert.IsTrue( (await behavior.ToggleDoorAsync( seed.Actor, seed.SessionId )).Succeeded );
		Assert.IsFalse( DoorState( environment, seed.EntityId ).IsOpen );
		Assert.IsTrue( (await ownership.ReleaseAsync( seed.Actor, seed.SessionId )).Succeeded );
		Assert.IsFalse( environment.Repositories.CharacterReferences.All().Any( value =>
			value.Value.Category == "door_ownership" && value.Value.SceneEntityId == seed.EntityId ) );
	}

	[TestMethod]
	public async Task CombineLockAndOwnershipAreDerivedFromCanonicalRecords()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var citizen = await SeedDoorAsync( environment, combineLocked: true );
		var service = new HL2RPSceneEntityBehaviorService(
			environment.Repositories, environment.Sessions, environment.Clock,
			FeatureTestEnvironment.AllowPolicy() );

		var denied = await service.ToggleDoorAsync( citizen.Actor, citizen.SessionId );
		Assert.AreEqual( ErrorCode.Unauthorized, denied.Error!.Code );
		Assert.IsFalse( DoorState( environment, citizen.EntityId ).IsOpen );

		var cpActor = environment.Actor( 88 );
		var cp = environment.Character( cpActor ) with
		{
			Faction = new FactionId( HL2RPIds.Factions.CivilProtection ),
			Class = new ClassId( HL2RPIds.Classes.Recruit )
		};
		await environment.SeedAsync( unit =>
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( cp.Id ), cp ) );
		var cpSession = environment.Sessions.Bind( citizen.EntityId, InteractionSessionKind.Door );
		Assert.IsTrue( (await service.ToggleDoorAsync( cpActor, cpSession )).Succeeded );
	}

	[TestMethod]
	public async Task ForcefieldMutationUsesOneCommitAndOnlyPublishesCommittedState()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor();
		var character = environment.Character( actor ) with
		{
			Faction = new FactionId( HL2RPIds.Factions.CivilProtection ),
			Class = new ClassId( HL2RPIds.Classes.Unit )
		};
		var entityId = SceneEntityId.New();
		await environment.SeedAsync( unit =>
		{
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unit.Create(
				environment.Repositories.CharacterLifecycleGuards,
				DomainKeys.CharacterLifecycleGuard( character.Id ),
				new CharacterLifecycleGuardRecord { CharacterId = character.Id, ReferenceRevision = 0 } );
			unit.Create( environment.Repositories.SceneEntities, DomainKeys.SceneEntity( entityId ),
				new PersistentSceneEntityRecord
				{
					Id = entityId,
					Kind = "forcefield",
					State = HL2RPPersistence.Payload( HL2RPPersistence.ForcefieldState,
						new ForcefieldEntityState { Enabled = true, CombineOnly = true } )
				} );
		} );
		var events = new RecordingHandler<ForcefieldStateChangedEvent>();
		var service = new HL2RPSceneEntityBehaviorService(
			environment.Repositories, environment.Sessions, environment.Clock,
			FeatureTestEnvironment.AllowPolicy(), forcefieldEvents: events.Bus() );

		var toggled = await service.ToggleForcefieldAsync( actor, entityId );

		Assert.IsTrue( toggled.Succeeded, toggled.Error?.Message );
		Assert.IsFalse( ForcefieldState( environment, entityId ).Enabled );
		Assert.HasCount( 1, events.Events );
		environment.Provider.FailNextCommit();
		Assert.IsTrue( (await service.ToggleForcefieldAsync( actor, entityId )).Failed );
		Assert.IsFalse( ForcefieldState( environment, entityId ).Enabled );
		Assert.HasCount( 1, events.Events );
	}

	private static async Task<SceneSeed> SeedDoorAsync(
		FeatureTestEnvironment environment,
		bool combineLocked )
	{
		var actor = environment.Actor();
		var character = environment.Character( actor );
		var entityId = SceneEntityId.New();
		await environment.SeedAsync( unit =>
		{
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unit.Create(
				environment.Repositories.CharacterLifecycleGuards,
				DomainKeys.CharacterLifecycleGuard( character.Id ),
				new CharacterLifecycleGuardRecord { CharacterId = character.Id, ReferenceRevision = 0 } );
			unit.Create( environment.Repositories.SceneEntities, DomainKeys.SceneEntity( entityId ),
				new PersistentSceneEntityRecord
				{
					Id = entityId,
					Kind = "door",
					State = HL2RPPersistence.Payload( HL2RPPersistence.DoorState,
						new DoorEntityState { CombineLocked = combineLocked, IsOpen = false } )
				} );
		} );
		return new SceneSeed(
			actor, entityId, environment.Sessions.Bind( entityId, InteractionSessionKind.Door ) );
	}

	private static DoorEntityState DoorState( FeatureTestEnvironment environment, SceneEntityId id )
	{
		var record = environment.Repositories.SceneEntities.Find( DomainKeys.SceneEntity( id ) )!.Value;
		return HL2RPPersistence.DoorState.Deserialize( record.State.Data, record.State.TypeVersion );
	}

	private static ForcefieldEntityState ForcefieldState( FeatureTestEnvironment environment, SceneEntityId id )
	{
		var record = environment.Repositories.SceneEntities.Find( DomainKeys.SceneEntity( id ) )!.Value;
		return HL2RPPersistence.ForcefieldState.Deserialize( record.State.Data, record.State.TypeVersion );
	}

	private sealed record SceneSeed(
		InventoryActor Actor,
		SceneEntityId EntityId,
		InteractionSessionId SessionId );
}
