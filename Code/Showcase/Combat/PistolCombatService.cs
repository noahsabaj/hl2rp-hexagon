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
	CommitReceipt Commit,
	Guid DamagePlanId = default) : IHL2RPCommittedOperation;

public enum PistolStateTransitionKind
{
	Raise,
	Lower
}

/// <summary>
/// Typed result for a host-authoritative pistol transition. Commit is present
/// exactly when this transition changed durable state. Raise completion is
/// deliberately session-only; FireAsync persists the first raised state in the
/// same transaction as ammunition and damage.
/// </summary>
public sealed record PistolStateTransitionReceipt(
	CharacterId CharacterId,
	InventoryId InventoryId,
	ItemId PistolId,
	PistolItemState Before,
	PistolItemState After,
	PistolStateTransitionKind Kind,
	CommitReceipt? Commit)
{
	public bool HasDurableChanges => Commit is not null;
}

/// <summary>
/// Result of one atomic lifecycle reconciliation. Commit is null only when no
/// persisted pistol required a change.
/// </summary>
public sealed record PistolLifecycleClearReceipt(
	CharacterId? CharacterId,
	IReadOnlyList<ItemId> ChangedPistols,
	CommitReceipt? Commit)
{
	public bool HasDurableChanges => Commit is not null;
}

/// <summary>
/// Immutable observation used to compose pistol lifecycle cleanup into a
/// caller-owned transaction. Preparing and staging do not clear any transient
/// raise authority; completion does so only after the shared commit is proven.
/// </summary>
public sealed class PreparedPistolLifecycleClear
{
	internal PreparedPistolLifecycleClear(
		PistolCombatService owner,
		CharacterId? characterId,
		IReadOnlyList<DocumentSnapshot<InventoryRecord>> inventories,
		IReadOnlyList<PreparedPistolClearEntry> pistols)
	{
		Owner = owner;
		CharacterId = characterId;
		Inventories = inventories;
		Pistols = pistols;
		ChangedPistols = pistols.Where(entry => entry.State.Raised)
			.Select(entry => entry.Document.Value.Id)
			.ToArray();
	}

	public CharacterId? CharacterId { get; }
	public IReadOnlyList<ItemId> ChangedPistols { get; }
	public bool HasDurableChanges => ChangedPistols.Count > 0;

	internal PistolCombatService Owner { get; }
	internal IReadOnlyList<DocumentSnapshot<InventoryRecord>> Inventories { get; }
	internal IReadOnlyList<PreparedPistolClearEntry> Pistols { get; }
	internal bool Staged { get; set; }
	internal bool Completed { get; set; }
}

internal sealed record PreparedPistolClearEntry(
	DocumentSnapshot<ItemRecord> Document,
	PistolItemState State);

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

	public ValueTask<OperationResult<PistolStateTransitionReceipt>> CompleteRaiseAsync(
		Guid ticketId,
		InventoryActor actor,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!_pendingRaises.TryGetValue(ticketId, out var ticket) || ticket.Actor != actor)
			return ValueTask.FromResult(OperationResult<PistolStateTransitionReceipt>.Failure(ErrorCode.Unauthorized,
				"Raise ticket is stale or bound to another actor."));
		if (_clock.UtcNow < ticket.ReadyAtUtc)
			return ValueTask.FromResult(OperationResult<PistolStateTransitionReceipt>.Failure(
				ErrorCode.Conflict, "Pistol raise delay has not completed."));
		_pendingRaises.Remove(ticketId);
		var resolved = Resolve(actor, ticket.InventoryId, ticket.PistolId);
		if (resolved.Failed) return ValueTask.FromResult(Failure<PistolStateTransitionReceipt>(resolved.Error!));
		if (!resolved.Value.State.Equipped)
			return ValueTask.FromResult(OperationResult<PistolStateTransitionReceipt>.Failure(
				ErrorCode.PolicyDenied, "Pistol is no longer equipped."));

		// Completing the delay grants only host-session authority. Persisting Raised here
		// would create a first durable transaction that a later failed fire could hide
		// from the command outcome and projection receipt. The first durable raise is
		// therefore committed atomically with ammunition and damage in FireAsync.
		_raisedThisSession.Add((actor.CharacterId, ticket.PistolId));
		return ValueTask.FromResult(OperationResult<PistolStateTransitionReceipt>.Success(
			new PistolStateTransitionReceipt(
				actor.CharacterId,
				ticket.InventoryId,
				ticket.PistolId,
				resolved.Value.State,
				resolved.Value.State with { Raised = true },
				PistolStateTransitionKind.Raise,
				null)));
	}

	public bool CancelRaise(Guid ticketId, InventoryActor actor)
	{
		if (!_pendingRaises.TryGetValue(ticketId, out var ticket) || ticket.Actor != actor) return false;
		_pendingRaises.Remove(ticketId);
		return true;
	}

	public async ValueTask<OperationResult<PistolStateTransitionReceipt>> LowerAsync(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId pistolId,
		CancellationToken cancellationToken = default)
	{
		var resolved = Resolve(actor, inventoryId, pistolId);
		if (resolved.Failed) return Failure<PistolStateTransitionReceipt>(resolved.Error!);
		if (!resolved.Value.State.Raised)
		{
			if (!_raisedThisSession.Remove((actor.CharacterId, pistolId)))
				return OperationResult<PistolStateTransitionReceipt>.Failure(
					ErrorCode.Conflict, "Pistol is already lowered.");
			RemovePending(actor.CharacterId, pistolId);
			return OperationResult<PistolStateTransitionReceipt>.Success(
				new PistolStateTransitionReceipt(
					actor.CharacterId,
					inventoryId,
					pistolId,
					resolved.Value.State with { Raised = true },
					resolved.Value.State,
					PistolStateTransitionKind.Lower,
					null));
		}
		var committed = await ReplaceStateAsync(resolved.Value,
			actor.CharacterId,
			inventoryId,
			resolved.Value.State with { Raised = false },
			PistolStateTransitionKind.Lower,
			cancellationToken);
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
		if (!state.Equipped ||
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
		unitOfWork.Require(resolved.Value.Access);
		HL2RPUnitOfWork.RequireActorState(unitOfWork, _repositories, resolved.Value.Character);
		unitOfWork.RequireUnchanged(_repositories.Inventories, resolved.Value.Inventory);
		var editor = unitOfWork.Edit(_repositories.Items, resolved.Value.Document);
		if (editor is null)
		{
			_damage.Abort(prepared.Value);
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return OperationResult<PistolFireReceipt>.Failure(ErrorCode.Conflict, "Pistol changed before firing.");
		}
		var nextState = state with
		{
			Raised = true,
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
			committed.Value, prepared.Value.PlanId));
	}

	public OperationResult<bool> IsHostRaised(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId pistolId)
	{
		var resolved = Resolve(actor, inventoryId, pistolId);
		return resolved.Succeeded
			? OperationResult<bool>.Success(resolved.Value.State.Equipped &&
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

	/// <summary>
	/// Lowers every persisted pistol in the character-owned inventory graph in
	/// one transaction. Session authority is cleared only after that transaction
	/// commits, so a failed clear leaves durable and in-memory state intact.
	/// </summary>
	public async ValueTask<OperationResult<PistolLifecycleClearReceipt>> ClearCharacterAsync(
		CharacterId characterId,
		CancellationToken cancellationToken = default)
	{
		var prepared = PrepareCharacterClear(characterId);
		return prepared.Succeeded
			? await CommitPreparedClearAsync(prepared.Value, cancellationToken)
			: Failure<PistolLifecycleClearReceipt>(prepared.Error!);
	}

	/// <summary>
	/// Validates and snapshots the complete character-owned pistol graph without
	/// changing persistence or transient session authority.
	/// </summary>
	public OperationResult<PreparedPistolLifecycleClear> PrepareCharacterClear(CharacterId characterId)
	{
		var graph = FindCharacterPistolGraph(characterId);
		return PrepareClear(characterId, graph.Pistols, graph.Inventories);
	}

	/// <summary>
	/// Pins the observed inventory graph and every owned pistol, then stages each
	/// durable Raised=false transition into the caller-owned unit of work.
	/// </summary>
	public OperationResult StageCharacterClear(
		IUnitOfWork unitOfWork,
		PreparedPistolLifecycleClear prepared)
	{
		ArgumentNullException.ThrowIfNull(unitOfWork);
		ArgumentNullException.ThrowIfNull(prepared);
		if (!ReferenceEquals(prepared.Owner, this))
			return OperationResult.Failure(ErrorCode.Unauthorized,
				"Prepared pistol cleanup belongs to another service instance.");
		if (prepared.Completed)
			return OperationResult.Failure(ErrorCode.Conflict, "Prepared pistol cleanup is already complete.");
		if (prepared.Staged)
			return OperationResult.Failure(ErrorCode.Conflict, "Prepared pistol cleanup is already staged.");

		foreach (var inventory in prepared.Inventories)
			unitOfWork.RequireUnchanged(_repositories.Inventories, inventory);
		foreach (var entry in prepared.Pistols)
		{
			if (!entry.State.Raised)
			{
				unitOfWork.RequireUnchanged(_repositories.Items, entry.Document);
				continue;
			}
			var editor = unitOfWork.Edit(_repositories.Items, entry.Document);
			if (editor is null)
				return OperationResult.Failure(
					ErrorCode.Conflict, "Pistol changed during lifecycle reconciliation.");
			editor.Replace(CombatPersistence.ReplaceTrait(
				editor.Value,
				CombatTraitNames.Pistol,
				HL2RPPersistence.Pistol,
				entry.State with { Raised = false }));
			unitOfWork.Save(editor);
		}
		prepared.Staged = true;
		return OperationResult.Success();
	}

	/// <summary>
	/// Applies the transient half of a prepared cleanup only after the caller
	/// supplies the receipt proving that every staged pistol transition committed.
	/// </summary>
	public OperationResult<PistolLifecycleClearReceipt> CompleteCharacterClear(
		PreparedPistolLifecycleClear prepared,
		CommitReceipt? commit)
	{
		ArgumentNullException.ThrowIfNull(prepared);
		if (!ReferenceEquals(prepared.Owner, this))
			return OperationResult<PistolLifecycleClearReceipt>.Failure(
				ErrorCode.Unauthorized, "Prepared pistol cleanup belongs to another service instance.");
		if (prepared.Completed)
			return OperationResult<PistolLifecycleClearReceipt>.Failure(
				ErrorCode.Conflict, "Prepared pistol cleanup is already complete.");
		if (prepared.HasDurableChanges && !prepared.Staged)
			return OperationResult<PistolLifecycleClearReceipt>.Failure(
				ErrorCode.Conflict, "Prepared pistol cleanup was not staged.");
		if (prepared.HasDurableChanges && commit is null)
			return OperationResult<PistolLifecycleClearReceipt>.Failure(
				ErrorCode.InvalidArgument, "A durable pistol cleanup requires its commit receipt.");
		if (commit is not null)
		{
			foreach (var pistolId in prepared.ChangedPistols)
			{
				var address = new DocumentAddress(DomainCollections.Items, DomainKeys.Item(pistolId));
				if (!commit.Documents.Any(document => document.Address == address && !document.IsDeleted))
					return OperationResult<PistolLifecycleClearReceipt>.Failure(
						ErrorCode.Conflict, "Commit receipt does not contain the prepared pistol cleanup.");
				var current = _repositories.Items.Find(address.Key);
				if (current is null)
					return OperationResult<PistolLifecycleClearReceipt>.Failure(
						ErrorCode.Conflict, "Committed pistol cleanup is no longer available.");
				var state = CombatPersistence.DecodeTrait(
					current.Value, CombatTraitNames.Pistol, HL2RPPersistence.Pistol);
				if (state.Failed || state.Value.Raised)
					return OperationResult<PistolLifecycleClearReceipt>.Failure(
						ErrorCode.Conflict, "Committed pistol cleanup did not publish a lowered state.");
			}
		}

		prepared.Completed = true;
		if (prepared.CharacterId is CharacterId characterId) ClearCharacter(characterId);
		else ClearAllSessions();
		return OperationResult<PistolLifecycleClearReceipt>.Success(new PistolLifecycleClearReceipt(
			prepared.CharacterId,
			prepared.ChangedPistols,
			prepared.HasDurableChanges ? commit : null));
	}

	/// <summary>
	/// Startup recovery boundary for stale persisted raised flags. All matching
	/// pistols are lowered in one transaction and the single commit is returned.
	/// </summary>
	public async ValueTask<OperationResult<PistolLifecycleClearReceipt>> ReconcileRaisedPistolsAsync(
		CancellationToken cancellationToken = default)
	{
		var prepared = PrepareClear(
			null,
			_repositories.Items.All()
				.Where(document => document.Value.Definition.Value == HL2RPIds.Items.Pistol)
				.OrderBy(document => document.Value.Id.Value)
				.ToArray(),
			Array.Empty<DocumentSnapshot<InventoryRecord>>());
		return prepared.Succeeded
			? await CommitPreparedClearAsync(prepared.Value, cancellationToken)
			: Failure<PistolLifecycleClearReceipt>(prepared.Error!);
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
		var access = _access.Prove(actor.ConnectionId, actor.CharacterId, inventoryId,
			InventoryCapability.View | InventoryCapability.Use);
		if (access is null)
			return OperationResult<ResolvedPistol>.Failure(ErrorCode.Unauthorized, "Pistol use capability is missing.");
		if (inventory.Value.Find(pistolId) is null || item.Value.Definition.Value != HL2RPIds.Items.Pistol)
			return OperationResult<ResolvedPistol>.Failure(ErrorCode.NotFound,
				"Claimed pistol is not a member of the inventory.");
		var state = CombatPersistence.DecodeTrait(item.Value, CombatTraitNames.Pistol, HL2RPPersistence.Pistol);
		if (state.Failed) return Failure<ResolvedPistol>(state.Error!);
		if (state.Value.MagazineRounds is < 0 or > PistolItemState.MagazineCapacity)
			return OperationResult<ResolvedPistol>.Failure(ErrorCode.PersistedTypeInvalid,
				"Pistol magazine state is outside registered limits.");
		return OperationResult<ResolvedPistol>.Success(
			new ResolvedPistol(character, inventory, item, state.Value, access));
	}

	private async ValueTask<OperationResult<PistolStateTransitionReceipt>> ReplaceStateAsync(
		ResolvedPistol resolved,
		CharacterId characterId,
		InventoryId inventoryId,
		PistolItemState state,
		PistolStateTransitionKind kind,
		CancellationToken cancellationToken)
	{
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require(resolved.Access);
		HL2RPUnitOfWork.RequireActorState(unitOfWork, _repositories, resolved.Character);
		unitOfWork.RequireUnchanged(_repositories.Inventories, resolved.Inventory);
		var editor = unitOfWork.Edit(_repositories.Items, resolved.Document);
		if (editor is null)
		{
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return OperationResult<PistolStateTransitionReceipt>.Failure(
				ErrorCode.Conflict, "Pistol changed.");
		}
		editor.Replace(CombatPersistence.ReplaceTrait(editor.Value, CombatTraitNames.Pistol,
			HL2RPPersistence.Pistol, state));
		unitOfWork.Save(editor);
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		return committed.Succeeded
			? OperationResult<PistolStateTransitionReceipt>.Success(
				new PistolStateTransitionReceipt(
					characterId,
					inventoryId,
					resolved.Document.Value.Id,
					resolved.State,
					state,
					kind,
					committed.Value))
			: CombatPersistence.Failure<PistolStateTransitionReceipt>(committed.Error!);
	}

	private OperationResult<PreparedPistolLifecycleClear> PrepareClear(
		CharacterId? characterId,
		IReadOnlyList<DocumentSnapshot<ItemRecord>> pistols,
		IReadOnlyList<DocumentSnapshot<InventoryRecord>> dependencies)
	{
		var prepared = new List<PreparedPistolClearEntry>();
		foreach (var pistol in pistols)
		{
			var decoded = CombatPersistence.DecodeTrait(
				pistol.Value, CombatTraitNames.Pistol, HL2RPPersistence.Pistol);
			if (decoded.Failed) return Failure<PreparedPistolLifecycleClear>(decoded.Error!);
			prepared.Add(new PreparedPistolClearEntry(pistol, decoded.Value));
		}
		return OperationResult<PreparedPistolLifecycleClear>.Success(
			new PreparedPistolLifecycleClear(this, characterId, dependencies, prepared));
	}

	private async ValueTask<OperationResult<PistolLifecycleClearReceipt>> CommitPreparedClearAsync(
		PreparedPistolLifecycleClear prepared,
		CancellationToken cancellationToken)
	{
		if (!prepared.HasDurableChanges)
			return CompleteCharacterClear(prepared, null);

		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		var staged = StageCharacterClear(unitOfWork, prepared);
		if (staged.Failed)
		{
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return Failure<PistolLifecycleClearReceipt>(staged.Error!);
		}
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		if (!committed.Succeeded)
			return CombatPersistence.Failure<PistolLifecycleClearReceipt>(committed.Error!);
		return CompleteCharacterClear(prepared, committed.Value);
	}

	private CharacterPistolGraph FindCharacterPistolGraph(CharacterId characterId)
	{
		var inventories = _repositories.Inventories.All().ToArray();
		var itemsById = _repositories.Items.All()
			.ToDictionary(document => document.Value.Id);
		var childInventoriesByParentItem = inventories
			.Where(inventory => inventory.Value.Owner.Kind == InventoryOwnerKind.ParentItem)
			.GroupBy(inventory => new ItemId(inventory.Value.Owner.OwnerId))
			.ToDictionary(group => group.Key, group => group.ToArray());
		var ownedInventoryIds = new HashSet<InventoryId>();
		var ownedItemIds = new HashSet<ItemId>();
		var pending = new Queue<DocumentSnapshot<InventoryRecord>>(inventories.Where(inventory =>
			inventory.Value.Owner.Kind == InventoryOwnerKind.Character &&
			inventory.Value.Owner.OwnerId == characterId.Value));
		while (pending.Count > 0)
		{
			var inventory = pending.Dequeue();
			if (!ownedInventoryIds.Add(inventory.Value.Id)) continue;
			foreach (var placement in inventory.Value.Placements)
			{
				ownedItemIds.Add(placement.ItemId);
				if (!childInventoriesByParentItem.TryGetValue(placement.ItemId, out var children)) continue;
				foreach (var child in children) pending.Enqueue(child);
			}
		}

		var pistols = ownedItemIds
			.Select(itemId => itemsById.GetValueOrDefault(itemId))
			.Where(document => document is not null &&
				document.Value.Definition.Value == HL2RPIds.Items.Pistol)
			.Select(document => document!)
			.OrderBy(document => document.Value.Id.Value)
			.ToArray();
		var ownedInventories = inventories
			.Where(inventory => ownedInventoryIds.Contains(inventory.Value.Id))
			.OrderBy(inventory => inventory.Value.Id.Value)
			.ToArray();
		return new CharacterPistolGraph(ownedInventories, pistols);
	}

	private void ClearAllSessions()
	{
		_pendingRaises.Clear();
		_raisedThisSession.Clear();
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
		DocumentSnapshot<CharacterRecord> Character,
		DocumentSnapshot<InventoryRecord> Inventory,
		DocumentSnapshot<ItemRecord> Document,
		PistolItemState State,
		InventoryAccessProof Access);

	private sealed record CharacterPistolGraph(
		IReadOnlyList<DocumentSnapshot<InventoryRecord>> Inventories,
		IReadOnlyList<DocumentSnapshot<ItemRecord>> Pistols);
}
