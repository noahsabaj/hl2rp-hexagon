
/// <summary>
/// Core HL2RP lifecycle hooks. Attach this Component to a GameObject in the scene
/// alongside HexagonFramework. Handles character creation, loading, and unloading.
/// </summary>
public class HL2RPHooks : Component,
	ICharacterCreatedListener,
	ICharacterLoadedListener,
	ICharacterUnloadedListener
{
	public void OnCharacterCreated( HexPlayerComponent player, HexCharacter character )
	{
		var data = (HL2RPCharacter)character.Data;
		var factionId = character.Faction;

		// Initialize CharData with default template
		if ( string.IsNullOrEmpty( data.CharData ) )
		{
			data.CharData = "Points:\nInfractions:\n";
			character.MarkDirty( "CharData" );
		}

		// Citizens get a CID on creation
		if ( factionId == "citizen" )
		{
			CIDSystem.AssignCID( player, character );
		}

		// Combine get loadout on creation
		if ( CombineUtils.IsCombineFaction( factionId ) )
		{
			LoadoutSystem.ApplyLoadout( player, character );
		}

		Log.Info( $"[HL2RP] Character created: {data.Name} ({factionId})" );
	}

	public void OnCharacterLoaded( HexPlayerComponent player, HexCharacter character )
	{
		// Apply rank-based model for combine
		if ( CombineUtils.IsCombine( character ) )
		{
			RankSystem.ApplyRankModel( player, character );
		}

		// Apply faction loadout (armor, ensure items)
		LoadoutSystem.ApplyLoadout( player, character );

		var data = (HL2RPCharacter)character.Data;
		Log.Info( $"[HL2RP] Character loaded: {data.Name} (faction: {character.Faction})" );
	}

	public void OnCharacterUnloaded( HexPlayerComponent player, HexCharacter character )
	{
		var data = (HL2RPCharacter)character.Data;
		Log.Info( $"[HL2RP] Character unloaded: {data.Name}" );
	}
}
