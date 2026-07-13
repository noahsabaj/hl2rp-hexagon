#nullable enable

using System;
using Hexagon.V2.Domain;
using Hexagon.V2.Networking;
using HL2RP.UI;

namespace HL2RP.V2.Tests.UI;

[TestClass]
public sealed class ItemActionArgumentBuilderTests
{
	[TestMethod]
	public void SplitRequiresPositiveAmountAndUsesIntegerScalar()
	{
		Assert.IsFalse(ItemActionArgumentBuilder.TrySplit(null, out _));
		Assert.IsFalse(ItemActionArgumentBuilder.TrySplit(0, out _));
		Assert.IsFalse(ItemActionArgumentBuilder.TrySplit(-1, out _));

		Assert.IsTrue(ItemActionArgumentBuilder.TrySplit(12, out var arguments));
		Assert.HasCount(1, arguments);
		Assert.AreEqual(SnapshotValueKind.Integer, arguments[ItemActionArgumentBuilder.Amount].Kind);
		Assert.AreEqual(12L, arguments[ItemActionArgumentBuilder.Amount].IntegerValue);
		Assert.IsFalse(ItemActionArgumentBuilder.TrySplit(12, 12, out _));
		Assert.IsFalse(ItemActionArgumentBuilder.TrySplit(13, 12, out _));
		Assert.IsTrue(ItemActionArgumentBuilder.TrySplit(11, 12, out _));
	}

	[TestMethod]
	public void CombineRequiresAnotherStackAndUsesStringItemIdScalar()
	{
		var primary = ItemId.New();
		var other = ItemId.New();

		Assert.IsFalse(ItemActionArgumentBuilder.TryCombine(primary, null, out _));
		Assert.IsFalse(ItemActionArgumentBuilder.TryCombine(primary, primary, out _));
		Assert.IsTrue(ItemActionArgumentBuilder.TryCombine(primary, other, out var arguments));
		Assert.HasCount(1, arguments);
		var value = arguments[ItemActionArgumentBuilder.OtherItemId];
		Assert.AreEqual(SnapshotValueKind.String, value.Kind);
		Assert.AreEqual(other.Value, Guid.Parse(value.StringValue));
	}
}
