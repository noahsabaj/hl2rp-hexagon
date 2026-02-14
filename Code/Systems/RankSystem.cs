
public static class RankSystem
{
	public static string GetModelForCharacter( HexCharacter character )
	{
		var factionId = character.Faction;
		var rank = CombineUtils.IsCombineFaction( factionId )
			? CombineUtils.GetCombineRank( character )
			: null;

		var configKey = CombineUtils.GetModelConfigKey( factionId, rank );
		return HexConfig.Get<string>( configKey, "" );
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
		var digitLength = HexConfig.Get<int>( "hl2rp.combine.digitLength", 5 );

		string expectedPrefix;
		string[] validRanks;

		if ( factionId == "cca" )
		{
			expectedPrefix = CombineUtils.CpPrefix;
			validRanks = CombineUtils.CPRanks;
		}
		else if ( factionId == "ota" )
		{
			expectedPrefix = CombineUtils.OwPrefix;
			validRanks = CombineUtils.OWRanks;
		}
		else
		{
			return null;
		}

		var parts = CombineUtils.ParseCombineName( name );
		if ( parts == null || parts.Prefix != expectedPrefix )
			return $"Combine name must follow format: {expectedPrefix}RANK.DIGITS";

		if ( !validRanks.Contains( parts.Rank ) )
			return $"Invalid rank '{parts.Rank}'. Valid ranks: {string.Join( ", ", validRanks )}";

		if ( parts.Digits.Length != digitLength || !parts.Digits.All( char.IsDigit ) )
			return $"Combine ID must be exactly {digitLength} digits.";

		return null;
	}
}
