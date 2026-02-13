
/// <summary>
/// Allows Civil Protection to kick doors open with a /doorkick command.
/// Traces forward from the player to find a nearby door and forces it open.
/// </summary>
[HexPlugin( "HL2RP - DoorKick",
	Description = "CP door kicking for HL2RP",
	Author = "Noah Sabaj",
	Version = "0.1",
	Priority = 20 )]
public class DoorKickPlugin : IHexPlugin
{
	/// <summary>
	/// Maximum distance for door kick trace.
	/// </summary>
	private const float KickRange = 100f;

	/// <summary>
	/// Force applied to the door when kicked.
	/// </summary>
	private const float KickForce = 3000f;

	public void OnPluginLoaded()
	{
		RegisterCommands();
		Log.Info( "[HL2RP] DoorKickPlugin loaded." );
	}

	private void RegisterCommands()
	{
		CommandManager.Register( new HexCommand
		{
			Name = "doorkick",
			Description = "Kick open a nearby door.",
			PermissionFunc = ( caller ) =>
			{
				return caller.Character != null && CombineUtils.IsCP( caller.Character );
			},
			OnRun = ( caller, ctx ) =>
			{
				var door = FindDoorInFront( caller );
				if ( door == null )
					return "No door found in front of you.";

				KickDoor( caller, door );
				return $"Kicked open \"{door.DoorName}\".";
			}
		} );
	}

	/// <summary>
	/// Trace forward from the player to find a door component.
	/// </summary>
	private static DoorComponent FindDoorInFront( HexPlayerComponent player )
	{
		var eyePos = player.WorldPosition + Vector3.Up * 64f;
		var eyeDir = player.WorldRotation.Forward;

		var trace = Game.ActiveScene.Trace
			.Ray( eyePos, eyePos + eyeDir * KickRange )
			.Run();

		if ( !trace.Hit || trace.GameObject == null )
			return null;

		return trace.GameObject.GetComponentInParent<DoorComponent>();
	}

	/// <summary>
	/// Force a door open with physics impulse.
	/// </summary>
	private static void KickDoor( HexPlayerComponent player, DoorComponent door )
	{
		door.SetLocked( false );
		door.IsOpen = true;

		// Apply physics force if door has a rigidbody
		var rb = door.Components.Get<Rigidbody>();
		if ( rb != null )
		{
			var kickDir = player.WorldRotation.Forward;
			rb.ApplyForce( kickDir * KickForce );
		}

		Log.Info( $"[HL2RP] {player.CharacterName} kicked open door \"{door.DoorName}\" ({door.DoorId})" );
	}
}
