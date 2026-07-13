#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Persistence;

namespace HL2RP.V2.Features;

public sealed record TokenStackReceipt(
	HL2RPFeatureOperation Operation,
	ItemId PrimaryItemId,
	ItemId? SecondaryItemId,
	long PrimaryAmount,
	long CommitSequence );

public sealed class TokenStackService
{
	private readonly DomainRepositories _repositories;
	private readonly InventoryAccessService _access;
	private readonly IAggregateIdGenerator _ids;
	private readonly InventoryLayoutService _layout;
	private readonly IHexClock _clock;
	private readonly PolicyPipeline<HL2RPFeaturePolicyContext> _policy;
	private readonly PostCommitEventBus<TokenStackReceipt> _events;
	private readonly PostCommitEventBus<AdminAuditFact> _audit;

	public TokenStackService(
		DomainRepositories repositories,
		InventoryAccessService access,
		IAggregateIdGenerator ids,
		InventoryLayoutService layout,
		IHexClock clock,
		PolicyPipeline<HL2RPFeaturePolicyContext> policy,
		PostCommitEventBus<TokenStackReceipt>? events = null,
		PostCommitEventBus<AdminAuditFact>? audit = null )
	{
		_repositories = repositories;
		_access = access;
		_ids = ids;
		_layout = layout;
		_clock = clock;
		_policy = policy;
		_events = events ?? new PostCommitEventBus<TokenStackReceipt>();
		_audit = audit ?? new PostCommitEventBus<AdminAuditFact>();
	}

	public async ValueTask<OperationResult<TokenStackReceipt>> SplitAsync(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId stackItemId,
		long amount,
		CancellationToken cancellationToken = default )
	{
		if ( amount <= 0 )
			return OperationResult<TokenStackReceipt>.Failure( ErrorCode.InvalidArgument, "Split amount must be positive." );
		var proof = RequireStack( actor, inventoryId, stackItemId );
		if ( proof.Failed )
			return OperationResult<TokenStackReceipt>.Failure( proof.Error!.Code, proof.Error.Message );
		if ( amount >= proof.Value.State.Amount )
			return OperationResult<TokenStackReceipt>.Failure(
				ErrorCode.InvalidArgument, "Split amount must leave tokens in the original stack." );
		var policy = EvaluatePolicy( actor, HL2RPFeatureOperation.SplitTokens, stackItemId );
		if ( policy.Failed )
			return OperationResult<TokenStackReceipt>.Failure( policy.Error!.Code, policy.Error.Message );
		var primary = ReplaceAmount( proof.Value.Item.Value, proof.Value.State.Amount - amount );
		var secondary = new ItemRecord
		{
			Id = _ids.NewItemId(),
			Definition = new DefinitionId( HL2RPIds.Items.TokenStack ),
			Traits = new Dictionary<string, TypedPayload>( StringComparer.Ordinal )
			{
				["tokens"] = HL2RPPersistence.Payload(
					HL2RPPersistence.TokenStack,
					new TokenStackItemState { Amount = amount } )
			}
		};
		var staged = new Dictionary<ItemId, DefinitionId> { [secondary.Id] = secondary.Definition };
		var fit = _layout.FindFirstFit( proof.Value.Inventory.Value, secondary, staged );
		if ( fit.Failed )
			return OperationResult<TokenStackReceipt>.Failure( fit.Error!.Code, fit.Error.Message );
		var inventoryAfter = _layout.AddAt(
			proof.Value.Inventory.Value,
			secondary,
			fit.Value.X,
			fit.Value.Y,
			staged );
		if ( inventoryAfter.Failed )
			return OperationResult<TokenStackReceipt>.Failure(
				inventoryAfter.Error!.Code, inventoryAfter.Error.Message );
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		var primaryEditor = unitOfWork.Edit( _repositories.Items, proof.Value.Item );
		var inventoryEditor = unitOfWork.Edit( _repositories.Inventories, proof.Value.Inventory );
		if ( primaryEditor is null || inventoryEditor is null )
		{
			await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
			return OperationResult<TokenStackReceipt>.Failure( ErrorCode.Conflict, "Token stack or inventory changed." );
		}
		primaryEditor.Replace( primary );
		inventoryEditor.Replace( inventoryAfter.Value );
		unitOfWork.Save( primaryEditor );
		unitOfWork.Save( inventoryEditor );
		unitOfWork.Create( _repositories.Items, DomainKeys.Item( secondary.Id ), secondary );
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unitOfWork, cancellationToken );
		if ( !committed.Succeeded )
			return HL2RPFeaturePersistence.Failure<TokenStackReceipt>( committed.Error! );
		var receipt = new TokenStackReceipt(
			HL2RPFeatureOperation.SplitTokens,
			stackItemId,
			secondary.Id,
			proof.Value.State.Amount - amount,
			committed.Value!.Sequence );
		Publish( actor, receipt );
		return OperationResult<TokenStackReceipt>.Success( receipt );
	}

	public async ValueTask<OperationResult<TokenStackReceipt>> CombineAsync(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId primaryItemId,
		ItemId secondaryItemId,
		CancellationToken cancellationToken = default )
	{
		if ( primaryItemId == secondaryItemId )
			return OperationResult<TokenStackReceipt>.Failure( ErrorCode.InvalidArgument, "Two distinct token stacks are required." );
		var primary = RequireStack( actor, inventoryId, primaryItemId );
		var secondary = RequireStack( actor, inventoryId, secondaryItemId );
		if ( primary.Failed )
			return OperationResult<TokenStackReceipt>.Failure( primary.Error!.Code, primary.Error.Message );
		if ( secondary.Failed )
			return OperationResult<TokenStackReceipt>.Failure( secondary.Error!.Code, secondary.Error.Message );
		long total;
		try
		{
			total = checked(primary.Value.State.Amount + secondary.Value.State.Amount);
		}
		catch ( OverflowException )
		{
			return OperationResult<TokenStackReceipt>.Failure( ErrorCode.Conflict, "Combined token amount would overflow." );
		}
		var policy = EvaluatePolicy( actor, HL2RPFeatureOperation.CombineTokens, primaryItemId );
		if ( policy.Failed )
			return OperationResult<TokenStackReceipt>.Failure( policy.Error!.Code, policy.Error.Message );
		var primaryAfter = ReplaceAmount( primary.Value.Item.Value, total );
		var inventoryAfter = primary.Value.Inventory.Value with
		{
			Placements = primary.Value.Inventory.Value.Placements
				.Where( placement => placement.ItemId != secondaryItemId )
				.ToArray()
		};
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		var primaryEditor = unitOfWork.Edit( _repositories.Items, primary.Value.Item );
		var inventoryEditor = unitOfWork.Edit( _repositories.Inventories, primary.Value.Inventory );
		if ( primaryEditor is null || inventoryEditor is null )
		{
			await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
			return OperationResult<TokenStackReceipt>.Failure( ErrorCode.Conflict, "Token stack or inventory changed." );
		}
		primaryEditor.Replace( primaryAfter );
		inventoryEditor.Replace( inventoryAfter );
		unitOfWork.Save( primaryEditor );
		unitOfWork.Save( inventoryEditor );
		unitOfWork.Delete( _repositories.Items, secondary.Value.Item );
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unitOfWork, cancellationToken );
		if ( !committed.Succeeded )
			return HL2RPFeaturePersistence.Failure<TokenStackReceipt>( committed.Error! );
		var receipt = new TokenStackReceipt(
			HL2RPFeatureOperation.CombineTokens,
			primaryItemId,
			secondaryItemId,
			total,
			committed.Value!.Sequence );
		Publish( actor, receipt );
		return OperationResult<TokenStackReceipt>.Success( receipt );
	}

	private OperationResult<StackProof> RequireStack(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId itemId )
	{
		var character = _repositories.Characters.Find( DomainKeys.Character( actor.CharacterId ) );
		var inventory = _repositories.Inventories.Find( DomainKeys.Inventory( inventoryId ) );
		var item = _repositories.Items.Find( DomainKeys.Item( itemId ) );
		if ( character is null || inventory is null || item is null )
			return OperationResult<StackProof>.Failure( ErrorCode.NotFound, "Character, inventory or token stack was not found." );
		if ( character.Value.AccountId != actor.AccountId || inventory.Value.Find( itemId ) is null ||
			item.Value.Definition.Value != HL2RPIds.Items.TokenStack )
			return OperationResult<StackProof>.Failure( ErrorCode.Unauthorized, "Token stack membership proof failed." );
		if ( !_access.Has(
			actor.ConnectionId,
			actor.CharacterId,
			inventoryId,
			InventoryCapability.View | InventoryCapability.Move | InventoryCapability.Use ) )
			return OperationResult<StackProof>.Failure( ErrorCode.Unauthorized, "Token stack capability is missing." );
		if ( !item.Value.Traits.TryGetValue( "tokens", out var payload ) )
			return OperationResult<StackProof>.Failure( ErrorCode.PersistedTypeInvalid, "Token stack state is missing." );
		var state = HL2RPFeaturePersistence.Decode( payload, HL2RPPersistence.TokenStack );
		if ( state.Failed || state.Value.Amount <= 0 )
			return OperationResult<StackProof>.Failure(
				state.Failed ? state.Error!.Code : ErrorCode.Conflict,
				state.Failed ? state.Error!.Message : "Token stack amount is invalid." );
		return OperationResult<StackProof>.Success( new StackProof( inventory, item, state.Value ) );
	}

	private OperationResult EvaluatePolicy(
		InventoryActor actor,
		HL2RPFeatureOperation operation,
		ItemId itemId ) => _policy.Evaluate( new HL2RPFeaturePolicyContext
	{
		Actor = actor,
		Operation = operation,
		ItemId = itemId
	} );

	private static ItemRecord ReplaceAmount( ItemRecord item, long amount )
	{
		var traits = new Dictionary<string, TypedPayload>( item.Traits, StringComparer.Ordinal )
		{
			["tokens"] = HL2RPPersistence.Payload(
				HL2RPPersistence.TokenStack,
				new TokenStackItemState { Amount = amount } )
		};
		return item with { Traits = traits };
	}

	private void Publish( InventoryActor actor, TokenStackReceipt receipt )
	{
		_events.Publish( receipt );
		HL2RPFeaturePersistence.PublishAudit(
			_audit,
			actor,
			receipt.Operation,
			receipt.PrimaryItemId.ToString(),
			_clock.UtcNow,
			receipt.CommitSequence );
	}

	private sealed record StackProof(
		DocumentSnapshot<InventoryRecord> Inventory,
		DocumentSnapshot<ItemRecord> Item,
		TokenStackItemState State );
}
