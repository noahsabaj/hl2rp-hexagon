
/// <summary>
/// Simple consumable with no special behavior. Used for water, supplements,
/// and other items that only differ by name/description.
/// </summary>
public class SimpleConsumable : ConsumableItemDef
{
	public SimpleConsumable( string id, string name, string description, string verb )
	{
		UniqueId = id;
		DisplayName = name;
		Description = description;
		Width = 1;
		Height = 1;
		MaxStack = 1;
		Category = "Consumables";
		ConsumeVerb = verb;
	}
}
