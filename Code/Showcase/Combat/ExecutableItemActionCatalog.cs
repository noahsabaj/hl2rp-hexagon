#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Networking;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Showcase.Combat;

public enum ExecutableItemActionRoute
{
	AtomicPlanner,
	RadioTuning,
	RequestDevice,
	NoteEditor,
	BagInteraction,
	HealthVialConsume,
	RestraintIntent,
	CombatFireIntent,
	CombineLockInstall,
	TokenSplit,
	TokenCombine
}

public sealed record ExecutableItemAction(
	DefinitionId DefinitionId,
	ActionId ActionId,
	ExecutableItemActionRoute Route);

/// <summary>
/// One conformance source for advertised item actions. Presentation must use
/// CreateSnapshot so an action is enabled only when an executable route exists.
/// </summary>
public sealed class HL2RPExecutableItemActionCatalog
{
	private readonly IReadOnlyDictionary<(DefinitionId Definition, ActionId Action), ExecutableItemAction> _routes;

	public HL2RPExecutableItemActionCatalog(IEnumerable<ExecutableItemAction> routes)
	{
		ArgumentNullException.ThrowIfNull(routes);
		var map = new Dictionary<(DefinitionId, ActionId), ExecutableItemAction>();
		foreach (var route in routes.OrderBy(route => route.DefinitionId.Value, StringComparer.Ordinal)
			.ThenBy(route => route.ActionId.Value, StringComparer.Ordinal))
		{
			ArgumentNullException.ThrowIfNull(route);
			if (!map.TryAdd((route.DefinitionId, route.ActionId), route))
				throw new InvalidOperationException(
					$"Executable item action '{route.DefinitionId}/{route.ActionId}' is duplicated.");
		}
		_routes = map;
	}

	public IReadOnlyCollection<ExecutableItemAction> Routes => _routes.Values.ToArray();

	public bool TryResolve(DefinitionId definitionId, ActionId actionId, out ExecutableItemAction route) =>
		_routes.TryGetValue((definitionId, actionId), out route!);

	public ItemActionSnapshot CreateSnapshot(DefinitionId definitionId, ActionId actionId, string label)
	{
		var routed = TryResolve(definitionId, actionId, out _);
		return new ItemActionSnapshot(
			actionId,
			label ?? actionId.Value,
			routed,
			routed ? null : "No host executable route is registered.");
	}

	public OperationResult Validate(CompiledSchema schema)
	{
		ArgumentNullException.ThrowIfNull(schema);
		foreach (var definition in schema.Items.All)
		foreach (var action in definition.ActionIds)
		{
			if (!schema.Actions.Contains(action))
				return OperationResult.Failure(ErrorCode.UnknownDefinition,
					$"Item '{definition.Id}' advertises unknown action '{action}'.");
			if (!TryResolve(new DefinitionId(definition.Id), new ActionId(action), out _))
				return OperationResult.Failure(ErrorCode.ConfigurationInvalid,
					$"Item '{definition.Id}' advertises unrouted action '{action}'.");
		}
		foreach (var route in _routes.Values)
		{
			if (!schema.Items.TryGet(route.DefinitionId.Value, out var definition) ||
				!definition!.ActionIds.Contains(route.ActionId.Value, StringComparer.Ordinal))
				return OperationResult.Failure(ErrorCode.ConfigurationInvalid,
					$"Executable route '{route.DefinitionId}/{route.ActionId}' is not advertised by the schema.");
		}
		return OperationResult.Success();
	}

	public static HL2RPExecutableItemActionCatalog CreateDefault() => new(new[]
	{
		Route(HL2RPIds.Items.CitizenIdCard, HL2RPIds.Actions.Show, ExecutableItemActionRoute.AtomicPlanner),
		Route(HL2RPIds.Items.Ration, HL2RPIds.Actions.Open, ExecutableItemActionRoute.AtomicPlanner),
		Route(HL2RPIds.Items.Water, HL2RPIds.Actions.Consume, ExecutableItemActionRoute.AtomicPlanner),
		Route(HL2RPIds.Items.HealthVial, HL2RPIds.Actions.Consume, ExecutableItemActionRoute.HealthVialConsume),
		Route(HL2RPIds.Items.Flashlight, HL2RPIds.Actions.Toggle, ExecutableItemActionRoute.AtomicPlanner),
		Route(HL2RPIds.Items.Radio, HL2RPIds.Actions.Tune, ExecutableItemActionRoute.RadioTuning),
		Route(HL2RPIds.Items.RequestDevice, HL2RPIds.Actions.Request, ExecutableItemActionRoute.RequestDevice),
		Route(HL2RPIds.Items.Note, HL2RPIds.Actions.Read, ExecutableItemActionRoute.AtomicPlanner),
		Route(HL2RPIds.Items.Note, HL2RPIds.Actions.Write, ExecutableItemActionRoute.NoteEditor),
		Route(HL2RPIds.Items.CivicHandbook, HL2RPIds.Actions.Read, ExecutableItemActionRoute.AtomicPlanner),
		Route(HL2RPIds.Items.Suitcase, HL2RPIds.Actions.OpenBag, ExecutableItemActionRoute.BagInteraction),
		Route(HL2RPIds.Items.BusinessPermit, HL2RPIds.Actions.PresentPermit, ExecutableItemActionRoute.AtomicPlanner),
		Route(HL2RPIds.Items.ZipTie, HL2RPIds.Actions.Restrain, ExecutableItemActionRoute.RestraintIntent),
		Route(HL2RPIds.Items.Pistol, HL2RPIds.Actions.Equip, ExecutableItemActionRoute.AtomicPlanner),
		Route(HL2RPIds.Items.Pistol, HL2RPIds.Actions.Unequip, ExecutableItemActionRoute.AtomicPlanner),
		Route(HL2RPIds.Items.Pistol, HL2RPIds.Actions.Reload, ExecutableItemActionRoute.AtomicPlanner),
		Route(HL2RPIds.Items.Pistol, HL2RPIds.Actions.Fire, ExecutableItemActionRoute.CombatFireIntent),
		Route(HL2RPIds.Items.PistolAmmunition, HL2RPIds.Actions.Replenish, ExecutableItemActionRoute.AtomicPlanner),
		Route(HL2RPIds.Items.ProtectiveVest, HL2RPIds.Actions.Equip, ExecutableItemActionRoute.AtomicPlanner),
		Route(HL2RPIds.Items.ProtectiveVest, HL2RPIds.Actions.Unequip, ExecutableItemActionRoute.AtomicPlanner),
		Route(HL2RPIds.Items.CombineLockKit, HL2RPIds.Actions.Install, ExecutableItemActionRoute.CombineLockInstall),
		Route(HL2RPIds.Items.TokenStack, HL2RPIds.Actions.Split, ExecutableItemActionRoute.TokenSplit),
		Route(HL2RPIds.Items.TokenStack, HL2RPIds.Actions.Combine, ExecutableItemActionRoute.TokenCombine)
	});

	private static ExecutableItemAction Route(string definition, string action, ExecutableItemActionRoute route) =>
		new(new DefinitionId(definition), new ActionId(action), route);
}
