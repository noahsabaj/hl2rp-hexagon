#nullable enable

using System.IO;
using System.Text.RegularExpressions;

namespace HL2RP.V2.Tests;

/// <summary>
/// Comment-stripped source loader for architecture and closure-evidence assertions,
/// ported from hexagon's LayerBoundaryTests: a pin satisfied by a comment or dead text
/// is not evidence, so positive source assertions must run against stripped source.
/// </summary>
internal static class HL2RPTestSource
{
	private static readonly Regex BlockComments = new( @"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled );
	private static readonly Regex LineComments = new( @"//.*?$", RegexOptions.Multiline | RegexOptions.Compiled );

	public static string WithoutComments( string file )
	{
		var source = File.ReadAllText( file );
		return LineComments.Replace( BlockComments.Replace( source, string.Empty ), string.Empty );
	}
}
