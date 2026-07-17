#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Showcase.Combat;

public sealed record CombatHealthSnapshot(CharacterId CharacterId, long MaximumHealth, long CurrentHealth)
{
	public bool IsDead => CurrentHealth <= 0;
}

public sealed record CombatHealthReservation(Guid Id, CombatHealthSnapshot Snapshot);

public interface ICombatHealthDirectory
{
	OperationResult<CombatHealthSnapshot> Require(CharacterId characterId);
	OperationResult<CombatHealthReservation> Reserve(CharacterId characterId);
	void CommitDamage(CombatHealthReservation reservation, long remainingHealth);
	CombatHealthSnapshot CommitHealing(CombatHealthReservation reservation, long amount);
	void Release(CombatHealthReservation reservation);
	void RestoreAlive(CharacterId characterId);
	void RestoreFull(CharacterId characterId);
}

public enum CombatHealthRemoval
{
	NotTracked = 0,
	Removed = 1,
	RemovedWithDoomedReservation = 2
}

/// <summary>Host-owned transient body health. Persistence owns items; bodies own health.</summary>
public sealed class CanonicalCombatHealthDirectory : ICombatHealthDirectory
{
	private readonly object _sync = new();
	private readonly Dictionary<CharacterId, CombatHealthSnapshot> _health = new();
	private readonly Dictionary<CharacterId, Guid> _reservations = new();
	private readonly HashSet<Guid> _doomed = new();

	/// <summary>
	/// Observes health mutations that arrived after their character's lifecycle exit
	/// doomed the reservation; wired to host logging. Must not throw.
	/// </summary>
	public Action<string>? Diagnostic { get; set; }

	public void Publish(CharacterId characterId, long maximumHealth, long currentHealth)
	{
		if (maximumHealth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumHealth));
		if (currentHealth < 0 || currentHealth > maximumHealth)
			throw new ArgumentOutOfRangeException(nameof(currentHealth));
		lock (_sync)
		{
			if (_reservations.ContainsKey(characterId))
				throw new InvalidOperationException("Reserved combat health cannot be republished.");
			_health[characterId] = new CombatHealthSnapshot(characterId, maximumHealth, currentHealth);
		}
	}

	public CombatHealthRemoval Remove(CharacterId characterId)
	{
		lock (_sync)
		{
			// A lifecycle exit always wins: any in-flight reservation is doomed so its
			// eventual CommitDamage/CommitHealing no-ops instead of mutating (and thereby
			// reviving) an entry for a character that already left play.
			var doomed = _reservations.Remove(characterId, out var reservationId);
			if (doomed) _doomed.Add(reservationId);
			var removed = _health.Remove(characterId);
			return removed
				? doomed
					? CombatHealthRemoval.RemovedWithDoomedReservation
					: CombatHealthRemoval.Removed
				: CombatHealthRemoval.NotTracked;
		}
	}

	public OperationResult<CombatHealthSnapshot> Require(CharacterId characterId)
	{
		lock (_sync)
			return _health.TryGetValue(characterId, out var state)
				? OperationResult<CombatHealthSnapshot>.Success(state)
				: OperationResult<CombatHealthSnapshot>.Failure(
					ErrorCode.NotFound, "Authoritative character health is unavailable.");
	}

	public OperationResult<CombatHealthReservation> Reserve(CharacterId characterId)
	{
		lock (_sync)
		{
			if (!_health.TryGetValue(characterId, out var current))
				return OperationResult<CombatHealthReservation>.Failure(
					ErrorCode.NotFound, "Authoritative character health is unavailable.");
			if (_reservations.ContainsKey(characterId))
				return OperationResult<CombatHealthReservation>.Failure(
					ErrorCode.Conflict, "Character health already has a pending host mutation.");
			var reservation = new CombatHealthReservation(Guid.NewGuid(), current);
			_reservations.Add(characterId, reservation.Id);
			return OperationResult<CombatHealthReservation>.Success(reservation);
		}
	}

	public void CommitDamage(CombatHealthReservation reservation, long remainingHealth)
	{
		ArgumentNullException.ThrowIfNull(reservation);
		lock (_sync)
		{
			if (_doomed.Remove(reservation.Id))
			{
				ObserveDoomedCommit(reservation, "damage");
				return;
			}
			var characterId = reservation.Snapshot.CharacterId;
			if (!_health.TryGetValue(characterId, out var current) ||
				!_reservations.Remove(characterId, out var id) || id != reservation.Id)
				throw new InvalidOperationException("Committed combat health reservation is stale.");
			_health[characterId] = current with
			{
				CurrentHealth = Math.Clamp(remainingHealth, 0, current.MaximumHealth)
			};
		}
	}

	public CombatHealthSnapshot CommitHealing(CombatHealthReservation reservation, long amount)
	{
		ArgumentNullException.ThrowIfNull(reservation);
		if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
		lock (_sync)
		{
			if (_doomed.Remove(reservation.Id))
			{
				// The character left play mid-mutation; the stale snapshot is returned for
				// the caller's presentation only and no directory entry is revived.
				ObserveDoomedCommit(reservation, "healing");
				return reservation.Snapshot;
			}
			var characterId = reservation.Snapshot.CharacterId;
			if (!_health.TryGetValue(characterId, out var current) ||
				!_reservations.Remove(characterId, out var id) || id != reservation.Id)
				throw new InvalidOperationException("Committed healing reservation is stale.");
			var next = current with
			{
				CurrentHealth = current.CurrentHealth > current.MaximumHealth - amount
					? current.MaximumHealth
					: current.CurrentHealth + amount
			};
			_health[characterId] = next;
			return next;
		}
	}

	public void Release(CombatHealthReservation reservation)
	{
		ArgumentNullException.ThrowIfNull(reservation);
		lock (_sync)
		{
			if (_doomed.Remove(reservation.Id)) return;
			var characterId = reservation.Snapshot.CharacterId;
			if (_reservations.TryGetValue(characterId, out var id) && id == reservation.Id)
				_reservations.Remove(characterId);
		}
	}

	private void ObserveDoomedCommit(CombatHealthReservation reservation, string kind)
	{
		try
		{
			Diagnostic?.Invoke(
				$"HL2RP_COMBAT_COMMIT_DOOMED character={reservation.Snapshot.CharacterId.Value:D} " +
				$"kind={kind} detail=\"the character exited play before its health mutation committed\"");
		}
		catch
		{
			// Diagnostics must never turn a doomed no-op into a throw.
		}
	}

	public void RestoreAlive(CharacterId characterId)
	{
		lock (_sync)
		{
			if (!_health.TryGetValue(characterId, out var current)) return;
			_health[characterId] = current with { CurrentHealth = Math.Max(1, current.CurrentHealth) };
		}
	}

	public void RestoreFull(CharacterId characterId)
	{
		lock (_sync)
		{
			if (!_health.TryGetValue(characterId, out var current)) return;
			_health[characterId] = current with { CurrentHealth = current.MaximumHealth };
		}
	}

}

public sealed record CombatPlayerTarget(
	string Token,
	InventoryActor Actor,
	InventoryId InventoryId,
	WorldTransformRecord DropTransform);

public interface ICombatPlayerTargetDirectory
{
	OperationResult<CombatPlayerTarget> Resolve(string token);
}

public sealed class CanonicalCombatPlayerTargetDirectory : ICombatPlayerTargetDirectory
{
	private readonly object _sync = new();
	private IReadOnlyDictionary<string, CombatPlayerTarget> _targets =
		new Dictionary<string, CombatPlayerTarget>(StringComparer.Ordinal);

	public void Publish(IEnumerable<CombatPlayerTarget> targets)
	{
		ArgumentNullException.ThrowIfNull(targets);
		var map = new Dictionary<string, CombatPlayerTarget>(StringComparer.Ordinal);
		var characters = new HashSet<CharacterId>();
		foreach (var target in targets.OrderBy(target => target.Token, StringComparer.Ordinal))
		{
			ArgumentNullException.ThrowIfNull(target);
			if (string.IsNullOrWhiteSpace(target.Token))
				throw new ArgumentException("Combat target token cannot be empty.", nameof(targets));
			ArgumentNullException.ThrowIfNull(target.DropTransform);
			if (!map.TryAdd(target.Token, target) || !characters.Add(target.Actor.CharacterId))
				throw new ArgumentException("Combat target tokens and characters must be unique.", nameof(targets));
		}
		lock (_sync) _targets = map;
	}

	public OperationResult<CombatPlayerTarget> Resolve(string token)
	{
		if (string.IsNullOrWhiteSpace(token))
			return OperationResult<CombatPlayerTarget>.Failure(ErrorCode.NotFound, "Shot has no player target.");
		lock (_sync)
			return _targets.TryGetValue(token, out var target)
				? OperationResult<CombatPlayerTarget>.Success(target)
				: OperationResult<CombatPlayerTarget>.Failure(ErrorCode.NotFound, "Shot target is not a live player.");
	}
}

public sealed record PlayerCombatDamageOutcome(
	Guid PlanId,
	CombatPlayerTarget Target,
	long IncomingDamage,
	long AbsorbedDamage,
	long AppliedDamage,
	long PreviousHealth,
	long RemainingHealth,
	ItemId? VestItemId,
	int? RemainingVestDurability)
{
	public bool IsLethal => RemainingHealth <= 0;
}

public interface IPlayerCombatDamageOutcomeBoundary : ICombatDamageBoundary
{
	bool CanResolve(AuthoritativeShot shot);
	bool Owns(Guid planId);
	bool TryTakeOutcome(Guid planId, out PlayerCombatDamageOutcome outcome);
	void RestoreAlive(CharacterId characterId);
}

/// <summary>
/// Player damage planner. Pistol ammunition and durable vest durability are
/// staged in one unit of work; transient host health is applied only after that
/// commit. One target may have only one prepared damage plan at a time.
/// </summary>
public sealed class PlayerCombatDamageBoundary : IPlayerCombatDamageOutcomeBoundary
{
	private readonly object _sync = new();
	private readonly DomainRepositories _repositories;
	private readonly ICombatPlayerTargetDirectory _targets;
	private readonly ICombatHealthDirectory _health;
	private readonly Func<Guid> _createPlanId;
	private readonly Dictionary<Guid, PlayerDamagePlan> _plans = new();
	private readonly Dictionary<Guid, PlayerCombatDamageOutcome> _outcomes = new();

	public PlayerCombatDamageBoundary(
		DomainRepositories repositories,
		ICombatPlayerTargetDirectory targets,
		ICombatHealthDirectory health,
		Func<Guid>? createPlanId = null)
	{
		_repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
		_targets = targets ?? throw new ArgumentNullException(nameof(targets));
		_health = health ?? throw new ArgumentNullException(nameof(health));
		_createPlanId = createPlanId ?? Guid.NewGuid;
	}

	public bool CanResolve(AuthoritativeShot shot) =>
		shot.Hit && !string.IsNullOrWhiteSpace(shot.TargetToken) && _targets.Resolve(shot.TargetToken).Succeeded;

	public bool Owns(Guid planId)
	{
		lock (_sync) return _plans.ContainsKey(planId) || _outcomes.ContainsKey(planId);
	}

	public OperationResult<PreparedCombatDamage> Prepare(AuthoritativeShot shot, long damage)
	{
		ArgumentNullException.ThrowIfNull(shot);
		if (!shot.Hit || damage <= 0)
			return OperationResult<PreparedCombatDamage>.Failure(
				ErrorCode.InvalidArgument, "Player combat damage requires a positive hit.");
		var target = _targets.Resolve(shot.TargetToken ?? string.Empty);
		if (target.Failed)
			return OperationResult<PreparedCombatDamage>.Failure(target.Error!.Code, target.Error.Message);
		var character = _repositories.Characters.Find(DomainKeys.Character(target.Value.Actor.CharacterId));
		var inventory = _repositories.Inventories.Find(DomainKeys.Inventory(target.Value.InventoryId));
		if (character is null || inventory is null || character.Value.AccountId != target.Value.Actor.AccountId ||
			inventory.Value.Owner != InventoryOwner.Character(target.Value.Actor.CharacterId))
			return OperationResult<PreparedCombatDamage>.Failure(
				ErrorCode.Unauthorized, "Live player damage target binding is invalid.");
		var health = _health.Require(target.Value.Actor.CharacterId);
		if (health.Failed)
			return OperationResult<PreparedCombatDamage>.Failure(health.Error!.Code, health.Error.Message);
		if (health.Value.IsDead)
			return OperationResult<PreparedCombatDamage>.Failure(ErrorCode.PolicyDenied, "Target is already dead.");

		DocumentSnapshot<ItemRecord>? vestDocument = null;
		ProtectiveVestDamagePlan? vestPlan = null;
		foreach (var placement in inventory.Value.Placements)
		{
			var item = _repositories.Items.Find(DomainKeys.Item(placement.ItemId));
			if (item?.Value.Definition.Value != HL2RPIds.Items.ProtectiveVest) continue;
			var state = CombatPersistence.DecodeTrait(item.Value, CombatTraitNames.Vest, HL2RPPersistence.ProtectiveVest);
			if (state.Failed)
				return OperationResult<PreparedCombatDamage>.Failure(state.Error!.Code, state.Error.Message);
			if (!state.Value.Equipped) continue;
			if (vestDocument is not null)
				return OperationResult<PreparedCombatDamage>.Failure(
					ErrorCode.Conflict, "Target has multiple equipped protective vests.");
			var planned = ProtectiveVestDamagePlanner.Plan(state.Value, damage);
			if (planned.Failed)
				return OperationResult<PreparedCombatDamage>.Failure(planned.Error!.Code, planned.Error.Message);
			vestDocument = item;
			vestPlan = planned.Value;
		}

		var reservation = _health.Reserve(target.Value.Actor.CharacterId);
		if (reservation.Failed)
			return OperationResult<PreparedCombatDamage>.Failure(
				reservation.Error!.Code, reservation.Error.Message);
		if (reservation.Value.Snapshot.IsDead)
		{
			_health.Release(reservation.Value);
			return OperationResult<PreparedCombatDamage>.Failure(ErrorCode.PolicyDenied, "Target is already dead.");
		}
		var absorbed = vestPlan?.AbsorbedDamage ?? 0;
		var applied = vestPlan?.AppliedDamage ?? damage;
		var remaining = applied >= reservation.Value.Snapshot.CurrentHealth
			? 0
			: reservation.Value.Snapshot.CurrentHealth - applied;
		var planId = _createPlanId();
		if (planId == Guid.Empty)
		{
			_health.Release(reservation.Value);
			return OperationResult<PreparedCombatDamage>.Failure(ErrorCode.InternalError, "Damage plan identity is invalid.");
		}
		lock (_sync)
		{
			_plans.Add(planId, new PlayerDamagePlan(target.Value, reservation.Value, damage, absorbed, applied,
				remaining, vestDocument, vestPlan, false));
		}
		return OperationResult<PreparedCombatDamage>.Success(new PreparedCombatDamage(shot, damage, planId));
	}

	public OperationResult Stage(IUnitOfWork unitOfWork, PreparedCombatDamage damage)
	{
		ArgumentNullException.ThrowIfNull(unitOfWork);
		PlayerDamagePlan plan;
		lock (_sync)
		{
			if (!_plans.TryGetValue(damage.PlanId, out plan!))
				return OperationResult.Failure(ErrorCode.Unauthorized, "Player damage plan is stale.");
		}
		if (plan.VestDocument is not null && plan.VestPlan is not null)
		{
			var editor = unitOfWork.Edit(_repositories.Items, plan.VestDocument);
			if (editor is null) return OperationResult.Failure(ErrorCode.Conflict, "Protective vest changed.");
			editor.Replace(CombatPersistence.ReplaceTrait(editor.Value, CombatTraitNames.Vest,
				HL2RPPersistence.ProtectiveVest, plan.VestPlan.NextState));
			unitOfWork.Save(editor);
		}
		lock (_sync)
		{
			if (!_plans.TryGetValue(damage.PlanId, out var current))
				return OperationResult.Failure(ErrorCode.Unauthorized, "Player damage plan was revoked.");
			_plans[damage.PlanId] = current with { Staged = true };
		}
		return OperationResult.Success();
	}

	public void Commit(PreparedCombatDamage damage)
	{
		PlayerDamagePlan plan;
		lock (_sync)
		{
			if (!_plans.Remove(damage.PlanId, out plan!) || !plan.Staged)
				throw new InvalidOperationException("Player damage committed without a staged plan.");
		}
		_health.CommitDamage(plan.Health, plan.RemainingHealth);
		var outcome = new PlayerCombatDamageOutcome(
			damage.PlanId,
			plan.Target,
			plan.IncomingDamage,
			plan.AbsorbedDamage,
			plan.AppliedDamage,
			plan.Health.Snapshot.CurrentHealth,
			plan.RemainingHealth,
			plan.VestDocument?.Value.Id,
			plan.VestPlan?.NextState.Durability);
		lock (_sync) _outcomes[damage.PlanId] = outcome;
	}

	public void Abort(PreparedCombatDamage damage)
	{
		PlayerDamagePlan? removed = null;
		lock (_sync)
			if (_plans.Remove(damage.PlanId, out var plan)) removed = plan;
		if (removed is not null) _health.Release(removed.Health);
	}

	public bool TryTakeOutcome(Guid planId, out PlayerCombatDamageOutcome outcome)
	{
		lock (_sync) return _outcomes.Remove(planId, out outcome!);
	}

	public void RestoreAlive(CharacterId characterId) => _health.RestoreAlive(characterId);

	private sealed record PlayerDamagePlan(
		CombatPlayerTarget Target,
		CombatHealthReservation Health,
		long IncomingDamage,
		long AbsorbedDamage,
		long AppliedDamage,
		long RemainingHealth,
		DocumentSnapshot<ItemRecord>? VestDocument,
		ProtectiveVestDamagePlan? VestPlan,
		bool Staged);
}

/// <summary>Routes player targets to staged player damage and every other hit to the world boundary.</summary>
public sealed class CompositeCombatDamageBoundary : ICombatDamageBoundary
{
	private readonly object _sync = new();
	private readonly IPlayerCombatDamageOutcomeBoundary _players;
	private readonly ICombatDamageBoundary _world;
	private readonly HashSet<Guid> _worldPlans = new();
	private readonly HashSet<Guid> _noDamagePlans = new();

	public CompositeCombatDamageBoundary(
		IPlayerCombatDamageOutcomeBoundary players,
		ICombatDamageBoundary world)
	{
		_players = players ?? throw new ArgumentNullException(nameof(players));
		_world = world ?? throw new ArgumentNullException(nameof(world));
	}

	public OperationResult<PreparedCombatDamage> Prepare(AuthoritativeShot shot, long damage)
	{
		if (_players.CanResolve(shot)) return _players.Prepare(shot, damage);
		if (!shot.Hit || damage <= 0)
		{
			var noDamageId = Guid.NewGuid();
			lock (_sync) _noDamagePlans.Add(noDamageId);
			return OperationResult<PreparedCombatDamage>.Success(
				new PreparedCombatDamage(shot, 0, noDamageId));
		}
		var prepared = _world.Prepare(shot, damage);
		if (prepared.Failed) return prepared;
		var id = prepared.Value.PlanId == Guid.Empty ? Guid.NewGuid() : prepared.Value.PlanId;
		lock (_sync) _worldPlans.Add(id);
		return OperationResult<PreparedCombatDamage>.Success(prepared.Value with { PlanId = id });
	}

	public OperationResult Stage(IUnitOfWork unitOfWork, PreparedCombatDamage damage)
	{
		if (_players.Owns(damage.PlanId)) return _players.Stage(unitOfWork, damage);
		lock (_sync)
		{
			if (_noDamagePlans.Contains(damage.PlanId)) return OperationResult.Success();
			if (!_worldPlans.Contains(damage.PlanId))
				return OperationResult.Failure(ErrorCode.Unauthorized, "Combat damage plan is stale.");
		}
		return _world.Stage(unitOfWork, damage);
	}

	public void Commit(PreparedCombatDamage damage)
	{
		if (_players.Owns(damage.PlanId)) _players.Commit(damage);
		else
		{
			lock (_sync)
			{
				if (_noDamagePlans.Remove(damage.PlanId)) return;
				if (!_worldPlans.Remove(damage.PlanId))
					throw new InvalidOperationException("Combat damage committed without a staged plan.");
			}
			_world.Commit(damage);
		}
	}

	public void Abort(PreparedCombatDamage damage)
	{
		if (_players.Owns(damage.PlanId)) _players.Abort(damage);
		else
		{
			var abortWorld = false;
			lock (_sync)
			{
				if (_noDamagePlans.Remove(damage.PlanId)) return;
				abortWorld = _worldPlans.Remove(damage.PlanId);
			}
			if (abortWorld) _world.Abort(damage);
		}
	}
}

public interface ICombatIntentDelay
{
	ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default);
}

public sealed class SystemCombatIntentDelay : ICombatIntentDelay
{
	public ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default) =>
		new(Task.Delay(duration, cancellationToken));
}

public sealed record CombatFireIntent(
	InventoryActor Actor,
	InventoryId InventoryId,
	ItemId PistolId,
	Func<bool>? CanCompleteRaise = null);

public sealed record CombatIntentReceipt(
	PistolFireReceipt Fire,
	PlayerCombatDamageOutcome? PlayerDamage,
	DeathTransitionReceipt? Death,
	OperationError? DegradedDeathTransition = null,
	PistolStateTransitionReceipt? Raise = null);

/// <summary>
/// Production fire route for the schema Fire action. Raise completion is host
/// delayed; ammo and durable target damage commit first. A lethal death/drop is
/// a second awaited atomic boundary. If that boundary fails, health is restored
/// to one and the already-committed shot remains a successful outcome carrying
/// a degradation diagnostic, so its receipt can never be hidden behind failure.
/// </summary>
public sealed class CombatIntentService
{
	private readonly PistolCombatService _pistol;
	private readonly IPlayerCombatDamageOutcomeBoundary _damage;
	private readonly CombatLifecycleService _lifecycle;
	private readonly ICombatHealthDirectory _health;
	private readonly ICombatIntentDelay _delay;
	private readonly IHexClock _clock;

	public CombatIntentService(
		PistolCombatService pistol,
		IPlayerCombatDamageOutcomeBoundary damage,
		CombatLifecycleService lifecycle,
		ICombatHealthDirectory health,
		ICombatIntentDelay delay,
		IHexClock clock)
	{
		_pistol = pistol ?? throw new ArgumentNullException(nameof(pistol));
		_damage = damage ?? throw new ArgumentNullException(nameof(damage));
		_lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
		_health = health ?? throw new ArgumentNullException(nameof(health));
		_delay = delay ?? throw new ArgumentNullException(nameof(delay));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
	}

	public async ValueTask<OperationResult<CombatIntentReceipt>> FireAsync(
		CombatFireIntent intent,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(intent);
		PistolStateTransitionReceipt? raise = null;
		var commitOwned = false;
		var raised = _pistol.IsHostRaised(intent.Actor, intent.InventoryId, intent.PistolId);
		if (raised.Failed) return Failure<CombatIntentReceipt>(raised.Error!);
		if (!raised.Value)
		{
			var ticket = _pistol.BeginRaise(intent.Actor, intent.InventoryId, intent.PistolId);
			if (ticket.Failed) return Failure<CombatIntentReceipt>(ticket.Error!);
			try
			{
				var remaining = ticket.Value.ReadyAtUtc - _clock.UtcNow;
				if (remaining > TimeSpan.Zero) await _delay.DelayAsync(remaining, cancellationToken);
				if (intent.CanCompleteRaise is not null)
				{
					if (!intent.CanCompleteRaise())
					{
						_pistol.ClearCharacter(intent.Actor.CharacterId);
						return OperationResult<CombatIntentReceipt>.Failure(
							ErrorCode.Conflict, "Pistol raise was cancelled or its actor binding changed.");
					}
					commitOwned = true;
				}
				var completed = await _pistol.CompleteRaiseAsync(
					ticket.Value.Id,
					intent.Actor,
					commitOwned ? CancellationToken.None : cancellationToken);
				if (completed.Failed)
				{
					_pistol.CancelRaise(ticket.Value.Id, intent.Actor);
					return Failure<CombatIntentReceipt>(completed.Error!);
				}
				raise = completed.Value;
			}
			catch
			{
				_pistol.CancelRaise(ticket.Value.Id, intent.Actor);
				throw;
			}
		}
		if (intent.CanCompleteRaise is not null && !commitOwned)
		{
			if (!intent.CanCompleteRaise())
				return OperationResult<CombatIntentReceipt>.Failure(
					ErrorCode.Conflict, "Pistol raise was cancelled or its actor binding changed.");
			commitOwned = true;
		}

		var fired = await _pistol.FireAsync(
			new PistolFireIntent(intent.Actor, intent.InventoryId, intent.PistolId),
			commitOwned ? CancellationToken.None : cancellationToken);
		if (fired.Failed) return Failure<CombatIntentReceipt>(fired.Error!);

		PlayerCombatDamageOutcome? playerDamage = null;
		DeathTransitionReceipt? death = null;
		OperationError? degradedDeathTransition = null;
		if (fired.Value.DamagePlanId != Guid.Empty &&
			_damage.TryTakeOutcome(fired.Value.DamagePlanId, out var outcome))
		{
			playerDamage = outcome;
			if (outcome.IsLethal)
			{
				OperationResult<DeathTransitionReceipt>? died = null;
				try
				{
					died = await _lifecycle.DieAsync(
						outcome.Target.Actor,
						outcome.Target.InventoryId,
						outcome.Target.DropTransform,
						"Pistol wound",
						commitOwned ? CancellationToken.None : cancellationToken);
				}
				catch
				{
					_damage.RestoreAlive(outcome.Target.Actor.CharacterId);
					degradedDeathTransition = new OperationError(
						ErrorCode.InternalError,
						"Shot committed, but death transition failed unexpectedly and target health was restored." );
				}
				if (died is { } deathResult && deathResult.Failed)
				{
					_damage.RestoreAlive(outcome.Target.Actor.CharacterId);
					degradedDeathTransition = new OperationError(
						deathResult.Error!.Code,
						$"Shot committed, but death transition failed and target health was restored: {deathResult.Error.Message}",
						deathResult.Error.Details);
				}
				else if (died is { } successfulDeath) death = successfulDeath.Value;
			}
		}
		return OperationResult<CombatIntentReceipt>.Success(new CombatIntentReceipt(
			fired.Value, playerDamage, death, degradedDeathTransition, raise));
	}

	public OperationResult<DeathRespawnState> Respawn(InventoryActor actor)
	{
		var respawned = _lifecycle.Respawn(actor);
		if (respawned.Succeeded)
		{
			_pistol.ClearCharacter(actor.CharacterId);
			_health.RestoreFull(actor.CharacterId);
		}
		return respawned;
	}

	public void ClearCharacter(CharacterId characterId) => _pistol.ClearCharacter(characterId);

	public ValueTask<OperationResult<PistolLifecycleClearReceipt>> ClearCharacterAsync(
		CharacterId characterId,
		CancellationToken cancellationToken = default) =>
		_pistol.ClearCharacterAsync(characterId, cancellationToken);

	private static OperationResult<T> Failure<T>(OperationError error) =>
		OperationResult<T>.Failure(error.Code, error.Message);
}

public sealed record HealthVialConsumedReceipt(
	ItemId VialItemId,
	long PreviousHealth,
	long CurrentHealth,
	long CommitSequence,
	CommitReceipt Commit) : IHL2RPCommittedOperation;

public sealed class HealthVialConsumeService
{
	public const long DefaultHealing = 25;

	private readonly DomainRepositories _repositories;
	private readonly InventoryAccessService _access;
	private readonly InventoryLayoutService _layout;
	private readonly ICombatHealthDirectory _health;
	private readonly long _healing;

	public HealthVialConsumeService(
		DomainRepositories repositories,
		InventoryAccessService access,
		InventoryLayoutService layout,
		ICombatHealthDirectory health,
		long healing = DefaultHealing)
	{
		_repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
		_access = access ?? throw new ArgumentNullException(nameof(access));
		_layout = layout ?? throw new ArgumentNullException(nameof(layout));
		_health = health ?? throw new ArgumentNullException(nameof(health));
		if (healing <= 0) throw new ArgumentOutOfRangeException(nameof(healing));
		_healing = healing;
	}

	public async ValueTask<OperationResult<HealthVialConsumedReceipt>> ConsumeAsync(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId vialItemId,
		CancellationToken cancellationToken = default)
	{
		var character = _repositories.Characters.Find(DomainKeys.Character(actor.CharacterId));
		var inventory = _repositories.Inventories.Find(DomainKeys.Inventory(inventoryId));
		var vial = _repositories.Items.Find(DomainKeys.Item(vialItemId));
		if (character is null || inventory is null || vial is null)
			return OperationResult<HealthVialConsumedReceipt>.Failure(
				ErrorCode.NotFound, "Character, inventory, or health vial was not found.");
		if (character.Value.AccountId != actor.AccountId ||
			inventory.Value.Owner != InventoryOwner.Character(actor.CharacterId) ||
			inventory.Value.Find(vialItemId) is null ||
			vial.Value.Definition.Value != HL2RPIds.Items.HealthVial)
			return OperationResult<HealthVialConsumedReceipt>.Failure(
				ErrorCode.Unauthorized, "Health vial membership proof failed.");
		var access = _access.Prove(actor.ConnectionId, actor.CharacterId, inventoryId,
			InventoryCapability.View | InventoryCapability.Use);
		if (access is null)
			return OperationResult<HealthVialConsumedReceipt>.Failure(
				ErrorCode.Unauthorized, "Health vial use capability is missing.");
		var reservation = _health.Reserve(actor.CharacterId);
		if (reservation.Failed) return Failure<HealthVialConsumedReceipt>(reservation.Error!);
		var before = reservation.Value.Snapshot;
		if (before.IsDead || before.CurrentHealth >= before.MaximumHealth)
		{
			_health.Release(reservation.Value);
			return OperationResult<HealthVialConsumedReceipt>.Failure(
				ErrorCode.PolicyDenied, "Health vial requires a living injured character.");
		}
		var removed = _layout.Remove(inventory.Value, vialItemId);
		if (removed.Failed)
		{
			_health.Release(reservation.Value);
			return Failure<HealthVialConsumedReceipt>(removed.Error!);
		}

		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require(access);
		HL2RPUnitOfWork.RequireActorState(unitOfWork, _repositories, character);
		var inventoryEditor = unitOfWork.Edit(_repositories.Inventories, inventory);
		if (inventoryEditor is null)
		{
			_health.Release(reservation.Value);
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return OperationResult<HealthVialConsumedReceipt>.Failure(ErrorCode.Conflict, "Inventory changed.");
		}
		inventoryEditor.Replace(removed.Value);
		unitOfWork.Save(inventoryEditor);
		unitOfWork.Delete(_repositories.Items, vial);
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		if (!committed.Succeeded)
		{
			_health.Release(reservation.Value);
			return CombatPersistence.Failure<HealthVialConsumedReceipt>(committed.Error!);
		}
		var healed = _health.CommitHealing(reservation.Value, _healing);
		return OperationResult<HealthVialConsumedReceipt>.Success(new HealthVialConsumedReceipt(
			vialItemId, before.CurrentHealth, healed.CurrentHealth,
			committed.Value!.Sequence, committed.Value));
	}

	private static OperationResult<T> Failure<T>(OperationError error) =>
		OperationResult<T>.Failure(error.Code, error.Message);
}
