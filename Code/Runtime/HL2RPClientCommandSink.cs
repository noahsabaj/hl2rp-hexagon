#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;

namespace HL2RP.V2.Runtime;

/// <summary>
/// Per-command routes behind the client command sink. The host implements this over its
/// engine-bound actor with thin delegations to its existing handlers, so the admission
/// guard and the complete routing table compile and run in the neutral test suite.
/// </summary>
public interface IHL2RPClientCommandRoutes<TActor>
{
	OperationResult ListCharacters( TActor actor );
	ValueTask<OperationResult> CreateAsync(
		TActor actor, CreateCharacterCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	ValueTask<OperationResult> LoadAsync(
		TActor actor, CharacterId characterId, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	ValueTask<OperationResult> DeleteAsync(
		TActor actor, CharacterId characterId, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	ValueTask<OperationResult> UnloadAsync(
		TActor actor, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	ValueTask<OperationResult> MoveAsync(
		TActor actor, MoveInventoryItemCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	ValueTask<OperationResult> RunItemActionAsync(
		TActor actor, RunItemActionCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	ValueTask<OperationResult> DropAsync(
		TActor actor, DropItemCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	ValueTask<OperationResult> PickupAsync(
		TActor actor, PickUpItemCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	OperationResult SendChat( TActor actor, SendChatCommand command );
	OperationResult CancelAction( TActor actor, Guid instanceId );
	ValueTask<OperationResult> BeginInteractionAsync(
		TActor actor, InteractionTargetInput target, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	OperationResult ContinueInteraction( TActor actor, ContinueInteractionCommand command );
	OperationResult CloseInteraction( TActor actor, InteractionSessionId sessionId );
	ValueTask<OperationResult> RunSchemaCommandAsync(
		TActor actor, RunSchemaCommandCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
}

/// <summary>
/// Engine-neutral core of the single HL2RP command sink: the disposal gate, the
/// cross-account binding guard, and the exhaustive command routing table. The host's
/// HandleCommandAsync is a thin adapter around <see cref="Admit"/> and
/// <see cref="RouteAsync"/> plus its presentation epilogue.
/// </summary>
public static class HL2RPClientCommandSink
{
	public static OperationResult Admit(
		bool disposed,
		bool bound,
		AccountId boundAccountId,
		AccountId actorAccountId )
	{
		if ( disposed )
			return OperationResult.Failure( ErrorCode.Conflict, "HL2RP host is disposed." );
		// Defense in depth over the engine-derived caller identity: a connection that
		// never registered, or whose binding belongs to another account, executes nothing.
		if ( !bound || boundAccountId != actorAccountId )
			return OperationResult.Failure( ErrorCode.Unauthorized, "RPC actor is not bound to this host scope." );
		return OperationResult.Success();
	}

	public static async ValueTask<OperationResult> RouteAsync<TActor>(
		IHL2RPClientCommandRoutes<TActor> routes,
		TActor actor,
		ClientCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		ArgumentNullException.ThrowIfNull( routes );
		ArgumentNullException.ThrowIfNull( command );
		return command switch
		{
			RequestCharacterListCommand => routes.ListCharacters( actor ),
			CreateCharacterCommand create => await routes.CreateAsync( actor, create, projectionDelta, cancellationToken ),
			LoadCharacterCommand load => await routes.LoadAsync( actor, load.CharacterId, projectionDelta, cancellationToken ),
			DeleteCharacterCommand delete => await routes.DeleteAsync( actor, delete.CharacterId, projectionDelta, cancellationToken ),
			UnloadCharacterCommand => await routes.UnloadAsync( actor, projectionDelta, cancellationToken ),
			MoveInventoryItemCommand move => await routes.MoveAsync( actor, move, projectionDelta, cancellationToken ),
			RunItemActionCommand action => await routes.RunItemActionAsync( actor, action, projectionDelta, cancellationToken ),
			DropItemCommand drop => await routes.DropAsync( actor, drop, projectionDelta, cancellationToken ),
			PickUpItemCommand pickup => await routes.PickupAsync( actor, pickup, projectionDelta, cancellationToken ),
			SendChatCommand chat => routes.SendChat( actor, chat ),
			CancelActionCommand cancel => routes.CancelAction( actor, cancel.InstanceId ),
			BeginInteractionCommand begin => await routes.BeginInteractionAsync( actor, begin.Target, projectionDelta, cancellationToken ),
			ContinueInteractionCommand continuation => routes.ContinueInteraction( actor, continuation ),
			CloseInteractionCommand close => routes.CloseInteraction( actor, close.SessionId ),
			RunSchemaCommandCommand schema => await routes.RunSchemaCommandAsync( actor, schema, projectionDelta, cancellationToken ),
			_ => OperationResult.Failure( ErrorCode.UnknownDefinition, "Client command is not registered by HL2RP." )
		};
	}
}
