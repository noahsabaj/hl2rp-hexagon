#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Persistence;
using HL2RP.V2.Features;
using HL2RP.V2.Showcase.Combat;

namespace HL2RP.V2.Runtime;

public enum HL2RPProjectionSliceKind
{
	PrivatePlayer = 1,
	Inventories = 2,
	SchemaViews = 3,
	Roster = 4
}

public sealed record HL2RPProjectionSlice<T>(
	T Value,
	IReadOnlyList<DocumentAddress> Documents,
	IReadOnlyList<string> RuntimeDependencies );

public sealed record HL2RPProjectionApplyResult(
	IReadOnlyList<InventoryId> AffectedInventories,
	IReadOnlyList<ItemId> AffectedLiveItems );

public sealed record HL2RPLiveInventoryDelta(
	ItemId ItemId,
	LiveInventoryItemView? Row );

/// <summary>
/// Read-optimized, receipt-updated projection data. Repository-wide enumeration is
/// confined to startup/recovery rebuilds; committed request paths refresh only the
/// document addresses named by their receipt.
/// </summary>
public sealed class HL2RPProjectionIndex
{
	private readonly Dictionary<InventoryId, DocumentSnapshot<InventoryRecord>> _inventories = new();
	private readonly Dictionary<string, InventoryId> _inventoryKeys = new( StringComparer.Ordinal );
	private readonly Dictionary<ItemId, ItemRecord> _items = new();
	private readonly Dictionary<string, ItemId> _itemKeys = new( StringComparer.Ordinal );
	private readonly Dictionary<ItemId, InventoryId> _itemLocations = new();
	private readonly Dictionary<InventoryId, CharacterId?> _owners = new();
	private readonly Dictionary<CharacterId, HashSet<InventoryId>> _characterInventories = new();
	private readonly Dictionary<ItemId, HashSet<InventoryId>> _nestedInventories = new();
	private readonly Dictionary<DefinitionId, int> _definitionCounts = new();
	private readonly Dictionary<string, CharacterReferenceRecord> _references = new( StringComparer.Ordinal );
	private readonly Dictionary<SceneEntityId, PersistentSceneEntityRecord> _sceneEntities = new();
	private readonly Dictionary<SceneEntityId, List<CharacterId>> _doorOwners = new();
	private readonly Dictionary<AccountId, int> _accountCharacters = new();
	private readonly Dictionary<string, AccountId> _characterAccounts = new( StringComparer.Ordinal );
	private readonly Dictionary<string, CharacterId> _characterIds = new( StringComparer.Ordinal );
	private readonly Dictionary<string, SceneEntityId> _sceneKeys = new( StringComparer.Ordinal );
	private readonly Dictionary<InventoryId, HashSet<ConnectionId>> _viewers = new();
	private readonly Dictionary<ConnectionId, HashSet<InventoryId>> _viewedInventoriesByConnection = new();
	private readonly Dictionary<ConnectionId, AccountId> _connectionAccounts = new();
	private readonly Dictionary<AccountId, HashSet<ConnectionId>> _accountConnections = new();
	private readonly Dictionary<ConnectionId, CharacterId> _connectionCharacters = new();
	private readonly Dictionary<CharacterId, ConnectionId> _characterConnections = new();
	private readonly Dictionary<(ConnectionId Connection, CharacterId Character), InventoryId[]> _visibleSlices = new();
	private readonly Dictionary<InteractionSessionId, IndexedSceneSession> _sceneSessions = new();
	private readonly Dictionary<SceneEntityId, HashSet<ConnectionId>> _sceneViewers = new();
	private readonly Dictionary<(ConnectionId Connection, HL2RPProjectionSliceKind Kind), CachedSlice> _projectionSlices = new();
	private readonly Dictionary<HL2RPProjectionSliceKind, long> _sliceBuildCounts = new();

	public IReadOnlyList<DocumentSnapshot<InventoryRecord>> Inventories => _inventories.Values.ToArray();
	public IReadOnlyCollection<InventoryId> InventoryIds => _inventories.Keys.ToArray();

	public void Rebuild(
		IEnumerable<DocumentSnapshot<InventoryRecord>> inventories,
		IEnumerable<DocumentSnapshot<ItemRecord>> items ) => Rebuild(
		inventories, items,
		Array.Empty<DocumentSnapshot<CharacterRecord>>(),
		Array.Empty<DocumentSnapshot<CharacterReferenceRecord>>(),
		Array.Empty<DocumentSnapshot<PersistentSceneEntityRecord>>() );

	public void Rebuild(
		IEnumerable<DocumentSnapshot<InventoryRecord>> inventories,
		IEnumerable<DocumentSnapshot<ItemRecord>> items,
		IEnumerable<DocumentSnapshot<CharacterRecord>> characters,
		IEnumerable<DocumentSnapshot<CharacterReferenceRecord>> references,
		IEnumerable<DocumentSnapshot<PersistentSceneEntityRecord>> sceneEntities )
	{
		ArgumentNullException.ThrowIfNull( inventories );
		ArgumentNullException.ThrowIfNull( items );
		ArgumentNullException.ThrowIfNull( characters );
		ArgumentNullException.ThrowIfNull( references );
		ArgumentNullException.ThrowIfNull( sceneEntities );
		_inventories.Clear();
		_inventoryKeys.Clear();
		_items.Clear();
		_itemKeys.Clear();
		_itemLocations.Clear();
		_owners.Clear();
		_characterInventories.Clear();
		_nestedInventories.Clear();
		_definitionCounts.Clear();
		_references.Clear();
		_sceneEntities.Clear();
		_doorOwners.Clear();
		_accountCharacters.Clear();
		_characterAccounts.Clear();
		_characterIds.Clear();
		_sceneKeys.Clear();
		_viewers.Clear();
		_viewedInventoriesByConnection.Clear();
		_visibleSlices.Clear();
		_projectionSlices.Clear();
		foreach ( var inventory in inventories )
		{
			_inventories[inventory.Value.Id] = inventory;
			_inventoryKeys[inventory.Key] = inventory.Value.Id;
		}
		foreach ( var item in items ) AddItem( item.Key, item.Value );
		foreach ( var character in characters )
		{
			_characterAccounts[character.Key] = character.Value.AccountId;
			_characterIds[character.Key] = character.Value.Id;
			Increment( _accountCharacters, character.Value.AccountId );
		}
		foreach ( var reference in references ) _references[reference.Key] = reference.Value;
		foreach ( var scene in sceneEntities )
		{
			_sceneEntities[scene.Value.Id] = scene.Value;
			_sceneKeys[scene.Key] = scene.Value.Id;
		}
		RebuildInventoryDerivedIndexes();
		RebuildDoorOwners();
	}

	public HL2RPProjectionApplyResult Apply( CommitReceipt receipt, DomainRepositories repositories )
	{
		ArgumentNullException.ThrowIfNull( receipt );
		ArgumentNullException.ThrowIfNull( repositories );
		var changedInventories = new List<(InventoryId Id, DocumentSnapshot<InventoryRecord>? Document)>();
		var affectedItems = new HashSet<ItemId>();
		var invalidatedDependencies = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var version in receipt.Documents )
		{
			var address = version.Address;
			invalidatedDependencies.Add( DocumentDependency( address ) );
			CollectDerivedDependencies( address, invalidatedDependencies );
			if ( address.Collection == DomainCollections.Inventories )
			{
				// Read the currently published document rather than trusting an older
				// receipt's deletion bit. Independently observed commit callbacks may
				// arrive out of order; converging each named key to repository state
				// keeps a stale receipt from rolling the projection backwards.
				var document = repositories.Inventories.Find( address.Key );
				var id = document?.Value.Id ?? (_inventoryKeys.TryGetValue( address.Key, out var existingId )
					? existingId : (InventoryId?)null);
				if ( id is InventoryId inventoryId ) changedInventories.Add( (inventoryId, document) );
			}
			else if ( address.Collection == DomainCollections.Items )
			{
				if ( _itemKeys.TryGetValue( address.Key, out var existingId ) &&
					_items.TryGetValue( existingId, out var existing ) )
				{
					affectedItems.Add( existing.Id );
					RemoveItem( address.Key, existing );
				}
				var document = repositories.Items.Find( address.Key );
				if ( document is not null )
				{
					affectedItems.Add( document.Value.Id );
					AddItem( document.Key, document.Value );
				}
			}
			else if ( address.Collection == DomainCollections.Characters )
			{
				if ( _characterAccounts.Remove( address.Key, out var previousAccount ) )
					Decrement( _accountCharacters, previousAccount );
				_characterIds.Remove( address.Key );
				var document = repositories.Characters.Find( address.Key );
				if ( document is not null )
				{
					_characterAccounts[address.Key] = document.Value.AccountId;
					_characterIds[address.Key] = document.Value.Id;
					Increment( _accountCharacters, document.Value.AccountId );
				}
			}
			else if ( address.Collection == DomainCollections.CharacterReferences )
			{
				_references.Remove( address.Key );
				var document = repositories.CharacterReferences.Find( address.Key );
				if ( document is not null ) _references[address.Key] = document.Value;
			}
			else if ( address.Collection == DomainCollections.SceneEntities )
			{
				if ( _sceneKeys.Remove( address.Key, out var existingId ) ) _sceneEntities.Remove( existingId );
				var document = repositories.SceneEntities.Find( address.Key );
				if ( document is not null )
				{
					_sceneEntities[document.Value.Id] = document.Value;
					_sceneKeys[document.Key] = document.Value.Id;
				}
			}
		}
		var inventoryRefresh = RefreshInventories( changedInventories );
		affectedItems.UnionWith( inventoryRefresh.AffectedLiveItems );
		if ( receipt.Documents.Any( document => document.Address.Collection == DomainCollections.CharacterReferences ) )
			RebuildDoorOwners();
		foreach ( var version in receipt.Documents ) CollectDerivedDependencies( version.Address, invalidatedDependencies );
		InvalidateDependencies( invalidatedDependencies );
		var affectedInventories = new HashSet<InventoryId>( inventoryRefresh.AffectedInventories );
		foreach ( var item in affectedItems )
			if ( _itemLocations.TryGetValue( item, out var inventory ) ) affectedInventories.Add( inventory );
		return new HL2RPProjectionApplyResult(
			affectedInventories.OrderBy( value => value.Value ).ToArray(),
			affectedItems.OrderBy( value => value.Value ).ToArray() );
	}

	public HL2RPProjectionApplyResult RefreshInventories(
		IEnumerable<(InventoryId Id, DocumentSnapshot<InventoryRecord>? Document)> changes )
	{
		ArgumentNullException.ThrowIfNull( changes );
		var changed = changes.ToArray();
		if ( changed.Length == 0 )
			return new HL2RPProjectionApplyResult(
				Array.Empty<InventoryId>(), Array.Empty<ItemId>() );
		var affectedItems = new HashSet<ItemId>();
		var affectedInventories = new HashSet<InventoryId>();

		// Capture every old edge first. Commit receipts are key-sorted, so a move
		// may name the destination before the source. Removing and adding one
		// document at a time would then erase the destination's new item location.
		foreach ( var change in changed )
		{
			if ( _inventories.TryGetValue( change.Id, out var previous ) )
			{
				affectedInventories.Add( change.Id );
				foreach ( var placement in previous.Value.Placements )
				{
					affectedItems.Add( placement.ItemId );
					CollectNestedDescendants( placement.ItemId, affectedInventories );
				}
			}
		}

		// Remove all old placement/owner edges as one phase.
		foreach ( var change in changed )
		{
			if ( !_inventories.TryGetValue( change.Id, out var previous ) ) continue;
			foreach ( var placement in previous.Value.Placements )
				if ( _itemLocations.GetValueOrDefault( placement.ItemId ) == change.Id )
					_itemLocations.Remove( placement.ItemId );
			RemoveNestedInventory( previous.Value );
			_inventoryKeys.Remove( previous.Key );
		}

		// Publish all replacement documents, then add every new edge. This makes
		// source/destination order irrelevant and leaves one canonical location.
		foreach ( var change in changed )
		{
			if ( change.Document is null ) _inventories.Remove( change.Id );
			else
			{
				_inventories[change.Id] = change.Document;
				_inventoryKeys[change.Document.Key] = change.Id;
			}
		}
		foreach ( var change in changed.Where( value => value.Document is not null ) )
		{
			var document = change.Document!;
			AddNestedInventory( document.Value );
			foreach ( var placement in document.Value.Placements )
			{
				affectedItems.Add( placement.ItemId );
				_itemLocations[placement.ItemId] = change.Id;
			}
		}
		foreach ( var item in affectedItems ) CollectNestedDescendants( item, affectedInventories );
		foreach ( var inventory in affectedInventories ) SetOwner( inventory, ResolveOwner( inventory ) );
		foreach ( var inventory in affectedInventories )
			if ( _inventories.TryGetValue( inventory, out var document ) )
				foreach ( var placement in document.Value.Placements ) affectedItems.Add( placement.ItemId );
		// Do not evict every connection's visibility discovery for a local
		// inventory change. PublishChanges refreshes the affected inventories'
		// old/new viewer sets and invalidates only those connections. Cached IDs
		// resolve through _inventories on every read, so deletions also disappear
		// without forcing unrelated clients to rediscover the whole collection.
		return new HL2RPProjectionApplyResult(
			affectedInventories.OrderBy( value => value.Value ).ToArray(),
			affectedItems.OrderBy( value => value.Value ).ToArray() );
	}

	public bool TryGetItem( ItemId itemId, out ItemRecord item ) => _items.TryGetValue( itemId, out item! );
	public bool TryGetInventory( InventoryId inventoryId, out DocumentSnapshot<InventoryRecord> inventory ) =>
		_inventories.TryGetValue( inventoryId, out inventory! );
	public CharacterId? OwningCharacter( InventoryId inventoryId ) =>
		_owners.TryGetValue( inventoryId, out var owner ) ? owner : null;
	public int NestedInventoryCount( ItemId itemId ) =>
		_nestedInventories.TryGetValue( itemId, out var inventories ) ? inventories.Count : 0;
	public int DefinitionCount( DefinitionId definition ) => _definitionCounts.GetValueOrDefault( definition );
	public IReadOnlyList<InventoryId> InventoriesOwnedBy( CharacterId character ) =>
		_characterInventories.TryGetValue( character, out var inventories )
			? inventories.ToArray() : Array.Empty<InventoryId>();
	public IReadOnlyList<ItemId> ItemsIn( InventoryId inventory ) =>
		_inventories.TryGetValue( inventory, out var document )
			? document.Value.Placements.Select( placement => placement.ItemId ).ToArray()
			: Array.Empty<ItemId>();
	public IReadOnlyList<LiveInventoryItemView> LiveInventoryRows() => _itemLocations.Keys
		.OrderBy( value => value.Value )
		.Select( item => TryCreateLiveInventoryRow( item, out var row ) ? row : null )
		.Where( row => row is not null )
		.Select( row => row! )
		.ToArray();
	public bool TryCreateLiveInventoryRow( ItemId itemId, out LiveInventoryItemView row )
	{
		row = null!;
		if ( !_items.TryGetValue( itemId, out var item ) ||
			!_itemLocations.TryGetValue( itemId, out var inventory ) ||
			!_inventories.ContainsKey( inventory ) ) return false;
		row = new LiveInventoryItemView(
			item.Id, OwningCharacter( inventory ), item.Definition, item.Traits );
		return true;
	}
	public IReadOnlyList<string> CharacterReferenceKeys( CharacterId character ) =>
		_references.Where( pair => pair.Value.CharacterId == character || pair.Value.RelatedCharacterId == character )
			.Select( pair => pair.Key ).ToArray();
	public bool IsKnownAccount( AccountId account ) => _accountCharacters.GetValueOrDefault( account ) > 0;
	public PersistentSceneEntityRecord? FirstSceneEntity( string kind ) =>
		_sceneEntities.Values.FirstOrDefault( value => value.Kind == kind );
	public IReadOnlyList<CharacterId> DoorOwners( SceneEntityId door ) =>
		_doorOwners.TryGetValue( door, out var owners ) ? owners.ToArray() : Array.Empty<CharacterId>();
	public IReadOnlyList<DocumentAddress> CharacterReferenceAddressesForScene( SceneEntityId sceneEntityId ) =>
		_references.Where( pair => pair.Value.SceneEntityId == sceneEntityId )
			.Select( pair => new DocumentAddress( DomainCollections.CharacterReferences, pair.Key ) ).ToArray();

	public void ObserveSceneSession( InteractionSession session )
	{
		ArgumentNullException.ThrowIfNull( session );
		RemoveSceneSession( session.Id );
		if ( session.Target.Kind != InteractionTargetKind.SceneEntity ) return;
		var indexed = new IndexedSceneSession(
			session.ConnectionId, session.CharacterId, new SceneEntityId( session.Target.Id ) );
		_sceneSessions[session.Id] = indexed;
		if ( !_sceneViewers.TryGetValue( indexed.SceneEntityId, out var viewers ) )
			_sceneViewers[indexed.SceneEntityId] = viewers = new HashSet<ConnectionId>();
		viewers.Add( indexed.ConnectionId );
		InvalidateRuntime( indexed.ConnectionId );
	}

	public void RemoveSceneSession( InteractionSessionId sessionId )
	{
		if ( !_sceneSessions.Remove( sessionId, out var indexed ) ) return;
		if ( _sceneViewers.TryGetValue( indexed.SceneEntityId, out var viewers ) )
		{
			viewers.Remove( indexed.ConnectionId );
			if ( viewers.Count == 0 ) _sceneViewers.Remove( indexed.SceneEntityId );
		}
		InvalidateRuntime( indexed.ConnectionId );
	}

	public IReadOnlyList<ConnectionId> SceneViewers( SceneEntityId sceneEntityId ) =>
		_sceneViewers.TryGetValue( sceneEntityId, out var viewers )
			? viewers.ToArray() : Array.Empty<ConnectionId>();

	public T GetOrCreateSlice<T>(
		ConnectionId connection,
		HL2RPProjectionSliceKind kind,
		Func<HL2RPProjectionSlice<T>> factory )
	{
		ArgumentNullException.ThrowIfNull( factory );
		var key = (connection, kind);
		if ( _projectionSlices.TryGetValue( key, out var cached ) && cached.Value is T typed ) return typed;
		var built = factory() ?? throw new InvalidOperationException( "Projection slice factory returned no value." );
		var dependencies = new HashSet<string>( built.RuntimeDependencies, StringComparer.Ordinal );
		dependencies.UnionWith( built.Documents.Select( DocumentDependency ) );
		_projectionSlices[key] = new CachedSlice( built.Value!, dependencies );
		_sliceBuildCounts[kind] = _sliceBuildCounts.GetValueOrDefault( kind ) + 1;
		return built.Value;
	}

	public long SliceBuildCount( HL2RPProjectionSliceKind kind ) => _sliceBuildCounts.GetValueOrDefault( kind );

	public void InvalidateRuntime( ConnectionId connection, params HL2RPProjectionSliceKind[] kinds )
	{
		var selected = kinds.Length == 0 ? null : kinds.ToHashSet();
		if ( selected is null || selected.Contains( HL2RPProjectionSliceKind.Inventories ) )
			foreach ( var key in _visibleSlices.Keys.Where( key => key.Connection == connection ).ToArray() )
				_visibleSlices.Remove( key );
		foreach ( var key in _projectionSlices.Keys.Where( key =>
			key.Connection == connection && (selected is null || selected.Contains( key.Kind )) ).ToArray() )
			_projectionSlices.Remove( key );
	}

	public void ObserveCharacterBindingChanged(
		ConnectionId connection,
		CharacterId? previous,
		CharacterId? current )
	{
		if ( previous == current ) return;
		if ( previous is CharacterId previousCharacter )
		{
			_connectionCharacters.Remove( connection );
			if ( _characterConnections.GetValueOrDefault( previousCharacter ) == connection )
				_characterConnections.Remove( previousCharacter );
		}
		if ( current is CharacterId currentCharacter )
		{
			if ( _characterConnections.TryGetValue( currentCharacter, out var existing ) && existing != connection )
				throw new InvalidOperationException( "An active character cannot be bound to two connections." );
			_connectionCharacters[connection] = currentCharacter;
			_characterConnections[currentCharacter] = connection;
		}
		InvalidateRuntime( connection );
	}

	public void ObserveConnection( ConnectionId connection, AccountId account )
	{
		if ( _connectionAccounts.TryGetValue( connection, out var previous ) && previous == account ) return;
		if ( _connectionAccounts.TryGetValue( connection, out previous ) &&
			_accountConnections.TryGetValue( previous, out var previousConnections ) )
		{
			previousConnections.Remove( connection );
			if ( previousConnections.Count == 0 ) _accountConnections.Remove( previous );
		}
		_connectionAccounts[connection] = account;
		if ( !_accountConnections.TryGetValue( account, out var connections ) )
			_accountConnections[account] = connections = new HashSet<ConnectionId>();
		connections.Add( connection );
	}

	public void ForgetConnection( ConnectionId connection )
	{
		if ( _connectionCharacters.TryGetValue( connection, out var character ) )
			ObserveCharacterBindingChanged( connection, character, null );
		if ( _connectionAccounts.Remove( connection, out var account ) &&
			_accountConnections.TryGetValue( account, out var connections ) )
		{
			connections.Remove( connection );
			if ( connections.Count == 0 ) _accountConnections.Remove( account );
		}
		ObserveVisibleInventories( connection, Array.Empty<InventoryId>() );
		InvalidateRuntime( connection );
	}

	public ConnectionId? ConnectionForCharacter( CharacterId character ) =>
		_characterConnections.TryGetValue( character, out var connection ) ? connection : null;
	public IReadOnlyList<ConnectionId> ConnectionsForAccount( AccountId account ) =>
		_accountConnections.TryGetValue( account, out var connections )
			? connections.ToArray() : Array.Empty<ConnectionId>();

	public void InvalidateRuntimeDependency( string category, string id ) =>
		InvalidateDependencies( new HashSet<string>( StringComparer.Ordinal )
		{
			RuntimeDependency( category, id )
		} );

	public static string RuntimeDependency( string category, string id ) => $"runtime:{category}:{id}";

	public IReadOnlyList<DocumentSnapshot<InventoryRecord>> VisibleInventories(
		ConnectionId connection,
		CharacterId character,
		Func<InventoryId, bool> canView )
	{
		ArgumentNullException.ThrowIfNull( canView );
		var key = (connection, character);
		if ( !_visibleSlices.TryGetValue( key, out var ids ) )
		{
			ids = _inventories.Keys.Where( canView ).ToArray();
			_visibleSlices[key] = ids;
		}
		// Re-check the transient capability on every read. The cached slice accelerates
		// discovery, but can never preserve access after a grant is revoked.
		return ids.Where( canView ).Select( id => _inventories.GetValueOrDefault( id ) )
			.Where( value => value is not null ).Select( value => value! ).ToArray();
	}

	public void InvalidateVisibility( ConnectionId? connection = null )
	{
		if ( connection is null )
		{
			_visibleSlices.Clear();
			foreach ( var key in _projectionSlices.Keys.Where(
				key => key.Kind == HL2RPProjectionSliceKind.Inventories ).ToArray() ) _projectionSlices.Remove( key );
		}
		if ( connection is ConnectionId id ) InvalidateRuntime( id, HL2RPProjectionSliceKind.Inventories );
	}

	public void ObserveVisibleInventories( ConnectionId connection, IEnumerable<InventoryId> inventories )
	{
		ArgumentNullException.ThrowIfNull( inventories );
		var current = inventories.ToHashSet();
		var previous = _viewedInventoriesByConnection.TryGetValue( connection, out var viewed )
			? viewed.ToArray() : Array.Empty<InventoryId>();
		foreach ( var inventory in previous.Where( inventory => !current.Contains( inventory ) ) )
			RemoveViewerMembership( inventory, connection );
		foreach ( var inventory in current ) AddViewerMembership( inventory, connection );
		if ( current.Count == 0 ) _viewedInventoriesByConnection.Remove( connection );
		else _viewedInventoriesByConnection[connection] = current;
	}

	public IReadOnlyList<ConnectionId> RefreshViewers(
		IEnumerable<InventoryId> inventories,
		Func<InventoryId, IReadOnlyList<ConnectionId>> resolver )
	{
		ArgumentNullException.ThrowIfNull( inventories );
		ArgumentNullException.ThrowIfNull( resolver );
		var updates = inventories.Distinct()
			.Select( inventory => (Inventory: inventory, Viewers: new HashSet<ConnectionId>( resolver( inventory ) )) )
			.ToArray();
		var affected = new HashSet<ConnectionId>();
		foreach ( var update in updates )
		{
			var previous = _viewers.TryGetValue( update.Inventory, out var viewers )
				? viewers.ToArray() : Array.Empty<ConnectionId>();
			affected.UnionWith( previous );
			affected.UnionWith( update.Viewers );
			foreach ( var viewer in previous.Where( viewer => !update.Viewers.Contains( viewer ) ) )
				RemoveViewerMembership( update.Inventory, viewer );
			foreach ( var viewer in update.Viewers ) AddViewerMembership( update.Inventory, viewer );
		}
		foreach ( var viewer in affected ) InvalidateRuntime( viewer, HL2RPProjectionSliceKind.Inventories );
		return affected.OrderBy( value => value.Value ).ToArray();
	}

	public IReadOnlyList<ConnectionId> Viewers( InventoryId inventory ) =>
		_viewers.TryGetValue( inventory, out var viewers ) ? viewers.ToArray() : Array.Empty<ConnectionId>();

	private void CollectDerivedDependencies( DocumentAddress address, ISet<string> dependencies )
	{
		if ( address.Collection == DomainCollections.Inventories )
		{
			if ( _inventoryKeys.TryGetValue( address.Key, out var inventory ) )
				dependencies.Add( RuntimeDependency( "inventory", inventory.ToString() ) );
		}
		else if ( address.Collection == DomainCollections.Items )
		{
			if ( _itemKeys.TryGetValue( address.Key, out var item ) )
				dependencies.Add( RuntimeDependency( "item", item.ToString() ) );
		}
		else if ( address.Collection == DomainCollections.Characters )
		{
			if ( _characterIds.TryGetValue( address.Key, out var character ) )
				dependencies.Add( RuntimeDependency( "character", character.ToString() ) );
		}
		else if ( address.Collection == DomainCollections.CharacterReferences &&
			_references.TryGetValue( address.Key, out var reference ) )
		{
			dependencies.Add( RuntimeDependency( "character", reference.CharacterId.ToString() ) );
			if ( reference.RelatedCharacterId is CharacterId related )
				dependencies.Add( RuntimeDependency( "character", related.ToString() ) );
			if ( reference.SceneEntityId is SceneEntityId scene )
				dependencies.Add( RuntimeDependency( "scene", scene.ToString() ) );
		}
		else if ( address.Collection == DomainCollections.SceneEntities )
		{
			if ( _sceneKeys.TryGetValue( address.Key, out var scene ) )
				dependencies.Add( RuntimeDependency( "scene", scene.ToString() ) );
		}
	}

	private void InvalidateDependencies( IReadOnlySet<string> dependencies )
	{
		if ( dependencies.Count == 0 ) return;
		foreach ( var key in _projectionSlices.Where(
			pair => pair.Value.Dependencies.Overlaps( dependencies ) ).Select( pair => pair.Key ).ToArray() )
			_projectionSlices.Remove( key );
	}

	private static string DocumentDependency( DocumentAddress address ) =>
		$"document:{address.Collection}:{address.Key}";

	private void RebuildInventoryDerivedIndexes()
	{
		_itemLocations.Clear();
		_owners.Clear();
		_characterInventories.Clear();
		_nestedInventories.Clear();
		foreach ( var inventory in _inventories.Values )
		{
			foreach ( var placement in inventory.Value.Placements ) _itemLocations[placement.ItemId] = inventory.Value.Id;
			AddNestedInventory( inventory.Value );
		}
		foreach ( var inventoryId in _inventories.Keys ) SetOwner( inventoryId, ResolveOwner( inventoryId ) );
	}

	private void AddViewerMembership( InventoryId inventory, ConnectionId connection )
	{
		if ( !_viewers.TryGetValue( inventory, out var viewers ) )
			_viewers[inventory] = viewers = new HashSet<ConnectionId>();
		viewers.Add( connection );
		if ( !_viewedInventoriesByConnection.TryGetValue( connection, out var inventories ) )
			_viewedInventoriesByConnection[connection] = inventories = new HashSet<InventoryId>();
		inventories.Add( inventory );
	}

	private void RemoveViewerMembership( InventoryId inventory, ConnectionId connection )
	{
		if ( _viewers.TryGetValue( inventory, out var viewers ) )
		{
			viewers.Remove( connection );
			if ( viewers.Count == 0 ) _viewers.Remove( inventory );
		}
		if ( _viewedInventoriesByConnection.TryGetValue( connection, out var inventories ) )
		{
			inventories.Remove( inventory );
			if ( inventories.Count == 0 ) _viewedInventoriesByConnection.Remove( connection );
		}
	}

	private void RebuildDoorOwners()
	{
		_doorOwners.Clear();
		foreach ( var reference in _references.Values.Where( value => value.Category == "door_ownership" && value.SceneEntityId is not null ) )
		{
			var door = reference.SceneEntityId!.Value;
			if ( !_doorOwners.TryGetValue( door, out var owners ) ) _doorOwners[door] = owners = new List<CharacterId>();
			owners.Add( reference.CharacterId );
		}
	}

	private void AddItem( string key, ItemRecord item )
	{
		_items[item.Id] = item;
		_itemKeys[key] = item.Id;
		Increment( _definitionCounts, item.Definition );
	}

	private void RemoveItem( string key, ItemRecord item )
	{
		_items.Remove( item.Id );
		_itemKeys.Remove( key );
		Decrement( _definitionCounts, item.Definition );
	}

	private void AddNestedInventory( InventoryRecord inventory )
	{
		if ( inventory.Owner.Kind != InventoryOwnerKind.ParentItem ) return;
		var item = new ItemId( inventory.Owner.OwnerId );
		if ( !_nestedInventories.TryGetValue( item, out var inventories ) )
			_nestedInventories[item] = inventories = new HashSet<InventoryId>();
		inventories.Add( inventory.Id );
	}

	private void RemoveNestedInventory( InventoryRecord inventory )
	{
		if ( inventory.Owner.Kind != InventoryOwnerKind.ParentItem ) return;
		var item = new ItemId( inventory.Owner.OwnerId );
		if ( !_nestedInventories.TryGetValue( item, out var inventories ) ) return;
		inventories.Remove( inventory.Id );
		if ( inventories.Count == 0 ) _nestedInventories.Remove( item );
	}

	private void CollectNestedDescendants( ItemId item, ISet<InventoryId> inventories )
	{
		if ( !_nestedInventories.TryGetValue( item, out var nested ) ) return;
		foreach ( var inventory in nested )
		{
			if ( !inventories.Add( inventory ) || !_inventories.TryGetValue( inventory, out var document ) ) continue;
			foreach ( var placement in document.Value.Placements ) CollectNestedDescendants( placement.ItemId, inventories );
		}
	}

	private void SetOwner( InventoryId inventory, CharacterId? owner )
	{
		if ( _owners.TryGetValue( inventory, out var previous ) && previous is CharacterId old &&
			_characterInventories.TryGetValue( old, out var previousInventories ) )
		{
			previousInventories.Remove( inventory );
			if ( previousInventories.Count == 0 ) _characterInventories.Remove( old );
		}
		if ( !_inventories.ContainsKey( inventory ) ) { _owners.Remove( inventory ); return; }
		_owners[inventory] = owner;
		if ( owner is not CharacterId character ) return;
		if ( !_characterInventories.TryGetValue( character, out var inventories ) )
			_characterInventories[character] = inventories = new HashSet<InventoryId>();
		inventories.Add( inventory );
	}

	private CharacterId? ResolveOwner( InventoryId inventoryId )
	{
		var seen = new HashSet<InventoryId>();
		var currentId = inventoryId;
		while ( seen.Add( currentId ) && _inventories.TryGetValue( currentId, out var document ) )
		{
			var owner = document.Value.Owner;
			if ( owner.Kind == InventoryOwnerKind.Character ) return new CharacterId( owner.OwnerId );
			if ( owner.Kind != InventoryOwnerKind.ParentItem ) return null;
			if ( !_itemLocations.TryGetValue( new ItemId( owner.OwnerId ), out currentId ) ) return null;
		}
		return null;
	}

	private static void Increment<TKey>( IDictionary<TKey, int> values, TKey key ) where TKey : notnull
	{
		values.TryGetValue( key, out var current );
		values[key] = current + 1;
	}
	private static void Decrement<TKey>( IDictionary<TKey, int> values, TKey key ) where TKey : notnull
	{
		values.TryGetValue( key, out var current );
		var next = current - 1;
		if ( next <= 0 ) values.Remove( key ); else values[key] = next;
	}

	private sealed record IndexedSceneSession(
		ConnectionId ConnectionId, CharacterId CharacterId, SceneEntityId SceneEntityId );
	private sealed record CachedSlice( object Value, HashSet<string> Dependencies );
}

/// <summary>
/// Delta-updated live inventory directory. Mutation paths touch only named rows;
/// the immutable, sorted snapshot is materialized lazily when a chat consumer
/// actually captures it.
/// </summary>
public sealed class HL2RPIncrementalLiveInventoryView : ILiveInventoryView
{
	private readonly object _sync = new();
	private readonly Dictionary<ItemId, LiveInventoryItemView> _rows = new();
	private long _version;
	private LiveInventorySnapshot? _snapshot = new( 0, Array.Empty<LiveInventoryItemView>() );

	public int Count { get { lock ( _sync ) return _rows.Count; } }

	public int Rebuild( long version, IEnumerable<LiveInventoryItemView> rows )
	{
		if ( version < 0 ) throw new ArgumentOutOfRangeException( nameof(version) );
		ArgumentNullException.ThrowIfNull( rows );
		var map = new Dictionary<ItemId, LiveInventoryItemView>();
		foreach ( var row in rows )
		{
			ArgumentNullException.ThrowIfNull( row );
			if ( !map.TryAdd( row.ItemId, row ) )
				throw new ArgumentException( "Live inventory items must be unique.", nameof(rows) );
		}
		lock ( _sync )
		{
			if ( version < _version )
				throw new InvalidOperationException( "Live inventory view cannot move backwards." );
			_rows.Clear();
			foreach ( var pair in map ) _rows[pair.Key] = pair.Value;
			_version = version;
			_snapshot = null;
		}
		return map.Count;
	}

	public int Apply( long version, IEnumerable<HL2RPLiveInventoryDelta> deltas )
	{
		if ( version < 0 ) throw new ArgumentOutOfRangeException( nameof(version) );
		ArgumentNullException.ThrowIfNull( deltas );
		var materialized = deltas.ToArray();
		if ( materialized.Any( value => value is null ) )
			throw new ArgumentException( "Live inventory deltas cannot contain null values.", nameof(deltas) );
		if ( materialized.Select( value => value.ItemId ).Distinct().Count() != materialized.Length )
			throw new ArgumentException( "Live inventory deltas must name unique item IDs.", nameof(deltas) );
		if ( materialized.Any( value => value.Row is not null && value.Row.ItemId != value.ItemId ) )
			throw new ArgumentException( "Live inventory delta identity does not match its row.", nameof(deltas) );
		lock ( _sync )
		{
			if ( version < _version )
				throw new InvalidOperationException( "Live inventory view cannot move backwards." );
			foreach ( var delta in materialized )
			{
				if ( delta.Row is null ) _rows.Remove( delta.ItemId );
				else _rows[delta.ItemId] = delta.Row;
			}
			_version = version;
			_snapshot = null;
		}
		return materialized.Length;
	}

	public LiveInventorySnapshot Capture()
	{
		lock ( _sync )
		{
			return _snapshot ??= new LiveInventorySnapshot(
				_version,
				Array.AsReadOnly( _rows.Values.OrderBy( value => value.ItemId.Value ).ToArray() ) );
		}
	}
}

/// <summary>
/// Connection-position directory with O(1) targeted updates. Full enumeration is
/// reserved for a capture or the host's explicit movement sampling pass.
/// </summary>
public sealed class HL2RPIncrementalChatConnectionDirectory : IChatConnectionPositionDirectory
{
	private readonly object _sync = new();
	private readonly Dictionary<ConnectionId, LiveChatConnection> _rows = new();
	private readonly Dictionary<CharacterId, ConnectionId> _characters = new();
	private long _version;
	private ChatConnectionSnapshot? _snapshot = ChatConnectionSnapshot.Empty;

	public int Count { get { lock ( _sync ) return _rows.Count; } }

	public int Rebuild( long version, IEnumerable<LiveChatConnection> rows )
	{
		if ( version < 0 ) throw new ArgumentOutOfRangeException( nameof(version) );
		ArgumentNullException.ThrowIfNull( rows );
		var materialized = rows.ToArray();
		var validated = new ChatConnectionSnapshot( version, materialized );
		lock ( _sync )
		{
			if ( version < _version )
				throw new InvalidOperationException( "Live chat connection directory cannot move backwards." );
			_rows.Clear();
			_characters.Clear();
			foreach ( var row in validated.Connections )
			{
				_rows[row.ConnectionId] = row;
				_characters[row.CharacterId] = row.ConnectionId;
			}
			_version = version;
			_snapshot = validated;
		}
		return materialized.Length;
	}

	public int Apply( long version, ConnectionId connectionId, LiveChatConnection? row )
	{
		if ( version < 0 ) throw new ArgumentOutOfRangeException( nameof(version) );
		if ( row is not null && row.ConnectionId != connectionId )
			throw new ArgumentException( "Live chat connection delta identity does not match its row.", nameof(row) );
		lock ( _sync )
		{
			if ( version < _version )
				throw new InvalidOperationException( "Live chat connection directory cannot move backwards." );
			if ( row is not null && _characters.TryGetValue( row.CharacterId, out var characterOwner ) &&
				characterOwner != connectionId )
				throw new ArgumentException( "Live chat connections must have unique active character IDs.", nameof(row) );
			if ( _rows.Remove( connectionId, out var previous ) ) _characters.Remove( previous.CharacterId );
			if ( row is not null )
			{
				_rows[connectionId] = row;
				_characters[row.CharacterId] = connectionId;
			}
			_version = version;
			_snapshot = null;
		}
		return 1;
	}

	public ChatConnectionSnapshot Capture()
	{
		lock ( _sync ) return _snapshot ??= new ChatConnectionSnapshot( _version, _rows.Values );
	}
}

public sealed class HL2RPIncrementalChatAuthorityDirectory : IChatAuthorityDirectory, IPermissionAuthorizer
{
	private readonly object _sync = new();
	private readonly Dictionary<ConnectionId, LiveChatAuthority> _rows = new();
	private readonly Dictionary<CharacterId, ConnectionId> _characters = new();
	private long _version;
	private ChatAuthoritySnapshot? _snapshot = ChatAuthoritySnapshot.Empty;

	public int Count { get { lock ( _sync ) return _rows.Count; } }

	public int Rebuild( long version, IEnumerable<LiveChatAuthority> rows )
	{
		if ( version < 0 ) throw new ArgumentOutOfRangeException( nameof(version) );
		ArgumentNullException.ThrowIfNull( rows );
		var materialized = rows.ToArray();
		var validated = new ChatAuthoritySnapshot( version, materialized );
		lock ( _sync )
		{
			if ( version < _version )
				throw new InvalidOperationException( "Live chat authority cannot move backwards." );
			_rows.Clear();
			_characters.Clear();
			foreach ( var row in validated.Authorities )
			{
				_rows[row.ConnectionId] = row;
				_characters[row.CharacterId] = row.ConnectionId;
			}
			_version = version;
			_snapshot = validated;
		}
		return materialized.Length;
	}

	public int Apply( long version, ConnectionId connectionId, LiveChatAuthority? row )
	{
		if ( version < 0 ) throw new ArgumentOutOfRangeException( nameof(version) );
		if ( row is not null && row.ConnectionId != connectionId )
			throw new ArgumentException( "Live chat authority delta identity does not match its row.", nameof(row) );
		lock ( _sync )
		{
			if ( version < _version )
				throw new InvalidOperationException( "Live chat authority cannot move backwards." );
			if ( row is not null && _characters.TryGetValue( row.CharacterId, out var characterOwner ) &&
				characterOwner != connectionId )
				throw new ArgumentException( "Live chat authorities must have unique active character IDs.", nameof(row) );
			if ( _rows.Remove( connectionId, out var previous ) ) _characters.Remove( previous.CharacterId );
			if ( row is not null )
			{
				_rows[connectionId] = row;
				_characters[row.CharacterId] = connectionId;
			}
			_version = version;
			_snapshot = null;
		}
		return 1;
	}

	public ChatAuthoritySnapshot Capture()
	{
		lock ( _sync ) return _snapshot ??= new ChatAuthoritySnapshot( _version, _rows.Values );
	}

	public bool HasPermission( AccountId accountId, CharacterId characterId, string permissionId )
	{
		if ( string.IsNullOrWhiteSpace( permissionId ) ) return false;
		lock ( _sync ) return _characters.TryGetValue( characterId, out var connection ) &&
			_rows.TryGetValue( connection, out var value ) && value.AccountId == accountId &&
			value.HasPermission( permissionId );
	}
}

public sealed class HL2RPIncrementalCombatPlayerTargetDirectory : ICombatPlayerTargetDirectory
{
	private readonly object _sync = new();
	private readonly Dictionary<ConnectionId, CombatPlayerTarget> _rows = new();
	private readonly Dictionary<string, ConnectionId> _tokens = new( StringComparer.Ordinal );
	private readonly Dictionary<CharacterId, ConnectionId> _characters = new();
	private IReadOnlyList<CombatPlayerTarget>? _snapshot = Array.Empty<CombatPlayerTarget>();

	public int Count { get { lock ( _sync ) return _rows.Count; } }

	public int Rebuild( IEnumerable<CombatPlayerTarget> rows )
	{
		ArgumentNullException.ThrowIfNull( rows );
		var materialized = rows.ToArray();
		var connections = new HashSet<ConnectionId>();
		var tokens = new HashSet<string>( StringComparer.Ordinal );
		var characters = new HashSet<CharacterId>();
		foreach ( var row in materialized )
		{
			Validate( row );
			if ( !connections.Add( row.Actor.ConnectionId ) || !tokens.Add( row.Token ) ||
				!characters.Add( row.Actor.CharacterId ) )
				throw new ArgumentException( "Combat target connections, tokens, and characters must be unique.", nameof(rows) );
		}
		lock ( _sync )
		{
			_rows.Clear();
			_tokens.Clear();
			_characters.Clear();
			foreach ( var row in materialized )
			{
				_rows[row.Actor.ConnectionId] = row;
				_tokens[row.Token] = row.Actor.ConnectionId;
				_characters[row.Actor.CharacterId] = row.Actor.ConnectionId;
			}
			_snapshot = Array.AsReadOnly( materialized.OrderBy( value => value.Token, StringComparer.Ordinal ).ToArray() );
		}
		return materialized.Length;
	}

	public int Apply( ConnectionId connectionId, CombatPlayerTarget? row )
	{
		if ( row is not null )
		{
			Validate( row );
			if ( row.Actor.ConnectionId != connectionId )
				throw new ArgumentException( "Combat target delta identity does not match its row.", nameof(row) );
		}
		lock ( _sync )
		{
			if ( row is not null && _tokens.TryGetValue( row.Token, out var tokenOwner ) && tokenOwner != connectionId )
				throw new ArgumentException( "Combat target tokens must be unique.", nameof(row) );
			if ( row is not null && _characters.TryGetValue( row.Actor.CharacterId, out var characterOwner ) &&
				characterOwner != connectionId )
				throw new ArgumentException( "Combat target characters must be unique.", nameof(row) );
			if ( _rows.Remove( connectionId, out var previous ) )
			{
				_tokens.Remove( previous.Token );
				_characters.Remove( previous.Actor.CharacterId );
			}
			if ( row is not null )
			{
				_rows[connectionId] = row;
				_tokens[row.Token] = connectionId;
				_characters[row.Actor.CharacterId] = connectionId;
			}
			_snapshot = null;
		}
		return 1;
	}

	public IReadOnlyList<CombatPlayerTarget> Capture()
	{
		lock ( _sync ) return _snapshot ??= Array.AsReadOnly(
			_rows.Values.OrderBy( value => value.Token, StringComparer.Ordinal ).ToArray() );
	}

	public OperationResult<CombatPlayerTarget> Resolve( string token )
	{
		if ( string.IsNullOrWhiteSpace( token ) )
			return OperationResult<CombatPlayerTarget>.Failure( ErrorCode.NotFound, "Shot has no player target." );
		lock ( _sync )
			return _tokens.TryGetValue( token, out var connection ) && _rows.TryGetValue( connection, out var target )
				? OperationResult<CombatPlayerTarget>.Success( target )
				: OperationResult<CombatPlayerTarget>.Failure( ErrorCode.NotFound, "Shot target is not a live player." );
	}

	private static void Validate( CombatPlayerTarget row )
	{
		ArgumentNullException.ThrowIfNull( row );
		if ( string.IsNullOrWhiteSpace( row.Token ) )
			throw new ArgumentException( "Combat target token cannot be empty.", nameof(row) );
		ArgumentNullException.ThrowIfNull( row.DropTransform );
	}
}
