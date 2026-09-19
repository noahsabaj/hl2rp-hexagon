#nullable enable

using System;
using System.Collections.Generic;
using Sandbox;
namespace HL2RP;

/// <summary>A faction a character can belong to, authored as an asset.</summary>
[AssetType( Name = "HL2RP Faction", Extension = "faction", Category = "HL2RP" )]
public sealed class FactionDefinition : GameResource
{
	[Property] public string Title { get; set; } = "Faction";
	[Property, TextArea] public string Description { get; set; } = string.Empty;
	[Property] public Color Tint { get; set; } = Color.White;

	/// <summary>When set, an operator must whitelist the account before it can create a character here.</summary>
	[Property] public bool RequiresWhitelist { get; set; }

	/// <summary>Members may lock and unlock doors.</summary>
	[Property] public bool CanLockDoors { get; set; }

	[Property] public List<ItemDefinition> StartingItems { get; set; } = new();

	public static FactionDefinition? Find( string path ) =>
		ResourceLibrary.TryGet<FactionDefinition>( path, out var definition ) ? definition : null;

	public static IEnumerable<FactionDefinition> All => ResourceLibrary.GetAll<FactionDefinition>();
}
