#nullable enable

namespace HL2RP.V2.Features;

public readonly record struct ChatPosition
{
	public ChatPosition(float x, float y, float z)
	{
		if (!float.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
		if (!float.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
		if (!float.IsFinite(z)) throw new ArgumentOutOfRangeException(nameof(z));
		X = x;
		Y = y;
		Z = z;
	}

	public float X { get; }
	public float Y { get; }
	public float Z { get; }

	public double DistanceSquared(ChatPosition other)
	{
		var x = (double)X - other.X;
		var y = (double)Y - other.Y;
		var z = (double)Z - other.Z;
		return x * x + y * y + z * z;
	}
}

public sealed record LiveChatConnection(
	ConnectionId ConnectionId,
	CharacterId CharacterId,
	ChatPosition Position);

public sealed record ChatConnectionSnapshot
{
	public ChatConnectionSnapshot(long version, IEnumerable<LiveChatConnection>? connections = null)
	{
		if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
		var input = (connections ?? Array.Empty<LiveChatConnection>()).ToArray();
		if (input.Any(connection => connection is null))
			throw new ArgumentException("Chat connections cannot contain null values.", nameof(connections));
		var copy = input
			.OrderBy(connection => connection.ConnectionId.Value)
			.ToArray();
		if (copy.Select(connection => connection.ConnectionId).Distinct().Count() != copy.Length)
			throw new ArgumentException("Chat connections must have unique connection IDs.", nameof(connections));
		if (copy.Select(connection => connection.CharacterId).Distinct().Count() != copy.Length)
			throw new ArgumentException("Chat connections must have unique active character IDs.", nameof(connections));

		Version = version;
		Connections = Array.AsReadOnly(copy);
	}

	public long Version { get; }
	public IReadOnlyList<LiveChatConnection> Connections { get; }
	public static ChatConnectionSnapshot Empty { get; } = new(0);
}

/// <summary>
/// Canonical live directory seam. Runtime adapters publish only connected
/// active-character rows; recipient calculation consumes one immutable capture.
/// </summary>
public interface IChatConnectionPositionDirectory
{
	ChatConnectionSnapshot Capture();
}

public sealed class CanonicalChatConnectionPositionDirectory : IChatConnectionPositionDirectory
{
	private readonly object _sync = new();
	private ChatConnectionSnapshot _current = ChatConnectionSnapshot.Empty;

	public ChatConnectionSnapshot Capture()
	{
		lock (_sync) return _current;
	}

	public void Publish(long version, IEnumerable<LiveChatConnection> connections)
	{
		var next = new ChatConnectionSnapshot(version,
			connections ?? throw new ArgumentNullException(nameof(connections)));
		lock (_sync)
		{
			if (version < _current.Version)
				throw new InvalidOperationException("Live chat connection directory cannot move backwards.");
			_current = next;
		}
	}
}

/// <summary>
/// Pure OOC/local recipient fallback. It has no repository, inventory, or
/// persistence dependency; the specialized radio resolver delegates here for
/// non-radio channels.
/// </summary>
public sealed class HL2RPLocalChatRecipientResolver : IChatRecipientResolver
{
	private readonly IChatConnectionPositionDirectory _directory;

	public HL2RPLocalChatRecipientResolver(IChatConnectionPositionDirectory directory) =>
		_directory = directory ?? throw new ArgumentNullException(nameof(directory));

	public IReadOnlyList<ConnectionId> Resolve(
		ChatSendContext context,
		ChatChannelRule rule,
		LiveInventorySnapshot inventory)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(rule);
		ArgumentNullException.ThrowIfNull(inventory);
		if (!string.Equals(context.ChannelId, rule.Id, StringComparison.Ordinal))
			return Array.Empty<ConnectionId>();

		var snapshot = _directory.Capture();
		var author = snapshot.Connections.FirstOrDefault(connection =>
			connection.ConnectionId == context.Actor.ConnectionId &&
			connection.CharacterId == context.Character.Id);
		if (author is null) return Array.Empty<ConnectionId>();

		if (context.ChannelId == HL2RPIds.Channels.OutOfCharacter)
			return snapshot.Connections.Select(connection => connection.ConnectionId).ToArray();

		if (!IsLocalChannel(context.ChannelId) || rule.Range is not float range ||
			!float.IsFinite(range) || range <= 0f)
			return Array.Empty<ConnectionId>();

		var maximumDistanceSquared = (double)range * range;
		return snapshot.Connections
			.Where(connection => author.Position.DistanceSquared(connection.Position) <= maximumDistanceSquared)
			.Select(connection => connection.ConnectionId)
			.ToArray();
	}

	private static bool IsLocalChannel(string channelId) => channelId is
		HL2RPIds.Channels.InCharacter or
		HL2RPIds.Channels.LocalOutOfCharacter or
		HL2RPIds.Channels.Emote or
		HL2RPIds.Channels.Whisper or
		HL2RPIds.Channels.Yell;
}

public static class HL2RPChatChannelRules
{
	/// <summary>
	/// Derives the recipient rules from the compiled schema, so a channel's range is stated exactly
	/// once — on its <see cref="ChatChannelDefinition"/>.
	/// <para>
	/// This used to be a hand-written table listing all nine channels with their ranges inline,
	/// parallel to the registrations and keyed by the same ids. Nothing kept the two agreeing, and a
	/// channel added to one and not the other failed silently: an unlisted channel simply resolved to
	/// no recipients, which looks exactly like "nobody was in range".
	/// </para>
	/// </summary>
	public static IReadOnlyList<ChatChannelRule> Create(CompiledSchema schema)
	{
		ArgumentNullException.ThrowIfNull(schema);
		return Array.AsReadOnly(schema.ChatChannels.All
			.Select(channel => new ChatChannelRule { Id = channel.Id, Range = channel.Range })
			.ToArray());
	}
}
