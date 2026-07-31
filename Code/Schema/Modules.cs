#nullable enable

namespace HL2RP.V2.Schema;

public sealed class CivicIdentityModule : IHexModule
{
	public string Id => HL2RPIds.Modules.CivicIdentity;

	public void Configure( ModuleBuilder builder )
	{
		builder.RegisterCharacterField( new CharacterFieldDefinition(
			HL2RPIds.CreationFields.Age,
			CharacterFieldValueKind.Integer,
			ShowInCreation: true,
			Required: true ) );
		builder.RegisterCharacterField( new CharacterFieldDefinition(
			HL2RPIds.CreationFields.Pronouns,
			CharacterFieldValueKind.String,
			ShowInCreation: true,
			Required: true ) );
		builder.RegisterCharacterField( new CharacterFieldDefinition(
			HL2RPIds.CreationFields.Origin,
			CharacterFieldValueKind.Choice,
			ShowInCreation: true,
			Required: true ) );
		builder.RegisterFaction( new FactionDefinition(
			HL2RPIds.Factions.Citizen,
			IsDefault: true,
			DisplayName: "Citizen",
			Description: "Residents of City 17 living under Combine rule." ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Show ) );
		builder.RegisterItem( Item(
			HL2RPIds.Items.CitizenIdCard,
			"Citizen ID",
			"An identification card issued by the Civil Administration.",
			"Identification",
			false,
			null,
			1,
			1,
			HL2RPIds.Actions.Show ) );
		builder.RegisterPermission( new PermissionDefinition( HL2RPIds.Permissions.CivicData ) );
		builder.RegisterPermission( new PermissionDefinition( HL2RPIds.Permissions.AuditedAdministration ) );
		builder.RegisterPermission( new PermissionDefinition( HL2RPIds.Permissions.ManageEntitlements ) );
		builder.RegisterPermission( new PermissionDefinition( HL2RPIds.Permissions.AdministrationKill ) );
		builder.RegisterCommand( new CommandDefinition(
			HL2RPIds.Commands.CivicData,
			HL2RPIds.Permissions.CivicData,
			Cost: CommandCostClass.Cheap ) );
		builder.RegisterCommand( new CommandDefinition( HL2RPIds.Commands.Introduce, Cost: CommandCostClass.Standard ) );
		builder.RegisterCommand( new CommandDefinition( HL2RPIds.Commands.DoorOwnership, Cost: CommandCostClass.Standard ) );
		builder.RegisterCommand( new CommandDefinition(
			HL2RPIds.Commands.AdministrationAudit,
			HL2RPIds.Permissions.AuditedAdministration,
			Cost: CommandCostClass.Cheap ) );
		// Expensive like the entitlement mutations: a lethal administrative act belongs in the
		// same rate class as granting and revoking capability, not the cheap read class.
		builder.RegisterCommand( new CommandDefinition(
			HL2RPIds.Commands.AdministrationKill,
			HL2RPIds.Permissions.AdministrationKill,
			Cost: CommandCostClass.Expensive ) );
		builder.RegisterCommand( new CommandDefinition(
			HL2RPIds.Commands.EntitlementQuery,
			HL2RPIds.Permissions.ManageEntitlements,
			Cost: CommandCostClass.Cheap ) );
		builder.RegisterCommand( new CommandDefinition(
			HL2RPIds.Commands.EntitlementGrant,
			HL2RPIds.Permissions.ManageEntitlements,
			Cost: CommandCostClass.Expensive ) );
		builder.RegisterCommand( new CommandDefinition(
			HL2RPIds.Commands.EntitlementRevoke,
			HL2RPIds.Permissions.ManageEntitlements,
			Cost: CommandCostClass.Expensive ) );
		builder.RegisterInitializer( new CharacterInitializerDefinition(
			HL2RPCitizenIdInitializer.InitializerId,
			typeof(HL2RPCitizenIdInitializer),
			HL2RPCitizenIdInitializer.InitializerOrder ) );
		builder.RegisterInitializer( new CharacterInitializerDefinition(
			HL2RPLoadoutInitializer.InitializerId,
			typeof(HL2RPLoadoutInitializer),
			HL2RPLoadoutInitializer.InitializerOrder ) );

		builder.RegisterPanel( Panel<CharacterSelectionPanelDescriptor>( HL2RPIds.Panels.CharacterSelection ) );
		builder.RegisterPanel( Panel<CharacterCreationPanelDescriptor>( HL2RPIds.Panels.CharacterCreation ) );
		builder.RegisterPanel( Panel<HudPanelDescriptor>( HL2RPIds.Panels.Hud ) );
		builder.RegisterPanel( Panel<ScoreboardPanelDescriptor>( HL2RPIds.Panels.Scoreboard ) );
		builder.RegisterPanel( Panel<NotificationsPanelDescriptor>( HL2RPIds.Panels.Notifications ) );
		builder.RegisterPanel( Panel<CivicDataPanelDescriptor>( HL2RPIds.Panels.CivicData ) );
	}

	internal static ItemDefinition Item(
		string id,
		string name,
		string description,
		string category,
		bool canDrop,
		string? model,
		int width,
		int height,
		params string[] actions ) => new(
		id,
		actions,
		canDrop,
		model,
		name,
		description,
		category,
		width,
		height );

	internal static PanelDefinition Panel<TPanel>( string id ) where TPanel : class =>
		new( id, typeof(TPanel) );
}

public sealed class CombineModule : IHexModule
{
	public string Id => HL2RPIds.Modules.Combine;

	public void Configure( ModuleBuilder builder )
	{
		builder.DependsOn( HL2RPIds.Modules.CivicIdentity );
		builder.RegisterFaction( new FactionDefinition(
			HL2RPIds.Factions.CivilProtection,
			DefaultClassId: HL2RPIds.Classes.Recruit,
			DisplayName: "Civil Protection",
			Description: "The metropolitan protection force responsible for City 17." ) );
		builder.RegisterFaction( new FactionDefinition(
			HL2RPIds.Factions.Overwatch,
			DisplayName: "Overwatch",
			Description: "The Combine transhuman military arm." ) );
		builder.RegisterFaction( new FactionDefinition(
			HL2RPIds.Factions.CityAdministration,
			DisplayName: "City Administration",
			Description: "The appointed civil government of City 17." ) );
		builder.RegisterClass( new ClassDefinition(
			HL2RPIds.Classes.Recruit,
			HL2RPIds.Factions.CivilProtection,
			"Recruit",
			Capacity: 32 ) );
		builder.RegisterClass( new ClassDefinition(
			HL2RPIds.Classes.Unit,
			HL2RPIds.Factions.CivilProtection,
			"Unit",
			Capacity: 64 ) );
		builder.RegisterClass( new ClassDefinition(
			HL2RPIds.Classes.Elite,
			HL2RPIds.Factions.CivilProtection,
			"Elite",
			Capacity: 8 ) );
		builder.RegisterClass( new ClassDefinition(
			HL2RPIds.Classes.Scanner,
			HL2RPIds.Factions.CivilProtection,
			"Scanner",
			Capacity: 4 ) );

		builder.RegisterPermission( new PermissionDefinition( HL2RPIds.Permissions.CivilProtection ) );
		builder.RegisterPermission( new PermissionDefinition( HL2RPIds.Permissions.Overwatch ) );
		builder.RegisterPermission( new PermissionDefinition( HL2RPIds.Permissions.CityAdministration ) );
		builder.RegisterPermission( new PermissionDefinition( HL2RPIds.Permissions.CityObjectives ) );
		builder.RegisterPermission( new PermissionDefinition( HL2RPIds.Permissions.Priority ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Install ) );
		builder.RegisterItem( CivicIdentityModule.Item(
			HL2RPIds.Items.CombineLockKit,
			"Combine Lock Kit",
			"A serialized installation kit for a Combine door lock.",
			"Combine",
			false,
			null,
			1,
			1,
			HL2RPIds.Actions.Install ) );
		builder.RegisterCommand( new CommandDefinition(
			HL2RPIds.Commands.CityObjectives,
			HL2RPIds.Permissions.CityObjectives,
			Cost: CommandCostClass.Expensive ) );
		builder.RegisterCommand( new CommandDefinition(
			HL2RPIds.Commands.Priority,
			HL2RPIds.Permissions.Priority,
			Cost: CommandCostClass.Standard ) );
		builder.RegisterPanel( CivicIdentityModule.Panel<CombineOverlayPanelDescriptor>(
			HL2RPIds.Panels.CombineOverlay ) );
		builder.RegisterPanel( CivicIdentityModule.Panel<ObjectivesPanelDescriptor>(
			HL2RPIds.Panels.Objectives ) );
	}
}

public sealed class CommunicationsModule : IHexModule
{
	public string Id => HL2RPIds.Modules.Communications;

	public void Configure( ModuleBuilder builder )
	{
		builder.DependsOn( HL2RPIds.Modules.CivicIdentity );
		builder.RegisterPermission( new PermissionDefinition( HL2RPIds.Permissions.DispatchChat ) );
		// The single source of truth for every channel. Ranges used to live in a parallel table in
		// Features/LocalChatRecipientResolver.cs keyed by these same ids; that table is gone, and the
		// recipient rules are now derived from these registrations.
		builder.RegisterChatChannel( new ChatChannelDefinition(
			HL2RPIds.Channels.InCharacter, DisplayName: "IC", Prefixes: new[] { "ic", "say" },
			Range: HL2RPIds.ChatRanges.Local, Colour: "#dce2e3" ) );
		builder.RegisterChatChannel( new ChatChannelDefinition(
			HL2RPIds.Channels.OutOfCharacter, DisplayName: "OOC", Prefixes: new[] { "ooc" },
			Colour: "#8fb6c4", AllowedWhileDead: false ) );
		builder.RegisterChatChannel( new ChatChannelDefinition(
			HL2RPIds.Channels.LocalOutOfCharacter, DisplayName: "LOOC", Prefixes: new[] { "looc" },
			Range: HL2RPIds.ChatRanges.Local, Colour: "#7f949c" ) );
		builder.RegisterChatChannel( new ChatChannelDefinition(
			HL2RPIds.Channels.Whisper, DisplayName: "W", Prefixes: new[] { "w", "whisper" },
			Range: HL2RPIds.ChatRanges.Whisper, Colour: "#9aa7ad" ) );
		builder.RegisterChatChannel( new ChatChannelDefinition(
			HL2RPIds.Channels.Yell, DisplayName: "Y", Prefixes: new[] { "y", "yell" },
			Range: HL2RPIds.ChatRanges.Yell, Colour: "#e4d3b0" ) );
		builder.RegisterChatChannel( new ChatChannelDefinition(
			HL2RPIds.Channels.Emote, DisplayName: "ME", Prefixes: new[] { "me", "emote" },
			Range: HL2RPIds.ChatRanges.Local, Colour: "#c2a4d4" ) );
		builder.RegisterChatChannel( new ChatChannelDefinition(
			HL2RPIds.Channels.Radio, DisplayName: "R", Prefixes: new[] { "r", "radio" },
			Colour: "#75c99a" ) );
		builder.RegisterChatChannel( new ChatChannelDefinition(
			HL2RPIds.Channels.Request, DisplayName: "REQ", Prefixes: new[] { "req", "request" },
			Colour: "#d7a84b" ) );
		builder.RegisterChatChannel( new ChatChannelDefinition(
			HL2RPIds.Channels.Dispatch, HL2RPIds.Permissions.DispatchChat,
			DisplayName: "DISP", Prefixes: new[] { "disp", "dispatch" }, Colour: "#db6a61" ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Tune ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Request ) );
		builder.RegisterItem( CivicIdentityModule.Item(
			HL2RPIds.Items.Radio,
			"Handheld Radio",
			"A frequency-tuned handheld radio.",
			"Communications",
			false,
			null,
			1,
			1,
			HL2RPIds.Actions.Tune ) );
		builder.RegisterItem( CivicIdentityModule.Item(
			HL2RPIds.Items.RequestDevice,
			"Request Device",
			"A device for sending requests to Civil Protection.",
			"Communications",
			false,
			null,
			1,
			1,
			HL2RPIds.Actions.Request ) );
		builder.RegisterCommand( new CommandDefinition( HL2RPIds.Commands.RadioFrequency, Cost: CommandCostClass.Standard ) );
		builder.RegisterPanel( CivicIdentityModule.Panel<ChatPanelDescriptor>( HL2RPIds.Panels.Chat ) );
		builder.RegisterPanel( CivicIdentityModule.Panel<RadioTuningPanelDescriptor>(
			HL2RPIds.Panels.RadioTuning ) );
	}
}

public sealed class CommerceModule : IHexModule
{
	public string Id => HL2RPIds.Modules.Commerce;

	public void Configure( ModuleBuilder builder )
	{
		builder.DependsOn( HL2RPIds.Modules.CivicIdentity );
		builder.RegisterPermission( new PermissionDefinition( HL2RPIds.Permissions.CommerceManagement ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Open ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Consume ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Toggle ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.OpenBag ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Split ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Combine ) );
		builder.RegisterItem( CivicIdentityModule.Item(
			HL2RPIds.Items.Ration, "Ration", "A sealed Combine-issued ration package.", "Consumables",
			false, null, 1, 1, HL2RPIds.Actions.Open ) );
		builder.RegisterItem( CivicIdentityModule.Item(
			HL2RPIds.Items.Water, "Water", "A bottle of clean drinking water.", "Consumables",
			false, null, 1, 1, HL2RPIds.Actions.Consume ) );
		builder.RegisterItem( CivicIdentityModule.Item(
			HL2RPIds.Items.HealthVial, "Health Vial", "A small vial of medical solution.", "Consumables",
			false, null, 1, 1, HL2RPIds.Actions.Consume ) );
		builder.RegisterItem( CivicIdentityModule.Item(
			HL2RPIds.Items.Flashlight, "Flashlight", "A handheld rechargeable flashlight.", "Equipment",
			false, null, 1, 1, HL2RPIds.Actions.Toggle ) );
		builder.RegisterItem( CivicIdentityModule.Item(
			HL2RPIds.Items.Suitcase, "Suitcase", "A sturdy suitcase with its own inventory.", "Containers",
			true, "models/dev/box.vmdl", 2, 1, HL2RPIds.Actions.OpenBag ) );
		builder.RegisterItem( CivicIdentityModule.Item(
			HL2RPIds.Items.TokenStack, "Token Stack", "Physical Combine ration tokens.", "Currency",
			true, "models/dev/box.vmdl", 1, 1, HL2RPIds.Actions.Split, HL2RPIds.Actions.Combine ) );
		builder.RegisterCommand( new CommandDefinition( HL2RPIds.Commands.CommerceBuy, Cost: CommandCostClass.Standard ) );
		builder.RegisterCommand( new CommandDefinition( HL2RPIds.Commands.CommerceSell, Cost: CommandCostClass.Standard ) );
		builder.RegisterCommand( new CommandDefinition( HL2RPIds.Commands.PermitPurchase, Cost: CommandCostClass.Standard ) );
		builder.RegisterPanel( CivicIdentityModule.Panel<InventoryPanelDescriptor>( HL2RPIds.Panels.Inventory ) );
		builder.RegisterPanel( CivicIdentityModule.Panel<StoragePanelDescriptor>( HL2RPIds.Panels.Storage ) );
		builder.RegisterPanel( CivicIdentityModule.Panel<DoorPanelDescriptor>( HL2RPIds.Panels.Door ) );
		builder.RegisterPanel( CivicIdentityModule.Panel<VendorPanelDescriptor>( HL2RPIds.Panels.Vendor ) );
	}
}

public sealed class DocumentsModule : IHexModule
{
	public string Id => HL2RPIds.Modules.Documents;

	public void Configure( ModuleBuilder builder )
	{
		builder.DependsOn( HL2RPIds.Modules.CivicIdentity );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Read ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Write ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.PresentPermit ) );
		builder.RegisterItem( CivicIdentityModule.Item(
			HL2RPIds.Items.Note, "Note", "A writable paper note.", "Documents",
			false, null, 1, 1, HL2RPIds.Actions.Read, HL2RPIds.Actions.Write ) );
		builder.RegisterItem( CivicIdentityModule.Item(
			HL2RPIds.Items.CivicHandbook, "Civic Handbook", "A concise guide to City 17 civic rules.", "Documents",
			false, null, 1, 1, HL2RPIds.Actions.Read ) );
		builder.RegisterItem( CivicIdentityModule.Item(
			HL2RPIds.Items.BusinessPermit, "Business Permit", "A typed permit authorizing a regulated business category.", "Documents",
			false, null, 1, 1, HL2RPIds.Actions.PresentPermit ) );
		builder.RegisterCommand( new CommandDefinition( HL2RPIds.Commands.NoteWrite, Cost: CommandCostClass.Standard ) );
		builder.RegisterPanel( CivicIdentityModule.Panel<PermitPanelDescriptor>( HL2RPIds.Panels.Permit ) );
		builder.RegisterPanel( CivicIdentityModule.Panel<NoteEditorPanelDescriptor>( HL2RPIds.Panels.NoteEditor ) );
	}
}

public sealed class RestraintModule : IHexModule
{
	public string Id => HL2RPIds.Modules.Restraint;

	public void Configure( ModuleBuilder builder )
	{
		builder.DependsOn( HL2RPIds.Modules.CivicIdentity, HL2RPIds.Modules.Combine );
		builder.RegisterPermission( new PermissionDefinition( HL2RPIds.Permissions.Restraint ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Restrain ) );
		builder.RegisterItem( CivicIdentityModule.Item(
			HL2RPIds.Items.ZipTie, "Zip Tie", "A single-use plastic restraint.", "Equipment",
			false, null, 1, 1, HL2RPIds.Actions.Restrain ) );
		builder.RegisterCommand( new CommandDefinition(
			HL2RPIds.Commands.RestraintSet,
			HL2RPIds.Permissions.Restraint,
			Cost: CommandCostClass.Standard ) );
		builder.RegisterPanel( CivicIdentityModule.Panel<SearchPanelDescriptor>( HL2RPIds.Panels.Search ) );
		builder.RegisterPanel( CivicIdentityModule.Panel<RestraintStatusPanelDescriptor>(
			HL2RPIds.Panels.RestraintStatus ) );
	}
}

public sealed class ScannerModule : IHexModule
{
	public string Id => HL2RPIds.Modules.Scanner;

	public void Configure( ModuleBuilder builder )
	{
		builder.DependsOn( HL2RPIds.Modules.Combine, HL2RPIds.Modules.Communications );
		builder.RegisterPermission( new PermissionDefinition( HL2RPIds.Permissions.ScannerPilot ) );
		builder.RegisterCommand( new CommandDefinition(
			HL2RPIds.Commands.ScannerIntent,
			HL2RPIds.Permissions.ScannerPilot,
			Cost: CommandCostClass.Cheap ) );
		builder.RegisterPanel( CivicIdentityModule.Panel<ScannerOverlayPanelDescriptor>(
			HL2RPIds.Panels.ScannerOverlay ) );
	}
}

public sealed class CombatModule : IHexModule
{
	public string Id => HL2RPIds.Modules.Combat;

	public void Configure( ModuleBuilder builder )
	{
		builder.DependsOn( HL2RPIds.Modules.Combine );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Equip ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Unequip ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Reload ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Fire ) );
		builder.RegisterAction( new ActionDefinition( HL2RPIds.Actions.Replenish ) );
		builder.RegisterItem( CivicIdentityModule.Item(
			HL2RPIds.Items.Pistol, "Pistol", "A standard 9mm service pistol.", "Weapons",
			true, "models/dev/box.vmdl", 2, 1,
			HL2RPIds.Actions.Equip,
			HL2RPIds.Actions.Unequip,
			HL2RPIds.Actions.Reload,
			HL2RPIds.Actions.Fire ) );
		builder.RegisterItem( CivicIdentityModule.Item(
			HL2RPIds.Items.PistolAmmunition, "Pistol Ammunition", "A box of 9mm pistol cartridges.", "Ammunition",
			true, "models/dev/box.vmdl", 1, 1, HL2RPIds.Actions.Replenish ) );
		builder.RegisterItem( CivicIdentityModule.Item(
			HL2RPIds.Items.ProtectiveVest, "Protective Vest", "A reinforced vest that reduces incoming damage.", "Armor",
			false, null, 2, 2, HL2RPIds.Actions.Equip, HL2RPIds.Actions.Unequip ) );
		builder.RegisterCommand( new CommandDefinition( HL2RPIds.Commands.CombatRespawn, Cost: CommandCostClass.Standard ) );
		builder.RegisterPanel( CivicIdentityModule.Panel<ActionBarPanelDescriptor>( HL2RPIds.Panels.ActionBar ) );
		builder.RegisterPanel( CivicIdentityModule.Panel<DeathPanelDescriptor>( HL2RPIds.Panels.Death ) );
	}
}
