#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Hexagon.V2.Client;
using Hexagon.V2.Domain;
using Hexagon.V2.Networking;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;

namespace HL2RP.UI;

/// <summary>
/// Pure client projection. It copies host-authored snapshots into immutable UI
/// models and never reaches into schema aggregates, persistence, RPC state, or managers.
/// </summary>
public sealed class HL2RPShowcaseProjection
{
	public HL2RPShowcaseViewModel Build(
		HexClientStore store,
		ShowcaseWorkspace requestedWorkspace,
		bool scoreboardOpen,
		ImmutableArray<NotificationViewModel> notifications,
		long dismissedItemPresentationSequence = 0,
		ItemId? selectedNoteItemId = null,
		ItemId? selectedRadioItemId = null)
	{
		ArgumentNullException.ThrowIfNull(store);

		var publicPlayer = store.PublicPlayer;
		var privatePlayer = store.PrivatePlayer;
		var mainInventory = store.Inventories.FirstOrDefault(inventory => inventory.Kind == InventoryViewKind.Main);
		var storageInventory = store.Inventories.FirstOrDefault(inventory => inventory.Kind == InventoryViewKind.Storage);
		var vendorInventory = store.Inventories.FirstOrDefault(inventory => inventory.Kind == InventoryViewKind.Vendor);
		var searchInventory = store.Inventories.FirstOrDefault(inventory => inventory.Kind == InventoryViewKind.Search);
		var storageSessionId = ReadSession(privatePlayer, "interaction.storage_session");
		var searchSessionId = ReadSession(privatePlayer, "interaction.search_session");
		var vendorSessionId = ReadSession(privatePlayer, "interaction.vendor_session");
		var scannerSessionId = ReadSession(privatePlayer, "interaction.scanner_session");
		var equipment = BuildEquipment( mainInventory );
		var schemaViews = store.SchemaViews;
		var creationAvailability = TryGetSchemaView(
			schemaViews, HL2RPIds.Panels.CharacterCreation, out var creationView )
			? creationView
			: null;
		var entitlementAdministration = BuildEntitlementAdministration( creationAvailability );

		var characterMenu = publicPlayer?.HasCharacter == true
			? null
			: new CharacterMenuViewModel(
				store.CharacterList ?? new CharacterListSnapshot(0, Array.Empty<CharacterSummarySnapshot>()),
				CreateCharacterDefinition( creationAvailability ),
				entitlementAdministration,
				4,
				false,
				store.Lifecycle == ClientLifecycleState.Disconnected
					? "Waiting for a host connection."
					: "Choose a dossier or register a new identity.");

		var hud = publicPlayer?.HasCharacter == true
			? new HudViewModel(
				publicPlayer,
				privatePlayer,
				ReadInt(privatePlayer, "vitals.health", 100),
				ReadInt(privatePlayer, "vitals.health_max", 100),
				equipment.Armor,
				ReadInt(privatePlayer, "vitals.armor_max", 100),
				ReadString(privatePlayer, "location.name", "Unknown sector"),
				ReadString(privatePlayer, "location.district", "City 17"),
				equipment.Magazine,
				equipment.ReserveAmmunition,
				equipment.FlashlightEnabled,
				equipment.RadioEnabled)
			: null;

		var inventory = mainInventory is null
			? null
			: new InventoryWorkspaceViewModel(mainInventory, null, true, "Your inventory is empty.");
		var door = TryGetSchemaView( schemaViews, HL2RPIds.Panels.Door, out var doorView )
			? BuildDoor( doorView )
			: null;
		var storage = mainInventory is null || storageInventory is null ||
			storageSessionId is not InteractionSessionId storageSession
			? null
			: new InventoryTransferViewModel(mainInventory, storageInventory, storageSession,
				"Storage access is valid while range, line of sight, and policy remain valid.", true, true);
		var search = mainInventory is null || searchInventory is null ||
			searchSessionId is not InteractionSessionId searchSession
			? null
			: new InventoryTransferViewModel(mainInventory, searchInventory, searchSession,
				"Search access belongs to this restraint session and closes immediately when invalid.", true, false);
		var vendor = TryGetSchemaView(schemaViews, HL2RPIds.Panels.Vendor, out var vendorView)
			? BuildVendor(vendorView, mainInventory)
			: BuildVendor(vendorInventory, mainInventory, privatePlayer, vendorSessionId);
		var permits = publicPlayer?.HasCharacter == true ? BuildPermits(privatePlayer, mainInventory) : null;
		var civic = TryGetSchemaView(schemaViews, HL2RPIds.Panels.CivicData, out var civicView)
			? BuildCivic(civicView)
			: BuildCivic(publicPlayer, privatePlayer);
		var objectives = TryGetSchemaView(schemaViews, HL2RPIds.Panels.Objectives, out var objectivesView)
			? BuildObjectives(objectivesView)
			: BuildObjectives(privatePlayer);
		var note = BuildNote(mainInventory, publicPlayer?.CharacterId, selectedNoteItemId);
		var radio = BuildRadio(mainInventory, selectedRadioItemId);
		var restraint = TryGetSchemaView(schemaViews, HL2RPIds.Panels.RestraintStatus, out var restraintView)
			? BuildRestraint(restraintView)
			: BuildRestraint(privatePlayer);
		var scanner = TryGetSchemaView(schemaViews, HL2RPIds.Panels.ScannerOverlay, out var scannerView)
			? BuildScanner(scannerView)
			: BuildScanner(privatePlayer, scannerSessionId);
		var combine = BuildCombine(publicPlayer, privatePlayer, objectives);
		var death = publicPlayer?.IsDead == true
			? new DeathViewModel(true, ReadString(privatePlayer, "death.cause", "Cause unavailable"),
				ReadUnixMilliseconds( privatePlayer, "death.respawn_available_at_ms" ),
				ReadBool(privatePlayer, "death.can_respawn"))
			: null;
		var scoreboard = BuildScoreboard(store.Roster, publicPlayer, privatePlayer);
		var itemPresentation = BuildItemPresentation(privatePlayer, dismissedItemPresentationSequence);

		var workspace = NormalizeWorkspace(requestedWorkspace, inventory, storage, vendor, permits, search,
			civic, objectives, note, radio, scanner, entitlementAdministration);

		return new HL2RPShowcaseViewModel(
			store.Lifecycle != ClientLifecycleState.Disconnected,
			characterMenu,
			hud,
			store.Chat,
			scoreboard,
			notifications.IsDefault ? ImmutableArray<NotificationViewModel>.Empty : notifications,
			store.ActiveAction,
			death,
			inventory,
			door,
			storage,
			vendor,
			permits,
			search,
			combine,
			civic,
			objectives,
			note,
			radio,
			restraint,
			scanner,
			itemPresentation,
			entitlementAdministration,
			workspace,
			scoreboardOpen);
	}

	private static DoorSessionViewModel? BuildDoor( SchemaViewSnapshot view )
	{
		if ( !TryReadGuid( view.Fields, HL2RPPresentationFields.Door.SessionId, out var sessionId ) ||
			!TryReadGuid( view.Fields, HL2RPPresentationFields.Door.SceneEntityId, out var sceneEntityId ) ||
			!TryReadBoolean( view.Fields, HL2RPPresentationFields.Door.IsOpen, out var isOpen ) ||
			!TryReadBoolean( view.Fields, HL2RPPresentationFields.Door.CombineLocked, out var combineLocked ) ||
			!TryReadText( view.Fields, HL2RPPresentationFields.Door.OwnerStatus, false, out var ownerStatus ) ||
			!TryReadBoolean( view.Fields, HL2RPPresentationFields.Door.CanClaim, out var canClaim ) ||
			!TryReadString( view.Fields, HL2RPPresentationFields.Door.ClaimDisabledReason, true, out var claimReason ) ||
			!TryReadBoolean( view.Fields, HL2RPPresentationFields.Door.CanRelease, out var canRelease ) ||
			!TryReadString( view.Fields, HL2RPPresentationFields.Door.ReleaseDisabledReason, true, out var releaseReason ) )
			return null;
		return new DoorSessionViewModel(
			new InteractionSessionId( sessionId ),
			new SceneEntityId( sceneEntityId ),
			isOpen,
			combineLocked,
			ownerStatus,
			canClaim,
			claimReason,
			canRelease,
			releaseReason );
	}

	private static CharacterCreationViewModel CreateCharacterDefinition( SchemaViewSnapshot? availability )
	{
		var enabled = ParseCreationAvailability( availability );
		return new(
		ImmutableArray.Create(
			new ModelChoice(new DefinitionId(HL2RPIds.Models.Citizen01), new FactionId(HL2RPIds.Factions.Citizen), "Citizen 01", "Standard civilian presentation.", Enabled( enabled, "model", HL2RPIds.Models.Citizen01 )),
			new ModelChoice(new DefinitionId(HL2RPIds.Models.Citizen02), new FactionId(HL2RPIds.Factions.Citizen), "Citizen 02", "Alternate civilian presentation.", Enabled( enabled, "model", HL2RPIds.Models.Citizen02 )),
			new ModelChoice(new DefinitionId(HL2RPIds.Models.Citizen03), new FactionId(HL2RPIds.Factions.Citizen), "Citizen 03", "Alternate civilian presentation.", Enabled( enabled, "model", HL2RPIds.Models.Citizen03 )),
			new ModelChoice(new DefinitionId(HL2RPIds.Models.CivilProtectionUnit), new FactionId(HL2RPIds.Factions.CivilProtection), "Civil Protection", "Metropolitan protection uniform.", Enabled( enabled, "model", HL2RPIds.Models.CivilProtectionUnit )),
			new ModelChoice(new DefinitionId(HL2RPIds.Models.OverwatchSoldier), new FactionId(HL2RPIds.Factions.Overwatch), "Overwatch Soldier", "Transhuman Overwatch presentation.", Enabled( enabled, "model", HL2RPIds.Models.OverwatchSoldier )),
			new ModelChoice(new DefinitionId(HL2RPIds.Models.CityAdministrator), new FactionId(HL2RPIds.Factions.CityAdministration), "City Administrator", "Civil Administration presentation.", Enabled( enabled, "model", HL2RPIds.Models.CityAdministrator ))),
		ImmutableArray.Create(
			new FactionChoice(new FactionId(HL2RPIds.Factions.Citizen), "Citizen", "A resident of City 17.", Enabled( enabled, "faction", HL2RPIds.Factions.Citizen )),
			new FactionChoice(new FactionId(HL2RPIds.Factions.CivilProtection), "Civil Protection", "Metropolitan protection force; whitelist required.", Enabled( enabled, "faction", HL2RPIds.Factions.CivilProtection )),
			new FactionChoice(new FactionId(HL2RPIds.Factions.Overwatch), "Overwatch", "Transhuman military arm; whitelist required.", Enabled( enabled, "faction", HL2RPIds.Factions.Overwatch )),
			new FactionChoice(new FactionId(HL2RPIds.Factions.CityAdministration), "City Administration", "Appointed civil government; whitelist required.", Enabled( enabled, "faction", HL2RPIds.Factions.CityAdministration ))),
		ImmutableArray.Create(
			new ClassChoice(new ClassId(HL2RPIds.Classes.Recruit), new FactionId(HL2RPIds.Factions.CivilProtection), "Recruit", "Civil Protection induction rank.", Enabled( enabled, "class", HL2RPIds.Classes.Recruit )),
			new ClassChoice(new ClassId(HL2RPIds.Classes.Unit), new FactionId(HL2RPIds.Factions.CivilProtection), "Unit", "Standard Civil Protection unit.", Enabled( enabled, "class", HL2RPIds.Classes.Unit )),
			new ClassChoice(new ClassId(HL2RPIds.Classes.Elite), new FactionId(HL2RPIds.Factions.CivilProtection), "Elite", "Elite Civil Protection unit.", Enabled( enabled, "class", HL2RPIds.Classes.Elite )),
			new ClassChoice(new ClassId(HL2RPIds.Classes.Scanner), new FactionId(HL2RPIds.Factions.CivilProtection), "Scanner", "Scanner pilot assignment.", Enabled( enabled, "class", HL2RPIds.Classes.Scanner ))),
		ImmutableArray.Create(
			new CreationFieldViewModel(HL2RPIds.CreationFields.Age, "Age", "18–80; verified by host policy.", SnapshotValueKind.Integer, true, ImmutableArray<CreationFieldOption>.Empty),
			new CreationFieldViewModel(HL2RPIds.CreationFields.Pronouns, "Pronouns", "Printable presentation text.", SnapshotValueKind.String, true, ImmutableArray<CreationFieldOption>.Empty),
			new CreationFieldViewModel(HL2RPIds.CreationFields.Origin, "Origin", "Prior residence before assignment to City 17.", SnapshotValueKind.Choice, true,
				ImmutableArray.Create(
					new CreationFieldOption("city_17", "City 17", "Long-term City 17 resident."),
					new CreationFieldOption("relocated", "Relocated", "Recently transferred from another city."),
					new CreationFieldOption("outlands", "Outlands", "Processed from an outlying region.")))),
		48,
		512);
	}

	private static IReadOnlyDictionary<string, bool> ParseCreationAvailability( SchemaViewSnapshot? view )
	{
		var secureDefaults = new Dictionary<string, bool>( StringComparer.Ordinal )
		{
			["faction:" + HL2RPIds.Factions.Citizen] = true,
			["model:" + HL2RPIds.Models.Citizen01] = true,
			["model:" + HL2RPIds.Models.Citizen02] = true,
			["model:" + HL2RPIds.Models.Citizen03] = true
		};
		if ( view is null ) return secureDefaults;
		var parsed = new Dictionary<string, bool>( StringComparer.Ordinal );
		foreach ( var row in view.Rows )
		{
			if ( !TryReadText( row, HL2RPPresentationFields.CharacterCreation.AvailabilityKind, false, out var kind ) ||
				kind is not ("faction" or "model" or "class") ||
				!TryReadText( row, HL2RPPresentationFields.CharacterCreation.DefinitionId, false, out var definitionId ) ||
				!TryReadText( row, HL2RPPresentationFields.CharacterCreation.FactionId, false, out _ ) ||
				!TryReadBoolean( row, HL2RPPresentationFields.CharacterCreation.Enabled, out var enabled ) ||
				!parsed.TryAdd( kind + ":" + definitionId, enabled ) )
				return secureDefaults;
		}
		return parsed;
	}

	private static bool Enabled( IReadOnlyDictionary<string, bool> availability, string kind, string id ) =>
		availability.TryGetValue( kind + ":" + id, out var enabled ) && enabled;

	private static EntitlementAdministrationViewModel? BuildEntitlementAdministration( SchemaViewSnapshot? view )
	{
		if ( view is null ||
			!TryReadString( view.Fields, HL2RPPresentationFields.CharacterCreation.AccountId, false, out var accountText ) ||
			!ulong.TryParse( accountText, NumberStyles.None, CultureInfo.InvariantCulture, out var accountValue ) || accountValue == 0 ||
			!TryReadInteger( view.Fields, HL2RPPresentationFields.CharacterCreation.EntitlementFlags,
				0, (long)HL2RPAccountEntitlements.All, out var flagsValue ) ||
			!TryReadBoolean( view.Fields, HL2RPPresentationFields.CharacterCreation.CanManageEntitlements, out var canManage ) ||
			!TryReadString( view.Fields, HL2RPPresentationFields.CharacterCreation.QueriedAccountId, true, out var queriedText ) ||
			!TryReadInteger( view.Fields, HL2RPPresentationFields.CharacterCreation.QueriedFlags,
				0, (long)HL2RPAccountEntitlements.All, out var queriedFlagsValue ) ||
			!TryReadInteger( view.Fields, HL2RPPresentationFields.CharacterCreation.QueriedRevision,
				0, long.MaxValue, out var queriedRevision ) ) return null;
		var flags = (HL2RPWhitelist)flagsValue;
		var queriedFlags = (HL2RPWhitelist)queriedFlagsValue;
		if ( !HL2RPAccountEntitlements.IsValidSet( flags ) ||
			!HL2RPAccountEntitlements.IsValidSet( queriedFlags ) ) return null;
		AccountId? queriedAccountId = null;
		if ( !string.IsNullOrWhiteSpace( queriedText ) )
		{
			if ( !ulong.TryParse( queriedText, NumberStyles.None, CultureInfo.InvariantCulture, out var queriedValue ) ||
				queriedValue == 0 ) return null;
			queriedAccountId = new AccountId( queriedValue );
		}
		return new EntitlementAdministrationViewModel(
			new AccountId( accountValue ), flags, canManage,
			canManage ? queriedAccountId : null,
			canManage ? queriedFlags : HL2RPWhitelist.None,
			canManage ? queriedRevision : 0 );
	}

	private static VendorViewModel? BuildVendor(InventorySnapshot? vendorInventory, InventorySnapshot? mainInventory,
		PlayerPrivateSnapshot? state, InteractionSessionId? sessionId)
	{
		if (vendorInventory is null || sessionId is not InteractionSessionId session) return null;
		var offers = vendorInventory.Items.Select(item => new VendorOfferViewModel(
			item.DefinitionId,
			item.DisplayName,
			item.Description,
			ReadLong(state, $"vendor.price.{item.DefinitionId.Value}", 0),
			item.Quantity,
			true,
			string.Empty)).ToImmutableArray();
		return new VendorViewModel(session, vendorInventory.Title, "Session-bound stock and transactional delivery.",
			state?.Balance ?? 0, offers, mainInventory);
	}

	private static VendorViewModel? BuildVendor(SchemaViewSnapshot view, InventorySnapshot? mainInventory)
	{
		if (!TryReadGuid(view.Fields, HL2RPPresentationFields.Vendor.SessionId, out var sessionValue) ||
			!TryReadString(view.Fields, HL2RPPresentationFields.Vendor.Name, false, out var name) ||
			!TryReadString(view.Fields, HL2RPPresentationFields.Vendor.Description, true, out var description) ||
			!TryReadInteger(view.Fields, HL2RPPresentationFields.Vendor.Balance, 0, long.MaxValue, out var balance))
			return null;

		var offers = ImmutableArray.CreateBuilder<VendorOfferViewModel>(view.Rows.Count);
		foreach (var row in view.Rows)
		{
			if (!TryReadText(row, HL2RPPresentationFields.Vendor.DefinitionId, false, out var definitionValue) ||
				!TryDefinitionId(definitionValue, out var definitionId) ||
				!TryReadString(row, HL2RPPresentationFields.Vendor.DisplayName, false, out var displayName) ||
				!TryReadString(row, HL2RPPresentationFields.Vendor.ItemDescription, true, out var itemDescription) ||
				!TryReadInteger(row, HL2RPPresentationFields.Vendor.Price, 0, long.MaxValue, out var price) ||
				!TryReadInteger(row, HL2RPPresentationFields.Vendor.Stock, -1, long.MaxValue, out var stock) ||
				!TryReadBoolean(row, HL2RPPresentationFields.Vendor.CanBuy, out var canBuy) ||
				!TryReadString(row, HL2RPPresentationFields.Vendor.DisabledReason, true, out var disabledReason))
				return null;

			offers.Add(new VendorOfferViewModel(
				definitionId, displayName, itemDescription, price, stock, canBuy, disabledReason));
		}

		return new VendorViewModel(new InteractionSessionId(sessionValue), name, description, balance,
			offers.MoveToImmutable(), mainInventory);
	}

	private static PermitViewModel BuildPermits(PlayerPrivateSnapshot? state, InventorySnapshot? inventory)
	{
		var ownedKinds = (inventory?.Items ?? Array.Empty<InventoryItemSnapshot>())
			.Where(item => item.DefinitionId.Value == HL2RPIds.Items.BusinessPermit &&
				ReadItemBool(item, HL2RPInventoryItemFields.PermitValid))
			.Select(item => ReadItemString(item, HL2RPInventoryItemFields.PermitKind, string.Empty))
			.Where(kind => !string.IsNullOrWhiteSpace(kind))
			.ToHashSet(StringComparer.Ordinal);
		return new PermitViewModel(state?.Balance ?? 0, ImmutableArray.Create(
			PermitCard(ownedKinds, "general", "General Business Permit", "Authorizes general regulated commerce."),
			PermitCard(ownedKinds, "food", "Food Business Permit", "Authorizes regulated food distribution."),
			PermitCard(ownedKinds, "electronics", "Electronics Business Permit", "Authorizes regulated electronic goods."),
			PermitCard(ownedKinds, "literature", "Literature Business Permit", "Authorizes regulated publications.")));
	}

	private static PermitCardViewModel PermitCard(
		IReadOnlySet<string> ownedKinds,
		string kind,
		string name,
		string description)
	{
		var owned = ownedKinds.Contains(kind);
		return new PermitCardViewModel($"permit_{kind}", name, description, 250, owned, !owned,
			owned ? "Credential verified" : "Available through Civil Administration");
	}

	private static CivicRecordViewModel? BuildCivic(PlayerPublicSnapshot? player, PlayerPrivateSnapshot? state)
	{
		if (player?.CharacterId is not CharacterId characterId) return null;
		var priorityValue = Math.Clamp(ReadInt(state, "civic.priority", 0), 0, (int)CivicPriority.Malignant);
		return new CivicRecordViewModel(
			characterId,
			player.CharacterName,
			ReadString(state, "civic.cid", "PENDING"),
			ReadInt(state, "civic.points", 0),
			ReadInt(state, "civic.infraction_count", 0),
			(CivicPriority)priorityValue,
			ReadString(state, "civic.record", string.Empty),
			ImmutableArray<CivicInfractionViewModel>.Empty,
			state?.Permissions.Contains(HL2RPIds.Permissions.CivicData, StringComparer.Ordinal) == true);
	}

	private static CivicRecordViewModel? BuildCivic(SchemaViewSnapshot view)
	{
		if (!TryReadGuid(view.Fields, HL2RPPresentationFields.CivicData.CharacterId, out var characterValue) ||
			!TryReadString(view.Fields, HL2RPPresentationFields.CivicData.DisplayName, false, out var displayName) ||
			!TryReadString(view.Fields, HL2RPPresentationFields.CivicData.CitizenId, false, out var citizenId) ||
			!TryReadInt(view.Fields, HL2RPPresentationFields.CivicData.Points, int.MinValue, int.MaxValue, out var points) ||
			!TryReadInt(view.Fields, HL2RPPresentationFields.CivicData.InfractionCount, 0, int.MaxValue, out var infractionCount) ||
			!TryReadInt(view.Fields, HL2RPPresentationFields.CivicData.Priority,
				0, (int)CivicPriority.Malignant, out var priority) ||
			!TryReadString(view.Fields, HL2RPPresentationFields.CivicData.Record, true, out var record) ||
			!TryReadBoolean(view.Fields, HL2RPPresentationFields.CivicData.CanEdit, out var canEdit))
			return null;

		var infractions = ImmutableArray.CreateBuilder<CivicInfractionViewModel>(view.Rows.Count);
		foreach (var row in view.Rows)
		{
			if (!TryReadGuid(row, HL2RPPresentationFields.CivicData.InfractionId, out var infractionId) ||
				!TryReadString(row, HL2RPPresentationFields.CivicData.Summary, false, out var summary) ||
				!TryReadInt(row, HL2RPPresentationFields.CivicData.InfractionPoints,
					int.MinValue, int.MaxValue, out var infractionPoints) ||
				!TryReadDateTime(row, HL2RPPresentationFields.CivicData.IssuedAtUnixMilliseconds, out var issuedAt) ||
				!TryReadString(row, HL2RPPresentationFields.CivicData.IssuedBy, false, out var issuedBy))
				return null;

			infractions.Add(new CivicInfractionViewModel(
				infractionId, summary, infractionPoints, issuedAt, issuedBy));
		}

		return new CivicRecordViewModel(new CharacterId(characterValue), displayName, citizenId, points,
			infractionCount, (CivicPriority)priority, record, infractions.MoveToImmutable(), canEdit);
	}

	private static ObjectivesViewModel? BuildObjectives(PlayerPrivateSnapshot? state)
	{
		if (state is null) return null;
		var title = ReadString(state, "city.objective.title", string.Empty);
		var entries = string.IsNullOrWhiteSpace(title)
			? ImmutableArray<CityObjectiveViewModel>.Empty
			: ImmutableArray.Create(new CityObjectiveViewModel(
				"objective.current", title, ReadString(state, "city.objective.detail", string.Empty),
				DateTimeOffset.UnixEpoch, ReadBool(state, "city.objective.completed")));
		return new ObjectivesViewModel(entries,
			state.Permissions.Contains(HL2RPIds.Permissions.CityObjectives, StringComparer.Ordinal));
	}

	private static ObjectivesViewModel? BuildObjectives(SchemaViewSnapshot view)
	{
		if (!TryReadBoolean(view.Fields, HL2RPPresentationFields.Objectives.CanEdit, out var canEdit))
			return null;

		var objectives = ImmutableArray.CreateBuilder<CityObjectiveViewModel>(view.Rows.Count);
		foreach (var row in view.Rows)
		{
			if (!TryReadText(row, HL2RPPresentationFields.Objectives.ObjectiveId, false, out var objectiveId) ||
				!TryDefinitionId(objectiveId, out _) ||
				!TryReadString(row, HL2RPPresentationFields.Objectives.Title, false, out var title) ||
				!TryReadString(row, HL2RPPresentationFields.Objectives.Detail, true, out var detail) ||
				!TryReadDateTime(row, HL2RPPresentationFields.Objectives.UpdatedAtUnixMilliseconds, out var updatedAt) ||
				!TryReadBoolean(row, HL2RPPresentationFields.Objectives.Completed, out var completed))
				return null;

			objectives.Add(new CityObjectiveViewModel(
				objectiveId, title, detail, updatedAt, completed));
		}

		return new ObjectivesViewModel(objectives.MoveToImmutable(), canEdit);
	}

	private static NoteEditorViewModel? BuildNote(
		InventorySnapshot? inventory,
		CharacterId? characterId,
		ItemId? selectedItemId )
	{
		var notes = inventory?.Items.Where(candidate => candidate.DefinitionId.Value == HL2RPIds.Items.Note).ToArray()
			?? Array.Empty<InventoryItemSnapshot>();
		var item = selectedItemId is ItemId selected
			? notes.FirstOrDefault( candidate => candidate.ItemId == selected )
			: notes.FirstOrDefault();
		if ( item is null || !item.State.TryGetValue( HL2RPInventoryItemFields.NoteBody, out var body ) ||
			body.Kind != SnapshotValueKind.String ) return null;
		var owner = ReadItemString( item, HL2RPInventoryItemFields.NoteOwnerCharacterId, string.Empty );
		var canEdit = string.IsNullOrWhiteSpace( owner ) ||
			characterId is CharacterId active && string.Equals( owner, active.Value.ToString( "D" ), StringComparison.Ordinal );
		return new NoteEditorViewModel( item.ItemId, body.StringValue, 4096, canEdit );
	}

	private static ItemActionPresentationViewModel? BuildItemPresentation(
		PlayerPrivateSnapshot? state,
		long dismissedSequence)
	{
		if ( state is null || !state.Values.TryGetValue("item.presentation.sequence", out var sequenceValue) ||
			sequenceValue.Kind != SnapshotValueKind.Integer || sequenceValue.IntegerValue <= dismissedSequence ) return null;
		var kind = ReadString(state, "item.presentation.kind", string.Empty);
		var title = ReadString(state, "item.presentation.title", string.Empty);
		if ( string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(title) ) return null;
		const string prefix = "item.presentation.field.";
		var fields = state.Values
			.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
			.Select(pair => new ItemPresentationFieldViewModel(
				pair.Key[prefix.Length..],
				PresentationLabel(pair.Key[prefix.Length..]),
				PresentationValue(pair.Key[prefix.Length..], pair.Value),
				pair.Key[prefix.Length..] == "body"))
			.OrderBy(field => field.IsBody)
			.ThenBy(field => field.Label, StringComparer.Ordinal)
			.ToImmutableArray();
		return new ItemActionPresentationViewModel(sequenceValue.IntegerValue, kind, title, fields);
	}

	private static string PresentationLabel(string id) => string.Join(" ", id
		.Split('_', StringSplitOptions.RemoveEmptyEntries)
		.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

	private static string PresentationValue(string id, SnapshotValue value)
	{
		if ( (id.EndsWith("_at_unix_milliseconds", StringComparison.Ordinal) ||
			id.EndsWith("_at_ms", StringComparison.Ordinal)) && value.Kind == SnapshotValueKind.Integer )
		{
			if ( value.IntegerValue < 0 ) return "No expiry";
			try { return DateTimeOffset.FromUnixTimeMilliseconds(value.IntegerValue).ToUniversalTime()
				.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture); }
			catch ( ArgumentOutOfRangeException ) { return "Invalid timestamp"; }
		}
		return value.Kind switch
		{
			SnapshotValueKind.String or SnapshotValueKind.Choice => value.StringValue,
			SnapshotValueKind.Integer => value.IntegerValue.ToString(CultureInfo.InvariantCulture),
			SnapshotValueKind.Boolean => value.BooleanValue ? "Yes" : "No",
			_ => string.Empty
		};
	}

	private static RadioViewModel? BuildRadio( InventorySnapshot? inventory, ItemId? selectedItemId )
	{
		var radios = inventory?.Items.Where(candidate => candidate.DefinitionId.Value == HL2RPIds.Items.Radio).ToArray()
			?? Array.Empty<InventoryItemSnapshot>();
		var item = selectedItemId is ItemId selected
			? radios.FirstOrDefault( candidate => candidate.ItemId == selected )
			: radios.FirstOrDefault();
		var frequency = item is null
			? "101.7"
			: ReadItemString( item, HL2RPInventoryItemFields.RadioFrequency, "101.7" );
		return item is null ? null : new RadioViewModel(item.ItemId,
			frequency, RadioChannelName(frequency),
			ReadItemBool( item, HL2RPInventoryItemFields.RadioPowered ),
			ImmutableArray.Create(
				new UiChoice("101.7", "Civic", "Public civic band"),
				new UiChoice("117.3", "CCA", "Civil Protection band"),
				new UiChoice("130.0", "Dispatch", "Dispatch command band")));
	}

	internal static string RadioChannelName(string frequency) => frequency switch
	{
		"101.7" => "Civic band",
		"117.3" => "Civil Protection band",
		"130.0" => "Dispatch command band",
		_ => "Custom frequency"
	};

	private static EquipmentProjection BuildEquipment( InventorySnapshot? inventory )
	{
		var items = inventory?.Items ?? Array.Empty<InventoryItemSnapshot>();
		var equippedPistols = items.Where( item => item.DefinitionId.Value == HL2RPIds.Items.Pistol &&
			ReadItemBool( item, HL2RPInventoryItemFields.PistolEquipped ) ).ToArray();
		var magazine = equippedPistols.Length == 1 && TryReadItemInteger(
			equippedPistols[0], HL2RPInventoryItemFields.PistolMagazineRounds, 0, PistolItemState.MagazineCapacity, out var rounds )
			? (int)rounds
			: -1;

		long reserve = 0;
		foreach ( var ammunition in items.Where( item => item.DefinitionId.Value == HL2RPIds.Items.PistolAmmunition ) )
		{
			if ( !TryReadItemInteger( ammunition, HL2RPInventoryItemFields.AmmunitionRounds,
				0, PistolItemState.MagazineCapacity, out var count ) ) continue;
			reserve = reserve > int.MaxValue - count ? int.MaxValue : reserve + count;
		}

		var equippedVests = items.Where( item => item.DefinitionId.Value == HL2RPIds.Items.ProtectiveVest &&
			ReadItemBool( item, HL2RPInventoryItemFields.VestEquipped ) ).ToArray();
		var armor = equippedVests.Length == 1 && TryReadItemInteger(
			equippedVests[0], HL2RPInventoryItemFields.VestDurability, 0, int.MaxValue, out var durability )
			? (int)durability
			: 0;
		var flashlight = items.Any( item => item.DefinitionId.Value == HL2RPIds.Items.Flashlight &&
			ReadItemBool( item, HL2RPInventoryItemFields.FlashlightPowered ) );
		var radio = items.Any( item => item.DefinitionId.Value == HL2RPIds.Items.Radio &&
			ReadItemBool( item, HL2RPInventoryItemFields.RadioPowered ) );
		return new EquipmentProjection( magazine, (int)reserve, armor, flashlight, radio );
	}

	private static string ReadItemString( InventoryItemSnapshot item, string key, string fallback ) =>
		item.State.TryGetValue( key, out var value ) && value.Kind is SnapshotValueKind.String or SnapshotValueKind.Choice
			? value.StringValue
			: fallback;

	private static bool ReadItemBool( InventoryItemSnapshot item, string key ) =>
		item.State.TryGetValue( key, out var value ) && value.Kind == SnapshotValueKind.Boolean && value.BooleanValue;

	private static bool TryReadItemInteger( InventoryItemSnapshot item, string key,
		long minimum, long maximum, out long result )
	{
		result = 0;
		if ( !item.State.TryGetValue( key, out var value ) || value.Kind != SnapshotValueKind.Integer ||
			value.IntegerValue < minimum || value.IntegerValue > maximum ) return false;
		result = value.IntegerValue;
		return true;
	}

	private sealed record EquipmentProjection(
		int Magazine,
		int ReserveAmmunition,
		int Armor,
		bool FlashlightEnabled,
		bool RadioEnabled );

	private static RestraintViewModel? BuildRestraint(PlayerPrivateSnapshot? state)
	{
		if (state is null) return null;
		var restrained = ReadBool(state, "restraint.active");
		var target = ReadCharacterId(state, "restraint.target");
		if (!restrained && target is null) return null;
		return new RestraintViewModel(restrained, null, target,
			ReadBool(state, "restraint.can_search"), restrained ? "Inventory and world interaction are restricted." : "Maintain range and line of sight for three seconds.");
	}

	private static RestraintViewModel? BuildRestraint(SchemaViewSnapshot view)
	{
		if (!TryReadBoolean(view.Fields, HL2RPPresentationFields.RestraintStatus.Restrained, out var restrained) ||
			!TryReadInteger(view.Fields, HL2RPPresentationFields.RestraintStatus.RemainingMilliseconds,
				-1, long.MaxValue, out var remainingMilliseconds) ||
			!TryReadOptionalGuid(view.Fields, HL2RPPresentationFields.RestraintStatus.TargetCharacterId, out var targetValue) ||
			!TryReadBoolean(view.Fields, HL2RPPresentationFields.RestraintStatus.CanSearch, out var canSearch) ||
			!TryReadString(view.Fields, HL2RPPresentationFields.RestraintStatus.Status, false, out var status))
			return null;

		if (!TryDuration(remainingMilliseconds, out var remaining)) return null;
		return new RestraintViewModel(restrained, remaining,
			targetValue is Guid target ? new CharacterId(target) : null, canSearch, status);
	}

	private static ScannerViewModel? BuildScanner(PlayerPrivateSnapshot? state, InteractionSessionId? sessionId)
	{
		if (!ReadBool(state, "scanner.piloting")) return null;
		return new ScannerViewModel(true, ReadString(state, "scanner.unit", "SCN-00"),
			ReadBool(state, "scanner.spotlight"), null, sessionId, ImmutableArray<ScannerContactViewModel>.Empty);
	}

	private static ScannerViewModel? BuildScanner(SchemaViewSnapshot view)
	{
		if (!TryReadBoolean(view.Fields, HL2RPPresentationFields.ScannerOverlay.Piloting, out var piloting) ||
			!TryReadString(view.Fields, HL2RPPresentationFields.ScannerOverlay.UnitName, false, out var unitName) ||
			!TryReadBoolean(view.Fields, HL2RPPresentationFields.ScannerOverlay.Spotlight, out var spotlight) ||
			!TryReadNullableDateTime(view.Fields, HL2RPPresentationFields.ScannerOverlay.PhotoReadyAtUnixMilliseconds,
				out var photoReadyAt) ||
			!TryReadOptionalGuid(view.Fields, HL2RPPresentationFields.ScannerOverlay.SessionId, out var sessionValue))
			return null;

		if (!piloting) return null;
		if (sessionValue is not Guid session) return null;

		var contacts = ImmutableArray.CreateBuilder<ScannerContactViewModel>(view.Rows.Count);
		foreach (var row in view.Rows)
		{
			if (!TryReadString(row, HL2RPPresentationFields.ScannerOverlay.ContactLabel, false, out var label) ||
				!TryReadInteger(row, HL2RPPresentationFields.ScannerOverlay.ContactDistance,
					0, long.MaxValue, out var distance) ||
				!TryReadBoolean(row, HL2RPPresentationFields.ScannerOverlay.ContactPriority, out var priority))
				return null;

			contacts.Add(new ScannerContactViewModel(label, distance, priority));
		}

		return new ScannerViewModel(true, unitName, spotlight, photoReadyAt,
			new InteractionSessionId(session), contacts.MoveToImmutable());
	}

	private static CombineOverlayViewModel? BuildCombine(
		PlayerPublicSnapshot? player,
		PlayerPrivateSnapshot? state,
		ObjectivesViewModel? objectives)
	{
		if (player?.Faction is not FactionId faction || faction.Value == HL2RPIds.Factions.Citizen) return null;
		var directive = ActiveDirective(objectives);
		return new CombineOverlayViewModel(true,
			ReadString(state, "combine.rank", player.Class?.Value ?? "UNIT"),
			ReadString(state, "combine.division", faction.Value),
			ReadString(state, "combine.callsign", player.CharacterName),
			directive,
			ImmutableArray<CombineAlertViewModel>.Empty);
	}

	internal static string ActiveDirective(ObjectivesViewModel? objectives) =>
		objectives?.Objectives.FirstOrDefault(objective => !objective.Completed)?.Title
		?? "No active city directive.";

	private static ShowcaseWorkspace NormalizeWorkspace(ShowcaseWorkspace workspace,
		InventoryWorkspaceViewModel? inventory, InventoryTransferViewModel? storage, VendorViewModel? vendor,
		PermitViewModel? permits, InventoryTransferViewModel? search, CivicRecordViewModel? civic,
		ObjectivesViewModel? objectives, NoteEditorViewModel? note, RadioViewModel? radio, ScannerViewModel? scanner,
		EntitlementAdministrationViewModel? entitlements) => workspace switch
	{
		ShowcaseWorkspace.Inventory when inventory is not null => workspace,
		ShowcaseWorkspace.Storage when storage is not null => workspace,
		ShowcaseWorkspace.Vendor when vendor is not null => workspace,
		ShowcaseWorkspace.Permits when permits is not null => workspace,
		ShowcaseWorkspace.Search when search is not null => workspace,
		ShowcaseWorkspace.CivicData when civic is not null => workspace,
		ShowcaseWorkspace.Objectives when objectives is not null => workspace,
		ShowcaseWorkspace.NoteEditor when note is not null => workspace,
		ShowcaseWorkspace.Radio when radio is not null => workspace,
		ShowcaseWorkspace.Scanner when scanner is not null => workspace,
		ShowcaseWorkspace.Entitlements when entitlements?.CanManage == true => workspace,
		_ => ShowcaseWorkspace.None
	};

	private static ScoreboardViewModel? BuildScoreboard(PlayerRosterSnapshot? roster,
		PlayerPublicSnapshot? localPlayer, PlayerPrivateSnapshot? privatePlayer)
	{
		if (roster is null || localPlayer is null) return null;

		var players = ImmutableArray.CreateBuilder<ScoreboardEntryViewModel>(roster.Rows.Count);
		foreach (var row in roster.Rows)
		{
			if (TryBuildScoreboardEntry(row, localPlayer.ConnectionId, out var entry)) players.Add(entry);
		}

		return new ScoreboardViewModel(
			"City 17 Roleplay",
			"Hexagon v2 / HL2RP",
			ReadString(privatePlayer, "server.map", "main"),
			players.ToImmutable(),
			privatePlayer?.Permissions.Contains(
				HL2RPIds.Permissions.CivicData, StringComparer.Ordinal) == true);
	}

	private static bool TryBuildScoreboardEntry(PlayerRosterRowSnapshot row,
		ConnectionId localConnectionId, out ScoreboardEntryViewModel entry)
	{
		entry = null!;
		if (!TryReadString(row.Fields, HL2RPPresentationFields.Roster.DisplayName, false, out var displayName) ||
			!TryReadText(row.Fields, HL2RPPresentationFields.Roster.FactionId, true, out var factionValue) ||
			!TryReadText(row.Fields, HL2RPPresentationFields.Roster.ClassId, true, out var classValue) ||
			!TryReadBoolean(row.Fields, HL2RPPresentationFields.Roster.IsDead, out var isDead) ||
			!TryReadText(row.Fields, HL2RPPresentationFields.Roster.Status, false, out var status) ||
			!TryReadBoolean(row.Fields, HL2RPPresentationFields.Roster.IsLocal, out var isLocal))
			return false;

		var hasCharacter = row.CharacterId is not null;
		if (hasCharacter != !string.IsNullOrWhiteSpace(factionValue) ||
			(!hasCharacter && (!string.IsNullOrWhiteSpace(classValue) || isDead)) ||
			isLocal != (row.ConnectionId == localConnectionId))
			return false;

		FactionId? faction = null;
		if (hasCharacter && !TryFactionId(factionValue, out faction)) return false;
		ClassId? characterClass = null;
		if (!string.IsNullOrWhiteSpace(classValue) && !TryClassId(classValue, out characterClass)) return false;

		entry = new ScoreboardEntryViewModel(row.ConnectionId, row.CharacterId, displayName, faction,
			characterClass, isDead, status, isLocal);
		return true;
	}

	private static bool TryGetSchemaView(IReadOnlyDictionary<string, SchemaViewSnapshot>? views,
		string panelId, out SchemaViewSnapshot view)
	{
		view = null!;
		if (views is null || !views.TryGetValue(panelId, out var resolved) || resolved is null) return false;
		view = resolved;
		return true;
	}

	private static bool TryReadString(IReadOnlyDictionary<string, SnapshotValue> values,
		string key, bool allowEmpty, out string value)
	{
		value = string.Empty;
		if (!values.TryGetValue(key, out var snapshot) || snapshot.Kind != SnapshotValueKind.String)
			return false;
		value = snapshot.StringValue;
		return allowEmpty || !string.IsNullOrWhiteSpace(value);
	}

	private static bool TryReadText(IReadOnlyDictionary<string, SnapshotValue> values,
		string key, bool allowEmpty, out string value)
	{
		value = string.Empty;
		if (!values.TryGetValue(key, out var snapshot) ||
			snapshot.Kind is not (SnapshotValueKind.String or SnapshotValueKind.Choice))
			return false;
		value = snapshot.StringValue;
		return allowEmpty || !string.IsNullOrWhiteSpace(value);
	}

	private static bool TryReadBoolean(IReadOnlyDictionary<string, SnapshotValue> values,
		string key, out bool value)
	{
		value = false;
		if (!values.TryGetValue(key, out var snapshot) || snapshot.Kind != SnapshotValueKind.Boolean)
			return false;
		value = snapshot.BooleanValue;
		return true;
	}

	private static bool TryReadInteger(IReadOnlyDictionary<string, SnapshotValue> values,
		string key, long minimum, long maximum, out long value)
	{
		value = 0;
		if (!values.TryGetValue(key, out var snapshot) || snapshot.Kind != SnapshotValueKind.Integer ||
			snapshot.IntegerValue < minimum || snapshot.IntegerValue > maximum)
			return false;
		value = snapshot.IntegerValue;
		return true;
	}

	private static bool TryReadInt(IReadOnlyDictionary<string, SnapshotValue> values,
		string key, int minimum, int maximum, out int value)
	{
		value = 0;
		if (!TryReadInteger(values, key, minimum, maximum, out var integer)) return false;
		value = (int)integer;
		return true;
	}

	private static bool TryReadGuid(IReadOnlyDictionary<string, SnapshotValue> values,
		string key, out Guid value)
	{
		value = Guid.Empty;
		return TryReadString(values, key, false, out var text) && Guid.TryParse(text, out value) && value != Guid.Empty;
	}

	private static bool TryReadOptionalGuid(IReadOnlyDictionary<string, SnapshotValue> values,
		string key, out Guid? value)
	{
		value = null;
		if (!TryReadString(values, key, true, out var text)) return false;
		if (string.IsNullOrWhiteSpace(text)) return true;
		if (!Guid.TryParse(text, out var parsed) || parsed == Guid.Empty) return false;
		value = parsed;
		return true;
	}

	private static bool TryReadDateTime(IReadOnlyDictionary<string, SnapshotValue> values,
		string key, out DateTimeOffset value)
	{
		value = default;
		if (!TryReadInteger(values, key, long.MinValue, long.MaxValue, out var milliseconds)) return false;
		try
		{
			value = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
			return true;
		}
		catch (ArgumentOutOfRangeException)
		{
			return false;
		}
	}

	private static bool TryReadNullableDateTime(IReadOnlyDictionary<string, SnapshotValue> values,
		string key, out DateTimeOffset? value)
	{
		value = null;
		if (!TryReadInteger(values, key, -1, long.MaxValue, out var milliseconds)) return false;
		if (milliseconds == -1) return true;
		try
		{
			value = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
			return true;
		}
		catch (ArgumentOutOfRangeException)
		{
			return false;
		}
	}

	private static bool TryDuration(long milliseconds, out TimeSpan? duration)
	{
		duration = null;
		if (milliseconds == -1) return true;
		try
		{
			duration = TimeSpan.FromTicks(checked(milliseconds * TimeSpan.TicksPerMillisecond));
			return true;
		}
		catch (OverflowException)
		{
			return false;
		}
	}

	private static bool TryDefinitionId(string value, out DefinitionId definitionId)
	{
		try
		{
			definitionId = new DefinitionId(value);
			return true;
		}
		catch (ArgumentException)
		{
			definitionId = default;
			return false;
		}
	}

	private static bool TryFactionId(string value, out FactionId? factionId)
	{
		try
		{
			factionId = new FactionId(value);
			return true;
		}
		catch (ArgumentException)
		{
			factionId = null;
			return false;
		}
	}

	private static bool TryClassId(string value, out ClassId? classId)
	{
		try
		{
			classId = new ClassId(value);
			return true;
		}
		catch (ArgumentException)
		{
			classId = null;
			return false;
		}
	}

	private static string ReadString(PlayerPrivateSnapshot? state, string key, string fallback) =>
		state?.Values.TryGetValue(key, out var value) == true && value.Kind is SnapshotValueKind.String or SnapshotValueKind.Choice
			? value.StringValue : fallback;

	private static long ReadLong(PlayerPrivateSnapshot? state, string key, long fallback) =>
		state?.Values.TryGetValue(key, out var value) == true && value.Kind == SnapshotValueKind.Integer
			? value.IntegerValue : fallback;

	private static int ReadInt(PlayerPrivateSnapshot? state, string key, int fallback) =>
		(int)Math.Clamp(ReadLong(state, key, fallback), int.MinValue, int.MaxValue);

	private static DateTimeOffset? ReadUnixMilliseconds( PlayerPrivateSnapshot? state, string key )
	{
		var milliseconds = ReadLong( state, key, 0 );
		if ( milliseconds <= 0 ) return null;
		try { return DateTimeOffset.FromUnixTimeMilliseconds( milliseconds ); }
		catch ( ArgumentOutOfRangeException ) { return null; }
	}

	private static bool ReadBool(PlayerPrivateSnapshot? state, string key) =>
		state?.Values.TryGetValue(key, out var value) == true && value.Kind == SnapshotValueKind.Boolean && value.BooleanValue;

	private static InteractionSessionId? ReadSession(PlayerPrivateSnapshot? state, string key) =>
		Guid.TryParse(ReadString(state, key, string.Empty), out var id) && id != Guid.Empty ? new InteractionSessionId(id) : null;

	private static CharacterId? ReadCharacterId(PlayerPrivateSnapshot? state, string key) =>
		Guid.TryParse(ReadString(state, key, string.Empty), out var id) && id != Guid.Empty ? new CharacterId(id) : null;

}
