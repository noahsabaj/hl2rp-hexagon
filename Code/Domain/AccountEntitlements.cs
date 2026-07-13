#nullable enable

using Hexagon.V2.Persistence;

namespace HL2RP.V2.Domain;

/// <summary>
/// The one immutable, schema-owned entitlement document for an authenticated
/// account. Character records deliberately keep their creation-time snapshot;
/// revocation affects future creation and does not rewrite existing characters.
/// </summary>
[PersistedType( HL2RPIds.PersistedTypes.AccountEntitlement, 1 )]
public sealed record HL2RPAccountEntitlementRecord
{
	public required AccountId AccountId { get; init; }
	public required HL2RPWhitelist Flags { get; init; }
	public required AccountId UpdatedByAccountId { get; init; }
	public CharacterId? UpdatedByCharacterId { get; init; }
	public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public static class HL2RPAccountEntitlements
{
	public const string Collection = "hl2rp.account-entitlements";
	public const HL2RPWhitelist All = HL2RPWhitelist.CivilProtection |
		HL2RPWhitelist.Overwatch | HL2RPWhitelist.CityAdministration;

	public static string Key( AccountId accountId ) =>
		accountId.Value.ToString( System.Globalization.CultureInfo.InvariantCulture );

	public static bool IsValidSet( HL2RPWhitelist flags ) => (flags & ~All) == 0;

	public static bool IsSingleFlag( HL2RPWhitelist flag ) => flag is
		HL2RPWhitelist.CivilProtection or HL2RPWhitelist.Overwatch or HL2RPWhitelist.CityAdministration;

	public static HL2RPWhitelist RequiredForFaction( FactionId faction ) => faction.Value switch
	{
		HL2RPIds.Factions.CivilProtection => HL2RPWhitelist.CivilProtection,
		HL2RPIds.Factions.Overwatch => HL2RPWhitelist.Overwatch,
		HL2RPIds.Factions.CityAdministration => HL2RPWhitelist.CityAdministration,
		_ => HL2RPWhitelist.None
	};

	public static OperationResult<HL2RPWhitelist> ParseSingleFlag( string value ) => value switch
	{
		"civil_protection" => OperationResult<HL2RPWhitelist>.Success( HL2RPWhitelist.CivilProtection ),
		"overwatch" => OperationResult<HL2RPWhitelist>.Success( HL2RPWhitelist.Overwatch ),
		"city_administration" => OperationResult<HL2RPWhitelist>.Success( HL2RPWhitelist.CityAdministration ),
		_ => OperationResult<HL2RPWhitelist>.Failure(
			ErrorCode.InvalidArgument, "Entitlement flag is unknown or is not a single grantable flag." )
	};

	public static string StableName( HL2RPWhitelist flag ) => flag switch
	{
		HL2RPWhitelist.CivilProtection => "civil_protection",
		HL2RPWhitelist.Overwatch => "overwatch",
		HL2RPWhitelist.CityAdministration => "city_administration",
		_ => throw new ArgumentOutOfRangeException( nameof(flag), "Entitlement must be one known flag." )
	};
}

public sealed record HL2RPAccountEntitlementSnapshot(
	AccountId AccountId,
	HL2RPWhitelist Flags,
	DocumentRevision Revision,
	bool IsPersisted );

public sealed record HL2RPEntitlementAdministrator(
	AccountId AccountId,
	CharacterId? CharacterId );

public sealed record HL2RPAccountEntitlementChanged(
	AccountId AccountId,
	HL2RPWhitelist PreviousFlags,
	HL2RPWhitelist CurrentFlags,
	DocumentRevision Revision,
	long CommitSequence,
	DateTimeOffset CommittedAtUtc );
