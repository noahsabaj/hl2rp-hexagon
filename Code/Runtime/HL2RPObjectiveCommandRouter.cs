#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;

namespace HL2RP.V2.Runtime;

public sealed record HL2RPObjectiveRouteReceipt(
	string ObjectiveId,
	CityObjectiveChangeKind Kind,
	CityObjectivesReceipt Persistence,
	CommitReceipt ProjectionReceipt,
	IReadOnlyList<IReadOnlyDictionary<string, SnapshotValue>> ProjectionRows,
	HL2RPPresentationChangeSet PresentationChanges );

public sealed record HL2RPObjectiveRouteOutcome(
	HL2RPCommandOutcome Command,
	HL2RPObjectiveRouteReceipt? Receipt );

/// <summary>
/// Engine-neutral objective route. The s&amp;box adapter supplies only the indexed
/// city identity and transport DTO; parsing, create/update/delete semantics,
/// commit, and presentation inputs are executable in the neutral test suite.
/// </summary>
public sealed class HL2RPObjectiveCommandRouter
{
	private readonly CityObjectiveService _service;
	private readonly Func<SceneEntityId?> _resolveCity;
	private readonly Func<Guid> _newId;

	public HL2RPObjectiveCommandRouter(
		CityObjectiveService service,
		Func<SceneEntityId?> resolveCity,
		Func<Guid> newId )
	{
		_service = service ?? throw new ArgumentNullException( nameof(service) );
		_resolveCity = resolveCity ?? throw new ArgumentNullException( nameof(resolveCity) );
		_newId = newId ?? throw new ArgumentNullException( nameof(newId) );
	}

	public async ValueTask<HL2RPObjectiveRouteOutcome> RouteAsync(
		InventoryActor actor,
		HL2RPCommandArguments arguments,
		CancellationToken cancellationToken = default )
	{
		ArgumentNullException.ThrowIfNull( arguments );
		var parsed = HL2RPObjectiveCommand.ParseChange( arguments );
		if ( parsed.Failed ) return Failure( parsed.Error! );
		var cityId = _resolveCity();
		if ( cityId is null )
			return Failure( new OperationError( ErrorCode.NotFound, "City state is unavailable." ) );
		var applied = await _service.ApplyAsync(
			actor, cityId.Value, parsed.Value, _newId, cancellationToken );
		if ( applied.Failed ) return Failure( applied.Error! );

		var projectionRows = HL2RPObjectiveProjection.Rows( new CityEntityState
		{
			Objectives = applied.Value.State.Objectives
		} );
		var projectionReceipt = applied.Value.State.CommitReceipt;
		var changes = new HL2RPPresentationChangeSet
		{
			Broadcast = true,
			Documents = projectionReceipt.Documents.Select( value => value.Address ).ToArray()
		};
		var receipt = new HL2RPObjectiveRouteReceipt(
			applied.Value.ObjectiveId,
			applied.Value.Kind,
			applied.Value.State,
			projectionReceipt,
			projectionRows,
			changes );
		return new HL2RPObjectiveRouteOutcome(
			new HL2RPCommandOutcome( OperationResult.Success(), changes ), receipt );
	}

	private static HL2RPObjectiveRouteOutcome Failure( OperationError error ) => new(
		new HL2RPCommandOutcome(
			OperationResult.Failure( error.Code, error.Message, error.Details ),
			HL2RPPresentationChangeSet.None ),
		null );
}
