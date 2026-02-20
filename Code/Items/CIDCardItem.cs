using Hexagon.Items;

public class CIDTrait : ItemDataTrait
{
	public string Name { get; set; } = "Unknown";
	public int Id { get; set; } = 0;
	public bool CWU { get; set; } = false;
}

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

	public override List<ItemAction> GetActions()
	{
		return new List<ItemAction>
		{
			new ItemAction
			{
				Name = "Show",
				OnCanRun = ( player, item ) => true,
				OnRun = ( player, item ) =>
				{
					var trait = item.GetTrait<CIDTrait>();
					var name = trait.Name;
					var id = trait.Id;
					var cwu = trait.CWU;
					var priority = cwu ? " [CWU PRIORITY]" : "";
					Log.Info( $"[CID] Name: {name} | ID: #{id}{priority}" );
					return false;
				}
			}
		};
	}
}
