#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Persistence;

namespace HL2RP.V2.Domain;

/// <summary>
/// Implemented by every durable HL2RP operation result. The presentation layer
/// consumes this provider-issued value directly and never reconstructs document
/// versions from command arguments.
/// </summary>
public interface IHL2RPCommittedOperation
{
	CommitReceipt Commit { get; }
}

/// <summary>
/// Whitelist-safe unit-of-work finalization. C# await-using lowers through
/// forbidden exception-rethrow helpers, which s&amp;box intentionally rejects; every HL2RP
/// transaction therefore commits and disposes explicitly through this helper.
/// </summary>
public static class HL2RPUnitOfWork
{
	public static async ValueTask<PersistenceResult<CommitReceipt>> CommitAndDisposeAsync(
		IUnitOfWork unitOfWork,
		CancellationToken cancellationToken = default)
	{
		var committed = await unitOfWork.CommitAsync(cancellationToken);
		await unitOfWork.DisposeAsync();
		return committed;
	}

	public static ValueTask DisposeAsync(IUnitOfWork unitOfWork) => unitOfWork.DisposeAsync();

	/// <summary>
	/// Pins the authenticated character and, when present, its lifecycle guard.
	/// Special HL2RP action routes use this alongside an InventoryAccessProof so
	/// character deletion and reference-lifecycle changes cannot race a commit.
	/// </summary>
	public static void RequireActorState(
		IUnitOfWork unitOfWork,
		DomainRepositories repositories,
		DocumentSnapshot<CharacterRecord> character)
	{
		ArgumentNullException.ThrowIfNull(unitOfWork);
		ArgumentNullException.ThrowIfNull(repositories);
		ArgumentNullException.ThrowIfNull(character);
		unitOfWork.RequireUnchanged(repositories.Characters, character);
		var guard = repositories.CharacterLifecycleGuards.Find(
			DomainKeys.CharacterLifecycleGuard(character.Value.Id));
		if (guard is not null)
			unitOfWork.RequireUnchanged(repositories.CharacterLifecycleGuards, guard);
	}

	public static IReadOnlyList<DocumentSnapshot<ItemRecord>> CaptureInventoryLayout(
		DomainRepositories repositories,
		InventoryRecord inventory)
	{
		ArgumentNullException.ThrowIfNull(repositories);
		ArgumentNullException.ThrowIfNull(inventory);
		var items = new List<DocumentSnapshot<ItemRecord>>(inventory.Placements.Count);
		foreach (var placement in inventory.Placements)
		{
			var item = repositories.Items.Find(DomainKeys.Item(placement.ItemId));
			if (item is not null) items.Add(item);
		}
		return items;
	}

	public static void RequireInventoryLayout(
		IUnitOfWork unitOfWork,
		DomainRepositories repositories,
		IReadOnlyList<DocumentSnapshot<ItemRecord>> items)
	{
		ArgumentNullException.ThrowIfNull(unitOfWork);
		ArgumentNullException.ThrowIfNull(repositories);
		ArgumentNullException.ThrowIfNull(items);
		foreach (var item in items)
			unitOfWork.RequireUnchanged(repositories.Items, item);
	}
}
