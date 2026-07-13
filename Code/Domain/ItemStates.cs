#nullable enable

namespace HL2RP.V2.Domain;

public sealed record CitizenIdCardItemState
{
	public required string CitizenId { get; init; }
	public required string IssuedName { get; init; }
	public required DateTimeOffset IssuedAtUtc { get; init; }
	public required CivicPriorityStatus Priority { get; init; }
}

public sealed record RadioItemState
{
	public required string Frequency { get; init; }
	public required bool Powered { get; init; }
}

public sealed record FlashlightItemState
{
	public required bool Powered { get; init; }
	public required int ChargePermille { get; init; }
}

public sealed record RequestDeviceItemState
{
	public required bool Powered { get; init; }
	public required DateTimeOffset? LastRequestAtUtc { get; init; }
}

public sealed record NoteItemState
{
	public required string Text { get; init; }
	public CharacterId? OwnerCharacterId { get; init; }
	public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public enum BusinessPermitKind
{
	General,
	Food,
	Electronics,
	Literature
}

public sealed record BusinessPermitItemState
{
	public required BusinessPermitKind Kind { get; init; }
	public required CharacterId OwnerCharacterId { get; init; }
	public required DateTimeOffset IssuedAtUtc { get; init; }
	public DateTimeOffset? ExpiresAtUtc { get; init; }
	public required bool Revoked { get; init; }
}

public sealed record PistolItemState
{
	public const int MagazineCapacity = 18;

	public required int MagazineRounds { get; init; }
	public required bool Equipped { get; init; }
	public required bool Raised { get; init; }
	public required DateTimeOffset? LastFiredAtUtc { get; init; }
}

public sealed record PistolAmmunitionItemState
{
	public required int Rounds { get; init; }
}

public sealed record ProtectiveVestItemState
{
	public required int Durability { get; init; }
	public required int DamageReductionPermille { get; init; }
	public required bool Equipped { get; init; }
}

public sealed record CombineLockKitItemState
{
	public required int RemainingInstallations { get; init; }
}

public sealed record TokenStackItemState
{
	public required long Amount { get; init; }
}
