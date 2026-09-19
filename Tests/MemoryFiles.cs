using System;
using System.Collections.Generic;
using System.Linq;
using HL2RP.Logic;

namespace HL2RP.Tests;

/// <summary>An in-memory disk that can lose power after a chosen number of writes.</summary>
internal sealed class MemoryFiles : IFileStore
{
	public Dictionary<string, string> Files { get; } = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>When set, the write with this 1-based index is torn and every later operation throws.</summary>
	public int? CrashOnWrite { get; set; }
	public int Writes { get; private set; }
	private bool _dead;

	public bool Exists( string path ) => Files.ContainsKey( path );
	public string Read( string path ) => Files[path];

	public void Write( string path, string text )
	{
		if ( _dead ) throw new InvalidOperationException( "Power is off." );
		Writes++;
		if ( CrashOnWrite == Writes )
		{
			// A torn write: the file exists but holds only the first half.
			Files[path] = text[..(text.Length / 2)];
			_dead = true;
			throw new InvalidOperationException( "Power lost mid-write." );
		}
		Files[path] = text;
	}

	public void Delete( string path )
	{
		if ( _dead ) throw new InvalidOperationException( "Power is off." );
		Files.Remove( path );
	}

	public IEnumerable<string> Find( string folder, string pattern ) =>
		Files.Keys.Where( path => path.StartsWith( folder + "/", StringComparison.OrdinalIgnoreCase ) )
			.Select( path => path[(folder.Length + 1)..] ).ToArray();

	public MemoryFiles Reboot()
	{
		var rebooted = new MemoryFiles();
		foreach ( var pair in Files ) rebooted.Files[pair.Key] = pair.Value;
		return rebooted;
	}
}
