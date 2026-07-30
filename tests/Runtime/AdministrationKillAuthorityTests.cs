#nullable enable

using System;
using System.Linq;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using HL2RP.V2.Runtime;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Runtime;

/// <summary>
/// Kill authority is account-level, not faction-level. These pin that split, because the natural
/// drift is to add the permission to a faction set alongside the other administration permissions -
/// which would silently make a lethal power an in-character rank.
/// </summary>
[TestClass]
public sealed class AdministrationKillAuthorityTests
{
	private static CharacterRecord CharacterIn( string faction ) => new()
	{
		Id = CharacterId.New(),
		AccountId = new AccountId( 76561190000000000 ),
		Slot = 0,
		Name = "Test Subject",
		Description = "A character used to read a faction's permission set.",
		Model = new DefinitionId( HL2RPIds.Models.Citizen01 ),
		Faction = new FactionId( faction ),
		Balance = 0,
		CreatedAt = DateTimeOffset.UnixEpoch,
		LastPlayedAt = DateTimeOffset.UnixEpoch,
		SchemaState = new TypedPayload
		{
			TypeId = new PersistedTypeId( HL2RPIds.PersistedTypes.CharacterState ),
			TypeVersion = 1,
			Data = System.Text.Json.JsonDocument.Parse( "{}" ).RootElement.Clone()
		}
	};

	[TestMethod]
	public void NoFactionGrantsTheKillPermission()
	{
		string[] factions =
		{
			HL2RPIds.Factions.Citizen,
			HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Factions.Overwatch,
			HL2RPIds.Factions.CityAdministration
		};

		foreach ( var faction in factions )
		{
			var permissions = HL2RPRuntimeProjection.PermissionsFor( CharacterIn( faction ) );

			Assert.IsFalse(
				permissions.Contains( HL2RPIds.Permissions.AdministrationKill, StringComparer.Ordinal ),
				$"Faction '{faction}' grants a lethal command through an in-character rank." );
		}
	}

	/// <summary>
	/// The administration faction keeps its other powers - this is a deliberate carve-out of one
	/// permission, not a general demotion of City Administration.
	/// </summary>
	[TestMethod]
	public void CityAdministrationKeepsItsOtherAdministrativePowers()
	{
		var permissions = HL2RPRuntimeProjection.PermissionsFor(
			CharacterIn( HL2RPIds.Factions.CityAdministration ) );

		Assert.IsTrue( permissions.Contains( HL2RPIds.Permissions.AuditedAdministration, StringComparer.Ordinal ) );
		Assert.IsTrue( permissions.Contains( HL2RPIds.Permissions.ManageEntitlements, StringComparer.Ordinal ) );
	}

	[TestMethod]
	public void OnlyListedOperatorAccountsAreOperators()
	{
		var directory = HL2RPBootstrapOperatorDirectory.Parse( "76561190000000000, 76561190000000001" );
		Assert.IsTrue( directory.Succeeded, directory.Error?.Message );

		Assert.IsTrue( directory.Value.Contains( new AccountId( 76561190000000000 ) ) );
		Assert.IsTrue( directory.Value.Contains( new AccountId( 76561190000000001 ) ) );
		Assert.IsFalse( directory.Value.Contains( new AccountId( 76561190000000002 ) ) );
	}

	/// <summary>
	/// An empty operator list is the shipped default, and it must mean nobody rather than everybody.
	/// </summary>
	[TestMethod]
	public void AnEmptyOperatorListGrantsNobody()
	{
		var directory = HL2RPBootstrapOperatorDirectory.Parse( string.Empty );
		Assert.IsTrue( directory.Succeeded, directory.Error?.Message );

		Assert.IsEmpty( directory.Value.Accounts );
		Assert.IsFalse( directory.Value.Contains( new AccountId( 76561190000000000 ) ) );
	}
}
