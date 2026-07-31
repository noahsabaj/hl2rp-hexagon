#nullable enable

using Hexagon.V2.Networking;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;

namespace HL2RP.UI;

public static class InventoryGridRules
{
	public static bool ShowAction( ItemActionSnapshot action )
	{
		ArgumentNullException.ThrowIfNull( action );
		return action.Invocation == ItemActionInvocationKind.ContextMenu ||
			action.Invocation == ItemActionInvocationKind.DedicatedPanel &&
			action.ActionId.Value is HL2RPIds.Actions.Write or HL2RPIds.Actions.Tune;
	}

	public static bool ShowDrop( bool workspaceAllowsDrop, InventoryItemSnapshot item )
	{
		ArgumentNullException.ThrowIfNull( item );
		return workspaceAllowsDrop;
	}

	public static bool EnableDrop( bool workspaceAllowsDrop, InventoryItemSnapshot item )
	{
		ArgumentNullException.ThrowIfNull( item );
		return workspaceAllowsDrop && item.CanDrop;
	}

	public static ItemActionSnapshot? DedicatedAction( InventoryItemSnapshot item, string actionId )
	{
		ArgumentNullException.ThrowIfNull( item );
		if ( string.IsNullOrWhiteSpace( actionId ) ) return null;
		return item.Actions.SingleOrDefault( action =>
			action.Invocation == ItemActionInvocationKind.DedicatedPanel &&
			string.Equals( action.ActionId.Value, actionId, StringComparison.Ordinal ) );
	}

	public static bool TryVendorSell(
		InventoryItemSnapshot item,
		out long payout,
		out string reason )
	{
		ArgumentNullException.ThrowIfNull( item );
		payout = 0;
		reason = "Vendor availability is unavailable.";
		if ( !item.State.TryGetValue( HL2RPInventoryItemFields.VendorSellEnabled, out var enabled ) ||
			enabled.Kind != SnapshotValueKind.Boolean ||
			!item.State.TryGetValue( HL2RPInventoryItemFields.VendorSellPayout, out var projectedPayout ) ||
			projectedPayout.Kind != SnapshotValueKind.Integer || projectedPayout.IntegerValue < 0 ||
			!item.State.TryGetValue( HL2RPInventoryItemFields.VendorSellReason, out var projectedReason ) ||
			projectedReason.Kind != SnapshotValueKind.String ) return false;
		payout = projectedPayout.IntegerValue;
		reason = projectedReason.StringValue;
		return enabled.BooleanValue;
	}
}
