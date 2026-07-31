#nullable enable

using Hexagon.V2.Kernel.Definitions;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Schema;

[TestClass]
public sealed class CuratedItemTests
{
	[TestMethod]
	public void RegistersExactlyTheSeventeenCuratedItems()
	{
		var schema = HL2RPSchemaTests.Compile();

		HL2RPSchemaTests.AssertSetEquals(
			new[]
			{
				HL2RPIds.Items.CitizenIdCard,
				HL2RPIds.Items.Ration,
				HL2RPIds.Items.Water,
				HL2RPIds.Items.HealthVial,
				HL2RPIds.Items.Flashlight,
				HL2RPIds.Items.Radio,
				HL2RPIds.Items.RequestDevice,
				HL2RPIds.Items.Note,
				HL2RPIds.Items.CivicHandbook,
				HL2RPIds.Items.Suitcase,
				HL2RPIds.Items.BusinessPermit,
				HL2RPIds.Items.ZipTie,
				HL2RPIds.Items.Pistol,
				HL2RPIds.Items.PistolAmmunition,
				HL2RPIds.Items.ProtectiveVest,
				HL2RPIds.Items.CombineLockKit,
				HL2RPIds.Items.TokenStack
			},
			schema.Items.All.Select( item => item.Id ) );
	}

	[TestMethod]
	public void EveryItemActionResolvesAndDropBehaviorHasAnExplicitModelDecision()
	{
		var schema = HL2RPSchemaTests.Compile();

		foreach ( var item in schema.Items.All )
		{
			foreach ( var action in item.ActionIds )
				Assert.IsTrue( schema.Actions.Contains( action ), $"{item.Id} references {action}." );
			if ( item.CanDrop )
			{
				Assert.IsFalse( string.IsNullOrWhiteSpace( item.WorldModel ), $"{item.Id} has no world model." );
				Assert.StartsWith( "models/", item.WorldModel, $"{item.Id} model is not rooted." );
				Assert.EndsWith( ".vmdl", item.WorldModel, $"{item.Id} model is not a vmdl." );
			}
			else
			{
				Assert.IsNull( item.WorldModel, $"{item.Id} must explicitly remain non-droppable." );
			}
		}
	}

	[TestMethod]
	public void PistolAmmoAndVestExposeOnlyTheReferenceCombatActions()
	{
		var schema = HL2RPSchemaTests.Compile();
		var pistol = RequireItem( schema.Items.Require( HL2RPIds.Items.Pistol ).Value );
		var ammunition = RequireItem( schema.Items.Require( HL2RPIds.Items.PistolAmmunition ).Value );
		var vest = RequireItem( schema.Items.Require( HL2RPIds.Items.ProtectiveVest ).Value );

		HL2RPSchemaTests.AssertSetEquals(
			new[]
			{
				HL2RPIds.Actions.Equip,
				HL2RPIds.Actions.Unequip,
				HL2RPIds.Actions.Reload,
				HL2RPIds.Actions.Fire
			},
			pistol.ActionIds );
		HL2RPSchemaTests.AssertSetEquals(
			new[] { HL2RPIds.Actions.Replenish },
			ammunition.ActionIds );
		HL2RPSchemaTests.AssertSetEquals(
			new[] { HL2RPIds.Actions.Equip, HL2RPIds.Actions.Unequip },
			vest.ActionIds );
	}

	private static ItemDefinition RequireItem( ItemDefinition definition ) => definition;
}
