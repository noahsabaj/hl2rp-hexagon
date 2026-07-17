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
using HL2RP.V2.Schema;
using HL2RP.V2.Showcase.Combat;
using HL2RP.V2.Showcase.Restraint;
using HL2RP.V2.Showcase.Scanner;
using HL2RP.UI;

namespace HL2RP.V2.Runtime;

internal enum HL2RPSceneFeatureKind
{
	ScannerDock,
	ScannerDrone,
	Forcefield,
	Machine,
	Door,
	Other
}

/// <summary>
/// Engine-neutral projection of one scene-feature component: which behavior family
/// it belongs to, the linked drone for scanner docks, and door ownability.
/// </summary>
internal sealed record HL2RPSceneFeatureClassification(
	HL2RPSceneFeatureKind Kind,
	SceneEntityId? LinkedDroneId,
	bool DoorOwnable );

/// <summary>
/// The engine-facing effects the command executor cannot perform itself: targeted
/// presentation publication, committed chat transport, and scene-component
/// classification. The host application implements this over its live scene and
/// transport; tests implement it over fixtures.
/// </summary>
internal interface IHL2RPCommandExecutionHost
{
	void PublishConnection( ConnectionId connectionId );
	void PublishChanges( HL2RPPresentationChangeSet changes, IReadOnlyList<CommitReceipt> receipts );
	void DeliverCommittedChat( ChatDelivery delivery, CharacterRecord author );
	bool TryClassifySceneFeature( SceneEntityId sceneEntityId, out HL2RPSceneFeatureClassification? classification );
}

/// <summary>
/// Constructor bundle for <see cref="HL2RPCommandExecution"/>. Built by the host at
/// the end of initialization, so the always-constructed services arrive non-null and
/// the initialization-created showcase services keep the same nullable contract the
/// host fields carry (route bodies null-assert exactly where the host did).
/// </summary>
internal sealed class HL2RPCommandExecutionServices
{
	public required DomainRepositories Repositories { get; init; }
	public required IHexClock Clock { get; init; }
	public required IPersistenceProvider Provider { get; init; }
	public required PostCommitEventBus<AdminAuditFact> Audit { get; init; }
	public required HL2RPAccountEntitlementService Entitlements { get; init; }
	public required Dictionary<ConnectionId, AccountId> EntitlementQueries { get; init; }
	public required HL2RPEntitlementPresentationInvalidation EntitlementPresentationInvalidation { get; init; }
	public required HL2RPCivicSubjectSelections CivicSubjects { get; init; }
	public required HL2RPProjectionIndex ProjectionIndex { get; init; }
	public required HL2RPTimedActionOwnership<ConnectionId, ActiveRestraintAction> ActiveRestraintActions { get; init; }
	public required HL2RPTimedActionOwnership<ConnectionId, ActivePistolRaiseAction> ActivePistolActions { get; init; }
	public required HL2RPExecutableItemActionCatalog ExecutableActions { get; init; }
	public required HL2RPWorldItemReconciler WorldReconciler { get; init; }
	public required InventoryMutationService Inventory { get; init; }
	public required WorldItemService WorldItems { get; init; }
	public required ItemActionService ItemActions { get; init; }
	public required ChatService? Chat { get; init; }
	public required InteractionAuthorityService? Interactions { get; init; }
	public required InteractionSessionService? Sessions { get; init; }
	public required BagInteractionService? Bags { get; init; }
	public required TokenStackService? Tokens { get; init; }
	public required CombineLockService? CombineLocks { get; init; }
	public required DoorOwnershipService? DoorOwnership { get; init; }
	public required HL2RPSceneEntityBehaviorService? SceneBehavior { get; init; }
	public required RequestDeviceService? Requests { get; init; }
	public required CivicService? Civic { get; init; }
	public required RecognitionService? Recognition { get; init; }
	public required HL2RPObjectiveCommandRouter? ObjectiveRouter { get; init; }
	public required RadioTuningService? Radio { get; init; }
	public required CommerceService? Commerce { get; init; }
	public required DocumentService? Documents { get; init; }
	public required PermitPurchaseService? PermitPurchases { get; init; }
	public required RestraintService? Restraints { get; init; }
	public required RestraintSearchService? Search { get; init; }
	public required ScannerPilotService? Scanner { get; init; }
	public required PistolCombatService? Pistol { get; init; }
	public required CombatIntentService? CombatIntent { get; init; }
	public required HealthVialConsumeService? HealthVials { get; init; }
	public required Func<CharacterId, InventoryRecord?> MainInventory { get; init; }
	public required Func<InventoryActor, InteractionSessionKind, InteractionSession?> CurrentSession { get; init; }
	public required Func<HL2RPEntitlementAdministrator, bool> CanManageEntitlements { get; init; }
	public required Func<AccountId, bool> IsKnownAccount { get; init; }
	public required Action<string> Warn { get; init; }
	public required Action<string> Fail { get; init; }
	public required Action<Exception, string> Report { get; init; }
}

/// <summary>
/// Executes every admitted client and schema command body — inventory movement,
/// item actions, world drops/pickups, chat, interactions, timed restraint and
/// pistol actions, and the whole schema route family — against committed services.
/// Engine-neutral by construction: admission, body/transform resolution, scene
/// classification, transport, and publication stay with the host application and
/// arrive through <see cref="IHL2RPCommandExecutionHost"/> or resolved parameters.
/// </summary>
internal sealed class HL2RPCommandExecution
{
	private readonly HL2RPCommandExecutionServices _services;
	private readonly IHL2RPCommandExecutionHost _host;

	public HL2RPCommandExecution( HL2RPCommandExecutionServices services, IHL2RPCommandExecutionHost host )
	{
		_services = services ?? throw new ArgumentNullException( nameof(services) );
		_host = host ?? throw new ArgumentNullException( nameof(host) );
	}

	private static OperationResult Failure( OperationError error ) =>
		OperationResult.Failure( error.Code, error.Message );

	private static OperationResult Untyped<T>( OperationResult<T> result ) =>
		result.Succeeded ? OperationResult.Success() : Failure( result.Error! );

	internal static void CancelToken( CancellationTokenSource cancellation )
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

	internal static OperationResult<InteractionTarget> ToTarget( InteractionTargetInput input )
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

	internal static bool IsFinite( WorldTransformRecord transform ) =>
		float.IsFinite( transform.PositionX ) && float.IsFinite( transform.PositionY ) && float.IsFinite( transform.PositionZ ) &&
		float.IsFinite( transform.RotationX ) && float.IsFinite( transform.RotationY ) &&
		float.IsFinite( transform.RotationZ ) && float.IsFinite( transform.RotationW );

	internal async ValueTask<OperationResult> MoveAsync(
		InventoryActor actor,
		MoveInventoryItemCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var result = await _services.Inventory.MoveCommittedAsync(
			actor, command.SourceId, command.TargetId, command.ItemId, command.X, command.Y, cancellationToken );
		if ( result.Succeeded )
		{
			projectionDelta.Observe( result.Value );
			_services.Bags?.ItemMoved( command.ItemId );
		}
		return Untyped( result );
	}

	internal async ValueTask<OperationResult> RunItemActionAsync(
		InventoryActor actor,
		RunItemActionCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var item = _services.Repositories.Items.Find( DomainKeys.Item( command.ItemId ) )?.Value;
		if ( item is null ) return OperationResult.Failure( ErrorCode.NotFound, "Item was not found." );
		if ( !_services.ExecutableActions.TryResolve( item.Definition, command.ActionId, out var executable ) )
			return OperationResult.Failure( ErrorCode.UnknownDefinition, "Item action has no executable host route." );
		if ( executable.Route == ExecutableItemActionRoute.BagInteraction )
			return Untyped( _services.Bags!.Open( actor, command.InventoryId, command.ItemId ) );
		if ( executable.Route == ExecutableItemActionRoute.TokenSplit )
		{
			var amount = new HL2RPCommandArguments( command.Arguments ).Integer( "amount" );
			if ( amount.Failed || amount.Value is <= 0 or > int.MaxValue )
				return OperationResult.Failure( ErrorCode.InvalidArgument, "Token split amount is invalid." );
			var split = await _services.Tokens!.SplitAsync(
				actor, command.InventoryId, command.ItemId, (int)amount.Value, cancellationToken );
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
			var combined = await _services.Tokens!.CombineAsync(
				actor, command.InventoryId, command.ItemId, new ItemId( other.Value ), cancellationToken );
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
			var session = _services.CurrentSession( actor, InteractionSessionKind.Door );
			if ( session is null )
				return OperationResult.Failure( ErrorCode.Unauthorized, "A current door session is required." );
			var installed = await _services.CombineLocks!.InstallAsync(
				actor, session.Id, command.InventoryId, command.ItemId, cancellationToken );
			if ( installed.Succeeded )
			{
				projectionDelta.Observe( installed.Value );
				projectionDelta.SceneEntities.Add( installed.Value.DoorEntityId );
			}
			return Untyped( installed );
		}
		if ( executable.Route == ExecutableItemActionRoute.HealthVialConsume )
		{
			var consumed = await _services.HealthVials!.ConsumeAsync(
				actor, command.InventoryId, command.ItemId, cancellationToken );
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
			var tuned = await _services.Radio!.TuneAsync(
				actor, command.InventoryId, command.ItemId, frequency.Value, cancellationToken );
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
			var requested = await _services.Requests!.SendAsync(
				actor, command.InventoryId, command.ItemId, text.Value, cancellationToken );
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
			var edited = await _services.Documents!.EditNoteAsync(
				actor, command.InventoryId, command.ItemId, body.Value, cancellationToken );
			if ( edited.Succeeded )
			{
				projectionDelta.Observe( edited.Value );
				projectionDelta.Items.Add( command.ItemId );
			}
			return Untyped( edited );
		}
		if ( executable.Route == ExecutableItemActionRoute.RestraintIntent )
			return await SetRestraintAsync(
				actor, new HL2RPCommandArguments( command.Arguments ), projectionDelta, cancellationToken );
		if ( executable.Route == ExecutableItemActionRoute.CombatFireIntent )
			return await FirePistolAsync(
				actor, command.InventoryId, command.ItemId, projectionDelta, cancellationToken );
		var executed = await _services.ItemActions.ExecuteCommittedAsync(
			actor,
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

	internal async ValueTask<OperationResult> DropAsync(
		InventoryActor actor,
		DropItemCommand command,
		WorldTransformRecord transform,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		if ( !IsFinite( transform ) )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Authoritative drop transform is not finite." );
		var result = await _services.WorldItems.DropCommittedAsync(
			actor, command.SourceId, command.ItemId, transform, cancellationToken );
		if ( result.Succeeded )
		{
			projectionDelta.Observe( result.Value );
			try { _services.Bags?.ItemMoved( command.ItemId ); }
			catch ( Exception exception )
			{
				_services.Warn( $"HL2RP_DROP_DEGRADED item={command.ItemId.Value:D} " +
					$"stage=bag_invalidation message={exception.Message}" );
			}
			var reconciled = await _services.WorldReconciler.ReconcileCommittedAsync(
				command.ItemId,
				CancellationToken.None );
			LogWorldItemReconciliation( reconciled, "drop" );
			return WorldItemCommandResult( reconciled );
		}
		return Untyped( result );
	}

	internal async ValueTask<OperationResult> PickupAsync(
		InventoryActor actor,
		PickUpItemCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var result = await _services.WorldItems.PickUpCommittedAsync(
			actor, command.ItemId, command.DestinationId, cancellationToken );
		if ( result.Succeeded )
		{
			projectionDelta.Observe( result.Value );
			try { _services.Bags?.ItemMoved( command.ItemId ); }
			catch ( Exception exception )
			{
				_services.Warn( $"HL2RP_PICKUP_DEGRADED item={command.ItemId.Value:D} " +
					$"stage=bag_invalidation message={exception.Message}" );
			}
			var reconciled = await _services.WorldReconciler.ReconcileCommittedAsync(
				command.ItemId,
				CancellationToken.None );
			LogWorldItemReconciliation( reconciled, "pickup" );
			return WorldItemCommandResult( reconciled );
		}
		return Untyped( result );
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

	private void LogWorldItemReconciliation( WorldItemReconciliationReceipt receipt, string stage )
	{
		if ( receipt.Disposition != WorldItemReconciliationDisposition.Applied )
			_services.Warn(
				$"HL2RP_WORLD_ITEM_DEGRADED item={receipt.ItemId.Value:D} stage={stage} " +
				$"disposition={receipt.Disposition} attempt={receipt.Attempt} " +
				$"code={receipt.Error?.Code} message={receipt.Error?.Message}" );
	}

	internal OperationResult SendChat( InventoryActor actor, SendChatCommand command )
	{
		if ( command.ChannelId == HL2RPIds.Channels.Request )
			return OperationResult.Failure(
				ErrorCode.PolicyDenied, "Request traffic requires a validated request-device action." );
		var character = _services.Repositories.Characters.Find( DomainKeys.Character( actor.CharacterId ) )?.Value;
		if ( character is null ) return OperationResult.Failure( ErrorCode.NotFound, "Chat author is unavailable." );
		var result = _services.Chat!.Send( actor, character, command.ChannelId, command.Text );
		if ( result.Failed ) return Failure( result.Error! );
		var author = _services.Repositories.Characters.Find( DomainKeys.Character( result.Value.AuthorCharacterId ) )?.Value;
		if ( author is null ) return OperationResult.Failure( ErrorCode.NotFound, "Chat author is unavailable." );
		_host.DeliverCommittedChat( result.Value, author );
		return OperationResult.Success();
	}

	internal async ValueTask<OperationResult> BeginInteractionAsync(
		InventoryActor actor,
		InteractionTargetInput input,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var target = ToTarget( input );
		if ( target.Failed ) return Failure( target.Error! );
		if ( target.Value.Kind == InteractionTargetKind.SceneEntity )
		{
			var targetId = new SceneEntityId( target.Value.Id );
			if ( _host.TryClassifySceneFeature( targetId, out var scannerFeature ) )
			{
				if ( scannerFeature!.Kind == HL2RPSceneFeatureKind.ScannerDock )
				{
					if ( scannerFeature.LinkedDroneId is not SceneEntityId droneId )
						return OperationResult.Failure( ErrorCode.ConfigurationInvalid, "Scanner dock has no linked drone." );
					return await EnterScannerTargetAsync(
						actor, droneId, projectionDelta, cancellationToken );
				}
				if ( scannerFeature.Kind == HL2RPSceneFeatureKind.ScannerDrone )
					return await EnterScannerTargetAsync(
						actor, targetId, projectionDelta, cancellationToken );
			}
		}
		var opened = _services.Interactions!.Begin(
			actor.ConnectionId, actor.AccountId, actor.CharacterId, target.Value );
		if ( opened.Failed ) return Failure( opened.Error! );
		if ( opened.Value.Session is InteractionSession observedSession )
			_services.ProjectionIndex.ObserveSceneSession( observedSession );
		if ( target.Value.Kind != InteractionTargetKind.SceneEntity ) return OperationResult.Success();
		var sceneId = new SceneEntityId( target.Value.Id );
		if ( !_host.TryClassifySceneFeature( sceneId, out var feature ) )
			return OperationResult.Failure( ErrorCode.NotFound, "Scene feature is unavailable." );
		if ( feature!.Kind == HL2RPSceneFeatureKind.Forcefield )
		{
			var toggled = await _services.SceneBehavior!.ToggleForcefieldAsync(
				actor, sceneId, cancellationToken );
			if ( toggled.Succeeded )
			{
				projectionDelta.Observe( toggled.Value );
				projectionDelta.SceneEntities.Add( toggled.Value.SceneEntityId );
			}
			return Untyped( toggled );
		}
		if ( opened.Value.Session is not InteractionSession session ) return OperationResult.Success();
		if ( feature.Kind == HL2RPSceneFeatureKind.Machine )
		{
			var main = _services.MainInventory( actor.CharacterId );
			if ( main is null )
				return OperationResult.Failure( ErrorCode.NotFound, "Character main inventory was not found." );
			var purchased = await _services.Commerce!.PurchaseFromMachineAsync(
				actor, session.Id, main.Id, cancellationToken );
			if ( purchased.Succeeded )
			{
				projectionDelta.Observe( purchased.Value );
				projectionDelta.SceneEntities.Add( purchased.Value.SceneEntityId );
				projectionDelta.Inventories.Add( main.Id );
				projectionDelta.Items.UnionWith( purchased.Value.ItemIds );
			}
			return Untyped( purchased );
		}
		if ( feature.Kind == HL2RPSceneFeatureKind.Door )
		{
			var toggled = await _services.SceneBehavior!.ToggleDoorAsync(
				actor, session.Id, cancellationToken );
			if ( toggled.Succeeded )
			{
				projectionDelta.Observe( toggled.Value );
				projectionDelta.SceneEntities.Add( toggled.Value.SceneEntityId );
			}
			return Untyped( toggled );
		}
		return OperationResult.Success();
	}

	internal OperationResult ContinueInteraction( InventoryActor actor, ContinueInteractionCommand command )
	{
		var target = ToTarget( command.Target );
		if ( target.Failed ) return Failure( target.Error! );
		return Untyped( _services.Interactions!.Continue(
			command.SessionId, actor.ConnectionId, actor.AccountId,
			actor.CharacterId, target.Value ) );
	}

	internal OperationResult CloseInteraction( InventoryActor actor, InteractionSessionId sessionId )
	{
		var session = _services.Sessions!.ActiveSessions.SingleOrDefault( value => value.Id == sessionId );
		if ( session is null || session.ConnectionId != actor.ConnectionId || session.CharacterId != actor.CharacterId )
			return OperationResult.Failure( ErrorCode.Unauthorized, "Interaction session is not bound to the actor." );
		_services.Interactions!.Close( sessionId );
		return OperationResult.Success();
	}

	internal OperationResult CancelAction( InventoryActor actor, Guid instanceId )
	{
		var restraintCancellation = _services.ActiveRestraintActions.TryCancel(
			actor.ConnectionId,
			candidate => candidate.Ticket.TicketId.Value == instanceId && candidate.Actor == actor,
			out var action );
		if ( restraintCancellation == HL2RPTimedActionCancelOutcome.Cancelled )
		{
			CancelToken( action!.Cancellation );
			var result = _services.Restraints!.Cancel( action.Ticket.TicketId, action.Actor );
			_host.PublishConnection( actor.ConnectionId );
			return result;
		}
		if ( restraintCancellation == HL2RPTimedActionCancelOutcome.CommitOwned )
			return OperationResult.Failure( ErrorCode.Conflict,
				"Action commit is already in progress and can no longer be cancelled." );
		var pistolCancellation = _services.ActivePistolActions.TryCancel(
			actor.ConnectionId,
			candidate => candidate.InstanceId == instanceId && candidate.Actor == actor,
			out var pistol );
		if ( pistolCancellation == HL2RPTimedActionCancelOutcome.Cancelled )
		{
			CancelToken( pistol!.Cancellation );
			_services.CombatIntent!.ClearCharacter( actor.CharacterId );
			_host.PublishConnection( actor.ConnectionId );
			return OperationResult.Success();
		}
		if ( pistolCancellation == HL2RPTimedActionCancelOutcome.CommitOwned )
			return OperationResult.Failure( ErrorCode.Conflict,
				"Action commit is already in progress and can no longer be cancelled." );
		return OperationResult.Failure( ErrorCode.Unauthorized, "Action instance is not bound to the actor." );
	}

	internal async ValueTask<OperationResult> FirePistolAsync(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId pistolId,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var raised = _services.Pistol!.IsHostRaised( actor, inventoryId, pistolId );
		if ( raised.Failed ) return Failure( raised.Error! );
		CancellationTokenSource? linked = null;
		ActivePistolRaiseAction? active = null;
		var durableSuccess = false;
		if ( !raised.Value )
		{
			linked = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
			active = new ActivePistolRaiseAction(
				actor, Guid.NewGuid(), _services.Clock.UtcNow + PistolCombatService.DefaultRaiseDelay, linked );
			if ( !_services.ActivePistolActions.TryAdd( actor.ConnectionId, active ) )
			{
				linked.Dispose();
				return OperationResult.Failure( ErrorCode.Conflict, "Another pistol raise is active." );
			}
			_host.PublishConnection( actor.ConnectionId );
		}
		OperationResult result;
		try
		{
			var fired = await _services.CombatIntent!.FireAsync(
				new CombatFireIntent( actor, inventoryId, pistolId, () =>
					active is null || _services.ActivePistolActions.TryClaimCommit( actor.ConnectionId, active ) ),
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
						_services.Warn(
							$"HL2RP_DEATH_BOUNDARY_DEGRADED character={death.Respawn.CharacterId.Value:D} " +
							$"code={boundaryError.Code} message={boundaryError.Message}" );
				}
				if ( fired.Value.DegradedDeathTransition is OperationError degraded )
					_services.Warn(
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
			_services.Report( exception, $"HL2RP pistol fire failed for character {actor.CharacterId.Value:D}." );
			result = OperationResult.Failure(
				ErrorCode.InternalError,
				"Pistol firing failed unexpectedly." );
		}

		HL2RPTimedActionCompletion completion;
		try
		{
			completion = active is null
				? default
				: _services.ActivePistolActions.Complete( actor.ConnectionId, active, durableSuccess );
		}
		finally
		{
			linked?.Dispose();
		}
		if ( completion.PublishFailure )
			_host.PublishConnection( actor.ConnectionId );
		if ( active is not null && completion.LifecycleCleanupRequested )
		{
			var cleanup = await ClearRaisedPistolsAsync( active.Actor, projectionDelta, CancellationToken.None );
			if ( cleanup.Failed )
				_services.Fail(
					$"HL2RP late pistol lifecycle cleanup failed for character {active.Actor.CharacterId.Value:D}: " +
					cleanup.Error!.Message );
		}
		return result;
	}

	internal async ValueTask<OperationResult> ClearRaisedPistolsAsync(
		InventoryActor actor,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		if ( _services.CombatIntent is null ) return OperationResult.Success();
		var cleared = await _services.CombatIntent.ClearCharacterAsync( actor.CharacterId, cancellationToken );
		if ( cleared.Failed ) return Failure( cleared.Error! );
		if ( cleared.Value.Commit is not null )
		{
			projectionDelta.Observe( cleared.Value.Commit );
			projectionDelta.Connections.Add( actor.ConnectionId );
			projectionDelta.Characters.Add( actor.CharacterId );
			projectionDelta.Items.UnionWith( cleared.Value.ChangedPistols );
			var main = _services.MainInventory( actor.CharacterId );
			if ( main is not null ) projectionDelta.Inventories.Add( main.Id );
		}
		return OperationResult.Success();
	}

	internal async Task ClearRaisedPistolsForLifecycleAsync(
		InventoryActor actor,
		CancellationToken cancellationToken )
	{
		var projectionDelta = new CommandProjectionDelta();
		var cleared = await ClearRaisedPistolsAsync( actor, projectionDelta, cancellationToken );
		if ( cleared.Failed )
		{
			_services.Fail(
				$"HL2RP pistol lifecycle cleanup failed for character {actor.CharacterId.Value:D}: " +
				cleared.Error!.Message );
			return;
		}
		if ( projectionDelta.Receipts.Count == 0 ) return;
		_host.PublishChanges(
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

	internal async ValueTask<OperationResult> RunEntitlementCommandAsync(
		ConnectionId connectionId,
		AccountId accountId,
		CharacterId? characterId,
		RunSchemaCommandCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var administrator = new HL2RPEntitlementAdministrator( accountId, characterId );
		if ( !_services.CanManageEntitlements( administrator ) )
			return OperationResult.Failure(
				ErrorCode.Unauthorized, "Authenticated account cannot manage entitlements." );
		if ( !HL2RPPresentationPlanner.TryAccountId( command.Arguments, "account", out var targetAccountId ) )
			return OperationResult.Failure(
				ErrorCode.InvalidArgument, "Target account must be a non-zero unsigned account ID." );
		if ( command.CommandId == HL2RPIds.Commands.EntitlementQuery )
		{
			var observed = _services.Entitlements.Observe( targetAccountId );
			if ( observed.Failed ) return Failure( observed.Error! );
			if ( !observed.Value.IsPersisted && !_services.IsKnownAccount( targetAccountId ) )
				return OperationResult.Failure( ErrorCode.NotFound, "Target account is not known to this host." );
			_services.EntitlementQueries[connectionId] = targetAccountId;
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
			? await _services.Entitlements.GrantAsync(
				administrator, targetAccountId, flag.Value, expectedRevision, cancellationToken )
			: await _services.Entitlements.RevokeAsync(
				administrator, targetAccountId, flag.Value, expectedRevision, cancellationToken );
		if ( changed.Failed ) return Failure( changed.Error! );
		projectionDelta.Observe( changed.Value );
		_services.EntitlementQueries[connectionId] = targetAccountId;
		projectionDelta.Connections.UnionWith(
			_services.EntitlementPresentationInvalidation.Claim( targetAccountId ) );
		return OperationResult.Success();
	}

	internal OperationResult PublishAdministrationAudit( InventoryActor actor )
	{
		_services.Audit.Publish( new AdminAuditFact
		{
			ActorAccountId = actor.AccountId,
			ActorCharacterId = actor.CharacterId,
			Operation = HL2RPFeatureOperation.AdministrationAudit,
			Target = $"character:{actor.CharacterId.Value:D}",
			OccurredAtUtc = _services.Clock.UtcNow,
			CommitSequence = _services.Provider.Health.Sequence
		} );
		return OperationResult.Success();
	}

	internal OperationResult CivicData( InventoryActor actor, HL2RPCommandArguments arguments )
	{
		var target = arguments.OptionalGuid( "character" );
		if ( target.Failed ) return Failure( target.Error! );
		var subjectId = target.Value is Guid rawId ? new CharacterId( rawId ) : actor.CharacterId;
		var read = _services.Civic!.Read( subjectId );
		if ( read.Failed ) return Failure( read.Error! );
		if ( target.Value is null ) _services.CivicSubjects.ClearConnection( actor.ConnectionId );
		else _services.CivicSubjects.Select( actor.ConnectionId, subjectId );
		return OperationResult.Success();
	}

	internal async ValueTask<OperationResult> SetObjectivesAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var routed = await _services.ObjectiveRouter!.RouteAsync( actor, args, cancellationToken );
		if ( routed.Receipt is not null )
		{
			projectionDelta.Observe( routed.Receipt.ProjectionReceipt );
			projectionDelta.Documents.UnionWith(
				routed.Receipt.ProjectionReceipt.Documents.Select( value => value.Address ) );
		}
		return routed.Command.Result;
	}

	internal async ValueTask<OperationResult> SetPriorityAsync(
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
		var updated = await _services.Civic!.UpdateRecordAsync(
			actor, new CharacterId( target.Value ), parsed, record.Value, cancellationToken );
		if ( updated.Succeeded ) projectionDelta.Observe( updated.Value );
		return Untyped( updated );
	}

	internal async ValueTask<OperationResult> TuneRadioAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var item = args.Guid( "item" );
		var frequency = args.String( "frequency" );
		var enabled = args.Boolean( "enabled" );
		var main = _services.MainInventory( actor.CharacterId );
		if ( item.Failed || frequency.Failed || enabled.Failed || main is null ) return OperationResult.Failure( ErrorCode.InvalidArgument, "Radio arguments are invalid." );
		var tuned = await _services.Radio!.ConfigureAsync(
			actor, main.Id, new ItemId( item.Value ), frequency.Value, enabled.Value, cancellationToken );
		if ( tuned.Succeeded )
		{
			projectionDelta.Observe( tuned.Value );
			projectionDelta.Inventories.Add( main.Id );
			projectionDelta.Items.Add( new ItemId( item.Value ) );
		}
		return Untyped( tuned );
	}

	internal async ValueTask<OperationResult> IntroduceAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var target = args.Guid( "character" );
		if ( target.Failed ) return Failure( target.Error! );
		var introduced = await _services.Recognition!.IntroduceAsync(
			actor, new CharacterId( target.Value ), cancellationToken );
		if ( introduced.Succeeded ) projectionDelta.Observe( introduced.Value );
		return Untyped( introduced );
	}

	internal async ValueTask<OperationResult> BuyAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var session = args.Guid( "session" );
		var definition = args.String( "definition" );
		var quantity = args.Integer( "quantity" );
		var main = _services.MainInventory( actor.CharacterId );
		if ( session.Failed || definition.Failed || quantity.Failed || quantity.Value is < 1 or > 64 || main is null )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Vendor purchase arguments are invalid." );
		var purchased = await _services.Commerce!.BuyAsync(
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

	internal async ValueTask<OperationResult> SellAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var session = args.Guid( "session" );
		var inventory = args.Guid( "inventory" );
		var item = args.Guid( "item" );
		if ( session.Failed || inventory.Failed || item.Failed ) return OperationResult.Failure( ErrorCode.InvalidArgument, "Vendor sale arguments are invalid." );
		var result = await _services.Commerce!.SellAsync(
			actor, new InteractionSessionId( session.Value ), new InventoryId( inventory.Value ), new ItemId( item.Value ), cancellationToken );
		if ( result.Succeeded )
		{
			projectionDelta.Observe( result.Value );
			projectionDelta.SceneEntities.Add( result.Value.SceneEntityId );
			_services.Bags?.ItemMoved( new ItemId( item.Value ) );
			projectionDelta.Inventories.Add( new InventoryId( inventory.Value ) );
			projectionDelta.Items.UnionWith( result.Value.ItemIds );
		}
		return Untyped( result );
	}

	internal async ValueTask<OperationResult> PurchasePermitAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var kind = args.String( "permit" );
		var main = _services.MainInventory( actor.CharacterId );
		if ( kind.Failed || main is null ) return OperationResult.Failure( ErrorCode.InvalidArgument, "Permit purchase arguments are invalid." );
		var parsed = HL2RPPresentationContracts.ParsePermitKind( kind.Value );
		if ( parsed.Failed ) return Failure( parsed.Error! );
		var purchased = await _services.PermitPurchases!.PurchaseAsync(
			actor, main.Id, parsed.Value, cancellationToken );
		if ( purchased.Succeeded )
		{
			projectionDelta.Observe( purchased.Value );
			projectionDelta.Inventories.Add( main.Id );
			projectionDelta.Items.Add( purchased.Value.PermitItemId );
		}
		return Untyped( purchased );
	}

	internal async ValueTask<OperationResult> WriteNoteAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var item = args.Guid( "item" );
		var body = args.String( "body", true );
		var main = _services.MainInventory( actor.CharacterId );
		if ( item.Failed || body.Failed || main is null ) return OperationResult.Failure( ErrorCode.InvalidArgument, "Note arguments are invalid." );
		var edited = await _services.Documents!.EditNoteAsync(
			actor, main.Id, new ItemId( item.Value ), body.Value, cancellationToken );
		if ( edited.Succeeded )
		{
			projectionDelta.Observe( edited.Value );
			projectionDelta.Inventories.Add( main.Id );
			projectionDelta.Items.Add( edited.Value.NoteItemId );
		}
		return Untyped( edited );
	}

	internal async ValueTask<OperationResult> SetRestraintAsync(
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
			var targetInventory = _services.MainInventory( targetId );
			if ( targetInventory is null )
				return OperationResult.Failure( ErrorCode.NotFound, "Search target main inventory was not found." );
			var opened = _services.Search!.Open( actor, targetId, targetInventory.Id );
			if ( opened.Succeeded ) projectionDelta.Inventories.Add( targetInventory.Id );
			return Untyped( opened );
		}
		if ( !restrain.Value )
		{
			var released = await _services.Restraints!.UnrestrainAsync( actor, targetId, cancellationToken );
			if ( released.Succeeded ) projectionDelta.Observe( released.Value );
			return Untyped( released );
		}
		var main = _services.MainInventory( actor.CharacterId );
		var zip = main?.Placements.Select( value => _services.Repositories.Items.Find( DomainKeys.Item( value.ItemId ) )?.Value )
			.FirstOrDefault( value => value?.Definition.Value == HL2RPIds.Items.ZipTie );
		if ( main is null || zip is null ) return OperationResult.Failure( ErrorCode.NotFound, "A zip tie is required." );
		var ticket = _services.Restraints!.Begin( actor, targetId, main.Id, zip.Id );
		if ( ticket.Failed ) return Failure( ticket.Error! );
		var linked = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
		var active = new ActiveRestraintAction( actor, ticket.Value, linked );
		var durableSuccess = false;
		var cancelledByCommand = false;
		if ( !_services.ActiveRestraintActions.TryAdd( actor.ConnectionId, active ) )
		{
			linked.Dispose();
			_services.Restraints.Cancel( ticket.Value.TicketId, actor );
			return OperationResult.Failure( ErrorCode.Conflict, "Another restraint action is already active." );
		}
		_host.PublishConnection( actor.ConnectionId );
		try
		{
			var delay = ticket.Value.CompletesAtUtc - _services.Clock.UtcNow;
			if ( delay > TimeSpan.Zero ) await Task.Delay( delay, linked.Token );
			if ( !_services.ActiveRestraintActions.TryClaimCommit( actor.ConnectionId, active ) )
				return OperationResult.Failure( ErrorCode.Conflict, "Restraint action was cancelled." );
			var completed = await _services.Restraints.CompleteAsync(
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
			var cancelled = _services.ActiveRestraintActions.TryCancel(
				actor.ConnectionId, candidate => ReferenceEquals( candidate, active ), out _ );
			if ( cancelled == HL2RPTimedActionCancelOutcome.Cancelled )
			{
				cancelledByCommand = true;
				_ = _services.Restraints.Cancel( ticket.Value.TicketId, actor );
			}
			return OperationResult.Failure( ErrorCode.Conflict, "Restraint action was cancelled." );
		}
		finally
		{
			var completion = _services.ActiveRestraintActions.Complete( actor.ConnectionId, active, durableSuccess );
			linked.Dispose();
			if ( cancelledByCommand || completion.PublishFailure )
				_host.PublishConnection( actor.ConnectionId );
		}
	}

	internal async ValueTask<OperationResult> ScannerIntentAsync(
		InventoryActor actor,
		HL2RPCommandArguments args,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var intent = args.String( "intent" );
		if ( intent.Failed ) return Failure( intent.Error! );
		var sessionId = args.Guid( "session" );
		var active = sessionId.Succeeded
			? _services.Scanner!.ActiveSessions.SingleOrDefault( value => value.SessionId.Value == sessionId.Value )
			: null;
		return intent.Value switch
		{
			"enter" => await EnterScannerAsync( actor, projectionDelta, cancellationToken ),
			"exit" when active is not null => await _services.Scanner!.ExitAsync( actor, active.SessionId, cancellationToken ),
			"spotlight" when active is not null => await ToggleScannerSpotlightAsync(
				actor, active.SessionId, projectionDelta, cancellationToken ),
			"flash" when active is not null => _services.Scanner!.Flash( actor, active.SessionId ),
			"photo" when active is not null => await TakeScannerPhotoAsync(
				actor, active.SessionId, projectionDelta, cancellationToken ),
			"move" when active is not null => await ApplyScannerInputAsync(
				actor, active.SessionId, args, projectionDelta, cancellationToken ),
			_ => OperationResult.Failure( ErrorCode.InvalidArgument, "Scanner intent or session is invalid." )
		};
	}

	internal async ValueTask<OperationResult> ApplyScannerInputAsync(
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
		var applied = await _services.Scanner!.ApplyInputAsync( actor, new ScannerInputIntent(
			sessionId, sequence.Value, forward.Value, right.Value, up.Value, yaw.Value, pitch.Value ), cancellationToken );
		if ( applied.Succeeded )
		{
			if ( applied.Value.Commit is not null ) projectionDelta.Observe( applied.Value.Commit );
			LogScannerBoundaryError( applied.Value.BoundaryError );
		}
		return Untyped( applied );
	}

	internal async ValueTask<OperationResult> TakeScannerPhotoAsync(
		InventoryActor actor,
		InteractionSessionId sessionId,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var photo = await _services.Scanner!.TakePhotoAsync( actor, sessionId, cancellationToken );
		if ( photo.Succeeded )
		{
			projectionDelta.Observe( photo.Value );
			LogScannerBoundaryError( photo.Value.BoundaryError );
		}
		return Untyped( photo );
	}

	internal async ValueTask<OperationResult> EnterScannerAsync(
		InventoryActor actor,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var session = _services.CurrentSession( actor, InteractionSessionKind.Scanner );
		if ( session is null || session.Target.Kind != InteractionTargetKind.SceneEntity )
			return OperationResult.Failure( ErrorCode.Unauthorized, "A current scanner interaction is required." );
		var target = new SceneEntityId( session.Target.Id );
		if ( _host.TryClassifySceneFeature( target, out var feature ) &&
			feature!.Kind == HL2RPSceneFeatureKind.ScannerDock )
		{
			if ( feature.LinkedDroneId is not SceneEntityId droneId )
				return OperationResult.Failure( ErrorCode.ConfigurationInvalid, "Scanner dock has no linked drone." );
			target = droneId;
		}
		return await EnterScannerTargetAsync( actor, target, projectionDelta, cancellationToken );
	}

	internal async ValueTask<OperationResult> EnterScannerTargetAsync(
		InventoryActor actor,
		SceneEntityId target,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var entered = await _services.Scanner!.EnterAsync( actor, target, cancellationToken );
		if ( entered.Succeeded )
		{
			projectionDelta.Observe( entered.Value );
			projectionDelta.SceneEntities.Add( entered.Value.Session.ScannerId );
			LogScannerBoundaryError( entered.Value.BoundaryError );
		}
		return Untyped( entered );
	}

	internal async ValueTask<OperationResult> ToggleScannerSpotlightAsync(
		InventoryActor actor,
		InteractionSessionId sessionId,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var toggled = await _services.Scanner!.ToggleSpotlightAsync( actor, sessionId, cancellationToken );
		if ( toggled.Succeeded )
		{
			projectionDelta.Observe( toggled.Value );
			projectionDelta.SceneEntities.Add( toggled.Value.ScannerId );
			LogScannerBoundaryError( toggled.Value.BoundaryError );
		}
		return Untyped( toggled );
	}

	private void LogScannerBoundaryError( OperationError? error )
	{
		if ( error is not null )
			_services.Warn( $"HL2RP_SCANNER_BOUNDARY_DEGRADED code={error.Code} message={error.Message}" );
	}

	internal async ValueTask<OperationResult> DoorOwnershipAsync(
		InventoryActor actor,
		HL2RPCommandArguments arguments,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		var intent = arguments.String( "intent" );
		if ( intent.Failed ) return Failure( intent.Error! );
		var session = _services.CurrentSession( actor, InteractionSessionKind.Door );
		if ( session is null )
			return OperationResult.Failure( ErrorCode.Unauthorized, "A current door session is required." );
		if ( session.Target.Kind != InteractionTargetKind.SceneEntity ||
			!_host.TryClassifySceneFeature( new SceneEntityId( session.Target.Id ), out var feature ) ||
			feature is not { Kind: HL2RPSceneFeatureKind.Door, DoorOwnable: true } )
			return OperationResult.Failure( ErrorCode.PolicyDenied, "Current door does not support personal ownership." );
		OperationResult<DoorOwnershipReceipt> changed;
		if ( intent.Value == "claim" )
			changed = await _services.DoorOwnership!.ClaimAsync( actor, session.Id, cancellationToken );
		else if ( intent.Value == "release" )
			changed = await _services.DoorOwnership!.ReleaseAsync( actor, session.Id, cancellationToken );
		else return OperationResult.Failure(
			ErrorCode.InvalidArgument, "Door ownership intent must be claim or release." );
		if ( changed.Succeeded )
		{
			projectionDelta.Observe( changed.Value );
			projectionDelta.SceneEntities.Add( changed.Value.DoorEntityId );
		}
		return Untyped( changed );
	}
}
