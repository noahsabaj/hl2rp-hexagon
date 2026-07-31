#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace HL2RP.V2.Runtime;

public sealed record HL2RPMaintenanceFailure(
	Exception Exception,
	int ConsecutiveFailures,
	TimeSpan RetryDelay );

public sealed record HL2RPMaintenanceStatus(
	bool Running,
	bool Pending,
	int ConsecutiveFailures,
	DateTimeOffset? LastSuccessAtUtc,
	DateTimeOffset? LastFailureAtUtc,
	TimeSpan? RetryDelay );

/// <summary>
/// Observes every maintenance invocation, prevents overlap, coalesces frame
/// signals, and retries transient failures without leaving the host wedged.
/// </summary>
public sealed class HL2RPMaintenanceSupervisor : IAsyncDisposable
{
	public static readonly TimeSpan DefaultMinimumInterval = TimeSpan.FromMilliseconds( 100 );

	private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds( 250 );
	private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds( 30 );
	private readonly object _sync = new();
	private readonly Func<CancellationToken, ValueTask> _tick;
	private readonly Func<TimeSpan, CancellationToken, Task> _delay;
	private readonly Func<DateTimeOffset> _utcNow;
	private readonly Action<HL2RPMaintenanceFailure> _failureSink;
	private readonly TimeSpan _minimumInterval;
	private readonly CancellationTokenSource _lifetime = new();
	private Task? _runner;
	private bool _pending;
	private bool _disposed;
	private int _consecutiveFailures;
	private DateTimeOffset? _lastSuccessAtUtc;
	private DateTimeOffset? _lastFailureAtUtc;
	private DateTimeOffset? _lastTickStartedAtUtc;
	private TimeSpan? _retryDelay;

	public HL2RPMaintenanceSupervisor(
		Func<CancellationToken, ValueTask> tick,
		Action<HL2RPMaintenanceFailure> failureSink,
		Func<TimeSpan, CancellationToken, Task>? delay = null,
		Func<DateTimeOffset>? utcNow = null,
		TimeSpan? minimumInterval = null )
	{
		_tick = tick ?? throw new ArgumentNullException( nameof(tick) );
		_failureSink = failureSink ?? throw new ArgumentNullException( nameof(failureSink) );
		_delay = delay ?? Task.Delay;
		_utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
		_minimumInterval = minimumInterval ?? DefaultMinimumInterval;
		if ( _minimumInterval < TimeSpan.Zero )
			throw new ArgumentOutOfRangeException( nameof(minimumInterval) );
	}

	public HL2RPMaintenanceStatus Status
	{
		get
		{
			lock ( _sync )
				return new HL2RPMaintenanceStatus(
					_runner is not null, _pending, _consecutiveFailures,
					_lastSuccessAtUtc, _lastFailureAtUtc, _retryDelay );
		}
	}

	public void RequestTick()
	{
		lock ( _sync )
		{
			if ( _disposed ) return;
			_pending = true;
			_runner ??= RunAsync();
		}
	}

	private async Task RunAsync()
	{
		try
		{
			// Ensure RequestTick stores the runner before even a fully synchronous tick
			// can complete and clear it.
			await Task.Yield();
			while ( true )
			{
				// Frame-driven tick requests arrive at render rate; pacing keeps the
				// steady-state maintenance cost bounded regardless of frame rate. Pacing
				// is strictly best-effort: a broken clock or delay must never stop or
				// delay maintenance beyond skipping the pause.
				var pace = TimeSpan.Zero;
				try
				{
					lock ( _sync )
					{
						if ( _lastTickStartedAtUtc is DateTimeOffset last )
							pace = _minimumInterval - (_utcNow() - last);
					}
				}
				catch
				{
				}
				if ( pace > TimeSpan.Zero )
				{
					try
					{
						await _delay( pace, _lifetime.Token );
					}
					catch ( OperationCanceledException ) when ( _lifetime.IsCancellationRequested )
					{
						return;
					}
					catch
					{
					}
				}
				lock ( _sync ) _pending = false;
				try
				{
					lock ( _sync ) _lastTickStartedAtUtc = _utcNow();
				}
				catch
				{
				}
				try
				{
					await _tick( _lifetime.Token );
					lock ( _sync )
					{
						_consecutiveFailures = 0;
						_retryDelay = null;
						_lastSuccessAtUtc = _utcNow();
					}
				}
				catch ( OperationCanceledException ) when ( _lifetime.IsCancellationRequested )
				{
					return;
				}
				catch ( Exception exception )
				{
					HL2RPMaintenanceFailure failure;
					lock ( _sync )
					{
						_consecutiveFailures++;
						_lastFailureAtUtc = _utcNow();
						_retryDelay = RetryDelay( _consecutiveFailures );
						failure = new HL2RPMaintenanceFailure( exception, _consecutiveFailures, _retryDelay.Value );
					}
					Report( failure );
					await _delay( failure.RetryDelay, _lifetime.Token );
					continue;
				}

				lock ( _sync )
				{
					if ( _pending ) continue;
					return;
				}
			}
		}
		catch ( OperationCanceledException ) when ( _lifetime.IsCancellationRequested ) { }
		catch ( Exception exception )
		{
			int failures;
			TimeSpan retry;
			lock ( _sync )
			{
				failures = ++_consecutiveFailures;
				retry = RetryDelay( failures );
				_retryDelay = retry;
			}
			Report( new HL2RPMaintenanceFailure( exception, failures, retry ) );
		}
		finally
		{
			lock ( _sync )
			{
				var restart = _pending && !_disposed && !_lifetime.IsCancellationRequested;
				_runner = null;
				_pending = false;
				if ( restart )
				{
					_pending = true;
					_runner = RunAsync();
				}
			}
		}
	}

	private void Report( HL2RPMaintenanceFailure failure )
	{
		try { _failureSink( failure ); }
		catch { /* Reporting must never terminate supervision. */ }
	}

	public async ValueTask DisposeAsync()
	{
		Task? runner;
		lock ( _sync )
		{
			if ( _disposed ) return;
			_disposed = true;
			_pending = false;
			runner = _runner;
		}
		_lifetime.Cancel();
		if ( runner is not null ) await runner;
		_lifetime.Dispose();
	}

	private static TimeSpan RetryDelay( int consecutiveFailures )
	{
		var exponent = Math.Min( consecutiveFailures - 1, 7 );
		var milliseconds = InitialRetryDelay.TotalMilliseconds * (1 << exponent);
		return TimeSpan.FromMilliseconds( Math.Min( milliseconds, MaximumRetryDelay.TotalMilliseconds ) );
	}
}
