#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Infrastructure;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Kernel.Policies;
using Hexagon.V2.Networking;
using Hexagon.V2.Persistence;
using Hexagon.V2.Runtime;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;
using HL2RP.V2.Showcase.Combat;
using HL2RP.V2.Showcase.Restraint;
using HL2RP.V2.Showcase.Scanner;
using HL2RP.V2.World;
using HL2RP.UI;
using Sandbox;

namespace HL2RP.V2.Runtime;

/// <summary>
/// Host-only HL2RP composition root. Every command resolves the authenticated
/// RpcActor, uses committed repositories and publishes immutable state only after
/// the relevant service has completed successfully.
/// </summary>
public sealed class HL2RPHostApplication : IHexHostApplication, IWorldItemReconciliationBoundary,
	IHL2RPClientCommandRoutes<RpcActor>, IHL2RPSchemaCommandRoutes<RpcActor>, IHL2RPPresentationHost,
	IHL2RPCommandExecutionHost
{
	private const InventoryCapability CharacterCapabilities =
		InventoryCapability.View | InventoryCapability.Move | InventoryCapability.TransferIn |
		InventoryCapability.TransferOut | InventoryCapability.Use | InventoryCapability.Drop |
		InventoryCapability.Sell;
	private static readonly TimeSpan PresentationTargetPollInterval = TimeSpan.FromMilliseconds( 250 );
	private static readonly AccountId VerificationAccountId = new( 76_561_198_000_000_001UL );
	private static readonly ConnectionId VerificationConnectionId = new( DeterministicGuid( "hl2rp.verification.connection" ) );

	private readonly HexHostRuntimeContext _context;
	private readonly DomainRepositories _repositories;
	private readonly IHexClock _clock = new SystemHexClock();
	private readonly IAggregateIdGenerator _ids = new RandomAggregateIdGenerator();
	private readonly HL2RPCharacterModelCatalog _characterModels = new();
	private readonly HL2RPWorldModelCatalog _worldModels = new();
	private readonly InventoryAccessService _access = new();
	private readonly Dictionary<ConnectionId, ClientBinding> _clients = new();
	private readonly Dictionary<ConnectionId, AccountId> _entitlementQueries = new();
	private readonly Dictionary<SceneEntityId, HL2RPSceneFeatureComponent> _features = new();
	private readonly Dictionary<ItemId, GameObject> _worldObjects = new();
	private readonly Dictionary<ConnectionId, ItemActionPresentationEnvelope> _itemActionPresentations = new();
	private readonly AsyncOperationRegistry _lifecycleOperations = new();
	private readonly HL2RPCharacterLifecycleGate _characterLifecycle = new();
	private readonly HL2RPTimedActionOwnership<ConnectionId, ActiveRestraintAction> _activeRestraintActions = new();
	private readonly HL2RPTimedActionOwnership<ConnectionId, ActivePistolRaiseAction> _activePistolActions = new();
	private readonly HashSet<CharacterId> _respawningCharacters = new();
	private readonly HL2RPIncrementalLiveInventoryView _liveInventory = new();
	private readonly HL2RPIncrementalChatConnectionDirectory _chatPositions = new();
	private readonly HL2RPIncrementalChatAuthorityDirectory _chatAuthorities = new();
	private readonly ChatAdmissionService _chatAdmission = new();
	private readonly CanonicalCombatHealthDirectory _combatHealth = new()
	{
		Diagnostic = static message => Log.Info( message )
	};
	private readonly HL2RPIncrementalCombatPlayerTargetDirectory _combatTargets = new();
	private readonly HL2RPPresentationInvalidation _presentationInvalidation = new();
	private readonly HL2RPEntitlementPresentationInvalidation _entitlementPresentationInvalidation;
	private readonly HL2RPMaintenanceSupervisor _maintenance;
	private readonly HL2RPRecoverySnapshotLifecycle _recoverySnapshots;
	private readonly HL2RPPresentationSequence _itemPresentationSequence = new();
	private readonly HL2RPProjectionIndex _projectionIndex = new();
	private readonly HL2RPPresentationComposer _presentation;
	private HL2RPCommandExecution? _execution;
	private readonly HL2RPCivicSubjectSelections _civicSubjects = new();
	private readonly HL2RPExecutableItemActionCatalog _executableActions =
		HL2RPExecutableItemActionCatalog.CreateDefault();
	private readonly PostCommitEventBus<AdminAuditFact> _audit;
	private readonly HL2RPAccountEntitlementService _entitlements;
	private readonly CharacterService _characters;
	private readonly AggregateMutationService _aggregates;
	private readonly InventoryLayoutService _layout;
	private readonly InventoryMutationService _inventory;
	private readonly WorldItemService _worldItems;
	private readonly HL2RPWorldItemReconciler _worldReconciler;
	private readonly RestraintStateReader _restraintState;
	private readonly HL2RPFeatureRuntimePolicy _featureAuthorization;
	private readonly PolicyPipeline<HL2RPFeaturePolicyContext> _featurePolicy;
	private readonly ItemActionService _itemActions;
	private InteractionSessionService? _sessions;
	private InteractionAuthorityService? _interactions;
	private BagInteractionService? _bags;
	private TokenStackService? _tokens;
	private CombineLockService? _combineLocks;
	private DoorOwnershipService? _doorOwnership;
	private HL2RPSceneEntityBehaviorService? _sceneBehavior;
	private RequestDeviceService? _requests;
	private CivicService? _civic;
	private RecognitionService? _recognition;
	private CityObjectiveService? _objectives;
	private HL2RPObjectiveCommandRouter? _objectiveRouter;
	private RadioTuningService? _radio;
	private CommerceService? _commerce;
	private DocumentService? _documents;
	private PermitPurchaseService? _permitPurchases;
	private RestraintService? _restraints;
	private RestraintSearchService? _search;
	private ScannerPilotService? _scanner;
	private readonly ScannerRecoveryRetryPolicy _scannerRecoveryRetries = new();
	private PistolCombatService? _pistol;
	private CombatIntentService? _combatIntent;
	private HealthVialConsumeService? _healthVials;
	private CombatLifecycleService? _combatLifecycle;
	private ChatService? _chat;
	private HL2RPRadioRecipientResolver? _chatRecipients;
	private ChatRateLimit? _globalChatRateLimit;
	private bool _disposed;
	private bool _shutdownEvidenceCompleted;
	private bool _probeStarted;
	private HL2RPRecoverySnapshot? _pendingShutdownSnapshot;
	private VerificationActorBinding? _verificationActor;
	private HL2RPBootstrapOperatorDirectory _bootstrapOperators;
	private long _presentationRevision;
	private DateTimeOffset _nextPresentationTargetPollAtUtc = DateTimeOffset.MinValue;

	public HL2RPHostApplication( HexHostRuntimeContext context )
	{
		_context = context ?? throw new ArgumentNullException( nameof(context) );
		_repositories = context.Repositories;
		_recoverySnapshots = new HL2RPRecoverySnapshotLifecycle( IsVerification );
		_entitlementPresentationInvalidation = new HL2RPEntitlementPresentationInvalidation(
			_presentationInvalidation, EntitlementRecipients );
		_maintenance = new HL2RPMaintenanceSupervisor(
			TickAsync,
			failure => Log.Error(
				failure.Exception,
				$"HL2RP_MAINTENANCE_FAILED consecutive={failure.ConsecutiveFailures} retry_ms={failure.RetryDelay.TotalMilliseconds:0}" ) );
		_audit = new PostCommitEventBus<AdminAuditFact>( new[]
		{
			new EventHandlerRegistration<AdminAuditFact>(
				"hl2rp.runtime.audit_log", new HL2RPAuditLogHandler() )
		}, failure => Log.Error( failure.Exception,
			$"HL2RP audit handler '{failure.HandlerId}' failed after commit." ) );
		_bootstrapOperators = HL2RPBootstrapOperatorDirectory.Parse( string.Empty ).Value;
		var entitlementChanges = new PostCommitEventBus<HL2RPAccountEntitlementChanged>( new[]
		{
			new EventHandlerRegistration<HL2RPAccountEntitlementChanged>(
				"hl2rp.runtime.entitlement_refresh",
				new HL2RPEntitlementChangedHandler( change =>
					_entitlementPresentationInvalidation.Observe( change ) ) )
		}, failure => Log.Error( failure.Exception,
			$"HL2RP entitlement handler '{failure.HandlerId}' failed after commit." ) );
		_entitlements = new HL2RPAccountEntitlementService(
			context.Persistence,
			_clock,
			CanManageEntitlements,
			IsKnownAccount,
			entitlementChanges,
			_audit,
			IsVerification ? VerificationAccountId : null );
		var shapes = new SchemaItemShapeCatalog( context.Schema, _repositories );
		_layout = new InventoryLayoutService( shapes );
		var characterInventoryWidth = RequiredConfigurationInt( HL2RPIds.Configs.CharacterInventoryWidth );
		var characterInventoryHeight = RequiredConfigurationInt( HL2RPIds.Configs.CharacterInventoryHeight );
		_characters = new CharacterService(
			_repositories,
			context.Schema,
			_characterModels,
			new HL2RPCharacterStateFactory( _entitlements ),
			HL2RPInitializers.CreateDefault(),
			_ids,
			_clock,
			_layout,
			RequirePolicy<CharacterCreationContext>(),
			RequirePolicy<CharacterDeletionContext>(),
			inventoryWidth: characterInventoryWidth,
			inventoryHeight: characterInventoryHeight,
			nameUniqueness: RequiredNameUniqueness() );
		_aggregates = new AggregateMutationService(
			_repositories,
			context.Schema,
			RequirePolicy<CharacterMutationContext>(),
			RequirePolicy<ItemTraitMutationContext>() );
		_restraintState = new RestraintStateReader( _repositories );
		_inventory = new InventoryMutationService(
			_repositories, _access, _layout,
			RequirePolicy( new PolicyHandler<InventoryTransferContext>(
				"hl2rp.runtime.restrained_transfer", new HL2RPInventoryTransferRuntimePolicy( _restraintState ) ) ) );
		_worldItems = new WorldItemService(
			_repositories, context.Schema, _access, _layout, _worldModels,
			RequirePolicy( new PolicyHandler<WorldDropContext>(
				"hl2rp.runtime.restrained_drop", new HL2RPWorldDropRuntimePolicy( _restraintState ) ) ),
			RequirePolicy( new PolicyHandler<WorldPickupContext>(
				"hl2rp.runtime.authoritative_pickup", new HL2RPWorldPickupRuntimePolicy(
					_restraintState, ActorPosition, HasLineOfSight ) ) ) );
		_worldReconciler = new HL2RPWorldItemReconciler( this, _clock );
		_featureAuthorization = new HL2RPFeatureRuntimePolicy( _repositories );
		_featurePolicy = RequirePolicy( new PolicyHandler<HL2RPFeaturePolicyContext>(
			"hl2rp.runtime.authorization", _featureAuthorization ) );

		var actionHandlers = HL2RPNonCombatItemActions.Create( _clock ).ToList();
		var combatGate = new RestrainedActionGuard( _restraintState );
		actionHandlers.Add( new EquipCombatItemActionHandler( combatGate ) );
		actionHandlers.Add( new UnequipCombatItemActionHandler( combatGate ) );
		actionHandlers.Add( new ReloadPistolItemActionHandler( combatGate ) );
		actionHandlers.Add( new ReplenishPistolAmmunitionItemActionHandler( combatGate ) );
		var itemActionEvents = new PostCommitEventBus<ItemActionCommittedEvent>( new[]
		{
			new EventHandlerRegistration<ItemActionCommittedEvent>(
				"hl2rp.runtime.item_presentation", new HL2RPItemActionPresentationHandler( this ) )
		} );
		_itemActions = new ItemActionService(
			_repositories,
			context.Schema,
			_access,
			shapes,
			new ItemActionRegistry( actionHandlers ),
			RequirePolicy( new PolicyHandler<ItemActionContext>(
				"hl2rp.runtime.restrained", new RestrainedItemActionPolicy( _restraintState ) ) ),
			itemActionEvents );
		_presentation = new HL2RPPresentationComposer( new HL2RPPresentationComposerServices
		{
			Repositories = _repositories,
			Schema = context.Schema,
			Access = _access,
			Clock = _clock,
			Entitlements = _entitlements,
			EntitlementQueries = _entitlementQueries,
			ItemActionPresentations = _itemActionPresentations,
			ActiveRestraintActions = _activeRestraintActions,
			ActivePistolActions = _activePistolActions,
			ProjectionIndex = _projectionIndex,
			PresentationInvalidation = _presentationInvalidation,
			CivicSubjects = _civicSubjects,
			CombatHealth = _combatHealth,
			ExecutableActions = _executableActions,
			FeaturePolicy = _featurePolicy,
			FeatureAuthorization = _featureAuthorization,
			RestraintState = _restraintState,
			WorldModels = _worldModels,
			CanManageEntitlements = CanManageEntitlements,
			Sessions = () => _sessions,
			Scanner = () => _scanner,
			CombatLifecycle = () => _combatLifecycle
		}, this );
	}

	private bool IsVerification => !string.IsNullOrWhiteSpace( _context.VerificationProbe );

	public async ValueTask<OperationResult> InitializeAsync( CancellationToken cancellationToken = default )
	{
		if ( _disposed ) return OperationResult.Failure( ErrorCode.Conflict, "HL2RP host is disposed." );
		var sceneIdentitySystem = _context.Scene.GetSystem<PersistentSceneIdentityIndexSystem>();
		if ( sceneIdentitySystem is null )
			return OperationResult.Failure(
				ErrorCode.ConfigurationInvalid,
				"Persistent scene identity index system is unavailable for the host scene." );
		var sceneIdentities = sceneIdentitySystem.EnsureRuntimeReady();
		if ( sceneIdentities.Failed ) return Failure( sceneIdentities.Error! );
		var executableActions = _executableActions.Validate( _context.Schema );
		if ( executableActions.Failed ) return executableActions;
		foreach ( var path in _characterModels.ModelPaths )
			if ( !_worldModels.IsValidModel( path ) )
				return OperationResult.Failure( ErrorCode.ConfigurationInvalid, $"Character model '{path}' does not resolve." );
		var bootstrapOperatorSources = _context.Scene.GetAll<HL2RPBootstrapOperatorsComponent>().ToArray();
		if ( bootstrapOperatorSources.Length != 1 )
			return OperationResult.Failure( ErrorCode.ConfigurationInvalid,
				$"Expected exactly one HL2RP bootstrap-operator source, found {bootstrapOperatorSources.Length}." );
		var bootstrapOperators = bootstrapOperatorSources[0].Parse();
		if ( bootstrapOperators.Failed ) return Failure( bootstrapOperators.Error! );
		_bootstrapOperators = bootstrapOperators.Value;
		var entitlementDocuments = _entitlements.ValidateAll();
		if ( entitlementDocuments.Failed ) return entitlementDocuments;

		var components = new List<HL2RPSceneFeatureComponent>();
		foreach ( var component in _context.Scene.GetAll<HL2RPSceneFeatureComponent>() )
		{
			var identity = component.IdentityComponent;
			var featureAdmission = HL2RPSceneFeatureAdmission.Evaluate( identity?.RuntimeResolution );
			if ( featureAdmission.Failed ) return Failure( featureAdmission.Error! );
			if ( !featureAdmission.Value ) continue;
			if ( component.SceneEntityId is not SceneEntityId id )
				return OperationResult.Failure( ErrorCode.ConfigurationInvalid, "HL2RP scene feature has no persistent identity." );
			if ( !_features.TryAdd( id, component ) )
				return OperationResult.Failure( ErrorCode.DuplicateRegistration, $"HL2RP scene identity '{id}' is duplicated." );
			components.Add( component );
		}
		var initialized = await HL2RPSceneStateInitializer.InitializeAsync(
			_repositories, components, _ids, cancellationToken );
		if ( initialized.Failed ) return initialized;
		var sceneStateHandler = new HL2RPSceneStateEventHandler( _features );
		var appliedSceneState = sceneStateHandler.ApplyCommittedState( _repositories );
		if ( appliedSceneState.Failed ) return appliedSceneState;

		_sessions = new InteractionSessionService(
			_clock,
			TimeSpan.FromSeconds( RequiredConfigurationInt( HL2RPIds.Configs.InteractionIdleSeconds ) ),
			_ids.NewInteractionSessionId );
		_sessions.SessionRevoked += OnInteractionSessionRevoked;
		var restraintAuthorization = new RestraintPermissionAuthorizer( _repositories, _chatAuthorities );
		var directory = new HL2RPSceneDirectory(
			_repositories, _restraintState, restraintAuthorization );
		foreach ( var feature in components ) directory.Register( new HL2RPSceneInteractable( feature, _repositories ) );
		if ( IsVerification )
		{
			_access.OpenConnection( VerificationConnectionId );
			_verificationActor = new VerificationActorBinding(
				VerificationConnectionId, VerificationAccountId, null, null );
		}
		var interactionWorld = new HL2RPServerInteractionWorld(
			_context.Scene, ResolveInteractionActorState, FindPlayer, _features, _restraintState );
		_interactions = new InteractionAuthorityService(
			interactionWorld,
			directory,
			_sessions,
			_access,
			RequirePolicy<ServerInteractionContext>(),
			_clock,
			_ids.NewInteractionSessionId );
		var sessionResolver = new AuthoritativeSceneSessionResolver( _sessions, _interactions );
		var doorStateEvents = new PostCommitEventBus<DoorStateChangedEvent>( new[]
		{
			new EventHandlerRegistration<DoorStateChangedEvent>(
				"hl2rp.runtime.door_scene_state", sceneStateHandler )
		}, failure => Log.Error( failure.Exception,
			$"HL2RP door scene-state handler '{failure.HandlerId}' failed after commit." ) );
		var forcefieldStateEvents = new PostCommitEventBus<ForcefieldStateChangedEvent>( new[]
		{
			new EventHandlerRegistration<ForcefieldStateChangedEvent>(
				"hl2rp.runtime.forcefield_scene_state", sceneStateHandler )
		}, failure => Log.Error( failure.Exception,
			$"HL2RP forcefield scene-state handler '{failure.HandlerId}' failed after commit." ) );
		_sceneBehavior = new HL2RPSceneEntityBehaviorService(
			_repositories, sessionResolver, _clock, _featurePolicy,
			doorStateEvents, forcefieldStateEvents, _audit );
		_bags = new BagInteractionService( _repositories, _sessions, _access, _featurePolicy );
		_tokens = new TokenStackService( _repositories, _access, _ids, _layout, _clock, _featurePolicy, audit: _audit );
		_combineLocks = new CombineLockService( _repositories, _access, sessionResolver, _clock, _featurePolicy, audit: _audit );
		_doorOwnership = new DoorOwnershipService( _repositories, sessionResolver, _clock, _featurePolicy, audit: _audit );
		_chat = BuildChat();
		var requestEvents = new PostCommitEventBus<RequestFact>( new[]
		{
			new EventHandlerRegistration<RequestFact>(
				"hl2rp.runtime.request_chat_delivery",
				new RequestChatDeliveryHandler(
					_chatRecipients!, _liveInventory, new HL2RPCommittedChatSink( this ) ) )
		}, failure => Log.Error( failure.Exception,
			$"HL2RP request delivery handler '{failure.HandlerId}' failed after commit." ) );
		_requests = new RequestDeviceService(
			_repositories,
			_access,
			_clock,
			_featurePolicy,
			requestEvents,
			_audit,
			_chatAdmission,
			_globalChatRateLimit,
			RequestDeviceService.RequestChannelRateLimit );
		_civic = new CivicService( _repositories, _clock, _featurePolicy, audit: _audit );
		_recognition = new RecognitionService(
			_repositories, _clock, new HL2RPEncounterAuthorizer( this ), _featurePolicy, audit: _audit );
		_objectives = new CityObjectiveService( _repositories, _clock, _featurePolicy, audit: _audit );
		_objectiveRouter = new HL2RPObjectiveCommandRouter(
			_objectives,
			() => _projectionIndex.FirstSceneEntity( "city" )?.Id,
			Guid.NewGuid );
		_radio = new RadioTuningService( _repositories, _access, _clock, _featurePolicy, audit: _audit );
		_commerce = new CommerceService(
			_repositories, _context.Schema, _access, sessionResolver, _ids, _layout,
			new HL2RPItemFactory(), _clock, _featurePolicy, audit: _audit );
		_documents = new DocumentService(
			_repositories, _access, _ids, _layout, _clock, _featurePolicy, audit: _audit );
		_permitPurchases = new PermitPurchaseService(
			_repositories, _access, _ids, _layout, _clock, _featurePolicy, audit: _audit );
		_restraints = new RestraintService( _repositories, _access, _layout, _interactions, _clock );
		_search = new RestraintSearchService(
			_repositories, _interactions, _access, _restraintState, restraintAuthorization );

		var boundaries = new HL2RPSandboxBoundaries(
			_context.Scene, _features, ResolveActorState, _repositories, ResolveCombatTargetToken,
			RefreshLiveConnection );
		_scanner = new ScannerPilotService(
			_repositories, _interactions, _sessions, _clock,
			boundaries, boundaries, boundaries, boundaries, boundaries,
			new HL2RPScannerCommitSink( this ) );
		var reconciledScanners = await _scanner.ReconcilePersistedPilotsAsync( cancellationToken );
		if ( reconciledScanners.Failed ) return reconciledScanners;
		_combatLifecycle = new CombatLifecycleService(
			_repositories, _context.Schema, _layout, _worldModels, _clock,
			new HL2RPCombatLifecycleBoundary( this ) );
		var playerDamage = new PlayerCombatDamageBoundary( _repositories, _combatTargets, _combatHealth );
		_pistol = new PistolCombatService(
			_repositories, _access, _clock, boundaries,
			new CompositeCombatDamageBoundary( playerDamage, boundaries ),
			new RestrainedActionGuard( _restraintState ) );
		_combatIntent = new CombatIntentService(
			_pistol, playerDamage, _combatLifecycle, _combatHealth, new SystemCombatIntentDelay(), _clock );
		_healthVials = new HealthVialConsumeService( _repositories, _access, _layout, _combatHealth );
		var reconciledPistols = await _pistol.ReconcileRaisedPistolsAsync( cancellationToken );
		if ( reconciledPistols.Failed ) return Failure( reconciledPistols.Error! );
		if ( reconciledPistols.Value.Commit is not null )
			_projectionIndex.Apply( reconciledPistols.Value.Commit, _repositories );
		RebuildProjectionIndex();
		RebuildLiveInventory();
		RefreshAllLiveConnections();
		var reconciledWorldItems = await _worldReconciler.ReconcileStartupAsync(
			_worldItems.LoadWorldItems().Select( world => world.ItemId ),
			cancellationToken );
		if ( reconciledWorldItems.Failed ) return reconciledWorldItems;
		var invariants = new DomainInvariantValidator(
			_repositories,
			_context.Schema,
			new SchemaItemShapeCatalog( _context.Schema, _repositories ),
			HL2RPPersistenceInvariants.Profile ).Validate();
		if ( !invariants.IsValid )
			return OperationResult.Failure( invariants.Issues[0].Code, invariants.Issues[0].Message );
		_execution = new HL2RPCommandExecution( new HL2RPCommandExecutionServices
		{
			Repositories = _repositories,
			Clock = _clock,
			Provider = _context.Persistence,
			Audit = _audit,
			Entitlements = _entitlements,
			EntitlementQueries = _entitlementQueries,
			EntitlementPresentationInvalidation = _entitlementPresentationInvalidation,
			CivicSubjects = _civicSubjects,
			ProjectionIndex = _projectionIndex,
			ActiveRestraintActions = _activeRestraintActions,
			ActivePistolActions = _activePistolActions,
			ExecutableActions = _executableActions,
			WorldReconciler = _worldReconciler,
			Inventory = _inventory,
			WorldItems = _worldItems,
			ItemActions = _itemActions,
			Chat = _chat,
			Interactions = _interactions,
			Sessions = _sessions,
			Bags = _bags,
			Tokens = _tokens,
			CombineLocks = _combineLocks,
			DoorOwnership = _doorOwnership,
			SceneBehavior = _sceneBehavior,
			Requests = _requests,
			Civic = _civic,
			Recognition = _recognition,
			ObjectiveRouter = _objectiveRouter,
			Radio = _radio,
			Commerce = _commerce,
			Documents = _documents,
			PermitPurchases = _permitPurchases,
			Restraints = _restraints,
			Search = _search,
			Scanner = _scanner,
			Pistol = _pistol,
			CombatIntent = _combatIntent,
			HealthVials = _healthVials,
			MainInventory = MainInventory,
			CurrentSession = CurrentSession,
			CanManageEntitlements = CanManageEntitlements,
			IsKnownAccount = IsKnownAccount,
			Warn = static message => Log.Warning( message ),
			Fail = static message => Log.Error( message ),
			Report = static ( exception, message ) => Log.Error( exception, message )
		}, this );
		_recoverySnapshots.CompleteInitialization(
			CaptureRecoverySnapshot,
			static marker => Log.Info( marker ) );
		return OperationResult.Success();
	}

	public void Connected( RpcActor actor )
	{
		if ( _disposed ) return;
		var connectionId = new ConnectionId( actor.Connection.Id );
		_characterLifecycle.Open( connectionId );
		_access.OpenConnection( connectionId );
		_clients[connectionId] = new ClientBinding(
			actor.Connection, actor.AccountId, actor.Player, null );
		_projectionIndex.ObserveConnection( connectionId, actor.AccountId );
		_projectionIndex.InvalidateRuntimeDependency( "roster-membership", "global" );
		SendCharacterList( actor.Connection, actor.AccountId );
		PublishAll();
	}

	public void Disconnected( RpcActor actor )
	{
		var connectionId = new ConnectionId( actor.Connection.Id );
		_characterLifecycle.Close( connectionId );
		_access.RevokeConnection( connectionId );
		if ( !_clients.Remove( connectionId, out var binding ) ) return;
		_projectionIndex.ForgetConnection( connectionId );
		_presentationInvalidation.ForgetConnection( connectionId );
		_itemActionPresentations.Remove( connectionId );
		_civicSubjects.ClearConnection( connectionId );
		_entitlementQueries.Remove( connectionId );
		CancelTimedActionsForLifecycle( connectionId, binding.CharacterId );
		if ( binding.CharacterId is CharacterId characterId )
		{
			TrackLifecycle(
				$"pistol-disconnect:{connectionId.Value:D}",
				() => ClearRaisedPistolsForLifecycleAsync(
					new InventoryActor( connectionId, binding.AccountId, characterId ), CancellationToken.None ) );
			ObserveCharacterExit( connectionId, characterId );
		}
		_interactions?.Disconnected( connectionId );
		_chat?.RevokeConnection( connectionId );
		_requests?.RevokeConnection( connectionId );
		if ( _scanner is not null )
			TrackLifecycle(
				$"scanner-disconnect:{connectionId.Value:D}",
				() => _scanner.DisconnectAsync( connectionId ).AsTask() );
		_ = binding.Player.HostDisembody();
		_projectionIndex.InvalidateRuntimeDependency( "roster-membership", "global" );
		RemoveLiveConnection( connectionId );
		PublishAll();
	}

	public CharacterRecord? FindActiveCharacter( ConnectionId connectionId ) =>
		_clients.TryGetValue( connectionId, out var binding ) && binding.CharacterId is CharacterId characterId
			? _repositories.Characters.Find( DomainKeys.Character( characterId ) )?.Value
			: null;

	public async ValueTask<OperationResult> HandleCommandAsync(
		RpcActor actor,
		ClientCommand command,
		CancellationToken cancellationToken = default )
	{
		var connectionId = new ConnectionId( actor.Connection.Id );
		// Disposal gate and cross-account binding guard live in the neutral sink so the
		// test suite executes them; this adapter contributes only the presentation
		// bookkeeping around the routed command.
		var bound = _clients.TryGetValue( connectionId, out var binding );
		var admitted = HL2RPClientCommandSink.Admit(
			_disposed, bound, bound ? binding!.AccountId : default, actor.AccountId );
		if ( admitted.Failed ) return admitted;
		var projectionDelta = new CommandProjectionDelta();
		var characterBefore = binding!.CharacterId;
		var mainBefore = characterBefore is CharacterId activeBefore ? MainInventory( activeBefore )?.Id : null;
		var restraintTieBefore = command is RunSchemaCommandCommand { CommandId: HL2RPIds.Commands.RestraintSet } &&
			mainBefore is InventoryId restraintInventory
			? _repositories.Inventories.Find( DomainKeys.Inventory( restraintInventory ) )?.Value.Placements
				.Select( placement => _repositories.Items.Find( DomainKeys.Item( placement.ItemId ) )?.Value )
				.FirstOrDefault( item => item?.Definition.Value == HL2RPIds.Items.ZipTie )?.Id
			: null;

		OperationResult result = await HL2RPClientCommandSink.RouteAsync<RpcActor>(
			this, actor, command, projectionDelta, cancellationToken );

		var characterAfter = _clients.TryGetValue( connectionId, out var afterBinding )
			? afterBinding.CharacterId : null;
		var durableMutation = result.Succeeded && projectionDelta.Receipts.Any(
			receipt => receipt.Documents.Count > 0 );
		var outcome = HL2RPPresentationPlanner.Outcome(
			connectionId, command, result,
			durableMutation,
			characterBefore != characterAfter );
		var changes = EnrichChanges(
			connectionId, command, outcome.Changes, projectionDelta, mainBefore, restraintTieBefore );
		PublishChanges( changes, projectionDelta.Receipts );
		return outcome.Result;
	}

	OperationResult IHL2RPClientCommandRoutes<RpcActor>.ListCharacters( RpcActor actor ) => ListCharacters( actor );

	ValueTask<OperationResult> IHL2RPClientCommandRoutes<RpcActor>.CreateAsync(
		RpcActor actor, CreateCharacterCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		CreateAsync( actor, command, projectionDelta, cancellationToken );

	ValueTask<OperationResult> IHL2RPClientCommandRoutes<RpcActor>.LoadAsync(
		RpcActor actor, CharacterId characterId, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		LoadAsync( actor, characterId, projectionDelta, cancellationToken );

	ValueTask<OperationResult> IHL2RPClientCommandRoutes<RpcActor>.DeleteAsync(
		RpcActor actor, CharacterId characterId, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		DeleteAsync( actor, characterId, projectionDelta, cancellationToken );

	ValueTask<OperationResult> IHL2RPClientCommandRoutes<RpcActor>.UnloadAsync(
		RpcActor actor, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		UnloadAsync( actor, projectionDelta, cancellationToken );

	ValueTask<OperationResult> IHL2RPClientCommandRoutes<RpcActor>.MoveAsync(
		RpcActor actor, MoveInventoryItemCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		MoveAsync( actor, command, projectionDelta, cancellationToken );

	ValueTask<OperationResult> IHL2RPClientCommandRoutes<RpcActor>.RunItemActionAsync(
		RpcActor actor, RunItemActionCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		RunItemActionAsync( actor, command, projectionDelta, cancellationToken );

	ValueTask<OperationResult> IHL2RPClientCommandRoutes<RpcActor>.DropAsync(
		RpcActor actor, DropItemCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		DropAsync( actor, command, projectionDelta, cancellationToken );

	ValueTask<OperationResult> IHL2RPClientCommandRoutes<RpcActor>.PickupAsync(
		RpcActor actor, PickUpItemCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		PickupAsync( actor, command, projectionDelta, cancellationToken );

	OperationResult IHL2RPClientCommandRoutes<RpcActor>.SendChat( RpcActor actor, SendChatCommand command ) =>
		SendChat( actor, command );

	OperationResult IHL2RPClientCommandRoutes<RpcActor>.CancelAction( RpcActor actor, Guid instanceId ) =>
		CancelAction( actor, instanceId );

	ValueTask<OperationResult> IHL2RPClientCommandRoutes<RpcActor>.BeginInteractionAsync(
		RpcActor actor, InteractionTargetInput target, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		BeginInteractionAsync( actor, target, projectionDelta, cancellationToken );

	OperationResult IHL2RPClientCommandRoutes<RpcActor>.ContinueInteraction( RpcActor actor, ContinueInteractionCommand command ) =>
		ContinueInteraction( actor, command );

	OperationResult IHL2RPClientCommandRoutes<RpcActor>.CloseInteraction( RpcActor actor, InteractionSessionId sessionId ) =>
		CloseInteraction( actor, sessionId );

	ValueTask<OperationResult> IHL2RPClientCommandRoutes<RpcActor>.RunSchemaCommandAsync(
		RpcActor actor, RunSchemaCommandCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		RunSchemaCommandAsync( actor, command, projectionDelta, cancellationToken );

	public void RequestMaintenanceTick() => _maintenance.RequestTick();

	internal HL2RPMaintenanceStatus MaintenanceStatus => _maintenance.Status;

	private async ValueTask TickAsync( CancellationToken cancellationToken )
	{
		if ( _disposed ) return;
		_entitlementPresentationInvalidation.MaterializePending();
		RefreshAllLiveConnections();
		_interactions?.RevalidateActiveSessions();
		_sessions?.RevokeExpired();
		var now = _clock.UtcNow;
		if ( now >= _nextPresentationTargetPollAtUtc )
		{
			_nextPresentationTargetPollAtUtc = now + PresentationTargetPollInterval;
			ObserveRestraintTargets();
		}
		if ( _presentationInvalidation.IsRefreshDue( now ) ) PublishDuePresentation( now );
		foreach ( var receipt in await _worldReconciler.ReconcileDueAsync( cancellationToken ) )
			LogWorldItemReconciliation( receipt, "maintenance" );
		if ( _scanner is not null )
		{
			// The recovery handles are fail-closed cleanup failures that previously had no
			// runtime consumer and wedged the scanner until restart; the policy paces
			// their retries and permanently skips the deliberate StorageLimit non-retry.
			foreach ( var recovery in _scannerRecoveryRetries.SelectDue( _scanner.RecoveryHandles, now ) )
			{
				var retried = await _scanner.RetryCleanupAsync( recovery, cancellationToken );
				if ( retried.Succeeded )
					Log.Info(
						$"HL2RP_SCANNER_RECOVERY_HEALED session={recovery.SessionId.Value:D} " +
						$"scanner={recovery.ScannerId.Value:D} attempts={recovery.Attempts}" );
				else
					Log.Warning(
						$"HL2RP_SCANNER_RECOVERY_RETRY_FAILED session={recovery.SessionId.Value:D} " +
						$"scanner={recovery.ScannerId.Value:D} message={retried.Error!.Message}" );
			}
		}
		if ( IsVerification && !_probeStarted )
		{
			cancellationToken.ThrowIfCancellationRequested();
			if ( _verificationActor is null ) return;
			_probeStarted = true;
			await RunVerificationProbeAsync();
		}
	}

	public async ValueTask DisposeAsync()
	{
		if ( _disposed ) return;
		_disposed = true;
		var failures = new List<Exception>();

		try { await _maintenance.DisposeAsync(); }
		catch ( Exception exception ) { RecordShutdownFailure( failures, "maintenance", exception ); }

		try
		{
			if ( _sessions is not null ) _sessions.SessionRevoked -= OnInteractionSessionRevoked;
			CancelAllTimedActionsForLifecycle();
			if ( _scanner is not null )
				foreach ( var connectionId in _clients.Keys.ToArray() )
					TrackLifecycle(
						$"scanner-shutdown:{connectionId.Value:D}",
						() => _scanner.DisconnectAsync( connectionId ).AsTask() );
			foreach ( var connectionId in _clients.Keys.ToArray() )
				_interactions?.Disconnected( connectionId );
		}
		catch ( Exception exception ) { RecordShutdownFailure( failures, "session-revocation", exception ); }
		finally { _lifecycleOperations.StopAdmission(); }

		try
		{
			var lifecycleDrain = await _lifecycleOperations.DrainAsync();
			foreach ( var failure in lifecycleDrain.Failures )
				RecordShutdownFailure(
					failures,
					$"lifecycle:{failure.Name}",
					failure.Exception );
		}
		catch ( Exception exception ) { RecordShutdownFailure( failures, "lifecycle-drain", exception ); }

		try
		{
			if ( _scanner is not null )
			{
				var scannerDrain = await _scanner.DrainCleanupAsync();
				foreach ( var recovery in scannerDrain.RecoveryHandles )
					RecordShutdownFailure(
						failures,
						$"scanner-cleanup:{recovery.SessionId.Value:D}",
						new InvalidOperationException(
							$"Scanner '{recovery.ScannerId.Value:D}' cleanup remains unresolved after " +
							$"{recovery.Attempts} attempts: {recovery.LastError.Message}" ) );
				foreach ( var flush in scannerDrain.InputFlushFailures )
					RecordShutdownFailure(
						failures,
						$"scanner-input-flush:{flush.SessionId.Value:D}",
						new InvalidOperationException(
							$"Scanner '{flush.ScannerId.Value:D}' input sequence {flush.Sequence} was not flushed: " +
							flush.Error.Message ) );
			}
		}
		catch ( Exception exception ) { RecordShutdownFailure( failures, "scanner", exception ); }

		try
		{
			if ( _pistol is not null )
			{
				var reconciledPistols = await _pistol.ReconcileRaisedPistolsAsync();
				if ( reconciledPistols.Failed )
					throw new InvalidOperationException( reconciledPistols.Error!.Message );
				if ( reconciledPistols.Value.Commit is not null )
					_projectionIndex.Apply( reconciledPistols.Value.Commit, _repositories );
			}
		}
		catch ( Exception exception ) { RecordShutdownFailure( failures, "pistol", exception ); }

		try
		{
			var worldDrain = await _worldReconciler.DrainAsync();
			foreach ( var receipt in worldDrain.Attempts ) LogWorldItemReconciliation( receipt, "shutdown" );
			if ( !worldDrain.IsClean )
				throw new InvalidOperationException(
					$"World-item reconciliation has {worldDrain.PendingItems.Count} unresolved desired states." );
		}
		catch ( Exception exception ) { RecordShutdownFailure( failures, "world-reconciliation", exception ); }

		try
		{
			foreach ( var entry in _worldObjects.ToArray() )
			{
				try
				{
					if ( entry.Value.IsValid() ) entry.Value.Destroy();
					_worldObjects.Remove( entry.Key );
				}
				catch ( Exception exception )
				{
					RecordShutdownFailure( failures, $"world-object:{entry.Key.Value:D}", exception );
				}
			}
		}
		catch ( Exception exception ) { RecordShutdownFailure( failures, "world-cleanup", exception ); }

		try
		{
			foreach ( var binding in _clients.Values )
			{
				try
				{
					var stripped = binding.Player.HostDisembody();
					if ( stripped.Failed ) throw new InvalidOperationException( stripped.Error!.Message );
				}
				catch ( Exception exception )
				{
					RecordShutdownFailure(
						failures,
						$"body:{binding.Connection.Id}",
						exception );
				}
			}
			try
			{
				if ( _verificationActor?.Body is GameObject verificationBody && verificationBody.IsValid() )
					verificationBody.Destroy();
			}
			catch ( Exception exception )
			{
				RecordShutdownFailure( failures, "verification-body", exception );
			}
		}
		catch ( Exception exception ) { RecordShutdownFailure( failures, "body-cleanup", exception ); }
		finally
		{
			foreach ( var connectionId in _clients.Keys )
			{
				_characterLifecycle.Close( connectionId );
				_access.RevokeConnection( connectionId );
			}
			if ( IsVerification ) _access.RevokeConnection( VerificationConnectionId );
			_verificationActor = null;
			_entitlementQueries.Clear();
			_clients.Clear();
		}

		if ( failures.Count == 0 )
		{
			try { _pendingShutdownSnapshot = CaptureRecoverySnapshot(); }
			catch ( Exception exception ) { RecordShutdownFailure( failures, "recovery-snapshot", exception ); }
		}

		if ( failures.Count > 0 )
			throw new AggregateException( "HL2RP host did not quiesce cleanly.", failures );
	}

	public OperationResult CompleteQuiescedShutdown( PersistenceShutdownResult persistenceShutdown )
	{
		ArgumentNullException.ThrowIfNull( persistenceShutdown );
		if ( _shutdownEvidenceCompleted ) return OperationResult.Success();
		if ( !_disposed || _pendingShutdownSnapshot is null )
			return OperationResult.Failure(
				ErrorCode.Conflict,
				"HL2RP shutdown evidence was requested before application cleanup completed." );
		if ( !persistenceShutdown.IsClean )
			return OperationResult.Failure(
				ErrorCode.InternalError,
				"HL2RP shutdown evidence requires a clean persistence shutdown." );
		try
		{
			HL2RPRecoverySnapshot snapshot = _pendingShutdownSnapshot;
			var completed = _recoverySnapshots.CompleteAfterPersistence(
				persistenceShutdown,
				snapshot,
				static marker => Log.Info( marker ) );
			if ( completed.Failed ) return completed;
			_pendingShutdownSnapshot = null;
			_shutdownEvidenceCompleted = true;
			return OperationResult.Success();
		}
		catch ( Exception exception )
		{
			return OperationResult.Failure(
				ErrorCode.InternalError,
				$"HL2RP shutdown evidence could not be emitted: {exception.Message}" );
		}
	}

	private HL2RPRecoverySnapshot CaptureRecoverySnapshot() => new(
		_context.Persistence.Health.Sequence,
		HL2RPRuntimeProjection.RecoveryDigest(
			_repositories,
			_context.Configuration.Snapshot() ) );

	private void TrackLifecycle( string name, Func<Task> operation )
	{
		if ( !_lifecycleOperations.TryStartTask( name, operation ) )
			Log.Warning( $"HL2RP lifecycle operation '{name}' was rejected because shutdown admission is closed." );
	}

	private static void RecordShutdownFailure(
		ICollection<Exception> failures,
		string stage,
		Exception exception )
	{
		failures.Add( new InvalidOperationException( $"HL2RP shutdown stage '{stage}' failed.", exception ) );
		Log.Error( exception, $"HL2RP_SHUTDOWN_STAGE_FAILED stage={stage}" );
	}

	private void OnInteractionSessionRevoked( InteractionSession session )
	{
		if ( _disposed ) return;
		_projectionIndex.RemoveSceneSession( session.Id );
		_projectionIndex.InvalidateVisibility( session.ConnectionId );
		_presentationInvalidation.Invalidate( session.ConnectionId );
	}

	private void PresentItemAction( ItemActionCommittedEvent committed )
	{
		if ( _disposed || committed.Presentation is null ||
			!_clients.TryGetValue( committed.Actor.ConnectionId, out var binding ) ||
			binding.AccountId != committed.Actor.AccountId || binding.CharacterId != committed.Actor.CharacterId ) return;
		_itemActionPresentations[committed.Actor.ConnectionId] = new ItemActionPresentationEnvelope(
			committed.Actor.CharacterId, _itemPresentationSequence.Next(), committed.Presentation );
	}

	private void ObserveRestraintTargets()
	{
		foreach ( var pair in _clients )
		{
			var character = FindActiveCharacter( pair.Key );
			if ( character is null ) continue;
			_presentationInvalidation.ObserveRestraintTarget(
				pair.Key, NearestCharacterTarget( character.Id )?.Id );
		}
	}

	private OperationResult ListCharacters( RpcActor actor )
	{
		SendCharacterList( actor.Connection, actor.AccountId );
		return OperationResult.Success();
	}

	private async ValueTask<OperationResult> CreateAsync(
		RpcActor actor,
		CreateCharacterCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var request = HL2RPRuntimeProjection.ToCreationRequest( _context.Schema, command.Input );
		if ( request.Failed ) return Failure( request.Error! );
		var created = await _characters.CreateAsync( actor.AccountId, request.Value, cancellationToken );
		if ( created.Failed ) return Failure( created.Error! );
		projectionDelta.Documents.Add( new DocumentAddress(
			DomainCollections.Characters, DomainKeys.Character( created.Value.Character.Id ) ) );
		projectionDelta.Inventories.UnionWith( created.Value.Inventories.Select( inventory => inventory.Id ) );
		projectionDelta.Items.UnionWith( created.Value.Items.Select( item => item.Id ) );
		projectionDelta.Documents.UnionWith( created.Value.Commit.Documents.Select( document => document.Address ) );
		projectionDelta.Observe( created.Value.Commit );
		SendCharacterList( actor.Connection, actor.AccountId );
		return OperationResult.Success();
	}

	private async ValueTask<OperationResult> LoadAsync(
		RpcActor actor,
		CharacterId characterId,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var connectionId = new ConnectionId( actor.Connection.Id );
		if ( !_characterLifecycle.TryBeginOperation(
			connectionId, characterId, out var lifecycleEpoch, out var lifecycleOperation ) )
			return OperationResult.Failure(
				ErrorCode.Conflict,
				"Character lifecycle work is already in progress for this connection or character." );
		using var reservedLifecycle = lifecycleOperation!;
		var document = _repositories.Characters.Find( DomainKeys.Character( characterId ) );
		if ( document is null || document.Value.AccountId != actor.AccountId )
			return OperationResult.Failure( ErrorCode.NotFound, "Character was not found for this account." );
		if ( document.Value.IsBanActive( _clock.UtcNow ) )
			return OperationResult.Failure( ErrorCode.PolicyDenied, "Character is banned." );
		var modelPath = _characterModels.ResolvePath( document.Value.Model );
		if ( modelPath.Failed || !_worldModels.IsValidModel( modelPath.Value ) )
			return OperationResult.Failure( ErrorCode.ConfigurationInvalid, "Character model does not resolve." );
		var main = MainInventory( characterId );
		if ( main is null ) return OperationResult.Failure( ErrorCode.NotFound, "Character main inventory was not found." );
		var admission = HL2RPCharacterLoadAdmission.Validate(
			connectionId,
			characterId,
			_clients.Select( pair => new HL2RPActiveCharacterBinding(
				pair.Key, pair.Value.CharacterId ) ) );
		if ( admission.Failed ) return admission;
		var previous = FindActiveCharacter( connectionId );
		var preparedTouch = _aggregates.PrepareTouchLastPlayed(
			actor.AccountId, characterId, _clock.UtcNow );
		if ( preparedTouch.Failed ) return Failure( preparedTouch.Error! );

		PreparedPistolLifecycleClear? preparedPistols = null;
		if ( previous is not null )
		{
			var prepared = _pistol!.PrepareCharacterClear( previous.Id );
			if ( prepared.Failed ) return Failure( prepared.Error! );
			preparedPistols = prepared.Value;
		}

		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		var stagedTouch = _aggregates.StageTouchLastPlayed( unitOfWork, preparedTouch.Value );
		if ( stagedTouch.Failed )
		{
			await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
			return Failure( stagedTouch.Error! );
		}
		if ( preparedPistols is not null )
		{
			var stagedPistols = _pistol!.StageCharacterClear( unitOfWork, preparedPistols );
			if ( stagedPistols.Failed )
			{
				await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
				return Failure( stagedPistols.Error! );
			}
		}
		if ( !_characterLifecycle.IsCurrent( connectionId, lifecycleEpoch ) )
		{
			await HL2RPUnitOfWork.DisposeAsync( unitOfWork );
			return OperationResult.Failure( ErrorCode.Conflict, "Character load was superseded by a later lifecycle request." );
		}
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unitOfWork, cancellationToken );
		if ( !committed.Succeeded ) return CombatPersistence.Failure( committed.Error! );

		projectionDelta.Observe( committed.Value! );
		projectionDelta.Connections.Add( connectionId );
		projectionDelta.Characters.Add( characterId );
		var completedTouch = _aggregates.CompleteTouchLastPlayed( preparedTouch.Value, committed.Value! );
		if ( completedTouch.Failed )
			Log.Error(
				$"HL2RP_LOAD_DEGRADED character={characterId.Value:D} stage=last_played_completion " +
				$"message={completedTouch.Error!.Message}" );
		if ( preparedPistols is not null )
		{
			var completedPistols = _pistol!.CompleteCharacterClear( preparedPistols, committed.Value );
			if ( completedPistols.Failed )
			{
				_combatIntent?.ClearCharacter( previous!.Id );
				Log.Error(
					$"HL2RP_LOAD_DEGRADED character={previous!.Id.Value:D} stage=pistol_completion " +
					$"message={completedPistols.Error!.Message}" );
			}
			else
			{
				projectionDelta.Items.UnionWith( completedPistols.Value.ChangedPistols );
				projectionDelta.Characters.Add( previous!.Id );
				var previousMain = MainInventory( previous.Id );
				if ( previousMain is not null ) projectionDelta.Inventories.Add( previousMain.Id );
			}
		}

		if ( !_characterLifecycle.IsCurrent( connectionId, lifecycleEpoch ) ||
			!_clients.TryGetValue( connectionId, out var currentBinding ) ||
			currentBinding.AccountId != actor.AccountId ||
			!ReferenceEquals( currentBinding.Player, actor.Player ) ||
			currentBinding.CharacterId != previous?.Id )
		{
			Log.Info(
				$"HL2RP_LOAD_BINDING_CHANGED_AFTER_COMMIT connection={connectionId.Value:D} " +
				$"character={characterId.Value:D} sequence={committed.Value!.Sequence}" );
			return OperationResult.Failure(
				ErrorCode.Conflict, "Character load was superseded after its durable preparation committed." );
		}
		var finalAdmission = HL2RPCharacterLoadAdmission.Validate(
			connectionId,
			characterId,
			_clients.Select( pair => new HL2RPActiveCharacterBinding(
				pair.Key, pair.Value.CharacterId ) ) );
		if ( finalAdmission.Failed )
		{
			Log.Info(
				$"HL2RP_LOAD_TARGET_CLAIMED_AFTER_COMMIT connection={connectionId.Value:D} " +
				$"character={characterId.Value:D} sequence={committed.Value!.Sequence}" );
			return finalAdmission;
		}

		var activatedBody = actor.Player.HostEmbody( candidate =>
		{
			ConfigureAuthoritativePlayerBody( candidate, document.Value );
			var bodyObject = candidate.Children.FirstOrDefault( child => child.Name == "Body" )
				?? new GameObject( candidate, true, "Body" );
			var renderer = bodyObject.GetOrAddComponent<SkinnedModelRenderer>();
			renderer.Model = Model.Load( modelPath.Value );
			renderer.Enabled = true;
		} );
		if ( activatedBody.Failed )
		{
			Log.Error(
				$"HL2RP_LOAD_BODY_ACTIVATION_FAILED connection={connectionId.Value:D} " +
				$"character={characterId.Value:D} sequence={committed.Value!.Sequence} " +
				$"message={activatedBody.Error!.Message}" );
			return Failure( activatedBody.Error );
		}
		_itemActionPresentations.Remove( connectionId );
		_civicSubjects.ClearConnection( connectionId );
		// The inline switch path is a lifecycle exit for the previous character and must
		// clean the same transient combat state as unload/disconnect, or reloading the
		// previous character later adopts its stale damage and death lifecycle.
		if ( previous is not null ) ObserveCharacterExit( connectionId, previous.Id );
		_access.Grant( new InventoryGrant
		{
			ConnectionId = connectionId,
			CharacterId = characterId,
			InventoryId = main.Id,
			Capabilities = CharacterCapabilities,
			Kind = InventoryGrantKind.Character
		} );
		SetBindingCharacter( connectionId, characterId );
		if ( _combatHealth.Require( characterId ).Failed ) _combatHealth.Publish( characterId, 100, 100 );
		return OperationResult.Success();
	}

	private async ValueTask<OperationResult> DeleteAsync(
		RpcActor actor,
		CharacterId characterId,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var connectionId = new ConnectionId( actor.Connection.Id );
		if ( !_characterLifecycle.TryBeginOperation(
			connectionId, characterId, out var lifecycleEpoch, out var lifecycleOperation ) )
			return OperationResult.Failure(
				ErrorCode.Conflict,
				"Character lifecycle work is already in progress for this connection or character." );
		using var reservedLifecycle = lifecycleOperation!;
		var admission = HL2RPCharacterLoadAdmission.Validate(
			connectionId,
			characterId,
			_clients.Select( pair => new HL2RPActiveCharacterBinding(
				pair.Key, pair.Value.CharacterId ) ) );
		if ( admission.Failed )
			return OperationResult.Failure(
				ErrorCode.Conflict,
				"An active character cannot be deleted from another connection." );
		var deletedInventories = _projectionIndex.InventoriesOwnedBy( characterId );
		var deletedItems = deletedInventories.SelectMany( _projectionIndex.ItemsIn ).Distinct().ToArray();
		var deletedReferences = _projectionIndex.CharacterReferenceKeys( characterId );
		var deleted = await _characters.DeleteAsync( actor.AccountId, characterId, cancellationToken );
		if ( deleted.Failed ) return Failure( deleted.Error! );
		projectionDelta.Documents.Add( new DocumentAddress(
			DomainCollections.Characters, DomainKeys.Character( characterId ) ) );
		projectionDelta.Inventories.UnionWith( deletedInventories );
		projectionDelta.Items.UnionWith( deletedItems );
		foreach ( var reference in deletedReferences )
			projectionDelta.Documents.Add( new DocumentAddress( DomainCollections.CharacterReferences, reference ) );
		projectionDelta.Documents.UnionWith( deleted.Value.Commit.Documents.Select( document => document.Address ) );
		projectionDelta.Observe( deleted.Value.Commit );
		_civicSubjects.ClearSubject( characterId );
		_civicSubjects.ClearConnection( connectionId );
		if ( _characterLifecycle.IsCurrent( connectionId, lifecycleEpoch ) &&
			_clients[connectionId].CharacterId == characterId ) UnloadBinding( connectionId, characterId );
		// A deleted character that is not bound here (switched away earlier) still owns
		// transient combat state; deletion is a lifecycle exit for it either way.
		else ObserveCharacterExit( connectionId, characterId );
		SendCharacterList( actor.Connection, actor.AccountId );
		return OperationResult.Success();
	}

	private async ValueTask<OperationResult> UnloadAsync(
		RpcActor actor,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var connectionId = new ConnectionId( actor.Connection.Id );
		if ( !_characterLifecycle.TryBeginOperation(
			connectionId, null, out var lifecycleEpoch, out var lifecycleOperation ) )
			return OperationResult.Failure(
				ErrorCode.Conflict, "Character lifecycle work is already in progress for this connection." );
		using var reservedLifecycle = lifecycleOperation!;
		if ( _clients[connectionId].CharacterId is CharacterId characterId )
		{
			CancelTimedActionsForLifecycle( connectionId, characterId );
			var cleared = await ClearRaisedPistolsAsync(
				new InventoryActor( connectionId, actor.AccountId, characterId ),
				projectionDelta,
				cancellationToken );
			if ( cleared.Failed ) return cleared;
			if ( !_characterLifecycle.IsCurrent( connectionId, lifecycleEpoch ) )
				return OperationResult.Failure(
					ErrorCode.Conflict, "Character unload was superseded by a later lifecycle request." );
			UnloadBinding( connectionId, characterId );
		}
		return OperationResult.Success();
	}

	private ValueTask<OperationResult> ClearRaisedPistolsAsync(
		InventoryActor actor,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken ) =>
		_execution is null
			? new ValueTask<OperationResult>( OperationResult.Success() )
			: _execution.ClearRaisedPistolsAsync( actor, projectionDelta, cancellationToken );

	private Task ClearRaisedPistolsForLifecycleAsync(
		InventoryActor actor,
		CancellationToken cancellationToken ) =>
		_execution is null
			? Task.CompletedTask
			: _execution.ClearRaisedPistolsForLifecycleAsync( actor, cancellationToken );

	private void UnloadBinding( ConnectionId connectionId, CharacterId characterId )
	{
		CancelTimedActionsForLifecycle( connectionId, characterId );
		ObserveCharacterExit( connectionId, characterId );
		_itemActionPresentations.Remove( connectionId );
		_civicSubjects.ClearConnection( connectionId );
		var binding = _clients[connectionId];
		_ = binding.Player.HostDisembody();
		SetBindingCharacter( connectionId, null );
		RefreshLiveConnection( connectionId );
	}

	private void ObserveCharacterExit( ConnectionId connectionId, CharacterId characterId )
	{
		var removal = HL2RPCharacterExitCleanup.Run(
			connectionId, characterId, _access, _interactions, _combatIntent, _combatLifecycle, _combatHealth );
		if ( removal == CombatHealthRemoval.RemovedWithDoomedReservation )
			Log.Info(
				$"HL2RP_COMBAT_RESERVATION_DOOMED character={characterId.Value:D} " +
				"detail=\"a pending health mutation was invalidated by the character's exit\"" );
	}

	private void CancelTimedActionsForLifecycle( ConnectionId connectionId, CharacterId? characterId )
	{
		foreach ( var action in _activeRestraintActions.CancelForLifecycle(
			connectionId,
			candidate => characterId is null || candidate.Actor.CharacterId == characterId ) )
		{
			HL2RPCommandExecution.CancelToken( action.Cancellation );
			if ( _restraints is not null )
			{
				var cancelled = _restraints.Cancel( action.Ticket.TicketId, action.Actor );
				if ( cancelled.Failed && cancelled.Error!.Code != ErrorCode.Unauthorized )
					Log.Warning( $"HL2RP restraint lifecycle cancellation degraded: {cancelled.Error.Message}" );
			}
		}
		foreach ( var action in _activePistolActions.CancelForLifecycle(
			connectionId,
			candidate => characterId is null || candidate.Actor.CharacterId == characterId ) )
			HL2RPCommandExecution.CancelToken( action.Cancellation );
	}

	private void CancelAllTimedActionsForLifecycle()
	{
		foreach ( var action in _activeRestraintActions.CancelAllForLifecycle() )
		{
			HL2RPCommandExecution.CancelToken( action.Cancellation );
			if ( _restraints is not null )
			{
				var cancelled = _restraints.Cancel( action.Ticket.TicketId, action.Actor );
				if ( cancelled.Failed && cancelled.Error!.Code != ErrorCode.Unauthorized )
					Log.Warning( $"HL2RP restraint shutdown cancellation degraded: {cancelled.Error.Message}" );
			}
		}
		foreach ( var action in _activePistolActions.CancelAllForLifecycle() )
			HL2RPCommandExecution.CancelToken( action.Cancellation );
	}

	private async ValueTask<OperationResult> MoveAsync(
		RpcActor rpc,
		MoveInventoryItemCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		return await _execution!.MoveAsync( actor.Value, command, projectionDelta, cancellationToken );
	}

	private async ValueTask<OperationResult> RunItemActionAsync(
		RpcActor rpc,
		RunItemActionCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		return await _execution!.RunItemActionAsync( actor.Value, command, projectionDelta, cancellationToken );
	}

	private async ValueTask<OperationResult> DropAsync(
		RpcActor rpc,
		DropItemCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		if ( !rpc.Player.TryGetUsableAuthoritativeBody( out var authoritativeBody ) )
			return OperationResult.Failure( ErrorCode.NotFound, "Authoritative drop body is unavailable." );
		return await _execution!.DropAsync(
			actor.Value, command, DropTransform( authoritativeBody ), projectionDelta, cancellationToken );
	}

	private async ValueTask<OperationResult> PickupAsync(
		RpcActor rpc,
		PickUpItemCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		return await _execution!.PickupAsync( actor.Value, command, projectionDelta, cancellationToken );
	}

	private OperationResult SendChat( RpcActor rpc, SendChatCommand command )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		return _execution!.SendChat( actor.Value, command );
	}


	private void DeliverCommittedChat( ChatDelivery delivery, CharacterRecord author )
	{
		var revision = ++_presentationRevision;
		foreach ( var recipient in delivery.Recipients )
		{
			if ( !_clients.TryGetValue( recipient, out var binding ) ) continue;
			var viewer = FindActiveCharacter( recipient );
			var label = DisplayNameFor( viewer, author );
			_context.Transport.SendChat( binding.Connection, revision, new[]
			{
				new ChatMessageSnapshot(
					delivery.MessageId, delivery.ChannelId, delivery.AuthorCharacterId,
					label, delivery.Text, delivery.SentAtUtc )
			} );
		}
	}

	private async ValueTask<OperationResult> BeginInteractionAsync(
		RpcActor rpc,
		InteractionTargetInput input,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		return await _execution!.BeginInteractionAsync( actor.Value, input, projectionDelta, cancellationToken );
	}

	private OperationResult ContinueInteraction( RpcActor rpc, ContinueInteractionCommand command )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		return _execution!.ContinueInteraction( actor.Value, command );
	}

	private OperationResult CloseInteraction( RpcActor rpc, InteractionSessionId sessionId )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		return _execution!.CloseInteraction( actor.Value, sessionId );
	}

	private OperationResult CancelAction( RpcActor rpc, Guid instanceId )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		return _execution!.CancelAction( actor.Value, instanceId );
	}


	private ValueTask<OperationResult> RunSchemaCommandAsync(
		RpcActor rpc,
		RunSchemaCommandCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken ) =>
		// The pipeline (definition lookup, entitlement-before-character-requirement,
		// fail-closed permission gate, routing table) lives in the neutral schema sink
		// so the test suite executes it; this host only implements the route seams.
		HL2RPSchemaCommandSink.RouteAsync<RpcActor>(
			this, rpc, command, projectionDelta, cancellationToken );

	bool IHL2RPSchemaCommandRoutes<RpcActor>.TryGetCommandDefinition( string commandId, out string? permissionId )
	{
		permissionId = null;
		if ( !_context.Schema.Commands.TryGet( commandId, out var definition ) ) return false;
		permissionId = definition!.PermissionId;
		return true;
	}

	ValueTask<OperationResult> IHL2RPSchemaCommandRoutes<RpcActor>.RunEntitlementCommandAsync(
		RpcActor rpc, RunSchemaCommandCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken )
	{
		var connectionId = new ConnectionId( rpc.Connection.Id );
		return _execution!.RunEntitlementCommandAsync(
			connectionId, rpc.AccountId, FindActiveCharacter( connectionId )?.Id,
			command, projectionDelta, cancellationToken );
	}

	OperationResult<InventoryActor> IHL2RPSchemaCommandRoutes<RpcActor>.RequireInventoryActor( RpcActor rpc, bool allowDead ) =>
		RequireInventoryActor( rpc, allowDead );

	bool IHL2RPSchemaCommandRoutes<RpcActor>.HasPermission( InventoryActor actor, string permissionId ) =>
		_featureAuthorization.HasPermission( actor.AccountId, actor.CharacterId, permissionId );

	OperationResult IHL2RPSchemaCommandRoutes<RpcActor>.CivicData( InventoryActor actor, HL2RPCommandArguments arguments ) =>
		_execution!.CivicData( actor, arguments );

	ValueTask<OperationResult> IHL2RPSchemaCommandRoutes<RpcActor>.SetObjectivesAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		_execution!.SetObjectivesAsync( actor, arguments, projectionDelta, cancellationToken );

	ValueTask<OperationResult> IHL2RPSchemaCommandRoutes<RpcActor>.SetPriorityAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		_execution!.SetPriorityAsync( actor, arguments, projectionDelta, cancellationToken );

	ValueTask<OperationResult> IHL2RPSchemaCommandRoutes<RpcActor>.TuneRadioAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		_execution!.TuneRadioAsync( actor, arguments, projectionDelta, cancellationToken );

	ValueTask<OperationResult> IHL2RPSchemaCommandRoutes<RpcActor>.IntroduceAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		_execution!.IntroduceAsync( actor, arguments, projectionDelta, cancellationToken );

	ValueTask<OperationResult> IHL2RPSchemaCommandRoutes<RpcActor>.DoorOwnershipAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		_execution!.DoorOwnershipAsync( actor, arguments, projectionDelta, cancellationToken );

	OperationResult IHL2RPSchemaCommandRoutes<RpcActor>.PublishAdministrationAudit( InventoryActor actor ) =>
		_execution!.PublishAdministrationAudit( actor );

	ValueTask<OperationResult> IHL2RPSchemaCommandRoutes<RpcActor>.BuyAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		_execution!.BuyAsync( actor, arguments, projectionDelta, cancellationToken );

	ValueTask<OperationResult> IHL2RPSchemaCommandRoutes<RpcActor>.SellAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		_execution!.SellAsync( actor, arguments, projectionDelta, cancellationToken );

	ValueTask<OperationResult> IHL2RPSchemaCommandRoutes<RpcActor>.PurchasePermitAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		_execution!.PurchasePermitAsync( actor, arguments, projectionDelta, cancellationToken );

	ValueTask<OperationResult> IHL2RPSchemaCommandRoutes<RpcActor>.WriteNoteAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		_execution!.WriteNoteAsync( actor, arguments, projectionDelta, cancellationToken );

	ValueTask<OperationResult> IHL2RPSchemaCommandRoutes<RpcActor>.SetRestraintAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		_execution!.SetRestraintAsync( actor, arguments, projectionDelta, cancellationToken );

	ValueTask<OperationResult> IHL2RPSchemaCommandRoutes<RpcActor>.ScannerIntentAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
		_execution!.ScannerIntentAsync( actor, arguments, projectionDelta, cancellationToken );

	OperationResult IHL2RPSchemaCommandRoutes<RpcActor>.RespawnCharacter( InventoryActor actor ) =>
		RespawnCharacter( actor );

	private IReadOnlyList<ConnectionId> EntitlementRecipients( AccountId accountId ) =>
		_clients
			.Where( pair => pair.Value.AccountId == accountId ||
				_entitlementQueries.TryGetValue( pair.Key, out var queried ) && queried == accountId )
			.Select( pair => pair.Key )
			.Distinct()
			.ToArray();

	private OperationResult RespawnCharacter( InventoryActor actor )
	{
		var death = _combatLifecycle?.GetState( actor.CharacterId );
		if ( death is null || death.AccountId != actor.AccountId )
			return OperationResult.Failure( ErrorCode.NotFound, "No death lifecycle is bound to this actor." );
		if ( !death.CanRespawn( _clock.UtcNow ) )
			return OperationResult.Failure( ErrorCode.PolicyDenied, "Respawn delay has not completed." );
		if ( !_clients.TryGetValue( actor.ConnectionId, out var binding ) || binding.CharacterId != actor.CharacterId )
			return OperationResult.Failure( ErrorCode.Unauthorized, "The dead character is not active on this connection." );
		var character = _repositories.Characters.Find( DomainKeys.Character( actor.CharacterId ) )?.Value;
		var main = MainInventory( actor.CharacterId );
		if ( character is null || character.AccountId != actor.AccountId || main is null )
			return OperationResult.Failure( ErrorCode.NotFound, "Respawn character or main inventory was not found." );
		var modelPath = _characterModels.ResolvePath( character.Model );
		if ( modelPath.Failed || !_worldModels.IsValidModel( modelPath.Value ) )
			return OperationResult.Failure( ErrorCode.ConfigurationInvalid, "Character model does not resolve." );
		if ( !_respawningCharacters.Add( actor.CharacterId ) )
			return OperationResult.Failure( ErrorCode.Conflict, "Character respawn is already in progress." );

		try
		{
			// Body and grant are prepared while both the death state and the respawn
			// phase guard deny commands. Clearing death is the final authority flip.
			var body = binding.Player.HostEmbody( candidate =>
			{
				ConfigureAuthoritativePlayerBody( candidate, character );
				var bodyObject = candidate.Children.FirstOrDefault( child => child.Name == "Body" )
					?? new GameObject( candidate, true, "Body" );
				var renderer = bodyObject.GetOrAddComponent<SkinnedModelRenderer>();
				renderer.Model = Model.Load( modelPath.Value );
				renderer.Enabled = true;
			} );
			if ( body.Failed ) return Failure( body.Error! );

			_access.RevokeCharacter( actor.ConnectionId, actor.CharacterId );
			try
			{
				_access.Grant( new InventoryGrant
				{
					ConnectionId = actor.ConnectionId,
					CharacterId = actor.CharacterId,
					InventoryId = main.Id,
					Capabilities = CharacterCapabilities,
					Kind = InventoryGrantKind.Character
				} );
			}
			catch ( Exception exception )
			{
				_ = binding.Player.HostDisembody();
				Log.Error( exception, "Failed to restore the active-character inventory grant." );
				return OperationResult.Failure( ErrorCode.InternalError, "Respawn authority could not be restored." );
			}

			var respawned = _combatIntent!.Respawn( actor );
			if ( respawned.Failed )
			{
				_access.RevokeCharacter( actor.ConnectionId, actor.CharacterId );
				_ = binding.Player.HostDisembody();
				PublishConnections( new[] { actor.ConnectionId }, invalidateRuntime: true );
				return Failure( respawned.Error! );
			}

			if ( _combatHealth.Require( actor.CharacterId ).Failed )
				_combatHealth.Publish( actor.CharacterId, 100, 100 );
			_presentationInvalidation.ClearDeathDeadline( actor.CharacterId );
			RefreshLiveConnection( actor.ConnectionId );
			return OperationResult.Success();
		}
		finally
		{
			_respawningCharacters.Remove( actor.CharacterId );
		}
	}

	private OperationResult<InventoryActor> RequireInventoryActor( RpcActor actor, bool allowDead = false )
	{
		var connectionId = new ConnectionId( actor.Connection.Id );
		var character = FindActiveCharacter( connectionId );
		if ( character is null || character.AccountId != actor.AccountId )
			return OperationResult<InventoryActor>.Failure(
				ErrorCode.Unauthorized, "An active canonical character is required." );
		if ( !allowDead && (_combatLifecycle?.GetState( character.Id ) is not null ||
			_respawningCharacters.Contains( character.Id )) )
			return OperationResult<InventoryActor>.Failure(
				ErrorCode.PolicyDenied, "Dead characters cannot perform commands." );
		return OperationResult<InventoryActor>.Success(
			new InventoryActor( connectionId, actor.AccountId, character.Id ) );
	}

	private bool CanManageEntitlements( HL2RPEntitlementAdministrator administrator ) =>
		_bootstrapOperators.Contains( administrator.AccountId ) ||
		administrator.CharacterId is CharacterId characterId &&
		_featureAuthorization.HasPermission(
			administrator.AccountId, characterId, HL2RPIds.Permissions.ManageEntitlements );

	private bool IsKnownAccount( AccountId accountId ) =>
		_clients.Values.Any( value => value.AccountId == accountId ) || _projectionIndex.IsKnownAccount( accountId );

	private InventoryRecord? MainInventory( CharacterId characterId ) =>
		_presentation.MainInventory( characterId );

	private string DisplayNameFor( CharacterRecord? viewer, CharacterRecord subject ) =>
		_presentation.DisplayNameFor( viewer, subject );

	private InteractionSession? CurrentSession( InventoryActor actor, InteractionSessionKind kind ) =>
		_sessions?.ActiveSessions.SingleOrDefault( value =>
			value.ConnectionId == actor.ConnectionId && value.CharacterId == actor.CharacterId && value.Kind == kind );

	private (AccountId Account, HexPlayerBody Player, CharacterRecord Character)? ResolveActorState( ConnectionId connectionId )
	{
		if ( !_clients.TryGetValue( connectionId, out var binding ) ||
			FindActiveCharacter( connectionId ) is not CharacterRecord character ) return null;
		return (binding.AccountId, binding.Player, character);
	}

	private HL2RPInteractionActorState? ResolveInteractionActorState( ConnectionId connectionId )
	{
		var connected = ResolveActorState( connectionId );
		if ( connected is not null && connected.Value.Player.TryGetUsableAuthoritativeBody( out var body ) )
			return new HL2RPInteractionActorState(
				connected.Value.Account, connected.Value.Character, body, connected.Value.Player.IsDead );
		if ( _verificationActor is not VerificationActorBinding verification ||
			verification.ConnectionId != connectionId ||
			verification.CharacterId is not CharacterId characterId ||
			verification.Body is not GameObject verificationBody || !verificationBody.IsValid() ) return null;
		var character = _repositories.Characters.Find( DomainKeys.Character( characterId ) )?.Value;
		return character is null
			? null
			: new HL2RPInteractionActorState( verification.AccountId, character, verificationBody, false );
	}

	private HexPlayerBody? FindPlayer( CharacterId characterId )
	{
		var player = _clients.Values.FirstOrDefault( value => value.CharacterId == characterId )?.Player;
		return player is not null && player.TryGetUsableAuthoritativeBody( out _ ) ? player : null;
	}

	private WorldPoint? ActorPosition( InventoryActor actor )
	{
		var player = FindPlayer( actor.CharacterId );
		if ( player is null || !player.TryGetUsableAuthoritativeBody( out _ ) ) return null;
		var position = player.AuthoritativeWorldPosition;
		return new WorldPoint( position.x, position.y, position.z );
	}

	private bool HasLineOfSight( WorldPoint actor, WorldPoint target )
	{
		var start = new Vector3( actor.X, actor.Y, actor.Z );
		var end = new Vector3( target.X, target.Y, target.Z );
		var trace = _context.Scene.Trace.Ray( start, end ).WithoutTags( "prediction" ).Run();
		return !trace.Hit || trace.Distance >= Vector3.DistanceBetween( start, end ) - 32f;
	}

	private bool HasBodyLineOfSight( GameObject actor, GameObject target )
	{
		var start = HexPlayerBody.AuthoritativeWorldPositionOf( actor );
		var end = HexPlayerBody.AuthoritativeWorldPositionOf( target );
		var trace = _context.Scene.Trace.Ray( start, end )
			.IgnoreGameObjectHierarchy( actor.Root )
			.WithoutTags( "prediction" )
			.Run();
		return !trace.Hit ||
			HL2RPObjectHierarchy.Contains( target, trace.GameObject, current => current.Parent ) ||
			trace.Distance >= Vector3.DistanceBetween( start, end ) - 32f;
	}

	private PolicyPipeline<T> RequirePolicy<T>()
	{
		var result = _context.Schema.CreatePolicyPipeline<T>( diagnostic =>
			Log.Error( diagnostic.Exception, $"HL2RP policy '{diagnostic.HandlerId}' failed." ) );
		return result.Succeeded ? result.Value : throw new InvalidOperationException( result.Error!.Message );
	}

	private PolicyPipeline<T> RequirePolicy<T>( params PolicyHandler<T>[] additionalHandlers )
	{
		var result = _context.Schema.CreatePolicyPipeline(
			additionalHandlers,
			diagnostic => Log.Error( diagnostic.Exception, $"HL2RP policy '{diagnostic.HandlerId}' failed." ) );
		return result.Succeeded ? result.Value : throw new InvalidOperationException( result.Error!.Message );
	}

	private static OperationResult Failure( OperationError error ) =>
		OperationResult.Failure( error.Code, error.Message );

	private static OperationResult Untyped<T>( OperationResult<T> result ) =>
		result.Succeeded ? OperationResult.Success() : Failure( result.Error! );

	private static WorldTransformRecord DropTransform( GameObject gameObject )
	{
		var forward = gameObject.Components.Get<PlayerController>()?.EyeAngles.ToRotation().Forward ??
			gameObject.WorldTransform.Forward;
		var position = HexPlayerBody.AuthoritativeWorldPositionOf( gameObject ) + forward * 48f + Vector3.Up * 24f;
		var rotation = gameObject.WorldRotation;
		return new WorldTransformRecord
		{
			PositionX = position.x,
			PositionY = position.y,
			PositionZ = position.z,
			RotationX = rotation.x,
			RotationY = rotation.y,
			RotationZ = rotation.z,
			RotationW = rotation.w
		};
	}

	private ChatService BuildChat()
	{
		var local = new HL2RPLocalChatRecipientResolver( _chatPositions );
		_chatRecipients = new HL2RPRadioRecipientResolver( _chatAuthorities, local );
		var capacity = RequiredConfigurationInt( HL2RPIds.Configs.ChatRateCapacity );
		var window = RequiredConfigurationInt( HL2RPIds.Configs.ChatRateWindowSeconds );
		_globalChatRateLimit = new ChatRateLimit( capacity, TimeSpan.FromSeconds( window ) );
		return new ChatService(
			_context.Schema,
			HL2RPChatChannelRules.Create(),
			_chatAuthorities,
			_chatRecipients,
			_liveInventory,
			_clock,
			RequirePolicy( new PolicyHandler<ChatSendContext>(
				"hl2rp.runtime.request_device_only", new HL2RPChatRuntimePolicy() ) ),
			globalRateLimit: _globalChatRateLimit,
			admission: _chatAdmission );
	}

	private void RebuildLiveInventory() => _liveInventory.Rebuild(
		_context.Persistence.Health.Sequence,
		_projectionIndex.LiveInventoryRows() );

	private void ApplyLiveInventory(
		HL2RPPresentationChangeSet changes,
		IReadOnlyList<HL2RPProjectionApplyResult> applied )
	{
		var affected = new HashSet<ItemId>( applied.SelectMany( value => value.AffectedLiveItems ) );
		affected.UnionWith( changes.Items );
		foreach ( var inventory in changes.Inventories ) affected.UnionWith( _projectionIndex.ItemsIn( inventory ) );
		var deltas = affected.OrderBy( value => value.Value )
			.Select( item => new HL2RPLiveInventoryDelta(
				item,
				_projectionIndex.TryCreateLiveInventoryRow( item, out var row ) ? row : null ) )
			.ToArray();
		_liveInventory.Apply( _context.Persistence.Health.Sequence, deltas );
	}

	private void RefreshAllLiveConnections()
	{
		foreach ( var connectionId in _clients.Keys.ToArray() ) RefreshLiveConnection( connectionId );
	}

	private void RemoveLiveConnection( ConnectionId connectionId )
	{
		var version = Math.Max( _presentationRevision, _context.Persistence.Health.Sequence );
		_chatPositions.Apply( version, connectionId, null );
		_chatAuthorities.Apply( version, connectionId, null );
		_combatTargets.Apply( connectionId, null );
	}

	private void RefreshLiveConnection( ConnectionId connectionId )
	{
		var version = Math.Max( _presentationRevision, _context.Persistence.Health.Sequence );
		if ( !_clients.TryGetValue( connectionId, out var binding ) )
		{
			RemoveLiveConnection( connectionId );
			return;
		}
		var character = FindActiveCharacter( connectionId );
		var hasUsableBody = binding.Player.TryGetUsableAuthoritativeBody( out var body );
		if ( character is null || !hasUsableBody )
		{
			_chatPositions.Apply( version, connectionId, null );
			_chatAuthorities.Apply( version, connectionId, null );
			_combatTargets.Apply( connectionId, null );
			return;
		}

		var position = binding.Player.AuthoritativeWorldPosition;
		_chatPositions.Apply( version, connectionId, new LiveChatConnection(
			connectionId, character.Id, new ChatPosition( position.x, position.y, position.z ) ) );
		_chatAuthorities.Apply( version, connectionId, new LiveChatAuthority(
			connectionId,
			character.AccountId,
			character.Id,
			character.Faction,
			HL2RPRuntimeProjection.PermissionsFor( character ) ) );

		var main = MainInventory( character.Id );
		if ( main is null || _combatLifecycle?.GetState( character.Id ) is not null )
		{
			_combatTargets.Apply( connectionId, null );
			return;
		}
		if ( _combatHealth.Require( character.Id ).Failed ) _combatHealth.Publish( character.Id, 100, 100 );
		_combatTargets.Apply( connectionId, new CombatPlayerTarget(
			character.Id.Value.ToString( "D" ),
			new InventoryActor( connectionId, binding.AccountId, character.Id ),
			main.Id,
			DropTransform( body ) ) );
	}

	private string? ResolveCombatTargetToken( GameObject hit )
	{
		for ( var current = hit; current is not null and not Scene; current = current.Parent )
		{
			foreach ( var pair in _clients )
			{
				if ( !pair.Value.Player.TryGetUsableAuthoritativeBody( out var body ) ||
					body != current || pair.Value.CharacterId is not CharacterId characterId ) continue;
				return characterId.Value.ToString( "D" );
			}
		}
		return null;
	}

	private void RebuildProjectionIndex() => _projectionIndex.Rebuild(
		_repositories.Inventories.All(),
		_repositories.Items.All(),
		_repositories.Characters.All(),
		_repositories.CharacterReferences.All(),
		_repositories.SceneEntities.All() );

	private HL2RPPresentationChangeSet EnrichChanges(
		ConnectionId connectionId,
		ClientCommand command,
		HL2RPPresentationChangeSet changes,
		CommandProjectionDelta projectionDelta,
		InventoryId? mainBefore,
		ItemId? restraintTieBefore )
	{
		if ( changes.IsEmpty && !projectionDelta.HasPersistentChanges ) return changes;
		var inventories = new HashSet<InventoryId>( changes.Inventories );
		var items = new HashSet<ItemId>( changes.Items );
		var sceneEntities = new HashSet<SceneEntityId>( changes.SceneEntities );
		var documents = new HashSet<DocumentAddress>( changes.Documents );
		var connections = new HashSet<ConnectionId>( changes.Connections );
		var characters = new HashSet<CharacterId>( changes.Characters );
		connections.UnionWith( projectionDelta.Connections );
		characters.UnionWith( projectionDelta.Characters );
		inventories.UnionWith( projectionDelta.Inventories );
		items.UnionWith( projectionDelta.Items );
		sceneEntities.UnionWith( projectionDelta.SceneEntities );
		documents.UnionWith( projectionDelta.Documents );
		if ( command is RunSchemaCommandCommand && changes.RebuildLiveInventory && mainBefore is InventoryId main )
			inventories.Add( main );
		if ( command is RunSchemaCommandCommand { CommandId: HL2RPIds.Commands.RestraintSet } &&
			restraintTieBefore is ItemId tie ) items.Add( tie );
		_projectionIndex.InvalidateVisibility( connectionId );
		return changes with
		{
			Connections = connections.ToArray(),
			Characters = characters.ToArray(),
			Inventories = inventories.ToArray(),
			Items = items.ToArray(),
			SceneEntities = sceneEntities.ToArray(),
			Documents = documents.ToArray(),
			Broadcast = changes.Broadcast || projectionDelta.Broadcast,
			RebuildLiveInventory = changes.RebuildLiveInventory || projectionDelta.RebuildLiveInventory ||
				projectionDelta.Inventories.Count > 0 || projectionDelta.Items.Count > 0,
			RebuildCombatTargets = changes.RebuildCombatTargets || projectionDelta.RebuildCombatTargets
		};
	}

	private void PublishChanges( HL2RPPresentationChangeSet changes, CommitReceipt receipt ) =>
		PublishChanges( changes, new[] { receipt } );

	private void PublishChanges( HL2RPPresentationChangeSet changes, IReadOnlyList<CommitReceipt> receipts )
	{
		ArgumentNullException.ThrowIfNull( receipts );
		var applied = receipts.OrderBy( value => value.Sequence )
			.Select( receipt => _projectionIndex.Apply( receipt, _repositories ) )
			.ToArray();
		var receiptInventories = applied.SelectMany( value => value.AffectedInventories ).ToHashSet();
		var receiptItems = applied.SelectMany( value => value.AffectedLiveItems ).ToHashSet();
		if ( receiptInventories.Count > 0 || receiptItems.Count > 0 )
		{
			receiptInventories.UnionWith( changes.Inventories );
			receiptItems.UnionWith( changes.Items );
			changes = changes with
			{
				Inventories = receiptInventories.OrderBy( value => value.Value ).ToArray(),
				Items = receiptItems.OrderBy( value => value.Value ).ToArray(),
				RebuildLiveInventory = true
			};
		}
		if ( changes.IsEmpty ) return;
		foreach ( var connection in changes.Connections ) _projectionIndex.InvalidateRuntime( connection );
		foreach ( var character in changes.Characters )
			if ( _projectionIndex.ConnectionForCharacter( character ) is ConnectionId connection )
				_projectionIndex.InvalidateRuntime( connection );
		if ( changes.RebuildLiveConnections )
			_projectionIndex.InvalidateRuntimeDependency( "roster-membership", "global" );
		if ( changes.RebuildCombatTargets )
			_projectionIndex.InvalidateRuntimeDependency( "combat-lifecycle", "global" );
		IReadOnlyList<ConnectionId> affectedInventoryViewers = Array.Empty<ConnectionId>();
		if ( changes.RebuildLiveInventory )
		{
			affectedInventoryViewers = _projectionIndex.RefreshViewers( changes.Inventories, _access.GetViewers );
			ApplyLiveInventory( changes, applied );
		}
		if ( changes.RebuildLiveConnections || changes.RebuildCombatTargets )
		{
			var liveConnections = new HashSet<ConnectionId>( changes.Connections );
			foreach ( var character in changes.Characters )
				if ( _projectionIndex.ConnectionForCharacter( character ) is ConnectionId connection )
					liveConnections.Add( connection );
			foreach ( var connection in liveConnections ) RefreshLiveConnection( connection );
		}
		if ( changes.Broadcast )
		{
			PublishAll();
			return;
		}
		var recipients = HL2RPPresentationPlanner.ResolveRecipients(
			changes, _projectionIndex.ConnectionForCharacter, _projectionIndex.ConnectionsForAccount,
			_projectionIndex.Viewers, _projectionIndex.SceneViewers )
			.Concat( affectedInventoryViewers )
			.Distinct()
			.OrderBy( value => value.Value )
			.ToArray();
		PublishConnections( recipients );
	}

	private void SendCharacterList( Connection connection, AccountId accountId ) =>
		_context.Transport.SendCharacterList(
			connection,
			HL2RPRuntimeProjection.CharacterList(
				_context.Persistence.Health.Sequence,
				_characters.ListForAccount( accountId ),
				_clock.UtcNow ) );

	private void PublishAll()
	{
		if ( _disposed ) return;
		var publication = _presentationInvalidation.BeginPublication( _clock.UtcNow );
		PublishConnections( _clients.Keys );
		_presentationInvalidation.AcknowledgePublished( publication );
	}

	private void PublishDuePresentation( DateTimeOffset nowUtc )
	{
		if ( _disposed ) return;
		var publication = _presentationInvalidation.BeginPublication( nowUtc );
		if ( publication.RequiresBroadcast )
			PublishConnections( _clients.Keys, invalidateRuntime: true );
		else
			PublishConnections( publication.ConnectionGenerations.Keys, invalidateRuntime: true );
		_presentationInvalidation.AcknowledgePublished( publication );
	}

	private void PublishConnections(
		IEnumerable<ConnectionId> connectionIds,
		bool invalidateRuntime = false )
	{
		if ( _disposed ) return;
		var recipients = connectionIds.Distinct().ToArray();
		var coveredInvalidations = _presentationInvalidation.BeginConnectionPublication( recipients );
		var revision = Math.Max( ++_presentationRevision, _context.Persistence.Health.Sequence );
		foreach ( var connectionId in recipients )
		{
			if ( invalidateRuntime ) _projectionIndex.InvalidateRuntime( connectionId );
			if ( !_clients.TryGetValue( connectionId, out var binding ) ) continue;
			var pair = new KeyValuePair<ConnectionId, ClientBinding>( connectionId, binding );
			var character = FindActiveCharacter( pair.Key );
			var clientView = new HL2RPClientPresentationView(
				pair.Key, pair.Value.AccountId, pair.Value.CharacterId, pair.Value.Connection.DisplayName );
			var publicSnapshot = _presentation.PublicSnapshot( clientView, character );
			PlayerPrivateSnapshot? privateSnapshot = null;
			IReadOnlyList<InventorySnapshot> inventories = Array.Empty<InventorySnapshot>();
			IReadOnlyList<SchemaViewSnapshot> views = new[]
			{
				_presentation.BuildCreationAvailabilityView( pair.Key, pair.Value.AccountId, character?.Id, revision )
			};
			if ( character is not null )
			{
				var mainInventoryId = MainInventory( character.Id )?.Id;
				privateSnapshot = _projectionIndex.GetOrCreateSlice(
					pair.Key,
					HL2RPProjectionSliceKind.PrivatePlayer,
					() => new HL2RPProjectionSlice<PlayerPrivateSnapshot>(
						_presentation.BuildPrivateSnapshot( character, mainInventoryId ),
						_presentation.PrivateProjectionDocuments( character.Id, mainInventoryId ),
						new[] { HL2RPProjectionIndex.RuntimeDependency( "connection", pair.Key.ToString() ) } ) );
				inventories = _projectionIndex.GetOrCreateSlice(
					pair.Key,
					HL2RPProjectionSliceKind.Inventories,
					() => _presentation.BuildInventoryProjectionSlice( pair.Key, character.Id ) );
				var cachedViews = _projectionIndex.GetOrCreateSlice(
					pair.Key,
					HL2RPProjectionSliceKind.SchemaViews,
					() => _presentation.BuildSchemaProjectionSlice( pair.Key, character ) );
				views = views.Concat( cachedViews.Select( view => new SchemaViewSnapshot(
					view.PanelId, revision, view.Fields, view.Rows ) ) ).ToArray();
			}
			_context.Transport.SendClientState(
				pair.Value.Connection,
				publicSnapshot,
				privateSnapshot,
				_projectionIndex.GetOrCreateSlice(
					pair.Key,
					HL2RPProjectionSliceKind.Roster,
					() => _presentation.BuildRosterProjectionSlice( pair.Key ) ).WithRevision( revision ),
				views,
				inventories,
				_presentation.BuildActionProgress( pair.Key ) );
		}
		_presentationInvalidation.AcknowledgePublished( coveredInvalidations );
	}

	IReadOnlyList<HL2RPClientPresentationView> IHL2RPPresentationHost.Clients =>
		_clients.Select( pair => new HL2RPClientPresentationView(
			pair.Key, pair.Value.AccountId, pair.Value.CharacterId, pair.Value.Connection.DisplayName ) ).ToArray();

	CharacterRecord? IHL2RPPresentationHost.NearestCharacterTarget( CharacterId viewerId ) =>
		NearestCharacterTarget( viewerId );

	bool IHL2RPPresentationHost.IsOwnableDoor( SceneEntityId sceneEntityId ) =>
		_features.TryGetValue( sceneEntityId, out var component ) &&
		component is HL2RPDoorComponent { Ownable: true };

	private static void LogScannerBoundaryError( OperationError? error )
	{
		if ( error is not null )
			Log.Warning( $"HL2RP_SCANNER_BOUNDARY_DEGRADED code={error.Code} message={error.Message}" );
	}

	void IHL2RPCommandExecutionHost.PublishConnection( ConnectionId connectionId ) =>
		PublishConnections( new[] { connectionId }, invalidateRuntime: true );

	void IHL2RPCommandExecutionHost.PublishChanges(
		HL2RPPresentationChangeSet changes, IReadOnlyList<CommitReceipt> receipts ) =>
		PublishChanges( changes, receipts );

	void IHL2RPCommandExecutionHost.DeliverCommittedChat( ChatDelivery delivery, CharacterRecord author ) =>
		DeliverCommittedChat( delivery, author );

	bool IHL2RPCommandExecutionHost.TryClassifySceneFeature(
		SceneEntityId sceneEntityId, out HL2RPSceneFeatureClassification? classification )
	{
		classification = null;
		if ( !_features.TryGetValue( sceneEntityId, out var scannerFeature ) ) return false;
		if ( scannerFeature is HL2RPScannerDockComponent dock )
			classification = new HL2RPSceneFeatureClassification(
				HL2RPSceneFeatureKind.ScannerDock, dock.LinkedDroneId, false );
		else if ( scannerFeature is HL2RPScannerDroneComponent )
			classification = new HL2RPSceneFeatureClassification( HL2RPSceneFeatureKind.ScannerDrone, null, false );
		else if ( scannerFeature is HL2RPForcefieldComponent )
			classification = new HL2RPSceneFeatureClassification( HL2RPSceneFeatureKind.Forcefield, null, false );
		else if ( scannerFeature is HL2RPMachineComponent )
			classification = new HL2RPSceneFeatureClassification( HL2RPSceneFeatureKind.Machine, null, false );
		else if ( scannerFeature is HL2RPDoorComponent door )
			classification = new HL2RPSceneFeatureClassification( HL2RPSceneFeatureKind.Door, null, door.Ownable );
		else
			classification = new HL2RPSceneFeatureClassification( HL2RPSceneFeatureKind.Other, null, false );
		return true;
	}

	private CharacterRecord? NearestCharacterTarget( CharacterId viewerId )
	{
		var viewer = FindPlayer( viewerId );
		if ( viewer is null || !viewer.TryGetUsableAuthoritativeBody( out var viewerBody ) ) return null;
		CharacterRecord? nearest = null;
		var nearestDistance = float.MaxValue;
		foreach ( var binding in _clients.Values )
		{
			if ( binding.CharacterId is not CharacterId candidateId || candidateId == viewerId ||
				!binding.Player.TryGetUsableAuthoritativeBody( out var candidateBody ) ) continue;
			var character = _repositories.Characters.Find( DomainKeys.Character( candidateId ) )?.Value;
			if ( character is null ) continue;
			var distance = Vector3.DistanceBetween( viewer.AuthoritativeWorldPosition, binding.Player.AuthoritativeWorldPosition );
			if ( distance > 130f || distance >= nearestDistance ||
				!HasBodyLineOfSight( viewerBody, candidateBody ) )
				continue;
			nearest = character;
			nearestDistance = distance;
		}
		return nearest;
	}

	WorldItemBoundaryAttempt IWorldItemReconciliationBoundary.ApplyDesiredState( ItemId itemId ) =>
		ApplyDesiredWorldItemState( itemId );

	private WorldItemBoundaryAttempt ApplyDesiredWorldItemState( ItemId itemId )
	{
		var world = _repositories.WorldItems.Find( DomainKeys.WorldItem( itemId ) )?.Value;
		if ( world is null )
		{
			if ( !_worldObjects.TryGetValue( itemId, out var existing ) )
				return WorldItemBoundaryAttempt.Applied();
			if ( !existing.IsValid() )
			{
				_worldObjects.Remove( itemId );
				return WorldItemBoundaryAttempt.Applied();
			}
			try
			{
				existing.Enabled = false;
				existing.Destroy();
				_worldObjects.Remove( itemId );
				return WorldItemBoundaryAttempt.Applied();
			}
			catch ( Exception exception )
			{
				return WorldItemBoundaryAttempt.Transient( new OperationError(
					ErrorCode.InternalError,
					$"Committed world-item removal could not be published: {exception.Message}" ) );
			}
		}

		var item = _repositories.Items.Find( DomainKeys.Item( itemId ) )?.Value;
		if ( item is null )
			return WorldItemBoundaryAttempt.Permanent( new OperationError(
				ErrorCode.NotFound,
				$"World item '{itemId}' references an item record that does not exist." ) );
		if ( !_context.Schema.Items.TryGet( item.Definition.Value, out var definition ) )
			return WorldItemBoundaryAttempt.Permanent( new OperationError(
				ErrorCode.ConfigurationInvalid,
				$"World item '{itemId}' references unknown definition '{item.Definition.Value}'." ) );
		if ( string.IsNullOrWhiteSpace( definition!.WorldModel ) ||
			!_worldModels.IsValidModel( definition.WorldModel ) )
			return WorldItemBoundaryAttempt.Permanent( new OperationError(
				ErrorCode.ConfigurationInvalid,
				$"World item '{itemId}' has no valid configured world model." ) );

		if ( _worldObjects.TryGetValue( itemId, out var cached ) )
		{
			if ( cached.IsValid() )
			{
				if ( !cached.Network.Active )
				{
					try
					{
						cached.Destroy();
						_worldObjects.Remove( itemId );
					}
					catch ( Exception exception )
					{
						return WorldItemBoundaryAttempt.Transient( new OperationError(
							ErrorCode.InternalError,
							$"A partial world-item object could not be destroyed: {exception.Message}" ) );
					}
				}
				else
				{
					try
					{
						cached.WorldPosition = new Vector3(
							world.Transform.PositionX,
							world.Transform.PositionY,
							world.Transform.PositionZ );
						cached.WorldRotation = new Rotation(
							world.Transform.RotationX,
							world.Transform.RotationY,
							world.Transform.RotationZ,
							world.Transform.RotationW );
						var cachedRenderer = cached.Components.Get<ModelRenderer>();
						if ( cachedRenderer is not null ) cachedRenderer.Model = Model.Load( definition.WorldModel );
						cached.Enabled = true;
						cached.Network.Refresh();
						return WorldItemBoundaryAttempt.Applied();
					}
					catch ( Exception exception )
					{
						return WorldItemBoundaryAttempt.Transient( new OperationError(
							ErrorCode.InternalError,
							$"Committed world-item publication could not be refreshed: {exception.Message}" ) );
					}
				}
			}
			else _worldObjects.Remove( itemId );
		}

		GameObject? gameObject = null;
		try
		{
			gameObject = new GameObject( false, $"HL2RP World Item {itemId}" );
			gameObject.WorldPosition = new Vector3( world.Transform.PositionX, world.Transform.PositionY, world.Transform.PositionZ );
			gameObject.WorldRotation = new Rotation(
				world.Transform.RotationX, world.Transform.RotationY,
				world.Transform.RotationZ, world.Transform.RotationW );
			gameObject.WorldTransform = gameObject.WorldTransform.WithScale( 0.5f );
			var renderer = gameObject.AddComponent<ModelRenderer>();
			renderer.Model = Model.Load( definition.WorldModel );
			var collider = gameObject.AddComponent<BoxCollider>();
			collider.Scale = new Vector3( 24f, 24f, 24f );
			gameObject.AddComponent<HL2RPWorldItemPressable>().HostBind( itemId );
			if ( !gameObject.NetworkSpawn( new NetworkSpawnOptions
			{
				Owner = null!,
				StartEnabled = false,
				OwnerTransfer = OwnerTransfer.Fixed,
				OrphanedMode = NetworkOrphaned.Destroy
			} ) )
			{
				gameObject.Destroy();
				return WorldItemBoundaryAttempt.Transient( new OperationError(
					ErrorCode.InternalError,
					"Committed world item could not be network-published." ) );
			}
			gameObject.Enabled = true;
			gameObject.Network.Refresh();
			_worldObjects[itemId] = gameObject;
			return WorldItemBoundaryAttempt.Applied();
		}
		catch ( Exception exception )
		{
			if ( gameObject is not null && gameObject.IsValid() )
			{
				try { gameObject.Destroy(); }
				catch ( Exception cleanupException )
				{
					_worldObjects[itemId] = gameObject;
					return WorldItemBoundaryAttempt.Transient( new OperationError(
						ErrorCode.InternalError,
						$"Committed world item failed and its partial object could not be destroyed: " +
						$"{exception.Message}; cleanup: {cleanupException.Message}" ) );
				}
			}
			return WorldItemBoundaryAttempt.Transient( new OperationError(
				ErrorCode.InternalError,
				$"Committed world item could not be materialized: {exception.Message}" ) );
		}
	}

	private async Task ReconcileCommittedWorldItemAsync( ItemId itemId, string stage )
	{
		var receipt = await _worldReconciler.ReconcileCommittedAsync( itemId, CancellationToken.None );
		LogWorldItemReconciliation( receipt, stage );
	}

	private static void LogWorldItemReconciliation(
		WorldItemReconciliationReceipt receipt,
		string stage )
	{
		if ( receipt.Disposition != WorldItemReconciliationDisposition.Applied )
			Log.Warning(
				$"HL2RP_WORLD_ITEM_DEGRADED item={receipt.ItemId.Value:D} stage={stage} " +
				$"disposition={receipt.Disposition} attempt={receipt.Attempt} " +
				$"code={receipt.Error?.Code} message={receipt.Error?.Message}" );
	}

	private async ValueTask RunVerificationProbeAsync()
	{
		try
		{
			await RunVerificationProbeCoreAsync();
		}
		catch ( Exception exception )
		{
			// A failed probe must not leave a live host running on partially committed
			// probe data; verification runs are one-shot and their outcome is the
			// sentinel below plus the shutdown both branches share.
			Log.Error(
				$"HL2RP_PROBE_FAILED probe={_context.VerificationProbe} " +
				$"detail=\"{exception.GetType().Name}: {exception.Message}\"" );
		}
		finally
		{
			// The probe runs inside the maintenance supervisor. Starting shutdown is
			// synchronous, but awaiting it here would deadlock when application disposal
			// waits for this maintenance tick to return.
			if ( HexagonRuntimeSystem.Current is not null )
				_ = HexagonRuntimeSystem.Current.ShutdownAsync();
		}
	}

	private async ValueTask RunVerificationProbeCoreAsync()
	{
		// The probe always runs as the synthetic verification actor: it durably mutates
		// its store and terminates the host, so it must never hijack a real connected
		// player's binding even when a client races the first maintenance tick.
		if ( _verificationActor is not VerificationActorBinding verification )
			throw new InvalidOperationException( "Verification actor was not initialized." );
		var connectionId = verification.ConnectionId;
		var account = verification.AccountId;

		if ( _context.VerificationProbe == "commit" )
		{
			if ( !_characters.ListForAccount( account ).Any( value => value.Name == "Verification Citizen" ) )
			{
				var created = await _characters.CreateAsync( account, new CharacterCreationRequest
				{
					Name = "Verification Citizen",
					Description = "Deterministic persistence verification character.",
					Model = new DefinitionId( HL2RPIds.Models.Citizen01 ),
					Faction = new FactionId( HL2RPIds.Factions.Citizen ),
					Fields = new Dictionary<string, CreationValue>( StringComparer.Ordinal )
					{
						[HL2RPIds.CreationFields.Age] = CreationValue.Integer( 30 ),
						[HL2RPIds.CreationFields.Pronouns] = CreationValue.String( "they/them" ),
						[HL2RPIds.CreationFields.Origin] = CreationValue.Choice( "city_17" )
					}
				}, CancellationToken.None );
				if ( created.Failed ) throw new InvalidOperationException( created.Error!.Message );
			}
			var probeCharacter = RequireSingleProbeCharacter( account );
			var actor = LoadVerificationProbeCharacter( connectionId, account, probeCharacter );
			var machines = _features.Where( value => value.Value is HL2RPVendingMachineComponent ).ToArray();
			if ( machines.Length != 1 )
				throw new InvalidOperationException(
					$"Expected exactly one vending machine feature; found {machines.Length}." );
			var machine = machines[0];
			var actorState = ResolveInteractionActorState( actor.ConnectionId ) ??
				throw new InvalidOperationException( "Verification interaction actor is unavailable." );
			actorState.Body.WorldPosition = machine.Value.GameObject.WorldPosition;
			var opened = _interactions!.Begin(
				actor.ConnectionId, actor.AccountId, actor.CharacterId, InteractionTarget.SceneEntity( machine.Key ) );
			if ( opened.Failed || opened.Value.Session is not InteractionSession session )
				throw new InvalidOperationException( opened.Error?.Message ?? "Verification machine session was not opened." );
			var continued = _interactions.Continue(
				session.Id, actor.ConnectionId, actor.AccountId, actor.CharacterId, session.Target );
			if ( continued.Failed ) throw new InvalidOperationException( continued.Error!.Message );
			var main = MainInventory( probeCharacter.Id )!;
			var purchased = await _commerce!.PurchaseFromMachineAsync( actor, session.Id, main.Id );
			_interactions.Close( session.Id );
			if ( purchased.Failed ) throw new InvalidOperationException( purchased.Error!.Message );
			var digest = HL2RPRuntimeProjection.RecoveryDigest(
				_repositories, _context.Configuration.Snapshot() );
			Log.Info( $"HL2RP_PROBE_COMMITTED sequence={_context.Persistence.Health.Sequence} digest={digest}" );
		}
		else if ( _context.VerificationProbe == "recover" )
		{
			var probeCharacter = RequireSingleProbeCharacter( account );
			_ = LoadVerificationProbeCharacter( connectionId, account, probeCharacter );
			var main = MainInventory( probeCharacter.Id );
			if ( main is null || !main.Placements.Any( placement =>
				_repositories.Items.Find( DomainKeys.Item( placement.ItemId ) )?.Value.Definition.Value == HL2RPIds.Items.Water ) )
				throw new InvalidOperationException( "Verification machine purchase was not recovered." );
			var machineEntities = _repositories.SceneEntities.All().Select( value => value.Value )
				.Where( value => value.Kind == "vending_machine" ).ToArray();
			if ( machineEntities.Length != 1 )
				throw new InvalidOperationException(
					$"Expected exactly one persisted vending machine; found {machineEntities.Length}." );
			var machine = machineEntities[0];
			var machineState = HL2RPPersistence.MachineState.Deserialize( machine.State.Data, machine.State.TypeVersion );
			if ( machineState.CooldownUntilUtc is null || probeCharacter.Balance >= 25 )
				throw new InvalidOperationException( "Verification machine economy state was not recovered." );
			var digest = HL2RPRuntimeProjection.RecoveryDigest(
				_repositories, _context.Configuration.Snapshot() );
			Log.Info( $"HL2RP_PROBE_RECOVERED sequence={_context.Persistence.Health.Sequence} digest={digest}" );
		}
		else throw new InvalidOperationException( "Unknown verification probe." );
	}

	private CharacterRecord RequireSingleProbeCharacter( AccountId account )
	{
		var candidates = _characters.ListForAccount( account )
			.Where( value => value.Name == "Verification Citizen" ).ToArray();
		if ( candidates.Length != 1 )
			throw new InvalidOperationException(
				$"Expected exactly one verification character; found {candidates.Length}." );
		return candidates[0];
	}

	private void SetBindingCharacter( ConnectionId connectionId, CharacterId? characterId )
	{
		var binding = _clients[connectionId];
		if ( binding.CharacterId == characterId ) return;
		_clients[connectionId] = binding with { CharacterId = characterId };
		_projectionIndex.ObserveCharacterBindingChanged( connectionId, binding.CharacterId, characterId );
		RefreshLiveConnection( connectionId );
	}

	private InventoryActor LoadVerificationProbeCharacter(
		ConnectionId connectionId,
		AccountId accountId,
		CharacterRecord character )
	{
		if ( _verificationActor is not VerificationActorBinding binding ||
			binding.ConnectionId != connectionId || binding.AccountId != accountId )
			throw new InvalidOperationException( "Verification actor identity is unavailable." );
		var main = MainInventory( character.Id ) ??
			throw new InvalidOperationException( "Verification character main inventory was not recovered." );
		var modelPath = _characterModels.ResolvePath( character.Model );
		if ( modelPath.Failed ) throw new InvalidOperationException( modelPath.Error!.Message );
		var body = binding.Body;
		if ( body is null || !body.IsValid() )
		{
			body = new GameObject( true, "HL2RP Verification Actor" );
			ConfigureForcefieldCollisionTags( body, character );
			body.AddComponent<SkinnedModelRenderer>().Model = Model.Load( modelPath.Value );
		}
		_verificationActor = binding with { CharacterId = character.Id, Body = body };
		_access.RevokeConnection( connectionId );
		_access.OpenConnection( connectionId );
		_access.Grant( new InventoryGrant
		{
			ConnectionId = connectionId,
			CharacterId = character.Id,
			InventoryId = main.Id,
			Capabilities = CharacterCapabilities,
			Kind = InventoryGrantKind.Character
		} );
		return new InventoryActor( connectionId, accountId, character.Id );
	}

	private static Guid DeterministicGuid( string value ) =>
		HL2RPPresentationComposer.DeterministicGuid( value );

	private int RequiredConfigurationInt( string key )
	{
		var configured = _context.Configuration.Get<int>( key );
		if ( configured.Failed )
			throw new InvalidOperationException( configured.Error!.Message );
		return configured.Value.Value;
	}

	/// <summary>
	/// The configured name-similarity strictness. The schema validates the value on write, so a
	/// value that fails to parse here means the store disagrees with the schema and the host
	/// refuses to start rather than silently falling back to a different rule than the operator
	/// asked for.
	/// </summary>
	private CharacterRules.NameUniqueness RequiredNameUniqueness()
	{
		var configured = _context.Configuration.Get<string>( HL2RPIds.Configs.CharacterNameUniqueness );
		if ( configured.Failed )
			throw new InvalidOperationException( configured.Error!.Message );
		if ( !Enum.TryParse<CharacterRules.NameUniqueness>( configured.Value.Value, true, out var parsed ) )
			throw new InvalidOperationException(
				$"Configuration '{HL2RPIds.Configs.CharacterNameUniqueness}' is not a known strictness." );
		return parsed;
	}

	private static void ConfigureForcefieldCollisionTags( GameObject body, CharacterRecord character )
	{
		body.Tags.Add( "playerclip" );
		if ( character.Faction.Value is HL2RPIds.Factions.CivilProtection or
			HL2RPIds.Factions.Overwatch or HL2RPIds.Factions.CityAdministration )
			body.Tags.Add( "combine" );
	}

	private static void ConfigureAuthoritativePlayerBody( GameObject body, CharacterRecord character )
	{
		ConfigureForcefieldCollisionTags( body, character );
		var controller = body.GetOrAddComponent<PlayerController>();
		// PlayerController leaves this editable tag set null for components created
		// entirely at runtime. Initialize it before the body is enabled so both the
		// generated colliders and authority traces receive the intended tags.
		controller.BodyCollisionTags = new TagSet();
		controller.BodyCollisionTags.Add( "playerclip" );
		if ( body.Tags.Contains( "combine" ) ) controller.BodyCollisionTags.Add( "combine" );
	}

	internal void ClearCombatSessions( InventoryActor actor )
	{
		_interactions?.CharacterChanged( actor.ConnectionId, actor.CharacterId );
		_combatIntent?.ClearCharacter( actor.CharacterId );
	}

	private sealed class HL2RPEncounterAuthorizer : ICharacterEncounterAuthorizer
	{
		private readonly HL2RPHostApplication _owner;
		public HL2RPEncounterAuthorizer( HL2RPHostApplication owner ) => _owner = owner;

		public OperationResult Authorize( InventoryActor actor, CharacterId subjectCharacterId )
		{
			if ( subjectCharacterId == actor.CharacterId )
				return OperationResult.Failure( ErrorCode.PolicyDenied, "A character cannot introduce themselves." );
			var sourcePlayer = _owner.FindPlayer( actor.CharacterId );
			var targetPlayer = _owner.FindPlayer( subjectCharacterId );
			if ( sourcePlayer is null || targetPlayer is null ||
				!sourcePlayer.TryGetUsableAuthoritativeBody( out var source ) ||
				!targetPlayer.TryGetUsableAuthoritativeBody( out var target ) )
				return OperationResult.Failure( ErrorCode.NotFound, "Encounter target is not active." );
			if ( Vector3.DistanceBetween( sourcePlayer.AuthoritativeWorldPosition, targetPlayer.AuthoritativeWorldPosition ) > 130f )
				return OperationResult.Failure( ErrorCode.PolicyDenied, "Encounter target is out of range." );
			var trace = _owner._context.Scene.Trace.Ray( sourcePlayer.AuthoritativeWorldPosition, targetPlayer.AuthoritativeWorldPosition )
				.IgnoreGameObjectHierarchy( source.Root )
				.WithoutTags( "prediction" )
				.Run();
			return !trace.Hit || HL2RPObjectHierarchy.Contains( target, trace.GameObject, current => current.Parent )
				? OperationResult.Success()
				: OperationResult.Failure( ErrorCode.PolicyDenied, "Encounter target is not visible." );
		}
	}

	private sealed record ClientBinding(
		Connection Connection,
		AccountId AccountId,
		HexPlayerBody Player,
		CharacterId? CharacterId );

	private sealed record VerificationActorBinding(
		ConnectionId ConnectionId,
		AccountId AccountId,
		CharacterId? CharacterId,
		GameObject? Body );

	private sealed class HL2RPItemActionPresentationHandler : IEventHandler<ItemActionCommittedEvent>
	{
		private readonly HL2RPHostApplication _owner;
		public HL2RPItemActionPresentationHandler( HL2RPHostApplication owner ) => _owner = owner;
		public void Handle( ItemActionCommittedEvent committed ) => _owner.PresentItemAction( committed );
	}

	private sealed class HL2RPCommittedChatSink : ICommittedChatDeliverySink
	{
		private readonly HL2RPHostApplication _owner;
		public HL2RPCommittedChatSink( HL2RPHostApplication owner ) => _owner = owner;
		public void Deliver( ChatDelivery delivery, CharacterRecord author ) =>
			_owner.DeliverCommittedChat( delivery, author );
	}

	private sealed class HL2RPCombatLifecycleBoundary : ICombatLifecycleBoundary
	{
		private readonly HL2RPHostApplication _owner;
		public HL2RPCombatLifecycleBoundary( HL2RPHostApplication owner ) => _owner = owner;
		public void ClearSessions( InventoryActor actor )
		{
			_owner.CancelTimedActionsForLifecycle( actor.ConnectionId, actor.CharacterId );
			_owner._access.RevokeCharacter( actor.ConnectionId, actor.CharacterId );
			_owner._interactions?.CharacterChanged( actor.ConnectionId, actor.CharacterId );
			_owner._combatIntent?.ClearCharacter( actor.CharacterId );
			if ( _owner._clients.TryGetValue( actor.ConnectionId, out var binding ) &&
				binding.CharacterId == actor.CharacterId )
			{
				var stripped = binding.Player.HostDisembody();
				if ( stripped.Failed ) Log.Error( $"Failed to strip dead player body: {stripped.Error!.Message}" );
			}
			_owner.RefreshLiveConnection( actor.ConnectionId );
		}
		public void PublishDeath( DeathTransitionReceipt transition )
		{
			_owner._presentationInvalidation.TrackDeathDeadline(
				transition.Respawn.CharacterId, transition.Respawn.RespawnAvailableAtUtc );
			if ( transition.DroppedPistol is not null )
				_owner.TrackLifecycle(
					$"world-item-death-drop:{transition.DroppedPistol.ItemId.Value:D}",
					() => _owner.ReconcileCommittedWorldItemAsync(
						transition.DroppedPistol.ItemId,
						"death-drop" ) );
			// The command outcome owns publication so the fire and optional death
			// receipts produce one assembled snapshot for the resulting revision.
		}
		// The host finishes body, grant, and health restoration before publishing a
		// respawn. Publishing from inside CombatLifecycleService would expose an
		// alive snapshot before those authorities are restored.
		public void PublishRespawn( DeathRespawnState state ) { }
	}

	private sealed class HL2RPScannerCommitSink : IScannerCommitSink
	{
		private readonly HL2RPHostApplication _owner;
		public HL2RPScannerCommitSink( HL2RPHostApplication owner ) => _owner = owner;

		public void Observe( ScannerInputPersistenceReceipt receipt )
		{
			try
			{
				_owner.PublishChanges(
					new HL2RPPresentationChangeSet
					{
						Connections = new[] { receipt.Session.Actor.ConnectionId },
						SceneEntities = new[] { receipt.Session.ScannerId }
					},
					receipt.Commit );
			}
			catch ( Exception exception )
			{
				Log.Error( exception,
					$"HL2RP scanner input receipt {receipt.CommitSequence} could not be projected." );
			}
		}

		public void Observe( ScannerPilotCleanupReceipt receipt )
		{
			try
			{
				LogScannerBoundaryError( receipt.BoundaryError );
				_owner.PublishChanges(
					new HL2RPPresentationChangeSet
					{
						Connections = new[] { receipt.Session.Actor.ConnectionId },
						SceneEntities = new[] { receipt.Session.ScannerId }
					},
					receipt.Commit );
			}
			catch ( Exception exception )
			{
				Log.Error( exception,
					$"HL2RP scanner cleanup receipt {receipt.CommitSequence} could not be projected." );
			}
		}
	}
}
