
/// <summary>
/// Base item for frequency-tuned radio devices (radio, pager, static radio).
/// Handles shared frequency initialization and optional tuning action.
/// </summary>
public class FrequencyDeviceItem : ItemDefinition
{
	private readonly bool _tunable;

	public FrequencyDeviceItem( string id, string name, string description, bool tunable = false, int width = 1, int height = 1 )
	{
		UniqueId = id;
		DisplayName = name;
		Description = description;
		Width = width;
		Height = height;
		MaxStack = 1;
		Category = "Equipment";
		_tunable = tunable;
	}

	public override void OnInstanced( ItemInstance item )
	{
		if ( !item.Data.ContainsKey( "frequency" ) )
			item.SetData( "frequency", "100.0" );
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		if ( !_tunable ) return base.GetActions();

		return new Dictionary<string, ItemAction>
		{
			["Tune"] = new ItemAction
			{
				Name = "Tune",
				OnRun = ( player, item ) =>
				{
					// TODO: Open radio tuning UI (Phase 7)
					var freq = item.GetData<string>( "frequency", "100.0" );
					Log.Info( $"[{DisplayName}] Current frequency: {freq}" );
					return false;
				}
			}
		};
	}
}
