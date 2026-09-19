#nullable enable

using System;
using System.Linq;
using HL2RP.Logic;
using Sandbox;

namespace HL2RP;

// Everything a client can ask of the host. Each request re-derives who is asking from the
// connection, validates its arguments on the host, and saves before replying.
public sealed partial class Player
{
	[Rpc.Host]
	public void RequestCharacters()
	{
		if ( Authorize( out var caller, out var game ) ) SendCharacterList( caller, game );
	}

	[Rpc.Host]
	public void RequestCreateCharacter( string name, string description, string factionPath )
	{
		if ( !Authorize( out var caller, out var game ) ) return;
		if ( FactionDefinition.Find( factionPath ) is not { } faction )
		{
			Chat.Tell( caller, "That faction does not exist." );
			return;
		}
		var steamId = SteamIdOf( caller );
		if ( faction.RequiresWhitelist && !game.Account( steamId ).Whitelists.Contains( faction.ResourcePath ) )
		{
			Chat.Tell( caller, $"You are not whitelisted for {faction.Title}." );
			return;
		}

		var created = game.Roster!.Create( steamId, name, description, faction.ResourcePath, DateTimeOffset.UtcNow );
		if ( !created.Ok )
		{
			Chat.Tell( caller, created.Message );
			return;
		}
		foreach ( var item in faction.StartingItems.Where( value => value.IsValid() ) )
			InventoryGrid.Add( created.Value.Inventory, item.ResourcePath, item.Width, item.Height );
		game.Roster.Save( created.Value );
		SendCharacterList( caller, game );
	}

	[Rpc.Host]
	public void RequestDeleteCharacter( Guid characterId )
	{
		if ( !Authorize( out var caller, out var game ) ) return;
		if ( _character?.Id == characterId )
		{
			Chat.Tell( caller, "Leave that character before deleting it." );
			return;
		}
		var deleted = game.Roster!.Delete( SteamIdOf( caller ), characterId );
		if ( !deleted.Ok ) Chat.Tell( caller, deleted.Message );
		SendCharacterList( caller, game );
	}

	[Rpc.Host]
	public void RequestEnterCity( Guid characterId )
	{
		if ( !Authorize( out var caller, out var game ) || _character is not null ) return;
		// "Not yours" and "does not exist" get the same answer, so ids cannot be probed.
		if ( game.Roster!.Find( characterId ) is not { } character || character.SteamId != SteamIdOf( caller ) )
		{
			Chat.Tell( caller, "Character was not found." );
			return;
		}
		if ( Scene.GetAllComponents<Player>().Any( other => other._character?.Id == characterId ) )
		{
			Chat.Tell( caller, "That character is already in the city." );
			return;
		}

		_character = character;
		CharacterName = character.Name;
		CharacterDescription = character.Description;
		FactionPath = character.Faction;
		HasCharacter = true;
		var position = character.Position is { Length: 3 } saved
			? new Vector3( saved[0], saved[1], saved[2] )
			: game.FindSpawn().Position;
		using ( Rpc.FilterInclude( caller ) ) ReceiveTeleport( position );
		SendPrivateState();
	}

	[Rpc.Host]
	public void RequestLeaveCity()
	{
		if ( !Authorize( out var caller, out var game ) || _character is null ) return;
		HostSave();
		_character = null;
		HasCharacter = false;
		CharacterName = string.Empty;
		CharacterDescription = string.Empty;
		FactionPath = string.Empty;
		SendCharacterList( caller, game );
	}

	[Rpc.Host]
	public void RequestSay( string text )
	{
		if ( !Authorize( out var caller, out _ ) ) return;
		if ( _character is null )
		{
			Chat.Tell( caller, "Enter the city before speaking." );
			return;
		}
		var message = ChatRules.Parse( text );
		if ( message.Ok ) Chat.Deliver( this, message.Value );
		else Chat.Tell( caller, message.Message );
	}

	[Rpc.Host]
	public void RequestMoveItem( Guid itemId, int x, int y )
	{
		if ( !Authorize( out var caller, out var game ) || _character is null ) return;
		var moved = InventoryGrid.Move( _character.Inventory, itemId, x, y );
		if ( moved.Ok ) game.Roster!.Save( _character );
		else Chat.Tell( caller, moved.Message );
		SendPrivateState();
	}

	[Rpc.Host]
	public void RequestDiscardItem( Guid itemId )
	{
		if ( !Authorize( out var caller, out var game ) || _character is null ) return;
		var removed = InventoryGrid.Remove( _character.Inventory, itemId );
		if ( removed.Ok ) game.Roster!.Save( _character );
		else Chat.Tell( caller, removed.Message );
		SendPrivateState();
	}
}
