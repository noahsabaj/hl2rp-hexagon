#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using HL2RP.V2.Domain;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Showcase.Combat;

public sealed record ProtectiveVestDamageReceipt(
	long IncomingDamage,
	long AbsorbedDamage,
	long AppliedDamage,
	int RemainingDurability,
	long CommitSequence);

public sealed record ProtectiveVestDamagePlan(
	long IncomingDamage,
	long AbsorbedDamage,
	long AppliedDamage,
	ProtectiveVestItemState NextState);

public static class ProtectiveVestDamagePlanner
{
	public static OperationResult<ProtectiveVestDamagePlan> Plan(
		ProtectiveVestItemState state,
		long incomingDamage)
	{
		ArgumentNullException.ThrowIfNull(state);
		if (incomingDamage <= 0)
			return OperationResult<ProtectiveVestDamagePlan>.Failure(ErrorCode.InvalidArgument,
				"Incoming damage must be positive.");
		if (!state.Equipped || state.Durability <= 0)
			return OperationResult<ProtectiveVestDamagePlan>.Failure(ErrorCode.PolicyDenied,
				"Protective vest is not equipped or has no durability.");
		if (state.DamageReductionPermille is < 0 or > 1000)
			return OperationResult<ProtectiveVestDamagePlan>.Failure(ErrorCode.PersistedTypeInvalid,
				"Protective vest reduction is outside registered limits.");

		var nominalAbsorption = incomingDamage / 1000 * state.DamageReductionPermille
			+ incomingDamage % 1000 * state.DamageReductionPermille / 1000;
		var absorbed = Math.Min(nominalAbsorption, state.Durability);
		var applied = incomingDamage - absorbed;
		var durability = checked(state.Durability - (int)absorbed);
		return OperationResult<ProtectiveVestDamagePlan>.Success(new ProtectiveVestDamagePlan(
			incomingDamage,
			absorbed,
			applied,
			state with { Durability = durability, Equipped = durability > 0 }));
	}
}

/// <summary>
/// Commits vest durability before returning damage for the host health boundary.
/// A caller must apply damage only after receiving a successful receipt.
/// </summary>
public sealed class ProtectiveVestService
{
	private readonly DomainRepositories _repositories;
	private readonly InventoryAccessService _access;
	private readonly ICharacterCombatGate _gate;

	public ProtectiveVestService(
		DomainRepositories repositories,
		InventoryAccessService access,
		ICharacterCombatGate? gate = null)
	{
		_repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
		_access = access ?? throw new ArgumentNullException(nameof(access));
		_gate = gate ?? new AllowCharacterCombatGate();
	}

	public async ValueTask<OperationResult<ProtectiveVestDamageReceipt>> ApplyAsync(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId vestItemId,
		long incomingDamage,
		CancellationToken cancellationToken = default)
	{
		if (incomingDamage <= 0)
			return OperationResult<ProtectiveVestDamageReceipt>.Failure(ErrorCode.InvalidArgument,
				"Incoming damage must be positive.");
		var gate = _gate.Authorize(actor.CharacterId);
		if (gate.Failed) return Failure(gate.Error!);
		var character = _repositories.Characters.Find(DomainKeys.Character(actor.CharacterId));
		var inventory = _repositories.Inventories.Find(DomainKeys.Inventory(inventoryId));
		var vest = _repositories.Items.Find(DomainKeys.Item(vestItemId));
		if (character is null || inventory is null || vest is null)
			return OperationResult<ProtectiveVestDamageReceipt>.Failure(ErrorCode.NotFound,
				"Character, inventory, or protective vest was not found.");
		if (character.Value.AccountId != actor.AccountId)
			return OperationResult<ProtectiveVestDamageReceipt>.Failure(ErrorCode.Unauthorized,
				"Active character does not belong to the authenticated actor.");
		if (!_access.Has(actor.ConnectionId, actor.CharacterId, inventoryId, InventoryCapability.View))
			return OperationResult<ProtectiveVestDamageReceipt>.Failure(ErrorCode.Unauthorized,
				"Protective vest view capability is missing.");
		if (inventory.Value.Find(vestItemId) is null ||
			vest.Value.Definition.Value != HL2RPIds.Items.ProtectiveVest)
			return OperationResult<ProtectiveVestDamageReceipt>.Failure(ErrorCode.NotFound,
				"Claimed protective vest is not in the inventory.");
		var state = CombatPersistence.DecodeTrait(vest.Value, CombatTraitNames.Vest,
			HL2RPPersistence.ProtectiveVest);
		if (state.Failed) return Failure(state.Error!);
		var plan = ProtectiveVestDamagePlanner.Plan(state.Value, incomingDamage);
		if (plan.Failed) return Failure(plan.Error!);

		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		var editor = unitOfWork.Edit(_repositories.Items, vest);
		if (editor is null)
		{
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return OperationResult<ProtectiveVestDamageReceipt>.Failure(ErrorCode.Conflict,
				"Protective vest changed before damage was applied.");
		}
		editor.Replace(CombatPersistence.ReplaceTrait(editor.Value, CombatTraitNames.Vest,
			HL2RPPersistence.ProtectiveVest, plan.Value.NextState));
		unitOfWork.Save(editor);
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		if (!committed.Succeeded)
			return CombatPersistence.Failure<ProtectiveVestDamageReceipt>(committed.Error!);
		return OperationResult<ProtectiveVestDamageReceipt>.Success(new ProtectiveVestDamageReceipt(
			incomingDamage, plan.Value.AbsorbedDamage, plan.Value.AppliedDamage,
			plan.Value.NextState.Durability, committed.Value!.Sequence));
	}

	private static OperationResult<ProtectiveVestDamageReceipt> Failure(OperationError error) =>
		OperationResult<ProtectiveVestDamageReceipt>.Failure(error.Code, error.Message);
}
