#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Hexagon.V2.Domain;
using Hexagon.V2.Networking;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Runtime;

public sealed record HL2RPPresentationChangeSet
{
	public static HL2RPPresentationChangeSet None { get; } = new();
	public IReadOnlyList<ConnectionId> Connections { get; init; } = Array.Empty<ConnectionId>();
	public IReadOnlyList<AccountId> Accounts { get; init; } = Array.Empty<AccountId>();
	public IReadOnlyList<CharacterId> Characters { get; init; } = Array.Empty<CharacterId>();
	public IReadOnlyList<InventoryId> Inventories { get; init; } = Array.Empty<InventoryId>();
	public IReadOnlyList<ItemId> Items { get; init; } = Array.Empty<ItemId>();
	public IReadOnlyList<SceneEntityId> SceneEntities { get; init; } = Array.Empty<SceneEntityId>();
	public IReadOnlyList<Hexagon.V2.Persistence.DocumentAddress> Documents { get; init; } =
		Array.Empty<Hexagon.V2.Persistence.DocumentAddress>();
	public bool Broadcast { get; init; }
	public bool RebuildLiveInventory { get; init; }
	public bool RebuildLiveConnections { get; init; }
	public bool RebuildCombatTargets { get; init; }

	public bool IsEmpty => !Broadcast && !RebuildLiveInventory &&
		!RebuildLiveConnections && !RebuildCombatTargets && Connections.Count == 0 && Accounts.Count == 0 &&
		Characters.Count == 0 && Inventories.Count == 0 && Items.Count == 0 &&
		SceneEntities.Count == 0 && Documents.Count == 0;
}

public sealed record HL2RPCommandOutcome( OperationResult Result, HL2RPPresentationChangeSet Changes );

/// <summary>
/// Converts observed command effects into presentation work. Durable mutation and
/// roster-change flags come from the executed handler, so a successful no-op is silent.
/// </summary>
public static class HL2RPPresentationPlanner
{
	public static HL2RPCommandOutcome Outcome(
		ConnectionId actor,
		ClientCommand command,
		OperationResult result,
		bool durableMutation = false,
		bool rosterChanged = false ) => new(
		result,
		result.Succeeded
			? ForObservedEffect( actor, command, durableMutation, rosterChanged )
			: HL2RPPresentationChangeSet.None );

	public static HL2RPPresentationChangeSet ForSuccessfulCommand(
		ConnectionId actor,
		ClientCommand command ) => ForObservedEffect( actor, command, durableMutation: true, rosterChanged: false );

	public static HL2RPPresentationChangeSet ForObservedEffect(
		ConnectionId actor,
		ClientCommand command,
		bool durableMutation,
		bool rosterChanged )
	{
		if ( command is RequestCharacterListCommand or SendChatCommand or ContinueInteractionCommand or
			CloseInteractionCommand or CancelActionCommand ) return HL2RPPresentationChangeSet.None;
		if ( rosterChanged )
			return new HL2RPPresentationChangeSet
			{
				Connections = new[] { actor },
				Broadcast = true,
				RebuildLiveInventory = durableMutation && command is DeleteCharacterCommand,
				RebuildLiveConnections = true,
				RebuildCombatTargets = true
			};
		if ( command is BeginInteractionCommand )
			return new HL2RPPresentationChangeSet { Connections = new[] { actor } };
		if ( !durableMutation )
		{
			if ( command is RunItemActionCommand action && action.ActionId.Value is
				HL2RPIds.Actions.Show or HL2RPIds.Actions.Read or HL2RPIds.Actions.OpenBag or
				HL2RPIds.Actions.PresentPermit )
				return new HL2RPPresentationChangeSet { Connections = new[] { actor } };
			if ( command is RunSchemaCommandCommand schema && schema.CommandId is
				HL2RPIds.Commands.CivicData or HL2RPIds.Commands.EntitlementQuery or
				HL2RPIds.Commands.RestraintSet )
				return new HL2RPPresentationChangeSet { Connections = new[] { actor } };
			return HL2RPPresentationChangeSet.None;
		}

		return command switch
		{
			MoveInventoryItemCommand move => InventoryMutation( actor, new[] { move.SourceId, move.TargetId }, Array.Empty<ItemId>() ),
			DropItemCommand drop => InventoryMutation( actor, new[] { drop.SourceId }, Array.Empty<ItemId>() ),
			PickUpItemCommand pickup => InventoryMutation( actor, new[] { pickup.DestinationId }, Array.Empty<ItemId>() ),
			RunItemActionCommand action => InventoryMutation( actor, new[] { action.InventoryId }, new[] { action.ItemId } ),
			CreateCharacterCommand or DeleteCharacterCommand => new()
			{
				Connections = new[] { actor }, RebuildLiveInventory = true
			},
			LoadCharacterCommand => new() { Connections = new[] { actor }, RebuildLiveConnections = true, RebuildCombatTargets = true },
			RunSchemaCommandCommand schema => Schema( actor, schema ),
			_ => new HL2RPPresentationChangeSet { Connections = new[] { actor } }
		};
	}

	private static HL2RPPresentationChangeSet Schema( ConnectionId actor, RunSchemaCommandCommand command )
	{
		if ( command.CommandId == HL2RPIds.Commands.AdministrationAudit ) return HL2RPPresentationChangeSet.None;
		if ( command.CommandId == HL2RPIds.Commands.CityObjectives ) return new() { Broadcast = true };
		if ( command.CommandId == HL2RPIds.Commands.CombatRespawn )
			return new() { Broadcast = true, Connections = new[] { actor }, RebuildCombatTargets = true };
		if ( command.CommandId == HL2RPIds.Commands.ScannerIntent &&
			command.Arguments.TryGetValue( "intent", out var intent ) &&
			intent.Kind == SnapshotValueKind.String && intent.StringValue == "move" )
			return HL2RPPresentationChangeSet.None;

		var characters = CharacterArgument( command );
		var accounts = AccountArgument( command );
		var inventories = InventoryArguments( command );
		var items = ItemArguments( command );
		var inventoryMutation = command.CommandId is
			HL2RPIds.Commands.CommerceBuy or HL2RPIds.Commands.CommerceSell or
			HL2RPIds.Commands.PermitPurchase or HL2RPIds.Commands.NoteWrite or
			HL2RPIds.Commands.RadioFrequency or HL2RPIds.Commands.RestraintSet;
		return new HL2RPPresentationChangeSet
		{
			Connections = new[] { actor },
			Accounts = accounts,
			Characters = characters,
			Inventories = inventories,
			Items = items,
			RebuildLiveInventory = inventoryMutation,
			RebuildCombatTargets = false
		};
	}

	private static HL2RPPresentationChangeSet InventoryMutation(
		ConnectionId actor, IEnumerable<InventoryId> inventories, IEnumerable<ItemId> items ) => new()
	{
		Connections = new[] { actor },
		Inventories = inventories.Distinct().ToArray(),
		Items = items.Distinct().ToArray(),
		RebuildLiveInventory = true
	};

	private static IReadOnlyList<CharacterId> CharacterArgument( RunSchemaCommandCommand command ) =>
		TryGuid( command.Arguments, "character", out var value ) ? new[] { new CharacterId( value ) } : Array.Empty<CharacterId>();
	private static IReadOnlyList<AccountId> AccountArgument( RunSchemaCommandCommand command ) =>
		TryAccountId( command.Arguments, "account", out var value )
			? new[] { value }
			: Array.Empty<AccountId>();
	private static IReadOnlyList<InventoryId> InventoryArguments( RunSchemaCommandCommand command ) =>
		TryGuid( command.Arguments, "inventory", out var value ) ? new[] { new InventoryId( value ) } : Array.Empty<InventoryId>();
	private static IReadOnlyList<ItemId> ItemArguments( RunSchemaCommandCommand command ) =>
		TryGuid( command.Arguments, "item", out var value ) ? new[] { new ItemId( value ) } : Array.Empty<ItemId>();
	private static bool TryGuid( IReadOnlyDictionary<string, SnapshotValue> arguments, string key, out Guid value )
	{
		value = default;
		return arguments.TryGetValue( key, out var candidate ) &&
			candidate.Kind is SnapshotValueKind.String or SnapshotValueKind.Choice &&
			Guid.TryParse( candidate.StringValue, out value );
	}

	public static bool TryAccountId(
		IReadOnlyDictionary<string, SnapshotValue> arguments,
		string key,
		out AccountId accountId )
	{
		accountId = default;
		if ( !arguments.TryGetValue( key, out var candidate ) ) return false;
		ulong raw;
		if ( candidate.Kind == SnapshotValueKind.Integer )
		{
			if ( candidate.IntegerValue <= 0 ) return false;
			raw = checked((ulong)candidate.IntegerValue);
		}
		else if ( candidate.Kind is SnapshotValueKind.String or SnapshotValueKind.Choice )
		{
			if ( !ulong.TryParse( candidate.StringValue, NumberStyles.None,
				CultureInfo.InvariantCulture, out raw ) || raw == 0 ) return false;
		}
		else return false;
		accountId = new AccountId( raw );
		return true;
	}

	public static IReadOnlyList<ConnectionId> ResolveRecipients(
		HL2RPPresentationChangeSet changes,
		Func<CharacterId, ConnectionId?> activeCharacter,
		Func<AccountId, IReadOnlyList<ConnectionId>> accountConnections,
		Func<InventoryId, IReadOnlyList<ConnectionId>> inventoryViewers,
		Func<SceneEntityId, IReadOnlyList<ConnectionId>> sceneViewers )
	{
		ArgumentNullException.ThrowIfNull( changes );
		ArgumentNullException.ThrowIfNull( activeCharacter );
		ArgumentNullException.ThrowIfNull( accountConnections );
		ArgumentNullException.ThrowIfNull( inventoryViewers );
		ArgumentNullException.ThrowIfNull( sceneViewers );
		if ( changes.Broadcast )
			throw new ArgumentException( "Broadcast recipients must be supplied by the host boundary.", nameof(changes) );
		var recipients = new HashSet<ConnectionId>( changes.Connections );
		foreach ( var account in changes.Accounts ) recipients.UnionWith( accountConnections( account ) );
		foreach ( var character in changes.Characters )
			if ( activeCharacter( character ) is ConnectionId connection ) recipients.Add( connection );
		foreach ( var inventory in changes.Inventories ) recipients.UnionWith( inventoryViewers( inventory ) );
		foreach ( var sceneEntity in changes.SceneEntities ) recipients.UnionWith( sceneViewers( sceneEntity ) );
		return recipients.ToArray();
	}

	public static IReadOnlyList<ConnectionId> ResolveRecipients(
		HL2RPPresentationChangeSet changes,
		IReadOnlyDictionary<CharacterId, ConnectionId> activeCharacters,
		IReadOnlyDictionary<AccountId, IReadOnlyList<ConnectionId>> accountConnections,
		IReadOnlyCollection<ConnectionId> allConnections,
		Func<InventoryId, IReadOnlyList<ConnectionId>> inventoryViewers,
		Func<SceneEntityId, IReadOnlyList<ConnectionId>> sceneViewers )
	{
		ArgumentNullException.ThrowIfNull( changes );
		if ( changes.Broadcast ) return allConnections.Distinct().ToArray();
		var recipients = new HashSet<ConnectionId>( changes.Connections );
		foreach ( var account in changes.Accounts )
			if ( accountConnections.TryGetValue( account, out var connections ) ) recipients.UnionWith( connections );
		foreach ( var character in changes.Characters )
			if ( activeCharacters.TryGetValue( character, out var connection ) ) recipients.Add( connection );
		foreach ( var inventory in changes.Inventories ) recipients.UnionWith( inventoryViewers( inventory ) );
		foreach ( var sceneEntity in changes.SceneEntities ) recipients.UnionWith( sceneViewers( sceneEntity ) );
		return recipients.ToArray();
	}

	public static IReadOnlyList<ConnectionId> ResolveRecipients(
		HL2RPPresentationChangeSet changes,
		IReadOnlyDictionary<CharacterId, ConnectionId> activeCharacters,
		IReadOnlyDictionary<AccountId, IReadOnlyList<ConnectionId>> accountConnections,
		IReadOnlyCollection<ConnectionId> allConnections,
		Func<InventoryId, IReadOnlyList<ConnectionId>> inventoryViewers ) =>
		ResolveRecipients( changes, activeCharacters, accountConnections, allConnections,
			inventoryViewers, _ => Array.Empty<ConnectionId>() );

	public static IReadOnlyList<ConnectionId> ResolveRecipients(
		HL2RPPresentationChangeSet changes,
		IReadOnlyDictionary<CharacterId, ConnectionId> activeCharacters,
		IReadOnlyCollection<ConnectionId> allConnections,
		Func<InventoryId, IReadOnlyList<ConnectionId>> inventoryViewers ) =>
		ResolveRecipients( changes, activeCharacters,
			new Dictionary<AccountId, IReadOnlyList<ConnectionId>>(), allConnections,
			inventoryViewers, _ => Array.Empty<ConnectionId>() );
}
