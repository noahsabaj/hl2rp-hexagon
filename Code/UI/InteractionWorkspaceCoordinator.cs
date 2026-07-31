#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hexagon.V2.Client;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;

namespace HL2RP.UI;

public enum ClientInteractionSessionKind
{
	Storage = 0,
	Vendor = 1,
	Search = 2
}

public enum SearchWorkspacePhase
{
	RequestingHost = 0,
	AwaitingSnapshot = 1
}

public sealed record ClientInteractionWorkspaceSession(
	ShowcaseWorkspace Workspace,
	ClientInteractionSessionKind Kind,
	InteractionSessionId SessionId);

public sealed record InteractionWorkspaceSnapshot(
	ShowcaseWorkspace ActiveWorkspace,
	InteractionSessionId? StorageSessionId,
	InteractionSessionId? VendorSessionId,
	InteractionSessionId? SearchSessionId)
{
	public static InteractionWorkspaceSnapshot From(HL2RPShowcaseViewModel model)
	{
		ArgumentNullException.ThrowIfNull(model);
		return new InteractionWorkspaceSnapshot(
			model.ActiveWorkspace,
			model.Storage?.SessionId,
			model.Vendor?.SessionId,
			model.Search?.SessionId);
	}
}

public sealed record PendingSearchWorkspace(
	long Sequence,
	CharacterId TargetCharacterId,
	SearchWorkspacePhase Phase);

public sealed record InteractionWorkspaceTransition(
	long Sequence,
	ShowcaseWorkspace Destination,
	ClientInteractionWorkspaceSession? ClosedSession,
	OperationResult CloseResult);

public sealed record InteractionWorkspaceObservation(
	bool SearchActivated,
	ClientInteractionWorkspaceSession? AbandonedSearchSession);

/// <summary>
/// Sole owner of client-side terminal interaction transitions. It sends one
/// close command for the exact host-authored session and shares that pending
/// close across rapid UI transitions until the host snapshot drops the session.
/// </summary>
public sealed class InteractionWorkspaceCoordinator
{
	private readonly Func<InteractionSessionId, ValueTask<OperationResult>> _close;
	private readonly Dictionary<InteractionSessionId, Task<OperationResult>> _closing = new();
	private readonly HashSet<InteractionSessionId> _terminal = new();
	private long _sequence;
	private PendingSearchWorkspace? _abandonedSearch;

	public InteractionWorkspaceCoordinator(IHexClientController controller)
		: this(CloseWith( controller ))
	{
	}

	public InteractionWorkspaceCoordinator(
		Func<InteractionSessionId, ValueTask<OperationResult>> close) =>
		_close = close ?? throw new ArgumentNullException(nameof(close));

	public long CurrentSequence => _sequence;
	public PendingSearchWorkspace? PendingSearch { get; private set; }
	public int PendingCloseCount => _closing.Count;

	public async ValueTask<InteractionWorkspaceTransition> PrepareTransitionAsync(
		InteractionWorkspaceSnapshot snapshot,
		ShowcaseWorkspace destination)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		var sequence = checked(++_sequence);
		if (PendingSearch is not null && destination != ShowcaseWorkspace.Search)
		{
			_abandonedSearch = PendingSearch;
			PendingSearch = null;
		}
		var destinationSessionId = ResolveDestinationSessionId(snapshot, destination);
		if (destinationSessionId is InteractionSessionId tombstonedSession &&
			_terminal.Contains(tombstonedSession))
			return new InteractionWorkspaceTransition(
				sequence,
				destination,
				null,
				OperationResult.Failure(
					ErrorCode.Conflict,
					"The prior interaction is closing; wait for the host revocation snapshot."));
		var activeSession = ResolveActiveSession(snapshot);
		var session = activeSession is not null && destination != activeSession.Workspace
			? activeSession : null;
		if (session is null && _abandonedSearch is not null &&
			snapshot.SearchSessionId is InteractionSessionId abandonedSessionId)
			session = new ClientInteractionWorkspaceSession(
				ShowcaseWorkspace.Search, ClientInteractionSessionKind.Search, abandonedSessionId);
		var closeResult = OperationResult.Success();
		if (session is not null)
			closeResult = await CloseOnceAsync(session.SessionId);
		if (session?.Kind == ClientInteractionSessionKind.Search && closeResult.Succeeded)
			_abandonedSearch = null;
		return new InteractionWorkspaceTransition(sequence, destination, session, closeResult);
	}

	public OperationResult<long> BeginSearch(CharacterId targetCharacterId)
	{
		if (PendingSearch is not null || _abandonedSearch is not null)
			return OperationResult<long>.Failure(
				ErrorCode.Conflict, "A search interaction request is already pending.");
		var sequence = checked(++_sequence);
		PendingSearch = new PendingSearchWorkspace(
			sequence, targetCharacterId, SearchWorkspacePhase.RequestingHost);
		return OperationResult<long>.Success(sequence);
	}

	public bool MarkSearchAccepted(long sequence)
	{
		if (PendingSearch is not PendingSearchWorkspace pending || pending.Sequence != sequence) return false;
		PendingSearch = pending with { Phase = SearchWorkspacePhase.AwaitingSnapshot };
		return true;
	}

	public bool CompleteSearchCommand(long sequence, OperationResult result)
	{
		if (PendingSearch?.Sequence == sequence)
		{
			if (result.Failed) PendingSearch = null;
			else MarkSearchAccepted(sequence);
			return true;
		}
		if (_abandonedSearch?.Sequence != sequence) return false;
		if (result.Failed) _abandonedSearch = null;
		else _abandonedSearch = _abandonedSearch with { Phase = SearchWorkspacePhase.AwaitingSnapshot };
		return false;
	}

	public InteractionWorkspaceObservation Observe(InteractionWorkspaceSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		var visible = new HashSet<InteractionSessionId>();
		if (snapshot.StorageSessionId is InteractionSessionId storage) visible.Add(storage);
		if (snapshot.VendorSessionId is InteractionSessionId vendor) visible.Add(vendor);
		if (snapshot.SearchSessionId is InteractionSessionId search) visible.Add(search);
		_terminal.RemoveWhere(sessionId => !visible.Contains(sessionId));

		if (snapshot.SearchSessionId is InteractionSessionId searchSession && _abandonedSearch is not null)
			return new InteractionWorkspaceObservation(false, new ClientInteractionWorkspaceSession(
				ShowcaseWorkspace.Search, ClientInteractionSessionKind.Search, searchSession));
		if (PendingSearch is null || snapshot.SearchSessionId is null)
			return new InteractionWorkspaceObservation(false, null);
		PendingSearch = null;
		return new InteractionWorkspaceObservation(true, null);
	}

	public async ValueTask<OperationResult> CloseAbandonedSearchAsync(
		ClientInteractionWorkspaceSession session)
	{
		if (session.Kind != ClientInteractionSessionKind.Search)
			return OperationResult.Failure(ErrorCode.InvalidArgument, "Only an abandoned search can use this close path.");
		var result = await CloseOnceAsync(session.SessionId);
		if (result.Succeeded) _abandonedSearch = null;
		return result;
	}

	public bool IsCurrent(long sequence) => sequence == _sequence;

	public void Reset()
	{
		_sequence = checked(_sequence + 1);
		PendingSearch = null;
		_abandonedSearch = null;
		_closing.Clear();
		_terminal.Clear();
	}

	public static ClientInteractionWorkspaceSession? ResolveActiveSession(
		InteractionWorkspaceSnapshot snapshot) => snapshot.ActiveWorkspace switch
	{
		ShowcaseWorkspace.Storage when snapshot.StorageSessionId is InteractionSessionId session =>
			new ClientInteractionWorkspaceSession(
				ShowcaseWorkspace.Storage, ClientInteractionSessionKind.Storage, session),
		ShowcaseWorkspace.Vendor when snapshot.VendorSessionId is InteractionSessionId session =>
			new ClientInteractionWorkspaceSession(
				ShowcaseWorkspace.Vendor, ClientInteractionSessionKind.Vendor, session),
		ShowcaseWorkspace.Search when snapshot.SearchSessionId is InteractionSessionId session =>
			new ClientInteractionWorkspaceSession(
				ShowcaseWorkspace.Search, ClientInteractionSessionKind.Search, session),
		_ => null
	};

	private static InteractionSessionId? ResolveDestinationSessionId(
		InteractionWorkspaceSnapshot snapshot,
		ShowcaseWorkspace destination) => destination switch
	{
		ShowcaseWorkspace.Storage => snapshot.StorageSessionId,
		ShowcaseWorkspace.Vendor => snapshot.VendorSessionId,
		ShowcaseWorkspace.Search => snapshot.SearchSessionId,
		_ => null
	};

	private static Func<InteractionSessionId, ValueTask<OperationResult>> CloseWith(
		IHexClientController controller)
	{
		ArgumentNullException.ThrowIfNull(controller);
		return sessionId => controller.CloseInteractionAsync(sessionId);
	}

	private async ValueTask<OperationResult> CloseOnceAsync(InteractionSessionId sessionId)
	{
		if (_closing.TryGetValue(sessionId, out var pending)) return await pending;
		if (_terminal.Contains(sessionId)) return OperationResult.Success();

		_terminal.Add(sessionId);
		var close = _close(sessionId).AsTask();
		_closing.Add(sessionId, close);
		OperationResult result;
		try
		{
			result = await close;
		}
		catch (Exception exception)
		{
			result = OperationResult.Failure(
				ErrorCode.InternalError, $"Interaction close failed unexpectedly: {exception.Message}");
		}
		finally
		{
			_closing.Remove(sessionId);
		}
		if (result.Failed) _terminal.Remove(sessionId);
		return result;
	}
}
