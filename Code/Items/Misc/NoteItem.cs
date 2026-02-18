
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
		if ( !item.Data.ContainsKey( "text" ) )
			item.SetData( "text", "" );
		if ( !item.Data.ContainsKey( "owner" ) )
			item.SetData( "owner", "" );
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
					var text = item.GetData<string>( "text", "" );
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
					var owner = item.GetData<string>( "owner", "" );
					return string.IsNullOrEmpty( owner ) || owner == player.Character?.Id;
				},
				OnRun = ( player, item ) =>
				{
					// TODO: Open note editor UI (Phase 7)
					if ( string.IsNullOrEmpty( item.GetData<string>( "owner", "" ) ) )
						item.SetData( "owner", player.Character.Id );
					return false;
				}
			}
		};
	}
}
