#nullable enable

using System.Linq;

namespace HL2RP.V2.Tests;

/// <summary>
/// Pins a snippet of product source. These pins are the only regression guard on engine-bound
/// files that no test project compiles, so they stay; but comparing exact text meant any
/// reformat broke them. Whitespace is ignored on both sides, so only the tokens matter.
/// </summary>
internal static class SourcePin
{
	public static void Contains( string source, string snippet, string? message = null )
	{
		if ( !Squash( source ).Contains( Squash( snippet ), System.StringComparison.Ordinal ) )
			Assert.Fail( message ?? $"Expected source to contain: {snippet}" );
	}

	private static string Squash( string text ) => new( text.Where( value => !char.IsWhiteSpace( value ) ).ToArray() );
}
