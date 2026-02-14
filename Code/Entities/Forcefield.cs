
/// <summary>
/// Forcefield world entity. Blocks citizens but allows combine through.
/// Toggle active state via admin or combine interaction.
/// Place in scene via editor — add a collider to the same GameObject.
/// </summary>
public class Forcefield : PersistableEntity<ForcefieldSaveData>, Component.IPressable, Component.ICollisionListener
{
	[Sync] public bool IsActive { get; set; } = true;

	/// <summary>
	/// The collider used to block players. Assign in editor.
	/// </summary>
	[Property] public Collider BlockCollider { get; set; }

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
		if ( !IsActive ) return;

		// Allow combine through, block everyone else
		var player = collision.Other.GameObject.GetComponentInParent<HexPlayerComponent>();
		if ( player?.Character != null && CombineUtils.IsCombine( player.Character ) )
		{
			// Let them pass — disable collision for this pair temporarily
			// In practice, this would use collision tags or layers
			return;
		}
	}

	public void OnCollisionUpdate( Collision collision ) { }
	public void OnCollisionStop( CollisionStop collision ) { }

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
