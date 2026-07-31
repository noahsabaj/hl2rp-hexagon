#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Hexagon.V2.Persistence;
using Sandbox;

namespace HL2RP.V2.Runtime;

/// <summary>
/// Binds the engine's gamemode data filesystem to the neutral durable storage core.
/// Every host shape — dedicated server, editor-hosted bootstrap, standalone — uses the
/// same whitelist-safe storage path, so the assembly passes s&amp;box access control
/// wherever it is streamed. The adapter forwards exactly the
/// <c>Sandbox.BaseFileSystem</c> members the core's boundary names;
/// <c>BaseFileSystem.OpenWrite</c> opens the physical backing file with no shared
/// writer (Zio's <c>PhysicalFileSystem</c> default share), which is what makes the
/// storage lease process-exclusive.
/// </summary>
internal static class HL2RPPersistenceStorageFactory
{
	public static IPersistenceStorage Create( BaseFileSystem fileSystem )
	{
		ArgumentNullException.ThrowIfNull( fileSystem );
		return new HL2RPDurableStorageCore( new SandboxStorageFileSystem( fileSystem ) );
	}

	private sealed class SandboxStorageFileSystem : IHL2RPStorageFileSystem
	{
		private readonly BaseFileSystem _fileSystem;

		public SandboxStorageFileSystem( BaseFileSystem fileSystem ) => _fileSystem = fileSystem;

		public Stream OpenWrite( string path, FileMode mode ) => _fileSystem.OpenWrite( path, mode );
		public Stream OpenRead( string path ) => _fileSystem.OpenRead( path );
		public bool FileExists( string path ) => _fileSystem.FileExists( path );
		public bool DirectoryExists( string path ) => _fileSystem.DirectoryExists( path );
		public long FileSize( string path ) => _fileSystem.FileSize( path );
		public IEnumerable<string> FindFile( string folder, string pattern, bool recursive ) =>
			_fileSystem.FindFile( folder, pattern, recursive );
		public void DeleteFile( string path ) => _fileSystem.DeleteFile( path );
	}
}
