
public class HealthVialItem : ItemDefinition
{
	public HealthVialItem()
	{
		UniqueId = "health_vial";
		DisplayName = "Health Vial";
		Description = "A small vial of medical solution.";
		Width = 1;
		Height = 1;
		MaxStack = 3;
		Category = "Consumables";
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Use"] = new ItemAction
			{
				Name = "Use",
				OnRun = ( player, item ) =>
				{
					// TODO: Apply healing when health system is wired
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

public class HealthKitItem : ItemDefinition
{
	public HealthKitItem()
	{
		UniqueId = "health_kit";
		DisplayName = "Health Kit";
		Description = "A medical kit containing bandages and supplies. Combine use only.";
		Width = 2;
		Height = 1;
		MaxStack = 1;
		Category = "Consumables";
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Use"] = new ItemAction
			{
				Name = "Use",
				OnCanRun = ( player, item ) =>
				{
					return player.Character != null && CombineUtils.IsCombine( player.Character );
				},
				OnRun = ( player, item ) =>
				{
					// TODO: Apply +50 HP when health system is wired
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

public class BleachItem : ItemDefinition
{
	public BleachItem()
	{
		UniqueId = "bleach";
		DisplayName = "Bleach";
		Description = "A bottle of industrial cleaning bleach.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Consumables";
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Use"] = new ItemAction
			{
				Name = "Use",
				OnRun = ( player, item ) =>
				{
					// TODO: Apply damage/poison when health system is wired
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

public class VegetableOilItem : ItemDefinition
{
	public VegetableOilItem()
	{
		UniqueId = "vegetable_oil";
		DisplayName = "Vegetable Oil";
		Description = "A bottle of cooking oil.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Consumables";
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Use"] = new ItemAction
			{
				Name = "Use",
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
