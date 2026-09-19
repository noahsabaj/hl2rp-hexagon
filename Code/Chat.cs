#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using HL2RP.Logic;
using Sandbox;

namespace HL2RP;

/// <summary>A received line. A notice is the host talking to this player alone, not speech.</summary>
public readonly record struct ChatLine( string Text, bool IsNotice, ChatChannel Channel, RealTimeSince Age );

/// <summary>
/// Chat travels as host-only broadcasts filtered to the connections that should hear it. A client
/// never learns that a message it was out of range for existed.
/// </summary>
public static class Chat
{
	public const int MaximumLines = 100;

	/// <summary>Lines this client has received, oldest first.</summary>
	public static List<ChatLine> Lines { get; } = new();

	/// <summary>Bumped on every new line so panels can rebuild.</summary>
	public static int Version { get; private set; }

	public static void Clear()
	{
		Lines.Clear();
		Version++;
	}

	/// <summary>Host: deliver a message from a speaker to everyone in range of it.</summary>
	public static void Deliver( Player speaker, ChatMessage message )
	{
		if ( !Networking.IsHost ) return;
		var range = ChatRules.Range( message.Channel );
		var origin = speaker.WorldPosition;
		var recipients = Game.ActiveScene.GetAllComponents<Player>()
			.Where( listener => listener.Network.Owner is not null )
			.Where( listener => range is null ||
				(listener.HasCharacter && listener.WorldPosition.Distance( origin ) <= range.Value) )
			.Select( listener => listener.Network.Owner! )
			.ToArray();
		if ( recipients.Length == 0 ) return;
		var line = ChatRules.Format( message.Channel, speaker.CharacterName, message.Text );
		using ( Rpc.FilterInclude( recipients ) ) Receive( (int)message.Channel, line );
	}

	/// <summary>Host: a line for one connection only, such as the reason an action was refused.</summary>
	public static void Tell( Connection connection, string text )
	{
		if ( !Networking.IsHost ) return;
		using ( Rpc.FilterInclude( connection ) ) Receive( -1, text );
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	private static void Receive( int channel, string text )
	{
		Lines.Add( new ChatLine( text, channel < 0, channel < 0 ? ChatChannel.Say : (ChatChannel)channel, 0 ) );
		if ( Lines.Count > MaximumLines ) Lines.RemoveAt( 0 );
		Version++;
	}
}
