using Hexagon.Items;

public class FlashlightTrait : ItemDataTrait
{
	public bool IsOn { get; set; } = false;
}

/// <summary>
/// Restricts flashlight usage to players who have a flashlight item in their inventory.
/// Without the item, the flashlight key does nothing.
/// </summary>
[HexPlugin( "HL2RP - Flashlight",
	Description = "Flashlight item requirement for HL2RP",
	Author = "Noah Sabaj",
	Version = "0.1",
	Priority = 20 )]
public class FlashlightPlugin : IHexPlugin
{
	public void OnPluginLoaded()
	{
		Log.Info( "[HL2RP] FlashlightPlugin loaded." );
	}

	public void OnPluginUnloaded() { }

	/// <summary>
	/// Check if a player has a flashlight item in their inventory.
	/// Called by input handling to gate flashlight activation.
	/// </summary>
	public static bool CanUseFlashlight( HexPlayerComponent player )
	{
		if ( player?.Character == null )
			return false;

		return player.Character.HasItem( "flashlight" );
	}

	/// <summary>
	/// Toggle flashlight state on the flashlight item.
	/// Returns true if toggled successfully.
	/// </summary>
	public static bool ToggleFlashlight( HexPlayerComponent player )
	{
		if ( player?.Character == null )
			return false;

		var item = player.Character.FindItem( "flashlight" );
		if ( item == null )
			return false;

		var trait = item.GetTrait<FlashlightTrait>();
		trait.IsOn = !trait.IsOn;
		item.MarkDirty();
		return true;
	}
}
