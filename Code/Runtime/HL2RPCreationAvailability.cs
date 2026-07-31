#nullable enable

using Hexagon.V2.Networking;
using HL2RP.UI;

namespace HL2RP.V2.Runtime;

/// <summary>
/// Host-only projection of account entitlements into creation availability.
/// Clients receive definition IDs and enabled decisions, never entitlement
/// authority or a mutable domain object.
/// </summary>
public static class HL2RPCreationAvailability
{
	public static SchemaViewSnapshot Build(
		long revision,
		HL2RPAccountEntitlementSnapshot self,
		bool canManage,
		HL2RPAccountEntitlementSnapshot? queried ) => new(
		HL2RPIds.Panels.CharacterCreation,
		revision,
		new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
		{
			[HL2RPPresentationFields.CharacterCreation.AccountId] = SnapshotValue.String(
				self.AccountId.Value.ToString( System.Globalization.CultureInfo.InvariantCulture ) ),
			[HL2RPPresentationFields.CharacterCreation.EntitlementFlags] = SnapshotValue.Integer( (long)self.Flags ),
			[HL2RPPresentationFields.CharacterCreation.CanManageEntitlements] = SnapshotValue.Boolean( canManage ),
			[HL2RPPresentationFields.CharacterCreation.QueriedAccountId] = SnapshotValue.String(
				queried?.AccountId.Value.ToString( System.Globalization.CultureInfo.InvariantCulture ) ?? string.Empty ),
			[HL2RPPresentationFields.CharacterCreation.QueriedFlags] = SnapshotValue.Integer( (long)(queried?.Flags ?? HL2RPWhitelist.None) ),
			[HL2RPPresentationFields.CharacterCreation.QueriedRevision] = SnapshotValue.Integer( queried?.Revision.Value ?? 0 )
		},
		Rows( self.Flags ) );

	public static IReadOnlyList<IReadOnlyDictionary<string, SnapshotValue>> Rows( HL2RPWhitelist flags )
	{
		if ( !HL2RPAccountEntitlements.IsValidSet( flags ) ) flags = HL2RPWhitelist.None;
		var cp = flags.HasFlag( HL2RPWhitelist.CivilProtection );
		var overwatch = flags.HasFlag( HL2RPWhitelist.Overwatch );
		var administration = flags.HasFlag( HL2RPWhitelist.CityAdministration );
		return new[]
		{
			Row( "faction", HL2RPIds.Factions.Citizen, HL2RPIds.Factions.Citizen, true ),
			Row( "faction", HL2RPIds.Factions.CivilProtection, HL2RPIds.Factions.CivilProtection, cp ),
			Row( "faction", HL2RPIds.Factions.Overwatch, HL2RPIds.Factions.Overwatch, overwatch ),
			Row( "faction", HL2RPIds.Factions.CityAdministration, HL2RPIds.Factions.CityAdministration, administration ),
			Row( "model", HL2RPIds.Models.Citizen01, HL2RPIds.Factions.Citizen, true ),
			Row( "model", HL2RPIds.Models.Citizen02, HL2RPIds.Factions.Citizen, true ),
			Row( "model", HL2RPIds.Models.Citizen03, HL2RPIds.Factions.Citizen, true ),
			Row( "model", HL2RPIds.Models.CivilProtectionUnit, HL2RPIds.Factions.CivilProtection, cp ),
			Row( "model", HL2RPIds.Models.OverwatchSoldier, HL2RPIds.Factions.Overwatch, overwatch ),
			Row( "model", HL2RPIds.Models.CityAdministrator, HL2RPIds.Factions.CityAdministration, administration ),
			Row( "class", HL2RPIds.Classes.Recruit, HL2RPIds.Factions.CivilProtection, cp ),
			Row( "class", HL2RPIds.Classes.Unit, HL2RPIds.Factions.CivilProtection, cp ),
			Row( "class", HL2RPIds.Classes.Elite, HL2RPIds.Factions.CivilProtection, cp ),
			Row( "class", HL2RPIds.Classes.Scanner, HL2RPIds.Factions.CivilProtection, cp )
		};
	}

	private static IReadOnlyDictionary<string, SnapshotValue> Row(
		string kind,
		string definitionId,
		string factionId,
		bool enabled ) => new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
		{
			[HL2RPPresentationFields.CharacterCreation.AvailabilityKind] = SnapshotValue.Choice( kind ),
			[HL2RPPresentationFields.CharacterCreation.DefinitionId] = SnapshotValue.Choice( definitionId ),
			[HL2RPPresentationFields.CharacterCreation.FactionId] = SnapshotValue.Choice( factionId ),
			[HL2RPPresentationFields.CharacterCreation.Enabled] = SnapshotValue.Boolean( enabled )
		};
}
