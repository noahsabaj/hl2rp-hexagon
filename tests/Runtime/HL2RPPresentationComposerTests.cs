#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Networking;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Runtime;
using HL2RP.V2.Showcase.Restraint;
using HL2RP.V2.Showcase.Combat;
using HL2RP.V2.Tests.Showcase;
using HL2RP.UI;

namespace HL2RP.V2.Tests.Runtime;

/// <summary>
/// Direct behavioral coverage for the engine-neutral presentation composer that
/// previously lived untested inside the host application: roster recognition,
/// private-snapshot projection, creation availability gating, door-view boundary
/// checks, and timed-action progress channel priority.
/// </summary>
[TestClass]
public sealed class HL2RPPresentationComposerTests
{
	private sealed class FakePresentationHost : IHL2RPPresentationHost
	{
		public List<HL2RPClientPresentationView> Views { get; } = new();
		public CharacterRecord? Nearest { get; set; }
		public HashSet<SceneEntityId> OwnableDoors { get; } = new();

		public IReadOnlyList<HL2RPClientPresentationView> Clients => Views;
		public CharacterRecord? NearestCharacterTarget( CharacterId viewerId ) => Nearest;
		public bool IsOwnableDoor( SceneEntityId sceneEntityId ) => OwnableDoors.Contains( sceneEntityId );
	}

	private sealed class AllModelsCatalog : IWorldModelCatalog
	{
		public bool IsValidModel( string modelPath ) => true;
	}

	private sealed record ComposerFixture(
		HL2RPPresentationComposer Composer,
		FakePresentationHost Host,
		CanonicalCombatHealthDirectory CombatHealth,
		Dictionary<ConnectionId, AccountId> EntitlementQueries,
		Dictionary<ConnectionId, ItemActionPresentationEnvelope> ItemActionPresentations,
		HL2RPTimedActionOwnership<ConnectionId, ActiveRestraintAction> RestraintActions,
		HL2RPTimedActionOwnership<ConnectionId, ActivePistolRaiseAction> PistolActions,
		Func<bool> ManageAllowed );

	private static ComposerFixture CreateComposer(
		ShowcaseTestEnvironment environment,
		bool manageAllowed = false )
	{
		var manageToggle = new[] { manageAllowed };
		var host = new FakePresentationHost();
		var combatHealth = new CanonicalCombatHealthDirectory();
		var entitlementQueries = new Dictionary<ConnectionId, AccountId>();
		var itemActionPresentations = new Dictionary<ConnectionId, ItemActionPresentationEnvelope>();
		var restraintActions = new HL2RPTimedActionOwnership<ConnectionId, ActiveRestraintAction>();
		var pistolActions = new HL2RPTimedActionOwnership<ConnectionId, ActivePistolRaiseAction>();
		var entitlements = new HL2RPAccountEntitlementService(
			environment.Provider,
			environment.Clock,
			administrator => manageToggle[0],
			_ => true );
		var composer = new HL2RPPresentationComposer( new HL2RPPresentationComposerServices
		{
			Repositories = environment.Repositories,
			Schema = environment.Schema,
			Access = environment.Access,
			Clock = environment.Clock,
			Entitlements = entitlements,
			EntitlementQueries = entitlementQueries,
			ItemActionPresentations = itemActionPresentations,
			ActiveRestraintActions = restraintActions,
			ActivePistolActions = pistolActions,
			ProjectionIndex = new HL2RPProjectionIndex(),
			PresentationInvalidation = new HL2RPPresentationInvalidation(),
			CivicSubjects = new HL2RPCivicSubjectSelections(),
			CombatHealth = combatHealth,
			ExecutableActions = HL2RPExecutableItemActionCatalog.CreateDefault(),
			FeaturePolicy = ShowcaseTestEnvironment.AllowPolicy<HL2RPFeaturePolicyContext>(),
			FeatureAuthorization = new HL2RPFeatureRuntimePolicy( environment.Repositories ),
			RestraintState = new RestraintStateReader( environment.Repositories ),
			WorldModels = new AllModelsCatalog(),
			CanManageEntitlements = administrator => manageToggle[0],
			Sessions = () => null,
			Scanner = () => null,
			CombatLifecycle = () => null
		}, host );
		return new ComposerFixture(
			composer, host, combatHealth, entitlementQueries, itemActionPresentations,
			restraintActions, pistolActions, () => manageToggle[0] );
	}

	private static CharacterRecord RequireCharacter( ShowcaseTestEnvironment environment, CharacterId id ) =>
		environment.Repositories.Characters.Find( DomainKeys.Character( id ) )!.Value;

	private static ConnectionId ConnectionAt( int value ) =>
		new( new Guid( value, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x7C ) );

	[TestMethod]
	public async Task RosterOrdersRowsHidesUnrecognizedNamesAndFallsBackToEngineDisplayNames()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var viewerSeed = await environment.SeedCharacterAsync( 76_561_198_000_000_101UL );
		var strangerSeed = await environment.SeedCharacterAsync( 76_561_198_000_000_102UL );
		var fixture = CreateComposer( environment );
		var viewerConnection = ConnectionAt( 1 );
		var strangerConnection = ConnectionAt( 2 );
		var selectingConnection = ConnectionAt( 3 );
		fixture.Host.Views.Add( new HL2RPClientPresentationView(
			strangerConnection, new AccountId( 76_561_198_000_000_102UL ),
			strangerSeed.Actor.CharacterId, "StrangerEngineName" ) );
		fixture.Host.Views.Add( new HL2RPClientPresentationView(
			selectingConnection, new AccountId( 76_561_198_000_000_103UL ), null, "SelectingEngineName" ) );
		fixture.Host.Views.Add( new HL2RPClientPresentationView(
			viewerConnection, new AccountId( 76_561_198_000_000_101UL ),
			viewerSeed.Actor.CharacterId, "ViewerEngineName" ) );

		var slice = fixture.Composer.BuildRosterProjectionSlice( viewerConnection );
		var rows = slice.Value.Rows;

		Assert.HasCount( 3, rows );
		Assert.AreEqual( viewerConnection, rows[0].ConnectionId );
		Assert.AreEqual( strangerConnection, rows[1].ConnectionId );
		Assert.AreEqual( selectingConnection, rows[2].ConnectionId );
		var viewerCharacter = RequireCharacter( environment, viewerSeed.Actor.CharacterId );
		Assert.AreEqual( viewerCharacter.Name,
			rows[0].Fields[HL2RPPresentationFields.Roster.DisplayName].StringValue );
		Assert.AreEqual( "Unknown citizen",
			rows[1].Fields[HL2RPPresentationFields.Roster.DisplayName].StringValue );
		Assert.AreEqual( "SelectingEngineName",
			rows[2].Fields[HL2RPPresentationFields.Roster.DisplayName].StringValue );
		Assert.AreEqual( "Selecting", rows[2].Fields[HL2RPPresentationFields.Roster.Status].StringValue );
		Assert.IsTrue( rows[0].Fields[HL2RPPresentationFields.Roster.IsLocal].BooleanValue );
		Assert.IsFalse( rows[1].Fields[HL2RPPresentationFields.Roster.IsLocal].BooleanValue );
	}

	[TestMethod]
	public async Task PrivateSnapshotProjectsCombatVitalsAndItemPresentationForTheOwningConnection()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var seed = await environment.SeedCharacterAsync( 76_561_198_000_000_104UL );
		var fixture = CreateComposer( environment );
		var connection = ConnectionAt( 4 );
		fixture.Host.Views.Add( new HL2RPClientPresentationView(
			connection, seed.Actor.AccountId, seed.Actor.CharacterId, "Owner" ) );
		fixture.CombatHealth.Publish( seed.Actor.CharacterId, 100, 63 );
		fixture.ItemActionPresentations[connection] = new ItemActionPresentationEnvelope(
			seed.Actor.CharacterId,
			7,
			new ItemActionPresentationReceipt(
				ItemActionPresentationKind.PersonalNote,
				"Field Note",
				new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
				{
					["note.body"] = SnapshotValue.String( "observed" )
				} ) );

		var character = RequireCharacter( environment, seed.Actor.CharacterId );
		var snapshot = fixture.Composer.BuildPrivateSnapshot( character, seed.InventoryId );

		Assert.AreEqual( 63, snapshot.Values["vitals.health"].IntegerValue );
		Assert.AreEqual( 100, snapshot.Values["vitals.health_max"].IntegerValue );
		Assert.AreEqual( string.Empty, snapshot.Values["death.cause"].StringValue );
		Assert.IsFalse( snapshot.Values["death.can_respawn"].BooleanValue );
		Assert.IsFalse( snapshot.Values["restraint.active"].BooleanValue );
		Assert.AreEqual( 7, snapshot.Values["item.presentation.sequence"].IntegerValue );
		Assert.AreEqual( "personal_note", snapshot.Values["item.presentation.kind"].StringValue );
		Assert.AreEqual( "Field Note", snapshot.Values["item.presentation.title"].StringValue );
		Assert.AreEqual( "observed", snapshot.Values["item.presentation.field.note.body"].StringValue );
	}

	[TestMethod]
	public async Task CreationAvailabilityRevealsQueriedAccountsOnlyToManagers()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var fixture = CreateComposer( environment, manageAllowed: false );
		var connection = ConnectionAt( 5 );
		var self = new AccountId( 76_561_198_000_000_105UL );
		var queried = new AccountId( 76_561_198_000_000_106UL );
		fixture.EntitlementQueries[connection] = queried;

		var restricted = fixture.Composer.BuildCreationAvailabilityView( connection, self, null, 3 );
		Assert.IsFalse( restricted.Fields[HL2RPPresentationFields.CharacterCreation.CanManageEntitlements].BooleanValue );
		Assert.AreEqual( string.Empty,
			restricted.Fields[HL2RPPresentationFields.CharacterCreation.QueriedAccountId].StringValue );

		var managing = CreateComposer( environment, manageAllowed: true );
		managing.EntitlementQueries[connection] = queried;
		var revealed = managing.Composer.BuildCreationAvailabilityView( connection, self, null, 3 );
		Assert.IsTrue( revealed.Fields[HL2RPPresentationFields.CharacterCreation.CanManageEntitlements].BooleanValue );
		Assert.AreEqual( queried.Value.ToString( System.Globalization.CultureInfo.InvariantCulture ),
			revealed.Fields[HL2RPPresentationFields.CharacterCreation.QueriedAccountId].StringValue );
	}

	[TestMethod]
	public async Task DoorViewRequiresTheHostToConfirmAnOwnableDoorComponent()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var seed = await environment.SeedCharacterAsync( 76_561_198_000_000_107UL );
		var fixture = CreateComposer( environment );
		var doorId = new SceneEntityId( Guid.NewGuid() );
		var record = new PersistentSceneEntityRecord
		{
			Id = doorId,
			Kind = "door",
			State = HL2RPPersistence.Payload(
				HL2RPPersistence.DoorState, new DoorEntityState { CombineLocked = false, IsOpen = false } )
		};
		await using ( var unitOfWork = environment.Provider.BeginUnitOfWork() )
		{
			unitOfWork.Create( environment.Repositories.SceneEntities, DomainKeys.SceneEntity( doorId ), record );
			var committed = await unitOfWork.CommitAsync();
			Assert.IsTrue( committed.Succeeded, committed.Error?.Message );
		}
		var character = RequireCharacter( environment, seed.Actor.CharacterId );
		var session = new InteractionSession
		{
			Id = InteractionSessionId.New(),
			Kind = InteractionSessionKind.Door,
			ConnectionId = seed.Actor.ConnectionId,
			CharacterId = seed.Actor.CharacterId,
			Target = InteractionTarget.SceneEntity( doorId ),
			OpenedAt = environment.Clock.UtcNow,
			LastActivityAt = environment.Clock.UtcNow
		};

		Assert.IsNull( fixture.Composer.DoorView( character, session, 5 ),
			"A door without a host-confirmed ownable component must not project a view." );

		fixture.Host.OwnableDoors.Add( doorId );
		var view = fixture.Composer.DoorView( character, session, 5 );
		Assert.IsNotNull( view );
		Assert.AreEqual( "unowned", view.Fields[HL2RPPresentationFields.Door.OwnerStatus].StringValue );
		Assert.IsTrue( view.Fields[HL2RPPresentationFields.Door.CanClaim].BooleanValue );
		Assert.IsFalse( view.Fields[HL2RPPresentationFields.Door.CombineLocked].BooleanValue );
	}

	[TestMethod]
	public async Task ActionProgressPrefersTheRestraintChannelOverThePistolChannel()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var seed = await environment.SeedCharacterAsync( 76_561_198_000_000_108UL );
		var fixture = CreateComposer( environment );
		var connection = seed.Actor.ConnectionId;
		var now = environment.Clock.UtcNow;
		Assert.IsTrue( fixture.PistolActions.TryAdd( connection, new ActivePistolRaiseAction(
			seed.Actor, Guid.NewGuid(), now + PistolCombatService.DefaultRaiseDelay,
			new CancellationTokenSource() ) ) );
		Assert.IsTrue( fixture.RestraintActions.TryAdd( connection, new ActiveRestraintAction(
			seed.Actor,
			new RestraintTicket(
				InteractionSessionId.New(), seed.Actor, CharacterId.New(), seed.InventoryId,
				ItemId.New(), now + RestraintService.RestraintDuration ),
			new CancellationTokenSource() ) ) );

		var progress = fixture.Composer.BuildActionProgress( connection );
		Assert.IsNotNull( progress );
		Assert.AreEqual( new ActionId( HL2RPIds.Actions.Restrain ), progress.ActionId );

		Assert.HasCount( 1, fixture.RestraintActions.CancelForLifecycle( connection ) );
		var pistolProgress = fixture.Composer.BuildActionProgress( connection );
		Assert.IsNotNull( pistolProgress );
		Assert.AreEqual( new ActionId( HL2RPIds.Actions.Fire ), pistolProgress.ActionId );
	}

	[TestMethod]
	public void DeterministicGuidIsStableAndNeverEmpty()
	{
		var first = HL2RPPresentationComposer.DeterministicGuid( "hl2rp.presentation.test" );
		var second = HL2RPPresentationComposer.DeterministicGuid( "hl2rp.presentation.test" );
		Assert.AreEqual( first, second );
		Assert.AreNotEqual( Guid.Empty, first );
		Assert.AreNotEqual( first, HL2RPPresentationComposer.DeterministicGuid( "hl2rp.presentation.other" ) );
	}
}
