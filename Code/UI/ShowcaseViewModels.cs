#nullable enable

using System;
using System.Collections.Immutable;
using Hexagon.V2.Domain;
using Hexagon.V2.Networking;
using HL2RP.V2.Domain;

namespace HL2RP.UI;

public enum ShowcaseWorkspace
{
	None = 0,
	Inventory = 1,
	Storage = 2,
	Vendor = 3,
	Permits = 4,
	Search = 5,
	CivicData = 6,
	Objectives = 7,
	NoteEditor = 8,
	Radio = 9,
	Scanner = 10,
	Entitlements = 11
}

public enum NotificationTone
{
	Neutral = 0,
	Positive = 1,
	Warning = 2,
	Critical = 3
}

public enum CivicPriority
{
	None = 0,
	Watch = 1,
	Detain = 2,
	Malignant = 3
}

public enum ScannerIntent
{
	Exit = 0,
	ToggleSpotlight = 1,
	Flash = 2,
	TakePhoto = 3,
	Move = 4
}

public sealed record UiChoice(string Id, string Label, string Description, bool Enabled = true);
public sealed record ModelChoice(DefinitionId Id, FactionId FactionId, string Label, string Description, bool Enabled = true);
public sealed record FactionChoice(FactionId Id, string Label, string Description, bool Enabled = true);
public sealed record ClassChoice(ClassId Id, FactionId FactionId, string Label, string Description, bool Enabled = true);

public sealed record CreationFieldOption(string Value, string Label, string Description);

public sealed record CreationFieldViewModel(
	string Id,
	string Label,
	string Hint,
	SnapshotValueKind Kind,
	bool Required,
	ImmutableArray<CreationFieldOption> Options);

public sealed record CharacterCreationViewModel(
	ImmutableArray<ModelChoice> Models,
	ImmutableArray<FactionChoice> Factions,
	ImmutableArray<ClassChoice> Classes,
	ImmutableArray<CreationFieldViewModel> Fields,
	int NameLimit,
	int DescriptionLimit);

public sealed record EntitlementAdministrationViewModel(
	AccountId AuthenticatedAccountId,
	HL2RPWhitelist SelfFlags,
	bool CanManage,
	AccountId? QueriedAccountId,
	HL2RPWhitelist QueriedFlags,
	long QueriedRevision);

public sealed record CharacterMenuViewModel(
	CharacterListSnapshot Characters,
	CharacterCreationViewModel Creation,
	EntitlementAdministrationViewModel? Entitlements,
	int MaximumSlots,
	bool IsBusy,
	string StatusText);

public sealed record HudViewModel(
	PlayerPublicSnapshot Public,
	PlayerPrivateSnapshot? Private,
	int Health,
	int MaximumHealth,
	int Armor,
	int MaximumArmor,
	string Location,
	string District,
	int Magazine,
	int ReserveAmmunition,
	bool FlashlightEnabled,
	bool RadioEnabled);

public sealed record ScoreboardEntryViewModel(
	ConnectionId ConnectionId,
	CharacterId? CharacterId,
	string DisplayName,
	FactionId? Faction,
	ClassId? Class,
	bool IsDead,
	string Status,
	bool IsLocal);

public sealed record ScoreboardViewModel(
	string ServerName,
	string SchemaName,
	string MapName,
	ImmutableArray<ScoreboardEntryViewModel> Players,
	bool CanInspectCivicData);

public sealed record NotificationViewModel(
	Guid Id,
	string Title,
	string Message,
	NotificationTone Tone,
	DateTimeOffset ExpiresAtUtc);

public sealed record InventoryWorkspaceViewModel(
	InventorySnapshot Inventory,
	ItemId? SelectedItemId,
	bool CanDrop,
	string EmptyMessage);

public sealed record InventoryTransferViewModel(
	InventorySnapshot Source,
	InventorySnapshot Destination,
	InteractionSessionId SessionId,
	string Caption,
	bool CanTake,
	bool CanDeposit);

public sealed record DoorSessionViewModel(
	InteractionSessionId SessionId,
	SceneEntityId SceneEntityId,
	bool IsOpen,
	bool CombineLocked,
	string OwnerStatus,
	bool CanClaim,
	string ClaimDisabledReason,
	bool CanRelease,
	string ReleaseDisabledReason);

public sealed record VendorOfferViewModel(
	DefinitionId DefinitionId,
	string Name,
	string Description,
	long Price,
	long Stock,
	bool CanBuy,
	string DisabledReason);

public sealed record VendorViewModel(
	InteractionSessionId SessionId,
	string Name,
	string Description,
	long Balance,
	ImmutableArray<VendorOfferViewModel> Offers,
	InventorySnapshot? SellingInventory);

public sealed record PermitCardViewModel(
	string PermitKindId,
	string Name,
	string Description,
	long Price,
	bool Owned,
	bool Available,
	string StatusText);

public sealed record PermitViewModel(long Balance, ImmutableArray<PermitCardViewModel> Permits);

public sealed record CivicInfractionViewModel(
	Guid Id,
	string Summary,
	int Points,
	DateTimeOffset IssuedAtUtc,
	string IssuedBy);

public sealed record CivicRecordViewModel(
	CharacterId CharacterId,
	string Name,
	string CitizenId,
	int Points,
	int InfractionCount,
	CivicPriority Priority,
	string RecordText,
	ImmutableArray<CivicInfractionViewModel> Infractions,
	bool CanEdit);

public sealed record CivicRecordUpdateRequest(
	CharacterId CharacterId,
	CivicPriority Priority,
	string RecordText);

public sealed record CityObjectiveViewModel(
	string Id,
	string Title,
	string Detail,
	DateTimeOffset UpdatedAtUtc,
	bool Completed);

public sealed record ObjectivesViewModel(
	ImmutableArray<CityObjectiveViewModel> Objectives,
	bool CanEdit);

public sealed record ObjectiveUpdateRequest(string? ObjectiveId, string Title, string Detail, bool Completed);

public sealed record NoteEditorViewModel(
	ItemId ItemId,
	string Body,
	int MaximumLength,
	bool CanEdit);

public sealed record NoteSaveRequest(ItemId ItemId, string Body);

public sealed record ItemPresentationFieldViewModel(
	string Id,
	string Label,
	string Value,
	bool IsBody);

public sealed record ItemActionPresentationViewModel(
	long Sequence,
	string Kind,
	string Title,
	ImmutableArray<ItemPresentationFieldViewModel> Fields);

public sealed record RadioViewModel(
	ItemId? RadioItemId,
	string Frequency,
	string ChannelName,
	bool Enabled,
	ImmutableArray<UiChoice> Presets);

public sealed record RadioTuneRequest(ItemId? RadioItemId, string Frequency, bool Enabled);
public sealed record ItemWorkspaceRequest(ItemId ItemId, ShowcaseWorkspace Workspace);

public sealed record RestraintViewModel(
	bool IsRestrained,
	TimeSpan? Remaining,
	CharacterId? TargetCharacterId,
	bool CanSearch,
	string StatusText);

public sealed record RestraintActionRequest(CharacterId TargetCharacterId, bool Restrain, bool Search = false);
public sealed record SearchOpenRequest(CharacterId TargetCharacterId);
public sealed record WorkspaceTransitionRequest(ShowcaseWorkspace Destination);

public sealed record ScannerContactViewModel(string Label, float Distance, bool Priority);

public sealed record ScannerViewModel(
	bool IsPiloting,
	string UnitName,
	bool SpotlightEnabled,
	DateTimeOffset? NextPhotoAtUtc,
	InteractionSessionId? SessionId,
	ImmutableArray<ScannerContactViewModel> Contacts);

public sealed record ScannerIntentRequest(
	InteractionSessionId? SessionId,
	ScannerIntent Intent,
	long Sequence = 0,
	int Forward = 0,
	int Right = 0,
	int Up = 0,
	int Yaw = 0,
	int Pitch = 0);

public sealed record CombineAlertViewModel(
	Guid Id,
	string Code,
	string Message,
	NotificationTone Tone,
	DateTimeOffset IssuedAtUtc);

public sealed record CombineOverlayViewModel(
	bool Visible,
	string Rank,
	string Division,
	string Callsign,
	string Directive,
	ImmutableArray<CombineAlertViewModel> Alerts);

public sealed record DeathViewModel(
	bool Visible,
	string Cause,
	DateTimeOffset? RespawnAtUtc,
	bool CanRespawn);

public sealed record VendorPurchaseRequest(InteractionSessionId SessionId, DefinitionId DefinitionId, long Quantity);
public sealed record VendorSaleRequest(InteractionSessionId SessionId, InventoryId InventoryId, ItemId ItemId, long Quantity);
public sealed record PermitPurchaseRequest(string PermitKindId);

public sealed record HL2RPShowcaseViewModel(
	bool IsReady,
	CharacterMenuViewModel? CharacterMenu,
	HudViewModel? Hud,
	ChatSnapshot? Chat,
	ScoreboardViewModel? Scoreboard,
	ImmutableArray<NotificationViewModel> Notifications,
	ActionProgressSnapshot? Action,
	DeathViewModel? Death,
	InventoryWorkspaceViewModel? Inventory,
	DoorSessionViewModel? Door,
	InventoryTransferViewModel? Storage,
	VendorViewModel? Vendor,
	PermitViewModel? Permits,
	InventoryTransferViewModel? Search,
	CombineOverlayViewModel? Combine,
	CivicRecordViewModel? CivicRecord,
	ObjectivesViewModel? Objectives,
	NoteEditorViewModel? Note,
	RadioViewModel? Radio,
	RestraintViewModel? Restraint,
	ScannerViewModel? Scanner,
	ItemActionPresentationViewModel? ItemPresentation,
	EntitlementAdministrationViewModel? Entitlements,
	ShowcaseWorkspace ActiveWorkspace,
	bool ScoreboardOpen);
