#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Hexagon.V2.Client;
using Hexagon.V2.Domain;
using Hexagon.V2.Networking;
using HL2RP.UI;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.UI;

[TestClass]
public sealed class HL2RPShowcaseProjectionTests
{
	[TestMethod]
	public void ScoreboardProjectsEveryValidRosterRowAndOmitsMalformedRows()
	{
		var localConnection = ConnectionId.New();
		var localCharacter = CharacterId.New();
		var remoteConnection = ConnectionId.New();
		var remoteCharacter = CharacterId.New();
		var observerConnection = ConnectionId.New();
		var malformedConnection = ConnectionId.New();
		var roster = new PlayerRosterSnapshot(7, new[]
		{
			RosterRow(localConnection, localCharacter, "LOCAL.01", "civil_protection", "unit", false, "Active", true),
			RosterRow(remoteConnection, remoteCharacter, "Citizen 40291", "citizen", string.Empty, true, "Deceased", false),
			RosterRow(observerConnection, null, "Joining Player", string.Empty, string.Empty, false, "Dossier selection", false),
			new PlayerRosterRowSnapshot(malformedConnection, remoteCharacter, Fields(
				(HL2RPPresentationFields.Roster.DisplayName, SnapshotValue.String("Malformed")),
				(HL2RPPresentationFields.Roster.FactionId, SnapshotValue.String("citizen")),
				(HL2RPPresentationFields.Roster.ClassId, SnapshotValue.String(string.Empty)),
				(HL2RPPresentationFields.Roster.IsDead, SnapshotValue.String("false")),
				(HL2RPPresentationFields.Roster.Status, SnapshotValue.String("Active")),
				(HL2RPPresentationFields.Roster.IsLocal, SnapshotValue.Boolean(false))))
		});
		var store = Store(localConnection, localCharacter, roster);

		var projected = Project(store);
		var scoreboard = projected.Scoreboard ?? throw new AssertFailedException("Scoreboard was not projected.");

		Assert.HasCount(3, scoreboard.Players);
		var local = scoreboard.Players.Single(row => row.ConnectionId == localConnection);
		Assert.AreEqual("LOCAL.01", local.DisplayName);
		Assert.AreEqual("civil_protection", local.Faction?.Value);
		Assert.AreEqual("unit", local.Class?.Value);
		Assert.IsTrue(local.IsLocal);
		var remote = scoreboard.Players.Single(row => row.ConnectionId == remoteConnection);
		Assert.IsTrue(remote.IsDead);
		Assert.IsNull(remote.Class);
		var observer = scoreboard.Players.Single(row => row.ConnectionId == observerConnection);
		Assert.IsNull(observer.CharacterId);
		Assert.IsNull(observer.Faction);
	}

	[TestMethod]
	public void RegisteredSchemaViewsProjectAllShowcasePanelsFromClosedScalars()
	{
		var localConnection = ConnectionId.New();
		var characterId = CharacterId.New();
		var vendorSession = InteractionSessionId.New();
		var doorSession = InteractionSessionId.New();
		var doorEntity = SceneEntityId.New();
		var scannerSession = InteractionSessionId.New();
		var infractionId = Guid.NewGuid();
		var issuedAt = DateTimeOffset.Parse("2026-07-12T13:00:00Z");
		var updatedAt = DateTimeOffset.Parse("2026-07-12T14:00:00Z");
		var photoAt = DateTimeOffset.Parse("2026-07-12T15:00:00Z");
		var views = new[]
		{
			new SchemaViewSnapshot(HL2RPIds.Panels.Door, 1, Fields(
				(HL2RPPresentationFields.Door.SessionId, SnapshotValue.String(doorSession.Value.ToString("D"))),
				(HL2RPPresentationFields.Door.SceneEntityId, SnapshotValue.String(doorEntity.Value.ToString("D"))),
				(HL2RPPresentationFields.Door.IsOpen, SnapshotValue.Boolean(false)),
				(HL2RPPresentationFields.Door.CombineLocked, SnapshotValue.Boolean(false)),
				(HL2RPPresentationFields.Door.OwnerStatus, SnapshotValue.Choice("unowned")),
				(HL2RPPresentationFields.Door.CanClaim, SnapshotValue.Boolean(true)),
				(HL2RPPresentationFields.Door.ClaimDisabledReason, SnapshotValue.String(string.Empty)),
				(HL2RPPresentationFields.Door.CanRelease, SnapshotValue.Boolean(false)),
				(HL2RPPresentationFields.Door.ReleaseDisabledReason, SnapshotValue.String("This door has no owner.")))),
			new SchemaViewSnapshot(HL2RPIds.Panels.Vendor, 1, Fields(
				(HL2RPPresentationFields.Vendor.SessionId, SnapshotValue.String(vendorSession.Value.ToString("D"))),
				(HL2RPPresentationFields.Vendor.Name, SnapshotValue.String("Civil Distribution")),
				(HL2RPPresentationFields.Vendor.Description, SnapshotValue.String("Approved goods.")),
				(HL2RPPresentationFields.Vendor.Balance, SnapshotValue.Integer(275))), new[]
			{
				Fields(
					(HL2RPPresentationFields.Vendor.DefinitionId, SnapshotValue.Choice(HL2RPIds.Items.Water)),
					(HL2RPPresentationFields.Vendor.DisplayName, SnapshotValue.String("Water")),
					(HL2RPPresentationFields.Vendor.ItemDescription, SnapshotValue.String("Sealed water.")),
					(HL2RPPresentationFields.Vendor.Price, SnapshotValue.Integer(15)),
					(HL2RPPresentationFields.Vendor.Stock, SnapshotValue.Integer(4)),
					(HL2RPPresentationFields.Vendor.CanBuy, SnapshotValue.Boolean(true)),
					(HL2RPPresentationFields.Vendor.DisabledReason, SnapshotValue.String(string.Empty)))
			}),
			new SchemaViewSnapshot(HL2RPIds.Panels.CivicData, 1, Fields(
				(HL2RPPresentationFields.CivicData.CharacterId, SnapshotValue.String(characterId.Value.ToString("D"))),
				(HL2RPPresentationFields.CivicData.DisplayName, SnapshotValue.String("Citizen 40291")),
				(HL2RPPresentationFields.CivicData.CitizenId, SnapshotValue.String("40291")),
				(HL2RPPresentationFields.CivicData.Points, SnapshotValue.Integer(8)),
				(HL2RPPresentationFields.CivicData.InfractionCount, SnapshotValue.Integer(1)),
				(HL2RPPresentationFields.CivicData.Priority, SnapshotValue.Integer((int)CivicPriority.Watch)),
				(HL2RPPresentationFields.CivicData.Record, SnapshotValue.String("Routine monitoring.")),
				(HL2RPPresentationFields.CivicData.CanEdit, SnapshotValue.Boolean(true))), new[]
			{
				Fields(
					(HL2RPPresentationFields.CivicData.InfractionId, SnapshotValue.String(infractionId.ToString("D"))),
					(HL2RPPresentationFields.CivicData.Summary, SnapshotValue.String("Civil disruption")),
					(HL2RPPresentationFields.CivicData.InfractionPoints, SnapshotValue.Integer(2)),
					(HL2RPPresentationFields.CivicData.IssuedAtUnixMilliseconds, SnapshotValue.Integer(issuedAt.ToUnixTimeMilliseconds())),
					(HL2RPPresentationFields.CivicData.IssuedBy, SnapshotValue.String("UNIT.04")))
			}),
			new SchemaViewSnapshot(HL2RPIds.Panels.Objectives, 1, Fields(
				(HL2RPPresentationFields.Objectives.CanEdit, SnapshotValue.Boolean(true))), new[]
			{
				Fields(
					(HL2RPPresentationFields.Objectives.ObjectiveId, SnapshotValue.Choice("objective.one")),
					(HL2RPPresentationFields.Objectives.Title, SnapshotValue.String("Report to plaza")),
					(HL2RPPresentationFields.Objectives.Detail, SnapshotValue.String("Await inspection.")),
					(HL2RPPresentationFields.Objectives.UpdatedAtUnixMilliseconds, SnapshotValue.Integer(updatedAt.ToUnixTimeMilliseconds())),
					(HL2RPPresentationFields.Objectives.Completed, SnapshotValue.Boolean(false)))
			}),
			new SchemaViewSnapshot(HL2RPIds.Panels.RestraintStatus, 1, Fields(
				(HL2RPPresentationFields.RestraintStatus.Restrained, SnapshotValue.Boolean(true)),
				(HL2RPPresentationFields.RestraintStatus.RemainingMilliseconds, SnapshotValue.Integer(-1)),
				(HL2RPPresentationFields.RestraintStatus.TargetCharacterId, SnapshotValue.String(string.Empty)),
				(HL2RPPresentationFields.RestraintStatus.CanSearch, SnapshotValue.Boolean(false)),
				(HL2RPPresentationFields.RestraintStatus.Status, SnapshotValue.String("Movement restricted.")))),
			new SchemaViewSnapshot(HL2RPIds.Panels.ScannerOverlay, 1, Fields(
				(HL2RPPresentationFields.ScannerOverlay.Piloting, SnapshotValue.Boolean(true)),
				(HL2RPPresentationFields.ScannerOverlay.UnitName, SnapshotValue.String("SCN-12")),
				(HL2RPPresentationFields.ScannerOverlay.Spotlight, SnapshotValue.Boolean(true)),
				(HL2RPPresentationFields.ScannerOverlay.PhotoReadyAtUnixMilliseconds, SnapshotValue.Integer(photoAt.ToUnixTimeMilliseconds())),
				(HL2RPPresentationFields.ScannerOverlay.SessionId, SnapshotValue.String(scannerSession.Value.ToString("D")))), new[]
			{
				Fields(
					(HL2RPPresentationFields.ScannerOverlay.ContactLabel, SnapshotValue.String("CITIZEN")),
					(HL2RPPresentationFields.ScannerOverlay.ContactDistance, SnapshotValue.Integer(64)),
					(HL2RPPresentationFields.ScannerOverlay.ContactPriority, SnapshotValue.Boolean(true)))
			})
		};
		var roster = new PlayerRosterSnapshot(1, new[]
		{
			RosterRow(localConnection, characterId, "Citizen 40291", "citizen", string.Empty, false, "Active", true)
		});
		var store = Store(localConnection, characterId, roster, views);

		var projected = Project(store);

		Assert.AreEqual(vendorSession, projected.Vendor?.SessionId);
		Assert.AreEqual(doorSession, projected.Door?.SessionId);
		Assert.AreEqual(doorEntity, projected.Door?.SceneEntityId);
		Assert.IsTrue(projected.Door?.CanClaim);
		Assert.IsFalse(projected.Door?.CanRelease);
		Assert.AreEqual(15L, projected.Vendor?.Offers[0].Price);
		Assert.AreEqual("40291", projected.CivicRecord?.CitizenId);
		Assert.AreEqual(infractionId, projected.CivicRecord?.Infractions[0].Id);
		Assert.AreEqual("objective.one", projected.Objectives?.Objectives[0].Id);
		Assert.AreEqual(updatedAt, projected.Objectives?.Objectives[0].UpdatedAtUtc);
		Assert.IsNull(projected.Restraint?.Remaining);
		Assert.AreEqual(scannerSession, projected.Scanner?.SessionId);
		Assert.AreEqual(photoAt, projected.Scanner?.NextPhotoAtUtc);
		Assert.AreEqual(64f, projected.Scanner?.Contacts[0].Distance);
	}

	[TestMethod]
	public void DoorControlsDisappearWhenTheHostRevokesItsSessionView()
	{
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		var roster = new PlayerRosterSnapshot(1, new[]
		{
			RosterRow(connection, character, "Citizen", "citizen", string.Empty, false, "Active", true)
		});
		var active = Store(connection, character, roster, new[]
		{
			new SchemaViewSnapshot(HL2RPIds.Panels.Door, 1, Fields(
				(HL2RPPresentationFields.Door.SessionId, SnapshotValue.String(Guid.NewGuid().ToString("D"))),
				(HL2RPPresentationFields.Door.SceneEntityId, SnapshotValue.String(Guid.NewGuid().ToString("D"))),
				(HL2RPPresentationFields.Door.IsOpen, SnapshotValue.Boolean(true)),
				(HL2RPPresentationFields.Door.CombineLocked, SnapshotValue.Boolean(false)),
				(HL2RPPresentationFields.Door.OwnerStatus, SnapshotValue.Choice("self")),
				(HL2RPPresentationFields.Door.CanClaim, SnapshotValue.Boolean(false)),
				(HL2RPPresentationFields.Door.ClaimDisabledReason, SnapshotValue.String("You already own this door.")),
				(HL2RPPresentationFields.Door.CanRelease, SnapshotValue.Boolean(true)),
				(HL2RPPresentationFields.Door.ReleaseDisabledReason, SnapshotValue.String(string.Empty))))
		});
		var revoked = Store(connection, character, roster);

		Assert.IsNotNull(Project(active).Door);
		Assert.IsNull(Project(revoked).Door);
	}

	[TestMethod]
	public void PresentMalformedSchemaViewsFailClosedWithoutPrivateStateFallback()
	{
		var localConnection = ConnectionId.New();
		var characterId = CharacterId.New();
		var views = new[]
		{
			new SchemaViewSnapshot(HL2RPIds.Panels.Vendor, 1, Fields(
				(HL2RPPresentationFields.Vendor.SessionId, SnapshotValue.String(Guid.NewGuid().ToString("D"))),
				(HL2RPPresentationFields.Vendor.Name, SnapshotValue.String("Vendor")),
				(HL2RPPresentationFields.Vendor.Description, SnapshotValue.String(string.Empty)),
				(HL2RPPresentationFields.Vendor.Balance, SnapshotValue.String("100")))),
			new SchemaViewSnapshot(HL2RPIds.Panels.CivicData, 1, Fields(
				(HL2RPPresentationFields.CivicData.CharacterId, SnapshotValue.String("not-a-guid")))),
			new SchemaViewSnapshot(HL2RPIds.Panels.Objectives, 1, Fields(
				(HL2RPPresentationFields.Objectives.CanEdit, SnapshotValue.Integer(1)))),
			new SchemaViewSnapshot(HL2RPIds.Panels.RestraintStatus, 1, Fields(
				(HL2RPPresentationFields.RestraintStatus.Restrained, SnapshotValue.Boolean(true)),
				(HL2RPPresentationFields.RestraintStatus.RemainingMilliseconds, SnapshotValue.String("1000")))),
			new SchemaViewSnapshot(HL2RPIds.Panels.ScannerOverlay, 1, Fields(
				(HL2RPPresentationFields.ScannerOverlay.Piloting, SnapshotValue.Boolean(true)),
				(HL2RPPresentationFields.ScannerOverlay.UnitName, SnapshotValue.String("SCN-12")),
				(HL2RPPresentationFields.ScannerOverlay.Spotlight, SnapshotValue.Integer(0)),
				(HL2RPPresentationFields.ScannerOverlay.PhotoReadyAtUnixMilliseconds, SnapshotValue.Integer(-1)),
				(HL2RPPresentationFields.ScannerOverlay.SessionId, SnapshotValue.String(Guid.NewGuid().ToString("D")))))
		};
		var roster = new PlayerRosterSnapshot(1, new[]
		{
			RosterRow(localConnection, characterId, "Citizen 40291", "citizen", string.Empty, false, "Active", true)
		});
		var privateValues = Fields(
			("interaction.session", SnapshotValue.String(Guid.NewGuid().ToString("D"))),
			("civic.cid", SnapshotValue.String("legacy")),
			("city.objective.title", SnapshotValue.String("Legacy objective")),
			("restraint.active", SnapshotValue.Boolean(true)),
			("scanner.piloting", SnapshotValue.Boolean(true)));
		var inventories = new[]
		{
			Inventory(InventoryViewKind.Main, "Inventory"),
			Inventory(InventoryViewKind.Vendor, "Legacy Vendor")
		};
		var store = Store(localConnection, characterId, roster, views, privateValues, inventories);

		var projected = Project(store);

		Assert.IsNull(projected.Vendor);
		Assert.IsNull(projected.CivicRecord);
		Assert.IsNull(projected.Objectives);
		Assert.IsNull(projected.Restraint);
		Assert.IsNull(projected.Scanner);
	}

	[TestMethod]
	public void ItemActionPresentationProjectsPrivateReceiptAndHonorsLocalDismissal()
	{
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		var roster = new PlayerRosterSnapshot(1, new[]
		{
			RosterRow(connection, character, "Citizen", "citizen", string.Empty, false, "Active", true)
		});
		var store = Store(connection, character, roster, privateValues: Fields(
			("item.presentation.sequence", SnapshotValue.Integer(42)),
			("item.presentation.kind", SnapshotValue.Choice("personal_note")),
			("item.presentation.title", SnapshotValue.String("Note")),
			("item.presentation.field.body", SnapshotValue.String("Host-authored note content")),
			("item.presentation.field.updated_at_unix_milliseconds", SnapshotValue.Integer(DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds())),
			("item.presentation.field.expires_at_unix_milliseconds", SnapshotValue.Integer(-1))));
		var nextStore = Store(connection, character, roster, privateValues: Fields(
			("item.presentation.sequence", SnapshotValue.Integer(43)),
			("item.presentation.kind", SnapshotValue.Choice("identity_document")),
			("item.presentation.title", SnapshotValue.String("Citizen Identification")),
			("item.presentation.field.citizen_id", SnapshotValue.String("12345"))));

		var visible = Project(store);
		var dismissed = new HL2RPShowcaseProjection().Build(store, ShowcaseWorkspace.None, false,
			ImmutableArray<NotificationViewModel>.Empty, 42);
		var nextVisible = new HL2RPShowcaseProjection().Build(nextStore, ShowcaseWorkspace.None, false,
			ImmutableArray<NotificationViewModel>.Empty, 42);

		Assert.AreEqual(42L, visible.ItemPresentation?.Sequence);
		Assert.AreEqual("Host-authored note content",
			visible.ItemPresentation?.Fields.Single(field => field.Id == "body").Value);
		StringAssert.Contains(visible.ItemPresentation?.Fields.Single(
			field => field.Id == "updated_at_unix_milliseconds").Value, "UTC");
		Assert.AreEqual("No expiry", visible.ItemPresentation?.Fields.Single(
			field => field.Id == "expires_at_unix_milliseconds").Value);
		Assert.IsNull(dismissed.ItemPresentation);
		Assert.AreEqual(43L, nextVisible.ItemPresentation?.Sequence,
			"A later empty-plan receipt must remain visible after the prior receipt was dismissed.");
	}

	[TestMethod]
	public void CivicPermissionDistinctSessionsPermitRadioAndDirectiveRemainTruthful()
	{
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		var storageSession = InteractionSessionId.New();
		var searchSession = InteractionSessionId.New();
		var roster = new PlayerRosterSnapshot(1, new[]
		{
			RosterRow(connection, character, "UNIT.01", "civil_protection", "unit", false, "Active", true)
		});
		var store = Store(connection, character, roster,
			privateValues: Fields(
				("interaction.storage_session", SnapshotValue.String(storageSession.Value.ToString("D"))),
				("interaction.search_session", SnapshotValue.String(searchSession.Value.ToString("D")))),
			inventories: new[]
			{
				Inventory(InventoryViewKind.Main, "Inventory"),
				Inventory(InventoryViewKind.Storage, "Storage"),
				Inventory(InventoryViewKind.Search, "Search")
			},
			permissions: new[] { HL2RPIds.Permissions.CivicData });

		var projected = Project(store);
		var objectives = new ObjectivesViewModel(ImmutableArray.Create(
			new CityObjectiveViewModel("objective.done", "Completed", string.Empty, DateTimeOffset.UnixEpoch, true),
			new CityObjectiveViewModel("objective.active", "Report to plaza", string.Empty, DateTimeOffset.UnixEpoch, false)), false);

		Assert.IsTrue(projected.Scoreboard?.CanInspectCivicData);
		Assert.AreEqual(storageSession, projected.Storage?.SessionId);
		Assert.AreEqual(searchSession, projected.Search?.SessionId);
		Assert.AreNotEqual(projected.Storage?.SessionId, projected.Search?.SessionId);
		Assert.AreEqual("permit_general", projected.Permits?.Permits[0].PermitKindId);
		Assert.AreEqual("Civil Protection band", HL2RPShowcaseProjection.RadioChannelName("117.3"));
		Assert.AreEqual("Dispatch command band", HL2RPShowcaseProjection.RadioChannelName("130.0"));
		Assert.AreEqual("Custom frequency", HL2RPShowcaseProjection.RadioChannelName("144.8"));
		Assert.AreEqual("Report to plaza", HL2RPShowcaseProjection.ActiveDirective(objectives));
		Assert.AreEqual("No active city directive.", HL2RPShowcaseProjection.ActiveDirective(
			new ObjectivesViewModel(ImmutableArray<CityObjectiveViewModel>.Empty, false)));
	}

	[TestMethod]
	public void SearchInventoryWithoutItsHostSessionNeverProducesABlankTerminalWorkspace()
	{
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		var roster = new PlayerRosterSnapshot(1, new[]
		{
			RosterRow(connection, character, "UNIT.01", "civil_protection", "unit", false, "Active", true)
		});
		var store = Store(connection, character, roster, inventories: new[]
		{
			Inventory(InventoryViewKind.Main, "Inventory"),
			Inventory(InventoryViewKind.Search, "Unbound search inventory")
		});

		var projected = new HL2RPShowcaseProjection().Build(
			store, ShowcaseWorkspace.Search, false, ImmutableArray<NotificationViewModel>.Empty);

		Assert.IsNull(projected.Search);
		Assert.AreEqual(ShowcaseWorkspace.None, projected.ActiveWorkspace);
	}

	[TestMethod]
	public void DuplicateInventoryTypesKeepSelectedStateAtomicAndHudUsesExplicitAggregates()
	{
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		var firstNote = Item( HL2RPIds.Items.Note, 0, Fields(
			(HL2RPInventoryItemFields.NoteBody, SnapshotValue.String("First note")),
			(HL2RPInventoryItemFields.NoteOwnerCharacterId, SnapshotValue.String(character.Value.ToString("D")))) );
		var secondNote = Item( HL2RPIds.Items.Note, 1, Fields(
			(HL2RPInventoryItemFields.NoteBody, SnapshotValue.String("Second note")),
			(HL2RPInventoryItemFields.NoteOwnerCharacterId, SnapshotValue.String(string.Empty))) );
		var firstRadio = Item( HL2RPIds.Items.Radio, 2, Fields(
			(HL2RPInventoryItemFields.RadioFrequency, SnapshotValue.String("101.7")),
			(HL2RPInventoryItemFields.RadioPowered, SnapshotValue.Boolean(false))) );
		var secondRadio = Item( HL2RPIds.Items.Radio, 3, Fields(
			(HL2RPInventoryItemFields.RadioFrequency, SnapshotValue.String("130.0")),
			(HL2RPInventoryItemFields.RadioPowered, SnapshotValue.Boolean(true))) );
		var firstPistol = Item( HL2RPIds.Items.Pistol, 4, Fields(
			(HL2RPInventoryItemFields.PistolMagazineRounds, SnapshotValue.Integer(3)),
			(HL2RPInventoryItemFields.PistolEquipped, SnapshotValue.Boolean(false))) );
		var secondPistol = Item( HL2RPIds.Items.Pistol, 5, Fields(
			(HL2RPInventoryItemFields.PistolMagazineRounds, SnapshotValue.Integer(9)),
			(HL2RPInventoryItemFields.PistolEquipped, SnapshotValue.Boolean(true))) );
		var firstFlashlight = Item( HL2RPIds.Items.Flashlight, 6, Fields(
			(HL2RPInventoryItemFields.FlashlightPowered, SnapshotValue.Boolean(false))) );
		var secondFlashlight = Item( HL2RPIds.Items.Flashlight, 7, Fields(
			(HL2RPInventoryItemFields.FlashlightPowered, SnapshotValue.Boolean(true))) );
		var foodPermit = Item( HL2RPIds.Items.BusinessPermit, 8, Fields(
			(HL2RPInventoryItemFields.PermitKind, SnapshotValue.Choice("food")),
			(HL2RPInventoryItemFields.PermitValid, SnapshotValue.Boolean(true))) );
		var inventory = new InventorySnapshot( InventoryId.New(), 1, InventoryViewKind.Main, "Inventory", 12, 1,
			new[] { secondFlashlight, firstNote, secondNote, firstRadio, secondRadio, firstPistol, secondPistol, firstFlashlight, foodPermit } );
		var roster = new PlayerRosterSnapshot(1, new[]
		{
			RosterRow(connection, character, "Citizen", "citizen", string.Empty, false, "Active", true)
		});
		var store = Store(connection, character, roster, inventories: new[] { inventory });
		var projected = Project( store );
		var selected = new HL2RPShowcaseProjection().Build(
			store, ShowcaseWorkspace.Inventory, false, ImmutableArray<NotificationViewModel>.Empty,
			selectedNoteItemId: secondNote.ItemId, selectedRadioItemId: secondRadio.ItemId );

		Assert.AreEqual(firstNote.ItemId, projected.Note?.ItemId);
		Assert.AreEqual("First note", projected.Note?.Body);
		Assert.AreEqual(firstRadio.ItemId, projected.Radio?.RadioItemId);
		Assert.AreEqual("101.7", projected.Radio?.Frequency);
		Assert.IsFalse(projected.Radio?.Enabled);
		Assert.AreEqual(secondNote.ItemId, selected.Note?.ItemId);
		Assert.AreEqual("Second note", selected.Note?.Body);
		Assert.AreEqual(secondRadio.ItemId, selected.Radio?.RadioItemId);
		Assert.AreEqual("130.0", selected.Radio?.Frequency);
		Assert.IsTrue(selected.Radio?.Enabled);
		Assert.AreEqual(9, projected.Hud?.Magazine);
		Assert.IsTrue(projected.Hud?.FlashlightEnabled);
		Assert.IsTrue(projected.Hud?.RadioEnabled);
		Assert.IsTrue(projected.Permits?.Permits.Single(card => card.PermitKindId == "permit_food").Owned);
		Assert.IsFalse(projected.Permits?.Permits.Single(card => card.PermitKindId == "permit_general").Owned);
	}

	private static HL2RPShowcaseViewModel Project(HexClientStore store) =>
		new HL2RPShowcaseProjection().Build(store, ShowcaseWorkspace.None, false,
			ImmutableArray<NotificationViewModel>.Empty);

	private static HexClientStore Store(ConnectionId connectionId, CharacterId characterId,
		PlayerRosterSnapshot roster, IEnumerable<SchemaViewSnapshot>? views = null,
		IReadOnlyDictionary<string, SnapshotValue>? privateValues = null,
		IEnumerable<InventorySnapshot>? inventories = null,
		IReadOnlyList<string>? permissions = null)
	{
		var store = new HexClientStore();
		var nonce = ClientSessionNonce.New();
		var connectionEpoch = ConnectionEpoch.New();
		var scope = new ClientSessionScope( nonce, connectionEpoch );
		store.PrepareSession( nonce );
		Assert.IsTrue( store.AcceptHello( new ClientSessionHello( scope ) ) );
		var snapshot = new ClientStateSnapshot(
			new ClientStateEpoch(connectionEpoch, 1, 1),
			new PlayerPublicSnapshot(connectionId, 7656119, "Local Player", characterId, "Citizen 40291",
				"Observable description", new DefinitionId(HL2RPIds.Models.Citizen01),
				new FactionId(HL2RPIds.Factions.Citizen), null, false, false),
			new PlayerPrivateSnapshot(characterId, 200, null, privateValues, permissions),
			roster,
			views,
			inventories,
			null);
		Assert.IsTrue(store.ApplyState(scope, snapshot));
		return store;
	}

	private static PlayerRosterRowSnapshot RosterRow(ConnectionId connectionId, CharacterId? characterId,
		string displayName, string faction, string characterClass, bool dead, string status, bool local) =>
		new(connectionId, characterId, Fields(
			(HL2RPPresentationFields.Roster.DisplayName, SnapshotValue.String(displayName)),
			(HL2RPPresentationFields.Roster.FactionId, SnapshotValue.String(faction)),
			(HL2RPPresentationFields.Roster.ClassId, SnapshotValue.String(characterClass)),
			(HL2RPPresentationFields.Roster.IsDead, SnapshotValue.Boolean(dead)),
			(HL2RPPresentationFields.Roster.Status, SnapshotValue.String(status)),
			(HL2RPPresentationFields.Roster.IsLocal, SnapshotValue.Boolean(local))));

	private static InventorySnapshot Inventory(InventoryViewKind kind, string title) =>
		new(InventoryId.New(), 1, kind, title, 4, 4, Array.Empty<InventoryItemSnapshot>());

	private static InventoryItemSnapshot Item(
		string definition,
		int x,
		IReadOnlyDictionary<string, SnapshotValue> state ) => new(
		ItemId.New(), new DefinitionId(definition), definition, string.Empty, "Test",
		x, 0, 1, 1, 1, state: state );

	private static IReadOnlyDictionary<string, SnapshotValue> Fields(
		params (string Key, SnapshotValue Value)[] values)
	{
		var fields = new Dictionary<string, SnapshotValue>(StringComparer.Ordinal);
		foreach (var (key, value) in values) fields.Add(key, value);
		return fields;
	}
}
