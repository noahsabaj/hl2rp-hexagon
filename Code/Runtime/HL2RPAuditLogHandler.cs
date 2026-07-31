#nullable enable

using System;
using System.Text;
using Hexagon.V2.Kernel.Events;
using HL2RP.V2.Features;
using Sandbox;

namespace HL2RP.V2.Runtime;

/// <summary>
/// One production sink shared by every post-commit administrative fact. The
/// payload is single-line and host-authored so operational logs retain a stable,
/// searchable audit trail without accepting client-formatted log content.
/// </summary>
internal sealed class HL2RPAuditLogHandler : IEventHandler<AdminAuditFact>
{
	public void Handle( AdminAuditFact fact )
	{
		ArgumentNullException.ThrowIfNull( fact );
		var character = fact.ActorCharacterId is CharacterId characterId
			? characterId.Value.ToString( "D" )
			: "none";
		Log.Info(
			$"HL2RP_AUDIT account={fact.ActorAccountId.Value} " +
			$"character={character} " +
			$"operation={fact.Operation} target=\"{Sanitize( fact.Target )}\" " +
			$"sequence={fact.CommitSequence} at={fact.OccurredAtUtc:O}" );
	}

	private static string Sanitize( string value )
	{
		if ( string.IsNullOrEmpty( value ) ) return "none";
		var length = Math.Min( value.Length, 256 );
		var builder = new StringBuilder( length );
		for ( var index = 0; index < length; index++ )
		{
			var character = value[index];
			builder.Append( char.IsControl( character ) ? ' ' : character == '"' ? '\'' : character );
		}
		return builder.ToString();
	}
}
