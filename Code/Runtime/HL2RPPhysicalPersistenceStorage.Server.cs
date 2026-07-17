#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Persistence;
using Sandbox;

namespace HL2RP.V2.Runtime;

/// <summary>
/// Dedicated-server implementation of Hexagon's durable storage protocol. The s&amp;box
/// compiler wraps every <c>.Server.cs</c> file in the <c>SERVER</c> compilation boundary
/// and strips its body from client code archives. This shim only binds the engine's
/// physical path resolution; every durability decision lives in
/// <c>HL2RPPhysicalPersistenceStorageCore</c>, which the unit-test suite executes
/// directly.
/// </summary>
internal sealed class HL2RPPhysicalPersistenceStorage : IPersistenceStorage
{
	private readonly HL2RPPhysicalPersistenceStorageCore _core;

	public HL2RPPhysicalPersistenceStorage( BaseFileSystem fileSystem )
	{
		ArgumentNullException.ThrowIfNull( fileSystem );
		_core = new HL2RPPhysicalPersistenceStorageCore( fileSystem.GetFullPath );
	}

	public ValueTask<IPersistenceLease> AcquireExclusiveLeaseAsync(
		string path,
		CancellationToken cancellationToken = default ) =>
		_core.AcquireExclusiveLeaseAsync( path, cancellationToken );

	public ValueTask<bool> ExistsAsync( string path, CancellationToken cancellationToken = default ) =>
		_core.ExistsAsync( path, cancellationToken );

	public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
		string path,
		CancellationToken cancellationToken = default ) =>
		_core.ReadAsync( path, cancellationToken );

	public ValueTask<IReadOnlyList<string>> ListAsync(
		string prefix,
		CancellationToken cancellationToken = default ) =>
		_core.ListAsync( prefix, cancellationToken );

	public ValueTask<bool> TryWriteImmutableAsync(
		string path,
		ReadOnlyMemory<byte> content,
		CancellationToken cancellationToken = default ) =>
		_core.TryWriteImmutableAsync( path, content, cancellationToken );

	public ValueTask DeleteAsync( string path, CancellationToken cancellationToken = default ) =>
		_core.DeleteAsync( path, cancellationToken );
}
