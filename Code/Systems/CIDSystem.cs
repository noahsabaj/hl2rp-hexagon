
public static class CIDSystem
{
	private static readonly Random _random = new();

	public static int GenerateCIDNumber()
	{
		return _random.Next( 10000, 100000 );
	}

	public static void AssignCID( HexPlayerComponent player, HexCharacter character )
	{
		var data = (HL2RPCharacter)character.Data;
		var cidNumber = GenerateCIDNumber();
		data.CIDNumber = cidNumber;
		character.MarkDirty( "CIDNumber" );

		var cidItem = ItemManager.CreateInstance( "cid_card", character.Id, new Dictionary<string, object>
		{
			{ "name", data.Name },
			{ "id", cidNumber },
			{ "cwu", false }
		} );

		var inventories = InventoryManager.LoadForCharacter( character.Id );
		if ( inventories.Count > 0 )
		{
			inventories[0].Add( cidItem );
		}

		Log.Info( $"[HL2RP] Assigned CID #{cidNumber} to {data.Name}" );
	}

	public static bool HasCID( HexCharacter character )
	{
		var data = (HL2RPCharacter)character.Data;
		return data.CIDNumber > 0;
	}

	public static int GetCIDNumber( HexCharacter character )
	{
		var data = (HL2RPCharacter)character.Data;
		return data.CIDNumber;
	}

	public static bool SetPriority( HexCharacter character, bool priority )
	{
		var cidItem = character.FindItem( "cid_card" );
		if ( cidItem == null )
			return false;

		cidItem.SetData( "cwu", priority );
		cidItem.MarkDirty();
		return true;
	}

	public static bool HasPriority( HexCharacter character )
	{
		var cidItem = character.FindItem( "cid_card" );
		return cidItem?.GetData<bool>( "cwu", false ) ?? false;
	}
}
