#nullable enable

using Hexagon.V2.Domain;
using HL2RP.V2.Runtime;

namespace HL2RP.V2.Tests.Runtime;

[TestClass]
public sealed class CharacterLifecycleGateTests
{
	[TestMethod]
	public void ConcurrentLifecycleWorkOnOneConnectionIsRejectedUntilReleased()
	{
		var gate = new HL2RPCharacterLifecycleGate();
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		gate.Open( connection );

		Assert.IsTrue( gate.TryBeginOperation(
			connection, character, out var loadEpoch, out var load ) );
		Assert.IsFalse( gate.TryBeginOperation(
			connection, null, out _, out _ ) );
		Assert.IsTrue( gate.IsCurrent( connection, loadEpoch ) );

		load!.Dispose();
		Assert.IsTrue( gate.TryBeginOperation(
			connection, null, out var unloadEpoch, out var unload ) );
		Assert.IsTrue( gate.IsCurrent( connection, unloadEpoch ) );
		unload!.Dispose();
	}

	[TestMethod]
	public void CloseAndSameIdReconnectCannotReviveOrReleaseOldOperation()
	{
		var gate = new HL2RPCharacterLifecycleGate();
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		gate.Open( connection );
		Assert.IsTrue( gate.TryBeginOperation(
			connection, character, out var oldEpoch, out var oldOperation ) );

		gate.Close( connection );
		gate.Open( connection );
		Assert.IsFalse( gate.IsCurrent( connection, oldEpoch ) );
		Assert.IsTrue( gate.TryBeginOperation(
			connection, character, out var reconnectEpoch, out var reconnectOperation ) );

		oldOperation!.Dispose();
		Assert.IsTrue( gate.IsCurrent( connection, reconnectEpoch ) );
		Assert.IsFalse( gate.TryBeginOperation(
			connection, null, out _, out _ ) );
		reconnectOperation!.Dispose();
	}

	[TestMethod]
	public void CharacterReservationExcludesAnotherConnectionUntilReleased()
	{
		var gate = new HL2RPCharacterLifecycleGate();
		var first = ConnectionId.New();
		var second = ConnectionId.New();
		var character = CharacterId.New();
		gate.Open( first );
		gate.Open( second );

		Assert.IsTrue( gate.TryBeginOperation(
			first, character, out _, out var reservation ) );
		Assert.IsFalse( gate.TryBeginOperation(
			second, character, out _, out _ ) );

		reservation!.Dispose();
		Assert.IsTrue( gate.TryBeginOperation(
			second, character, out _, out var successor ) );
		successor!.Dispose();
	}

	[TestMethod]
	public void SameConnectionCannotTakeOverItsOwnInFlightCharacterOperation()
	{
		var gate = new HL2RPCharacterLifecycleGate();
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		gate.Open( connection );
		Assert.IsTrue( gate.TryBeginOperation(
			connection, character, out var firstEpoch, out var first ) );

		Assert.IsFalse( gate.TryBeginOperation(
			connection, character, out _, out _ ) );
		Assert.IsTrue( gate.IsCurrent( connection, firstEpoch ) );

		first!.Dispose();
		Assert.IsTrue( gate.TryBeginOperation(
			connection, character, out _, out var second ) );
		second!.Dispose();
	}

	[TestMethod]
	public void DisconnectReleasesConnectionAndCharacterReservations()
	{
		var gate = new HL2RPCharacterLifecycleGate();
		var connection = ConnectionId.New();
		var contender = ConnectionId.New();
		var character = CharacterId.New();
		gate.Open( connection );
		gate.Open( contender );
		Assert.IsTrue( gate.TryBeginOperation(
			connection, character, out _, out var disconnectedOperation ) );

		gate.Close( connection );
		Assert.IsTrue( gate.TryBeginOperation(
			contender, character, out _, out var successor ) );

		disconnectedOperation!.Dispose();
		Assert.IsFalse( gate.TryBeginOperation(
			contender, null, out _, out _ ) );
		successor!.Dispose();
	}
}
