#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Showcase.Combat;

public sealed record DeathRespawnState(
	AccountId AccountId,
	CharacterId CharacterId,
	DateTimeOffset DiedAtUtc,
	DateTimeOffset RespawnAvailableAtUtc,
	string Cause)
{
	public bool CanRespawn(DateTimeOffset nowUtc) => nowUtc >= RespawnAvailableAtUtc;
}

public sealed record DeathTransitionReceipt(
	DeathRespawnState Respawn,
	WorldItemRecord? DroppedPistol,
	long CommitSequence,
	CommitReceipt? Commit = null,
	OperationError? BoundaryError = null);

public interface ICombatLifecycleBoundary
{
	/// <summary>Must synchronously revoke combat, action, and interaction sessions.</summary>
	void ClearSessions(InventoryActor actor);
	void PublishDeath(DeathTransitionReceipt transition);
	void PublishRespawn(DeathRespawnState state);
}

public interface IDeathPresentationBoundary
{
	void PublishDeath(DeathTransitionReceipt transition);
	void PublishRespawn(DeathRespawnState state);
}

/// <summary>
/// Neutral lifecycle adapter that clears the concrete combat and interaction
/// authorities before forwarding immutable death/respawn presentation state.
/// </summary>
public sealed class CombatSessionLifecycleBoundary : ICombatLifecycleBoundary
{
	private readonly PistolCombatService _combat;
	private readonly InteractionAuthorityService _interactions;
	private readonly IDeathPresentationBoundary _presentation;

	public CombatSessionLifecycleBoundary(
		PistolCombatService combat,
		InteractionAuthorityService interactions,
		IDeathPresentationBoundary presentation)
	{
		_combat = combat ?? throw new ArgumentNullException(nameof(combat));
		_interactions = interactions ?? throw new ArgumentNullException(nameof(interactions));
		_presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
	}

	public void ClearSessions(InventoryActor actor)
	{
		_combat.ClearCharacter(actor.CharacterId);
		_interactions.CharacterChanged(actor.ConnectionId, actor.CharacterId);
	}

	public void PublishDeath(DeathTransitionReceipt transition) => _presentation.PublishDeath(transition);
	public void PublishRespawn(DeathRespawnState state) => _presentation.PublishRespawn(state);
}

/// <summary>
/// Owns the reference death transition. Equipped-pistol state, inventory
/// placement, and world location are one persistence commit.
/// </summary>
public sealed class CombatLifecycleService
{
	public static readonly TimeSpan DefaultRespawnDelay = TimeSpan.FromSeconds(10);

	private readonly DomainRepositories _repositories;
	private readonly CompiledSchema _schema;
	private readonly InventoryLayoutService _layout;
	private readonly IWorldModelCatalog _models;
	private readonly IHexClock _clock;
	private readonly ICombatLifecycleBoundary _boundary;
	private readonly TimeSpan _respawnDelay;
	private readonly Dictionary<CharacterId, DeathRespawnState> _deaths = new();

	public CombatLifecycleService(
		DomainRepositories repositories,
		CompiledSchema schema,
		InventoryLayoutService layout,
		IWorldModelCatalog models,
		IHexClock clock,
		ICombatLifecycleBoundary boundary,
		TimeSpan? respawnDelay = null)
	{
		_repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
		_schema = schema ?? throw new ArgumentNullException(nameof(schema));
		_layout = layout ?? throw new ArgumentNullException(nameof(layout));
		_models = models ?? throw new ArgumentNullException(nameof(models));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
		_boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
		_respawnDelay = respawnDelay ?? DefaultRespawnDelay;
		if (_respawnDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(respawnDelay));
	}

	public DeathRespawnState? GetState(CharacterId characterId) =>
		_deaths.TryGetValue(characterId, out var state) ? state : null;

	public async ValueTask<OperationResult<DeathTransitionReceipt>> DieAsync(
		InventoryActor actor,
		InventoryId inventoryId,
		WorldTransformRecord dropTransform,
		string cause,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(dropTransform);
		if (string.IsNullOrWhiteSpace(cause))
			return OperationResult<DeathTransitionReceipt>.Failure(ErrorCode.InvalidArgument,
				"Death cause is required.");
		if (_deaths.ContainsKey(actor.CharacterId))
			return OperationResult<DeathTransitionReceipt>.Failure(ErrorCode.Conflict,
				"Character is already in the death lifecycle.");
		var character = _repositories.Characters.Find(DomainKeys.Character(actor.CharacterId));
		var inventory = _repositories.Inventories.Find(DomainKeys.Inventory(inventoryId));
		if (character is null || inventory is null)
			return OperationResult<DeathTransitionReceipt>.Failure(ErrorCode.NotFound,
				"Character or death inventory was not found.");
		if (character.Value.AccountId != actor.AccountId)
			return OperationResult<DeathTransitionReceipt>.Failure(ErrorCode.Unauthorized,
				"Active character does not belong to the authenticated actor.");

		var equipped = new List<(DocumentSnapshot<ItemRecord> Document, PistolItemState State)>();
		foreach (var placement in inventory.Value.Placements)
		{
			var item = _repositories.Items.Find(DomainKeys.Item(placement.ItemId));
			if (item?.Value.Definition.Value != HL2RPIds.Items.Pistol) continue;
			var state = CombatPersistence.DecodeTrait(item.Value, CombatTraitNames.Pistol, HL2RPPersistence.Pistol);
			if (state.Failed) return Failure<DeathTransitionReceipt>(state.Error!);
			if (state.Value.Equipped) equipped.Add((item, state.Value));
		}
		if (equipped.Count > 1)
			return OperationResult<DeathTransitionReceipt>.Failure(ErrorCode.Conflict,
				"Multiple equipped pistols violate combat state invariants.");

		WorldItemRecord? worldItem = null;
		long sequence = 0;
		CommitReceipt? commitReceipt = null;
		if (equipped.Count == 1)
		{
			var pistol = equipped[0];
			if (!_schema.Items.TryGet(pistol.Document.Value.Definition.Value, out var definition) ||
				!definition!.CanDrop || string.IsNullOrWhiteSpace(definition.WorldModel) ||
				!_models.IsValidModel(definition.WorldModel))
				return OperationResult<DeathTransitionReceipt>.Failure(ErrorCode.PolicyDenied,
					"Equipped pistol has no validated world-drop model.");
			if (_repositories.WorldItems.Find(DomainKeys.WorldItem(pistol.Document.Value.Id)) is not null)
				return OperationResult<DeathTransitionReceipt>.Failure(ErrorCode.Conflict,
					"Equipped pistol already has a world location.");
			var removed = _layout.Remove(inventory.Value, pistol.Document.Value.Id);
			if (removed.Failed) return Failure<DeathTransitionReceipt>(removed.Error!);
			worldItem = new WorldItemRecord
			{
				ItemId = pistol.Document.Value.Id,
				Transform = dropTransform,
				Revision = 0
			};

			var unitOfWork = _repositories.Provider.BeginUnitOfWork();
			var inventoryEditor = unitOfWork.Edit(_repositories.Inventories, inventory);
			var itemEditor = unitOfWork.Edit(_repositories.Items, pistol.Document);
			if (inventoryEditor is null || itemEditor is null)
			{
				await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
				return OperationResult<DeathTransitionReceipt>.Failure(ErrorCode.Conflict,
					"Death inventory or pistol changed before commit.");
			}
			inventoryEditor.Replace(removed.Value);
			itemEditor.Replace(CombatPersistence.ReplaceTrait(itemEditor.Value, CombatTraitNames.Pistol,
				HL2RPPersistence.Pistol, pistol.State with { Equipped = false, Raised = false }));
			unitOfWork.Save(inventoryEditor);
			unitOfWork.Save(itemEditor);
			unitOfWork.Create(_repositories.WorldItems, DomainKeys.WorldItem(pistol.Document.Value.Id), worldItem);
			var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
			if (!committed.Succeeded) return CombatPersistence.Failure<DeathTransitionReceipt>(committed.Error!);
			sequence = committed.Value!.Sequence;
			commitReceipt = committed.Value;
		}

		var now = _clock.UtcNow;
		var respawn = new DeathRespawnState(actor.AccountId, actor.CharacterId, now,
			now + _respawnDelay, cause.Trim());
		var receipt = new DeathTransitionReceipt(respawn, worldItem, sequence, commitReceipt);
		_deaths.Add(actor.CharacterId, respawn);
		OperationError? boundaryError = null;
		try
		{
			_boundary.ClearSessions(actor);
		}
		catch (Exception exception)
		{
			boundaryError = new OperationError(
				ErrorCode.InternalError,
				$"Death committed, but combat-session cleanup failed: {exception.Message}");
		}
		try
		{
			_boundary.PublishDeath(receipt);
		}
		catch (Exception exception)
		{
			var message = $"Death committed, but presentation failed: {exception.Message}";
			boundaryError = boundaryError is null
				? new OperationError(ErrorCode.InternalError, message)
				: new OperationError(
					ErrorCode.InternalError,
					$"{boundaryError.Message} {message}");
		}
		if (boundaryError is not null) receipt = receipt with { BoundaryError = boundaryError };
		return OperationResult<DeathTransitionReceipt>.Success(receipt);
	}

	public OperationResult<DeathRespawnState> Respawn(InventoryActor actor)
	{
		if (!_deaths.TryGetValue(actor.CharacterId, out var state) || state.AccountId != actor.AccountId)
			return OperationResult<DeathRespawnState>.Failure(ErrorCode.NotFound,
				"No death lifecycle is bound to this actor.");
		if (!state.CanRespawn(_clock.UtcNow))
			return OperationResult<DeathRespawnState>.Failure(ErrorCode.PolicyDenied,
				"Respawn delay has not completed.");
		_deaths.Remove(actor.CharacterId);
		_boundary.PublishRespawn(state);
		return OperationResult<DeathRespawnState>.Success(state);
	}

	private static OperationResult<T> Failure<T>(OperationError error) =>
		OperationResult<T>.Failure(error.Code, error.Message);
}
