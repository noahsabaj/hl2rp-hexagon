#nullable enable

using System.IO;
using System.Threading.Tasks;
using Hexagon.V2.Persistence;
using HL2RP.V2.Runtime;

namespace HL2RP.V2.Tests.Runtime;

/// <summary>
/// Executes the production dedicated-server storage core (not a test-only duplicate)
/// against a temporary physical root: the DI-01/R-05 single-writer and durability
/// closures now rest on the exact shipped code path.
/// </summary>
[TestClass]
public sealed class PhysicalPersistenceStorageCoreTests
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

	private static FileSystemPersistenceProvider CreateProvider( HL2RPPhysicalPersistenceStorageCore core ) => new(
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

		public HL2RPPhysicalPersistenceStorageCore CreateCore() => new(
			logical => Path.GetFullPath( Path.Combine( _root, logical.Replace( '/', Path.DirectorySeparatorChar ) ) ) );

		public void Dispose()
		{
			if ( Directory.Exists( _root ) ) Directory.Delete( _root, recursive: true );
		}
	}
}
