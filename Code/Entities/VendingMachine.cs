
/// <summary>
/// Vending machine world entity. Citizens buy water variants for tokens.
/// CP can refill or toggle active state. Place in scene via editor.
/// </summary>
public class VendingMachine : PersistableEntity<VendingSaveData>, Component.IPressable
{
	[Sync] public int StockButton1 { get; set; } = 10;
	[Sync] public int StockButton2 { get; set; } = 5;
	[Sync] public int StockButton3 { get; set; } = 5;
	[Sync] public bool IsActive { get; set; } = true;

	private Dictionary<string, int> _selectedButton = new();

	private static readonly (string ItemId, int Price)[] Buttons = new[]
	{
		("water", 5),
		("water_sparkling", 15),
		("water_special", 20)
	};

	protected override string CollectionName => "hl2rp_vending";

	protected override void OnLoadState( VendingSaveData data )
	{
		IsActive = data.IsActive;
		StockButton1 = data.Stock1;
		StockButton2 = data.Stock2;
		StockButton3 = data.Stock3;
	}

	protected override VendingSaveData CreateSaveData()
	{
		return new VendingSaveData
		{
			IsActive = IsActive,
			Stock1 = StockButton1,
			Stock2 = StockButton2,
			Stock3 = StockButton3
		};
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

		if ( CombineUtils.IsCombine( player.Character ) )
			return HandleCombineUse( player );

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

		if ( !_selectedButton.TryGetValue( charId, out var selected ) )
			selected = 0;

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
}

public class VendingSaveData
{
	public bool IsActive { get; set; } = true;
	public int Stock1 { get; set; } = 10;
	public int Stock2 { get; set; } = 5;
	public int Stock3 { get; set; } = 5;
}
