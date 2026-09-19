#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace HL2RP.Logic;

/// <summary>The few file operations the store needs. The game backs this with <c>FileSystem.Data</c>.</summary>
public interface IFileStore
{
	bool Exists( string path );
	string Read( string path );
	void Write( string path, string text );
	void Delete( string path );
	IEnumerable<string> Find( string folder, string pattern );
}

/// <summary>
/// One JSON file per document. The sandboxed filesystem has no rename, so a write cannot be made
/// atomic; instead every save writes a complete sibling first, then the real file, then removes
/// the sibling. A crash leaves at least one complete copy, and <see cref="Load{T}"/> prefers the
/// real file and falls back to the sibling. A sibling that does not parse is a save that never
/// finished, so the real file is still the last committed state.
/// </summary>
public sealed class DocumentStore
{
	public const string PendingSuffix = ".pending";

	private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
	private readonly IFileStore _files;
	private readonly Action<string> _warn;

	public DocumentStore( IFileStore files, Action<string>? warn = null )
	{
		_files = files ?? throw new ArgumentNullException( nameof(files) );
		_warn = warn ?? (_ => { });
	}

	public void Save<T>( string path, T document ) where T : class
	{
		var json = JsonSerializer.Serialize( document, Options );
		_files.Write( path + PendingSuffix, json );
		_files.Write( path, json );
		_files.Delete( path + PendingSuffix );
	}

	public T? Load<T>( string path ) where T : class
	{
		var committed = TryRead<T>( path );
		var pending = TryRead<T>( path + PendingSuffix );
		if ( committed is null && pending is not null )
		{
			// The crash fell between the two writes: the sibling is the newest complete copy.
			_warn( $"Recovered '{path}' from its pending copy." );
			Save( path, pending );
			return pending;
		}
		if ( _files.Exists( path + PendingSuffix ) ) _files.Delete( path + PendingSuffix );
		if ( committed is null && _files.Exists( path ) ) _warn( $"'{path}' is unreadable and was skipped." );
		return committed;
	}

	public IReadOnlyList<T> LoadAll<T>( string folder ) where T : class
	{
		var paths = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var found in _files.Find( folder, "*.json*" ) )
		{
			var name = found.EndsWith( PendingSuffix, StringComparison.OrdinalIgnoreCase )
				? found[..^PendingSuffix.Length]
				: found;
			if ( name.EndsWith( ".json", StringComparison.OrdinalIgnoreCase ) ) paths.Add( $"{folder}/{name}" );
		}
		var documents = new List<T>();
		foreach ( var path in paths )
			if ( Load<T>( path ) is { } document ) documents.Add( document );
		return documents;
	}

	public void Delete( string path )
	{
		if ( _files.Exists( path + PendingSuffix ) ) _files.Delete( path + PendingSuffix );
		if ( _files.Exists( path ) ) _files.Delete( path );
	}

	private T? TryRead<T>( string path ) where T : class
	{
		if ( !_files.Exists( path ) ) return null;
		try
		{
			return JsonSerializer.Deserialize<T>( _files.Read( path ), Options );
		}
		catch ( JsonException )
		{
			return null;
		}
	}
}
