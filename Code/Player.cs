#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HL2RP.Logic;
using Sandbox;

namespace HL2RP;

/// <summary>What a player sees of one of their characters before entering the city.</summary>
public sealed record CharacterSummary( Guid Id, string Name, string Faction );

/// <summary>
/// One connection's presence in the city. Movement is the engine's <see cref="PlayerController"/>,
/// simulated by the owner. Everything that matters to other players is written by the host and
/// replicated through <c>[Sync]</c>; everything private to the owner arrives by owner-only RPC.
/// A client changes nothing directly: it calls a <c>[Rpc.Host]</c> request, and the host decides.
/// </summary>
[Title( "HL2RP Player" ), Category( "HL2RP" ), Icon( "person" )]
public sealed partial class Player : Component
{
	/// <summary>The pawn this client controls, or null before it has spawned.</summary>
	public static Player? Local { get; private set; }

	[Sync( SyncFlags.FromHost )] public bool HasCharacter { get; set; }
	[Sync( SyncFlags.FromHost )] public string CharacterName { get; set; } = string.Empty;
	[Sync( SyncFlags.FromHost )] public string CharacterDescription { get; set; } = string.Empty;
	[Sync( SyncFlags.FromHost )] public string FactionPath { get; set; } = string.Empty;

	public FactionDefinition? Faction => FactionDefinition.Find( FactionPath );

	// Owner-side copies of private state.
	public IReadOnlyList<CharacterSummary> Characters { get; private set; } = Array.Empty<CharacterSummary>();
	public InventoryData? Inventory { get; private set; }
	public long Tokens { get; private set; }

	/// <summary>Bumped whenever private state changes so panels can rebuild.</summary>
	public int PrivateVersion { get; private set; }

	// Host-side.
	private CharacterData? _character;
	private readonly RateLimiter _requests = new( capacity: 12, refillPerSecond: 4 );

	/// <summary>Builds the pawn on the host, before it is network-spawned, so clients receive it whole.</summary>
	public static void Compose( GameObject pawn )
	{
		pawn.Tags.Add( "player" );
		// The animated model sits on a child: PlayerController moves its renderer locally for the
		// duck bob, which on the root would drag the whole pawn to the origin.
		var body = new GameObject( pawn, true, "Body" );
		var renderer = body.AddComponent<SkinnedModelRenderer>();
		renderer.Model = Model.Load( "models/citizen/citizen.vmdl" );

		var controller = pawn.AddComponent<PlayerController>();
		// Left null for controllers created at runtime, yet its generated colliders need a tag set.
		controller.BodyCollisionTags = new TagSet();
		controller.BodyCollisionTags.Add( "player" );
		// Linked explicitly: the automatic link runs when the controller enables, before the child exists.
		controller.Renderer = renderer;
		pawn.AddComponent<Player>();
	}

	protected override void OnStart()
	{
		if ( !IsProxy )
		{
			Local = this;
			// Statics outlive a session in the editor, so a new one starts with a clean log.
			Chat.Clear();
		}
		ApplyPresence();
		if ( !IsProxy ) RequestCharacters();
	}

	protected override void OnDestroy()
	{
		if ( Local == this ) Local = null;
	}

	protected override void OnUpdate()
	{
		ApplyPresence();
		if ( IsProxy || !HasCharacter ) return;
		// "Use" opens and closes a door through IPressable. "Reload" locks and unlocks it.
		if ( Input.Pressed( "Reload" ) && Controller?.Hovered?.GetComponent<Door>() is { } door ) door.RequestLock();
	}

	private PlayerController? Controller => GetComponent<PlayerController>( true );

	/// <summary>A connection without a character is in the menu: no body in the world, no movement.</summary>
	private void ApplyPresence()
	{
		if ( Controller is not { } controller ) return;
		if ( controller.Renderer.IsValid() ) controller.Renderer.GameObject.Enabled = HasCharacter;
		if ( IsProxy ) return;
		controller.UseInputControls = HasCharacter;
		controller.UseLookControls = HasCharacter;
		controller.UseCameraControls = HasCharacter;
	}

	/// <summary>
	/// Every host request starts here. The caller must own this pawn, so one player cannot act as
	/// another, and must be within its request budget.
	/// </summary>
	private bool Authorize( out Connection caller, out GameManager game )
	{
		caller = Rpc.Caller;
		game = GameManager.Instance!;
		if ( !Networking.IsHost || game?.Roster is null || caller is null || caller != Network.Owner ) return false;
		if ( _requests.TryTake( RealTime.Now ) ) return true;
		Chat.Tell( caller, "You are doing that too fast." );
		return false;
	}

	private static long SteamIdOf( Connection connection ) => (long)connection.SteamId.ValueUnsigned;

	/// <summary>Host: writes the loaded character, including where it stands.</summary>
	public void HostSave()
	{
		if ( !Networking.IsHost || _character is null || GameManager.Instance?.Roster is not { } roster ) return;
		_character.Position = new[] { WorldPosition.x, WorldPosition.y, WorldPosition.z };
		roster.Save( _character );
	}

	/// <summary>Host: the loaded character, for other host systems such as doors.</summary>
	public CharacterData? HostCharacter => Networking.IsHost ? _character : null;

	/// <summary>Host: gives an item to the loaded character and tells the owner.</summary>
	public Result HostGive( ItemDefinition definition )
	{
		if ( _character is null ) return Result.Fail( ErrorCode.NotFound, "That player has no character loaded." );
		var added = InventoryGrid.Add( _character.Inventory, definition.ResourcePath, definition.Width, definition.Height );
		if ( !added.Ok ) return added.ToResult();
		GameManager.Instance?.Roster?.Save( _character );
		SendPrivateState();
		return Result.Success();
	}

	private void SendCharacterList( Connection caller, GameManager game )
	{
		var summaries = game.Roster!.OwnedBy( SteamIdOf( caller ) )
			.Select( value => new CharacterSummary( value.Id, value.Name, FactionDefinition.Find( value.Faction )?.Title ?? value.Faction ) )
			.ToArray();
		using ( Rpc.FilterInclude( caller ) ) ReceiveCharacters( JsonSerializer.Serialize( summaries ) );
	}

	private void SendPrivateState()
	{
		if ( _character is null || Network.Owner is not { } owner ) return;
		using ( Rpc.FilterInclude( owner ) )
			ReceivePrivateState( JsonSerializer.Serialize( _character.Inventory ), _character.Tokens );
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveCharacters( string json )
	{
		Characters = JsonSerializer.Deserialize<CharacterSummary[]>( json ) ?? Array.Empty<CharacterSummary>();
		PrivateVersion++;
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceivePrivateState( string inventoryJson, long tokens )
	{
		Inventory = JsonSerializer.Deserialize<InventoryData>( inventoryJson );
		Tokens = tokens;
		PrivateVersion++;
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveTeleport( Vector3 position )
	{
		WorldPosition = position;
		if ( Controller?.Body is { } body && body.IsValid() ) body.Velocity = Vector3.Zero;
	}
}
