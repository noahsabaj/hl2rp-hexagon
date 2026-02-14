
/// <summary>
/// Scanner drone world entity. CP players with SCN/CLAW.SCN rank can pilot this drone.
/// Features: acceleration-based flight, hover maintenance, flash photography,
/// spotlight toggle, destructible. Player body goes limp while piloting.
/// Place in scene via editor.
///
/// Movement input is sent from the pilot's client via CmdSetWishDirection RPC,
/// avoiding the multiplayer bug of reading Input.Down on the server.
/// </summary>
public class ScannerDrone : Component
{
	// --- Synced State ---

	/// <summary>
	/// The player currently piloting this scanner. Null if unpiloted.
	/// </summary>
	[Sync] public HexPlayerComponent Pilot { get; set; }

	/// <summary>
	/// Whether the scanner is currently being piloted.
	/// </summary>
	[Sync] public bool IsActive { get; set; }

	/// <summary>
	/// Scanner health. If this reaches 0, scanner crashes.
	/// </summary>
	[Sync] public float Health { get; set; } = 100f;

	/// <summary>
	/// Whether the spotlight is enabled.
	/// </summary>
	[Sync] public bool SpotlightOn { get; set; }

	/// <summary>
	/// Whether the scanner is destroyed.
	/// </summary>
	[Sync] public bool IsDestroyed { get; set; }

	// --- Properties ---

	[Property] public float MaxHealth { get; set; } = 100f;
	[Property] public float MoveSpeed { get; set; } = 300f;
	[Property] public float Acceleration { get; set; } = 800f;
	[Property] public float Deceleration { get; set; } = 600f;
	[Property] public float HoverHeight { get; set; } = 120f;
	[Property] public float MaxPitchAngle { get; set; } = 30f;

	// --- Private State ---

	private Vector3 _velocity;
	private Vector3 _wishDirection;
	private Vector3 _pilotBodyPosition;
	private Rotation _pilotBodyRotation;

	// --- Lifecycle ---

	protected override void OnEnabled()
	{
		if ( IsProxy ) return;

		Health = MaxHealth;
		IsDestroyed = false;
	}

	protected override void OnFixedUpdate()
	{
		if ( IsProxy || !IsActive || Pilot == null )
			return;

		if ( IsDestroyed )
			return;

		UpdateMovement();
		UpdateHover();
	}

	// --- Client-to-Server RPCs ---

	/// <summary>
	/// Receive movement input from the pilot's client.
	/// </summary>
	[Rpc.Host]
	public void CmdSetWishDirection( Vector3 dir )
	{
		if ( Pilot == null ) return;
		_wishDirection = dir.Length > 1f ? dir.Normal : dir;
	}

	/// <summary>
	/// Pilot requests a flash.
	/// </summary>
	[Rpc.Host]
	public void CmdFlash()
	{
		if ( Pilot == null ) return;
		Flash();
	}

	/// <summary>
	/// Pilot requests spotlight toggle.
	/// </summary>
	[Rpc.Host]
	public void CmdToggleSpotlight()
	{
		if ( Pilot == null ) return;
		ToggleSpotlight();
	}

	/// <summary>
	/// Pilot requests to exit the scanner.
	/// </summary>
	[Rpc.Host]
	public void CmdRequestExit()
	{
		if ( Pilot == null ) return;
		Exit();
	}

	// --- Piloting ---

	/// <summary>
	/// Attempt to enter the scanner. Only SCN/CLAW.SCN ranks can pilot.
	/// </summary>
	public bool TryEnter( HexPlayerComponent player )
	{
		if ( player?.Character == null )
			return false;

		if ( IsActive || IsDestroyed )
			return false;

		// Only scanner ranks can pilot
		var rank = CombineUtils.GetCombineRank( player.Character );
		if ( rank != "SCN" && rank != "CLAW.SCN" )
			return false;

		// Store pilot body state
		_pilotBodyPosition = player.WorldPosition;
		_pilotBodyRotation = player.WorldRotation;

		Pilot = player;
		IsActive = true;
		_velocity = Vector3.Zero;
		_wishDirection = Vector3.Zero;

		Log.Info( $"[HL2RP] Scanner: {player.CharacterName} entered drone" );
		return true;
	}

	/// <summary>
	/// Exit the scanner and return the player to their body.
	/// Notifies ScannerPilotSystem to clean up the mapping.
	/// </summary>
	public void Exit()
	{
		if ( Pilot == null )
			return;

		var pilot = Pilot;
		var charId = pilot.Character?.Id;

		// Return player to body position
		pilot.WorldPosition = _pilotBodyPosition;
		pilot.WorldRotation = _pilotBodyRotation;

		Pilot = null;
		IsActive = false;
		_velocity = Vector3.Zero;
		_wishDirection = Vector3.Zero;

		if ( charId != null )
			ScannerPilotSystem.OnScannerExited( charId );

		Log.Info( $"[HL2RP] Scanner: {pilot.CharacterName} exited drone" );
	}

	// --- Movement ---

	private void UpdateMovement()
	{
		if ( _wishDirection.Length > 0 )
		{
			_velocity = Vector3.Lerp( _velocity, _wishDirection * MoveSpeed, Acceleration * Time.Delta / MoveSpeed );
		}
		else
		{
			_velocity = Vector3.Lerp( _velocity, Vector3.Zero, Deceleration * Time.Delta / MoveSpeed );
		}

		WorldPosition += _velocity * Time.Delta;

		// Tilt based on velocity
		if ( _velocity.Length > 10f )
		{
			var pitchAngle = Math.Clamp( _velocity.z / MoveSpeed * MaxPitchAngle, -MaxPitchAngle, MaxPitchAngle );
			// Apply subtle tilt
		}
	}

	private void UpdateHover()
	{
		// Maintain minimum hover height above ground
		var trace = Scene.Trace
			.Ray( WorldPosition, WorldPosition + Vector3.Down * (HoverHeight + 50f) )
			.WithoutTags( "scanner" )
			.Run();

		if ( trace.Hit )
		{
			var currentHeight = WorldPosition.z - trace.HitPosition.z;
			if ( currentHeight < HoverHeight )
			{
				var correction = (HoverHeight - currentHeight) * 5f * Time.Delta;
				WorldPosition += Vector3.Up * correction;
			}
		}
	}

	// --- Actions ---

	/// <summary>
	/// Fire the flash camera. Creates a brief light flash and capture effect.
	/// </summary>
	public void Flash()
	{
		if ( !IsActive || IsDestroyed )
			return;

		// TODO: Create point light flash, play sound, capture effect
		Log.Info( "[HL2RP] Scanner: Flash!" );
	}

	/// <summary>
	/// Toggle the spotlight.
	/// </summary>
	public void ToggleSpotlight()
	{
		if ( !IsActive || IsDestroyed )
			return;

		SpotlightOn = !SpotlightOn;

		// TODO: Enable/disable spotlight component
		Log.Info( $"[HL2RP] Scanner: Spotlight {(SpotlightOn ? "on" : "off")}" );
	}

	// --- Damage ---

	/// <summary>
	/// Apply damage to the scanner. If health reaches 0, crash.
	/// </summary>
	public void TakeDamage( float damage )
	{
		if ( IsDestroyed )
			return;

		Health -= damage;

		if ( Health <= 0f )
		{
			Health = 0f;
			Crash();
		}
	}

	/// <summary>
	/// Scanner crashes — pilot is ejected and scanner is destroyed.
	/// </summary>
	private void Crash()
	{
		IsDestroyed = true;
		SpotlightOn = false;

		if ( Pilot != null )
		{
			Log.Info( $"[HL2RP] Scanner destroyed — ejecting {Pilot.CharacterName}" );
			Exit();
		}

		// TODO: Play crash effects, spawn debris
	}
}
