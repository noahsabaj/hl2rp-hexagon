#nullable enable

namespace HL2RP.V2.Features;

public sealed record BagSessionReceipt(
	InteractionSession Session,
	InventoryId BagInventoryId,
	InventoryCapability Capabilities );

/// <summary>
/// Opens nested inventory access from a proven bag placement. Grants are bound to
/// the server session and disappear whenever the session closes or the bag moves.
/// </summary>
public sealed class BagInteractionService
{
	private const InventoryCapability BagCapabilities =
		InventoryCapability.View |
		InventoryCapability.Move |
		InventoryCapability.TransferIn |
		InventoryCapability.TransferOut |
		InventoryCapability.Use |
		InventoryCapability.Drop;

	private readonly DomainRepositories _repositories;
	private readonly InteractionSessionService _sessions;
	private readonly InventoryAccessService _access;
	private readonly PolicyPipeline<HL2RPFeaturePolicyContext> _policy;
	private readonly Dictionary<InteractionSessionId, InventoryId> _sourceInventories = new();

	public BagInteractionService(
		DomainRepositories repositories,
		InteractionSessionService sessions,
		InventoryAccessService access,
		PolicyPipeline<HL2RPFeaturePolicyContext> policy )
	{
		_repositories = repositories ?? throw new ArgumentNullException( nameof(repositories) );
		_sessions = sessions ?? throw new ArgumentNullException( nameof(sessions) );
		_access = access ?? throw new ArgumentNullException( nameof(access) );
		_policy = policy ?? throw new ArgumentNullException( nameof(policy) );
		_sessions.SessionRevoked += OnSessionRevoked;
	}

	public OperationResult<BagSessionReceipt> Open(
		InventoryActor actor,
		InventoryId sourceInventoryId,
		ItemId bagItemId )
	{
		var resolved = ResolveBag( actor, sourceInventoryId, bagItemId );
		if ( resolved.Failed )
			return OperationResult<BagSessionReceipt>.Failure( resolved.Error!.Code, resolved.Error.Message );
		var policy = _policy.Evaluate( new HL2RPFeaturePolicyContext
		{
			Actor = actor,
			Operation = HL2RPFeatureOperation.OpenBag,
			ItemId = bagItemId
		} );
		if ( policy.Failed )
			return OperationResult<BagSessionReceipt>.Failure( policy.Error!.Code, policy.Error.Message );
		var session = _sessions.Open(
			InteractionSessionKind.NestedBag,
			actor.ConnectionId,
			actor.CharacterId,
			InteractionTarget.Item( bagItemId ) );
		_sourceInventories[session.Id] = sourceInventoryId;
		_access.Grant( new InventoryGrant
		{
			ConnectionId = actor.ConnectionId,
			CharacterId = actor.CharacterId,
			InventoryId = resolved.Value.Id,
			Capabilities = BagCapabilities,
			Kind = InventoryGrantKind.NestedBag,
			SessionId = session.Id
		} );
		return OperationResult<BagSessionReceipt>.Success( new BagSessionReceipt(
			session,
			resolved.Value.Id,
			BagCapabilities ) );
	}

	public OperationResult<BagSessionReceipt> Continue(
		InventoryActor actor,
		InteractionSessionId sessionId )
	{
		var active = _sessions.ActiveSessions.SingleOrDefault( session => session.Id == sessionId );
		if ( active is null || active.Kind != InteractionSessionKind.NestedBag ||
			active.ConnectionId != actor.ConnectionId || active.CharacterId != actor.CharacterId ||
			active.Target.Kind != InteractionTargetKind.Item ||
			!_sourceInventories.TryGetValue( sessionId, out var sourceInventoryId ) )
			return OperationResult<BagSessionReceipt>.Failure(
				ErrorCode.Unauthorized, "Nested bag session is stale or belongs to another actor." );
		var bagItemId = new ItemId( active.Target.Id );
		var resolved = ResolveBag( actor, sourceInventoryId, bagItemId );
		if ( resolved.Failed || !_sessions.TryTouch(
			sessionId,
			actor.ConnectionId,
			actor.CharacterId,
			active.Target,
			out var touched ) )
		{
			_sessions.Revoke( sessionId, "bag_moved_or_stale" );
			return OperationResult<BagSessionReceipt>.Failure(
				ErrorCode.Unauthorized, "Bag moved or nested session expired." );
		}
		return OperationResult<BagSessionReceipt>.Success( new BagSessionReceipt(
			touched,
			resolved.Value.Id,
			BagCapabilities ) );
	}

	public void ItemMoved( ItemId itemId ) =>
		_sessions.RevokeTarget( InteractionTarget.Item( itemId ), "bag_moved" );

	public void Close( InteractionSessionId sessionId ) =>
		_sessions.Revoke( sessionId, "closed" );

	private OperationResult<InventoryRecord> ResolveBag(
		InventoryActor actor,
		InventoryId sourceInventoryId,
		ItemId bagItemId )
	{
		var character = _repositories.Characters.Find( DomainKeys.Character( actor.CharacterId ) );
		var source = _repositories.Inventories.Find( DomainKeys.Inventory( sourceInventoryId ) );
		var item = _repositories.Items.Find( DomainKeys.Item( bagItemId ) );
		if ( character is null || source is null || item is null )
			return OperationResult<InventoryRecord>.Failure( ErrorCode.NotFound, "Character, source inventory or bag was not found." );
		if ( character.Value.AccountId != actor.AccountId || source.Value.Find( bagItemId ) is null ||
			item.Value.Definition.Value != HL2RPIds.Items.Suitcase )
			return OperationResult<InventoryRecord>.Failure( ErrorCode.Unauthorized, "Bag membership proof failed." );
		if ( !_access.Has(
			actor.ConnectionId,
			actor.CharacterId,
			sourceInventoryId,
			InventoryCapability.View | InventoryCapability.Use ) )
			return OperationResult<InventoryRecord>.Failure( ErrorCode.Unauthorized, "Bag use capability is missing." );
		var ownerIndex = _repositories.OwnerInventories.Find(
			DomainKeys.OwnerInventory( InventoryOwner.ParentItem( bagItemId ), "bag" ) );
		var child = ownerIndex is null ? null : _repositories.Inventories.Find(
			DomainKeys.Inventory( ownerIndex.Value.InventoryId ) )?.Value;
		return child is not null && child.Owner == InventoryOwner.ParentItem( bagItemId )
			? OperationResult<InventoryRecord>.Success( child )
			: OperationResult<InventoryRecord>.Failure(
				ErrorCode.NotFound, "Bag must own exactly one indexed nested inventory." );
	}

	private void OnSessionRevoked( InteractionSession session )
	{
		_sourceInventories.Remove( session.Id );
		_access.RevokeSession( session.Id );
	}
}
