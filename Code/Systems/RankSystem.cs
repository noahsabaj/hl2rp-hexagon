using Hexagon.Config;

public static class RankSystem
{
	public static string GetModelForCharacter( HexCharacter character )
	{
		var factionId = character.Faction;

		if ( CombineUtils.IsCombineFaction( factionId ) )
		{
			var rank = CombineUtils.GetCombineRank( character );
			var configKey = CombineUtils.GetModelConfigKey( factionId, rank );
			return HexConfig.Get<string>( configKey, "" );
		}

		var defaultKey = CombineUtils.GetModelConfigKey( factionId, null );
		return HexConfig.Get<string>( defaultKey, "" );
	}

	public static void ApplyRankModel( HexPlayerComponent player, HexCharacter character )
	{
		var model = GetModelForCharacter( character );
		if ( string.IsNullOrEmpty( model ) )
			return;

		var data = (HL2RPCharacter)character.Data;
		data.Model = model;
		player.CharacterModel = model;
		character.MarkDirty( "Model" );
	}

	public static string ValidateCombineName( string name, string factionId )
	{
		var cpPrefix = HexConfig.Get<string>( "hl2rp.combine.cpPrefix", "CP-" );
		var owPrefix = HexConfig.Get<string>( "hl2rp.combine.owPrefix", "OT-" );
		var digitLength = HexConfig.Get<int>( "hl2rp.combine.digitLength", 5 );

		string expectedPrefix;
		string[] validRanks;

		if ( factionId == "cca" )
		{
			expectedPrefix = cpPrefix;
			validRanks = new[] { "RCT", "05", "04", "03", "02", "01", "OfC", "EpU", "DvL", "SeC", "SCN", "CLAW.SCN" };
		}
		else if ( factionId == "ota" )
		{
			expectedPrefix = owPrefix;
			validRanks = new[] { "OWS", "OWE", "OPG", "SGS", "SPG" };
		}
		else
		{
			return null;
		}

		if ( !name.StartsWith( expectedPrefix ) )
			return $"Combine name must start with '{expectedPrefix}'.";

		var remainder = name.Substring( expectedPrefix.Length );
		var dotIndex = remainder.IndexOf( '.' );
		if ( dotIndex <= 0 )
			return $"Combine name must follow format: {expectedPrefix}RANK.DIGITS";

		var rank = remainder.Substring( 0, dotIndex );
		if ( !validRanks.Contains( rank ) )
			return $"Invalid rank '{rank}'. Valid ranks: {string.Join( ", ", validRanks )}";

		var digits = remainder.Substring( dotIndex + 1 );
		if ( digits.Length != digitLength || !digits.All( char.IsDigit ) )
			return $"Combine ID must be exactly {digitLength} digits.";

		return null;
	}
}
