#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel.Definitions;
using Hexagon.V2.Networking;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;
using HL2RP.V2.Showcase.Combat;

namespace HL2RP.V2.Tests.Features;

[TestClass]
public sealed class ItemActionAvailabilityTests
{
	private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays( 10 );

	[TestMethod]
	public void CombatActionsMirrorMagazineReserveVestAndHealthPreconditions()
	{
		var pistol = Pistol( 4, equipped: true, raised: false );
		var noReserve = Context( pistol );
		AssertDisabled( Project( noReserve, HL2RPIds.Actions.Reload ), "No pistol ammunition" );

		var ammunition = Ammunition( 5 );
		var withReserve = Context( pistol, ammunition );
		Assert.IsTrue( Project( withReserve, HL2RPIds.Actions.Reload ).Enabled );
		var fullAmmunition = Ammunition( PistolItemState.MagazineCapacity );
		AssertDisabled( Project( Context( fullAmmunition ), HL2RPIds.Actions.Replenish ), "already replenished" );

		var destroyedVest = Vest( 0, equipped: false );
		AssertDisabled( Project( Context( destroyedVest ), HL2RPIds.Actions.Equip ), "Destroyed armor" );
		var vest = Vest( 80, equipped: false );
		Assert.IsTrue( Project( Context( vest ), HL2RPIds.Actions.Equip ).Enabled );
		AssertDisabled( Project( Context( vest ), HL2RPIds.Actions.Unequip ), "not equipped" );
		var equippedVest = Vest( 80, equipped: true );
		AssertDisabled( Project( Context( equippedVest ), HL2RPIds.Actions.Equip ), "already equipped" );
		Assert.IsTrue( Project( Context( equippedVest ), HL2RPIds.Actions.Unequip ).Enabled );

		var vial = Item( HL2RPIds.Items.HealthVial );
		AssertDisabled( Project( Context( vial ) with
		{
			Health = new CombatHealthSnapshot( CharacterId.New(), 100, 100 )
		}, HL2RPIds.Actions.Consume ), "living injured" );
		Assert.IsTrue( Project( Context( vial ) with
		{
			Health = new CombatHealthSnapshot( CharacterId.New(), 100, 50 )
		}, HL2RPIds.Actions.Consume ).Enabled );
		AssertDisabled( Project( Context( vial ) with
		{
			IsDead = true,
			Health = new CombatHealthSnapshot( CharacterId.New(), 100, 0 )
		}, HL2RPIds.Actions.Consume ), "deceased" );
	}

	[TestMethod]
	public void DocumentsRequestRationAndTokensMirrorDeterministicPreconditions()
	{
		var actor = Character();
		var permit = Permit( actor.Id, revoked: false, expiresAt: Now.AddDays( 1 ) );
		Assert.IsTrue( Project( Context( permit, character: actor ), HL2RPIds.Actions.PresentPermit ).Enabled );
		AssertDisabled( Project( Context( Permit( actor.Id, true, null ), character: actor ),
			HL2RPIds.Actions.PresentPermit ), "revoked" );
		AssertDisabled( Project( Context( Permit( actor.Id, false, Now ), character: actor ),
			HL2RPIds.Actions.PresentPermit ), "expired" );

		var coolingDevice = RequestDevice( Now );
		var cooling = Project( Context( coolingDevice ), HL2RPIds.Actions.Request );
		Assert.AreEqual( ItemActionInvocationKind.DedicatedPanel, cooling.Invocation );
		AssertDisabled( cooling, "cooling down" );
		Assert.IsTrue( Project( Context( RequestDevice( Now - RequestDeviceService.RequestCooldown ) ),
			HL2RPIds.Actions.Request ).Enabled );
		AssertDisabled( Project( Context( RequestDevice( null, powered: false ) ),
			HL2RPIds.Actions.Request ), "powered off" );

		var ration = Item( HL2RPIds.Items.Ration );
		AssertDisabled( Project( Context( ration, character: Character( long.MaxValue ) ),
			HL2RPIds.Actions.Open ), "overflow" );

		var primary = Token( long.MaxValue );
		var secondary = Token( 1 );
		AssertDisabled( Project( Context( primary, secondary ), HL2RPIds.Actions.Combine ), "overflow" );
		var compatible = Token( 2 );
		Assert.IsTrue( Project( Context( compatible, Token( 3 ) ), HL2RPIds.Actions.Combine ).Enabled );
		AssertDisabled( Project( Context( Token( 1 ) ), HL2RPIds.Actions.Split ), "cannot be split" );
	}

	[TestMethod]
	public void LockDropAndVendorAvailabilityMirrorHostFacts()
	{
		var kit = LockKit( 1 );
		var citizen = Context( kit );
		AssertDisabled( Project( citizen with
		{
			CombineLock = new CombineLockActionAvailability( true, null )
		}, HL2RPIds.Actions.Install ), "Only Civil Protection" );
		var cp = Character() with { Faction = new FactionId( HL2RPIds.Factions.CivilProtection ) };
		AssertDisabled( Project( Context( kit, character: cp ) with
		{
			CombineLock = new CombineLockActionAvailability( false, "Door already has a Combine lock." )
		}, HL2RPIds.Actions.Install ), "already has" );
		Assert.IsTrue( Project( Context( kit, character: cp ) with
		{
			CombineLock = new CombineLockActionAvailability( true, null )
		}, HL2RPIds.Actions.Install ).Enabled );

		var definition = new ItemDefinition(
			"droppable", Array.Empty<string>(), true, "models/dev/box.vmdl" );
		Assert.IsTrue( HL2RPItemDropAvailability.Project( definition, true, true, false ).CanDrop );
		Assert.IsFalse( HL2RPItemDropAvailability.Project( definition, false, true, false ).CanDrop );
		Assert.IsFalse( HL2RPItemDropAvailability.Project( definition, true, true, true ).CanDrop );
		Assert.IsFalse( HL2RPItemDropAvailability.Project(
			definition with { CanDrop = false, WorldModel = null }, true, false, false ).CanDrop );

		var sellItem = Item( HL2RPIds.Items.Water );
		var vendor = new VendorEntityState
		{
			RequiredPermit = BusinessPermitKind.Food,
			Stock = new[]
			{
				new VendorStockEntry { Definition = sellItem.Definition, Quantity = 4, UnitPrice = 15 }
			}
		};
		var available = HL2RPVendorSellAvailability.Project(
			Character(), sellItem, vendor, true, false, true, true );
		Assert.IsTrue( available.Enabled );
		Assert.AreEqual( 7L, available.Payout );
		Assert.IsFalse( HL2RPVendorSellAvailability.Project(
			Character(), sellItem, vendor, true, false, false, true ).Enabled );
		Assert.IsFalse( HL2RPVendorSellAvailability.Project(
			Character(), sellItem, vendor with
			{
				Stock = new[] { vendor.Stock[0] with { Quantity = int.MaxValue } }
			}, true, false, true, true ).Enabled );
	}

	[TestMethod]
	public void PerItemProjectionKeepsDuplicateItemStateBoundToItsIdentity()
	{
		var firstNote = Note( "First" );
		var secondNote = Note( "Second" );
		var firstRadio = Radio( "101.7", true );
		var secondRadio = Radio( "130.0", false );

		var firstNoteState = HL2RPInventoryItemState.Project( firstNote, CharacterId.New(), Now );
		var secondNoteState = HL2RPInventoryItemState.Project( secondNote, CharacterId.New(), Now );
		var firstRadioState = HL2RPInventoryItemState.Project( firstRadio, CharacterId.New(), Now );
		var secondRadioState = HL2RPInventoryItemState.Project( secondRadio, CharacterId.New(), Now );

		Assert.AreEqual( "First", firstNoteState[HL2RPInventoryItemFields.NoteBody].StringValue );
		Assert.AreEqual( "Second", secondNoteState[HL2RPInventoryItemFields.NoteBody].StringValue );
		Assert.AreEqual( "101.7", firstRadioState[HL2RPInventoryItemFields.RadioFrequency].StringValue );
		Assert.AreEqual( "130.0", secondRadioState[HL2RPInventoryItemFields.RadioFrequency].StringValue );
		Assert.IsTrue( firstRadioState[HL2RPInventoryItemFields.RadioPowered].BooleanValue );
		Assert.IsFalse( secondRadioState[HL2RPInventoryItemFields.RadioPowered].BooleanValue );
	}

	private static ItemActionSnapshot Project(
		HL2RPItemActionAvailabilityContext context,
		string action ) => HL2RPItemActionAvailability.Project(
		new ItemActionSnapshot( new ActionId( action ), action, true ), context );

	private static void AssertDisabled( ItemActionSnapshot snapshot, string reason )
	{
		Assert.IsFalse( snapshot.Enabled );
		StringAssert.Contains( snapshot.DisabledReason ?? string.Empty, reason );
	}

	private static HL2RPItemActionAvailabilityContext Context(
		ItemRecord item,
		ItemRecord? other = null,
		CharacterRecord? character = null )
	{
		character ??= Character();
		var items = other is null
			? new Dictionary<ItemId, ItemRecord> { [item.Id] = item }
			: new Dictionary<ItemId, ItemRecord> { [item.Id] = item, [other.Id] = other };
		return new HL2RPItemActionAvailabilityContext
		{
			Character = character,
			Inventory = new InventoryRecord
			{
				Id = InventoryId.New(),
				Owner = InventoryOwner.Character( character.Id ),
				Width = 4,
				Height = 4,
				Placements = items.Keys.Select( (id, index) => new InventoryPlacement( id, index, 0 ) ).ToArray()
			},
			Item = item,
			InventoryItems = items,
			NowUtc = Now,
			HasUseCapability = true,
			IsRestrained = false,
			IsDead = false
		};
	}

	private static CharacterRecord Character( long balance = 0 ) => new()
	{
		Id = CharacterId.New(),
		AccountId = new AccountId( 42 ),
		Slot = 0,
		Name = "Citizen",
		Description = "A sufficiently detailed availability test description.",
		Model = new DefinitionId( "model.citizen_01" ),
		Faction = new FactionId( HL2RPIds.Factions.Citizen ),
		Balance = balance,
		CreatedAt = DateTimeOffset.UnixEpoch,
		LastPlayedAt = DateTimeOffset.UnixEpoch,
		SchemaState = HL2RPPersistence.Payload( HL2RPPersistence.CharacterState, new HL2RPCharacterState
		{
			CitizenId = "C17-42-00",
			Age = 28,
			Pronouns = "they/them",
			Origin = "city_17",
			Whitelists = HL2RPWhitelist.None,
			CivicRecord = new CivicRecordState { Points = 0, Priority = CivicPriorityStatus.None }
		} )
	};

	private static ItemRecord Item( string definition, string? trait = null, TypedPayload? payload = null ) => new()
	{
		Id = ItemId.New(),
		Definition = new DefinitionId( definition ),
		Traits = trait is null || payload is null
			? new Dictionary<string, TypedPayload>()
			: new Dictionary<string, TypedPayload> { [trait] = payload }
	};

	private static ItemRecord Pistol( int rounds, bool equipped, bool raised ) => Item(
		HL2RPIds.Items.Pistol, "pistol", HL2RPPersistence.Payload( HL2RPPersistence.Pistol,
			new PistolItemState { MagazineRounds = rounds, Equipped = equipped, Raised = raised, LastFiredAtUtc = null } ) );

	private static ItemRecord Ammunition( int rounds ) => Item(
		HL2RPIds.Items.PistolAmmunition, "ammunition", HL2RPPersistence.Payload(
			HL2RPPersistence.PistolAmmunition, new PistolAmmunitionItemState { Rounds = rounds } ) );

	private static ItemRecord Vest( int durability, bool equipped ) => Item(
		HL2RPIds.Items.ProtectiveVest, "vest", HL2RPPersistence.Payload(
			HL2RPPersistence.ProtectiveVest, new ProtectiveVestItemState
			{
				Durability = durability, DamageReductionPermille = 300, Equipped = equipped
			} ) );

	private static ItemRecord Permit( CharacterId owner, bool revoked, DateTimeOffset? expiresAt ) => Item(
		HL2RPIds.Items.BusinessPermit, "permit", HL2RPPersistence.Payload(
			HL2RPPersistence.BusinessPermit, new BusinessPermitItemState
			{
				Kind = BusinessPermitKind.Food, OwnerCharacterId = owner, IssuedAtUtc = Now.AddDays( -1 ),
				ExpiresAtUtc = expiresAt, Revoked = revoked
			} ) );

	private static ItemRecord RequestDevice( DateTimeOffset? last, bool powered = true ) => Item(
		HL2RPIds.Items.RequestDevice, "request_device", HL2RPPersistence.Payload(
			HL2RPPersistence.RequestDevice, new RequestDeviceItemState { Powered = powered, LastRequestAtUtc = last } ) );

	private static ItemRecord Token( long amount ) => Item(
		HL2RPIds.Items.TokenStack, "tokens", HL2RPPersistence.Payload(
			HL2RPPersistence.TokenStack, new TokenStackItemState { Amount = amount } ) );

	private static ItemRecord LockKit( int remaining ) => Item(
		HL2RPIds.Items.CombineLockKit, "lock_kit", HL2RPPersistence.Payload(
			HL2RPPersistence.CombineLockKit, new CombineLockKitItemState { RemainingInstallations = remaining } ) );

	private static ItemRecord Note( string body ) => Item(
		HL2RPIds.Items.Note, "note", HL2RPPersistence.Payload( HL2RPPersistence.Note,
			new NoteItemState { Text = body, OwnerCharacterId = null, UpdatedAtUtc = Now } ) );

	private static ItemRecord Radio( string frequency, bool powered ) => Item(
		HL2RPIds.Items.Radio, "radio", HL2RPPersistence.Payload( HL2RPPersistence.Radio,
			new RadioItemState { Frequency = frequency, Powered = powered } ) );
}
