#nullable enable

using Hexagon.V2.Kernel.Events;
using HL2RP.V2.Features;

namespace HL2RP.V2.Runtime;

// The bootstrap-operator scene component used to live here. Operator authority is a per-deployment
// fact, so it moved to the hl2rp-operator-accounts ConVar in HL2RPOperatorAccounts; keeping it in a
// scene meant an operator's platform account id was serialized into committed map content.

internal sealed class HL2RPEntitlementChangedHandler : IEventHandler<HL2RPAccountEntitlementChanged>
{
	private readonly Action<HL2RPAccountEntitlementChanged> _changed;

	public HL2RPEntitlementChangedHandler( Action<HL2RPAccountEntitlementChanged> changed ) =>
		_changed = changed ?? throw new ArgumentNullException( nameof(changed) );

	public void Handle( HL2RPAccountEntitlementChanged notification )
	{
		ArgumentNullException.ThrowIfNull( notification );
		_changed( notification );
	}
}
