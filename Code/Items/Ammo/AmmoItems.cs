public class AmmoItem : AmmoItemDef
{
	public AmmoItem( string id, string name, string description, string ammoType, int amount )
	{
		UniqueId = id;
		DisplayName = name;
		Description = description;
		AmmoType = ammoType;
		AmmoAmount = amount;
		Width = 1;
		Height = 1;
		MaxStack = 3;
		Category = "Ammo";
	}
}
