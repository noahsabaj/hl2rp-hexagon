#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Networking;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Features;

[TestClass]
public sealed class ItemActionPresentationTests
{
	[TestMethod]
	public void DocumentActionsAttachTypedClientSafeReceipts()
	{
		var actor = new InventoryActor( ConnectionId.New(), new AccountId( 42 ), CharacterId.New() );
		var issuedAt = DateTimeOffset.UnixEpoch.AddDays( 1 );
		var cid = Item(
			HL2RPIds.Items.CitizenIdCard,
			"cid",
			HL2RPPersistence.Payload(
				HL2RPPersistence.CitizenIdCard,
				new CitizenIdCardItemState
				{
					CitizenId = "C17-42-00",
					IssuedName = "Jordan Citizen",
					IssuedAtUtc = issuedAt,
					Priority = CivicPriorityStatus.Watch
				} ) );
		var note = Item(
			HL2RPIds.Items.Note,
			"note",
			HL2RPPersistence.Payload(
				HL2RPPersistence.Note,
				new NoteItemState
				{
					Text = "Meet at the ration terminal.",
					OwnerCharacterId = actor.CharacterId,
					UpdatedAtUtc = issuedAt.AddHours( 1 )
				} ) );
		var handbook = Item( HL2RPIds.Items.CivicHandbook );
		var permit = Item(
			HL2RPIds.Items.BusinessPermit,
			"permit",
			HL2RPPersistence.Payload(
				HL2RPPersistence.BusinessPermit,
				new BusinessPermitItemState
				{
					Kind = BusinessPermitKind.Food,
					OwnerCharacterId = actor.CharacterId,
					IssuedAtUtc = issuedAt,
					ExpiresAtUtc = issuedAt.AddDays( 30 ),
					Revoked = false
				} ) );

		var cidReceipt = Plan( actor, cid, HL2RPIds.Actions.Show );
		var noteReceipt = Plan( actor, note, HL2RPIds.Actions.Read );
		var handbookReceipt = Plan( actor, handbook, HL2RPIds.Actions.Read );
		var permitReceipt = Plan( actor, permit, HL2RPIds.Actions.PresentPermit );

		Assert.AreEqual( ItemActionPresentationKind.IdentityDocument, cidReceipt.Kind );
		Assert.AreEqual( "C17-42-00", cidReceipt.Fields[HL2RPItemActionPresentations.Fields.CitizenId].StringValue );
		Assert.AreEqual( "Jordan Citizen", cidReceipt.Fields[HL2RPItemActionPresentations.Fields.IssuedName].StringValue );
		Assert.AreEqual( "watch", cidReceipt.Fields[HL2RPItemActionPresentations.Fields.Priority].StringValue );

		Assert.AreEqual( ItemActionPresentationKind.PersonalNote, noteReceipt.Kind );
		Assert.AreEqual( "Meet at the ration terminal.", noteReceipt.Fields[HL2RPItemActionPresentations.Fields.Body].StringValue );
		Assert.AreEqual( actor.CharacterId.Value.ToString( "D" ),
			noteReceipt.Fields[HL2RPItemActionPresentations.Fields.OwnerCharacterId].StringValue );

		Assert.AreEqual( ItemActionPresentationKind.ReferenceDocument, handbookReceipt.Kind );
		Assert.AreEqual( HL2RPItemActionPresentations.HandbookText,
			handbookReceipt.Fields[HL2RPItemActionPresentations.Fields.Body].StringValue );

		Assert.AreEqual( ItemActionPresentationKind.PermitCredential, permitReceipt.Kind );
		Assert.AreEqual( "food", permitReceipt.Fields[HL2RPItemActionPresentations.Fields.PermitKind].StringValue );
		Assert.AreEqual( "valid", permitReceipt.Fields[HL2RPItemActionPresentations.Fields.Status].StringValue );
		Assert.AreEqual( issuedAt.AddDays( 30 ).ToUnixTimeMilliseconds(),
			permitReceipt.Fields[HL2RPItemActionPresentations.Fields.ExpiresAtUnixMilliseconds].IntegerValue );
	}

	[TestMethod]
	public void ReceiptCopiesFieldsAndRejectsUnboundedText()
	{
		var fields = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
		{
			[HL2RPItemActionPresentations.Fields.Body] = SnapshotValue.String( "Original" )
		};
		var receipt = new ItemActionPresentationReceipt(
			ItemActionPresentationKind.PersonalNote,
			"Note",
			fields );

		fields[HL2RPItemActionPresentations.Fields.Body] = SnapshotValue.String( "Changed" );

		Assert.AreEqual( "Original", receipt.Fields[HL2RPItemActionPresentations.Fields.Body].StringValue );
		Assert.Throws<ArgumentException>( () => new ItemActionPresentationReceipt(
			ItemActionPresentationKind.PersonalNote,
			"Note",
			new Dictionary<string, SnapshotValue>
			{
				[HL2RPItemActionPresentations.Fields.Body] = SnapshotValue.String(
					new string( 'x', ItemActionPresentationReceipt.MaximumTextLength + 1 ) )
			} ) );
	}

	private static ItemActionPresentationReceipt Plan(
		InventoryActor actor,
		ItemRecord item,
		string actionId )
	{
		var inventory = new InventoryRecord
		{
			Id = InventoryId.New(),
			Owner = InventoryOwner.Character( actor.CharacterId ),
			Width = 4,
			Height = 4,
			Placements = new[] { new InventoryPlacement( item.Id, 0, 0 ) }
		};
		var context = new ItemActionContext(
			actor,
			Character( actor ),
			inventory,
			item,
			new ActionId( actionId ),
			new Dictionary<ItemId, ItemRecord> { [item.Id] = item } );
		var handler = HL2RPNonCombatItemActions.Create( new FixedClock() )
			.Single( candidate => candidate.Id.Value == actionId );

		var result = handler.Plan( context );

		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		Assert.IsNotNull( result.Value.Presentation );
		return result.Value.Presentation;
	}

	private static CharacterRecord Character( InventoryActor actor ) => new()
	{
		Id = actor.CharacterId,
		AccountId = actor.AccountId,
		Slot = 0,
		Name = "Jordan Citizen",
		Description = "A sufficiently detailed item presentation test description.",
		Model = new DefinitionId( "model.citizen_01" ),
		Faction = new FactionId( HL2RPIds.Factions.Citizen ),
		Balance = 0,
		CreatedAt = DateTimeOffset.UnixEpoch,
		LastPlayedAt = DateTimeOffset.UnixEpoch,
		SchemaState = HL2RPPersistence.Payload(
			HL2RPPersistence.CharacterState,
			new HL2RPCharacterState
			{
				CitizenId = "C17-42-00",
				Age = 28,
				Pronouns = "they/them",
				Origin = "city_17",
				Whitelists = HL2RPWhitelist.None,
				CivicRecord = new CivicRecordState
				{
					Points = 0,
					Priority = CivicPriorityStatus.None
				}
			} )
	};

	private static ItemRecord Item(
		string definition,
		string? traitId = null,
		TypedPayload? payload = null ) => new()
	{
		Id = ItemId.New(),
		Definition = new DefinitionId( definition ),
		Traits = traitId is null || payload is null
			? new Dictionary<string, TypedPayload>()
			: new Dictionary<string, TypedPayload> { [traitId] = payload }
	};

	private sealed class FixedClock : IHexClock
	{
		public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddDays( 2 );
	}
}
