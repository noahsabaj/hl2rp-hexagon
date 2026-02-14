using Hexagon.Persistence;

/// <summary>
/// Combine lock world entity. Attaches to a door to restrict access.
/// CP/OW can toggle lock state. CP can detonate (crouch+use, 10s).
/// Non-combine are blocked when locked. Place in scene via editor.
/// </summary>
public class CombineLock : Component, Component.IPressable
{
	public enum LockState
	{
		Locked,
		Unlocked,
		Error,
		Detonating
	}

	[Property] public string PersistenceId { get; set; } = "";

	/// <summary>
	/// Reference to the door this lock is attached to. Set in the editor.
	/// </summary>
	[Property] public DoorComponent Door { get; set; }

	[Sync] public LockState State { get; set; } = LockState.Locked;

	/// <summary>
	/// Detonation countdown remaining in seconds.
	/// </summary>
	[Sync] public float DetonationTimer { get; set; }

	private string _detonatingPlayerId;

	protected override void OnEnabled()
	{
		if ( IsProxy ) return;

		if ( string.IsNullOrEmpty( PersistenceId ) )
			PersistenceId = DatabaseManager.NewId();

		LoadState();

		// Apply lock state to door
		if ( Door != null )
			Door.SetLocked( State == LockState.Locked );
	}

	protected override void OnDisabled()
	{
		if ( IsProxy ) return;
		SaveState();
	}

	protected override void OnFixedUpdate()
	{
		if ( IsProxy ) return;

		if ( State == LockState.Detonating )
		{
			DetonationTimer -= Time.Delta;

			if ( DetonationTimer <= 0f )
			{
				Detonate();
			}
		}
	}

	// --- IPressable ---

	private HexPlayerComponent GetPlayer( Component.IPressable.Event e )
	{
		return e.Source?.GetComponentInParent<HexPlayerComponent>();
	}

	public bool CanPress( Component.IPressable.Event e )
	{
		var player = GetPlayer( e );
		return player?.Character != null;
	}

	public bool Press( Component.IPressable.Event e )
	{
		var player = GetPlayer( e );
		if ( player?.Character == null ) return false;

		// Non-combine cannot interact
		if ( !CombineUtils.IsCombine( player.Character ) )
		{
			if ( State == LockState.Locked )
				return false;

			// Unlocked — allow door use
			return true;
		}

		// Cancel detonation if someone presses during countdown
		if ( State == LockState.Detonating )
		{
			CancelDetonation();
			return true;
		}

		// Check if crouching for detonation (CP only)
		if ( CombineUtils.IsCP( player.Character ) && Input.Down( "duck" ) )
		{
			StartDetonation( player );
			return true;
		}

		// Toggle lock state
		ToggleLock();
		Log.Info( $"[HL2RP] Combine lock {PersistenceId}: {State} by {player.CharacterName}" );
		return true;
	}

	public Component.IPressable.Tooltip? GetTooltip( Component.IPressable.Event e )
	{
		var desc = State switch
		{
			LockState.Locked => "Locked",
			LockState.Unlocked => "Unlocked",
			LockState.Detonating => $"Detonating... {DetonationTimer:F1}s",
			LockState.Error => "Error",
			_ => ""
		};

		return new Component.IPressable.Tooltip( "Combine Lock", "lock", desc );
	}

	// --- Lock Actions ---

	private void ToggleLock()
	{
		if ( State == LockState.Locked )
		{
			State = LockState.Unlocked;
			if ( Door != null ) Door.SetLocked( false );
		}
		else
		{
			State = LockState.Locked;
			if ( Door != null ) Door.SetLocked( true );
		}

		SaveState();
	}

	private void StartDetonation( HexPlayerComponent player )
	{
		State = LockState.Detonating;
		DetonationTimer = 10f;
		_detonatingPlayerId = player.Character.Id;
		Log.Info( $"[HL2RP] Combine lock {PersistenceId}: Detonation started by {player.CharacterName}" );
	}

	private void CancelDetonation()
	{
		State = LockState.Locked;
		DetonationTimer = 0f;
		_detonatingPlayerId = null;
		Log.Info( $"[HL2RP] Combine lock {PersistenceId}: Detonation cancelled" );
	}

	private void Detonate()
	{
		State = LockState.Unlocked;
		DetonationTimer = 0f;
		_detonatingPlayerId = null;

		if ( Door != null )
		{
			Door.SetLocked( false );
			Door.IsOpen = true;

			// Apply blast force to door if it has a rigidbody
			var rb = Door.Components.Get<Rigidbody>();
			if ( rb != null )
			{
				var force = Door.WorldRotation.Forward * 5000f;
				rb.ApplyForce( force );
			}
		}

		SaveState();
		Log.Info( $"[HL2RP] Combine lock {PersistenceId}: Detonated" );
	}

	// --- Persistence ---

	private void SaveState()
	{
		DatabaseManager.Save( "hl2rp_locks", PersistenceId, new CombineLockSaveData
		{
			IsLocked = State == LockState.Locked,
			DoorId = Door?.DoorId ?? ""
		} );
	}

	private void LoadState()
	{
		var saved = DatabaseManager.Load<CombineLockSaveData>( "hl2rp_locks", PersistenceId );
		if ( saved != null )
		{
			State = saved.IsLocked ? LockState.Locked : LockState.Unlocked;
		}
	}
}

public class CombineLockSaveData
{
	public bool IsLocked { get; set; } = true;
	public string DoorId { get; set; } = "";
}
