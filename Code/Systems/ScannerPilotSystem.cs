
/// <summary>
/// Handles the camera switch, input remapping, and body management
/// when a player enters/exits scanner mode. This is the controller layer
/// on top of ScannerDrone.
/// </summary>
public static class ScannerPilotSystem
{
	/// <summary>
	/// Currently active scanner for the local player. Null if not piloting.
	/// </summary>
	private static ScannerDrone _activeScanner;

	/// <summary>
	/// Try to enter a scanner drone.
	/// </summary>
	public static bool EnterScanner( HexPlayerComponent player, ScannerDrone scanner )
	{
		if ( player == null || scanner == null )
			return false;

		if ( _activeScanner != null )
			return false;

		if ( !scanner.TryEnter( player ) )
			return false;

		_activeScanner = scanner;

		// TODO: Switch camera to scanner position
		// TODO: Disable player body controls
		// TODO: Enable scanner input bindings

		Log.Info( $"[HL2RP] ScannerPilotSystem: {player.CharacterName} now piloting scanner" );
		return true;
	}

	/// <summary>
	/// Exit the currently piloted scanner.
	/// </summary>
	public static void ExitScanner()
	{
		if ( _activeScanner == null )
			return;

		_activeScanner.Exit();

		// TODO: Switch camera back to player
		// TODO: Re-enable player body controls
		// TODO: Disable scanner input bindings

		Log.Info( "[HL2RP] ScannerPilotSystem: Exited scanner" );
		_activeScanner = null;
	}

	/// <summary>
	/// Whether the local player is currently piloting a scanner.
	/// </summary>
	public static bool IsPiloting => _activeScanner != null;

	/// <summary>
	/// Get the currently piloted scanner.
	/// </summary>
	public static ScannerDrone ActiveScanner => _activeScanner;

	/// <summary>
	/// Process scanner-specific input. Called from input hook.
	/// </summary>
	public static void ProcessInput()
	{
		if ( _activeScanner == null )
			return;

		// Flash on primary attack
		if ( Input.Pressed( "attack1" ) )
		{
			_activeScanner.Flash();
		}

		// Toggle spotlight on secondary attack
		if ( Input.Pressed( "attack2" ) )
		{
			_activeScanner.ToggleSpotlight();
		}

		// Exit on use key
		if ( Input.Pressed( "use" ) )
		{
			ExitScanner();
		}
	}
}
