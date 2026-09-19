#nullable enable

using System.Globalization;
using System.Text;

namespace HL2RP.Logic.Names;

/// <summary>
/// Reduces a canonical character name to a comparison key that collapses look-alike spellings
/// onto each other, following the UTS #39 skeleton algorithm over the full Unicode confusables
/// table in <see cref="ConfusableMappings"/>. Two names with the same skeleton render similarly
/// enough that one could pass for the other, so the framework compares skeletons rather than names
/// and refuses the second of any such pair.
/// <para>
/// The script profile already stops a name from MIXING writing systems. This closes what it
/// cannot see: look-alikes within one script, and a name written entirely in another script to
/// imitate a Latin one.
/// </para>
/// <para>
/// Folding is deliberately lossy and will occasionally judge two genuinely different names too
/// close. That trade is intentional: a rejected name costs a player one retry, while a
/// successful impersonation is not recoverable by the player being impersonated.
/// </para>
/// </summary>
public static class CharacterNameSkeleton
{
	/// <summary>
	/// The comparison key for a canonical name, or an empty string when the name carries no
	/// identity-bearing characters at all. Callers must treat empty as a rejected name.
	/// </summary>
	public static string Of( string canonicalName ) => Reduce( canonicalName, foldConfusables: true );

	/// <summary>
	/// The same reduction WITHOUT confusable folding: it still ignores case, accents, spacing and
	/// punctuation, so it catches a plain repeat of a name, but "Alice" and "A1ice" stay distinct.
	/// Used when an operator judges full folding too aggressive. Like <see cref="Of"/> it yields
	/// only letters and digits, which is what keeps it usable as a reservation key.
	/// </summary>
	public static string Exact( string canonicalName ) => Reduce( canonicalName, foldConfusables: false );

	private static string Reduce( string canonicalName, bool foldConfusables )
	{
		if ( string.IsNullOrEmpty( canonicalName ) ) return string.Empty;

		// UTS #39: decompose, replace every confusable with its prototype, recompose to NFD.
		// Case folding comes last, because the table's prototypes are case-sensitive - it maps
		// "I" onto "l", which lowercasing first would have already destroyed.
		var decomposed = canonicalName.Normalize( NormalizationForm.FormD );
		var mapped = (foldConfusables ? MapConfusables( decomposed ) : decomposed)
			.Normalize( NormalizationForm.FormD );

		var builder = new StringBuilder( mapped.Length );
		for ( var index = 0; index < mapped.Length; index++ )
		{
			var category = CharUnicodeInfo.GetUnicodeCategory( mapped, index );
			var codePoint = char.ConvertToUtf32( mapped, index );
			if ( char.IsHighSurrogate( mapped[index] ) ) index++;
			// Only letters and digits carry identity. Marks, spaces, punctuation and symbols are
			// dropped, so "Al Ice", "Al-Ice" and "AlIce" are one identity rather than three, and
			// the accent in "Jose" decomposed above is decoration rather than a second name.
			if ( category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
				or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
				or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber )
				builder.Append( char.ConvertFromUtf32( codePoint ) );
		}

		return builder.ToString().ToLowerInvariant();
	}

	private static string MapConfusables( string value )
	{
		var builder = new StringBuilder( value.Length );
		for ( var index = 0; index < value.Length; index++ )
		{
			var codePoint = char.ConvertToUtf32( value, index );
			if ( char.IsHighSurrogate( value[index] ) ) index++;

			// The table's output is folded again by the supplementary rules, because UTS #39 maps
			// several letters onto DIGITS (U+01BC tone five onto '5', U+0222 ou onto '8', U+01A7
			// onto '2') and a digit is exactly what the supplementary fold turns back into a
			// letter. Folding only the misses left "Ƽara" distinct from "Sara" while "5ara" was
			// not. One pass is a fixpoint: no prototype in the table is itself a table source,
			// and no supplementary output is either.
			if ( ConfusableMappings.TryMap( codePoint, out var single, out var sequence ) )
			{
				if ( sequence is not null ) AppendSupplementaryFolded( builder, sequence );
				else AppendSupplementaryFolded( builder, single );
				continue;
			}

			AppendSupplementaryFolded( builder, codePoint );
		}

		return builder.ToString();
	}

	private static void AppendSupplementaryFolded( StringBuilder builder, string value )
	{
		for ( var index = 0; index < value.Length; index++ )
		{
			var codePoint = char.ConvertToUtf32( value, index );
			if ( char.IsHighSurrogate( value[index] ) ) index++;
			AppendSupplementaryFolded( builder, codePoint );
		}
	}

	private static void AppendSupplementaryFolded( StringBuilder builder, int codePoint )
	{
		var supplement = SupplementaryFold( codePoint );
		if ( supplement is not null ) builder.Append( supplement );
		else builder.Append( char.ConvertFromUtf32( codePoint ) );
	}

	/// <summary>
	/// The two confusions the Unicode table does not express, kept deliberately small and
	/// separate so it stays obvious what is standard and what is this framework's own judgement.
	/// <list type="bullet">
	/// <item>Digit-for-letter substitution. UTS #39 maps only '0' and '1', judging the rest not
	/// visually confusable; in a game where names are typed to be read by other players,
	/// leetspeak is the ordinary way to imitate a name, so the remaining digits fold too.</item>
	/// <item>"w" onto "vv". A UTS #39 source is always a single code point, so the table can
	/// express "m" onto "rn" - which it does, and which is why that pair needs nothing here -
	/// but it cannot express the reverse direction for a two-letter sequence.</item>
	/// </list>
	/// </summary>
	private static string? SupplementaryFold( int codePoint ) => codePoint switch
	{
		'2' => "z",
		'3' => "e",
		'4' => "a",
		'5' => "s",
		'6' => "b",
		'7' => "t",
		'8' => "b",
		'9' => "g",
		'w' => "vv",
		'W' => "vv",
		_ => null
	};
}