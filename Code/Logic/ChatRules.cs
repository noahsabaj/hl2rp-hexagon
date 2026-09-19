#nullable enable

using System;
using System.Globalization;
using System.Text;

namespace HL2RP.Logic;

public enum ChatChannel
{
	Say,
	Whisper,
	Yell,
	Me,
	Ooc
}

public readonly record struct ChatMessage( ChatChannel Channel, string Text );

/// <summary>What a player typed, turned into a channel and clean text. The host decides who hears it.</summary>
public static class ChatRules
{
	public const int MaximumScalars = 512;

	/// <summary>Hearing range in world units, or null for a channel everyone receives.</summary>
	public static float? Range( ChatChannel channel ) => channel switch
	{
		ChatChannel.Whisper => 90f,
		ChatChannel.Say or ChatChannel.Me => 300f,
		ChatChannel.Yell => 900f,
		_ => null
	};

	public static Result<ChatMessage> Parse( string? raw )
	{
		if ( raw is null ) return Result<ChatMessage>.Fail( ErrorCode.Invalid, "Say something." );
		var text = raw.Trim();
		var channel = ChatChannel.Say;
		foreach ( var (prefix, mapped) in Prefixes )
		{
			if ( !text.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) ) continue;
			// "/w" must not swallow "/wave": a prefix ends at a space or the end of the line.
			if ( prefix != "//" && text.Length > prefix.Length && text[prefix.Length] != ' ' ) continue;
			channel = mapped;
			text = text[prefix.Length..].TrimStart();
			break;
		}
		var clean = Normalize( text );
		return clean.Ok
			? Result<ChatMessage>.Success( new ChatMessage( channel, clean.Value ) )
			: Result<ChatMessage>.Fail( clean.Code, clean.Message );
	}

	public static Result<string> Normalize( string text )
	{
		for ( var index = 0; index < text.Length; index++ )
		{
			if ( char.IsHighSurrogate( text[index] ) )
			{
				if ( index + 1 >= text.Length || !char.IsLowSurrogate( text[index + 1] ) )
					return Result<string>.Fail( ErrorCode.Invalid, "Message contains invalid Unicode." );
				index++;
			}
			else if ( char.IsLowSurrogate( text[index] ) )
				return Result<string>.Fail( ErrorCode.Invalid, "Message contains invalid Unicode." );
		}
		var normalized = text.Normalize( NormalizationForm.FormC ).Trim();
		if ( normalized.Length == 0 ) return Result<string>.Fail( ErrorCode.Invalid, "Say something." );
		var scalars = 0;
		for ( var index = 0; index < normalized.Length; index++ )
		{
			if ( char.IsControl( normalized[index] ) ||
				CharUnicodeInfo.GetUnicodeCategory( normalized, index ) == UnicodeCategory.Format )
				return Result<string>.Fail( ErrorCode.Invalid, "Message contains control characters." );
			if ( char.IsHighSurrogate( normalized[index] ) ) index++;
			scalars++;
		}
		return scalars > MaximumScalars
			? Result<string>.Fail( ErrorCode.Invalid, $"Message is longer than {MaximumScalars} characters." )
			: Result<string>.Success( normalized );
	}

	public static string Format( ChatChannel channel, string speaker, string text ) => channel switch
	{
		ChatChannel.Whisper => $"{speaker} whispers \"{text}\"",
		ChatChannel.Yell => $"{speaker} yells \"{text}\"",
		ChatChannel.Me => $"** {speaker} {text}",
		ChatChannel.Ooc => $"[OOC] {speaker}: {text}",
		_ => $"{speaker} says \"{text}\""
	};

	private static readonly (string Prefix, ChatChannel Channel)[] Prefixes =
	{
		("//", ChatChannel.Ooc), ("/ooc", ChatChannel.Ooc), ("/me", ChatChannel.Me),
		("/w", ChatChannel.Whisper), ("/y", ChatChannel.Yell)
	};
}
