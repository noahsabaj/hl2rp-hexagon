using Hexagon.Items.Bases;

public class CrowbarWeapon : WeaponItemDef
{
	public CrowbarWeapon()
	{
		UniqueId = "weapon_crowbar";
		DisplayName = "Crowbar";
		Description = "A standard crowbar. Useful for prying things open.";
		AmmoType = "";
		ClipSize = 0;
		Width = 1;
		Height = 2;
		MaxStack = 1;
		Category = "Weapons";
	}
}

public class StunstickWeapon : WeaponItemDef
{
	public StunstickWeapon()
	{
		UniqueId = "weapon_stunstick";
		DisplayName = "Stunstick";
		Description = "A Civil Protection-issued stun baton.";
		AmmoType = "";
		ClipSize = 0;
		Width = 1;
		Height = 2;
		MaxStack = 1;
		Category = "Weapons";
	}
}

public class PistolWeapon : WeaponItemDef
{
	public PistolWeapon()
	{
		UniqueId = "weapon_pistol";
		DisplayName = "Pistol";
		Description = "A standard 9mm pistol.";
		AmmoType = "pistol";
		ClipSize = 18;
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Weapons";
	}
}

public class MagnumWeapon : WeaponItemDef
{
	public MagnumWeapon()
	{
		UniqueId = "weapon_357";
		DisplayName = ".357 Magnum";
		Description = "A powerful .357 revolver.";
		AmmoType = "357";
		ClipSize = 6;
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Weapons";
	}
}

public class SMGWeapon : WeaponItemDef
{
	public SMGWeapon()
	{
		UniqueId = "weapon_smg";
		DisplayName = "SMG";
		Description = "A Heckler & Koch MP7 submachine gun.";
		AmmoType = "smg";
		ClipSize = 45;
		TwoHanded = true;
		Width = 2;
		Height = 1;
		MaxStack = 1;
		Category = "Weapons";
	}
}

public class ShotgunWeapon : WeaponItemDef
{
	public ShotgunWeapon()
	{
		UniqueId = "weapon_shotgun";
		DisplayName = "Shotgun";
		Description = "A SPAS-12 pump-action shotgun.";
		AmmoType = "shotgun";
		ClipSize = 8;
		TwoHanded = true;
		Width = 2;
		Height = 1;
		MaxStack = 1;
		Category = "Weapons";
	}
}

public class CrossbowWeapon : WeaponItemDef
{
	public CrossbowWeapon()
	{
		UniqueId = "weapon_crossbow";
		DisplayName = "Crossbow";
		Description = "A modified crossbow that fires heated rebar.";
		AmmoType = "crossbow";
		ClipSize = 1;
		TwoHanded = true;
		Width = 2;
		Height = 1;
		MaxStack = 1;
		Category = "Weapons";
	}
}

public class AR2Weapon : WeaponItemDef
{
	public AR2Weapon()
	{
		UniqueId = "weapon_ar2";
		DisplayName = "Pulse Rifle";
		Description = "A Combine Overwatch Standard Issue Pulse Rifle.";
		AmmoType = "ar2";
		ClipSize = 30;
		TwoHanded = true;
		Width = 2;
		Height = 1;
		MaxStack = 1;
		Category = "Weapons";
	}
}

public class RPGWeapon : WeaponItemDef
{
	public RPGWeapon()
	{
		UniqueId = "weapon_rpg";
		DisplayName = "RPG";
		Description = "A rocket-propelled grenade launcher.";
		AmmoType = "rocket";
		ClipSize = 1;
		TwoHanded = true;
		Width = 2;
		Height = 2;
		MaxStack = 1;
		Category = "Weapons";
	}
}
