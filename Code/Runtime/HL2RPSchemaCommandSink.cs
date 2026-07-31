#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Runtime;

/// <summary>
/// Seams behind the schema command sink. The host implements this over its engine-bound
/// actor with thin delegations to its existing handlers.
/// </summary>
public interface IHL2RPSchemaCommandRoutes<TActor>
{
	bool TryGetCommandDefinition( string commandId, out string? permissionId );
	ValueTask<OperationResult> RunEntitlementCommandAsync(
		TActor rpc, RunSchemaCommandCommand command, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	OperationResult<InventoryActor> RequireInventoryActor( TActor rpc, bool allowDead );
	bool HasPermission( InventoryActor actor, string permissionId );
	OperationResult CivicData( InventoryActor actor, HL2RPCommandArguments arguments );
	ValueTask<OperationResult> SetObjectivesAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	ValueTask<OperationResult> SetPriorityAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	ValueTask<OperationResult> TuneRadioAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	ValueTask<OperationResult> IntroduceAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	ValueTask<OperationResult> DoorOwnershipAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	OperationResult PublishAdministrationAudit( InventoryActor actor );
	ValueTask<OperationResult> BuyAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	ValueTask<OperationResult> SellAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	ValueTask<OperationResult> PurchasePermitAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	ValueTask<OperationResult> WriteNoteAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	ValueTask<OperationResult> SetRestraintAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	ValueTask<OperationResult> ScannerIntentAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
	OperationResult RespawnCharacter( InventoryActor actor );
	ValueTask<OperationResult> KillCharacterAsync(
		InventoryActor actor, HL2RPCommandArguments arguments, CommandProjectionDelta projectionDelta, CancellationToken cancellationToken );
}

/// <summary>
/// Engine-neutral core of the schema command pipeline: definition lookup, the
/// entitlement dispatch that authenticates by RPC account and must run before the
/// active-character requirement, the character requirement itself (dead actors allowed
/// only for respawn), the fail-closed permission gate, and the exhaustive routing table.
/// </summary>
public static class HL2RPSchemaCommandSink
{
	public static async ValueTask<OperationResult> RouteAsync<TActor>(
		IHL2RPSchemaCommandRoutes<TActor> routes,
		TActor rpc,
		RunSchemaCommandCommand command,
		CommandProjectionDelta projectionDelta,
		CancellationToken cancellationToken )
	{
		if ( !routes.TryGetCommandDefinition( command.CommandId, out var permissionId ) )
			return OperationResult.Failure( ErrorCode.UnknownDefinition, "Schema command is not registered." );
		// Entitlement administration authenticates by the RPC account (an administrator
		// need not have an active character), so it dispatches before the character
		// requirement below.
		if ( command.CommandId is HL2RPIds.Commands.EntitlementQuery or
			HL2RPIds.Commands.EntitlementGrant or HL2RPIds.Commands.EntitlementRevoke )
			return await routes.RunEntitlementCommandAsync(
				rpc, command, projectionDelta, cancellationToken );
		var actor = routes.RequireInventoryActor(
			rpc, allowDead: command.CommandId == HL2RPIds.Commands.CombatRespawn );
		if ( actor.Failed )
			return OperationResult.Failure( actor.Error!.Code, actor.Error.Message, actor.Error.Details );
		if ( permissionId is not null &&
			!routes.HasPermission( actor.Value, permissionId ) )
			return OperationResult.Failure( ErrorCode.Unauthorized, $"Permission '{permissionId}' is required." );
		var arguments = new HL2RPCommandArguments( command.Arguments );
		return command.CommandId switch
		{
			HL2RPIds.Commands.CivicData => routes.CivicData( actor.Value, arguments ),
			HL2RPIds.Commands.CityObjectives => await routes.SetObjectivesAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.Priority => await routes.SetPriorityAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.RadioFrequency => await routes.TuneRadioAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.Introduce => await routes.IntroduceAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.DoorOwnership => await routes.DoorOwnershipAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.AdministrationAudit => routes.PublishAdministrationAudit( actor.Value ),
			HL2RPIds.Commands.CommerceBuy => await routes.BuyAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.CommerceSell => await routes.SellAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.PermitPurchase => await routes.PurchasePermitAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.NoteWrite => await routes.WriteNoteAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.RestraintSet => await routes.SetRestraintAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.ScannerIntent => await routes.ScannerIntentAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			HL2RPIds.Commands.CombatRespawn => routes.RespawnCharacter( actor.Value ),
			HL2RPIds.Commands.AdministrationKill => await routes.KillCharacterAsync(
				actor.Value, arguments, projectionDelta, cancellationToken ),
			_ => OperationResult.Failure( ErrorCode.UnknownDefinition, "Schema command has no HL2RP runtime handler." )
		};
	}
}
