using Hexagon.Items;

public class NoteTrait : ItemDataTrait
{
	public string Text { get; set; } = "";
	public string Owner { get; set; } = "";
}

public class NoteItem : ItemDefinition
{
	public NoteItem()
	{
		UniqueId = "note";
		DisplayName = "Note";
		Description = "A writable note.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Misc";
	}

	public override void OnInstanced( ItemInstance item )
	{
		item.GetTrait<NoteTrait>();
	}

	public override List<ItemAction> GetActions()
	{
		return new List<ItemAction>
		{
			new ItemAction
			{
				Name = "Read",
				OnRun = ( player, item ) =>
				{
					var text = item.GetTrait<NoteTrait>().Text;
					// TODO: Open note UI (Phase 7)
					Log.Info( $"[Note] {text}" );
					return false;
				}
			},
			new ItemAction
			{
				Name = "Write",
				OnCanRun = ( player, item ) =>
				{
					var owner = item.GetTrait<NoteTrait>().Owner;
					return string.IsNullOrEmpty( owner ) || owner == player.Character?.Id;
				},
				OnRun = ( player, item ) =>
				{
					// TODO: Open note editor UI (Phase 7)
					var trait = item.GetTrait<NoteTrait>();
					if ( string.IsNullOrEmpty( trait.Owner ) )
						trait.Owner = player.Character.Id;
					return false;
				}
			}
		};
	}
}
