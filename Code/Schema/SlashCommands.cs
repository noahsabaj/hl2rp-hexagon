#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;

namespace HL2RP.V2.Schema;

/// <summary>How a typed token becomes the <see cref="SnapshotValue"/> a handler reads.</summary>
public enum SlashParameterKind
{
	/// <summary>A single whitespace-delimited token, or a quoted run.</summary>
	Text = 0,
	/// <summary>Every remaining character, verbatim. Only legal as the final parameter.</summary>
	RestOfLine = 1,
	Integer = 2,
	Boolean = 3,
	/// <summary>A closed set the handler validates; carried as <see cref="SnapshotValueKind.Choice"/>.</summary>
	Choice = 4,
	/// <summary>
	/// An opaque runtime identifier with no human-readable handle (vendor sessions, inventories,
	/// individual item instances). Text invocation can only accept the raw GUID; these commands
	/// exist here for completeness and are driven in practice by their panels.
	/// </summary>
	Guid = 5,
	/// <summary>
	/// A character NAME the caller types, resolved to a character id before dispatch. This is the
	/// only parameter kind that needs outside knowledge, which is why binding takes a resolver.
	/// </summary>
	CharacterName = 6
}

public sealed record SlashCommandParameter(
	string Key,
	SlashParameterKind Kind,
	bool Required = true );

/// <summary>One typed command: what the player types, and where it lands.</summary>
public sealed record SlashCommandDescriptor(
	string Name,
	string CommandId,
	string Summary,
	IReadOnlyList<SlashCommandParameter> Parameters )
{
	/// <summary>A usage line, built from the parameters so it cannot drift from them.</summary>
	public string Usage
	{
		get
		{
			var builder = new StringBuilder( "/" ).Append( Name );
			foreach ( var parameter in Parameters )
			{
				builder.Append( parameter.Required ? " <" : " [" );
				builder.Append( parameter.Key );
				if ( parameter.Kind == SlashParameterKind.RestOfLine ) builder.Append( "..." );
				builder.Append( parameter.Required ? '>' : ']' );
			}

			return builder.ToString();
		}
	}
}

/// <summary>
/// Every schema command, addressable as text. HL2RP registers commands with a permission and a
/// cost class but shipped no way to invoke one except a purpose-built panel, so a command without
/// a panel was unreachable - which is why nothing could kill a character.
/// <para>
/// Parameter keys and kinds mirror what each handler actually reads; a completeness test asserts
/// this catalogue and the compiled command registry describe the same set, so a command cannot be
/// registered without deciding how it is typed.
/// </para>
/// </summary>
public static class SlashCommandCatalog
{
	private static SlashCommandParameter Text( string key, bool required = true ) =>
		new( key, SlashParameterKind.Text, required );

	private static SlashCommandParameter Rest( string key, bool required = true ) =>
		new( key, SlashParameterKind.RestOfLine, required );

	private static SlashCommandParameter Number( string key ) =>
		new( key, SlashParameterKind.Integer );

	private static SlashCommandParameter Flag( string key ) =>
		new( key, SlashParameterKind.Boolean );

	private static SlashCommandParameter Choice( string key ) =>
		new( key, SlashParameterKind.Choice );

	private static SlashCommandParameter Id( string key ) =>
		new( key, SlashParameterKind.Guid );

	private static SlashCommandParameter Who( string key ) =>
		new( key, SlashParameterKind.CharacterName );

	public static IReadOnlyList<SlashCommandDescriptor> All { get; } = Array.AsReadOnly( new[]
	{
		new SlashCommandDescriptor( "kill", HL2RPIds.Commands.AdministrationKill,
			"End a character's life. Requires the administration kill capability.",
			new[] { Who( "character" ) } ),
		new SlashCommandDescriptor( "respawn", HL2RPIds.Commands.CombatRespawn,
			"Respawn your own dead character.",
			Array.Empty<SlashCommandParameter>() ),
		new SlashCommandDescriptor( "introduce", HL2RPIds.Commands.Introduce,
			"Introduce yourself to a character so they know your name.",
			new[] { Who( "character" ) } ),
		new SlashCommandDescriptor( "civicdata", HL2RPIds.Commands.CivicData,
			"Open a character's civic record.",
			new[] { Who( "character" ) } ),
		new SlashCommandDescriptor( "priority", HL2RPIds.Commands.Priority,
			"Set a character's priority and civic record text.",
			new[] { Who( "character" ), Choice( "priority" ), Rest( "record" ) } ),
		new SlashCommandDescriptor( "restrain", HL2RPIds.Commands.RestraintSet,
			"Restrain or release a character, optionally searching them.",
			new[] { Who( "character" ), Flag( "restrain" ), Flag( "search" ) } ),
		new SlashCommandDescriptor( "objectives", HL2RPIds.Commands.CityObjectives,
			"Publish or amend a city objective. Quote the title if it contains spaces.",
			new[] { Text( "objective" ), Flag( "completed" ), Text( "title" ), Rest( "detail", false ) } ),
		new SlashCommandDescriptor( "audit", HL2RPIds.Commands.AdministrationAudit,
			"Publish the administration audit.",
			Array.Empty<SlashCommandParameter>() ),
		new SlashCommandDescriptor( "door", HL2RPIds.Commands.DoorOwnership,
			"Claim or release the door you are currently interacting with.",
			new[] { Choice( "intent" ) } ),
		new SlashCommandDescriptor( "permit", HL2RPIds.Commands.PermitPurchase,
			"Purchase a permit of the given kind.",
			new[] { Choice( "permit" ) } ),
		// "tune", not "radio": the radio CHANNEL owns the /radio prefix, and channels resolve before
		// commands, so naming this one radio would have made it permanently unreachable. Caught by
		// NoChannelPrefixShadowsACommandName on its first run, which is the whole point of that guard.
		new SlashCommandDescriptor( "tune", HL2RPIds.Commands.RadioFrequency,
			"Tune a radio item to a frequency.",
			new[] { Id( "item" ), Text( "frequency" ), Flag( "enabled" ) } ),
		new SlashCommandDescriptor( "note", HL2RPIds.Commands.NoteWrite,
			"Write the body of a note item.",
			new[] { Id( "item" ), Rest( "body" ) } ),
		new SlashCommandDescriptor( "entitlements", HL2RPIds.Commands.EntitlementQuery,
			"Query the entitlements held by a platform account.",
			new[] { Text( "account" ), Number( "revision" ) } ),
		new SlashCommandDescriptor( "grant", HL2RPIds.Commands.EntitlementGrant,
			"Grant an entitlement flag to a platform account.",
			new[] { Text( "account" ), Choice( "flag" ), Number( "revision" ) } ),
		new SlashCommandDescriptor( "revoke", HL2RPIds.Commands.EntitlementRevoke,
			"Revoke an entitlement flag from a platform account.",
			new[] { Text( "account" ), Choice( "flag" ), Number( "revision" ) } ),
		new SlashCommandDescriptor( "buy", HL2RPIds.Commands.CommerceBuy,
			"Buy from an open vendor session. The session id comes from the vendor panel.",
			new[] { Id( "session" ), Choice( "definition" ), Number( "quantity" ) } ),
		new SlashCommandDescriptor( "sell", HL2RPIds.Commands.CommerceSell,
			"Sell to an open vendor session. The ids come from the vendor panel.",
			new[] { Id( "session" ), Id( "inventory" ), Id( "item" ), Number( "quantity" ) } ),
		new SlashCommandDescriptor( "scanner", HL2RPIds.Commands.ScannerIntent,
			"Issue a scanner intent. Driven by the scanner UI in practice.",
			new[]
			{
				Id( "session" ), Choice( "intent" ), Number( "sequence" ), Number( "forward" ),
				Number( "right" ), Number( "up" ), Number( "yaw" ), Number( "pitch" )
			} )
	} );

	public static bool TryFind( string name, out SlashCommandDescriptor descriptor )
	{
		descriptor = All.FirstOrDefault( candidate =>
			string.Equals( candidate.Name, name, StringComparison.OrdinalIgnoreCase ) ||
			string.Equals( candidate.CommandId, name, StringComparison.OrdinalIgnoreCase ) )!;
		return descriptor is not null;
	}
}

/// <summary>One character a typed name could refer to.</summary>
public readonly record struct SlashCommandTarget( Guid CharacterId, string Name );

/// <summary>
/// Turns the name a player typed into the character it means. Kept separate from the parser and
/// free of any view type so the matching rule is unit tested: a wrong answer here targets the
/// wrong person with a lethal command, so "close enough" must fail rather than guess.
/// </summary>
public static class SlashCommandTargets
{
	public static OperationResult<Guid> Resolve(
		string typed, IReadOnlyList<SlashCommandTarget> candidates )
	{
		ArgumentNullException.ThrowIfNull( candidates );
		if ( string.IsNullOrWhiteSpace( typed ) )
			return OperationResult<Guid>.Failure( ErrorCode.InvalidArgument, "Name a character." );
		var needle = typed.Trim();

		var exact = candidates
			.Where( candidate => string.Equals( candidate.Name, needle, StringComparison.OrdinalIgnoreCase ) )
			.ToArray();
		if ( exact.Length == 1 ) return OperationResult<Guid>.Success( exact[0].CharacterId );
		if ( exact.Length > 1 ) return Ambiguous( needle, exact );

		// Only after an exact match fails, so someone named "Bar" is never beaten by "Barney".
		var partial = candidates
			.Where( candidate => candidate.Name.Contains( needle, StringComparison.OrdinalIgnoreCase ) )
			.ToArray();
		if ( partial.Length == 1 ) return OperationResult<Guid>.Success( partial[0].CharacterId );
		if ( partial.Length > 1 ) return Ambiguous( needle, partial );

		return OperationResult<Guid>.Failure(
			ErrorCode.NotFound, $"No character here matches '{needle}'." );
	}

	private static OperationResult<Guid> Ambiguous(
		string needle, IReadOnlyList<SlashCommandTarget> matches ) =>
		OperationResult<Guid>.Failure(
			ErrorCode.Conflict,
			$"'{needle}' matches {matches.Count} characters: " +
			$"{string.Join( ", ", matches.Select( match => match.Name ) )}. Quote the full name." );
}

/// <summary>A parsed command awaiting argument binding.</summary>
public sealed record SlashCommandParse(
	SlashCommandDescriptor Descriptor,
	IReadOnlyDictionary<string, string> Tokens );

/// <summary>
/// Turns a line of chat into a schema command. Engine-neutral on purpose: the chat box is Razor,
/// but nothing here needs a Scene, so the rule that decides what a player just asked for is unit
/// tested rather than only observable by typing in a running game.
/// </summary>
public static class SlashCommandParser
{
	public const char Prefix = '/';

	/// <summary>True when this line is a command rather than in-character speech.</summary>
	public static bool IsCommand( string? text ) =>
		!string.IsNullOrWhiteSpace( text ) && text.TrimStart().StartsWith( Prefix );

	/// <summary>
	/// Splits on whitespace, honouring double quotes so a character name survives as one token.
	/// A trailing unterminated quote is treated as closing at end of line rather than failing, so
	/// typing an unbalanced quote does not silently swallow the command.
	/// </summary>
	public static IReadOnlyList<string> Tokenize( string text )
	{
		ArgumentNullException.ThrowIfNull( text );
		var tokens = new List<string>();
		var current = new StringBuilder();
		var quoted = false;
		var started = false;
		for ( var index = 0; index < text.Length; index++ )
		{
			var character = text[index];
			if ( character == '\\' && index + 1 < text.Length && text[index + 1] == '"' )
			{
				current.Append( '"' );
				started = true;
				index++;
				continue;
			}

			if ( character == '"' )
			{
				quoted = !quoted;
				started = true;
				continue;
			}

			if ( !quoted && char.IsWhiteSpace( character ) )
			{
				if ( started ) tokens.Add( current.ToString() );
				current.Clear();
				started = false;
				continue;
			}

			current.Append( character );
			started = true;
		}

		if ( started ) tokens.Add( current.ToString() );
		return tokens;
	}

	public static OperationResult<SlashCommandParse> Parse( string text )
	{
		if ( !IsCommand( text ) )
			return OperationResult<SlashCommandParse>.Failure(
				ErrorCode.InvalidArgument, "A command must begin with '/'." );

		var trimmed = text.TrimStart();
		var body = trimmed[1..];
		var tokens = Tokenize( body );
		if ( tokens.Count == 0 )
			return OperationResult<SlashCommandParse>.Failure(
				ErrorCode.InvalidArgument, "Type a command name after '/'. Try /help." );

		if ( !SlashCommandCatalog.TryFind( tokens[0], out var descriptor ) )
			return OperationResult<SlashCommandParse>.Failure(
				ErrorCode.UnknownDefinition, $"'/{tokens[0]}' is not a command. Try /help." );

		var supplied = tokens.Skip( 1 ).ToArray();
		var bound = new Dictionary<string, string>( StringComparer.Ordinal );
		var cursor = 0;
		for ( var index = 0; index < descriptor.Parameters.Count; index++ )
		{
			var parameter = descriptor.Parameters[index];
			if ( parameter.Kind == SlashParameterKind.RestOfLine )
			{
				// Re-read the remainder from the raw text rather than re-joining tokens, so quotes
				// and runs of spaces inside a record or note body survive exactly as typed.
				var remainder = RemainderAfter( body, tokens, cursor + 1 );
				if ( string.IsNullOrWhiteSpace( remainder ) )
				{
					if ( parameter.Required )
						return Missing( descriptor, parameter );
					continue;
				}

				bound[parameter.Key] = remainder;
				cursor = supplied.Length;
				continue;
			}

			if ( cursor >= supplied.Length )
			{
				if ( parameter.Required ) return Missing( descriptor, parameter );
				continue;
			}

			bound[parameter.Key] = supplied[cursor];
			cursor++;
		}

		return OperationResult<SlashCommandParse>.Success( new SlashCommandParse( descriptor, bound ) );
	}

	/// <summary>
	/// Binds parsed tokens to the typed arguments a handler reads. <paramref name="resolveCharacter"/>
	/// turns a typed name into a character id; it is a delegate because that knowledge lives in the
	/// caller's projection, keeping this rule free of any world state.
	/// </summary>
	public static OperationResult<Dictionary<string, SnapshotValue>> Bind(
		SlashCommandParse parse,
		Func<string, OperationResult<Guid>> resolveCharacter )
	{
		ArgumentNullException.ThrowIfNull( parse );
		ArgumentNullException.ThrowIfNull( resolveCharacter );
		var arguments = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal );
		foreach ( var parameter in parse.Descriptor.Parameters )
		{
			if ( !parse.Tokens.TryGetValue( parameter.Key, out var raw ) ) continue;
			switch ( parameter.Kind )
			{
				case SlashParameterKind.Text:
				case SlashParameterKind.RestOfLine:
					arguments[parameter.Key] = SnapshotValue.String( raw );
					break;
				case SlashParameterKind.Choice:
					arguments[parameter.Key] = SnapshotValue.Choice( raw );
					break;
				case SlashParameterKind.Integer:
					if ( !long.TryParse( raw, out var number ) )
						return Invalid( parameter, $"'{raw}' is not a whole number." );
					arguments[parameter.Key] = SnapshotValue.Integer( number );
					break;
				case SlashParameterKind.Boolean:
					if ( !TryParseBoolean( raw, out var flag ) )
						return Invalid( parameter, $"'{raw}' is not true or false." );
					arguments[parameter.Key] = SnapshotValue.Boolean( flag );
					break;
				case SlashParameterKind.Guid:
					if ( !System.Guid.TryParse( raw, out var id ) || id == System.Guid.Empty )
						return Invalid( parameter, $"'{raw}' is not an identifier." );
					arguments[parameter.Key] = SnapshotValue.String( id.ToString( "D" ) );
					break;
				case SlashParameterKind.CharacterName:
					var resolved = resolveCharacter( raw );
					if ( resolved.Failed )
						return OperationResult<Dictionary<string, SnapshotValue>>.Failure(
							resolved.Error!.Code, resolved.Error.Message );
					arguments[parameter.Key] = SnapshotValue.String( resolved.Value.ToString( "D" ) );
					break;
				default:
					return Invalid( parameter, "Unsupported parameter kind." );
			}
		}

		return OperationResult<Dictionary<string, SnapshotValue>>.Success( arguments );
	}

	/// <summary>Accepts the spellings a player actually types, not just C#'s.</summary>
	public static bool TryParseBoolean( string raw, out bool value )
	{
		switch ( raw?.Trim().ToLowerInvariant() )
		{
			case "true" or "yes" or "y" or "1" or "on":
				value = true;
				return true;
			case "false" or "no" or "n" or "0" or "off":
				value = false;
				return true;
			default:
				value = false;
				return false;
		}
	}

	// Walks the raw text past the first `skip` tokens and returns what is left, so a rest-of-line
	// parameter keeps the caller's exact spacing.
	private static string RemainderAfter( string body, IReadOnlyList<string> tokens, int skip )
	{
		var consumed = 0;
		var index = 0;
		while ( index < body.Length && consumed < skip )
		{
			while ( index < body.Length && char.IsWhiteSpace( body[index] ) ) index++;
			if ( index >= body.Length ) break;
			var quoted = false;
			while ( index < body.Length && (quoted || !char.IsWhiteSpace( body[index] )) )
			{
				if ( body[index] == '\\' && index + 1 < body.Length && body[index + 1] == '"' ) index++;
				else if ( body[index] == '"' ) quoted = !quoted;
				index++;
			}

			consumed++;
		}

		return index >= body.Length ? string.Empty : body[index..].TrimStart();
	}

	private static OperationResult<SlashCommandParse> Missing(
		SlashCommandDescriptor descriptor, SlashCommandParameter parameter ) =>
		OperationResult<SlashCommandParse>.Failure(
			ErrorCode.InvalidArgument,
			$"'{parameter.Key}' is required. Usage: {descriptor.Usage}" );

	private static OperationResult<Dictionary<string, SnapshotValue>> Invalid(
		SlashCommandParameter parameter, string detail ) =>
		OperationResult<Dictionary<string, SnapshotValue>>.Failure(
			ErrorCode.InvalidArgument, $"Argument '{parameter.Key}': {detail}" );
}
