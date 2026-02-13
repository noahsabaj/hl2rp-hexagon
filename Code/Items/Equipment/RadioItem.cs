
public class RadioItem : ItemDefinition
{
	public RadioItem()
	{
		UniqueId = "radio";
		DisplayName = "Radio";
		Description = "A handheld frequency-tuned radio for long-range communication.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Equipment";
	}

	public override void OnInstanced( ItemInstance item )
	{
		if ( !item.Data.ContainsKey( "frequency" ) )
		{
			item.SetData( "frequency", "100.0" );
		}
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Tune"] = new ItemAction
			{
				Name = "Tune",
				OnRun = ( player, item ) =>
				{
					// TODO: Open radio tuning UI (Phase 7)
					var freq = item.GetData<string>( "frequency", "100.0" );
					Log.Info( $"[Radio] Current frequency: {freq}" );
					return true;
				}
			}
		};
	}
}

public class PagerItem : ItemDefinition
{
	public PagerItem()
	{
		UniqueId = "pager";
		DisplayName = "Pager";
		Description = "A small pager that receives radio transmissions.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Equipment";
	}

	public override void OnInstanced( ItemInstance item )
	{
		if ( !item.Data.ContainsKey( "frequency" ) )
		{
			item.SetData( "frequency", "100.0" );
		}
	}
}

public class StaticRadioItem : ItemDefinition
{
	public StaticRadioItem()
	{
		UniqueId = "static_radio";
		DisplayName = "Static Radio";
		Description = "A stationary radio with unlimited transmission range.";
		Width = 2;
		Height = 1;
		MaxStack = 1;
		Category = "Equipment";
	}

	public override void OnInstanced( ItemInstance item )
	{
		if ( !item.Data.ContainsKey( "frequency" ) )
		{
			item.SetData( "frequency", "100.0" );
		}
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Tune"] = new ItemAction
			{
				Name = "Tune",
				OnRun = ( player, item ) =>
				{
					var freq = item.GetData<string>( "frequency", "100.0" );
					Log.Info( $"[Static Radio] Current frequency: {freq}" );
					return true;
				}
			}
		};
	}
}
