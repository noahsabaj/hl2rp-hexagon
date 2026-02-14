using Hexagon.Config;

/// <summary>
/// Schema-specific armor assignment. Item loadouts are now handled by the framework's
/// LoadoutManager via ClassDefinition.Loadout entries.
/// </summary>
public static class LoadoutSystem
{
	/// <summary>
	/// Apply faction-specific armor values. Called on character creation and load.
	/// </summary>
	public static void ApplyArmor( HexPlayerComponent player, HexCharacter character )
	{
		var data = (HL2RPCharacter)character.Data;
		var factionId = character.Faction;

		switch ( factionId )
		{
			case "cca":
				data.Armor = HexConfig.Get<int>( "hl2rp.combine.cpArmor", 50 );
				character.MarkDirty( "Armor" );
				break;
			case "ota":
				data.Armor = HexConfig.Get<int>( "hl2rp.combine.owArmor", 100 );
				character.MarkDirty( "Armor" );
				break;
			default:
				data.Armor = 0;
				character.MarkDirty( "Armor" );
				break;
		}
	}
}
