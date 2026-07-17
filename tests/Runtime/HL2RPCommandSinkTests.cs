#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;
using HL2RP.V2.Runtime;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Runtime;

[TestClass]
public sealed class HL2RPCommandSinkTests
{
	[TestMethod]
	public void AdmissionRejectsDisposedUnboundAndCrossAccountActors()
	{
		var disposed = HL2RPClientCommandSink.Admit(
			disposed: true, bound: true, new AccountId( 1 ), new AccountId( 1 ) );
		var unbound = HL2RPClientCommandSink.Admit(
			disposed: false, bound: false, default, new AccountId( 1 ) );
		var mismatch = HL2RPClientCommandSink.Admit(
			disposed: false, bound: true, new AccountId( 1 ), new AccountId( 2 ) );
		var admitted = HL2RPClientCommandSink.Admit(
			disposed: false, bound: true, new AccountId( 1 ), new AccountId( 1 ) );

		Assert.AreEqual( ErrorCode.Conflict, disposed.Error!.Code );
		Assert.AreEqual( ErrorCode.Unauthorized, unbound.Error!.Code );
		Assert.AreEqual( ErrorCode.Unauthorized, mismatch.Error!.Code,
			"A binding owned by another account must never execute." );
		Assert.IsTrue( admitted.Succeeded );
	}

	[TestMethod]
	public async Task EveryClientCommandRoutesToItsHandler()
	{
		var characterId = CharacterId.New();
		var inventoryId = InventoryId.New();
		var itemId = ItemId.New();
		var sessionId = InteractionSessionId.New();
		var table = new (ClientCommand Command, string Route)[]
		{
			(new RequestCharacterListCommand(), "list"),
			(new CreateCharacterCommand( new CharacterCreationInput(
				"Name", "Description", new DefinitionId( "model" ), new FactionId( "citizen" ), null,
				new Dictionary<string, SnapshotValue>( StringComparer.Ordinal ) ) ), "create"),
			(new LoadCharacterCommand( characterId ), $"load:{characterId}"),
			(new DeleteCharacterCommand( characterId ), $"delete:{characterId}"),
			(new UnloadCharacterCommand(), "unload"),
			(new MoveInventoryItemCommand( inventoryId, inventoryId, itemId, new InventoryGridPosition( 0, 0 ) ), "move"),
			(new RunItemActionCommand( inventoryId, itemId, new ActionId( "use" ),
				new Dictionary<string, SnapshotValue>( StringComparer.Ordinal ) ), "item-action"),
			(new DropItemCommand( inventoryId, itemId ), "drop"),
			(new PickUpItemCommand( itemId, inventoryId ), "pickup"),
			(new SendChatCommand( "ic", "hello" ), "chat"),
			(new CancelActionCommand( Guid.NewGuid() ), "cancel"),
			(new BeginInteractionCommand( new InteractionTargetInput(
				InteractionTargetInputKind.SceneEntity, Guid.NewGuid() ) ), "begin-interaction"),
			(new ContinueInteractionCommand( sessionId, new InteractionTargetInput(
				InteractionTargetInputKind.SceneEntity, Guid.NewGuid() ) ), "continue-interaction"),
			(new CloseInteractionCommand( sessionId ), $"close-interaction:{sessionId}"),
			(new RunSchemaCommandCommand( "civic_data",
				new Dictionary<string, SnapshotValue>( StringComparer.Ordinal ) ), "schema:civic_data")
		};

		foreach ( var (command, route) in table )
		{
			var routes = new RecordingClientRoutes();
			var result = await HL2RPClientCommandSink.RouteAsync(
				routes, "actor", command, new CommandProjectionDelta(), CancellationToken.None );
			Assert.IsTrue( result.Succeeded, $"{command.GetType().Name}: {result.Error?.Message}" );
			Assert.HasCount( 1, routes.Calls, command.GetType().Name );
			Assert.AreEqual( route, routes.Calls[0], command.GetType().Name );
		}
	}

	[TestMethod]
	public async Task UnknownClientCommandFailsClosedWithoutRouting()
	{
		var routes = new RecordingClientRoutes();
		var result = await HL2RPClientCommandSink.RouteAsync(
			routes, "actor", new UnknownCommand(), new CommandProjectionDelta(), CancellationToken.None );

		Assert.AreEqual( ErrorCode.UnknownDefinition, result.Error!.Code );
		Assert.IsEmpty( routes.Calls );
	}

	[TestMethod]
	public async Task UnregisteredSchemaCommandFailsClosedWithoutAnyEvaluation()
	{
		var routes = new RecordingSchemaRoutes { DefinitionKnown = false };
		var result = await HL2RPSchemaCommandSink.RouteAsync(
			routes, "rpc", Schema( "not_registered" ), new CommandProjectionDelta(), CancellationToken.None );

		Assert.AreEqual( ErrorCode.UnknownDefinition, result.Error!.Code );
		Assert.IsEmpty( routes.Calls );
	}

	[TestMethod]
	public async Task EntitlementCommandsDispatchByRpcAccountBeforeTheCharacterRequirement()
	{
		foreach ( var commandId in new[]
		{
			HL2RPIds.Commands.EntitlementQuery,
			HL2RPIds.Commands.EntitlementGrant,
			HL2RPIds.Commands.EntitlementRevoke
		} )
		{
			var routes = new RecordingSchemaRoutes
			{
				// An administrator without an active character must still administer.
				RequireResult = OperationResult<InventoryActor>.Failure(
					ErrorCode.Unauthorized, "An active character is required." )
			};
			var result = await HL2RPSchemaCommandSink.RouteAsync(
				routes, "rpc", Schema( commandId ), new CommandProjectionDelta(), CancellationToken.None );

			Assert.IsTrue( result.Succeeded, commandId );
			CollectionAssert.AreEqual( new[] { $"entitlement:{commandId}" }, routes.Calls,
				$"{commandId} must dispatch before RequireInventoryActor is even consulted." );
		}
	}

	[TestMethod]
	public async Task CharacterRequirementFailurePropagatesBeforePermissionAndRouting()
	{
		var routes = new RecordingSchemaRoutes
		{
			PermissionId = "hl2rp.permission",
			RequireResult = OperationResult<InventoryActor>.Failure( ErrorCode.Unauthorized, "no character" )
		};
		var result = await HL2RPSchemaCommandSink.RouteAsync(
			routes, "rpc", Schema( HL2RPIds.Commands.CivicData ), new CommandProjectionDelta(), CancellationToken.None );

		Assert.AreEqual( ErrorCode.Unauthorized, result.Error!.Code );
		Assert.AreEqual( "no character", result.Error.Message );
		CollectionAssert.AreEqual( new[] { "require:allowDead=False" }, routes.Calls );
	}

	[TestMethod]
	public async Task OnlyCombatRespawnMayRunWhileDead()
	{
		var respawnRoutes = new RecordingSchemaRoutes();
		_ = await HL2RPSchemaCommandSink.RouteAsync(
			respawnRoutes, "rpc", Schema( HL2RPIds.Commands.CombatRespawn ),
			new CommandProjectionDelta(), CancellationToken.None );
		var civicRoutes = new RecordingSchemaRoutes();
		_ = await HL2RPSchemaCommandSink.RouteAsync(
			civicRoutes, "rpc", Schema( HL2RPIds.Commands.CivicData ),
			new CommandProjectionDelta(), CancellationToken.None );

		Assert.AreEqual( "require:allowDead=True", respawnRoutes.Calls[0] );
		Assert.AreEqual( "require:allowDead=False", civicRoutes.Calls[0] );
	}

	[TestMethod]
	public async Task MissingPermissionFailsClosedBeforeRouting()
	{
		var routes = new RecordingSchemaRoutes
		{
			PermissionId = "hl2rp.restraint",
			PermissionGranted = false
		};
		var result = await HL2RPSchemaCommandSink.RouteAsync(
			routes, "rpc", Schema( HL2RPIds.Commands.RestraintSet ), new CommandProjectionDelta(), CancellationToken.None );

		Assert.AreEqual( ErrorCode.Unauthorized, result.Error!.Code );
		StringAssert.Contains( result.Error.Message, "hl2rp.restraint" );
		CollectionAssert.AreEqual(
			new[] { "require:allowDead=False", "permission:hl2rp.restraint" },
			routes.Calls,
			"The permission gate runs after the character requirement and blocks the route." );
	}

	[TestMethod]
	public async Task CommandsWithoutADeclaredPermissionSkipTheGate()
	{
		var routes = new RecordingSchemaRoutes { PermissionId = null };
		var result = await HL2RPSchemaCommandSink.RouteAsync(
			routes, "rpc", Schema( HL2RPIds.Commands.Introduce ), new CommandProjectionDelta(), CancellationToken.None );

		Assert.IsTrue( result.Succeeded );
		CollectionAssert.AreEqual( new[] { "require:allowDead=False", "route:introduce" }, routes.Calls );
	}

	[TestMethod]
	public async Task EverySchemaCommandRoutesToItsHandler()
	{
		var table = new (string CommandId, string Route)[]
		{
			(HL2RPIds.Commands.CivicData, "route:civic_data"),
			(HL2RPIds.Commands.CityObjectives, "route:city_objectives"),
			(HL2RPIds.Commands.Priority, "route:priority"),
			(HL2RPIds.Commands.RadioFrequency, "route:radio_frequency"),
			(HL2RPIds.Commands.Introduce, "route:introduce"),
			(HL2RPIds.Commands.DoorOwnership, "route:door_ownership"),
			(HL2RPIds.Commands.AdministrationAudit, "route:administration_audit"),
			(HL2RPIds.Commands.CommerceBuy, "route:commerce_buy"),
			(HL2RPIds.Commands.CommerceSell, "route:commerce_sell"),
			(HL2RPIds.Commands.PermitPurchase, "route:permit_purchase"),
			(HL2RPIds.Commands.NoteWrite, "route:note_write"),
			(HL2RPIds.Commands.RestraintSet, "route:restraint_set"),
			(HL2RPIds.Commands.ScannerIntent, "route:scanner_intent"),
			(HL2RPIds.Commands.CombatRespawn, "route:combat_respawn")
		};

		foreach ( var (commandId, route) in table )
		{
			var routes = new RecordingSchemaRoutes();
			var result = await HL2RPSchemaCommandSink.RouteAsync(
				routes, "rpc", Schema( commandId ), new CommandProjectionDelta(), CancellationToken.None );
			Assert.IsTrue( result.Succeeded, $"{commandId}: {result.Error?.Message}" );
			Assert.AreEqual( route, routes.Calls[^1], commandId );
		}
	}

	[TestMethod]
	public async Task RegisteredCommandWithoutARuntimeHandlerFailsClosed()
	{
		var routes = new RecordingSchemaRoutes();
		var result = await HL2RPSchemaCommandSink.RouteAsync(
			routes, "rpc", Schema( "registered_but_unrouted" ), new CommandProjectionDelta(), CancellationToken.None );

		Assert.AreEqual( ErrorCode.UnknownDefinition, result.Error!.Code );
		StringAssert.Contains( result.Error.Message, "no HL2RP runtime handler" );
	}

	private static RunSchemaCommandCommand Schema( string commandId ) => new(
		commandId, new Dictionary<string, SnapshotValue>( StringComparer.Ordinal ) );

	private sealed record UnknownCommand : ClientCommand;

	private sealed class RecordingClientRoutes : IHL2RPClientCommandRoutes<string>
	{
		public List<string> Calls { get; } = new();

		public OperationResult ListCharacters( string actor ) => Record( "list" );
		public ValueTask<OperationResult> CreateAsync(
			string actor, CreateCharacterCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RecordAsync( "create" );
		public ValueTask<OperationResult> LoadAsync(
			string actor, CharacterId characterId, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RecordAsync( $"load:{characterId}" );
		public ValueTask<OperationResult> DeleteAsync(
			string actor, CharacterId characterId, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RecordAsync( $"delete:{characterId}" );
		public ValueTask<OperationResult> UnloadAsync(
			string actor, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RecordAsync( "unload" );
		public ValueTask<OperationResult> MoveAsync(
			string actor, MoveInventoryItemCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RecordAsync( "move" );
		public ValueTask<OperationResult> RunItemActionAsync(
			string actor, RunItemActionCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RecordAsync( "item-action" );
		public ValueTask<OperationResult> DropAsync(
			string actor, DropItemCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RecordAsync( "drop" );
		public ValueTask<OperationResult> PickupAsync(
			string actor, PickUpItemCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RecordAsync( "pickup" );
		public OperationResult SendChat( string actor, SendChatCommand command ) => Record( "chat" );
		public OperationResult CancelAction( string actor, Guid instanceId ) => Record( "cancel" );
		public ValueTask<OperationResult> BeginInteractionAsync(
			string actor, InteractionTargetInput target, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RecordAsync( "begin-interaction" );
		public OperationResult ContinueInteraction( string actor, ContinueInteractionCommand command ) =>
			Record( "continue-interaction" );
		public OperationResult CloseInteraction( string actor, InteractionSessionId sessionId ) =>
			Record( $"close-interaction:{sessionId}" );
		public ValueTask<OperationResult> RunSchemaCommandAsync(
			string actor, RunSchemaCommandCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RecordAsync( $"schema:{command.CommandId}" );

		private OperationResult Record( string route )
		{
			Calls.Add( route );
			return OperationResult.Success();
		}

		private ValueTask<OperationResult> RecordAsync( string route ) => ValueTask.FromResult( Record( route ) );
	}

	private sealed class RecordingSchemaRoutes : IHL2RPSchemaCommandRoutes<string>
	{
		public List<string> Calls { get; } = new();
		public bool DefinitionKnown { get; set; } = true;
		public string? PermissionId { get; set; }
		public bool PermissionGranted { get; set; } = true;
		public OperationResult<InventoryActor> RequireResult { get; set; } =
			OperationResult<InventoryActor>.Success( new InventoryActor(
				ConnectionId.New(), new AccountId( 1 ), CharacterId.New() ) );

		public bool TryGetCommandDefinition( string commandId, out string? permissionId )
		{
			permissionId = PermissionId;
			return DefinitionKnown;
		}

		public ValueTask<OperationResult> RunEntitlementCommandAsync(
			string rpc, RunSchemaCommandCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken )
		{
			Calls.Add( $"entitlement:{command.CommandId}" );
			return ValueTask.FromResult( OperationResult.Success() );
		}

		public OperationResult<InventoryActor> RequireInventoryActor( string rpc, bool allowDead )
		{
			Calls.Add( $"require:allowDead={allowDead}" );
			return RequireResult;
		}

		public bool HasPermission( InventoryActor actor, string permissionId )
		{
			Calls.Add( $"permission:{permissionId}" );
			return PermissionGranted;
		}

		public OperationResult CivicData( InventoryActor actor, HL2RPCommandArguments arguments ) =>
			Route( "civic_data" );
		public ValueTask<OperationResult> SetObjectivesAsync(
			InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RouteAsync( "city_objectives" );
		public ValueTask<OperationResult> SetPriorityAsync(
			InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RouteAsync( "priority" );
		public ValueTask<OperationResult> TuneRadioAsync(
			InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RouteAsync( "radio_frequency" );
		public ValueTask<OperationResult> IntroduceAsync(
			InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RouteAsync( "introduce" );
		public ValueTask<OperationResult> DoorOwnershipAsync(
			InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RouteAsync( "door_ownership" );
		public OperationResult PublishAdministrationAudit( InventoryActor actor ) =>
			Route( "administration_audit" );
		public ValueTask<OperationResult> BuyAsync(
			InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RouteAsync( "commerce_buy" );
		public ValueTask<OperationResult> SellAsync(
			InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RouteAsync( "commerce_sell" );
		public ValueTask<OperationResult> PurchasePermitAsync(
			InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RouteAsync( "permit_purchase" );
		public ValueTask<OperationResult> WriteNoteAsync(
			InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RouteAsync( "note_write" );
		public ValueTask<OperationResult> SetRestraintAsync(
			InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RouteAsync( "restraint_set" );
		public ValueTask<OperationResult> ScannerIntentAsync(
			InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken ) =>
			RouteAsync( "scanner_intent" );
		public OperationResult RespawnCharacter( InventoryActor actor ) =>
			Route( "combat_respawn" );

		private OperationResult Route( string name )
		{
			Calls.Add( $"route:{name}" );
			return OperationResult.Success();
		}

		private ValueTask<OperationResult> RouteAsync( string name ) => ValueTask.FromResult( Route( name ) );
	}
}
