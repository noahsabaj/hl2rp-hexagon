#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;

namespace HL2RP.V2.Runtime;

public readonly record struct HL2RPActiveCharacterBinding(
	ConnectionId ConnectionId,
	CharacterId? CharacterId );

/// <summary>
/// Pure admission rule for the runtime directories that require one live
/// connection per character. The target character's persisted revision remains
/// the reservation between simultaneous loads that have not bound yet.
/// </summary>
public static class HL2RPCharacterLoadAdmission
{
	public static OperationResult Validate(
		ConnectionId requestingConnection,
		CharacterId targetCharacter,
		IEnumerable<HL2RPActiveCharacterBinding> bindings )
	{
		ArgumentNullException.ThrowIfNull( bindings );
		return bindings.Any( binding =>
			binding.ConnectionId != requestingConnection &&
			binding.CharacterId == targetCharacter )
			? OperationResult.Failure(
				ErrorCode.Conflict,
				"Character is already active on another connection." )
			: OperationResult.Success();
	}
}
