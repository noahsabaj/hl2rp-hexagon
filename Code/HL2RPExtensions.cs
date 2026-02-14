
/// <summary>
/// Shared extension methods and utilities for HL2RP.
/// </summary>
public static class HL2RPExtensions
{
	/// <summary>
	/// Get the HexPlayerComponent from an IPressable event source.
	/// </summary>
	public static HexPlayerComponent GetPlayer( this Component.IPressable.Event e )
	{
		return e.Source?.GetComponentInParent<HexPlayerComponent>();
	}

	/// <summary>
	/// Check if a character has a specific item anywhere in their inventories.
	/// </summary>
	public static bool HasItem( this HexCharacter character, string itemId )
	{
		var inventories = InventoryManager.LoadForCharacter( character.Id );
		foreach ( var inv in inventories )
		{
			if ( inv.HasItem( itemId ) )
				return true;
		}
		return false;
	}

	/// <summary>
	/// Check if a character has any of the specified items in their inventories.
	/// </summary>
	public static bool HasAnyItem( this HexCharacter character, params string[] itemIds )
	{
		var inventories = InventoryManager.LoadForCharacter( character.Id );
		foreach ( var inv in inventories )
		{
			foreach ( var itemId in itemIds )
			{
				if ( inv.HasItem( itemId ) )
					return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Find the first instance of an item in a character's inventories.
	/// </summary>
	public static ItemInstance FindItem( this HexCharacter character, string itemId )
	{
		var inventories = InventoryManager.LoadForCharacter( character.Id );
		foreach ( var inv in inventories )
		{
			foreach ( var instanceId in inv.ItemIds )
			{
				var instance = ItemManager.GetInstance( instanceId );
				if ( instance?.DefinitionId == itemId )
					return instance;
			}
		}
		return null;
	}

	/// <summary>
	/// Find the first matching item from a list of item IDs.
	/// </summary>
	public static ItemInstance FindAnyItem( this HexCharacter character, params string[] itemIds )
	{
		var inventories = InventoryManager.LoadForCharacter( character.Id );
		foreach ( var inv in inventories )
		{
			foreach ( var instanceId in inv.ItemIds )
			{
				var instance = ItemManager.GetInstance( instanceId );
				if ( instance != null && itemIds.Contains( instance.DefinitionId ) )
					return instance;
			}
		}
		return null;
	}
}
