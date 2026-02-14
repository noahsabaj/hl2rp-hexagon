
public class HealthVialItem : ConsumableItemDef
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
		ConsumeVerb = "Applying";
	}

	public override bool OnConsume( HexPlayerComponent player, ItemInstance item )
	{
		// TODO: Apply healing when health system is wired
		return true;
	}
}

public class HealthKitItem : ConsumableItemDef
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
		ConsumeVerb = "Applying";
	}

	public override bool OnCanUse( HexPlayerComponent player, ItemInstance item )
	{
		return player.Character != null && CombineUtils.IsCombine( player.Character );
	}

	public override bool OnConsume( HexPlayerComponent player, ItemInstance item )
	{
		// TODO: Apply +50 HP when health system is wired
		return true;
	}
}

public class BleachItem : ConsumableItemDef
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
		ConsumeVerb = "Drinking";
	}

	public override bool OnConsume( HexPlayerComponent player, ItemInstance item )
	{
		// TODO: Apply damage/poison when health system is wired
		return true;
	}
}

public class VegetableOilItem : ConsumableItemDef
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
		ConsumeVerb = "Drinking";
	}
}
