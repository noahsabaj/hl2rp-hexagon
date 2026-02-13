
public class RationItem : ItemDefinition
{
	public RationItem()
	{
		UniqueId = "ration";
		DisplayName = "Ration Package";
		Description = "A standard Combine-issued ration package.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Consumables";
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Open"] = new ItemAction
			{
				Name = "Open",
				OnRun = ( player, item ) =>
				{
					var inventories = InventoryManager.LoadForCharacter( player.Character.Id );
					if ( inventories.Count == 0 ) return;

					var water = ItemManager.CreateInstance( "water", player.Character.Id );
					var supplement = ItemManager.CreateInstance( "supplement", player.Character.Id );
					inventories[0].Add( water );
					inventories[0].Add( supplement );

					var tokens = new Random().Next( 10, 16 );
					CurrencyManager.GiveMoney( player.Character, tokens, "ration" );

					foreach ( var inv in inventories )
					{
						if ( inv.Remove( item.Id ) )
						{
							ItemManager.DestroyInstance( item.Id );
							break;
						}
					}
				}
			}
		};
	}
}
