
/// <summary>
/// Allows shooting off standard door locks. When a locked door takes enough
/// bullet damage, its lock breaks and the door opens. Does not affect CombineLocks.
/// </summary>
[HexPlugin( "HL2RP - Shootlock",
	Description = "Shoot locks off doors for HL2RP",
	Author = "Noah Sabaj",
	Version = "0.1",
	Priority = 20 )]
public class ShootlockPlugin : IHexPlugin
{
	/// <summary>
	/// Tracks accumulated damage per door. Key = door ID, Value = total damage.
	/// </summary>
	private static readonly Dictionary<string, float> _doorDamage = new();

	/// <summary>
	/// Damage threshold to break a lock.
	/// </summary>
	private const float BreakThreshold = 150f;

	public void OnPluginLoaded()
	{
		Log.Info( "[HL2RP] ShootlockPlugin loaded." );
	}

	public void OnPluginUnloaded()
	{
		_doorDamage.Clear();
	}

	/// <summary>
	/// Called when a door takes bullet damage.
	/// Accumulates damage and breaks the lock if threshold is reached.
	/// </summary>
	public static void OnDoorDamaged( DoorComponent door, float damage )
	{
		if ( door == null || !door.IsLocked )
			return;

		// Don't affect doors with combine locks
		var combineLock = door.Components.Get<CombineLock>();
		if ( combineLock != null )
			return;

		var doorId = door.DoorId;

		if ( !_doorDamage.TryGetValue( doorId, out var accumulated ) )
			accumulated = 0f;

		accumulated += damage;
		_doorDamage[doorId] = accumulated;

		if ( accumulated >= BreakThreshold )
		{
			// Break the lock
			door.SetLocked( false );
			door.IsOpen = true;
			_doorDamage.Remove( doorId );

			Log.Info( $"[HL2RP] Door lock broken on \"{door.DoorName}\" ({doorId})" );
		}
	}

	/// <summary>
	/// Reset accumulated damage for a door (e.g. when it's relocked).
	/// </summary>
	public static void ResetDamage( string doorId )
	{
		_doorDamage.Remove( doorId );
	}
}
