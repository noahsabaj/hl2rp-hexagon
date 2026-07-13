#nullable enable

using Sandbox;
using Hexagon.V2.Kernel.Events;
using HL2RP.V2.Features;

namespace HL2RP.V2.Runtime;

/// <summary>
/// Editor-authored bootstrap authority for the first entitlement administrator.
/// Values are authenticated platform account IDs, never character names or
/// client-authored replicated identity. Leave empty only when no bootstrap
/// operator should exist for this scene.
/// </summary>
[Title( "HL2RP Bootstrap Operators" )]
[Category( "HL2RP" )]
[Icon( "admin_panel_settings" )]
public sealed class HL2RPBootstrapOperatorsComponent : Component
{
	[Property]
	[Title( "Authenticated Account IDs" )]
	[Description( "Comma, semicolon, or whitespace separated unsigned account IDs allowed to grant the first persisted entitlement." )]
	public string AccountIds { get; set; } = string.Empty;

	internal OperationResult<HL2RPBootstrapOperatorDirectory> Parse() =>
		HL2RPBootstrapOperatorDirectory.Parse( AccountIds );
}

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
