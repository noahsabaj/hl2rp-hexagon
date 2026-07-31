#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Networking;
using Hexagon.V2.Runtime;
using HL2RP.V2.Domain;
using Sandbox;

namespace HL2RP.V2.World;

public enum HL2RPSceneFeatureKind
{
	Door = 0,
	Storage = 1,
	Vendor = 2,
	RationDispenser = 3,
	VendingMachine = 4,
	Forcefield = 5,
	ScannerDock = 6,
	ScannerDrone = 7,
	CombatTarget = 8,
	WorldItemArea = 9
}

/// <summary>
/// Typed, editor-authored presentation/configuration for one showcase entity.
/// Persistent identity and state remain owned by Hexagon's host application;
/// these components never perform mutations from a client press callback.
/// </summary>
public abstract class HL2RPSceneFeatureComponent : Component, Component.IPressable
{
	[RequireComponent]
	public PersistentSceneEntity PersistentIdentity { get; set; } = null!;

	public abstract HL2RPSceneFeatureKind Kind { get; }
	public abstract InteractionPolicy InteractionPolicy { get; }

	public PersistentSceneEntity? IdentityComponent =>
		PersistentIdentity ?? Components.Get<PersistentSceneEntity>();
	public SceneEntityId? SceneEntityId => IdentityComponent?.Identity;

	bool IPressable.CanPress( IPressable.Event e ) =>
		SceneEntityId is not null &&
		HexPlayerBody.IsLocalPredictionSource( e.Source ) &&
		HexagonRuntimeSystem.Current?.ClientReadiness == HexRuntimeReadiness.Ready &&
		HexagonRuntimeSystem.Current.ClientController is not null;

	bool IPressable.Press( IPressable.Event e )
	{
		if ( !((IPressable)this).CanPress( e ) || SceneEntityId is not SceneEntityId id ) return false;
		_ = HexagonRuntimeSystem.Current!.ClientController!.BeginInteractionAsync(
			new InteractionTargetInput( InteractionTargetInputKind.SceneEntity, id.Value ) );
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) => new(
		TooltipTitle,
		"touch_app",
		"The host validates range, line of sight, policy, and capability before opening this interaction." );

	private string TooltipTitle => Kind switch
	{
		HL2RPSceneFeatureKind.Door => "Use door",
		HL2RPSceneFeatureKind.Storage => "Open storage",
		HL2RPSceneFeatureKind.Vendor => "Browse vendor",
		HL2RPSceneFeatureKind.RationDispenser => "Request ration",
		HL2RPSceneFeatureKind.VendingMachine => "Use vending machine",
		HL2RPSceneFeatureKind.Forcefield => "Inspect forcefield",
		HL2RPSceneFeatureKind.ScannerDock => "Pilot scanner",
		HL2RPSceneFeatureKind.ScannerDrone => "Inspect scanner",
		HL2RPSceneFeatureKind.CombatTarget => "Inspect combat target",
		HL2RPSceneFeatureKind.WorldItemArea => "Inspect drop area",
		_ => "Interact"
	};
}

[Title( "HL2RP Ownable Door" )]
[Category( "HL2RP/Showcase" )]
[Icon( "door_front" )]
public sealed class HL2RPDoorComponent : HL2RPSceneFeatureComponent
{
	[Property] public bool Ownable { get; set; } = true;
	[Property] public bool InitiallyCombineLocked { get; set; }
	[Property] public bool InitiallyOpen { get; set; }
	[Property] public float OpenYawDegrees { get; set; } = 90f;

	private Rotation _closedRotation;
	private bool _closedRotationCaptured;

	public override HL2RPSceneFeatureKind Kind => HL2RPSceneFeatureKind.Door;
	public override InteractionPolicy InteractionPolicy => new()
	{
		SessionKind = InteractionSessionKind.Door
	};

	public void ApplyState( DoorEntityState state )
	{
		ArgumentNullException.ThrowIfNull( state );
		if ( !_closedRotationCaptured )
		{
			_closedRotation = GameObject.WorldRotation;
			_closedRotationCaptured = true;
		}
		GameObject.WorldRotation = _closedRotation *
			Rotation.FromYaw( state.IsOpen ? OpenYawDegrees : 0f );
		foreach ( var collider in Components.GetAll<Collider>() ) collider.Enabled = !state.IsOpen;
	}

	protected override void OnValidate()
	{
		base.OnValidate();
		OpenYawDegrees = Math.Clamp( OpenYawDegrees, -180f, 180f );
	}
}

[Title( "HL2RP Storage Container" )]
[Category( "HL2RP/Showcase" )]
[Icon( "inventory_2" )]
public sealed class HL2RPStorageComponent : HL2RPSceneFeatureComponent
{
	[Property] public int Width { get; set; } = 6;
	[Property] public int Height { get; set; } = 4;
	[Property] public bool InitiallyLocked { get; set; }

	public override HL2RPSceneFeatureKind Kind => HL2RPSceneFeatureKind.Storage;
	public override InteractionPolicy InteractionPolicy => new()
	{
		SessionKind = InteractionSessionKind.Storage
	};

	protected override void OnValidate()
	{
		base.OnValidate();
		Width = Math.Clamp( Width, 1, 16 );
		Height = Math.Clamp( Height, 1, 16 );
	}
}

[Title( "HL2RP Permit Vendor" )]
[Category( "HL2RP/Showcase" )]
[Icon( "storefront" )]
public sealed class HL2RPVendorComponent : HL2RPSceneFeatureComponent
{
	[Property] public BusinessPermitKind RequiredPermit { get; set; } = BusinessPermitKind.Food;
	[Property] public int InitialRationStock { get; set; } = 12;
	[Property] public long RationPrice { get; set; } = 10;
	[Property] public int InitialWaterStock { get; set; } = 12;
	[Property] public long WaterPrice { get; set; } = 6;

	public override HL2RPSceneFeatureKind Kind => HL2RPSceneFeatureKind.Vendor;
	public override InteractionPolicy InteractionPolicy => new()
	{
		SessionKind = InteractionSessionKind.Vendor
	};

	protected override void OnValidate()
	{
		base.OnValidate();
		InitialRationStock = Math.Max( 0, InitialRationStock );
		InitialWaterStock = Math.Max( 0, InitialWaterStock );
		RationPrice = Math.Max( 0, RationPrice );
		WaterPrice = Math.Max( 0, WaterPrice );
	}
}

public abstract class HL2RPMachineComponent : HL2RPSceneFeatureComponent
{
	[Property] public long InitialStock { get; set; } = 20;
	[Property] public long UnitPrice { get; set; } = 5;
	[Property] public float CooldownSeconds { get; set; } = 2f;

	public abstract string DispensedDefinitionId { get; }

	protected override void OnValidate()
	{
		base.OnValidate();
		InitialStock = Math.Max( 0, InitialStock );
		UnitPrice = Math.Max( 0, UnitPrice );
		CooldownSeconds = Math.Clamp( CooldownSeconds, 0f, 300f );
	}
}

[Title( "HL2RP Ration Dispenser" )]
[Category( "HL2RP/Showcase" )]
[Icon( "takeout_dining" )]
public sealed class HL2RPRationDispenserComponent : HL2RPMachineComponent
{
	public override HL2RPSceneFeatureKind Kind => HL2RPSceneFeatureKind.RationDispenser;
	public override string DispensedDefinitionId => HL2RPIds.Items.Ration;
	public override InteractionPolicy InteractionPolicy => new() { SessionKind = InteractionSessionKind.Vendor };
}

[Title( "HL2RP Vending Machine" )]
[Category( "HL2RP/Showcase" )]
[Icon( "local_drink" )]
public sealed class HL2RPVendingMachineComponent : HL2RPMachineComponent
{
	public override HL2RPSceneFeatureKind Kind => HL2RPSceneFeatureKind.VendingMachine;
	public override string DispensedDefinitionId => HL2RPIds.Items.Water;
	public override InteractionPolicy InteractionPolicy => new() { SessionKind = InteractionSessionKind.Vendor };
}

[Title( "HL2RP Forcefield" )]
[Category( "HL2RP/Showcase" )]
[Icon( "shield" )]
public sealed class HL2RPForcefieldComponent : HL2RPSceneFeatureComponent
{
	[Property] public bool InitiallyEnabled { get; set; } = true;
	[Property] public bool CombineOnly { get; set; } = true;

	public override HL2RPSceneFeatureKind Kind => HL2RPSceneFeatureKind.Forcefield;
	public override InteractionPolicy InteractionPolicy => new();

	public void ApplyState( ForcefieldEntityState state )
	{
		ArgumentNullException.ThrowIfNull( state );
		foreach ( var renderer in Components.GetAll<ModelRenderer>() )
			renderer.Enabled = ForcefieldEntityRules.IsVisible( state );
		// Combine-only fields are physical barriers with a collision-rule bypass
		// for Combine-tagged bodies. Public fields remain visible but non-solid.
		foreach ( var collider in Components.GetAll<Collider>() )
			collider.Enabled = ForcefieldEntityRules.IsSolid( state );
	}
}

[Title( "HL2RP Scanner Dock" )]
[Category( "HL2RP/Showcase" )]
[Icon( "precision_manufacturing" )]
public sealed class HL2RPScannerDockComponent : HL2RPSceneFeatureComponent
{
	[Property] public Guid DronePersistentId { get; set; }

	public override HL2RPSceneFeatureKind Kind => HL2RPSceneFeatureKind.ScannerDock;
	public override InteractionPolicy InteractionPolicy => new()
	{
		SessionKind = InteractionSessionKind.Scanner
	};

	public SceneEntityId? LinkedDroneId => DronePersistentId == Guid.Empty
		? null
		: new SceneEntityId( DronePersistentId );
}

[Title( "HL2RP Scanner Drone" )]
[Category( "HL2RP/Showcase" )]
[Icon( "flight" )]
public sealed class HL2RPScannerDroneComponent : HL2RPSceneFeatureComponent
{
	[Property] public float MaximumSpeed { get; set; } = 300f;
	[Property] public float MaximumAcceleration { get; set; } = 900f;

	public override HL2RPSceneFeatureKind Kind => HL2RPSceneFeatureKind.ScannerDrone;
	public override InteractionPolicy InteractionPolicy => new()
	{
		SessionKind = InteractionSessionKind.Scanner
	};

	protected override void OnValidate()
	{
		base.OnValidate();
		MaximumSpeed = Math.Clamp( MaximumSpeed, 1f, 1_000f );
		MaximumAcceleration = Math.Clamp( MaximumAcceleration, 1f, 4_000f );
	}
}

[Title( "HL2RP Combat Target" )]
[Category( "HL2RP/Showcase" )]
[Icon( "target" )]
public sealed class HL2RPCombatTargetComponent : HL2RPSceneFeatureComponent
{
	[Property] public long MaximumHealth { get; set; } = 100;

	public override HL2RPSceneFeatureKind Kind => HL2RPSceneFeatureKind.CombatTarget;
	public override InteractionPolicy InteractionPolicy => new();

	protected override void OnValidate()
	{
		base.OnValidate();
		MaximumHealth = Math.Clamp( MaximumHealth, 1, 1_000_000 );
	}
}

[Title( "HL2RP World Item Area" )]
[Category( "HL2RP/Showcase" )]
[Icon( "move_to_inbox" )]
public sealed class HL2RPWorldItemAreaComponent : HL2RPSceneFeatureComponent
{
	[Property] public float Radius { get; set; } = 160f;

	public override HL2RPSceneFeatureKind Kind => HL2RPSceneFeatureKind.WorldItemArea;
	public override InteractionPolicy InteractionPolicy => new()
	{
		RequireLineOfSight = false
	};

	protected override void OnValidate()
	{
		base.OnValidate();
		Radius = Math.Clamp( Radius, 16f, 2_048f );
	}
}
