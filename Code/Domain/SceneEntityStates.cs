#nullable enable

using HL2RP.V2.Schema;

namespace HL2RP.V2.Domain;

public sealed record DoorEntityState
{
	public required bool CombineLocked { get; init; }
	public required bool IsOpen { get; init; }
}

public sealed record StorageEntityState
{
	public required InventoryId InventoryId { get; init; }
	public required bool Locked { get; init; }
}

public sealed record VendorStockEntry
{
	public required DefinitionId Definition { get; init; }
	public required int Quantity { get; init; }
	public required long UnitPrice { get; init; }
}

public sealed record VendorEntityState
{
	public required BusinessPermitKind RequiredPermit { get; init; }
	public IReadOnlyList<VendorStockEntry> Stock { get; init; } = Array.Empty<VendorStockEntry>();
}

public sealed record MachineEntityState
{
	public required long Stock { get; init; }
	public required long UnitPrice { get; init; }
	public required double CooldownSeconds { get; init; }
	public required DateTimeOffset? CooldownUntilUtc { get; init; }
}

public sealed record ForcefieldEntityState
{
	public required bool Enabled { get; init; }
	public required bool CombineOnly { get; init; }
}

public static class ForcefieldEntityRules
{
	public static bool IsVisible( ForcefieldEntityState state ) => state.Enabled;
	public static bool IsSolid( ForcefieldEntityState state ) => state.Enabled && state.CombineOnly;
	public static bool IsPassageAuthorized( ForcefieldEntityState state, FactionId faction ) =>
		!state.Enabled || !state.CombineOnly || faction.Value is
			HL2RPIds.Factions.CivilProtection or HL2RPIds.Factions.Overwatch or
			HL2RPIds.Factions.CityAdministration;
}

public sealed record ScannerPhotoMetadata
{
	public required Guid PhotoId { get; init; }
	public required CharacterId PilotCharacterId { get; init; }
	public required DateTimeOffset CapturedAtUtc { get; init; }
	public required float PositionX { get; init; }
	public required float PositionY { get; init; }
	public required float PositionZ { get; init; }
}

public sealed record ScannerEntityState
{
	/// <summary>
	/// Stable scene link between a scanner dock and its drone. The dock points at
	/// the drone and the drone points back at its dock; only the drone may own a
	/// pilot session.
	/// </summary>
	public SceneEntityId? LinkedEntityId { get; init; }
	public CharacterId? PilotCharacterId { get; init; }
	public required bool SpotlightEnabled { get; init; }
	public required long LastAcceptedInputSequence { get; init; }
	public required DateTimeOffset? PhotoCooldownUntilUtc { get; init; }
	public IReadOnlyList<ScannerPhotoMetadata> Photos { get; init; } =
		Array.Empty<ScannerPhotoMetadata>();
}

public sealed record ScannerSceneLinkState(
	SceneEntityId EntityId,
	string Kind,
	SceneEntityId? LinkedEntityId );

public static class ScannerSceneLinkTopology
{
	public static OperationResult Validate( IEnumerable<ScannerSceneLinkState> links )
	{
		ArgumentNullException.ThrowIfNull( links );
		var materialized = links.ToArray();
		if ( materialized.Select( value => value.EntityId ).Distinct().Count() != materialized.Length )
			return OperationResult.Failure( ErrorCode.DuplicateRegistration, "Scanner scene identities are duplicated." );
		var byId = materialized.ToDictionary( value => value.EntityId );
		foreach ( var link in materialized )
		{
			if ( link.Kind is not ("scanner_dock" or "scanner_drone") )
				return OperationResult.Failure( ErrorCode.ConfigurationInvalid, "Scanner link has an unknown entity kind." );
			if ( link.LinkedEntityId is not SceneEntityId linkedId ||
				!byId.TryGetValue( linkedId, out var reciprocal ) ||
				reciprocal.LinkedEntityId != link.EntityId ||
				reciprocal.Kind == link.Kind )
				return OperationResult.Failure(
					ErrorCode.ConfigurationInvalid, $"Scanner scene link '{link.EntityId}' is missing, same-kind, or non-reciprocal." );
		}
		return OperationResult.Success();
	}
}

public sealed record CombatTargetEntityState
{
	public required long MaximumHealth { get; init; }
	public required long CurrentHealth { get; init; }
	public required DateTimeOffset? LastHitAtUtc { get; init; }
}

public sealed record CityObjectiveState
{
	public required string Id { get; init; }
	public required string Text { get; init; }
	public required bool Completed { get; init; }
	public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record CityEntityState
{
	public IReadOnlyList<CityObjectiveState> Objectives { get; init; } =
		Array.Empty<CityObjectiveState>();
}
