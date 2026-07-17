#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Persistence;
using Sandbox;

namespace HL2RP.V2.Runtime;

/// <summary>
/// Whitelist-safe persistence adapter used by the editor-hosted bootstrap flow. Production
/// dedicated servers select <c>HL2RPPhysicalPersistenceStorage</c> instead, retaining
/// write-through physical-file durability and a process-wide exclusive lease. Remote clients
/// carry this type in their streamed assembly but never construct persistence storage.
/// </summary>
internal sealed class HL2RPSandboxPersistenceStorage : IPersistenceStorage
{
	private readonly BaseFileSystem _fileSystem;
	private readonly object _sync = new();

	public HL2RPSandboxPersistenceStorage( BaseFileSystem fileSystem ) =>
		_fileSystem = fileSystem ?? throw new ArgumentNullException( nameof(fileSystem) );

	public ValueTask<IPersistenceLease> AcquireExclusiveLeaseAsync(
		string path,
		CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		var normalized = Normalize( path );
		lock ( _sync )
		{
			Stream? stream = null;
			try
			{
				// BaseFileSystem.OpenWrite ultimately opens the physical backing store without a
				// shared writer. Holding the stream for the provider lifetime prevents a second
				// editor-hosted provider from acquiring the same logical root.
				stream = _fileSystem.OpenWrite( normalized, FileMode.OpenOrCreate );
				stream.SetLength( 0 );
				var marker = Encoding.UTF8.GetBytes(
					$"editor={Guid.NewGuid():N};acquired={DateTimeOffset.UtcNow:O}" );
				stream.Write( marker );
				stream.Flush();
				return ValueTask.FromResult<IPersistenceLease>(
					new HL2RPRetryablePersistenceLease<Stream>(
						normalized,
						stream,
						static value => !value.CanWrite,
						static value => value.DisposeAsync() ) );
			}
			catch ( IOException exception )
			{
				stream?.Dispose();
				throw new PersistenceLeaseUnavailableException(
					$"Persistence lease '{normalized}' is already held or unavailable.", exception );
			}
		}
	}

	public ValueTask<bool> ExistsAsync( string path, CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync ) return ValueTask.FromResult( _fileSystem.FileExists( Normalize( path ) ) );
	}

	public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
		string path,
		CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync )
		{
			var normalized = Normalize( path );
			if ( !_fileSystem.FileExists( normalized ) )
				return ValueTask.FromResult<ReadOnlyMemory<byte>?>( null );
			var length = _fileSystem.FileSize( normalized );
			if ( length > int.MaxValue )
				throw new IOException(
					$"Persistence file '{normalized}' exceeds the supported in-memory read size." );
			using var stream = _fileSystem.OpenRead( normalized );
			var bytes = new byte[(int)length];
			stream.ReadExactly( bytes );
			return ValueTask.FromResult<ReadOnlyMemory<byte>?>( bytes );
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
			if ( !_fileSystem.DirectoryExists( normalized ) )
				return ValueTask.FromResult<IReadOnlyList<string>>( Array.Empty<string>() );
			IReadOnlyList<string> paths = _fileSystem.FindFile( normalized, "*", recursive: true )
				.Select( relative => $"{normalized}/{relative.Replace( '\\', '/' )}" )
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
			if ( _fileSystem.FileExists( normalized ) ) return ValueTask.FromResult( false );
			// BaseFileSystem exposes no atomic rename to whitelist-safe code, so the write
			// is staged and verified first and only then copied to the final name: a crash
			// mid-stage leaves a .staging file recovery already deletes, shrinking the
			// torn final-name window to the short copy — which the framework's torn-tail
			// recovery tolerates.
			var separator = normalized.LastIndexOf( '/' );
			var staging = separator < 0
				? $".{normalized}.{Guid.NewGuid():N}.staging"
				: $"{normalized[..separator]}/.{normalized[(separator + 1)..]}.{Guid.NewGuid():N}.staging";
			try
			{
				using ( var stream = _fileSystem.OpenWrite( staging, FileMode.CreateNew ) )
				{
					stream.Write( content.Span );
					stream.Flush();
				}
				var staged = ReadAllBytes( staging );
				if ( staged.Length != content.Length || !staged.AsSpan().SequenceEqual( content.Span ) )
					throw new IOException(
						$"Immutable persistence staging for '{normalized}' failed verification." );

				Stream? destination = null;
				try
				{
					destination = _fileSystem.OpenWrite( normalized, FileMode.CreateNew );
					destination.Write( staged );
					destination.Flush();
					if ( destination.Length != content.Length )
						throw new IOException(
							$"Immutable persistence write for '{normalized}' produced " +
							$"{destination.Length} of {content.Length} bytes." );
					return ValueTask.FromResult( true );
				}
				catch ( IOException ) when ( destination is null && _fileSystem.FileExists( normalized ) )
				{
					return ValueTask.FromResult( false );
				}
				catch
				{
					destination?.Dispose();
					destination = null;
					if ( _fileSystem.FileExists( normalized ) ) _fileSystem.DeleteFile( normalized );
					throw;
				}
				finally
				{
					destination?.Dispose();
				}
			}
			finally
			{
				if ( _fileSystem.FileExists( staging ) ) _fileSystem.DeleteFile( staging );
			}
		}
	}

	private byte[] ReadAllBytes( string normalized )
	{
		using var stream = _fileSystem.OpenRead( normalized );
		var bytes = new byte[stream.Length];
		stream.ReadExactly( bytes );
		return bytes;
	}

	public ValueTask DeleteAsync( string path, CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync )
		{
			var normalized = Normalize( path );
			if ( _fileSystem.FileExists( normalized ) ) _fileSystem.DeleteFile( normalized );
		}
		return ValueTask.CompletedTask;
	}

	private static string Normalize( string path )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( path );
		var normalized = path.Replace( '\\', '/' ).Trim( '/' );
		if ( normalized.Split( '/' ).Any( segment => segment is "" or "." or ".." ) )
			throw new ArgumentException( "Persistence path must be normalized and relative.", nameof(path) );
		return normalized;
	}
}
