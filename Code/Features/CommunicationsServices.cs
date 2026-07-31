#nullable enable

using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Persistence;

namespace HL2RP.V2.Features;

public sealed record RadioTunedReceipt(
	ItemId RadioItemId,
	string Frequency,
	long CommitSequence,
	CommitReceipt Commit ) : IHL2RPCommittedOperation;

public sealed class RadioTuningService
{
	private readonly DomainRepositories _repositories;
	private readonly InventoryAccessService _access;
	private readonly IHexClock _clock;
	private readonly PolicyPipeline<HL2RPFeaturePolicyContext> _policy;
	private readonly PostCommitEventBus<RadioTunedReceipt> _events;
	private readonly PostCommitEventBus<AdminAuditFact> _audit;

	public RadioTuningService(
		DomainRepositories repositories,
		InventoryAccessService access,
		IHexClock clock,
		PolicyPipeline<HL2RPFeaturePolicyContext> policy,
		PostCommitEventBus<RadioTunedReceipt>? events = null,
		PostCommitEventBus<AdminAuditFact>? audit = null )
	{
		_repositories = repositories;
		_access = access;
		_clock = clock;
		_policy = policy;
		_events = events ?? new PostCommitEventBus<RadioTunedReceipt>();
		_audit = audit ?? new PostCommitEventBus<AdminAuditFact>();
	}

	public ValueTask<OperationResult<RadioTunedReceipt>> TuneAsync(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId radioItemId,
		string frequency,
		CancellationToken cancellationToken = default ) =>
		ConfigureAsync( actor, inventoryId, radioItemId, frequency, null, cancellationToken );

	public async ValueTask<OperationResult<RadioTunedReceipt>> ConfigureAsync(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId radioItemId,
		string frequency,
		bool? powered,
		CancellationToken cancellationToken = default )
	{
		var normalized = NormalizeFrequency( frequency );
		if ( normalized.Failed )
			return OperationResult<RadioTunedReceipt>.Failure( normalized.Error!.Code, normalized.Error.Message );
		var character = _repositories.Characters.Find( DomainKeys.Character( actor.CharacterId ) );
		var inventory = _repositories.Inventories.Find( DomainKeys.Inventory( inventoryId ) );
		var item = _repositories.Items.Find( DomainKeys.Item( radioItemId ) );
		if ( character is null || inventory is null || item is null )
			return OperationResult<RadioTunedReceipt>.Failure( ErrorCode.NotFound, "Character, inventory or radio was not found." );
		if ( character.Value.AccountId != actor.AccountId || inventory.Value.Find( radioItemId ) is null ||
			item.Value.Definition.Value != HL2RPIds.Items.Radio )
			return OperationResult<RadioTunedReceipt>.Failure( ErrorCode.Unauthorized, "Radio ownership proof failed." );
		var access = _access.Prove(
			actor.ConnectionId,
			actor.CharacterId,
			inventoryId,
			InventoryCapability.View | InventoryCapability.Use );
		if ( access is null )
			return OperationResult<RadioTunedReceipt>.Failure( ErrorCode.Unauthorized, "Radio use capability is missing." );
		if ( !item.Value.Traits.TryGetValue( "radio", out var payload ) )
			return OperationResult<RadioTunedReceipt>.Failure( ErrorCode.PersistedTypeInvalid, "Radio state is missing." );
		var decoded = HL2RPFeaturePersistence.Decode( payload, HL2RPPersistence.Radio );
		if ( decoded.Failed )
			return OperationResult<RadioTunedReceipt>.Failure( decoded.Error!.Code, decoded.Error.Message );
		var policy = _policy.Evaluate( new HL2RPFeaturePolicyContext
		{
			Actor = actor,
			Operation = HL2RPFeatureOperation.TuneRadio,
			ItemId = radioItemId
		} );
		if ( policy.Failed )
			return OperationResult<RadioTunedReceipt>.Failure( policy.Error!.Code, policy.Error.Message );
		var traits = new Dictionary<string, TypedPayload>( item.Value.Traits, StringComparer.Ordinal )
		{
			["radio"] = HL2RPPersistence.Payload(
				HL2RPPersistence.Radio,
				decoded.Value with
				{
					Frequency = normalized.Value,
					Powered = powered ?? decoded.Value.Powered
				} )
		};
		var after = item.Value with { Traits = traits };
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require( access );
		HL2RPUnitOfWork.RequireActorState( unitOfWork, _repositories, character );
		unitOfWork.RequireUnchanged( _repositories.Inventories, inventory );
		var editor = unitOfWork.Edit( _repositories.Items, item );
		if ( editor is null )
		{
			await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
			return OperationResult<RadioTunedReceipt>.Failure( ErrorCode.Conflict, "Radio changed." );
		}
		editor.Replace( after );
		unitOfWork.Save( editor );
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unitOfWork, cancellationToken );
		if ( !committed.Succeeded )
			return HL2RPFeaturePersistence.Failure<RadioTunedReceipt>( committed.Error! );
		var receipt = new RadioTunedReceipt(
			radioItemId, normalized.Value, committed.Value!.Sequence, committed.Value );
		_events.Publish( receipt );
		HL2RPFeaturePersistence.PublishAudit(
			_audit,
			actor,
			HL2RPFeatureOperation.TuneRadio,
			radioItemId.ToString(),
			_clock.UtcNow,
			committed.Value.Sequence );
		return OperationResult<RadioTunedReceipt>.Success( receipt );
	}

	public static OperationResult<string> NormalizeFrequency( string value )
	{
		if ( !decimal.TryParse(
			value?.Trim(),
			NumberStyles.AllowDecimalPoint,
			CultureInfo.InvariantCulture,
			out var frequency ) || frequency is < 100.0m or > 999.9m ||
			decimal.Round( frequency, 1 ) != frequency )
			return OperationResult<string>.Failure(
				ErrorCode.InvalidArgument, "Radio frequency must be between 100.0 and 999.9 with one decimal place." );
		return OperationResult<string>.Success( frequency.ToString( "F1", CultureInfo.InvariantCulture ) );
	}
}
