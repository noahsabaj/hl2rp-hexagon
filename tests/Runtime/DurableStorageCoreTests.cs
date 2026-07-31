#nullable enable

using System.IO;
using System.Threading.Tasks;
using Hexagon.V2.Persistence;
using HL2RP.V2.Runtime;

namespace HL2RP.V2.Tests.Runtime;

/// <summary>
/// Executes the production durable storage core (not a test-only duplicate) against a
/// physical boundary implementation that encodes the engine contract the factory's
/// <c>Sandbox.BaseFileSystem</c> adapter provides — no-shared-writer opens, relative
/// recursive listing, parent-directory creation. The DI-01/R-05 single-writer and
/// durability closures rest on the exact shipped code path.
/// </summary>
[TestClass]
public sealed class DurableStorageCoreTests
{
	[TestMethod]
	public async Task ExclusiveLeaseBlocksASecondWriterUntilReleased()
	{
		using var root = new TempRoot();
		var first = root.CreateCore();
		var second = root.CreateCore();

		var lease = await first.AcquireExclusiveLeaseAsync( "store/lease.lock" );
		await Assert.ThrowsAsync<PersistenceLeaseUnavailableException>(
			async () => await second.AcquireExclusiveLeaseAsync( "store/lease.lock" ) );

		await lease.DisposeAsync();
		Assert.IsTrue( lease.IsReleased );
		var successor = await second.AcquireExclusiveLeaseAsync( "store/lease.lock" );
		await successor.DisposeAsync();
	}

	[TestMethod]
	public async Task ImmutablePublicationIsAbsentOrCompleteAndLeavesNoStaging()
	{
		using var root = new TempRoot();
		var core = root.CreateCore();
		var content = new byte[] { 1, 2, 3, 4, 5 };

		Assert.IsTrue( await core.TryWriteImmutableAsync( "store/wal/frames/one.frame", content ) );
		Assert.IsFalse( await core.TryWriteImmutableAsync( "store/wal/frames/one.frame", new byte[] { 9 } ),
			"A second write to an immutable path must refuse without touching the original." );

		var read = await core.ReadAsync( "store/wal/frames/one.frame" );
		CollectionAssert.AreEqual( content, read!.Value.ToArray() );
		var listed = await core.ListAsync( "store" );
		Assert.HasCount( 1, listed );
		Assert.AreEqual( "store/wal/frames/one.frame", listed[0] );
		Assert.IsFalse( listed.Any( value => value.EndsWith( ".staging", StringComparison.Ordinal ) ),
			"Staged bytes must never remain visible after publication." );

		await core.DeleteAsync( "store/wal/frames/one.frame" );
		Assert.IsFalse( await core.ExistsAsync( "store/wal/frames/one.frame" ) );
	}

	[TestMethod]
	public async Task PathTraversalIsRejectedBeforeAnyPhysicalResolution()
	{
		using var root = new TempRoot();
		var core = root.CreateCore();

		await Assert.ThrowsAsync<ArgumentException>(
			async () => await core.ReadAsync( "store/../escape" ) );
		await Assert.ThrowsAsync<ArgumentException>(
			async () => await core.TryWriteImmutableAsync( "store//double", new byte[] { 1 } ) );
	}

	[TestMethod]
	public async Task ProviderRoundTripRecoversExactlyThroughTheProductionCore()
	{
		using var root = new TempRoot();
		await using ( var provider = CreateProvider( root.CreateCore() ) )
		{
			await provider.InitializeAsync();
			var repository = provider.Repository<CoreTestDocument>( "documents" );
			await using var unit = provider.BeginUnitOfWork();
			unit.Create( repository, "one", new CoreTestDocument( "one", 42 ) );
			Assert.IsTrue( (await unit.CommitAsync()).Succeeded );
			Assert.IsTrue( (await provider.ShutdownAsync()).IsClean );
		}

		await using var recovered = CreateProvider( root.CreateCore() );
		await recovered.InitializeAsync();
		Assert.AreEqual( 42, recovered.Repository<CoreTestDocument>( "documents" ).Find( "one" )!.Value.Score );
		Assert.IsTrue( (await recovered.ShutdownAsync()).IsClean );
	}

	private static FileSystemPersistenceProvider CreateProvider( HL2RPDurableStorageCore core ) => new(
		core,
		new FileSystemPersistenceOptions( "hl2rp-core-roundtrip" ) { CheckpointEveryCommits = 0 },
		new PersistedTypeRegistry().Register<CoreTestDocument>(
			new PersistedTypeKey( "hl2rp.core-test" ), 1, PersistedValuePublication.Immutable ) );

	private sealed record CoreTestDocument( string Name, int Score );

	private sealed class TempRoot : IDisposable
	{
		private readonly string _root = Path.Combine(
			Path.GetTempPath(), $"hl2rp-core-storage-{Guid.NewGuid():N}" );

		public TempRoot() => Directory.CreateDirectory( _root );

		public HL2RPDurableStorageCore CreateCore() => new( new PhysicalStorageFileSystem( _root ) );

		public void Dispose()
		{
			if ( Directory.Exists( _root ) ) Directory.Delete( _root, recursive: true );
		}
	}

	/// <summary>
	/// Encodes the engine contract of the factory's <c>Sandbox.BaseFileSystem</c> adapter:
	/// writes create missing parent directories and open the backing file with
	/// <c>FileShare.None</c> (Zio's <c>PhysicalFileSystem</c> default), reads admit shared
	/// readers, and <c>FindFile</c> yields paths relative to the queried folder.
	/// </summary>
	private sealed class PhysicalStorageFileSystem : IHL2RPStorageFileSystem
	{
		private readonly string _root;

		public PhysicalStorageFileSystem( string root ) => _root = root;

		public Stream OpenWrite( string path, FileMode mode )
		{
			var physical = Resolve( path );
			Directory.CreateDirectory( Path.GetDirectoryName( physical )! );
			return new FileStream( physical, mode, FileAccess.Write, FileShare.None );
		}

		public Stream OpenRead( string path ) =>
			new FileStream( Resolve( path ), FileMode.Open, FileAccess.Read, FileShare.Read );

		public bool FileExists( string path ) => File.Exists( Resolve( path ) );

		public bool DirectoryExists( string path ) => Directory.Exists( Resolve( path ) );

		public long FileSize( string path ) => new FileInfo( Resolve( path ) ).Length;

		public IEnumerable<string> FindFile( string folder, string pattern, bool recursive )
		{
			var directory = Resolve( folder );
			return Directory.EnumerateFiles(
					directory, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly )
				.Select( physical => Path.GetRelativePath( directory, physical ) );
		}

		public void DeleteFile( string path ) => File.Delete( Resolve( path ) );

		private string Resolve( string path ) =>
			Path.GetFullPath( Path.Combine( _root, path.Replace( '/', Path.DirectorySeparatorChar ) ) );
	}
}
