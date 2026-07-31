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
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Runtime;
using HL2RP.V2.Showcase.Combat;
using HL2RP.V2.Tests.Showcase;

namespace HL2RP.V2.Tests.Runtime;

/// <summary>
/// Direct behavioral coverage for the engine-neutral command execution core:
/// admission-independent argument validation, entitlement administration gating,
/// door-ownership and scanner session prerequisites, timed-action cancellation
/// authority, and the request-channel chat policy. Services a case must not reach
/// are deliberately left null so an unexpected touch fails loudly.
/// </summary>
[TestClass]
public sealed class HL2RPCommandExecutionTests
{
	private sealed class FakeExecutionHost : IHL2RPCommandExecutionHost
	{
		public List<ConnectionId> PublishedConnections { get; } = new();
		public Dictionary<SceneEntityId, HL2RPSceneFeatureClassification> Features { get; } = new();

		public void PublishConnection( ConnectionId connectionId ) => PublishedConnections.Add( connectionId );
		public void PublishChanges( HL2RPPresentationChangeSet changes, IReadOnlyList<CommitReceipt> receipts ) { }
		public void DeliverCommittedChat( ChatDelivery delivery, CharacterRecord author ) { }

		public bool TryClassifySceneFeature(
			SceneEntityId sceneEntityId, out HL2RPSceneFeatureClassification? classification )
		{
			var found = Features.TryGetValue( sceneEntityId, out var value );
			classification = found ? value : null;
			return found;
		}
	}

	private sealed record ExecutionFixture(
		HL2RPCommandExecution Execution,
		FakeExecutionHost Host,
		Dictionary<ConnectionId, AccountId> EntitlementQueries,
		HL2RPTimedActionOwnership<ConnectionId, ActiveRestraintAction> RestraintActions,
		HL2RPTimedActionOwnership<ConnectionId, ActivePistolRaiseAction> PistolActions );

	private static ExecutionFixture CreateExecution(
		ShowcaseTestEnvironment environment,
		bool manageAllowed = false,
		Func<InventoryActor, InteractionSessionKind, InteractionSession?>? currentSession = null )
	{
		var host = new FakeExecutionHost();
		var entitlementQueries = new Dictionary<ConnectionId, AccountId>();
		var restraintActions = new HL2RPTimedActionOwnership<ConnectionId, ActiveRestraintAction>();
		var pistolActions = new HL2RPTimedActionOwnership<ConnectionId, ActivePistolRaiseAction>();
		var invalidation = new HL2RPPresentationInvalidation();
		var entitlements = new HL2RPAccountEntitlementService(
			environment.Provider,
			environment.Clock,
			administrator => manageAllowed,
			_ => true );
		var execution = new HL2RPCommandExecution( new HL2RPCommandExecutionServices
		{
			Repositories = environment.Repositories,
			Clock = environment.Clock,
			Provider = environment.Provider,
			Audit = new PostCommitEventBus<AdminAuditFact>(
				Array.Empty<EventHandlerRegistration<AdminAuditFact>>() ),
			Entitlements = entitlements,
			EntitlementQueries = entitlementQueries,
			EntitlementPresentationInvalidation = new HL2RPEntitlementPresentationInvalidation(
				invalidation, _ => Array.Empty<ConnectionId>() ),
			CivicSubjects = new HL2RPCivicSubjectSelections(),
			ProjectionIndex = new HL2RPProjectionIndex(),
			ActiveRestraintActions = restraintActions,
			ActivePistolActions = pistolActions,
			ExecutableActions = HL2RPExecutableItemActionCatalog.CreateDefault(),
			WorldReconciler = null!,
			Inventory = null!,
			WorldItems = null!,
			ItemActions = null!,
			Chat = null,
			Interactions = null,
			Sessions = null,
			Bags = null,
			Tokens = null,
			CombineLocks = null,
			DoorOwnership = null,
			SceneBehavior = null,
			Requests = null,
			Civic = null,
			Recognition = null,
			ObjectiveRouter = null,
			Radio = null,
			Commerce = null,
			Documents = null,
			PermitPurchases = null,
			Restraints = null,
			Search = null,
			Scanner = null,
			Pistol = null,
			CombatIntent = null,
			HealthVials = null,
			MainInventory = _ => null,
			CurrentSession = currentSession ?? (( _, _ ) => null),
			CanManageEntitlements = _ => manageAllowed,
			IsKnownAccount = _ => true,
			Warn = _ => { },
			Fail = _ => { },
			Report = ( _, _ ) => { }
		}, host );
		return new ExecutionFixture( execution, host, entitlementQueries, restraintActions, pistolActions );
	}

	private static InventoryActor Actor() =>
		new( ConnectionId.New(), new AccountId( 76_561_198_000_000_201UL ), CharacterId.New() );

	private static HL2RPCommandArguments Arguments( params (string Key, SnapshotValue Value)[] values ) =>
		new( values.ToDictionary( pair => pair.Key, pair => pair.Value, StringComparer.Ordinal ) );

	[TestMethod]
	public async Task EntitlementCommandsDenyNonManagersAndRecordManagerQueries()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var connection = ConnectionId.New();
		var account = new AccountId( 76_561_198_000_000_202UL );
		var queried = new AccountId( 76_561_198_000_000_203UL );
		var command = new RunSchemaCommandCommand(
			HL2RPIds.Commands.EntitlementQuery,
			new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
			{
				["account"] = SnapshotValue.String( queried.Value.ToString() )
			} );

		var restricted = CreateExecution( environment, manageAllowed: false );
		var denied = await restricted.Execution.RunEntitlementCommandAsync(
			connection, account, null, command, new CommandProjectionDelta(), CancellationToken.None );
		Assert.AreEqual( ErrorCode.Unauthorized, denied.Error?.Code );
		Assert.IsEmpty( restricted.EntitlementQueries );

		var managing = CreateExecution( environment, manageAllowed: true );
		var allowed = await managing.Execution.RunEntitlementCommandAsync(
			connection, account, null, command, new CommandProjectionDelta(), CancellationToken.None );
		Assert.IsTrue( allowed.Succeeded, allowed.Error?.Message );
		Assert.AreEqual( queried, managing.EntitlementQueries[connection] );
	}

	[TestMethod]
	public async Task VendorPurchaseValidatesArgumentsBeforeTouchingCommerce()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var fixture = CreateExecution( environment );
		var result = await fixture.Execution.BuyAsync(
			Actor(),
			Arguments(
				("session", SnapshotValue.String( Guid.NewGuid().ToString() )),
				("definition", SnapshotValue.String( "item_ration" )),
				("quantity", SnapshotValue.Integer( 0 )) ),
			new CommandProjectionDelta(),
			CancellationToken.None );
		Assert.AreEqual( ErrorCode.InvalidArgument, result.Error?.Code );
	}

	[TestMethod]
	public async Task DoorOwnershipRequiresASessionAndAHostConfirmedOwnableDoor()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var actor = Actor();
		var doorId = new SceneEntityId( Guid.NewGuid() );
		InteractionSession Session( InventoryActor forActor ) => new()
		{
			Id = InteractionSessionId.New(),
			Kind = InteractionSessionKind.Door,
			ConnectionId = forActor.ConnectionId,
			CharacterId = forActor.CharacterId,
			Target = InteractionTarget.SceneEntity( doorId ),
			OpenedAt = environment.Clock.UtcNow,
			LastActivityAt = environment.Clock.UtcNow
		};
		var arguments = Arguments( ("intent", SnapshotValue.String( "claim" )) );

		var sessionless = CreateExecution( environment );
		var unauthorized = await sessionless.Execution.DoorOwnershipAsync(
			actor, arguments, new CommandProjectionDelta(), CancellationToken.None );
		Assert.AreEqual( ErrorCode.Unauthorized, unauthorized.Error?.Code );

		var unownable = CreateExecution( environment, currentSession: ( a, _ ) => Session( a ) );
		unownable.Host.Features[doorId] = new HL2RPSceneFeatureClassification(
			HL2RPSceneFeatureKind.Door, null, false );
		var deniedByComponent = await unownable.Execution.DoorOwnershipAsync(
			actor, arguments, new CommandProjectionDelta(), CancellationToken.None );
		Assert.AreEqual( ErrorCode.PolicyDenied, deniedByComponent.Error?.Code );

		var ownable = CreateExecution( environment, currentSession: ( a, _ ) => Session( a ) );
		ownable.Host.Features[doorId] = new HL2RPSceneFeatureClassification(
			HL2RPSceneFeatureKind.Door, null, true );
		var badIntent = await ownable.Execution.DoorOwnershipAsync(
			actor,
			Arguments( ("intent", SnapshotValue.String( "annex" )) ),
			new CommandProjectionDelta(),
			CancellationToken.None );
		Assert.AreEqual( ErrorCode.InvalidArgument, badIntent.Error?.Code );
	}

	[TestMethod]
	public async Task ScannerEntryRequiresACurrentScannerInteraction()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var fixture = CreateExecution( environment );
		var entered = await fixture.Execution.ScannerIntentAsync(
			Actor(),
			Arguments( ("intent", SnapshotValue.String( "enter" )) ),
			new CommandProjectionDelta(),
			CancellationToken.None );
		Assert.AreEqual( ErrorCode.Unauthorized, entered.Error?.Code );

		var invalid = await fixture.Execution.ScannerIntentAsync(
			Actor(),
			Arguments( ("intent", SnapshotValue.String( "warble" )) ),
			new CommandProjectionDelta(),
			CancellationToken.None );
		Assert.AreEqual( ErrorCode.InvalidArgument, invalid.Error?.Code );
	}

	[TestMethod]
	public async Task CancelActionRejectsInstancesTheActorDoesNotOwn()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var fixture = CreateExecution( environment );
		var result = fixture.Execution.CancelAction( Actor(), Guid.NewGuid() );
		Assert.AreEqual( ErrorCode.Unauthorized, result.Error?.Code );
		Assert.IsEmpty( fixture.Host.PublishedConnections );
	}

	[TestMethod]
	public async Task ChatRefusesTheRequestChannelBeforeReachingTheChatService()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var fixture = CreateExecution( environment );
		var result = fixture.Execution.SendChat(
			Actor(), new SendChatCommand( HL2RPIds.Channels.Request, "unfiltered request traffic" ) );
		Assert.AreEqual( ErrorCode.PolicyDenied, result.Error?.Code );
	}
}
