#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Persistence;

namespace HL2RP.V2.Features;

public sealed record CombineLockInstalledReceipt(
	SceneEntityId DoorEntityId,
	ItemId LockKitItemId,
	int RemainingInstallations,
	long CommitSequence,
	CommitReceipt Commit ) : IHL2RPCommittedOperation;

public sealed class CombineLockService
{
	private readonly DomainRepositories _repositories;
	private readonly InventoryAccessService _access;
	private readonly ISceneSessionResolver _sessions;
	private readonly IHexClock _clock;
	private readonly PolicyPipeline<HL2RPFeaturePolicyContext> _policy;
	private readonly PostCommitEventBus<CombineLockInstalledReceipt> _events;
	private readonly PostCommitEventBus<AdminAuditFact> _audit;

	public CombineLockService(
		DomainRepositories repositories,
		InventoryAccessService access,
		ISceneSessionResolver sessions,
		IHexClock clock,
		PolicyPipeline<HL2RPFeaturePolicyContext> policy,
		PostCommitEventBus<CombineLockInstalledReceipt>? events = null,
		PostCommitEventBus<AdminAuditFact>? audit = null )
	{
		_repositories = repositories;
		_access = access;
		_sessions = sessions;
		_clock = clock;
		_policy = policy;
		_events = events ?? new PostCommitEventBus<CombineLockInstalledReceipt>();
		_audit = audit ?? new PostCommitEventBus<AdminAuditFact>();
	}

	public async ValueTask<OperationResult<CombineLockInstalledReceipt>> InstallAsync(
		InventoryActor actor,
		InteractionSessionId doorSessionId,
		InventoryId sourceInventoryId,
		ItemId lockKitItemId,
		CancellationToken cancellationToken = default )
	{
		var session = _sessions.Resolve( actor, doorSessionId, InteractionSessionKind.Door );
		if ( session.Failed )
			return OperationResult<CombineLockInstalledReceipt>.Failure(
				session.Error!.Code, session.Error.Message );
		var character = _repositories.Characters.Find( DomainKeys.Character( actor.CharacterId ) );
		var source = _repositories.Inventories.Find( DomainKeys.Inventory( sourceInventoryId ) );
		var kit = _repositories.Items.Find( DomainKeys.Item( lockKitItemId ) );
		var door = _repositories.SceneEntities.Find( DomainKeys.SceneEntity( session.Value.SceneEntityId ) );
		if ( character is null || source is null || kit is null || door is null )
			return OperationResult<CombineLockInstalledReceipt>.Failure(
				ErrorCode.NotFound, "Character, lock kit, inventory or door was not found." );
		if ( character.Value.AccountId != actor.AccountId ||
			character.Value.Faction.Value is not HL2RPIds.Factions.CivilProtection and not HL2RPIds.Factions.Overwatch )
			return OperationResult<CombineLockInstalledReceipt>.Failure(
				ErrorCode.Unauthorized, "Only Civil Protection or Overwatch may install a Combine lock." );
		if ( source.Value.Find( lockKitItemId ) is null ||
			kit.Value.Definition.Value != HL2RPIds.Items.CombineLockKit )
			return OperationResult<CombineLockInstalledReceipt>.Failure(
				ErrorCode.Unauthorized, "Lock kit membership proof failed." );
		var access = _access.Prove(
			actor.ConnectionId,
			actor.CharacterId,
			sourceInventoryId,
			InventoryCapability.View | InventoryCapability.Use );
		if ( access is null )
			return OperationResult<CombineLockInstalledReceipt>.Failure(
				ErrorCode.Unauthorized, "Lock kit use capability is missing." );
		if ( door.Value.Kind != "door" )
			return OperationResult<CombineLockInstalledReceipt>.Failure(
				ErrorCode.PolicyDenied, "Bound session target is not a door." );
		if ( !kit.Value.Traits.TryGetValue( "lock_kit", out var kitPayload ) )
			return OperationResult<CombineLockInstalledReceipt>.Failure(
				ErrorCode.PersistedTypeInvalid, "Lock kit state is missing." );
		var kitState = HL2RPFeaturePersistence.Decode( kitPayload, HL2RPPersistence.CombineLockKit );
		var doorState = HL2RPFeaturePersistence.Decode( door.Value.State, HL2RPPersistence.DoorState );
		if ( kitState.Failed || kitState.Value.RemainingInstallations <= 0 )
			return OperationResult<CombineLockInstalledReceipt>.Failure(
				kitState.Failed ? kitState.Error!.Code : ErrorCode.Conflict,
				kitState.Failed ? kitState.Error!.Message : "Lock kit has no remaining installations." );
		if ( doorState.Failed )
			return OperationResult<CombineLockInstalledReceipt>.Failure(
				doorState.Error!.Code, doorState.Error.Message );
		if ( doorState.Value.CombineLocked )
			return OperationResult<CombineLockInstalledReceipt>.Failure(
				ErrorCode.Conflict, "Door already has a Combine lock." );
		var policy = _policy.Evaluate( new HL2RPFeaturePolicyContext
		{
			Actor = actor,
			Operation = HL2RPFeatureOperation.InstallCombineLock,
			SceneEntityId = session.Value.SceneEntityId,
			ItemId = lockKitItemId
		} );
		if ( policy.Failed )
			return OperationResult<CombineLockInstalledReceipt>.Failure(
				policy.Error!.Code, policy.Error.Message );

		var remaining = kitState.Value.RemainingInstallations - 1;
		var doorAfter = door.Value with
		{
			State = HL2RPPersistence.Payload(
				HL2RPPersistence.DoorState,
				doorState.Value with { CombineLocked = true } )
		};
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require( session.Value.CommitProof );
		unitOfWork.Require( access );
		HL2RPUnitOfWork.RequireActorState( unitOfWork, _repositories, character );
		unitOfWork.RequireUnchanged( _repositories.Inventories, source );
		var doorEditor = unitOfWork.Edit( _repositories.SceneEntities, door );
		if ( doorEditor is null )
		{
			await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
			return OperationResult<CombineLockInstalledReceipt>.Failure( ErrorCode.Conflict, "Door changed." );
		}
		doorEditor.Replace( doorAfter );
		unitOfWork.Save( doorEditor );
		if ( remaining == 0 )
		{
			var inventoryEditor = unitOfWork.Edit( _repositories.Inventories, source );
			if ( inventoryEditor is null )
			{
				await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
				return OperationResult<CombineLockInstalledReceipt>.Failure( ErrorCode.Conflict, "Source inventory changed." );
			}
			inventoryEditor.Replace( source.Value with
			{
				Placements = source.Value.Placements
					.Where( placement => placement.ItemId != lockKitItemId )
					.ToArray()
			} );
			unitOfWork.Save( inventoryEditor );
			unitOfWork.Delete( _repositories.Items, kit );
		}
		else
		{
			var kitEditor = unitOfWork.Edit( _repositories.Items, kit );
			if ( kitEditor is null )
			{
				await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
				return OperationResult<CombineLockInstalledReceipt>.Failure( ErrorCode.Conflict, "Lock kit changed." );
			}
			var traits = new Dictionary<string, TypedPayload>( kit.Value.Traits, StringComparer.Ordinal )
			{
				["lock_kit"] = HL2RPPersistence.Payload(
					HL2RPPersistence.CombineLockKit,
					kitState.Value with { RemainingInstallations = remaining } )
			};
			kitEditor.Replace( kit.Value with { Traits = traits } );
			unitOfWork.Save( kitEditor );
		}
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unitOfWork, cancellationToken );
		if ( !committed.Succeeded )
			return HL2RPFeaturePersistence.Failure<CombineLockInstalledReceipt>( committed.Error! );
		var receipt = new CombineLockInstalledReceipt(
			session.Value.SceneEntityId,
			lockKitItemId,
			remaining,
			committed.Value!.Sequence,
			committed.Value );
		_events.Publish( receipt );
		HL2RPFeaturePersistence.PublishAudit(
			_audit,
			actor,
			HL2RPFeatureOperation.InstallCombineLock,
			session.Value.SceneEntityId.ToString(),
			_clock.UtcNow,
			committed.Value.Sequence );
		return OperationResult<CombineLockInstalledReceipt>.Success( receipt );
	}
}
