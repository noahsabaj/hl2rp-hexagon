using System;
using HL2RP.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HL2RP.Tests;

[TestClass]
public sealed class RulesTests
{
	[TestMethod]
	public void InventoryPlacesMovesAndRefusesOverlap()
	{
		var inventory = new InventoryData { Width = 3, Height = 2 };
		var big = InventoryGrid.Add( inventory, "items/pistol.item", 2, 1 );
		var small = InventoryGrid.Add( inventory, "items/ration.item", 1, 1 );

		Assert.IsTrue( big.Ok && small.Ok );
		Assert.AreEqual( (0, 0), (big.Value.X, big.Value.Y) );
		Assert.AreEqual( (2, 0), (small.Value.X, small.Value.Y) );
		Assert.AreEqual( ErrorCode.Conflict, InventoryGrid.Move( inventory, small.Value.Id, 1, 0 ).Code );
		Assert.AreEqual( ErrorCode.Conflict, InventoryGrid.Move( inventory, big.Value.Id, 2, 1 ).Code, "would hang off the edge" );
		Assert.IsTrue( InventoryGrid.Move( inventory, big.Value.Id, 0, 1 ).Ok );
		Assert.IsTrue( InventoryGrid.Move( inventory, big.Value.Id, 1, 1 ).Ok, "an item may overlap its own old cells" );
		Assert.AreEqual( ErrorCode.NotFound, InventoryGrid.Move( inventory, Guid.NewGuid(), 0, 0 ).Code );
	}

	[TestMethod]
	public void AFullInventoryRefusesAndRemovalMakesRoom()
	{
		var inventory = new InventoryData { Width = 1, Height = 1 };
		var only = InventoryGrid.Add( inventory, "items/ration.item", 1, 1 );

		Assert.AreEqual( ErrorCode.Conflict, InventoryGrid.Add( inventory, "items/ration.item", 1, 1 ).Code );
		Assert.IsTrue( InventoryGrid.Remove( inventory, only.Value.Id ).Ok );
		Assert.IsTrue( InventoryGrid.Add( inventory, "items/ration.item", 1, 1 ).Ok );
	}

	[TestMethod]
	public void ChatPrefixesSelectTheChannelWithoutSwallowingOtherWords()
	{
		Assert.AreEqual( new ChatMessage( ChatChannel.Say, "hello" ), ChatRules.Parse( "  hello " ).Value );
		Assert.AreEqual( new ChatMessage( ChatChannel.Ooc, "brb" ), ChatRules.Parse( "//brb" ).Value );
		Assert.AreEqual( new ChatMessage( ChatChannel.Ooc, "brb" ), ChatRules.Parse( "/OOC brb" ).Value );
		Assert.AreEqual( new ChatMessage( ChatChannel.Me, "waves" ), ChatRules.Parse( "/me waves" ).Value );
		Assert.AreEqual( new ChatMessage( ChatChannel.Whisper, "psst" ), ChatRules.Parse( "/w psst" ).Value );
		Assert.AreEqual( ChatChannel.Say, ChatRules.Parse( "/wave at them" ).Value.Channel );
	}

	[TestMethod]
	public void ChatRefusesEmptyOversizedAndControlText()
	{
		Assert.AreEqual( ErrorCode.Invalid, ChatRules.Parse( "   " ).Code );
		Assert.AreEqual( ErrorCode.Invalid, ChatRules.Parse( "/me" ).Code );
		Assert.AreEqual( ErrorCode.Invalid, ChatRules.Parse( new string( 'a', ChatRules.MaximumScalars + 1 ) ).Code );
		Assert.IsTrue( ChatRules.Parse( new string( 'a', ChatRules.MaximumScalars ) ).Ok );
		// A bell, a right-to-left override, and a lone surrogate.
		Assert.AreEqual( ErrorCode.Invalid, ChatRules.Parse( "hi" + (char)7 + "there" ).Code );
		Assert.AreEqual( ErrorCode.Invalid, ChatRules.Parse( "hi" + (char)0x202E + "there" ).Code );
		Assert.AreEqual( ErrorCode.Invalid, ChatRules.Parse( "bad " + (char)0xD800 + " surrogate" ).Code );
	}

	[TestMethod]
	public void OnlyOutOfCharacterChatIsGlobal()
	{
		foreach ( var channel in Enum.GetValues<ChatChannel>() )
		{
			Assert.AreEqual( channel == ChatChannel.Ooc, ChatRules.Range( channel ) is null, channel.ToString() );
		}
		Assert.IsLessThan( ChatRules.Range( ChatChannel.Say )!.Value, ChatRules.Range( ChatChannel.Whisper )!.Value );
		Assert.IsLessThan( ChatRules.Range( ChatChannel.Yell )!.Value, ChatRules.Range( ChatChannel.Say )!.Value );
	}

	[TestMethod]
	public void RateLimiterSpendsItsBurstThenRefillsWithTime()
	{
		var limiter = new RateLimiter( capacity: 3, refillPerSecond: 1 );

		Assert.IsTrue( limiter.TryTake( 0 ) && limiter.TryTake( 0 ) && limiter.TryTake( 0 ) );
		Assert.IsFalse( limiter.TryTake( 0 ) );
		Assert.IsFalse( limiter.TryTake( 0.5 ) );
		Assert.IsTrue( limiter.TryTake( 1.5 ) );
		Assert.IsFalse( limiter.TryTake( 1.5 ), "refill is proportional to elapsed time" );
		Assert.IsTrue( limiter.TryTake( 1000 ) && limiter.TryTake( 1000 ) && limiter.TryTake( 1000 ) );
		Assert.IsFalse( limiter.TryTake( 1000 ), "a long idle never banks more than the burst" );
	}
}
