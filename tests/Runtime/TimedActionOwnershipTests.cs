#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using HL2RP.V2.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HL2RP.V2.Tests.Runtime;

[TestClass]
public sealed class TimedActionOwnershipTests
{
	[TestMethod]
	public async Task CancelAndCommitRaceHasExactlyOneWinner()
	{
		for ( var iteration = 0; iteration < 2_000; iteration++ )
		{
			var ownership = new HL2RPTimedActionOwnership<Guid, object>();
			var key = Guid.NewGuid();
			var action = new object();
			Assert.IsTrue( ownership.TryAdd( key, action ) );
			var start = new ManualResetEventSlim( false );
			var cancel = Task.Run( () =>
			{
				start.Wait();
				return ownership.TryCancel( key, candidate => ReferenceEquals( candidate, action ), out _ );
			} );
			var commit = Task.Run( () =>
			{
				start.Wait();
				return ownership.TryClaimCommit( key, action );
			} );
			start.Set();
			await Task.WhenAll( cancel, commit );

			Assert.AreNotEqual(
				cancel.Result == HL2RPTimedActionCancelOutcome.Cancelled,
				commit.Result,
				"Cancellation and durable commit ownership must not both win." );
			if ( commit.Result )
			{
				Assert.AreEqual( HL2RPTimedActionCancelOutcome.CommitOwned, cancel.Result );
				var completion = ownership.Complete( key, action, durableSuccess: true );
				Assert.IsTrue( completion.Owned );
				Assert.IsFalse( completion.PublishFailure );
			}
			else
			{
				Assert.AreEqual( HL2RPTimedActionCancelOutcome.Cancelled, cancel.Result );
				Assert.IsFalse( ownership.Complete( key, action, durableSuccess: true ).Owned );
			}
		}
	}

	[TestMethod]
	public void LifecycleCancellationCannotRemoveCommitOwnerOrHideItsReceipt()
	{
		var ownership = new HL2RPTimedActionOwnership<Guid, object>();
		var key = Guid.NewGuid();
		var action = new object();
		Assert.IsTrue( ownership.TryAdd( key, action ) );
		Assert.IsTrue( ownership.TryClaimCommit( key, action ) );

		Assert.IsEmpty( ownership.CancelForLifecycle( key ) );
		Assert.AreEqual(
			HL2RPTimedActionCancelOutcome.CommitOwned,
			ownership.TryCancel( key, _ => true, out _ ) );
		var completion = ownership.Complete( key, action, durableSuccess: true );

		Assert.IsTrue( completion.Owned );
		Assert.IsFalse( completion.PublishFailure );
		Assert.IsTrue( completion.LifecycleCleanupRequested );
	}

	[TestMethod]
	public void PreCommitLifecycleCancellationCleansOnceAndSuppressesOwnerFinalizer()
	{
		var ownership = new HL2RPTimedActionOwnership<Guid, object>();
		var key = Guid.NewGuid();
		var action = new object();
		Assert.IsTrue( ownership.TryAdd( key, action ) );

		var cancelled = ownership.CancelForLifecycle( key );

		Assert.HasCount( 1, cancelled );
		Assert.AreSame( action, cancelled[0] );
		Assert.IsEmpty( ownership.CancelForLifecycle( key ) );
		Assert.IsFalse( ownership.TryClaimCommit( key, action ) );
		Assert.IsFalse( ownership.Complete( key, action, durableSuccess: false ).Owned );
	}
}
