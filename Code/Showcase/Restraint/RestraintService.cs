#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Policies;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Schema;
using HL2RP.V2.Showcase.Combat;

namespace HL2RP.V2.Showcase.Restraint;

public enum RestrainedActionKind
{
	Weapon = 0,
	Inventory = 1,
	WorldInteraction = 2
}

public interface IRestraintStateReader
{
	bool IsRestrained(CharacterId characterId);
}

public sealed class RestraintStateReader : IRestraintStateReader
{
	private readonly DomainRepositories _repositories;

	public RestraintStateReader(DomainRepositories repositories) =>
		_repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));

	public bool IsRestrained(CharacterId characterId)
	{
		var document = _repositories.CharacterReferences.Find(RestraintService.DocumentKey(characterId));
		if (document is null || document.Value.Category != RestraintService.ReferenceCategory)
			return false;
		var state = RestraintPersistence.Decode(document.Value.State, HL2RPPersistence.Restraint);
		return state.Failed || state.Value.Active;
	}
}

public sealed class RestrainedActionGuard : ICharacterCombatGate
{
	private readonly IRestraintStateReader _state;

	public RestrainedActionGuard(IRestraintStateReader state) =>
		_state = state ?? throw new ArgumentNullException(nameof(state));

	public OperationResult Authorize(CharacterId characterId) => Authorize(characterId, RestrainedActionKind.Weapon);

	public OperationResult Authorize(CharacterId characterId, RestrainedActionKind kind) =>
		_state.IsRestrained(characterId)
			? OperationResult.Failure(ErrorCode.PolicyDenied,
				$"Restrained characters cannot perform {kind.ToString().ToLowerInvariant()} actions.")
			: OperationResult.Success();
}

public sealed class RestrainedItemActionPolicy : IPolicy<ItemActionContext>
{
	private readonly IRestraintStateReader _state;

	public RestrainedItemActionPolicy(IRestraintStateReader state) =>
		_state = state ?? throw new ArgumentNullException(nameof(state));

	public PolicyDecision Evaluate(ItemActionContext context) =>
		_state.IsRestrained(context.Actor.CharacterId)
			? PolicyDecision.Deny("Restrained characters cannot use inventory actions.")
			: PolicyDecision.Allow();
}

public sealed record RestraintTicket(
	InteractionSessionId TicketId,
	InventoryActor Actor,
	CharacterId TargetCharacterId,
	InventoryId SourceInventoryId,
	ItemId ZipTieItemId,
	DateTimeOffset CompletesAtUtc);

public sealed record RestraintReceipt(
	CharacterId RestrainerCharacterId,
	CharacterId RestrainedCharacterId,
	DateTimeOffset RestrainedAtUtc,
	long CommitSequence,
	CommitReceipt Commit) : IHL2RPCommittedOperation;

public sealed record UnrestrainReceipt(
	CharacterId ActorCharacterId,
	CharacterId ReleasedCharacterId,
	long CommitSequence,
	CommitReceipt Commit) : IHL2RPCommittedOperation;

/// <summary>
/// Three-second host-authoritative zip-tie operation. The tie and durable
/// restraint reference are committed together after the authority revalidates.
/// </summary>
public sealed class RestraintService
{
	public const string ReferenceCategory = "restraint";
	public static readonly TimeSpan RestraintDuration = TimeSpan.FromSeconds(3);

	private readonly DomainRepositories _repositories;
	private readonly CharacterReferenceMutationService _references;
	private readonly InventoryAccessService _access;
	private readonly InventoryLayoutService _layout;
	private readonly InteractionAuthorityService _authority;
	private readonly IHexClock _clock;
	private readonly Dictionary<InteractionSessionId, RestraintTicket> _tickets = new();

	public RestraintService(
		DomainRepositories repositories,
		InventoryAccessService access,
		InventoryLayoutService layout,
		InteractionAuthorityService authority,
		IHexClock clock)
	{
		_repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
		_references = new CharacterReferenceMutationService(_repositories);
		_access = access ?? throw new ArgumentNullException(nameof(access));
		_layout = layout ?? throw new ArgumentNullException(nameof(layout));
		_authority = authority ?? throw new ArgumentNullException(nameof(authority));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
	}

	public static string DocumentKey(CharacterId targetCharacterId) => $"restraint-{targetCharacterId}";

	public OperationResult<RestraintTicket> Begin(
		InventoryActor actor,
		CharacterId targetCharacterId,
		InventoryId sourceInventoryId,
		ItemId zipTieItemId)
	{
		var validated = ValidateActorAndTie(actor, sourceInventoryId, zipTieItemId);
		if (validated.Failed) return Failure<RestraintTicket>(validated.Error!);
		if (targetCharacterId == actor.CharacterId)
			return OperationResult<RestraintTicket>.Failure(ErrorCode.PolicyDenied,
				"A character cannot restrain themselves.");
		if (_repositories.Characters.Find(DomainKeys.Character(targetCharacterId)) is null)
			return OperationResult<RestraintTicket>.Failure(ErrorCode.NotFound, "Restraint target was not found.");
		if (IsRestrained(targetCharacterId))
			return OperationResult<RestraintTicket>.Failure(ErrorCode.Conflict, "Target is already restrained.");
		var timed = _authority.BeginTimedAction(
			actor.ConnectionId,
			actor.AccountId,
			actor.CharacterId,
			InteractionTarget.Character(targetCharacterId),
			RestraintDuration);
		if (timed.Failed) return Failure<RestraintTicket>(timed.Error!);
		var ticket = new RestraintTicket(timed.Value.Id, actor, targetCharacterId,
			sourceInventoryId, zipTieItemId, timed.Value.StartedAt + timed.Value.Duration);
		_tickets[ticket.TicketId] = ticket;
		return OperationResult<RestraintTicket>.Success(ticket);
	}

	public async ValueTask<OperationResult<RestraintReceipt>> CompleteAsync(
		InteractionSessionId ticketId,
		InventoryActor actor,
		CancellationToken cancellationToken = default)
	{
		if (!_tickets.TryGetValue(ticketId, out var ticket) || ticket.Actor != actor)
			return OperationResult<RestraintReceipt>.Failure(ErrorCode.Unauthorized,
				"Restraint ticket is stale or belongs to another actor.");
		var completed = _authority.CompleteTimedAction(ticketId, actor.ConnectionId,
			actor.AccountId, actor.CharacterId);
		if (completed.Failed)
		{
			if (completed.Error!.Code != ErrorCode.Conflict) _tickets.Remove(ticketId);
			return Failure<RestraintReceipt>(completed.Error);
		}
		_tickets.Remove(ticketId);
		if (completed.Value.Target != InteractionTarget.Character(ticket.TargetCharacterId))
			return OperationResult<RestraintReceipt>.Failure(ErrorCode.Unauthorized,
				"Restraint target changed during the timed action.");
		var validated = ValidateActorAndTie(actor, ticket.SourceInventoryId, ticket.ZipTieItemId);
		if (validated.Failed) return Failure<RestraintReceipt>(validated.Error!);
		if (IsRestrained(ticket.TargetCharacterId))
			return OperationResult<RestraintReceipt>.Failure(ErrorCode.Conflict, "Target is already restrained.");
		var source = validated.Value.Inventory;
		var removed = _layout.Remove(source.Value, ticket.ZipTieItemId);
		if (removed.Failed) return Failure<RestraintReceipt>(removed.Error!);
		var now = _clock.UtcNow;
		var reference = new CharacterReferenceRecord
		{
			Category = ReferenceCategory,
			CharacterId = ticket.TargetCharacterId,
			RelatedCharacterId = actor.CharacterId,
			State = HL2RPPersistence.Payload(HL2RPPersistence.Restraint,
				new RestraintReferenceState { RestrainedAtUtc = now, Active = true })
		};

		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require(validated.Value.Access);
		HL2RPUnitOfWork.RequireActorState(unitOfWork, _repositories, validated.Value.Character);
		var inventoryEditor = unitOfWork.Edit(_repositories.Inventories, source);
		if (inventoryEditor is null)
		{
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return OperationResult<RestraintReceipt>.Failure(ErrorCode.Conflict,
				"Zip-tie inventory changed before commit.");
		}
		inventoryEditor.Replace(removed.Value);
		unitOfWork.Save(inventoryEditor);
		unitOfWork.Delete(_repositories.Items, validated.Value.Item);
		var staged = _references.StageUpsert(
			unitOfWork, DocumentKey(ticket.TargetCharacterId), reference);
		if (staged.Failed)
		{
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return OperationResult<RestraintReceipt>.Failure(
				staged.Error!.Code, staged.Error.Message);
		}
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		if (!committed.Succeeded) return RestraintPersistence.Failure<RestraintReceipt>(committed.Error!);
		return OperationResult<RestraintReceipt>.Success(new RestraintReceipt(
			actor.CharacterId, ticket.TargetCharacterId, now,
			committed.Value!.Sequence, committed.Value));
	}

	public OperationResult Cancel( InteractionSessionId ticketId, InventoryActor actor )
	{
		if ( !_tickets.TryGetValue( ticketId, out var ticket ) || ticket.Actor != actor )
			return OperationResult.Failure( ErrorCode.Unauthorized,
				"Restraint ticket is stale or belongs to another actor." );
		_tickets.Remove( ticketId );
		return _authority.CancelTimedAction( ticketId, actor.ConnectionId, actor.CharacterId )
			? OperationResult.Success()
			: OperationResult.Failure( ErrorCode.Unauthorized, "Restraint action is no longer active." );
	}

	public async ValueTask<OperationResult<UnrestrainReceipt>> UnrestrainAsync(
		InventoryActor actor,
		CharacterId targetCharacterId,
		CancellationToken cancellationToken = default)
	{
		var role = ValidateRole(actor);
		if (role.Failed) return Failure<UnrestrainReceipt>(role.Error!);
		if (targetCharacterId == actor.CharacterId)
			return OperationResult<UnrestrainReceipt>.Failure(ErrorCode.PolicyDenied,
				"A character cannot remove their own restraint.");
		var authorized = _authority.AuthorizeOneShot(
			actor.ConnectionId,
			actor.AccountId,
			actor.CharacterId,
			InteractionTarget.Character(targetCharacterId));
		if (authorized.Failed) return Failure<UnrestrainReceipt>(authorized.Error!);
		var document = _repositories.CharacterReferences.Find(DocumentKey(targetCharacterId));
		if (document is null || document.Value.Category != ReferenceCategory)
			return OperationResult<UnrestrainReceipt>.Failure(ErrorCode.NotFound,
				"Target is not restrained.");
		var state = RestraintPersistence.Decode(document.Value.State, HL2RPPersistence.Restraint);
		if (state.Failed) return Failure<UnrestrainReceipt>(state.Error!);
		if (!state.Value.Active)
			return OperationResult<UnrestrainReceipt>.Failure(ErrorCode.NotFound,
				"Target is not actively restrained.");
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		HL2RPUnitOfWork.RequireActorState(unitOfWork, _repositories, role.Value);
		var staged = _references.StageDelete(unitOfWork, DocumentKey(targetCharacterId));
		if (staged.Failed)
		{
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return OperationResult<UnrestrainReceipt>.Failure(
				staged.Error!.Code, staged.Error.Message);
		}
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		if (!committed.Succeeded) return RestraintPersistence.Failure<UnrestrainReceipt>(committed.Error!);
		_authority.TargetInvalidated(InteractionTarget.Character(targetCharacterId), "unrestrained");
		return OperationResult<UnrestrainReceipt>.Success(new UnrestrainReceipt(
			actor.CharacterId, targetCharacterId,
			committed.Value!.Sequence, committed.Value));
	}

	public bool IsRestrained(CharacterId characterId) => new RestraintStateReader(_repositories).IsRestrained(characterId);

	private OperationResult<ValidatedTie> ValidateActorAndTie(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId zipTieItemId)
	{
		var role = ValidateRole(actor);
		if (role.Failed) return Failure<ValidatedTie>(role.Error!);
		var inventory = _repositories.Inventories.Find(DomainKeys.Inventory(inventoryId));
		var item = _repositories.Items.Find(DomainKeys.Item(zipTieItemId));
		if (inventory is null || item is null || inventory.Value.Find(zipTieItemId) is null ||
			item.Value.Definition.Value != HL2RPIds.Items.ZipTie)
			return OperationResult<ValidatedTie>.Failure(ErrorCode.NotFound,
				"Claimed zip tie is not a member of the inventory.");
		var access = _access.Prove(actor.ConnectionId, actor.CharacterId, inventoryId,
			InventoryCapability.View | InventoryCapability.Use);
		if (access is null)
			return OperationResult<ValidatedTie>.Failure(ErrorCode.Unauthorized,
				"Zip-tie use capability is missing.");
		return OperationResult<ValidatedTie>.Success(new ValidatedTie(role.Value, inventory, item, access));
	}

	private OperationResult<DocumentSnapshot<CharacterRecord>> ValidateRole(InventoryActor actor)
	{
		var character = _repositories.Characters.Find(DomainKeys.Character(actor.CharacterId));
		if (character is null || character.Value.AccountId != actor.AccountId)
			return OperationResult<DocumentSnapshot<CharacterRecord>>.Failure(
				ErrorCode.Unauthorized, "Authenticated actor binding is invalid.");
		return character.Value.Faction.Value is HL2RPIds.Factions.CivilProtection or HL2RPIds.Factions.Overwatch
			? OperationResult<DocumentSnapshot<CharacterRecord>>.Success(character)
			: OperationResult<DocumentSnapshot<CharacterRecord>>.Failure(ErrorCode.PolicyDenied,
				"Only Civil Protection or Overwatch characters may use restraints.");
	}

	private static OperationResult<T> Failure<T>(OperationError error) =>
		OperationResult<T>.Failure(error.Code, error.Message);

	private sealed record ValidatedTie(
		DocumentSnapshot<CharacterRecord> Character,
		DocumentSnapshot<InventoryRecord> Inventory,
		DocumentSnapshot<ItemRecord> Item,
		InventoryAccessProof Access);
}

internal static class RestraintPersistence
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

	public static OperationResult<T> Failure<T>(PersistenceError error) =>
		OperationResult<T>.Failure(error.Code switch
		{
			PersistenceErrorCode.NotFound => ErrorCode.NotFound,
			PersistenceErrorCode.AlreadyExists or PersistenceErrorCode.RevisionConflict => ErrorCode.Conflict,
			PersistenceErrorCode.TypeNotRegistered or PersistenceErrorCode.CollectionTypeMismatch => ErrorCode.PersistedTypeInvalid,
			PersistenceErrorCode.InvalidOperation => ErrorCode.InvalidArgument,
			_ => ErrorCode.InternalError
		}, error.Message);
}
