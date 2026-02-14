
public class SupplementItem : ConsumableItemDef
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
		ConsumeVerb = "Eating";
	}
}

public class LargeSupplementItem : ConsumableItemDef
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
		ConsumeVerb = "Eating";
	}
}
