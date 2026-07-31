#nullable enable

using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using HL2RP.V2.Runtime;

namespace HL2RP.V2.Tests.Runtime;

[TestClass]
public sealed class CharacterLoadAdmissionTests
{
	[TestMethod]
	public void CharacterAlreadyBoundToAnotherConnectionFailsClosed()
	{
		var requester = ConnectionId.New();
		var other = ConnectionId.New();
		var target = CharacterId.New();

		var result = HL2RPCharacterLoadAdmission.Validate(
			requester,
			target,
			new[] { new HL2RPActiveCharacterBinding( other, target ) } );

		Assert.AreEqual( ErrorCode.Conflict, result.Error!.Code );
	}

	[TestMethod]
	public void RequestersOwnBindingAndUnrelatedBindingsRemainEligible()
	{
		var requester = ConnectionId.New();
		var target = CharacterId.New();
		var bindings = new[]
		{
			new HL2RPActiveCharacterBinding( requester, target ),
			new HL2RPActiveCharacterBinding( ConnectionId.New(), CharacterId.New() ),
			new HL2RPActiveCharacterBinding( ConnectionId.New(), null )
		};

		Assert.IsTrue( HL2RPCharacterLoadAdmission.Validate(
			requester, target, bindings ).Succeeded );
	}
}
