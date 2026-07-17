#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Hexagon.V2.Application;
using Hexagon.V2.Composition;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Policies;
using Hexagon.V2.Networking;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;
using HL2RP.V2.Showcase.Combat;
using HL2RP.V2.Showcase.Restraint;
using HL2RP.V2.Showcase.Scanner;
using HL2RP.UI;

namespace HL2RP.V2.Runtime;

/// <summary>
/// The engine-facing state the presentation composer cannot compute itself: the
/// connected-client roster (with engine-owned display names), scene raycast
/// proximity, and scene-component door metadata. The host application implements
/// this over its live bindings; tests implement it over fixtures.
/// </summary>
internal interface IHL2RPPresentationHost
{
	IReadOnlyList<HL2RPClientPresentationView> Clients { get; }
	CharacterRecord? NearestCharacterTarget( CharacterId viewerId );
	bool IsOwnableDoor( SceneEntityId sceneEntityId );
}

/// <summary>Engine-neutral projection of one connected client binding.</summary>
internal sealed record HL2RPClientPresentationView(
	ConnectionId ConnectionId,
	AccountId AccountId,
	CharacterId? CharacterId,
	string DisplayName );

internal sealed record ItemActionPresentationEnvelope(
	CharacterId CharacterId,
	long PresentationSequence,
	ItemActionPresentationReceipt Receipt );

internal sealed record ActiveRestraintAction(
	InventoryActor Actor,
	RestraintTicket Ticket,
	CancellationTokenSource Cancellation );

internal sealed record ActivePistolRaiseAction(
	InventoryActor Actor,
	Guid InstanceId,
	DateTimeOffset ReadyAtUtc,
	CancellationTokenSource Cancellation );

/// <summary>
/// Constructor bundle for <see cref="HL2RPPresentationComposer"/>. Every member is
/// the same live instance the host application owns; dictionaries are read through
/// their read-only surface and the nullable showcase services arrive as accessors
/// because they only exist after host initialization completes.
/// </summary>
internal sealed class HL2RPPresentationComposerServices
{
	public required DomainRepositories Repositories { get; init; }
	public required CompiledSchema Schema { get; init; }
	public required InventoryAccessService Access { get; init; }
	public required IHexClock Clock { get; init; }
	public required HL2RPAccountEntitlementService Entitlements { get; init; }
	public required IReadOnlyDictionary<ConnectionId, AccountId> EntitlementQueries { get; init; }
	public required IReadOnlyDictionary<ConnectionId, ItemActionPresentationEnvelope> ItemActionPresentations { get; init; }
	public required HL2RPTimedActionOwnership<ConnectionId, ActiveRestraintAction> ActiveRestraintActions { get; init; }
	public required HL2RPTimedActionOwnership<ConnectionId, ActivePistolRaiseAction> ActivePistolActions { get; init; }
	public required HL2RPProjectionIndex ProjectionIndex { get; init; }
	public required HL2RPPresentationInvalidation PresentationInvalidation { get; init; }
	public required HL2RPCivicSubjectSelections CivicSubjects { get; init; }
	public required CanonicalCombatHealthDirectory CombatHealth { get; init; }
	public required HL2RPExecutableItemActionCatalog ExecutableActions { get; init; }
	public required PolicyPipeline<HL2RPFeaturePolicyContext> FeaturePolicy { get; init; }
	public required HL2RPFeatureRuntimePolicy FeatureAuthorization { get; init; }
	public required RestraintStateReader RestraintState { get; init; }
	public required IWorldModelCatalog WorldModels { get; init; }
	public required Func<HL2RPEntitlementAdministrator, bool> CanManageEntitlements { get; init; }
	public required Func<InteractionSessionService?> Sessions { get; init; }
	public required Func<ScannerPilotService?> Scanner { get; init; }
	public required Func<CombatLifecycleService?> CombatLifecycle { get; init; }
}

/// <summary>
/// Builds every client-facing presentation artifact — public/private/roster
/// snapshots, inventory views, and schema panel views — from committed domain
/// state and the live host directories. Engine-neutral by construction: the only
/// engine-derived inputs arrive through <see cref="IHL2RPPresentationHost"/>.
/// Transport, revision stamping, and invalidation acknowledgement stay with the
/// host application.
/// </summary>
internal sealed class HL2RPPresentationComposer
{
	private readonly HL2RPPresentationComposerServices _services;
	private readonly IHL2RPPresentationHost _host;

	private DomainRepositories _repositories => _services.Repositories;
	private InteractionSessionService? Sessions => _services.Sessions();
	private ScannerPilotService? Scanner => _services.Scanner();
	private CombatLifecycleService? CombatLifecycle => _services.CombatLifecycle();

	public HL2RPPresentationComposer( HL2RPPresentationComposerServices services, IHL2RPPresentationHost host )
	{
		_services = services ?? throw new ArgumentNullException( nameof(services) );
		_host = host ?? throw new ArgumentNullException( nameof(host) );
	}

	internal static Guid DeterministicGuid( string value )
	{
		var bytes = new byte[16];
		for ( var index = 0; index < value.Length; index++ ) bytes[index % bytes.Length] ^= (byte)value[index];
		bytes[0] |= 1;
		return new Guid( bytes );
	}

	internal InventoryRecord? MainInventory( CharacterId characterId )
	{
		var owner = InventoryOwner.Character( characterId );
		var index = _repositories.OwnerInventories.Find( DomainKeys.OwnerInventory( owner, InventoryRoles.Main ) );
		return index is null ? null : _repositories.Inventories.Find( DomainKeys.Inventory( index.Value.InventoryId ) )?.Value;
	}

	internal string DisplayNameFor( CharacterRecord? viewer, CharacterRecord subject )
		=> HL2RPRuntimeProjection.PresentationName( viewer, subject, _repositories );

	private CharacterRecord? ActiveCharacter( HL2RPClientPresentationView view ) =>
		view.CharacterId is CharacterId characterId
			? _repositories.Characters.Find( DomainKeys.Character( characterId ) )?.Value
			: null;

	internal SchemaViewSnapshot BuildCreationAvailabilityView(
		ConnectionId connectionId,
		AccountId accountId,
		CharacterId? characterId,
		long revision )
	{
		var self = _services.Entitlements.Observe( accountId );
		var selfSnapshot = self.Succeeded
			? self.Value
			: new HL2RPAccountEntitlementSnapshot(
				accountId, HL2RPWhitelist.None, Hexagon.V2.Persistence.DocumentRevision.None, false );
		var canManage = _services.CanManageEntitlements( new HL2RPEntitlementAdministrator( accountId, characterId ) );
		HL2RPAccountEntitlementSnapshot? queried = null;
		if ( canManage && _services.EntitlementQueries.TryGetValue( connectionId, out var queriedAccountId ) )
		{
			var observed = _services.Entitlements.Observe( queriedAccountId );
			if ( observed.Succeeded ) queried = observed.Value;
		}
		return HL2RPCreationAvailability.Build( revision, selfSnapshot, canManage, queried );
	}

	internal IReadOnlyList<DocumentAddress> PrivateProjectionDocuments(
		CharacterId characterId,
		InventoryId? mainInventoryId )
	{
		var documents = new List<DocumentAddress>
		{
			new( DomainCollections.Characters, DomainKeys.Character( characterId ) )
		};
		if ( mainInventoryId is InventoryId inventoryId )
			documents.Add( new DocumentAddress( DomainCollections.Inventories, DomainKeys.Inventory( inventoryId ) ) );
		return documents;
	}

	internal HL2RPProjectionSlice<IReadOnlyList<InventorySnapshot>> BuildInventoryProjectionSlice(
		ConnectionId connectionId,
		CharacterId characterId )
	{
		var value = BuildInventories( connectionId, characterId );
		_services.ProjectionIndex.ObserveVisibleInventories(
			connectionId, value.Select( inventory => inventory.InventoryId ) );
		var documents = new HashSet<DocumentAddress>
		{
			new( DomainCollections.Characters, DomainKeys.Character( characterId ) )
		};
		foreach ( var inventory in value )
		{
			documents.Add( new DocumentAddress(
				DomainCollections.Inventories, DomainKeys.Inventory( inventory.InventoryId ) ) );
			foreach ( var item in inventory.Items )
				documents.Add( new DocumentAddress( DomainCollections.Items, DomainKeys.Item( item.ItemId ) ) );
		}
		foreach ( var session in Sessions?.ActiveSessions.Where( value =>
			value.ConnectionId == connectionId && value.Target.Kind == InteractionTargetKind.SceneEntity ) ??
			Array.Empty<InteractionSession>() )
			documents.Add( new DocumentAddress( DomainCollections.SceneEntities,
				DomainKeys.SceneEntity( new SceneEntityId( session.Target.Id ) ) ) );
		return new HL2RPProjectionSlice<IReadOnlyList<InventorySnapshot>>(
			value, documents.ToArray(),
			new[] { HL2RPProjectionIndex.RuntimeDependency( "connection", connectionId.ToString() ) } );
	}

	internal HL2RPProjectionSlice<IReadOnlyList<SchemaViewSnapshot>> BuildSchemaProjectionSlice(
		ConnectionId connectionId,
		CharacterRecord character )
	{
		var value = BuildSchemaViews( connectionId, character, 0 );
		var documents = new HashSet<DocumentAddress>
		{
			new( DomainCollections.Characters, DomainKeys.Character( character.Id ) )
		};
		var civicSubject = _services.CivicSubjects.Resolve(
			connectionId, character.Id,
			candidate => _repositories.Characters.Find( DomainKeys.Character( candidate ) ) is not null );
		documents.Add( new DocumentAddress( DomainCollections.Characters, DomainKeys.Character( civicSubject ) ) );
		if ( _services.ProjectionIndex.FirstSceneEntity( "city" ) is PersistentSceneEntityRecord city )
			documents.Add( new DocumentAddress( DomainCollections.SceneEntities, DomainKeys.SceneEntity( city.Id ) ) );
		foreach ( var reference in _services.ProjectionIndex.CharacterReferenceKeys( character.Id ) )
			documents.Add( new DocumentAddress( DomainCollections.CharacterReferences, reference ) );
		foreach ( var session in Sessions?.ActiveSessions.Where( session =>
			session.ConnectionId == connectionId && session.Target.Kind == InteractionTargetKind.SceneEntity ) ??
			Array.Empty<InteractionSession>() )
		{
			var scene = new SceneEntityId( session.Target.Id );
			documents.Add( new DocumentAddress( DomainCollections.SceneEntities, DomainKeys.SceneEntity( scene ) ) );
			foreach ( var reference in _services.ProjectionIndex.CharacterReferenceAddressesForScene( scene ) ) documents.Add( reference );
		}
		return new HL2RPProjectionSlice<IReadOnlyList<SchemaViewSnapshot>>(
			value, documents.ToArray(),
			new[] { HL2RPProjectionIndex.RuntimeDependency( "connection", connectionId.ToString() ) } );
	}

	internal ActionProgressSnapshot? BuildActionProgress( ConnectionId connectionId )
	{
		if ( _services.ActiveRestraintActions.TryGet( connectionId, out var action ) )
			return new ActionProgressSnapshot(
				action.Ticket.TicketId.Value,
				new ActionId( HL2RPIds.Actions.Restrain ),
				"Applying restraint",
				action.Ticket.CompletesAtUtc - RestraintService.RestraintDuration,
				RestraintService.RestraintDuration,
				true );
		if ( _services.ActivePistolActions.TryGet( connectionId, out var pistol ) )
			return new ActionProgressSnapshot(
				pistol.InstanceId,
				new ActionId( HL2RPIds.Actions.Fire ),
				"Raising pistol",
				pistol.ReadyAtUtc - PistolCombatService.DefaultRaiseDelay,
				PistolCombatService.DefaultRaiseDelay,
				true );
		return null;
	}

	internal PlayerPublicSnapshot PublicSnapshot(
		HL2RPClientPresentationView view,
		CharacterRecord? character ) => new(
		view.ConnectionId,
		view.AccountId.Value,
		view.DisplayName,
		character?.Id,
		character?.Name ?? string.Empty,
		character?.Description ?? string.Empty,
		character?.Model,
		character?.Faction,
		character?.Class,
		character is not null && CombatLifecycle?.GetState( character.Id ) is not null,
		character is not null && IsPistolRaised( character.Id ),
		character is null ? string.Empty : HL2RPRuntimeProjection.SafeReplicatedLabel( character ) );

	internal bool IsPistolRaised( CharacterId characterId )
	{
		var main = MainInventory( characterId );
		if ( main is null ) return false;
		foreach ( var placement in main.Placements )
		{
			var item = _repositories.Items.Find( DomainKeys.Item( placement.ItemId ) )?.Value;
			if ( item?.Definition.Value != HL2RPIds.Items.Pistol || !item.Traits.TryGetValue( "pistol", out var payload ) ) continue;
			try
			{
				var pistol = HL2RPPersistence.Pistol.Deserialize( payload.Data, payload.TypeVersion );
				if ( pistol.Equipped && pistol.Raised ) return true;
			}
			catch ( Exception ) { }
		}
		return false;
	}

	internal PlayerPrivateSnapshot BuildPrivateSnapshot( CharacterRecord character, InventoryId? mainInventoryId )
	{
		var baseline = HL2RPRuntimeProjection.PrivateSnapshot( character, mainInventoryId );
		var values = new Dictionary<string, SnapshotValue>( baseline.Values, StringComparer.Ordinal );
		var health = _services.CombatHealth.Require( character.Id );
		values["vitals.health"] = SnapshotValue.Integer( health.Succeeded ? health.Value.CurrentHealth : 0 );
		values["vitals.health_max"] = SnapshotValue.Integer( health.Succeeded ? health.Value.MaximumHealth : 100 );
		values["vitals.armor_max"] = SnapshotValue.Integer( 100 );
		var death = CombatLifecycle?.GetState( character.Id );
		values["death.cause"] = SnapshotValue.String( death?.Cause ?? string.Empty );
		values["death.can_respawn"] = SnapshotValue.Boolean( death?.CanRespawn( _services.Clock.UtcNow ) == true );
		values["death.respawn_available_at_ms"] = SnapshotValue.Integer(
			death?.RespawnAvailableAtUtc.ToUnixTimeMilliseconds() ?? 0 );
		values["restraint.active"] = SnapshotValue.Boolean( _services.RestraintState.IsRestrained( character.Id ) );
		var activeSessions = Sessions?.ActiveSessions.Where( value => value.CharacterId == character.Id ).ToArray() ?? Array.Empty<InteractionSession>();
		foreach ( var session in activeSessions )
			values[$"interaction.{session.Kind.ToString().ToLowerInvariant()}_session"] = SnapshotValue.String( session.Id.Value.ToString( "D" ) );
		var connection = _host.Clients.FirstOrDefault( value => value.CharacterId == character.Id )?.ConnectionId ?? default;
		values["action.active"] = SnapshotValue.Boolean(
			_services.ActiveRestraintActions.Contains( connection ) || _services.ActivePistolActions.Contains( connection ) );
		if ( _services.ItemActionPresentations.TryGetValue( connection, out var itemPresentation ) &&
			itemPresentation.CharacterId == character.Id )
		{
			values["item.presentation.sequence"] = SnapshotValue.Integer( itemPresentation.PresentationSequence );
			values["item.presentation.kind"] = SnapshotValue.Choice( itemPresentation.Receipt.Kind switch
			{
				ItemActionPresentationKind.IdentityDocument => "identity_document",
				ItemActionPresentationKind.ReferenceDocument => "reference_document",
				ItemActionPresentationKind.PersonalNote => "personal_note",
				ItemActionPresentationKind.PermitCredential => "permit_credential",
				_ => "unknown"
			} );
			values["item.presentation.title"] = SnapshotValue.String( itemPresentation.Receipt.Title );
			foreach ( var field in itemPresentation.Receipt.Fields )
				values[$"item.presentation.field.{field.Key}"] = field.Value;
		}
		return new PlayerPrivateSnapshot( character.Id, character.Balance, mainInventoryId, values, baseline.Permissions );
	}

	internal HL2RPProjectionSlice<PlayerRosterSnapshot> BuildRosterProjectionSlice( ConnectionId recipient )
	{
		var rows = new List<PlayerRosterRowSnapshot>();
		var documents = new HashSet<DocumentAddress>();
		var clients = _host.Clients;
		var viewer = clients.FirstOrDefault( value => value.ConnectionId == recipient ) is HL2RPClientPresentationView viewerView
			? ActiveCharacter( viewerView )
			: null;
		if ( viewer is not null )
			documents.Add( new DocumentAddress( DomainCollections.Characters, DomainKeys.Character( viewer.Id ) ) );
		foreach ( var view in clients.OrderBy( value => value.ConnectionId.Value ) )
		{
			var character = ActiveCharacter( view );
			if ( character is not null )
			{
				documents.Add( new DocumentAddress(
					DomainCollections.Characters, DomainKeys.Character( character.Id ) ) );
				if ( viewer is not null && viewer.Id != character.Id &&
					character.Faction.Value is not (HL2RPIds.Factions.CivilProtection or HL2RPIds.Factions.Overwatch) )
					documents.Add( new DocumentAddress(
						DomainCollections.CharacterReferences,
						$"recognition-{viewer.Id}-{character.Id}" ) );
			}
			var isDead = character is not null && CombatLifecycle?.GetState( character.Id ) is not null;
			var fields = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
			{
				[HL2RPPresentationFields.Roster.DisplayName] = SnapshotValue.String(
					character is null ? view.DisplayName : DisplayNameFor( viewer, character ) ),
				[HL2RPPresentationFields.Roster.FactionId] = SnapshotValue.String( character?.Faction.Value ?? string.Empty ),
				[HL2RPPresentationFields.Roster.ClassId] = SnapshotValue.String( character?.Class?.Value ?? string.Empty ),
				[HL2RPPresentationFields.Roster.IsDead] = SnapshotValue.Boolean( isDead ),
				[HL2RPPresentationFields.Roster.Status] = SnapshotValue.String(
					character is null ? "Selecting" : isDead ? "Deceased" : "Active" ),
				[HL2RPPresentationFields.Roster.IsLocal] = SnapshotValue.Boolean( view.ConnectionId == recipient )
			};
			rows.Add( new PlayerRosterRowSnapshot( view.ConnectionId, character?.Id, fields ) );
		}
		return new HL2RPProjectionSlice<PlayerRosterSnapshot>(
			new PlayerRosterSnapshot( 0, rows ), documents.ToArray(),
			new[]
			{
				HL2RPProjectionIndex.RuntimeDependency( "roster-membership", "global" ),
				HL2RPProjectionIndex.RuntimeDependency( "combat-lifecycle", "global" )
			} );
	}

	internal IReadOnlyList<InventorySnapshot> BuildInventories( ConnectionId connectionId, CharacterId characterId )
	{
		var snapshots = new List<InventorySnapshot>();
		var character = _repositories.Characters.Find( DomainKeys.Character( characterId ) )?.Value;
		if ( character is null ) return snapshots;
		foreach ( var document in _services.ProjectionIndex.VisibleInventories(
			connectionId, characterId,
			inventoryId => _services.Access.Has( connectionId, characterId, inventoryId, InventoryCapability.View ) ) )
		{
			var inventory = document.Value;
			var kind = inventory.Owner == InventoryOwner.Character( characterId )
				? InventoryViewKind.Main
				: inventory.Owner.Kind == InventoryOwnerKind.SceneEntity
					? InventoryViewKind.Storage
					: inventory.Owner.Kind == InventoryOwnerKind.Character ? InventoryViewKind.Search : InventoryViewKind.Bag;
			var inventoryItems = inventory.Placements
				.Select( placement => _services.ProjectionIndex.TryGetItem( placement.ItemId, out var item ) ? item : null )
				.Where( item => item is not null )
				.ToDictionary( item => item!.Id, item => item! );
			var items = new List<InventoryItemSnapshot>();
			foreach ( var placement in inventory.Placements )
			{
				inventoryItems.TryGetValue( placement.ItemId, out var item );
				if ( item is null || !_services.Schema.Items.TryGet( item.Definition.Value, out var definition ) ) continue;
				var actions = definition!.ActionIds.Select( action => ActionSnapshot(
					connectionId, character, inventory, item, inventoryItems, action ) );
				var state = new Dictionary<string, SnapshotValue>(
					HL2RPInventoryItemState.Project( item, characterId, _services.Clock.UtcNow ), StringComparer.Ordinal );
				TrackItemPresentationDeadline( item );
				var sell = VendorSellAvailabilityFor( connectionId, character, inventory, item );
				if ( sell is not null )
				{
					state[HL2RPInventoryItemFields.VendorSellEnabled] = SnapshotValue.Boolean( sell.Enabled );
					state[HL2RPInventoryItemFields.VendorSellPayout] = SnapshotValue.Integer( sell.Payout );
					state[HL2RPInventoryItemFields.VendorSellReason] = SnapshotValue.String( sell.DisabledReason );
				}
				var drop = HL2RPItemDropAvailability.Project(
					definition,
					_services.Access.Has( connectionId, characterId, inventory.Id, InventoryCapability.Move | InventoryCapability.Drop ),
					!string.IsNullOrWhiteSpace( definition.WorldModel ) && _services.WorldModels.IsValidModel( definition.WorldModel ),
					_services.RestraintState.IsRestrained( characterId ) );
				items.Add( new InventoryItemSnapshot(
					item.Id, item.Definition, definition.DisplayName ?? item.Definition.Value,
					definition.Description ?? string.Empty, definition.Category ?? string.Empty,
					placement.X, placement.Y, definition.Width, definition.Height, Quantity( item ),
					actions, state, drop.CanDrop, drop.DisabledReason ) );
			}
			snapshots.Add( new InventorySnapshot(
				inventory.Id, Math.Max( document.Revision.Value, 0 ), kind, kind.ToString(),
				inventory.Width, inventory.Height, items ) );
		}
		return snapshots;
	}

	internal void TrackItemPresentationDeadline( ItemRecord item )
	{
		try
		{
			DateTimeOffset? refreshAt = null;
			if ( item.Definition.Value == HL2RPIds.Items.RequestDevice &&
				item.Traits.TryGetValue( "request_device", out var requestPayload ) )
			{
				var request = HL2RPPersistence.RequestDevice.Deserialize( requestPayload.Data, requestPayload.TypeVersion );
				if ( request.LastRequestAtUtc is DateTimeOffset last )
					refreshAt = last + RequestDeviceService.RequestCooldown;
			}
			else if ( item.Definition.Value == HL2RPIds.Items.Pistol &&
				item.Traits.TryGetValue( "pistol", out var pistolPayload ) )
			{
				var pistol = HL2RPPersistence.Pistol.Deserialize( pistolPayload.Data, pistolPayload.TypeVersion );
				if ( pistol.LastFiredAtUtc is DateTimeOffset last )
					refreshAt = last + PistolCombatService.DefaultFireInterval;
			}
			else if ( item.Definition.Value == HL2RPIds.Items.BusinessPermit &&
				item.Traits.TryGetValue( "permit", out var permitPayload ) )
			{
				refreshAt = HL2RPPersistence.BusinessPermit.Deserialize(
					permitPayload.Data, permitPayload.TypeVersion ).ExpiresAtUtc;
			}
			if ( refreshAt is DateTimeOffset deadline && deadline > _services.Clock.UtcNow )
				_services.PresentationInvalidation.TrackRefreshDeadline( $"item:{item.Id}", deadline );
		}
		catch ( Exception ) { }
	}

	internal ItemActionSnapshot ActionSnapshot(
		ConnectionId connectionId,
		CharacterRecord character,
		InventoryRecord inventory,
		ItemRecord item,
		IReadOnlyDictionary<ItemId, ItemRecord> inventoryItems,
		string action )
	{
		var routed = _services.ExecutableActions.CreateSnapshot(
			item.Definition, new ActionId( action ), action.Replace( '_', ' ' ) );
		var health = _services.CombatHealth.Require( character.Id );
		var nestedBags = _services.ProjectionIndex.NestedInventoryCount( item.Id );
		return HL2RPItemActionAvailability.Project( routed, new HL2RPItemActionAvailabilityContext
		{
			Character = character,
			Inventory = inventory,
			Item = item,
			InventoryItems = inventoryItems,
			NowUtc = _services.Clock.UtcNow,
			HasUseCapability = _services.Access.Has(
				connectionId, character.Id, inventory.Id, InventoryCapability.View | InventoryCapability.Use ),
			IsRestrained = _services.RestraintState.IsRestrained( character.Id ),
			IsDead = CombatLifecycle?.GetState( character.Id ) is not null,
			Health = health.Succeeded ? health.Value : null,
			HasNestedBagInventory = nestedBags == 1,
			CombineLock = action == HL2RPIds.Actions.Install
				? CombineLockAvailabilityFor( connectionId, character, item )
				: null
		} );
	}

	internal CombineLockActionAvailability CombineLockAvailabilityFor(
		ConnectionId connectionId,
		CharacterRecord character,
		ItemRecord item )
	{
		var session = Sessions?.ActiveSessions.FirstOrDefault( value =>
			value.ConnectionId == connectionId && value.CharacterId == character.Id &&
			value.Kind == InteractionSessionKind.Door && value.Target.Kind == InteractionTargetKind.SceneEntity );
		if ( session is null ) return new CombineLockActionAvailability( false, "A current door session is required." );
		var door = _repositories.SceneEntities.Find(
			DomainKeys.SceneEntity( new SceneEntityId( session.Target.Id ) ) )?.Value;
		if ( door is null || door.Kind != "door" )
			return new CombineLockActionAvailability( false, "Bound session target is not a door." );
		var state = HL2RPFeaturePersistence.Decode( door.State, HL2RPPersistence.DoorState );
		if ( state.Failed ) return new CombineLockActionAvailability( false, "Door state is malformed." );
		if ( state.Value.CombineLocked ) return new CombineLockActionAvailability( false, "Door already has a Combine lock." );
		var policy = _services.FeaturePolicy.Evaluate( new HL2RPFeaturePolicyContext
		{
			Actor = new InventoryActor( connectionId, character.AccountId, character.Id ),
			Operation = HL2RPFeatureOperation.InstallCombineLock,
			SceneEntityId = new SceneEntityId( session.Target.Id ),
			ItemId = item.Id
		} );
		return policy.Succeeded
			? new CombineLockActionAvailability( true, null )
			: new CombineLockActionAvailability( false, policy.Error!.Message );
	}

	internal VendorSellAvailability? VendorSellAvailabilityFor(
		ConnectionId connectionId,
		CharacterRecord character,
		InventoryRecord inventory,
		ItemRecord item )
	{
		if ( inventory.Owner != InventoryOwner.Character( character.Id ) ) return null;
		var session = Sessions?.ActiveSessions.FirstOrDefault( value =>
			value.ConnectionId == connectionId && value.CharacterId == character.Id &&
			value.Kind == InteractionSessionKind.Vendor && value.Target.Kind == InteractionTargetKind.SceneEntity );
		if ( session is null ) return null;
		var sceneEntityId = new SceneEntityId( session.Target.Id );
		var vendorEntity = _repositories.SceneEntities.Find( DomainKeys.SceneEntity( sceneEntityId ) )?.Value;
		if ( vendorEntity is null || vendorEntity.Kind != "vendor" )
			return new VendorSellAvailability( false, 0, "Bound session target is not a vendor." );
		var vendor = HL2RPFeaturePersistence.Decode( vendorEntity.State, HL2RPPersistence.VendorState );
		if ( vendor.Failed ) return new VendorSellAvailability( false, 0, "Vendor state is malformed." );
		var actor = new InventoryActor( connectionId, character.AccountId, character.Id );
		var policy = _services.FeaturePolicy.Evaluate( new HL2RPFeaturePolicyContext
		{
			Actor = actor,
			Operation = HL2RPFeatureOperation.VendorSell,
			SceneEntityId = sceneEntityId,
			ItemId = item.Id
		} );
		return HL2RPVendorSellAvailability.Project(
			character,
			item,
			vendor.Value,
			_services.Access.Has( connectionId, character.Id, inventory.Id,
				InventoryCapability.View | InventoryCapability.TransferOut | InventoryCapability.Sell ),
			_services.ProjectionIndex.NestedInventoryCount( item.Id ) > 0,
			PermitInspector.HasValidPermit(
				_repositories, character.Id, vendor.Value.RequiredPermit, _services.Clock.UtcNow ),
			policy.Succeeded );
	}

	internal static long Quantity( ItemRecord item )
	{
		if ( !item.Traits.TryGetValue( "tokens", out var payload ) ) return 1;
		try { return Math.Max( 1, HL2RPPersistence.TokenStack.Deserialize( payload.Data, payload.TypeVersion ).Amount ); }
		catch ( Exception ) { return 1; }
	}

	internal IReadOnlyList<SchemaViewSnapshot> BuildSchemaViews(
		ConnectionId connectionId,
		CharacterRecord character,
		long revision )
	{
		var views = new List<SchemaViewSnapshot>();
		var civicSubjectId = _services.CivicSubjects.Resolve(
			connectionId,
			character.Id,
			candidate => _repositories.Characters.Find( DomainKeys.Character( candidate ) ) is not null );
		var civicSubject = _repositories.Characters.Find( DomainKeys.Character( civicSubjectId ) )?.Value ?? character;
		var state = HL2RPRuntimeProjection.DecodeState( civicSubject );
		if ( state.Failed && civicSubject.Id != character.Id )
		{
			_services.CivicSubjects.ClearConnection( connectionId );
			civicSubject = character;
			state = HL2RPRuntimeProjection.DecodeState( character );
		}
		if ( state.Succeeded ) views.Add( CivicView( character, civicSubject, state.Value, revision ) );
		views.Add( ObjectiveView( character, revision ) );
		views.Add( RestraintView( connectionId, character, revision ) );
		var vendorSession = Sessions?.ActiveSessions.SingleOrDefault( value =>
			value.ConnectionId == connectionId && value.CharacterId == character.Id && value.Kind == InteractionSessionKind.Vendor );
		if ( vendorSession is not null )
		{
			var view = VendorView( character, vendorSession, revision );
			if ( view is not null ) views.Add( view );
		}
		var doorSession = Sessions?.ActiveSessions.SingleOrDefault( value =>
			value.ConnectionId == connectionId && value.CharacterId == character.Id &&
			value.Kind == InteractionSessionKind.Door );
		if ( doorSession is not null )
		{
			var view = DoorView( character, doorSession, revision );
			if ( view is not null ) views.Add( view );
		}
		var scannerSession = Scanner?.ActiveSessions.SingleOrDefault( value => value.Actor.ConnectionId == connectionId );
		if ( scannerSession is not null ) views.Add( ScannerView( scannerSession, revision ) );
		return views;
	}

	internal SchemaViewSnapshot? DoorView(
		CharacterRecord character,
		InteractionSession session,
		long revision )
	{
		if ( session.Target.Kind != InteractionTargetKind.SceneEntity ) return null;
		var id = new SceneEntityId( session.Target.Id );
		if ( !_host.IsOwnableDoor( id ) ) return null;
		var document = _repositories.SceneEntities.Find( DomainKeys.SceneEntity( id ) )?.Value;
		if ( document is null || document.Kind != "door" ) return null;
		var decoded = HL2RPFeaturePersistence.Decode( document.State, HL2RPPersistence.DoorState );
		if ( decoded.Failed ) return null;
		var owners = _services.ProjectionIndex.DoorOwners( id );
		var ownerStatus = owners.Count switch
		{
			0 => "unowned",
			1 when owners[0] == character.Id => "self",
			1 => "other",
			_ => "conflict"
		};
		var canClaim = owners.Count == 0 && !decoded.Value.CombineLocked;
		var claimReason = canClaim ? string.Empty : owners.Count switch
		{
			> 1 => "Door ownership is ambiguous.",
			1 when owners[0] == character.Id => "You already own this door.",
			1 => "This door is owned by another character.",
			_ => "A Combine-locked door cannot be claimed."
		};
		var canRelease = owners.Count == 1 && owners[0] == character.Id;
		var releaseReason = canRelease ? string.Empty : owners.Count switch
		{
			> 1 => "Door ownership is ambiguous.",
			1 => "Only the current owner may release this door.",
			_ => "This door has no owner."
		};
		return new SchemaViewSnapshot(
			HL2RPIds.Panels.Door,
			revision,
			new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
			{
				[HL2RPPresentationFields.Door.SessionId] = SnapshotValue.String( session.Id.Value.ToString( "D" ) ),
				[HL2RPPresentationFields.Door.SceneEntityId] = SnapshotValue.String( id.Value.ToString( "D" ) ),
				[HL2RPPresentationFields.Door.IsOpen] = SnapshotValue.Boolean( decoded.Value.IsOpen ),
				[HL2RPPresentationFields.Door.CombineLocked] = SnapshotValue.Boolean( decoded.Value.CombineLocked ),
				[HL2RPPresentationFields.Door.OwnerStatus] = SnapshotValue.Choice( ownerStatus ),
				[HL2RPPresentationFields.Door.CanClaim] = SnapshotValue.Boolean( canClaim ),
				[HL2RPPresentationFields.Door.ClaimDisabledReason] = SnapshotValue.String( claimReason ),
				[HL2RPPresentationFields.Door.CanRelease] = SnapshotValue.Boolean( canRelease ),
				[HL2RPPresentationFields.Door.ReleaseDisabledReason] = SnapshotValue.String( releaseReason )
			} );
	}

	internal SchemaViewSnapshot CivicView(
		CharacterRecord viewer,
		CharacterRecord subject,
		HL2RPCharacterState state,
		long revision )
	{
		var fields = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
		{
			[HL2RPPresentationFields.CivicData.CharacterId] = SnapshotValue.String( subject.Id.Value.ToString( "D" ) ),
			[HL2RPPresentationFields.CivicData.DisplayName] = SnapshotValue.String( subject.Name ),
			[HL2RPPresentationFields.CivicData.CitizenId] = SnapshotValue.String( state.CitizenId ),
			[HL2RPPresentationFields.CivicData.Points] = SnapshotValue.Integer( state.CivicRecord.Points ),
			[HL2RPPresentationFields.CivicData.InfractionCount] = SnapshotValue.Integer( state.CivicRecord.Infractions.Count ),
			[HL2RPPresentationFields.CivicData.Priority] = SnapshotValue.Integer( (int)state.CivicRecord.Priority ),
			[HL2RPPresentationFields.CivicData.Record] = SnapshotValue.String( state.CivicRecord.RecordText ),
			[HL2RPPresentationFields.CivicData.CanEdit] = SnapshotValue.Boolean(
				_services.FeatureAuthorization.HasPermission( viewer.AccountId, viewer.Id, HL2RPIds.Permissions.Priority ) )
		};
		var rows = state.CivicRecord.Infractions.Select( (infraction, index) =>
			(IReadOnlyDictionary<string, SnapshotValue>)new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
			{
				[HL2RPPresentationFields.CivicData.InfractionId] = SnapshotValue.String( DeterministicGuid( $"{state.CitizenId}:{index}" ).ToString( "D" ) ),
				[HL2RPPresentationFields.CivicData.Summary] = SnapshotValue.String( infraction.Summary ),
				[HL2RPPresentationFields.CivicData.InfractionPoints] = SnapshotValue.Integer( infraction.Points ),
				[HL2RPPresentationFields.CivicData.IssuedAtUnixMilliseconds] = SnapshotValue.Integer( infraction.IssuedAtUtc.ToUnixTimeMilliseconds() ),
				[HL2RPPresentationFields.CivicData.IssuedBy] = SnapshotValue.String( infraction.IssuedBy.ToString() )
			} ).ToArray();
		return new SchemaViewSnapshot( HL2RPIds.Panels.CivicData, revision, fields, rows );
	}

	internal SchemaViewSnapshot ObjectiveView( CharacterRecord character, long revision )
	{
		var rows = new List<IReadOnlyDictionary<string, SnapshotValue>>();
		var city = _services.ProjectionIndex.FirstSceneEntity( "city" );
		if ( city is not null )
		{
			try
			{
				var state = HL2RPPersistence.CityState.Deserialize( city.State.Data, city.State.TypeVersion );
				foreach ( var objective in HL2RPObjectiveProjection.Rows( state ) )
					rows.Add( new Dictionary<string, SnapshotValue>( objective, StringComparer.Ordinal ) );
			}
			catch ( Exception ) { rows.Clear(); }
		}
		return new SchemaViewSnapshot( HL2RPIds.Panels.Objectives, revision,
			new Dictionary<string, SnapshotValue>
			{
				[HL2RPPresentationFields.Objectives.CanEdit] = SnapshotValue.Boolean(
					_services.FeatureAuthorization.HasPermission( character.AccountId, character.Id, HL2RPIds.Permissions.CityObjectives ) )
			}, rows );
	}

	internal SchemaViewSnapshot RestraintView(
		ConnectionId connectionId,
		CharacterRecord character,
		long revision )
	{
		var target = _host.NearestCharacterTarget( character.Id );
		_services.PresentationInvalidation.RememberRestraintTarget( connectionId, target?.Id );
		var subjectId = target?.Id ?? character.Id;
		var restrained = _services.RestraintState.IsRestrained( subjectId );
		return new SchemaViewSnapshot( HL2RPIds.Panels.RestraintStatus, revision,
			new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
			{
				[HL2RPPresentationFields.RestraintStatus.Restrained] = SnapshotValue.Boolean( restrained ),
				[HL2RPPresentationFields.RestraintStatus.RemainingMilliseconds] = SnapshotValue.Integer( -1 ),
				[HL2RPPresentationFields.RestraintStatus.TargetCharacterId] = SnapshotValue.String(
					target?.Id.Value.ToString( "D" ) ?? string.Empty ),
				[HL2RPPresentationFields.RestraintStatus.CanSearch] = SnapshotValue.Boolean( target is not null && restrained ),
				[HL2RPPresentationFields.RestraintStatus.Status] = SnapshotValue.String( target is null
					? (restrained ? "Movement restricted." : "No character in authoritative reach.")
					: (restrained ? $"{target.Name} is restrained and searchable." : $"{target.Name} can be restrained.") )
			} );
	}

	internal SchemaViewSnapshot? VendorView( CharacterRecord character, InteractionSession session, long revision )
	{
		if ( session.Target.Kind != InteractionTargetKind.SceneEntity ) return null;
		var document = _repositories.SceneEntities.Find( DomainKeys.SceneEntity( new SceneEntityId( session.Target.Id ) ) );
		if ( document is null || document.Value.Kind != "vendor" ) return null;
		try
		{
			var vendor = HL2RPPersistence.VendorState.Deserialize( document.Value.State.Data, document.Value.State.TypeVersion );
			var hasPermit = PermitInspector.HasValidPermit(
				_repositories, character.Id, vendor.RequiredPermit, _services.Clock.UtcNow );
			var rows = vendor.Stock.Select( entry =>
			{
				_services.Schema.Items.TryGet( entry.Definition.Value, out var item );
				var availability = HL2RPPresentationContracts.VendorOffer(
					hasPermit, entry.Quantity, entry.UnitPrice, character.Balance );
				return (IReadOnlyDictionary<string, SnapshotValue>)new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
				{
					[HL2RPPresentationFields.Vendor.DefinitionId] = SnapshotValue.Choice( entry.Definition.Value ),
					[HL2RPPresentationFields.Vendor.DisplayName] = SnapshotValue.String( item?.DisplayName ?? entry.Definition.Value ),
					[HL2RPPresentationFields.Vendor.ItemDescription] = SnapshotValue.String( item?.Description ?? string.Empty ),
					[HL2RPPresentationFields.Vendor.Price] = SnapshotValue.Integer( entry.UnitPrice ),
					[HL2RPPresentationFields.Vendor.Stock] = SnapshotValue.Integer( entry.Quantity ),
					[HL2RPPresentationFields.Vendor.CanBuy] = SnapshotValue.Boolean( availability.CanBuy ),
					[HL2RPPresentationFields.Vendor.DisabledReason] = SnapshotValue.String( availability.DisabledReason )
				};
			} ).ToArray();
			return new SchemaViewSnapshot( HL2RPIds.Panels.Vendor, revision,
				new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
				{
					[HL2RPPresentationFields.Vendor.SessionId] = SnapshotValue.String( session.Id.Value.ToString( "D" ) ),
					[HL2RPPresentationFields.Vendor.Name] = SnapshotValue.String( "Civil Distribution" ),
					[HL2RPPresentationFields.Vendor.Description] = SnapshotValue.String( "Session-bound regulated goods." ),
					[HL2RPPresentationFields.Vendor.Balance] = SnapshotValue.Integer( character.Balance )
				}, rows );
		}
		catch ( Exception ) { return null; }
	}

	internal SchemaViewSnapshot ScannerView( ScannerPilotSession session, long revision )
	{
		var document = _repositories.SceneEntities.Find( DomainKeys.SceneEntity( session.ScannerId ) );
		var state = document is null
			? null
			: ScannerPersistence.Decode( document.Value.State, HL2RPPersistence.ScannerState ).Value;
		return new SchemaViewSnapshot(
			HL2RPIds.Panels.ScannerOverlay,
			revision,
			new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
			{
				[HL2RPPresentationFields.ScannerOverlay.Piloting] = SnapshotValue.Boolean( state?.PilotCharacterId == session.Actor.CharacterId ),
				[HL2RPPresentationFields.ScannerOverlay.UnitName] = SnapshotValue.String( $"SCN-{session.ScannerId.Value.ToString( "N" )[..2].ToUpperInvariant()}" ),
				[HL2RPPresentationFields.ScannerOverlay.Spotlight] = SnapshotValue.Boolean( state?.SpotlightEnabled == true ),
				[HL2RPPresentationFields.ScannerOverlay.PhotoReadyAtUnixMilliseconds] = SnapshotValue.Integer(
					(state?.PhotoCooldownUntilUtc ?? _services.Clock.UtcNow).ToUnixTimeMilliseconds() ),
				[HL2RPPresentationFields.ScannerOverlay.SessionId] = SnapshotValue.String( session.SessionId.Value.ToString( "D" ) )
			} );
	}
}
