#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using HL2RP.V2.Showcase.Scanner;

namespace HL2RP.V2.Tests.Showcase.Scanner;

[TestClass]
public sealed class ScannerRecoveryRetryPolicyTests
{
	private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

	[TestMethod]
	public void NewHandleArmsAtTheInitialDelayInsteadOfRetryingImmediately()
	{
		var policy = new ScannerRecoveryRetryPolicy();
		var handles = new[] { Handle(ErrorCode.Conflict) };

		Assert.IsEmpty(policy.SelectDue(handles, Epoch));
		Assert.IsEmpty(policy.SelectDue(
			handles, Epoch + ScannerRecoveryRetryPolicy.InitialDelay - TimeSpan.FromTicks(1)));
		Assert.HasCount(1, policy.SelectDue(handles, Epoch + ScannerRecoveryRetryPolicy.InitialDelay));
	}

	[TestMethod]
	public void EachSelectionDoublesTheDelayUpToTheCap()
	{
		var policy = new ScannerRecoveryRetryPolicy();
		var handles = new[] { Handle(ErrorCode.InternalError) };
		var now = Epoch + ScannerRecoveryRetryPolicy.InitialDelay;
		Assert.IsEmpty(policy.SelectDue(handles, Epoch));
		Assert.HasCount(1, policy.SelectDue(handles, now));

		var delay = ScannerRecoveryRetryPolicy.InitialDelay;
		for (var round = 0; round < 8; round++)
		{
			delay = TimeSpan.FromTicks(Math.Min(
				delay.Ticks * 2, ScannerRecoveryRetryPolicy.MaximumDelay.Ticks));
			Assert.IsEmpty(policy.SelectDue(handles, now + delay - TimeSpan.FromTicks(1)),
				$"Round {round}: the handle must not be due before its doubled delay elapses.");
			now += delay;
			Assert.HasCount(1, policy.SelectDue(handles, now), $"Round {round}");
		}

		Assert.AreEqual(ScannerRecoveryRetryPolicy.MaximumDelay, delay,
			"Eight doublings from five seconds must have reached the two-minute cap.");
	}

	[TestMethod]
	public void StorageLimitHandlesAreNeverSelected()
	{
		var policy = new ScannerRecoveryRetryPolicy();
		var handles = new[] { Handle(ErrorCode.StorageLimitExceeded) };

		Assert.IsEmpty(policy.SelectDue(handles, Epoch));
		Assert.IsEmpty(policy.SelectDue(handles, Epoch + TimeSpan.FromDays(365)));
	}

	[TestMethod]
	public void HealedHandlesAreForgottenSoALaterFailureStartsAFreshBackoff()
	{
		var policy = new ScannerRecoveryRetryPolicy();
		var handle = Handle(ErrorCode.Conflict);
		Assert.IsEmpty(policy.SelectDue(new[] { handle }, Epoch));
		Assert.HasCount(1, policy.SelectDue(
			new[] { handle }, Epoch + ScannerRecoveryRetryPolicy.InitialDelay));

		Assert.IsEmpty(policy.SelectDue(Array.Empty<ScannerCleanupRecoveryHandle>(), Epoch));

		var later = Epoch + TimeSpan.FromHours(1);
		Assert.IsEmpty(policy.SelectDue(new[] { handle }, later),
			"A re-observed handle re-arms at the initial delay instead of inheriting old state.");
		Assert.HasCount(1, policy.SelectDue(
			new[] { handle }, later + ScannerRecoveryRetryPolicy.InitialDelay));
	}

	private static ScannerCleanupRecoveryHandle Handle(ErrorCode code) => new(
		InteractionSessionId.New(),
		new InventoryActor(ConnectionId.New(), new AccountId(1), CharacterId.New()),
		SceneEntityId.New(),
		"exit",
		1,
		new OperationError(code, "injected"),
		Epoch,
		null);
}
