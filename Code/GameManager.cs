#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HL2RP.Logic;
using Sandbox;
using Sandbox.Network;

namespace HL2RP;

/// <summary>
/// The one scene object that runs the server: it opens the lobby, gives each connection a pawn,
/// and owns the host's saved state. Everything it holds exists on the host only.
/// </summary>
[Title( "HL2RP Game Manager" ), Category( "HL2RP" ), Icon( "location_city" )]
public sealed class GameManager : Component, Component.INetworkListener
{
	public static GameManager? Instance { get; private set; }

	/// <summary>Host-only. Null on clients.</summary>
	public CharacterRoster? Roster { get; private set; }
	public DocumentStore? Store { get; private set; }
	public WorldData World { get; private set; } = new();

	[Property] public float AutosaveSeconds { get; set; } = 60f;

	private readonly Dictionary<long, AccountData> _accounts = new();
	private RealTimeSince _sinceAutosave;

	protected override void OnAwake() => Instance = this;

	protected override void OnDestroy()
	{
		if ( Instance == this ) Instance = null;
	}

	protected override async Task OnLoad()
	{
		if ( Scene.IsEditor || Networking.IsActive ) return;
		LoadingScreen.Title = "Opening the city";
		await Task.DelayRealtimeSeconds( 0.1f );
		Networking.CreateLobby( new LobbyConfig() );
	}

	protected override void OnStart()
	{
		if ( !Networking.IsHost ) return;
		Store = new DocumentStore( new SandboxFileStore(), message => Log.Warning( $"[store] {message}" ) );
		Roster = new CharacterRoster( Store );
		World = Store.Load<WorldData>( "world.json" ) ?? new WorldData();
		Log.Info( $"HL2RP host ready: {Roster.Count} characters, {World.Doors.Count} saved doors." );
	}

	protected override void OnFixedUpdate()
	{
		if ( !Networking.IsHost || Roster is null || _sinceAutosave < AutosaveSeconds ) return;
		_sinceAutosave = 0;
		foreach ( var player in Scene.GetAllComponents<Player>() ) player.HostSave();
	}

	void INetworkListener.OnActive( Connection connection )
	{
		// The project settings already deny these; repeat it per connection so a bad setting fails closed.
		if ( !connection.IsHost )
		{
			connection.CanSpawnObjects = false;
			connection.CanRefreshObjects = false;
			connection.CanDestroyObjects = false;
		}

		var spawn = FindSpawn();
		var pawn = new GameObject( true, $"Player - {connection.DisplayName}" );
		pawn.WorldTransform = spawn;
		Player.Compose( pawn );
		if ( !pawn.NetworkSpawn( connection ) )
		{
			pawn.Destroy();
			connection.Kick( "The server could not create your player." );
		}
	}

	void INetworkListener.OnDisconnected( Connection connection )
	{
		foreach ( var player in Scene.GetAllComponents<Player>().Where( value => value.Network.Owner == connection ) )
		{
			player.HostSave();
			player.GameObject.Destroy();
		}
	}

	void INetworkListener.OnBecameHost( Connection previousHost )
	{
		// A new host has none of the saved state, so it would run an empty city. Stop instead.
		Log.Warning( "HL2RP does not support host migration; leaving the session." );
		Networking.Disconnect();
	}

	public Transform FindSpawn()
	{
		var points = Scene.GetAllComponents<SpawnPoint>().ToArray();
		return points.Length == 0 ? global::Transform.Zero : Game.Random.FromArray( points )!.WorldTransform;
	}

	public void SaveWorld() => Store?.Save( "world.json", World );

	public AccountData Account( long steamId )
	{
		if ( _accounts.TryGetValue( steamId, out var account ) ) return account;
		account = Store?.Load<AccountData>( $"accounts/{steamId}.json" ) ?? new AccountData { SteamId = steamId };
		_accounts[steamId] = account;
		return account;
	}

	public void SaveAccount( AccountData account ) => Store?.Save( $"accounts/{account.SteamId}.json", account );
}
