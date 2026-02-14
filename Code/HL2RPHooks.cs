
/// <summary>
/// Core HL2RP lifecycle hooks. Attach this Component to a GameObject in the scene
/// alongside HexagonFramework. Handles character creation, loading, unloading,
/// and door breach permission checks.
/// </summary>
public class HL2RPHooks : Component,
	ICharacterCreatedListener,
	ICharacterLoadedListener,
	ICharacterUnloadedListener,
	ICanKickDoorListener,
	IDoorBreachedListener
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

		// Apply faction-specific armor (items handled by framework LoadoutManager)
		if ( CombineUtils.IsCombineFaction( factionId ) )
		{
			LoadoutSystem.ApplyArmor( player, character );
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

		// Reset armor for faction on every load
		LoadoutSystem.ApplyArmor( player, character );

		var data = (HL2RPCharacter)character.Data;
		Log.Info( $"[HL2RP] Character loaded: {data.Name} (faction: {character.Faction})" );
	}

	public void OnCharacterUnloaded( HexPlayerComponent player, HexCharacter character )
	{
		var data = (HL2RPCharacter)character.Data;
		Log.Info( $"[HL2RP] Character unloaded: {data.Name}" );
	}

	// --- Door Breach Hooks ---

	/// <summary>
	/// Only Civil Protection can kick doors.
	/// </summary>
	public bool CanKickDoor( HexPlayerComponent player, DoorComponent door )
	{
		if ( player?.Character == null )
			return false;

		return CombineUtils.IsCP( player.Character );
	}

	/// <summary>
	/// Prevent combine-locked doors from being breached. If a door with a CombineLock
	/// is breached (shot open), immediately repair it.
	/// </summary>
	public void OnDoorBreached( HexPlayerComponent attacker, DoorComponent door )
	{
		var combineLock = door.Components.Get<CombineLock>();
		if ( combineLock != null )
		{
			door.RepairLock();
		}
	}
}
