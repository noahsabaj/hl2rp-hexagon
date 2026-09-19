#nullable enable

using System.Globalization;
using System.Text;
using HL2RP.Logic.Names;

namespace HL2RP.Logic;

/// <summary>
/// A character name is an impersonation surface, not free text. Names are NFKC-normalized, may
/// not mix writing systems, and are compared by a confusable skeleton so "A1ice" cannot sit
/// beside "Alice". Descriptions are prose: NFC, line breaks allowed, no script rule.
/// </summary>
public static class NameRules
{
	public const int MinimumNameLength = 3;
	public const int MaximumNameLength = 64;
	public const int MinimumDescriptionLength = 16;
	public const int MaximumDescriptionLength = 512;

	public static Result<string> NormalizeName( string? value )
	{
		var text = Normalize( value, "Name", MinimumNameLength, MaximumNameLength, lineBreaks: false, compatibility: true );
		return text.Ok ? NameScriptProfile.Validate( text.Value ) : text;
	}

	public static Result<string> NormalizeDescription( string? value ) =>
		Normalize( value, "Description", MinimumDescriptionLength, MaximumDescriptionLength, lineBreaks: true, compatibility: false );

	/// <summary>Two names with the same key could pass for each other.</summary>
	public static string Key( string normalizedName ) => CharacterNameSkeleton.Of( normalizedName );

	private static Result<string> Normalize(
		string? value, string field, int minimum, int maximum, bool lineBreaks, bool compatibility )
	{
		if ( value is null ) return Result<string>.Fail( ErrorCode.Invalid, $"{field} is required." );

		// Normalize throws on unpaired surrogates, so well-formedness is proven first.
		for ( var index = 0; index < value.Length; index++ )
		{
			if ( char.IsHighSurrogate( value[index] ) )
			{
				if ( index + 1 >= value.Length || !char.IsLowSurrogate( value[index + 1] ) )
					return Result<string>.Fail( ErrorCode.Invalid, $"{field} contains invalid Unicode." );
				index++;
			}
			else if ( char.IsLowSurrogate( value[index] ) )
			{
				return Result<string>.Fail( ErrorCode.Invalid, $"{field} contains invalid Unicode." );
			}
		}

		var normalized = value.Normalize( compatibility ? NormalizationForm.FormKC : NormalizationForm.FormC ).Trim();
		if ( normalized.Length < minimum || normalized.Length > maximum )
			return Result<string>.Fail( ErrorCode.Invalid, $"{field} must be {minimum}-{maximum} characters." );

		for ( var index = 0; index < normalized.Length; index++ )
		{
			var character = normalized[index];
			if ( char.IsControl( character ) && !(lineBreaks && character is '\n') )
				return Result<string>.Fail( ErrorCode.Invalid, $"{field} contains control characters." );
			// Read from the string, not the char, so a surrogate pair is classified as itself.
			if ( CharUnicodeInfo.GetUnicodeCategory( normalized, index ) == UnicodeCategory.Format )
				return Result<string>.Fail( ErrorCode.Invalid, $"{field} contains invisible formatting characters." );
			if ( char.IsHighSurrogate( character ) ) index++;
		}

		return Result<string>.Success( normalized );
	}
}
