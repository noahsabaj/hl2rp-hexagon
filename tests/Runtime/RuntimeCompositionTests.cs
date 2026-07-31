#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Composition;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Runtime;
using HL2RP.V2.Schema;
using HL2RP.V2.Showcase.Restraint;
using HL2RP.V2.Tests.Features;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace HL2RP.V2.Tests.Runtime;

[TestClass]
public sealed class RuntimeCompositionTests
{
	[TestMethod]
	public void MainSceneAutoStartsNetworkingWithoutCompetingPlayerSpawner()
	{
		var scenePath = Path.Combine( FindRoot(), "Assets", "scenes", "main.scene" );
		using var document = JsonDocument.Parse( File.ReadAllBytes( scenePath ) );
		var helpers = document.RootElement.GetProperty( "GameObjects" )
			.EnumerateArray()
			.SelectMany( gameObject => gameObject.GetProperty( "Components" ).EnumerateArray() )
			.Where( component =>
				component.TryGetProperty( "__type", out var type ) &&
				type.GetString() == "Sandbox.NetworkHelper" )
			.ToArray();

		Assert.HasCount( 1, helpers );
		Assert.IsTrue( helpers[0].GetProperty( "StartServer" ).GetBoolean() );
		Assert.AreEqual( JsonValueKind.Null, helpers[0].GetProperty( "PlayerPrefab" ).ValueKind,
			"Hexagon must remain the only player spawner." );
	}

	[TestMethod]
	public void SixModelCatalogEnforcesFactionAndClassAndResolvesInstalledPaths()
	{
		var catalog = new HL2RPCharacterModelCatalog();
		Assert.IsTrue( catalog.IsAllowed(
			new DefinitionId( HL2RPIds.Models.Citizen01 ), new FactionId( HL2RPIds.Factions.Citizen ), null ) );
		Assert.IsFalse( catalog.IsAllowed(
			new DefinitionId( HL2RPIds.Models.CivilProtectionUnit ), new FactionId( HL2RPIds.Factions.Citizen ), null ) );
		Assert.IsFalse( catalog.IsAllowed(
			new DefinitionId( HL2RPIds.Models.CivilProtectionUnit ), new FactionId( HL2RPIds.Factions.CivilProtection ), null ) );
		Assert.IsTrue( catalog.IsAllowed(
			new DefinitionId( HL2RPIds.Models.CivilProtectionUnit ),
			new FactionId( HL2RPIds.Factions.CivilProtection ), new ClassId( HL2RPIds.Classes.Unit ) ) );
		Assert.HasCount( 3, catalog.ModelPaths );
		Assert.IsTrue( catalog.ModelPaths.All( path => path.StartsWith( "models/", StringComparison.Ordinal ) ) );
		Assert.IsTrue( catalog.ModelPaths.All( path => path.EndsWith( ".vmdl", StringComparison.Ordinal ) ) );
	}

	[TestMethod]
	public void WhitelistFlagContractRejectsUnknownAndCompositeAdministrativeInput()
	{
		Assert.IsTrue( HL2RPAccountEntitlements.IsValidSet( HL2RPAccountEntitlements.All ) );
		Assert.IsFalse( HL2RPAccountEntitlements.IsValidSet( (HL2RPWhitelist)8 ) );
		Assert.IsTrue( HL2RPAccountEntitlements.IsSingleFlag( HL2RPWhitelist.CivilProtection ) );
		Assert.IsFalse( HL2RPAccountEntitlements.IsSingleFlag(
			HL2RPWhitelist.CivilProtection | HL2RPWhitelist.Overwatch ) );
		Assert.IsTrue( HL2RPAccountEntitlements.ParseSingleFlag( "city_administration" ).Succeeded );
		Assert.IsTrue( HL2RPAccountEntitlements.ParseSingleFlag( "all" ).Failed );
	}

	[TestMethod]
	public async Task FeatureAuthorizationUsesCanonicalCommittedRoleAndAllowsCpOrOverwatchLockInstall()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var citizen = environment.Actor( 100 );
		var cp = environment.Actor( 101 );
		var ota = environment.Actor( 102 );
		await environment.SeedAsync( unit =>
		{
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( citizen.CharacterId ), environment.Character( citizen ) );
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( cp.CharacterId ),
				environment.Character( cp ) with { Faction = new FactionId( HL2RPIds.Factions.CivilProtection ), Class = new ClassId( HL2RPIds.Classes.Unit ) } );
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( ota.CharacterId ),
				environment.Character( ota ) with { Faction = new FactionId( HL2RPIds.Factions.Overwatch ) } );
		} );
		var policy = new HL2RPFeatureRuntimePolicy( environment.Repositories );
		Assert.IsFalse( policy.Evaluate( Context( citizen, HL2RPFeatureOperation.InstallCombineLock ) ).Allowed );
		Assert.IsTrue( policy.Evaluate( Context( cp, HL2RPFeatureOperation.InstallCombineLock ) ).Allowed );
		Assert.IsTrue( policy.Evaluate( Context( ota, HL2RPFeatureOperation.InstallCombineLock ) ).Allowed );
		Assert.IsFalse( policy.HasPermission( citizen.AccountId, citizen.CharacterId, HL2RPIds.Permissions.CivicData ) );
		Assert.IsTrue( policy.HasPermission( cp.AccountId, cp.CharacterId, HL2RPIds.Permissions.CivicData ) );
		Assert.IsTrue( policy.HasPermission( cp.AccountId, cp.CharacterId, HL2RPIds.Permissions.Priority ) );
		Assert.IsFalse( policy.Evaluate( Context( citizen with { AccountId = new AccountId( 999 ) }, HL2RPFeatureOperation.OpenBag ) ).Allowed );
	}

	[TestMethod]
	public void RemoteAndRestrainedWorldPickupAndRestrainedMoveDropDeny()
	{
		var actor = new InventoryActor( ConnectionId.New(), new AccountId( 77 ), CharacterId.New() );
		var restraint = new FixedRestraintState( false );
		var pickup = new HL2RPWorldPickupRuntimePolicy(
			restraint,
			_ => new WorldPoint( 0, 0, 0 ),
			(_, _) => true );
		var context = PickupContext( actor, 131f );
		Assert.IsFalse( pickup.Evaluate( context ).Allowed, "Known item IDs must not permit remote pickup." );
		Assert.IsTrue( pickup.Evaluate( PickupContext( actor, 129f ) ).Allowed );

		restraint.Restrained = true;
		Assert.IsFalse( pickup.Evaluate( PickupContext( actor, 1f ) ).Allowed );
		Assert.IsFalse( new HL2RPWorldDropRuntimePolicy( restraint ).Evaluate( DropContext( actor ) ).Allowed );
		Assert.IsFalse( new HL2RPInventoryTransferRuntimePolicy( restraint ).Evaluate( TransferContext( actor ) ).Allowed );
	}

	[TestMethod]
	public async Task RecognitionProjectionHidesThenRevealsOnlyTheCommittedIntroduction()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var viewerActor = environment.Actor( 201 );
		var subjectActor = environment.Actor( 202 );
		var viewer = environment.Character( viewerActor, name: "Viewer" );
		var subject = environment.Character( subjectActor, name: "Private Citizen Name" );
		await environment.SeedAsync( unit =>
		{
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( viewer.Id ), viewer );
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( subject.Id ), subject );
			unit.Create(
				environment.Repositories.CharacterLifecycleGuards,
				DomainKeys.CharacterLifecycleGuard( viewer.Id ),
				new CharacterLifecycleGuardRecord { CharacterId = viewer.Id, ReferenceRevision = 0 } );
			unit.Create(
				environment.Repositories.CharacterLifecycleGuards,
				DomainKeys.CharacterLifecycleGuard( subject.Id ),
				new CharacterLifecycleGuardRecord { CharacterId = subject.Id, ReferenceRevision = 0 } );
		} );
		Assert.AreEqual( "Unknown citizen", HL2RPRuntimeProjection.PresentationName( viewer, subject, environment.Repositories ) );
		var stored = await new CharacterReferenceMutationService( environment.Repositories ).UpsertAsync(
			$"recognition-{viewer.Id}-{subject.Id}",
			new CharacterReferenceRecord
			{
				Category = "recognition",
				CharacterId = viewer.Id,
				RelatedCharacterId = subject.Id,
				State = HL2RPPersistence.Payload( HL2RPPersistence.Recognition, new RecognitionReferenceState
				{
					IntroducedName = subject.Name,
					IntroducedAtUtc = environment.Clock.UtcNow
				} )
			} );
		Assert.IsTrue( stored.Succeeded, stored.Error?.Message );
		Assert.AreEqual( subject.Name, HL2RPRuntimeProjection.PresentationName( viewer, subject, environment.Repositories ) );
		Assert.AreEqual( "Unknown citizen", HL2RPRuntimeProjection.PresentationName( null, subject, environment.Repositories ) );
	}

	[TestMethod]
	[TestCategory( "WiringLint" )]
	public void ProductRuntimeRoutesShowcaseServicesAndCanonicalRestraintContext()
	{
		var root = FindRoot();
		var host = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "Runtime", "HL2RPHostApplication.cs" ) );
		foreach ( var route in new[]
		{
			"new CombatIntentService", "ReconcileRaisedPistolsAsync",
			"ReconcilePersistedPilotsAsync", "DrainCleanupAsync", "_executableActions.Validate",
			"NearestCharacterTarget",
			"scannerFeature is HL2RPScannerDockComponent",
			"new HL2RPAuditLogHandler",
			"audit: _audit", "IHL2RPSchemaCommandRoutes<RpcActor>.PublishAdministrationAudit(",
			"var restraintAuthorization = new RestraintPermissionAuthorizer( _repositories, _chatAuthorities )",
			"_restraintState, restraintAuthorization",
			"IHL2RPSchemaCommandRoutes<RpcActor>.DoorOwnershipAsync(",
			"_context.Configuration.Snapshot()",
			"HL2RPPersistenceInvariants.Profile"
		} ) StringAssert.Contains( host, route );
		// The snapshot/view builders moved to the engine-neutral presentation composer;
		// their projection contracts are pinned against that source and exercised
		// directly by HL2RPPresentationComposerTests.
		var composer = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "Runtime", "HL2RPPresentationComposer.cs" ) );
		foreach ( var route in new[]
		{
			"death.respawn_available_at_ms", "HL2RPInventoryItemState.Project",
			"HL2RPItemDropAvailability.Project", "HL2RPItemActionAvailability.Project", "VendorSellAvailabilityFor",
			"_host.NearestCharacterTarget", "_host.IsOwnableDoor"
		} ) StringAssert.Contains( composer, route );
		// The command route bodies moved to the engine-neutral command execution core;
		// their service wiring is pinned against that source and the admission seams
		// stay pinned on the host above.
		var execution = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "Runtime", "HL2RPCommandExecution.cs" ) );
		foreach ( var route in new[]
		{
			"_services.CombatIntent!.FireAsync", "PurchaseFromMachineAsync",
			"_services.DoorOwnership!.ClaimAsync", "_services.DoorOwnership!.ReleaseAsync",
			"ExecutableItemActionRoute.HealthVialConsume", "ApplyScannerInputAsync",
			"_services.Search!.Open", "UpdateRecordAsync", "ConfigureAsync",
			"_services.SceneBehavior!.ToggleDoorAsync"
		} ) StringAssert.Contains( execution, route );
		var world = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "Runtime", "HL2RPSceneRuntime.cs" ) );
		StringAssert.Contains( world, "IsRestrained = _restraints.IsRestrained( characterId )" );
		StringAssert.Contains( world, "new CharacterRestraintInteractable" );
		var sandbox = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "Runtime", "HL2RPSandboxBoundaries.cs" ) );
		foreach ( var route in new[]
		{
			"HL2RPScannerVisualEffects", "Rotation.FromYaw", "PublishPhoto",
			"GetOrAddComponent<HL2RPScannerMotionController>", "command.MaximumAcceleration",
			"_maximumAcceleration * delta", "Time.Delta"
		} )
			StringAssert.Contains( sandbox, route );
		StringAssert.Contains( world, "ValidatePersistedScannerTopology" );
		StringAssert.Contains( world, "scanner.LinkedEntityId != expected.LinkedEntityId" );
		var operations = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "UI", "OperationsPanel.razor" ) );
		foreach ( var route in new[]
		{
			"SelectedRequestDevice", "SendRequestAsync", "HL2RPIds.Actions.Request", "OnItemWorkspace",
			"ItemActionArgumentBuilder.TrySplit( _splitAmount, item.Quantity"
		} )
			StringAssert.Contains( operations, route );
		StringAssert.Contains( operations, "ClaimDoorAsync" );
		StringAssert.Contains( operations, "HL2RPIds.Commands.DoorOwnership" );
		Assert.IsTrue( File.Exists( Path.Combine(
			root, "Code", "UI", "HL2RPClientPresenter.razor.scss" ) ) );
	}

	/// <summary>
	/// The authoritative shot origin must derive its eye height from the controller, never a literal.
	/// It was hardcoded to 56: eight units low standing, and twenty units ABOVE a ducked player's head
	/// (CurrentHeight falls to DuckedHeight 36, so the real eye is 28), which let a crouched player
	/// shoot over cover that visually concealed them.
	/// <para>
	/// This is a source pin and therefore weak — it proves the shape, not the behaviour. The absence
	/// half is the part that earns its place, because the failure mode is someone reintroducing a
	/// constant. `Resolve` needs a Scene and a PlayerController, so the behavioural proof is a live
	/// ducked shot; nothing offline can assert the origin.
	/// </para>
	/// </summary>
	[TestMethod]
	[TestCategory( "WiringLint" )]
	public void AuthoritativeShotOriginDerivesEyeHeightFromTheController()
	{
		var sandbox = HL2RPTestSource.WithoutComments(
			Path.Combine( FindRoot(), "Code", "Runtime", "HL2RPSandboxBoundaries.cs" ) );

		StringAssert.Contains( sandbox, "controller.CurrentHeight - controller.EyeDistanceFromTop",
			"The shot origin must use the engine's own eye arithmetic." );
		Assert.IsFalse( Regex.IsMatch( sandbox, @"Vector3\.Up \* \d" ),
			"The shot origin must not multiply Vector3.Up by a literal — that is the hardcoded eye " +
			"height regression, which is silently wrong only while ducking." );
	}

	[TestMethod]
	[TestCategory( "WiringLint" )]
	public void HostConsumesDurableConfigurationAndIncludesItInBothRecoveryProbeDigests()
	{
		var root = FindRoot();
		var host = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "Runtime", "HL2RPHostApplication.cs" ) );
		foreach ( var required in new[]
		{
			"RequiredConfigurationInt( HL2RPIds.Configs.CharacterInventoryWidth )",
			"RequiredConfigurationInt( HL2RPIds.Configs.CharacterInventoryHeight )",
			"RequiredConfigurationInt( HL2RPIds.Configs.InteractionIdleSeconds )",
			"RequiredConfigurationInt( HL2RPIds.Configs.ChatRateCapacity )",
			"RequiredConfigurationInt( HL2RPIds.Configs.ChatRateWindowSeconds )",
			"_globalChatRateLimit = new ChatRateLimit",
			"globalRateLimit: _globalChatRateLimit",
			"admission: _chatAdmission",
			"_context.Configuration.Snapshot()"
		} )
			StringAssert.Contains( host, required );
		Assert.HasCount(
			2,
			Regex.Matches( host, @"RecoveryDigest\(\s*_repositories, _context\.Configuration\.Snapshot\(\) \)",
				RegexOptions.CultureInvariant ).Cast<Match>() );
	}

	[TestMethod]
	public async Task RecoveryDigestCoversIndexesReservationsAndCompleteTypedItemTraits()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor( 303 );
		var item = environment.Item( HL2RPIds.Items.TokenStack, new Dictionary<string, TypedPayload>
		{
			["token_stack"] = HL2RPPersistence.Payload(
				HL2RPPersistence.TokenStack, new TokenStackItemState { Amount = 3 } )
		} );
		var inventory = environment.Inventory( actor.CharacterId, new[]
		{
			new InventoryPlacement( item.Id, 0, 0 )
		} );
		await environment.SeedAsync( unit =>
		{
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( actor.CharacterId ), environment.Character( actor ) );
			unit.Create( environment.Repositories.CharacterSlots, DomainKeys.CharacterSlot( actor.AccountId, 0 ), new CharacterSlotRecord
			{
				AccountId = actor.AccountId, Slot = 0, CharacterId = actor.CharacterId
			} );
			unit.Create( environment.Repositories.Inventories, DomainKeys.Inventory( inventory.Id ), inventory );
			unit.Create( environment.Repositories.OwnerInventories,
				DomainKeys.OwnerInventory( inventory.Owner, "main" ), new OwnerInventoryRecord
				{
					Role = "main", Owner = inventory.Owner, InventoryId = inventory.Id
				} );
			unit.Create( environment.Repositories.Items, DomainKeys.Item( item.Id ), item );
		} );

		var original = HL2RPRuntimeProjection.RecoveryDigest( environment.Repositories );
		Assert.AreEqual( 64, original.Length );
		Assert.AreEqual( original, HL2RPRuntimeProjection.RecoveryDigest( environment.Repositories ) );

		await environment.SeedAsync( unit =>
		{
			var observed = environment.Repositories.Items.Find( DomainKeys.Item( item.Id ) )!;
			var editor = unit.Edit( environment.Repositories.Items, observed )!;
			editor.Replace( editor.Value with
			{
				Traits = new Dictionary<string, TypedPayload>
				{
					["token_stack"] = HL2RPPersistence.Payload(
						HL2RPPersistence.TokenStack, new TokenStackItemState { Amount = 4 } )
				}
			} );
			unit.Save( editor );
		} );
		var traitChanged = HL2RPRuntimeProjection.RecoveryDigest( environment.Repositories );
		Assert.AreNotEqual( original, traitChanged );

		await environment.SeedAsync( unit => unit.Create(
			environment.Repositories.UniqueReservations,
			DomainKeys.UniqueReservation( "test.digest", "reserved" ),
			new UniqueReservationRecord
			{
				Namespace = "test.digest", Value = "reserved", CharacterId = actor.CharacterId
			} ) );
		Assert.AreNotEqual( traitChanged, HL2RPRuntimeProjection.RecoveryDigest( environment.Repositories ) );
	}

	[TestMethod]
	public async Task RecoveryDigestIncludesCanonicalTypedConfigurationSnapshot()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var bindings = SchemaPersistenceAdapter.Bind(
			environment.Schema,
			new PersistedTypeRegistry().RegisterHexagonDomainTypes(),
			HL2RPPersistence.Codecs );
		Assert.IsTrue( bindings.Succeeded, bindings.Error?.Message );
		var configuration = new TypedConfigurationStore(
			environment.Provider, bindings.Value.PersistenceConfigs );
		var initialized = await configuration.InitializeAsync();
		Assert.IsTrue( initialized.Succeeded, initialized.Error?.Message );
		var initial = configuration.Get<int>( HL2RPIds.Configs.ChatRateCapacity );
		Assert.IsTrue( initial.Succeeded, initial.Error?.Message );
		var snapshotBefore = configuration.Snapshot();
		var before = HL2RPRuntimeProjection.RecoveryDigest(
			environment.Repositories, snapshotBefore );

		var changed = await configuration.SetAsync(
			HL2RPIds.Configs.ChatRateCapacity,
			3,
			initial.Value.Revision );
		Assert.IsTrue( changed.Succeeded, changed.Error?.Message );
		var after = HL2RPRuntimeProjection.RecoveryDigest(
			environment.Repositories, configuration.Snapshot() );

		Assert.AreEqual( before, HL2RPRuntimeProjection.RecoveryDigest(
			environment.Repositories,
			snapshotBefore.Reverse() ) );
		Assert.AreNotEqual( before, after );
	}

	[TestMethod]
	public void EntitlementCommandPublicationClaimsTargetedInvalidationWithoutMaintenanceBroadcast()
	{
		var state = new HL2RPPresentationInvalidation();
		var targetAccount = new AccountId( 7001 );
		var targetConnection = ConnectionId.New();
		var queryingAdministrator = ConnectionId.New();
		var unrelated = ConnectionId.New();
		var recipientsByAccount = new Dictionary<AccountId, IReadOnlyList<ConnectionId>>
		{
			[targetAccount] = new[] { targetConnection, queryingAdministrator, targetConnection }
		};
		var invalidation = new HL2RPEntitlementPresentationInvalidation(
			state,
			account => recipientsByAccount.TryGetValue( account, out var recipients )
				? recipients
				: Array.Empty<ConnectionId>() );
		var changed = new HL2RPAccountEntitlementChanged(
			targetAccount,
			HL2RPWhitelist.None,
			HL2RPWhitelist.CivilProtection,
			new DocumentRevision( 1 ),
			42,
			DateTimeOffset.UnixEpoch );

		invalidation.Observe( changed );
		var commandRecipients = invalidation.Claim( targetAccount );
		Assert.HasCount( 2, commandRecipients );
		Assert.Contains( targetConnection, commandRecipients );
		Assert.Contains( queryingAdministrator, commandRecipients );
		Assert.DoesNotContain( unrelated, commandRecipients );
		Assert.IsTrue( state.IsRefreshDue( DateTimeOffset.UnixEpoch ) );

		var immediatePublication = state.BeginConnectionPublication( commandRecipients );
		Assert.IsFalse( immediatePublication.RequiresBroadcast );
		Assert.HasCount( 2, immediatePublication.ConnectionGenerations );
		state.AcknowledgePublished( immediatePublication );

		Assert.IsFalse( state.IsRefreshDue( DateTimeOffset.UnixEpoch ),
			"The in-band targeted publication must claim the callback invalidation." );
		var maintenance = state.BeginPublication( DateTimeOffset.UnixEpoch );
		Assert.IsFalse( maintenance.RequiresBroadcast );
		Assert.IsEmpty( maintenance.ConnectionGenerations );

		invalidation.Observe( changed with { CommitSequence = 43 } );
		var materialized = invalidation.MaterializePending();
		Assert.HasCount( 2, materialized );
		var outOfBand = state.BeginPublication( DateTimeOffset.UnixEpoch );
		Assert.IsFalse( outOfBand.RequiresBroadcast,
			"Out-of-band entitlement updates must remain account-targeted." );
		Assert.HasCount( 2, outOfBand.ConnectionGenerations );
	}

	[TestMethod]
	public void PresentationInvalidationCoalescesEventsDeadlinesAndMovementDerivedTargets()
	{
		var state = new HL2RPPresentationInvalidation();
		var now = DateTimeOffset.UnixEpoch;
		var connection = ConnectionId.New();
		var first = CharacterId.New();
		var second = CharacterId.New();

		state.RememberRestraintTarget( connection, first );
		state.ObserveRestraintTarget( connection, first );
		Assert.IsFalse( state.IsRefreshDue( now ) );
		state.ObserveRestraintTarget( connection, second );
		Assert.IsTrue( state.IsRefreshDue( now ) );
		var targeted = state.BeginPublication( now );
		Assert.IsFalse( targeted.RequiresBroadcast );
		Assert.IsTrue( targeted.ConnectionGenerations.ContainsKey( connection ) );
		state.AcknowledgePublished( targeted );
		Assert.IsFalse( state.IsRefreshDue( now ) );
		var inFlight = state.BeginPublication( now );
		state.Invalidate();
		state.AcknowledgePublished( inFlight );
		Assert.IsTrue( state.IsRefreshDue( now ), "An event raised during publication must not be lost." );
		state.AcknowledgePublished( state.BeginPublication( now ) );

		state.TrackDeathDeadline( first, now.AddSeconds( 10 ) );
		Assert.IsFalse( state.IsRefreshDue( now.AddSeconds( 9 ) ) );
		Assert.IsTrue( state.IsRefreshDue( now.AddSeconds( 10 ) ) );
		state.AcknowledgePublished( state.BeginPublication( now.AddSeconds( 10 ) ) );
		Assert.IsFalse( state.IsRefreshDue( now.AddSeconds( 11 ) ) );
		state.TrackRefreshDeadline( "request:one", now.AddSeconds( 20 ) );
		Assert.IsFalse( state.IsRefreshDue( now.AddSeconds( 19 ) ) );
		Assert.IsTrue( state.IsRefreshDue( now.AddSeconds( 20 ) ) );
		state.AcknowledgePublished( state.BeginPublication( now.AddSeconds( 20 ) ) );
		Assert.IsFalse( state.IsRefreshDue( now.AddSeconds( 21 ) ) );
	}

	[TestMethod]
	public void CivicSubjectSelectionsAreConnectionScopedAndClearStaleOrDeletedSubjects()
	{
		var selections = new HL2RPCivicSubjectSelections();
		var firstConnection = ConnectionId.New();
		var secondConnection = ConnectionId.New();
		var firstViewer = CharacterId.New();
		var secondViewer = CharacterId.New();
		var firstSubject = CharacterId.New();
		var secondSubject = CharacterId.New();
		var existing = new HashSet<CharacterId> { firstViewer, secondViewer, firstSubject, secondSubject };
		selections.Select( firstConnection, firstSubject );
		selections.Select( secondConnection, secondSubject );

		Assert.AreEqual( firstSubject, selections.Resolve( firstConnection, firstViewer, existing.Contains ) );
		Assert.AreEqual( secondSubject, selections.Resolve( secondConnection, secondViewer, existing.Contains ) );
		existing.Remove( firstSubject );
		Assert.AreEqual( firstViewer, selections.Resolve( firstConnection, firstViewer, existing.Contains ) );
		Assert.AreEqual( secondSubject, selections.Resolve( secondConnection, secondViewer, existing.Contains ) );
		selections.ClearSubject( secondSubject );
		Assert.AreEqual( secondViewer, selections.Resolve( secondConnection, secondViewer, existing.Contains ) );
	}

	[TestMethod]
	public void CivicOptionalTargetFailsClosedAndItemPresentationSequenceIsHostLocal()
	{
		var omitted = new HL2RPCommandArguments( new Dictionary<string, SnapshotValue>() ).OptionalGuid( "character" );
		var malformed = new HL2RPCommandArguments( new Dictionary<string, SnapshotValue>
		{
			["character"] = SnapshotValue.String( "not-a-character" )
		} ).OptionalGuid( "character" );
		var sequence = new HL2RPPresentationSequence();

		Assert.IsTrue( omitted.Succeeded );
		Assert.IsNull( omitted.Value );
		Assert.AreEqual( ErrorCode.InvalidArgument, malformed.Error!.Code );
		Assert.AreEqual( 1L, sequence.Next() );
		Assert.AreEqual( 2L, sequence.Next(),
			"Two zero-mutation receipts at one durable commit sequence still need distinct presentation identities." );
	}

	[TestMethod]
	public void ObjectiveStableIdsRoundTripAcrossMultipleCreatesAndOneEdit()
	{
		var firstAt = DateTimeOffset.UnixEpoch.AddMinutes( 1 );
		var editedAt = firstAt.AddMinutes( 2 );
		IReadOnlyList<CityObjectiveState> objectives = Array.Empty<CityObjectiveState>();
		objectives = HL2RPObjectiveState.Upsert( objectives, "objective.one", new CityObjectiveContent( "First", "Detail" ), false, firstAt );
		objectives = HL2RPObjectiveState.Upsert( objectives, "objective.two", new CityObjectiveContent( "Second", string.Empty ), false, firstAt );
		objectives = HL2RPObjectiveState.Upsert( objectives, "objective.one", new CityObjectiveContent( "First edited", "New detail" ), true, editedAt );

		Assert.HasCount( 2, objectives );
		var edited = objectives.Single( value => value.Id == "objective.one" );
		Assert.AreEqual( "First edited", edited.Title );
		Assert.AreEqual( "New detail", edited.Detail );
		Assert.IsTrue( edited.Completed );
		Assert.AreEqual( editedAt, edited.UpdatedAtUtc );
		Assert.HasCount( 2, objectives.Select( value => value.Id ).Distinct( StringComparer.Ordinal ).ToArray() );
	}

	[TestMethod]
	public void ObjectiveContractNormalizesMultilineDetailAndRejectsInvalidBoundaries()
	{
		var valid = CityObjectiveContract.CreateContent( "  Maintain order  ", "Line one\r\nLine two" );
		Assert.IsTrue( valid.Succeeded, valid.Error?.Message );
		Assert.AreEqual( "Maintain order", valid.Value.Title );
		Assert.AreEqual( "Line one\nLine two", valid.Value.Detail );
		Assert.IsTrue( CityObjectiveContract.CreateContent(
			new string( 't', CityObjectiveContract.MaximumTitleLength ),
			new string( 'd', CityObjectiveContract.MaximumDetailLength ) ).Succeeded );
		Assert.AreEqual( ErrorCode.InvalidArgument, CityObjectiveContract.CreateContent( string.Empty, string.Empty ).Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument, CityObjectiveContract.CreateContent(
			new string( 't', CityObjectiveContract.MaximumTitleLength + 1 ), string.Empty ).Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument, CityObjectiveContract.CreateContent( "Title\nSecond", string.Empty ).Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument, CityObjectiveContract.CreateContent(
			"Title", new string( 'd', CityObjectiveContract.MaximumDetailLength + 1 ) ).Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument, CityObjectiveContract.CreateContent( "Title", "Tab\there" ).Error!.Code );
	}

	[TestMethod]
	public void ObjectiveCommandParserCarriesTypedTitleAndMultilineDetailAcrossHostSeam()
	{
		var parsed = HL2RPObjectiveCommand.Parse( new HL2RPCommandArguments(
			new Dictionary<string, SnapshotValue>
			{
				["objective"] = SnapshotValue.String( "objective.one" ),
				["title"] = SnapshotValue.String( "  Inspect plaza  " ),
				["detail"] = SnapshotValue.String( "First line\r\nSecond line" ),
				["completed"] = SnapshotValue.Boolean( false )
			} ) );
		Assert.IsTrue( parsed.Succeeded, parsed.Error?.Message );
		Assert.AreEqual( "objective.one", parsed.Value.ObjectiveId );
		Assert.AreEqual( "Inspect plaza", parsed.Value.Content.Title );
		Assert.AreEqual( "First line\nSecond line", parsed.Value.Content.Detail );
		Assert.IsFalse( parsed.Value.Completed );

		var malformed = HL2RPObjectiveCommand.Parse( new HL2RPCommandArguments(
			new Dictionary<string, SnapshotValue>
			{
				["title"] = SnapshotValue.Integer( 1 ),
				["detail"] = SnapshotValue.String( string.Empty ),
				["completed"] = SnapshotValue.Boolean( false )
			} ) );
		Assert.AreEqual( ErrorCode.InvalidArgument, malformed.Error!.Code );
	}

	[TestMethod]
	public async Task MaintenanceSupervisorObservesFailureRetriesAndAcceptsLaterSignals()
	{
		var attempts = 0;
		var failures = new List<HL2RPMaintenanceFailure>();
		var succeeded = new TaskCompletionSource<bool>( TaskCreationOptions.RunContinuationsAsynchronously );
		await using var supervisor = new HL2RPMaintenanceSupervisor(
			_ =>
			{
				if ( Interlocked.Increment( ref attempts ) == 1 ) throw new InvalidOperationException( "fault" );
				succeeded.TrySetResult( true );
				return ValueTask.CompletedTask;
			},
			failures.Add,
			(_, _) => Task.CompletedTask,
			() => DateTimeOffset.UnixEpoch );
		supervisor.RequestTick();
		await succeeded.Task.WaitAsync( TimeSpan.FromSeconds( 2 ) );
		Assert.AreEqual( 2, attempts );
		Assert.HasCount( 1, failures );
		Assert.AreEqual( TimeSpan.FromMilliseconds( 250 ), failures[0].RetryDelay );
		Assert.AreEqual( 0, supervisor.Status.ConsecutiveFailures );
		supervisor.RequestTick();
		await WaitUntilAsync( () => attempts >= 3 );
		Assert.AreEqual( 3, attempts );
	}

	// Bounded by wall clock rather than scheduler turns: counting Task.Yield iterations
	// flaked on loaded CI runners where the supervisor's runner task lagged behind the spin.
	private static async Task WaitUntilAsync( Func<bool> condition )
	{
		for ( var attempt = 0; attempt < 500 && !condition(); attempt++ )
			await Task.Delay( 10 );
	}

	[TestMethod]
	public async Task MaintenanceSupervisorCoalescesSignalsWithoutOverlappingTicks()
	{
		var entered = new TaskCompletionSource<bool>( TaskCreationOptions.RunContinuationsAsynchronously );
		var release = new TaskCompletionSource<bool>( TaskCreationOptions.RunContinuationsAsynchronously );
		var second = new TaskCompletionSource<bool>( TaskCreationOptions.RunContinuationsAsynchronously );
		var active = 0;
		var maximumActive = 0;
		var attempts = 0;
		await using var supervisor = new HL2RPMaintenanceSupervisor(
			async _ =>
			{
				var current = Interlocked.Increment( ref active );
				maximumActive = Math.Max( maximumActive, current );
				var attempt = Interlocked.Increment( ref attempts );
				if ( attempt == 1 )
				{
					entered.TrySetResult( true );
					await release.Task;
				}
				else second.TrySetResult( true );
				Interlocked.Decrement( ref active );
			},
			_ => Assert.Fail( "No failure expected." ) );
		supervisor.RequestTick();
		await entered.Task.WaitAsync( TimeSpan.FromSeconds( 2 ) );
		for ( var index = 0; index < 20; index++ ) supervisor.RequestTick();
		release.TrySetResult( true );
		await second.Task.WaitAsync( TimeSpan.FromSeconds( 2 ) );
		Assert.AreEqual( 2, attempts );
		Assert.AreEqual( 1, maximumActive );
	}

	[TestMethod]
	public async Task MaintenanceSupervisorPacesFrameDrivenTicksAtTheMinimumInterval()
	{
		var ticks = 0;
		var paces = new List<TimeSpan>();
		var second = new TaskCompletionSource<bool>( TaskCreationOptions.RunContinuationsAsynchronously );
		await using var supervisor = new HL2RPMaintenanceSupervisor(
			_ =>
			{
				if ( Interlocked.Increment( ref ticks ) == 2 ) second.TrySetResult( true );
				return ValueTask.CompletedTask;
			},
			_ => Assert.Fail( "No failure expected." ),
			( duration, _ ) =>
			{
				paces.Add( duration );
				return Task.CompletedTask;
			},
			() => DateTimeOffset.UnixEpoch,
			minimumInterval: TimeSpan.FromMilliseconds( 100 ) );

		supervisor.RequestTick();
		await WaitUntilAsync( () => ticks >= 1 );
		Assert.AreEqual( 1, ticks );
		Assert.IsEmpty( paces, "The first tick after idle runs unpaced." );

		supervisor.RequestTick();
		await second.Task.WaitAsync( TimeSpan.FromSeconds( 2 ) );
		Assert.AreEqual( 2, ticks );
		Assert.HasCount( 1, paces );
		Assert.AreEqual( TimeSpan.FromMilliseconds( 100 ), paces[0],
			"With a frozen clock the full minimum interval separates consecutive ticks." );
	}

	[TestMethod]
	public async Task MaintenanceSupervisorRecoversAfterDelayInfrastructureFailure()
	{
		var attempts = 0;
		var failures = 0;
		await using var supervisor = new HL2RPMaintenanceSupervisor(
			_ => Interlocked.Increment( ref attempts ) == 1
				? ValueTask.FromException( new InvalidOperationException( "tick" ) )
				: ValueTask.CompletedTask,
			_ => Interlocked.Increment( ref failures ),
			(_, _) => Task.FromException( new InvalidOperationException( "delay" ) ) );
		supervisor.RequestTick();
		await WaitUntilAsync( () => !supervisor.Status.Running );
		Assert.IsFalse( supervisor.Status.Running );
		Assert.IsGreaterThanOrEqualTo( 2, failures );
		supervisor.RequestTick();
		await WaitUntilAsync( () => !supervisor.Status.Running && attempts >= 2 );
		Assert.AreEqual( 2, attempts );
		Assert.IsFalse( supervisor.Status.Running );
	}

	[TestMethod]
	public async Task MaintenanceSupervisorClockFailureCannotLeaveRunnerWedged()
	{
		var failClock = true;
		var attempts = 0;
		await using var supervisor = new HL2RPMaintenanceSupervisor(
			_ => { Interlocked.Increment( ref attempts ); return ValueTask.CompletedTask; },
			_ => { },
			(_, _) => Task.CompletedTask,
			() => failClock ? throw new InvalidOperationException( "clock" ) : DateTimeOffset.UnixEpoch );
		supervisor.RequestTick();
		await WaitUntilAsync( () => !supervisor.Status.Running );
		Assert.IsFalse( supervisor.Status.Running );
		failClock = false;
		supervisor.RequestTick();
		await WaitUntilAsync( () => !supervisor.Status.Running && attempts >= 2 );
		Assert.AreEqual( 2, attempts );
		Assert.IsNotNull( supervisor.Status.LastSuccessAtUtc );
	}

	[TestMethod]
	public async Task MaintenanceSupervisorDisposalCancelsAndAwaitsRetryDelay()
	{
		var enteredDelay = new TaskCompletionSource<bool>( TaskCreationOptions.RunContinuationsAsynchronously );
		var delayCancelled = false;
		var supervisor = new HL2RPMaintenanceSupervisor(
			_ => ValueTask.FromException( new InvalidOperationException( "retry" ) ),
			_ => { },
			async (_, token) =>
			{
				enteredDelay.TrySetResult( true );
				try { await Task.Delay( Timeout.InfiniteTimeSpan, token ); }
				catch ( OperationCanceledException ) when ( token.IsCancellationRequested )
				{
					delayCancelled = true;
					throw;
				}
			} );
		supervisor.RequestTick();
		await enteredDelay.Task.WaitAsync( TimeSpan.FromSeconds( 2 ) );
		await supervisor.DisposeAsync().AsTask().WaitAsync( TimeSpan.FromSeconds( 2 ) );
		Assert.IsTrue( delayCancelled );
		Assert.IsFalse( supervisor.Status.Running );
	}

	[TestMethod]
	public void PresentationPlannerDoesNotAmplifyNoOpsAndTargetsInventoryViewers()
	{
		var actor = ConnectionId.New();
		var source = InventoryId.New();
		var target = InventoryId.New();
		Assert.IsTrue( HL2RPPresentationPlanner.ForSuccessfulCommand(
			actor, new ContinueInteractionCommand( InteractionSessionId.New(),
				new InteractionTargetInput( InteractionTargetInputKind.Inventory, source.Value ) ) ).IsEmpty );
		Assert.IsTrue( HL2RPPresentationPlanner.ForSuccessfulCommand(
			actor, new SendChatCommand( "ic", "hello" ) ).IsEmpty );
		var failed = HL2RPPresentationPlanner.Outcome(
			actor,
			new MoveInventoryItemCommand( source, target, ItemId.New(), 0, 0 ),
			OperationResult.Failure( ErrorCode.Conflict, "failed" ) );
		Assert.IsTrue( failed.Changes.IsEmpty );
		var successfulNoOp = HL2RPPresentationPlanner.Outcome(
			actor,
			new MoveInventoryItemCommand( source, target, ItemId.New(), 0, 0 ),
			OperationResult.Success(),
			durableMutation: false );
		Assert.IsTrue( successfulNoOp.Changes.IsEmpty );
		var changed = HL2RPPresentationPlanner.ForSuccessfulCommand(
			actor, new MoveInventoryItemCommand( source, target, ItemId.New(), 0, 0 ) );
		Assert.IsTrue( changed.RebuildLiveInventory );
		CollectionAssert.AreEquivalent( new[] { source, target }, changed.Inventories.ToArray() );
		Assert.AreEqual( actor, changed.Connections.Single() );
		var viewer = ConnectionId.New();
		var recipients = HL2RPPresentationPlanner.ResolveRecipients(
			changed,
			new Dictionary<CharacterId, ConnectionId>(),
			new[] { actor, viewer },
			_ => new[] { actor, viewer, viewer } );
		CollectionAssert.AreEquivalent( new[] { actor, viewer }, recipients.ToArray() );

		var all = Enumerable.Range( 0, 64 ).Select( _ => ConnectionId.New() ).ToArray();
		var objectives = HL2RPPresentationPlanner.ForSuccessfulCommand(
			actor,
			new RunSchemaCommandCommand( HL2RPIds.Commands.CityObjectives,
				new Dictionary<string, SnapshotValue>() ) );
		Assert.IsTrue( objectives.Broadcast );
		Assert.HasCount( 64, HL2RPPresentationPlanner.ResolveRecipients(
			objectives, new Dictionary<CharacterId, ConnectionId>(), all, _ => Array.Empty<ConnectionId>() ) );

		var commerce = HL2RPPresentationPlanner.ForObservedEffect(
			actor,
			new RunSchemaCommandCommand( HL2RPIds.Commands.CommerceBuy,
				new Dictionary<string, SnapshotValue>() ),
			durableMutation: true,
			rosterChanged: false );
		Assert.IsFalse( commerce.Broadcast );
		Assert.AreEqual( actor, commerce.Connections.Single() );
		var account = new AccountId( 991 );
		var accountViewer = ConnectionId.New();
		var accountChanges = new HL2RPPresentationChangeSet { Accounts = new[] { account } };
		Assert.AreEqual( accountViewer, HL2RPPresentationPlanner.ResolveRecipients(
			accountChanges,
			new Dictionary<CharacterId, ConnectionId>(),
			new Dictionary<AccountId, IReadOnlyList<ConnectionId>> { [account] = new[] { accountViewer } },
			Array.Empty<ConnectionId>(),
			_ => Array.Empty<ConnectionId>() ).Single() );
	}

	[TestMethod]
	public void CharacterBindingChangeInvalidatesActorProjectionSlicesBeforeBroadcast()
	{
		var actor = ConnectionId.New();
		var characterA = CharacterId.New();
		var characterB = CharacterId.New();
		var characterAddress = new DocumentAddress( DomainCollections.Characters, "character-a" );
		var index = new HL2RPProjectionIndex();
		var inventoryId = InventoryId.New();
		index.Rebuild( new[]
		{
			new DocumentSnapshot<InventoryRecord>( "main", new DocumentRevision( 1 ), new InventoryRecord
			{
				Id = inventoryId,
				Owner = InventoryOwner.Character( characterA ),
				Width = 1,
				Height = 1
			} )
		}, Array.Empty<DocumentSnapshot<ItemRecord>>() );
		var visibilityChecks = 0;
		bool CanView( InventoryId _ ) { visibilityChecks++; return true; }
		Assert.HasCount( 1, index.VisibleInventories( actor, characterA, CanView ) );
		var initialVisibilityChecks = visibilityChecks;
		foreach ( var kind in Enum.GetValues<HL2RPProjectionSliceKind>() )
			Assert.AreEqual( "character-a", Slice( index, actor, kind, "character-a", characterAddress ) );

		index.ObserveCharacterBindingChanged( actor, characterA, characterA );
		foreach ( var kind in Enum.GetValues<HL2RPProjectionSliceKind>() )
			Assert.AreEqual( "character-a", Slice( index, actor, kind, "same-binding", characterAddress ) );
		Assert.HasCount( 1, index.VisibleInventories( actor, characterA, CanView ) );
		Assert.AreEqual( initialVisibilityChecks + 1, visibilityChecks,
			"An unchanged binding must retain the indexed visibility discovery." );

		index.ObserveCharacterBindingChanged( actor, characterA, characterB );
		foreach ( var kind in Enum.GetValues<HL2RPProjectionSliceKind>() )
		{
			Assert.AreEqual( "character-b", Slice( index, actor, kind, "character-b", characterAddress ) );
			Assert.AreEqual( 2, index.SliceBuildCount( kind ) );
		}
		Assert.HasCount( 1, index.VisibleInventories( actor, characterA, CanView ) );
		Assert.AreEqual( initialVisibilityChecks + 3, visibilityChecks,
			"A changed binding must discard the old character visibility discovery." );

		var outcome = HL2RPPresentationPlanner.Outcome(
			actor,
			new LoadCharacterCommand( characterB ),
			OperationResult.Success(),
			durableMutation: false,
			rosterChanged: true );

		Assert.IsTrue( outcome.Changes.Broadcast );
		Assert.AreEqual( actor, outcome.Changes.Connections.Single() );
	}

	[TestMethod]
	public void EntitlementUiAccountShapeTargetsTheMutatedAccountBinding()
	{
		var actor = ConnectionId.New();
		var targetConnection = ConnectionId.New();
		var targetAccount = new AccountId( 9_007_199_254_740_991UL );
		var command = new RunSchemaCommandCommand(
			HL2RPIds.Commands.EntitlementGrant,
			new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
			{
				["account"] = SnapshotValue.String( targetAccount.Value.ToString(
					System.Globalization.CultureInfo.InvariantCulture ) ),
				["flag"] = SnapshotValue.Choice( "civil_protection" ),
				["revision"] = SnapshotValue.Integer( 0 )
			} );
		var changes = HL2RPPresentationPlanner.ForSuccessfulCommand( actor, command );
		Assert.AreEqual( targetAccount, changes.Accounts.Single() );
		CollectionAssert.AreEquivalent(
			new[] { actor, targetConnection },
			HL2RPPresentationPlanner.ResolveRecipients(
				changes,
				new Dictionary<CharacterId, ConnectionId>(),
				new Dictionary<AccountId, IReadOnlyList<ConnectionId>>
				{
					[targetAccount] = new[] { targetConnection }
				},
				new[] { actor, targetConnection },
				_ => Array.Empty<ConnectionId>() ).ToArray() );
		Assert.IsFalse( HL2RPPresentationPlanner.TryAccountId(
			new Dictionary<string, SnapshotValue> { ["account"] = SnapshotValue.String( "+12" ) },
			"account", out _ ) );
	}

	[TestMethod]
	public void SceneSessionIndexTargetsDoorAndVendorViewersWithoutBroadcasting()
	{
		var index = new HL2RPProjectionIndex();
		var door = SceneEntityId.New();
		var vendor = SceneEntityId.New();
		var doorViewer = ConnectionId.New();
		var vendorViewer = ConnectionId.New();
		var doorSession = Session( doorViewer, door );
		var vendorSession = Session( vendorViewer, vendor );
		index.ObserveSceneSession( doorSession );
		index.ObserveSceneSession( vendorSession );

		var doorChanges = new HL2RPPresentationChangeSet { SceneEntities = new[] { door } };
		Assert.AreEqual( doorViewer, HL2RPPresentationPlanner.ResolveRecipients(
			doorChanges,
			new Dictionary<CharacterId, ConnectionId>(),
			new Dictionary<AccountId, IReadOnlyList<ConnectionId>>(),
			new[] { doorViewer, vendorViewer },
			_ => Array.Empty<ConnectionId>(),
			index.SceneViewers ).Single() );
		index.RemoveSceneSession( doorSession.Id );
		Assert.IsEmpty( index.SceneViewers( door ) );
		Assert.AreEqual( vendorViewer, index.SceneViewers( vendor ).Single() );
	}

	[TestMethod]
	public async Task DependencyKeyedSlicesReuseUnaffectedWorkAcrossSixtyFourClients()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var index = new HL2RPProjectionIndex();
		var cityAddress = new DocumentAddress( DomainCollections.SceneEntities, "city" );
		var inventoryAddress = new DocumentAddress( DomainCollections.Inventories, "main" );
		var characterAddress = new DocumentAddress( DomainCollections.Characters, "character" );
		var clients = Enumerable.Range( 0, 64 ).Select( _ => ConnectionId.New() ).ToArray();
		var recognitionAddresses = clients.ToDictionary(
			client => client,
			client => new DocumentAddress(
				DomainCollections.CharacterReferences, $"recognition-{client}-subject" ) );
		foreach ( var client in clients )
		{
			Assert.AreEqual( "private", Slice( index, client, HL2RPProjectionSliceKind.PrivatePlayer,
				"private", characterAddress ) );
			Assert.AreEqual( "inventory", Slice( index, client, HL2RPProjectionSliceKind.Inventories,
				"inventory", inventoryAddress ) );
			Assert.AreEqual( "schema", Slice( index, client, HL2RPProjectionSliceKind.SchemaViews,
				"schema", cityAddress ) );
			Assert.AreEqual( "roster", RosterSlice( index, client, recognitionAddresses[client] ) );
		}
		Assert.AreEqual( 64, index.SliceBuildCount( HL2RPProjectionSliceKind.PrivatePlayer ) );
		Assert.AreEqual( 64, index.SliceBuildCount( HL2RPProjectionSliceKind.Inventories ) );
		Assert.AreEqual( 64, index.SliceBuildCount( HL2RPProjectionSliceKind.SchemaViews ) );
		Assert.AreEqual( 64, index.SliceBuildCount( HL2RPProjectionSliceKind.Roster ) );

		index.Apply( new CommitReceipt( 1, new[]
		{
			new CommittedDocumentVersion( cityAddress, DocumentRevision.None, true )
		} ), environment.Repositories );
		foreach ( var client in clients )
		{
			_ = Slice( index, client, HL2RPProjectionSliceKind.PrivatePlayer, "private", characterAddress );
			_ = Slice( index, client, HL2RPProjectionSliceKind.Inventories, "inventory", inventoryAddress );
			_ = Slice( index, client, HL2RPProjectionSliceKind.SchemaViews, "schema", cityAddress );
			_ = RosterSlice( index, client, recognitionAddresses[client] );
		}
		Assert.AreEqual( 64, index.SliceBuildCount( HL2RPProjectionSliceKind.PrivatePlayer ) );
		Assert.AreEqual( 64, index.SliceBuildCount( HL2RPProjectionSliceKind.Inventories ) );
		Assert.AreEqual( 128, index.SliceBuildCount( HL2RPProjectionSliceKind.SchemaViews ) );
		Assert.AreEqual( 64, index.SliceBuildCount( HL2RPProjectionSliceKind.Roster ),
			"A city-objective receipt must preserve unrelated roster slices." );

		index.Apply( new CommitReceipt( 2, new[]
		{
			new CommittedDocumentVersion( recognitionAddresses[clients[0]], DocumentRevision.None, true )
		} ), environment.Repositories );
		foreach ( var client in clients ) _ = RosterSlice( index, client, recognitionAddresses[client] );
		Assert.AreEqual( 65, index.SliceBuildCount( HL2RPProjectionSliceKind.Roster ),
			"A recognition receipt must invalidate only the dependent viewer roster." );
		index.Apply( new CommitReceipt( 3, Array.Empty<CommittedDocumentVersion>() ), environment.Repositories );
		foreach ( var client in clients )
			_ = Slice( index, client, HL2RPProjectionSliceKind.SchemaViews, "schema", cityAddress );
		Assert.AreEqual( 128, index.SliceBuildCount( HL2RPProjectionSliceKind.SchemaViews ),
			"A successful zero-mutation receipt must not invalidate any projection slice." );

		index.InvalidateRuntimeDependency( "combat-lifecycle", "global" );
		foreach ( var client in clients ) _ = RosterSlice( index, client, recognitionAddresses[client] );
		Assert.AreEqual( 129, index.SliceBuildCount( HL2RPProjectionSliceKind.Roster ) );
		index.InvalidateRuntimeDependency( "roster-membership", "global" );
		foreach ( var client in clients ) _ = RosterSlice( index, client, recognitionAddresses[client] );
		Assert.AreEqual( 193, index.SliceBuildCount( HL2RPProjectionSliceKind.Roster ) );
		index.InvalidateRuntime( clients[0] );
		_ = RosterSlice( index, clients[0], recognitionAddresses[clients[0]] );
		Assert.AreEqual( 194, index.SliceBuildCount( HL2RPProjectionSliceKind.Roster ),
			"Disconnect-style invalidation must remove the departing connection's cached slice." );
	}

	private static string Slice(
		HL2RPProjectionIndex index,
		ConnectionId connection,
		HL2RPProjectionSliceKind kind,
		string value,
		DocumentAddress dependency ) => index.GetOrCreateSlice(
		connection, kind,
		() => new HL2RPProjectionSlice<string>(
			value, new[] { dependency }, Array.Empty<string>() ) );

	private static string RosterSlice(
		HL2RPProjectionIndex index,
		ConnectionId connection,
		DocumentAddress recognitionDependency ) => index.GetOrCreateSlice(
		connection,
		HL2RPProjectionSliceKind.Roster,
		() => new HL2RPProjectionSlice<string>(
			"roster",
			new[] { recognitionDependency },
			new[]
			{
				HL2RPProjectionIndex.RuntimeDependency( "roster-membership", "global" ),
				HL2RPProjectionIndex.RuntimeDependency( "combat-lifecycle", "global" )
			} ) );

	private static InteractionSession Session( ConnectionId connection, SceneEntityId scene ) => new()
	{
		Id = InteractionSessionId.New(),
		Kind = InteractionSessionKind.Door,
		ConnectionId = connection,
		CharacterId = CharacterId.New(),
		Target = InteractionTarget.SceneEntity( scene ),
		OpenedAt = DateTimeOffset.UnixEpoch,
		LastActivityAt = DateTimeOffset.UnixEpoch
	};

	[TestMethod]
	public void PresentationInvalidationDoesNotLoseTargetedWorkAcrossSixtyFourClients()
	{
		var state = new HL2RPPresentationInvalidation();
		var clients = Enumerable.Range( 0, 64 ).Select( _ => ConnectionId.New() ).ToArray();
		foreach ( var client in clients ) state.Invalidate( client );
		var direct = state.BeginConnectionPublication( clients.Take( 32 ) );
		state.Invalidate( clients[0] );
		state.AcknowledgePublished( direct );
		var afterDirect = state.BeginPublication( DateTimeOffset.UnixEpoch );
		Assert.HasCount( 33, afterDirect.ConnectionGenerations );
		Assert.IsTrue( afterDirect.ConnectionGenerations.ContainsKey( clients[0] ),
			"A newer invalidation for a directly published recipient must remain pending." );
		var inFlight = state.BeginPublication( DateTimeOffset.UnixEpoch );
		Assert.IsFalse( inFlight.RequiresBroadcast );
		Assert.HasCount( 33, inFlight.ConnectionGenerations );
		state.Invalidate( clients[0] );
		state.AcknowledgePublished( inFlight );
		var remaining = state.BeginPublication( DateTimeOffset.UnixEpoch );
		Assert.HasCount( 1, remaining.ConnectionGenerations );
		Assert.IsTrue( remaining.ConnectionGenerations.ContainsKey( clients[0] ) );
	}

	[TestMethod]
	public void ProjectionIndexResolvesNestedOwnershipAndCountsWithoutRepositoryScans()
	{
		var character = CharacterId.New();
		var parentInventory = InventoryId.New();
		var bagItem = ItemId.New();
		var nestedInventory = InventoryId.New();
		var bag = new ItemRecord { Id = bagItem, Definition = new DefinitionId( "item.bag" ) };
		var index = new HL2RPProjectionIndex();
		index.Rebuild( new[]
		{
			new DocumentSnapshot<InventoryRecord>( "parent", new DocumentRevision( 1 ), new InventoryRecord
			{
				Id = parentInventory, Owner = InventoryOwner.Character( character ), Width = 4, Height = 4,
				Placements = new[] { new InventoryPlacement( bagItem, 0, 0 ) }
			} ),
			new DocumentSnapshot<InventoryRecord>( "nested", new DocumentRevision( 1 ), new InventoryRecord
			{
				Id = nestedInventory, Owner = InventoryOwner.ParentItem( bagItem ), Width = 2, Height = 2
			} )
		}, new[] { new DocumentSnapshot<ItemRecord>( "bag", new DocumentRevision( 1 ), bag ) } );
		Assert.AreEqual( character, index.OwningCharacter( nestedInventory ) );
		Assert.AreEqual( 1, index.NestedInventoryCount( bagItem ) );
		Assert.IsTrue( index.TryGetItem( bagItem, out var indexed ) );
		Assert.AreEqual( bag, indexed );
		var originalOwner = index.OwningCharacter( nestedInventory );
		var reversedInventories = index.Inventories.Reverse().ToArray();
		index.Rebuild( reversedInventories,
			new[] { new DocumentSnapshot<ItemRecord>( "bag", new DocumentRevision( 1 ), bag ) } );
		Assert.AreEqual( originalOwner, index.OwningCharacter( nestedInventory ) );
		Assert.AreEqual( 1, index.NestedInventoryCount( bagItem ) );
	}

	[TestMethod]
	public async Task ProjectionIndexAppliesTypedCommitReceiptAndMatchesFreshRebuild()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor( 881 );
		var character = environment.Character( actor );
		var inventory = environment.Inventory( actor.CharacterId );
		var item = environment.Item( HL2RPIds.Items.Water );
		await environment.SeedAsync( unit =>
		{
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unit.Create( environment.Repositories.CharacterLifecycleGuards,
				DomainKeys.CharacterLifecycleGuard( character.Id ),
				new CharacterLifecycleGuardRecord { CharacterId = character.Id, ReferenceRevision = 0 } );
			unit.Create( environment.Repositories.Inventories, DomainKeys.Inventory( inventory.Id ), inventory );
			unit.Create( environment.Repositories.Items, DomainKeys.Item( item.Id ), item );
		} );
		var incremental = new HL2RPProjectionIndex();
		incremental.Rebuild(
			environment.Repositories.Inventories.All(), environment.Repositories.Items.All(),
			environment.Repositories.Characters.All(), environment.Repositories.CharacterReferences.All(),
			environment.Repositories.SceneEntities.All() );
		await using var mutation = environment.Provider.BeginUnitOfWork();
		var inventoryDocument = environment.Repositories.Inventories.Find( DomainKeys.Inventory( inventory.Id ) )!;
		var editor = mutation.Edit( environment.Repositories.Inventories, inventoryDocument )!;
		editor.Replace( inventory with { Placements = new[] { new InventoryPlacement( item.Id, 0, 0 ) } } );
		mutation.Save( editor );
		var receipt = await mutation.CommitAsync();
		Assert.IsTrue( receipt.Succeeded, receipt.Error?.Message );
		incremental.Apply( receipt.Value!, environment.Repositories );

		var rebuilt = new HL2RPProjectionIndex();
		rebuilt.Rebuild(
			environment.Repositories.Inventories.All(), environment.Repositories.Items.All(),
			environment.Repositories.Characters.All(), environment.Repositories.CharacterReferences.All(),
			environment.Repositories.SceneEntities.All() );
		CollectionAssert.AreEquivalent( rebuilt.ItemsIn( inventory.Id ).ToArray(), incremental.ItemsIn( inventory.Id ).ToArray() );
		CollectionAssert.AreEquivalent( rebuilt.InventoriesOwnedBy( character.Id ).ToArray(),
			incremental.InventoriesOwnedBy( character.Id ).ToArray() );
		Assert.AreEqual( rebuilt.DefinitionCount( item.Definition ), incremental.DefinitionCount( item.Definition ) );

		var viewer = ConnectionId.New();
		incremental.RefreshViewers( new[] { inventory.Id }, _ => new[] { viewer, viewer } );
		Assert.AreEqual( viewer, incremental.Viewers( inventory.Id ).Single() );
		Assert.HasCount( 1, incremental.VisibleInventories(
			viewer, character.Id, candidate => candidate == inventory.Id ) );
		incremental.InvalidateVisibility( viewer );
		Assert.IsEmpty( incremental.VisibleInventories( viewer, character.Id, _ => false ) );
	}

	[TestMethod]
	public void VendorPresentationAndPermitParsingFailClosedLikeCommerce()
	{
		Assert.IsTrue( HL2RPPresentationContracts.VendorOffer( true, 1, 25, 25 ).CanBuy );
		StringAssert.Contains( HL2RPPresentationContracts.VendorOffer( false, 1, 25, 25 ).DisabledReason, "permit" );
		StringAssert.Contains( HL2RPPresentationContracts.VendorOffer( true, 0, 25, 25 ).DisabledReason, "stock" );
		StringAssert.Contains( HL2RPPresentationContracts.VendorOffer( true, 1, 25, 24 ).DisabledReason, "funds" );
		Assert.AreEqual( BusinessPermitKind.Food,
			HL2RPPresentationContracts.ParsePermitKind( "permit_food" ).Value );
		Assert.AreEqual( BusinessPermitKind.General,
			HL2RPPresentationContracts.ParsePermitKind( "permit_general" ).Value );
		Assert.AreEqual( ErrorCode.InvalidArgument,
			HL2RPPresentationContracts.ParsePermitKind( "permit_counterfeit" ).Error!.Code );
	}

	[TestMethod]
	[TestCategory( "WiringLint" )]
	public void ProductSessionAdaptersAndUiRoutesAreWired()
	{
		var root = FindRoot();
		var host = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "Runtime", "HL2RPHostApplication.cs" ) );
		foreach ( var required in new[]
		{
			"_sessions.SessionRevoked += OnInteractionSessionRevoked",
			"_sessions.SessionRevoked -= OnInteractionSessionRevoked",
			"hl2rp.runtime.item_presentation"
		} ) StringAssert.Contains( host, required );
		// The optional civic-subject argument is parsed by the neutral command
		// execution core, which owns the CivicData route body.
		StringAssert.Contains(
			HL2RPTestSource.WithoutComments( Path.Combine(
				root, "Code", "Runtime", "HL2RPCommandExecution.cs" ) ),
			"arguments.OptionalGuid( \"character\" )" );

		var presenter = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "UI", "HL2RPClientPresenter.razor" ) );
		StringAssert.Contains( presenter, "private void Introduce( CharacterId characterId )" );
		StringAssert.Contains( presenter, "private async Task InspectCivicAsync( CharacterId characterId )" );
		StringAssert.Contains( presenter, "_characterPresentation.Workspace = ShowcaseWorkspace.CivicData" );
		StringAssert.Contains( presenter, "request.PermitKindId" );
		var scoreboard = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "UI", "ScoreboardPanel.razor" ) );
		StringAssert.Contains( scoreboard, "OnIntroduce?.Invoke( characterId )" );
		var combine = File.ReadAllText( Path.Combine( root, "Code", "UI", "CombineSuitePanel.razor" ) );
		Assert.IsFalse( combine.Contains( "NoteTitle", StringComparison.Ordinal ) );
		var rootPanel = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "UI", "HL2RPShowcaseRoot.razor" ) );
		StringAssert.Contains( rootPanel, "item-presentation-backdrop" );
		StringAssert.Contains( rootPanel, "private void CloseCombineWorkspace()" );
		StringAssert.Contains( rootPanel, "ScannerIntent.Exit" );
		var operations = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "UI", "OperationsPanel.razor" ) );
		StringAssert.Contains( operations, "×@candidate.Quantity" );
	}

	[TestMethod]
	[TestCategory( "WiringLint" )]
	public void ProductSourcesStayInsideSboxAsyncAndRazorCompatibilitySurface()
	{
		var root = FindRoot();
		foreach ( var path in Directory.GetFiles( Path.Combine( root, "Code" ), "*.cs", SearchOption.AllDirectories ) )
		{
			var source = File.ReadAllText( path );
			Assert.IsFalse( source.Contains( "await using", StringComparison.Ordinal ), path );
			Assert.IsFalse( source.Contains( "Task.WhenAll", StringComparison.Ordinal ), path );
			Assert.IsFalse( source.Contains( "TaskScheduler", StringComparison.Ordinal ), path );
			Assert.IsFalse( source.Contains( ".ContinueWith", StringComparison.Ordinal ), path );
		}
		foreach ( var path in Directory.GetFiles( Path.Combine( root, "Code", "UI" ), "*.razor", SearchOption.AllDirectories ) )
		{
			var source = File.ReadAllText( path );
			Assert.IsFalse( source.Contains( "ReadOnly=", StringComparison.Ordinal ), path );
			Assert.IsFalse( Regex.IsMatch( source, "MaxLength=\"[0-9]" ), path );
			Assert.IsFalse( Regex.IsMatch( source, "On[A-Z][A-Za-z0-9]*=\"(?!@)" ), path );
			Assert.IsFalse(
				Regex.IsMatch( source, "(?<![A-Za-z0-9_])@?on[a-z][a-z0-9_-]*\\s*=\\s*\"[A-Za-z_][A-Za-z0-9_]*\"" ),
				$"{path}: s&box event handlers require an explicit Razor expression, for example @onclick=\"@Handler\"." );
		}
	}

	private static HL2RPFeaturePolicyContext Context( InventoryActor actor, HL2RPFeatureOperation operation ) => new()
	{
		Actor = actor,
		Operation = operation
	};

	private static WorldPickupContext PickupContext( InventoryActor actor, float x ) => new(
		actor,
		new InventoryRecord { Id = InventoryId.New(), Owner = InventoryOwner.Character( actor.CharacterId ), Width = 4, Height = 4 },
		new ItemRecord { Id = ItemId.New(), Definition = new DefinitionId( HL2RPIds.Items.Water ) },
		new WorldItemRecord
		{
			ItemId = ItemId.New(),
			Transform = new WorldTransformRecord
			{
				PositionX = x, PositionY = 0, PositionZ = 0,
				RotationX = 0, RotationY = 0, RotationZ = 0, RotationW = 1
			}
		} );

	private static WorldDropContext DropContext( InventoryActor actor ) => new(
		actor,
		new InventoryRecord { Id = InventoryId.New(), Owner = InventoryOwner.Character( actor.CharacterId ), Width = 1, Height = 1 },
		new ItemRecord { Id = ItemId.New(), Definition = new DefinitionId( HL2RPIds.Items.Water ) },
		new WorldTransformRecord
		{
			PositionX = 0, PositionY = 0, PositionZ = 0,
			RotationX = 0, RotationY = 0, RotationZ = 0, RotationW = 1
		} );

	private static InventoryTransferContext TransferContext( InventoryActor actor )
	{
		var source = new InventoryRecord { Id = InventoryId.New(), Owner = InventoryOwner.Character( actor.CharacterId ), Width = 1, Height = 1 };
		return new InventoryTransferContext( actor, source, source,
			new ItemRecord { Id = ItemId.New(), Definition = new DefinitionId( HL2RPIds.Items.Water ) } );
	}

	private sealed class FixedRestraintState : IRestraintStateReader
	{
		public FixedRestraintState( bool restrained ) => Restrained = restrained;
		public bool Restrained { get; set; }
		public bool IsRestrained( CharacterId characterId ) => Restrained;
	}

	private static string FindRoot()
	{
		var directory = new DirectoryInfo( AppContext.BaseDirectory );
		while ( directory is not null && !File.Exists( Path.Combine( directory.FullName, "hl2rp.sbproj" ) ) )
			directory = directory.Parent;
		Assert.IsNotNull( directory );
		return directory.FullName;
	}
}
