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
public sealed class HL2RPHostApplication : IHexHostApplication, IWorldItemReconciliationBoundary
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
			inventoryHeight: characterInventoryHeight );
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
		_ = binding.Player.HostStripAuthoritativeBody();
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
		if ( _disposed ) return OperationResult.Failure( ErrorCode.Conflict, "HL2RP host is disposed." );
		if ( !_clients.TryGetValue( new ConnectionId( actor.Connection.Id ), out var binding ) ||
			binding.AccountId != actor.AccountId )
			return OperationResult.Failure( ErrorCode.Unauthorized, "RPC actor is not bound to this host scope." );
		var connectionId = new ConnectionId( actor.Connection.Id );
		var projectionDelta = new CommandProjectionDelta();
		var characterBefore = binding.CharacterId;
		var mainBefore = characterBefore is CharacterId activeBefore ? MainInventory( activeBefore )?.Id : null;
		var restraintTieBefore = command is RunSchemaCommandCommand { CommandId: HL2RPIds.Commands.RestraintSet } &&
			mainBefore is InventoryId restraintInventory
			? _repositories.Inventories.Find( DomainKeys.Inventory( restraintInventory ) )?.Value.Placements
				.Select( placement => _repositories.Items.Find( DomainKeys.Item( placement.ItemId ) )?.Value )
				.FirstOrDefault( item => item?.Definition.Value == HL2RPIds.Items.ZipTie )?.Id
			: null;

		OperationResult result = command switch
		{
			RequestCharacterListCommand => ListCharacters( actor ),
			CreateCharacterCommand create => await CreateAsync( actor, create, projectionDelta, cancellationToken ),
			LoadCharacterCommand load => await LoadAsync( actor, load.CharacterId, projectionDelta, cancellationToken ),
			DeleteCharacterCommand delete => await DeleteAsync( actor, delete.CharacterId, projectionDelta, cancellationToken ),
			UnloadCharacterCommand => await UnloadAsync( actor, projectionDelta, cancellationToken ),
			MoveInventoryItemCommand move => await MoveAsync( actor, move, projectionDelta, cancellationToken ),
			RunItemActionCommand action => await RunItemActionAsync( actor, action, projectionDelta, cancellationToken ),
			DropItemCommand drop => await DropAsync( actor, drop, projectionDelta, cancellationToken ),
			PickUpItemCommand pickup => await PickupAsync( actor, pickup, projectionDelta, cancellationToken ),
			SendChatCommand chat => SendChat( actor, chat ),
			CancelActionCommand cancel => CancelAction( actor, cancel.InstanceId ),
			BeginInteractionCommand begin => await BeginInteractionAsync( actor, begin.Target, projectionDelta, cancellationToken ),
			ContinueInteractionCommand continuation => ContinueInteraction( actor, continuation ),
			CloseInteractionCommand close => CloseInteraction( actor, close.SessionId ),
			RunSchemaCommandCommand schema => await RunSchemaCommandAsync( actor, schema, projectionDelta, cancellationToken ),
			_ => OperationResult.Failure( ErrorCode.UnknownDefinition, "Client command is not registered by HL2RP." )
		};

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
			if ( _clients.Count == 0 && _verificationActor is null ) return;
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
					var stripped = binding.Player.HostStripAuthoritativeBody();
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
		var body = actor.Player.HostPrepareAuthoritativeBody( candidate =>
		{
			ConfigureAuthoritativePlayerBody( candidate, document.Value );
			var renderer = candidate.AddComponent<SkinnedModelRenderer>();
			renderer.Model = Model.Load( modelPath.Value );
		} );
		if ( body.Failed ) return Failure( body.Error! );
		using var preparedBody = body.Value;

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

		var activatedBody = preparedBody.TryActivate();
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

	private async ValueTask<OperationResult> ClearRaisedPistolsAsync(
		InventoryActor actor,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		if ( _combatIntent is null ) return OperationResult.Success();
		var cleared = await _combatIntent.ClearCharacterAsync( actor.CharacterId, cancellationToken );
		if ( cleared.Failed ) return Failure( cleared.Error! );
		if ( cleared.Value.Commit is not null )
		{
			projectionDelta.Observe( cleared.Value.Commit );
			projectionDelta.Connections.Add( actor.ConnectionId );
			projectionDelta.Characters.Add( actor.CharacterId );
			projectionDelta.Items.UnionWith( cleared.Value.ChangedPistols );
			var main = MainInventory( actor.CharacterId );
			if ( main is not null ) projectionDelta.Inventories.Add( main.Id );
		}
		return OperationResult.Success();
	}

	private async Task ClearRaisedPistolsForLifecycleAsync(
		InventoryActor actor,
		CancellationToken cancellationToken )
	{
		var projectionDelta = new CommandProjectionDelta();
		var cleared = await ClearRaisedPistolsAsync( actor, projectionDelta, cancellationToken );
		if ( cleared.Failed )
		{
			Log.Error(
				$"HL2RP pistol lifecycle cleanup failed for character {actor.CharacterId.Value:D}: " +
				cleared.Error!.Message );
			return;
		}
		if ( projectionDelta.Receipts.Count == 0 ) return;
		PublishChanges(
			new HL2RPPresentationChangeSet
			{
				Connections = projectionDelta.Connections.ToArray(),
				Characters = projectionDelta.Characters.ToArray(),
				Inventories = projectionDelta.Inventories.ToArray(),
				Items = projectionDelta.Items.ToArray(),
				RebuildLiveInventory = projectionDelta.Inventories.Count > 0 || projectionDelta.Items.Count > 0
			},
			projectionDelta.Receipts );
	}

	private void UnloadBinding( ConnectionId connectionId, CharacterId characterId )
	{
		CancelTimedActionsForLifecycle( connectionId, characterId );
		ObserveCharacterExit( connectionId, characterId );
		_itemActionPresentations.Remove( connectionId );
		_civicSubjects.ClearConnection( connectionId );
		var binding = _clients[connectionId];
		_ = binding.Player.HostStripAuthoritativeBody();
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
			CancelToken( action.Cancellation );
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
			CancelToken( action.Cancellation );
	}

	private void CancelAllTimedActionsForLifecycle()
	{
		foreach ( var action in _activeRestraintActions.CancelAllForLifecycle() )
		{
			CancelToken( action.Cancellation );
			if ( _restraints is not null )
			{
				var cancelled = _restraints.Cancel( action.Ticket.TicketId, action.Actor );
				if ( cancelled.Failed && cancelled.Error!.Code != ErrorCode.Unauthorized )
					Log.Warning( $"HL2RP restraint shutdown cancellation degraded: {cancelled.Error.Message}" );
			}
		}
		foreach ( var action in _activePistolActions.CancelAllForLifecycle() )
			CancelToken( action.Cancellation );
	}

	private static void CancelToken( CancellationTokenSource cancellation )
	{
		try
		{
			cancellation.Cancel();
		}
		catch ( ObjectDisposedException )
		{
			// The action owner may have completed between the atomic state change
			// and delivery of the best-effort pre-commit cancellation signal.
		}
	}

	private async ValueTask<OperationResult> MoveAsync(
		RpcActor rpc,
		MoveInventoryItemCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		var result = await _inventory.MoveCommittedAsync(
			actor.Value, command.SourceId, command.TargetId, command.ItemId, command.X, command.Y, cancellationToken );
		if ( result.Succeeded )
		{
			projectionDelta.Observe( result.Value );
			_bags?.ItemMoved( command.ItemId );
		}
		return Untyped( result );
	}

	private async ValueTask<OperationResult> RunItemActionAsync(
		RpcActor rpc,
		RunItemActionCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		var item = _repositories.Items.Find( DomainKeys.Item( command.ItemId ) )?.Value;
		if ( item is null ) return OperationResult.Failure( ErrorCode.NotFound, "Item was not found." );
		if ( !_executableActions.TryResolve( item.Definition, command.ActionId, out var executable ) )
			return OperationResult.Failure( ErrorCode.UnknownDefinition, "Item action has no executable host route." );
		if ( executable.Route == ExecutableItemActionRoute.BagInteraction )
			return Untyped( _bags!.Open( actor.Value, command.InventoryId, command.ItemId ) );
		if ( executable.Route == ExecutableItemActionRoute.TokenSplit )
		{
			var amount = new HL2RPCommandArguments( command.Arguments ).Integer( "amount" );
			if ( amount.Failed || amount.Value is <= 0 or > int.MaxValue )
				return OperationResult.Failure( ErrorCode.InvalidArgument, "Token split amount is invalid." );
			var split = await _tokens!.SplitAsync(
				actor.Value, command.InventoryId, command.ItemId, (int)amount.Value, cancellationToken );
			if ( split.Succeeded )
			{
				projectionDelta.Observe( split.Value );
				projectionDelta.Inventories.Add( command.InventoryId );
				projectionDelta.Items.Add( split.Value.PrimaryItemId );
				if ( split.Value.SecondaryItemId is ItemId secondary ) projectionDelta.Items.Add( secondary );
			}
			return Untyped( split );
		}
		if ( executable.Route == ExecutableItemActionRoute.TokenCombine )
		{
			var other = new HL2RPCommandArguments( command.Arguments ).Guid( "other_item_id" );
			if ( other.Failed ) return Failure( other.Error! );
			var combined = await _tokens!.CombineAsync(
				actor.Value, command.InventoryId, command.ItemId, new ItemId( other.Value ), cancellationToken );
			if ( combined.Succeeded )
			{
				projectionDelta.Observe( combined.Value );
				projectionDelta.Inventories.Add( command.InventoryId );
				projectionDelta.Items.Add( combined.Value.PrimaryItemId );
				if ( combined.Value.SecondaryItemId is ItemId secondary ) projectionDelta.Items.Add( secondary );
			}
			return Untyped( combined );
		}
		if ( executable.Route == ExecutableItemActionRoute.CombineLockInstall )
		{
			var session = CurrentSession( actor.Value, InteractionSessionKind.Door );
			if ( session is null )
				return OperationResult.Failure( ErrorCode.Unauthorized, "A current door session is required." );
			var installed = await _combineLocks!.InstallAsync(
				actor.Value, session.Id, command.InventoryId, command.ItemId, cancellationToken );
			if ( installed.Succeeded )
			{
				projectionDelta.Observe( installed.Value );
				projectionDelta.SceneEntities.Add( installed.Value.DoorEntityId );
			}
			return Untyped( installed );
		}
		if ( executable.Route == ExecutableItemActionRoute.HealthVialConsume )
		{
			var consumed = await _healthVials!.ConsumeAsync(
				actor.Value, command.InventoryId, command.ItemId, cancellationToken );
			if ( consumed.Succeeded )
			{
				projectionDelta.Observe( consumed.Value );
				projectionDelta.Inventories.Add( command.InventoryId );
				projectionDelta.Items.Add( consumed.Value.VialItemId );
			}
			return Untyped( consumed );
		}
		if ( executable.Route == ExecutableItemActionRoute.RadioTuning )
		{
			var frequency = new HL2RPCommandArguments( command.Arguments ).String( "frequency" );
			if ( frequency.Failed ) return Failure( frequency.Error! );
			var tuned = await _radio!.TuneAsync(
				actor.Value, command.InventoryId, command.ItemId, frequency.Value, cancellationToken );
			if ( tuned.Succeeded )
			{
				projectionDelta.Observe( tuned.Value );
				projectionDelta.Items.Add( command.ItemId );
			}
			return Untyped( tuned );
		}
		if ( executable.Route == ExecutableItemActionRoute.RequestDevice )
		{
			var text = new HL2RPCommandArguments( command.Arguments ).String( "text" );
			if ( text.Failed ) return Failure( text.Error! );
			var requested = await _requests!.SendAsync(
				actor.Value, command.InventoryId, command.ItemId, text.Value, cancellationToken );
			if ( requested.Succeeded )
			{
				projectionDelta.Observe( requested.Value );
				projectionDelta.Items.Add( command.ItemId );
			}
			return Untyped( requested );
		}
		if ( executable.Route == ExecutableItemActionRoute.NoteEditor )
		{
			var body = new HL2RPCommandArguments( command.Arguments ).String( "body", true );
			if ( body.Failed ) return Failure( body.Error! );
			var edited = await _documents!.EditNoteAsync(
				actor.Value, command.InventoryId, command.ItemId, body.Value, cancellationToken );
			if ( edited.Succeeded )
			{
				projectionDelta.Observe( edited.Value );
				projectionDelta.Items.Add( command.ItemId );
			}
			return Untyped( edited );
		}
		if ( executable.Route == ExecutableItemActionRoute.RestraintIntent )
			return await SetRestraintAsync(
				actor.Value, new HL2RPCommandArguments( command.Arguments ), projectionDelta, cancellationToken );
		if ( executable.Route == ExecutableItemActionRoute.CombatFireIntent )
			return await FirePistolAsync(
				actor.Value, command.InventoryId, command.ItemId, projectionDelta, cancellationToken );
		var executed = await _itemActions.ExecuteCommittedAsync(
			actor.Value,
			command.InventoryId,
			command.ItemId,
			command.ActionId,
			command.Arguments,
			cancellationToken );
		if ( executed.Succeeded )
		{
			projectionDelta.Observe( executed.Value );
			projectionDelta.Inventories.Add( command.InventoryId );
			projectionDelta.Items.Add( command.ItemId );
		}
		return Untyped( executed );
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
		var transform = DropTransform( authoritativeBody );
		if ( !IsFinite( transform ) )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Authoritative drop transform is not finite." );
		var result = await _worldItems.DropCommittedAsync(
			actor.Value, command.SourceId, command.ItemId, transform, cancellationToken );
		if ( result.Succeeded )
		{
			projectionDelta.Observe( result.Value );
			try { _bags?.ItemMoved( command.ItemId ); }
			catch ( Exception exception )
			{
				Log.Warning( $"HL2RP_DROP_DEGRADED item={command.ItemId.Value:D} " +
					$"stage=bag_invalidation message={exception.Message}" );
			}
			var reconciled = await _worldReconciler.ReconcileCommittedAsync(
				command.ItemId,
				CancellationToken.None );
			LogWorldItemReconciliation( reconciled, "drop" );
			return WorldItemCommandResult( reconciled );
		}
		return Untyped( result );
	}

	private async ValueTask<OperationResult> PickupAsync(
		RpcActor rpc,
		PickUpItemCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		var result = await _worldItems.PickUpCommittedAsync(
			actor.Value, command.ItemId, command.DestinationId, cancellationToken );
		if ( result.Succeeded )
		{
			projectionDelta.Observe( result.Value );
			try { _bags?.ItemMoved( command.ItemId ); }
			catch ( Exception exception )
			{
				Log.Warning( $"HL2RP_PICKUP_DEGRADED item={command.ItemId.Value:D} " +
					$"stage=bag_invalidation message={exception.Message}" );
			}
			var reconciled = await _worldReconciler.ReconcileCommittedAsync(
				command.ItemId,
				CancellationToken.None );
			LogWorldItemReconciliation( reconciled, "pickup" );
			return WorldItemCommandResult( reconciled );
		}
		return Untyped( result );
	}

	private OperationResult SendChat( RpcActor rpc, SendChatCommand command )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		if ( command.ChannelId == HL2RPIds.Channels.Request )
			return OperationResult.Failure(
				ErrorCode.PolicyDenied, "Request traffic requires a validated request-device action." );
		var character = FindActiveCharacter( actor.Value.ConnectionId )!;
		var result = _chat!.Send( actor.Value, character, command.ChannelId, command.Text );
		if ( result.Failed ) return Failure( result.Error! );
		var author = _repositories.Characters.Find( DomainKeys.Character( result.Value.AuthorCharacterId ) )?.Value;
		if ( author is null ) return OperationResult.Failure( ErrorCode.NotFound, "Chat author is unavailable." );
		DeliverCommittedChat( result.Value, author );
		return OperationResult.Success();
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
		var target = ToTarget( input );
		if ( target.Failed ) return Failure( target.Error! );
		if ( target.Value.Kind == InteractionTargetKind.SceneEntity )
		{
			var targetId = new SceneEntityId( target.Value.Id );
			if ( _features.TryGetValue( targetId, out var scannerFeature ) )
			{
				if ( scannerFeature is HL2RPScannerDockComponent dock )
				{
					if ( dock.LinkedDroneId is not SceneEntityId droneId )
						return OperationResult.Failure( ErrorCode.ConfigurationInvalid, "Scanner dock has no linked drone." );
					return await EnterScannerTargetAsync(
						actor.Value, droneId, projectionDelta, cancellationToken );
				}
				if ( scannerFeature is HL2RPScannerDroneComponent )
					return await EnterScannerTargetAsync(
						actor.Value, targetId, projectionDelta, cancellationToken );
			}
		}
		var opened = _interactions!.Begin(
			actor.Value.ConnectionId, actor.Value.AccountId, actor.Value.CharacterId, target.Value );
		if ( opened.Failed ) return Failure( opened.Error! );
		if ( opened.Value.Session is InteractionSession observedSession )
			_projectionIndex.ObserveSceneSession( observedSession );
		if ( target.Value.Kind != InteractionTargetKind.SceneEntity ) return OperationResult.Success();
		var sceneId = new SceneEntityId( target.Value.Id );
		if ( !_features.TryGetValue( sceneId, out var feature ) )
			return OperationResult.Failure( ErrorCode.NotFound, "Scene feature is unavailable." );
		if ( feature is HL2RPForcefieldComponent )
		{
			var toggled = await _sceneBehavior!.ToggleForcefieldAsync(
				actor.Value, sceneId, cancellationToken );
			if ( toggled.Succeeded )
			{
				projectionDelta.Observe( toggled.Value );
				projectionDelta.SceneEntities.Add( toggled.Value.SceneEntityId );
			}
			return Untyped( toggled );
		}
		if ( opened.Value.Session is not InteractionSession session ) return OperationResult.Success();
		if ( feature is HL2RPMachineComponent )
		{
			var main = MainInventory( actor.Value.CharacterId );
			if ( main is null )
				return OperationResult.Failure( ErrorCode.NotFound, "Character main inventory was not found." );
			var purchased = await _commerce!.PurchaseFromMachineAsync(
				actor.Value, session.Id, main.Id, cancellationToken );
			if ( purchased.Succeeded )
			{
				projectionDelta.Observe( purchased.Value );
				projectionDelta.SceneEntities.Add( purchased.Value.SceneEntityId );
				projectionDelta.Inventories.Add( main.Id );
				projectionDelta.Items.UnionWith( purchased.Value.ItemIds );
			}
			return Untyped( purchased );
		}
		if ( feature is HL2RPDoorComponent )
		{
			var toggled = await _sceneBehavior!.ToggleDoorAsync(
				actor.Value, session.Id, cancellationToken );
			if ( toggled.Succeeded )
			{
				projectionDelta.Observe( toggled.Value );
				projectionDelta.SceneEntities.Add( toggled.Value.SceneEntityId );
			}
			return Untyped( toggled );
		}
		return OperationResult.Success();
	}

	private OperationResult ContinueInteraction( RpcActor rpc, ContinueInteractionCommand command )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		var target = ToTarget( command.Target );
		if ( target.Failed ) return Failure( target.Error! );
		return Untyped( _interactions!.Continue(
			command.SessionId, actor.Value.ConnectionId, actor.Value.AccountId,
			actor.Value.CharacterId, target.Value ) );
	}

	private OperationResult CloseInteraction( RpcActor rpc, InteractionSessionId sessionId )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		var session = _sessions!.ActiveSessions.SingleOrDefault( value => value.Id == sessionId );
		if ( session is null || session.ConnectionId != actor.Value.ConnectionId || session.CharacterId != actor.Value.CharacterId )
			return OperationResult.Failure( ErrorCode.Unauthorized, "Interaction session is not bound to the actor." );
		_interactions!.Close( sessionId );
		return OperationResult.Success();
	}

	private OperationResult CancelAction( RpcActor rpc, Guid instanceId )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		var restraintCancellation = _activeRestraintActions.TryCancel(
			actor.Value.ConnectionId,
			candidate => candidate.Ticket.TicketId.Value == instanceId && candidate.Actor == actor.Value,
			out var action );
		if ( restraintCancellation == HL2RPTimedActionCancelOutcome.Cancelled )
		{
			CancelToken( action!.Cancellation );
			var result = _restraints!.Cancel( action.Ticket.TicketId, action.Actor );
			PublishConnections( new[] { actor.Value.ConnectionId }, invalidateRuntime: true );
			return result;
		}
		if ( restraintCancellation == HL2RPTimedActionCancelOutcome.CommitOwned )
			return OperationResult.Failure( ErrorCode.Conflict,
				"Action commit is already in progress and can no longer be cancelled." );
		var pistolCancellation = _activePistolActions.TryCancel(
			actor.Value.ConnectionId,
			candidate => candidate.InstanceId == instanceId && candidate.Actor == actor.Value,
			out var pistol );
		if ( pistolCancellation == HL2RPTimedActionCancelOutcome.Cancelled )
		{
			CancelToken( pistol!.Cancellation );
			_combatIntent!.ClearCharacter( actor.Value.CharacterId );
			PublishConnections( new[] { actor.Value.ConnectionId }, invalidateRuntime: true );
			return OperationResult.Success();
		}
		if ( pistolCancellation == HL2RPTimedActionCancelOutcome.CommitOwned )
			return OperationResult.Failure( ErrorCode.Conflict,
				"Action commit is already in progress and can no longer be cancelled." );
		return OperationResult.Failure( ErrorCode.Unauthorized, "Action instance is not bound to the actor." );
	}

	private async ValueTask<OperationResult> FirePistolAsync(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId pistolId,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var raised = _pistol!.IsHostRaised( actor, inventoryId, pistolId );
		if ( raised.Failed ) return Failure( raised.Error! );
		CancellationTokenSource? linked = null;
		ActivePistolRaiseAction? active = null;
		var durableSuccess = false;
		if ( !raised.Value )
		{
			linked = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
			active = new ActivePistolRaiseAction(
				actor, Guid.NewGuid(), _clock.UtcNow + PistolCombatService.DefaultRaiseDelay, linked );
			if ( !_activePistolActions.TryAdd( actor.ConnectionId, active ) )
			{
				linked.Dispose();
				return OperationResult.Failure( ErrorCode.Conflict, "Another pistol raise is active." );
			}
			PublishConnections( new[] { actor.ConnectionId }, invalidateRuntime: true );
		}
		OperationResult result;
		try
		{
			var fired = await _combatIntent!.FireAsync(
				new CombatFireIntent( actor, inventoryId, pistolId, () =>
					active is null || _activePistolActions.TryClaimCommit( actor.ConnectionId, active ) ),
				linked?.Token ?? cancellationToken );
			if ( fired.Succeeded )
			{
				projectionDelta.Observe( fired.Value.Fire.Commit );
				projectionDelta.Inventories.Add( inventoryId );
				projectionDelta.Items.Add( pistolId );
				if ( fired.Value.PlayerDamage is PlayerCombatDamageOutcome playerDamage )
				{
					projectionDelta.Connections.Add( playerDamage.Target.Actor.ConnectionId );
					projectionDelta.Characters.Add( playerDamage.Target.Actor.CharacterId );
					projectionDelta.Inventories.Add( playerDamage.Target.InventoryId );
					if ( playerDamage.VestItemId is ItemId vestId ) projectionDelta.Items.Add( vestId );
				}
				if ( fired.Value.Death is DeathTransitionReceipt death )
				{
					if ( death.Commit is not null ) projectionDelta.Observe( death.Commit );
					projectionDelta.Broadcast = true;
					projectionDelta.RebuildLiveInventory = death.DroppedPistol is not null;
					projectionDelta.RebuildCombatTargets = true;
					if ( death.BoundaryError is OperationError boundaryError )
						Log.Warning(
							$"HL2RP_DEATH_BOUNDARY_DEGRADED character={death.Respawn.CharacterId.Value:D} " +
							$"code={boundaryError.Code} message={boundaryError.Message}" );
				}
				if ( fired.Value.DegradedDeathTransition is OperationError degraded )
					Log.Warning(
						$"HL2RP_COMBAT_DEGRADED character={actor.CharacterId.Value:D} " +
						$"code={degraded.Code} message={degraded.Message}" );
				durableSuccess = true;
			}
			result = Untyped( fired );
		}
		catch ( OperationCanceledException )
		{
			result = OperationResult.Failure( ErrorCode.Conflict, "Pistol raise was cancelled." );
		}
		catch ( Exception exception )
		{
			Log.Error( exception, $"HL2RP pistol fire failed for character {actor.CharacterId.Value:D}." );
			result = OperationResult.Failure(
				ErrorCode.InternalError,
				"Pistol firing failed unexpectedly." );
		}

		HL2RPTimedActionCompletion completion;
		try
		{
			completion = active is null
				? default
				: _activePistolActions.Complete( actor.ConnectionId, active, durableSuccess );
		}
		finally
		{
			linked?.Dispose();
		}
		if ( completion.PublishFailure )
			PublishConnections( new[] { actor.ConnectionId }, invalidateRuntime: true );
		if ( active is not null && completion.LifecycleCleanupRequested )
		{
			var cleanup = await ClearRaisedPistolsAsync( active.Actor, projectionDelta, CancellationToken.None );
			if ( cleanup.Failed )
				Log.Error(
					$"HL2RP late pistol lifecycle cleanup failed for character {active.Actor.CharacterId.Value:D}: " +
					cleanup.Error!.Message );
		}
		return result;
	}

	private async ValueTask<OperationResult> RunSchemaCommandAsync(
		RpcActor rpc,
		RunSchemaCommandCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		if ( !_context.Schema.Commands.TryGet( command.CommandId, out var definition ) )
			return OperationResult.Failure( ErrorCode.UnknownDefinition, "Schema command is not registered." );
		if ( command.CommandId is HL2RPIds.Commands.EntitlementQuery or
			HL2RPIds.Commands.EntitlementGrant or HL2RPIds.Commands.EntitlementRevoke )
			return await RunEntitlementCommandAsync(
				rpc, command, projectionDelta, cancellationToken );
		var actor = RequireInventoryActor(
			rpc, allowDead: command.CommandId == HL2RPIds.Commands.CombatRespawn );
		if ( actor.Failed ) return Failure( actor.Error! );
		if ( definition!.PermissionId is not null &&
			!_featureAuthorization.HasPermission( actor.Value.AccountId, actor.Value.CharacterId, definition.PermissionId ) )
			return OperationResult.Failure( ErrorCode.Unauthorized, $"Permission '{definition.PermissionId}' is required." );
		var arguments = new HL2RPCommandArguments( command.Arguments );
		return command.CommandId switch
		{
			HL2RPIds.Commands.CivicData => CivicData( actor.Value, arguments ),
			HL2RPIds.Commands.CityObjectives => await SetObjectivesAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.Priority => await SetPriorityAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.RadioFrequency => await TuneRadioAsync( actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.Introduce => await IntroduceAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.DoorOwnership => await DoorOwnershipAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.AdministrationAudit => PublishAdministrationAudit( actor.Value ),
			HL2RPIds.Commands.CommerceBuy => await BuyAsync( actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.CommerceSell => await SellAsync( actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.PermitPurchase => await PurchasePermitAsync( actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.NoteWrite => await WriteNoteAsync( actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.RestraintSet => await SetRestraintAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.ScannerIntent => await ScannerIntentAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.CombatRespawn => RespawnCharacter( actor.Value ),
			_ => OperationResult.Failure( ErrorCode.UnknownDefinition, "Schema command has no HL2RP runtime handler." )
		};
	}

	private async ValueTask<OperationResult> RunEntitlementCommandAsync(
		RpcActor rpc,
		RunSchemaCommandCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var connectionId = new ConnectionId( rpc.Connection.Id );
		var characterId = FindActiveCharacter( connectionId )?.Id;
		var administrator = new HL2RPEntitlementAdministrator( rpc.AccountId, characterId );
		if ( !CanManageEntitlements( administrator ) )
			return OperationResult.Failure(
				ErrorCode.Unauthorized, "Authenticated account cannot manage entitlements." );
		if ( !HL2RPPresentationPlanner.TryAccountId( command.Arguments, "account", out var targetAccountId ) )
			return OperationResult.Failure(
				ErrorCode.InvalidArgument, "Target account must be a non-zero unsigned account ID." );
		if ( command.CommandId == HL2RPIds.Commands.EntitlementQuery )
		{
			var observed = _entitlements.Observe( targetAccountId );
			if ( observed.Failed ) return Failure( observed.Error! );
			if ( !observed.Value.IsPersisted && !IsKnownAccount( targetAccountId ) )
				return OperationResult.Failure( ErrorCode.NotFound, "Target account is not known to this host." );
			_entitlementQueries[connectionId] = targetAccountId;
			return OperationResult.Success();
		}

		var arguments = new HL2RPCommandArguments( command.Arguments );
		var flagText = arguments.String( "flag" );
		var revision = arguments.Integer( "revision" );
		if ( flagText.Failed || revision.Failed || revision.Value < 0 )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Entitlement flag or revision is invalid." );
		var flag = HL2RPAccountEntitlements.ParseSingleFlag( flagText.Value );
		if ( flag.Failed ) return Failure( flag.Error! );
		var expectedRevision = new Hexagon.V2.Persistence.DocumentRevision( revision.Value );
		var changed = command.CommandId == HL2RPIds.Commands.EntitlementGrant
			? await _entitlements.GrantAsync(
				administrator, targetAccountId, flag.Value, expectedRevision, cancellationToken )
			: await _entitlements.RevokeAsync(
				administrator, targetAccountId, flag.Value, expectedRevision, cancellationToken );
		if ( changed.Failed ) return Failure( changed.Error! );
		projectionDelta.Observe( changed.Value );
		_entitlementQueries[connectionId] = targetAccountId;
		projectionDelta.Connections.UnionWith(
			_entitlementPresentationInvalidation.Claim( targetAccountId ) );
		return OperationResult.Success();
	}

	private IReadOnlyList<ConnectionId> EntitlementRecipients( AccountId accountId ) =>
		_clients
			.Where( pair => pair.Value.AccountId == accountId ||
				_entitlementQueries.TryGetValue( pair.Key, out var queried ) && queried == accountId )
			.Select( pair => pair.Key )
			.Distinct()
			.ToArray();

	private OperationResult PublishAdministrationAudit( InventoryActor actor )
	{
		_audit.Publish( new AdminAuditFact
		{
			ActorAccountId = actor.AccountId,
			ActorCharacterId = actor.CharacterId,
			Operation = HL2RPFeatureOperation.AdministrationAudit,
			Target = $"character:{actor.CharacterId.Value:D}",
			OccurredAtUtc = _clock.UtcNow,
			CommitSequence = _context.Persistence.Health.Sequence
		} );
		return OperationResult.Success();
	}

	private OperationResult CivicData( InventoryActor actor, HL2RPCommandArguments arguments )
	{
		var target = arguments.OptionalGuid( "character" );
		if ( target.Failed ) return Failure( target.Error! );
		var subjectId = target.Value is Guid rawId ? new CharacterId( rawId ) : actor.CharacterId;
		var read = _civic!.Read( subjectId );
		if ( read.Failed ) return Failure( read.Error! );
		if ( target.Value is null ) _civicSubjects.ClearConnection( actor.ConnectionId );
		else _civicSubjects.Select( actor.ConnectionId, subjectId );
		return OperationResult.Success();
	}

	private async ValueTask<OperationResult> SetObjectivesAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var routed = await _objectiveRouter!.RouteAsync( actor, args, cancellationToken );
		if ( routed.Receipt is not null )
		{
			projectionDelta.Observe( routed.Receipt.ProjectionReceipt );
			projectionDelta.Documents.UnionWith(
				routed.Receipt.ProjectionReceipt.Documents.Select( value => value.Address ) );
		}
		return routed.Command.Result;
	}

	private async ValueTask<OperationResult> SetPriorityAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var target = args.Guid( "character" );
		var priority = args.String( "priority" );
		var record = args.String( "record", true );
		if ( target.Failed || priority.Failed || record.Failed || !Enum.TryParse<CivicPriorityStatus>( priority.Value, true, out var parsed ) )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Priority arguments are invalid." );
		var updated = await _civic!.UpdateRecordAsync(
			actor, new CharacterId( target.Value ), parsed, record.Value, cancellationToken );
		if ( updated.Succeeded ) projectionDelta.Observe( updated.Value );
		return Untyped( updated );
	}

	private async ValueTask<OperationResult> TuneRadioAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var item = args.Guid( "item" );
		var frequency = args.String( "frequency" );
		var enabled = args.Boolean( "enabled" );
		var main = MainInventory( actor.CharacterId );
		if ( item.Failed || frequency.Failed || enabled.Failed || main is null ) return OperationResult.Failure( ErrorCode.InvalidArgument, "Radio arguments are invalid." );
		var tuned = await _radio!.ConfigureAsync(
			actor, main.Id, new ItemId( item.Value ), frequency.Value, enabled.Value, cancellationToken );
		if ( tuned.Succeeded )
		{
			projectionDelta.Observe( tuned.Value );
			projectionDelta.Inventories.Add( main.Id );
			projectionDelta.Items.Add( new ItemId( item.Value ) );
		}
		return Untyped( tuned );
	}

	private async ValueTask<OperationResult> IntroduceAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var target = args.Guid( "character" );
		if ( target.Failed ) return Failure( target.Error! );
		var introduced = await _recognition!.IntroduceAsync(
			actor, new CharacterId( target.Value ), cancellationToken );
		if ( introduced.Succeeded ) projectionDelta.Observe( introduced.Value );
		return Untyped( introduced );
	}

	private async ValueTask<OperationResult> BuyAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var session = args.Guid( "session" );
		var definition = args.String( "definition" );
		var quantity = args.Integer( "quantity" );
		var main = MainInventory( actor.CharacterId );
		if ( session.Failed || definition.Failed || quantity.Failed || quantity.Value is < 1 or > 64 || main is null )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Vendor purchase arguments are invalid." );
		var purchased = await _commerce!.BuyAsync(
			actor, new InteractionSessionId( session.Value ), main.Id,
			new DefinitionId( definition.Value ), (int)quantity.Value, cancellationToken );
		if ( purchased.Succeeded )
		{
			projectionDelta.Observe( purchased.Value );
			projectionDelta.SceneEntities.Add( purchased.Value.SceneEntityId );
			projectionDelta.Inventories.Add( main.Id );
			projectionDelta.Items.UnionWith( purchased.Value.ItemIds );
		}
		return Untyped( purchased );
	}

	private async ValueTask<OperationResult> SellAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var session = args.Guid( "session" );
		var inventory = args.Guid( "inventory" );
		var item = args.Guid( "item" );
		if ( session.Failed || inventory.Failed || item.Failed ) return OperationResult.Failure( ErrorCode.InvalidArgument, "Vendor sale arguments are invalid." );
		var result = await _commerce!.SellAsync(
			actor, new InteractionSessionId( session.Value ), new InventoryId( inventory.Value ), new ItemId( item.Value ), cancellationToken );
		if ( result.Succeeded )
		{
			projectionDelta.Observe( result.Value );
			projectionDelta.SceneEntities.Add( result.Value.SceneEntityId );
			_bags?.ItemMoved( new ItemId( item.Value ) );
			projectionDelta.Inventories.Add( new InventoryId( inventory.Value ) );
			projectionDelta.Items.UnionWith( result.Value.ItemIds );
		}
		return Untyped( result );
	}

	private async ValueTask<OperationResult> PurchasePermitAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var kind = args.String( "permit" );
		var main = MainInventory( actor.CharacterId );
		if ( kind.Failed || main is null ) return OperationResult.Failure( ErrorCode.InvalidArgument, "Permit purchase arguments are invalid." );
		var parsed = HL2RPPresentationContracts.ParsePermitKind( kind.Value );
		if ( parsed.Failed ) return Failure( parsed.Error! );
		var purchased = await _permitPurchases!.PurchaseAsync(
			actor, main.Id, parsed.Value, cancellationToken );
		if ( purchased.Succeeded )
		{
			projectionDelta.Observe( purchased.Value );
			projectionDelta.Inventories.Add( main.Id );
			projectionDelta.Items.Add( purchased.Value.PermitItemId );
		}
		return Untyped( purchased );
	}

	private async ValueTask<OperationResult> WriteNoteAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var item = args.Guid( "item" );
		var body = args.String( "body", true );
		var main = MainInventory( actor.CharacterId );
		if ( item.Failed || body.Failed || main is null ) return OperationResult.Failure( ErrorCode.InvalidArgument, "Note arguments are invalid." );
		var edited = await _documents!.EditNoteAsync(
			actor, main.Id, new ItemId( item.Value ), body.Value, cancellationToken );
		if ( edited.Succeeded )
		{
			projectionDelta.Observe( edited.Value );
			projectionDelta.Inventories.Add( main.Id );
			projectionDelta.Items.Add( edited.Value.NoteItemId );
		}
		return Untyped( edited );
	}

	private async ValueTask<OperationResult> SetRestraintAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var target = args.Guid( "character" );
		var restrain = args.Boolean( "restrain" );
		var search = args.Boolean( "search" );
		if ( target.Failed || restrain.Failed || search.Failed ) return OperationResult.Failure( ErrorCode.InvalidArgument, "Restraint arguments are invalid." );
		var targetId = new CharacterId( target.Value );
		if ( search.Value )
		{
			var targetInventory = MainInventory( targetId );
			if ( targetInventory is null )
				return OperationResult.Failure( ErrorCode.NotFound, "Search target main inventory was not found." );
			var opened = _search!.Open( actor, targetId, targetInventory.Id );
			if ( opened.Succeeded ) projectionDelta.Inventories.Add( targetInventory.Id );
			return Untyped( opened );
		}
		if ( !restrain.Value )
		{
			var released = await _restraints!.UnrestrainAsync( actor, targetId, cancellationToken );
			if ( released.Succeeded ) projectionDelta.Observe( released.Value );
			return Untyped( released );
		}
		var main = MainInventory( actor.CharacterId );
		var zip = main?.Placements.Select( value => _repositories.Items.Find( DomainKeys.Item( value.ItemId ) )?.Value )
			.FirstOrDefault( value => value?.Definition.Value == HL2RPIds.Items.ZipTie );
		if ( main is null || zip is null ) return OperationResult.Failure( ErrorCode.NotFound, "A zip tie is required." );
		var ticket = _restraints!.Begin( actor, targetId, main.Id, zip.Id );
		if ( ticket.Failed ) return Failure( ticket.Error! );
		var linked = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
		var active = new ActiveRestraintAction( actor, ticket.Value, linked );
		var durableSuccess = false;
		var cancelledByCommand = false;
		if ( !_activeRestraintActions.TryAdd( actor.ConnectionId, active ) )
		{
			linked.Dispose();
			_restraints.Cancel( ticket.Value.TicketId, actor );
			return OperationResult.Failure( ErrorCode.Conflict, "Another restraint action is already active." );
		}
		PublishConnections( new[] { actor.ConnectionId }, invalidateRuntime: true );
		try
		{
			var delay = ticket.Value.CompletesAtUtc - _clock.UtcNow;
			if ( delay > TimeSpan.Zero ) await Task.Delay( delay, linked.Token );
			if ( !_activeRestraintActions.TryClaimCommit( actor.ConnectionId, active ) )
				return OperationResult.Failure( ErrorCode.Conflict, "Restraint action was cancelled." );
			var completed = await _restraints.CompleteAsync(
				ticket.Value.TicketId, actor, CancellationToken.None );
			if ( completed.Succeeded )
			{
				projectionDelta.Observe( completed.Value );
				durableSuccess = true;
			}
			return Untyped( completed );
		}
		catch ( OperationCanceledException )
		{
			var cancelled = _activeRestraintActions.TryCancel(
				actor.ConnectionId, candidate => ReferenceEquals( candidate, active ), out _ );
			if ( cancelled == HL2RPTimedActionCancelOutcome.Cancelled )
			{
				cancelledByCommand = true;
				_ = _restraints.Cancel( ticket.Value.TicketId, actor );
			}
			return OperationResult.Failure( ErrorCode.Conflict, "Restraint action was cancelled." );
		}
		finally
		{
			var completion = _activeRestraintActions.Complete( actor.ConnectionId, active, durableSuccess );
			linked.Dispose();
			if ( cancelledByCommand || completion.PublishFailure )
				PublishConnections( new[] { actor.ConnectionId }, invalidateRuntime: true );
		}
	}

	private async ValueTask<OperationResult> ScannerIntentAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var intent = args.String( "intent" );
		if ( intent.Failed ) return Failure( intent.Error! );
		var sessionId = args.Guid( "session" );
		var active = sessionId.Succeeded
			? _scanner!.ActiveSessions.SingleOrDefault( value => value.SessionId.Value == sessionId.Value )
			: null;
		return intent.Value switch
		{
			"enter" => await EnterScannerAsync( actor, projectionDelta, cancellationToken ),
			"exit" when active is not null => await _scanner!.ExitAsync( actor, active.SessionId, cancellationToken ),
			"spotlight" when active is not null => await ToggleScannerSpotlightAsync(
				actor, active.SessionId, projectionDelta, cancellationToken ),
			"flash" when active is not null => _scanner!.Flash( actor, active.SessionId ),
			"photo" when active is not null => await TakeScannerPhotoAsync(
				actor, active.SessionId, projectionDelta, cancellationToken ),
			"move" when active is not null => await ApplyScannerInputAsync(
				actor, active.SessionId, args, projectionDelta, cancellationToken ),
			_ => OperationResult.Failure( ErrorCode.InvalidArgument, "Scanner intent or session is invalid." )
		};
	}

	private async ValueTask<OperationResult> ApplyScannerInputAsync(
		InventoryActor actor,
		InteractionSessionId sessionId,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var sequence = args.Integer( "sequence" );
		var forward = args.Integer( "forward" );
		var right = args.Integer( "right" );
		var up = args.Integer( "up" );
		var yaw = args.Integer( "yaw" );
		var pitch = args.Integer( "pitch" );
		if ( sequence.Failed || forward.Failed || right.Failed || up.Failed || yaw.Failed || pitch.Failed ||
			sequence.Value <= 0 || new[] { forward.Value, right.Value, up.Value, yaw.Value, pitch.Value }
				.Any( value => value is < -1 or > 1 ) )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Scanner motion axes or sequence are invalid." );
		var applied = await _scanner!.ApplyInputAsync( actor, new ScannerInputIntent(
			sessionId, sequence.Value, forward.Value, right.Value, up.Value, yaw.Value, pitch.Value ), cancellationToken );
		if ( applied.Succeeded )
		{
			if ( applied.Value.Commit is not null ) projectionDelta.Observe( applied.Value.Commit );
			LogScannerBoundaryError( applied.Value.BoundaryError );
		}
		return Untyped( applied );
	}

	private async ValueTask<OperationResult> TakeScannerPhotoAsync(
		InventoryActor actor,
		InteractionSessionId sessionId,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var photo = await _scanner!.TakePhotoAsync( actor, sessionId, cancellationToken );
		if ( photo.Succeeded )
		{
			projectionDelta.Observe( photo.Value );
			LogScannerBoundaryError( photo.Value.BoundaryError );
		}
		return Untyped( photo );
	}

	private async ValueTask<OperationResult> EnterScannerAsync(
		InventoryActor actor,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var session = CurrentSession( actor, InteractionSessionKind.Scanner );
		if ( session is null || session.Target.Kind != InteractionTargetKind.SceneEntity )
			return OperationResult.Failure( ErrorCode.Unauthorized, "A current scanner interaction is required." );
		var target = new SceneEntityId( session.Target.Id );
		if ( _features.TryGetValue( target, out var feature ) && feature is HL2RPScannerDockComponent dock )
		{
			if ( dock.LinkedDroneId is not SceneEntityId droneId )
				return OperationResult.Failure( ErrorCode.ConfigurationInvalid, "Scanner dock has no linked drone." );
			target = droneId;
		}
		return await EnterScannerTargetAsync( actor, target, projectionDelta, cancellationToken );
	}

	private async ValueTask<OperationResult> EnterScannerTargetAsync(
		InventoryActor actor,
		SceneEntityId target,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var entered = await _scanner!.EnterAsync( actor, target, cancellationToken );
		if ( entered.Succeeded )
		{
			projectionDelta.Observe( entered.Value );
			projectionDelta.SceneEntities.Add( entered.Value.Session.ScannerId );
			LogScannerBoundaryError( entered.Value.BoundaryError );
		}
		return Untyped( entered );
	}

	private async ValueTask<OperationResult> ToggleScannerSpotlightAsync(
		InventoryActor actor,
		InteractionSessionId sessionId,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var toggled = await _scanner!.ToggleSpotlightAsync( actor, sessionId, cancellationToken );
		if ( toggled.Succeeded )
		{
			projectionDelta.Observe( toggled.Value );
			projectionDelta.SceneEntities.Add( toggled.Value.ScannerId );
			LogScannerBoundaryError( toggled.Value.BoundaryError );
		}
		return Untyped( toggled );
	}

	private static void LogScannerBoundaryError( OperationError? error )
	{
		if ( error is not null )
			Log.Warning( $"HL2RP_SCANNER_BOUNDARY_DEGRADED code={error.Code} message={error.Message}" );
	}

	private async ValueTask<OperationResult> DoorOwnershipAsync(
		InventoryActor actor,
		HL2RPCommandArguments arguments,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var intent = arguments.String( "intent" );
		if ( intent.Failed ) return Failure( intent.Error! );
		var session = CurrentSession( actor, InteractionSessionKind.Door );
		if ( session is null )
			return OperationResult.Failure( ErrorCode.Unauthorized, "A current door session is required." );
		if ( session.Target.Kind != InteractionTargetKind.SceneEntity ||
			!_features.TryGetValue( new SceneEntityId( session.Target.Id ), out var feature ) ||
			feature is not HL2RPDoorComponent { Ownable: true } )
			return OperationResult.Failure( ErrorCode.PolicyDenied, "Current door does not support personal ownership." );
		OperationResult<DoorOwnershipReceipt> changed;
		if ( intent.Value == "claim" )
			changed = await _doorOwnership!.ClaimAsync( actor, session.Id, cancellationToken );
		else if ( intent.Value == "release" )
			changed = await _doorOwnership!.ReleaseAsync( actor, session.Id, cancellationToken );
		else return OperationResult.Failure(
			ErrorCode.InvalidArgument, "Door ownership intent must be claim or release." );
		if ( changed.Succeeded )
		{
			projectionDelta.Observe( changed.Value );
			projectionDelta.SceneEntities.Add( changed.Value.DoorEntityId );
		}
		return Untyped( changed );
	}

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
			var body = binding.Player.HostBuildAuthoritativeBody( candidate =>
			{
				ConfigureAuthoritativePlayerBody( candidate, character );
				candidate.AddComponent<SkinnedModelRenderer>().Model = Model.Load( modelPath.Value );
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
				_ = binding.Player.HostStripAuthoritativeBody();
				Log.Error( exception, "Failed to restore the active-character inventory grant." );
				return OperationResult.Failure( ErrorCode.InternalError, "Respawn authority could not be restored." );
			}

			var respawned = _combatIntent!.Respawn( actor );
			if ( respawned.Failed )
			{
				_access.RevokeCharacter( actor.ConnectionId, actor.CharacterId );
				_ = binding.Player.HostStripAuthoritativeBody();
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

	private InventoryRecord? MainInventory( CharacterId characterId )
	{
		var owner = InventoryOwner.Character( characterId );
		var index = _repositories.OwnerInventories.Find( DomainKeys.OwnerInventory( owner, InventoryRoles.Main ) );
		return index is null ? null : _repositories.Inventories.Find( DomainKeys.Inventory( index.Value.InventoryId ) )?.Value;
	}

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
		if ( player is null || !player.TryGetUsableAuthoritativeBody( out var body ) ) return null;
		var position = body.WorldPosition;
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
		var start = actor.WorldPosition;
		var end = target.WorldPosition;
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

	private static OperationResult<InteractionTarget> ToTarget( InteractionTargetInput input )
	{
		if ( input.Id == Guid.Empty ) return OperationResult<InteractionTarget>.Failure( ErrorCode.InvalidArgument, "Interaction target ID is empty." );
		try
		{
			return OperationResult<InteractionTarget>.Success( input.Kind switch
			{
				InteractionTargetInputKind.SceneEntity => InteractionTarget.SceneEntity( new SceneEntityId( input.Id ) ),
				InteractionTargetInputKind.Inventory => InteractionTarget.Inventory( new InventoryId( input.Id ) ),
				InteractionTargetInputKind.Item => InteractionTarget.Item( new ItemId( input.Id ) ),
				InteractionTargetInputKind.Character => InteractionTarget.Character( new CharacterId( input.Id ) ),
				_ => throw new ArgumentOutOfRangeException( nameof(input) )
			} );
		}
		catch ( ArgumentException exception )
		{
			return OperationResult<InteractionTarget>.Failure( ErrorCode.InvalidArgument, exception.Message );
		}
	}

	private static WorldTransformRecord DropTransform( GameObject gameObject )
	{
		var forward = gameObject.Components.Get<PlayerController>()?.EyeAngles.ToRotation().Forward ??
			gameObject.WorldTransform.Forward;
		var position = gameObject.WorldPosition + forward * 48f + Vector3.Up * 24f;
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

	private static bool IsFinite( WorldTransformRecord transform ) =>
		float.IsFinite( transform.PositionX ) && float.IsFinite( transform.PositionY ) && float.IsFinite( transform.PositionZ ) &&
		float.IsFinite( transform.RotationX ) && float.IsFinite( transform.RotationY ) &&
		float.IsFinite( transform.RotationZ ) && float.IsFinite( transform.RotationW );

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

		var position = body.WorldPosition;
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

	private SchemaViewSnapshot BuildCreationAvailabilityView(
		ConnectionId connectionId,
		AccountId accountId,
		CharacterId? characterId,
		long revision )
	{
		var self = _entitlements.Observe( accountId );
		var selfSnapshot = self.Succeeded
			? self.Value
			: new HL2RPAccountEntitlementSnapshot(
				accountId, HL2RPWhitelist.None, Hexagon.V2.Persistence.DocumentRevision.None, false );
		var canManage = CanManageEntitlements( new HL2RPEntitlementAdministrator( accountId, characterId ) );
		HL2RPAccountEntitlementSnapshot? queried = null;
		if ( canManage && _entitlementQueries.TryGetValue( connectionId, out var queriedAccountId ) )
		{
			var observed = _entitlements.Observe( queriedAccountId );
			if ( observed.Succeeded ) queried = observed.Value;
		}
		return HL2RPCreationAvailability.Build( revision, selfSnapshot, canManage, queried );
	}

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
			var publicSnapshot = PublicSnapshot( pair.Key, pair.Value, character );
			PlayerPrivateSnapshot? privateSnapshot = null;
			IReadOnlyList<InventorySnapshot> inventories = Array.Empty<InventorySnapshot>();
			IReadOnlyList<SchemaViewSnapshot> views = new[]
			{
				BuildCreationAvailabilityView( pair.Key, pair.Value.AccountId, character?.Id, revision )
			};
			if ( character is not null )
			{
				var mainInventoryId = MainInventory( character.Id )?.Id;
				privateSnapshot = _projectionIndex.GetOrCreateSlice(
					pair.Key,
					HL2RPProjectionSliceKind.PrivatePlayer,
					() => new HL2RPProjectionSlice<PlayerPrivateSnapshot>(
						BuildPrivateSnapshot( character, mainInventoryId ),
						PrivateProjectionDocuments( character.Id, mainInventoryId ),
						new[] { HL2RPProjectionIndex.RuntimeDependency( "connection", pair.Key.ToString() ) } ) );
				inventories = _projectionIndex.GetOrCreateSlice(
					pair.Key,
					HL2RPProjectionSliceKind.Inventories,
					() => BuildInventoryProjectionSlice( pair.Key, character.Id ) );
				var cachedViews = _projectionIndex.GetOrCreateSlice(
					pair.Key,
					HL2RPProjectionSliceKind.SchemaViews,
					() => BuildSchemaProjectionSlice( pair.Key, character ) );
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
					() => BuildRosterProjectionSlice( pair.Key ) ).WithRevision( revision ),
				views,
				inventories,
				BuildActionProgress( pair.Key ) );
		}
		_presentationInvalidation.AcknowledgePublished( coveredInvalidations );
	}

	private IReadOnlyList<DocumentAddress> PrivateProjectionDocuments(
		CharacterId characterId,
		InventoryId? mainInventoryId )
	{
		var documents = new List<DocumentAddress>
		{
			new( DomainCollections.Characters, DomainKeys.Character( characterId ) )
		};
		if ( mainInventoryId is InventoryId inventoryId )
			documents.Add( new DocumentAddress( DomainCollections.Inventories, DomainKeys.Inventory( inventoryId ) ) );
		return documents;
	}

	private HL2RPProjectionSlice<IReadOnlyList<InventorySnapshot>> BuildInventoryProjectionSlice(
		ConnectionId connectionId,
		CharacterId characterId )
	{
		var value = BuildInventories( connectionId, characterId );
		_projectionIndex.ObserveVisibleInventories(
			connectionId, value.Select( inventory => inventory.InventoryId ) );
		var documents = new HashSet<DocumentAddress>
		{
			new( DomainCollections.Characters, DomainKeys.Character( characterId ) )
		};
		foreach ( var inventory in value )
		{
			documents.Add( new DocumentAddress(
				DomainCollections.Inventories, DomainKeys.Inventory( inventory.InventoryId ) ) );
			foreach ( var item in inventory.Items )
				documents.Add( new DocumentAddress( DomainCollections.Items, DomainKeys.Item( item.ItemId ) ) );
		}
		foreach ( var session in _sessions?.ActiveSessions.Where( value =>
			value.ConnectionId == connectionId && value.Target.Kind == InteractionTargetKind.SceneEntity ) ??
			Array.Empty<InteractionSession>() )
			documents.Add( new DocumentAddress( DomainCollections.SceneEntities,
				DomainKeys.SceneEntity( new SceneEntityId( session.Target.Id ) ) ) );
		return new HL2RPProjectionSlice<IReadOnlyList<InventorySnapshot>>(
			value, documents.ToArray(),
			new[] { HL2RPProjectionIndex.RuntimeDependency( "connection", connectionId.ToString() ) } );
	}

	private HL2RPProjectionSlice<IReadOnlyList<SchemaViewSnapshot>> BuildSchemaProjectionSlice(
		ConnectionId connectionId,
		CharacterRecord character )
	{
		var value = BuildSchemaViews( connectionId, character, 0 );
		var documents = new HashSet<DocumentAddress>
		{
			new( DomainCollections.Characters, DomainKeys.Character( character.Id ) )
		};
		var civicSubject = _civicSubjects.Resolve(
			connectionId, character.Id,
			candidate => _repositories.Characters.Find( DomainKeys.Character( candidate ) ) is not null );
		documents.Add( new DocumentAddress( DomainCollections.Characters, DomainKeys.Character( civicSubject ) ) );
		if ( _projectionIndex.FirstSceneEntity( "city" ) is PersistentSceneEntityRecord city )
			documents.Add( new DocumentAddress( DomainCollections.SceneEntities, DomainKeys.SceneEntity( city.Id ) ) );
		foreach ( var reference in _projectionIndex.CharacterReferenceKeys( character.Id ) )
			documents.Add( new DocumentAddress( DomainCollections.CharacterReferences, reference ) );
		foreach ( var session in _sessions?.ActiveSessions.Where( session =>
			session.ConnectionId == connectionId && session.Target.Kind == InteractionTargetKind.SceneEntity ) ??
			Array.Empty<InteractionSession>() )
		{
			var scene = new SceneEntityId( session.Target.Id );
			documents.Add( new DocumentAddress( DomainCollections.SceneEntities, DomainKeys.SceneEntity( scene ) ) );
			foreach ( var reference in _projectionIndex.CharacterReferenceAddressesForScene( scene ) ) documents.Add( reference );
		}
		return new HL2RPProjectionSlice<IReadOnlyList<SchemaViewSnapshot>>(
			value, documents.ToArray(),
			new[] { HL2RPProjectionIndex.RuntimeDependency( "connection", connectionId.ToString() ) } );
	}

	private ActionProgressSnapshot? BuildActionProgress( ConnectionId connectionId )
	{
		if ( _activeRestraintActions.TryGet( connectionId, out var action ) )
			return new ActionProgressSnapshot(
				action.Ticket.TicketId.Value,
				new ActionId( HL2RPIds.Actions.Restrain ),
				"Applying restraint",
				action.Ticket.CompletesAtUtc - RestraintService.RestraintDuration,
				RestraintService.RestraintDuration,
				true );
		if ( _activePistolActions.TryGet( connectionId, out var pistol ) )
			return new ActionProgressSnapshot(
				pistol.InstanceId,
				new ActionId( HL2RPIds.Actions.Fire ),
				"Raising pistol",
				pistol.ReadyAtUtc - PistolCombatService.DefaultRaiseDelay,
				PistolCombatService.DefaultRaiseDelay,
				true );
		return null;
	}

	private PlayerPublicSnapshot PublicSnapshot(
		ConnectionId connectionId,
		ClientBinding binding,
		CharacterRecord? character ) => new(
		connectionId,
		binding.AccountId.Value,
		binding.Connection.DisplayName,
		character?.Id,
		character?.Name ?? string.Empty,
		character?.Description ?? string.Empty,
		character?.Model,
		character?.Faction,
		character?.Class,
		character is not null && _combatLifecycle?.GetState( character.Id ) is not null,
		character is not null && IsPistolRaised( character.Id ),
		character is null ? string.Empty : HL2RPRuntimeProjection.SafeReplicatedLabel( character ) );

	private bool IsPistolRaised( CharacterId characterId )
	{
		var main = MainInventory( characterId );
		if ( main is null ) return false;
		foreach ( var placement in main.Placements )
		{
			var item = _repositories.Items.Find( DomainKeys.Item( placement.ItemId ) )?.Value;
			if ( item?.Definition.Value != HL2RPIds.Items.Pistol || !item.Traits.TryGetValue( "pistol", out var payload ) ) continue;
			try
			{
				var pistol = HL2RPPersistence.Pistol.Deserialize( payload.Data, payload.TypeVersion );
				if ( pistol.Equipped && pistol.Raised ) return true;
			}
			catch ( Exception ) { }
		}
		return false;
	}

	private PlayerPrivateSnapshot BuildPrivateSnapshot( CharacterRecord character, InventoryId? mainInventoryId )
	{
		var baseline = HL2RPRuntimeProjection.PrivateSnapshot( character, mainInventoryId );
		var values = new Dictionary<string, SnapshotValue>( baseline.Values, StringComparer.Ordinal );
		var health = _combatHealth.Require( character.Id );
		values["vitals.health"] = SnapshotValue.Integer( health.Succeeded ? health.Value.CurrentHealth : 0 );
		values["vitals.health_max"] = SnapshotValue.Integer( health.Succeeded ? health.Value.MaximumHealth : 100 );
		values["vitals.armor_max"] = SnapshotValue.Integer( 100 );
		var death = _combatLifecycle?.GetState( character.Id );
		values["death.cause"] = SnapshotValue.String( death?.Cause ?? string.Empty );
		values["death.can_respawn"] = SnapshotValue.Boolean( death?.CanRespawn( _clock.UtcNow ) == true );
		values["death.respawn_available_at_ms"] = SnapshotValue.Integer(
			death?.RespawnAvailableAtUtc.ToUnixTimeMilliseconds() ?? 0 );
		values["restraint.active"] = SnapshotValue.Boolean( _restraintState.IsRestrained( character.Id ) );
		var activeSessions = _sessions?.ActiveSessions.Where( value => value.CharacterId == character.Id ).ToArray() ?? Array.Empty<InteractionSession>();
		foreach ( var session in activeSessions )
			values[$"interaction.{session.Kind.ToString().ToLowerInvariant()}_session"] = SnapshotValue.String( session.Id.Value.ToString( "D" ) );
		var connection = _clients.FirstOrDefault( value => value.Value.CharacterId == character.Id ).Key;
		values["action.active"] = SnapshotValue.Boolean(
			_activeRestraintActions.Contains( connection ) || _activePistolActions.Contains( connection ) );
		if ( _itemActionPresentations.TryGetValue( connection, out var itemPresentation ) &&
			itemPresentation.CharacterId == character.Id )
		{
			values["item.presentation.sequence"] = SnapshotValue.Integer( itemPresentation.PresentationSequence );
			values["item.presentation.kind"] = SnapshotValue.Choice( itemPresentation.Receipt.Kind switch
			{
				ItemActionPresentationKind.IdentityDocument => "identity_document",
				ItemActionPresentationKind.ReferenceDocument => "reference_document",
				ItemActionPresentationKind.PersonalNote => "personal_note",
				ItemActionPresentationKind.PermitCredential => "permit_credential",
				_ => "unknown"
			} );
			values["item.presentation.title"] = SnapshotValue.String( itemPresentation.Receipt.Title );
			foreach ( var field in itemPresentation.Receipt.Fields )
				values[$"item.presentation.field.{field.Key}"] = field.Value;
		}
		return new PlayerPrivateSnapshot( character.Id, character.Balance, mainInventoryId, values, baseline.Permissions );
	}

	private HL2RPProjectionSlice<PlayerRosterSnapshot> BuildRosterProjectionSlice( ConnectionId recipient )
	{
		var rows = new List<PlayerRosterRowSnapshot>();
		var documents = new HashSet<DocumentAddress>();
		var viewer = FindActiveCharacter( recipient );
		if ( viewer is not null )
			documents.Add( new DocumentAddress( DomainCollections.Characters, DomainKeys.Character( viewer.Id ) ) );
		foreach ( var pair in _clients.OrderBy( value => value.Key.Value ) )
		{
			var character = FindActiveCharacter( pair.Key );
			if ( character is not null )
			{
				documents.Add( new DocumentAddress(
					DomainCollections.Characters, DomainKeys.Character( character.Id ) ) );
				if ( viewer is not null && viewer.Id != character.Id &&
					character.Faction.Value is not (HL2RPIds.Factions.CivilProtection or HL2RPIds.Factions.Overwatch) )
					documents.Add( new DocumentAddress(
						DomainCollections.CharacterReferences,
						$"recognition-{viewer.Id}-{character.Id}" ) );
			}
			var isDead = character is not null && _combatLifecycle?.GetState( character.Id ) is not null;
			var fields = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
			{
				[HL2RPPresentationFields.Roster.DisplayName] = SnapshotValue.String(
					character is null ? pair.Value.Connection.DisplayName : DisplayNameFor( viewer, character ) ),
				[HL2RPPresentationFields.Roster.FactionId] = SnapshotValue.String( character?.Faction.Value ?? string.Empty ),
				[HL2RPPresentationFields.Roster.ClassId] = SnapshotValue.String( character?.Class?.Value ?? string.Empty ),
				[HL2RPPresentationFields.Roster.IsDead] = SnapshotValue.Boolean( isDead ),
				[HL2RPPresentationFields.Roster.Status] = SnapshotValue.String(
					character is null ? "Selecting" : isDead ? "Deceased" : "Active" ),
				[HL2RPPresentationFields.Roster.IsLocal] = SnapshotValue.Boolean( pair.Key == recipient )
			};
			rows.Add( new PlayerRosterRowSnapshot( pair.Key, character?.Id, fields ) );
		}
		return new HL2RPProjectionSlice<PlayerRosterSnapshot>(
			new PlayerRosterSnapshot( 0, rows ), documents.ToArray(),
			new[]
			{
				HL2RPProjectionIndex.RuntimeDependency( "roster-membership", "global" ),
				HL2RPProjectionIndex.RuntimeDependency( "combat-lifecycle", "global" )
			} );
	}

	private string DisplayNameFor( CharacterRecord? viewer, CharacterRecord subject )
		=> HL2RPRuntimeProjection.PresentationName( viewer, subject, _repositories );

	private IReadOnlyList<InventorySnapshot> BuildInventories( ConnectionId connectionId, CharacterId characterId )
	{
		var snapshots = new List<InventorySnapshot>();
		var character = _repositories.Characters.Find( DomainKeys.Character( characterId ) )?.Value;
		if ( character is null ) return snapshots;
		foreach ( var document in _projectionIndex.VisibleInventories(
			connectionId, characterId,
			inventoryId => _access.Has( connectionId, characterId, inventoryId, InventoryCapability.View ) ) )
		{
			var inventory = document.Value;
			var kind = inventory.Owner == InventoryOwner.Character( characterId )
				? InventoryViewKind.Main
				: inventory.Owner.Kind == InventoryOwnerKind.SceneEntity
					? InventoryViewKind.Storage
					: inventory.Owner.Kind == InventoryOwnerKind.Character ? InventoryViewKind.Search : InventoryViewKind.Bag;
			var inventoryItems = inventory.Placements
				.Select( placement => _projectionIndex.TryGetItem( placement.ItemId, out var item ) ? item : null )
				.Where( item => item is not null )
				.ToDictionary( item => item!.Id, item => item! );
			var items = new List<InventoryItemSnapshot>();
			foreach ( var placement in inventory.Placements )
			{
				inventoryItems.TryGetValue( placement.ItemId, out var item );
				if ( item is null || !_context.Schema.Items.TryGet( item.Definition.Value, out var definition ) ) continue;
				var actions = definition!.ActionIds.Select( action => ActionSnapshot(
					connectionId, character, inventory, item, inventoryItems, action ) );
				var state = new Dictionary<string, SnapshotValue>(
					HL2RPInventoryItemState.Project( item, characterId, _clock.UtcNow ), StringComparer.Ordinal );
				TrackItemPresentationDeadline( item );
				var sell = VendorSellAvailabilityFor( connectionId, character, inventory, item );
				if ( sell is not null )
				{
					state[HL2RPInventoryItemFields.VendorSellEnabled] = SnapshotValue.Boolean( sell.Enabled );
					state[HL2RPInventoryItemFields.VendorSellPayout] = SnapshotValue.Integer( sell.Payout );
					state[HL2RPInventoryItemFields.VendorSellReason] = SnapshotValue.String( sell.DisabledReason );
				}
				var drop = HL2RPItemDropAvailability.Project(
					definition,
					_access.Has( connectionId, characterId, inventory.Id, InventoryCapability.Move | InventoryCapability.Drop ),
					!string.IsNullOrWhiteSpace( definition.WorldModel ) && _worldModels.IsValidModel( definition.WorldModel ),
					_restraintState.IsRestrained( characterId ) );
				items.Add( new InventoryItemSnapshot(
					item.Id, item.Definition, definition.DisplayName ?? item.Definition.Value,
					definition.Description ?? string.Empty, definition.Category ?? string.Empty,
					placement.X, placement.Y, definition.Width, definition.Height, Quantity( item ),
					actions, state, drop.CanDrop, drop.DisabledReason ) );
			}
			snapshots.Add( new InventorySnapshot(
				inventory.Id, Math.Max( document.Revision.Value, 0 ), kind, kind.ToString(),
				inventory.Width, inventory.Height, items ) );
		}
		return snapshots;
	}

	private void TrackItemPresentationDeadline( ItemRecord item )
	{
		try
		{
			DateTimeOffset? refreshAt = null;
			if ( item.Definition.Value == HL2RPIds.Items.RequestDevice &&
				item.Traits.TryGetValue( "request_device", out var requestPayload ) )
			{
				var request = HL2RPPersistence.RequestDevice.Deserialize( requestPayload.Data, requestPayload.TypeVersion );
				if ( request.LastRequestAtUtc is DateTimeOffset last )
					refreshAt = last + RequestDeviceService.RequestCooldown;
			}
			else if ( item.Definition.Value == HL2RPIds.Items.Pistol &&
				item.Traits.TryGetValue( "pistol", out var pistolPayload ) )
			{
				var pistol = HL2RPPersistence.Pistol.Deserialize( pistolPayload.Data, pistolPayload.TypeVersion );
				if ( pistol.LastFiredAtUtc is DateTimeOffset last )
					refreshAt = last + PistolCombatService.DefaultFireInterval;
			}
			else if ( item.Definition.Value == HL2RPIds.Items.BusinessPermit &&
				item.Traits.TryGetValue( "permit", out var permitPayload ) )
			{
				refreshAt = HL2RPPersistence.BusinessPermit.Deserialize(
					permitPayload.Data, permitPayload.TypeVersion ).ExpiresAtUtc;
			}
			if ( refreshAt is DateTimeOffset deadline && deadline > _clock.UtcNow )
				_presentationInvalidation.TrackRefreshDeadline( $"item:{item.Id}", deadline );
		}
		catch ( Exception ) { }
	}

	private ItemActionSnapshot ActionSnapshot(
		ConnectionId connectionId,
		CharacterRecord character,
		InventoryRecord inventory,
		ItemRecord item,
		IReadOnlyDictionary<ItemId, ItemRecord> inventoryItems,
		string action )
	{
		var routed = _executableActions.CreateSnapshot(
			item.Definition, new ActionId( action ), action.Replace( '_', ' ' ) );
		var health = _combatHealth.Require( character.Id );
		var nestedBags = _projectionIndex.NestedInventoryCount( item.Id );
		return HL2RPItemActionAvailability.Project( routed, new HL2RPItemActionAvailabilityContext
		{
			Character = character,
			Inventory = inventory,
			Item = item,
			InventoryItems = inventoryItems,
			NowUtc = _clock.UtcNow,
			HasUseCapability = _access.Has(
				connectionId, character.Id, inventory.Id, InventoryCapability.View | InventoryCapability.Use ),
			IsRestrained = _restraintState.IsRestrained( character.Id ),
			IsDead = _combatLifecycle?.GetState( character.Id ) is not null,
			Health = health.Succeeded ? health.Value : null,
			HasNestedBagInventory = nestedBags == 1,
			CombineLock = action == HL2RPIds.Actions.Install
				? CombineLockAvailabilityFor( connectionId, character, item )
				: null
		} );
	}

	private CombineLockActionAvailability CombineLockAvailabilityFor(
		ConnectionId connectionId,
		CharacterRecord character,
		ItemRecord item )
	{
		var session = _sessions?.ActiveSessions.FirstOrDefault( value =>
			value.ConnectionId == connectionId && value.CharacterId == character.Id &&
			value.Kind == InteractionSessionKind.Door && value.Target.Kind == InteractionTargetKind.SceneEntity );
		if ( session is null ) return new CombineLockActionAvailability( false, "A current door session is required." );
		var door = _repositories.SceneEntities.Find(
			DomainKeys.SceneEntity( new SceneEntityId( session.Target.Id ) ) )?.Value;
		if ( door is null || door.Kind != "door" )
			return new CombineLockActionAvailability( false, "Bound session target is not a door." );
		var state = HL2RPFeaturePersistence.Decode( door.State, HL2RPPersistence.DoorState );
		if ( state.Failed ) return new CombineLockActionAvailability( false, "Door state is malformed." );
		if ( state.Value.CombineLocked ) return new CombineLockActionAvailability( false, "Door already has a Combine lock." );
		var policy = _featurePolicy.Evaluate( new HL2RPFeaturePolicyContext
		{
			Actor = new InventoryActor( connectionId, character.AccountId, character.Id ),
			Operation = HL2RPFeatureOperation.InstallCombineLock,
			SceneEntityId = new SceneEntityId( session.Target.Id ),
			ItemId = item.Id
		} );
		return policy.Succeeded
			? new CombineLockActionAvailability( true, null )
			: new CombineLockActionAvailability( false, policy.Error!.Message );
	}

	private VendorSellAvailability? VendorSellAvailabilityFor(
		ConnectionId connectionId,
		CharacterRecord character,
		InventoryRecord inventory,
		ItemRecord item )
	{
		if ( inventory.Owner != InventoryOwner.Character( character.Id ) ) return null;
		var session = _sessions?.ActiveSessions.FirstOrDefault( value =>
			value.ConnectionId == connectionId && value.CharacterId == character.Id &&
			value.Kind == InteractionSessionKind.Vendor && value.Target.Kind == InteractionTargetKind.SceneEntity );
		if ( session is null ) return null;
		var sceneEntityId = new SceneEntityId( session.Target.Id );
		var vendorEntity = _repositories.SceneEntities.Find( DomainKeys.SceneEntity( sceneEntityId ) )?.Value;
		if ( vendorEntity is null || vendorEntity.Kind != "vendor" )
			return new VendorSellAvailability( false, 0, "Bound session target is not a vendor." );
		var vendor = HL2RPFeaturePersistence.Decode( vendorEntity.State, HL2RPPersistence.VendorState );
		if ( vendor.Failed ) return new VendorSellAvailability( false, 0, "Vendor state is malformed." );
		var actor = new InventoryActor( connectionId, character.AccountId, character.Id );
		var policy = _featurePolicy.Evaluate( new HL2RPFeaturePolicyContext
		{
			Actor = actor,
			Operation = HL2RPFeatureOperation.VendorSell,
			SceneEntityId = sceneEntityId,
			ItemId = item.Id
		} );
		return HL2RPVendorSellAvailability.Project(
			character,
			item,
			vendor.Value,
			_access.Has( connectionId, character.Id, inventory.Id,
				InventoryCapability.View | InventoryCapability.TransferOut | InventoryCapability.Sell ),
			_projectionIndex.NestedInventoryCount( item.Id ) > 0,
			PermitInspector.HasValidPermit(
				_repositories, character.Id, vendor.Value.RequiredPermit, _clock.UtcNow ),
			policy.Succeeded );
	}

	private static long Quantity( ItemRecord item )
	{
		if ( !item.Traits.TryGetValue( "tokens", out var payload ) ) return 1;
		try { return Math.Max( 1, HL2RPPersistence.TokenStack.Deserialize( payload.Data, payload.TypeVersion ).Amount ); }
		catch ( Exception ) { return 1; }
	}

	private IReadOnlyList<SchemaViewSnapshot> BuildSchemaViews(
		ConnectionId connectionId,
		CharacterRecord character,
		long revision )
	{
		var views = new List<SchemaViewSnapshot>();
		var civicSubjectId = _civicSubjects.Resolve(
			connectionId,
			character.Id,
			candidate => _repositories.Characters.Find( DomainKeys.Character( candidate ) ) is not null );
		var civicSubject = _repositories.Characters.Find( DomainKeys.Character( civicSubjectId ) )?.Value ?? character;
		var state = HL2RPRuntimeProjection.DecodeState( civicSubject );
		if ( state.Failed && civicSubject.Id != character.Id )
		{
			_civicSubjects.ClearConnection( connectionId );
			civicSubject = character;
			state = HL2RPRuntimeProjection.DecodeState( character );
		}
		if ( state.Succeeded ) views.Add( CivicView( character, civicSubject, state.Value, revision ) );
		views.Add( ObjectiveView( character, revision ) );
		views.Add( RestraintView( connectionId, character, revision ) );
		var vendorSession = _sessions?.ActiveSessions.SingleOrDefault( value =>
			value.ConnectionId == connectionId && value.CharacterId == character.Id && value.Kind == InteractionSessionKind.Vendor );
		if ( vendorSession is not null )
		{
			var view = VendorView( character, vendorSession, revision );
			if ( view is not null ) views.Add( view );
		}
		var doorSession = _sessions?.ActiveSessions.SingleOrDefault( value =>
			value.ConnectionId == connectionId && value.CharacterId == character.Id &&
			value.Kind == InteractionSessionKind.Door );
		if ( doorSession is not null )
		{
			var view = DoorView( character, doorSession, revision );
			if ( view is not null ) views.Add( view );
		}
		var scannerSession = _scanner?.ActiveSessions.SingleOrDefault( value => value.Actor.ConnectionId == connectionId );
		if ( scannerSession is not null ) views.Add( ScannerView( scannerSession, revision ) );
		return views;
	}

	private SchemaViewSnapshot? DoorView(
		CharacterRecord character,
		InteractionSession session,
		long revision )
	{
		if ( session.Target.Kind != InteractionTargetKind.SceneEntity ) return null;
		var id = new SceneEntityId( session.Target.Id );
		if ( !_features.TryGetValue( id, out var component ) ||
			component is not HL2RPDoorComponent { Ownable: true } ) return null;
		var document = _repositories.SceneEntities.Find( DomainKeys.SceneEntity( id ) )?.Value;
		if ( document is null || document.Kind != "door" ) return null;
		var decoded = HL2RPFeaturePersistence.Decode( document.State, HL2RPPersistence.DoorState );
		if ( decoded.Failed ) return null;
		var owners = _projectionIndex.DoorOwners( id );
		var ownerStatus = owners.Count switch
		{
			0 => "unowned",
			1 when owners[0] == character.Id => "self",
			1 => "other",
			_ => "conflict"
		};
		var canClaim = owners.Count == 0 && !decoded.Value.CombineLocked;
		var claimReason = canClaim ? string.Empty : owners.Count switch
		{
			> 1 => "Door ownership is ambiguous.",
			1 when owners[0] == character.Id => "You already own this door.",
			1 => "This door is owned by another character.",
			_ => "A Combine-locked door cannot be claimed."
		};
		var canRelease = owners.Count == 1 && owners[0] == character.Id;
		var releaseReason = canRelease ? string.Empty : owners.Count switch
		{
			> 1 => "Door ownership is ambiguous.",
			1 => "Only the current owner may release this door.",
			_ => "This door has no owner."
		};
		return new SchemaViewSnapshot(
			HL2RPIds.Panels.Door,
			revision,
			new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
			{
				[HL2RPPresentationFields.Door.SessionId] = SnapshotValue.String( session.Id.Value.ToString( "D" ) ),
				[HL2RPPresentationFields.Door.SceneEntityId] = SnapshotValue.String( id.Value.ToString( "D" ) ),
				[HL2RPPresentationFields.Door.IsOpen] = SnapshotValue.Boolean( decoded.Value.IsOpen ),
				[HL2RPPresentationFields.Door.CombineLocked] = SnapshotValue.Boolean( decoded.Value.CombineLocked ),
				[HL2RPPresentationFields.Door.OwnerStatus] = SnapshotValue.Choice( ownerStatus ),
				[HL2RPPresentationFields.Door.CanClaim] = SnapshotValue.Boolean( canClaim ),
				[HL2RPPresentationFields.Door.ClaimDisabledReason] = SnapshotValue.String( claimReason ),
				[HL2RPPresentationFields.Door.CanRelease] = SnapshotValue.Boolean( canRelease ),
				[HL2RPPresentationFields.Door.ReleaseDisabledReason] = SnapshotValue.String( releaseReason )
			} );
	}

	private SchemaViewSnapshot CivicView(
		CharacterRecord viewer,
		CharacterRecord subject,
		HL2RPCharacterState state,
		long revision )
	{
		var fields = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
		{
			[HL2RPPresentationFields.CivicData.CharacterId] = SnapshotValue.String( subject.Id.Value.ToString( "D" ) ),
			[HL2RPPresentationFields.CivicData.DisplayName] = SnapshotValue.String( subject.Name ),
			[HL2RPPresentationFields.CivicData.CitizenId] = SnapshotValue.String( state.CitizenId ),
			[HL2RPPresentationFields.CivicData.Points] = SnapshotValue.Integer( state.CivicRecord.Points ),
			[HL2RPPresentationFields.CivicData.InfractionCount] = SnapshotValue.Integer( state.CivicRecord.Infractions.Count ),
			[HL2RPPresentationFields.CivicData.Priority] = SnapshotValue.Integer( (int)state.CivicRecord.Priority ),
			[HL2RPPresentationFields.CivicData.Record] = SnapshotValue.String( state.CivicRecord.RecordText ),
			[HL2RPPresentationFields.CivicData.CanEdit] = SnapshotValue.Boolean(
				_featureAuthorization.HasPermission( viewer.AccountId, viewer.Id, HL2RPIds.Permissions.Priority ) )
		};
		var rows = state.CivicRecord.Infractions.Select( (infraction, index) =>
			(IReadOnlyDictionary<string, SnapshotValue>)new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
			{
				[HL2RPPresentationFields.CivicData.InfractionId] = SnapshotValue.String( DeterministicGuid( $"{state.CitizenId}:{index}" ).ToString( "D" ) ),
				[HL2RPPresentationFields.CivicData.Summary] = SnapshotValue.String( infraction.Summary ),
				[HL2RPPresentationFields.CivicData.InfractionPoints] = SnapshotValue.Integer( infraction.Points ),
				[HL2RPPresentationFields.CivicData.IssuedAtUnixMilliseconds] = SnapshotValue.Integer( infraction.IssuedAtUtc.ToUnixTimeMilliseconds() ),
				[HL2RPPresentationFields.CivicData.IssuedBy] = SnapshotValue.String( infraction.IssuedBy.ToString() )
			} ).ToArray();
		return new SchemaViewSnapshot( HL2RPIds.Panels.CivicData, revision, fields, rows );
	}

	private SchemaViewSnapshot ObjectiveView( CharacterRecord character, long revision )
	{
		var rows = new List<IReadOnlyDictionary<string, SnapshotValue>>();
		var city = _projectionIndex.FirstSceneEntity( "city" );
		if ( city is not null )
		{
			try
			{
				var state = HL2RPPersistence.CityState.Deserialize( city.State.Data, city.State.TypeVersion );
				foreach ( var objective in HL2RPObjectiveProjection.Rows( state ) )
					rows.Add( new Dictionary<string, SnapshotValue>( objective, StringComparer.Ordinal ) );
			}
			catch ( Exception ) { rows.Clear(); }
		}
		return new SchemaViewSnapshot( HL2RPIds.Panels.Objectives, revision,
			new Dictionary<string, SnapshotValue>
			{
				[HL2RPPresentationFields.Objectives.CanEdit] = SnapshotValue.Boolean(
					_featureAuthorization.HasPermission( character.AccountId, character.Id, HL2RPIds.Permissions.CityObjectives ) )
			}, rows );
	}

	private SchemaViewSnapshot RestraintView(
		ConnectionId connectionId,
		CharacterRecord character,
		long revision )
	{
		var target = NearestCharacterTarget( character.Id );
		_presentationInvalidation.RememberRestraintTarget( connectionId, target?.Id );
		var subjectId = target?.Id ?? character.Id;
		var restrained = _restraintState.IsRestrained( subjectId );
		return new SchemaViewSnapshot( HL2RPIds.Panels.RestraintStatus, revision,
			new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
			{
				[HL2RPPresentationFields.RestraintStatus.Restrained] = SnapshotValue.Boolean( restrained ),
				[HL2RPPresentationFields.RestraintStatus.RemainingMilliseconds] = SnapshotValue.Integer( -1 ),
				[HL2RPPresentationFields.RestraintStatus.TargetCharacterId] = SnapshotValue.String(
					target?.Id.Value.ToString( "D" ) ?? string.Empty ),
				[HL2RPPresentationFields.RestraintStatus.CanSearch] = SnapshotValue.Boolean( target is not null && restrained ),
				[HL2RPPresentationFields.RestraintStatus.Status] = SnapshotValue.String( target is null
					? (restrained ? "Movement restricted." : "No character in authoritative reach.")
					: (restrained ? $"{target.Name} is restrained and searchable." : $"{target.Name} can be restrained.") )
			} );
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
			var distance = Vector3.DistanceBetween( viewerBody.WorldPosition, candidateBody.WorldPosition );
			if ( distance > 130f || distance >= nearestDistance ||
				!HasBodyLineOfSight( viewerBody, candidateBody ) )
				continue;
			nearest = character;
			nearestDistance = distance;
		}
		return nearest;
	}

	private SchemaViewSnapshot? VendorView( CharacterRecord character, InteractionSession session, long revision )
	{
		if ( session.Target.Kind != InteractionTargetKind.SceneEntity ) return null;
		var document = _repositories.SceneEntities.Find( DomainKeys.SceneEntity( new SceneEntityId( session.Target.Id ) ) );
		if ( document is null || document.Value.Kind != "vendor" ) return null;
		try
		{
			var vendor = HL2RPPersistence.VendorState.Deserialize( document.Value.State.Data, document.Value.State.TypeVersion );
			var hasPermit = PermitInspector.HasValidPermit(
				_repositories, character.Id, vendor.RequiredPermit, _clock.UtcNow );
			var rows = vendor.Stock.Select( entry =>
			{
				_context.Schema.Items.TryGet( entry.Definition.Value, out var item );
				var availability = HL2RPPresentationContracts.VendorOffer(
					hasPermit, entry.Quantity, entry.UnitPrice, character.Balance );
				return (IReadOnlyDictionary<string, SnapshotValue>)new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
				{
					[HL2RPPresentationFields.Vendor.DefinitionId] = SnapshotValue.Choice( entry.Definition.Value ),
					[HL2RPPresentationFields.Vendor.DisplayName] = SnapshotValue.String( item?.DisplayName ?? entry.Definition.Value ),
					[HL2RPPresentationFields.Vendor.ItemDescription] = SnapshotValue.String( item?.Description ?? string.Empty ),
					[HL2RPPresentationFields.Vendor.Price] = SnapshotValue.Integer( entry.UnitPrice ),
					[HL2RPPresentationFields.Vendor.Stock] = SnapshotValue.Integer( entry.Quantity ),
					[HL2RPPresentationFields.Vendor.CanBuy] = SnapshotValue.Boolean( availability.CanBuy ),
					[HL2RPPresentationFields.Vendor.DisabledReason] = SnapshotValue.String( availability.DisabledReason )
				};
			} ).ToArray();
			return new SchemaViewSnapshot( HL2RPIds.Panels.Vendor, revision,
				new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
				{
					[HL2RPPresentationFields.Vendor.SessionId] = SnapshotValue.String( session.Id.Value.ToString( "D" ) ),
					[HL2RPPresentationFields.Vendor.Name] = SnapshotValue.String( "Civil Distribution" ),
					[HL2RPPresentationFields.Vendor.Description] = SnapshotValue.String( "Session-bound regulated goods." ),
					[HL2RPPresentationFields.Vendor.Balance] = SnapshotValue.Integer( character.Balance )
				}, rows );
		}
		catch ( Exception ) { return null; }
	}

	private SchemaViewSnapshot ScannerView( ScannerPilotSession session, long revision )
	{
		var document = _repositories.SceneEntities.Find( DomainKeys.SceneEntity( session.ScannerId ) );
		var state = document is null
			? null
			: ScannerPersistence.Decode( document.Value.State, HL2RPPersistence.ScannerState ).Value;
		return new SchemaViewSnapshot(
			HL2RPIds.Panels.ScannerOverlay,
			revision,
			new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
			{
				[HL2RPPresentationFields.ScannerOverlay.Piloting] = SnapshotValue.Boolean( state?.PilotCharacterId == session.Actor.CharacterId ),
				[HL2RPPresentationFields.ScannerOverlay.UnitName] = SnapshotValue.String( $"SCN-{session.ScannerId.Value.ToString( "N" )[..2].ToUpperInvariant()}" ),
				[HL2RPPresentationFields.ScannerOverlay.Spotlight] = SnapshotValue.Boolean( state?.SpotlightEnabled == true ),
				[HL2RPPresentationFields.ScannerOverlay.PhotoReadyAtUnixMilliseconds] = SnapshotValue.Integer(
					(state?.PhotoCooldownUntilUtc ?? _clock.UtcNow).ToUnixTimeMilliseconds() ),
				[HL2RPPresentationFields.ScannerOverlay.SessionId] = SnapshotValue.String( session.SessionId.Value.ToString( "D" ) )
			} );
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

	private static OperationResult WorldItemCommandResult( WorldItemReconciliationReceipt receipt ) =>
		receipt.Disposition switch
		{
			WorldItemReconciliationDisposition.Applied => OperationResult.Success(),
			WorldItemReconciliationDisposition.CommittedPendingReconciliation => OperationResult.Failure(
				ErrorCode.ReconciliationPending,
				"The item change committed, but its world update is pending reconciliation; do not retry." ),
			WorldItemReconciliationDisposition.ConfigurationFailed => OperationResult.Failure(
				receipt.Error?.Code ?? ErrorCode.ConfigurationInvalid,
				$"The item change committed, but its world update cannot be applied: " +
				(receipt.Error?.Message ?? "unknown configuration failure") ),
			_ => throw new ArgumentOutOfRangeException( nameof(receipt.Disposition) )
		};

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
		ConnectionId connectionId;
		AccountId account;
		ClientBinding? connectedClient = null;
		if ( _clients.Count > 0 )
		{
			var client = _clients.OrderBy( value => value.Key.Value ).First();
			connectionId = client.Key;
			account = client.Value.AccountId;
			connectedClient = client.Value;
		}
		else if ( _verificationActor is VerificationActorBinding verification )
		{
			connectionId = verification.ConnectionId;
			account = verification.AccountId;
		}
		else throw new InvalidOperationException( "Verification actor was not initialized." );

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
			var probeCharacter = _characters.ListForAccount( account ).Single( value => value.Name == "Verification Citizen" );
			var actor = connectedClient is null
				? LoadVerificationProbeCharacter( connectionId, account, probeCharacter )
				: LoadConnectedProbeCharacter( connectionId, connectedClient, probeCharacter );
			var machine = _features.Single( value => value.Value is HL2RPVendingMachineComponent );
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
			var probeCharacter = _characters.ListForAccount( account ).SingleOrDefault( value => value.Name == "Verification Citizen" );
			if ( probeCharacter is null )
				throw new InvalidOperationException( "Verification character was not recovered." );
			_ = connectedClient is null
				? LoadVerificationProbeCharacter( connectionId, account, probeCharacter )
				: LoadConnectedProbeCharacter( connectionId, connectedClient, probeCharacter );
			var main = MainInventory( probeCharacter.Id );
			if ( main is null || !main.Placements.Any( placement =>
				_repositories.Items.Find( DomainKeys.Item( placement.ItemId ) )?.Value.Definition.Value == HL2RPIds.Items.Water ) )
				throw new InvalidOperationException( "Verification machine purchase was not recovered." );
			var machine = _repositories.SceneEntities.All().Select( value => value.Value )
				.Single( value => value.Kind == "vending_machine" );
			var machineState = HL2RPPersistence.MachineState.Deserialize( machine.State.Data, machine.State.TypeVersion );
			if ( machineState.CooldownUntilUtc is null || probeCharacter.Balance >= 25 )
				throw new InvalidOperationException( "Verification machine economy state was not recovered." );
			var digest = HL2RPRuntimeProjection.RecoveryDigest(
				_repositories, _context.Configuration.Snapshot() );
			Log.Info( $"HL2RP_PROBE_RECOVERED sequence={_context.Persistence.Health.Sequence} digest={digest}" );
		}
		else throw new InvalidOperationException( "Unknown verification probe." );
		// The probe runs inside the maintenance supervisor. Starting shutdown is
		// synchronous, but awaiting it here would deadlock when application disposal
		// waits for this maintenance tick to return.
		if ( HexagonRuntimeSystem.Current is not null )
			_ = HexagonRuntimeSystem.Current.ShutdownAsync();
	}

	private void SetBindingCharacter( ConnectionId connectionId, CharacterId? characterId )
	{
		var binding = _clients[connectionId];
		if ( binding.CharacterId == characterId ) return;
		_clients[connectionId] = binding with { CharacterId = characterId };
		_projectionIndex.ObserveCharacterBindingChanged( connectionId, binding.CharacterId, characterId );
		RefreshLiveConnection( connectionId );
	}

	private InventoryActor LoadConnectedProbeCharacter(
		ConnectionId connectionId,
		ClientBinding binding,
		CharacterRecord character )
	{
		var main = MainInventory( character.Id ) ??
			throw new InvalidOperationException( "Verification character main inventory was not recovered." );
		var modelPath = _characterModels.ResolvePath( character.Model );
		if ( modelPath.Failed ) throw new InvalidOperationException( modelPath.Error!.Message );
		var body = binding.Player.HostBuildAuthoritativeBody( candidate =>
		{
			ConfigureAuthoritativePlayerBody( candidate, character );
			candidate.AddComponent<SkinnedModelRenderer>().Model = Model.Load( modelPath.Value );
		} );
		if ( body.Failed ) throw new InvalidOperationException( body.Error!.Message );
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
		SetBindingCharacter( connectionId, character.Id );
		// The verification probe consumes host spatial authority immediately rather
		// than returning through the normal command-publication path. Publish the
		// canonical character snapshot now so body eligibility and the probe observe
		// the same session state.
		PublishConnections( new[] { connectionId }, invalidateRuntime: true );
		return new InventoryActor( connectionId, binding.AccountId, character.Id );
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

	private static Guid DeterministicGuid( string value )
	{
		var bytes = new byte[16];
		for ( var index = 0; index < value.Length; index++ ) bytes[index % bytes.Length] ^= (byte)value[index];
		bytes[0] |= 1;
		return new Guid( bytes );
	}

	private int RequiredConfigurationInt( string key )
	{
		var configured = _context.Configuration.Get<int>( key );
		if ( configured.Failed )
			throw new InvalidOperationException( configured.Error!.Message );
		return configured.Value.Value;
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
		var controller = body.AddComponent<PlayerController>();
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
			if ( Vector3.DistanceBetween( source.WorldPosition, target.WorldPosition ) > 130f )
				return OperationResult.Failure( ErrorCode.PolicyDenied, "Encounter target is out of range." );
			var trace = _owner._context.Scene.Trace.Ray( source.WorldPosition, target.WorldPosition )
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

	private sealed record ItemActionPresentationEnvelope(
		CharacterId CharacterId,
		long PresentationSequence,
		ItemActionPresentationReceipt Receipt );

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

	private sealed record ActiveRestraintAction(
		InventoryActor Actor,
		RestraintTicket Ticket,
		CancellationTokenSource Cancellation );

	private sealed record ActivePistolRaiseAction(
		InventoryActor Actor,
		Guid InstanceId,
		DateTimeOffset ReadyAtUtc,
		CancellationTokenSource Cancellation );

	private sealed class CommandProjectionDelta
	{
		public HashSet<InventoryId> Inventories { get; } = new();
		public HashSet<ItemId> Items { get; } = new();
		public HashSet<SceneEntityId> SceneEntities { get; } = new();
		public HashSet<DocumentAddress> Documents { get; } = new();
		public HashSet<ConnectionId> Connections { get; } = new();
		public HashSet<CharacterId> Characters { get; } = new();
		public List<CommitReceipt> Receipts { get; } = new();
		public bool Broadcast { get; set; }
		public bool RebuildLiveInventory { get; set; }
		public bool RebuildCombatTargets { get; set; }
		public bool HasPersistentChanges => Receipts.Count > 0 || Inventories.Count > 0 ||
			Items.Count > 0 || SceneEntities.Count > 0 || Documents.Count > 0 ||
			Connections.Count > 0 || Characters.Count > 0 || Broadcast ||
			RebuildLiveInventory || RebuildCombatTargets;

		public void Observe( CommitReceipt receipt )
		{
			ArgumentNullException.ThrowIfNull( receipt );
			if ( !Receipts.Any( existing => existing.Sequence == receipt.Sequence ) ) Receipts.Add( receipt );
			Documents.UnionWith( receipt.Documents.Select( value => value.Address ) );
		}

		public void Observe( IHL2RPCommittedOperation operation ) => Observe( operation.Commit );
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
				var stripped = binding.Player.HostStripAuthoritativeBody();
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
