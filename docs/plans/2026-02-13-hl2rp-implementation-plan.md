# HL2RP Full Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Port the complete NutScript HL2RP schema to Hexagon/s&box — all factions, classes, items, entities, chat, commands, UI, and systems.

**Architecture:** Schema plugin (`HL2RPPlugin`) on top of Hexagon framework. Core systems in main plugin, self-contained features as sub-plugins. Asset-agnostic — all model/sound paths are configurable via `HexConfig`.

**Tech Stack:** C# / .NET 10 / s&box / Hexagon framework / Razor UI components

**Testing:** s&box has no unit test framework. Verification is: compile check (s&box auto-compiles on save) → run scene → check console output. Each phase ends with a verification step.

**Coding Conventions:** Tabs for indentation, Allman braces, spaces inside parentheses: `Method( param )`, `if ( condition )`.

**Reference:** Design doc at `docs/plans/2026-02-13-hl2rp-full-design.md`. NutScript HL2RP at `D:\SteamLibrary\steamapps\common\GarrysMod\garrysmod\gamemodes\hl2rp`.

**Hexagon Source:** `C:\sboxprojects\hexagon\Code` — consult for exact API signatures.

---

## Phase 1: Core Schema Enhancement

### Task 1: Expand HL2RPCharacter

**Files:**
- Modify: `Code/HL2RPCharacter.cs`

**Step 1: Add new CharVars**

```csharp

public class HL2RPCharacter : HexCharacterData
{
	[CharVar( Default = "John Doe", MinLength = 3, MaxLength = 64, Order = 1, ShowInCreation = true )]
	public string Name { get; set; }

	[CharVar( Default = "A citizen of City 17.", MinLength = 16, MaxLength = 512, Order = 2, ShowInCreation = true )]
	public string Description { get; set; }

	[CharVar( Order = 3, ShowInCreation = true )]
	public string Model { get; set; }

	[CharVar( Local = true, Default = 0 )]
	public int Money { get; set; }

	[CharVar( Local = true, Default = "" )]
	public string CharData { get; set; }

	[CharVar( Local = true, Default = 0 )]
	public int CIDNumber { get; set; }

	[CharVar( Local = true, Default = 0 )]
	public int Armor { get; set; }
}
```

- `CharData`: freeform text field (750 char limit enforced in commands), default empty. Combine officers write notes/infractions here.
- `CIDNumber`: auto-assigned on citizen character creation (random 10000-99999). 0 means no CID.
- `Armor`: set by loadout system on spawn. 0-100 scale.

**Step 2: Commit**

```
git add Code/HL2RPCharacter.cs
git commit -m "feat: expand HL2RPCharacter with CharData, CIDNumber, Armor"
```

---

### Task 2: Create HL2RPConfig

**Files:**
- Create: `Code/HL2RPConfig.cs`

**Step 1: Create config registration class**

```csharp

public static class HL2RPConfig
{
	public static void Register()
	{
		// Rank model paths (placeholder values - swap for real assets later)
		HexConfig.Add( "hl2rp.model.citizen", "models/citizen/male_01.vmdl", "Default citizen model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.cp.rct", "models/police.vmdl", "CP Recruit model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.cp.unit", "models/police.vmdl", "CP Unit model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.cp.ofc", "models/police.vmdl", "CP Officer model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.cp.epu", "models/police.vmdl", "CP Elite model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.cp.dvl", "models/police.vmdl", "CP Divisional model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.cp.sec", "models/police.vmdl", "CP Sectoral model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.cp.scn", "models/scanner.vmdl", "CP Scanner model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.ow.ows", "models/combine_soldier.vmdl", "OW Soldier model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.ow.owe", "models/combine_soldier.vmdl", "OW Elite model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.ow.opg", "models/combine_soldier.vmdl", "OW Prison Guard model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.ow.sgs", "models/combine_soldier.vmdl", "OW Shotgunner model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.ow.spg", "models/combine_soldier.vmdl", "OW Special Guard model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.admin", "models/breen.vmdl", "City Administrator model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.vortigaunt", "models/vortigaunt.vmdl", "Vortigaunt model", "HL2RP Models" );

		// Faction pay rates (per payment period)
		HexConfig.Add( "hl2rp.pay.citizen", 0, "Citizen pay per period", "HL2RP Economy" );
		HexConfig.Add( "hl2rp.pay.cca", 25, "Civil Protection pay per period", "HL2RP Economy" );
		HexConfig.Add( "hl2rp.pay.ota", 30, "Overwatch pay per period", "HL2RP Economy" );
		HexConfig.Add( "hl2rp.pay.cityadmin", 40, "City Admin pay per period", "HL2RP Economy" );
		HexConfig.Add( "hl2rp.pay.vortigaunt", 0, "Vortigaunt pay per period", "HL2RP Economy" );

		// Gameplay
		HexConfig.Add( "hl2rp.dispenser.cooldown", 300f, "Ration dispenser cooldown in seconds", "HL2RP Gameplay" );
		HexConfig.Add( "hl2rp.request.cooldown", 5f, "Request device cooldown in seconds", "HL2RP Gameplay" );
		HexConfig.Add( "hl2rp.chardata.maxlength", 750, "Max length of character data notes", "HL2RP Gameplay" );

		// Chat
		HexConfig.Add( "hl2rp.chat.radioRange", 280f, "Radio chat physical range", "HL2RP Chat" );
		HexConfig.Add( "hl2rp.chat.dispatchColor", new Color( 0.75f, 0.22f, 0.17f ), "Dispatch chat color", "HL2RP Chat" );
		HexConfig.Add( "hl2rp.chat.radioColor", new Color( 0.3f, 0.5f, 0.9f ), "Radio chat color", "HL2RP Chat" );
		HexConfig.Add( "hl2rp.chat.requestColor", new Color( 0.9f, 0.7f, 0.2f ), "Request chat color", "HL2RP Chat" );

		// Combine
		HexConfig.Add( "hl2rp.combine.cpPrefix", "CP-", "Civil Protection name prefix", "HL2RP Combine" );
		HexConfig.Add( "hl2rp.combine.owPrefix", "OT-", "Overwatch name prefix", "HL2RP Combine" );
		HexConfig.Add( "hl2rp.combine.digitLength", 5, "Number of digits in combine ID", "HL2RP Combine" );
		HexConfig.Add( "hl2rp.combine.cpArmor", 50, "CP spawn armor percentage", "HL2RP Combine" );
		HexConfig.Add( "hl2rp.combine.owArmor", 100, "OW spawn armor percentage", "HL2RP Combine" );

		// Sounds (placeholder paths)
		HexConfig.Add( "hl2rp.sound.radioOn.cp", "sounds/radio/cp_on.vsnd", "CP radio on beep", "HL2RP Sounds" );
		HexConfig.Add( "hl2rp.sound.radioOff.cp", "sounds/radio/cp_off.vsnd", "CP radio off beep", "HL2RP Sounds" );
		HexConfig.Add( "hl2rp.sound.radioOn.ow", "sounds/radio/ow_on.vsnd", "OW radio on beep", "HL2RP Sounds" );
		HexConfig.Add( "hl2rp.sound.radioOff.ow", "sounds/radio/ow_off.vsnd", "OW radio off beep", "HL2RP Sounds" );
	}
}
```

**Step 2: Commit**

```
git add Code/HL2RPConfig.cs
git commit -m "feat: add HL2RPConfig with all configurable values"
```

---

### Task 3: Create CombineUtils

**Files:**
- Create: `Code/Systems/CombineUtils.cs`

**Step 1: Implement static helper class**

```csharp
using System.Text.RegularExpressions;

public static class CombineUtils
{
	private static readonly string[] EliteRanks = { "EpU", "DvL", "SeC", "SCN", "CLAW.SCN" };
	private static readonly string[] CPRanks = { "RCT", "05", "04", "03", "02", "01", "OfC", "EpU", "DvL", "SeC", "SCN", "CLAW.SCN" };
	private static readonly string[] OWRanks = { "OWS", "OWE", "OPG", "SGS", "SPG" };

	/// <summary>
	/// Returns true if the faction ID belongs to a Combine faction (CCA or OTA).
	/// </summary>
	public static bool IsCombineFaction( string factionId )
	{
		return factionId == "cca" || factionId == "ota";
	}

	/// <summary>
	/// Returns true if the character belongs to a Combine faction.
	/// </summary>
	public static bool IsCombine( HexCharacter character )
	{
		return IsCombineFaction( character.Faction );
	}

	/// <summary>
	/// Returns true if the character is Civil Protection.
	/// </summary>
	public static bool IsCP( HexCharacter character )
	{
		return character.Faction == "cca";
	}

	/// <summary>
	/// Returns true if the character is Overwatch.
	/// </summary>
	public static bool IsOverwatch( HexCharacter character )
	{
		return character.Faction == "ota";
	}

	/// <summary>
	/// Extracts the rank tag from a Combine character name.
	/// CP names: "CP-RCT.12345" -> "RCT"
	/// OW names: "OT-OWS.54321" -> "OWS"
	/// Returns null if not a valid Combine name format.
	/// </summary>
	public static string GetCombineRank( HexCharacter character )
	{
		if ( character == null || character.Data == null )
			return null;

		var data = (HL2RPCharacter)character.Data;
		var name = data.Name;

		if ( string.IsNullOrEmpty( name ) )
			return null;

		var cpPrefix = HexConfig.Get<string>( "hl2rp.combine.cpPrefix", "CP-" );
		var owPrefix = HexConfig.Get<string>( "hl2rp.combine.owPrefix", "OT-" );

		string prefix = null;
		if ( name.StartsWith( cpPrefix ) )
			prefix = cpPrefix;
		else if ( name.StartsWith( owPrefix ) )
			prefix = owPrefix;

		if ( prefix == null )
			return null;

		var remainder = name.Substring( prefix.Length );
		var dotIndex = remainder.IndexOf( '.' );
		if ( dotIndex <= 0 )
			return null;

		return remainder.Substring( 0, dotIndex );
	}

	/// <summary>
	/// Extracts the trailing digit ID from a Combine character name.
	/// "CP-RCT.54321" -> "54321"
	/// Returns null if not a valid format.
	/// </summary>
	public static string GetDigits( HexCharacter character )
	{
		if ( character == null || character.Data == null )
			return null;

		var data = (HL2RPCharacter)character.Data;
		var name = data.Name;

		if ( string.IsNullOrEmpty( name ) )
			return null;

		var dotIndex = name.LastIndexOf( '.' );
		if ( dotIndex < 0 || dotIndex >= name.Length - 1 )
			return null;

		return name.Substring( dotIndex + 1 );
	}

	/// <summary>
	/// Returns true if the rank is an elite/dispatch-eligible rank.
	/// </summary>
	public static bool IsEliteRank( string rank )
	{
		if ( string.IsNullOrEmpty( rank ) )
			return false;

		return EliteRanks.Contains( rank );
	}

	/// <summary>
	/// Returns true if the character can use the dispatch channel.
	/// Must be Combine with elite rank (EpU, DvL, SeC) or Scanner rank (SCN, CLAW.SCN).
	/// </summary>
	public static bool IsDispatch( HexCharacter character )
	{
		if ( !IsCombine( character ) )
			return false;

		var rank = GetCombineRank( character );
		return IsEliteRank( rank );
	}

	/// <summary>
	/// Returns true if the given rank string is a valid CP rank.
	/// </summary>
	public static bool IsValidCPRank( string rank )
	{
		return CPRanks.Contains( rank );
	}

	/// <summary>
	/// Returns true if the given rank string is a valid OW rank.
	/// </summary>
	public static bool IsValidOWRank( string rank )
	{
		return OWRanks.Contains( rank );
	}

	/// <summary>
	/// Gets the config key for the model path for a given faction and rank.
	/// Returns the config key string (not the model path itself).
	/// </summary>
	public static string GetModelConfigKey( string factionId, string rank )
	{
		if ( factionId == "cca" )
		{
			return rank?.ToLower() switch
			{
				"rct" => "hl2rp.model.cp.rct",
				"05" or "04" or "03" or "02" or "01" => "hl2rp.model.cp.unit",
				"ofc" => "hl2rp.model.cp.ofc",
				"epu" => "hl2rp.model.cp.epu",
				"dvl" => "hl2rp.model.cp.dvl",
				"sec" => "hl2rp.model.cp.sec",
				"scn" or "claw.scn" => "hl2rp.model.cp.scn",
				_ => "hl2rp.model.cp.rct"
			};
		}

		if ( factionId == "ota" )
		{
			return rank?.ToUpper() switch
			{
				"OWS" => "hl2rp.model.ow.ows",
				"OWE" => "hl2rp.model.ow.owe",
				"OPG" => "hl2rp.model.ow.opg",
				"SGS" => "hl2rp.model.ow.sgs",
				"SPG" => "hl2rp.model.ow.spg",
				_ => "hl2rp.model.ow.ows"
			};
		}

		if ( factionId == "cityadmin" )
			return "hl2rp.model.admin";

		if ( factionId == "vortigaunt" )
			return "hl2rp.model.vortigaunt";

		return "hl2rp.model.citizen";
	}
}
```

**Step 2: Commit**

```
git add Code/Systems/CombineUtils.cs
git commit -m "feat: add CombineUtils static helper class"
```

---

### Task 4: Create RankSystem

**Files:**
- Create: `Code/Systems/RankSystem.cs`

**Step 1: Implement rank-to-model resolution**

The RankSystem resolves a character's model based on their faction and rank. It's called by the hooks component when a character loads.

```csharp

/// <summary>
/// Resolves character models based on faction and rank.
/// Combine characters get models assigned by their rank tag parsed from their name.
/// Other factions use their default faction model.
/// </summary>
public static class RankSystem
{
	/// <summary>
	/// Gets the appropriate model path for a character based on faction and rank.
	/// </summary>
	public static string GetModelForCharacter( HexCharacter character )
	{
		var factionId = character.Faction;

		if ( CombineUtils.IsCombineFaction( factionId ) )
		{
			var rank = CombineUtils.GetCombineRank( character );
			var configKey = CombineUtils.GetModelConfigKey( factionId, rank );
			return HexConfig.Get<string>( configKey, "" );
		}

		var defaultKey = CombineUtils.GetModelConfigKey( factionId, null );
		return HexConfig.Get<string>( defaultKey, "" );
	}

	/// <summary>
	/// Applies the rank-appropriate model to the character.
	/// Updates both the character data and the player component's synced model.
	/// </summary>
	public static void ApplyRankModel( HexPlayerComponent player, HexCharacter character )
	{
		var model = GetModelForCharacter( character );
		if ( string.IsNullOrEmpty( model ) )
			return;

		var data = (HL2RPCharacter)character.Data;
		data.Model = model;
		player.CharacterModel = model;
		character.MarkDirty( "Model" );
	}

	/// <summary>
	/// Validates a Combine character name format.
	/// Returns null if valid, or an error message if invalid.
	/// </summary>
	public static string ValidateCombineName( string name, string factionId )
	{
		var cpPrefix = HexConfig.Get<string>( "hl2rp.combine.cpPrefix", "CP-" );
		var owPrefix = HexConfig.Get<string>( "hl2rp.combine.owPrefix", "OT-" );
		var digitLength = HexConfig.Get<int>( "hl2rp.combine.digitLength", 5 );

		string expectedPrefix;
		string[] validRanks;

		if ( factionId == "cca" )
		{
			expectedPrefix = cpPrefix;
			validRanks = new[] { "RCT", "05", "04", "03", "02", "01", "OfC", "EpU", "DvL", "SeC", "SCN", "CLAW.SCN" };
		}
		else if ( factionId == "ota" )
		{
			expectedPrefix = owPrefix;
			validRanks = new[] { "OWS", "OWE", "OPG", "SGS", "SPG" };
		}
		else
		{
			return null;
		}

		if ( !name.StartsWith( expectedPrefix ) )
			return $"Combine name must start with '{expectedPrefix}'.";

		var remainder = name.Substring( expectedPrefix.Length );
		var dotIndex = remainder.IndexOf( '.' );
		if ( dotIndex <= 0 )
			return $"Combine name must follow format: {expectedPrefix}RANK.DIGITS";

		var rank = remainder.Substring( 0, dotIndex );
		if ( !validRanks.Contains( rank ) )
			return $"Invalid rank '{rank}'. Valid ranks: {string.Join( ", ", validRanks )}";

		var digits = remainder.Substring( dotIndex + 1 );
		if ( digits.Length != digitLength || !digits.All( char.IsDigit ) )
			return $"Combine ID must be exactly {digitLength} digits.";

		return null;
	}
}
```

**Step 2: Commit**

```
git add Code/Systems/RankSystem.cs
git commit -m "feat: add RankSystem for faction-based model resolution"
```

---

### Task 5: Create CIDSystem

**Files:**
- Create: `Code/Systems/CIDSystem.cs`

**Step 1: Implement CID generation and management**

```csharp

/// <summary>
/// Manages Citizen ID numbers. Auto-generates CID on citizen character creation.
/// CID numbers are stored on the character data; CID cards are inventory items.
/// </summary>
public static class CIDSystem
{
	private static readonly Random _random = new();

	/// <summary>
	/// Generates a random 5-digit CID number (10000-99999).
	/// </summary>
	public static int GenerateCIDNumber()
	{
		return _random.Next( 10000, 100000 );
	}

	/// <summary>
	/// Assigns a CID number to a character and creates a CID card item in their inventory.
	/// Called automatically on citizen character creation.
	/// </summary>
	public static void AssignCID( HexPlayerComponent player, HexCharacter character )
	{
		var data = (HL2RPCharacter)character.Data;
		var cidNumber = GenerateCIDNumber();
		data.CIDNumber = cidNumber;
		character.MarkDirty( "CIDNumber" );

		// Create CID card item in player's inventory
		var cidItem = ItemManager.CreateInstance( "cid_card", character.Id, new Dictionary<string, object>
		{
			{ "name", data.Name },
			{ "id", cidNumber },
			{ "cwu", false }
		} );

		var inventories = InventoryManager.LoadForCharacter( character.Id );
		if ( inventories.Count > 0 )
		{
			inventories[0].Add( cidItem );
		}

		Log.Info( $"[HL2RP] Assigned CID #{cidNumber} to {data.Name}" );
	}

	/// <summary>
	/// Checks if a character has a valid CID (number > 0).
	/// </summary>
	public static bool HasCID( HexCharacter character )
	{
		var data = (HL2RPCharacter)character.Data;
		return data.CIDNumber > 0;
	}

	/// <summary>
	/// Gets the CID number for a character.
	/// </summary>
	public static int GetCIDNumber( HexCharacter character )
	{
		var data = (HL2RPCharacter)character.Data;
		return data.CIDNumber;
	}

	/// <summary>
	/// Sets the CWU priority flag on a player's CID card item.
	/// Returns true if successful, false if no CID card found.
	/// </summary>
	public static bool SetPriority( HexCharacter character, bool priority )
	{
		var inventories = InventoryManager.LoadForCharacter( character.Id );
		foreach ( var inv in inventories )
		{
			var cidItem = inv.FindItem( "cid_card" );
			if ( cidItem != null )
			{
				cidItem.SetData( "cwu", priority );
				cidItem.MarkDirty();
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Checks if a character has CWU priority status.
	/// </summary>
	public static bool HasPriority( HexCharacter character )
	{
		var inventories = InventoryManager.LoadForCharacter( character.Id );
		foreach ( var inv in inventories )
		{
			var cidItem = inv.FindItem( "cid_card" );
			if ( cidItem != null )
			{
				return cidItem.GetData<bool>( "cwu", false );
			}
		}
		return false;
	}
}
```

**Step 2: Commit**

```
git add Code/Systems/CIDSystem.cs
git commit -m "feat: add CIDSystem for citizen ID management"
```

---

### Task 6: Create LoadoutSystem

**Files:**
- Create: `Code/Systems/LoadoutSystem.cs`

**Step 1: Implement per-faction spawn loadout**

```csharp

/// <summary>
/// Handles per-faction spawn loadouts. When a character loads, gives them
/// their faction-appropriate items, weapons, and armor.
/// </summary>
public static class LoadoutSystem
{
	/// <summary>
	/// Applies the loadout for a character based on their faction.
	/// Called when a character is loaded.
	/// </summary>
	public static void ApplyLoadout( HexPlayerComponent player, HexCharacter character )
	{
		var data = (HL2RPCharacter)character.Data;
		var factionId = character.Faction;

		switch ( factionId )
		{
			case "cca":
				ApplyCPLoadout( player, character, data );
				break;
			case "ota":
				ApplyOWLoadout( player, character, data );
				break;
			case "citizen":
			case "cityadmin":
			case "vortigaunt":
			default:
				data.Armor = 0;
				character.MarkDirty( "Armor" );
				break;
		}
	}

	private static void ApplyCPLoadout( HexPlayerComponent player, HexCharacter character, HL2RPCharacter data )
	{
		data.Armor = HexConfig.Get<int>( "hl2rp.combine.cpArmor", 50 );
		character.MarkDirty( "Armor" );

		// Give stunstick if not already owned
		EnsureItem( character, "weapon_stunstick" );

		// Give radio if not already owned
		EnsureItem( character, "radio", new Dictionary<string, object>
		{
			{ "frequency", "100.0" }
		} );
	}

	private static void ApplyOWLoadout( HexPlayerComponent player, HexCharacter character, HL2RPCharacter data )
	{
		data.Armor = HexConfig.Get<int>( "hl2rp.combine.owArmor", 100 );
		character.MarkDirty( "Armor" );

		// Give radio if not already owned
		EnsureItem( character, "radio", new Dictionary<string, object>
		{
			{ "frequency", "100.0" }
		} );
	}

	/// <summary>
	/// Ensures a character has at least one instance of the given item.
	/// If they already have one, does nothing.
	/// </summary>
	private static void EnsureItem( HexCharacter character, string definitionId, Dictionary<string, object> data = null )
	{
		var inventories = InventoryManager.LoadForCharacter( character.Id );
		foreach ( var inv in inventories )
		{
			if ( inv.HasItem( definitionId ) )
				return;
		}

		// Item not found, create and add to first inventory
		if ( inventories.Count > 0 )
		{
			var item = ItemManager.CreateInstance( definitionId, character.Id, data );
			inventories[0].Add( item );
		}
	}
}
```

**Step 2: Commit**

```
git add Code/Systems/LoadoutSystem.cs
git commit -m "feat: add LoadoutSystem for per-faction spawn gear"
```

---

### Task 7: Create HL2RPHooks Component

**Files:**
- Create: `Code/HL2RPHooks.cs`

**Step 1: Implement character lifecycle hooks**

This is a Component that lives in the scene (user must add it via editor). It hooks into Hexagon's event system to run HL2RP logic on character create/load/unload.

```csharp

/// <summary>
/// Core HL2RP lifecycle hooks. Attach this Component to a GameObject in the scene
/// alongside HexagonFramework. Handles character creation, loading, and unloading.
/// </summary>
public class HL2RPHooks : Component,
	ICharacterCreatedListener,
	ICharacterLoadedListener,
	ICharacterUnloadedListener
{
	public void OnCharacterCreated( HexPlayerComponent player, HexCharacter character )
	{
		var data = (HL2RPCharacter)character.Data;
		var factionId = character.Faction;

		// Initialize CharData with default template
		if ( string.IsNullOrEmpty( data.CharData ) )
		{
			data.CharData = "Points:\nInfractions:\n";
			character.MarkDirty( "CharData" );
		}

		// Citizens get a CID on creation
		if ( factionId == "citizen" )
		{
			CIDSystem.AssignCID( player, character );
		}

		// Combine get a radio on creation
		if ( CombineUtils.IsCombineFaction( factionId ) )
		{
			LoadoutSystem.ApplyLoadout( player, character );
		}

		Log.Info( $"[HL2RP] Character created: {data.Name} ({factionId})" );
	}

	public void OnCharacterLoaded( HexPlayerComponent player, HexCharacter character )
	{
		// Apply rank-based model for combine
		if ( CombineUtils.IsCombine( character ) )
		{
			RankSystem.ApplyRankModel( player, character );
		}

		// Apply faction loadout (armor, ensure items)
		LoadoutSystem.ApplyLoadout( player, character );

		var data = (HL2RPCharacter)character.Data;
		Log.Info( $"[HL2RP] Character loaded: {data.Name} (faction: {character.Faction})" );
	}

	public void OnCharacterUnloaded( HexPlayerComponent player, HexCharacter character )
	{
		var data = (HL2RPCharacter)character.Data;
		Log.Info( $"[HL2RP] Character unloaded: {data.Name}" );
	}
}
```

**Step 2: Commit**

```
git add Code/HL2RPHooks.cs
git commit -m "feat: add HL2RPHooks component for character lifecycle"
```

---

### Task 8: Register HL2RP Commands

**Files:**
- Create: `Code/HL2RPCommands.cs`

**Step 1: Implement /data, /objectives, /setpriority commands**

```csharp

/// <summary>
/// Registers all HL2RP-specific commands.
/// </summary>
public static class HL2RPCommands
{
	private static string _serverObjectives = "";

	public static void Register()
	{
		RegisterDataCommand();
		RegisterObjectivesCommand();
		RegisterSetPriorityCommand();
	}

	/// <summary>
	/// /data <player> — View or edit a citizen's CharData notes. Combine only.
	/// </summary>
	private static void RegisterDataCommand()
	{
		CommandManager.Register( new HexCommand
		{
			Name = "data",
			Description = "View or edit a citizen's data notes.",
			Arguments = new[]
			{
				Arg.Player( "target" ),
				Arg.Optional( Arg.String( "text", remainder: true ) )
			},
			PermissionFunc = ( caller ) =>
			{
				return caller.Character != null && CombineUtils.IsCombine( caller.Character );
			},
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "target" );
				if ( target?.Character == null )
					return "Target has no active character.";

				var targetData = (HL2RPCharacter)target.Character.Data;

				if ( ctx.Has( "text" ) )
				{
					var text = ctx.Get<string>( "text" );
					var maxLength = HexConfig.Get<int>( "hl2rp.chardata.maxlength", 750 );
					if ( text.Length > maxLength )
						return $"Data text exceeds maximum length of {maxLength} characters.";

					targetData.CharData = text;
					target.Character.MarkDirty( "CharData" );
					return $"Updated data for {targetData.Name}.";
				}

				var charData = string.IsNullOrEmpty( targetData.CharData )
					? "Points:\nInfractions:\n"
					: targetData.CharData;

				return $"[{targetData.Name} - CID #{targetData.CIDNumber}]\n{charData}";
			}
		} );
	}

	/// <summary>
	/// /objectives — View or edit server-wide objectives. Admin only for editing.
	/// </summary>
	private static void RegisterObjectivesCommand()
	{
		// Load saved objectives
		_serverObjectives = DatabaseManager.Load<string>( "hl2rp", "objectives" ) ?? "";

		CommandManager.Register( new HexCommand
		{
			Name = "objectives",
			Description = "View or edit server objectives.",
			Arguments = new[]
			{
				Arg.Optional( Arg.String( "text", remainder: true ) )
			},
			OnRun = ( caller, ctx ) =>
			{
				if ( ctx.Has( "text" ) )
				{
					// Editing requires admin
					if ( !caller.Character.HasFlag( 'a' ) && !caller.Character.HasFlag( 's' ) )
						return "You do not have permission to edit objectives.";

					var text = ctx.Get<string>( "text" );
					var maxLength = HexConfig.Get<int>( "hl2rp.chardata.maxlength", 750 );
					if ( text.Length > maxLength )
						return $"Objectives text exceeds maximum length of {maxLength} characters.";

					_serverObjectives = text;
					DatabaseManager.Save( "hl2rp", "objectives", _serverObjectives );
					return "Server objectives updated.";
				}

				if ( string.IsNullOrEmpty( _serverObjectives ) )
					return "No objectives set.";

				return $"[Server Objectives]\n{_serverObjectives}";
			}
		} );
	}

	/// <summary>
	/// /setpriority <player> <true/false> — Toggle CWU priority on a citizen's CID. Combine only.
	/// </summary>
	private static void RegisterSetPriorityCommand()
	{
		CommandManager.Register( new HexCommand
		{
			Name = "setpriority",
			Description = "Set CWU priority status on a citizen's CID.",
			Arguments = new[]
			{
				Arg.Player( "target" ),
				Arg.String( "value" )
			},
			PermissionFunc = ( caller ) =>
			{
				return caller.Character != null && CombineUtils.IsCombine( caller.Character );
			},
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "target" );
				if ( target?.Character == null )
					return "Target has no active character.";

				var value = ctx.Get<string>( "value" );
				bool priority = value.ToLower() is "true" or "1" or "yes";

				if ( !CIDSystem.SetPriority( target.Character, priority ) )
					return $"{target.CharacterName} does not have a CID card.";

				var status = priority ? "granted" : "revoked";
				return $"CWU priority {status} for {target.CharacterName}.";
			}
		} );
	}
}
```

**Step 2: Commit**

```
git add Code/HL2RPCommands.cs
git commit -m "feat: add /data, /objectives, /setpriority commands"
```

---

### Task 9: Wire Everything into HL2RPPlugin

**Files:**
- Modify: `Code/HL2RPPlugin.cs`

**Step 1: Update OnPluginLoaded to initialize all systems**

Add calls to register config, commands, and log system status. Keep existing faction/class registration.

```csharp

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

		Log.Info( "[HL2RP] Schema loaded — Phase 1 core systems initialized." );
	}

	public void OnPluginUnloaded()
	{
		Log.Info( "[HL2RP] Schema unloaded." );
	}

	// ... existing RegisterFactions() and RegisterClasses() methods unchanged ...
}
```

**Step 2: Commit**

```
git add Code/HL2RPPlugin.cs
git commit -m "feat: wire config, commands into HL2RPPlugin"
```

---

### Task 10: Phase 1 Verification

**Step 1: Compile check**

Save all files. s&box should auto-compile. Check for errors.

**Step 2: Run scene**

Run the scene in s&box editor. Check console for:
- `[HL2RP] Schema loaded — Phase 1 core systems initialized.`
- No errors related to HL2RP code.

**Step 3: Tell user to add HL2RPHooks component**

The user must add the `HL2RPHooks` component to the same GameObject as `HexagonFramework` in the scene editor.

**Step 4: Test character creation**

Create a citizen character in-game. Verify console shows:
- `[HL2RP] Character created: [name] (citizen)`
- `[HL2RP] Assigned CID #[number] to [name]`
- `[HL2RP] Character loaded: [name] (faction: citizen)`

**Step 5: Commit phase completion**

```
git add -A
git commit -m "feat: complete Phase 1 — core schema enhancement"
```

---

## Phase 2: Chat & Communication

### Task 11: Create RadioChatClass

**Files:**
- Create: `Code/Chat/RadioChatClass.cs`

**Step 1: Implement frequency-based radio chat**

```csharp

/// <summary>
/// Radio chat class (/r, /radio). Frequency-based communication.
/// Requires a radio item in inventory. Speaker and listener must have matching frequencies
/// or be within physical range.
/// </summary>
public class RadioChatClass : IChatClass
{
	public string Name => "Radio";
	public string Prefix => "/r";
	public float Range => 0f; // Global - we handle range ourselves in CanHear
	public Color Color => HexConfig.Get<Color>( "hl2rp.chat.radioColor", new Color( 0.3f, 0.5f, 0.9f ) );

	public bool CanSay( HexPlayerComponent speaker, string message )
	{
		if ( !speaker.HasActiveCharacter )
			return false;

		return HasRadioItem( speaker.Character );
	}

	public bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener )
	{
		if ( !listener.HasActiveCharacter )
			return false;

		if ( !HasRadioItem( listener.Character ) )
			return false;

		var speakerFreq = GetFrequency( speaker.Character );
		var listenerFreq = GetFrequency( listener.Character );

		// Matching frequency = can hear regardless of distance
		if ( speakerFreq == listenerFreq )
			return true;

		// Otherwise, must be within physical radio range
		var range = HexConfig.Get<float>( "hl2rp.chat.radioRange", 280f );
		var distance = Vector3.DistanceBetween(
			speaker.WorldPosition,
			listener.WorldPosition
		);

		return distance <= range;
	}

	public string Format( HexPlayerComponent speaker, string message )
	{
		var freq = GetFrequency( speaker.Character );
		return $"[FREQ {freq}] {speaker.CharacterName} says \"{message}\"";
	}

	private bool HasRadioItem( HexCharacter character )
	{
		var inventories = InventoryManager.LoadForCharacter( character.Id );
		foreach ( var inv in inventories )
		{
			if ( inv.HasItem( "radio" ) || inv.HasItem( "static_radio" ) || inv.HasItem( "pager" ) )
				return true;
		}
		return false;
	}

	private string GetFrequency( HexCharacter character )
	{
		var inventories = InventoryManager.LoadForCharacter( character.Id );
		foreach ( var inv in inventories )
		{
			var radio = inv.FindItem( "radio" ) ?? inv.FindItem( "static_radio" ) ?? inv.FindItem( "pager" );
			if ( radio != null )
				return radio.GetData<string>( "frequency", "100.0" );
		}
		return "100.0";
	}
}
```

**Step 2: Commit**

```
git add Code/Chat/RadioChatClass.cs
git commit -m "feat: add RadioChatClass with frequency matching"
```

---

### Task 12: Create DispatchChatClass

**Files:**
- Create: `Code/Chat/DispatchChatClass.cs`

**Step 1: Implement combine dispatch chat**

```csharp

/// <summary>
/// Dispatch chat class (/dispatch). Elite combine only, broadcasts to all combine players.
/// </summary>
public class DispatchChatClass : IChatClass
{
	public string Name => "Dispatch";
	public string Prefix => "/dispatch";
	public float Range => 0f; // Global
	public Color Color => HexConfig.Get<Color>( "hl2rp.chat.dispatchColor", new Color( 0.75f, 0.22f, 0.17f ) );

	public bool CanSay( HexPlayerComponent speaker, string message )
	{
		if ( !speaker.HasActiveCharacter || speaker.Character == null )
			return false;

		return CombineUtils.IsDispatch( speaker.Character );
	}

	public bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener )
	{
		if ( !listener.HasActiveCharacter || listener.Character == null )
			return false;

		return CombineUtils.IsCombine( listener.Character );
	}

	public string Format( HexPlayerComponent speaker, string message )
	{
		return $"<:: {speaker.CharacterName} dispatches \"{message}\"";
	}
}
```

**Step 2: Commit**

```
git add Code/Chat/DispatchChatClass.cs
git commit -m "feat: add DispatchChatClass for elite combine"
```

---

### Task 13: Create RequestChatClass

**Files:**
- Create: `Code/Chat/RequestChatClass.cs`

**Step 1: Implement citizen request device chat**

```csharp

/// <summary>
/// Request chat class (/request). Citizens send one-way messages to combine players.
/// Requires a request device item. Has a 5-second cooldown.
/// </summary>
public class RequestChatClass : IChatClass
{
	public string Name => "Request";
	public string Prefix => "/request";
	public float Range => 0f; // Global
	public Color Color => HexConfig.Get<Color>( "hl2rp.chat.requestColor", new Color( 0.9f, 0.7f, 0.2f ) );

	private readonly Dictionary<string, DateTime> _cooldowns = new();

	public bool CanSay( HexPlayerComponent speaker, string message )
	{
		if ( !speaker.HasActiveCharacter || speaker.Character == null )
			return false;

		// Must have request device
		var inventories = InventoryManager.LoadForCharacter( speaker.Character.Id );
		bool hasDevice = false;
		foreach ( var inv in inventories )
		{
			if ( inv.HasItem( "request_device" ) )
			{
				hasDevice = true;
				break;
			}
		}

		if ( !hasDevice )
			return false;

		// Check cooldown
		var charId = speaker.Character.Id;
		var cooldown = HexConfig.Get<float>( "hl2rp.request.cooldown", 5f );
		if ( _cooldowns.TryGetValue( charId, out var lastUse ) )
		{
			if ( (DateTime.UtcNow - lastUse).TotalSeconds < cooldown )
				return false;
		}

		_cooldowns[charId] = DateTime.UtcNow;
		return true;
	}

	public bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener )
	{
		if ( !listener.HasActiveCharacter || listener.Character == null )
			return false;

		return CombineUtils.IsCombine( listener.Character );
	}

	public string Format( HexPlayerComponent speaker, string message )
	{
		var data = (HL2RPCharacter)speaker.Character.Data;
		return $"[DEVICE, CID {data.CIDNumber}] {message}";
	}
}
```

**Step 2: Commit**

```
git add Code/Chat/RequestChatClass.cs
git commit -m "feat: add RequestChatClass for citizen-to-combine messaging"
```

---

### Task 14: Create CombineVoiceHook

**Files:**
- Create: `Code/Chat/CombineVoiceHook.cs`

**Step 1: Implement combine radio beep hook**

```csharp

/// <summary>
/// Hooks into chat messages to play radio beep sounds when combine players speak.
/// When a CP or OW player sends an IC or Yell message, plays a radio on/off beep.
/// Attach this Component to the same GameObject as HexagonFramework.
/// </summary>
public class CombineVoiceHook : Component, IChatMessageListener
{
	public void OnChatMessage( HexPlayerComponent sender, IChatClass chatClass, string rawMessage, string formattedMessage )
	{
		if ( sender?.Character == null )
			return;

		if ( !CombineUtils.IsCombine( sender.Character ) )
			return;

		// Only play beeps for IC and Yell chat, not dispatch/radio/etc.
		var chatName = chatClass.Name;
		if ( chatName != "In-Character" && chatName != "Yell" )
			return;

		PlayRadioBeep( sender, true );

		// Schedule off beep after a short delay (approximate message duration)
		// In practice, this could use a timer or delayed action
		PlayRadioBeep( sender, false );
	}

	private void PlayRadioBeep( HexPlayerComponent player, bool isOn )
	{
		var isCP = CombineUtils.IsCP( player.Character );
		string configKey;

		if ( isOn )
			configKey = isCP ? "hl2rp.sound.radioOn.cp" : "hl2rp.sound.radioOn.ow";
		else
			configKey = isCP ? "hl2rp.sound.radioOff.cp" : "hl2rp.sound.radioOff.ow";

		var soundPath = HexConfig.Get<string>( configKey, "" );
		if ( string.IsNullOrEmpty( soundPath ) )
			return;

		// Play sound at player's position
		// Note: actual sound playback depends on s&box Sound API
		// Sound.Play( soundPath, player.WorldPosition );
		// TODO: Wire up actual sound playback when assets are available
	}
}
```

**Step 2: Commit**

```
git add Code/Chat/CombineVoiceHook.cs
git commit -m "feat: add CombineVoiceHook for radio beep sounds"
```

---

### Task 15: Register Chat Classes in Plugin

**Files:**
- Modify: `Code/HL2RPPlugin.cs`

**Step 1: Add chat registration to OnPluginLoaded**

Add after the existing `HL2RPCommands.Register()` call:

```csharp
// Register chat classes
ChatManager.Register( new RadioChatClass() );
ChatManager.Register( new DispatchChatClass() );
ChatManager.Register( new RequestChatClass() );
```

**Step 2: Commit**

```
git add Code/HL2RPPlugin.cs
git commit -m "feat: register HL2RP chat classes in plugin"
```

---

### Task 16: Phase 2 Verification

**Step 1: Compile and run scene**

Verify console shows no errors. Check that radio/dispatch/request chat classes are registered.

**Step 2: Tell user to add CombineVoiceHook component**

User must add the `CombineVoiceHook` component to the scene alongside `HL2RPHooks`.

**Step 3: Test chat classes**

In-game, test:
- `/r Hello` as a citizen — should fail (no radio item)
- `/dispatch Test` as a citizen — should fail (not combine)
- `/request Help` as a citizen without device — should fail

**Step 4: Commit**

```
git add -A
git commit -m "feat: complete Phase 2 — chat and communication"
```

---

## Phase 3: Items — Core

### Task 17: Create CID Card Item

**Files:**
- Create: `Code/Items/CIDCardItem.cs`

**Step 1: Implement CID card with Show and Assign actions**

```csharp

/// <summary>
/// Citizen ID card. Contains citizen name, ID number, and CWU priority status.
/// Combine officers can assign CIDs to citizens.
/// </summary>
public class CIDCardItem : ItemDefinition
{
	public CIDCardItem()
	{
		UniqueId = "cid_card";
		DisplayName = "Citizen ID";
		Description = "An identification card issued to citizens of City 17.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		CanDrop = true;
		Category = "Identification";
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Show"] = new ItemAction
			{
				Name = "Show",
				OnCanRun = ( player, item ) => true,
				OnRun = ( player, item ) =>
				{
					var name = item.GetData<string>( "name", "Unknown" );
					var id = item.GetData<int>( "id", 0 );
					var cwu = item.GetData<bool>( "cwu", false );
					var priority = cwu ? " [CWU PRIORITY]" : "";
					// TODO: Show via UI panel instead of chat
					Log.Info( $"[CID] Name: {name} | ID: #{id}{priority}" );
				}
			}
		};
	}
}
```

**Step 2: Register in HL2RPPlugin.OnPluginLoaded()**

```csharp
ItemManager.Register( new CIDCardItem() );
```

**Step 3: Commit**

```
git add Code/Items/CIDCardItem.cs Code/HL2RPPlugin.cs
git commit -m "feat: add CID card item definition"
```

---

### Task 18: Create Consumable Items

**Files:**
- Create: `Code/Items/Consumables/RationItem.cs`
- Create: `Code/Items/Consumables/WaterItem.cs`
- Create: `Code/Items/Consumables/SupplementItem.cs`
- Create: `Code/Items/Consumables/HealthVialItem.cs`
- Create: `Code/Items/Consumables/HealthKitItem.cs`
- Create: `Code/Items/Consumables/BleachItem.cs`
- Create: `Code/Items/Consumables/VegetableOilItem.cs`

**Step 1: Create base consumable pattern**

Each consumable follows the same pattern. Here's the ration (most complex) and water (simplest) as templates:

```csharp
// RationItem.cs

public class RationItem : ItemDefinition
{
	public RationItem()
	{
		UniqueId = "ration";
		DisplayName = "Ration Package";
		Description = "A standard Combine-issued ration package.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Consumables";
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Open"] = new ItemAction
			{
				Name = "Open",
				OnRun = ( player, item ) =>
				{
					// Give water + supplement + 10-15 tokens
					var inv = InventoryManager.LoadForCharacter( player.Character.Id );
					if ( inv.Count == 0 ) return;

					var water = ItemManager.CreateInstance( "water", player.Character.Id );
					var supplement = ItemManager.CreateInstance( "supplement", player.Character.Id );
					inv[0].Add( water );
					inv[0].Add( supplement );

					var tokens = new Random().Next( 10, 16 );
					CurrencyManager.GiveMoney( player.Character, tokens, "ration" );

					// Remove the ration package
					inv[0].Remove( item.Id );
					ItemManager.DestroyInstance( item.Id );
				}
			}
		};
	}
}
```

```csharp
// WaterItem.cs

public class WaterItem : ItemDefinition
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
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Drink"] = new ItemAction
			{
				Name = "Drink",
				OnRun = ( player, item ) =>
				{
					// TODO: Apply thirst/health effect when attribute system is wired
					var inv = InventoryManager.LoadForCharacter( player.Character.Id );
					foreach ( var i in inv )
					{
						if ( i.Remove( item.Id ) )
						{
							ItemManager.DestroyInstance( item.Id );
							break;
						}
					}
				}
			}
		};
	}
}
```

**Step 2: Create remaining consumables**

Follow the same pattern for each. Key differences:

| Item | UniqueId | DisplayName | Action | Notes |
|------|----------|-------------|--------|-------|
| Sparkling Water | water_sparkling | Sparkling Water | Drink | 15 token vendor price |
| Special Water | water_special | Special Water | Drink | 20 token vendor price |
| Large Supplement | supplement_large | Large Supplement | Eat | Better healing |
| Health Vial | health_vial | Health Vial | Use | Small heal |
| Health Kit | health_kit | Health Kit | Use | +50 HP, restrict to CP/OW via OnCanRun |
| Bleach | bleach | Bleach | Use | Damage on use |
| Vegetable Oil | vegetable_oil | Vegetable Oil | Use | Cooking ingredient |

For Health Kit, add faction restriction:

```csharp
OnCanRun = ( player, item ) =>
{
	return player.Character != null && CombineUtils.IsCombine( player.Character );
}
```

**Step 3: Register all consumables in HL2RPPlugin.OnPluginLoaded()**

**Step 4: Commit**

```
git add Code/Items/Consumables/
git commit -m "feat: add consumable item definitions"
```

---

### Task 19: Create Equipment Items

**Files:**
- Create: `Code/Items/Equipment/RadioItem.cs`
- Create: `Code/Items/Equipment/PagerItem.cs`
- Create: `Code/Items/Equipment/StaticRadioItem.cs`
- Create: `Code/Items/Equipment/RequestDeviceItem.cs`
- Create: `Code/Items/Equipment/FlashlightItem.cs`
- Create: `Code/Items/Equipment/SpraycanItem.cs`

**Step 1: Create radio item with frequency tuning**

```csharp
// RadioItem.cs

public class RadioItem : ItemDefinition
{
	public RadioItem()
	{
		UniqueId = "radio";
		DisplayName = "Radio";
		Description = "A handheld frequency-tuned radio for long-range communication.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Equipment";
	}

	public override void OnInstanced( ItemInstance item )
	{
		// Default frequency if not set
		if ( !item.Data.ContainsKey( "frequency" ) )
		{
			item.SetData( "frequency", "100.0" );
		}
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Tune"] = new ItemAction
			{
				Name = "Tune",
				OnRun = ( player, item ) =>
				{
					// TODO: Open radio tuning UI (Phase 7)
					var freq = item.GetData<string>( "frequency", "100.0" );
					Log.Info( $"[Radio] Current frequency: {freq}" );
				}
			}
		};
	}
}
```

**Step 2: Create remaining equipment**

Follow the pattern. Key differences:

| Item | UniqueId | Instance Data | Notes |
|------|----------|---------------|-------|
| Pager | pager | frequency | Receive-only (no Tune action) |
| Static Radio | static_radio | frequency | Same as radio, unlimited range |
| Request Device | request_device | — | No actions, passive (enables /request) |
| Flashlight | flashlight | on: false | Toggle action |
| Spraycan | spraycan | — | Requires "misc" permit (check in OnCanRun) |

**Step 3: Register all in HL2RPPlugin, commit**

```
git add Code/Items/Equipment/
git commit -m "feat: add equipment item definitions"
```

---

### Task 20: Create Bag Items

**Files:**
- Create: `Code/Items/Bags/SmallBagItem.cs`
- Create: `Code/Items/Bags/LargeBagItem.cs`
- Create: `Code/Items/Bags/SuitcaseItem.cs`

**Step 1: Implement bags using BagItemDef base**

```csharp
// SmallBagItem.cs

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
```

Large bag: BagWidth=4, BagHeight=4. Suitcase: BagWidth=3, BagHeight=3.

**Step 2: Register and commit**

```
git add Code/Items/Bags/
git commit -m "feat: add bag item definitions"
```

---

### Task 21: Create Permit Items

**Files:**
- Create: `Code/Items/Permits/PermitItem.cs`

**Step 1: Create a single parameterized permit class**

```csharp
// PermitItem.cs

/// <summary>
/// Business permit. Having one in inventory allows buying/selling items
/// of the matching category. Types: food, electronics, literature, misc.
/// </summary>
public class PermitItem : ItemDefinition
{
	public string PermitType { get; set; }

	public PermitItem( string type, string displayName, string description )
	{
		UniqueId = $"permit_{type}";
		DisplayName = displayName;
		Description = description;
		Width = 1;
		Height = 1;
		MaxStack = 1;
		CanDrop = true;
		Category = "Permits";
		PermitType = type;
	}
}
```

Register four instances in plugin:

```csharp
ItemManager.Register( new PermitItem( "food", "Food Permit", "Allows the sale of food and consumable items." ) );
ItemManager.Register( new PermitItem( "elec", "Electronics Permit", "Allows the sale of electronic devices." ) );
ItemManager.Register( new PermitItem( "lit", "Literature Permit", "Allows the sale of written materials." ) );
ItemManager.Register( new PermitItem( "misc", "Miscellaneous Permit", "Allows the sale of miscellaneous goods." ) );
```

**Step 2: Commit**

```
git add Code/Items/Permits/
git commit -m "feat: add business permit items"
```

---

### Task 22: Create Misc Items (Note, Book, Zip Tie)

**Files:**
- Create: `Code/Items/Misc/NoteItem.cs`
- Create: `Code/Items/Misc/BookItem.cs`
- Create: `Code/Items/Misc/ZipTieItem.cs`

**Step 1: Note item with Read/Write actions**

```csharp
// NoteItem.cs

public class NoteItem : ItemDefinition
{
	public NoteItem()
	{
		UniqueId = "note";
		DisplayName = "Note";
		Description = "A writable note.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Misc";
	}

	public override void OnInstanced( ItemInstance item )
	{
		if ( !item.Data.ContainsKey( "text" ) )
			item.SetData( "text", "" );
		if ( !item.Data.ContainsKey( "owner" ) )
			item.SetData( "owner", "" );
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Read"] = new ItemAction
			{
				Name = "Read",
				OnRun = ( player, item ) =>
				{
					var text = item.GetData<string>( "text", "" );
					// TODO: Open note UI (Phase 7)
					Log.Info( $"[Note] {text}" );
				}
			},
			["Write"] = new ItemAction
			{
				Name = "Write",
				OnCanRun = ( player, item ) =>
				{
					var owner = item.GetData<string>( "owner", "" );
					return string.IsNullOrEmpty( owner ) || owner == player.Character?.Id;
				},
				OnRun = ( player, item ) =>
				{
					// TODO: Open note editor UI (Phase 7)
					if ( string.IsNullOrEmpty( item.GetData<string>( "owner", "" ) ) )
						item.SetData( "owner", player.Character.Id );
				}
			}
		};
	}
}
```

**Step 2: Book and Zip Tie**

Book: UniqueId="book", Read action only, predefined content.

Zip Tie: UniqueId="zip_tie", Use action targets nearest player (CP/OW only via OnCanRun), triggers restraint (Phase 6 TyingPlugin wires this up).

```csharp
// ZipTieItem.cs

public class ZipTieItem : ItemDefinition
{
	public ZipTieItem()
	{
		UniqueId = "zip_tie";
		DisplayName = "Zip Tie";
		Description = "A plastic restraint used to tie someone's hands.";
		Width = 1;
		Height = 1;
		MaxStack = 5;
		Category = "Equipment";
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Use"] = new ItemAction
			{
				Name = "Tie",
				OnCanRun = ( player, item ) =>
				{
					return player.Character != null && CombineUtils.IsCombine( player.Character );
				},
				OnRun = ( player, item ) =>
				{
					// Restraint logic handled by TyingPlugin (Phase 6)
					// This just consumes the item
					Log.Info( "[HL2RP] Zip tie used — TyingPlugin handles restraint." );
				}
			}
		};
	}
}
```

**Step 3: Register all and commit**

```
git add Code/Items/Misc/
git commit -m "feat: add note, book, and zip tie items"
```

---

### Task 23: Phase 3 Verification & Commit

**Step 1: Register ALL items in HL2RPPlugin.OnPluginLoaded()**

Create a helper method `RegisterItems()` that registers every item via `ItemManager.Register()`.

**Step 2: Compile and run scene**

Verify console shows item registration without errors.

**Step 3: Commit**

```
git add -A
git commit -m "feat: complete Phase 3 — core items (CID, consumables, equipment, bags, permits, misc)"
```

---

## Phase 4: Items — Weapons & Ammo

### Task 24: Create Ammo Items

**Files:**
- Create: `Code/Items/Ammo/AmmoItems.cs`

**Step 1: Define all 7 ammo types in a single file**

```csharp
// AmmoItems.cs

public class PistolAmmoItem : AmmoItemDef
{
	public PistolAmmoItem()
	{
		UniqueId = "ammo_pistol";
		DisplayName = "Pistol Ammo";
		Description = "A box of pistol rounds.";
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
		Description = "A box of SMG rounds.";
		AmmoType = "smg";
		AmmoAmount = 45;
		Width = 1;
		Height = 1;
		MaxStack = 3;
		Category = "Ammo";
	}
}

// Continue pattern for: ammo_shotgun, ammo_357, ammo_ar2, ammo_crossbow, ammo_rocket
// Each with appropriate AmmoType, AmmoAmount, and display values.
```

**Step 2: Register and commit**

```
git add Code/Items/Ammo/
git commit -m "feat: add 7 ammo item definitions"
```

---

### Task 25: Create Weapon Items

**Files:**
- Create: `Code/Items/Weapons/WeaponItems.cs`

**Step 1: Define all 8 weapons**

```csharp
// WeaponItems.cs

public class CrowbarWeapon : WeaponItemDef
{
	public CrowbarWeapon()
	{
		UniqueId = "weapon_crowbar";
		DisplayName = "Crowbar";
		Description = "A standard crowbar. Useful for prying things open.";
		AmmoType = "";  // Melee, no ammo
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

public class StunstickWeapon : WeaponItemDef
{
	public StunstickWeapon()
	{
		UniqueId = "weapon_stunstick";
		DisplayName = "Stunstick";
		Description = "A Civil Protection-issued stun baton.";
		AmmoType = "";  // Melee
		ClipSize = 0;
		Width = 1;
		Height = 2;
		MaxStack = 1;
		Category = "Weapons";
	}
}

// Continue pattern for: weapon_357, weapon_smg, weapon_shotgun, weapon_crossbow, weapon_ar2, weapon_rpg
// Each with appropriate AmmoType, ClipSize, dimensions.
```

**Step 2: Register and commit**

```
git add Code/Items/Weapons/
git commit -m "feat: add 8 weapon item definitions (+ stunstick)"
```

---

### Task 26: Create Armor Items

**Files:**
- Create: `Code/Items/Armor/ArmorItems.cs`

**Step 1: Define armor using OutfitItemDef**

```csharp
// ArmorItems.cs

public class RebelArmorItem : OutfitItemDef
{
	public RebelArmorItem()
	{
		UniqueId = "armor_rebel";
		DisplayName = "Rebel Armor";
		Description = "Makeshift citizen armor cobbled together from scrap.";
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
		Description = "An enhanced set of makeshift armor with additional plating.";
		Slot = "torso";
		Width = 2;
		Height = 2;
		MaxStack = 1;
		Category = "Armor";
	}
}
```

**Step 2: Register and commit**

```
git add Code/Items/Armor/
git commit -m "feat: add rebel armor item definitions"
```

---

### Task 27: Phase 4 Verification & Commit

**Step 1: Register all weapons, ammo, armor in plugin**

**Step 2: Compile, run, verify console**

**Step 3: Commit**

```
git add -A
git commit -m "feat: complete Phase 4 — weapons, ammo, and armor items"
```

---

## Phase 5: World Entities

### Task 28: Create RationDispenser Component

**Files:**
- Create: `Code/Entities/RationDispenser.cs`

**Step 1: Implement state machine dispenser**

```csharp

/// <summary>
/// Ration dispenser world entity. Citizens insert CID to receive rations.
/// Place in scene via editor. States: Idle, Checking, Arming, Dispensing, Error, Disabled.
/// </summary>
public class RationDispenser : Component
{
	public enum DispenserState
	{
		Idle,
		Checking,
		Arming,
		Dispensing,
		Error,
		Disabled
	}

	[Sync] public DispenserState State { get; set; } = DispenserState.Idle;
	[Sync] public bool IsDisabled { get; set; }

	/// <summary>
	/// Tracks cooldowns per CID number. Key = CID number, Value = last dispense time.
	/// Server-side only.
	/// </summary>
	private Dictionary<int, DateTime> _cooldowns = new();

	/// <summary>
	/// Unique ID for persistence.
	/// </summary>
	[Property] public string PersistenceId { get; set; } = "";

	protected override void OnStart()
	{
		if ( string.IsNullOrEmpty( PersistenceId ) )
			PersistenceId = DatabaseManager.NewId();

		LoadState();
	}

	/// <summary>
	/// Called when a player interacts (Use key) with the dispenser.
	/// </summary>
	public void OnInteract( HexPlayerComponent player )
	{
		if ( player?.Character == null )
			return;

		// CP can toggle disabled state
		if ( CombineUtils.IsCombine( player.Character ) )
		{
			IsDisabled = !IsDisabled;
			State = IsDisabled ? DispenserState.Disabled : DispenserState.Idle;
			SaveState();
			return;
		}

		if ( IsDisabled || State != DispenserState.Idle )
			return;

		// Check for CID in inventory
		var inventories = InventoryManager.LoadForCharacter( player.Character.Id );
		ItemInstance cidItem = null;
		foreach ( var inv in inventories )
		{
			cidItem = inv.FindItem( "cid_card" );
			if ( cidItem != null ) break;
		}

		if ( cidItem == null )
		{
			State = DispenserState.Error;
			// TODO: Reset to Idle after delay
			return;
		}

		var cidNumber = cidItem.GetData<int>( "id", 0 );
		var cooldown = HexConfig.Get<float>( "hl2rp.dispenser.cooldown", 300f );

		// Check cooldown
		if ( _cooldowns.TryGetValue( cidNumber, out var lastUse ) )
		{
			if ( (DateTime.UtcNow - lastUse).TotalSeconds < cooldown )
			{
				State = DispenserState.Error;
				return;
			}
		}

		// Begin dispensing sequence
		State = DispenserState.Checking;
		// TODO: Animate checking -> arming -> dispense with delays

		// Dispense rations
		var isPriority = cidItem.GetData<bool>( "cwu", false );
		var rationCount = isPriority ? 2 : 1;

		for ( int i = 0; i < rationCount; i++ )
		{
			var ration = ItemManager.CreateInstance( "ration", player.Character.Id );
			if ( inventories.Count > 0 )
				inventories[0].Add( ration );
		}

		_cooldowns[cidNumber] = DateTime.UtcNow;
		State = DispenserState.Idle;
		SaveState();

		Log.Info( $"[HL2RP] Dispenser: Gave {rationCount} ration(s) to CID #{cidNumber}" );
	}

	private void SaveState()
	{
		DatabaseManager.Save( "hl2rp_dispensers", PersistenceId, new DispenserSaveData
		{
			IsDisabled = IsDisabled,
			Position = WorldPosition,
			Rotation = WorldRotation
		} );
	}

	private void LoadState()
	{
		var saved = DatabaseManager.Load<DispenserSaveData>( "hl2rp_dispensers", PersistenceId );
		if ( saved != null )
		{
			IsDisabled = saved.IsDisabled;
			State = IsDisabled ? DispenserState.Disabled : DispenserState.Idle;
		}
	}
}

public class DispenserSaveData
{
	public bool IsDisabled { get; set; }
	public Vector3 Position { get; set; }
	public Rotation Rotation { get; set; }
}
```

**Step 2: Commit**

```
git add Code/Entities/RationDispenser.cs
git commit -m "feat: add RationDispenser world entity"
```

---

### Task 29: Create VendingMachine Component

**Files:**
- Create: `Code/Entities/VendingMachine.cs`

Follow the same pattern as RationDispenser but with stock tracking (3 buttons), pricing, and CP refill capability. Key properties:

- `[Sync]` StockButton1 (default 10), StockButton2 (default 5), StockButton3 (default 5)
- `[Sync]` IsActive
- Button 1: Water (5 tokens), Button 2: Sparkling Water (15), Button 3: Special Water (20)
- CP interact: Refill (25 tokens) or toggle active
- Citizen interact: Select button → check stock → check tokens → dispense → decrement stock
- Persistence: Save/load stock levels and active state

**Commit:**

```
git add Code/Entities/VendingMachine.cs
git commit -m "feat: add VendingMachine world entity"
```

---

### Task 30: Create CombineLock Component

**Files:**
- Create: `Code/Entities/CombineLock.cs`

Key features:
- `[Sync]` State (Locked/Unlocked/Error/Detonating)
- References a door component (Hexagon's DoorComponent)
- CP/OW Use: toggle lock state
- CP Crouch+Use: 10-second detonation countdown → blast force on door
- Non-combine blocked when locked
- LED color based on state
- Persistence: save lock state and parent door reference

**Commit:**

```
git add Code/Entities/CombineLock.cs
git commit -m "feat: add CombineLock world entity"
```

---

### Task 31: Create Forcefield Component

**Files:**
- Create: `Code/Entities/Forcefield.cs`

Key features:
- `[Sync]` IsActive
- Physical collider that blocks citizens but allows combine through (collision tag filtering)
- Admin-only spawning
- Persistence: save position, rotation, active state

**Commit:**

```
git add Code/Entities/Forcefield.cs
git commit -m "feat: add Forcefield world entity"
```

---

### Task 32: Phase 5 Verification & Commit

**Step 1: Compile and run**

**Step 2: Tell user to place entities in scene via editor**

User must add RationDispenser, VendingMachine, etc. as components on GameObjects in the scene.

**Step 3: Commit**

```
git add -A
git commit -m "feat: complete Phase 5 — world entities"
```

---

## Phase 6: Sub-Plugins

### Task 33: Create TyingPlugin

**Files:**
- Create: `Code/Plugins/TyingPlugin.cs`

Key features:
- `[HexPlugin]` with its own priority
- Hooks into zip tie item use action
- 5-second tying action (both players animated)
- Adds `[Sync]` IsRestrained to player (via custom component or extending HexPlayerComponent data)
- Restrained players: movement disabled, inventory blocked
- Untying: 5-second action by another player
- Register `/untie` command

**Commit:**

```
git add Code/Plugins/TyingPlugin.cs
git commit -m "feat: add TyingPlugin for zip tie restraint system"
```

---

### Task 34: Create FlashlightPlugin

**Files:**
- Create: `Code/Plugins/FlashlightPlugin.cs`

Simple plugin: hooks into flashlight input. If player doesn't have flashlight item in inventory, block flashlight activation.

**Commit:**

```
git add Code/Plugins/FlashlightPlugin.cs
git commit -m "feat: add FlashlightPlugin"
```

---

### Task 35: Create PermitPlugin

**Files:**
- Create: `Code/Plugins/PermitPlugin.cs`

Hooks into vendor/business interactions. Checks if player has matching permit in inventory before allowing buy/sell of categorized items.

**Commit:**

```
git add Code/Plugins/PermitPlugin.cs
git commit -m "feat: add PermitPlugin for business permit enforcement"
```

---

### Task 36: Create ShootlockPlugin

**Files:**
- Create: `Code/Plugins/ShootlockPlugin.cs`

Hooks into damage system. When a door takes enough bullet damage, its lock breaks. Does not affect CombineLocks.

**Commit:**

```
git add Code/Plugins/ShootlockPlugin.cs
git commit -m "feat: add ShootlockPlugin"
```

---

### Task 37: Create DoorKickPlugin

**Files:**
- Create: `Code/Plugins/DoorKickPlugin.cs`

Registers `/doorkick` command (CP only). Traces forward from player, finds door within range, forces it open with physics impulse.

**Commit:**

```
git add Code/Plugins/DoorKickPlugin.cs
git commit -m "feat: add DoorKickPlugin with /doorkick command"
```

---

### Task 38: Phase 6 Verification & Commit

**Step 1: Compile, run, verify all plugins load in console**

**Step 2: Tell user to add TyingPlugin components to scene if needed**

**Step 3: Commit**

```
git add -A
git commit -m "feat: complete Phase 6 — sub-plugins"
```

---

## Phase 7: UI/HUD

### Task 39: Create CombineOverlay

**Files:**
- Create: `Code/UI/CombineOverlay.razor`

Razor component: dark blue tinted fullscreen overlay. Only visible when player's faction is CCA or OTA. Checks `HexPlayerComponent.FactionId` on the local player.

**Commit:**

```
git add Code/UI/CombineOverlay.razor
git commit -m "feat: add CombineOverlay HUD for combine factions"
```

---

### Task 40: Create DispatchPanel

**Files:**
- Create: `Code/UI/DispatchPanel.razor`

Razor component: scrolling text panel in top-right corner. Shows recent dispatch/radio messages with typewriter reveal effect. Queue-based with auto-fade. Only visible to combine players.

**Commit:**

```
git add Code/UI/DispatchPanel.razor
git commit -m "feat: add DispatchPanel for combine message display"
```

---

### Task 41: Create DataPanel

**Files:**
- Create: `Code/UI/DataPanel.razor`

Razor component: 280x380 popup window showing a citizen's CharData notes. Editable text area when opened by combine. 750 char limit. Opened via `/data` command result.

**Commit:**

```
git add Code/UI/DataPanel.razor
git commit -m "feat: add DataPanel for citizen notes viewing/editing"
```

---

### Task 42: Create ObjectivesPanel

**Files:**
- Create: `Code/UI/ObjectivesPanel.razor`

Same pattern as DataPanel but shows server objectives text. Editable by admins only.

**Commit:**

```
git add Code/UI/ObjectivesPanel.razor
git commit -m "feat: add ObjectivesPanel for server objectives"
```

---

### Task 43: Create RadioTuningUI

**Files:**
- Create: `Code/UI/RadioTuningUI.razor`

Razor component: frequency dial with XXX.X format display. Up/down buttons for each digit. Sound effects on adjustment. Opened when using "Tune" action on radio items.

**Commit:**

```
git add Code/UI/RadioTuningUI.razor
git commit -m "feat: add RadioTuningUI for frequency adjustment"
```

---

### Task 44: Create NoteEditor

**Files:**
- Create: `Code/UI/NoteEditor.razor`

Razor component: text editor popup for note items. 1000 char limit. Read-only for non-owners. Opened via note item Read/Write actions.

**Commit:**

```
git add Code/UI/NoteEditor.razor
git commit -m "feat: add NoteEditor UI for writable notes"
```

---

### Task 45: Phase 7 Verification & Commit

**Step 1: Compile, run, verify UI components render**

**Step 2: Test combine overlay appears for CP character, not for citizen**

**Step 3: Commit**

```
git add -A
git commit -m "feat: complete Phase 7 — UI/HUD components"
```

---

## Phase 8: Scanner Drone

### Task 46: Create ScannerDrone Component

**Files:**
- Create: `Code/Entities/ScannerDrone.cs`

Most complex entity. Key features:
- `[Sync]` PilotPlayer, Health, IsActive
- CP with SCN/CLAW.SCN rank can enter scanner mode
- Player body goes limp/ragdoll while piloting
- Scanner uses acceleration-based flight physics
- Hover height maintenance
- Flash photography action (creates light flash + capture effect)
- Spotlight toggle
- Scan sound effects
- Destructible: if health reaches 0, scanner crashes and pilot is ejected back to body

This is the most complex feature and the least critical — acceptable to implement as a skeleton and iterate.

**Commit:**

```
git add Code/Entities/ScannerDrone.cs
git commit -m "feat: add ScannerDrone world entity (skeleton)"
```

---

### Task 47: Create Scanner Piloting System

**Files:**
- Create: `Code/Systems/ScannerPilotSystem.cs`

Handles the camera switch, input remapping, and body management when a player enters/exits scanner mode. This is the controller layer on top of ScannerDrone.

**Commit:**

```
git add Code/Systems/ScannerPilotSystem.cs
git commit -m "feat: add ScannerPilotSystem for player-drone control"
```

---

### Task 48: Phase 8 Verification & Final Commit

**Step 1: Compile, run, verify scanner components exist**

**Step 2: Full regression — verify all phases still work together**

Run scene, check console for:
- All plugins loaded
- All items registered
- All chat classes registered
- All commands registered
- Character creation works (citizen gets CID, combine gets loadout)

**Step 3: Final commit**

```
git add -A
git commit -m "feat: complete Phase 8 — scanner drone (all phases complete)"
```

---

## Summary

| Phase | Tasks | Description |
|-------|-------|-------------|
| 1 | 1-10 | Core schema: CharVars, config, utils, rank, CID, loadout, hooks, commands |
| 2 | 11-16 | Chat: radio, dispatch, request, combine voice beeps |
| 3 | 17-23 | Items core: CID, consumables, equipment, bags, permits, misc |
| 4 | 24-27 | Items weapons: 7 ammo, 8 weapons, 2 armor |
| 5 | 28-32 | World entities: dispenser, vending, lock, forcefield |
| 6 | 33-38 | Sub-plugins: tying, flashlight, permits, shootlock, doorkick |
| 7 | 39-45 | UI/HUD: overlay, dispatch, data, objectives, radio tuning, notes |
| 8 | 46-48 | Scanner drone: entity, piloting system |

**Total: 48 tasks across 8 phases.**

After each phase, the user should run the scene in s&box to verify. The user must add scene components (HL2RPHooks, CombineVoiceHook, world entities) via the s&box editor — never do editor work in code.
