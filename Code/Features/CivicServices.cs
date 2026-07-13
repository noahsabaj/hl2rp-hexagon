#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Kernel.Events;

namespace HL2RP.V2.Features;

public sealed record CivicMutationReceipt(
	CharacterId CharacterId,
	CivicRecordState CivicRecord,
	long CommitSequence );

public sealed record IntroductionReceipt(
	CharacterId ViewerCharacterId,
	CharacterId SubjectCharacterId,
	string IntroducedName,
	long CommitSequence );

public sealed record CityObjectivesReceipt(
	SceneEntityId CityEntityId,
	IReadOnlyList<CityObjectiveState> Objectives,
	long CommitSequence );

public interface ICharacterEncounterAuthorizer
{
	OperationResult Authorize( InventoryActor actor, CharacterId subjectCharacterId );
}

public sealed class CivicService
{
	private readonly DomainRepositories _repositories;
	private readonly IHexClock _clock;
	private readonly PolicyPipeline<HL2RPFeaturePolicyContext> _policy;
	private readonly PostCommitEventBus<CivicMutationReceipt> _events;
	private readonly PostCommitEventBus<AdminAuditFact> _audit;

	public CivicService(
		DomainRepositories repositories,
		IHexClock clock,
		PolicyPipeline<HL2RPFeaturePolicyContext> policy,
		PostCommitEventBus<CivicMutationReceipt>? events = null,
		PostCommitEventBus<AdminAuditFact>? audit = null )
	{
		_repositories = repositories ?? throw new ArgumentNullException( nameof(repositories) );
		_clock = clock ?? throw new ArgumentNullException( nameof(clock) );
		_policy = policy ?? throw new ArgumentNullException( nameof(policy) );
		_events = events ?? new PostCommitEventBus<CivicMutationReceipt>();
		_audit = audit ?? new PostCommitEventBus<AdminAuditFact>();
	}

	public OperationResult<HL2RPCharacterState> Read( CharacterId characterId )
	{
		var document = _repositories.Characters.Find( DomainKeys.Character( characterId ) );
		return document is null
			? OperationResult<HL2RPCharacterState>.Failure( ErrorCode.NotFound, "Character was not found." )
			: HL2RPFeaturePersistence.Decode( document.Value.SchemaState, HL2RPPersistence.CharacterState );
	}

	public ValueTask<OperationResult<CivicMutationReceipt>> AddInfractionAsync(
		InventoryActor actor,
		CharacterId targetCharacterId,
		string code,
		string summary,
		int points,
		CancellationToken cancellationToken = default )
	{
		if ( string.IsNullOrWhiteSpace( code ) || code.Length > 32 || code.Any( char.IsControl ) )
			return ValueTask.FromResult( OperationResult<CivicMutationReceipt>.Failure(
				ErrorCode.InvalidArgument, "Infraction code must contain at most 32 printable characters." ) );
		if ( string.IsNullOrWhiteSpace( summary ) || summary.Length > 512 || summary.Any( char.IsControl ) )
			return ValueTask.FromResult( OperationResult<CivicMutationReceipt>.Failure(
				ErrorCode.InvalidArgument, "Infraction summary must contain at most 512 printable characters." ) );
		if ( points <= 0 )
			return ValueTask.FromResult( OperationResult<CivicMutationReceipt>.Failure(
				ErrorCode.InvalidArgument, "Infraction points must be positive." ) );
		return MutateAsync(
			actor,
			targetCharacterId,
			HL2RPFeatureOperation.AddInfraction,
			state =>
			{
				long total;
				try
				{
					total = checked(state.CivicRecord.Points + points);
				}
				catch ( OverflowException )
				{
					return OperationResult<HL2RPCharacterState>.Failure(
						ErrorCode.Conflict, "Civic points would overflow." );
				}
				var infractions = state.CivicRecord.Infractions.Append( new CivicInfractionState
				{
					Code = code.Trim(),
					Summary = summary.Trim(),
					Points = points,
					IssuedAtUtc = _clock.UtcNow,
					IssuedBy = actor.AccountId
				} ).ToArray();
				return OperationResult<HL2RPCharacterState>.Success( state with
				{
					CivicRecord = state.CivicRecord with
					{
						Points = total,
						Infractions = infractions
					}
				} );
			},
			cancellationToken );
	}

	public ValueTask<OperationResult<CivicMutationReceipt>> SetPriorityAsync(
		InventoryActor actor,
		CharacterId targetCharacterId,
		CivicPriorityStatus priority,
		CancellationToken cancellationToken = default )
	{
		if ( !Enum.IsDefined( priority ) )
			return ValueTask.FromResult( OperationResult<CivicMutationReceipt>.Failure(
				ErrorCode.InvalidArgument, "Priority status is invalid." ) );
		return MutateAsync(
			actor,
			targetCharacterId,
			HL2RPFeatureOperation.SetPriority,
			state => OperationResult<HL2RPCharacterState>.Success( state with
			{
				CivicRecord = state.CivicRecord with { Priority = priority }
			} ),
			cancellationToken );
	}

	public ValueTask<OperationResult<CivicMutationReceipt>> UpdateRecordAsync(
		InventoryActor actor,
		CharacterId targetCharacterId,
		CivicPriorityStatus priority,
		string recordText,
		CancellationToken cancellationToken = default )
	{
		recordText ??= string.Empty;
		if ( !Enum.IsDefined( priority ) || recordText.Length > 4_096 ||
			recordText.Any( value => char.IsControl( value ) && value is not '\r' and not '\n' and not '\t' ) )
			return ValueTask.FromResult( OperationResult<CivicMutationReceipt>.Failure(
				ErrorCode.InvalidArgument, "Civic record priority or text is invalid." ) );
		return MutateAsync(
			actor,
			targetCharacterId,
			HL2RPFeatureOperation.SetPriority,
			state => OperationResult<HL2RPCharacterState>.Success( state with
			{
				CivicRecord = state.CivicRecord with
				{
					Priority = priority,
					RecordText = recordText.Trim()
				}
			} ),
			cancellationToken );
	}

	private async ValueTask<OperationResult<CivicMutationReceipt>> MutateAsync(
		InventoryActor actor,
		CharacterId targetCharacterId,
		HL2RPFeatureOperation operation,
		Func<HL2RPCharacterState, OperationResult<HL2RPCharacterState>> mutate,
		CancellationToken cancellationToken )
	{
		var actorValidation = ValidateActor( actor );
		if ( actorValidation.Failed )
			return OperationResult<CivicMutationReceipt>.Failure(
				actorValidation.Error!.Code, actorValidation.Error.Message );
		var document = _repositories.Characters.Find( DomainKeys.Character( targetCharacterId ) );
		if ( document is null )
			return OperationResult<CivicMutationReceipt>.Failure( ErrorCode.NotFound, "Target character was not found." );
		var policy = _policy.Evaluate( new HL2RPFeaturePolicyContext
		{
			Actor = actor,
			Operation = operation,
			TargetCharacterId = targetCharacterId
		} );
		if ( policy.Failed )
			return OperationResult<CivicMutationReceipt>.Failure( policy.Error!.Code, policy.Error.Message );
		var decoded = HL2RPFeaturePersistence.Decode(
			document.Value.SchemaState,
			HL2RPPersistence.CharacterState );
		if ( decoded.Failed )
			return OperationResult<CivicMutationReceipt>.Failure( decoded.Error!.Code, decoded.Error.Message );
		var changed = mutate( decoded.Value );
		if ( changed.Failed )
			return OperationResult<CivicMutationReceipt>.Failure( changed.Error!.Code, changed.Error.Message );
		var after = document.Value with
		{
			SchemaState = HL2RPPersistence.Payload( HL2RPPersistence.CharacterState, changed.Value )
		};
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		var editor = unitOfWork.Edit( _repositories.Characters, document );
		if ( editor is null )
		{
			await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
			return OperationResult<CivicMutationReceipt>.Failure( ErrorCode.Conflict, "Character changed." );
		}
		editor.Replace( after );
		unitOfWork.Save( editor );
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unitOfWork, cancellationToken );
		if ( !committed.Succeeded )
			return HL2RPFeaturePersistence.Failure<CivicMutationReceipt>( committed.Error! );
		var receipt = new CivicMutationReceipt( targetCharacterId, changed.Value.CivicRecord, committed.Value!.Sequence );
		_events.Publish( receipt );
		HL2RPFeaturePersistence.PublishAudit(
			_audit, actor, operation, targetCharacterId.ToString(), _clock.UtcNow, committed.Value.Sequence );
		return OperationResult<CivicMutationReceipt>.Success( receipt );
	}

	private OperationResult ValidateActor( InventoryActor actor )
	{
		var character = _repositories.Characters.Find( DomainKeys.Character( actor.CharacterId ) );
		return character is not null && character.Value.AccountId == actor.AccountId
			? OperationResult.Success()
			: OperationResult.Failure( ErrorCode.Unauthorized, "Active character does not belong to the actor." );
	}
}

public sealed class RecognitionService
{
	private readonly DomainRepositories _repositories;
	private readonly IHexClock _clock;
	private readonly ICharacterEncounterAuthorizer _encounters;
	private readonly PolicyPipeline<HL2RPFeaturePolicyContext> _policy;
	private readonly PostCommitEventBus<IntroductionReceipt> _events;
	private readonly PostCommitEventBus<AdminAuditFact> _audit;

	public RecognitionService(
		DomainRepositories repositories,
		IHexClock clock,
		ICharacterEncounterAuthorizer encounters,
		PolicyPipeline<HL2RPFeaturePolicyContext> policy,
		PostCommitEventBus<IntroductionReceipt>? events = null,
		PostCommitEventBus<AdminAuditFact>? audit = null )
	{
		_repositories = repositories;
		_clock = clock;
		_encounters = encounters ?? throw new ArgumentNullException( nameof(encounters) );
		_policy = policy;
		_events = events ?? new PostCommitEventBus<IntroductionReceipt>();
		_audit = audit ?? new PostCommitEventBus<AdminAuditFact>();
	}

	public async ValueTask<OperationResult<IntroductionReceipt>> IntroduceAsync(
		InventoryActor actor,
		CharacterId subjectCharacterId,
		CancellationToken cancellationToken = default )
	{
		var viewer = _repositories.Characters.Find( DomainKeys.Character( actor.CharacterId ) );
		var subject = _repositories.Characters.Find( DomainKeys.Character( subjectCharacterId ) );
		if ( viewer is null || viewer.Value.AccountId != actor.AccountId )
			return OperationResult<IntroductionReceipt>.Failure( ErrorCode.Unauthorized, "Actor binding is invalid." );
		if ( subject is null )
			return OperationResult<IntroductionReceipt>.Failure( ErrorCode.NotFound, "Introduced character was not found." );
		if ( subjectCharacterId == actor.CharacterId )
			return OperationResult<IntroductionReceipt>.Failure( ErrorCode.PolicyDenied, "A character cannot introduce themselves." );
		var encounter = _encounters.Authorize( actor, subjectCharacterId );
		if ( encounter.Failed )
			return OperationResult<IntroductionReceipt>.Failure( encounter.Error!.Code, encounter.Error.Message );
		var policy = _policy.Evaluate( new HL2RPFeaturePolicyContext
		{
			Actor = actor,
			Operation = HL2RPFeatureOperation.Introduce,
			TargetCharacterId = subjectCharacterId
		} );
		if ( policy.Failed )
			return OperationResult<IntroductionReceipt>.Failure( policy.Error!.Code, policy.Error.Message );

		var key = $"recognition-{actor.CharacterId}-{subjectCharacterId}";
		var state = new RecognitionReferenceState
		{
			IntroducedName = subject.Value.Name,
			IntroducedAtUtc = _clock.UtcNow
		};
		var reference = new CharacterReferenceRecord
		{
			Category = "recognition",
			CharacterId = actor.CharacterId,
			RelatedCharacterId = subjectCharacterId,
			State = HL2RPPersistence.Payload( HL2RPPersistence.Recognition, state )
		};
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		var existing = _repositories.CharacterReferences.Find( key );
		if ( existing is null )
		{
			unitOfWork.Create( _repositories.CharacterReferences, key, reference );
		}
		else
		{
			var editor = unitOfWork.Edit( _repositories.CharacterReferences, existing );
			if ( editor is null )
			{
				await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
				return OperationResult<IntroductionReceipt>.Failure( ErrorCode.Conflict, "Recognition changed." );
			}
			editor.Replace( reference );
			unitOfWork.Save( editor );
		}
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unitOfWork, cancellationToken );
		if ( !committed.Succeeded )
			return HL2RPFeaturePersistence.Failure<IntroductionReceipt>( committed.Error! );
		var receipt = new IntroductionReceipt(
			actor.CharacterId, subjectCharacterId, subject.Value.Name, committed.Value!.Sequence );
		_events.Publish( receipt );
		HL2RPFeaturePersistence.PublishAudit(
			_audit,
			actor,
			HL2RPFeatureOperation.Introduce,
			subjectCharacterId.ToString(),
			_clock.UtcNow,
			committed.Value.Sequence );
		return OperationResult<IntroductionReceipt>.Success( receipt );
	}
}

public sealed class CityObjectiveService
{
	private readonly DomainRepositories _repositories;
	private readonly IHexClock _clock;
	private readonly PolicyPipeline<HL2RPFeaturePolicyContext> _policy;
	private readonly PostCommitEventBus<CityObjectivesReceipt> _events;
	private readonly PostCommitEventBus<AdminAuditFact> _audit;

	public CityObjectiveService(
		DomainRepositories repositories,
		IHexClock clock,
		PolicyPipeline<HL2RPFeaturePolicyContext> policy,
		PostCommitEventBus<CityObjectivesReceipt>? events = null,
		PostCommitEventBus<AdminAuditFact>? audit = null )
	{
		_repositories = repositories;
		_clock = clock;
		_policy = policy;
		_events = events ?? new PostCommitEventBus<CityObjectivesReceipt>();
		_audit = audit ?? new PostCommitEventBus<AdminAuditFact>();
	}

	public async ValueTask<OperationResult<CityObjectivesReceipt>> ReplaceAsync(
		InventoryActor actor,
		SceneEntityId cityEntityId,
		IReadOnlyList<CityObjectiveState> objectives,
		CancellationToken cancellationToken = default )
	{
		ArgumentNullException.ThrowIfNull( objectives );
		if ( objectives.Count > 32 || objectives.Any( objective =>
			string.IsNullOrWhiteSpace( objective.Id ) || string.IsNullOrWhiteSpace( objective.Text ) ||
			objective.Text.Length > 512 || objective.Text.Any( char.IsControl ) || objective.UpdatedAtUtc == default ) ||
			objectives.Select( objective => objective.Id ).Distinct( StringComparer.Ordinal ).Count() != objectives.Count )
			return OperationResult<CityObjectivesReceipt>.Failure(
				ErrorCode.InvalidArgument, "City objectives are duplicated, empty or exceed limits." );
		try
		{
			foreach ( var objective in objectives ) StableIdentifier.Require( objective.Id, nameof(objectives) );
		}
		catch ( ArgumentException )
		{
			return OperationResult<CityObjectivesReceipt>.Failure(
				ErrorCode.InvalidArgument, "City objective ID is invalid." );
		}
		var actorCharacter = _repositories.Characters.Find( DomainKeys.Character( actor.CharacterId ) );
		if ( actorCharacter is null || actorCharacter.Value.AccountId != actor.AccountId )
			return OperationResult<CityObjectivesReceipt>.Failure( ErrorCode.Unauthorized, "Actor binding is invalid." );
		var document = _repositories.SceneEntities.Find( DomainKeys.SceneEntity( cityEntityId ) );
		if ( document is null || document.Value.Kind != "city" )
			return OperationResult<CityObjectivesReceipt>.Failure( ErrorCode.NotFound, "City state was not found." );
		var decoded = HL2RPFeaturePersistence.Decode( document.Value.State, HL2RPPersistence.CityState );
		if ( decoded.Failed )
			return OperationResult<CityObjectivesReceipt>.Failure( decoded.Error!.Code, decoded.Error.Message );
		var policy = _policy.Evaluate( new HL2RPFeaturePolicyContext
		{
			Actor = actor,
			Operation = HL2RPFeatureOperation.SetCityObjectives,
			SceneEntityId = cityEntityId
		} );
		if ( policy.Failed )
			return OperationResult<CityObjectivesReceipt>.Failure( policy.Error!.Code, policy.Error.Message );
		var after = document.Value with
		{
			State = HL2RPPersistence.Payload(
				HL2RPPersistence.CityState,
				new CityEntityState { Objectives = objectives.ToArray() } )
		};
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		var editor = unitOfWork.Edit( _repositories.SceneEntities, document );
		if ( editor is null )
		{
			await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
			return OperationResult<CityObjectivesReceipt>.Failure( ErrorCode.Conflict, "City state changed." );
		}
		editor.Replace( after );
		unitOfWork.Save( editor );
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unitOfWork, cancellationToken );
		if ( !committed.Succeeded )
			return HL2RPFeaturePersistence.Failure<CityObjectivesReceipt>( committed.Error! );
		var receipt = new CityObjectivesReceipt( cityEntityId, objectives.ToArray(), committed.Value!.Sequence );
		_events.Publish( receipt );
		HL2RPFeaturePersistence.PublishAudit(
			_audit,
			actor,
			HL2RPFeatureOperation.SetCityObjectives,
			cityEntityId.ToString(),
			_clock.UtcNow,
			committed.Value.Sequence );
		return OperationResult<CityObjectivesReceipt>.Success( receipt );
	}
}
