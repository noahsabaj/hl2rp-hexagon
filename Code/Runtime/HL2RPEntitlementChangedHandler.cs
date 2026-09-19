#nullable enable

using Hexagon.V2.Kernel.Events;
using HL2RP.V2.Features;

namespace HL2RP.V2.Runtime;

/// <summary>Adapts the entitlement-changed event to a host callback.</summary>
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
