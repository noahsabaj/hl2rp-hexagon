#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Composition;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Kernel.Policies;
using Hexagon.V2.Persistence;
using System.Threading;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Features;

internal sealed class FeatureTestEnvironment : IAsyncDisposable
{
	private FeatureTestEnvironment(
		FaultPersistenceProvider provider,
		CompiledSchema schema )
	{
		Provider = provider;
		Schema = schema;
		Repositories = new DomainRepositories( provider );
		Access = new InventoryAccessService();
		Clock = new MutableClock();
		Ids = new RandomAggregateIdGenerator();
		Layout = new InventoryLayoutService( new SchemaItemShapeCatalog( schema, Repositories ) );
		Sessions = new FakeSceneSessionResolver();
	}

	public FaultPersistenceProvider Provider { get; }
	public CompiledSchema Schema { get; }
	public DomainRepositories Repositories { get; }
	public InventoryAccessService Access { get; }
	public MutableClock Clock { get; }
	public IAggregateIdGenerator Ids { get; }
	public InventoryLayoutService Layout { get; }
	public FakeSceneSessionResolver Sessions { get; }

	public static async Task<FeatureTestEnvironment> CreateAsync()
	{
		var compiled = SchemaCompiler.Compile( new HL2RPSchema() );
		if ( compiled.Failed ) throw new InvalidOperationException( compiled.Error!.Message );
		var registry = new PersistedTypeRegistry().RegisterHexagonDomainTypes();
		var binding = SchemaPersistenceAdapter.Bind( compiled.Value, registry, HL2RPPersistence.Codecs );
		if ( binding.Failed ) throw new InvalidOperationException( binding.Error!.Message );
		var provider = new FaultPersistenceProvider( new InMemoryPersistenceProvider( binding.Value.Types ) );
		await provider.InitializeAsync();
		return new FeatureTestEnvironment( provider, compiled.Value );
	}

	public InventoryActor Actor( ulong account = 42, CharacterId? characterId = null ) => new(
		ConnectionId.New(),
		new AccountId( account ),
		characterId ?? CharacterId.New() );

	public CharacterRecord Character( InventoryActor actor, long balance = 100, string name = "Test Citizen" ) => new()
	{
		Id = actor.CharacterId,
		AccountId = actor.AccountId,
		Slot = 0,
		Name = name,
		Description = "A sufficiently detailed test character description.",
		Model = new DefinitionId( "model.citizen_01" ),
		Faction = new FactionId( HL2RPIds.Factions.Citizen ),
		Balance = balance,
		CreatedAt = Clock.UtcNow,
		LastPlayedAt = Clock.UtcNow,
		SchemaState = CharacterPayload( actor.AccountId, 0 )
	};

	public TypedPayload CharacterPayload( AccountId accountId, int slot ) => HL2RPPersistence.Payload(
		HL2RPPersistence.CharacterState,
		new HL2RPCharacterState
		{
			CitizenId = HL2RPCharacterStateFactory.CreateCitizenId( accountId, slot ),
			Age = 28,
			Pronouns = "they/them",
			Origin = "city_17",
			Whitelists = HL2RPWhitelist.None,
			CivicRecord = new CivicRecordState
			{
				Points = 0,
				Priority = CivicPriorityStatus.None
			}
		} );

	public InventoryRecord Inventory(
		CharacterId owner,
		IEnumerable<InventoryPlacement>? placements = null,
		int width = 4,
		int height = 4,
		InventoryId? id = null ) => new()
	{
		Id = id ?? InventoryId.New(),
		Owner = InventoryOwner.Character( owner ),
		Width = width,
		Height = height,
		Placements = placements?.ToArray() ?? Array.Empty<InventoryPlacement>()
	};

	public ItemRecord Item( string definition, IReadOnlyDictionary<string, TypedPayload>? traits = null ) => new()
	{
		Id = ItemId.New(),
		Definition = new DefinitionId( definition ),
		Traits = traits ?? new Dictionary<string, TypedPayload>()
	};

	public async Task SeedAsync( Action<IUnitOfWork> stage )
	{
		await using var unitOfWork = Provider.BeginUnitOfWork();
		stage( unitOfWork );
		var committed = await unitOfWork.CommitAsync();
		if ( !committed.Succeeded ) throw new InvalidOperationException( committed.Error!.Message );
		var missingOwnerIndexes = Repositories.Inventories.All()
			.Select( document => document.Value )
			.Where( inventory => inventory.Owner.Kind is InventoryOwnerKind.Character or InventoryOwnerKind.ParentItem )
			.Select( inventory => new OwnerInventoryRecord
			{
				Owner = inventory.Owner,
				Role = inventory.Owner.Kind == InventoryOwnerKind.Character ? "main" : "bag",
				InventoryId = inventory.Id
			} )
			.GroupBy( index => DomainKeys.OwnerInventory( index.Owner, index.Role ), StringComparer.Ordinal )
			.Select( group => group.First() )
			.Where( index => Repositories.OwnerInventories.Find(
				DomainKeys.OwnerInventory( index.Owner, index.Role ) ) is null )
			.ToArray();
		if ( missingOwnerIndexes.Length == 0 ) return;
		await using var indexUnit = Provider.BeginUnitOfWork();
		foreach ( var index in missingOwnerIndexes )
			indexUnit.Create( Repositories.OwnerInventories,
				DomainKeys.OwnerInventory( index.Owner, index.Role ), index );
		var indexed = await indexUnit.CommitAsync();
		if ( !indexed.Succeeded ) throw new InvalidOperationException( indexed.Error!.Message );
	}

	public void Grant( InventoryActor actor, InventoryId inventory, InventoryCapability capabilities ) =>
		Access.Grant( new InventoryGrant
		{
			ConnectionId = actor.ConnectionId,
			CharacterId = actor.CharacterId,
			InventoryId = inventory,
			Capabilities = capabilities,
			Kind = InventoryGrantKind.Character
		} );

	public void GrantSession(
		InventoryActor actor,
		InventoryId inventory,
		InventoryCapability capabilities,
		InteractionSessionId sessionId ) =>
		Access.Grant( new InventoryGrant
		{
			ConnectionId = actor.ConnectionId,
			CharacterId = actor.CharacterId,
			InventoryId = inventory,
			Capabilities = capabilities,
			Kind = InventoryGrantKind.InteractionSession,
			SessionId = sessionId
		} );

	public static PolicyPipeline<HL2RPFeaturePolicyContext> AllowPolicy() => new(
		new PolicyHandler<HL2RPFeaturePolicyContext>( "built_in", new FixedPolicy( PolicyDecision.Allow() ) ) );

	public static PolicyPipeline<HL2RPFeaturePolicyContext> DenyPolicy() => new(
		new PolicyHandler<HL2RPFeaturePolicyContext>(
			"built_in",
			new FixedPolicy( PolicyDecision.Deny( "veto" ) ) ) );

	public ValueTask DisposeAsync() => Provider.DisposeAsync();

	private sealed class FixedPolicy : IPolicy<HL2RPFeaturePolicyContext>
	{
		private readonly PolicyDecision _decision;
		public FixedPolicy( PolicyDecision decision ) => _decision = decision;
		public PolicyDecision Evaluate( HL2RPFeaturePolicyContext context ) => _decision;
	}
}

internal sealed class MutableClock : IHexClock
{
	public DateTimeOffset UtcNow { get; private set; } =
		new( 2030, 1, 2, 3, 4, 5, TimeSpan.Zero );
	public void Advance( TimeSpan duration ) => UtcNow += duration;
}

internal sealed class FakeSceneSessionResolver : ISceneSessionResolver
{
	private readonly Dictionary<InteractionSessionId, FakeBinding> _bindings = new();
	private long _revision;

	public InteractionSessionId Bind( SceneEntityId sceneEntityId, InteractionSessionKind kind = InteractionSessionKind.Vendor )
	{
		var id = InteractionSessionId.New();
		_bindings[id] = new FakeBinding( kind, sceneEntityId, checked(++_revision) );
		return id;
	}

	public bool Revoke( InteractionSessionId id )
	{
		if ( !_bindings.Remove( id ) ) return false;
		_revision = checked(_revision + 1);
		return true;
	}

	public OperationResult<BoundSceneSession> Resolve(
		InventoryActor actor,
		InteractionSessionId sessionId,
		InteractionSessionKind expectedKind ) =>
		_bindings.TryGetValue( sessionId, out var binding ) && binding.Kind == expectedKind
			? OperationResult<BoundSceneSession>.Success( new BoundSceneSession(
				sessionId,
				binding.Kind,
				binding.SceneEntityId,
				new FakeSceneSessionProof( this, sessionId, binding.Revision ) ) )
			: OperationResult<BoundSceneSession>.Failure( ErrorCode.Unauthorized, "stale session" );

	private bool IsCurrent( InteractionSessionId id, long revision ) =>
		_bindings.TryGetValue( id, out var binding ) && binding.Revision == revision;

	private sealed record FakeBinding(
		InteractionSessionKind Kind,
		SceneEntityId SceneEntityId,
		long Revision );

	private sealed class FakeSceneSessionProof : ICommitPrecondition
	{
		private readonly FakeSceneSessionResolver _owner;
		private readonly InteractionSessionId _id;
		private readonly long _revision;

		public FakeSceneSessionProof(
			FakeSceneSessionResolver owner,
			InteractionSessionId id,
			long revision )
		{
			_owner = owner;
			_id = id;
			_revision = revision;
		}

		public PersistenceInvariantIssue? Validate( CommitPreconditionContext context ) =>
			_owner.IsCurrent( _id, _revision )
				? null
				: new PersistenceInvariantIssue(
					"interaction.session.stale",
					$"interaction-session/{_id}",
					"Interaction session changed after the action was planned." );
	}
}

internal sealed class RecordingHandler<TEvent> : IEventHandler<TEvent>
{
	public List<TEvent> Events { get; } = new();
	public void Handle( TEvent @event ) => Events.Add( @event );
	public PostCommitEventBus<TEvent> Bus() => new( new[]
	{
		new EventHandlerRegistration<TEvent>( "recorder", this )
	} );
}

internal sealed class FaultPersistenceProvider : IPersistenceProvider
{
	private readonly IPersistenceProvider _inner;
	private bool _failNextCommit;
	private Func<Task>? _beforeNextCommit;

	public FaultPersistenceProvider( IPersistenceProvider inner ) => _inner = inner;

	public PersistedTypeRegistry Types => _inner.Types;
	public PersistenceHealth Health => _inner.Health;
	public bool IsInitialized => _inner.IsInitialized;
	public PersistenceProviderState State => _inner.State;
	public Guid StoreId => _inner.StoreId;
	public Guid WriterEpoch => _inner.WriterEpoch;
	public long CompactionGeneration => _inner.CompactionGeneration;
	public void FailNextCommit() => _failNextCommit = true;
	public void InterleaveNextCommit( Func<Task> action ) =>
		_beforeNextCommit = action ?? throw new ArgumentNullException( nameof(action) );
	public ValueTask InitializeAsync( CancellationToken cancellationToken = default ) =>
		_inner.InitializeAsync( cancellationToken );
	public IPersistenceRepository<T> Repository<T>( string collection ) where T : class =>
		_inner.Repository<T>( collection );
	public IUnitOfWork BeginUnitOfWork() => new FaultUnitOfWork( this, _inner.BeginUnitOfWork() );
	public ValueTask<PersistenceResult<long>> CheckpointAsync( CancellationToken cancellationToken = default ) =>
		_inner.CheckpointAsync( cancellationToken );
	public ValueTask<PersistenceShutdownResult> ShutdownAsync( CancellationToken cancellationToken = default ) =>
		_inner.ShutdownAsync( cancellationToken );
	public ValueTask DisposeAsync() => _inner.DisposeAsync();

	private bool ConsumeFailure()
	{
		if ( !_failNextCommit ) return false;
		_failNextCommit = false;
		return true;
	}

	private Func<Task>? ConsumeInterleave()
	{
		var action = _beforeNextCommit;
		_beforeNextCommit = null;
		return action;
	}

	private sealed class FaultUnitOfWork : IUnitOfWork
	{
		private readonly FaultPersistenceProvider _provider;
		private readonly IUnitOfWork _inner;

		public FaultUnitOfWork( FaultPersistenceProvider provider, IUnitOfWork inner )
		{
			_provider = provider;
			_inner = inner;
		}

		public DocumentEditor<T>? Edit<T>( IPersistenceRepository<T> repository, DocumentSnapshot<T> observed ) where T : class =>
			_inner.Edit( repository, observed );
		public void RequireUnchanged<T>( IPersistenceRepository<T> repository, DocumentSnapshot<T> observed ) where T : class =>
			_inner.RequireUnchanged( repository, observed );
		public void Create<T>( IPersistenceRepository<T> repository, string key, T value ) where T : class =>
			_inner.Create( repository, key, value );
		public void Put<T>( IPersistenceRepository<T> repository, string key, T value ) where T : class =>
			_inner.Put( repository, key, value );
		public void Save<T>( DocumentEditor<T> editor ) where T : class => _inner.Save( editor );
		public void Delete<T>( IPersistenceRepository<T> repository, DocumentSnapshot<T> observed ) where T : class =>
			_inner.Delete( repository, observed );
		public void Require( ICommitPrecondition precondition ) => _inner.Require( precondition );
		public async ValueTask<PersistenceResult<CommitReceipt>> CommitAsync(
			CancellationToken cancellationToken = default )
		{
			var interleave = _provider.ConsumeInterleave();
			if ( interleave is not null ) await interleave();
			if ( !_provider.ConsumeFailure() ) return await _inner.CommitAsync( cancellationToken );
			return PersistenceResult<CommitReceipt>.Failure( new PersistenceError(
				PersistenceErrorCode.DurabilityFailed,
				"Injected commit failure." ) );
		}
		public ValueTask DisposeAsync() => _inner.DisposeAsync();
	}
}
