#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Persistence;

namespace HL2RP.V2.Features;

public sealed record NoteEditReceipt( ItemId NoteItemId, string Text, long CommitSequence );

public sealed record PermitIssuedReceipt(
	ItemId PermitItemId,
	CharacterId OwnerCharacterId,
	BusinessPermitKind Kind,
	long CommitSequence );

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
		var receipt = new NoteEditReceipt( noteItemId, normalized.Value, committed.Value!.Sequence );
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
		if ( !_access.Has(
			actor.ConnectionId,
			actor.CharacterId,
			destinationInventoryId,
			InventoryCapability.TransferIn ) )
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
		var firstFit = _layout.FindFirstFit( destination.Value, item );
		if ( firstFit.Failed )
			return OperationResult<PermitIssuedReceipt>.Failure( firstFit.Error!.Code, firstFit.Error.Message );
		var placed = _layout.AddAt( destination.Value, item, firstFit.Value.X, firstFit.Value.Y );
		if ( placed.Failed )
			return OperationResult<PermitIssuedReceipt>.Failure( placed.Error!.Code, placed.Error.Message );
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
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
		var receipt = new PermitIssuedReceipt( item.Id, ownerCharacterId, kind, committed.Value!.Sequence );
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
		if ( !_access.Has( actor.ConnectionId, actor.CharacterId, inventoryId, capability ) )
			return OperationResult<ItemProof>.Failure( ErrorCode.Unauthorized, "Inventory capability is missing." );
		return OperationResult<ItemProof>.Success( new ItemProof( item ) );
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

	private sealed record ItemProof( DocumentSnapshot<ItemRecord> Item );
}

internal static class PermitInspector
{
	public static bool HasValidPermit(
		DomainRepositories repositories,
		CharacterId characterId,
		BusinessPermitKind requiredKind,
		DateTimeOffset nowUtc )
	{
		var allInventories = repositories.Inventories.All().Select( document => document.Value ).ToArray();
		var pending = new Queue<InventoryRecord>( allInventories.Where( inventory =>
			inventory.Owner == InventoryOwner.Character( characterId ) ) );
		var visitedInventories = new HashSet<InventoryId>();
		var visitedItems = new HashSet<ItemId>();
		while ( pending.Count > 0 )
		{
			var inventory = pending.Dequeue();
			if ( !visitedInventories.Add( inventory.Id ) ) continue;
			foreach ( var placement in inventory.Placements )
			{
				if ( !visitedItems.Add( placement.ItemId ) ) continue;
				var item = repositories.Items.Find( DomainKeys.Item( placement.ItemId ) )?.Value;
				if ( item is not null && item.Definition.Value == HL2RPIds.Items.BusinessPermit &&
					item.Traits.TryGetValue( "permit", out var payload ) )
				{
					var decoded = HL2RPFeaturePersistence.Decode( payload, HL2RPPersistence.BusinessPermit );
					if ( decoded.Succeeded && decoded.Value.OwnerCharacterId == characterId &&
						decoded.Value.Kind == requiredKind && !decoded.Value.Revoked &&
						(decoded.Value.ExpiresAtUtc is null || decoded.Value.ExpiresAtUtc > nowUtc) )
						return true;
				}
				foreach ( var child in allInventories.Where( candidate =>
					candidate.Owner == InventoryOwner.ParentItem( placement.ItemId ) ) )
					if ( !visitedInventories.Contains( child.Id ) ) pending.Enqueue( child );
			}
		}
		return false;
	}
}
