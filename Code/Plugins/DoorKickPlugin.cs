
/// <summary>
/// Registers the /doorkick command for Civil Protection. Door kicking mechanics
/// (timed action, damage, breach) are handled by the framework's DoorComponent.TryKick().
/// Permission gating (CP-only) is handled by ICanKickDoorListener on HL2RPHooks.
/// </summary>
[HexPlugin( "HL2RP - DoorKick",
	Description = "CP door kicking for HL2RP",
	Author = "Noah Sabaj",
	Version = "0.1",
	Priority = 20 )]
public class DoorKickPlugin : IHexPlugin
{
	public void OnPluginLoaded()
	{
		RegisterCommands();
		Log.Info( "[HL2RP] DoorKickPlugin loaded." );
	}

	public void OnPluginUnloaded() { }

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

				door.TryKick( caller );
				return $"Kicking \"{door.DoorName}\"...";
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
			.Ray( eyePos, eyePos + eyeDir * 100f )
			.Run();

		if ( !trace.Hit || trace.GameObject == null )
			return null;

		return trace.GameObject.GetComponentInParent<DoorComponent>();
	}
}
