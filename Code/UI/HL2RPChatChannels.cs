#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Client.Chat;
using Hexagon.V2.Kernel.Schema;
using HL2RP.V2.Schema;

namespace HL2RP.UI;

/// <summary>
/// Projects the compiled schema's chat channels into the view the framework composer needs.
/// <para>
/// Compiled once: the registrations cannot change at runtime, and rebuilding this per keystroke
/// would recompile the schema on every character typed. Crucially it is DERIVED, not restated —
/// a hand-written client copy of the channel list is exactly the parallel table this rework
/// deleted on the host side, and it would drift the same way.
/// </para>
/// </summary>
internal static class HL2RPChatChannels
{
	private static IReadOnlyList<HexChatChannelView>? _views;
	private static IReadOnlyDictionary<string, string?>? _colours;

	private static CompiledSchema Compiled =>
		field ??= SchemaCompiler.Compile( new HL2RPSchema() ).Value;

	public static IReadOnlyList<HexChatChannelView> Views() =>
		_views ??= Compiled.ChatChannels.All
			.Select( channel => new HexChatChannelView(
				channel.Id,
				channel.DisplayName ?? channel.Id.ToUpperInvariant(),
				channel.Prefixes ?? Array.Empty<string>(),
				channel.AllowedWhileDead ) )
			.ToArray();

	public static string? Colour( string channelId ) =>
		(_colours ??= Compiled.ChatChannels.All.ToDictionary(
			channel => channel.Id,
			channel => channel.Colour,
			StringComparer.Ordinal ))
		.TryGetValue( channelId, out var colour ) ? colour : null;
}
