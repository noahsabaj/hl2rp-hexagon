#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Persistence;

namespace HL2RP.V2.Features;

public sealed record DoorStateChangedEvent(
	SceneEntityId SceneEntityId,
	DoorEntityState State,
	long CommitSequence,
	CommitReceipt Commit ) : IHL2RPCommittedOperation;

public sealed record ForcefieldStateChangedEvent(
	SceneEntityId SceneEntityId,
	ForcefieldEntityState State,
	long CommitSequence,
	CommitReceipt Commit ) : IHL2RPCommittedOperation;

/// <summary>
/// Transactional mutation boundary for scene state that has live physical
/// presentation. Scene behavior is notified only after the durable commit.
/// </summary>
public sealed class HL2RPSceneEntityBehaviorService
{
	private const string DoorOwnershipCategory = "door_ownership";

	private readonly DomainRepositories _repositories;
	private readonly ISceneSessionResolver _sessions;
	private readonly IHexClock _clock;
	private readonly PolicyPipeline<HL2RPFeaturePolicyContext> _policy;
	private readonly PostCommitEventBus<DoorStateChangedEvent> _doorEvents;
	private readonly PostCommitEventBus<ForcefieldStateChangedEvent> _forcefieldEvents;
	private readonly PostCommitEventBus<AdminAuditFact> _audit;

	public HL2RPSceneEntityBehaviorService(
		DomainRepositories repositories,
		ISceneSessionResolver sessions,
		IHexClock clock,
		PolicyPipeline<HL2RPFeaturePolicyContext> policy,
		PostCommitEventBus<DoorStateChangedEvent>? doorEvents = null,
		PostCommitEventBus<ForcefieldStateChangedEvent>? forcefieldEvents = null,
		PostCommitEventBus<AdminAuditFact>? audit = null )
	{
		_repositories = repositories ?? throw new ArgumentNullException( nameof(repositories) );
		_sessions = sessions ?? throw new ArgumentNullException( nameof(sessions) );
		_clock = clock ?? throw new ArgumentNullException( nameof(clock) );
		_policy = policy ?? throw new ArgumentNullException( nameof(policy) );
		_doorEvents = doorEvents ?? new PostCommitEventBus<DoorStateChangedEvent>();
		_forcefieldEvents = forcefieldEvents ?? new PostCommitEventBus<ForcefieldStateChangedEvent>();
		_audit = audit ?? new PostCommitEventBus<AdminAuditFact>();
	}

	public async ValueTask<OperationResult<DoorStateChangedEvent>> ToggleDoorAsync(
		InventoryActor actor,
		InteractionSessionId doorSessionId,
		CancellationToken cancellationToken = default )
	{
		var session = _sessions.Resolve( actor, doorSessionId, InteractionSessionKind.Door );
		if ( session.Failed ) return Failure<DoorStateChangedEvent>( session.Error! );
		var loaded = Load( actor, session.Value.SceneEntityId, "door", HL2RPPersistence.DoorState );
		if ( loaded.Failed ) return Failure<DoorStateChangedEvent>( loaded.Error! );
		var authorized = AuthorizeDoor( actor, loaded.Value.Character.Value, loaded.Value.Document.Value.Id, loaded.Value.State );
		if ( authorized.Failed ) return Failure<DoorStateChangedEvent>( authorized.Error! );
		var policy = Evaluate( actor, HL2RPFeatureOperation.ToggleDoor, loaded.Value.Document.Value.Id );
		if ( policy.Failed ) return Failure<DoorStateChangedEvent>( policy.Error! );

		var next = loaded.Value.State with { IsOpen = !loaded.Value.State.IsOpen };
		var committed = await CommitAsync(
			loaded.Value.Character,
			loaded.Value.Document,
			HL2RPPersistence.Payload( HL2RPPersistence.DoorState, next ),
			session.Value.CommitProof,
			cancellationToken );
		if ( !committed.Succeeded ) return HL2RPFeaturePersistence.Failure<DoorStateChangedEvent>( committed.Error! );
		var receipt = new DoorStateChangedEvent(
			loaded.Value.Document.Value.Id, next, committed.Value!.Sequence, committed.Value );
		_doorEvents.Publish( receipt );
		PublishAudit( actor, HL2RPFeatureOperation.ToggleDoor, receipt.SceneEntityId, receipt.CommitSequence );
		return OperationResult<DoorStateChangedEvent>.Success( receipt );
	}

	public async ValueTask<OperationResult<ForcefieldStateChangedEvent>> ToggleForcefieldAsync(
		InventoryActor actor,
		SceneEntityId sceneEntityId,
		CancellationToken cancellationToken = default )
	{
		var loaded = Load( actor, sceneEntityId, "forcefield", HL2RPPersistence.ForcefieldState );
		if ( loaded.Failed ) return Failure<ForcefieldStateChangedEvent>( loaded.Error! );
		var policy = Evaluate( actor, HL2RPFeatureOperation.ToggleForcefield, sceneEntityId );
		if ( policy.Failed ) return Failure<ForcefieldStateChangedEvent>( policy.Error! );

		var next = loaded.Value.State with { Enabled = !loaded.Value.State.Enabled };
		var committed = await CommitAsync(
			loaded.Value.Character,
			loaded.Value.Document,
			HL2RPPersistence.Payload( HL2RPPersistence.ForcefieldState, next ),
			null,
			cancellationToken );
		if ( !committed.Succeeded ) return HL2RPFeaturePersistence.Failure<ForcefieldStateChangedEvent>( committed.Error! );
		var receipt = new ForcefieldStateChangedEvent(
			sceneEntityId, next, committed.Value!.Sequence, committed.Value );
		_forcefieldEvents.Publish( receipt );
		PublishAudit( actor, HL2RPFeatureOperation.ToggleForcefield, sceneEntityId, receipt.CommitSequence );
		return OperationResult<ForcefieldStateChangedEvent>.Success( receipt );
	}

	private OperationResult AuthorizeDoor(
		InventoryActor actor,
		CharacterRecord character,
		SceneEntityId doorId,
		DoorEntityState state )
	{
		var combine = character.Faction.Value is
			HL2RPIds.Factions.CivilProtection or HL2RPIds.Factions.Overwatch;
		if ( state.CombineLocked && !combine )
			return OperationResult.Failure( ErrorCode.Unauthorized, "A Combine-locked door requires a CP or Overwatch role." );
		var owner = _repositories.CharacterReferences.Find( $"door-ownership-{doorId}" );
		if ( owner is not null && (owner.Value.Category != DoorOwnershipCategory || owner.Value.SceneEntityId != doorId) )
			return OperationResult.Failure( ErrorCode.Conflict, "Canonical door ownership reference is malformed." );
		if ( owner is not null && owner.Value.CharacterId != actor.CharacterId && !combine )
			return OperationResult.Failure( ErrorCode.Unauthorized, "Only the owner or Combine may operate this door." );
		return OperationResult.Success();
	}

	private OperationResult<LoadedSceneState<T>> Load<T>(
		InventoryActor actor,
		SceneEntityId id,
		string requiredKind,
		IPersistedTypeCodec<T> codec ) where T : class
	{
		var character = _repositories.Characters.Find( DomainKeys.Character( actor.CharacterId ) );
		var document = _repositories.SceneEntities.Find( DomainKeys.SceneEntity( id ) );
		if ( character is null || document is null )
			return OperationResult<LoadedSceneState<T>>.Failure( ErrorCode.NotFound, "Actor or scene state was not found." );
		if ( character.Value.AccountId != actor.AccountId )
			return OperationResult<LoadedSceneState<T>>.Failure( ErrorCode.Unauthorized, "Actor binding is invalid." );
		if ( document.Value.Kind != requiredKind )
			return OperationResult<LoadedSceneState<T>>.Failure( ErrorCode.PolicyDenied, "Scene entity has the wrong kind." );
		var state = HL2RPFeaturePersistence.Decode( document.Value.State, codec );
		return state.Succeeded
			? OperationResult<LoadedSceneState<T>>.Success( new LoadedSceneState<T>( character, document, state.Value ) )
			: OperationResult<LoadedSceneState<T>>.Failure( state.Error!.Code, state.Error.Message );
	}

	private async ValueTask<PersistenceResult<CommitReceipt>> CommitAsync(
		DocumentSnapshot<CharacterRecord> character,
		DocumentSnapshot<PersistentSceneEntityRecord> document,
		TypedPayload state,
		ICommitPrecondition? sessionProof,
		CancellationToken cancellationToken )
	{
		var unit = _repositories.Provider.BeginUnitOfWork();
		if ( sessionProof is not null ) unit.Require( sessionProof );
		HL2RPUnitOfWork.RequireActorState( unit, _repositories, character );
		var editor = unit.Edit( _repositories.SceneEntities, document );
		if ( editor is null )
		{
			await HL2RPUnitOfWork.DisposeAsync( unit );
			return PersistenceResult<CommitReceipt>.Failure(
				new PersistenceError( PersistenceErrorCode.RevisionConflict, "Scene entity changed." ) );
		}
		editor.Replace( editor.Value with { State = state } );
		unit.Save( editor );
		return await HL2RPUnitOfWork.CommitAndDisposeAsync( unit, cancellationToken );
	}

	private OperationResult Evaluate( InventoryActor actor, HL2RPFeatureOperation operation, SceneEntityId id ) =>
		_policy.Evaluate( new HL2RPFeaturePolicyContext
		{
			Actor = actor,
			Operation = operation,
			SceneEntityId = id
		} );

	private void PublishAudit( InventoryActor actor, HL2RPFeatureOperation operation, SceneEntityId id, long sequence ) =>
		HL2RPFeaturePersistence.PublishAudit( _audit, actor, operation, id.ToString(), _clock.UtcNow, sequence );

	private static OperationResult<T> Failure<T>( OperationError error ) =>
		OperationResult<T>.Failure( error.Code, error.Message );

	private sealed record LoadedSceneState<T>(
		DocumentSnapshot<CharacterRecord> Character,
		DocumentSnapshot<PersistentSceneEntityRecord> Document,
		T State ) where T : class;
}
