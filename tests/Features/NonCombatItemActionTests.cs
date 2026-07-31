#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Features;

[TestClass]
public sealed class NonCombatItemActionTests
{
	[TestMethod]
	public void RegistersOnlyActionsExecutedByTheAtomicGenericBoundary()
	{
		var handlers = HL2RPNonCombatItemActions.Create( new FixedClock() );

		CollectionAssert.AreEquivalent(
			new[]
			{
				HL2RPIds.Actions.Show,
				HL2RPIds.Actions.Open,
				HL2RPIds.Actions.Consume,
				HL2RPIds.Actions.Toggle,
				HL2RPIds.Actions.Read,
				HL2RPIds.Actions.PresentPermit
			},
			handlers.Select( handler => handler.Id.Value ).ToArray() );
		Assert.AreEqual( handlers.Count, handlers.Select( handler => handler.Id ).Distinct().Count() );
	}

	[TestMethod]
	public void RationOpenCreditsCheckedCurrencyAndDeletesOnlyTheProvenItem()
	{
		var item = Item( HL2RPIds.Items.Ration );
		var context = Context( item, balance: 20 );
		var handler = Handler( HL2RPIds.Actions.Open );

		var result = handler.Plan( context );

		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		Assert.AreEqual( 30L, result.Value.UpdatedCharacter!.Balance );
		Assert.Contains( item.Id, result.Value.DeletedItems );
		Assert.HasCount( 1, result.Value.DeletedItems );
		var overflow = handler.Plan( Context( item, long.MaxValue ) );
		Assert.AreEqual( ErrorCode.Conflict, overflow.Error!.Code );
	}

	[TestMethod]
	public void FlashlightToggleProducesTypedReplacementWithoutMutatingInput()
	{
		var originalState = new FlashlightItemState { Powered = false, ChargePermille = 500 };
		var item = Item(
			HL2RPIds.Items.Flashlight,
			"flashlight",
			HL2RPPersistence.Payload( HL2RPPersistence.Flashlight, originalState ) );

		var result = Handler( HL2RPIds.Actions.Toggle ).Plan( Context( item ) );

		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		var updated = result.Value.UpdatedItems[item.Id];
		var state = HL2RPPersistence.Flashlight.Deserialize(
			updated.Traits["flashlight"].Data,
			updated.Traits["flashlight"].TypeVersion );
		Assert.IsTrue( state.Powered );
		Assert.AreEqual( 500, state.ChargePermille );
		var original = HL2RPPersistence.Flashlight.Deserialize(
			item.Traits["flashlight"].Data,
			item.Traits["flashlight"].TypeVersion );
		Assert.IsFalse( original.Powered );
	}

	[TestMethod]
	public void ReadShowConsumeAndPresentValidateTheirTypedItemBoundary()
	{
		var cid = Item(
			HL2RPIds.Items.CitizenIdCard,
			"cid",
			HL2RPPersistence.Payload(
				HL2RPPersistence.CitizenIdCard,
				new CitizenIdCardItemState
				{
					CitizenId = "C17-42-00",
					IssuedName = "Citizen",
					IssuedAtUtc = DateTimeOffset.UnixEpoch,
					Priority = CivicPriorityStatus.None
				} ) );
		var note = Item(
			HL2RPIds.Items.Note,
			"note",
			HL2RPPersistence.Payload(
				HL2RPPersistence.Note,
				new NoteItemState { Text = "Text", OwnerCharacterId = null, UpdatedAtUtc = DateTimeOffset.UnixEpoch } ) );
		var water = Item( HL2RPIds.Items.Water );
		var health = Item( HL2RPIds.Items.HealthVial );
		var permitContext = ContextForPermit();

		Assert.IsTrue( Handler( HL2RPIds.Actions.Show ).Plan( Context( cid ) ).Succeeded );
		Assert.IsTrue( Handler( HL2RPIds.Actions.Read ).Plan( Context( note ) ).Succeeded );
		Assert.IsTrue( Handler( HL2RPIds.Actions.Consume ).Plan( Context( water ) ).Succeeded );
		Assert.AreEqual(
			ErrorCode.PolicyDenied,
			Handler( HL2RPIds.Actions.Consume ).Plan( Context( health ) ).Error!.Code );
		Assert.IsTrue( Handler( HL2RPIds.Actions.PresentPermit ).Plan( permitContext ).Succeeded );
	}

	[TestMethod]
	public void RoutedActionsAreNotExposedAsFailingGenericHandlers()
	{
		var handlers = HL2RPNonCombatItemActions.Create( new FixedClock() )
			.Select( handler => handler.Id.Value )
			.ToHashSet( StringComparer.Ordinal );
		var routed = new[]
		{
			HL2RPIds.Actions.Tune,
			HL2RPIds.Actions.Request,
			HL2RPIds.Actions.Write,
			HL2RPIds.Actions.OpenBag,
			HL2RPIds.Actions.Split,
			HL2RPIds.Actions.Combine,
			HL2RPIds.Actions.Install
		};

		foreach ( var action in routed ) Assert.DoesNotContain( action, handlers );
	}

	private static IItemActionHandler Handler( string action ) =>
		HL2RPNonCombatItemActions.Create( new FixedClock() ).Single( handler => handler.Id.Value == action );

	private static ItemActionContext Context( ItemRecord item, long balance = 0 )
	{
		var actor = new InventoryActor( ConnectionId.New(), new AccountId( 42 ), CharacterId.New() );
		var character = Character( actor, balance );
		var inventory = new InventoryRecord
		{
			Id = InventoryId.New(),
			Owner = InventoryOwner.Character( actor.CharacterId ),
			Width = 4,
			Height = 4,
			Placements = new[] { new InventoryPlacement( item.Id, 0, 0 ) }
		};
		return new ItemActionContext(
			actor,
			character,
			inventory,
			item,
			new ActionId( HL2RPIds.Actions.Show ),
			new Dictionary<ItemId, ItemRecord> { [item.Id] = item } );
	}

	private static ItemActionContext ContextForPermit()
	{
		var actor = new InventoryActor( ConnectionId.New(), new AccountId( 42 ), CharacterId.New() );
		var permit = Item(
			HL2RPIds.Items.BusinessPermit,
			"permit",
			HL2RPPersistence.Payload(
				HL2RPPersistence.BusinessPermit,
				new BusinessPermitItemState
				{
					Kind = BusinessPermitKind.General,
					OwnerCharacterId = actor.CharacterId,
					IssuedAtUtc = DateTimeOffset.UnixEpoch,
					ExpiresAtUtc = null,
					Revoked = false
				} ) );
		var inventory = new InventoryRecord
		{
			Id = InventoryId.New(),
			Owner = InventoryOwner.Character( actor.CharacterId ),
			Width = 4,
			Height = 4,
			Placements = new[] { new InventoryPlacement( permit.Id, 0, 0 ) }
		};
		return new ItemActionContext(
			actor,
			Character( actor, 0 ),
			inventory,
			permit,
			new ActionId( HL2RPIds.Actions.PresentPermit ),
			new Dictionary<ItemId, ItemRecord> { [permit.Id] = permit } );
	}

	private static CharacterRecord Character( InventoryActor actor, long balance ) => new()
	{
		Id = actor.CharacterId,
		AccountId = actor.AccountId,
		Slot = 0,
		Name = "Citizen",
		Description = "A sufficiently detailed item action test description.",
		Model = new DefinitionId( "model.citizen_01" ),
		Faction = new FactionId( HL2RPIds.Factions.Citizen ),
		Balance = balance,
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
		public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddDays( 1 );
	}
}
