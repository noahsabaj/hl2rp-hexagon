using Hexagon.Items;

public class RadioTrait : ItemDataTrait
{
	public string Frequency { get; set; } = "100.0";
}

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
		item.GetTrait<RadioTrait>();
	}

	public override List<ItemAction> GetActions()
	{
		if ( !_tunable ) return base.GetActions();

		return new List<ItemAction>
		{
			new ItemAction
			{
				Name = "Tune",
				OnRun = ( player, item ) =>
				{
					// TODO: Open radio tuning UI (Phase 7)
					var freq = item.GetTrait<RadioTrait>().Frequency;
					Log.Info( $"[{DisplayName}] Current frequency: {freq}" );
					return false;
				}
			}
		};
	}
}
