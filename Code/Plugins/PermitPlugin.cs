
/// <summary>
/// Business permit enforcement. Items tagged with a permit category
/// can only be sold by players who hold the matching permit in their inventory.
/// </summary>
[HexPlugin( "HL2RP - Permits",
	Description = "Business permit enforcement for HL2RP",
	Author = "Noah Sabaj",
	Version = "0.1",
	Priority = 20 )]
public class PermitPlugin : IHexPlugin
{
	/// <summary>
	/// Maps item categories to required permit types.
	/// </summary>
	private static readonly Dictionary<string, string> CategoryToPermit = new()
	{
		{ "Consumables", "food" },
		{ "Equipment", "elec" },
		{ "Literature", "lit" },
		{ "Misc", "misc" }
	};

	public void OnPluginLoaded()
	{
		Log.Info( "[HL2RP] PermitPlugin loaded." );
	}

	/// <summary>
	/// Check if a player has the required permit to sell an item of the given category.
	/// Returns true if no permit is required or the player has the correct permit.
	/// </summary>
	public static bool HasPermitForCategory( HexPlayerComponent player, string itemCategory )
	{
		if ( player?.Character == null )
			return false;

		if ( !CategoryToPermit.TryGetValue( itemCategory, out var requiredPermitType ) )
			return true; // No permit needed for this category

		return HasPermit( player, requiredPermitType );
	}

	/// <summary>
	/// Check if a player has a specific permit type in their inventory.
	/// </summary>
	public static bool HasPermit( HexPlayerComponent player, string permitType )
	{
		if ( player?.Character == null )
			return false;

		var permitItemId = $"permit_{permitType}";
		var inventories = InventoryManager.LoadForCharacter( player.Character.Id );

		foreach ( var inv in inventories )
		{
			if ( inv.HasItem( permitItemId ) )
				return true;
		}

		return false;
	}
}
