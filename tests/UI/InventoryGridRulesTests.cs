#nullable enable

using Hexagon.V2.Domain;
using Hexagon.V2.Networking;
using HL2RP.UI;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.UI;

[TestClass]
public sealed class InventoryGridRulesTests
{
	[TestMethod]
	public void DropRequiresBothWorkspaceAndPerItemHostAvailability()
	{
		var enabled = Item( canDrop: true );
		var disabled = Item( canDrop: false, dropReason: "Item is explicitly non-droppable." );

		Assert.IsTrue( InventoryGridRules.ShowDrop( true, enabled ) );
		Assert.IsTrue( InventoryGridRules.EnableDrop( true, enabled ) );
		Assert.IsTrue( InventoryGridRules.ShowDrop( true, disabled ) );
		Assert.IsFalse( InventoryGridRules.EnableDrop( true, disabled ) );
		Assert.IsFalse( InventoryGridRules.ShowDrop( false, enabled ) );
		Assert.AreEqual( "Item is explicitly non-droppable.", disabled.DropDisabledReason );
	}

	[TestMethod]
	public void DedicatedAndVendorAvailabilityRemainBoundToTheSelectedItem()
	{
		var action = new ItemActionSnapshot(
			new ActionId( "request" ), "Request", false, "Cooling down",
			ItemActionInvocationKind.DedicatedPanel );
		var item = Item( actions: new[] { action }, state: new Dictionary<string, SnapshotValue>
		{
			[HL2RPInventoryItemFields.VendorSellEnabled] = SnapshotValue.Boolean( true ),
			[HL2RPInventoryItemFields.VendorSellPayout] = SnapshotValue.Integer( 7 ),
			[HL2RPInventoryItemFields.VendorSellReason] = SnapshotValue.String( string.Empty )
		} );

		Assert.AreSame( action, InventoryGridRules.DedicatedAction( item, "request" ) );
		Assert.IsFalse( InventoryGridRules.ShowAction( action ), "Request uses its dedicated text controls." );
		Assert.IsTrue( InventoryGridRules.ShowAction( new ItemActionSnapshot(
			new ActionId( HL2RPIds.Actions.Write ), "Write", true, Invocation: ItemActionInvocationKind.DedicatedPanel ) ) );
		Assert.IsTrue( InventoryGridRules.ShowAction( new ItemActionSnapshot(
			new ActionId( HL2RPIds.Actions.Tune ), "Tune", true, Invocation: ItemActionInvocationKind.DedicatedPanel ) ) );
		Assert.IsTrue( InventoryGridRules.TryVendorSell( item, out var payout, out var reason ) );
		Assert.AreEqual( 7L, payout );
		Assert.AreEqual( string.Empty, reason );
		Assert.IsFalse( InventoryGridRules.TryVendorSell( Item(), out _, out var unavailable ) );
		StringAssert.Contains( unavailable, "unavailable" );
	}

	private static InventoryItemSnapshot Item(
		bool canDrop = false,
		string? dropReason = null,
		IEnumerable<ItemActionSnapshot>? actions = null,
		IReadOnlyDictionary<string, SnapshotValue>? state = null ) => new(
		ItemId.New(), new DefinitionId( "test.item" ), "Item", "Description", "General",
		0, 0, 1, 1, 1, actions, state, canDrop, dropReason );
}
