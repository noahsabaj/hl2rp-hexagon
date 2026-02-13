
public class RequestDeviceItem : ItemDefinition
{
	public RequestDeviceItem()
	{
		UniqueId = "request_device";
		DisplayName = "Request Device";
		Description = "A communication device for sending requests to Civil Protection.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Equipment";
	}
}

public class FlashlightItem : ItemDefinition
{
	public FlashlightItem()
	{
		UniqueId = "flashlight";
		DisplayName = "Flashlight";
		Description = "A handheld flashlight.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Equipment";
	}

	public override Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>
		{
			["Toggle"] = new ItemAction
			{
				Name = "Toggle",
				OnRun = ( player, item ) =>
				{
					var isOn = item.GetData<bool>( "on", false );
					item.SetData( "on", !isOn );
					item.MarkDirty();
				}
			}
		};
	}
}

public class SpraycanItem : ItemDefinition
{
	public SpraycanItem()
	{
		UniqueId = "spraycan";
		DisplayName = "Spraycan";
		Description = "A can of spray paint.";
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Equipment";
	}
}
