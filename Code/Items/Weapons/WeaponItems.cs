public class WeaponItem : WeaponItemDef
{
	public WeaponItem( string id, string name, string description, string ammoType = "", int clipSize = 0, int width = 1, int height = 1, bool twoHanded = false )
	{
		UniqueId = id;
		DisplayName = name;
		Description = description;
		AmmoType = ammoType;
		ClipSize = clipSize;
		Width = width;
		Height = height;
		TwoHanded = twoHanded;
		MaxStack = 1;
		Category = "Weapons";
	}
}
