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
public sealed class HL2RPHostApplication : IHexHostApplication
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
	private readonly object _lifecycleSync = new();
	private readonly HashSet<Task> _pendingLifecycle = new();
	private readonly Dictionary<ConnectionId, ActiveRestraintAction> _activeRestraintActions = new();
	private readonly Dictionary<ConnectionId, ActivePistolRaiseAction> _activePistolActions = new();
	private readonly HashSet<CharacterId> _respawningCharacters = new();
	private readonly CanonicalLiveInventoryView _liveInventory = new();
	private readonly CanonicalChatConnectionPositionDirectory _chatPositions = new();
	private readonly CanonicalChatAuthorityDirectory _chatAuthorities = new();
	private readonly ChatAdmissionService _chatAdmission = new();
	private readonly CanonicalCombatHealthDirectory _combatHealth = new();
	private readonly CanonicalCombatPlayerTargetDirectory _combatTargets = new();
	private readonly HL2RPPresentationInvalidation _presentationInvalidation = new();
	private readonly HL2RPPresentationSequence _itemPresentationSequence = new();
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
	private RadioTuningService? _radio;
	private CommerceService? _commerce;
	private DocumentService? _documents;
	private PermitPurchaseService? _permitPurchases;
	private RestraintService? _restraints;
	private RestraintSearchService? _search;
	private ScannerPilotService? _scanner;
	private PistolCombatService? _pistol;
	private CombatIntentService? _combatIntent;
	private HealthVialConsumeService? _healthVials;
	private CombatLifecycleService? _combatLifecycle;
	private ChatService? _chat;
	private HL2RPRadioRecipientResolver? _chatRecipients;
	private ChatRateLimit? _globalChatRateLimit;
	private bool _disposed;
	private bool _probeStarted;
	private VerificationActorBinding? _verificationActor;
	private HL2RPBootstrapOperatorDirectory _bootstrapOperators;
	private long _presentationRevision;
	private DateTimeOffset _nextPresentationTargetPollAtUtc = DateTimeOffset.MinValue;

	public HL2RPHostApplication( HexHostRuntimeContext context )
	{
		_context = context ?? throw new ArgumentNullException( nameof(context) );
		_repositories = context.Repositories;
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
				new HL2RPEntitlementChangedHandler( () => _presentationInvalidation.Invalidate() ) )
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

		var components = _context.Scene.GetAll<HL2RPSceneFeatureComponent>().ToArray();
		foreach ( var component in components )
		{
			if ( component.SceneEntityId is not SceneEntityId id )
				return OperationResult.Failure( ErrorCode.ConfigurationInvalid, "HL2RP scene feature has no persistent identity." );
			if ( !_features.TryAdd( id, component ) )
				return OperationResult.Failure( ErrorCode.DuplicateRegistration, $"HL2RP scene identity '{id}' is duplicated." );
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
			_verificationActor = new VerificationActorBinding(
				VerificationConnectionId, VerificationAccountId, null, null );
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
			_context.Scene, _features, ResolveActorState, _repositories, ResolveCombatTargetToken );
		_scanner = new ScannerPilotService(
			_repositories, _interactions, _sessions, _clock,
			boundaries, boundaries, boundaries, boundaries, boundaries );
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
		var reconciledPistols = await ReconcileRaisedPistolsAsync( cancellationToken );
		if ( reconciledPistols.Failed ) return reconciledPistols;
		PublishLiveInventory();
		RestoreWorldItems();
		var invariants = new DomainInvariantValidator(
			_repositories,
			_context.Schema,
			new SchemaItemShapeCatalog( _context.Schema, _repositories ),
			HL2RPPersistenceInvariants.Profile ).Validate();
		if ( !invariants.IsValid )
			return OperationResult.Failure( invariants.Issues[0].Code, invariants.Issues[0].Message );
		return OperationResult.Success();
	}

	public void Connected( RpcActor actor )
	{
		if ( _disposed ) return;
		_clients[new ConnectionId( actor.Connection.Id )] = new ClientBinding(
			actor.Connection, actor.AccountId, actor.Player, null );
		SendCharacterList( actor.Connection, actor.AccountId );
		PublishAll();
	}

	public void Disconnected( RpcActor actor )
	{
		var connectionId = new ConnectionId( actor.Connection.Id );
		if ( !_clients.Remove( connectionId, out var binding ) ) return;
		_presentationInvalidation.ForgetConnection( connectionId );
		_itemActionPresentations.Remove( connectionId );
		_civicSubjects.ClearConnection( connectionId );
		_entitlementQueries.Remove( connectionId );
		if ( binding.CharacterId is CharacterId characterId )
		{
			TrackLifecycle( ClearRaisedPistolsAsync(
				new InventoryActor( connectionId, binding.AccountId, characterId ), CancellationToken.None ) );
			_access.RevokeCharacter( connectionId, characterId );
			_interactions?.CharacterChanged( connectionId, characterId );
			_combatIntent?.ClearCharacter( characterId );
		}
		if ( _activeRestraintActions.Remove( connectionId, out var action ) ) action.Cancellation.Cancel();
		if ( _activePistolActions.Remove( connectionId, out var pistolAction ) ) pistolAction.Cancellation.Cancel();
		_access.RevokeConnection( connectionId );
		_interactions?.Disconnected( connectionId );
		_chat?.RevokeConnection( connectionId );
		_requests?.RevokeConnection( connectionId );
		if ( _scanner is not null ) TrackLifecycle( _scanner.DisconnectAsync( connectionId ).AsTask() );
		_ = binding.Player.HostStripPlayableBody();
		if ( binding.CharacterId is CharacterId disconnectedCharacter )
			_combatHealth.Remove( disconnectedCharacter );
		PublishLiveConnections();
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

		OperationResult result = command switch
		{
			RequestCharacterListCommand => ListCharacters( actor ),
			CreateCharacterCommand create => await CreateAsync( actor, create, cancellationToken ),
			LoadCharacterCommand load => await LoadAsync( actor, load.CharacterId, cancellationToken ),
			DeleteCharacterCommand delete => await DeleteAsync( actor, delete.CharacterId, cancellationToken ),
			UnloadCharacterCommand => await UnloadAsync( actor, cancellationToken ),
			MoveInventoryItemCommand move => await MoveAsync( actor, move, cancellationToken ),
			RunItemActionCommand action => await RunItemActionAsync( actor, action, cancellationToken ),
			DropItemCommand drop => await DropAsync( actor, drop, cancellationToken ),
			PickUpItemCommand pickup => await PickupAsync( actor, pickup, cancellationToken ),
			SendChatCommand chat => SendChat( actor, chat ),
			CancelActionCommand cancel => CancelAction( actor, cancel.InstanceId ),
			BeginInteractionCommand begin => await BeginInteractionAsync( actor, begin.Target, cancellationToken ),
			ContinueInteractionCommand continuation => ContinueInteraction( actor, continuation ),
			CloseInteractionCommand close => CloseInteraction( actor, close.SessionId ),
			RunSchemaCommandCommand schema => await RunSchemaCommandAsync( actor, schema, cancellationToken ),
			_ => OperationResult.Failure( ErrorCode.UnknownDefinition, "Client command is not registered by HL2RP." )
		};

		if ( result.Succeeded && command is not RequestCharacterListCommand and not SendChatCommand )
		{
			PublishLiveInventory();
			PublishAll();
		}
		return result;
	}

	public async ValueTask TickAsync()
	{
		if ( _disposed ) return;
		PublishLiveConnections();
		_interactions?.RevalidateActiveSessions();
		_sessions?.RevokeExpired();
		var now = _clock.UtcNow;
		if ( now >= _nextPresentationTargetPollAtUtc )
		{
			_nextPresentationTargetPollAtUtc = now + PresentationTargetPollInterval;
			ObserveRestraintTargets();
		}
		if ( _presentationInvalidation.IsRefreshDue( now ) ) PublishAll();
		if ( IsVerification && !_probeStarted )
		{
			if ( _clients.Count == 0 && _verificationActor is null ) return;
			_probeStarted = true;
			await RunVerificationProbeAsync();
		}
	}

	public async ValueTask DisposeAsync()
	{
		if ( _disposed ) return;
		_disposed = true;
		if ( _sessions is not null ) _sessions.SessionRevoked -= OnInteractionSessionRevoked;
		foreach ( var action in _activeRestraintActions.Values ) action.Cancellation.Cancel();
		foreach ( var action in _activePistolActions.Values ) action.Cancellation.Cancel();
		if ( _scanner is not null )
			foreach ( var connectionId in _clients.Keys.ToArray() )
				TrackLifecycle( _scanner.DisconnectAsync( connectionId ).AsTask() );
		foreach ( var connectionId in _clients.Keys.ToArray() ) _interactions?.Disconnected( connectionId );
		Task[] pending;
		lock ( _lifecycleSync ) pending = _pendingLifecycle.ToArray();
		foreach ( var task in pending ) await task;
		if ( _scanner is not null ) await _scanner.DrainCleanupAsync();
		foreach ( var binding in _clients.Values ) _ = binding.Player.HostStripPlayableBody();
		foreach ( var worldObject in _worldObjects.Values )
			if ( worldObject.IsValid() ) worldObject.Destroy();
		_worldObjects.Clear();
		foreach ( var action in _activeRestraintActions.Values ) action.Cancellation.Dispose();
		_activeRestraintActions.Clear();
		foreach ( var action in _activePistolActions.Values ) action.Cancellation.Dispose();
		_activePistolActions.Clear();
		if ( _verificationActor?.Body is GameObject verificationBody && verificationBody.IsValid() )
			verificationBody.Destroy();
		_verificationActor = null;
		_entitlementQueries.Clear();
		_clients.Clear();
	}

	private void TrackLifecycle( Task task )
	{
		lock ( _lifecycleSync ) _pendingLifecycle.Add( task );
	}

	private void OnInteractionSessionRevoked( InteractionSession session )
	{
		if ( !_disposed ) _presentationInvalidation.Invalidate();
	}

	private void PresentItemAction( ItemActionCommittedEvent committed )
	{
		if ( _disposed || committed.Presentation is null ||
			!_clients.TryGetValue( committed.Actor.ConnectionId, out var binding ) ||
			binding.AccountId != committed.Actor.AccountId || binding.CharacterId != committed.Actor.CharacterId ) return;
		_itemActionPresentations[committed.Actor.ConnectionId] = new ItemActionPresentationEnvelope(
			committed.Actor.CharacterId, _itemPresentationSequence.Next(), committed.Presentation );
		_presentationInvalidation.Invalidate();
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
		CancellationToken cancellationToken )
	{
		var request = HL2RPRuntimeProjection.ToCreationRequest( _context.Schema, command.Input );
		if ( request.Failed ) return Failure( request.Error! );
		var created = await _characters.CreateAsync( actor.AccountId, request.Value, cancellationToken );
		if ( created.Failed ) return Failure( created.Error! );
		SendCharacterList( actor.Connection, actor.AccountId );
		return OperationResult.Success();
	}

	private async ValueTask<OperationResult> LoadAsync(
		RpcActor actor,
		CharacterId characterId,
		CancellationToken cancellationToken )
	{
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
		var connectionId = new ConnectionId( actor.Connection.Id );
		var previous = FindActiveCharacter( connectionId );
		_itemActionPresentations.Remove( connectionId );
		_civicSubjects.ClearConnection( connectionId );
		if ( previous is not null )
			await ClearRaisedPistolsAsync( new InventoryActor( connectionId, actor.AccountId, previous.Id ), cancellationToken );
		var touched = await _aggregates.TouchLastPlayedAsync(
			actor.AccountId, characterId, _clock.UtcNow, cancellationToken );
		if ( touched.Failed ) return Failure( touched.Error! );
		var body = actor.Player.HostBuildPlayableBody( candidate =>
		{
			candidate.WorldTransform = actor.Player.GameObject.WorldTransform;
			ConfigureForcefieldCollisionTags( candidate, document.Value );
			candidate.AddComponent<PlayerController>();
			var renderer = candidate.AddComponent<SkinnedModelRenderer>();
			renderer.Model = Model.Load( modelPath.Value );
		} );
		if ( body.Failed ) return Failure( body.Error! );
		if ( previous is not null )
		{
			_access.RevokeCharacter( connectionId, previous.Id );
			_interactions?.CharacterChanged( connectionId, previous.Id );
			_combatIntent?.ClearCharacter( previous.Id );
		}
		_access.Grant( new InventoryGrant
		{
			ConnectionId = connectionId,
			CharacterId = characterId,
			InventoryId = main.Id,
			Capabilities = CharacterCapabilities,
			Kind = InventoryGrantKind.Character
		} );
		_clients[connectionId] = _clients[connectionId] with { CharacterId = characterId };
		if ( _combatHealth.Require( characterId ).Failed ) _combatHealth.Publish( characterId, 100, 100 );
		PublishCombatTargets();
		return OperationResult.Success();
	}

	private async ValueTask<OperationResult> DeleteAsync(
		RpcActor actor,
		CharacterId characterId,
		CancellationToken cancellationToken )
	{
		var connectionId = new ConnectionId( actor.Connection.Id );
		if ( _clients[connectionId].CharacterId == characterId )
			await ClearRaisedPistolsAsync( new InventoryActor( connectionId, actor.AccountId, characterId ), cancellationToken );
		var deleted = await _characters.DeleteAsync( actor.AccountId, characterId, cancellationToken );
		if ( deleted.Failed ) return deleted;
		_civicSubjects.ClearSubject( characterId );
		_civicSubjects.ClearConnection( connectionId );
		if ( _clients[connectionId].CharacterId == characterId ) UnloadBinding( connectionId, characterId );
		SendCharacterList( actor.Connection, actor.AccountId );
		return OperationResult.Success();
	}

	private async ValueTask<OperationResult> UnloadAsync( RpcActor actor, CancellationToken cancellationToken )
	{
		var connectionId = new ConnectionId( actor.Connection.Id );
		if ( _clients[connectionId].CharacterId is CharacterId characterId )
		{
			await ClearRaisedPistolsAsync( new InventoryActor( connectionId, actor.AccountId, characterId ), cancellationToken );
			UnloadBinding( connectionId, characterId );
		}
		return OperationResult.Success();
	}

	private async Task ClearRaisedPistolsAsync( InventoryActor actor, CancellationToken cancellationToken )
	{
		var main = MainInventory( actor.CharacterId );
		if ( main is null || _pistol is null ) return;
		foreach ( var placement in main.Placements )
		{
			var item = _repositories.Items.Find( DomainKeys.Item( placement.ItemId ) )?.Value;
			if ( item?.Definition.Value != HL2RPIds.Items.Pistol || !item.Traits.TryGetValue( "pistol", out var payload ) ) continue;
			PistolItemState state;
			try { state = HL2RPPersistence.Pistol.Deserialize( payload.Data, payload.TypeVersion ); }
			catch ( Exception ) { continue; }
			if ( !state.Raised ) continue;
			var lowered = await _pistol.LowerAsync( actor, main.Id, item.Id, cancellationToken );
			if ( lowered.Failed ) throw new InvalidOperationException( lowered.Error!.Message );
		}
	}

	private async ValueTask<OperationResult> ReconcileRaisedPistolsAsync( CancellationToken cancellationToken )
	{
		var raised = new List<(Hexagon.V2.Persistence.DocumentSnapshot<ItemRecord> Document, PistolItemState State)>();
		foreach ( var document in _repositories.Items.All().Where( value => value.Value.Definition.Value == HL2RPIds.Items.Pistol ) )
		{
			if ( !document.Value.Traits.TryGetValue( "pistol", out var payload ) ) continue;
			try
			{
				var state = HL2RPPersistence.Pistol.Deserialize( payload.Data, payload.TypeVersion );
				if ( state.Raised ) raised.Add( (document, state) );
			}
			catch ( Exception )
			{
				return OperationResult.Failure( ErrorCode.PersistedTypeInvalid, "Persisted pistol state is malformed." );
			}
		}
		if ( raised.Count == 0 ) return OperationResult.Success();
		var unit = _repositories.Provider.BeginUnitOfWork();
		foreach ( var entry in raised )
		{
			var editor = unit.Edit( _repositories.Items, entry.Document );
			if ( editor is null )
			{
				await HL2RPUnitOfWork.DisposeAsync( unit );
				return OperationResult.Failure( ErrorCode.Conflict, "Pistol changed during startup reconciliation." );
			}
			var traits = new Dictionary<string, TypedPayload>( editor.Value.Traits, StringComparer.Ordinal )
			{
				["pistol"] = HL2RPPersistence.Payload( HL2RPPersistence.Pistol, entry.State with { Raised = false } )
			};
			editor.Replace( editor.Value with { Traits = traits } );
			unit.Save( editor );
		}
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unit, cancellationToken );
		return committed.Succeeded ? OperationResult.Success() : OperationResult.Failure( ErrorCode.InternalError, committed.Error!.Message );
	}

	private void UnloadBinding( ConnectionId connectionId, CharacterId characterId )
	{
		if ( _activeRestraintActions.Remove( connectionId, out var action ) ) action.Cancellation.Cancel();
		if ( _activePistolActions.Remove( connectionId, out var pistolAction ) ) pistolAction.Cancellation.Cancel();
		_access.RevokeCharacter( connectionId, characterId );
		_interactions?.CharacterChanged( connectionId, characterId );
		_combatIntent?.ClearCharacter( characterId );
		_itemActionPresentations.Remove( connectionId );
		_civicSubjects.ClearConnection( connectionId );
		var binding = _clients[connectionId];
		_ = binding.Player.HostStripPlayableBody();
		_clients[connectionId] = binding with { CharacterId = null };
		_combatHealth.Remove( characterId );
		PublishCombatTargets();
	}

	private async ValueTask<OperationResult> MoveAsync(
		RpcActor rpc,
		MoveInventoryItemCommand command,
		CancellationToken cancellationToken )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		var result = await _inventory.MoveAsync(
			actor.Value, command.SourceId, command.TargetId, command.ItemId, command.X, command.Y, cancellationToken );
		if ( result.Succeeded ) _bags?.ItemMoved( command.ItemId );
		return result;
	}

	private async ValueTask<OperationResult> RunItemActionAsync(
		RpcActor rpc,
		RunItemActionCommand command,
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
			return amount.Succeeded && amount.Value is > 0 and <= int.MaxValue
				? Untyped( await _tokens!.SplitAsync( actor.Value, command.InventoryId, command.ItemId, (int)amount.Value, cancellationToken ) )
				: OperationResult.Failure( ErrorCode.InvalidArgument, "Token split amount is invalid." );
		}
		if ( executable.Route == ExecutableItemActionRoute.TokenCombine )
		{
			var other = new HL2RPCommandArguments( command.Arguments ).Guid( "other_item_id" );
			return other.Succeeded
				? Untyped( await _tokens!.CombineAsync( actor.Value, command.InventoryId, command.ItemId, new ItemId( other.Value ), cancellationToken ) )
				: Failure( other.Error! );
		}
		if ( executable.Route == ExecutableItemActionRoute.CombineLockInstall )
		{
			var session = CurrentSession( actor.Value, InteractionSessionKind.Door );
			return session is null
				? OperationResult.Failure( ErrorCode.Unauthorized, "A current door session is required." )
				: Untyped( await _combineLocks!.InstallAsync(
					actor.Value, session.Id, command.InventoryId, command.ItemId, cancellationToken ) );
		}
		if ( executable.Route == ExecutableItemActionRoute.HealthVialConsume )
			return Untyped( await _healthVials!.ConsumeAsync(
				actor.Value, command.InventoryId, command.ItemId, cancellationToken ) );
		if ( executable.Route == ExecutableItemActionRoute.RadioTuning )
		{
			var frequency = new HL2RPCommandArguments( command.Arguments ).String( "frequency" );
			return frequency.Failed ? Failure( frequency.Error! ) : Untyped( await _radio!.TuneAsync(
				actor.Value, command.InventoryId, command.ItemId, frequency.Value, cancellationToken ) );
		}
		if ( executable.Route == ExecutableItemActionRoute.RequestDevice )
		{
			var text = new HL2RPCommandArguments( command.Arguments ).String( "text" );
			return text.Failed ? Failure( text.Error! ) : Untyped( await _requests!.SendAsync(
				actor.Value, command.InventoryId, command.ItemId, text.Value, cancellationToken ) );
		}
		if ( executable.Route == ExecutableItemActionRoute.NoteEditor )
		{
			var body = new HL2RPCommandArguments( command.Arguments ).String( "body", true );
			return body.Failed ? Failure( body.Error! ) : Untyped( await _documents!.EditNoteAsync(
				actor.Value, command.InventoryId, command.ItemId, body.Value, cancellationToken ) );
		}
		if ( executable.Route == ExecutableItemActionRoute.RestraintIntent )
			return await SetRestraintAsync( actor.Value, new HL2RPCommandArguments( command.Arguments ), cancellationToken );
		if ( executable.Route == ExecutableItemActionRoute.CombatFireIntent )
			return await FirePistolAsync( actor.Value, command.InventoryId, command.ItemId, cancellationToken );
		return await _itemActions.ExecuteAsync(
			actor.Value,
			command.InventoryId,
			command.ItemId,
			command.ActionId,
			command.Arguments,
			cancellationToken );
	}

	private async ValueTask<OperationResult> DropAsync(
		RpcActor rpc,
		DropItemCommand command,
		CancellationToken cancellationToken )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		var transform = DropTransform( rpc.Player.PlayableBody ?? rpc.Player.GameObject );
		if ( !IsFinite( transform ) )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Authoritative drop transform is not finite." );
		var result = await _worldItems.DropAsync( actor.Value, command.SourceId, command.ItemId, transform, cancellationToken );
		if ( result.Succeeded )
		{
			_bags?.ItemMoved( command.ItemId );
			RestoreWorldItem( command.ItemId );
		}
		return result;
	}

	private async ValueTask<OperationResult> PickupAsync(
		RpcActor rpc,
		PickUpItemCommand command,
		CancellationToken cancellationToken )
	{
		var actor = RequireInventoryActor( rpc );
		if ( actor.Failed ) return Failure( actor.Error! );
		var result = await _worldItems.PickUpAsync(
			actor.Value, command.ItemId, command.DestinationId, cancellationToken );
		if ( result.Succeeded )
		{
			_bags?.ItemMoved( command.ItemId );
			if ( _worldObjects.Remove( command.ItemId, out var worldObject ) && worldObject.IsValid() ) worldObject.Destroy();
		}
		return result;
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
					return Untyped( await _scanner!.EnterAsync( actor.Value, droneId, cancellationToken ) );
				}
				if ( scannerFeature is HL2RPScannerDroneComponent )
					return Untyped( await _scanner!.EnterAsync( actor.Value, targetId, cancellationToken ) );
			}
		}
		var opened = _interactions!.Begin(
			actor.Value.ConnectionId, actor.Value.AccountId, actor.Value.CharacterId, target.Value );
		if ( opened.Failed ) return Failure( opened.Error! );
		if ( target.Value.Kind != InteractionTargetKind.SceneEntity ) return OperationResult.Success();
		var sceneId = new SceneEntityId( target.Value.Id );
		if ( !_features.TryGetValue( sceneId, out var feature ) )
			return OperationResult.Failure( ErrorCode.NotFound, "Scene feature is unavailable." );
		if ( feature is HL2RPForcefieldComponent )
			return Untyped( await _sceneBehavior!.ToggleForcefieldAsync(
				actor.Value, sceneId, cancellationToken ) );
		if ( opened.Value.Session is not InteractionSession session ) return OperationResult.Success();
		if ( feature is HL2RPMachineComponent )
		{
			var main = MainInventory( actor.Value.CharacterId );
			return main is null
				? OperationResult.Failure( ErrorCode.NotFound, "Character main inventory was not found." )
				: Untyped( await _commerce!.PurchaseFromMachineAsync(
					actor.Value, session.Id, main.Id, cancellationToken ) );
		}
		if ( feature is HL2RPDoorComponent )
			return Untyped( await _sceneBehavior!.ToggleDoorAsync(
				actor.Value, session.Id, cancellationToken ) );
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
		if ( _activeRestraintActions.TryGetValue( actor.Value.ConnectionId, out var action ) &&
			action.Ticket.TicketId.Value == instanceId && action.Actor == actor.Value )
		{
			action.Cancellation.Cancel();
			var result = _restraints!.Cancel( action.Ticket.TicketId, action.Actor );
			_activeRestraintActions.Remove( actor.Value.ConnectionId );
			PublishAll();
			return result;
		}
		if ( _activePistolActions.TryGetValue( actor.Value.ConnectionId, out var pistol ) &&
			pistol.InstanceId == instanceId && pistol.Actor == actor.Value )
		{
			pistol.Cancellation.Cancel();
			_combatIntent!.ClearCharacter( actor.Value.CharacterId );
			_activePistolActions.Remove( actor.Value.ConnectionId );
			PublishAll();
			return OperationResult.Success();
		}
		return OperationResult.Failure( ErrorCode.Unauthorized, "Action instance is not bound to the actor." );
	}

	private async ValueTask<OperationResult> FirePistolAsync(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId pistolId,
		CancellationToken cancellationToken )
	{
		var raised = _pistol!.IsHostRaised( actor, inventoryId, pistolId );
		if ( raised.Failed ) return Failure( raised.Error! );
		CancellationTokenSource? linked = null;
		ActivePistolRaiseAction? active = null;
		if ( !raised.Value )
		{
			if ( _activePistolActions.ContainsKey( actor.ConnectionId ) )
				return OperationResult.Failure( ErrorCode.Conflict, "Another pistol raise is active." );
			linked = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
			active = new ActivePistolRaiseAction(
				actor, Guid.NewGuid(), _clock.UtcNow + PistolCombatService.DefaultRaiseDelay, linked );
			_activePistolActions.Add( actor.ConnectionId, active );
			PublishAll();
		}
		try
		{
			return Untyped( await _combatIntent!.FireAsync(
				new CombatFireIntent( actor, inventoryId, pistolId, () =>
					active is null || _activePistolActions.TryGetValue( actor.ConnectionId, out var current ) && current == active ),
				linked?.Token ?? cancellationToken ) );
		}
		catch ( OperationCanceledException )
		{
			return OperationResult.Failure( ErrorCode.Conflict, "Pistol raise was cancelled." );
		}
		finally
		{
			if ( active is not null && _activePistolActions.TryGetValue( actor.ConnectionId, out var current ) && current == active )
				_activePistolActions.Remove( actor.ConnectionId );
			linked?.Dispose();
			if ( active is not null ) PublishAll();
		}
	}

	private async ValueTask<OperationResult> RunSchemaCommandAsync(
		RpcActor rpc,
		RunSchemaCommandCommand command,
		CancellationToken cancellationToken )
	{
		if ( !_context.Schema.Commands.TryGet( command.CommandId, out var definition ) )
			return OperationResult.Failure( ErrorCode.UnknownDefinition, "Schema command is not registered." );
		if ( command.CommandId is HL2RPIds.Commands.EntitlementQuery or
			HL2RPIds.Commands.EntitlementGrant or HL2RPIds.Commands.EntitlementRevoke )
			return await RunEntitlementCommandAsync(
				rpc, command.CommandId, new HL2RPCommandArguments( command.Arguments ), cancellationToken );
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
			HL2RPIds.Commands.CityObjectives => await SetObjectivesAsync( actor.Value, arguments, cancellationToken ),
			HL2RPIds.Commands.Priority => await SetPriorityAsync( actor.Value, arguments, cancellationToken ),
			HL2RPIds.Commands.RadioFrequency => await TuneRadioAsync( actor.Value, arguments, cancellationToken ),
			HL2RPIds.Commands.Introduce => await IntroduceAsync( actor.Value, arguments, cancellationToken ),
			HL2RPIds.Commands.DoorOwnership => await DoorOwnershipAsync( actor.Value, arguments, cancellationToken ),
			HL2RPIds.Commands.AdministrationAudit => PublishAdministrationAudit( actor.Value ),
			HL2RPIds.Commands.CommerceBuy => await BuyAsync( actor.Value, arguments, cancellationToken ),
			HL2RPIds.Commands.CommerceSell => await SellAsync( actor.Value, arguments, cancellationToken ),
			HL2RPIds.Commands.PermitPurchase => await PurchasePermitAsync( actor.Value, arguments, cancellationToken ),
			HL2RPIds.Commands.NoteWrite => await WriteNoteAsync( actor.Value, arguments, cancellationToken ),
			HL2RPIds.Commands.RestraintSet => await SetRestraintAsync( actor.Value, arguments, cancellationToken ),
			HL2RPIds.Commands.ScannerIntent => await ScannerIntentAsync( actor.Value, arguments, cancellationToken ),
			HL2RPIds.Commands.CombatRespawn => RespawnCharacter( actor.Value ),
			_ => OperationResult.Failure( ErrorCode.UnknownDefinition, "Schema command has no HL2RP runtime handler." )
		};
	}

	private async ValueTask<OperationResult> RunEntitlementCommandAsync(
		RpcActor rpc,
		string commandId,
		HL2RPCommandArguments arguments,
		CancellationToken cancellationToken )
	{
		var connectionId = new ConnectionId( rpc.Connection.Id );
		var characterId = FindActiveCharacter( connectionId )?.Id;
		var administrator = new HL2RPEntitlementAdministrator( rpc.AccountId, characterId );
		if ( !CanManageEntitlements( administrator ) )
			return OperationResult.Failure(
				ErrorCode.Unauthorized, "Authenticated account cannot manage entitlements." );
		var accountText = arguments.String( "account" );
		if ( accountText.Failed || !ulong.TryParse(
			accountText.Value,
			System.Globalization.NumberStyles.None,
			System.Globalization.CultureInfo.InvariantCulture,
			out var accountValue ) || accountValue == 0 )
			return OperationResult.Failure(
				ErrorCode.InvalidArgument, "Target account must be a non-zero unsigned account ID." );
		var targetAccountId = new AccountId( accountValue );
		if ( commandId == HL2RPIds.Commands.EntitlementQuery )
		{
			var observed = _entitlements.Observe( targetAccountId );
			if ( observed.Failed ) return Failure( observed.Error! );
			if ( !observed.Value.IsPersisted && !IsKnownAccount( targetAccountId ) )
				return OperationResult.Failure( ErrorCode.NotFound, "Target account is not known to this host." );
			_entitlementQueries[connectionId] = targetAccountId;
			return OperationResult.Success();
		}

		var flagText = arguments.String( "flag" );
		var revision = arguments.Integer( "revision" );
		if ( flagText.Failed || revision.Failed || revision.Value < 0 )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Entitlement flag or revision is invalid." );
		var flag = HL2RPAccountEntitlements.ParseSingleFlag( flagText.Value );
		if ( flag.Failed ) return Failure( flag.Error! );
		var expectedRevision = new Hexagon.V2.Persistence.DocumentRevision( revision.Value );
		var changed = commandId == HL2RPIds.Commands.EntitlementGrant
			? await _entitlements.GrantAsync(
				administrator, targetAccountId, flag.Value, expectedRevision, cancellationToken )
			: await _entitlements.RevokeAsync(
				administrator, targetAccountId, flag.Value, expectedRevision, cancellationToken );
		if ( changed.Failed ) return Failure( changed.Error! );
		_entitlementQueries[connectionId] = targetAccountId;
		return OperationResult.Success();
	}

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
		InventoryActor actor, HL2RPCommandArguments args, CancellationToken cancellationToken )
	{
		var title = args.String( "title" );
		var detail = args.String( "detail", true );
		var completed = args.Boolean( "completed" );
		if ( title.Failed || detail.Failed || completed.Failed ) return OperationResult.Failure( ErrorCode.InvalidArgument, "Objective arguments are invalid." );
		var city = _repositories.SceneEntities.All().Select( value => value.Value ).FirstOrDefault( value => value.Kind == "city" );
		if ( city is null ) return OperationResult.Failure( ErrorCode.NotFound, "City state is unavailable." );
		var id = args.String( "objective", true );
		var objectiveId = id.Succeeded && !string.IsNullOrWhiteSpace( id.Value )
			? id.Value
			: $"objective.{Guid.NewGuid():N}";
		var cityState = HL2RPPersistence.CityState.Deserialize( city.State.Data, city.State.TypeVersion );
		var objectives = HL2RPObjectiveState.Upsert(
			cityState.Objectives, objectiveId, $"{title.Value}\n{detail.Value}".Trim(), completed.Value, _clock.UtcNow );
		return Untyped( await _objectives!.ReplaceAsync( actor, city.Id, objectives, cancellationToken ) );
	}

	private async ValueTask<OperationResult> SetPriorityAsync(
		InventoryActor actor, HL2RPCommandArguments args, CancellationToken cancellationToken )
	{
		var target = args.Guid( "character" );
		var priority = args.String( "priority" );
		var record = args.String( "record", true );
		if ( target.Failed || priority.Failed || record.Failed || !Enum.TryParse<CivicPriorityStatus>( priority.Value, true, out var parsed ) )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Priority arguments are invalid." );
		return Untyped( await _civic!.UpdateRecordAsync(
			actor, new CharacterId( target.Value ), parsed, record.Value, cancellationToken ) );
	}

	private async ValueTask<OperationResult> TuneRadioAsync(
		InventoryActor actor, HL2RPCommandArguments args, CancellationToken cancellationToken )
	{
		var item = args.Guid( "item" );
		var frequency = args.String( "frequency" );
		var enabled = args.Boolean( "enabled" );
		var main = MainInventory( actor.CharacterId );
		if ( item.Failed || frequency.Failed || enabled.Failed || main is null ) return OperationResult.Failure( ErrorCode.InvalidArgument, "Radio arguments are invalid." );
		return Untyped( await _radio!.ConfigureAsync(
			actor, main.Id, new ItemId( item.Value ), frequency.Value, enabled.Value, cancellationToken ) );
	}

	private async ValueTask<OperationResult> IntroduceAsync(
		InventoryActor actor, HL2RPCommandArguments args, CancellationToken cancellationToken )
	{
		var target = args.Guid( "character" );
		return target.Failed ? Failure( target.Error! ) : Untyped( await _recognition!.IntroduceAsync( actor, new CharacterId( target.Value ), cancellationToken ) );
	}

	private async ValueTask<OperationResult> BuyAsync(
		InventoryActor actor, HL2RPCommandArguments args, CancellationToken cancellationToken )
	{
		var session = args.Guid( "session" );
		var definition = args.String( "definition" );
		var quantity = args.Integer( "quantity" );
		var main = MainInventory( actor.CharacterId );
		if ( session.Failed || definition.Failed || quantity.Failed || quantity.Value is < 1 or > 64 || main is null )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Vendor purchase arguments are invalid." );
		return Untyped( await _commerce!.BuyAsync(
			actor, new InteractionSessionId( session.Value ), main.Id,
			new DefinitionId( definition.Value ), (int)quantity.Value, cancellationToken ) );
	}

	private async ValueTask<OperationResult> SellAsync(
		InventoryActor actor, HL2RPCommandArguments args, CancellationToken cancellationToken )
	{
		var session = args.Guid( "session" );
		var inventory = args.Guid( "inventory" );
		var item = args.Guid( "item" );
		if ( session.Failed || inventory.Failed || item.Failed ) return OperationResult.Failure( ErrorCode.InvalidArgument, "Vendor sale arguments are invalid." );
		var result = await _commerce!.SellAsync(
			actor, new InteractionSessionId( session.Value ), new InventoryId( inventory.Value ), new ItemId( item.Value ), cancellationToken );
		if ( result.Succeeded ) _bags?.ItemMoved( new ItemId( item.Value ) );
		return Untyped( result );
	}

	private async ValueTask<OperationResult> PurchasePermitAsync(
		InventoryActor actor, HL2RPCommandArguments args, CancellationToken cancellationToken )
	{
		var kind = args.String( "permit" );
		var main = MainInventory( actor.CharacterId );
		if ( kind.Failed || main is null ) return OperationResult.Failure( ErrorCode.InvalidArgument, "Permit purchase arguments are invalid." );
		var parsed = HL2RPPresentationContracts.ParsePermitKind( kind.Value );
		return parsed.Failed
			? Failure( parsed.Error! )
			: Untyped( await _permitPurchases!.PurchaseAsync( actor, main.Id, parsed.Value, cancellationToken ) );
	}

	private async ValueTask<OperationResult> WriteNoteAsync(
		InventoryActor actor, HL2RPCommandArguments args, CancellationToken cancellationToken )
	{
		var item = args.Guid( "item" );
		var body = args.String( "body", true );
		var main = MainInventory( actor.CharacterId );
		if ( item.Failed || body.Failed || main is null ) return OperationResult.Failure( ErrorCode.InvalidArgument, "Note arguments are invalid." );
		return Untyped( await _documents!.EditNoteAsync( actor, main.Id, new ItemId( item.Value ), body.Value, cancellationToken ) );
	}

	private async ValueTask<OperationResult> SetRestraintAsync(
		InventoryActor actor, HL2RPCommandArguments args, CancellationToken cancellationToken )
	{
		var target = args.Guid( "character" );
		var restrain = args.Boolean( "restrain" );
		var search = args.Boolean( "search" );
		if ( target.Failed || restrain.Failed || search.Failed ) return OperationResult.Failure( ErrorCode.InvalidArgument, "Restraint arguments are invalid." );
		var targetId = new CharacterId( target.Value );
		if ( search.Value )
		{
			var targetInventory = MainInventory( targetId );
			return targetInventory is null
				? OperationResult.Failure( ErrorCode.NotFound, "Search target main inventory was not found." )
				: Untyped( _search!.Open( actor, targetId, targetInventory.Id ) );
		}
		if ( !restrain.Value ) return Untyped( await _restraints!.UnrestrainAsync( actor, targetId, cancellationToken ) );
		var main = MainInventory( actor.CharacterId );
		var zip = main?.Placements.Select( value => _repositories.Items.Find( DomainKeys.Item( value.ItemId ) )?.Value )
			.FirstOrDefault( value => value?.Definition.Value == HL2RPIds.Items.ZipTie );
		if ( main is null || zip is null ) return OperationResult.Failure( ErrorCode.NotFound, "A zip tie is required." );
		var ticket = _restraints!.Begin( actor, targetId, main.Id, zip.Id );
		if ( ticket.Failed ) return Failure( ticket.Error! );
		if ( _activeRestraintActions.ContainsKey( actor.ConnectionId ) )
		{
			_restraints.Cancel( ticket.Value.TicketId, actor );
			return OperationResult.Failure( ErrorCode.Conflict, "Another restraint action is already active." );
		}
		var linked = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
		var active = new ActiveRestraintAction( actor, ticket.Value, linked );
		_activeRestraintActions.Add( actor.ConnectionId, active );
		PublishAll();
		try
		{
			var delay = ticket.Value.CompletesAtUtc - _clock.UtcNow;
			if ( delay > TimeSpan.Zero ) await Task.Delay( delay, linked.Token );
			var stillActive = _activeRestraintActions.TryGetValue( actor.ConnectionId, out var current ) && current == active;
			return stillActive
				? Untyped( await _restraints.CompleteAsync( ticket.Value.TicketId, actor, linked.Token ) )
				: OperationResult.Failure( ErrorCode.Conflict, "Restraint action was cancelled." );
		}
		catch ( OperationCanceledException )
		{
			_ = _restraints.Cancel( ticket.Value.TicketId, actor );
			return OperationResult.Failure( ErrorCode.Conflict, "Restraint action was cancelled." );
		}
		finally
		{
			if ( _activeRestraintActions.TryGetValue( actor.ConnectionId, out var current ) && current == active )
				_activeRestraintActions.Remove( actor.ConnectionId );
			linked.Dispose();
			PublishAll();
		}
	}

	private async ValueTask<OperationResult> ScannerIntentAsync(
		InventoryActor actor, HL2RPCommandArguments args, CancellationToken cancellationToken )
	{
		var intent = args.String( "intent" );
		if ( intent.Failed ) return Failure( intent.Error! );
		var sessionId = args.Guid( "session" );
		var active = sessionId.Succeeded
			? _scanner!.ActiveSessions.SingleOrDefault( value => value.SessionId.Value == sessionId.Value )
			: null;
		return intent.Value switch
		{
			"enter" => await EnterScannerAsync( actor, cancellationToken ),
			"exit" when active is not null => await _scanner!.ExitAsync( actor, active.SessionId, cancellationToken ),
			"spotlight" when active is not null => Untyped( await _scanner!.ToggleSpotlightAsync( actor, active.SessionId, cancellationToken ) ),
			"flash" when active is not null => _scanner!.Flash( actor, active.SessionId ),
			"photo" when active is not null => Untyped( await _scanner!.TakePhotoAsync( actor, active.SessionId, cancellationToken ) ),
			"move" when active is not null => await ApplyScannerInputAsync( actor, active.SessionId, args, cancellationToken ),
			_ => OperationResult.Failure( ErrorCode.InvalidArgument, "Scanner intent or session is invalid." )
		};
	}

	private async ValueTask<OperationResult> ApplyScannerInputAsync(
		InventoryActor actor,
		InteractionSessionId sessionId,
		HL2RPCommandArguments args,
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
		return Untyped( await _scanner!.ApplyInputAsync( actor, new ScannerInputIntent(
			sessionId, sequence.Value, forward.Value, right.Value, up.Value, yaw.Value, pitch.Value ), cancellationToken ) );
	}

	private async ValueTask<OperationResult> EnterScannerAsync( InventoryActor actor, CancellationToken cancellationToken )
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
		return Untyped( await _scanner!.EnterAsync( actor, target, cancellationToken ) );
	}

	private async ValueTask<OperationResult> DoorOwnershipAsync(
		InventoryActor actor,
		HL2RPCommandArguments arguments,
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
		return intent.Value switch
		{
			"claim" => Untyped( await _doorOwnership!.ClaimAsync(
				actor, session.Id, cancellationToken ) ),
			"release" => Untyped( await _doorOwnership!.ReleaseAsync(
				actor, session.Id, cancellationToken ) ),
			_ => OperationResult.Failure( ErrorCode.InvalidArgument, "Door ownership intent must be claim or release." )
		};
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
			var body = binding.Player.HostBuildPlayableBody( candidate =>
			{
				candidate.WorldTransform = binding.Player.GameObject.WorldTransform;
				ConfigureForcefieldCollisionTags( candidate, character );
				candidate.AddComponent<PlayerController>();
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
				_ = binding.Player.HostStripPlayableBody();
				Log.Error( exception, "Failed to restore the active-character inventory grant." );
				return OperationResult.Failure( ErrorCode.InternalError, "Respawn authority could not be restored." );
			}

			var respawned = _combatIntent!.Respawn( actor );
			if ( respawned.Failed )
			{
				_access.RevokeCharacter( actor.ConnectionId, actor.CharacterId );
				_ = binding.Player.HostStripPlayableBody();
				PublishAll();
				return Failure( respawned.Error! );
			}

			if ( _combatHealth.Require( actor.CharacterId ).Failed )
				_combatHealth.Publish( actor.CharacterId, 100, 100 );
			_presentationInvalidation.ClearDeathDeadline( actor.CharacterId );
			PublishCombatTargets();
			PublishAll();
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
		_clients.Values.Any( value => value.AccountId == accountId ) ||
		_repositories.Characters.All().Any( value => value.Value.AccountId == accountId );

	private InventoryRecord? MainInventory( CharacterId characterId )
	{
		var owner = InventoryOwner.Character( characterId );
		var index = _repositories.OwnerInventories.Find( DomainKeys.OwnerInventory( owner, "main" ) );
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
		if ( connected is not null && connected.Value.Player.PlayableBody is GameObject body )
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

	private HexPlayerBody? FindPlayer( CharacterId characterId ) =>
		_clients.Values.FirstOrDefault( value => value.CharacterId == characterId )?.Player;

	private WorldPoint? ActorPosition( InventoryActor actor )
	{
		var body = FindPlayer( actor.CharacterId )?.PlayableBody;
		if ( body is null ) return null;
		var position = body.WorldPosition;
		return new WorldPoint( position.x, position.y, position.z );
	}

	private bool HasLineOfSight( WorldPoint actor, WorldPoint target )
	{
		var start = new Vector3( actor.X, actor.Y, actor.Z );
		var end = new Vector3( target.X, target.Y, target.Z );
		var trace = _context.Scene.Trace.Ray( start, end ).Run();
		return !trace.Hit || trace.Distance >= Vector3.DistanceBetween( start, end ) - 32f;
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
		var position = gameObject.WorldPosition + gameObject.WorldTransform.Forward * 48f + Vector3.Up * 24f;
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

	private void PublishLiveInventory()
	{
		var allInventories = _repositories.Inventories.All().Select( value => value.Value ).ToArray();
		var rows = new List<LiveInventoryItemView>();
		foreach ( var inventory in allInventories )
		{
			var owner = OwningCharacter( inventory, allInventories );
			foreach ( var placement in inventory.Placements )
			{
				var item = _repositories.Items.Find( DomainKeys.Item( placement.ItemId ) )?.Value;
				if ( item is not null ) rows.Add( new LiveInventoryItemView( item.Id, owner, item.Definition, item.Traits ) );
			}
		}
		_liveInventory.Publish( _context.Persistence.Health.Sequence, rows );
		PublishLiveConnections();
	}

	private void PublishLiveConnections()
	{
		var rows = new List<LiveChatConnection>();
		var authorities = new List<LiveChatAuthority>();
		foreach ( var pair in _clients )
		{
			var character = FindActiveCharacter( pair.Key );
			var body = pair.Value.Player.PlayableBody;
			if ( character is null || body is null ) continue;
			var position = body.WorldPosition;
			rows.Add( new LiveChatConnection(
				pair.Key, character.Id, new ChatPosition( position.x, position.y, position.z ) ) );
			authorities.Add( new LiveChatAuthority(
				pair.Key,
				character.AccountId,
				character.Id,
				character.Faction,
				HL2RPRuntimeProjection.PermissionsFor( character ) ) );
		}
		var version = Math.Max( _presentationRevision, _context.Persistence.Health.Sequence );
		_chatPositions.Publish( version, rows );
		_chatAuthorities.Publish( version, authorities );
		PublishCombatTargets();
	}

	private void PublishCombatTargets()
	{
		var targets = new List<CombatPlayerTarget>();
		foreach ( var pair in _clients )
		{
			var character = FindActiveCharacter( pair.Key );
			var body = pair.Value.Player.PlayableBody;
			var main = character is null ? null : MainInventory( character.Id );
			if ( character is null || body is null || main is null || _combatLifecycle?.GetState( character.Id ) is not null ) continue;
			if ( _combatHealth.Require( character.Id ).Failed ) _combatHealth.Publish( character.Id, 100, 100 );
			targets.Add( new CombatPlayerTarget(
				character.Id.Value.ToString( "D" ),
				new InventoryActor( pair.Key, pair.Value.AccountId, character.Id ),
				main.Id,
				DropTransform( body ) ) );
		}
		_combatTargets.Publish( targets );
	}

	private string? ResolveCombatTargetToken( GameObject hit )
	{
		for ( var current = hit; current is not null and not Scene; current = current.Parent )
		{
			foreach ( var pair in _clients )
			{
				if ( pair.Value.Player.PlayableBody != current || pair.Value.CharacterId is not CharacterId characterId ) continue;
				return characterId.Value.ToString( "D" );
			}
		}
		return null;
	}

	private CharacterId? OwningCharacter( InventoryRecord inventory, IReadOnlyList<InventoryRecord> all )
	{
		var current = inventory;
		var seen = new HashSet<InventoryId>();
		while ( seen.Add( current.Id ) )
		{
			if ( current.Owner.Kind == InventoryOwnerKind.Character ) return new CharacterId( current.Owner.OwnerId );
			if ( current.Owner.Kind != InventoryOwnerKind.ParentItem ) return null;
			var parent = new ItemId( current.Owner.OwnerId );
			current = all.FirstOrDefault( candidate => candidate.Find( parent ) is not null )!;
			if ( current is null ) return null;
		}
		return null;
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
		var revision = Math.Max( ++_presentationRevision, _context.Persistence.Health.Sequence );
		foreach ( var pair in _clients.ToArray() )
		{
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
				privateSnapshot = BuildPrivateSnapshot( character, MainInventory( character.Id )?.Id );
				inventories = BuildInventories( pair.Key, character.Id );
				views = views.Concat( BuildSchemaViews( pair.Key, character, revision ) ).ToArray();
			}
			_context.Transport.SendClientState(
				pair.Value.Connection,
				publicSnapshot,
				privateSnapshot,
				BuildRoster( pair.Key, revision ),
				views,
				inventories,
				BuildActionProgress( pair.Key ) );
		}
		_presentationInvalidation.AcknowledgePublished( publication );
	}

	private ActionProgressSnapshot? BuildActionProgress( ConnectionId connectionId )
	{
		if ( _activeRestraintActions.TryGetValue( connectionId, out var action ) )
			return new ActionProgressSnapshot(
				action.Ticket.TicketId.Value,
				new ActionId( HL2RPIds.Actions.Restrain ),
				"Applying restraint",
				action.Ticket.CompletesAtUtc - RestraintService.RestraintDuration,
				RestraintService.RestraintDuration,
				true );
		if ( _activePistolActions.TryGetValue( connectionId, out var pistol ) )
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
			_activeRestraintActions.ContainsKey( connection ) || _activePistolActions.ContainsKey( connection ) );
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

	private PlayerRosterSnapshot BuildRoster( ConnectionId recipient, long revision )
	{
		var rows = new List<PlayerRosterRowSnapshot>();
		var viewer = FindActiveCharacter( recipient );
		foreach ( var pair in _clients.OrderBy( value => value.Key.Value ) )
		{
			var character = FindActiveCharacter( pair.Key );
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
		return new PlayerRosterSnapshot( revision, rows );
	}

	private string DisplayNameFor( CharacterRecord? viewer, CharacterRecord subject )
		=> HL2RPRuntimeProjection.PresentationName( viewer, subject, _repositories );

	private IReadOnlyList<InventorySnapshot> BuildInventories( ConnectionId connectionId, CharacterId characterId )
	{
		var snapshots = new List<InventorySnapshot>();
		var character = _repositories.Characters.Find( DomainKeys.Character( characterId ) )?.Value;
		if ( character is null ) return snapshots;
		foreach ( var document in _repositories.Inventories.All() )
		{
			var inventory = document.Value;
			if ( !_access.Has( connectionId, characterId, inventory.Id, InventoryCapability.View ) ) continue;
			var kind = inventory.Owner == InventoryOwner.Character( characterId )
				? InventoryViewKind.Main
				: inventory.Owner.Kind == InventoryOwnerKind.SceneEntity
					? InventoryViewKind.Storage
					: inventory.Owner.Kind == InventoryOwnerKind.Character ? InventoryViewKind.Search : InventoryViewKind.Bag;
			var inventoryItems = inventory.Placements
				.Select( placement => _repositories.Items.Find( DomainKeys.Item( placement.ItemId ) )?.Value )
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
		var nestedBags = _repositories.Inventories.All().Count( candidate =>
			candidate.Value.Owner == InventoryOwner.ParentItem( item.Id ) );
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
			_repositories.Inventories.All().Any( candidate =>
				candidate.Value.Owner == InventoryOwner.ParentItem( item.Id ) ),
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
		var owners = _repositories.CharacterReferences.All()
			.Where( value => value.Value.Category == "door_ownership" && value.Value.SceneEntityId == id )
			.Select( value => value.Value.CharacterId )
			.ToArray();
		var ownerStatus = owners.Length switch
		{
			0 => "unowned",
			1 when owners[0] == character.Id => "self",
			1 => "other",
			_ => "conflict"
		};
		var canClaim = owners.Length == 0 && !decoded.Value.CombineLocked;
		var claimReason = canClaim ? string.Empty : owners.Length switch
		{
			> 1 => "Door ownership is ambiguous.",
			1 when owners[0] == character.Id => "You already own this door.",
			1 => "This door is owned by another character.",
			_ => "A Combine-locked door cannot be claimed."
		};
		var canRelease = owners.Length == 1 && owners[0] == character.Id;
		var releaseReason = canRelease ? string.Empty : owners.Length switch
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
		var city = _repositories.SceneEntities.All().Select( value => value.Value ).FirstOrDefault( value => value.Kind == "city" );
		if ( city is not null )
		{
			try
			{
				var state = HL2RPPersistence.CityState.Deserialize( city.State.Data, city.State.TypeVersion );
				foreach ( var objective in state.Objectives ) rows.Add( new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
				{
					[HL2RPPresentationFields.Objectives.ObjectiveId] = SnapshotValue.Choice( objective.Id ),
					[HL2RPPresentationFields.Objectives.Title] = SnapshotValue.String( objective.Text.Split( '\n' )[0] ),
					[HL2RPPresentationFields.Objectives.Detail] = SnapshotValue.String( string.Join( "\n", objective.Text.Split( '\n' ).Skip( 1 ) ) ),
					[HL2RPPresentationFields.Objectives.UpdatedAtUnixMilliseconds] = SnapshotValue.Integer( objective.UpdatedAtUtc.ToUnixTimeMilliseconds() ),
					[HL2RPPresentationFields.Objectives.Completed] = SnapshotValue.Boolean( objective.Completed )
				} );
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
		var viewerBody = FindPlayer( viewerId )?.PlayableBody;
		if ( viewerBody is null ) return null;
		return _clients.Values
			.Where( value => value.CharacterId is CharacterId id && id != viewerId && value.Player.PlayableBody is not null )
			.Select( value => new
			{
				Character = _repositories.Characters.Find( DomainKeys.Character( value.CharacterId!.Value ) )?.Value,
				Body = value.Player.PlayableBody!,
				Distance = Vector3.DistanceBetween( viewerBody.WorldPosition, value.Player.PlayableBody!.WorldPosition )
			} )
			.Where( value => value.Character is not null && value.Distance <= 130f &&
				HasLineOfSight(
					new WorldPoint( viewerBody.WorldPosition.x, viewerBody.WorldPosition.y, viewerBody.WorldPosition.z ),
					new WorldPoint( value.Body.WorldPosition.x, value.Body.WorldPosition.y, value.Body.WorldPosition.z ) ) )
			.OrderBy( value => value.Distance )
			.Select( value => value.Character )
			.FirstOrDefault();
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

	private void RestoreWorldItems()
	{
		foreach ( var world in _worldItems.LoadWorldItems() ) RestoreWorldItem( world.ItemId );
	}

	private void RestoreWorldItem( ItemId itemId )
	{
		if ( _worldObjects.ContainsKey( itemId ) ) return;
		var world = _repositories.WorldItems.Find( DomainKeys.WorldItem( itemId ) )?.Value;
		var item = _repositories.Items.Find( DomainKeys.Item( itemId ) )?.Value;
		if ( world is null || item is null || !_context.Schema.Items.TryGet( item.Definition.Value, out var definition ) ||
			string.IsNullOrWhiteSpace( definition!.WorldModel ) || !_worldModels.IsValidModel( definition.WorldModel ) ) return;
		var gameObject = new GameObject( true, $"HL2RP World Item {itemId}" );
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
		gameObject.NetworkSpawn();
		_worldObjects[itemId] = gameObject;
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
		if ( HexagonRuntimeSystem.Current is not null ) _ = await HexagonRuntimeSystem.Current.ShutdownAsync();
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
		var body = binding.Player.HostBuildPlayableBody( candidate =>
		{
			candidate.WorldTransform = binding.Player.GameObject.WorldTransform;
			ConfigureForcefieldCollisionTags( candidate, character );
			candidate.AddComponent<PlayerController>();
			candidate.AddComponent<SkinnedModelRenderer>().Model = Model.Load( modelPath.Value );
		} );
		if ( body.Failed ) throw new InvalidOperationException( body.Error!.Message );
		_access.RevokeConnection( connectionId );
		_access.Grant( new InventoryGrant
		{
			ConnectionId = connectionId,
			CharacterId = character.Id,
			InventoryId = main.Id,
			Capabilities = CharacterCapabilities,
			Kind = InventoryGrantKind.Character
		} );
		_clients[connectionId] = binding with { CharacterId = character.Id };
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
			var source = _owner.FindPlayer( actor.CharacterId )?.PlayableBody;
			var target = _owner.FindPlayer( subjectCharacterId )?.PlayableBody;
			if ( source is null || target is null )
				return OperationResult.Failure( ErrorCode.NotFound, "Encounter target is not active." );
			if ( Vector3.DistanceBetween( source.WorldPosition, target.WorldPosition ) > 130f )
				return OperationResult.Failure( ErrorCode.PolicyDenied, "Encounter target is out of range." );
			var trace = _owner._context.Scene.Trace.Ray( source.WorldPosition, target.WorldPosition ).Run();
			return !trace.Hit || trace.GameObject == target
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

	private sealed class HL2RPCombatLifecycleBoundary : ICombatLifecycleBoundary
	{
		private readonly HL2RPHostApplication _owner;
		public HL2RPCombatLifecycleBoundary( HL2RPHostApplication owner ) => _owner = owner;
		public void ClearSessions( InventoryActor actor )
		{
			if ( _owner._activeRestraintActions.Remove( actor.ConnectionId, out var restraintAction ) )
				restraintAction.Cancellation.Cancel();
			if ( _owner._activePistolActions.Remove( actor.ConnectionId, out var pistolAction ) )
				pistolAction.Cancellation.Cancel();
			_owner._access.RevokeCharacter( actor.ConnectionId, actor.CharacterId );
			_owner._interactions?.CharacterChanged( actor.ConnectionId, actor.CharacterId );
			_owner._combatIntent?.ClearCharacter( actor.CharacterId );
			if ( _owner._clients.TryGetValue( actor.ConnectionId, out var binding ) &&
				binding.CharacterId == actor.CharacterId )
			{
				var stripped = binding.Player.HostStripPlayableBody();
				if ( stripped.Failed ) Log.Error( $"Failed to strip dead player body: {stripped.Error!.Message}" );
			}
			_owner.PublishCombatTargets();
		}
		public void PublishDeath( DeathTransitionReceipt transition )
		{
			_owner._presentationInvalidation.TrackDeathDeadline(
				transition.Respawn.CharacterId, transition.Respawn.RespawnAvailableAtUtc );
			if ( transition.DroppedPistol is not null )
				_owner.RestoreWorldItem( transition.DroppedPistol.ItemId );
			_owner.PublishAll();
		}
		// The host finishes body, grant, and health restoration before publishing a
		// respawn. Publishing from inside CombatLifecycleService would expose an
		// alive snapshot before those authorities are restored.
		public void PublishRespawn( DeathRespawnState state ) { }
	}
}
