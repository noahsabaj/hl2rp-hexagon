
public class CIDCardItem : ItemDefinition
{
	public CIDCardItem()
	{
		UniqueId = "cid_card";
		DisplayName = "Citizen ID";
		Description = "An identification card issued to citizens of City 17.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		CanDrop = true;
		Category = "Identification";
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Show"] = new ItemAction
			{
				Name = "Show",
				OnCanRun = ( player, item ) => true,
				OnRun = ( player, item ) =>
				{
					var name = item.GetData<string>( "name", "Unknown" );
					var id = item.GetData<int>( "id", 0 );
					var cwu = item.GetData<bool>( "cwu", false );
					var priority = cwu ? " [CWU PRIORITY]" : "";
					Log.Info( $"[CID] Name: {name} | ID: #{id}{priority}" );
				}
			}
		};
	}
}
