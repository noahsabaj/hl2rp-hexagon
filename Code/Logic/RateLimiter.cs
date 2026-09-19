#nullable enable

using System;

namespace HL2RP.Logic;

/// <summary>A token bucket. Time is passed in so the host can use the engine clock and tests a fake one.</summary>
public sealed class RateLimiter
{
	private readonly double _capacity;
	private readonly double _refillPerSecond;
	private double _tokens;
	private double _lastSeconds = double.NaN;

	public RateLimiter( double capacity, double refillPerSecond )
	{
		if ( capacity <= 0 || refillPerSecond <= 0 ) throw new ArgumentOutOfRangeException( nameof(capacity) );
		_capacity = capacity;
		_refillPerSecond = refillPerSecond;
		_tokens = capacity;
	}

	public bool TryTake( double nowSeconds, double cost = 1 )
	{
		if ( !double.IsNaN( _lastSeconds ) && nowSeconds > _lastSeconds )
			_tokens = Math.Min( _capacity, _tokens + (nowSeconds - _lastSeconds) * _refillPerSecond );
		_lastSeconds = nowSeconds;
		if ( _tokens < cost ) return false;
		_tokens -= cost;
		return true;
	}
}
