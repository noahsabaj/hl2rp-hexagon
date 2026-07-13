#nullable enable

namespace HL2RP.V2.Features;

public sealed record LiveChatAuthority
{
	public LiveChatAuthority(
		ConnectionId connectionId,
		AccountId accountId,
		CharacterId characterId,
		FactionId factionId,
		IEnumerable<string>? permissions = null )
	{
		ConnectionId = connectionId;
		AccountId = accountId;
		CharacterId = characterId;
		FactionId = factionId;
		Permissions = Array.AsReadOnly( (permissions ?? Array.Empty<string>())
			.Where( value => !string.IsNullOrWhiteSpace( value ) )
			.Distinct( StringComparer.Ordinal )
			.OrderBy( value => value, StringComparer.Ordinal )
			.ToArray() );
	}

	public ConnectionId ConnectionId { get; }
	public AccountId AccountId { get; }
	public CharacterId CharacterId { get; }
	public FactionId FactionId { get; }
	public IReadOnlyList<string> Permissions { get; }
	public bool HasPermission( string permissionId ) => Permissions.Contains( permissionId, StringComparer.Ordinal );
}

public sealed record ChatAuthoritySnapshot
{
	public ChatAuthoritySnapshot( long version, IEnumerable<LiveChatAuthority>? authorities = null )
	{
		if ( version < 0 ) throw new ArgumentOutOfRangeException( nameof(version) );
		var rows = (authorities ?? Array.Empty<LiveChatAuthority>())
			.OrderBy( value => value.ConnectionId.Value )
			.ToArray();
		if ( rows.Select( value => value.ConnectionId ).Distinct().Count() != rows.Length ||
			rows.Select( value => value.CharacterId ).Distinct().Count() != rows.Length )
			throw new ArgumentException( "Live chat authority identities must be unique.", nameof(authorities) );
		Version = version;
		Authorities = Array.AsReadOnly( rows );
	}

	public long Version { get; }
	public IReadOnlyList<LiveChatAuthority> Authorities { get; }
	public static ChatAuthoritySnapshot Empty { get; } = new( 0 );
}

public interface IChatAuthorityDirectory
{
	ChatAuthoritySnapshot Capture();
}

/// <summary>
/// Frozen live authority projection. It is refreshed from committed active
/// characters by the host and is the only permission source used by ChatService.
/// </summary>
public sealed class CanonicalChatAuthorityDirectory : IChatAuthorityDirectory, IPermissionAuthorizer
{
	private readonly object _sync = new();
	private ChatAuthoritySnapshot _current = ChatAuthoritySnapshot.Empty;

	public ChatAuthoritySnapshot Capture()
	{
		lock ( _sync ) return _current;
	}

	public void Publish( long version, IEnumerable<LiveChatAuthority> authorities )
	{
		var next = new ChatAuthoritySnapshot(
			version, authorities ?? throw new ArgumentNullException( nameof(authorities) ) );
		lock ( _sync )
		{
			if ( version < _current.Version )
				throw new InvalidOperationException( "Live chat authority cannot move backwards." );
			_current = next;
		}
	}

	public bool HasPermission( AccountId accountId, CharacterId characterId, string permissionId )
	{
		if ( string.IsNullOrWhiteSpace( permissionId ) ) return false;
		var snapshot = Capture();
		return snapshot.Authorities.Any( value =>
			value.AccountId == accountId && value.CharacterId == characterId &&
			value.HasPermission( permissionId ) );
	}
}

/// <summary>
/// Pure recipient calculation over one immutable live inventory capture and one
/// immutable active-character authority capture. Item possession never grants
/// official request or dispatch visibility.
/// </summary>
public sealed class HL2RPRadioRecipientResolver : IChatRecipientResolver
{
	private readonly IChatAuthorityDirectory _authorities;
	private readonly IChatRecipientResolver? _fallback;

	public HL2RPRadioRecipientResolver(
		IChatAuthorityDirectory authorities,
		IChatRecipientResolver? fallback = null )
	{
		_authorities = authorities ?? throw new ArgumentNullException( nameof(authorities) );
		_fallback = fallback;
	}

	public IReadOnlyList<ConnectionId> Resolve(
		ChatSendContext context,
		ChatChannelRule rule,
		LiveInventorySnapshot inventory )
	{
		if ( context.ChannelId is not HL2RPIds.Channels.Radio and
			not HL2RPIds.Channels.Request and not HL2RPIds.Channels.Dispatch )
			return _fallback?.Resolve( context, rule, inventory ) ?? new[] { context.Actor.ConnectionId };

		var authority = _authorities.Capture();
		var author = authority.Authorities.SingleOrDefault( value =>
			value.ConnectionId == context.Actor.ConnectionId &&
			value.AccountId == context.Actor.AccountId &&
			value.CharacterId == context.Character.Id );
		if ( author is null ) return Array.Empty<ConnectionId>();

		if ( context.ChannelId == HL2RPIds.Channels.Request )
		{
			return authority.Authorities
				.Where( value => value.ConnectionId == context.Actor.ConnectionId || IsRequestOfficial( value ) )
				.Select( value => value.ConnectionId )
				.Distinct()
				.OrderBy( value => value.Value )
				.ToArray();
		}

		if ( context.ChannelId == HL2RPIds.Channels.Dispatch )
		{
			return authority.Authorities
				.Where( IsDispatchOfficial )
				.Select( value => value.ConnectionId )
				.OrderBy( value => value.Value )
				.ToArray();
		}

		var senderRadio = inventory.Items
			.Where( item => item.OwningCharacterId == context.Character.Id &&
				item.Definition.Value == HL2RPIds.Items.Radio )
			.OrderBy( item => item.ItemId.Value )
			.Select( DecodePoweredRadio )
			.FirstOrDefault( state => state is not null );
		if ( senderRadio is null ) return Array.Empty<ConnectionId>();
		return authority.Authorities
			.Where( value => inventory.Items.Any( item =>
				item.OwningCharacterId == value.CharacterId &&
				item.Definition.Value == HL2RPIds.Items.Radio &&
				DecodePoweredRadio( item ) is RadioItemState radio &&
				radio.Frequency == senderRadio.Frequency ) )
			.Select( value => value.ConnectionId )
			.OrderBy( value => value.Value )
			.ToArray();
	}

	private static bool IsRequestOfficial( LiveChatAuthority value ) =>
		value.FactionId.Value is HL2RPIds.Factions.CivilProtection or HL2RPIds.Factions.Overwatch ||
		IsDispatchOfficial( value );

	private static bool IsDispatchOfficial( LiveChatAuthority value ) =>
		(value.FactionId.Value is HL2RPIds.Factions.CivilProtection or
			HL2RPIds.Factions.Overwatch or HL2RPIds.Factions.CityAdministration) &&
		value.HasPermission( HL2RPIds.Permissions.DispatchChat );

	private static RadioItemState? DecodePoweredRadio( LiveInventoryItemView item )
	{
		if ( !item.Traits.TryGetValue( "radio", out var payload ) ) return null;
		var decoded = HL2RPFeaturePersistence.Decode( payload, HL2RPPersistence.Radio );
		return decoded.Succeeded && decoded.Value.Powered ? decoded.Value : null;
	}
}
