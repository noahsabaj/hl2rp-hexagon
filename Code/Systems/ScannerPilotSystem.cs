
/// <summary>
/// Handles the camera switch, input remapping, and body management
/// when a player enters/exits scanner mode. This is the controller layer
/// on top of ScannerDrone. Tracks per-player scanner assignments for multiplayer.
/// </summary>
public static class ScannerPilotSystem
{
	/// <summary>
	/// Maps character ID to their active scanner drone.
	/// </summary>
	private static readonly Dictionary<string, ScannerDrone> _activeScanners = new();

	/// <summary>
	/// Try to enter a scanner drone.
	/// </summary>
	public static bool EnterScanner( HexPlayerComponent player, ScannerDrone scanner )
	{
		if ( player?.Character == null || scanner == null )
			return false;

		var charId = player.Character.Id;
		if ( _activeScanners.ContainsKey( charId ) )
			return false;

		if ( !scanner.TryEnter( player ) )
			return false;

		_activeScanners[charId] = scanner;

		// TODO: Switch camera to scanner position
		// TODO: Disable player body controls
		// TODO: Enable scanner input bindings

		Log.Info( $"[HL2RP] ScannerPilotSystem: {player.CharacterName} now piloting scanner" );
		return true;
	}

	/// <summary>
	/// Exit the scanner a specific player is piloting.
	/// </summary>
	public static void ExitScanner( HexPlayerComponent player )
	{
		if ( player?.Character == null )
			return;

		var charId = player.Character.Id;
		if ( !_activeScanners.TryGetValue( charId, out var scanner ) )
			return;

		scanner.Exit();
		// scanner.Exit() calls OnScannerExited to clean up the dictionary
	}

	/// <summary>
	/// Called by ScannerDrone.Exit() to remove the mapping when a drone is exited
	/// (whether by player request, crash, or destruction).
	/// </summary>
	internal static void OnScannerExited( string characterId )
	{
		if ( _activeScanners.Remove( characterId ) )
		{
			// TODO: Switch camera back to player
			// TODO: Re-enable player body controls
			// TODO: Disable scanner input bindings

			Log.Info( "[HL2RP] ScannerPilotSystem: Scanner exited" );
		}
	}

	/// <summary>
	/// Whether a specific player is currently piloting a scanner.
	/// </summary>
	public static bool IsPiloting( HexPlayerComponent player )
	{
		return player?.Character != null && _activeScanners.ContainsKey( player.Character.Id );
	}

	/// <summary>
	/// Get the scanner a specific player is piloting, or null.
	/// </summary>
	public static ScannerDrone GetActiveScanner( HexPlayerComponent player )
	{
		if ( player?.Character == null ) return null;
		_activeScanners.TryGetValue( player.Character.Id, out var scanner );
		return scanner;
	}

	/// <summary>
	/// Process scanner-specific input for the local player. Called from input hook.
	/// Reads local input and sends it to the server via RPCs on the drone.
	/// </summary>
	public static void ProcessInput()
	{
		var localPlayer = HexUIManager.GetLocalPlayer();
		var scanner = GetActiveScanner( localPlayer );
		if ( scanner == null )
			return;

		// Build wish direction from local input
		var wishDir = Vector3.Zero;
		if ( Input.Down( "forward" ) ) wishDir += scanner.WorldRotation.Forward;
		if ( Input.Down( "backward" ) ) wishDir -= scanner.WorldRotation.Forward;
		if ( Input.Down( "left" ) ) wishDir -= scanner.WorldRotation.Right;
		if ( Input.Down( "right" ) ) wishDir += scanner.WorldRotation.Right;
		if ( Input.Down( "jump" ) ) wishDir += Vector3.Up;
		if ( Input.Down( "duck" ) ) wishDir -= Vector3.Up;
		if ( wishDir.Length > 0 )
			wishDir = wishDir.Normal;

		// Send input to server
		scanner.CmdSetWishDirection( wishDir );

		if ( Input.Pressed( "attack1" ) )
			scanner.CmdFlash();

		if ( Input.Pressed( "attack2" ) )
			scanner.CmdToggleSpotlight();

		if ( Input.Pressed( "use" ) )
			scanner.CmdRequestExit();
	}
}
