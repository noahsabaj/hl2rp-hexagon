
/// <summary>
/// Forcefield world entity. Blocks citizens but allows combine through.
/// Toggle active state via admin or combine interaction.
/// Place in scene via editor — assign a trigger collider to BlockCollider.
///
/// Uses a trigger collider for selective filtering: combine players pass through
/// freely, all other objects are pushed away continuously while inside.
/// </summary>
public class Forcefield : PersistableEntity<ForcefieldSaveData>, Component.IPressable, Component.ICollisionListener
{
	[Sync] public bool IsActive { get; set; } = true;

	/// <summary>
	/// The trigger collider used to detect and push back unauthorized players.
	/// Must be set as a trigger (IsTrigger = true) in the editor.
	/// </summary>
	[Property] public Collider BlockCollider { get; set; }

	/// <summary>
	/// Force applied to push back non-combine objects per frame.
	/// </summary>
	[Property] public float PushForce { get; set; } = 300f;

	protected override string CollectionName => "hl2rp_forcefields";

	protected override void OnLoadState( ForcefieldSaveData data )
	{
		IsActive = data.IsActive;
	}

	protected override void OnStateLoaded()
	{
		UpdateCollider();
	}

	protected override ForcefieldSaveData CreateSaveData()
	{
		return new ForcefieldSaveData { IsActive = IsActive };
	}

	// --- IPressable ---

	public bool CanPress( Component.IPressable.Event e )
	{
		var player = e.GetPlayer();
		if ( player?.Character == null ) return false;

		return CombineUtils.IsCombine( player.Character ) || player.Character.HasFlag( 'a' );
	}

	public bool Press( Component.IPressable.Event e )
	{
		var player = e.GetPlayer();
		if ( player?.Character == null ) return false;

		if ( !CombineUtils.IsCombine( player.Character ) && !player.Character.HasFlag( 'a' ) )
			return false;

		IsActive = !IsActive;
		UpdateCollider();
		SaveState();

		Log.Info( $"[HL2RP] Forcefield {PersistenceId}: {(IsActive ? "activated" : "deactivated")} by {player.CharacterName}" );
		return true;
	}

	public Component.IPressable.Tooltip? GetTooltip( Component.IPressable.Event e )
	{
		var desc = IsActive ? "Active — blocking passage" : "Inactive";
		return new Component.IPressable.Tooltip( "Forcefield", "shield", desc );
	}

	// --- Collision Filtering ---

	public void OnCollisionStart( Collision collision )
	{
		PushBackIfUnauthorized( collision );
	}

	public void OnCollisionUpdate( Collision collision )
	{
		PushBackIfUnauthorized( collision );
	}

	public void OnCollisionStop( CollisionStop collision ) { }

	/// <summary>
	/// Check if the colliding object is authorized. Combine players pass through;
	/// everything else gets pushed away from the forcefield center.
	/// </summary>
	private void PushBackIfUnauthorized( Collision collision )
	{
		if ( !IsActive ) return;

		var go = collision.Other.GameObject;
		var player = go.GetComponentInParent<HexPlayerComponent>();

		// Combine players pass through freely
		if ( player?.Character != null && CombineUtils.IsCombine( player.Character ) )
			return;

		// Push non-authorized objects away from the forcefield
		var pushDir = (go.WorldPosition - WorldPosition).WithZ( 0 ).Normal;
		if ( pushDir.LengthSquared < 0.01f )
			pushDir = WorldRotation.Forward;

		go.WorldPosition += pushDir * PushForce * Time.Delta;
	}

	// --- Internal ---

	private void UpdateCollider()
	{
		if ( BlockCollider != null )
			BlockCollider.Enabled = IsActive;
	}
}

public class ForcefieldSaveData
{
	public bool IsActive { get; set; } = true;
}
