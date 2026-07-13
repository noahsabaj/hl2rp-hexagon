#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Persistence;

namespace HL2RP.V2.Runtime;

/// <summary>
/// Serializes release attempts while retaining the resource until its authoritative
/// release probe succeeds. A throwing release can therefore be retried without
/// falsely reporting that the process no longer owns the persistence root.
/// </summary>
internal sealed class HL2RPRetryablePersistenceLease<TResource> : IPersistenceLease
	where TResource : class
{
	private readonly SemaphoreSlim _releaseGate = new( 1, 1 );
	private readonly Func<TResource, bool> _isReleased;
	private readonly Func<TResource, ValueTask> _releaseAsync;
	private TResource? _resource;

	public HL2RPRetryablePersistenceLease(
		string path,
		TResource resource,
		Func<TResource, bool> isReleased,
		Func<TResource, ValueTask> releaseAsync )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( path );
		Path = path;
		_resource = resource ?? throw new ArgumentNullException( nameof(resource) );
		_isReleased = isReleased ?? throw new ArgumentNullException( nameof(isReleased) );
		_releaseAsync = releaseAsync ?? throw new ArgumentNullException( nameof(releaseAsync) );
	}

	public string Path { get; }
	public bool IsReleased
	{
		get
		{
			var resource = Interlocked.CompareExchange( ref _resource, null, null );
			return resource is null || _isReleased( resource );
		}
	}

	public async ValueTask DisposeAsync()
	{
		await _releaseGate.WaitAsync();
		try
		{
			var resource = Interlocked.CompareExchange( ref _resource, null, null );
			if ( resource is null ) return;
			if ( _isReleased( resource ) )
			{
				Interlocked.CompareExchange( ref _resource, null, resource );
				return;
			}

			try
			{
				await _releaseAsync( resource );
			}
			finally
			{
				if ( _isReleased( resource ) )
					Interlocked.CompareExchange( ref _resource, null, resource );
			}

			if ( !_isReleased( resource ) )
				throw new InvalidOperationException( $"Persistence lease '{Path}' did not release its resource." );
		}
		finally
		{
			_releaseGate.Release();
		}
	}
}
