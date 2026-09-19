#nullable enable

using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Persistence;

namespace HL2RP.V2.Features;

public enum HL2RPFeatureOperation
{
	AddInfraction,
	SetPriority,
	UpdateRecord,
	Introduce,
	SetCityObjectives,
	TuneRadio,
	EditNote,
	PurchasePermit,
	OpenBag,
	SplitTokens,
	CombineTokens,
	InstallCombineLock,
	ToggleDoor,
	ToggleForcefield,
	ClaimDoor,
	ReleaseDoor,
	SendRequest,
	VendorBuy,
	VendorSell,
	MachinePurchase,
	AdministrationAudit,
	GrantAccountEntitlement,
	RevokeAccountEntitlement
}

public sealed record HL2RPFeaturePolicyContext
{
	public required InventoryActor Actor { get; init; }
	public required HL2RPFeatureOperation Operation { get; init; }
	public CharacterId? TargetCharacterId { get; init; }
	public SceneEntityId? SceneEntityId { get; init; }
	public ItemId? ItemId { get; init; }
}

public sealed record AdminAuditFact
{
	public required AccountId ActorAccountId { get; init; }
	public CharacterId? ActorCharacterId { get; init; }
	public required HL2RPFeatureOperation Operation { get; init; }
	public required string Target { get; init; }
	public required DateTimeOffset OccurredAtUtc { get; init; }
	public required long CommitSequence { get; init; }
}

public sealed record BoundSceneSession(
	InteractionSessionId SessionId,
	InteractionSessionKind Kind,
	SceneEntityId SceneEntityId,
	ICommitPrecondition CommitProof );

public interface ISceneSessionResolver
{
	OperationResult<BoundSceneSession> Resolve(
		InventoryActor actor,
		InteractionSessionId sessionId,
		InteractionSessionKind expectedKind );
}

/// <summary>
/// Resolves the target from the host session itself, then invokes the regular
/// authority continuation path so range, LOS and policy are revalidated.
/// </summary>
public sealed class AuthoritativeSceneSessionResolver : ISceneSessionResolver
{
	private readonly InteractionSessionService _sessions;
	private readonly InteractionAuthorityService _authority;

	public AuthoritativeSceneSessionResolver(
		InteractionSessionService sessions,
		InteractionAuthorityService authority )
	{
		_sessions = sessions ?? throw new ArgumentNullException( nameof(sessions) );
		_authority = authority ?? throw new ArgumentNullException( nameof(authority) );
	}

	public OperationResult<BoundSceneSession> Resolve(
		InventoryActor actor,
		InteractionSessionId sessionId,
		InteractionSessionKind expectedKind )
	{
		var session = _sessions.ActiveSessions.SingleOrDefault( candidate => candidate.Id == sessionId );
		if ( session is null || session.Kind != expectedKind ||
			session.ConnectionId != actor.ConnectionId || session.CharacterId != actor.CharacterId ||
			session.Target.Kind != InteractionTargetKind.SceneEntity )
			return OperationResult<BoundSceneSession>.Failure(
				ErrorCode.Unauthorized, "Scene interaction session is stale or bound to another actor." );
		var continued = _authority.Continue(
			session.Id,
			actor.ConnectionId,
			actor.AccountId,
			actor.CharacterId,
			session.Target );
		if ( continued.Failed )
			return OperationResult<BoundSceneSession>.Failure(
				continued.Error!.Code, continued.Error.Message );
		var proof = _sessions.Prove( continued.Value );
		if ( proof is null )
			return OperationResult<BoundSceneSession>.Failure(
				ErrorCode.Unauthorized, "Scene interaction session changed during validation." );
		return OperationResult<BoundSceneSession>.Success( new BoundSceneSession(
			session.Id,
			session.Kind,
			new SceneEntityId( session.Target.Id ),
			proof ) );
	}
}

public interface IHL2RPItemFactory
{
	OperationResult<ItemRecord> Create( DefinitionId definition, ItemId itemId, DateTimeOffset nowUtc );
}

/// <summary>
/// Creates only server-owned default traits. Identity-bound documents are issued
/// by their dedicated service and cannot be bought as anonymous stock.
/// </summary>
public sealed class HL2RPItemFactory : IHL2RPItemFactory
{
	public OperationResult<ItemRecord> Create( DefinitionId definition, ItemId itemId, DateTimeOffset nowUtc )
	{
		if ( nowUtc.Offset != TimeSpan.Zero )
			return OperationResult<ItemRecord>.Failure( ErrorCode.InvalidArgument, "Item timestamp must be UTC." );
		var traits = new Dictionary<string, TypedPayload>( StringComparer.Ordinal );
		switch ( definition.Value )
		{
			case HL2RPIds.Items.CitizenIdCard:
			case HL2RPIds.Items.BusinessPermit:
				return OperationResult<ItemRecord>.Failure(
					ErrorCode.PolicyDenied, "Identity-bound documents require a dedicated issuer." );
			case HL2RPIds.Items.Radio:
				traits["radio"] = HL2RPPersistence.Payload(
					HL2RPPersistence.Radio,
					new RadioItemState { Frequency = "100.0", Powered = true } );
				break;
			case HL2RPIds.Items.Flashlight:
				traits["flashlight"] = HL2RPPersistence.Payload(
					HL2RPPersistence.Flashlight,
					new FlashlightItemState { Powered = false, ChargePermille = 1_000 } );
				break;
			case HL2RPIds.Items.RequestDevice:
				traits["request_device"] = HL2RPPersistence.Payload(
					HL2RPPersistence.RequestDevice,
					new RequestDeviceItemState { Powered = true, LastRequestAtUtc = null } );
				break;
			case HL2RPIds.Items.Note:
				traits["note"] = HL2RPPersistence.Payload(
					HL2RPPersistence.Note,
					new NoteItemState { Text = "", OwnerCharacterId = null, UpdatedAtUtc = nowUtc } );
				break;
			case HL2RPIds.Items.Pistol:
				traits["pistol"] = HL2RPPersistence.Payload(
					HL2RPPersistence.Pistol,
					new PistolItemState
					{
						MagazineRounds = 0,
						Equipped = false,
						Raised = false,
						LastFiredAtUtc = null
					} );
				break;
			case HL2RPIds.Items.PistolAmmunition:
				traits["ammunition"] = HL2RPPersistence.Payload(
					HL2RPPersistence.PistolAmmunition,
					new PistolAmmunitionItemState { Rounds = PistolItemState.MagazineCapacity } );
				break;
			case HL2RPIds.Items.ProtectiveVest:
				traits["vest"] = HL2RPPersistence.Payload(
					HL2RPPersistence.ProtectiveVest,
					new ProtectiveVestItemState
					{
						Durability = ProtectiveVestItemState.DefaultDurability,
						DamageReductionPermille = ProtectiveVestItemState.DefaultDamageReductionPermille,
						Equipped = false
					} );
				break;
			case HL2RPIds.Items.CombineLockKit:
				traits["lock_kit"] = HL2RPPersistence.Payload(
					HL2RPPersistence.CombineLockKit,
					new CombineLockKitItemState { RemainingInstallations = 1 } );
				break;
			case HL2RPIds.Items.TokenStack:
				traits["tokens"] = HL2RPPersistence.Payload(
					HL2RPPersistence.TokenStack,
					new TokenStackItemState { Amount = 1 } );
				break;
			case HL2RPIds.Items.Ration:
			case HL2RPIds.Items.Water:
			case HL2RPIds.Items.HealthVial:
			case HL2RPIds.Items.CivicHandbook:
			case HL2RPIds.Items.Suitcase:
			case HL2RPIds.Items.ZipTie:
				break;
			default:
				return OperationResult<ItemRecord>.Failure(
					ErrorCode.UnknownDefinition, "Item factory definition is not curated by HL2RP." );
		}
		return OperationResult<ItemRecord>.Success( new ItemRecord
		{
			Id = itemId,
			Definition = definition,
			Traits = traits
		} );
	}
}

/// <summary>
/// The one membership preamble every item-bound feature shares: the actor's canonical
/// character, the claimed inventory, the item inside it with the expected definition, and a
/// live capability proof for that inventory.
/// </summary>
internal sealed record ItemProof(
	DocumentSnapshot<CharacterRecord> Character,
	DocumentSnapshot<InventoryRecord> Inventory,
	DocumentSnapshot<ItemRecord> Item,
	InventoryAccessProof Access )
{
	public static OperationResult<ItemProof> Require(
		DomainRepositories repositories,
		InventoryAccessService access,
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId itemId,
		string definitionId,
		InventoryCapability capability )
	{
		var character = repositories.Characters.Find( DomainKeys.Character( actor.CharacterId ) );
		var inventory = repositories.Inventories.Find( DomainKeys.Inventory( inventoryId ) );
		var item = repositories.Items.Find( DomainKeys.Item( itemId ) );
		if ( character is null || inventory is null || item is null )
			return OperationResult<ItemProof>.Failure( ErrorCode.NotFound, "Character, inventory or item was not found." );
		if ( character.Value.AccountId != actor.AccountId || inventory.Value.Find( itemId ) is null ||
			item.Value.Definition.Value != definitionId )
			return OperationResult<ItemProof>.Failure( ErrorCode.Unauthorized, "Item membership proof failed." );
		var proof = access.Prove( actor.ConnectionId, actor.CharacterId, inventoryId, capability );
		return proof is null
			? OperationResult<ItemProof>.Failure( ErrorCode.Unauthorized, "Inventory capability is missing." )
			: OperationResult<ItemProof>.Success( new ItemProof( character, inventory, item, proof ) );
	}
}

internal static class HL2RPFeaturePersistence
{
	/// <summary>Shared bound for free-form civic record text and note bodies.</summary>
	public const int MaximumDocumentTextLength = 4_096;

	public static OperationResult<T> Decode<T>( TypedPayload payload, IPersistedTypeCodec<T> codec ) where T : class
	{
		if ( payload.TypeId.Value != codec.Key.Value || payload.TypeVersion != codec.CurrentVersion )
			return OperationResult<T>.Failure( ErrorCode.PersistedTypeInvalid, $"Expected '{codec.Key}' v{codec.CurrentVersion}." );
		try
		{
			return OperationResult<T>.Success( codec.Deserialize( payload.Data, payload.TypeVersion ) );
		}
		catch ( Exception exception )
		{
			Warn( $"Payload '{codec.Key}' v{payload.TypeVersion} failed to decode: {exception.Message}" );
			return OperationResult<T>.Failure( ErrorCode.PersistedTypeInvalid, $"Payload '{codec.Key}' is malformed." );
		}
	}

	/// <summary>
	/// Host-wired warning sink. Feature code is engine-neutral, so the runtime assigns the
	/// engine logger here; a throwing sink must never turn a handled decode failure into a fault.
	/// </summary>
	public static Action<string>? Diagnostic { get; set; }

	private static readonly HashSet<string> Reported = new( StringComparer.Ordinal );

	/// <summary>
	/// Each distinct message is reported once: these failures are raised from snapshot
	/// composition, which would otherwise repeat one corrupt item many times a second.
	/// </summary>
	public static void Warn( string message )
	{
		lock ( Reported )
		{
			if ( Reported.Count >= 512 ) Reported.Clear();
			if ( !Reported.Add( message ) ) return;
		}
		try { Diagnostic?.Invoke( message ); }
		catch ( Exception ) { }
	}

	public static OperationResult Failure( PersistenceError error ) =>
		OperationResult.Failure( Map( error.Code ), error.Message );

	public static OperationResult<T> Failure<T>( PersistenceError error ) =>
		OperationResult<T>.Failure( Map( error.Code ), error.Message );

	/// <summary>The framework owns the one persistence-to-operation error mapping.</summary>
	public static ErrorCode Map( PersistenceErrorCode code ) => PersistenceResultMapping.MapCode( code );

	public static void PublishAudit(
		PostCommitEventBus<AdminAuditFact> audit,
		InventoryActor actor,
		HL2RPFeatureOperation operation,
		string target,
		DateTimeOffset now,
		long sequence ) => audit.Publish( new AdminAuditFact
	{
		ActorAccountId = actor.AccountId,
		ActorCharacterId = actor.CharacterId,
		Operation = operation,
		Target = target,
		OccurredAtUtc = now,
		CommitSequence = sequence
	} );
}
