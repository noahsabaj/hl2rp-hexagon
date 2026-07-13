#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Composition;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Policies;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Schema;
using HL2RP.V2.Showcase.Combat;
using HL2RP.V2.Tests.Schema;

namespace HL2RP.V2.Tests.Showcase;

internal sealed class ShowcaseTestEnvironment : IAsyncDisposable
{
	private ShowcaseTestEnvironment(
		FaultInjectingProvider provider,
		DomainRepositories repositories,
		CompiledSchema schema,
		MutableClock clock,
		InventoryAccessService access)
	{
		Provider = provider;
		Repositories = repositories;
		Schema = schema;
		Clock = clock;
		Access = access;
		Layout = new InventoryLayoutService(new SchemaItemShapeCatalog(schema, repositories));
	}

	public FaultInjectingProvider Provider { get; }
	public DomainRepositories Repositories { get; }
	public CompiledSchema Schema { get; }
	public MutableClock Clock { get; }
	public InventoryAccessService Access { get; }
	public InventoryLayoutService Layout { get; }

	public static async Task<ShowcaseTestEnvironment> CreateAsync()
	{
		var schema = HL2RPSchemaTests.Compile();
		var registry = new PersistedTypeRegistry().RegisterHexagonDomainTypes();
		var binding = SchemaPersistenceAdapter.Bind(schema, registry, HL2RPPersistence.Codecs);
		if (binding.Failed) throw new InvalidOperationException(binding.Error!.Message);
		var provider = new FaultInjectingProvider(new InMemoryPersistenceProvider(binding.Value.Types));
		await provider.InitializeAsync();
		return new ShowcaseTestEnvironment(provider, new DomainRepositories(provider), schema,
			new MutableClock(), new InventoryAccessService());
	}

	public async Task<SeededCharacter> SeedCharacterAsync(
		ulong accountValue,
		string faction = HL2RPIds.Factions.Citizen,
		string? characterClass = null,
		params ItemRecord[] items)
	{
		var actor = new InventoryActor(ConnectionId.New(), new AccountId(accountValue), CharacterId.New());
		var inventoryId = InventoryId.New();
		var state = new HL2RPCharacterState
		{
			CitizenId = $"C17-{accountValue:D20}-00",
			Age = 30,
			Pronouns = "they/them",
			Origin = "city_17",
			Whitelists = HL2RPWhitelist.None,
			CivicRecord = new CivicRecordState { Points = 0, Priority = CivicPriorityStatus.None },
			CombineIdentity = null
		};
		var character = new CharacterRecord
		{
			Id = actor.CharacterId,
			AccountId = actor.AccountId,
			Slot = 0,
			Name = $"Character {accountValue}",
			Description = "Test character",
			Model = new DefinitionId(HL2RPIds.Models.Citizen01),
			Faction = new FactionId(faction),
			Class = characterClass is null ? null : new ClassId(characterClass),
			Balance = 100,
			CreatedAt = Clock.UtcNow,
			LastPlayedAt = Clock.UtcNow,
			SchemaState = HL2RPPersistence.Payload(HL2RPPersistence.CharacterState, state)
		};
		var placements = items.Select((item, index) => new InventoryPlacement(item.Id, index * 2, 0)).ToArray();
		var inventory = new InventoryRecord
		{
			Id = inventoryId,
			Owner = InventoryOwner.Character(actor.CharacterId),
			Width = 8,
			Height = 6,
			Placements = placements
		};
		await using var unitOfWork = Provider.BeginUnitOfWork();
		unitOfWork.Create(Repositories.Characters, DomainKeys.Character(actor.CharacterId), character);
		unitOfWork.Create(
			Repositories.CharacterLifecycleGuards,
			DomainKeys.CharacterLifecycleGuard(actor.CharacterId),
			new CharacterLifecycleGuardRecord { CharacterId = actor.CharacterId, ReferenceRevision = 0 });
		unitOfWork.Create(Repositories.Inventories, DomainKeys.Inventory(inventoryId), inventory);
		unitOfWork.Create(
			Repositories.OwnerInventories,
			DomainKeys.OwnerInventory(inventory.Owner, "main"),
			new OwnerInventoryRecord { Owner = inventory.Owner, Role = "main", InventoryId = inventory.Id });
		foreach (var item in items)
			unitOfWork.Create(Repositories.Items, DomainKeys.Item(item.Id), item);
		var committed = await unitOfWork.CommitAsync();
		if (!committed.Succeeded) throw new InvalidOperationException(committed.Error!.Message);
		Access.Grant(new InventoryGrant
		{
			ConnectionId = actor.ConnectionId,
			CharacterId = actor.CharacterId,
			InventoryId = inventoryId,
			Capabilities = InventoryCapability.View | InventoryCapability.Use | InventoryCapability.Move |
				InventoryCapability.Drop | InventoryCapability.TransferIn | InventoryCapability.TransferOut,
			Kind = InventoryGrantKind.Character
		});
		return new SeededCharacter(actor, inventoryId);
	}

	public async Task SeedScannerAsync(SceneEntityId scannerId, string kind = "scanner_drone")
	{
		var record = new PersistentSceneEntityRecord
		{
			Id = scannerId,
			Kind = kind,
			State = HL2RPPersistence.Payload(HL2RPPersistence.ScannerState, new ScannerEntityState
			{
				PilotCharacterId = null,
				SpotlightEnabled = false,
				LastAcceptedInputSequence = 0,
				PhotoCooldownUntilUtc = null,
				Photos = Array.Empty<ScannerPhotoMetadata>()
			})
		};
		await using var unitOfWork = Provider.BeginUnitOfWork();
		unitOfWork.Create(Repositories.SceneEntities, DomainKeys.SceneEntity(scannerId), record);
		var committed = await unitOfWork.CommitAsync();
		if (!committed.Succeeded) throw new InvalidOperationException(committed.Error!.Message);
	}

	public static ItemRecord Pistol(int rounds = PistolItemState.MagazineCapacity,
		bool equipped = true, bool raised = false) => new()
	{
		Id = ItemId.New(),
		Definition = new DefinitionId(HL2RPIds.Items.Pistol),
		Traits = new Dictionary<string, TypedPayload>
		{
			[CombatTraitNames.Pistol] = HL2RPPersistence.Payload(HL2RPPersistence.Pistol,
				new PistolItemState
				{
					MagazineRounds = rounds,
					Equipped = equipped,
					Raised = raised,
					LastFiredAtUtc = null
				})
		}
	};

	public static ItemRecord Ammunition(int rounds = PistolItemState.MagazineCapacity) => new()
	{
		Id = ItemId.New(),
		Definition = new DefinitionId(HL2RPIds.Items.PistolAmmunition),
		Traits = new Dictionary<string, TypedPayload>
		{
			[CombatTraitNames.Ammunition] = HL2RPPersistence.Payload(HL2RPPersistence.PistolAmmunition,
				new PistolAmmunitionItemState { Rounds = rounds })
		}
	};

	public static ItemRecord Vest(int durability = 100, bool equipped = true) => new()
	{
		Id = ItemId.New(),
		Definition = new DefinitionId(HL2RPIds.Items.ProtectiveVest),
		Traits = new Dictionary<string, TypedPayload>
		{
			[CombatTraitNames.Vest] = HL2RPPersistence.Payload(HL2RPPersistence.ProtectiveVest,
				new ProtectiveVestItemState
				{
					Durability = durability,
					DamageReductionPermille = 300,
					Equipped = equipped
				})
		}
	};

	public static ItemRecord ZipTie() => new()
	{
		Id = ItemId.New(),
		Definition = new DefinitionId(HL2RPIds.Items.ZipTie)
	};

	public static PolicyPipeline<T> AllowPolicy<T>() => new(
		new PolicyHandler<T>("test.allow", new AlwaysAllowPolicy<T>()));

	public PistolItemState ReadPistol(ItemId id)
	{
		var item = Repositories.Items.Find(DomainKeys.Item(id))!.Value;
		return HL2RPPersistence.Pistol.Deserialize(item.Traits[CombatTraitNames.Pistol].Data,
			item.Traits[CombatTraitNames.Pistol].TypeVersion);
	}

	public ProtectiveVestItemState ReadVest(ItemId id)
	{
		var item = Repositories.Items.Find(DomainKeys.Item(id))!.Value;
		return HL2RPPersistence.ProtectiveVest.Deserialize(item.Traits[CombatTraitNames.Vest].Data,
			item.Traits[CombatTraitNames.Vest].TypeVersion);
	}

	public PistolAmmunitionItemState ReadAmmunition(ItemId id)
	{
		var item = Repositories.Items.Find(DomainKeys.Item(id))!.Value;
		return HL2RPPersistence.PistolAmmunition.Deserialize(item.Traits[CombatTraitNames.Ammunition].Data,
			item.Traits[CombatTraitNames.Ammunition].TypeVersion);
	}

	public ScannerEntityState ReadScanner(SceneEntityId id)
	{
		var record = Repositories.SceneEntities.Find(DomainKeys.SceneEntity(id))!.Value;
		return HL2RPPersistence.ScannerState.Deserialize(record.State.Data, record.State.TypeVersion);
	}

	public ValueTask DisposeAsync() => Provider.DisposeAsync();

	private sealed class AlwaysAllowPolicy<T> : IPolicy<T>
	{
		public PolicyDecision Evaluate(T context) => PolicyDecision.Allow();
	}
}

internal sealed record SeededCharacter(InventoryActor Actor, InventoryId InventoryId);

internal sealed class MutableClock : IHexClock
{
	public DateTimeOffset UtcNow { get; private set; } =
		new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);

	public void Advance(TimeSpan duration) => UtcNow += duration;
}

internal sealed class FaultInjectingProvider : IPersistenceProvider
{
	private readonly IPersistenceProvider _inner;
	private int _remainingCommitFailures;
	private PersistenceErrorCode _commitFailureCode = PersistenceErrorCode.DurabilityFailed;
	private bool _forceFatalHealth;
	private Action? _afterNextSuccessfulCommit;
	private Func<Task>? _beforeNextCommit;

	public FaultInjectingProvider(IPersistenceProvider inner) =>
		_inner = inner ?? throw new ArgumentNullException(nameof(inner));

	public PersistedTypeRegistry Types => _inner.Types;
	public PersistenceHealth Health => _forceFatalHealth
		? _inner.Health with { Status = PersistenceHealthStatus.Fatal, Detail = "Injected fatal health." }
		: _inner.Health;
	public bool IsInitialized => _inner.IsInitialized;
	public PersistenceProviderState State => _inner.State;
	public Guid StoreId => _inner.StoreId;
	public Guid WriterEpoch => _inner.WriterEpoch;
	public long CompactionGeneration => _inner.CompactionGeneration;

	public void FailNextCommit()
	{
		_remainingCommitFailures = 1;
		_commitFailureCode = PersistenceErrorCode.DurabilityFailed;
	}

	public void ConflictNextCommits(int count)
	{
		if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
		_remainingCommitFailures = count;
		_commitFailureCode = PersistenceErrorCode.RevisionConflict;
	}

	public void ForceFatalHealth(bool fatal) => _forceFatalHealth = fatal;
	public void AfterNextSuccessfulCommit(Action callback) =>
		_afterNextSuccessfulCommit = callback ?? throw new ArgumentNullException(nameof(callback));
	public void InterleaveNextCommit(Func<Task> callback) =>
		_beforeNextCommit = callback ?? throw new ArgumentNullException(nameof(callback));
	public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
		_inner.InitializeAsync(cancellationToken);
	public IPersistenceRepository<T> Repository<T>(string collection) where T : class =>
		_inner.Repository<T>(collection);
	public IUnitOfWork BeginUnitOfWork() => new FaultUnitOfWork(this, _inner.BeginUnitOfWork());
	public ValueTask<PersistenceResult<long>> CheckpointAsync(CancellationToken cancellationToken = default) =>
		_inner.CheckpointAsync(cancellationToken);
	public ValueTask<PersistenceShutdownResult> ShutdownAsync(CancellationToken cancellationToken = default) =>
		_inner.ShutdownAsync(cancellationToken);
	public ValueTask DisposeAsync() => _inner.DisposeAsync();

	private bool TryConsumeFailure(out PersistenceErrorCode code)
	{
		code = _commitFailureCode;
		if (_remainingCommitFailures <= 0) return false;
		_remainingCommitFailures--;
		return true;
	}

	private sealed class FaultUnitOfWork : IUnitOfWork
	{
		private readonly FaultInjectingProvider _provider;
		private readonly IUnitOfWork _inner;

		public FaultUnitOfWork(FaultInjectingProvider provider, IUnitOfWork inner)
		{
			_provider = provider;
			_inner = inner;
		}

		public DocumentEditor<T>? Edit<T>(IPersistenceRepository<T> repository, DocumentSnapshot<T> observed) where T : class =>
			_inner.Edit(repository, observed);
		public void RequireUnchanged<T>(IPersistenceRepository<T> repository, DocumentSnapshot<T> observed) where T : class =>
			_inner.RequireUnchanged(repository, observed);
		public void Create<T>(IPersistenceRepository<T> repository, string key, T value) where T : class =>
			_inner.Create(repository, key, value);
		public void Put<T>(IPersistenceRepository<T> repository, string key, T value) where T : class =>
			_inner.Put(repository, key, value);
		public void Save<T>(DocumentEditor<T> editor) where T : class => _inner.Save(editor);
		public void Delete<T>(IPersistenceRepository<T> repository, DocumentSnapshot<T> observed) where T : class =>
			_inner.Delete(repository, observed);
		public void Require(ICommitPrecondition precondition) => _inner.Require(precondition);
		public async ValueTask<PersistenceResult<CommitReceipt>> CommitAsync(CancellationToken cancellationToken = default)
		{
			if (_provider._beforeNextCommit is Func<Task> beforeCommit)
			{
				_provider._beforeNextCommit = null;
				await beforeCommit();
			}
			if (_provider.TryConsumeFailure(out var code))
				return PersistenceResult<CommitReceipt>.Failure(new PersistenceError(
					code, "Injected commit failure."));
			var committed = await _inner.CommitAsync(cancellationToken);
			if (committed.Succeeded && _provider._afterNextSuccessfulCommit is Action callback)
			{
				_provider._afterNextSuccessfulCommit = null;
				callback();
			}
			return committed;
		}
		public ValueTask DisposeAsync() => _inner.DisposeAsync();
	}
}
