
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

		data.Armor = character.Faction switch
		{
			"cca" => HexConfig.Get<int>( "hl2rp.combine.cpArmor", 50 ),
			"ota" => HexConfig.Get<int>( "hl2rp.combine.owArmor", 100 ),
			_ => 0
		};

		character.MarkDirty( "Armor" );
	}
}
