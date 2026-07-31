#nullable enable

using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Persistence;
using System.Threading;
using System.Threading.Tasks;

namespace HL2RP.V2.Features;

public sealed record HL2RPAccountEntitlementMutationReceipt(
	HL2RPAccountEntitlementSnapshot Snapshot,
	CommitReceipt Commit ) : IHL2RPCommittedOperation
{
	public AccountId AccountId => Snapshot.AccountId;
	public HL2RPWhitelist Flags => Snapshot.Flags;
	public DocumentRevision Revision => Snapshot.Revision;
	public bool IsPersisted => Snapshot.IsPersisted;
}

/// <summary>
/// Canonical repository-backed account entitlement authority. The verification
/// account is an in-memory overlay and can never be written to the entitlement
/// collection. Every real change commits before notifications and audit facts.
/// </summary>
public sealed class HL2RPAccountEntitlementService : IHL2RPWhitelistService
{
	public const ulong VerificationAccount = 76_561_198_000_000_001UL;
	private readonly IPersistenceRepository<HL2RPAccountEntitlementRecord> _repository;
	private readonly IPersistenceProvider _provider;
	private readonly IHexClock _clock;
	private readonly Func<HL2RPEntitlementAdministrator, bool> _canManage;
	private readonly Func<AccountId, bool> _isKnownAccount;
	private readonly PostCommitEventBus<HL2RPAccountEntitlementChanged> _changes;
	private readonly PostCommitEventBus<AdminAuditFact> _audit;
	private readonly AccountId? _verificationAccount;

	public HL2RPAccountEntitlementService(
		IPersistenceProvider provider,
		IHexClock clock,
		Func<HL2RPEntitlementAdministrator, bool> canManage,
		Func<AccountId, bool> isKnownAccount,
		PostCommitEventBus<HL2RPAccountEntitlementChanged>? changes = null,
		PostCommitEventBus<AdminAuditFact>? audit = null,
		AccountId? verificationAccount = null )
	{
		_provider = provider ?? throw new ArgumentNullException( nameof(provider) );
		_clock = clock ?? throw new ArgumentNullException( nameof(clock) );
		_canManage = canManage ?? throw new ArgumentNullException( nameof(canManage) );
		_isKnownAccount = isKnownAccount ?? throw new ArgumentNullException( nameof(isKnownAccount) );
		_changes = changes ?? new PostCommitEventBus<HL2RPAccountEntitlementChanged>();
		_audit = audit ?? new PostCommitEventBus<AdminAuditFact>();
		_verificationAccount = verificationAccount;
		_repository = provider.Repository<HL2RPAccountEntitlementRecord>( HL2RPAccountEntitlements.Collection );
	}

	public HL2RPWhitelist GetWhitelists( AccountId accountId )
	{
		var observed = Observe( accountId );
		return observed.Succeeded ? observed.Value.Flags : HL2RPWhitelist.None;
	}

	public OperationResult ValidateAll()
	{
		foreach ( var document in _repository.All() )
		{
			if ( !string.Equals( document.Key, HL2RPAccountEntitlements.Key( document.Value.AccountId ),
				StringComparison.Ordinal ) )
				return OperationResult.Failure(
					ErrorCode.PersistedTypeInvalid, "Account entitlement is stored under the wrong document key." );
			var validation = ValidateDocument( document.Value.AccountId, document.Value );
			if ( validation.Failed ) return validation;
		}
		return OperationResult.Success();
	}

	public OperationResult<HL2RPAccountEntitlementSnapshot> Observe( AccountId accountId )
	{
		if ( _verificationAccount == accountId )
			return OperationResult<HL2RPAccountEntitlementSnapshot>.Success( new(
				accountId, HL2RPAccountEntitlements.All, DocumentRevision.None, false ) );
		var document = _repository.Find( HL2RPAccountEntitlements.Key( accountId ) );
		if ( document is null )
			return OperationResult<HL2RPAccountEntitlementSnapshot>.Success( new(
				accountId, HL2RPWhitelist.None, DocumentRevision.None, false ) );
		var validation = ValidateDocument( accountId, document.Value );
		return validation.Failed
			? OperationResult<HL2RPAccountEntitlementSnapshot>.Failure(
				validation.Error!.Code, validation.Error.Message )
			: OperationResult<HL2RPAccountEntitlementSnapshot>.Success( new(
				accountId, document.Value.Flags, document.Revision, true ) );
	}

	public ValueTask<OperationResult<HL2RPAccountEntitlementMutationReceipt>> GrantAsync(
		HL2RPEntitlementAdministrator administrator,
		AccountId targetAccountId,
		HL2RPWhitelist flag,
		DocumentRevision expectedRevision,
		CancellationToken cancellationToken = default ) =>
		MutateAsync( administrator, targetAccountId, flag, expectedRevision, grant: true, cancellationToken );

	public ValueTask<OperationResult<HL2RPAccountEntitlementMutationReceipt>> RevokeAsync(
		HL2RPEntitlementAdministrator administrator,
		AccountId targetAccountId,
		HL2RPWhitelist flag,
		DocumentRevision expectedRevision,
		CancellationToken cancellationToken = default ) =>
		MutateAsync( administrator, targetAccountId, flag, expectedRevision, grant: false, cancellationToken );

	private async ValueTask<OperationResult<HL2RPAccountEntitlementMutationReceipt>> MutateAsync(
		HL2RPEntitlementAdministrator administrator,
		AccountId targetAccountId,
		HL2RPWhitelist flag,
		DocumentRevision expectedRevision,
		bool grant,
		CancellationToken cancellationToken )
	{
		ArgumentNullException.ThrowIfNull( administrator );
		if ( !_canManage( administrator ) )
			return Failure( ErrorCode.Unauthorized, "Authenticated account cannot manage entitlements." );
		if ( !HL2RPAccountEntitlements.IsSingleFlag( flag ) )
			return Failure( ErrorCode.InvalidArgument, "Exactly one known entitlement flag is required." );
		if ( _verificationAccount == targetAccountId )
			return Failure( ErrorCode.PolicyDenied, "Synthetic verification entitlements cannot be persisted." );
		if ( expectedRevision.Value < 0 )
			return Failure( ErrorCode.InvalidArgument, "Expected entitlement revision cannot be negative." );

		var key = HL2RPAccountEntitlements.Key( targetAccountId );
		var document = _repository.Find( key );
		if ( document is null && !_isKnownAccount( targetAccountId ) )
			return Failure( ErrorCode.NotFound, "Target account is not known to this host." );
		if ( document is not null )
		{
			var validation = ValidateDocument( targetAccountId, document.Value );
			if ( validation.Failed ) return Failure( validation.Error!.Code, validation.Error.Message );
		}
		var currentRevision = document?.Revision ?? DocumentRevision.None;
		if ( currentRevision != expectedRevision )
			return Failure( ErrorCode.Conflict,
				$"Entitlement revision changed from {expectedRevision} to {currentRevision}. Refresh before retrying." );
		var previous = document?.Value.Flags ?? HL2RPWhitelist.None;
		var updated = grant ? previous | flag : previous & ~flag;
		if ( updated == previous )
			return Failure( ErrorCode.Conflict,
				grant ? "Entitlement is already granted." : "Entitlement is already revoked." );

		var record = new HL2RPAccountEntitlementRecord
		{
			AccountId = targetAccountId,
			Flags = updated,
			UpdatedByAccountId = administrator.AccountId,
			UpdatedByCharacterId = administrator.CharacterId,
			UpdatedAtUtc = _clock.UtcNow
		};
		var unit = _provider.BeginUnitOfWork();
		if ( document is null )
		{
			unit.Create( _repository, key, record );
		}
		else
		{
			var editor = unit.Edit( _repository, document );
			if ( editor is null )
			{
				await unit.DisposeAsync();
				return Failure( ErrorCode.Conflict, "Entitlement changed before it could be edited." );
			}
			editor.Replace( record );
			unit.Save( editor );
		}
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unit, cancellationToken );
		if ( !committed.Succeeded )
			return Failure( Map( committed.Error!.Code ), committed.Error.Message );
		if ( committed.Value is not CommitReceipt receipt )
			return Failure( ErrorCode.InternalError, "Entitlement commit returned no receipt." );
		var published = _repository.Find( key );
		if ( published is null )
			return Failure( ErrorCode.InternalError, "Committed entitlement was not published." );
		var snapshot = new HL2RPAccountEntitlementSnapshot(
			targetAccountId, published.Value.Flags, published.Revision, true );
		_changes.Publish( new HL2RPAccountEntitlementChanged(
			targetAccountId, previous, updated, published.Revision,
			receipt.Sequence, _clock.UtcNow ) );
		_audit.Publish( new AdminAuditFact
		{
			ActorAccountId = administrator.AccountId,
			ActorCharacterId = administrator.CharacterId,
			Operation = grant
				? HL2RPFeatureOperation.GrantAccountEntitlement
				: HL2RPFeatureOperation.RevokeAccountEntitlement,
			Target = $"account:{targetAccountId.Value};flag:{HL2RPAccountEntitlements.StableName( flag )}",
			OccurredAtUtc = _clock.UtcNow,
			CommitSequence = receipt.Sequence
		} );
		return OperationResult<HL2RPAccountEntitlementMutationReceipt>.Success(
			new HL2RPAccountEntitlementMutationReceipt( snapshot, receipt ) );
	}

	private static OperationResult ValidateDocument(
		AccountId expectedAccountId,
		HL2RPAccountEntitlementRecord record )
	{
		if ( record.AccountId != expectedAccountId || !HL2RPAccountEntitlements.IsValidSet( record.Flags ) ||
			record.UpdatedAtUtc.Offset != TimeSpan.Zero )
			return OperationResult.Failure(
				ErrorCode.PersistedTypeInvalid, "Account entitlement document is malformed or stored under the wrong account." );
		return OperationResult.Success();
	}

	private static ErrorCode Map( PersistenceErrorCode code ) => code switch
	{
		PersistenceErrorCode.NotFound => ErrorCode.NotFound,
		PersistenceErrorCode.AlreadyExists or PersistenceErrorCode.RevisionConflict => ErrorCode.Conflict,
		PersistenceErrorCode.TypeNotRegistered or PersistenceErrorCode.CollectionTypeMismatch => ErrorCode.PersistedTypeInvalid,
		PersistenceErrorCode.InvalidOperation => ErrorCode.InvalidArgument,
		_ => ErrorCode.InternalError
	};

	private static OperationResult<HL2RPAccountEntitlementMutationReceipt> Failure( ErrorCode code, string message ) =>
		OperationResult<HL2RPAccountEntitlementMutationReceipt>.Failure( code, message );
}
