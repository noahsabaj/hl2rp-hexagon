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
using System.Text.RegularExpressions;

namespace HL2RP.V2.Tests.Runtime;

[TestClass]
public sealed class RuntimeCompositionTests
{
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
		} );
		Assert.AreEqual( "Unknown citizen", HL2RPRuntimeProjection.PresentationName( viewer, subject, environment.Repositories ) );
		await environment.SeedAsync( unit => unit.Create(
			environment.Repositories.CharacterReferences,
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
			} ) );
		Assert.AreEqual( subject.Name, HL2RPRuntimeProjection.PresentationName( viewer, subject, environment.Repositories ) );
		Assert.AreEqual( "Unknown citizen", HL2RPRuntimeProjection.PresentationName( null, subject, environment.Repositories ) );
	}

	[TestMethod]
	public void ProductRuntimeRoutesShowcaseServicesAndCanonicalRestraintContext()
	{
		var root = FindRoot();
		var host = File.ReadAllText( Path.Combine( root, "Code", "Runtime", "HL2RPHostApplication.cs" ) );
		foreach ( var route in new[]
		{
			"new CombatIntentService", "_combatIntent!.FireAsync", "ReconcileRaisedPistolsAsync",
			"PurchaseFromMachineAsync", "_doorOwnership!.ClaimAsync", "_doorOwnership!.ReleaseAsync",
			"ReconcilePersistedPilotsAsync", "DrainCleanupAsync", "_executableActions.Validate",
			"ExecutableItemActionRoute.HealthVialConsume", "ApplyScannerInputAsync",
			"NearestCharacterTarget", "death.respawn_available_at_ms", "HL2RPInventoryItemState.Project",
			"HL2RPItemDropAvailability.Project", "HL2RPItemActionAvailability.Project", "VendorSellAvailabilityFor",
			"_search!.Open", "scannerFeature is HL2RPScannerDockComponent", "UpdateRecordAsync",
			"ConfigureAsync", "HL2RPObjectiveState.Upsert", "new HL2RPAuditLogHandler",
			"audit: _audit", "PublishAdministrationAudit( actor.Value )",
			"var restraintAuthorization = new RestraintPermissionAuthorizer( _repositories, _chatAuthorities )",
			"_restraintState, restraintAuthorization",
			"HL2RPIds.Commands.DoorOwnership => await DoorOwnershipAsync",
			"_sceneBehavior!.ToggleDoorAsync", "_context.Configuration.Snapshot()",
			"HL2RPPersistenceInvariants.Profile"
		} ) StringAssert.Contains( host, route );
		var world = File.ReadAllText( Path.Combine( root, "Code", "Runtime", "HL2RPSceneRuntime.cs" ) );
		StringAssert.Contains( world, "IsRestrained = _restraints.IsRestrained( characterId )" );
		StringAssert.Contains( world, "new CharacterRestraintInteractable" );
		var sandbox = File.ReadAllText( Path.Combine( root, "Code", "Runtime", "HL2RPSandboxBoundaries.cs" ) );
		foreach ( var route in new[]
		{
			"HL2RPScannerVisualEffects", "Rotation.FromYaw", "PublishPhoto",
			"GetOrAddComponent<HL2RPScannerMotionController>", "command.MaximumAcceleration",
			"_maximumAcceleration * delta", "Time.Delta"
		} )
			StringAssert.Contains( sandbox, route );
		StringAssert.Contains( world, "ValidatePersistedScannerTopology" );
		StringAssert.Contains( world, "scanner.LinkedEntityId != expected.LinkedEntityId" );
		var operations = File.ReadAllText( Path.Combine( root, "Code", "UI", "OperationsPanel.razor" ) );
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

	[TestMethod]
	public void HostConsumesDurableConfigurationAndIncludesItInBothRecoveryProbeDigests()
	{
		var root = FindRoot();
		var host = File.ReadAllText( Path.Combine( root, "Code", "Runtime", "HL2RPHostApplication.cs" ) );
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
		state.AcknowledgePublished( state.BeginPublication( now ) );
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
		objectives = HL2RPObjectiveState.Upsert( objectives, "objective.one", "First", false, firstAt );
		objectives = HL2RPObjectiveState.Upsert( objectives, "objective.two", "Second", false, firstAt );
		objectives = HL2RPObjectiveState.Upsert( objectives, "objective.one", "First edited", true, editedAt );

		Assert.HasCount( 2, objectives );
		var edited = objectives.Single( value => value.Id == "objective.one" );
		Assert.AreEqual( "First edited", edited.Text );
		Assert.IsTrue( edited.Completed );
		Assert.AreEqual( editedAt, edited.UpdatedAtUtc );
		Assert.HasCount( 2, objectives.Select( value => value.Id ).Distinct( StringComparer.Ordinal ).ToArray() );
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
	public void ProductRuntimeDeathLifecycleRevokesAuthorityAndRespawnsFailureSafely()
	{
		var root = FindRoot();
		var host = File.ReadAllText( Path.Combine( root, "Code", "Runtime", "HL2RPHostApplication.cs" ) );
		foreach ( var required in new[]
		{
			"allowDead: command.CommandId == HL2RPIds.Commands.CombatRespawn",
			"Dead characters cannot perform commands.",
			"private OperationResult RespawnCharacter",
			"_respawningCharacters.Contains( character.Id )",
			"var respawned = _combatIntent!.Respawn( actor )",
			"_access.RevokeCharacter( actor.ConnectionId, actor.CharacterId )",
			"binding.Player.HostStripPlayableBody()",
			"_owner.RestoreWorldItem( transition.DroppedPistol.ItemId )",
			"_owner.PublishCombatTargets()"
		} ) StringAssert.Contains( host, required );
		var respawnIndex = host.IndexOf( "var respawned = _combatIntent!.Respawn( actor )", StringComparison.Ordinal );
		var methodIndex = host.IndexOf( "private OperationResult RespawnCharacter", StringComparison.Ordinal );
		var grantIndex = host.IndexOf( "_access.Grant( new InventoryGrant", methodIndex, StringComparison.Ordinal );
		Assert.IsLessThan( respawnIndex, grantIndex,
			"The host must prepare the inventory capability while the death and respawn guards still deny commands." );
	}

	[TestMethod]
	public void ProductPresentationInvalidationCancellationAndUiRoutesAreWired()
	{
		var root = FindRoot();
		var host = File.ReadAllText( Path.Combine( root, "Code", "Runtime", "HL2RPHostApplication.cs" ) );
		foreach ( var required in new[]
		{
			"_sessions.SessionRevoked += OnInteractionSessionRevoked",
			"_sessions.SessionRevoked -= OnInteractionSessionRevoked",
			"_presentationInvalidation.IsRefreshDue( now )",
			"ObserveRestraintTargets()",
			"TrackDeathDeadline(",
			"Task.Delay( delay, linked.Token )",
			"CompleteAsync( ticket.Value.TicketId, actor, linked.Token )",
			"linked?.Token ?? cancellationToken",
			"PermitInspector.HasValidPermit",
			"hl2rp.runtime.item_presentation",
			"binding.AccountId != committed.Actor.AccountId",
			"binding.CharacterId != committed.Actor.CharacterId",
			"item.presentation.sequence",
			"item.presentation.field.",
			"_itemPresentationSequence.Next()",
			"arguments.OptionalGuid( \"character\" )",
			"_civicSubjects.Select( actor.ConnectionId, subjectId )",
			"_civicSubjects.Resolve(",
			"HasPermission( viewer.AccountId, viewer.Id, HL2RPIds.Permissions.Priority )",
			"_civicSubjects.ClearSubject( characterId )",
			"isDead ? \"Deceased\" : \"Active\""
		} ) StringAssert.Contains( host, required );

		var presenter = File.ReadAllText( Path.Combine( root, "Code", "UI", "HL2RPClientPresenter.razor" ) );
		StringAssert.Contains( presenter, "private void Introduce( CharacterId characterId )" );
		StringAssert.Contains( presenter, "private async Task InspectCivicAsync( CharacterId characterId )" );
		StringAssert.Contains( presenter, "_workspace = ShowcaseWorkspace.CivicData" );
		StringAssert.Contains( presenter, "request.PermitKindId" );
		StringAssert.Contains( presenter, "request.ObjectiveId ?? string.Empty" );
		var scoreboard = File.ReadAllText( Path.Combine( root, "Code", "UI", "ScoreboardPanel.razor" ) );
		StringAssert.Contains( scoreboard, "OnIntroduce?.Invoke( characterId )" );
		var combine = File.ReadAllText( Path.Combine( root, "Code", "UI", "CombineSuitePanel.razor" ) );
		Assert.IsFalse( combine.Contains( "NoteTitle", StringComparison.Ordinal ) );
		var rootPanel = File.ReadAllText( Path.Combine( root, "Code", "UI", "HL2RPShowcaseRoot.razor" ) );
		StringAssert.Contains( rootPanel, "item-presentation-backdrop" );
		StringAssert.Contains( rootPanel, "private void CloseCombineWorkspace()" );
		StringAssert.Contains( rootPanel, "ScannerIntent.Exit" );
		Assert.IsFalse( host.Contains(
			"committed.Actor.CharacterId, committed.CommitSequence, committed.Presentation", StringComparison.Ordinal ) );
		Assert.IsFalse( host.Contains( "HL2RPPresentationFields.Roster.Ping", StringComparison.Ordinal ) );
		var operations = File.ReadAllText( Path.Combine( root, "Code", "UI", "OperationsPanel.razor" ) );
		StringAssert.Contains( operations, "×@candidate.Quantity" );
	}

	[TestMethod]
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
		}
	}

	[TestMethod]
	public void InteractionWorkspaceTerminalOwnershipIsCentralizedAndTyped()
	{
		var root = FindRoot();
		var presenter = File.ReadAllText( Path.Combine( root, "Code", "UI", "HL2RPClientPresenter.razor" ) );
		var showcase = File.ReadAllText( Path.Combine( root, "Code", "UI", "HL2RPShowcaseRoot.razor" ) );
		var operations = File.ReadAllText( Path.Combine( root, "Code", "UI", "OperationsPanel.razor" ) );
		var restraint = File.ReadAllText( Path.Combine( root, "Code", "UI", "RestraintPanel.razor" ) );

		foreach ( var required in new[]
		{
			"InteractionWorkspaceCoordinator", "PrepareTransitionAsync", "CompleteSearchCommand",
			"current.Connection != previous.Connection", "current.Character != previous.Character",
			"if ( transition.CloseResult.Failed )", "if ( terminal.CloseResult.Failed )",
			"InteractionWorkspaceSnapshot.From( ViewModel ), ShowcaseWorkspace.CivicData",
			"IsCurrent( transition.Sequence )"
		} ) StringAssert.Contains( presenter, required );
		StringAssert.Contains( showcase, "Action<WorkspaceTransitionRequest> OnWorkspaceChanged" );
		StringAssert.Contains( showcase, "Action<SearchOpenRequest> OnOpenSearch" );
		Assert.IsFalse( showcase.Contains( "ActiveWorkspace { get; set; }", StringComparison.Ordinal ) );
		Assert.IsFalse( showcase.Contains( "_boundWorkspace", StringComparison.Ordinal ) );
		StringAssert.Contains( operations, "new WorkspaceTransitionRequest( workspace )" );
		Assert.IsFalse( operations.Contains( "CloseInteractionAsync", StringComparison.Ordinal ) );
		StringAssert.Contains( restraint, "new SearchOpenRequest( target )" );
		Assert.IsFalse( restraint.Contains(
			"new RestraintActionRequest( target, false, true )", StringComparison.Ordinal ) );
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
