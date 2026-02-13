
/// <summary>
/// Vending machine world entity. Citizens buy water variants for tokens.
/// CP can refill or toggle active state. Place in scene via editor.
/// </summary>
public class VendingMachine : Component, Component.IPressable
{
	[Property] public string PersistenceId { get; set; } = "";

	/// <summary>
	/// Stock level for button 1 (Water, 5 tokens).
	/// </summary>
	[Sync] public int StockButton1 { get; set; } = 10;

	/// <summary>
	/// Stock level for button 2 (Sparkling Water, 15 tokens).
	/// </summary>
	[Sync] public int StockButton2 { get; set; } = 5;

	/// <summary>
	/// Stock level for button 3 (Special Water, 20 tokens).
	/// </summary>
	[Sync] public int StockButton3 { get; set; } = 5;

	[Sync] public bool IsActive { get; set; } = true;

	/// <summary>
	/// Which button was last selected by a citizen (0 = none). Used for two-press buy.
	/// Key = character ID, Value = selected button (1-3).
	/// </summary>
	private Dictionary<string, int> _selectedButton = new();

	private static readonly (string ItemId, int Price)[] Buttons = new[]
	{
		("water", 5),
		("water_sparkling", 15),
		("water_special", 20)
	};

	protected override void OnEnabled()
	{
		if ( IsProxy ) return;

		if ( string.IsNullOrEmpty( PersistenceId ) )
			PersistenceId = DatabaseManager.NewId();

		LoadState();
	}

	protected override void OnDisabled()
	{
		if ( IsProxy ) return;
		SaveState();
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

		// CP/OW: refill or toggle
		if ( CombineUtils.IsCombine( player.Character ) )
		{
			return HandleCombineUse( player );
		}

		if ( !IsActive )
			return false;

		return HandleCitizenUse( player );
	}

	public Component.IPressable.Tooltip? GetTooltip( Component.IPressable.Event e )
	{
		if ( !IsActive )
			return new Component.IPressable.Tooltip( "Vending Machine", "local_cafe", "Inactive" );

		var desc = $"Water ({StockButton1}) | Sparkling ({StockButton2}) | Special ({StockButton3})";
		return new Component.IPressable.Tooltip( "Vending Machine", "local_cafe", desc );
	}

	private bool HandleCombineUse( HexPlayerComponent player )
	{
		if ( !IsActive )
		{
			IsActive = true;
			SaveState();
			Log.Info( $"[HL2RP] Vending machine activated by {player.CharacterName}" );
			return true;
		}

		// Refill stock for 25 tokens
		var refillCost = 25;
		var data = (HL2RPCharacter)player.Character.Data;
		if ( data.Money >= refillCost )
		{
			CurrencyManager.TakeMoney( player.Character, refillCost, "vending_refill" );
			StockButton1 = 10;
			StockButton2 = 5;
			StockButton3 = 5;
			SaveState();
			Log.Info( $"[HL2RP] Vending machine refilled by {player.CharacterName}" );
			return true;
		}

		return false;
	}

	private bool HandleCitizenUse( HexPlayerComponent player )
	{
		var charId = player.Character.Id;

		// Simple sequential buy: cycle through buttons 1->2->3->buy
		if ( !_selectedButton.TryGetValue( charId, out var selected ) )
			selected = 0;

		// Cycle to next button
		selected = (selected % 3) + 1;
		_selectedButton[charId] = selected;

		var stock = GetStock( selected );
		if ( stock <= 0 )
		{
			Log.Info( $"[HL2RP] Vending machine: Button {selected} out of stock" );
			return false;
		}

		var (itemId, price) = Buttons[selected - 1];
		var data = (HL2RPCharacter)player.Character.Data;

		if ( data.Money < price )
		{
			Log.Info( $"[HL2RP] Vending machine: Not enough tokens ({data.Money}/{price})" );
			return false;
		}

		// Dispense
		CurrencyManager.TakeMoney( player.Character, price, "vending_purchase" );
		SetStock( selected, stock - 1 );

		var inventories = InventoryManager.LoadForCharacter( player.Character.Id );
		if ( inventories.Count > 0 )
		{
			var item = ItemManager.CreateInstance( itemId, player.Character.Id );
			inventories[0].Add( item );
		}

		_selectedButton.Remove( charId );
		SaveState();

		Log.Info( $"[HL2RP] Vending machine: Dispensed {itemId} to {player.CharacterName} for {price} tokens" );
		return true;
	}

	private int GetStock( int button )
	{
		return button switch
		{
			1 => StockButton1,
			2 => StockButton2,
			3 => StockButton3,
			_ => 0
		};
	}

	private void SetStock( int button, int value )
	{
		switch ( button )
		{
			case 1: StockButton1 = value; break;
			case 2: StockButton2 = value; break;
			case 3: StockButton3 = value; break;
		}
	}

	private void SaveState()
	{
		DatabaseManager.Save( "hl2rp_vending", PersistenceId, new VendingSaveData
		{
			IsActive = IsActive,
			Stock1 = StockButton1,
			Stock2 = StockButton2,
			Stock3 = StockButton3
		} );
	}

	private void LoadState()
	{
		var saved = DatabaseManager.Load<VendingSaveData>( "hl2rp_vending", PersistenceId );
		if ( saved != null )
		{
			IsActive = saved.IsActive;
			StockButton1 = saved.Stock1;
			StockButton2 = saved.Stock2;
			StockButton3 = saved.Stock3;
		}
	}
}

public class VendingSaveData
{
	public bool IsActive { get; set; } = true;
	public int Stock1 { get; set; } = 10;
	public int Stock2 { get; set; } = 5;
	public int Stock3 { get; set; } = 5;
}
