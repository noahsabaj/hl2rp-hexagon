#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Hexagon.V2.Domain;
using Hexagon.V2.Networking;

namespace HL2RP.UI;

/// <summary>
/// Neutral construction boundary for the only showcase item actions which
/// require typed arguments. Callers must select values from current UI state.
/// </summary>
public static class ItemActionArgumentBuilder
{
	public const string Amount = "amount";
	public const string OtherItemId = "other_item_id";

	public static bool TrySplit(long? amount,
		out IReadOnlyDictionary<string, SnapshotValue> arguments)
	{
		arguments = Empty;
		if (amount is not > 0) return false;
		arguments = ReadOnly(new Dictionary<string, SnapshotValue>(StringComparer.Ordinal)
		{
			[Amount] = SnapshotValue.Integer(amount.Value)
		});
		return true;
	}

	public static bool TrySplit(long? amount, long visibleQuantity,
		out IReadOnlyDictionary<string, SnapshotValue> arguments)
	{
		if (visibleQuantity <= 1 || amount is null || amount.Value >= visibleQuantity)
		{
			arguments = Empty;
			return false;
		}
		return TrySplit(amount, out arguments);
	}

	public static bool TryCombine(ItemId primaryItemId, ItemId? otherItemId,
		out IReadOnlyDictionary<string, SnapshotValue> arguments)
	{
		arguments = Empty;
		if (otherItemId is not ItemId other || other == primaryItemId) return false;
		arguments = ReadOnly(new Dictionary<string, SnapshotValue>(StringComparer.Ordinal)
		{
			[OtherItemId] = SnapshotValue.String(other.Value.ToString("D"))
		});
		return true;
	}

	private static IReadOnlyDictionary<string, SnapshotValue> ReadOnly(
		Dictionary<string, SnapshotValue> values) => new ReadOnlyDictionary<string, SnapshotValue>(values);

	private static IReadOnlyDictionary<string, SnapshotValue> Empty { get; } =
		new ReadOnlyDictionary<string, SnapshotValue>(
			new Dictionary<string, SnapshotValue>(StringComparer.Ordinal));
}
