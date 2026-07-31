#nullable enable

namespace HL2RP.UI;

/// <summary>
/// Exact, versioned-by-code field contract between host-authored presentation
/// snapshots and the neutral client projection. Field matching is ordinal.
/// </summary>
public static class HL2RPPresentationFields
{
	public static class CharacterCreation
	{
		public const string AccountId = "account.id";
		public const string EntitlementFlags = "entitlements.flags";
		public const string CanManageEntitlements = "entitlements.can_manage";
		public const string QueriedAccountId = "entitlements.query.account_id";
		public const string QueriedFlags = "entitlements.query.flags";
		public const string QueriedRevision = "entitlements.query.revision";
		public const string AvailabilityKind = "availability.kind";
		public const string DefinitionId = "definition.id";
		public const string FactionId = "faction.id";
		public const string Enabled = "enabled";
	}

	public static class Roster
	{
		public const string DisplayName = "display.name";
		public const string FactionId = "faction.id";
		public const string ClassId = "class.id";
		public const string IsDead = "player.dead";
		public const string Status = "player.status";
		public const string IsLocal = "player.local";
	}

	public static class Vendor
	{
		public const string SessionId = "session.id";
		public const string Name = "vendor.name";
		public const string Description = "vendor.description";
		public const string Balance = "balance";
		public const string DefinitionId = "definition.id";
		public const string DisplayName = "display.name";
		public const string ItemDescription = "description";
		public const string Price = "price";
		public const string Stock = "stock";
		public const string CanBuy = "can_buy";
		public const string DisabledReason = "disabled.reason";
	}

	public static class Door
	{
		public const string SessionId = "session.id";
		public const string SceneEntityId = "scene_entity.id";
		public const string IsOpen = "door.open";
		public const string CombineLocked = "door.combine_locked";
		public const string OwnerStatus = "door.owner_status";
		public const string CanClaim = "can_claim";
		public const string ClaimDisabledReason = "claim.disabled_reason";
		public const string CanRelease = "can_release";
		public const string ReleaseDisabledReason = "release.disabled_reason";
	}

	public static class CivicData
	{
		public const string CharacterId = "character.id";
		public const string DisplayName = "display.name";
		public const string CitizenId = "civic.cid";
		public const string Points = "civic.points";
		public const string InfractionCount = "civic.infraction_count";
		public const string Priority = "civic.priority";
		public const string Record = "civic.record";
		public const string CanEdit = "can_edit";
		public const string InfractionId = "infraction.id";
		public const string Summary = "summary";
		public const string InfractionPoints = "points";
		public const string IssuedAtUnixMilliseconds = "issued_at.unix_ms";
		public const string IssuedBy = "issued_by";
	}

	public static class Objectives
	{
		public const string CanEdit = "can_edit";
		public const string ObjectiveId = "objective.id";
		public const string Title = "title";
		public const string Detail = "detail";
		public const string UpdatedAtUnixMilliseconds = "updated_at.unix_ms";
		public const string Completed = "completed";
	}

	public static class RestraintStatus
	{
		public const string Restrained = "restrained";
		public const string RemainingMilliseconds = "remaining_ms";
		public const string TargetCharacterId = "target_character.id";
		public const string CanSearch = "can_search";
		public const string Status = "status";
	}

	public static class ScannerOverlay
	{
		public const string Piloting = "piloting";
		public const string UnitName = "unit.name";
		public const string Spotlight = "spotlight";
		public const string PhotoReadyAtUnixMilliseconds = "photo_ready_at.unix_ms";
		public const string SessionId = "session.id";
		public const string ContactLabel = "label";
		public const string ContactDistance = "distance";
		public const string ContactPriority = "priority";
	}
}
