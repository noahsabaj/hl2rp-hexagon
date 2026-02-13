
public class SmallBagItem : BagItemDef
{
	public SmallBagItem()
	{
		UniqueId = "bag_small";
		DisplayName = "Small Bag";
		Description = "A small bag with limited storage space.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Bags";
		BagWidth = 2;
		BagHeight = 2;
	}
}

public class LargeBagItem : BagItemDef
{
	public LargeBagItem()
	{
		UniqueId = "bag_large";
		DisplayName = "Large Bag";
		Description = "A large bag with plenty of storage space.";
		Width = 2;
		Height = 2;
		MaxStack = 1;
		Category = "Bags";
		BagWidth = 4;
		BagHeight = 4;
	}
}

public class SuitcaseItem : BagItemDef
{
	public SuitcaseItem()
	{
		UniqueId = "suitcase";
		DisplayName = "Suitcase";
		Description = "A sturdy suitcase for carrying belongings.";
		Width = 2;
		Height = 1;
		MaxStack = 1;
		Category = "Bags";
		BagWidth = 3;
		BagHeight = 3;
	}
}
