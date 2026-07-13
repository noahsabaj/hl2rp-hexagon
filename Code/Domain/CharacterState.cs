#nullable enable

namespace HL2RP.V2.Domain;

[Flags]
public enum HL2RPWhitelist
{
	None = 0,
	CivilProtection = 1 << 0,
	Overwatch = 1 << 1,
	CityAdministration = 1 << 2
}

public enum CivicPriorityStatus
{
	None,
	Watch,
	Detain,
	Malignant
}

public enum CombineRank
{
	None,
	Recruit,
	Unit,
	Elite,
	Scanner,
	OverwatchSoldier,
	Administrator
}

public enum CombineDivision
{
	None,
	Protection,
	Scanner,
	Overwatch,
	Administration
}

public sealed record CivicInfractionState
{
	public required string Code { get; init; }
	public required string Summary { get; init; }
	public required int Points { get; init; }
	public required DateTimeOffset IssuedAtUtc { get; init; }
	public required AccountId IssuedBy { get; init; }
}

public sealed record CivicRecordState
{
	public required long Points { get; init; }
	public required CivicPriorityStatus Priority { get; init; }
	public string RecordText { get; init; } = string.Empty;
	public IReadOnlyList<CivicInfractionState> Infractions { get; init; } =
		Array.Empty<CivicInfractionState>();
}

public sealed record CombineIdentityState
{
	public required CombineRank Rank { get; init; }
	public required CombineDivision Division { get; init; }
	public required string ServiceName { get; init; }
}

/// <summary>
/// Complete schema-owned character state. Authorization reads typed rank, division
/// and whitelist values; it never parses a display or service name. Introductions
/// are separate typed CharacterReferenceRecord payloads so both sides are indexed.
/// </summary>
public sealed record HL2RPCharacterState
{
	public required string CitizenId { get; init; }
	public required int Age { get; init; }
	public required string Pronouns { get; init; }
	public required string Origin { get; init; }
	public required HL2RPWhitelist Whitelists { get; init; }
	public required CivicRecordState CivicRecord { get; init; }
	public CombineIdentityState? CombineIdentity { get; init; }
}
