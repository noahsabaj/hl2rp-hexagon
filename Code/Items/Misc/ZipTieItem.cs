
public class ZipTieItem : ItemDefinition
{
	public ZipTieItem()
	{
		UniqueId = "zip_tie";
		DisplayName = "Zip Tie";
		Description = "A plastic restraint used to tie someone's hands.";
		Width = 1;
		Height = 1;
		MaxStack = 5;
		Category = "Equipment";
	}

	public override List<ItemAction> GetActions()
	{
		return new List<ItemAction>
		{
			new ItemAction
			{
				Name = "Tie",
				OnCanRun = ( player, item ) =>
				{
					return player.Character != null && CombineUtils.IsCombine( player.Character );
				},
				OnRun = ( player, item ) =>
				{
					// Restraint logic handled by TyingPlugin (Phase 6)
					Log.Info( "[HL2RP] Zip tie used — TyingPlugin handles restraint." );
					return true;
				}
			}
		};
	}
}
