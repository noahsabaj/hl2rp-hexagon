#nullable enable

using System;
using System.Linq;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Schema;

[TestClass]
public sealed class SlashCommandTests
{
	private static readonly Guid Target = Guid.Parse( "11111111-2222-3333-4444-555555555555" );

	private static OperationResult<Guid> Resolves( string name ) =>
		OperationResult<Guid>.Success( Target );

	private static OperationResult<Guid> NeverResolves( string name ) =>
		OperationResult<Guid>.Failure( ErrorCode.NotFound, $"No character named '{name}'." );

	/// <summary>
	/// The completeness guard. HL2RP shipped 17 registered commands and no text surface at all, so
	/// a command's only route in was a hand-built panel; this asserts the catalogue and the compiled
	/// registry describe the SAME set, in both directions. A new command therefore cannot be
	/// registered without deciding how a player types it, which is the condition that let a command
	/// become unreachable in the first place.
	/// </summary>
	[TestMethod]
	public void EveryRegisteredCommandIsTypeableAndEveryTypeableCommandIsRegistered()
	{
		var registered = HL2RPSchemaTests.Compile().Commands.All.Select( command => command.Id ).ToHashSet( StringComparer.Ordinal );
		var typeable = SlashCommandCatalog.All.Select( descriptor => descriptor.CommandId ).ToHashSet( StringComparer.Ordinal );

		var unreachable = registered.Except( typeable ).OrderBy( id => id, StringComparer.Ordinal ).ToArray();
		var dangling = typeable.Except( registered ).OrderBy( id => id, StringComparer.Ordinal ).ToArray();

		Assert.IsEmpty( unreachable,
			$"Registered but not typeable: {string.Join( ", ", unreachable )}" );
		Assert.IsEmpty( dangling,
			$"Typeable but not registered: {string.Join( ", ", dangling )}" );
	}

	private static readonly SlashCommandTarget[] Bystanders =
	{
		new( Guid.Parse( "aaaaaaaa-0000-0000-0000-000000000001" ), "Noah Sabaj" ),
		new( Guid.Parse( "aaaaaaaa-0000-0000-0000-000000000002" ), "Barney Calhoun" ),
		new( Guid.Parse( "aaaaaaaa-0000-0000-0000-000000000003" ), "Bar" )
	};

	[TestMethod]
	public void AnExactNameBeatsALongerNameContainingIt()
	{
		var resolved = SlashCommandTargets.Resolve( "Bar", Bystanders );

		Assert.IsTrue( resolved.Succeeded, resolved.Error?.Message );
		Assert.AreEqual( Bystanders[2].CharacterId, resolved.Value );
	}

	[TestMethod]
	public void APartialNameResolvesWhenItIsUnambiguous()
	{
		var resolved = SlashCommandTargets.Resolve( "calhoun", Bystanders );

		Assert.IsTrue( resolved.Succeeded, resolved.Error?.Message );
		Assert.AreEqual( Bystanders[1].CharacterId, resolved.Value );
	}

	/// <summary>Guessing between two matches would point a lethal command at the wrong person.</summary>
	[TestMethod]
	public void AnAmbiguousNameFailsInsteadOfPickingOne()
	{
		var resolved = SlashCommandTargets.Resolve( "a", Bystanders );

		Assert.IsTrue( resolved.Failed );
		Assert.AreEqual( ErrorCode.Conflict, resolved.Error!.Code );
		StringAssert.Contains( resolved.Error.Message, "Noah Sabaj" );
	}

	[TestMethod]
	public void AnUnknownNameIsNotFoundRatherThanAmbiguous()
	{
		var resolved = SlashCommandTargets.Resolve( "Gordon", Bystanders );

		Assert.IsTrue( resolved.Failed );
		Assert.AreEqual( ErrorCode.NotFound, resolved.Error!.Code );
	}

	/// <summary>
	/// Every channel must be fully described by its own registration. The range half is the reason
	/// this exists: ranges used to live in a parallel table keyed by the same ids, and a channel
	/// present in one and missing from the other failed SILENTLY — it resolved to no recipients,
	/// which is indistinguishable from "nobody was in range".
	/// </summary>
	[TestMethod]
	public void EveryChannelCarriesItsOwnDisplayPrefixesAndRange()
	{
		var channels = HL2RPSchemaTests.Compile().ChatChannels.All.ToArray();
		var ranged = new[]
		{
			HL2RPIds.Channels.InCharacter, HL2RPIds.Channels.LocalOutOfCharacter,
			HL2RPIds.Channels.Whisper, HL2RPIds.Channels.Yell, HL2RPIds.Channels.Emote
		};

		foreach ( var channel in channels )
		{
			Assert.IsFalse( string.IsNullOrWhiteSpace( channel.DisplayName ),
				$"Channel '{channel.Id}' has no display name." );
			Assert.IsTrue( channel.Prefixes is { Count: > 0 },
				$"Channel '{channel.Id}' has no prefix, so nothing a player types can reach it." );
			var shouldBeRanged = ranged.Contains( channel.Id, StringComparer.Ordinal );
			Assert.AreEqual( shouldBeRanged, channel.Range is not null,
				$"Channel '{channel.Id}' disagrees with its positional nature: Range={channel.Range}." );
			if ( channel.Range is float range )
				Assert.IsGreaterThan( 0f, range, $"Channel '{channel.Id}' has a non-positive range." );
		}

		var allPrefixes = channels.SelectMany( channel => channel.Prefixes! ).ToArray();
		CollectionAssert.AllItemsAreUnique( allPrefixes,
			"Two channels share a prefix, so one of them is unreachable." );
	}

	/// <summary>
	/// Channels resolve BEFORE the command catalogue, so a channel prefix equal to a command name
	/// silently shadows that command. This is the failure mode where adding a channel kills /kill.
	/// </summary>
	[TestMethod]
	public void NoChannelPrefixShadowsACommandName()
	{
		var prefixes = HL2RPSchemaTests.Compile().ChatChannels.All
			.SelectMany( channel => channel.Prefixes ?? Array.Empty<string>() )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );
		var collisions = SlashCommandCatalog.All
			.Where( descriptor => prefixes.Contains( descriptor.Name ) )
			.Select( descriptor => descriptor.Name )
			.ToArray();

		Assert.IsEmpty( collisions,
			$"These command names are shadowed by a channel prefix: {string.Join( ", ", collisions )}" );
	}

	[TestMethod]
	public void CommandNamesAreUniqueAndDoNotCollideWithCommandIds()
	{
		var names = SlashCommandCatalog.All.Select( descriptor => descriptor.Name ).ToArray();

		CollectionAssert.AllItemsAreUnique( names );
		Assert.IsTrue( names.All( name => name.All( character => char.IsLetterOrDigit( character ) ) ),
			"A command name must be a single bare word so it can be typed after '/'." );
	}

	/// <summary>A rest-of-line parameter consumes the remainder, so nothing may follow it.</summary>
	[TestMethod]
	public void RestOfLineParametersAreAlwaysLast()
	{
		foreach ( var descriptor in SlashCommandCatalog.All )
		{
			var restIndex = -1;
			for ( var index = 0; index < descriptor.Parameters.Count; index++ )
				if ( descriptor.Parameters[index].Kind == SlashParameterKind.RestOfLine ) restIndex = index;
			if ( restIndex < 0 ) continue;
			Assert.AreEqual( descriptor.Parameters.Count - 1, restIndex,
				$"/{descriptor.Name} has a rest-of-line parameter that is not last." );
		}
	}

	[TestMethod]
	public void SpeechIsNotMistakenForACommand()
	{
		Assert.IsFalse( SlashCommandParser.IsCommand( "hello there" ) );
		Assert.IsFalse( SlashCommandParser.IsCommand( "  " ) );
		Assert.IsFalse( SlashCommandParser.IsCommand( null ) );
		Assert.IsTrue( SlashCommandParser.IsCommand( "/kill Noah" ) );
		Assert.IsTrue( SlashCommandParser.IsCommand( "   /kill Noah" ) );
	}

	[TestMethod]
	public void QuotedNamesSurviveAsOneToken()
	{
		var tokens = SlashCommandParser.Tokenize( "kill \"Noah Sabaj\" extra" );

		CollectionAssert.AreEqual( new[] { "kill", "Noah Sabaj", "extra" }, tokens.ToArray() );
	}

	[TestMethod]
	public void AnUnterminatedQuoteStillYieldsTheCommand()
	{
		var tokens = SlashCommandParser.Tokenize( "kill \"Noah Sabaj" );

		CollectionAssert.AreEqual( new[] { "kill", "Noah Sabaj" }, tokens.ToArray() );
	}

	[TestMethod]
	public void KillResolvesTheTypedNameToACharacterArgument()
	{
		var parsed = SlashCommandParser.Parse( "/kill \"Noah Sabaj\"" );
		Assert.IsTrue( parsed.Succeeded, parsed.Error?.Message );
		Assert.AreEqual( HL2RPIds.Commands.AdministrationKill, parsed.Value.Descriptor.CommandId );
		Assert.AreEqual( "Noah Sabaj", parsed.Value.Tokens["character"] );

		var bound = SlashCommandParser.Bind( parsed.Value, Resolves );

		Assert.IsTrue( bound.Succeeded, bound.Error?.Message );
		Assert.AreEqual( Target.ToString( "D" ), bound.Value["character"].StringValue );
	}

	[TestMethod]
	public void AnUnresolvableNameFailsRatherThanTargetingSomeoneElse()
	{
		var parsed = SlashCommandParser.Parse( "/kill Nobody" );
		Assert.IsTrue( parsed.Succeeded );

		var bound = SlashCommandParser.Bind( parsed.Value, NeverResolves );

		Assert.IsTrue( bound.Failed );
		Assert.AreEqual( ErrorCode.NotFound, bound.Error!.Code );
	}

	[TestMethod]
	public void AnUnknownCommandSaysSoInsteadOfBecomingSpeech()
	{
		var parsed = SlashCommandParser.Parse( "/notacommand whatever" );

		Assert.IsTrue( parsed.Failed );
		Assert.AreEqual( ErrorCode.UnknownDefinition, parsed.Error!.Code );
	}

	[TestMethod]
	public void AMissingRequiredArgumentReportsTheUsageLine()
	{
		var parsed = SlashCommandParser.Parse( "/kill" );

		Assert.IsTrue( parsed.Failed );
		StringAssert.Contains( parsed.Error!.Message, "/kill <character>" );
	}

	[TestMethod]
	public void CommandsTakingNoArgumentsParseBare()
	{
		var parsed = SlashCommandParser.Parse( "/respawn" );

		Assert.IsTrue( parsed.Succeeded, parsed.Error?.Message );
		Assert.AreEqual( HL2RPIds.Commands.CombatRespawn, parsed.Value.Descriptor.CommandId );
	}

	/// <summary>
	/// The record text is prose a player typed. Re-joining tokens would collapse runs of spaces and
	/// drop quotes, silently rewriting what they wrote into the civic record.
	/// </summary>
	[TestMethod]
	public void RestOfLineKeepsTheTextExactlyAsTyped()
	{
		var parsed = SlashCommandParser.Parse( "/priority \"Noah Sabaj\" high Loitering  near   the plaza" );

		Assert.IsTrue( parsed.Succeeded, parsed.Error?.Message );
		Assert.AreEqual( "Loitering  near   the plaza", parsed.Value.Tokens["record"] );
	}

	[TestMethod]
	public void BooleanArgumentsAcceptWhatPlayersActuallyType()
	{
		foreach ( var affirmative in new[] { "true", "yes", "y", "1", "on", "TRUE" } )
		{
			Assert.IsTrue( SlashCommandParser.TryParseBoolean( affirmative, out var value ) );
			Assert.IsTrue( value, affirmative );
		}

		foreach ( var negative in new[] { "false", "no", "n", "0", "off" } )
		{
			Assert.IsTrue( SlashCommandParser.TryParseBoolean( negative, out var value ) );
			Assert.IsFalse( value, negative );
		}

		Assert.IsFalse( SlashCommandParser.TryParseBoolean( "maybe", out _ ) );
	}

	[TestMethod]
	public void ANonNumericQuantityIsRejectedRatherThanCoercedToZero()
	{
		var parsed = SlashCommandParser.Parse(
			$"/buy {Target:D} ration lots" );
		Assert.IsTrue( parsed.Succeeded, parsed.Error?.Message );

		var bound = SlashCommandParser.Bind( parsed.Value, Resolves );

		Assert.IsTrue( bound.Failed );
		Assert.AreEqual( ErrorCode.InvalidArgument, bound.Error!.Code );
	}

	[TestMethod]
	public void AMalformedIdentifierIsRejected()
	{
		var parsed = SlashCommandParser.Parse( "/note not-a-guid the body text" );
		Assert.IsTrue( parsed.Succeeded, parsed.Error?.Message );

		var bound = SlashCommandParser.Bind( parsed.Value, Resolves );

		Assert.IsTrue( bound.Failed );
		Assert.AreEqual( ErrorCode.InvalidArgument, bound.Error!.Code );
	}

	[TestMethod]
	public void RestraintBindsBothFlagsIndependently()
	{
		var parsed = SlashCommandParser.Parse( "/restrain \"Noah Sabaj\" yes no" );
		Assert.IsTrue( parsed.Succeeded, parsed.Error?.Message );

		var bound = SlashCommandParser.Bind( parsed.Value, Resolves );

		Assert.IsTrue( bound.Succeeded, bound.Error?.Message );
		Assert.IsTrue( bound.Value["restrain"].BooleanValue );
		Assert.IsFalse( bound.Value["search"].BooleanValue );
	}

	[TestMethod]
	public void ChoiceArgumentsCarryChoiceKindSoHandlersValidateThem()
	{
		var parsed = SlashCommandParser.Parse( "/door claim" );
		Assert.IsTrue( parsed.Succeeded, parsed.Error?.Message );

		var bound = SlashCommandParser.Bind( parsed.Value, Resolves );

		Assert.IsTrue( bound.Succeeded, bound.Error?.Message );
		Assert.AreEqual( SnapshotValueKind.Choice, bound.Value["intent"].Kind );
		Assert.AreEqual( "claim", bound.Value["intent"].StringValue );
	}

	[TestMethod]
	public void AnOptionalTrailingArgumentMayBeOmitted()
	{
		var parsed = SlashCommandParser.Parse( "/objectives new false \"Secure sector seven\"" );

		Assert.IsTrue( parsed.Succeeded, parsed.Error?.Message );
		Assert.IsFalse( parsed.Value.Tokens.ContainsKey( "detail" ) );
		Assert.AreEqual( "Secure sector seven", parsed.Value.Tokens["title"] );
	}
}
