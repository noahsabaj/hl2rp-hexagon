#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Showcase.Combat;

public interface ICharacterCombatGate
{
	OperationResult Authorize(CharacterId characterId);
}

public sealed class AllowCharacterCombatGate : ICharacterCombatGate
{
	public OperationResult Authorize(CharacterId characterId) => OperationResult.Success();
}

public sealed record PistolRaiseTicket(
	Guid Id,
	InventoryActor Actor,
	InventoryId InventoryId,
	ItemId PistolId,
	DateTimeOffset ReadyAtUtc);

public sealed record PistolFireIntent(
	InventoryActor Actor,
	InventoryId InventoryId,
	ItemId PistolId);

public sealed record AuthoritativeShot(
	WorldPoint Origin,
	WorldPoint End,
	string? TargetToken,
	bool Hit);

public sealed record PreparedCombatDamage(AuthoritativeShot Shot, long Damage, Guid PlanId = default);

public interface IAuthoritativePistolRaycast
{
	OperationResult<AuthoritativeShot> Resolve(PistolFireIntent intent);
}

/// <summary>
/// Prepare must be side-effect free. Commit is called only after the ammunition
/// transaction is durable and must be an infallible application of the prepared result.
/// </summary>
public interface ICombatDamageBoundary
{
	OperationResult<PreparedCombatDamage> Prepare(AuthoritativeShot shot, long damage);
	/// <summary>
	/// Stages any durable target mutation in the same unit of work as ammunition.
	/// This method must not publish effects or mutate committed state directly.
	/// </summary>
	OperationResult Stage(IUnitOfWork unitOfWork, PreparedCombatDamage damage);
	void Commit(PreparedCombatDamage damage);
	void Abort(PreparedCombatDamage damage) { }
}

public sealed record PistolFireReceipt(
	ItemId PistolId,
	int RemainingRounds,
	AuthoritativeShot Shot,
	long CommitSequence,
	Guid DamagePlanId = default);

/// <summary>
/// Host-only raise/fire boundary. Client intent contains no transform, target,
/// damage, ammunition count, or raise state.
/// </summary>
public sealed class PistolCombatService
{
	public static readonly TimeSpan DefaultRaiseDelay = TimeSpan.FromMilliseconds(350);
	public static readonly TimeSpan DefaultFireInterval = TimeSpan.FromMilliseconds(200);

	private readonly DomainRepositories _repositories;
	private readonly InventoryAccessService _access;
	private readonly IHexClock _clock;
	private readonly IAuthoritativePistolRaycast _raycast;
	private readonly ICombatDamageBoundary _damage;
	private readonly ICharacterCombatGate _gate;
	private readonly TimeSpan _raiseDelay;
	private readonly TimeSpan _fireInterval;
	private readonly long _damagePerShot;
	private readonly Func<Guid> _createTicketId;
	private readonly Dictionary<Guid, PistolRaiseTicket> _pendingRaises = new();
	private readonly HashSet<(CharacterId CharacterId, ItemId ItemId)> _raisedThisSession = new();

	public PistolCombatService(
		DomainRepositories repositories,
		InventoryAccessService access,
		IHexClock clock,
		IAuthoritativePistolRaycast raycast,
		ICombatDamageBoundary damage,
		ICharacterCombatGate? gate = null,
		TimeSpan? raiseDelay = null,
		TimeSpan? fireInterval = null,
		long damagePerShot = 20,
		Func<Guid>? createTicketId = null)
	{
		_repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
		_access = access ?? throw new ArgumentNullException(nameof(access));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
		_raycast = raycast ?? throw new ArgumentNullException(nameof(raycast));
		_damage = damage ?? throw new ArgumentNullException(nameof(damage));
		_gate = gate ?? new AllowCharacterCombatGate();
		_raiseDelay = raiseDelay ?? DefaultRaiseDelay;
		_fireInterval = fireInterval ?? DefaultFireInterval;
		_damagePerShot = damagePerShot;
		_createTicketId = createTicketId ?? Guid.NewGuid;
		if (_raiseDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(raiseDelay));
		if (_fireInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(fireInterval));
		if (_damagePerShot <= 0) throw new ArgumentOutOfRangeException(nameof(damagePerShot));
	}

	public OperationResult<PistolRaiseTicket> BeginRaise(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId pistolId)
	{
		var resolved = Resolve(actor, inventoryId, pistolId);
		if (resolved.Failed) return Failure<PistolRaiseTicket>(resolved.Error!);
		if (!resolved.Value.State.Equipped)
			return OperationResult<PistolRaiseTicket>.Failure(ErrorCode.PolicyDenied,
				"Pistol must be equipped before it can be raised.");
		if (resolved.Value.State.Raised || _raisedThisSession.Contains((actor.CharacterId, pistolId)))
			return OperationResult<PistolRaiseTicket>.Failure(ErrorCode.Conflict, "Pistol is already raised.");
		foreach (var stale in _pendingRaises.Values
			.Where(ticket => ticket.Actor.ConnectionId == actor.ConnectionId && ticket.PistolId == pistolId)
			.Select(ticket => ticket.Id).ToArray())
			_pendingRaises.Remove(stale);
		var ticket = new PistolRaiseTicket(_createTicketId(), actor, inventoryId, pistolId,
			_clock.UtcNow + _raiseDelay);
		if (ticket.Id == Guid.Empty)
			return OperationResult<PistolRaiseTicket>.Failure(ErrorCode.InternalError, "Raise ticket identity is invalid.");
		_pendingRaises.Add(ticket.Id, ticket);
		return OperationResult<PistolRaiseTicket>.Success(ticket);
	}

	public async ValueTask<OperationResult> CompleteRaiseAsync(
		Guid ticketId,
		InventoryActor actor,
		CancellationToken cancellationToken = default)
	{
		if (!_pendingRaises.TryGetValue(ticketId, out var ticket) || ticket.Actor != actor)
			return OperationResult.Failure(ErrorCode.Unauthorized,
				"Raise ticket is stale or bound to another actor.");
		if (_clock.UtcNow < ticket.ReadyAtUtc)
			return OperationResult.Failure(ErrorCode.Conflict, "Pistol raise delay has not completed.");
		_pendingRaises.Remove(ticketId);
		var resolved = Resolve(actor, ticket.InventoryId, ticket.PistolId);
		if (resolved.Failed) return Failure(resolved.Error!);
		if (!resolved.Value.State.Equipped)
			return OperationResult.Failure(ErrorCode.PolicyDenied, "Pistol is no longer equipped.");
		var committed = await ReplaceStateAsync(resolved.Value,
			resolved.Value.State with { Raised = true }, cancellationToken);
		if (committed.Succeeded)
			_raisedThisSession.Add((actor.CharacterId, ticket.PistolId));
		return committed;
	}

	public bool CancelRaise(Guid ticketId, InventoryActor actor)
	{
		if (!_pendingRaises.TryGetValue(ticketId, out var ticket) || ticket.Actor != actor) return false;
		_pendingRaises.Remove(ticketId);
		return true;
	}

	public async ValueTask<OperationResult> LowerAsync(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId pistolId,
		CancellationToken cancellationToken = default)
	{
		var resolved = Resolve(actor, inventoryId, pistolId);
		if (resolved.Failed) return Failure(resolved.Error!);
		if (!resolved.Value.State.Raised)
			return OperationResult.Failure(ErrorCode.Conflict, "Pistol is already lowered.");
		var committed = await ReplaceStateAsync(resolved.Value,
			resolved.Value.State with { Raised = false }, cancellationToken);
		if (committed.Succeeded)
		{
			_raisedThisSession.Remove((actor.CharacterId, pistolId));
			RemovePending(actor.CharacterId, pistolId);
		}
		return committed;
	}

	public async ValueTask<OperationResult<PistolFireReceipt>> FireAsync(
		PistolFireIntent intent,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(intent);
		var resolved = Resolve(intent.Actor, intent.InventoryId, intent.PistolId);
		if (resolved.Failed) return Failure<PistolFireReceipt>(resolved.Error!);
		var state = resolved.Value.State;
		if (!state.Equipped || !state.Raised ||
			!_raisedThisSession.Contains((intent.Actor.CharacterId, intent.PistolId)))
			return OperationResult<PistolFireReceipt>.Failure(ErrorCode.PolicyDenied,
				"Pistol is not host-raised for this combat session.");
		if (state.MagazineRounds <= 0)
			return OperationResult<PistolFireReceipt>.Failure(ErrorCode.Conflict, "Pistol magazine is empty.");
		var now = _clock.UtcNow;
		if (state.LastFiredAtUtc is DateTimeOffset lastFired &&
			(now < lastFired || now - lastFired < _fireInterval))
			return OperationResult<PistolFireReceipt>.Failure(ErrorCode.PolicyDenied, "Pistol fire rate exceeded.");

		OperationResult<AuthoritativeShot> shot;
		OperationResult<PreparedCombatDamage> prepared;
		try
		{
			shot = _raycast.Resolve(intent);
			if (shot.Failed) return Failure<PistolFireReceipt>(shot.Error!);
			prepared = _damage.Prepare(shot.Value, shot.Value.Hit ? _damagePerShot : 0);
			if (prepared.Failed) return Failure<PistolFireReceipt>(prepared.Error!);
		}
		catch (Exception)
		{
			return OperationResult<PistolFireReceipt>.Failure(ErrorCode.InternalError,
				"Authoritative combat boundary failed closed.");
		}

		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		var editor = unitOfWork.Edit(_repositories.Items, resolved.Value.Document);
		if (editor is null)
		{
			_damage.Abort(prepared.Value);
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return OperationResult<PistolFireReceipt>.Failure(ErrorCode.Conflict, "Pistol changed before firing.");
		}
		var nextState = state with
		{
			MagazineRounds = state.MagazineRounds - 1,
			LastFiredAtUtc = now
		};
		editor.Replace(CombatPersistence.ReplaceTrait(editor.Value, CombatTraitNames.Pistol,
			HL2RPPersistence.Pistol, nextState));
		unitOfWork.Save(editor);
		OperationResult stagedDamage;
		try
		{
			stagedDamage = _damage.Stage(unitOfWork, prepared.Value);
		}
		catch (Exception)
		{
			_damage.Abort(prepared.Value);
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return OperationResult<PistolFireReceipt>.Failure(ErrorCode.InternalError,
				"Authoritative combat staging failed closed.");
		}
		if (stagedDamage.Failed)
		{
			_damage.Abort(prepared.Value);
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return Failure<PistolFireReceipt>(stagedDamage.Error!);
		}
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		if (!committed.Succeeded)
		{
			_damage.Abort(prepared.Value);
			return CombatPersistence.Failure<PistolFireReceipt>(committed.Error!);
		}

		_damage.Commit(prepared.Value);
		return OperationResult<PistolFireReceipt>.Success(new PistolFireReceipt(
			intent.PistolId, nextState.MagazineRounds, shot.Value, committed.Value!.Sequence,
			prepared.Value.PlanId));
	}

	public OperationResult<bool> IsHostRaised(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId pistolId)
	{
		var resolved = Resolve(actor, inventoryId, pistolId);
		return resolved.Succeeded
			? OperationResult<bool>.Success(resolved.Value.State.Equipped && resolved.Value.State.Raised &&
				_raisedThisSession.Contains((actor.CharacterId, pistolId)))
			: Failure<bool>(resolved.Error!);
	}

	public void ClearCharacter(CharacterId characterId)
	{
		foreach (var key in _pendingRaises.Where(pair => pair.Value.Actor.CharacterId == characterId)
			.Select(pair => pair.Key).ToArray())
			_pendingRaises.Remove(key);
		_raisedThisSession.RemoveWhere(value => value.CharacterId == characterId);
	}

	private OperationResult<ResolvedPistol> Resolve(InventoryActor actor, InventoryId inventoryId, ItemId pistolId)
	{
		var gate = _gate.Authorize(actor.CharacterId);
		if (gate.Failed) return OperationResult<ResolvedPistol>.Failure(gate.Error!.Code, gate.Error.Message);
		var character = _repositories.Characters.Find(DomainKeys.Character(actor.CharacterId));
		var inventory = _repositories.Inventories.Find(DomainKeys.Inventory(inventoryId));
		var item = _repositories.Items.Find(DomainKeys.Item(pistolId));
		if (character is null || inventory is null || item is null)
			return OperationResult<ResolvedPistol>.Failure(ErrorCode.NotFound,
				"Character, inventory, or pistol was not found.");
		if (character.Value.AccountId != actor.AccountId)
			return OperationResult<ResolvedPistol>.Failure(ErrorCode.Unauthorized,
				"Active character does not belong to the authenticated actor.");
		if (!_access.Has(actor.ConnectionId, actor.CharacterId, inventoryId,
			InventoryCapability.View | InventoryCapability.Use))
			return OperationResult<ResolvedPistol>.Failure(ErrorCode.Unauthorized, "Pistol use capability is missing.");
		if (inventory.Value.Find(pistolId) is null || item.Value.Definition.Value != HL2RPIds.Items.Pistol)
			return OperationResult<ResolvedPistol>.Failure(ErrorCode.NotFound,
				"Claimed pistol is not a member of the inventory.");
		var state = CombatPersistence.DecodeTrait(item.Value, CombatTraitNames.Pistol, HL2RPPersistence.Pistol);
		if (state.Failed) return Failure<ResolvedPistol>(state.Error!);
		if (state.Value.MagazineRounds is < 0 or > PistolItemState.MagazineCapacity)
			return OperationResult<ResolvedPistol>.Failure(ErrorCode.PersistedTypeInvalid,
				"Pistol magazine state is outside registered limits.");
		return OperationResult<ResolvedPistol>.Success(new ResolvedPistol(item, state.Value));
	}

	private async ValueTask<OperationResult> ReplaceStateAsync(
		ResolvedPistol resolved,
		PistolItemState state,
		CancellationToken cancellationToken)
	{
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		var editor = unitOfWork.Edit(_repositories.Items, resolved.Document);
		if (editor is null)
		{
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return OperationResult.Failure(ErrorCode.Conflict, "Pistol changed.");
		}
		editor.Replace(CombatPersistence.ReplaceTrait(editor.Value, CombatTraitNames.Pistol,
			HL2RPPersistence.Pistol, state));
		unitOfWork.Save(editor);
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		return committed.Succeeded ? OperationResult.Success() : CombatPersistence.Failure(committed.Error!);
	}

	private void RemovePending(CharacterId characterId, ItemId pistolId)
	{
		foreach (var key in _pendingRaises.Where(pair => pair.Value.Actor.CharacterId == characterId &&
			pair.Value.PistolId == pistolId).Select(pair => pair.Key).ToArray())
			_pendingRaises.Remove(key);
	}

	private static OperationResult Failure(OperationError error) =>
		OperationResult.Failure(error.Code, error.Message);

	private static OperationResult<T> Failure<T>(OperationError error) =>
		OperationResult<T>.Failure(error.Code, error.Message);

	private sealed record ResolvedPistol(
		DocumentSnapshot<ItemRecord> Document,
		PistolItemState State);
}
