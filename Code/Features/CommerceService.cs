#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Persistence;

namespace HL2RP.V2.Features;

public sealed record CommerceReceipt : IHL2RPCommittedOperation
{
	public required HL2RPFeatureOperation Operation { get; init; }
	public required SceneEntityId SceneEntityId { get; init; }
	public required CharacterId CharacterId { get; init; }
	public required IReadOnlyList<ItemId> ItemIds { get; init; }
	public required long CurrencyAmount { get; init; }
	public required long BalanceAfter { get; init; }
	public required long CommitSequence { get; init; }
	public required CommitReceipt Commit { get; init; }
}

public sealed class CommerceService
{
	private readonly DomainRepositories _repositories;
	private readonly CompiledSchema _schema;
	private readonly InventoryAccessService _access;
	private readonly ISceneSessionResolver _sessions;
	private readonly IAggregateIdGenerator _ids;
	private readonly InventoryLayoutService _layout;
	private readonly IHL2RPItemFactory _items;
	private readonly IHexClock _clock;
	private readonly PolicyPipeline<HL2RPFeaturePolicyContext> _policy;
	private readonly PostCommitEventBus<CommerceReceipt> _events;
	private readonly PostCommitEventBus<AdminAuditFact> _audit;

	public CommerceService(
		DomainRepositories repositories,
		CompiledSchema schema,
		InventoryAccessService access,
		ISceneSessionResolver sessions,
		IAggregateIdGenerator ids,
		InventoryLayoutService layout,
		IHL2RPItemFactory items,
		IHexClock clock,
		PolicyPipeline<HL2RPFeaturePolicyContext> policy,
		PostCommitEventBus<CommerceReceipt>? events = null,
		PostCommitEventBus<AdminAuditFact>? audit = null )
	{
		_repositories = repositories;
		_schema = schema;
		_access = access;
		_sessions = sessions;
		_ids = ids;
		_layout = layout;
		_items = items;
		_clock = clock;
		_policy = policy;
		_events = events ?? new PostCommitEventBus<CommerceReceipt>();
		_audit = audit ?? new PostCommitEventBus<AdminAuditFact>();
	}

	public async ValueTask<OperationResult<CommerceReceipt>> BuyAsync(
		InventoryActor actor,
		InteractionSessionId sessionId,
		InventoryId destinationInventoryId,
		DefinitionId definition,
		int quantity,
		CancellationToken cancellationToken = default )
	{
		if ( quantity is < 1 or > 64 )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.InvalidArgument, "Purchase quantity must be between 1 and 64." );
		var session = _sessions.Resolve( actor, sessionId, InteractionSessionKind.Vendor );
		if ( session.Failed )
			return OperationResult<CommerceReceipt>.Failure( session.Error!.Code, session.Error.Message );
		var loaded = LoadActorInventoryEntity( actor, destinationInventoryId, session.Value.SceneEntityId, "vendor" );
		if ( loaded.Failed )
			return OperationResult<CommerceReceipt>.Failure( loaded.Error!.Code, loaded.Error.Message );
		var access = _access.Prove(
				actor.ConnectionId,
				actor.CharacterId,
				destinationInventoryId,
				InventoryCapability.View | InventoryCapability.TransferIn );
		if ( loaded.Value.Inventory.Value.Owner != InventoryOwner.Character( actor.CharacterId ) ||
			access is null )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.Unauthorized, "Purchase destination capability is missing." );
		if ( !_schema.Items.Contains( definition.Value ) )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.UnknownDefinition, "Vendor item is not registered." );
		var vendor = HL2RPFeaturePersistence.Decode(
			loaded.Value.Entity.Value.State,
			HL2RPPersistence.VendorState );
		if ( vendor.Failed )
			return OperationResult<CommerceReceipt>.Failure( vendor.Error!.Code, vendor.Error.Message );
		var vendorValidation = ValidateVendor( vendor.Value );
		if ( vendorValidation.Failed )
			return OperationResult<CommerceReceipt>.Failure(
				vendorValidation.Error!.Code, vendorValidation.Error.Message );
		var permitInspection = PermitInspector.Inspect(
			_repositories,
			actor.CharacterId,
			vendor.Value.RequiredPermit,
			_clock.UtcNow );
		if ( !permitInspection.HasValidPermit )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.PolicyDenied, "Required business permit is missing or expired." );
		var stockIndex = FindStock( vendor.Value, definition );
		if ( stockIndex < 0 )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.PolicyDenied, "Vendor does not sell this item." );
		var stock = vendor.Value.Stock[stockIndex];
		if ( stock.Quantity < quantity )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.Conflict, "Vendor stock is exhausted." );
		if ( stock.UnitPrice < 0 )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.Conflict, "Vendor price is invalid." );
		long totalPrice;
		try
		{
			totalPrice = checked(stock.UnitPrice * quantity);
		}
		catch ( OverflowException )
		{
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.Conflict, "Purchase price would overflow." );
		}
		var debited = CurrencyService.Debit( loaded.Value.Character.Value, totalPrice );
		if ( debited.Failed )
			return OperationResult<CommerceReceipt>.Failure( debited.Error!.Code, debited.Error.Message );
		var policy = EvaluatePolicy(
			actor,
			HL2RPFeatureOperation.VendorBuy,
			session.Value.SceneEntityId );
		if ( policy.Failed )
			return OperationResult<CommerceReceipt>.Failure( policy.Error!.Code, policy.Error.Message );

		var layoutDependencies = HL2RPUnitOfWork.CaptureInventoryLayout(
			_repositories, loaded.Value.Inventory.Value );
		var finalInventory = loaded.Value.Inventory.Value;
		var createdItems = new List<ItemRecord>( quantity );
		var stagedDefinitions = new Dictionary<ItemId, DefinitionId>();
		for ( var index = 0; index < quantity; index++ )
		{
			var created = _items.Create( definition, _ids.NewItemId(), _clock.UtcNow );
			if ( created.Failed )
				return OperationResult<CommerceReceipt>.Failure( created.Error!.Code, created.Error.Message );
			stagedDefinitions[created.Value.Id] = created.Value.Definition;
			var fit = _layout.FindFirstFit( finalInventory, created.Value, stagedDefinitions );
			if ( fit.Failed )
				return OperationResult<CommerceReceipt>.Failure( fit.Error!.Code, fit.Error.Message );
			var placed = _layout.AddAt(
				finalInventory,
				created.Value,
				fit.Value.X,
				fit.Value.Y,
				stagedDefinitions );
			if ( placed.Failed )
				return OperationResult<CommerceReceipt>.Failure( placed.Error!.Code, placed.Error.Message );
			finalInventory = placed.Value;
			createdItems.Add( created.Value );
		}
		var stockAfter = vendor.Value.Stock.ToArray();
		stockAfter[stockIndex] = stock with { Quantity = stock.Quantity - quantity };
		var entityAfter = loaded.Value.Entity.Value with
		{
			State = HL2RPPersistence.Payload(
				HL2RPPersistence.VendorState,
				vendor.Value with { Stock = stockAfter } )
		};
		return await CommitPurchaseAsync(
			actor,
			loaded.Value,
			finalInventory,
			debited.Value,
			entityAfter,
			createdItems,
			HL2RPFeatureOperation.VendorBuy,
			totalPrice,
			access,
			session.Value.CommitProof,
			permitInspection,
			layoutDependencies,
			cancellationToken );
	}

	public async ValueTask<OperationResult<CommerceReceipt>> SellAsync(
		InventoryActor actor,
		InteractionSessionId sessionId,
		InventoryId sourceInventoryId,
		ItemId itemId,
		CancellationToken cancellationToken = default )
	{
		var session = _sessions.Resolve( actor, sessionId, InteractionSessionKind.Vendor );
		if ( session.Failed )
			return OperationResult<CommerceReceipt>.Failure( session.Error!.Code, session.Error.Message );
		var loaded = LoadActorInventoryEntity( actor, sourceInventoryId, session.Value.SceneEntityId, "vendor" );
		if ( loaded.Failed )
			return OperationResult<CommerceReceipt>.Failure( loaded.Error!.Code, loaded.Error.Message );
		var item = _repositories.Items.Find( DomainKeys.Item( itemId ) );
		if ( item is null || loaded.Value.Inventory.Value.Find( itemId ) is null )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.NotFound, "Item is not in the claimed inventory." );
		var access = _access.Prove(
				actor.ConnectionId,
				actor.CharacterId,
				sourceInventoryId,
				InventoryCapability.View | InventoryCapability.TransferOut | InventoryCapability.Sell );
		if ( loaded.Value.Inventory.Value.Owner != InventoryOwner.Character( actor.CharacterId ) ||
			access is null )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.Unauthorized, "Sell capability is missing." );
		if ( _repositories.OwnerInventories.Find( DomainKeys.OwnerInventory(
			InventoryOwner.ParentItem( itemId ), InventoryRoles.Bag ) ) is not null )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.PolicyDenied, "Container items cannot be sold with nested inventory." );
		var vendor = HL2RPFeaturePersistence.Decode(
			loaded.Value.Entity.Value.State,
			HL2RPPersistence.VendorState );
		if ( vendor.Failed )
			return OperationResult<CommerceReceipt>.Failure( vendor.Error!.Code, vendor.Error.Message );
		var vendorValidation = ValidateVendor( vendor.Value );
		if ( vendorValidation.Failed )
			return OperationResult<CommerceReceipt>.Failure(
				vendorValidation.Error!.Code, vendorValidation.Error.Message );
		var permitInspection = PermitInspector.Inspect(
			_repositories,
			actor.CharacterId,
			vendor.Value.RequiredPermit,
			_clock.UtcNow );
		if ( !permitInspection.HasValidPermit )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.PolicyDenied, "Required business permit is missing or expired." );
		var stockIndex = FindStock( vendor.Value, item.Value.Definition );
		if ( stockIndex < 0 )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.PolicyDenied, "Vendor does not buy this item." );
		var stock = vendor.Value.Stock[stockIndex];
		if ( stock.UnitPrice < 0 || stock.Quantity == int.MaxValue )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.Conflict, "Vendor stock or price would overflow." );
		var payout = stock.UnitPrice <= 1 ? stock.UnitPrice : stock.UnitPrice / 2;
		var credited = CurrencyService.Credit( loaded.Value.Character.Value, payout );
		if ( credited.Failed )
			return OperationResult<CommerceReceipt>.Failure( credited.Error!.Code, credited.Error.Message );
		var policy = EvaluatePolicy(
			actor,
			HL2RPFeatureOperation.VendorSell,
			session.Value.SceneEntityId,
			itemId );
		if ( policy.Failed )
			return OperationResult<CommerceReceipt>.Failure( policy.Error!.Code, policy.Error.Message );
		var stockAfter = vendor.Value.Stock.ToArray();
		stockAfter[stockIndex] = stock with { Quantity = stock.Quantity + 1 };
		var inventoryAfter = loaded.Value.Inventory.Value with
		{
			Placements = loaded.Value.Inventory.Value.Placements
				.Where( placement => placement.ItemId != itemId )
				.ToArray()
		};
		var entityAfter = loaded.Value.Entity.Value with
		{
			State = HL2RPPersistence.Payload(
				HL2RPPersistence.VendorState,
				vendor.Value with { Stock = stockAfter } )
		};
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require( session.Value.CommitProof );
		unitOfWork.Require( access );
		HL2RPUnitOfWork.RequireActorState( unitOfWork, _repositories, loaded.Value.Character );
		permitInspection.RequireUnchanged( unitOfWork, _repositories );
		var characterEditor = unitOfWork.Edit( _repositories.Characters, loaded.Value.Character );
		var inventoryEditor = unitOfWork.Edit( _repositories.Inventories, loaded.Value.Inventory );
		var entityEditor = unitOfWork.Edit( _repositories.SceneEntities, loaded.Value.Entity );
		if ( characterEditor is null || inventoryEditor is null || entityEditor is null )
		{
			await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.Conflict, "Commerce aggregate changed." );
		}
		characterEditor.Replace( credited.Value );
		inventoryEditor.Replace( inventoryAfter );
		entityEditor.Replace( entityAfter );
		unitOfWork.Save( characterEditor );
		unitOfWork.Save( inventoryEditor );
		unitOfWork.Save( entityEditor );
		unitOfWork.Delete( _repositories.Items, item );
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unitOfWork, cancellationToken );
		if ( !committed.Succeeded )
			return HL2RPFeaturePersistence.Failure<CommerceReceipt>( committed.Error! );
		var receipt = new CommerceReceipt
		{
			Operation = HL2RPFeatureOperation.VendorSell,
			SceneEntityId = session.Value.SceneEntityId,
			CharacterId = actor.CharacterId,
			ItemIds = new[] { itemId },
			CurrencyAmount = payout,
			BalanceAfter = credited.Value.Balance,
			CommitSequence = committed.Value!.Sequence,
			Commit = committed.Value
		};
		Publish( actor, receipt );
		return OperationResult<CommerceReceipt>.Success( receipt );
	}

	public async ValueTask<OperationResult<CommerceReceipt>> PurchaseFromMachineAsync(
		InventoryActor actor,
		InteractionSessionId sessionId,
		InventoryId destinationInventoryId,
		CancellationToken cancellationToken = default )
	{
		var session = _sessions.Resolve( actor, sessionId, InteractionSessionKind.Vendor );
		if ( session.Failed )
			return OperationResult<CommerceReceipt>.Failure( session.Error!.Code, session.Error.Message );
		var loaded = LoadActorInventoryEntity( actor, destinationInventoryId, session.Value.SceneEntityId, null );
		if ( loaded.Failed )
			return OperationResult<CommerceReceipt>.Failure( loaded.Error!.Code, loaded.Error.Message );
		if ( loaded.Value.Entity.Value.Kind is not "ration_dispenser" and not "vending_machine" )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.PolicyDenied, "Session target is not a supported machine." );
		var access = _access.Prove(
				actor.ConnectionId,
				actor.CharacterId,
				destinationInventoryId,
				InventoryCapability.View | InventoryCapability.TransferIn );
		if ( loaded.Value.Inventory.Value.Owner != InventoryOwner.Character( actor.CharacterId ) ||
			access is null )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.Unauthorized, "Machine destination capability is missing." );
		var machine = HL2RPFeaturePersistence.Decode(
			loaded.Value.Entity.Value.State,
			HL2RPPersistence.MachineState );
		if ( machine.Failed )
			return OperationResult<CommerceReceipt>.Failure( machine.Error!.Code, machine.Error.Message );
		if ( machine.Value.Stock <= 0 )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.Conflict, "Machine stock is exhausted." );
		if ( machine.Value.CooldownUntilUtc is not null && machine.Value.CooldownUntilUtc > _clock.UtcNow )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.Conflict, "Machine is cooling down." );
		if ( machine.Value.UnitPrice < 0 )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.Conflict, "Machine price is invalid." );
		if ( !double.IsFinite( machine.Value.CooldownSeconds ) || machine.Value.CooldownSeconds is < 0 or > 300 )
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.Conflict, "Machine cooldown is invalid." );
		var debited = CurrencyService.Debit( loaded.Value.Character.Value, machine.Value.UnitPrice );
		if ( debited.Failed )
			return OperationResult<CommerceReceipt>.Failure( debited.Error!.Code, debited.Error.Message );
		var policy = EvaluatePolicy(
			actor,
			HL2RPFeatureOperation.MachinePurchase,
			session.Value.SceneEntityId );
		if ( policy.Failed )
			return OperationResult<CommerceReceipt>.Failure( policy.Error!.Code, policy.Error.Message );
		var definition = new DefinitionId(
			loaded.Value.Entity.Value.Kind == "ration_dispenser"
				? HL2RPIds.Items.Ration
				: HL2RPIds.Items.Water );
		var item = _items.Create( definition, _ids.NewItemId(), _clock.UtcNow );
		if ( item.Failed )
			return OperationResult<CommerceReceipt>.Failure( item.Error!.Code, item.Error.Message );
		var layoutDependencies = HL2RPUnitOfWork.CaptureInventoryLayout(
			_repositories, loaded.Value.Inventory.Value );
		var fit = _layout.FindFirstFit( loaded.Value.Inventory.Value, item.Value );
		if ( fit.Failed )
			return OperationResult<CommerceReceipt>.Failure( fit.Error!.Code, fit.Error.Message );
		var placed = _layout.AddAt(
			loaded.Value.Inventory.Value,
			item.Value,
			fit.Value.X,
			fit.Value.Y );
		if ( placed.Failed )
			return OperationResult<CommerceReceipt>.Failure( placed.Error!.Code, placed.Error.Message );
		var cooldown = TimeSpan.FromSeconds( machine.Value.CooldownSeconds );
		var entityAfter = loaded.Value.Entity.Value with
		{
			State = HL2RPPersistence.Payload(
				HL2RPPersistence.MachineState,
				machine.Value with
				{
					Stock = machine.Value.Stock - 1,
					CooldownUntilUtc = _clock.UtcNow + cooldown
				} )
		};
		return await CommitPurchaseAsync(
			actor,
			loaded.Value,
			placed.Value,
			debited.Value,
			entityAfter,
			new[] { item.Value },
			HL2RPFeatureOperation.MachinePurchase,
			machine.Value.UnitPrice,
			access,
			session.Value.CommitProof,
			null,
			layoutDependencies,
			cancellationToken );
	}

	private async ValueTask<OperationResult<CommerceReceipt>> CommitPurchaseAsync(
		InventoryActor actor,
		LoadedCommerce loaded,
		InventoryRecord inventoryAfter,
		CharacterRecord characterAfter,
		PersistentSceneEntityRecord entityAfter,
		IReadOnlyList<ItemRecord> createdItems,
		HL2RPFeatureOperation operation,
		long price,
		InventoryAccessProof access,
		ICommitPrecondition sessionProof,
		PermitInspectionProof? permitInspection,
		IReadOnlyList<DocumentSnapshot<ItemRecord>> layoutDependencies,
		CancellationToken cancellationToken )
	{
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require( sessionProof );
		unitOfWork.Require( access );
		HL2RPUnitOfWork.RequireActorState( unitOfWork, _repositories, loaded.Character );
		permitInspection?.RequireUnchanged( unitOfWork, _repositories );
		HL2RPUnitOfWork.RequireInventoryLayout( unitOfWork, _repositories, layoutDependencies );
		var characterEditor = unitOfWork.Edit( _repositories.Characters, loaded.Character );
		var inventoryEditor = unitOfWork.Edit( _repositories.Inventories, loaded.Inventory );
		var entityEditor = unitOfWork.Edit( _repositories.SceneEntities, loaded.Entity );
		if ( characterEditor is null || inventoryEditor is null || entityEditor is null )
		{
			await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
			return OperationResult<CommerceReceipt>.Failure( ErrorCode.Conflict, "Commerce aggregate changed." );
		}
		characterEditor.Replace( characterAfter );
		inventoryEditor.Replace( inventoryAfter );
		entityEditor.Replace( entityAfter );
		unitOfWork.Save( characterEditor );
		unitOfWork.Save( inventoryEditor );
		unitOfWork.Save( entityEditor );
		foreach ( var item in createdItems )
			unitOfWork.Create( _repositories.Items, DomainKeys.Item( item.Id ), item );
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unitOfWork, cancellationToken );
		if ( !committed.Succeeded )
			return HL2RPFeaturePersistence.Failure<CommerceReceipt>( committed.Error! );
		var receipt = new CommerceReceipt
		{
			Operation = operation,
			SceneEntityId = loaded.Entity.Value.Id,
			CharacterId = actor.CharacterId,
			ItemIds = createdItems.Select( item => item.Id ).ToArray(),
			CurrencyAmount = price,
			BalanceAfter = characterAfter.Balance,
			CommitSequence = committed.Value!.Sequence,
			Commit = committed.Value
		};
		Publish( actor, receipt );
		return OperationResult<CommerceReceipt>.Success( receipt );
	}

	private void Publish( InventoryActor actor, CommerceReceipt receipt )
	{
		_events.Publish( receipt );
		HL2RPFeaturePersistence.PublishAudit(
			_audit,
			actor,
			receipt.Operation,
			receipt.SceneEntityId.ToString(),
			_clock.UtcNow,
			receipt.CommitSequence );
	}

	private OperationResult<LoadedCommerce> LoadActorInventoryEntity(
		InventoryActor actor,
		InventoryId inventoryId,
		SceneEntityId sceneEntityId,
		string? requiredKind )
	{
		var character = _repositories.Characters.Find( DomainKeys.Character( actor.CharacterId ) );
		var inventory = _repositories.Inventories.Find( DomainKeys.Inventory( inventoryId ) );
		var entity = _repositories.SceneEntities.Find( DomainKeys.SceneEntity( sceneEntityId ) );
		if ( character is null || inventory is null || entity is null )
			return OperationResult<LoadedCommerce>.Failure( ErrorCode.NotFound, "Commerce aggregate was not found." );
		if ( character.Value.AccountId != actor.AccountId )
			return OperationResult<LoadedCommerce>.Failure( ErrorCode.Unauthorized, "Actor binding is invalid." );
		if ( requiredKind is not null && entity.Value.Kind != requiredKind )
			return OperationResult<LoadedCommerce>.Failure( ErrorCode.PolicyDenied, "Session target has the wrong entity kind." );
		return OperationResult<LoadedCommerce>.Success( new LoadedCommerce( character, inventory, entity ) );
	}

	private OperationResult EvaluatePolicy(
		InventoryActor actor,
		HL2RPFeatureOperation operation,
		SceneEntityId sceneEntityId,
		ItemId? itemId = null ) => _policy.Evaluate( new HL2RPFeaturePolicyContext
	{
		Actor = actor,
		Operation = operation,
		SceneEntityId = sceneEntityId,
		ItemId = itemId
	} );

	private static int FindStock( VendorEntityState vendor, DefinitionId definition )
	{
		for ( var index = 0; index < vendor.Stock.Count; index++ )
			if ( vendor.Stock[index].Definition == definition ) return index;
		return -1;
	}

	private OperationResult ValidateVendor( VendorEntityState vendor )
	{
		if ( !Enum.IsDefined( vendor.RequiredPermit ) ||
			vendor.Stock.Select( entry => entry.Definition ).Distinct().Count() != vendor.Stock.Count ||
			vendor.Stock.Any( entry => entry.Quantity < 0 || entry.UnitPrice < 0 ||
				!_schema.Items.Contains( entry.Definition.Value ) ) )
			return OperationResult.Failure( ErrorCode.Conflict, "Vendor state is malformed." );
		return OperationResult.Success();
	}

	private sealed record LoadedCommerce(
		DocumentSnapshot<CharacterRecord> Character,
		DocumentSnapshot<InventoryRecord> Inventory,
		DocumentSnapshot<PersistentSceneEntityRecord> Entity );
}
