#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Persistence;

namespace HL2RP.V2.Features;

public sealed record PermitPurchasedReceipt(
	ItemId PermitItemId,
	CharacterId OwnerCharacterId,
	InventoryId DestinationInventoryId,
	BusinessPermitKind Kind,
	long Price,
	long RemainingBalance,
	long CommitSequence,
	CommitReceipt Commit) : IHL2RPCommittedOperation;

/// <summary>
/// Character self-service purchase boundary. Price and ownership are server
/// inputs; character debit, typed permit creation, and placement commit once.
/// Administrative permit issuance remains a separate DocumentService operation.
/// </summary>
public sealed class PermitPurchaseService
{
	public const long DefaultPrice = 250;

	private readonly DomainRepositories _repositories;
	private readonly InventoryAccessService _access;
	private readonly IAggregateIdGenerator _ids;
	private readonly InventoryLayoutService _layout;
	private readonly IHexClock _clock;
	private readonly PolicyPipeline<HL2RPFeaturePolicyContext> _policy;
	private readonly long _price;
	private readonly PostCommitEventBus<PermitPurchasedReceipt> _events;
	private readonly PostCommitEventBus<AdminAuditFact> _audit;

	public PermitPurchaseService(
		DomainRepositories repositories,
		InventoryAccessService access,
		IAggregateIdGenerator ids,
		InventoryLayoutService layout,
		IHexClock clock,
		PolicyPipeline<HL2RPFeaturePolicyContext> policy,
		long price = DefaultPrice,
		PostCommitEventBus<PermitPurchasedReceipt>? events = null,
		PostCommitEventBus<AdminAuditFact>? audit = null)
	{
		_repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
		_access = access ?? throw new ArgumentNullException(nameof(access));
		_ids = ids ?? throw new ArgumentNullException(nameof(ids));
		_layout = layout ?? throw new ArgumentNullException(nameof(layout));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
		_policy = policy ?? throw new ArgumentNullException(nameof(policy));
		if (price < 0) throw new ArgumentOutOfRangeException(nameof(price), "Permit price cannot be negative.");
		_price = price;
		_events = events ?? new PostCommitEventBus<PermitPurchasedReceipt>();
		_audit = audit ?? new PostCommitEventBus<AdminAuditFact>();
	}

	public long Price => _price;

	public async ValueTask<OperationResult<PermitPurchasedReceipt>> PurchaseAsync(
		InventoryActor actor,
		InventoryId mainInventoryId,
		BusinessPermitKind kind = BusinessPermitKind.General,
		CancellationToken cancellationToken = default)
	{
		if (!Enum.IsDefined(kind))
			return OperationResult<PermitPurchasedReceipt>.Failure(
				ErrorCode.InvalidArgument, "Permit kind is invalid.");

		var character = _repositories.Characters.Find(DomainKeys.Character(actor.CharacterId));
		if (character is null || character.Value.AccountId != actor.AccountId)
			return OperationResult<PermitPurchasedReceipt>.Failure(
				ErrorCode.Unauthorized, "Permit purchaser binding is invalid.");

		var ownerIndex = _repositories.OwnerInventories.Find( DomainKeys.OwnerInventory(
			InventoryOwner.Character( actor.CharacterId ), "main" ) );
		var destination = ownerIndex is null ? null : _repositories.Inventories.Find(
			DomainKeys.Inventory( ownerIndex.Value.InventoryId ) );
		if (destination is null || destination.Value.Id != mainInventoryId ||
			destination.Value.Owner != InventoryOwner.Character( actor.CharacterId ))
			return OperationResult<PermitPurchasedReceipt>.Failure(
				ErrorCode.Unauthorized, "Destination is not the purchaser's active main inventory.");

		var requiredAccess = InventoryCapability.View | InventoryCapability.TransferIn;
		var access = _access.Prove(actor.ConnectionId, actor.CharacterId, mainInventoryId, requiredAccess);
		if (access is null)
			return OperationResult<PermitPurchasedReceipt>.Failure(
				ErrorCode.Unauthorized, "Main inventory view and transfer capability are required.");

		var permitInspection = PermitInspector.Inspect(
			_repositories, actor.CharacterId, kind, _clock.UtcNow);
		if (permitInspection.HasValidPermit)
			return OperationResult<PermitPurchasedReceipt>.Failure(
				ErrorCode.Conflict, "An equivalent active permit is already owned.");
		if (character.Value.Balance < _price)
			return OperationResult<PermitPurchasedReceipt>.Failure(
				ErrorCode.Conflict, "Insufficient funds for the permit purchase.");

		var policy = _policy.Evaluate(new HL2RPFeaturePolicyContext
		{
			Actor = actor,
			Operation = HL2RPFeatureOperation.PurchasePermit,
			TargetCharacterId = actor.CharacterId
		});
		if (policy.Failed)
			return OperationResult<PermitPurchasedReceipt>.Failure(
				policy.Error!.Code, policy.Error.Message);

		long remainingBalance;
		try
		{
			remainingBalance = checked(character.Value.Balance - _price);
		}
		catch (OverflowException)
		{
			return OperationResult<PermitPurchasedReceipt>.Failure(
				ErrorCode.Conflict, "Permit balance calculation overflowed.");
		}

		var permit = new ItemRecord
		{
			Id = _ids.NewItemId(),
			Definition = new DefinitionId(HL2RPIds.Items.BusinessPermit),
			Traits = new Dictionary<string, TypedPayload>(StringComparer.Ordinal)
			{
				["permit"] = HL2RPPersistence.Payload(
					HL2RPPersistence.BusinessPermit,
					new BusinessPermitItemState
					{
						Kind = kind,
						OwnerCharacterId = actor.CharacterId,
						IssuedAtUtc = _clock.UtcNow,
						ExpiresAtUtc = null,
						Revoked = false
					})
			}
		};
		var layoutDependencies = HL2RPUnitOfWork.CaptureInventoryLayout(
			_repositories, destination.Value);
		var firstFit = _layout.FindFirstFit(destination.Value, permit);
		if (firstFit.Failed)
			return OperationResult<PermitPurchasedReceipt>.Failure(
				firstFit.Error!.Code, firstFit.Error.Message);
		var placed = _layout.AddAt(destination.Value, permit, firstFit.Value.X, firstFit.Value.Y);
		if (placed.Failed)
			return OperationResult<PermitPurchasedReceipt>.Failure(
				placed.Error!.Code, placed.Error.Message);

		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require(access);
		HL2RPUnitOfWork.RequireActorState(unitOfWork, _repositories, character);
		permitInspection.RequireUnchanged(unitOfWork, _repositories);
		HL2RPUnitOfWork.RequireInventoryLayout(unitOfWork, _repositories, layoutDependencies);
		var characterEditor = unitOfWork.Edit(_repositories.Characters, character);
		var inventoryEditor = unitOfWork.Edit(_repositories.Inventories, destination);
		if (characterEditor is null || inventoryEditor is null)
		{
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return OperationResult<PermitPurchasedReceipt>.Failure(
				ErrorCode.Conflict, "Purchaser or main inventory changed.");
		}
		characterEditor.Replace(character.Value.WithBalance(remainingBalance));
		inventoryEditor.Replace(placed.Value);
		unitOfWork.Save(characterEditor);
		unitOfWork.Save(inventoryEditor);
		unitOfWork.Create(_repositories.Items, DomainKeys.Item(permit.Id), permit);

		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		if (!committed.Succeeded)
			return HL2RPFeaturePersistence.Failure<PermitPurchasedReceipt>(committed.Error!);

		var receipt = new PermitPurchasedReceipt(
			permit.Id,
			actor.CharacterId,
			mainInventoryId,
			kind,
			_price,
			remainingBalance,
			committed.Value!.Sequence,
			committed.Value);
		_events.Publish(receipt);
		HL2RPFeaturePersistence.PublishAudit(
			_audit,
			actor,
			HL2RPFeatureOperation.PurchasePermit,
			permit.Id.ToString(),
			_clock.UtcNow,
			committed.Value.Sequence);
		return OperationResult<PermitPurchasedReceipt>.Success(receipt);
	}
}
