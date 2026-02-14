
[HexPlugin( "HL2RP",
	Description = "Half-Life 2 Roleplay schema for Hexagon",
	Author = "Noah Sabaj",
	Version = "0.1",
	Priority = 10 )]
public class HL2RPPlugin : IHexPlugin
{
	public void OnPluginLoaded()
	{
		HL2RPConfig.Register();
		RegisterFactions();
		RegisterClasses();
		HL2RPCommands.Register();

		// Chat classes
		ChatManager.Register( new RadioChatClass() );
		ChatManager.Register( new DispatchChatClass() );
		ChatManager.Register( new RequestChatClass() );

		RegisterItems();

		Log.Info( "[HL2RP] Schema loaded — all phases initialized." );
	}

	public void OnPluginUnloaded()
	{
		Log.Info( "[HL2RP] Schema unloaded." );
	}

	private void RegisterItems()
	{
		// Identification
		ItemManager.Register( new CIDCardItem() );

		// Consumables
		ItemManager.Register( new RationItem() );
		ItemManager.Register( new WaterItem() );
		ItemManager.Register( new SparklingWaterItem() );
		ItemManager.Register( new SpecialWaterItem() );
		ItemManager.Register( new SupplementItem() );
		ItemManager.Register( new LargeSupplementItem() );
		ItemManager.Register( new HealthVialItem() );
		ItemManager.Register( new HealthKitItem() );
		ItemManager.Register( new BleachItem() );
		ItemManager.Register( new VegetableOilItem() );

		// Equipment
		ItemManager.Register( new RadioItem() );
		ItemManager.Register( new PagerItem() );
		ItemManager.Register( new StaticRadioItem() );
		ItemManager.Register( new RequestDeviceItem() );
		ItemManager.Register( new FlashlightItem() );
		ItemManager.Register( new SpraycanItem() );

		// Bags
		ItemManager.Register( new SmallBagItem() );
		ItemManager.Register( new LargeBagItem() );
		ItemManager.Register( new SuitcaseItem() );

		// Permits
		ItemManager.Register( new PermitItem( "food", "Food Permit", "Allows the sale of food and consumable items." ) );
		ItemManager.Register( new PermitItem( "elec", "Electronics Permit", "Allows the sale of electronic devices." ) );
		ItemManager.Register( new PermitItem( "lit", "Literature Permit", "Allows the sale of written materials." ) );
		ItemManager.Register( new PermitItem( "misc", "Miscellaneous Permit", "Allows the sale of miscellaneous goods." ) );

		// Misc
		ItemManager.Register( new NoteItem() );
		ItemManager.Register( new BookItem( "book_blackcat", "The Black Cat", "A short story by Edgar Allan Poe.", "The Black Cat - a tale of guilt and madness..." ) );
		ItemManager.Register( new ZipTieItem() );

		// Ammo
		ItemManager.Register( new PistolAmmoItem() );
		ItemManager.Register( new SMGAmmoItem() );
		ItemManager.Register( new ShotgunAmmoItem() );
		ItemManager.Register( new MagnumAmmoItem() );
		ItemManager.Register( new AR2AmmoItem() );
		ItemManager.Register( new CrossbowAmmoItem() );
		ItemManager.Register( new RocketAmmoItem() );

		// Weapons
		ItemManager.Register( new CrowbarWeapon() );
		ItemManager.Register( new StunstickWeapon() );
		ItemManager.Register( new PistolWeapon() );
		ItemManager.Register( new MagnumWeapon() );
		ItemManager.Register( new SMGWeapon() );
		ItemManager.Register( new ShotgunWeapon() );
		ItemManager.Register( new CrossbowWeapon() );
		ItemManager.Register( new AR2Weapon() );
		ItemManager.Register( new RPGWeapon() );

		// Armor
		ItemManager.Register( new RebelArmorItem() );
		ItemManager.Register( new ModifiedRebelArmorItem() );
	}

	private void RegisterFactions()
	{
		var citizen = new FactionDefinition();
		citizen.UniqueId = "citizen";
		citizen.Name = "Citizen";
		citizen.Description = "Residents of City 17, living under Combine rule.";
		citizen.IsDefault = true;
		citizen.MaxPlayers = 0;
		citizen.Color = new Color( 0.4f, 0.75f, 0.4f );
		citizen.StartingMoney = 0;
		citizen.Order = 1;
		FactionManager.Register( citizen );

		var cca = new FactionDefinition();
		cca.UniqueId = "cca";
		cca.Name = "Civil Protection";
		cca.Description = "The Combine Civil Authority, tasked with maintaining order in the city.";
		cca.IsDefault = false;
		cca.MaxPlayers = 0;
		cca.Color = new Color( 0.2f, 0.4f, 0.85f );
		cca.StartingMoney = 0;
		cca.IsGloballyRecognized = true;
		cca.Order = 2;
		FactionManager.Register( cca );

		var ota = new FactionDefinition();
		ota.UniqueId = "ota";
		ota.Name = "Overwatch Transhuman Arm";
		ota.Description = "The Combine's elite military force, deployed for high-threat operations.";
		ota.IsDefault = false;
		ota.MaxPlayers = 0;
		ota.Color = new Color( 0.85f, 0.2f, 0.2f );
		ota.StartingMoney = 0;
		ota.IsGloballyRecognized = true;
		ota.Order = 3;
		FactionManager.Register( ota );

		var admin = new FactionDefinition();
		admin.UniqueId = "cityadmin";
		admin.Name = "City Administration";
		admin.Description = "The governing body of City 17, appointed by the Combine.";
		admin.IsDefault = false;
		admin.MaxPlayers = 1;
		admin.Color = new Color( 0.85f, 0.75f, 0.2f );
		admin.StartingMoney = 5000;
		admin.Order = 4;
		FactionManager.Register( admin );

		var vort = new FactionDefinition();
		vort.UniqueId = "vortigaunt";
		vort.Name = "Vortigaunt";
		vort.Description = "Alien beings from the border world Xen, now residing alongside humanity.";
		vort.IsDefault = false;
		vort.MaxPlayers = 0;
		vort.Color = new Color( 0.5f, 0.85f, 0.5f );
		vort.StartingMoney = 0;
		vort.Order = 5;
		FactionManager.Register( vort );
	}

	private void RegisterClasses()
	{
		// Citizen
		var citizenClass = new ClassDefinition();
		citizenClass.UniqueId = "citizen";
		citizenClass.Name = "Citizen";
		citizenClass.Description = "An ordinary citizen of City 17.";
		citizenClass.FactionId = "citizen";
		citizenClass.MaxPlayers = 0;
		citizenClass.Order = 1;
		FactionManager.RegisterClass( citizenClass );

		// Civil Protection
		var rct = new ClassDefinition();
		rct.UniqueId = "cca_rct";
		rct.Name = "Recruit";
		rct.Description = "A newly inducted Civil Protection recruit.";
		rct.FactionId = "cca";
		rct.MaxPlayers = 0;
		rct.Order = 1;
		rct.Loadout = new List<LoadoutEntry>
		{
			new LoadoutEntry { ItemDefinitionId = "weapon_stunstick", Count = 1 },
			new LoadoutEntry { ItemDefinitionId = "radio", Count = 1 }
		};
		FactionManager.RegisterClass( rct );

		var unit = new ClassDefinition();
		unit.UniqueId = "cca_unit";
		unit.Name = "Unit";
		unit.Description = "A standard Civil Protection unit.";
		unit.FactionId = "cca";
		unit.MaxPlayers = 0;
		unit.Order = 2;
		unit.Loadout = new List<LoadoutEntry>
		{
			new LoadoutEntry { ItemDefinitionId = "weapon_stunstick", Count = 1 },
			new LoadoutEntry { ItemDefinitionId = "radio", Count = 1 }
		};
		FactionManager.RegisterClass( unit );

		var epu = new ClassDefinition();
		epu.UniqueId = "cca_epu";
		epu.Name = "Elite Protection Unit";
		epu.Description = "An elite Civil Protection operative.";
		epu.FactionId = "cca";
		epu.MaxPlayers = 0;
		epu.Order = 3;
		epu.Loadout = new List<LoadoutEntry>
		{
			new LoadoutEntry { ItemDefinitionId = "weapon_stunstick", Count = 1 },
			new LoadoutEntry { ItemDefinitionId = "radio", Count = 1 }
		};
		FactionManager.RegisterClass( epu );

		var cmd = new ClassDefinition();
		cmd.UniqueId = "cca_cmd";
		cmd.Name = "Commander";
		cmd.Description = "A commanding officer of Civil Protection.";
		cmd.FactionId = "cca";
		cmd.MaxPlayers = 2;
		cmd.Order = 4;
		cmd.Loadout = new List<LoadoutEntry>
		{
			new LoadoutEntry { ItemDefinitionId = "weapon_stunstick", Count = 1 },
			new LoadoutEntry { ItemDefinitionId = "radio", Count = 1 }
		};
		FactionManager.RegisterClass( cmd );

		var sec = new ClassDefinition();
		sec.UniqueId = "cca_sec";
		sec.Name = "Sectoral Commander";
		sec.Description = "The highest ranking Civil Protection officer in the sector.";
		sec.FactionId = "cca";
		sec.MaxPlayers = 1;
		sec.Order = 5;
		sec.Loadout = new List<LoadoutEntry>
		{
			new LoadoutEntry { ItemDefinitionId = "weapon_stunstick", Count = 1 },
			new LoadoutEntry { ItemDefinitionId = "radio", Count = 1 }
		};
		FactionManager.RegisterClass( sec );

		// Overwatch Transhuman Arm
		var ows = new ClassDefinition();
		ows.UniqueId = "ota_ows";
		ows.Name = "Overwatch Soldier";
		ows.Description = "A standard transhuman Overwatch soldier.";
		ows.FactionId = "ota";
		ows.MaxPlayers = 0;
		ows.Order = 1;
		ows.Loadout = new List<LoadoutEntry>
		{
			new LoadoutEntry { ItemDefinitionId = "radio", Count = 1 }
		};
		FactionManager.RegisterClass( ows );

		var owe = new ClassDefinition();
		owe.UniqueId = "ota_owe";
		owe.Name = "Overwatch Elite";
		owe.Description = "An elite transhuman Overwatch operative.";
		owe.FactionId = "ota";
		owe.MaxPlayers = 0;
		owe.Order = 2;
		owe.Loadout = new List<LoadoutEntry>
		{
			new LoadoutEntry { ItemDefinitionId = "radio", Count = 1 }
		};
		FactionManager.RegisterClass( owe );

		// City Administration
		var cityAdmin = new ClassDefinition();
		cityAdmin.UniqueId = "city_administrator";
		cityAdmin.Name = "City Administrator";
		cityAdmin.Description = "The appointed administrator of City 17.";
		cityAdmin.FactionId = "cityadmin";
		cityAdmin.MaxPlayers = 1;
		cityAdmin.Order = 1;
		FactionManager.RegisterClass( cityAdmin );

		// Vortigaunt
		var freeVort = new ClassDefinition();
		freeVort.UniqueId = "vort_free";
		freeVort.Name = "Free Vortigaunt";
		freeVort.Description = "A Vortigaunt freed from Combine servitude.";
		freeVort.FactionId = "vortigaunt";
		freeVort.MaxPlayers = 0;
		freeVort.Order = 1;
		FactionManager.RegisterClass( freeVort );

		var enslaved = new ClassDefinition();
		enslaved.UniqueId = "vort_enslaved";
		enslaved.Name = "Enslaved Vortigaunt";
		enslaved.Description = "A Vortigaunt still under Combine control.";
		enslaved.FactionId = "vortigaunt";
		enslaved.MaxPlayers = 0;
		enslaved.Order = 2;
		FactionManager.RegisterClass( enslaved );
	}
}
