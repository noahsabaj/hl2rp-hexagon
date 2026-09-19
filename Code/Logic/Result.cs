#nullable enable

namespace HL2RP.Logic;

public enum ErrorCode
{
	None,
	Invalid,
	Denied,
	NotFound,
	Conflict,
	RateLimited,
	Internal
}

/// <summary>The outcome of a host-side operation. Failures carry a message safe to show the player.</summary>
public readonly record struct Result( ErrorCode Code, string Message )
{
	public bool Ok => Code == ErrorCode.None;
	public static Result Success() => new( ErrorCode.None, string.Empty );
	public static Result Fail( ErrorCode code, string message ) => new( code, message );
}

public readonly record struct Result<T>( ErrorCode Code, string Message, T Value )
{
	public bool Ok => Code == ErrorCode.None;
	public static Result<T> Success( T value ) => new( ErrorCode.None, string.Empty, value );
	public static Result<T> Fail( ErrorCode code, string message ) => new( code, message, default! );
	public Result ToResult() => new( Code, Message );
}
