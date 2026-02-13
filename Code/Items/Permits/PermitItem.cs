
public class PermitItem : ItemDefinition
{
	public string PermitType { get; set; }

	public PermitItem( string type, string displayName, string description )
	{
		UniqueId = $"permit_{type}";
		DisplayName = displayName;
		Description = description;
		Width = 1;
		Height = 1;
		MaxStack = 1;
		CanDrop = true;
		Category = "Permits";
		PermitType = type;
	}
}
