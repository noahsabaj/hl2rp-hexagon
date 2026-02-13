
public class BookItem : ItemDefinition
{
	public string BookContent { get; set; }

	public BookItem( string id, string title, string description, string content )
	{
		UniqueId = id;
		DisplayName = title;
		Description = description;
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Literature";
		BookContent = content;
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Read"] = new ItemAction
			{
				Name = "Read",
				OnRun = ( player, item ) =>
				{
					// TODO: Open book reader UI (Phase 7)
					Log.Info( $"[Book] {BookContent}" );
					return true;
				}
			}
		};
	}
}
