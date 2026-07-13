#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Features;

[TestClass]
public sealed class LocalChatRecipientResolverTests
{
	private static readonly LiveInventorySnapshot EmptyInventory = new(0, Array.Empty<LiveInventoryItemView>());

	[TestMethod]
	public void OutOfCharacterIsGlobalAndDeterministicallyIncludesValidAuthor()
	{
		var actor = Actor();
		var remoteA = Connection(CharacterId.New(), 5000, 0, 0);
		var remoteB = Connection(CharacterId.New(), -5000, 0, 0);
		var author = new LiveChatConnection(actor.ConnectionId, actor.CharacterId, new ChatPosition(0, 0, 0));
		var directory = Directory(remoteA, author, remoteB);
		var resolver = new HL2RPLocalChatRecipientResolver(directory);

		var recipients = resolver.Resolve(Context(actor, HL2RPIds.Channels.OutOfCharacter),
			Rule(HL2RPIds.Channels.OutOfCharacter), EmptyInventory);

		CollectionAssert.AreEqual(
			new[] { remoteA.ConnectionId, author.ConnectionId, remoteB.ConnectionId }
				.OrderBy(connection => connection.Value).ToArray(),
			recipients.ToArray());
		CollectionAssert.Contains(recipients.ToArray(), actor.ConnectionId);
	}

	[TestMethod]
	public void LocalChannelsUseInclusiveRuleRangeBoundary()
	{
		var actor = Actor();
		var author = new LiveChatConnection(actor.ConnectionId, actor.CharacterId, new ChatPosition(0, 0, 0));
		var boundary = Connection(CharacterId.New(), HL2RPChatChannelRules.LocalRange, 0, 0);
		var outside = Connection(CharacterId.New(), HL2RPChatChannelRules.LocalRange + 0.25f, 0, 0);
		var resolver = new HL2RPLocalChatRecipientResolver(Directory(outside, boundary, author));

		foreach (var channel in new[]
		{
			HL2RPIds.Channels.InCharacter,
			HL2RPIds.Channels.LocalOutOfCharacter,
			HL2RPIds.Channels.Emote
		})
		{
			var recipients = resolver.Resolve(Context(actor, channel),
				Rule(channel, HL2RPChatChannelRules.LocalRange), EmptyInventory);
			CollectionAssert.AreEquivalent(
				new[] { author.ConnectionId, boundary.ConnectionId }, recipients.ToArray(), channel);
		}
	}

	[TestMethod]
	public void WhisperAndYellUseTheirStricterAndWiderConfiguredRanges()
	{
		var actor = Actor();
		var author = new LiveChatConnection(actor.ConnectionId, actor.CharacterId, new ChatPosition(0, 0, 0));
		var near = Connection(CharacterId.New(), 79, 0, 0);
		var normal = Connection(CharacterId.New(), 200, 0, 0);
		var far = Connection(CharacterId.New(), 500, 0, 0);
		var resolver = new HL2RPLocalChatRecipientResolver(Directory(far, normal, near, author));

		var whisper = resolver.Resolve(Context(actor, HL2RPIds.Channels.Whisper),
			Rule(HL2RPIds.Channels.Whisper, HL2RPChatChannelRules.WhisperRange), EmptyInventory);
		var local = resolver.Resolve(Context(actor, HL2RPIds.Channels.InCharacter),
			Rule(HL2RPIds.Channels.InCharacter, HL2RPChatChannelRules.LocalRange), EmptyInventory);
		var yell = resolver.Resolve(Context(actor, HL2RPIds.Channels.Yell),
			Rule(HL2RPIds.Channels.Yell, HL2RPChatChannelRules.YellRange), EmptyInventory);

		CollectionAssert.AreEquivalent(new[] { author.ConnectionId, near.ConnectionId }, whisper.ToArray());
		CollectionAssert.AreEquivalent(
			new[] { author.ConnectionId, near.ConnectionId, normal.ConnectionId }, local.ToArray());
		CollectionAssert.AreEquivalent(
			new[] { author.ConnectionId, near.ConnectionId, normal.ConnectionId, far.ConnectionId }, yell.ToArray());
	}

	[TestMethod]
	public void DirectoryPublicationRevokesDisconnectedRecipientsAndInvalidAuthor()
	{
		var actor = Actor();
		var author = new LiveChatConnection(actor.ConnectionId, actor.CharacterId, new ChatPosition(0, 0, 0));
		var remote = Connection(CharacterId.New(), 10, 0, 0);
		var directory = new CanonicalChatConnectionPositionDirectory();
		var resolver = new HL2RPLocalChatRecipientResolver(directory);
		var context = Context(actor, HL2RPIds.Channels.OutOfCharacter);
		var rule = Rule(HL2RPIds.Channels.OutOfCharacter);

		directory.Publish(1, new[] { remote, author });
		CollectionAssert.Contains(resolver.Resolve(context, rule, EmptyInventory).ToArray(), remote.ConnectionId);

		directory.Publish(2, new[] { author });
		CollectionAssert.AreEqual(
			new[] { author.ConnectionId }, resolver.Resolve(context, rule, EmptyInventory).ToArray());

		directory.Publish(3, new[] { remote });
		Assert.IsEmpty(resolver.Resolve(context, rule, EmptyInventory));
	}

	[TestMethod]
	public void RadioResolverDelegatesNonRadioChannelsToLocalFallback()
	{
		var actor = Actor();
		var author = new LiveChatConnection(actor.ConnectionId, actor.CharacterId, new ChatPosition(0, 0, 0));
		var remote = Connection(CharacterId.New(), 20, 0, 0);
		var fallback = new HL2RPLocalChatRecipientResolver(Directory(remote, author));
		var resolver = new HL2RPRadioRecipientResolver(new EmptyRadioDirectory(), fallback);

		var recipients = resolver.Resolve(Context(actor, HL2RPIds.Channels.InCharacter),
			Rule(HL2RPIds.Channels.InCharacter, 25), EmptyInventory);

		CollectionAssert.AreEquivalent(
			new[] { actor.ConnectionId, remote.ConnectionId }, recipients.ToArray());
	}

	[TestMethod]
	public void DefaultRulesCoverEveryShowcaseChannelWithExactRanges()
	{
		var rules = HL2RPChatChannelRules.Create().ToDictionary(rule => rule.Id, StringComparer.Ordinal);

		Assert.HasCount(9, rules);
		Assert.IsNull(rules[HL2RPIds.Channels.OutOfCharacter].Range);
		Assert.AreEqual(HL2RPChatChannelRules.LocalRange, rules[HL2RPIds.Channels.InCharacter].Range);
		Assert.AreEqual(HL2RPChatChannelRules.LocalRange, rules[HL2RPIds.Channels.LocalOutOfCharacter].Range);
		Assert.AreEqual(HL2RPChatChannelRules.LocalRange, rules[HL2RPIds.Channels.Emote].Range);
		Assert.AreEqual(HL2RPChatChannelRules.WhisperRange, rules[HL2RPIds.Channels.Whisper].Range);
		Assert.AreEqual(HL2RPChatChannelRules.YellRange, rules[HL2RPIds.Channels.Yell].Range);
		Assert.IsNull(rules[HL2RPIds.Channels.Radio].Range);
		Assert.IsNull(rules[HL2RPIds.Channels.Request].Range);
		Assert.IsNull(rules[HL2RPIds.Channels.Dispatch].Range);
	}

	private static CanonicalChatConnectionPositionDirectory Directory(
		params LiveChatConnection[] connections)
	{
		var directory = new CanonicalChatConnectionPositionDirectory();
		directory.Publish(1, connections);
		return directory;
	}

	private static LiveChatConnection Connection(CharacterId characterId, float x, float y, float z) =>
		new(ConnectionId.New(), characterId, new ChatPosition(x, y, z));

	private static InventoryActor Actor() =>
		new(ConnectionId.New(), new AccountId(42), CharacterId.New());

	private static ChatSendContext Context(InventoryActor actor, string channel) =>
		new(actor, Character(actor), channel, "Test message");

	private static CharacterRecord Character(InventoryActor actor) => new()
	{
		Id = actor.CharacterId,
		AccountId = actor.AccountId,
		Slot = 0,
		Name = "Citizen 40291",
		Description = "A chat recipient resolver test character.",
		Model = new DefinitionId(HL2RPIds.Models.Citizen01),
		Faction = new FactionId(HL2RPIds.Factions.Citizen),
		Balance = 0,
		CreatedAt = DateTimeOffset.UnixEpoch,
		LastPlayedAt = DateTimeOffset.UnixEpoch,
		SchemaState = HL2RPPersistence.Payload(HL2RPPersistence.CharacterState, new HL2RPCharacterState
		{
			CitizenId = "C17-42-00",
			Age = 28,
			Pronouns = "they/them",
			Origin = "city_17",
			Whitelists = HL2RPWhitelist.None,
			CivicRecord = new CivicRecordState
			{
				Points = 0,
				Priority = CivicPriorityStatus.None
			}
		})
	};

	private static ChatChannelRule Rule(string id, float? range = null) => new()
	{
		Id = id,
		Range = range
	};

	private sealed class EmptyRadioDirectory : IChatAuthorityDirectory
	{
		public ChatAuthoritySnapshot Capture() => ChatAuthoritySnapshot.Empty;
	}
}
