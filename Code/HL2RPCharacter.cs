
public class HL2RPCharacter : HexCharacterData
{
	[CharVar( Default = "John Doe", MinLength = 3, MaxLength = 64, Order = 1, ShowInCreation = true )]
	public string Name { get; set; }

	[CharVar( Default = "A citizen of City 17.", MinLength = 16, MaxLength = 512, Order = 2, ShowInCreation = true )]
	public string Description { get; set; }

	[CharVar( Order = 3, ShowInCreation = true )]
	public string Model { get; set; }

	[CharVar( Local = true, Default = 0 )]
	public int Money { get; set; }

	[CharVar( Local = true, Default = "" )]
	public string CharData { get; set; }

	[CharVar( Local = true, Default = 0 )]
	public int CIDNumber { get; set; }

	[CharVar( Local = true, Default = 0 )]
	public int Armor { get; set; }
}
