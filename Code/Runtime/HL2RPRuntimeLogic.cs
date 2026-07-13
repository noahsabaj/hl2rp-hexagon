#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Hexagon.V2.Application;
using Hexagon.V2.Composition;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Policies;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Networking;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Schema;
using HL2RP.V2.Showcase.Restraint;

namespace HL2RP.V2.Runtime;

/// <summary>
/// Coalesces presentation invalidations that are not coupled to a successful
/// client command: interaction revocation, death deadlines, and changing
/// movement-derived restraint targets.
/// </summary>
public sealed class HL2RPPresentationInvalidation
{
	private readonly object _sync = new();
	private readonly Dictionary<ConnectionId, CharacterId?> _restraintTargets = new();
	private readonly Dictionary<CharacterId, DateTimeOffset> _deathDeadlines = new();
	private readonly Dictionary<string, DateTimeOffset> _refreshDeadlines = new( StringComparer.Ordinal );
	private readonly Dictionary<ConnectionId, long> _connectionGenerations = new();
	private long _generation;
	private long _acknowledgedGeneration;
	private long _connectionGeneration;

	public void Invalidate()
	{
		lock ( _sync ) _generation++;
	}

	public void Invalidate( ConnectionId connectionId )
	{
		lock ( _sync ) _connectionGenerations[connectionId] = ++_connectionGeneration;
	}

	public void TrackDeathDeadline( CharacterId characterId, DateTimeOffset respawnAvailableAtUtc )
	{
		lock ( _sync ) _deathDeadlines[characterId] = respawnAvailableAtUtc;
	}

	public void ClearDeathDeadline( CharacterId characterId )
	{
		lock ( _sync ) _deathDeadlines.Remove( characterId );
	}

	public void TrackRefreshDeadline( string key, DateTimeOffset refreshAtUtc )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( key );
		lock ( _sync ) _refreshDeadlines[key] = refreshAtUtc;
	}

	public void RememberRestraintTarget( ConnectionId connectionId, CharacterId? characterId )
	{
		lock ( _sync ) _restraintTargets[connectionId] = characterId;
	}

	public void ObserveRestraintTarget( ConnectionId connectionId, CharacterId? characterId )
	{
		lock ( _sync )
		{
			if ( !_restraintTargets.TryGetValue( connectionId, out var previous ) )
			{
				_restraintTargets[connectionId] = characterId;
				if ( characterId is not null ) _connectionGenerations[connectionId] = ++_connectionGeneration;
				return;
			}
			if ( previous == characterId ) return;
			_restraintTargets[connectionId] = characterId;
			_connectionGenerations[connectionId] = ++_connectionGeneration;
		}
	}

	public void ForgetConnection( ConnectionId connectionId )
	{
		lock ( _sync )
		{
			_restraintTargets.Remove( connectionId );
			_connectionGenerations.Remove( connectionId );
		}
	}

	public bool IsRefreshDue( DateTimeOffset nowUtc )
	{
		lock ( _sync ) return _generation != _acknowledgedGeneration || _connectionGenerations.Count > 0 ||
			_deathDeadlines.Values.Any( deadline => deadline <= nowUtc ) ||
			_refreshDeadlines.Values.Any( deadline => deadline <= nowUtc );
	}

	public HL2RPPresentationPublication BeginPublication( DateTimeOffset nowUtc )
	{
		lock ( _sync ) return new HL2RPPresentationPublication(
			_generation,
			_generation != _acknowledgedGeneration,
			new Dictionary<ConnectionId, long>( _connectionGenerations ),
			_deathDeadlines
				.Where( pair => pair.Value <= nowUtc )
				.ToDictionary( pair => pair.Key, pair => pair.Value ),
			_refreshDeadlines
				.Where( pair => pair.Value <= nowUtc )
				.ToDictionary( pair => pair.Key, pair => pair.Value, StringComparer.Ordinal ) );
	}

	/// <summary>
	/// Captures only the current generations covered by an immediate targeted
	/// snapshot. Acknowledgement cannot consume global work, deadlines, other
	/// recipients, or a newer invalidation raised while the send is in flight.
	/// </summary>
	public HL2RPPresentationPublication BeginConnectionPublication(
		IEnumerable<ConnectionId> connectionIds )
	{
		ArgumentNullException.ThrowIfNull( connectionIds );
		lock ( _sync )
		{
			var requested = connectionIds.ToHashSet();
			return new HL2RPPresentationPublication(
				_acknowledgedGeneration,
				false,
				_connectionGenerations
					.Where( pair => requested.Contains( pair.Key ) )
					.ToDictionary( pair => pair.Key, pair => pair.Value ),
				new Dictionary<CharacterId, DateTimeOffset>(),
				new Dictionary<string, DateTimeOffset>( StringComparer.Ordinal ) );
		}
	}

	/// <summary>
	/// Called only after a complete snapshot publication succeeds. Invalidations
	/// raised after <see cref="BeginPublication"/> remain pending.
	/// </summary>
	public void AcknowledgePublished( HL2RPPresentationPublication publication )
	{
		ArgumentNullException.ThrowIfNull( publication );
		lock ( _sync )
		{
			_acknowledgedGeneration = Math.Max( _acknowledgedGeneration, publication.Generation );
			foreach ( var connection in publication.ConnectionGenerations )
				if ( _connectionGenerations.TryGetValue( connection.Key, out var current ) && current == connection.Value )
					_connectionGenerations.Remove( connection.Key );
			foreach ( var deadline in publication.DueDeathDeadlines )
				if ( _deathDeadlines.TryGetValue( deadline.Key, out var current ) && current == deadline.Value )
					_deathDeadlines.Remove( deadline.Key );
			foreach ( var deadline in publication.DueRefreshDeadlines )
				if ( _refreshDeadlines.TryGetValue( deadline.Key, out var current ) && current == deadline.Value )
					_refreshDeadlines.Remove( deadline.Key );
		}
	}
}

/// <summary>
/// Converts a typed entitlement change into connection-scoped invalidations.
/// An in-band command claims and publishes the resolved recipients immediately, and
/// that publication acknowledges their generations. Out-of-band changes remain
/// queued for the maintenance supervisor without ever becoming a global refresh.
/// </summary>
public sealed class HL2RPEntitlementPresentationInvalidation
{
	private readonly HL2RPPresentationInvalidation _invalidation;
	private readonly Func<AccountId, IReadOnlyList<ConnectionId>> _resolveRecipients;
	private readonly object _sync = new();
	private readonly HashSet<AccountId> _pendingAccounts = new();

	public HL2RPEntitlementPresentationInvalidation(
		HL2RPPresentationInvalidation invalidation,
		Func<AccountId, IReadOnlyList<ConnectionId>> resolveRecipients )
	{
		_invalidation = invalidation ?? throw new ArgumentNullException( nameof(invalidation) );
		_resolveRecipients = resolveRecipients ?? throw new ArgumentNullException( nameof(resolveRecipients) );
	}

	public void Observe( HL2RPAccountEntitlementChanged change )
	{
		ArgumentNullException.ThrowIfNull( change );
		lock ( _sync ) _pendingAccounts.Add( change.AccountId );
	}

	/// <summary>
	/// Claims the callback raised synchronously by an in-band command and turns
	/// it into connection generations covered by that command's direct send.
	/// </summary>
	public IReadOnlyList<ConnectionId> Claim( AccountId accountId )
	{
		lock ( _sync ) _pendingAccounts.Remove( accountId );
		return InvalidateRecipients( accountId );
	}

	/// <summary>
	/// Materializes out-of-band account changes on the host maintenance thread,
	/// where resolving live connection bindings is safe.
	/// </summary>
	public IReadOnlyList<ConnectionId> MaterializePending()
	{
		AccountId[] accounts;
		lock ( _sync )
		{
			accounts = _pendingAccounts.ToArray();
			_pendingAccounts.Clear();
		}
		var recipients = new HashSet<ConnectionId>();
		foreach ( var account in accounts ) recipients.UnionWith( InvalidateRecipients( account ) );
		return recipients.ToArray();
	}

	private IReadOnlyList<ConnectionId> InvalidateRecipients( AccountId accountId )
	{
		var recipients = _resolveRecipients( accountId ).Distinct().ToArray();
		foreach ( var recipient in recipients ) _invalidation.Invalidate( recipient );
		return recipients;
	}
}

public sealed record HL2RPPresentationPublication(
	long Generation,
	bool HasGlobalInvalidation,
	IReadOnlyDictionary<ConnectionId, long> ConnectionGenerations,
	IReadOnlyDictionary<CharacterId, DateTimeOffset> DueDeathDeadlines,
	IReadOnlyDictionary<string, DateTimeOffset> DueRefreshDeadlines )
{
	public bool RequiresBroadcast => HasGlobalInvalidation || DueDeathDeadlines.Count > 0 || DueRefreshDeadlines.Count > 0;
}

public sealed class HL2RPPresentationSequence
{
	private readonly object _sync = new();
	private long _current;

	public long Next()
	{
		lock ( _sync ) return checked( ++_current );
	}
}

public sealed class HL2RPCivicSubjectSelections
{
	private readonly object _sync = new();
	private readonly Dictionary<ConnectionId, CharacterId> _subjects = new();

	public void Select( ConnectionId connectionId, CharacterId subjectId )
	{
		lock ( _sync ) _subjects[connectionId] = subjectId;
	}

	public void ClearConnection( ConnectionId connectionId )
	{
		lock ( _sync ) _subjects.Remove( connectionId );
	}

	public void ClearSubject( CharacterId subjectId )
	{
		lock ( _sync )
			foreach ( var connectionId in _subjects
				.Where( pair => pair.Value == subjectId )
				.Select( pair => pair.Key )
				.ToArray() )
				_subjects.Remove( connectionId );
	}

	public CharacterId Resolve(
		ConnectionId connectionId,
		CharacterId viewerId,
		Func<CharacterId, bool> subjectExists )
	{
		ArgumentNullException.ThrowIfNull( subjectExists );
		lock ( _sync )
		{
			if ( !_subjects.TryGetValue( connectionId, out var selected ) ) return viewerId;
			if ( subjectExists( selected ) ) return selected;
			_subjects.Remove( connectionId );
			return viewerId;
		}
	}
}

public static class HL2RPObjectiveState
{
	public static IReadOnlyList<CityObjectiveState> Upsert(
		IReadOnlyList<CityObjectiveState> objectives,
		string objectiveId,
		CityObjectiveContent content,
		bool completed,
		DateTimeOffset updatedAtUtc )
	{
		ArgumentNullException.ThrowIfNull( objectives );
		ArgumentException.ThrowIfNullOrWhiteSpace( objectiveId );
		ArgumentNullException.ThrowIfNull( content );
		return objectives
			.Where( value => !string.Equals( value.Id, objectiveId, StringComparison.Ordinal ) )
			.Append( new CityObjectiveState
			{
				Id = objectiveId,
				Title = content.Title,
				Detail = content.Detail,
				Completed = completed,
				UpdatedAtUtc = updatedAtUtc
			} )
			.ToArray();
	}
}

public static class HL2RPObjectiveCommand
{
	public const string UpsertOperation = "upsert";
	public const string DeleteOperation = "delete";

	public static string ResolveId( CityObjectiveUpdate update, Func<Guid> newId )
	{
		ArgumentNullException.ThrowIfNull( update );
		ArgumentNullException.ThrowIfNull( newId );
		return update.ObjectiveId ?? $"objective.{newId():N}";
	}

	public static OperationResult<CityObjectiveUpdate> Parse( HL2RPCommandArguments arguments )
	{
		ArgumentNullException.ThrowIfNull( arguments );
		var objectiveId = arguments.OptionalString( "objective" );
		var title = arguments.String( "title" );
		var detail = arguments.String( "detail", true );
		var completed = arguments.Boolean( "completed" );
		if ( objectiveId.Failed || title.Failed || detail.Failed || completed.Failed )
			return OperationResult<CityObjectiveUpdate>.Failure(
				ErrorCode.InvalidArgument, "Objective arguments are invalid." );
		var normalizedId = string.IsNullOrWhiteSpace( objectiveId.Value ) ? null : objectiveId.Value;
		if ( normalizedId is not null )
		{
			var validId = CityObjectiveContract.ValidateIdentifier( normalizedId );
			if ( validId.Failed )
				return OperationResult<CityObjectiveUpdate>.Failure(
					ErrorCode.InvalidArgument, "Objective ID is invalid." );
			normalizedId = validId.Value;
		}
		var content = CityObjectiveContract.CreateContent( title.Value, detail.Value );
		return content.Succeeded
			? OperationResult<CityObjectiveUpdate>.Success(
				new CityObjectiveUpdate( normalizedId, content.Value, completed.Value ) )
			: OperationResult<CityObjectiveUpdate>.Failure( content.Error!.Code, content.Error.Message );
	}

	public static OperationResult<CityObjectiveChange> ParseChange( HL2RPCommandArguments arguments )
	{
		ArgumentNullException.ThrowIfNull( arguments );
		var operation = arguments.OptionalString( "operation" );
		if ( operation.Failed )
			return OperationResult<CityObjectiveChange>.Failure(
				operation.Error!.Code, operation.Error.Message );
		var normalizedOperation = string.IsNullOrWhiteSpace( operation.Value )
			? UpsertOperation
			: operation.Value.Trim().ToLowerInvariant();
		if ( normalizedOperation == DeleteOperation )
		{
			var objectiveId = arguments.String( "objective" );
			if ( objectiveId.Failed )
				return OperationResult<CityObjectiveChange>.Failure(
					ErrorCode.InvalidArgument, "Objective ID is required for deletion." );
			var validId = CityObjectiveContract.ValidateIdentifier( objectiveId.Value );
			return validId.Succeeded
				? OperationResult<CityObjectiveChange>.Success( CityObjectiveChange.Delete( validId.Value ) )
				: OperationResult<CityObjectiveChange>.Failure( validId.Error!.Code, validId.Error.Message );
		}
		if ( normalizedOperation != UpsertOperation )
			return OperationResult<CityObjectiveChange>.Failure(
				ErrorCode.InvalidArgument, "Objective operation must be 'upsert' or 'delete'." );
		var update = Parse( arguments );
		return update.Succeeded
			? OperationResult<CityObjectiveChange>.Success( CityObjectiveChange.Upsert( update.Value ) )
			: OperationResult<CityObjectiveChange>.Failure( update.Error!.Code, update.Error.Message );
	}
}

public static class HL2RPObjectiveProjection
{
	public static IReadOnlyList<IReadOnlyDictionary<string, SnapshotValue>> Rows( CityEntityState state )
	{
		ArgumentNullException.ThrowIfNull( state );
		return state.Objectives.Select( objective =>
			(IReadOnlyDictionary<string, SnapshotValue>)new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
			{
				[HL2RP.UI.HL2RPPresentationFields.Objectives.ObjectiveId] = SnapshotValue.Choice( objective.Id ),
				[HL2RP.UI.HL2RPPresentationFields.Objectives.Title] = SnapshotValue.String( objective.Title ),
				[HL2RP.UI.HL2RPPresentationFields.Objectives.Detail] = SnapshotValue.String( objective.Detail ),
				[HL2RP.UI.HL2RPPresentationFields.Objectives.UpdatedAtUnixMilliseconds] = SnapshotValue.Integer(
					objective.UpdatedAtUtc.ToUnixTimeMilliseconds() ),
				[HL2RP.UI.HL2RPPresentationFields.Objectives.Completed] = SnapshotValue.Boolean( objective.Completed )
			} ).ToArray();
	}
}

public sealed record VendorOfferAvailability( bool CanBuy, string DisabledReason );

public static class HL2RPPresentationContracts
{
	public static VendorOfferAvailability VendorOffer(
		bool hasRequiredPermit,
		int stock,
		long unitPrice,
		long balance )
	{
		if ( !hasRequiredPermit ) return new VendorOfferAvailability( false, "Required permit missing or expired" );
		if ( stock <= 0 ) return new VendorOfferAvailability( false, "Out of stock" );
		if ( unitPrice < 0 ) return new VendorOfferAvailability( false, "Invalid vendor price" );
		if ( balance < unitPrice ) return new VendorOfferAvailability( false, "Insufficient funds" );
		return new VendorOfferAvailability( true, string.Empty );
	}

	public static OperationResult<BusinessPermitKind> ParsePermitKind( string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) )
			return OperationResult<BusinessPermitKind>.Failure( ErrorCode.InvalidArgument, "Permit kind is required." );
		var normalized = value.StartsWith( "permit_", StringComparison.OrdinalIgnoreCase )
			? value["permit_".Length..]
			: value;
		return Enum.TryParse<BusinessPermitKind>( normalized, true, out var parsed ) && Enum.IsDefined( parsed )
			? OperationResult<BusinessPermitKind>.Success( parsed )
			: OperationResult<BusinessPermitKind>.Failure( ErrorCode.InvalidArgument, "Permit kind is unknown." );
	}
}

/// <summary>
/// Closed model allowlist for the six schema-authored character presentations.
/// Model selection is always revalidated against faction/class on the host.
/// </summary>
public sealed class HL2RPCharacterModelCatalog : ICharacterModelCatalog
{
	private static readonly IReadOnlyDictionary<string, string> Paths =
		new Dictionary<string, string>( StringComparer.Ordinal )
		{
			[HL2RPIds.Models.Citizen01] = "models/citizen_human/citizen_human_male.vmdl",
			[HL2RPIds.Models.Citizen02] = "models/citizen_human/citizen_human_female.vmdl",
			[HL2RPIds.Models.Citizen03] = "models/citizen/citizen.vmdl",
			[HL2RPIds.Models.CivilProtectionUnit] = "models/citizen/citizen.vmdl",
			[HL2RPIds.Models.OverwatchSoldier] = "models/citizen/citizen.vmdl",
			[HL2RPIds.Models.CityAdministrator] = "models/citizen_human/citizen_human_male.vmdl"
		};

	public IReadOnlyCollection<string> ModelPaths => Paths.Values.Distinct( StringComparer.Ordinal ).ToArray();

	public OperationResult<string> ResolvePath( DefinitionId model ) =>
		Paths.TryGetValue( model.Value, out var path )
			? OperationResult<string>.Success( path )
			: OperationResult<string>.Failure( ErrorCode.UnknownDefinition, "Character model ID is not registered by HL2RP." );

	public bool IsAllowed( DefinitionId model, FactionId faction, ClassId? characterClass ) =>
		(model.Value, faction.Value) switch
		{
			(HL2RPIds.Models.Citizen01 or HL2RPIds.Models.Citizen02 or HL2RPIds.Models.Citizen03,
				HL2RPIds.Factions.Citizen) => characterClass is null,
			(HL2RPIds.Models.CivilProtectionUnit, HL2RPIds.Factions.CivilProtection) =>
				characterClass is not null && characterClass.Value.Value is
					HL2RPIds.Classes.Recruit or HL2RPIds.Classes.Unit or
					HL2RPIds.Classes.Elite or HL2RPIds.Classes.Scanner,
			(HL2RPIds.Models.OverwatchSoldier, HL2RPIds.Factions.Overwatch) => characterClass is null,
			(HL2RPIds.Models.CityAdministrator, HL2RPIds.Factions.CityAdministration) => characterClass is null,
			_ => false
		};
}

/// <summary>
/// Repository-backed authorization for feature services. It first proves the
/// authenticated account/active-character binding, then derives role grants from
/// the committed CharacterRecord. Unrecognized operations deny by default.
/// </summary>
public sealed class HL2RPFeatureRuntimePolicy : IPolicy<Features.HL2RPFeaturePolicyContext>, IPermissionAuthorizer
{
	private readonly DomainRepositories _repositories;

	public HL2RPFeatureRuntimePolicy( DomainRepositories repositories ) =>
		_repositories = repositories ?? throw new ArgumentNullException( nameof(repositories) );

	public PolicyDecision Evaluate( Features.HL2RPFeaturePolicyContext context )
	{
		var document = _repositories.Characters.Find( DomainKeys.Character( context.Actor.CharacterId ) );
		if ( document is null || document.Value.AccountId != context.Actor.AccountId )
			return PolicyDecision.Deny( "Feature actor binding is not canonical.", ErrorCode.Unauthorized );
		if ( context.Operation is Features.HL2RPFeatureOperation.InstallCombineLock or
			Features.HL2RPFeatureOperation.ToggleForcefield )
			return document.Value.Faction.Value is HL2RPIds.Factions.CivilProtection or HL2RPIds.Factions.Overwatch
				? PolicyDecision.Allow()
				: PolicyDecision.Deny( "This operation requires a CP or Overwatch role.", ErrorCode.Unauthorized );

		var permission = context.Operation switch
		{
			Features.HL2RPFeatureOperation.AddInfraction or Features.HL2RPFeatureOperation.SetPriority =>
				HL2RPIds.Permissions.Priority,
			Features.HL2RPFeatureOperation.SetCityObjectives => HL2RPIds.Permissions.CityObjectives,
			Features.HL2RPFeatureOperation.IssuePermit => HL2RPIds.Permissions.CommerceManagement,
			Features.HL2RPFeatureOperation.ToggleDoor or
			Features.HL2RPFeatureOperation.ClaimDoor or Features.HL2RPFeatureOperation.ReleaseDoor => null,
			Features.HL2RPFeatureOperation.Introduce or
			Features.HL2RPFeatureOperation.TuneRadio or
			Features.HL2RPFeatureOperation.EditNote or
			Features.HL2RPFeatureOperation.OpenBag or
			Features.HL2RPFeatureOperation.SplitTokens or
			Features.HL2RPFeatureOperation.CombineTokens or
			Features.HL2RPFeatureOperation.SendRequest or
			Features.HL2RPFeatureOperation.PurchasePermit or
			Features.HL2RPFeatureOperation.VendorBuy or
			Features.HL2RPFeatureOperation.VendorSell or
			Features.HL2RPFeatureOperation.MachinePurchase => null,
			_ => "<unknown>"
		};
		if ( permission is null ) return PolicyDecision.Allow();
		if ( permission == "<unknown>" )
			return PolicyDecision.Deny( "Unknown HL2RP feature operation.", ErrorCode.UnknownDefinition );
		return HasPermission( context.Actor.AccountId, context.Actor.CharacterId, permission )
			? PolicyDecision.Allow()
			: PolicyDecision.Deny( $"Permission '{permission}' is required.", ErrorCode.Unauthorized );
	}

	public bool HasPermission( AccountId accountId, CharacterId characterId, string permissionId )
	{
		if ( string.IsNullOrWhiteSpace( permissionId ) ) return false;
		var document = _repositories.Characters.Find( DomainKeys.Character( characterId ) );
		return document is not null && document.Value.AccountId == accountId &&
			HL2RPRuntimeProjection.PermissionsFor( document.Value ).Contains( permissionId, StringComparer.Ordinal );
	}
}

/// <summary>
/// The generic chat command can never prove possession, capability, powered
/// state, or cooldown for a request device. Request facts enter chat only through
/// the post-commit RequestChatDeliveryHandler.
/// </summary>
public sealed class HL2RPChatRuntimePolicy : IPolicy<ChatSendContext>
{
	public PolicyDecision Evaluate( ChatSendContext context ) =>
		context.ChannelId == HL2RPIds.Channels.Request
			? PolicyDecision.Deny(
				"Request traffic requires a validated request-device action.",
				ErrorCode.PolicyDenied )
			: PolicyDecision.Allow();
}

public sealed class HL2RPInventoryTransferRuntimePolicy : IPolicy<InventoryTransferContext>
{
	private readonly IRestraintStateReader _restraints;
	public HL2RPInventoryTransferRuntimePolicy( IRestraintStateReader restraints ) => _restraints = restraints;
	public PolicyDecision Evaluate( InventoryTransferContext context ) => _restraints.IsRestrained( context.Actor.CharacterId )
		? PolicyDecision.Deny( "Restrained characters cannot move inventory items." )
		: PolicyDecision.Allow();
}

public sealed class HL2RPWorldDropRuntimePolicy : IPolicy<WorldDropContext>
{
	private readonly IRestraintStateReader _restraints;
	public HL2RPWorldDropRuntimePolicy( IRestraintStateReader restraints ) => _restraints = restraints;
	public PolicyDecision Evaluate( WorldDropContext context ) => _restraints.IsRestrained( context.Actor.CharacterId )
		? PolicyDecision.Deny( "Restrained characters cannot drop items." )
		: PolicyDecision.Allow();
}

public sealed class HL2RPWorldPickupRuntimePolicy : IPolicy<WorldPickupContext>
{
	public const float MaximumReach = 130f;
	private readonly IRestraintStateReader _restraints;
	private readonly Func<InventoryActor, WorldPoint?> _actorPosition;
	private readonly Func<WorldPoint, WorldPoint, bool> _lineOfSight;

	public HL2RPWorldPickupRuntimePolicy(
		IRestraintStateReader restraints,
		Func<InventoryActor, WorldPoint?> actorPosition,
		Func<WorldPoint, WorldPoint, bool> lineOfSight )
	{
		_restraints = restraints;
		_actorPosition = actorPosition;
		_lineOfSight = lineOfSight;
	}

	public PolicyDecision Evaluate( WorldPickupContext context )
	{
		if ( _restraints.IsRestrained( context.Actor.CharacterId ) )
			return PolicyDecision.Deny( "Restrained characters cannot pick up items." );
		var actor = _actorPosition( context.Actor );
		var target = new WorldPoint(
			context.WorldItem.Transform.PositionX,
			context.WorldItem.Transform.PositionY,
			context.WorldItem.Transform.PositionZ );
		if ( actor is null ) return PolicyDecision.Deny( "Authoritative player body is unavailable.", ErrorCode.Unauthorized );
		if ( actor.Value.DistanceSquared( target ) > MaximumReach * MaximumReach )
			return PolicyDecision.Deny( "World item is out of range." );
		return _lineOfSight( actor.Value, target )
			? PolicyDecision.Allow()
			: PolicyDecision.Deny( "World item is not visible." );
	}
}

/// <summary>
/// Strict conversion and projection helpers shared by the s&amp;box adapter and
/// neutral tests. These helpers never accept an account ID from client input.
/// </summary>
public static class HL2RPRuntimeProjection
{
	public static OperationResult<CharacterCreationRequest> ToCreationRequest(
		CompiledSchema schema,
		CharacterCreationInput input )
	{
		ArgumentNullException.ThrowIfNull( schema );
		ArgumentNullException.ThrowIfNull( input );
		var fields = new Dictionary<string, CreationValue>( StringComparer.Ordinal );
		foreach ( var pair in input.Fields )
		{
			if ( !schema.CharacterFields.TryGet( pair.Key, out var definition ) || !definition!.ShowInCreation )
				return OperationResult<CharacterCreationRequest>.Failure(
					ErrorCode.InvalidArgument, $"Creation field '{pair.Key}' is not registered for creation." );
			var converted = ToCreationValue( pair.Value );
			if ( converted.Failed )
				return OperationResult<CharacterCreationRequest>.Failure(
					converted.Error!.Code, $"Creation field '{pair.Key}' is invalid: {converted.Error.Message}" );
			fields.Add( pair.Key, converted.Value );
		}

		return OperationResult<CharacterCreationRequest>.Success( new CharacterCreationRequest
		{
			Name = input.Name,
			Description = input.Description,
			Model = input.Model,
			Faction = input.Faction,
			Class = input.Class,
			Fields = fields
		} );
	}

	public static CharacterListSnapshot CharacterList(
		long revision,
		IEnumerable<CharacterRecord> characters,
		DateTimeOffset nowUtc ) => new(
		revision,
		characters.Select( character => new CharacterSummarySnapshot(
			character.Id,
			character.Slot,
			character.Name,
			character.Description,
			character.Model,
			character.Faction,
			character.Class,
			character.LastPlayedAt,
			character.IsBanActive( nowUtc ) ) ) );

	public static OperationResult<HL2RPCharacterState> DecodeState( CharacterRecord character )
	{
		ArgumentNullException.ThrowIfNull( character );
		if ( character.SchemaState.TypeId.Value != HL2RPIds.PersistedTypes.CharacterState ||
			character.SchemaState.TypeVersion != HL2RPPersistence.CharacterState.CurrentVersion )
			return OperationResult<HL2RPCharacterState>.Failure(
				ErrorCode.PersistedTypeInvalid, "Character has incompatible HL2RP schema state." );
		try
		{
			return OperationResult<HL2RPCharacterState>.Success(
				HL2RPPersistence.CharacterState.Deserialize(
					character.SchemaState.Data,
					character.SchemaState.TypeVersion ) );
		}
		catch ( Exception )
		{
			return OperationResult<HL2RPCharacterState>.Failure(
				ErrorCode.PersistedTypeInvalid, "Character HL2RP state is malformed." );
		}
	}

	public static PlayerPrivateSnapshot PrivateSnapshot(
		CharacterRecord character,
		InventoryId? mainInventoryId )
	{
		var state = DecodeState( character );
		if ( state.Failed ) throw new InvalidOperationException( state.Error!.Message );
		var values = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
		{
			["civic.cid"] = SnapshotValue.String( state.Value.CitizenId ),
			["civic.points"] = SnapshotValue.Integer( state.Value.CivicRecord.Points ),
			["civic.infraction_count"] = SnapshotValue.Integer( state.Value.CivicRecord.Infractions.Count ),
			["civic.priority"] = SnapshotValue.Integer( (int)state.Value.CivicRecord.Priority ),
			["character.age"] = SnapshotValue.Integer( state.Value.Age ),
			["character.pronouns"] = SnapshotValue.String( state.Value.Pronouns ),
			["character.origin"] = SnapshotValue.Choice( state.Value.Origin )
		};
		if ( state.Value.CombineIdentity is not null )
		{
			values["combine.rank"] = SnapshotValue.String( state.Value.CombineIdentity.Rank.ToString() );
			values["combine.division"] = SnapshotValue.String( state.Value.CombineIdentity.Division.ToString() );
			values["combine.callsign"] = SnapshotValue.String( state.Value.CombineIdentity.ServiceName );
		}

		return new PlayerPrivateSnapshot(
			character.Id,
			character.Balance,
			mainInventoryId,
			values,
			PermissionsFor( character ) );
	}

	public static IReadOnlyList<string> PermissionsFor( CharacterRecord character ) =>
		character.Faction.Value switch
		{
			HL2RPIds.Factions.CivilProtection => new[]
			{
				HL2RPIds.Permissions.CivilProtection,
				HL2RPIds.Permissions.CivicData,
				HL2RPIds.Permissions.Priority,
				HL2RPIds.Permissions.Restraint,
				HL2RPIds.Permissions.DispatchChat,
				HL2RPIds.Permissions.ScannerPilot
			},
			HL2RPIds.Factions.Overwatch => new[]
			{
				HL2RPIds.Permissions.Overwatch,
				HL2RPIds.Permissions.CivicData,
				HL2RPIds.Permissions.Priority,
				HL2RPIds.Permissions.Restraint,
				HL2RPIds.Permissions.DispatchChat
			},
			HL2RPIds.Factions.CityAdministration => new[]
			{
				HL2RPIds.Permissions.CityAdministration,
				HL2RPIds.Permissions.CivicData,
				HL2RPIds.Permissions.CityObjectives,
				HL2RPIds.Permissions.AuditedAdministration,
				HL2RPIds.Permissions.ManageEntitlements,
				HL2RPIds.Permissions.CommerceManagement,
				HL2RPIds.Permissions.DispatchChat
			},
			_ => Array.Empty<string>()
		};

	public static string PresentationName(
		CharacterRecord? viewer,
		CharacterRecord subject,
		DomainRepositories repositories )
	{
		ArgumentNullException.ThrowIfNull( subject );
		ArgumentNullException.ThrowIfNull( repositories );
		if ( viewer?.Id == subject.Id ) return subject.Name;
		if ( subject.Faction.Value is HL2RPIds.Factions.CivilProtection or HL2RPIds.Factions.Overwatch )
			return SafeReplicatedLabel( subject );
		if ( viewer is not null )
		{
			var reference = repositories.CharacterReferences.Find(
				$"recognition-{viewer.Id}-{subject.Id}" )?.Value;
			if ( reference is not null )
			{
				try { return HL2RPPersistence.Recognition.Deserialize( reference.State.Data, reference.State.TypeVersion ).IntroducedName; }
				catch ( Exception ) { }
			}
		}
		return subject.Faction.Value == HL2RPIds.Factions.Citizen ? "Unknown citizen" : "Unknown official";
	}

	public static string SafeReplicatedLabel( CharacterRecord character )
	{
		var state = DecodeState( character );
		return state.Succeeded && state.Value.CombineIdentity is not null
			? state.Value.CombineIdentity.ServiceName
			: character.Faction.Value == HL2RPIds.Factions.Citizen ? "Unknown citizen" : "Unknown official";
	}

	public static string RecoveryDigest(
		DomainRepositories repositories,
		IEnumerable<PersistedConfigurationSnapshot>? configuration = null )
	{
		ArgumentNullException.ThrowIfNull( repositories );
		var builder = new StringBuilder();
		AppendRepository( builder, repositories, DomainCollections.Characters, repositories.Characters );
		AppendRepository( builder, repositories, DomainCollections.CharacterSlots, repositories.CharacterSlots );
		AppendRepository( builder, repositories, DomainCollections.Inventories, repositories.Inventories );
		AppendRepository( builder, repositories, DomainCollections.OwnerInventories, repositories.OwnerInventories );
		AppendRepository( builder, repositories, DomainCollections.Items, repositories.Items );
		AppendRepository( builder, repositories, DomainCollections.WorldItems, repositories.WorldItems );
		AppendRepository( builder, repositories, DomainCollections.UniqueReservations, repositories.UniqueReservations );
		AppendRepository( builder, repositories, DomainCollections.CharacterReferences, repositories.CharacterReferences );
		AppendRepository( builder, repositories, DomainCollections.SceneEntities, repositories.SceneEntities );
		AppendRepository(
			builder,
			repositories,
			HL2RPAccountEntitlements.Collection,
				repositories.Provider.Repository<HL2RPAccountEntitlementRecord>(
				HL2RPAccountEntitlements.Collection ) );
		foreach ( var entry in (configuration ?? Array.Empty<PersistedConfigurationSnapshot>())
			.OrderBy( value => value.Key, StringComparer.Ordinal ) )
		{
			AppendFramed( builder, DomainCollections.Configuration );
			AppendFramed( builder, entry.Key );
			AppendFramed( builder, entry.ValueTypeId );
			AppendFramed( builder, entry.Revision.Value.ToString( System.Globalization.CultureInfo.InvariantCulture ) );
			AppendFramed( builder, entry.CanonicalEncodedValue );
			builder.Append( '\n' );
		}
		var hash = SHA256.HashData( Encoding.UTF8.GetBytes( builder.ToString() ) );
		var digest = new StringBuilder( hash.Length * 2 );
		foreach ( var value in hash )
			digest.Append( value.ToString( "x2", System.Globalization.CultureInfo.InvariantCulture ) );
		return digest.ToString();
	}

	private static OperationResult<CreationValue> ToCreationValue( SnapshotValue value ) => value.Kind switch
	{
		SnapshotValueKind.String => OperationResult<CreationValue>.Success( CreationValue.String( value.StringValue ) ),
		SnapshotValueKind.Integer => OperationResult<CreationValue>.Success( CreationValue.Integer( value.IntegerValue ) ),
		SnapshotValueKind.Boolean => OperationResult<CreationValue>.Success( CreationValue.Boolean( value.BooleanValue ) ),
		SnapshotValueKind.Choice => ConvertChoice( value.StringValue ),
		_ => OperationResult<CreationValue>.Failure( ErrorCode.InvalidArgument, "Creation value kind is invalid." )
	};

	private static OperationResult<CreationValue> ConvertChoice( string value )
	{
		try { return OperationResult<CreationValue>.Success( CreationValue.Choice( value ) ); }
		catch ( ArgumentException exception )
		{
			return OperationResult<CreationValue>.Failure( ErrorCode.InvalidArgument, exception.Message );
		}
	}

	private static void AppendRepository<T>(
		StringBuilder builder,
		DomainRepositories repositories,
		string collection,
		IPersistenceRepository<T> repository ) where T : class
	{
		var codec = repositories.Provider.Types.Resolve<T>();
		foreach ( var document in repository.All().OrderBy( value => value.Key, StringComparer.Ordinal ) )
		{
			AppendFramed( builder, collection );
			AppendFramed( builder, document.Key );
			AppendFramed( builder, document.Revision.Value.ToString( System.Globalization.CultureInfo.InvariantCulture ) );
			AppendFramed( builder, codec.Key.Value );
			AppendFramed( builder, codec.CurrentVersion.ToString( System.Globalization.CultureInfo.InvariantCulture ) );
			AppendFramed( builder, codec.Serialize( document.Value ).GetRawText() );
			builder.Append( '\n' );
		}
	}

	private static void AppendFramed( StringBuilder builder, string value ) =>
		builder.Append( value.Length.ToString( System.Globalization.CultureInfo.InvariantCulture ) )
			.Append( '#' ).Append( value );
}

public sealed class HL2RPCommandArguments
{
	private readonly IReadOnlyDictionary<string, SnapshotValue> _values;

	public HL2RPCommandArguments( IReadOnlyDictionary<string, SnapshotValue> values ) =>
		_values = values ?? throw new ArgumentNullException( nameof(values) );

	public OperationResult<string> String( string key, bool allowEmpty = false )
	{
		if ( !_values.TryGetValue( key, out var value ) ||
			value.Kind is not (SnapshotValueKind.String or SnapshotValueKind.Choice) ||
			(!allowEmpty && string.IsNullOrWhiteSpace( value.StringValue )) )
			return OperationResult<string>.Failure( ErrorCode.InvalidArgument, $"Argument '{key}' must be a string." );
		return OperationResult<string>.Success( value.StringValue );
	}

	public OperationResult<string?> OptionalString( string key )
	{
		if ( !_values.TryGetValue( key, out var value ) )
			return OperationResult<string?>.Success( null );
		return value.Kind is SnapshotValueKind.String or SnapshotValueKind.Choice
			? OperationResult<string?>.Success( value.StringValue )
			: OperationResult<string?>.Failure(
				ErrorCode.InvalidArgument, $"Argument '{key}' must be a string when provided." );
	}

	public OperationResult<long> Integer( string key ) =>
		_values.TryGetValue( key, out var value ) && value.Kind == SnapshotValueKind.Integer
			? OperationResult<long>.Success( value.IntegerValue )
			: OperationResult<long>.Failure( ErrorCode.InvalidArgument, $"Argument '{key}' must be an integer." );

	public OperationResult<bool> Boolean( string key ) =>
		_values.TryGetValue( key, out var value ) && value.Kind == SnapshotValueKind.Boolean
			? OperationResult<bool>.Success( value.BooleanValue )
			: OperationResult<bool>.Failure( ErrorCode.InvalidArgument, $"Argument '{key}' must be Boolean." );

	public OperationResult<Guid> Guid( string key )
	{
		var value = String( key );
		return value.Succeeded && System.Guid.TryParse( value.Value, out var parsed ) && parsed != System.Guid.Empty
			? OperationResult<Guid>.Success( parsed )
			: OperationResult<Guid>.Failure( ErrorCode.InvalidArgument, $"Argument '{key}' must be a non-empty GUID." );
	}

	public OperationResult<Guid?> OptionalGuid( string key )
	{
		if ( !_values.TryGetValue( key, out var value ) )
			return OperationResult<Guid?>.Success( null );
		if ( value.Kind is not (SnapshotValueKind.String or SnapshotValueKind.Choice) )
			return OperationResult<Guid?>.Failure( ErrorCode.InvalidArgument, $"Argument '{key}' must be a GUID when provided." );
		if ( string.IsNullOrWhiteSpace( value.StringValue ) )
			return OperationResult<Guid?>.Success( null );
		return System.Guid.TryParse( value.StringValue, out var parsed ) && parsed != System.Guid.Empty
			? OperationResult<Guid?>.Success( parsed )
			: OperationResult<Guid?>.Failure( ErrorCode.InvalidArgument, $"Argument '{key}' must be a non-empty GUID when provided." );
	}
}
