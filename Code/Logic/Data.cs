#nullable enable

using System;
using System.Collections.Generic;

namespace HL2RP.Logic;

// Plain documents. One JSON file each; see docs/decisions.md for why there is no database.

public sealed class CharacterData
{
	public Guid Id { get; set; }
	public long SteamId { get; set; }
	public string Name { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	public string Faction { get; set; } = string.Empty;
	public long Tokens { get; set; }
	public InventoryData Inventory { get; set; } = new();
	public DateTimeOffset CreatedAt { get; set; }
	/// <summary>Where the character last stood, or null to use a spawn point.</summary>
	public float[]? Position { get; set; }
}

public sealed class InventoryData
{
	public int Width { get; set; } = 6;
	public int Height { get; set; } = 4;
	public List<ItemStack> Items { get; set; } = new();
}

public sealed class ItemStack
{
	public Guid Id { get; set; }
	/// <summary>Resource path of the item definition asset.</summary>
	public string Definition { get; set; } = string.Empty;
	public int X { get; set; }
	public int Y { get; set; }
	public int Width { get; set; } = 1;
	public int Height { get; set; } = 1;
}

public sealed class AccountData
{
	public long SteamId { get; set; }
	/// <summary>Faction ids this account may create characters in, beyond the open ones.</summary>
	public List<string> Whitelists { get; set; } = new();
}

public sealed class WorldData
{
	public Dictionary<Guid, DoorData> Doors { get; set; } = new();
}

public sealed class DoorData
{
	public bool IsOpen { get; set; }
	public bool IsLocked { get; set; }
}
