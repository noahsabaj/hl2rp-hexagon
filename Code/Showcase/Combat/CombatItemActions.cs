#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Showcase.Combat;

public static class CombatTraitNames
{
	public const string Pistol = "pistol";
	public const string Ammunition = "ammunition";
	public const string Vest = "vest";
}

internal static class CombatPersistence
{
	public static OperationResult<T> Decode<T>(TypedPayload payload, IPersistedTypeCodec<T> codec) where T : class
	{
		if (payload.TypeId.Value != codec.Key.Value || payload.TypeVersion != codec.CurrentVersion)
			return OperationResult<T>.Failure(ErrorCode.PersistedTypeInvalid,
				$"Expected '{codec.Key}' v{codec.CurrentVersion}.");
		try
		{
			return OperationResult<T>.Success(codec.Deserialize(payload.Data, payload.TypeVersion));
		}
		catch (Exception)
		{
			return OperationResult<T>.Failure(ErrorCode.PersistedTypeInvalid,
				$"Payload '{codec.Key}' is malformed.");
		}
	}

	public static OperationResult<T> DecodeTrait<T>(ItemRecord item, string trait,
		IPersistedTypeCodec<T> codec) where T : class
	{
		if (!item.Traits.TryGetValue(trait, out var payload))
			return OperationResult<T>.Failure(ErrorCode.PersistedTypeInvalid,
				$"Item '{item.Id}' is missing required trait '{trait}'.");
		return Decode(payload, codec);
	}

	public static ItemRecord ReplaceTrait<T>(ItemRecord item, string trait,
		IPersistedTypeCodec<T> codec, T value) where T : class
	{
		var traits = item.Traits.ToDictionary(pair => pair.Key, pair => pair.Value.DeepCopy(), StringComparer.Ordinal);
		traits[trait] = HL2RPPersistence.Payload(codec, value);
		return item with { Traits = traits };
	}

	public static OperationResult Failure(PersistenceError error) =>
		OperationResult.Failure(Map(error.Code), error.Message);

	public static OperationResult<T> Failure<T>(PersistenceError error) =>
		OperationResult<T>.Failure(Map(error.Code), error.Message);

	private static ErrorCode Map(PersistenceErrorCode code) => code switch
	{
		PersistenceErrorCode.NotFound => ErrorCode.NotFound,
		PersistenceErrorCode.AlreadyExists => ErrorCode.Conflict,
		PersistenceErrorCode.RevisionConflict => ErrorCode.Conflict,
		PersistenceErrorCode.TypeNotRegistered => ErrorCode.PersistedTypeInvalid,
		PersistenceErrorCode.CollectionTypeMismatch => ErrorCode.PersistedTypeInvalid,
		PersistenceErrorCode.InvalidOperation => ErrorCode.InvalidArgument,
		_ => ErrorCode.InternalError
	};
}

public sealed class EquipCombatItemActionHandler : IItemActionHandler
{
	private readonly ICharacterCombatGate _gate;

	public EquipCombatItemActionHandler(ICharacterCombatGate? gate = null) =>
		_gate = gate ?? new AllowCharacterCombatGate();

	public ActionId Id { get; } = new(HL2RPIds.Actions.Equip);

	public OperationResult<ItemActionPlan> Plan(ItemActionContext context)
	{
		var gate = _gate.Authorize(context.Actor.CharacterId);
		if (gate.Failed) return OperationResult<ItemActionPlan>.Failure(gate.Error!.Code, gate.Error.Message);
		if (context.Item.Definition.Value == HL2RPIds.Items.Pistol)
			return EquipPistol(context);
		if (context.Item.Definition.Value == HL2RPIds.Items.ProtectiveVest)
			return EquipVest(context);
		return OperationResult<ItemActionPlan>.Failure(ErrorCode.PolicyDenied,
			"Equip is not a combat action for this item.");
	}

	private static OperationResult<ItemActionPlan> EquipPistol(ItemActionContext context)
	{
		var state = CombatPersistence.DecodeTrait(context.Item, CombatTraitNames.Pistol, HL2RPPersistence.Pistol);
		if (state.Failed) return Fail(state);
		if (state.Value.MagazineRounds is < 0 or > PistolItemState.MagazineCapacity)
			return OperationResult<ItemActionPlan>.Failure(ErrorCode.PersistedTypeInvalid,
				"Pistol magazine state is outside registered limits.");
		if (state.Value.Equipped)
			return OperationResult<ItemActionPlan>.Failure(ErrorCode.Conflict, "Pistol is already equipped.");

		var updates = new Dictionary<ItemId, ItemRecord>();
		foreach (var candidate in context.InventoryItems.Values
			.Where(item => item.Definition.Value == HL2RPIds.Items.Pistol)
			.OrderBy(item => item.Id.Value))
		{
			var candidateState = CombatPersistence.DecodeTrait(candidate, CombatTraitNames.Pistol, HL2RPPersistence.Pistol);
			if (candidateState.Failed)
				return OperationResult<ItemActionPlan>.Failure(candidateState.Error!.Code, candidateState.Error.Message);
			var equipped = candidate.Id == context.Item.Id;
			if (candidateState.Value.Equipped != equipped || candidateState.Value.Raised)
				updates[candidate.Id] = CombatPersistence.ReplaceTrait(candidate, CombatTraitNames.Pistol,
					HL2RPPersistence.Pistol, candidateState.Value with { Equipped = equipped, Raised = false });
		}
		return OperationResult<ItemActionPlan>.Success(new ItemActionPlan { UpdatedItems = updates });
	}

	private static OperationResult<ItemActionPlan> EquipVest(ItemActionContext context)
	{
		var state = CombatPersistence.DecodeTrait(context.Item, CombatTraitNames.Vest, HL2RPPersistence.ProtectiveVest);
		if (state.Failed) return Fail(state);
		if (state.Value.Durability < 0 || state.Value.DamageReductionPermille is < 0 or > 1000)
			return OperationResult<ItemActionPlan>.Failure(ErrorCode.PersistedTypeInvalid,
				"Protective vest state is outside registered limits.");
		if (state.Value.Durability == 0)
			return OperationResult<ItemActionPlan>.Failure(ErrorCode.PolicyDenied, "Destroyed armor cannot be equipped.");
		if (state.Value.Equipped)
			return OperationResult<ItemActionPlan>.Failure(ErrorCode.Conflict, "Protective vest is already equipped.");

		var updates = new Dictionary<ItemId, ItemRecord>();
		foreach (var candidate in context.InventoryItems.Values
			.Where(item => item.Definition.Value == HL2RPIds.Items.ProtectiveVest)
			.OrderBy(item => item.Id.Value))
		{
			var candidateState = CombatPersistence.DecodeTrait(candidate, CombatTraitNames.Vest, HL2RPPersistence.ProtectiveVest);
			if (candidateState.Failed)
				return OperationResult<ItemActionPlan>.Failure(candidateState.Error!.Code, candidateState.Error.Message);
			var equipped = candidate.Id == context.Item.Id;
			if (candidateState.Value.Equipped != equipped)
				updates[candidate.Id] = CombatPersistence.ReplaceTrait(candidate, CombatTraitNames.Vest,
					HL2RPPersistence.ProtectiveVest, candidateState.Value with { Equipped = equipped });
		}
		return OperationResult<ItemActionPlan>.Success(new ItemActionPlan { UpdatedItems = updates });
	}

	private static OperationResult<ItemActionPlan> Fail<T>(OperationResult<T> result) =>
		OperationResult<ItemActionPlan>.Failure(result.Error!.Code, result.Error.Message);
}

public sealed class UnequipCombatItemActionHandler : IItemActionHandler
{
	private readonly ICharacterCombatGate _gate;

	public UnequipCombatItemActionHandler(ICharacterCombatGate? gate = null) =>
		_gate = gate ?? new AllowCharacterCombatGate();

	public ActionId Id { get; } = new(HL2RPIds.Actions.Unequip);

	public OperationResult<ItemActionPlan> Plan(ItemActionContext context)
	{
		var gate = _gate.Authorize(context.Actor.CharacterId);
		if (gate.Failed) return OperationResult<ItemActionPlan>.Failure(gate.Error!.Code, gate.Error.Message);
		if (context.Item.Definition.Value == HL2RPIds.Items.Pistol)
		{
			var state = CombatPersistence.DecodeTrait(context.Item, CombatTraitNames.Pistol, HL2RPPersistence.Pistol);
			if (state.Failed) return Failure(state);
			if (state.Value.MagazineRounds is < 0 or > PistolItemState.MagazineCapacity)
				return OperationResult<ItemActionPlan>.Failure(ErrorCode.PersistedTypeInvalid,
					"Pistol magazine state is outside registered limits.");
			if (!state.Value.Equipped)
				return OperationResult<ItemActionPlan>.Failure(ErrorCode.Conflict, "Pistol is not equipped.");
			return Updated(context.Item, CombatTraitNames.Pistol, HL2RPPersistence.Pistol,
				state.Value with { Equipped = false, Raised = false });
		}
		if (context.Item.Definition.Value == HL2RPIds.Items.ProtectiveVest)
		{
			var state = CombatPersistence.DecodeTrait(context.Item, CombatTraitNames.Vest, HL2RPPersistence.ProtectiveVest);
			if (state.Failed) return Failure(state);
			if (state.Value.Durability < 0 || state.Value.DamageReductionPermille is < 0 or > 1000)
				return OperationResult<ItemActionPlan>.Failure(ErrorCode.PersistedTypeInvalid,
					"Protective vest state is outside registered limits.");
			if (!state.Value.Equipped)
				return OperationResult<ItemActionPlan>.Failure(ErrorCode.Conflict, "Protective vest is not equipped.");
			return Updated(context.Item, CombatTraitNames.Vest, HL2RPPersistence.ProtectiveVest,
				state.Value with { Equipped = false });
		}
		return OperationResult<ItemActionPlan>.Failure(ErrorCode.PolicyDenied,
			"Unequip is not a combat action for this item.");
	}

	private static OperationResult<ItemActionPlan> Updated<T>(ItemRecord item, string trait,
		IPersistedTypeCodec<T> codec, T state) where T : class =>
		OperationResult<ItemActionPlan>.Success(new ItemActionPlan
		{
			UpdatedItems = new Dictionary<ItemId, ItemRecord>
			{
				[item.Id] = CombatPersistence.ReplaceTrait(item, trait, codec, state)
			}
		});

	private static OperationResult<ItemActionPlan> Failure<T>(OperationResult<T> result) =>
		OperationResult<ItemActionPlan>.Failure(result.Error!.Code, result.Error.Message);
}

public sealed class ReloadPistolItemActionHandler : IItemActionHandler
{
	private readonly ICharacterCombatGate _gate;

	public ReloadPistolItemActionHandler(ICharacterCombatGate? gate = null) =>
		_gate = gate ?? new AllowCharacterCombatGate();

	public ActionId Id { get; } = new(HL2RPIds.Actions.Reload);

	public OperationResult<ItemActionPlan> Plan(ItemActionContext context)
	{
		var gate = _gate.Authorize(context.Actor.CharacterId);
		if (gate.Failed) return OperationResult<ItemActionPlan>.Failure(gate.Error!.Code, gate.Error.Message);
		if (context.Item.Definition.Value != HL2RPIds.Items.Pistol)
			return OperationResult<ItemActionPlan>.Failure(ErrorCode.PolicyDenied, "Reload requires a pistol.");
		var pistol = CombatPersistence.DecodeTrait(context.Item, CombatTraitNames.Pistol, HL2RPPersistence.Pistol);
		if (pistol.Failed) return Failure(pistol);
		if (!pistol.Value.Equipped || pistol.Value.Raised)
			return OperationResult<ItemActionPlan>.Failure(ErrorCode.PolicyDenied,
				"Pistol must be equipped and lowered before reloading.");
		if (pistol.Value.MagazineRounds is < 0 or > PistolItemState.MagazineCapacity)
			return OperationResult<ItemActionPlan>.Failure(ErrorCode.PersistedTypeInvalid, "Pistol magazine state is invalid.");
		var needed = PistolItemState.MagazineCapacity - pistol.Value.MagazineRounds;
		if (needed == 0)
			return OperationResult<ItemActionPlan>.Failure(ErrorCode.Conflict, "Pistol magazine is already full.");

		var ammunition = context.InventoryItems.Values
			.Where(item => item.Definition.Value == HL2RPIds.Items.PistolAmmunition)
			.OrderBy(item => item.Id.Value)
			.Select(item => (Item: item, State: CombatPersistence.DecodeTrait(item,
				CombatTraitNames.Ammunition, HL2RPPersistence.PistolAmmunition)))
			.ToArray();
		foreach (var candidate in ammunition)
		{
			if (candidate.State.Failed)
				return OperationResult<ItemActionPlan>.Failure(candidate.State.Error!.Code, candidate.State.Error.Message);
			if (candidate.State.Value.Rounds is < 0 or > PistolItemState.MagazineCapacity)
				return OperationResult<ItemActionPlan>.Failure(ErrorCode.PersistedTypeInvalid,
					"Ammunition count is outside registered limits.");
		}
		var source = ammunition.FirstOrDefault(value => value.State.Value.Rounds > 0);
		if (source.Item is null)
			return OperationResult<ItemActionPlan>.Failure(ErrorCode.NotFound, "No pistol ammunition is available.");
		var transferred = Math.Min(needed, source.State.Value.Rounds);
		var updates = new Dictionary<ItemId, ItemRecord>
		{
			[context.Item.Id] = CombatPersistence.ReplaceTrait(context.Item, CombatTraitNames.Pistol,
				HL2RPPersistence.Pistol, pistol.Value with { MagazineRounds = pistol.Value.MagazineRounds + transferred }),
			[source.Item.Id] = CombatPersistence.ReplaceTrait(source.Item, CombatTraitNames.Ammunition,
				HL2RPPersistence.PistolAmmunition, source.State.Value with { Rounds = source.State.Value.Rounds - transferred })
		};
		return OperationResult<ItemActionPlan>.Success(new ItemActionPlan { UpdatedItems = updates });
	}

	private static OperationResult<ItemActionPlan> Failure<T>(OperationResult<T> result) =>
		OperationResult<ItemActionPlan>.Failure(result.Error!.Code, result.Error.Message);
}

public sealed class ReplenishPistolAmmunitionItemActionHandler : IItemActionHandler
{
	private readonly ICharacterCombatGate _gate;

	public ReplenishPistolAmmunitionItemActionHandler(ICharacterCombatGate? gate = null) =>
		_gate = gate ?? new AllowCharacterCombatGate();

	public ActionId Id { get; } = new(HL2RPIds.Actions.Replenish);

	public OperationResult<ItemActionPlan> Plan(ItemActionContext context)
	{
		var gate = _gate.Authorize(context.Actor.CharacterId);
		if (gate.Failed) return OperationResult<ItemActionPlan>.Failure(gate.Error!.Code, gate.Error.Message);
		if (context.Item.Definition.Value != HL2RPIds.Items.PistolAmmunition)
			return OperationResult<ItemActionPlan>.Failure(ErrorCode.PolicyDenied,
				"Ammunition replenishment requires a pistol ammunition item.");
		var state = CombatPersistence.DecodeTrait(context.Item, CombatTraitNames.Ammunition,
			HL2RPPersistence.PistolAmmunition);
		if (state.Failed)
			return OperationResult<ItemActionPlan>.Failure(state.Error!.Code, state.Error.Message);
		if (state.Value.Rounds is < 0 or > PistolItemState.MagazineCapacity)
			return OperationResult<ItemActionPlan>.Failure(ErrorCode.PersistedTypeInvalid,
				"Ammunition count is outside registered limits.");
		if (state.Value.Rounds == PistolItemState.MagazineCapacity)
			return OperationResult<ItemActionPlan>.Failure(ErrorCode.Conflict, "Ammunition is already replenished.");
		return OperationResult<ItemActionPlan>.Success(new ItemActionPlan
		{
			UpdatedItems = new Dictionary<ItemId, ItemRecord>
			{
				[context.Item.Id] = CombatPersistence.ReplaceTrait(context.Item, CombatTraitNames.Ammunition,
					HL2RPPersistence.PistolAmmunition,
					state.Value with { Rounds = PistolItemState.MagazineCapacity })
			}
		});
	}
}
