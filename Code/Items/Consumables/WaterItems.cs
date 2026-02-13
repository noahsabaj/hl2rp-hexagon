
public class WaterItem : ItemDefinition
{
	public WaterItem()
	{
		UniqueId = "water";
		DisplayName = "Water";
		Description = "A bottle of clean drinking water.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Consumables";
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Drink"] = new ItemAction
			{
				Name = "Drink",
				OnRun = ( player, item ) =>
				{
					ConsumeItem( player, item );
					return true;
				}
			}
		};
	}

	protected void ConsumeItem( HexPlayerComponent player, ItemInstance item )
	{
		var inventories = InventoryManager.LoadForCharacter( player.Character.Id );
		foreach ( var inv in inventories )
		{
			if ( inv.Remove( item.Id ) )
			{
				ItemManager.DestroyInstance( item.Id );
				break;
			}
		}
	}
}

public class SparklingWaterItem : ItemDefinition
{
	public SparklingWaterItem()
	{
		UniqueId = "water_sparkling";
		DisplayName = "Sparkling Water";
		Description = "A bottle of sparkling mineral water.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Consumables";
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Drink"] = new ItemAction
			{
				Name = "Drink",
				OnRun = ( player, item ) =>
				{
					var inventories = InventoryManager.LoadForCharacter( player.Character.Id );
					foreach ( var inv in inventories )
					{
						if ( inv.Remove( item.Id ) )
						{
							ItemManager.DestroyInstance( item.Id );
							break;
						}
					}
				}
			}
		};
	}
}

public class SpecialWaterItem : ItemDefinition
{
	public SpecialWaterItem()
	{
		UniqueId = "water_special";
		DisplayName = "Special Water";
		Description = "A bottle of premium purified water.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Consumables";
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Drink"] = new ItemAction
			{
				Name = "Drink",
				OnRun = ( player, item ) =>
				{
					var inventories = InventoryManager.LoadForCharacter( player.Character.Id );
					foreach ( var inv in inventories )
					{
						if ( inv.Remove( item.Id ) )
						{
							ItemManager.DestroyInstance( item.Id );
							break;
						}
					}
				}
			}
		};
	}
}
