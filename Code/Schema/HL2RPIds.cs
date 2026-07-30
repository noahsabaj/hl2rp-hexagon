#nullable enable

namespace HL2RP.V2.Schema;

/// <summary>
/// Stable v2 identifiers shared by schema registration, host composition and tests.
/// These values are persistence and protocol surface, not presentation text.
/// </summary>
public static class HL2RPIds
{
	public const string Schema = "hl2rp";

	public static class Modules
	{
		public const string CivicIdentity = "civic_identity";
		public const string Combine = "combine";
		public const string Communications = "communications";
		public const string Commerce = "commerce";
		public const string Documents = "documents";
		public const string Restraint = "restraint";
		public const string Scanner = "scanner";
		public const string Combat = "combat";
	}

	/// <summary>
	/// Audible radii in world units. Referenced by the channel registrations that own them, so a
	/// range is stated once and read from the compiled schema everywhere else.
	/// </summary>
	public static class ChatRanges
	{
		public const float Local = 280f;
		public const float Whisper = 80f;
		public const float Yell = 550f;
	}

	public static class Factions
	{
		public const string Citizen = "citizen";
		public const string CivilProtection = "cca";
		public const string Overwatch = "ota";
		public const string CityAdministration = "cityadmin";
	}

	public static class Classes
	{
		public const string Recruit = "cca_rct";
		public const string Unit = "cca_unit";
		public const string Elite = "cca_epu";
		public const string Scanner = "cca_scanner";
	}

	public static class CreationFields
	{
		public const string Age = "age";
		public const string Pronouns = "pronouns";
		public const string Origin = "origin";
	}

	public static class Models
	{
		public const string Citizen01 = "model.citizen_01";
		public const string Citizen02 = "model.citizen_02";
		public const string Citizen03 = "model.citizen_03";
		public const string CivilProtectionUnit = "model.cca_unit";
		public const string OverwatchSoldier = "model.ota_soldier";
		public const string CityAdministrator = "model.city_administrator";
	}

	public static class Actions
	{
		public const string Show = "show";
		public const string Open = "open";
		public const string Consume = "consume";
		public const string Toggle = "toggle";
		public const string Tune = "tune";
		public const string Request = "request";
		public const string Read = "read";
		public const string Write = "write";
		public const string OpenBag = "open_bag";
		public const string PresentPermit = "present_permit";
		public const string Restrain = "restrain";
		public const string Equip = "equip";
		public const string Unequip = "unequip";
		public const string Reload = "reload";
		public const string Fire = "fire";
		public const string Replenish = "replenish";
		public const string Install = "install";
		public const string Split = "split";
		public const string Combine = "combine";
	}

	public static class Items
	{
		public const string CitizenIdCard = "cid_card";
		public const string Ration = "ration";
		public const string Water = "water";
		public const string HealthVial = "health_vial";
		public const string Flashlight = "flashlight";
		public const string Radio = "radio";
		public const string RequestDevice = "request_device";
		public const string Note = "note";
		public const string CivicHandbook = "civic_handbook";
		public const string Suitcase = "suitcase";
		public const string BusinessPermit = "permit_business";
		public const string ZipTie = "zip_tie";
		public const string Pistol = "weapon_pistol";
		public const string PistolAmmunition = "ammo_pistol";
		public const string ProtectiveVest = "protective_vest";
		public const string CombineLockKit = "combine_lock_kit";
		public const string TokenStack = "tokens";
	}

	public static class Channels
	{
		public const string InCharacter = "ic";
		public const string OutOfCharacter = "ooc";
		public const string LocalOutOfCharacter = "looc";
		public const string Whisper = "whisper";
		public const string Yell = "yell";
		public const string Emote = "emote";
		public const string Radio = "radio";
		public const string Request = "request";
		public const string Dispatch = "dispatch";
	}

	public static class Permissions
	{
		public const string DispatchChat = "chat.dispatch";
		public const string CivicData = "command.civic_data";
		public const string CityObjectives = "command.city_objectives";
		public const string Priority = "command.priority";
		public const string AuditedAdministration = "command.admin_audit";
		/// <summary>
		/// Deliberately NOT <see cref="AuditedAdministration"/>. Reading an audit and ending a
		/// character's life are different powers, and granting the second through the permission
		/// that carries the first would make the capability set unable to express "may audit,
		/// may not kill".
		/// </summary>
		public const string AdministrationKill = "command.admin_kill";
		public const string ManageEntitlements = "accounts.entitlements.manage";
		public const string CivilProtection = "faction.cca";
		public const string Overwatch = "faction.ota";
		public const string CityAdministration = "faction.cityadmin";
		public const string ScannerPilot = "scanner.pilot";
		public const string Restraint = "restraint.use";
		public const string CommerceManagement = "commerce.manage";
	}

	public static class Commands
	{
		public const string CivicData = "civic_data";
		public const string CityObjectives = "city_objectives";
		public const string Priority = "priority";
		public const string RadioFrequency = "radio_frequency";
		public const string Introduce = "introduce";
		public const string DoorOwnership = "door.ownership";
		public const string AdministrationAudit = "admin_audit";
		public const string AdministrationKill = "admin_kill";
		public const string EntitlementQuery = "accounts.entitlements.query";
		public const string EntitlementGrant = "accounts.entitlements.grant";
		public const string EntitlementRevoke = "accounts.entitlements.revoke";
		public const string CommerceBuy = "commerce.buy";
		public const string CommerceSell = "commerce.sell";
		public const string PermitPurchase = "commerce.permit.purchase";
		public const string NoteWrite = "documents.note.write";
		public const string RestraintSet = "restraint.set";
		public const string ScannerIntent = "scanner.intent";
		public const string CombatRespawn = "combat.respawn";
	}

	public static class Panels
	{
		public const string CharacterSelection = "character_selection";
		public const string CharacterCreation = "character_creation";
		public const string Hud = "hud";
		public const string Chat = "chat";
		public const string Scoreboard = "scoreboard";
		public const string Notifications = "notifications";
		public const string ActionBar = "action_bar";
		public const string Death = "death";
		public const string Inventory = "inventory";
		public const string Storage = "storage";
		public const string Door = "door";
		public const string Vendor = "vendor";
		public const string Permit = "permit";
		public const string Search = "search";
		public const string CombineOverlay = "combine_overlay";
		public const string CivicData = "civic_data";
		public const string Objectives = "objectives";
		public const string NoteEditor = "note_editor";
		public const string RadioTuning = "radio_tuning";
		public const string RestraintStatus = "restraint_status";
		public const string ScannerOverlay = "scanner_overlay";
	}

	public static class PersistedTypes
	{
		public const string AccountEntitlement = "hl2rp.account-entitlement";
		public const string CharacterState = "hl2rp.character-state";
		public const string CitizenIdCard = "hl2rp.trait.cid-card";
		public const string Radio = "hl2rp.trait.radio";
		public const string Flashlight = "hl2rp.trait.flashlight";
		public const string RequestDevice = "hl2rp.trait.request-device";
		public const string Note = "hl2rp.trait.note";
		public const string BusinessPermit = "hl2rp.trait.business-permit";
		public const string Pistol = "hl2rp.trait.pistol";
		public const string PistolAmmunition = "hl2rp.trait.pistol-ammunition";
		public const string ProtectiveVest = "hl2rp.trait.protective-vest";
		public const string CombineLockKit = "hl2rp.trait.combine-lock-kit";
		public const string TokenStack = "hl2rp.trait.token-stack";
		public const string Recognition = "hl2rp.reference.recognition";
		public const string Restraint = "hl2rp.reference.restraint";
		public const string DoorOwnership = "hl2rp.reference.door-ownership";
		public const string DoorState = "hl2rp.state.door";
		public const string StorageState = "hl2rp.state.storage";
		public const string VendorState = "hl2rp.state.vendor";
		public const string MachineState = "hl2rp.state.machine";
		public const string ForcefieldState = "hl2rp.state.forcefield";
		public const string ScannerState = "hl2rp.state.scanner";
		public const string CombatTargetState = "hl2rp.state.combat-target";
		public const string CityState = "hl2rp.state.city";
	}

	public static class Configs
	{
		public const string CharacterInventoryWidth = "character_inventory_width";
		public const string CharacterInventoryHeight = "character_inventory_height";
		public const string InteractionIdleSeconds = "interaction_idle_seconds";
		public const string ChatRateCapacity = "chat_rate_capacity";
		public const string ChatRateWindowSeconds = "chat_rate_window_seconds";
		public const string CharacterNameUniqueness = "character_name_uniqueness";
	}

	public static class ReservationNamespaces
	{
		public const string CitizenId = "hl2rp.cid";
	}
}
