using System;
using HL2RP.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HL2RP.Tests;

[TestClass]
public sealed class RosterTests
{
	private const string Description = "A tired resident of the city.";
	private static readonly DateTimeOffset Now = new( 2026, 9, 18, 0, 0, 0, TimeSpan.Zero );

	[TestMethod]
	public void ACreatedCharacterSurvivesARestart()
	{
		var files = new MemoryFiles();
		var created = new CharacterRoster( new DocumentStore( files ) ).Create( 1, "  John Doe ", Description, "citizen", Now );

		Assert.IsTrue( created.Ok, created.Message );
		var reloaded = new CharacterRoster( new DocumentStore( files.Reboot() ) );
		Assert.AreEqual( "John Doe", reloaded.Find( created.Value.Id )!.Name );
		Assert.AreEqual( CharacterRoster.StartingTokens, reloaded.Find( created.Value.Id )!.Tokens );
	}

	[TestMethod]
	public void LookAlikeNamesAreRefusedIncludingAfterARestart()
	{
		var files = new MemoryFiles();
		var roster = new CharacterRoster( new DocumentStore( files ) );
		Assert.IsTrue( roster.Create( 1, "Sara Connor", Description, "citizen", Now ).Ok );

		Assert.AreEqual( ErrorCode.Conflict, roster.Create( 2, "sara  connor", Description, "citizen", Now ).Code );
		Assert.AreEqual( ErrorCode.Conflict, roster.Create( 2, "5ara Connor", Description, "citizen", Now ).Code );
		// x01BC is a Latin letter the Unicode table folds onto the digit five.
		Assert.AreEqual( ErrorCode.Conflict, roster.Create( 2, "\x01BCara Connor", Description, "citizen", Now ).Code );
		var restarted = new CharacterRoster( new DocumentStore( files.Reboot() ) );
		Assert.AreEqual( ErrorCode.Conflict, restarted.Create( 2, "Sara Connor", Description, "citizen", Now ).Code );
	}

	[TestMethod]
	public void MixedScriptAndInvisibleCharactersAreRefused()
	{
		var roster = new CharacterRoster( new DocumentStore( new MemoryFiles() ) );

		// x0430 is the Cyrillic letter that looks like a Latin "a"; x200B is a zero-width space.
		Assert.AreEqual( ErrorCode.Invalid, roster.Create( 1, "S\x0430ra Connor", Description, "citizen", Now ).Code );
		Assert.AreEqual( ErrorCode.Invalid, roster.Create( 1, "Sara\x200BConnor", Description, "citizen", Now ).Code );
		Assert.AreEqual( ErrorCode.Invalid, roster.Create( 1, "Al", Description, "citizen", Now ).Code );
		Assert.AreEqual( ErrorCode.Invalid, roster.Create( 1, "Sara Connor", "too short", "citizen", Now ).Code );
	}

	[TestMethod]
	public void SlotsAreLimitedPerAccountAndDeletionFreesTheName()
	{
		var roster = new CharacterRoster( new DocumentStore( new MemoryFiles() ) );
		Guid first = default;
		for ( var index = 0; index < CharacterRoster.MaximumSlots; index++ )
		{
			var created = roster.Create( 1, $"Resident Number {(char)('A' + index)}", Description, "citizen", Now );
			Assert.IsTrue( created.Ok, created.Message );
			if ( index == 0 ) first = created.Value.Id;
		}

		Assert.AreEqual( ErrorCode.Conflict, roster.Create( 1, "One Too Many", Description, "citizen", Now ).Code );
		Assert.AreEqual( ErrorCode.NotFound, roster.Delete( 2, first ).Code, "another account cannot delete it" );
		Assert.IsTrue( roster.Delete( 1, first ).Ok );
		Assert.IsTrue( roster.Create( 2, "Resident Number A", Description, "citizen", Now ).Ok );
	}
}
