using System;
using HL2RP.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HL2RP.Tests;

[TestClass]
public sealed class StoreTests
{
	[TestMethod]
	public void SavedDocumentsRoundTripAndLeaveNoPendingCopy()
	{
		var files = new MemoryFiles();
		var store = new DocumentStore( files );
		store.Save( "characters/a.json", new CharacterData { Name = "Alice", Tokens = 7 } );

		Assert.AreEqual( 7, store.Load<CharacterData>( "characters/a.json" )!.Tokens );
		Assert.IsFalse( files.Exists( "characters/a.json" + DocumentStore.PendingSuffix ) );
	}

	[TestMethod]
	public void ACrashAtAnyPointOfASaveLeavesTheOldOrTheNewDocumentNeverNeither()
	{
		// A save is two writes. Tear each one in turn and reboot.
		for ( var crashAt = 1; crashAt <= 2; crashAt++ )
		{
			var files = new MemoryFiles();
			new DocumentStore( files ).Save( "characters/a.json", new CharacterData { Name = "Alice", Tokens = 1 } );
			files.CrashOnWrite = files.Writes + crashAt;
			Assert.ThrowsExactly<InvalidOperationException>( () =>
				new DocumentStore( files ).Save( "characters/a.json", new CharacterData { Name = "Alice", Tokens = 2 } ) );

			var recovered = new DocumentStore( files.Reboot() ).Load<CharacterData>( "characters/a.json" );

			Assert.IsNotNull( recovered, $"crash at write {crashAt} lost the document" );
			// Tearing the sibling keeps the old state; tearing the real file recovers the new one.
			Assert.AreEqual( crashAt == 1 ? 1 : 2, recovered.Tokens, $"crash at write {crashAt}" );
		}
	}

	[TestMethod]
	public void LoadAllFindsADocumentWhoseOnlyCompleteCopyIsPending()
	{
		var files = new MemoryFiles();
		var store = new DocumentStore( files );
		store.Save( "characters/a.json", new CharacterData { Name = "Alice" } );
		files.Files["characters/b.json" + DocumentStore.PendingSuffix] = files.Files["characters/a.json"].Replace( "Alice", "Bob" );

		var all = store.LoadAll<CharacterData>( "characters" );

		Assert.HasCount( 2, all );
		Assert.IsTrue( files.Exists( "characters/b.json" ), "the recovered document is promoted to its real path" );
	}

	[TestMethod]
	public void AnUnreadableDocumentIsSkippedWithAWarningInsteadOfStoppingTheServer()
	{
		var files = new MemoryFiles();
		files.Files["characters/bad.json"] = "{ not json";
		var warnings = 0;

		var all = new DocumentStore( files, _ => warnings++ ).LoadAll<CharacterData>( "characters" );

		Assert.HasCount( 0, all );
		Assert.AreEqual( 1, warnings );
	}
}
