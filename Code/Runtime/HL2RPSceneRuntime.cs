#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Persistence;
using Hexagon.V2.Runtime;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Showcase.Restraint;
using HL2RP.V2.World;
using Sandbox;

namespace HL2RP.V2.Runtime;

internal sealed record HL2RPInteractionActorState(
	AccountId Account,
	CharacterRecord Character,
	GameObject Body,
	bool IsDead );

internal sealed class HL2RPWorldModelCatalog : IWorldModelCatalog
{
	public bool IsValidModel( string modelPath )
	{
		if ( string.IsNullOrWhiteSpace( modelPath ) ) return false;
		try { return Model.Load( modelPath ) is not null; }
		catch ( Exception ) { return false; }
	}
}

internal sealed class HL2RPSceneDirectory : IInteractionDirectory
{
	private readonly Dictionary<InteractionTarget, IHexInteractable> _fixed = new();
	private readonly DomainRepositories _repositories;
	private readonly IRestraintStateReader _restraints;
	private readonly RestraintPermissionAuthorizer _restraintAuthorization;

	public HL2RPSceneDirectory(
		DomainRepositories repositories,
		IRestraintStateReader restraints,
		RestraintPermissionAuthorizer restraintAuthorization )
	{
		_repositories = repositories;
		_restraints = restraints;
		_restraintAuthorization = restraintAuthorization;
	}

	public void Register( IHexInteractable interactable ) => _fixed.Add( interactable.Target, interactable );

	public bool TryResolve( InteractionTarget target, out IHexInteractable interactable )
	{
		if ( _fixed.TryGetValue( target, out interactable! ) ) return true;
		if ( target.Kind == InteractionTargetKind.Character )
		{
			var characterId = new CharacterId( target.Id );
			if ( _repositories.Characters.Find( DomainKeys.Character( characterId ) ) is not null )
			{
				interactable = new CharacterRestraintInteractable(
					characterId, _repositories, _restraints, _restraintAuthorization );
				return true;
			}
		}
		interactable = null!;
		return false;
	}

}

internal sealed class HL2RPSceneInteractable : IHexInteractable
{
	private readonly HL2RPSceneFeatureComponent _component;
	private readonly DomainRepositories _repositories;

	public HL2RPSceneInteractable( HL2RPSceneFeatureComponent component, DomainRepositories repositories )
	{
		_component = component;
		_repositories = repositories;
	}

	public InteractionTarget Target => InteractionTarget.SceneEntity( _component.SceneEntityId!.Value );
	public InteractionPolicy Policy => _component.InteractionPolicy;

	public OperationResult<InteractionOffer> Authorize( ServerInteractionContext context )
	{
		if ( _component is HL2RPForcefieldComponent )
		{
			var character = _repositories.Characters.Find( DomainKeys.Character( context.CharacterId ) );
			var document = _repositories.SceneEntities.Find(
				DomainKeys.SceneEntity( _component.SceneEntityId!.Value ) );
			if ( character is null || character.Value.AccountId != context.AccountId ||
				document is null || document.Value.Kind != "forcefield" )
				return OperationResult<InteractionOffer>.Failure(
					ErrorCode.Unauthorized, "Forcefield actor or state is unavailable." );
			var decoded = HL2RPFeaturePersistence.Decode(
				document.Value.State, HL2RPPersistence.ForcefieldState );
			if ( decoded.Failed ) return OperationResult<InteractionOffer>.Failure(
				decoded.Error!.Code, decoded.Error.Message );
			if ( !ForcefieldEntityRules.IsPassageAuthorized( decoded.Value, character.Value.Faction ) )
				return OperationResult<InteractionOffer>.Failure(
					ErrorCode.PolicyDenied, "This forcefield only authorizes Combine personnel." );
			return OperationResult<InteractionOffer>.Success( new InteractionOffer() );
		}
		if ( _component is not HL2RPStorageComponent )
			return OperationResult<InteractionOffer>.Success( new InteractionOffer
			{
				SessionKind = Policy.SessionKind
			} );
		var state = _repositories.SceneEntities.Find(
			DomainKeys.SceneEntity( _component.SceneEntityId!.Value ) );
		if ( state is null || state.Value.Kind != "storage" )
			return OperationResult<InteractionOffer>.Failure( ErrorCode.NotFound, "Storage state was not found." );
		StorageEntityState storage;
		try { storage = HL2RPPersistence.StorageState.Deserialize( state.Value.State.Data, state.Value.State.TypeVersion ); }
		catch ( Exception )
		{
			return OperationResult<InteractionOffer>.Failure( ErrorCode.PersistedTypeInvalid, "Storage state is malformed." );
		}
		if ( storage.Locked )
			return OperationResult<InteractionOffer>.Failure( ErrorCode.PolicyDenied, "Storage is locked." );
		return OperationResult<InteractionOffer>.Success( new InteractionOffer
		{
			SessionKind = InteractionSessionKind.Storage,
			InventoryGrants = new[]
			{
				new InteractionInventoryGrant(
					storage.InventoryId,
					InventoryCapability.View | InventoryCapability.Move |
					InventoryCapability.TransferIn | InventoryCapability.TransferOut |
					InventoryCapability.Use | InventoryCapability.Drop,
					InventoryGrantKind.InteractionSession )
			}
		} );
	}
}

internal sealed class HL2RPServerInteractionWorld : IServerInteractionWorld
{
	private readonly Scene _scene;
	private readonly Func<ConnectionId, HL2RPInteractionActorState?> _actors;
	private readonly Func<CharacterId, HexPlayerBody?> _players;
	private readonly IReadOnlyDictionary<SceneEntityId, HL2RPSceneFeatureComponent> _features;
	private readonly IRestraintStateReader _restraints;

	public HL2RPServerInteractionWorld(
		Scene scene,
		Func<ConnectionId, HL2RPInteractionActorState?> actors,
		Func<CharacterId, HexPlayerBody?> players,
		IReadOnlyDictionary<SceneEntityId, HL2RPSceneFeatureComponent> features,
		IRestraintStateReader restraints )
	{
		_scene = scene;
		_actors = actors;
		_players = players;
		_features = features;
		_restraints = restraints ?? throw new ArgumentNullException( nameof(restraints) );
	}

	public bool TryBuildContext(
		ConnectionId connectionId,
		CharacterId characterId,
		InteractionTarget target,
		out ServerInteractionContext context )
	{
		var actor = _actors( connectionId );
		if ( actor is null || actor.Character.Id != characterId ||
			!actor.Body.IsValid() || !actor.Body.Enabled ||
			!TryTargetPosition( target, out var targetPosition ) )
		{
			context = null!;
			return false;
		}
		var position = actor.Body.WorldPosition;
		context = new ServerInteractionContext
		{
			ConnectionId = connectionId,
			AccountId = actor.Account,
			CharacterId = characterId,
			Target = target,
			ActorPosition = new WorldPoint( position.x, position.y, position.z ),
			TargetPosition = targetPosition,
			IsAlive = !actor.IsDead,
			IsRestrained = _restraints.IsRestrained( characterId )
		};
		return true;
	}

	public bool HasLineOfSight( ServerInteractionContext context )
	{
		var start = new Vector3( context.ActorPosition.X, context.ActorPosition.Y, context.ActorPosition.Z );
		var end = new Vector3( context.TargetPosition.X, context.TargetPosition.Y, context.TargetPosition.Z );
		var trace = _scene.Trace.Ray( start, end ).WithoutTags( "prediction" );
		var actor = _actors( context.ConnectionId );
		if ( actor is not null && actor.Body.IsValid() ) trace = trace.IgnoreGameObjectHierarchy( actor.Body.Root );
		var result = trace.Run();
		return !result.Hit || result.Distance >= Vector3.DistanceBetween( start, end ) - 32f;
	}

	private bool TryTargetPosition( InteractionTarget target, out WorldPoint position )
	{
		if ( target.Kind == InteractionTargetKind.SceneEntity &&
			_features.TryGetValue( new SceneEntityId( target.Id ), out var feature ) )
		{
			var point = feature.GameObject.WorldPosition;
			position = new WorldPoint( point.x, point.y, point.z );
			return true;
		}
		if ( target.Kind == InteractionTargetKind.Character &&
			_players( new CharacterId( target.Id ) ) is { } player &&
			player.TryGetUsableAuthoritativeBody( out var body ) )
		{
			var point = body.WorldPosition;
			position = new WorldPoint( point.x, point.y, point.z );
			return true;
		}
		position = default;
		return false;
	}
}

internal static class HL2RPSceneStateInitializer
{
	public static async ValueTask<OperationResult> InitializeAsync(
		DomainRepositories repositories,
		IEnumerable<HL2RPSceneFeatureComponent> components,
		IAggregateIdGenerator ids,
		CancellationToken cancellationToken )
	{
		var materialized = components.ToArray();
		var scannerLinks = BuildScannerLinks( materialized );
		if ( scannerLinks.Failed ) return OperationResult.Failure(
			scannerLinks.Error!.Code, scannerLinks.Error.Message );
		foreach ( var component in materialized.OrderBy( value => value.SceneEntityId!.Value.Value ) )
		{
			var result = await EnsureAsync(
				repositories, component, ids, scannerLinks.Value, cancellationToken );
			if ( result.Failed ) return result;
		}
		return ValidatePersistedScannerTopology( repositories, materialized );
	}

	private static async ValueTask<OperationResult> EnsureAsync(
		DomainRepositories repositories,
		HL2RPSceneFeatureComponent component,
		IAggregateIdGenerator ids,
		IReadOnlyDictionary<SceneEntityId, SceneEntityId> scannerLinks,
		CancellationToken cancellationToken )
	{
		var id = component.SceneEntityId!.Value;
		var key = DomainKeys.SceneEntity( id );
		var plan = InitialState( component, ids, scannerLinks );
		var existing = repositories.SceneEntities.Find( key );
		if ( existing is not null )
		{
			if ( existing.Value.Kind != plan.Kind )
				return OperationResult.Failure( ErrorCode.Conflict, $"Scene identity '{id}' changed kind." );
			var validated = ValidateExistingState( component, existing.Value.State, plan.State );
			if ( validated.Failed ) return OperationResult.Failure(
				validated.Error!.Code, validated.Error.Message );
			if ( !HL2RPPersistence.RequiresVersionMigration(
				existing.Value.State, validated.Value ) )
				return OperationResult.Success();
			var migration = repositories.Provider.BeginUnitOfWork();
			var editor = migration.Edit( repositories.SceneEntities, existing );
			if ( editor is null )
			{
				await HL2RPUnitOfWork.DisposeAsync( migration );
				return OperationResult.Failure( ErrorCode.Conflict, $"Scene state '{id}' changed during migration." );
			}
			editor.Replace( editor.Value with { State = validated.Value } );
			migration.Save( editor );
			var migrated = await HL2RPUnitOfWork.CommitAndDisposeAsync( migration, cancellationToken );
			if ( migrated.Succeeded )
				Log.Info( $"HL2RP_SCENE_STATE_MIGRATED id={id} kind={plan.Kind} " +
					$"type={existing.Value.State.TypeId.Value}@{existing.Value.State.TypeVersion}->" +
					$"{validated.Value.TypeId.Value}@{validated.Value.TypeVersion}" );
			return migrated.Succeeded
				? OperationResult.Success()
				: OperationResult.Failure( ErrorCode.InternalError, migrated.Error!.Message );
		}

		var unit = repositories.Provider.BeginUnitOfWork();
		unit.Create( repositories.SceneEntities, key, new PersistentSceneEntityRecord
		{
			Id = id,
			Kind = plan.Kind,
			State = plan.State
		} );
		if ( plan.Storage is not null )
		{
			unit.Create( repositories.Inventories, DomainKeys.Inventory( plan.Storage.Id ), plan.Storage );
			unit.Create(
				repositories.OwnerInventories,
				DomainKeys.OwnerInventory( plan.Storage.Owner, "storage" ),
				new OwnerInventoryRecord
				{
					Owner = plan.Storage.Owner,
					Role = "storage",
					InventoryId = plan.Storage.Id
				} );
		}
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unit, cancellationToken );
		return committed.Succeeded
			? OperationResult.Success()
			: OperationResult.Failure( ErrorCode.InternalError, committed.Error!.Message );
	}

	private static SceneStatePlan InitialState(
		HL2RPSceneFeatureComponent component,
		IAggregateIdGenerator ids,
		IReadOnlyDictionary<SceneEntityId, SceneEntityId> scannerLinks )
	{
		return component switch
		{
			HL2RPDoorComponent door => new( "door", HL2RPPersistence.Payload(
				HL2RPPersistence.DoorState,
				new DoorEntityState { CombineLocked = door.InitiallyCombineLocked, IsOpen = door.InitiallyOpen } ), null ),
			HL2RPStorageComponent storage => Storage( component.SceneEntityId!.Value, storage, ids.NewInventoryId() ),
			HL2RPVendorComponent vendor => new( "vendor", HL2RPPersistence.Payload(
				HL2RPPersistence.VendorState,
				new VendorEntityState
				{
					RequiredPermit = vendor.RequiredPermit,
					Stock = new[]
					{
						new VendorStockEntry { Definition = new DefinitionId( HL2RPIds.Items.Ration ), Quantity = vendor.InitialRationStock, UnitPrice = vendor.RationPrice },
						new VendorStockEntry { Definition = new DefinitionId( HL2RPIds.Items.Water ), Quantity = vendor.InitialWaterStock, UnitPrice = vendor.WaterPrice }
					}
				} ), null ),
			HL2RPRationDispenserComponent machine => Machine( "ration_dispenser", machine ),
			HL2RPVendingMachineComponent machine => Machine( "vending_machine", machine ),
			HL2RPForcefieldComponent forcefield => new( "forcefield", HL2RPPersistence.Payload(
				HL2RPPersistence.ForcefieldState,
				new ForcefieldEntityState { Enabled = forcefield.InitiallyEnabled, CombineOnly = forcefield.CombineOnly } ), null ),
			HL2RPScannerDockComponent => Scanner(
				"scanner_dock", scannerLinks[component.SceneEntityId!.Value] ),
			HL2RPScannerDroneComponent => Scanner(
				"scanner_drone", scannerLinks[component.SceneEntityId!.Value] ),
			HL2RPCombatTargetComponent target => new( "combat_target", HL2RPPersistence.Payload(
				HL2RPPersistence.CombatTargetState,
				new CombatTargetEntityState { MaximumHealth = target.MaximumHealth, CurrentHealth = target.MaximumHealth, LastHitAtUtc = null } ), null ),
			HL2RPWorldItemAreaComponent => new( "city", HL2RPPersistence.Payload(
				HL2RPPersistence.CityState,
				new CityEntityState() ), null ),
			_ => throw new InvalidOperationException( "Unknown HL2RP scene feature component." )
		};
	}

	private static SceneStatePlan Storage( SceneEntityId id, HL2RPStorageComponent component, InventoryId inventoryId )
	{
		var inventory = new InventoryRecord
		{
			Id = inventoryId,
			Owner = InventoryOwner.SceneEntity( id ),
			Width = component.Width,
			Height = component.Height
		};
		return new SceneStatePlan( "storage", HL2RPPersistence.Payload(
			HL2RPPersistence.StorageState,
			new StorageEntityState { InventoryId = inventory.Id, Locked = component.InitiallyLocked } ), inventory );
	}

	private static SceneStatePlan Machine( string kind, HL2RPMachineComponent component ) => new(
		kind,
		HL2RPPersistence.Payload( HL2RPPersistence.MachineState, new MachineEntityState
		{
			Stock = component.InitialStock,
			UnitPrice = component.UnitPrice,
			CooldownSeconds = component.CooldownSeconds,
			CooldownUntilUtc = null
		} ),
		null );

	private static SceneStatePlan Scanner( string kind, SceneEntityId linkedEntityId ) => new(
		kind,
		HL2RPPersistence.Payload( HL2RPPersistence.ScannerState, new ScannerEntityState
		{
			LinkedEntityId = linkedEntityId,
			PilotCharacterId = null,
			SpotlightEnabled = false,
			LastAcceptedInputSequence = 0,
			PhotoCooldownUntilUtc = null
		} ),
		null );

	private static OperationResult<IReadOnlyDictionary<SceneEntityId, SceneEntityId>> BuildScannerLinks(
		IReadOnlyList<HL2RPSceneFeatureComponent> components )
	{
		var byId = components.ToDictionary( value => value.SceneEntityId!.Value );
		var links = new Dictionary<SceneEntityId, SceneEntityId>();
		foreach ( var dock in components.OfType<HL2RPScannerDockComponent>() )
		{
			if ( dock.SceneEntityId is not SceneEntityId dockId || dock.LinkedDroneId is not SceneEntityId droneId )
				return OperationResult<IReadOnlyDictionary<SceneEntityId, SceneEntityId>>.Failure(
					ErrorCode.ConfigurationInvalid, "Every scanner dock must declare a stable drone identity." );
			if ( !byId.TryGetValue( droneId, out var linked ) || linked is not HL2RPScannerDroneComponent )
				return OperationResult<IReadOnlyDictionary<SceneEntityId, SceneEntityId>>.Failure(
					ErrorCode.ConfigurationInvalid, $"Scanner dock '{dockId}' links to a missing or non-drone entity '{droneId}'." );
			if ( links.ContainsKey( dockId ) || links.ContainsKey( droneId ) )
				return OperationResult<IReadOnlyDictionary<SceneEntityId, SceneEntityId>>.Failure(
					ErrorCode.DuplicateRegistration, "Scanner dock/drone links must be one-to-one." );
			links.Add( dockId, droneId );
			links.Add( droneId, dockId );
		}
		var unlinkedDrone = components.OfType<HL2RPScannerDroneComponent>()
			.FirstOrDefault( value => value.SceneEntityId is SceneEntityId id && !links.ContainsKey( id ) );
		if ( unlinkedDrone?.SceneEntityId is SceneEntityId unlinkedId )
			return OperationResult<IReadOnlyDictionary<SceneEntityId, SceneEntityId>>.Failure(
				ErrorCode.ConfigurationInvalid, $"Scanner drone '{unlinkedId}' is not linked to a dock." );
		return OperationResult<IReadOnlyDictionary<SceneEntityId, SceneEntityId>>.Success( links );
	}

	private static OperationResult<TypedPayload> ValidateExistingState(
		HL2RPSceneFeatureComponent component,
		TypedPayload persisted,
		TypedPayload planned )
	{
		try
		{
			switch ( component )
			{
				case HL2RPDoorComponent:
					return Normalize( persisted, HL2RPPersistence.DoorState );
				case HL2RPStorageComponent:
					return Normalize( persisted, HL2RPPersistence.StorageState );
				case HL2RPVendorComponent:
					return Normalize( persisted, HL2RPPersistence.VendorState );
				case HL2RPMachineComponent:
					return Normalize( persisted, HL2RPPersistence.MachineState );
				case HL2RPForcefieldComponent:
					return Normalize( persisted, HL2RPPersistence.ForcefieldState );
				case HL2RPScannerDockComponent:
				case HL2RPScannerDroneComponent:
					var scanner = Deserialize( persisted, HL2RPPersistence.ScannerState );
					var expected = Deserialize( planned, HL2RPPersistence.ScannerState );
					if ( scanner.LinkedEntityId != expected.LinkedEntityId )
						return OperationResult<TypedPayload>.Failure(
							ErrorCode.ConfigurationInvalid,
							$"Scanner scene link for '{component.SceneEntityId}' does not match the authored reciprocal mapping." );
					return OperationResult<TypedPayload>.Success(
						HL2RPPersistence.Payload( HL2RPPersistence.ScannerState, scanner ) );
				case HL2RPCombatTargetComponent:
					return Normalize( persisted, HL2RPPersistence.CombatTargetState );
				case HL2RPWorldItemAreaComponent:
					return Normalize( persisted, HL2RPPersistence.CityState );
			}
			return OperationResult<TypedPayload>.Failure(
				ErrorCode.ConfigurationInvalid, "Unknown scene feature state cannot be normalized." );
		}
		catch ( Exception )
		{
			return OperationResult<TypedPayload>.Failure(
				ErrorCode.PersistedTypeInvalid,
				$"Persisted scene state for '{component.SceneEntityId}' is malformed or incompatible." );
		}
	}

	private static OperationResult<TypedPayload> Normalize<T>(
		TypedPayload payload,
		IPersistedTypeCodec<T> codec ) where T : class =>
		OperationResult<TypedPayload>.Success(
			HL2RPPersistence.Payload( codec, Deserialize( payload, codec ) ) );

	private static T Deserialize<T>( TypedPayload payload, IPersistedTypeCodec<T> codec ) where T : class
	{
		if ( payload.TypeId.Value != codec.Key.Value )
			throw new InvalidOperationException( $"Expected scene payload '{codec.Key}'." );
		return codec.Deserialize( payload.Data, payload.TypeVersion );
	}

	private static OperationResult ValidatePersistedScannerTopology(
		DomainRepositories repositories,
		IReadOnlyList<HL2RPSceneFeatureComponent> components )
	{
		var links = new List<ScannerSceneLinkState>();
		foreach ( var component in components.Where( value => value is
			HL2RPScannerDockComponent or HL2RPScannerDroneComponent ) )
		{
			var id = component.SceneEntityId!.Value;
			var document = repositories.SceneEntities.Find( DomainKeys.SceneEntity( id ) );
			if ( document is null )
				return OperationResult.Failure( ErrorCode.NotFound, $"Scanner scene state '{id}' is missing." );
			try
			{
				var state = Deserialize( document.Value.State, HL2RPPersistence.ScannerState );
				links.Add( new ScannerSceneLinkState( id, document.Value.Kind, state.LinkedEntityId ) );
			}
			catch ( Exception )
			{
				return OperationResult.Failure( ErrorCode.PersistedTypeInvalid, $"Scanner scene state '{id}' is malformed." );
			}
		}
		return ScannerSceneLinkTopology.Validate( links );
	}

	private sealed record SceneStatePlan( string Kind, TypedPayload State, InventoryRecord? Storage );
}
