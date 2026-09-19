#nullable enable

using System;
using System.Linq;
using Sandbox;

namespace HL2RP.Dev;

/// <summary>
/// Lets the editor's automation play the game. It is never placed in a scene: a test adds it at
/// runtime and writes <see cref="Command"/>, and each command calls the same client request the
/// HUD would, so it travels the real RPC path and is judged by the real host rules. It refuses to
/// run outside the editor. The automation attaches it to the editor's copy of the scene, so it
/// always reaches the running game through <c>Game.ActiveScene</c> and <c>Player.Local</c>.
/// </summary>
[Title( "HL2RP Dev Driver" ), Category( "HL2RP" ), Icon( "smart_toy" )]
public sealed class DevDriver : Component
{
	private string _command = string.Empty;

	/// <summary>What happened to the last command, for the automation to read back.</summary>
	[Property] public string LastResult { get; set; } = string.Empty;

	/// <summary>Arguments are separated by '|', for example <c>create|John Doe|A tired resident of the city.|citizen</c>.</summary>
	[Property]
	public string Command
	{
		get => _command;
		set
		{
			_command = value ?? string.Empty;
			if ( _command.Length == 0 ) return;
			LastResult = Run( _command.Split( '|' ) );
			// The automation reads results back from the console.
			Log.Info( $"[dev] {_command} => {LastResult}" );
		}
	}

	private string Run( string[] args )
	{
		if ( !Game.IsEditor ) return "refused: editor only";
		if ( Player.Local is not { } player ) return "no local player";
		try
		{
			switch ( args[0] )
			{
				case "create":
					var faction = FactionDefinition.All.FirstOrDefault( value => value.ResourceName == args[3] );
					player.RequestCreateCharacter( args[1], args[2], faction?.ResourcePath ?? args[3] );
					return "sent create";
				case "enter":
					var target = player.Characters.FirstOrDefault( value => value.Name == args[1] );
					if ( target is null ) return $"no character named '{args[1]}' in: {string.Join( ", ", player.Characters.Select( value => value.Name ) )}";
					player.RequestEnterCity( target.Id );
					return "sent enter";
				case "leave":
					player.RequestLeaveCity();
					return "sent leave";
				case "delete":
					var doomed = player.Characters.FirstOrDefault( value => value.Name == args[1] );
					if ( doomed is null ) return "no such character";
					player.RequestDeleteCharacter( doomed.Id );
					return "sent delete";
				case "say":
					player.RequestSay( args[1] );
					return "sent say";
				case "move":
					var item = player.Inventory?.Items.ElementAtOrDefault( int.Parse( args[1] ) );
					if ( item is null ) return "no such item";
					player.RequestMoveItem( item.Id, int.Parse( args[2] ), int.Parse( args[3] ) );
					return "sent move";
				case "door":
					var door = Game.ActiveScene.GetAllComponents<Door>().FirstOrDefault( value => value.GameObject.Name == args[1] );
					if ( door is null ) return "no such door";
					if ( args[2] == "lock" ) door.RequestLock(); else door.RequestUse();
					return $"sent door {args[2]}";
				case "console":
					// Runs a real console command, so operator commands are tested as an operator types them.
					ConsoleSystem.Run( args[1] );
					return "ran";
				case "steamid":
					return Connection.Local.SteamId.ValueUnsigned.ToString();
				case "net":
					return string.Join( " ; ", Game.ActiveScene.GetAllComponents<Door>().Select( value =>
						$"{value.GameObject.Name}: active={value.Network.Active} proxy={value.IsProxy} owner={value.Network.Owner?.DisplayName} mode={value.GameObject.NetworkMode}" ) )
						+ $" | host={Networking.IsHost} netactive={Networking.IsActive} manager={GameManager.Instance is not null} roster={GameManager.Instance?.Roster is not null}";
				case "goto":
					player.WorldPosition = new Vector3( float.Parse( args[1] ), float.Parse( args[2] ), float.Parse( args[3] ) );
					return "moved";
				case "state":
					return $"has={player.HasCharacter} name='{player.CharacterName}' faction='{player.Faction?.Title}' tokens={player.Tokens} " +
						$"characters=[{string.Join( ", ", player.Characters.Select( value => value.Name ) )}] " +
						$"items=[{string.Join( ", ", player.Inventory?.Items.Select( value => $"{ItemDefinition.Find( value.Definition )?.Title}@{value.X},{value.Y}" ) ?? Array.Empty<string>() )}] " +
						$"doors=[{string.Join( ", ", Game.ActiveScene.GetAllComponents<Door>().Select( value => $"{value.GameObject.Name}:open={value.IsOpen},locked={value.IsLocked}" ) )}] " +
						$"pos={player.WorldPosition} chat=[{string.Join( " / ", Chat.Lines.TakeLast( 4 ).Select( value => value.Text ) )}]";
				default:
					return $"unknown command '{args[0]}'";
			}
		}
		catch ( Exception exception )
		{
			return $"threw: {exception.Message}";
		}
	}
}
