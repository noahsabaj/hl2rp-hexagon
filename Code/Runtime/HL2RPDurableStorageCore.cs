#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Persistence;

namespace HL2RP.V2.Runtime;

/// <summary>
/// The exact whitelist-safe filesystem surface of <c>Sandbox.BaseFileSystem</c> that the
/// durable storage core consumes. Implementations must preserve the engine contract:
/// <c>OpenWrite</c> creates missing parent directories and opens the physical backing
/// file with no shared writer (Zio's <c>PhysicalFileSystem</c> default share), which is
/// what makes the storage lease process-exclusive; <c>FindFile</c> yields paths relative
/// to the queried folder.
/// </summary>
internal interface IHL2RPStorageFileSystem
{
	Stream OpenWrite( string path, FileMode mode );
	Stream OpenRead( string path );
	bool FileExists( string path );
	bool DirectoryExists( string path );
	long FileSize( string path );
	IEnumerable<string> FindFile( string folder, string pattern, bool recursive );
	void DeleteFile( string path );
}

/// <summary>
/// Engine-neutral core of the durable storage protocol shared by every HL2RP host shape:
/// path normalization and confinement, the exclusive-writer lease, staged and verified
/// immutable publication, and ordinal-ordered listing. Whitelist-safe code has no atomic
/// rename and no flush-to-disk, so publication stages, verifies, then copies — a crash
/// can leave one torn artifact at the final name, which Hexagon's recovery contract
/// (<c>IPersistenceStorage</c>) explicitly tolerates per WAL metadata class — and
/// durability is bounded by the operating-system cache rather than the disk cache. The
/// unit-test suite executes this exact production code against a physical boundary
/// implementation (DI-01/R-05 evidence).
/// </summary>
internal sealed class HL2RPDurableStorageCore : IPersistenceStorage
{
	private readonly IHL2RPStorageFileSystem _fileSystem;
	private readonly object _sync = new();

	public HL2RPDurableStorageCore( IHL2RPStorageFileSystem fileSystem ) =>
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
				// The boundary's OpenWrite opens the physical backing store without a
				// shared writer. Holding the stream for the provider lifetime prevents a
				// second provider — in this or any other process — from acquiring the
				// same logical root.
				stream = _fileSystem.OpenWrite( normalized, FileMode.OpenOrCreate );
				stream.SetLength( 0 );
				var marker = Encoding.UTF8.GetBytes(
					$"lease={Guid.NewGuid():N};acquired={DateTimeOffset.UtcNow:O}" );
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
			// The boundary exposes no atomic rename to whitelist-safe code, so the write
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
