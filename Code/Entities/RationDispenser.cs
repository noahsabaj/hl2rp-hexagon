
/// <summary>
/// Ration dispenser world entity. Citizens insert CID to receive rations.
/// Place in scene via editor. CP can toggle disabled state.
/// States: Idle, Checking, Arming, Dispensing, Error, Disabled.
/// </summary>
public class RationDispenser : PersistableEntity<DispenserSaveData>, Component.IPressable
{
	public enum DispenserState
	{
		Idle,
		Checking,
		Arming,
		Dispensing,
		Error,
		Disabled
	}

	[Sync] public DispenserState State { get; set; } = DispenserState.Idle;
	[Sync] public bool IsDisabled { get; set; }

	private Dictionary<int, DateTime> _cooldowns = new();
	private DateTime _errorTime;

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

		// Auto-reset error state after 3 seconds
		if ( State == DispenserState.Error && (DateTime.UtcNow - _errorTime).TotalSeconds > 3.0 )
		{
			State = DispenserState.Idle;
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

		var cidNumber = cidItem.GetData<int>( "id", 0 );
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

		// Dispense
		State = DispenserState.Dispensing;

		var isPriority = cidItem.GetData<bool>( "cwu", false );
		var rationCount = isPriority ? 2 : 1;

		var inventories = InventoryManager.LoadForCharacter( player.Character.Id );
		for ( int i = 0; i < rationCount; i++ )
		{
			var ration = ItemManager.CreateInstance( "ration", player.Character.Id );
			if ( inventories.Count > 0 )
				inventories[0].Add( ration );
		}

		_cooldowns[cidNumber] = DateTime.UtcNow;
		State = DispenserState.Idle;
		SaveState();

		Log.Info( $"[HL2RP] Dispenser: Gave {rationCount} ration(s) to CID #{cidNumber}" );
		return true;
	}

	public Component.IPressable.Tooltip? GetTooltip( Component.IPressable.Event e )
	{
		var desc = State switch
		{
			DispenserState.Disabled => "Disabled",
			DispenserState.Error => "Error",
			DispenserState.Idle => "Insert CID to receive rations",
			_ => "Processing..."
		};

		return new Component.IPressable.Tooltip( "Ration Dispenser", "local_shipping", desc );
	}

	private void SetError()
	{
		State = DispenserState.Error;
		_errorTime = DateTime.UtcNow;
	}
}

public class DispenserSaveData
{
	public bool IsDisabled { get; set; }
}
