
public class SupplementItem : ItemDefinition
{
	public SupplementItem()
	{
		UniqueId = "supplement";
		DisplayName = "Supplement";
		Description = "A standard nutritional supplement.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Consumables";
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Eat"] = new ItemAction
			{
				Name = "Eat",
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
					return true;
				}
			}
		};
	}
}

public class LargeSupplementItem : ItemDefinition
{
	public LargeSupplementItem()
	{
		UniqueId = "supplement_large";
		DisplayName = "Large Supplement";
		Description = "A larger nutritional supplement with extra calories.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Consumables";
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Eat"] = new ItemAction
			{
				Name = "Eat",
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
					return true;
				}
			}
		};
	}
}
