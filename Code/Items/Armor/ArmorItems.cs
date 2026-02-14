using Hexagon.Items.Bases;

public class RebelArmorItem : OutfitItemDef
{
	public RebelArmorItem()
	{
		UniqueId = "armor_rebel";
		DisplayName = "Rebel Armor";
		Description = "Makeshift citizen armor cobbled together from scrap metal and leather.";
		Slot = "torso";
		Width = 2;
		Height = 2;
		MaxStack = 1;
		Category = "Armor";
	}
}

public class ModifiedRebelArmorItem : OutfitItemDef
{
	public ModifiedRebelArmorItem()
	{
		UniqueId = "armor_rebel_modified";
		DisplayName = "Modified Rebel Armor";
		Description = "An enhanced set of makeshift armor with additional plating and reinforcement.";
		Slot = "torso";
		Width = 2;
		Height = 2;
		MaxStack = 1;
		Category = "Armor";
	}
}
