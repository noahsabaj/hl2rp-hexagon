
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

		// Attach hook components to framework GameObject for Scene.GetAll<T>() discovery
		var framework = HexagonFramework.Instance;
		if ( framework != null )
		{
			framework.GameObject.GetOrAddComponent<HL2RPHooks>();
			framework.GameObject.GetOrAddComponent<CombineVoiceHook>();
		}

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
		ItemManager.Register( new SimpleConsumable( "water", "Water", "A bottle of clean drinking water.", "Drinking" ) );
		ItemManager.Register( new SimpleConsumable( "water_sparkling", "Sparkling Water", "A bottle of sparkling mineral water.", "Drinking" ) );
		ItemManager.Register( new SimpleConsumable( "water_special", "Special Water", "A bottle of premium purified water.", "Drinking" ) );
		ItemManager.Register( new SimpleConsumable( "supplement", "Supplement", "A standard nutritional supplement.", "Eating" ) );
		ItemManager.Register( new SimpleConsumable( "supplement_large", "Large Supplement", "A larger nutritional supplement with extra calories.", "Eating" ) );
		ItemManager.Register( new SimpleConsumable( "vegetable_oil", "Vegetable Oil", "A bottle of cooking oil.", "Drinking" ) );
		ItemManager.Register( new HealthVialItem() );
		ItemManager.Register( new HealthKitItem() );
		ItemManager.Register( new BleachItem() );

		// Equipment
		ItemManager.Register( new FrequencyDeviceItem( "radio", "Radio", "A handheld frequency-tuned radio for long-range communication.", tunable: true ) );
		ItemManager.Register( new FrequencyDeviceItem( "pager", "Pager", "A small pager that receives radio transmissions." ) );
		ItemManager.Register( new FrequencyDeviceItem( "static_radio", "Static Radio", "A stationary radio with unlimited transmission range.", tunable: true, width: 2 ) );
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
		ItemManager.Register( new AmmoItem( "ammo_pistol", "Pistol Ammo", "A box of 9mm pistol rounds.", "pistol", 18 ) );
		ItemManager.Register( new AmmoItem( "ammo_smg", "SMG Ammo", "A box of submachine gun rounds.", "smg", 45 ) );
		ItemManager.Register( new AmmoItem( "ammo_shotgun", "Shotgun Shells", "A box of 12-gauge shotgun shells.", "shotgun", 8 ) );
		ItemManager.Register( new AmmoItem( "ammo_357", ".357 Ammo", "A box of .357 magnum rounds.", "357", 6 ) );
		ItemManager.Register( new AmmoItem( "ammo_ar2", "AR2 Pulse Rounds", "A magazine of Combine pulse rifle ammunition.", "ar2", 30 ) );
		ItemManager.Register( new AmmoItem( "ammo_crossbow", "Crossbow Bolts", "A bundle of heated rebar crossbow bolts.", "crossbow", 6 ) );
		ItemManager.Register( new AmmoItem( "ammo_rocket", "Rocket", "A single RPG rocket.", "rocket", 1 ) );

		// Weapons
		ItemManager.Register( new WeaponItem( "weapon_crowbar", "Crowbar", "A standard crowbar. Useful for prying things open.", height: 2 ) );
		ItemManager.Register( new WeaponItem( "weapon_stunstick", "Stunstick", "A Civil Protection-issued stun baton.", height: 2 ) );
		ItemManager.Register( new WeaponItem( "weapon_pistol", "Pistol", "A standard 9mm pistol.", "pistol", 18 ) );
		ItemManager.Register( new WeaponItem( "weapon_357", ".357 Magnum", "A powerful .357 revolver.", "357", 6 ) );
		ItemManager.Register( new WeaponItem( "weapon_smg", "SMG", "A Heckler & Koch MP7 submachine gun.", "smg", 45, width: 2, twoHanded: true ) );
		ItemManager.Register( new WeaponItem( "weapon_shotgun", "Shotgun", "A SPAS-12 pump-action shotgun.", "shotgun", 8, width: 2, twoHanded: true ) );
		ItemManager.Register( new WeaponItem( "weapon_crossbow", "Crossbow", "A modified crossbow that fires heated rebar.", "crossbow", 1, width: 2, twoHanded: true ) );
		ItemManager.Register( new WeaponItem( "weapon_ar2", "Pulse Rifle", "A Combine Overwatch Standard Issue Pulse Rifle.", "ar2", 30, width: 2, twoHanded: true ) );
		ItemManager.Register( new WeaponItem( "weapon_rpg", "RPG", "A rocket-propelled grenade launcher.", "rocket", 1, width: 2, height: 2, twoHanded: true ) );

		// Armor
		ItemManager.Register( new RebelArmorItem() );
		ItemManager.Register( new ModifiedRebelArmorItem() );
	}

	private void RegisterFactions()
	{
		RegFaction( "citizen", "Citizen", "Residents of City 17, living under Combine rule.",
			new Color( 0.4f, 0.75f, 0.4f ), 1, isDefault: true );

		RegFaction( "cca", "Civil Protection", "The Combine Civil Authority, tasked with maintaining order in the city.",
			new Color( 0.2f, 0.4f, 0.85f ), 2, globallyRecognized: true );

		RegFaction( "ota", "Overwatch Transhuman Arm", "The Combine's elite military force, deployed for high-threat operations.",
			new Color( 0.85f, 0.2f, 0.2f ), 3, globallyRecognized: true );

		RegFaction( "cityadmin", "City Administration", "The governing body of City 17, appointed by the Combine.",
			new Color( 0.85f, 0.75f, 0.2f ), 4, maxPlayers: 1, startMoney: 5000 );

		RegFaction( "vortigaunt", "Vortigaunt", "Alien beings from the border world Xen, now residing alongside humanity.",
			new Color( 0.5f, 0.85f, 0.5f ), 5 );
	}

	private void RegisterClasses()
	{
		var cpLoadout = new List<LoadoutEntry>
		{
			new LoadoutEntry { ItemDefinitionId = "weapon_stunstick", Count = 1 },
			new LoadoutEntry { ItemDefinitionId = "radio", Count = 1 }
		};

		var otaLoadout = new List<LoadoutEntry>
		{
			new LoadoutEntry { ItemDefinitionId = "radio", Count = 1 }
		};

		// Citizen
		RegClass( "citizen", "Citizen", "An ordinary citizen of City 17.", "citizen", 1 );

		// Civil Protection
		RegClass( "cca_rct", "Recruit", "A newly inducted Civil Protection recruit.", "cca", 1, loadout: cpLoadout );
		RegClass( "cca_unit", "Unit", "A standard Civil Protection unit.", "cca", 2, loadout: cpLoadout );
		RegClass( "cca_epu", "Elite Protection Unit", "An elite Civil Protection operative.", "cca", 3, loadout: cpLoadout );
		RegClass( "cca_cmd", "Commander", "A commanding officer of Civil Protection.", "cca", 4, maxPlayers: 2, loadout: cpLoadout );
		RegClass( "cca_sec", "Sectoral Commander", "The highest ranking Civil Protection officer in the sector.", "cca", 5, maxPlayers: 1, loadout: cpLoadout );

		// Overwatch Transhuman Arm
		RegClass( "ota_ows", "Overwatch Soldier", "A standard transhuman Overwatch soldier.", "ota", 1, loadout: otaLoadout );
		RegClass( "ota_owe", "Overwatch Elite", "An elite transhuman Overwatch operative.", "ota", 2, loadout: otaLoadout );

		// City Administration
		RegClass( "city_administrator", "City Administrator", "The appointed administrator of City 17.", "cityadmin", 1, maxPlayers: 1 );

		// Vortigaunt
		RegClass( "vort_free", "Free Vortigaunt", "A Vortigaunt freed from Combine servitude.", "vortigaunt", 1 );
		RegClass( "vort_enslaved", "Enslaved Vortigaunt", "A Vortigaunt still under Combine control.", "vortigaunt", 2 );
	}

	// --- Registration Helpers ---

	private static void RegFaction( string id, string name, string desc, Color color, int order,
		bool isDefault = false, int maxPlayers = 0, int startMoney = 0, bool globallyRecognized = false )
	{
		var f = TypeLibrary.Create<FactionDefinition>();
		f.UniqueId = id;
		f.Name = name;
		f.Description = desc;
		f.Color = color;
		f.IsDefault = isDefault;
		f.MaxPlayers = maxPlayers;
		f.StartingMoney = startMoney;
		f.IsGloballyRecognized = globallyRecognized;
		f.Order = order;
		FactionManager.Register( f );
	}

	private static void RegClass( string id, string name, string desc, string factionId, int order,
		int maxPlayers = 0, List<LoadoutEntry> loadout = null )
	{
		var c = TypeLibrary.Create<ClassDefinition>();
		c.UniqueId = id;
		c.Name = name;
		c.Description = desc;
		c.FactionId = factionId;
		c.MaxPlayers = maxPlayers;
		c.Order = order;
		if ( loadout != null )
			c.Loadout = loadout;
		FactionManager.RegisterClass( c );
	}
}
