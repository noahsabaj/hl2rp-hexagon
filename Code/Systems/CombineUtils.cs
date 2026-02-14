
public record CombineNameParts( string Prefix, string Rank, string Digits );

public static class CombineUtils
{
	public static readonly string[] EliteRanks = { "EpU", "DvL", "SeC", "SCN", "CLAW.SCN" };
	public static readonly string[] CPRanks = { "RCT", "05", "04", "03", "02", "01", "OfC", "EpU", "DvL", "SeC", "SCN", "CLAW.SCN" };
	public static readonly string[] OWRanks = { "OWS", "OWE", "OPG", "SGS", "SPG" };

	public static string CpPrefix => HexConfig.Get<string>( "hl2rp.combine.cpPrefix", "CP-" );
	public static string OwPrefix => HexConfig.Get<string>( "hl2rp.combine.owPrefix", "OT-" );

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

	/// <summary>
	/// Check if a player has an active character that belongs to a combine faction.
	/// Used by chat classes for CanHear checks.
	/// </summary>
	public static bool IsCombineListener( HexPlayerComponent player )
	{
		return player.HasActiveCharacter && player.Character != null && IsCombine( player.Character );
	}

	/// <summary>
	/// Parse a combine name (e.g. "CP-RCT.12345") into its components.
	/// Uses LastIndexOf to correctly handle ranks with dots (e.g. "CLAW.SCN").
	/// </summary>
	public static CombineNameParts ParseCombineName( string name )
	{
		if ( string.IsNullOrEmpty( name ) )
			return null;

		var cpPrefix = CpPrefix;
		var owPrefix = OwPrefix;

		string prefix = null;
		if ( name.StartsWith( cpPrefix ) )
			prefix = cpPrefix;
		else if ( name.StartsWith( owPrefix ) )
			prefix = owPrefix;

		if ( prefix == null )
			return null;

		var remainder = name.Substring( prefix.Length );
		var lastDot = remainder.LastIndexOf( '.' );
		if ( lastDot <= 0 || lastDot >= remainder.Length - 1 )
			return null;

		var rank = remainder.Substring( 0, lastDot );
		var digits = remainder.Substring( lastDot + 1 );

		return new CombineNameParts( prefix, rank, digits );
	}

	public static string GetCombineRank( HexCharacter character )
	{
		var data = (HL2RPCharacter)character?.Data;
		if ( data == null ) return null;

		return ParseCombineName( data.Name )?.Rank;
	}

	public static string GetDigits( HexCharacter character )
	{
		var data = (HL2RPCharacter)character?.Data;
		if ( data == null ) return null;

		return ParseCombineName( data.Name )?.Digits;
	}

	public static bool IsEliteRank( string rank )
	{
		return !string.IsNullOrEmpty( rank ) && EliteRanks.Contains( rank );
	}

	public static bool IsDispatch( HexCharacter character )
	{
		return IsCombine( character ) && IsEliteRank( GetCombineRank( character ) );
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
