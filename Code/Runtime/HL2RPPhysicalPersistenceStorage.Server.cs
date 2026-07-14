#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Persistence;
using Microsoft.Win32.SafeHandles;
using Sandbox;

namespace HL2RP.V2.Runtime;

/// <summary>
/// Dedicated-server implementation of Hexagon's durable storage protocol. The s&amp;box compiler
/// wraps every <c>.Server.cs</c> file in the <c>SERVER</c> compilation boundary and strips its
/// body from client code archives. Raw operating-system access must remain in this file so a
/// joining client can pass assembly access control while production retains write-through,
/// exclusive physical-file semantics.
/// </summary>
internal sealed class HL2RPPhysicalPersistenceStorage : IPersistenceStorage
{
	private readonly BaseFileSystem _fileSystem;
	private readonly object _sync = new();

	public HL2RPPhysicalPersistenceStorage( BaseFileSystem fileSystem ) =>
		_fileSystem = fileSystem ?? throw new ArgumentNullException( nameof(fileSystem) );

	public ValueTask<IPersistenceLease> AcquireExclusiveLeaseAsync(
		string path,
		CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		var normalized = Normalize( path );
		lock ( _sync )
		{
			var physical = Resolve( normalized );
			Directory.CreateDirectory( RequireParentDirectory( normalized, physical ) );
			return ValueTask.FromResult<IPersistenceLease>(
				PhysicalPersistenceLease.Acquire( normalized, physical ) );
		}
	}

	public ValueTask<bool> ExistsAsync( string path, CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync ) return ValueTask.FromResult( File.Exists( Resolve( Normalize( path ) ) ) );
	}

	public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
		string path,
		CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync )
		{
			var normalized = Normalize( path );
			var physical = Resolve( normalized );
			if ( !File.Exists( physical ) ) return ValueTask.FromResult<ReadOnlyMemory<byte>?>( null );
			var info = new FileInfo( physical );
			if ( info.Length > int.MaxValue )
				throw new IOException( $"Persistence file '{normalized}' exceeds the supported in-memory read size." );
			ReadOnlyMemory<byte>? result = File.ReadAllBytes( physical );
			return ValueTask.FromResult( result );
		}
	}

	public ValueTask<IReadOnlyList<string>> ListAsync(
		string prefix,
		CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync )
		{
			var normalized = Normalize( prefix );
			var directory = Resolve( normalized );
			if ( !Directory.Exists( directory ) )
				return ValueTask.FromResult<IReadOnlyList<string>>( Array.Empty<string>() );
			IReadOnlyList<string> paths = Directory.EnumerateFiles(
					directory, "*", SearchOption.AllDirectories )
				.Select( physical =>
					$"{normalized}/{Path.GetRelativePath( directory, physical ).Replace( '\\', '/' )}" )
				.OrderBy( path => path, StringComparer.Ordinal )
				.ToArray();
			return ValueTask.FromResult( paths );
		}
	}

	public ValueTask<bool> TryWriteImmutableAsync(
		string path,
		ReadOnlyMemory<byte> content,
		CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync )
		{
			var normalized = Normalize( path );
			var destination = Resolve( normalized );
			if ( File.Exists( destination ) ) return ValueTask.FromResult( false );
			var directory = RequireParentDirectory( normalized, destination );
			Directory.CreateDirectory( directory );
			var staging = Path.Combine(
				directory,
				$".{Path.GetFileName( destination )}.{Guid.NewGuid():N}.staging" );
			try
			{
				using ( var stream = new FileStream(
					staging,
					FileMode.CreateNew,
					FileAccess.Write,
					FileShare.None,
					bufferSize: 4096,
					FileOptions.WriteThrough ) )
				{
					stream.Write( content.Span );
					stream.Flush( flushToDisk: true );
					if ( stream.Length != content.Length )
						throw new IOException(
							$"Immutable persistence staging write for '{normalized}' produced " +
							$"{stream.Length} of {content.Length} bytes." );
				}
				try
				{
					File.Move( staging, destination, overwrite: false );
					return ValueTask.FromResult( true );
				}
				catch ( IOException ) when ( File.Exists( destination ) )
				{
					return ValueTask.FromResult( false );
				}
			}
			finally
			{
				if ( File.Exists( staging ) ) File.Delete( staging );
			}
		}
	}

	public ValueTask DeleteAsync( string path, CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync )
		{
			var physical = Resolve( Normalize( path ) );
			if ( File.Exists( physical ) ) File.Delete( physical );
		}
		return ValueTask.CompletedTask;
	}

	private string Resolve( string normalized )
	{
		try
		{
			return _fileSystem.GetFullPath( normalized );
		}
		catch ( Exception exception )
		{
			throw new IOException(
				$"Persistence storage cannot resolve physical path '{normalized}'.", exception );
		}
	}

	private static string RequireParentDirectory( string logicalPath, string physicalPath ) =>
		Path.GetDirectoryName( physicalPath )
		?? throw new IOException( $"Persistence path '{logicalPath}' has no physical parent directory." );

	private static string Normalize( string path )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( path );
		var normalized = path.Replace( '\\', '/' ).Trim( '/' );
		if ( normalized.Split( '/' ).Any( segment => segment is "" or "." or ".." ) )
			throw new ArgumentException( "Persistence path must be normalized and relative.", nameof(path) );
		return normalized;
	}

	private static class PhysicalPersistenceLease
	{
		public static IPersistenceLease Acquire( string logicalPath, string physicalPath )
		{
			FileStream? stream = null;
			try
			{
				stream = new FileStream(
					physicalPath,
					FileMode.OpenOrCreate,
					FileAccess.ReadWrite,
					FileShare.None,
					bufferSize: 1,
					FileOptions.WriteThrough );
				stream.SetLength( 0 );
				var marker = Encoding.UTF8.GetBytes(
					$"pid={Environment.ProcessId};acquired={DateTimeOffset.UtcNow:O}" );
				stream.Write( marker );
				stream.Flush( flushToDisk: true );
				var resource = new PhysicalLeaseResource( stream, stream.SafeFileHandle );
				return new HL2RPRetryablePersistenceLease<PhysicalLeaseResource>(
					logicalPath,
					resource,
					static value => value.Handle.IsClosed,
					static value => value.Stream.DisposeAsync() );
			}
			catch ( IOException exception )
			{
				stream?.Dispose();
				throw new PersistenceLeaseUnavailableException(
					$"Persistence lease '{logicalPath}' is already held or unavailable.", exception );
			}
		}

		private sealed record PhysicalLeaseResource( FileStream Stream, SafeFileHandle Handle );
	}
}
