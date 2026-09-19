#nullable enable

using System;
using System.Collections.Generic;
using Sandbox;
using HL2RP.Logic;

namespace HL2RP;

/// <summary>Backs the document store with the game's sandboxed data folder.</summary>
public sealed class SandboxFileStore : IFileStore
{
	/// <summary>Folder under the game's data directory. Tests point this elsewhere so they never touch a real city.</summary>
	[ConVar( "hl2rp_data_root" )]
	public static string Root { get; set; } = "hl2rp";

	private static BaseFileSystem Files => FileSystem.Data;

	public bool Exists( string path ) => Files.FileExists( Full( path ) );
	public string Read( string path ) => Files.ReadAllText( Full( path ) );
	public void Delete( string path ) => Files.DeleteFile( Full( path ) );

	public void Write( string path, string text )
	{
		var full = Full( path );
		var slash = full.LastIndexOf( '/' );
		if ( slash > 0 ) Files.CreateDirectory( full[..slash] );
		Files.WriteAllText( full, text );
	}

	public IEnumerable<string> Find( string folder, string pattern ) =>
		Files.DirectoryExists( Full( folder ) ) ? Files.FindFile( Full( folder ), pattern ) : Array.Empty<string>();

	private static string Full( string path ) => $"{Root}/{path}";
}
