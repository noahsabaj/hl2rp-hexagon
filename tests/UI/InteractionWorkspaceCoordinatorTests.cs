#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using HL2RP.UI;

namespace HL2RP.V2.Tests.UI;

[TestClass]
public sealed class InteractionWorkspaceCoordinatorTests
{
	[TestMethod]
	public async Task EveryTerminalWorkspaceClosesItsExactSessionAndKindBeforeTransition()
	{
		foreach (var test in new[]
		{
			(Workspace: ShowcaseWorkspace.Storage, Kind: ClientInteractionSessionKind.Storage),
			(Workspace: ShowcaseWorkspace.Vendor, Kind: ClientInteractionSessionKind.Vendor),
			(Workspace: ShowcaseWorkspace.Search, Kind: ClientInteractionSessionKind.Search)
		})
		{
			var sessionId = InteractionSessionId.New();
			var closed = new List<InteractionSessionId>();
			var coordinator = new InteractionWorkspaceCoordinator(session =>
			{
				closed.Add(session);
				return ValueTask.FromResult(OperationResult.Success());
			});
			var snapshot = Snapshot(test.Workspace,
				test.Workspace == ShowcaseWorkspace.Storage ? sessionId : null,
				test.Workspace == ShowcaseWorkspace.Vendor ? sessionId : null,
				test.Workspace == ShowcaseWorkspace.Search ? sessionId : null);

			var transition = await coordinator.PrepareTransitionAsync(snapshot, ShowcaseWorkspace.Inventory);

			Assert.HasCount(1, closed);
			Assert.AreEqual(sessionId, closed[0]);
			Assert.AreEqual(sessionId, transition.ClosedSession?.SessionId);
			Assert.AreEqual(test.Kind, transition.ClosedSession?.Kind);
			Assert.AreEqual(ShowcaseWorkspace.Inventory, transition.Destination);
			Assert.IsTrue(transition.CloseResult.Succeeded);
		}
	}

	[TestMethod]
	public async Task RapidSwitchesShareOneInflightCloseAndOnlyLatestTransitionRemainsCurrent()
	{
		var sessionId = InteractionSessionId.New();
		var release = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		var closeCount = 0;
		var coordinator = new InteractionWorkspaceCoordinator(session =>
		{
			Assert.AreEqual(sessionId, session);
			closeCount++;
			return new ValueTask<OperationResult>(release.Task);
		});
		var snapshot = Snapshot(ShowcaseWorkspace.Vendor, vendor: sessionId);

		var first = coordinator.PrepareTransitionAsync(snapshot, ShowcaseWorkspace.Storage).AsTask();
		var second = coordinator.PrepareTransitionAsync(snapshot, ShowcaseWorkspace.Inventory).AsTask();

		Assert.AreEqual(1, closeCount);
		Assert.AreEqual(1, coordinator.PendingCloseCount);
		release.SetResult(OperationResult.Success());
		var firstResult = await first;
		var secondResult = await second;

		Assert.IsFalse(coordinator.IsCurrent(firstResult.Sequence));
		Assert.IsTrue(coordinator.IsCurrent(secondResult.Sequence));
		Assert.AreEqual(ShowcaseWorkspace.Inventory, secondResult.Destination);
		Assert.AreEqual(0, coordinator.PendingCloseCount);
	}

	[TestMethod]
	public async Task FailedCloseIsRetriedByTheNextExplicitTransition()
	{
		var sessionId = InteractionSessionId.New();
		var attempts = 0;
		var coordinator = new InteractionWorkspaceCoordinator(_ =>
		{
			attempts++;
			return ValueTask.FromResult(attempts == 1
				? OperationResult.Failure(ErrorCode.Conflict, "Injected close conflict.")
				: OperationResult.Success());
		});
		var snapshot = Snapshot(ShowcaseWorkspace.Storage, storage: sessionId);

		var failed = await coordinator.PrepareTransitionAsync(snapshot, ShowcaseWorkspace.None);
		var retried = await coordinator.PrepareTransitionAsync(snapshot, ShowcaseWorkspace.Inventory);

		Assert.IsTrue(failed.CloseResult.Failed);
		Assert.IsTrue(retried.CloseResult.Succeeded);
		Assert.AreEqual(2, attempts);
	}

	[TestMethod]
	public async Task ClosedTerminalIdsCannotReopenFromStaleSnapshotsUntilHostRevocationAppears()
	{
		foreach (var workspace in new[]
		{
			ShowcaseWorkspace.Storage,
			ShowcaseWorkspace.Vendor,
			ShowcaseWorkspace.Search
		})
		{
			var sessionId = InteractionSessionId.New();
			var closeCount = 0;
			var coordinator = new InteractionWorkspaceCoordinator(_ =>
			{
				closeCount++;
				return ValueTask.FromResult(OperationResult.Success());
			});
			var active = Snapshot(workspace,
				workspace == ShowcaseWorkspace.Storage ? sessionId : null,
				workspace == ShowcaseWorkspace.Vendor ? sessionId : null,
				workspace == ShowcaseWorkspace.Search ? sessionId : null);
			Assert.IsTrue((await coordinator.PrepareTransitionAsync(
				active, ShowcaseWorkspace.None)).CloseResult.Succeeded);

			var stale = Snapshot(ShowcaseWorkspace.None,
				workspace == ShowcaseWorkspace.Storage ? sessionId : null,
				workspace == ShowcaseWorkspace.Vendor ? sessionId : null,
				workspace == ShowcaseWorkspace.Search ? sessionId : null);
			var denied = await coordinator.PrepareTransitionAsync(stale, workspace);
			coordinator.Observe(stale);
			var deniedAgain = await coordinator.PrepareTransitionAsync(stale, workspace);

			Assert.AreEqual(ErrorCode.Conflict, denied.CloseResult.Error?.Code);
			Assert.AreEqual(ErrorCode.Conflict, deniedAgain.CloseResult.Error?.Code);
			Assert.AreEqual(1, closeCount);

			coordinator.Observe(Snapshot(ShowcaseWorkspace.None));
			var freshSession = InteractionSessionId.New();
			var fresh = Snapshot(ShowcaseWorkspace.None,
				workspace == ShowcaseWorkspace.Storage ? freshSession : null,
				workspace == ShowcaseWorkspace.Vendor ? freshSession : null,
				workspace == ShowcaseWorkspace.Search ? freshSession : null);
			Assert.IsTrue((await coordinator.PrepareTransitionAsync(fresh, workspace)).CloseResult.Succeeded);
		}
	}

	[TestMethod]
	public async Task RapidReopenIsDeniedWhileTheExactCloseCommandIsStillInflight()
	{
		var sessionId = InteractionSessionId.New();
		var release = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		var closeCount = 0;
		var coordinator = new InteractionWorkspaceCoordinator(_ =>
		{
			closeCount++;
			return new ValueTask<OperationResult>(release.Task);
		});
		var closing = coordinator.PrepareTransitionAsync(
			Snapshot(ShowcaseWorkspace.Storage, storage: sessionId), ShowcaseWorkspace.None).AsTask();

		var reopened = await coordinator.PrepareTransitionAsync(
			Snapshot(ShowcaseWorkspace.None, storage: sessionId), ShowcaseWorkspace.Storage);

		Assert.AreEqual(ErrorCode.Conflict, reopened.CloseResult.Error?.Code);
		Assert.AreEqual(1, closeCount);
		release.SetResult(OperationResult.Success());
		Assert.IsTrue((await closing).CloseResult.Succeeded);
	}

	[TestMethod]
	public async Task AbandonedInflightSearchClosesTheLateHostSessionAndDeniesASecondSearch()
	{
		var closed = new List<InteractionSessionId>();
		var coordinator = new InteractionWorkspaceCoordinator(session =>
		{
			closed.Add(session);
			return ValueTask.FromResult(OperationResult.Success());
		});
		var target = CharacterId.New();
		var pending = coordinator.BeginSearch(target);
		Assert.IsTrue(pending.Succeeded);

		await coordinator.PrepareTransitionAsync(
			Snapshot(ShowcaseWorkspace.None), ShowcaseWorkspace.Inventory);
		Assert.IsTrue(coordinator.BeginSearch(CharacterId.New()).Failed,
			"A second search must be denied until the abandoned host command resolves.");
		Assert.IsFalse(coordinator.CompleteSearchCommand(pending.Value, OperationResult.Success()));
		var lateSession = InteractionSessionId.New();
		var observation = coordinator.Observe(
			Snapshot(ShowcaseWorkspace.Inventory, search: lateSession));

		Assert.IsFalse(observation.SearchActivated);
		Assert.AreEqual(lateSession, observation.AbandonedSearchSession?.SessionId);
		Assert.AreEqual(ClientInteractionSessionKind.Search, observation.AbandonedSearchSession?.Kind);
		Assert.IsTrue((await coordinator.CloseAbandonedSearchAsync(
			observation.AbandonedSearchSession!)).Succeeded);
		CollectionAssert.AreEqual(new[] { lateSession }, closed);
		var staleReopen = await coordinator.PrepareTransitionAsync(
			Snapshot(ShowcaseWorkspace.Inventory, search: lateSession), ShowcaseWorkspace.Search);
		Assert.AreEqual(ErrorCode.Conflict, staleReopen.CloseResult.Error?.Code);
		Assert.HasCount(1, closed);
	}

	[TestMethod]
	public void DeniedSearchClearsPendingAndAuthoritativeSnapshotActivatesWithoutLocalDuplicateState()
	{
		var coordinator = new InteractionWorkspaceCoordinator(_ =>
			ValueTask.FromResult(OperationResult.Success()));
		var denied = coordinator.BeginSearch(CharacterId.New());
		Assert.IsTrue(coordinator.CompleteSearchCommand(denied.Value,
			OperationResult.Failure(ErrorCode.PolicyDenied, "Denied.")));
		Assert.IsNull(coordinator.PendingSearch);

		var accepted = coordinator.BeginSearch(CharacterId.New());
		Assert.IsTrue(coordinator.CompleteSearchCommand(accepted.Value, OperationResult.Success()));
		Assert.AreEqual(SearchWorkspacePhase.AwaitingSnapshot, coordinator.PendingSearch?.Phase);
		var sessionId = InteractionSessionId.New();
		var observed = coordinator.Observe(Snapshot(ShowcaseWorkspace.Search, search: sessionId));

		Assert.IsTrue(observed.SearchActivated);
		Assert.IsNull(observed.AbandonedSearchSession);
		Assert.IsNull(coordinator.PendingSearch);
		var resolved = InteractionWorkspaceCoordinator.ResolveActiveSession(
			Snapshot(ShowcaseWorkspace.Search, search: sessionId));
		Assert.AreEqual(sessionId, resolved?.SessionId);
		Assert.AreEqual(ClientInteractionSessionKind.Search, resolved?.Kind);
	}

	private static InteractionWorkspaceSnapshot Snapshot(
		ShowcaseWorkspace workspace,
		InteractionSessionId? storage = null,
		InteractionSessionId? vendor = null,
		InteractionSessionId? search = null) =>
		new(workspace, storage, vendor, search);
}
