#nullable enable

namespace HL2RP.V2.Domain;

/// <summary>
/// Typed state for a framework CharacterReferenceRecord. The outer record owns
/// both character IDs so deletion can remove either side without decoding this payload.
/// </summary>
public sealed record RecognitionReferenceState
{
	public required string IntroducedName { get; init; }
	public required DateTimeOffset IntroducedAtUtc { get; init; }
}

/// <summary>
/// Durable restraint fact stored in a CharacterReferenceRecord. Active search
/// capabilities remain transient and are revoked whenever this fact is removed.
/// </summary>
public sealed record RestraintReferenceState
{
	public required DateTimeOffset RestrainedAtUtc { get; init; }
	public required bool Active { get; init; }
}

/// <summary>
/// Typed ownership metadata for a CharacterReferenceRecord whose outer record
/// indexes both the owning character and scene entity.
/// </summary>
public sealed record DoorOwnershipReferenceState
{
	public required DateTimeOffset AcquiredAtUtc { get; init; }
}
