#nullable enable

using System;
using Hexagon.V2.Domain;
using Hexagon.V2.Runtime;
using Sandbox;

namespace HL2RP.V2.Runtime;

/// <summary>
/// Client presentation adapter only. It submits ItemId intent and the current
/// private main-inventory ID; host policy reconstructs world position, reach,
/// line of sight and restraint state before any pickup commit.
/// </summary>
public sealed class HL2RPWorldItemPressable : Component, Component.IPressable
{
	[Sync( SyncFlags.FromHost )] public Guid ItemGuid { get; private set; }

	internal void HostBind( ItemId itemId ) => ItemGuid = itemId.Value;

	bool IPressable.CanPress( IPressable.Event e ) =>
		ItemGuid != Guid.Empty &&
		e.Source is PlayerController &&
		e.Source.Network.IsOwner &&
		HexagonRuntimeSystem.Current?.ClientReadiness == HexRuntimeReadiness.Ready &&
		HexagonRuntimeSystem.Current.ClientController is not null &&
		HexagonRuntimeSystem.Current.ClientStore?.PrivatePlayer?.MainInventoryId is not null;

	bool IPressable.Press( IPressable.Event e )
	{
		if ( !((IPressable)this).CanPress( e ) ||
			HexagonRuntimeSystem.Current!.ClientStore!.PrivatePlayer!.MainInventoryId is not InventoryId destination )
			return false;
		_ = HexagonRuntimeSystem.Current.ClientController!.PickUpItemAsync( new ItemId( ItemGuid ), destination );
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) => new(
		"Pick up item", "move_to_inbox", "The host validates reach, line of sight, restraint, capacity, and ownership." );
}
