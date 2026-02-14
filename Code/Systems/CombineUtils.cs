using Hexagon.Config;

public static class CombineUtils
{
	private static readonly string[] EliteRanks = { "EpU", "DvL", "SeC", "SCN", "CLAW.SCN" };
	private static readonly string[] CPRanks = { "RCT", "05", "04", "03", "02", "01", "OfC", "EpU", "DvL", "SeC", "SCN", "CLAW.SCN" };
	private static readonly string[] OWRanks = { "OWS", "OWE", "OPG", "SGS", "SPG" };

	public static bool IsCombineFaction( string factionId )
	{
		return factionId == "cca" || factionId == "ota";
	}

	public static bool IsCombine( HexCharacter character )
	{
		return IsCombineFaction( character.Faction );
	}

	public static bool IsCP( HexCharacter character )
	{
		return character.Faction == "cca";
	}

	public static bool IsOverwatch( HexCharacter character )
	{
		return character.Faction == "ota";
	}

	public static string GetCombineRank( HexCharacter character )
	{
		if ( character?.Data == null )
			return null;

		var data = (HL2RPCharacter)character.Data;
		var name = data.Name;

		if ( string.IsNullOrEmpty( name ) )
			return null;

		var cpPrefix = HexConfig.Get<string>( "hl2rp.combine.cpPrefix", "CP-" );
		var owPrefix = HexConfig.Get<string>( "hl2rp.combine.owPrefix", "OT-" );

		string prefix = null;
		if ( name.StartsWith( cpPrefix ) )
			prefix = cpPrefix;
		else if ( name.StartsWith( owPrefix ) )
			prefix = owPrefix;

		if ( prefix == null )
			return null;

		var remainder = name.Substring( prefix.Length );
		var dotIndex = remainder.IndexOf( '.' );
		if ( dotIndex <= 0 )
			return null;

		return remainder.Substring( 0, dotIndex );
	}

	public static string GetDigits( HexCharacter character )
	{
		if ( character?.Data == null )
			return null;

		var data = (HL2RPCharacter)character.Data;
		var name = data.Name;

		if ( string.IsNullOrEmpty( name ) )
			return null;

		var dotIndex = name.LastIndexOf( '.' );
		if ( dotIndex < 0 || dotIndex >= name.Length - 1 )
			return null;

		return name.Substring( dotIndex + 1 );
	}

	public static bool IsEliteRank( string rank )
	{
		if ( string.IsNullOrEmpty( rank ) )
			return false;

		return EliteRanks.Contains( rank );
	}

	public static bool IsDispatch( HexCharacter character )
	{
		if ( !IsCombine( character ) )
			return false;

		var rank = GetCombineRank( character );
		return IsEliteRank( rank );
	}

	public static bool IsValidCPRank( string rank )
	{
		return CPRanks.Contains( rank );
	}

	public static bool IsValidOWRank( string rank )
	{
		return OWRanks.Contains( rank );
	}

	public static string GetModelConfigKey( string factionId, string rank )
	{
		if ( factionId == "cca" )
		{
			return rank switch
			{
				"RCT" => "hl2rp.model.cp.rct",
				"05" or "04" or "03" or "02" or "01" => "hl2rp.model.cp.unit",
				"OfC" => "hl2rp.model.cp.ofc",
				"EpU" => "hl2rp.model.cp.epu",
				"DvL" => "hl2rp.model.cp.dvl",
				"SeC" => "hl2rp.model.cp.sec",
				"SCN" or "CLAW.SCN" => "hl2rp.model.cp.scn",
				_ => "hl2rp.model.cp.rct"
			};
		}

		if ( factionId == "ota" )
		{
			return rank switch
			{
				"OWS" => "hl2rp.model.ow.ows",
				"OWE" => "hl2rp.model.ow.owe",
				"OPG" => "hl2rp.model.ow.opg",
				"SGS" => "hl2rp.model.ow.sgs",
				"SPG" => "hl2rp.model.ow.spg",
				_ => "hl2rp.model.ow.ows"
			};
		}

		if ( factionId == "cityadmin" )
			return "hl2rp.model.admin";

		if ( factionId == "vortigaunt" )
			return "hl2rp.model.vortigaunt";

		return "hl2rp.model.citizen";
	}
}
