#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using HL2RP.V2.Domain;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Showcase.Combat;

public sealed record ProtectiveVestDamagePlan(
	long IncomingDamage,
	long AbsorbedDamage,
	long AppliedDamage,
	ProtectiveVestItemState NextState);

public static class ProtectiveVestDamagePlanner
{
	public static OperationResult<ProtectiveVestDamagePlan> Plan(
		ProtectiveVestItemState state,
		long incomingDamage)
	{
		ArgumentNullException.ThrowIfNull(state);
		if (incomingDamage <= 0)
			return OperationResult<ProtectiveVestDamagePlan>.Failure(ErrorCode.InvalidArgument,
				"Incoming damage must be positive.");
		if (!state.Equipped || state.Durability <= 0)
			return OperationResult<ProtectiveVestDamagePlan>.Failure(ErrorCode.PolicyDenied,
				"Protective vest is not equipped or has no durability.");
		if (state.DamageReductionPermille is < 0 or > 1000)
			return OperationResult<ProtectiveVestDamagePlan>.Failure(ErrorCode.PersistedTypeInvalid,
				"Protective vest reduction is outside registered limits.");

		var nominalAbsorption = incomingDamage / 1000 * state.DamageReductionPermille
			+ incomingDamage % 1000 * state.DamageReductionPermille / 1000;
		var absorbed = Math.Min(nominalAbsorption, state.Durability);
		var applied = incomingDamage - absorbed;
		var durability = checked(state.Durability - (int)absorbed);
		return OperationResult<ProtectiveVestDamagePlan>.Success(new ProtectiveVestDamagePlan(
			incomingDamage,
			absorbed,
			applied,
			state with { Durability = durability, Equipped = durability > 0 }));
	}
}
