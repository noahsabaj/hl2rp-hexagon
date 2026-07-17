#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Hexagon.V2.Domain;
using Hexagon.V2.Persistence;
using HL2RP.V2.Features;
using HL2RP.V2.Runtime;
using HL2RP.V2.Showcase.Combat;
using HL2RP.V2.Tests.Features;

namespace HL2RP.V2.Tests.Runtime;

[TestClass]
public sealed class LiveProjectionIncrementalTests
{
	[TestMethod]
	public async Task DestinationBeforeSourceReceiptMoveIsOrderIndependentAndMatchesFreshRebuild()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var sourceActor = environment.Actor( 901, new CharacterId( Guid.Parse( "10000000-0000-4000-8000-000000000001" ) ) );
		var targetActor = environment.Actor( 902, new CharacterId( Guid.Parse( "20000000-0000-4000-8000-000000000002" ) ) );
		var sourceCharacter = environment.Character( sourceActor, name: "Source Citizen" );
		var targetCharacter = environment.Character( targetActor, name: "Target Citizen" );
		var sourceId = new InventoryId( Guid.Parse( "ffffffff-ffff-4fff-8fff-fffffffffff0" ) );
		var targetId = new InventoryId( Guid.Parse( "00000000-0000-4000-8000-000000000010" ) );
		var item = environment.Item( "item.water" ) with
		{
			Id = new ItemId( Guid.Parse( "30000000-0000-4000-8000-000000000003" ) )
		};
		var source = environment.Inventory( sourceCharacter.Id,
			new[] { new InventoryPlacement( item.Id, 0, 0 ) }, 1, 1, sourceId );
		var target = environment.Inventory( targetCharacter.Id, id: targetId, width: 1, height: 1 );
		await environment.SeedAsync( unit =>
		{
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( sourceCharacter.Id ), sourceCharacter );
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( targetCharacter.Id ), targetCharacter );
			unit.Create( environment.Repositories.CharacterLifecycleGuards,
				DomainKeys.CharacterLifecycleGuard( sourceCharacter.Id ),
				new CharacterLifecycleGuardRecord { CharacterId = sourceCharacter.Id } );
			unit.Create( environment.Repositories.CharacterLifecycleGuards,
				DomainKeys.CharacterLifecycleGuard( targetCharacter.Id ),
				new CharacterLifecycleGuardRecord { CharacterId = targetCharacter.Id } );
			unit.Create( environment.Repositories.Items, DomainKeys.Item( item.Id ), item );
			unit.Create( environment.Repositories.Inventories, DomainKeys.Inventory( source.Id ), source );
			unit.Create( environment.Repositories.Inventories, DomainKeys.Inventory( target.Id ), target );
		} );

		var incremental = Rebuild( environment );
		var live = new HL2RPIncrementalLiveInventoryView();
		live.Rebuild( environment.Provider.Health.Sequence, incremental.LiveInventoryRows() );
		await using var mutation = environment.Provider.BeginUnitOfWork();
		var sourceDocument = environment.Repositories.Inventories.Find( DomainKeys.Inventory( source.Id ) )!;
		var targetDocument = environment.Repositories.Inventories.Find( DomainKeys.Inventory( target.Id ) )!;
		var sourceEditor = mutation.Edit( environment.Repositories.Inventories, sourceDocument )!;
		var targetEditor = mutation.Edit( environment.Repositories.Inventories, targetDocument )!;
		sourceEditor.Replace( sourceDocument.Value with { Placements = Array.Empty<InventoryPlacement>() } );
		targetEditor.Replace( targetDocument.Value with { Placements = new[] { new InventoryPlacement( item.Id, 0, 0 ) } } );
		mutation.Save( sourceEditor );
		mutation.Save( targetEditor );
		var committed = await mutation.CommitAsync();
		Assert.IsTrue( committed.Succeeded, committed.Error?.Message );
		var receipt = committed.Value!;
		var inventoryAddresses = receipt.Documents
			.Where( value => value.Address.Collection == DomainCollections.Inventories )
			.Select( value => value.Address.Key )
			.ToArray();
		CollectionAssert.AreEqual(
			new[] { DomainKeys.Inventory( target.Id ), DomainKeys.Inventory( source.Id ) },
			inventoryAddresses,
			"The regression requires the provider's key order to present destination before source." );

		var applied = incremental.Apply( receipt, environment.Repositories );
		Assert.AreEqual( 1, live.Apply( receipt.Sequence, Deltas( incremental, applied ) ),
			"Only the moved item row should be revisited." );
		Assert.IsEmpty( incremental.ItemsIn( source.Id ) );
		Assert.AreEqual( item.Id, incremental.ItemsIn( target.Id ).Single() );
		var captured = live.Capture();
		Assert.AreEqual( receipt.Sequence, captured.Version );
		Assert.AreEqual( environment.Provider.Health.Sequence, captured.Version );
		Assert.AreEqual( targetCharacter.Id, captured.Items.Single().OwningCharacterId );

		var rebuilt = Rebuild( environment );
		var rebuiltSnapshot = CaptureLive( receipt.Sequence, rebuilt );
		AssertLiveRowsEqual( rebuiltSnapshot.Items, captured.Items );
		CollectionAssert.AreEqual( LiveSnapshotBytes( rebuiltSnapshot ), LiveSnapshotBytes( captured ) );

		var staleDeletion = new CommitReceipt( Math.Max( 1, receipt.Sequence - 1 ), new[]
		{
			new CommittedDocumentVersion(
				new DocumentAddress( DomainCollections.Inventories, DomainKeys.Inventory( target.Id ) ),
				DocumentRevision.None,
				true )
		} );
		var staleApplied = incremental.Apply( staleDeletion, environment.Repositories );
		live.Apply( environment.Provider.Health.Sequence, Deltas( incremental, staleApplied ) );
		Assert.AreEqual( item.Id, incremental.ItemsIn( target.Id ).Single(),
			"A delayed older deletion receipt must converge to the currently published document." );
		CollectionAssert.AreEqual( LiveSnapshotBytes( captured ), LiveSnapshotBytes( live.Capture() ) );
	}

	[TestMethod]
	public void TenThousandUnrelatedInventoriesAreNotRevisitedByOneMove()
	{
		const int unrelatedCount = 10_000;
		var owner = new CharacterId( Guid.Parse( "40000000-0000-4000-8000-000000000004" ) );
		var targetOwner = new CharacterId( Guid.Parse( "50000000-0000-4000-8000-000000000005" ) );
		var inventories = new List<DocumentSnapshot<InventoryRecord>>( unrelatedCount + 2 );
		var items = new List<DocumentSnapshot<ItemRecord>>( unrelatedCount + 1 );
		for ( var index = 0; index < unrelatedCount; index++ )
		{
			var inventoryId = InventoryIdAt( index + 1 );
			var itemId = ItemIdAt( index + 1 );
			inventories.Add( InventoryDocument( inventoryId, owner, new[] { new InventoryPlacement( itemId, 0, 0 ) } ) );
			items.Add( ItemDocument( itemId ) );
		}
		var sourceId = InventoryIdAt( unrelatedCount + 1 );
		var targetId = InventoryIdAt( unrelatedCount + 2 );
		var movedId = ItemIdAt( unrelatedCount + 1 );
		var source = InventoryDocument( sourceId, owner, new[] { new InventoryPlacement( movedId, 0, 0 ) } );
		var target = InventoryDocument( targetId, targetOwner, Array.Empty<InventoryPlacement>() );
		inventories.Add( source );
		inventories.Add( target );
		items.Add( ItemDocument( movedId ) );

		var indexProjection = new HL2RPProjectionIndex();
		indexProjection.Rebuild( inventories, items );
		var live = new HL2RPIncrementalLiveInventoryView();
		live.Rebuild( 1, indexProjection.LiveInventoryRows() );
		var sourceAfter = source.Value with { Placements = Array.Empty<InventoryPlacement>() };
		var targetAfter = target.Value with { Placements = new[] { new InventoryPlacement( movedId, 0, 0 ) } };
		var applied = indexProjection.RefreshInventories( new[]
		{
			(targetId, (DocumentSnapshot<InventoryRecord>?)new DocumentSnapshot<InventoryRecord>(
				DomainKeys.Inventory( targetId ), new DocumentRevision( 2 ), targetAfter )),
			(sourceId, (DocumentSnapshot<InventoryRecord>?)new DocumentSnapshot<InventoryRecord>(
				DomainKeys.Inventory( sourceId ), new DocumentRevision( 2 ), sourceAfter ))
		} );

		Assert.HasCount( 2, applied.AffectedInventories );
		Assert.AreEqual( movedId, applied.AffectedLiveItems.Single(),
			"No item from an unrelated inventory may be revisited." );
		Assert.AreEqual( 1, live.Apply( 2, Deltas( indexProjection, applied ) ) );
		Assert.AreEqual( unrelatedCount + 1, live.Count );
		Assert.AreEqual( 2L, live.Capture().Version );

		var finalInventories = inventories
			.Where( value => value.Value.Id != sourceId && value.Value.Id != targetId )
			.Append( new DocumentSnapshot<InventoryRecord>( DomainKeys.Inventory( sourceId ), new DocumentRevision( 2 ), sourceAfter ) )
			.Append( new DocumentSnapshot<InventoryRecord>( DomainKeys.Inventory( targetId ), new DocumentRevision( 2 ), targetAfter ) )
			.ToArray();
		var rebuilt = new HL2RPProjectionIndex();
		rebuilt.Rebuild( finalInventories, items );
		var rebuiltSnapshot = CaptureLive( 2, rebuilt );
		AssertLiveRowsEqual( rebuiltSnapshot.Items, live.Capture().Items );
		CollectionAssert.AreEqual( LiveSnapshotBytes( rebuiltSnapshot ), LiveSnapshotBytes( live.Capture() ) );
	}

	[TestMethod]
	public void NestedBagMoveInvalidatesDescendantOwnershipAndMatchesFreshRebuild()
	{
		var sourceOwner = new CharacterId( DeterministicGuid( 1, 0x41 ) );
		var targetOwner = new CharacterId( DeterministicGuid( 2, 0x41 ) );
		var sourceId = InventoryIdAt( 20_001 );
		var targetId = InventoryIdAt( 20_002 );
		var nestedId = InventoryIdAt( 20_003 );
		var bagId = ItemIdAt( 20_001 );
		var nestedItemId = ItemIdAt( 20_002 );
		var source = InventoryDocument(
			sourceId, InventoryOwner.Character( sourceOwner ), new[] { new InventoryPlacement( bagId, 0, 0 ) } );
		var target = InventoryDocument(
			targetId, InventoryOwner.Character( targetOwner ), Array.Empty<InventoryPlacement>() );
		var nested = InventoryDocument(
			nestedId, InventoryOwner.ParentItem( bagId ), new[] { new InventoryPlacement( nestedItemId, 0, 0 ) } );
		var itemDocuments = new[] { ItemDocument( bagId, "item.bag" ), ItemDocument( nestedItemId ) };
		var index = new HL2RPProjectionIndex();
		index.Rebuild( new[] { source, target, nested }, itemDocuments );
		var live = new HL2RPIncrementalLiveInventoryView();
		live.Rebuild( 1, index.LiveInventoryRows() );

		var sourceAfter = source.Value with { Placements = Array.Empty<InventoryPlacement>() };
		var targetAfter = target.Value with { Placements = new[] { new InventoryPlacement( bagId, 0, 0 ) } };
		var applied = index.RefreshInventories( new[]
		{
			(targetId, (DocumentSnapshot<InventoryRecord>?)new DocumentSnapshot<InventoryRecord>(
				DomainKeys.Inventory( targetId ), new DocumentRevision( 2 ), targetAfter )),
			(sourceId, (DocumentSnapshot<InventoryRecord>?)new DocumentSnapshot<InventoryRecord>(
				DomainKeys.Inventory( sourceId ), new DocumentRevision( 2 ), sourceAfter ))
		} );
		CollectionAssert.AreEquivalent(
			new[] { sourceId, targetId, nestedId }, applied.AffectedInventories.ToArray() );
		CollectionAssert.AreEquivalent(
			new[] { bagId, nestedItemId }, applied.AffectedLiveItems.ToArray() );
		Assert.AreEqual( targetOwner, index.OwningCharacter( nestedId ) );
		Assert.AreEqual( 2, live.Apply( 2, Deltas( index, applied ) ) );
		Assert.IsTrue( live.Capture().Items.All( row => row.OwningCharacterId == targetOwner ) );

		var rebuilt = new HL2RPProjectionIndex();
		rebuilt.Rebuild( new[]
		{
			new DocumentSnapshot<InventoryRecord>( source.Key, new DocumentRevision( 2 ), sourceAfter ),
			new DocumentSnapshot<InventoryRecord>( target.Key, new DocumentRevision( 2 ), targetAfter ),
			nested
		}, itemDocuments );
		CollectionAssert.AreEqual(
			LiveSnapshotBytes( CaptureLive( 2, rebuilt ) ), LiveSnapshotBytes( live.Capture() ) );
	}

	[TestMethod]
	public void DeletedInventoryRemovesLiveRowsAndMatchesFreshRebuild()
	{
		var owner = new CharacterId( DeterministicGuid( 1, 0x42 ) );
		var inventoryId = InventoryIdAt( 30_001 );
		var itemId = ItemIdAt( 30_001 );
		var inventory = InventoryDocument(
			inventoryId, owner, new[] { new InventoryPlacement( itemId, 0, 0 ) } );
		var item = ItemDocument( itemId );
		var index = new HL2RPProjectionIndex();
		index.Rebuild( new[] { inventory }, new[] { item } );
		var live = new HL2RPIncrementalLiveInventoryView();
		live.Rebuild( 1, index.LiveInventoryRows() );

		var applied = index.RefreshInventories( new[]
		{
			(inventoryId, (DocumentSnapshot<InventoryRecord>?)null)
		} );
		Assert.AreEqual( inventoryId, applied.AffectedInventories.Single() );
		Assert.AreEqual( itemId, applied.AffectedLiveItems.Single() );
		Assert.AreEqual( 1, live.Apply( 2, Deltas( index, applied ) ) );
		Assert.AreEqual( 2L, live.Capture().Version );
		Assert.IsEmpty( live.Capture().Items );

		var rebuilt = new HL2RPProjectionIndex();
		rebuilt.Rebuild( Array.Empty<DocumentSnapshot<InventoryRecord>>(), new[] { item } );
		CollectionAssert.AreEqual(
			LiveSnapshotBytes( CaptureLive( 2, rebuilt ) ), LiveSnapshotBytes( live.Capture() ) );
	}

	[TestMethod]
	public void ViewerRefreshTargetsBothPreviousAndCurrentViewer()
	{
		var inventory = InventoryIdAt( 40_001 );
		var previous = new ConnectionId( DeterministicGuid( 1, 0x43 ) );
		var current = new ConnectionId( DeterministicGuid( 2, 0x43 ) );
		var index = new HL2RPProjectionIndex();
		index.ObserveVisibleInventories( previous, new[] { inventory } );
		var lookups = 0;
		var affected = index.RefreshViewers( new[] { inventory }, candidate =>
		{
			lookups++;
			Assert.AreEqual( inventory, candidate );
			return new[] { current };
		} );
		Assert.AreEqual( 1, lookups );
		CollectionAssert.AreEquivalent( new[] { previous, current }, affected.ToArray() );
		Assert.AreEqual( current, index.Viewers( inventory ).Single() );
		index.ForgetConnection( current );
		Assert.IsEmpty( index.Viewers( inventory ) );
	}

	[TestMethod]
	public void UnaffectedVisibilityDiscoverySurvivesAnotherInventoryMutation()
	{
		var ownerA = new CharacterId( DeterministicGuid( 1, 0x44 ) );
		var ownerB = new CharacterId( DeterministicGuid( 2, 0x44 ) );
		var connectionA = new ConnectionId( DeterministicGuid( 3, 0x44 ) );
		var connectionB = new ConnectionId( DeterministicGuid( 4, 0x44 ) );
		var inventoryA = InventoryDocument(
			InventoryIdAt( 50_001 ), ownerA, Array.Empty<InventoryPlacement>() );
		var inventoryB = InventoryDocument(
			InventoryIdAt( 50_002 ), ownerB, Array.Empty<InventoryPlacement>() );
		var index = new HL2RPProjectionIndex();
		index.Rebuild( new[] { inventoryA, inventoryB }, Array.Empty<DocumentSnapshot<ItemRecord>>() );
		var checksA = 0;
		var checksB = 0;
		bool CanViewA( InventoryId candidate ) { checksA++; return candidate == inventoryA.Value.Id; }
		bool CanViewB( InventoryId candidate ) { checksB++; return candidate == inventoryB.Value.Id; }
		Assert.AreEqual( inventoryA.Value.Id,
			index.VisibleInventories( connectionA, ownerA, CanViewA ).Single().Value.Id );
		Assert.AreEqual( inventoryB.Value.Id,
			index.VisibleInventories( connectionB, ownerB, CanViewB ).Single().Value.Id );
		index.ObserveVisibleInventories( connectionA, new[] { inventoryA.Value.Id } );
		index.ObserveVisibleInventories( connectionB, new[] { inventoryB.Value.Id } );
		var checksAfterDiscoveryA = checksA;
		var checksAfterDiscoveryB = checksB;

		index.RefreshInventories( new[]
		{
			(inventoryA.Value.Id, (DocumentSnapshot<InventoryRecord>?)new DocumentSnapshot<InventoryRecord>(
				inventoryA.Key, new DocumentRevision( 2 ), inventoryA.Value ))
		} );
		index.RefreshViewers( new[] { inventoryA.Value.Id }, _ => new[] { connectionA } );
		_ = index.VisibleInventories( connectionA, ownerA, CanViewA );
		_ = index.VisibleInventories( connectionB, ownerB, CanViewB );

		Assert.AreEqual( 3, checksA - checksAfterDiscoveryA,
			"The affected connection must rediscover both indexed inventories, then recheck the retained capability." );
		Assert.AreEqual( 1, checksB - checksAfterDiscoveryB,
			"The unaffected connection must only recheck its cached capability, not rediscover all inventories." );
	}

	[TestMethod]
	public void IdenticalRowDeltasSkipSnapshotInvalidationInBothDirectories()
	{
		var positions = new HL2RPIncrementalChatConnectionDirectory();
		var authorities = new HL2RPIncrementalChatAuthorityDirectory();
		var connection = new ConnectionId( DeterministicGuid( 1, 0x71 ) );
		var character = new CharacterId( DeterministicGuid( 1, 0x72 ) );
		var account = new AccountId( 5UL );
		positions.Rebuild( 1, new[]
		{
			new LiveChatConnection( connection, character, new ChatPosition( 1, 2, 3 ) )
		} );
		authorities.Rebuild( 1, new[]
		{
			new LiveChatAuthority( connection, account, character, new FactionId( "citizen" ), new[] { "chat.local" } )
		} );
		var positionSnapshot = positions.Capture();
		var authoritySnapshot = authorities.Capture();

		Assert.AreEqual( 0, positions.Apply( 2, connection,
			new LiveChatConnection( connection, character, new ChatPosition( 1, 2, 3 ) ) ) );
		Assert.AreEqual( 0, authorities.Apply( 2, connection,
			new LiveChatAuthority( connection, account, character, new FactionId( "citizen" ), new[] { "chat.local" } ) ) );
		Assert.AreSame( positionSnapshot, positions.Capture(),
			"A value-identical connection row must not invalidate the cached snapshot." );
		Assert.AreSame( authoritySnapshot, authorities.Capture(),
			"A value-identical authority row must not invalidate the cached snapshot." );
		Assert.AreEqual( 0, authorities.Apply( 3, new ConnectionId( DeterministicGuid( 2, 0x73 ) ), null ),
			"Removing an absent row is also a snapshot-preserving no-op." );
		Assert.AreSame( authoritySnapshot, authorities.Capture() );

		Assert.AreEqual( 1, authorities.Apply( 4, connection, new LiveChatAuthority(
			connection, account, character, new FactionId( "citizen" ),
			new[] { "chat.local", "chat.dispatch" } ) ) );
		Assert.AreNotSame( authoritySnapshot, authorities.Capture(),
			"A genuinely changed row still invalidates and rematerializes." );
	}

	[TestMethod]
	public void LiveChatAuthorityEqualityIsStructuralOverTheCanonicalizedPermissionList()
	{
		var connection = new ConnectionId( DeterministicGuid( 1, 0x74 ) );
		var character = new CharacterId( DeterministicGuid( 1, 0x75 ) );
		var left = new LiveChatAuthority(
			connection, new AccountId( 7 ), character, new FactionId( "citizen" ), new[] { "b", "a" } );
		var right = new LiveChatAuthority(
			connection, new AccountId( 7 ), character, new FactionId( "citizen" ), new[] { "a", "b" } );
		var different = new LiveChatAuthority(
			connection, new AccountId( 7 ), character, new FactionId( "citizen" ), new[] { "a" } );

		Assert.AreEqual( left, right, "The constructor canonicalizes ordering; equality is structural." );
		Assert.AreEqual( left.GetHashCode(), right.GetHashCode() );
		Assert.AreNotEqual( left, different );
	}

	[TestMethod]
	public void OneConnectionDeltaTouchesOneOfSixtyFourRowsAndMatchesFreshRebuild()
	{
		var positions = new HL2RPIncrementalChatConnectionDirectory();
		var authorities = new HL2RPIncrementalChatAuthorityDirectory();
		var combat = new HL2RPIncrementalCombatPlayerTargetDirectory();
		var projection = new HL2RPProjectionIndex();
		var positionRows = new List<LiveChatConnection>();
		var authorityRows = new List<LiveChatAuthority>();
		var combatRows = new List<CombatPlayerTarget>();
		for ( var index = 0; index < 64; index++ )
		{
			var connection = new ConnectionId( DeterministicGuid( index + 1, 0x61 ) );
			var character = new CharacterId( DeterministicGuid( index + 1, 0x62 ) );
			var account = new AccountId( checked((ulong)index + 1UL) );
			positionRows.Add( new LiveChatConnection( connection, character, new ChatPosition( index, 0, 0 ) ) );
			authorityRows.Add( new LiveChatAuthority(
				connection, account, character, new FactionId( "citizen" ), new[] { "chat.local" } ) );
			combatRows.Add( Target( connection, account, character, index ) );
			projection.ObserveConnection( connection, account );
			projection.ObserveCharacterBindingChanged( connection, null, character );
		}
		positions.Rebuild( 1, positionRows );
		authorities.Rebuild( 1, authorityRows );
		combat.Rebuild( combatRows );

		var changedConnection = positionRows[17].ConnectionId;
		var changedCharacter = positionRows[17].CharacterId;
		var changedAccount = authorityRows[17].AccountId;
		var updatedPosition = new LiveChatConnection(
			changedConnection, changedCharacter, new ChatPosition( 999, 1, 2 ) );
		var updatedAuthority = new LiveChatAuthority(
			changedConnection, changedAccount, changedCharacter, new FactionId( "civil_protection" ),
			new[] { "chat.local", "chat.dispatch" } );
		var updatedTarget = Target( changedConnection, changedAccount, changedCharacter, 999 );
		Assert.AreEqual( 1, positions.Apply( 2, changedConnection, updatedPosition ) );
		Assert.AreEqual( 1, authorities.Apply( 2, changedConnection, updatedAuthority ) );
		Assert.AreEqual( 1, combat.Apply( changedConnection, updatedTarget ) );
		Assert.AreEqual( 64, positions.Count );
		Assert.AreEqual( 64, authorities.Count );
		Assert.AreEqual( 64, combat.Count );

		var expectedPositions = positionRows.Select( value =>
			value.ConnectionId == changedConnection ? updatedPosition : value ).ToArray();
		var expectedAuthorities = authorityRows.Select( value =>
			value.ConnectionId == changedConnection ? updatedAuthority : value ).ToArray();
		var expectedCombat = combatRows.Select( value =>
			value.Actor.ConnectionId == changedConnection ? updatedTarget : value ).ToArray();
		var rebuiltPositions = new HL2RPIncrementalChatConnectionDirectory();
		var rebuiltAuthorities = new HL2RPIncrementalChatAuthorityDirectory();
		var rebuiltCombat = new HL2RPIncrementalCombatPlayerTargetDirectory();
		rebuiltPositions.Rebuild( 2, expectedPositions );
		rebuiltAuthorities.Rebuild( 2, expectedAuthorities );
		rebuiltCombat.Rebuild( expectedCombat );
		Assert.AreEqual( 2L, positions.Capture().Version );
		Assert.AreEqual( 2L, authorities.Capture().Version );
		CollectionAssert.AreEqual( PositionShape( rebuiltPositions.Capture() ), PositionShape( positions.Capture() ) );
		CollectionAssert.AreEqual( AuthorityShape( rebuiltAuthorities.Capture() ), AuthorityShape( authorities.Capture() ) );
		CollectionAssert.AreEqual( CombatShape( rebuiltCombat.Capture() ), CombatShape( combat.Capture() ) );
		CollectionAssert.AreEqual(
			PositionSnapshotBytes( rebuiltPositions.Capture() ), PositionSnapshotBytes( positions.Capture() ) );
		CollectionAssert.AreEqual(
			AuthoritySnapshotBytes( rebuiltAuthorities.Capture() ), AuthoritySnapshotBytes( authorities.Capture() ) );
		CollectionAssert.AreEqual(
			CombatSnapshotBytes( rebuiltCombat.Capture() ), CombatSnapshotBytes( combat.Capture() ) );

		var characterLookups = 0;
		var accountLookups = 0;
		var inventoryLookups = 0;
		var sceneLookups = 0;
		var recipients = HL2RPPresentationPlanner.ResolveRecipients(
			new HL2RPPresentationChangeSet
			{
				Characters = new[] { changedCharacter },
				Accounts = new[] { changedAccount },
				Inventories = new[] { InventoryIdAt( 77 ) },
				SceneEntities = new[] { new SceneEntityId( DeterministicGuid( 77, 0x63 ) ) }
			},
			character => { characterLookups++; return projection.ConnectionForCharacter( character ); },
			account => { accountLookups++; return projection.ConnectionsForAccount( account ); },
			_ => { inventoryLookups++; return Array.Empty<ConnectionId>(); },
			_ => { sceneLookups++; return Array.Empty<ConnectionId>(); } );
		Assert.AreEqual( changedConnection, recipients.Single() );
		Assert.AreEqual( 1, characterLookups );
		Assert.AreEqual( 1, accountLookups );
		Assert.AreEqual( 1, inventoryLookups );
		Assert.AreEqual( 1, sceneLookups );
	}

	private static HL2RPProjectionIndex Rebuild( FeatureTestEnvironment environment )
	{
		var index = new HL2RPProjectionIndex();
		index.Rebuild(
			environment.Repositories.Inventories.All(),
			environment.Repositories.Items.All(),
			environment.Repositories.Characters.All(),
			environment.Repositories.CharacterReferences.All(),
			environment.Repositories.SceneEntities.All() );
		return index;
	}

	private static IReadOnlyList<HL2RPLiveInventoryDelta> Deltas(
		HL2RPProjectionIndex index,
		HL2RPProjectionApplyResult applied ) => applied.AffectedLiveItems
		.Select( item => new HL2RPLiveInventoryDelta(
			item, index.TryCreateLiveInventoryRow( item, out var row ) ? row : null ) )
		.ToArray();

	private static DocumentSnapshot<InventoryRecord> InventoryDocument(
		InventoryId id,
		CharacterId owner,
		IReadOnlyList<InventoryPlacement> placements ) => InventoryDocument(
		id, InventoryOwner.Character( owner ), placements );

	private static DocumentSnapshot<InventoryRecord> InventoryDocument(
		InventoryId id,
		InventoryOwner owner,
		IReadOnlyList<InventoryPlacement> placements ) => new(
		DomainKeys.Inventory( id ),
		new DocumentRevision( 1 ),
		new InventoryRecord
		{
			Id = id,
			Owner = owner,
			Width = 1,
			Height = 1,
			Placements = placements
		} );

	private static DocumentSnapshot<ItemRecord> ItemDocument( ItemId id, string definition = "item.water" ) => new(
		DomainKeys.Item( id ),
		new DocumentRevision( 1 ),
		new ItemRecord { Id = id, Definition = new DefinitionId( definition ) } );

	private static InventoryId InventoryIdAt( int value ) => new( DeterministicGuid( value, 0x71 ) );
	private static ItemId ItemIdAt( int value ) => new( DeterministicGuid( value, 0x72 ) );
	private static Guid DeterministicGuid( int value, byte marker )
	{
		var bytes = new byte[16];
		BitConverter.GetBytes( value ).CopyTo( bytes, 0 );
		bytes[6] = 0x40;
		bytes[8] = 0x80;
		bytes[15] = marker;
		return new Guid( bytes );
	}

	private static CombatPlayerTarget Target(
		ConnectionId connection,
		AccountId account,
		CharacterId character,
		int position ) => new(
		character.Value.ToString( "D" ),
		new InventoryActor( connection, account, character ),
		new InventoryId( DeterministicGuid( position + 1, 0x73 ) ),
		new WorldTransformRecord
		{
			PositionX = position,
			PositionY = 0,
			PositionZ = 0,
			RotationX = 0,
			RotationY = 0,
			RotationZ = 0,
			RotationW = 1
		} );

	private static void AssertLiveRowsEqual(
		IReadOnlyList<LiveInventoryItemView> expected,
		IReadOnlyList<LiveInventoryItemView> actual ) => CollectionAssert.AreEqual(
		expected.OrderBy( value => value.ItemId.Value ).Select( LiveShape ).ToArray(),
		actual.OrderBy( value => value.ItemId.Value ).Select( LiveShape ).ToArray() );

	private static string LiveShape( LiveInventoryItemView value ) =>
		$"{value.ItemId.Value:D}|{value.OwningCharacterId?.Value.ToString( "D" )}|{value.Definition.Value}|" +
		string.Join( ",", value.Traits.OrderBy( pair => pair.Key, StringComparer.Ordinal )
			.Select( pair => $"{pair.Key}:{pair.Value.TypeId}:{pair.Value.TypeVersion}:{pair.Value.Data}" ) );

	private static LiveInventorySnapshot CaptureLive( long version, HL2RPProjectionIndex index )
	{
		var view = new HL2RPIncrementalLiveInventoryView();
		view.Rebuild( version, index.LiveInventoryRows() );
		return view.Capture();
	}

	private static byte[] LiveSnapshotBytes( LiveInventorySnapshot snapshot ) =>
		SnapshotBytes( "live", snapshot.Version.ToString( CultureInfo.InvariantCulture ),
			snapshot.Items.OrderBy( value => value.ItemId.Value ).Select( LiveShape ) );
	private static byte[] PositionSnapshotBytes( ChatConnectionSnapshot snapshot ) =>
		SnapshotBytes( "positions", snapshot.Version.ToString( CultureInfo.InvariantCulture ), PositionShape( snapshot ) );
	private static byte[] AuthoritySnapshotBytes( ChatAuthoritySnapshot snapshot ) =>
		SnapshotBytes( "authorities", snapshot.Version.ToString( CultureInfo.InvariantCulture ), AuthorityShape( snapshot ) );
	private static byte[] CombatSnapshotBytes( IReadOnlyList<CombatPlayerTarget> snapshot ) =>
		SnapshotBytes( "combat", "unversioned", CombatShape( snapshot ) );
	private static byte[] SnapshotBytes( string kind, string version, IEnumerable<string> rows ) =>
		Encoding.UTF8.GetBytes( string.Join( "\n", new[] { Atom( kind ), Atom( version ) }
			.Concat( rows.Select( Atom ) ) ) );
	private static string Atom( string value ) =>
		$"{Encoding.UTF8.GetByteCount( value ).ToString( CultureInfo.InvariantCulture )}:{value}";

	private static string[] PositionShape( ChatConnectionSnapshot snapshot ) => snapshot.Connections
		.Select( value => FormattableString.Invariant(
			$"{value.ConnectionId.Value:D}|{value.CharacterId.Value:D}|{value.Position.X:R}|{value.Position.Y:R}|{value.Position.Z:R}" ) )
		.ToArray();
	private static string[] AuthorityShape( ChatAuthoritySnapshot snapshot ) => snapshot.Authorities
		.Select( value => $"{value.ConnectionId.Value:D}|{value.AccountId.Value}|{value.CharacterId.Value:D}|" +
			$"{value.FactionId.Value}|{string.Join( ",", value.Permissions )}" )
		.ToArray();
	private static string[] CombatShape( IReadOnlyList<CombatPlayerTarget> targets ) => targets
		.Select( value => FormattableString.Invariant(
			$"{value.Token}|{value.Actor.ConnectionId.Value:D}|{value.Actor.AccountId.Value}|{value.Actor.CharacterId.Value:D}|{value.InventoryId.Value:D}|{value.DropTransform.PositionX:R}|{value.DropTransform.PositionY:R}|{value.DropTransform.PositionZ:R}|{value.DropTransform.RotationX:R}|{value.DropTransform.RotationY:R}|{value.DropTransform.RotationZ:R}|{value.DropTransform.RotationW:R}" ) )
		.ToArray();
}
