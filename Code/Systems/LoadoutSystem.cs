
public static class LoadoutSystem
{
	public static void ApplyLoadout( HexPlayerComponent player, HexCharacter character )
	{
		var data = (HL2RPCharacter)character.Data;
		var factionId = character.Faction;

		switch ( factionId )
		{
			case "cca":
				ApplyCPLoadout( player, character, data );
				break;
			case "ota":
				ApplyOWLoadout( player, character, data );
				break;
			case "citizen":
			case "cityadmin":
			case "vortigaunt":
			default:
				data.Armor = 0;
				character.MarkDirty( "Armor" );
				break;
		}
	}

	private static void ApplyCPLoadout( HexPlayerComponent player, HexCharacter character, HL2RPCharacter data )
	{
		data.Armor = HexConfig.Get<int>( "hl2rp.combine.cpArmor", 50 );
		character.MarkDirty( "Armor" );

		EnsureItem( character, "weapon_stunstick" );
		EnsureItem( character, "radio", new Dictionary<string, object>
		{
			{ "frequency", "100.0" }
		} );
	}

	private static void ApplyOWLoadout( HexPlayerComponent player, HexCharacter character, HL2RPCharacter data )
	{
		data.Armor = HexConfig.Get<int>( "hl2rp.combine.owArmor", 100 );
		character.MarkDirty( "Armor" );

		EnsureItem( character, "radio", new Dictionary<string, object>
		{
			{ "frequency", "100.0" }
		} );
	}

	private static void EnsureItem( HexCharacter character, string definitionId, Dictionary<string, object> data = null )
	{
		var inventories = InventoryManager.LoadForCharacter( character.Id );
		foreach ( var inv in inventories )
		{
			if ( inv.HasItem( definitionId ) )
				return;
		}

		if ( inventories.Count > 0 )
		{
			var item = ItemManager.CreateInstance( definitionId, character.Id, data );
			inventories[0].Add( item );
		}
	}
}
