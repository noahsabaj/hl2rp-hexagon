
/// <summary>
/// Ration dispenser world entity. Citizens insert CID to receive rations.
/// Place in scene via editor. CP can toggle disabled state.
/// Dispensing takes 2 seconds before items are given, making the state visible.
/// </summary>
public class RationDispenser : PersistableEntity<DispenserSaveData>, Component.IPressable
{
	public enum DispenserState
	{
		Idle,
		Dispensing,
		Error,
		Disabled
	}

	private const float DispenseDelay = 2f;
	private const float ErrorDuration = 3f;

	[Sync] public DispenserState State { get; set; } = DispenserState.Idle;
	[Sync] public bool IsDisabled { get; set; }

	private Dictionary<int, DateTime> _cooldowns = new();
	private TimeSince _stateTimer;

	// Pending dispense data (stored while in Dispensing state)
	private HexPlayerComponent _pendingPlayer;
	private int _pendingCidNumber;
	private bool _pendingPriority;

	protected override string CollectionName => "hl2rp_dispensers";

	protected override void OnLoadState( DispenserSaveData data )
	{
		IsDisabled = data.IsDisabled;
		State = IsDisabled ? DispenserState.Disabled : DispenserState.Idle;
	}

	protected override DispenserSaveData CreateSaveData()
	{
		return new DispenserSaveData { IsDisabled = IsDisabled };
	}

	protected override void OnFixedUpdate()
	{
		if ( IsProxy ) return;

		if ( State == DispenserState.Error && _stateTimer > ErrorDuration )
		{
			State = DispenserState.Idle;
		}

		if ( State == DispenserState.Dispensing && _stateTimer > DispenseDelay )
		{
			CompleteDispensing();
		}
	}

	// --- IPressable ---

	public bool CanPress( Component.IPressable.Event e )
	{
		var player = e.GetPlayer();
		return player?.Character != null;
	}

	public bool Press( Component.IPressable.Event e )
	{
		var player = e.GetPlayer();
		if ( player?.Character == null ) return false;

		// CP/OW can toggle disabled state
		if ( CombineUtils.IsCombine( player.Character ) )
		{
			IsDisabled = !IsDisabled;
			State = IsDisabled ? DispenserState.Disabled : DispenserState.Idle;
			SaveState();
			Log.Info( $"[HL2RP] Dispenser {PersistenceId}: {(IsDisabled ? "disabled" : "enabled")} by {player.CharacterName}" );
			return true;
		}

		if ( IsDisabled || State != DispenserState.Idle )
			return false;

		// Check for CID in inventory
		var cidItem = player.Character.FindItem( "cid_card" );

		if ( cidItem == null )
		{
			SetError();
			return false;
		}

		var trait = cidItem.GetTrait<CIDTrait>();
		var cidNumber = trait.Id;
		var cooldown = HexConfig.Get<float>( "hl2rp.dispenser.cooldown", 300f );

		// Check cooldown
		if ( _cooldowns.TryGetValue( cidNumber, out var lastUse ) )
		{
			if ( (DateTime.UtcNow - lastUse).TotalSeconds < cooldown )
			{
				SetError();
				return false;
			}
		}

		// Begin dispensing — items are given after DispenseDelay
		State = DispenserState.Dispensing;
		_stateTimer = 0;
		_pendingPlayer = player;
		_pendingCidNumber = cidNumber;
		_pendingPriority = trait.CWU;

		Log.Info( $"[HL2RP] Dispenser {PersistenceId}: Dispensing for CID #{cidNumber}..." );
		return true;
	}

	public Component.IPressable.Tooltip? GetTooltip( Component.IPressable.Event e )
	{
		var desc = State switch
		{
			DispenserState.Disabled => "Disabled",
			DispenserState.Error => "Error",
			DispenserState.Dispensing => "Dispensing...",
			DispenserState.Idle => "Insert CID to receive rations",
			_ => ""
		};

		return new Component.IPressable.Tooltip( "Ration Dispenser", "local_shipping", desc );
	}

	private void SetError()
	{
		State = DispenserState.Error;
		_stateTimer = 0;
	}

	private void CompleteDispensing()
	{
		if ( _pendingPlayer?.Character == null )
		{
			State = DispenserState.Idle;
			_pendingPlayer = null;
			return;
		}

		var rationCount = _pendingPriority ? 2 : 1;
		var inventories = InventoryManager.LoadForCharacter( _pendingPlayer.Character.Id );
		for ( int i = 0; i < rationCount; i++ )
		{
			var ration = ItemManager.CreateInstance( "ration", _pendingPlayer.Character.Id );
			if ( inventories.Count > 0 )
				inventories[0].Add( ration );
		}

		_cooldowns[_pendingCidNumber] = DateTime.UtcNow;
		_pendingPlayer = null;
		State = DispenserState.Idle;
		SaveState();

		Log.Info( $"[HL2RP] Dispenser: Gave {rationCount} ration(s) to CID #{_pendingCidNumber}" );
	}
}

public class DispenserSaveData
{
	public bool IsDisabled { get; set; }
}
