#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;
using Hexagon.V2.Persistence;
using HL2RP.V2.Features;

namespace HL2RP.V2.Runtime;

/// <summary>
/// Mutable per-command projection bookkeeping shared by the neutral command sinks and
/// the host's presentation epilogue. Promoted from a private nested host class so the
/// sink seams can carry it through the routing table.
/// </summary>
public sealed class CommandProjectionDelta
{
	public HashSet<InventoryId> Inventories { get; } = new();
	public HashSet<ItemId> Items { get; } = new();
	public HashSet<SceneEntityId> SceneEntities { get; } = new();
	public HashSet<DocumentAddress> Documents { get; } = new();
	public HashSet<ConnectionId> Connections { get; } = new();
	public HashSet<CharacterId> Characters { get; } = new();
	public List<CommitReceipt> Receipts { get; } = new();
	public bool Broadcast { get; set; }
	public bool RebuildLiveInventory { get; set; }
	public bool RebuildCombatTargets { get; set; }
	public bool HasPersistentChanges => Receipts.Count > 0 || Inventories.Count > 0 ||
		Items.Count > 0 || SceneEntities.Count > 0 || Documents.Count > 0 ||
		Connections.Count > 0 || Characters.Count > 0 || Broadcast ||
		RebuildLiveInventory || RebuildCombatTargets;

	public void Observe( CommitReceipt receipt )
	{
		ArgumentNullException.ThrowIfNull( receipt );
		if ( !Receipts.Any( existing => existing.Sequence == receipt.Sequence ) ) Receipts.Add( receipt );
		Documents.UnionWith( receipt.Documents.Select( value => value.Address ) );
	}

	public void Observe( IHL2RPCommittedOperation operation ) => Observe( operation.Commit );
}
