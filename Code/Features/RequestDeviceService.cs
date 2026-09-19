#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Persistence;

namespace HL2RP.V2.Features;

public sealed record RequestFact : IHL2RPCommittedOperation
{
	public required InventoryActor Actor { get; init; }
	public required CharacterRecord Character { get; init; }
	public required string ChannelId { get; init; }
	public required string Text { get; init; }
	public required DateTimeOffset RequestedAtUtc { get; init; }
	public required long CommitSequence { get; init; }
	public required CommitReceipt Commit { get; init; }
}

public interface ICommittedChatDeliverySink
{
	void Deliver( ChatDelivery delivery, CharacterRecord author );
}

/// <summary>
/// Converts only committed request-device facts into chat delivery. It captures
/// the canonical inventory exactly once and delegates role/frequency authority
/// to the same pure resolver used by ChatService.
/// </summary>
public sealed class RequestChatDeliveryHandler : IEventHandler<RequestFact>
{
	private readonly IChatRecipientResolver _recipients;
	private readonly ILiveInventoryView _inventory;
	private readonly ICommittedChatDeliverySink _sink;
	private readonly Func<Guid> _createMessageId;

	public RequestChatDeliveryHandler(
		IChatRecipientResolver recipients,
		ILiveInventoryView inventory,
		ICommittedChatDeliverySink sink,
		Func<Guid>? createMessageId = null )
	{
		_recipients = recipients ?? throw new ArgumentNullException( nameof(recipients) );
		_inventory = inventory ?? throw new ArgumentNullException( nameof(inventory) );
		_sink = sink ?? throw new ArgumentNullException( nameof(sink) );
		_createMessageId = createMessageId ?? Guid.NewGuid;
	}

	public void Handle( RequestFact fact )
	{
		ArgumentNullException.ThrowIfNull( fact );
		if ( fact.ChannelId != HL2RPIds.Channels.Request ||
			fact.Actor.CharacterId != fact.Character.Id ||
			fact.Actor.AccountId != fact.Character.AccountId )
			throw new InvalidOperationException( "Committed request fact has an invalid actor or channel binding." );
		var messageId = _createMessageId();
		if ( messageId == Guid.Empty )
			throw new InvalidOperationException( "Committed request message identity is invalid." );
		var context = new ChatSendContext(
			fact.Actor, fact.Character, fact.ChannelId, fact.Text );
		var inventory = _inventory.Capture();
		var recipients = _recipients.Resolve(
			context,
			new ChatChannelRule { Id = HL2RPIds.Channels.Request },
			inventory )
			.Distinct()
			.OrderBy( value => value.Value )
			.ToArray();
		// The author character travels alongside the delivery so the sink can resolve the displayed
		// name per recipient; the delivery itself deliberately carries no name.
		_sink.Deliver( new ChatDelivery(
			messageId,
			fact.ChannelId,
			fact.Character.Id,
			fact.Text,
			fact.RequestedAtUtc,
			recipients ), fact.Character );
	}
}

public sealed class RequestDeviceService
{
	public static readonly TimeSpan RequestCooldown = TimeSpan.FromSeconds( 10 );
	public static ChatRateLimit RequestChannelRateLimit { get; } =
		new( 1, RequestCooldown );

	private readonly DomainRepositories _repositories;
	private readonly InventoryAccessService _access;
	private readonly IHexClock _clock;
	private readonly PolicyPipeline<HL2RPFeaturePolicyContext> _policy;
	private readonly PostCommitEventBus<RequestFact> _events;
	private readonly PostCommitEventBus<AdminAuditFact> _audit;
	private readonly IChatAdmissionService _admission;
	private readonly ChatRateLimit _globalRateLimit;
	private readonly ChatRateLimit _requestRateLimit;

	public RequestDeviceService(
		DomainRepositories repositories,
		InventoryAccessService access,
		IHexClock clock,
		PolicyPipeline<HL2RPFeaturePolicyContext> policy,
		PostCommitEventBus<RequestFact>? events = null,
		PostCommitEventBus<AdminAuditFact>? audit = null,
		IChatAdmissionService? admission = null,
		ChatRateLimit? globalRateLimit = null,
		ChatRateLimit? requestRateLimit = null )
	{
		_repositories = repositories;
		_access = access;
		_clock = clock;
		_policy = policy;
		_events = events ?? new PostCommitEventBus<RequestFact>();
		_audit = audit ?? new PostCommitEventBus<AdminAuditFact>();
		_admission = admission ?? new ChatAdmissionService();
		_globalRateLimit = globalRateLimit ?? ChatRateLimit.Default;
		_requestRateLimit = requestRateLimit ?? RequestChannelRateLimit;
	}

	public void RevokeConnection( ConnectionId connectionId ) => _admission.Revoke( connectionId );

	public async ValueTask<OperationResult<RequestFact>> SendAsync(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId requestDeviceItemId,
		string text,
		CancellationToken cancellationToken = default )
	{
		var normalized = ChatService.Normalize( text );
		if ( normalized.Failed )
			return OperationResult<RequestFact>.Failure( normalized.Error!.Code, normalized.Error.Message );
		var proof = ItemProof.Require( _repositories, _access, actor, inventoryId, requestDeviceItemId,
			HL2RPIds.Items.RequestDevice, InventoryCapability.View | InventoryCapability.Use );
		if ( proof.Failed )
			return OperationResult<RequestFact>.Failure( proof.Error!.Code, proof.Error.Message );
		var (character, inventory, item, access) = proof.Value;
		if ( !item.Value.Traits.TryGetValue( "request_device", out var payload ) )
			return OperationResult<RequestFact>.Failure( ErrorCode.PersistedTypeInvalid, "Request device state is missing." );
		var state = HL2RPFeaturePersistence.Decode( payload, HL2RPPersistence.RequestDevice );
		if ( state.Failed )
			return OperationResult<RequestFact>.Failure( state.Error!.Code, state.Error.Message );
		if ( !state.Value.Powered )
			return OperationResult<RequestFact>.Failure( ErrorCode.PolicyDenied, "Request device is powered off." );
		if ( state.Value.LastRequestAtUtc is not null &&
			_clock.UtcNow - state.Value.LastRequestAtUtc.Value < RequestCooldown )
			return OperationResult<RequestFact>.Failure( ErrorCode.Conflict, "Request device is cooling down." );
		var policy = _policy.Evaluate( new HL2RPFeaturePolicyContext
		{
			Actor = actor,
			Operation = HL2RPFeatureOperation.SendRequest,
			ItemId = requestDeviceItemId
		} );
		if ( policy.Failed )
			return OperationResult<RequestFact>.Failure( policy.Error!.Code, policy.Error.Message );
		var reserved = _admission.Reserve(
			actor.ConnectionId,
			HL2RPIds.Channels.Request,
			_globalRateLimit,
			_requestRateLimit,
			_clock.UtcNow );
		if ( reserved.Failed )
			return OperationResult<RequestFact>.Failure( reserved.Error!.Code, reserved.Error.Message );
		using var admission = reserved.Value;
		// One instant for the persisted cooldown, the rate-limit commit and the published fact.
		var requestedAt = _clock.UtcNow;
		var traits = new Dictionary<string, TypedPayload>( item.Value.Traits, StringComparer.Ordinal )
		{
			["request_device"] = HL2RPPersistence.Payload(
				HL2RPPersistence.RequestDevice,
				state.Value with { LastRequestAtUtc = requestedAt } )
		};
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require( access );
		HL2RPUnitOfWork.RequireActorState( unitOfWork, _repositories, character );
		unitOfWork.RequireUnchanged( _repositories.Inventories, inventory );
		var editor = unitOfWork.Edit( _repositories.Items, item );
		if ( editor is null )
		{
			await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
			return OperationResult<RequestFact>.Failure( ErrorCode.Conflict, "Request device changed." );
		}
		editor.Replace( item.Value with { Traits = traits } );
		unitOfWork.Save( editor );
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unitOfWork, cancellationToken );
		if ( !committed.Succeeded )
			return HL2RPFeaturePersistence.Failure<RequestFact>( committed.Error! );
		admission.Commit( requestedAt );
		var fact = new RequestFact
		{
			Actor = actor,
			Character = character.Value.DeepCopy(),
			ChannelId = HL2RPIds.Channels.Request,
			Text = normalized.Value,
			RequestedAtUtc = requestedAt,
			CommitSequence = committed.Value!.Sequence,
			Commit = committed.Value
		};
		_events.Publish( fact );
		HL2RPFeaturePersistence.PublishAudit(
			_audit,
			actor,
			HL2RPFeatureOperation.SendRequest,
			requestDeviceItemId.ToString(),
			_clock.UtcNow,
			committed.Value.Sequence );
		return OperationResult<RequestFact>.Success( fact );
	}
}
