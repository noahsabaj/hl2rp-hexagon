#nullable enable

using System;
using System.Linq;
using Sandbox;

namespace HL2RP;

/// <summary>
/// Server console commands. They run in the host's console, so there is no in-game operator
/// account to steal or spoof; whoever can type into the server console already owns the server.
/// </summary>
public static class Operators
{
	[ConCmd( "hl2rp_whitelist" )]
	public static void Whitelist( string steamId, string faction )
	{
		if ( !TryHost( out var game ) ) return;
		if ( !long.TryParse( steamId, out var id ) || id <= 0 )
		{
			Log.Warning( "Usage: hl2rp_whitelist <steamid64> <faction>" );
			return;
		}
		if ( FindFaction( faction ) is not { } definition ) return;
		var account = game.Account( id );
		if ( !account.Whitelists.Contains( definition.ResourcePath ) ) account.Whitelists.Add( definition.ResourcePath );
		game.SaveAccount( account );
		Log.Info( $"{id} may now create {definition.Title} characters." );
	}

	[ConCmd( "hl2rp_unwhitelist" )]
	public static void Unwhitelist( string steamId, string faction )
	{
		if ( !TryHost( out var game ) || !long.TryParse( steamId, out var id ) ) return;
		if ( FindFaction( faction ) is not { } definition ) return;
		var account = game.Account( id );
		account.Whitelists.Remove( definition.ResourcePath );
		game.SaveAccount( account );
		Log.Info( $"{id} may no longer create {definition.Title} characters." );
	}

	[ConCmd( "hl2rp_give" )]
	public static void Give( string characterName, string item )
	{
		if ( !TryHost( out var game ) ) return;
		var target = game.Scene.GetAllComponents<Player>()
			.FirstOrDefault( value => value.HasCharacter && value.CharacterName.Equals( characterName, StringComparison.OrdinalIgnoreCase ) );
		var definition = ResourceLibrary.GetAll<ItemDefinition>()
			.FirstOrDefault( value => value.ResourceName.Equals( item, StringComparison.OrdinalIgnoreCase ) );
		if ( target is null || definition is null )
		{
			Log.Warning( "Usage: hl2rp_give \"<character name>\" <item>. The character must be in the city." );
			return;
		}
		var given = target.HostGive( definition );
		Log.Info( given.Ok ? $"Gave {definition.Title} to {target.CharacterName}." : given.Message );
	}

	private static bool TryHost( out GameManager game )
	{
		game = GameManager.Instance!;
		if ( Networking.IsHost && game?.Roster is not null ) return true;
		Log.Warning( "This command only works on the host." );
		return false;
	}

	private static FactionDefinition? FindFaction( string name )
	{
		var definition = FactionDefinition.All.FirstOrDefault( value => value.ResourceName.Equals( name, StringComparison.OrdinalIgnoreCase ) );
		if ( definition is null ) Log.Warning( $"No faction named '{name}'. Known: {string.Join( ", ", FactionDefinition.All.Select( value => value.ResourceName ) )}" );
		return definition;
	}
}
