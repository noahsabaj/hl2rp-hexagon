#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Persistence;

namespace HL2RP.V2.Features;

public sealed record DoorOwnershipReceipt(
	SceneEntityId DoorEntityId,
	CharacterId CharacterId,
	bool Claimed,
	long CommitSequence,
	CommitReceipt Commit ) : IHL2RPCommittedOperation;

/// <summary>
/// The sole personal-door ownership boundary. Ownership lives only in the indexed
/// CharacterReferenceRecord, so generic character deletion removes it without
/// decoding or rewriting scene state.
/// </summary>
public sealed class DoorOwnershipService
{
	private const string ReferenceCategory = "door_ownership";

	private readonly DomainRepositories _repositories;
	private readonly CharacterReferenceMutationService _references;
	private readonly ISceneSessionResolver _sessions;
	private readonly IHexClock _clock;
	private readonly PolicyPipeline<HL2RPFeaturePolicyContext> _policy;
	private readonly PostCommitEventBus<DoorOwnershipReceipt> _events;
	private readonly PostCommitEventBus<AdminAuditFact> _audit;

	public DoorOwnershipService(
		DomainRepositories repositories,
		ISceneSessionResolver sessions,
		IHexClock clock,
		PolicyPipeline<HL2RPFeaturePolicyContext> policy,
		PostCommitEventBus<DoorOwnershipReceipt>? events = null,
		PostCommitEventBus<AdminAuditFact>? audit = null )
	{
		_repositories = repositories;
		_references = new CharacterReferenceMutationService( repositories );
		_sessions = sessions;
		_clock = clock;
		_policy = policy;
		_events = events ?? new PostCommitEventBus<DoorOwnershipReceipt>();
		_audit = audit ?? new PostCommitEventBus<AdminAuditFact>();
	}

	public ValueTask<OperationResult<DoorOwnershipReceipt>> ClaimAsync(
		InventoryActor actor,
		InteractionSessionId doorSessionId,
		CancellationToken cancellationToken = default ) =>
		ChangeAsync( actor, doorSessionId, claim: true, cancellationToken );

	public ValueTask<OperationResult<DoorOwnershipReceipt>> ReleaseAsync(
		InventoryActor actor,
		InteractionSessionId doorSessionId,
		CancellationToken cancellationToken = default ) =>
		ChangeAsync( actor, doorSessionId, claim: false, cancellationToken );

	private async ValueTask<OperationResult<DoorOwnershipReceipt>> ChangeAsync(
		InventoryActor actor,
		InteractionSessionId doorSessionId,
		bool claim,
		CancellationToken cancellationToken )
	{
		var resolved = ResolveDoor( actor, doorSessionId );
		if ( resolved.Failed )
			return OperationResult<DoorOwnershipReceipt>.Failure(
				resolved.Error!.Code, resolved.Error.Message );
		if ( claim && resolved.Value.State.CombineLocked )
			return OperationResult<DoorOwnershipReceipt>.Failure(
				ErrorCode.PolicyDenied, "A Combine-locked door cannot be personally claimed." );
		var referenceKey = ReferenceKey( resolved.Value.Door.Value.Id );
		var reference = _repositories.CharacterReferences.Find( referenceKey );
		if ( reference is not null && (reference.Value.Category != ReferenceCategory ||
			reference.Value.SceneEntityId != resolved.Value.Door.Value.Id) )
			return OperationResult<DoorOwnershipReceipt>.Failure(
				ErrorCode.Conflict, "Canonical door ownership reference is malformed." );
		if ( claim && reference is not null )
			return OperationResult<DoorOwnershipReceipt>.Failure(
				ErrorCode.Conflict, "Door is already owned." );
		if ( !claim && (reference is null || reference.Value.CharacterId != actor.CharacterId) )
			return OperationResult<DoorOwnershipReceipt>.Failure(
				ErrorCode.Unauthorized, "Only the current owner may release this door." );
		var operation = claim ? HL2RPFeatureOperation.ClaimDoor : HL2RPFeatureOperation.ReleaseDoor;
		var policy = _policy.Evaluate( new HL2RPFeaturePolicyContext
		{
			Actor = actor,
			Operation = operation,
			SceneEntityId = resolved.Value.Door.Value.Id
		} );
		if ( policy.Failed )
			return OperationResult<DoorOwnershipReceipt>.Failure(
				policy.Error!.Code, policy.Error.Message );
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require( resolved.Value.SessionProof );
		HL2RPUnitOfWork.RequireActorState( unitOfWork, _repositories, resolved.Value.Character );
		unitOfWork.RequireUnchanged( _repositories.SceneEntities, resolved.Value.Door );
		OperationResult staged;
		if ( claim )
		{
			var ownershipReference = new CharacterReferenceRecord
			{
				Category = ReferenceCategory,
				CharacterId = actor.CharacterId,
				SceneEntityId = resolved.Value.Door.Value.Id,
				State = HL2RPPersistence.Payload(
					HL2RPPersistence.DoorOwnership,
					new DoorOwnershipReferenceState { AcquiredAtUtc = _clock.UtcNow } )
			};
			staged = _references.StageUpsert( unitOfWork, referenceKey, ownershipReference );
		}
		else
		{
			staged = _references.StageDelete( unitOfWork, referenceKey );
		}
		if ( staged.Failed )
		{
			await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
			return OperationResult<DoorOwnershipReceipt>.Failure(
				staged.Error!.Code, staged.Error.Message );
		}
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unitOfWork, cancellationToken );
		if ( !committed.Succeeded )
			return HL2RPFeaturePersistence.Failure<DoorOwnershipReceipt>( committed.Error! );
		var receipt = new DoorOwnershipReceipt(
			resolved.Value.Door.Value.Id,
			actor.CharacterId,
			claim,
			committed.Value!.Sequence,
			committed.Value );
		_events.Publish( receipt );
		HL2RPFeaturePersistence.PublishAudit(
			_audit,
			actor,
			operation,
			receipt.DoorEntityId.ToString(),
			_clock.UtcNow,
			committed.Value.Sequence );
		return OperationResult<DoorOwnershipReceipt>.Success( receipt );
	}

	private OperationResult<ResolvedDoor> ResolveDoor(
		InventoryActor actor,
		InteractionSessionId doorSessionId )
	{
		var session = _sessions.Resolve( actor, doorSessionId, InteractionSessionKind.Door );
		if ( session.Failed )
			return OperationResult<ResolvedDoor>.Failure( session.Error!.Code, session.Error.Message );
		var character = _repositories.Characters.Find( DomainKeys.Character( actor.CharacterId ) );
		var door = _repositories.SceneEntities.Find( DomainKeys.SceneEntity( session.Value.SceneEntityId ) );
		if ( character is null || character.Value.AccountId != actor.AccountId )
			return OperationResult<ResolvedDoor>.Failure( ErrorCode.Unauthorized, "Actor binding is invalid." );
		if ( door is null || door.Value.Kind != "door" )
			return OperationResult<ResolvedDoor>.Failure( ErrorCode.NotFound, "Bound door state was not found." );
		var state = HL2RPFeaturePersistence.Decode( door.Value.State, HL2RPPersistence.DoorState );
		return state.Succeeded
			? OperationResult<ResolvedDoor>.Success( new ResolvedDoor(
				character, door, state.Value, session.Value.CommitProof ) )
			: OperationResult<ResolvedDoor>.Failure( state.Error!.Code, state.Error.Message );
	}

	private static string ReferenceKey( SceneEntityId doorEntityId ) =>
		$"door-ownership-{doorEntityId}";

	private sealed record ResolvedDoor(
		DocumentSnapshot<CharacterRecord> Character,
		DocumentSnapshot<PersistentSceneEntityRecord> Door,
		DoorEntityState State,
		ICommitPrecondition SessionProof );
}
