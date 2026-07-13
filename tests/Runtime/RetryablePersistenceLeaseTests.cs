#nullable enable

using System.IO;
using System.Threading;
using Hexagon.V2.Persistence;
using HL2RP.V2.Runtime;
using Microsoft.Win32.SafeHandles;

namespace HL2RP.V2.Tests.Runtime;

[TestClass]
public sealed class RetryablePersistenceLeaseTests
{
	[TestMethod]
	public async Task FailedPhysicalReleaseRemainsHeldAndProviderShutdownCanRetryTruthfully()
	{
		await using var storage = new FailOnceLeaseStorage();
		await using var provider = CreateProvider( storage );
		await provider.InitializeAsync();
		storage.FailNextRelease = true;

		var failed = await provider.ShutdownAsync();

		Assert.IsFalse( failed.LeaseReleased );
		Assert.IsFalse( failed.IsClean );
		Assert.AreEqual( 1, storage.ReleaseAttempts );
		await using ( var blocked = CreateProvider( storage ) )
		{
			await Assert.ThrowsAsync<PersistenceLeaseUnavailableException>(
				async () => await blocked.InitializeAsync() );
		}

		var retried = await provider.ShutdownAsync();
		Assert.IsTrue( retried.LeaseReleased );
		Assert.IsTrue( retried.IsClean );
		Assert.AreEqual( 2, storage.ReleaseAttempts );
		Assert.AreSame( retried, await provider.ShutdownAsync() );
		Assert.AreEqual( 2, storage.ReleaseAttempts, "Completed shutdown must remain idempotent." );

		await using var successor = CreateProvider( storage );
		await successor.InitializeAsync();
	}

	private static FileSystemPersistenceProvider CreateProvider( IPersistenceStorage storage ) => new(
		storage,
		new FileSystemPersistenceOptions( "hl2rp-physical-lease-test" ) { CheckpointEveryCommits = 0 },
		new PersistedTypeRegistry() );

	private sealed class FailOnceLeaseStorage : IPersistenceStorage, IAsyncDisposable
	{
		private readonly InMemoryPersistenceStorage _inner = new();
		private readonly string _root = Path.Combine(
			Path.GetTempPath(),
			$"hl2rp-retryable-physical-lease-{Guid.NewGuid():N}" );
		private readonly string _leasePath;

		public FailOnceLeaseStorage()
		{
			Directory.CreateDirectory( _root );
			_leasePath = Path.Combine( _root, "lease.lock" );
		}

		public bool FailNextRelease { get; set; }
		public int ReleaseAttempts { get; private set; }

		public ValueTask<IPersistenceLease> AcquireExclusiveLeaseAsync(
			string path,
			CancellationToken cancellationToken = default )
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				var stream = new FileStream(
					_leasePath,
					FileMode.OpenOrCreate,
					FileAccess.ReadWrite,
					FileShare.None );
				var resource = new FailOnceLeaseResource( this, stream, stream.SafeFileHandle );
				IPersistenceLease lease = new HL2RPRetryablePersistenceLease<FailOnceLeaseResource>(
					path,
					resource,
					static value => value.Handle.IsClosed,
					static value => value.DisposeAsync() );
				return ValueTask.FromResult( lease );
			}
			catch ( IOException exception )
			{
				throw new PersistenceLeaseUnavailableException(
					$"Persistence lease '{path}' is already held or unavailable.", exception );
			}
		}

		public ValueTask<bool> ExistsAsync( string path, CancellationToken cancellationToken = default ) =>
			_inner.ExistsAsync( path, cancellationToken );

		public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
			string path,
			CancellationToken cancellationToken = default ) =>
			_inner.ReadAsync( path, cancellationToken );

		public ValueTask<IReadOnlyList<string>> ListAsync(
			string prefix,
			CancellationToken cancellationToken = default ) =>
			_inner.ListAsync( prefix, cancellationToken );

		public ValueTask<bool> TryWriteImmutableAsync(
			string path,
			ReadOnlyMemory<byte> content,
			CancellationToken cancellationToken = default ) =>
			_inner.TryWriteImmutableAsync( path, content, cancellationToken );

		public ValueTask DeleteAsync( string path, CancellationToken cancellationToken = default ) =>
			_inner.DeleteAsync( path, cancellationToken );

		public ValueTask DisposeAsync()
		{
			Directory.Delete( _root, recursive: true );
			return ValueTask.CompletedTask;
		}

		private sealed class FailOnceLeaseResource
		{
			private readonly FailOnceLeaseStorage _owner;

			public FailOnceLeaseResource(
				FailOnceLeaseStorage owner,
				FileStream stream,
				SafeFileHandle handle )
			{
				_owner = owner;
				Stream = stream;
				Handle = handle;
			}

			public FileStream Stream { get; }
			public SafeFileHandle Handle { get; }

			public async ValueTask DisposeAsync()
			{
				_owner.ReleaseAttempts++;
				if ( _owner.FailNextRelease )
				{
					_owner.FailNextRelease = false;
					throw new IOException( "Injected physical stream disposal failure." );
				}
				await Stream.DisposeAsync();
			}
		}
	}
}
