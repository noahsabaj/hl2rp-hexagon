
public class WaterItem : ConsumableItemDef
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
		ConsumeVerb = "Drinking";
	}
}

public class SparklingWaterItem : ConsumableItemDef
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
		ConsumeVerb = "Drinking";
	}
}

public class SpecialWaterItem : ConsumableItemDef
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
		ConsumeVerb = "Drinking";
	}
}
