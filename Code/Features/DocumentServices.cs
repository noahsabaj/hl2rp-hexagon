#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Persistence;

namespace HL2RP.V2.Features;

public sealed record NoteEditReceipt(
	ItemId NoteItemId, string Text, long CommitSequence, CommitReceipt Commit ) : IHL2RPCommittedOperation;

public sealed record PermitIssuedReceipt(
	ItemId PermitItemId,
	CharacterId OwnerCharacterId,
	BusinessPermitKind Kind,
	long CommitSequence,
	CommitReceipt Commit ) : IHL2RPCommittedOperation;

public sealed class DocumentService
{
	public const int MaximumNoteUnicodeScalars = 4_096;

	private readonly DomainRepositories _repositories;
	private readonly InventoryAccessService _access;
	private readonly IAggregateIdGenerator _ids;
	private readonly InventoryLayoutService _layout;
	private readonly IHexClock _clock;
	private readonly PolicyPipeline<HL2RPFeaturePolicyContext> _policy;
	private readonly PostCommitEventBus<NoteEditReceipt> _noteEvents;
	private readonly PostCommitEventBus<PermitIssuedReceipt> _permitEvents;
	private readonly PostCommitEventBus<AdminAuditFact> _audit;

	public DocumentService(
		DomainRepositories repositories,
		InventoryAccessService access,
		IAggregateIdGenerator ids,
		InventoryLayoutService layout,
		IHexClock clock,
		PolicyPipeline<HL2RPFeaturePolicyContext> policy,
		PostCommitEventBus<NoteEditReceipt>? noteEvents = null,
		PostCommitEventBus<PermitIssuedReceipt>? permitEvents = null,
		PostCommitEventBus<AdminAuditFact>? audit = null )
	{
		_repositories = repositories;
		_access = access;
		_ids = ids;
		_layout = layout;
		_clock = clock;
		_policy = policy;
		_noteEvents = noteEvents ?? new PostCommitEventBus<NoteEditReceipt>();
		_permitEvents = permitEvents ?? new PostCommitEventBus<PermitIssuedReceipt>();
		_audit = audit ?? new PostCommitEventBus<AdminAuditFact>();
	}

	public OperationResult<NoteItemState> ReadNote(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId noteItemId )
	{
		var proof = RequireItem( actor, inventoryId, noteItemId, HL2RPIds.Items.Note, InventoryCapability.View );
		if ( proof.Failed )
			return OperationResult<NoteItemState>.Failure( proof.Error!.Code, proof.Error.Message );
		if ( !proof.Value.Item.Value.Traits.TryGetValue( "note", out var payload ) )
			return OperationResult<NoteItemState>.Failure( ErrorCode.PersistedTypeInvalid, "Note state is missing." );
		return HL2RPFeaturePersistence.Decode( payload, HL2RPPersistence.Note );
	}

	public async ValueTask<OperationResult<NoteEditReceipt>> EditNoteAsync(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId noteItemId,
		string text,
		CancellationToken cancellationToken = default )
	{
		var normalized = NormalizeNote( text );
		if ( normalized.Failed )
			return OperationResult<NoteEditReceipt>.Failure( normalized.Error!.Code, normalized.Error.Message );
		var proof = RequireItem(
			actor,
			inventoryId,
			noteItemId,
			HL2RPIds.Items.Note,
			InventoryCapability.View | InventoryCapability.Use );
		if ( proof.Failed )
			return OperationResult<NoteEditReceipt>.Failure( proof.Error!.Code, proof.Error.Message );
		if ( !proof.Value.Item.Value.Traits.TryGetValue( "note", out var payload ) )
			return OperationResult<NoteEditReceipt>.Failure( ErrorCode.PersistedTypeInvalid, "Note state is missing." );
		var state = HL2RPFeaturePersistence.Decode( payload, HL2RPPersistence.Note );
		if ( state.Failed )
			return OperationResult<NoteEditReceipt>.Failure( state.Error!.Code, state.Error.Message );
		if ( state.Value.OwnerCharacterId is not null && state.Value.OwnerCharacterId != actor.CharacterId )
			return OperationResult<NoteEditReceipt>.Failure( ErrorCode.Unauthorized, "Note belongs to another character." );
		var policy = _policy.Evaluate( new HL2RPFeaturePolicyContext
		{
			Actor = actor,
			Operation = HL2RPFeatureOperation.EditNote,
			ItemId = noteItemId
		} );
		if ( policy.Failed )
			return OperationResult<NoteEditReceipt>.Failure( policy.Error!.Code, policy.Error.Message );
		var traits = new Dictionary<string, TypedPayload>( proof.Value.Item.Value.Traits, StringComparer.Ordinal )
		{
			["note"] = HL2RPPersistence.Payload(
				HL2RPPersistence.Note,
				state.Value with
				{
					Text = normalized.Value,
					OwnerCharacterId = actor.CharacterId,
					UpdatedAtUtc = _clock.UtcNow
				} )
		};
		var after = proof.Value.Item.Value with { Traits = traits };
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require( proof.Value.Access );
		HL2RPUnitOfWork.RequireActorState( unitOfWork, _repositories, proof.Value.Character );
		unitOfWork.RequireUnchanged( _repositories.Inventories, proof.Value.Inventory );
		var editor = unitOfWork.Edit( _repositories.Items, proof.Value.Item );
		if ( editor is null )
		{
			await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
			return OperationResult<NoteEditReceipt>.Failure( ErrorCode.Conflict, "Note changed." );
		}
		editor.Replace( after );
		unitOfWork.Save( editor );
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unitOfWork, cancellationToken );
		if ( !committed.Succeeded )
			return HL2RPFeaturePersistence.Failure<NoteEditReceipt>( committed.Error! );
		var receipt = new NoteEditReceipt(
			noteItemId, normalized.Value, committed.Value!.Sequence, committed.Value );
		_noteEvents.Publish( receipt );
		HL2RPFeaturePersistence.PublishAudit(
			_audit,
			actor,
			HL2RPFeatureOperation.EditNote,
			noteItemId.ToString(),
			_clock.UtcNow,
			committed.Value.Sequence );
		return OperationResult<NoteEditReceipt>.Success( receipt );
	}

	public async ValueTask<OperationResult<PermitIssuedReceipt>> IssuePermitAsync(
		InventoryActor actor,
		CharacterId ownerCharacterId,
		InventoryId destinationInventoryId,
		BusinessPermitKind kind,
		DateTimeOffset? expiresAtUtc,
		CancellationToken cancellationToken = default )
	{
		if ( !Enum.IsDefined( kind ) ||
			expiresAtUtc is not null && (expiresAtUtc.Value.Offset != TimeSpan.Zero || expiresAtUtc <= _clock.UtcNow) )
			return OperationResult<PermitIssuedReceipt>.Failure( ErrorCode.InvalidArgument, "Permit kind or expiry is invalid." );
		var actorCharacter = _repositories.Characters.Find( DomainKeys.Character( actor.CharacterId ) );
		var owner = _repositories.Characters.Find( DomainKeys.Character( ownerCharacterId ) );
		var destination = _repositories.Inventories.Find( DomainKeys.Inventory( destinationInventoryId ) );
		if ( actorCharacter is null || actorCharacter.Value.AccountId != actor.AccountId )
			return OperationResult<PermitIssuedReceipt>.Failure( ErrorCode.Unauthorized, "Actor binding is invalid." );
		if ( owner is null || destination is null ||
			destination.Value.Owner != InventoryOwner.Character( ownerCharacterId ) )
			return OperationResult<PermitIssuedReceipt>.Failure( ErrorCode.NotFound, "Permit owner inventory was not found." );
		var access = _access.Prove(
			actor.ConnectionId,
			actor.CharacterId,
			destinationInventoryId,
			InventoryCapability.TransferIn );
		if ( access is null )
			return OperationResult<PermitIssuedReceipt>.Failure( ErrorCode.Unauthorized, "Permit transfer capability is missing." );
		var policy = _policy.Evaluate( new HL2RPFeaturePolicyContext
		{
			Actor = actor,
			Operation = HL2RPFeatureOperation.IssuePermit,
			TargetCharacterId = ownerCharacterId
		} );
		if ( policy.Failed )
			return OperationResult<PermitIssuedReceipt>.Failure( policy.Error!.Code, policy.Error.Message );
		var item = new ItemRecord
		{
			Id = _ids.NewItemId(),
			Definition = new DefinitionId( HL2RPIds.Items.BusinessPermit ),
			Traits = new Dictionary<string, TypedPayload>( StringComparer.Ordinal )
			{
				["permit"] = HL2RPPersistence.Payload(
					HL2RPPersistence.BusinessPermit,
					new BusinessPermitItemState
					{
						Kind = kind,
						OwnerCharacterId = ownerCharacterId,
						IssuedAtUtc = _clock.UtcNow,
						ExpiresAtUtc = expiresAtUtc,
						Revoked = false
					} )
			}
		};
		var layoutDependencies = HL2RPUnitOfWork.CaptureInventoryLayout(
			_repositories, destination.Value );
		var firstFit = _layout.FindFirstFit( destination.Value, item );
		if ( firstFit.Failed )
			return OperationResult<PermitIssuedReceipt>.Failure( firstFit.Error!.Code, firstFit.Error.Message );
		var placed = _layout.AddAt( destination.Value, item, firstFit.Value.X, firstFit.Value.Y );
		if ( placed.Failed )
			return OperationResult<PermitIssuedReceipt>.Failure( placed.Error!.Code, placed.Error.Message );
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require( access );
		HL2RPUnitOfWork.RequireActorState( unitOfWork, _repositories, actorCharacter );
		HL2RPUnitOfWork.RequireActorState( unitOfWork, _repositories, owner );
		HL2RPUnitOfWork.RequireInventoryLayout( unitOfWork, _repositories, layoutDependencies );
		var inventoryEditor = unitOfWork.Edit( _repositories.Inventories, destination );
		if ( inventoryEditor is null )
		{
			await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
			return OperationResult<PermitIssuedReceipt>.Failure( ErrorCode.Conflict, "Destination inventory changed." );
		}
		inventoryEditor.Replace( placed.Value );
		unitOfWork.Save( inventoryEditor );
		unitOfWork.Create( _repositories.Items, DomainKeys.Item( item.Id ), item );
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unitOfWork, cancellationToken );
		if ( !committed.Succeeded )
			return HL2RPFeaturePersistence.Failure<PermitIssuedReceipt>( committed.Error! );
		var receipt = new PermitIssuedReceipt(
			item.Id, ownerCharacterId, kind, committed.Value!.Sequence, committed.Value );
		_permitEvents.Publish( receipt );
		HL2RPFeaturePersistence.PublishAudit(
			_audit,
			actor,
			HL2RPFeatureOperation.IssuePermit,
			ownerCharacterId.ToString(),
			_clock.UtcNow,
			committed.Value.Sequence );
		return OperationResult<PermitIssuedReceipt>.Success( receipt );
	}

	private OperationResult<ItemProof> RequireItem(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId itemId,
		string definitionId,
		InventoryCapability capability )
	{
		var character = _repositories.Characters.Find( DomainKeys.Character( actor.CharacterId ) );
		var inventory = _repositories.Inventories.Find( DomainKeys.Inventory( inventoryId ) );
		var item = _repositories.Items.Find( DomainKeys.Item( itemId ) );
		if ( character is null || inventory is null || item is null )
			return OperationResult<ItemProof>.Failure( ErrorCode.NotFound, "Character, inventory or item was not found." );
		if ( character.Value.AccountId != actor.AccountId || inventory.Value.Find( itemId ) is null ||
			item.Value.Definition.Value != definitionId )
			return OperationResult<ItemProof>.Failure( ErrorCode.Unauthorized, "Item ownership proof failed." );
		var access = _access.Prove( actor.ConnectionId, actor.CharacterId, inventoryId, capability );
		if ( access is null )
			return OperationResult<ItemProof>.Failure( ErrorCode.Unauthorized, "Inventory capability is missing." );
		return OperationResult<ItemProof>.Success( new ItemProof( character, inventory, item, access ) );
	}

	private static OperationResult<string> NormalizeNote( string text )
	{
		if ( text is null )
			return OperationResult<string>.Failure( ErrorCode.InvalidArgument, "Note text is required." );
		string normalized;
		try
		{
			normalized = text.Normalize().Trim();
		}
		catch ( ArgumentException )
		{
			return OperationResult<string>.Failure( ErrorCode.InvalidArgument, "Note contains invalid Unicode." );
		}
		var scalars = 0;
		for ( var index = 0; index < normalized.Length; index++ )
		{
			var character = normalized[index];
			if ( char.IsControl( character ) && character is not '\r' and not '\n' and not '\t' )
				return OperationResult<string>.Failure( ErrorCode.InvalidArgument, "Note contains control characters." );
			if ( char.IsHighSurrogate( character ) )
			{
				if ( index + 1 >= normalized.Length || !char.IsLowSurrogate( normalized[index + 1] ) )
					return OperationResult<string>.Failure( ErrorCode.InvalidArgument, "Note contains invalid Unicode." );
				index++;
			}
			else if ( char.IsLowSurrogate( character ) )
			{
				return OperationResult<string>.Failure( ErrorCode.InvalidArgument, "Note contains invalid Unicode." );
			}
			scalars++;
			if ( scalars > MaximumNoteUnicodeScalars )
				return OperationResult<string>.Failure( ErrorCode.InvalidArgument, "Note exceeds the Unicode scalar limit." );
		}
		return OperationResult<string>.Success( normalized );
	}

	private sealed record ItemProof(
		DocumentSnapshot<CharacterRecord> Character,
		DocumentSnapshot<InventoryRecord> Inventory,
		DocumentSnapshot<ItemRecord> Item,
		InventoryAccessProof Access );
}

internal static class PermitInspector
{
	public static bool HasValidPermit(
		DomainRepositories repositories,
		CharacterId characterId,
		BusinessPermitKind requiredKind,
		DateTimeOffset nowUtc ) =>
		Inspect( repositories, characterId, requiredKind, nowUtc ).HasValidPermit;

	public static PermitInspectionProof Inspect(
		DomainRepositories repositories,
		CharacterId characterId,
		BusinessPermitKind requiredKind,
		DateTimeOffset nowUtc )
	{
		ArgumentNullException.ThrowIfNull( repositories );
		var ownerIndexes = new Dictionary<string, DocumentSnapshot<OwnerInventoryRecord>>( StringComparer.Ordinal );
		var absentOwnerIndexes = new HashSet<string>( StringComparer.Ordinal );
		var inventories = new Dictionary<InventoryId, DocumentSnapshot<InventoryRecord>>();
		var items = new Dictionary<ItemId, DocumentSnapshot<ItemRecord>>();
		var pending = new Queue<DocumentSnapshot<InventoryRecord>>();
		var mainKey = DomainKeys.OwnerInventory( InventoryOwner.Character( characterId ), InventoryRoles.Main );
		var mainIndex = repositories.OwnerInventories.Find( mainKey );
		if ( mainIndex is null ) absentOwnerIndexes.Add( mainKey );
		else ownerIndexes[mainIndex.Key] = mainIndex;
		var main = mainIndex is null ? null : repositories.Inventories.Find(
			DomainKeys.Inventory( mainIndex.Value.InventoryId ) );
		if ( main is not null && main.Value.Owner == InventoryOwner.Character( characterId ) ) pending.Enqueue( main );
		var visitedInventories = new HashSet<InventoryId>();
		var visitedItems = new HashSet<ItemId>();
		while ( pending.Count > 0 )
		{
			var inventory = pending.Dequeue();
			if ( !visitedInventories.Add( inventory.Value.Id ) ) continue;
			inventories[inventory.Value.Id] = inventory;
			foreach ( var placement in inventory.Value.Placements )
			{
				if ( !visitedItems.Add( placement.ItemId ) ) continue;
				var item = repositories.Items.Find( DomainKeys.Item( placement.ItemId ) );
				if ( item is not null ) items[item.Value.Id] = item;
				if ( item is not null && item.Value.Definition.Value == HL2RPIds.Items.BusinessPermit &&
					item.Value.Traits.TryGetValue( "permit", out var payload ) )
				{
					var decoded = HL2RPFeaturePersistence.Decode( payload, HL2RPPersistence.BusinessPermit );
					if ( decoded.Succeeded && decoded.Value.OwnerCharacterId == characterId &&
						decoded.Value.Kind == requiredKind && !decoded.Value.Revoked &&
						(decoded.Value.ExpiresAtUtc is null || decoded.Value.ExpiresAtUtc > nowUtc) )
						return BuildProof( true, ownerIndexes, absentOwnerIndexes, inventories, items );
				}
				var childKey = DomainKeys.OwnerInventory(
					InventoryOwner.ParentItem( placement.ItemId ), InventoryRoles.Bag );
				var childIndex = repositories.OwnerInventories.Find( childKey );
				if ( childIndex is null ) absentOwnerIndexes.Add( childKey );
				else ownerIndexes[childIndex.Key] = childIndex;
				var child = childIndex is null ? null : repositories.Inventories.Find(
					DomainKeys.Inventory( childIndex.Value.InventoryId ) );
				if ( child is not null && child.Value.Owner == InventoryOwner.ParentItem( placement.ItemId ) &&
					!visitedInventories.Contains( child.Value.Id ) ) pending.Enqueue( child );
			}
		}
		return BuildProof( false, ownerIndexes, absentOwnerIndexes, inventories, items );
	}

	private static PermitInspectionProof BuildProof(
		bool hasValidPermit,
		IReadOnlyDictionary<string, DocumentSnapshot<OwnerInventoryRecord>> ownerIndexes,
		IReadOnlySet<string> absentOwnerIndexes,
		IReadOnlyDictionary<InventoryId, DocumentSnapshot<InventoryRecord>> inventories,
		IReadOnlyDictionary<ItemId, DocumentSnapshot<ItemRecord>> items ) => new(
			hasValidPermit,
			ownerIndexes.Values.ToArray(),
			absentOwnerIndexes.ToArray(),
			inventories.Values.ToArray(),
			items.Values.ToArray() );
}

internal sealed class PermitInspectionProof
{
	public PermitInspectionProof(
		bool hasValidPermit,
		IReadOnlyList<DocumentSnapshot<OwnerInventoryRecord>> ownerIndexes,
		IReadOnlyList<string> absentOwnerIndexKeys,
		IReadOnlyList<DocumentSnapshot<InventoryRecord>> inventories,
		IReadOnlyList<DocumentSnapshot<ItemRecord>> items )
	{
		HasValidPermit = hasValidPermit;
		OwnerIndexes = ownerIndexes;
		AbsentOwnerIndexKeys = absentOwnerIndexKeys;
		Inventories = inventories;
		Items = items;
	}

	public bool HasValidPermit { get; }
	public IReadOnlyList<DocumentSnapshot<OwnerInventoryRecord>> OwnerIndexes { get; }
	public IReadOnlyList<string> AbsentOwnerIndexKeys { get; }
	public IReadOnlyList<DocumentSnapshot<InventoryRecord>> Inventories { get; }
	public IReadOnlyList<DocumentSnapshot<ItemRecord>> Items { get; }

	public void RequireUnchanged( IUnitOfWork unitOfWork, DomainRepositories repositories )
	{
		ArgumentNullException.ThrowIfNull( unitOfWork );
		ArgumentNullException.ThrowIfNull( repositories );
		foreach ( var ownerIndex in OwnerIndexes )
			unitOfWork.RequireUnchanged( repositories.OwnerInventories, ownerIndex );
		foreach ( var key in AbsentOwnerIndexKeys )
			unitOfWork.Require( new AbsentDocumentPrecondition(
				new DocumentAddress( DomainCollections.OwnerInventories, key ) ) );
		foreach ( var inventory in Inventories )
			unitOfWork.RequireUnchanged( repositories.Inventories, inventory );
		foreach ( var item in Items )
			unitOfWork.RequireUnchanged( repositories.Items, item );
	}

	private sealed class AbsentDocumentPrecondition : ICommitPrecondition
	{
		private readonly DocumentAddress _address;
		public AbsentDocumentPrecondition( DocumentAddress address ) => _address = address;

		public PersistenceInvariantIssue? Validate( CommitPreconditionContext context ) =>
			context.Candidate.TryGet( _address, out _ )
				? new PersistenceInvariantIssue(
					"permit.inspection.stale",
					_address.ToString(),
					"A previously absent permit graph edge appeared after inspection." )
				: null;
	}
}
