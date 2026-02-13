
public class PistolAmmoItem : AmmoItemDef
{
	public PistolAmmoItem()
	{
		UniqueId = "ammo_pistol";
		DisplayName = "Pistol Ammo";
		Description = "A box of 9mm pistol rounds.";
		AmmoType = "pistol";
		AmmoAmount = 18;
		Width = 1;
		Height = 1;
		MaxStack = 3;
		Category = "Ammo";
	}
}

public class SMGAmmoItem : AmmoItemDef
{
	public SMGAmmoItem()
	{
		UniqueId = "ammo_smg";
		DisplayName = "SMG Ammo";
		Description = "A box of submachine gun rounds.";
		AmmoType = "smg";
		AmmoAmount = 45;
		Width = 1;
		Height = 1;
		MaxStack = 3;
		Category = "Ammo";
	}
}

public class ShotgunAmmoItem : AmmoItemDef
{
	public ShotgunAmmoItem()
	{
		UniqueId = "ammo_shotgun";
		DisplayName = "Shotgun Shells";
		Description = "A box of 12-gauge shotgun shells.";
		AmmoType = "shotgun";
		AmmoAmount = 8;
		Width = 1;
		Height = 1;
		MaxStack = 3;
		Category = "Ammo";
	}
}

public class MagnumAmmoItem : AmmoItemDef
{
	public MagnumAmmoItem()
	{
		UniqueId = "ammo_357";
		DisplayName = ".357 Ammo";
		Description = "A box of .357 magnum rounds.";
		AmmoType = "357";
		AmmoAmount = 6;
		Width = 1;
		Height = 1;
		MaxStack = 3;
		Category = "Ammo";
	}
}

public class AR2AmmoItem : AmmoItemDef
{
	public AR2AmmoItem()
	{
		UniqueId = "ammo_ar2";
		DisplayName = "AR2 Pulse Rounds";
		Description = "A magazine of Combine pulse rifle ammunition.";
		AmmoType = "ar2";
		AmmoAmount = 30;
		Width = 1;
		Height = 1;
		MaxStack = 3;
		Category = "Ammo";
	}
}

public class CrossbowAmmoItem : AmmoItemDef
{
	public CrossbowAmmoItem()
	{
		UniqueId = "ammo_crossbow";
		DisplayName = "Crossbow Bolts";
		Description = "A bundle of heated rebar crossbow bolts.";
		AmmoType = "crossbow";
		AmmoAmount = 6;
		Width = 1;
		Height = 1;
		MaxStack = 3;
		Category = "Ammo";
	}
}

public class RocketAmmoItem : AmmoItemDef
{
	public RocketAmmoItem()
	{
		UniqueId = "ammo_rocket";
		DisplayName = "Rocket";
		Description = "A single RPG rocket.";
		AmmoType = "rocket";
		AmmoAmount = 1;
		Width = 1;
		Height = 1;
		MaxStack = 3;
		Category = "Ammo";
	}
}
