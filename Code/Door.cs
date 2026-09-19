#nullable enable

using System;
using System.Linq;
using HL2RP.Logic;
using Sandbox;

namespace HL2RP;

/// <summary>
/// A door anyone can open and only some factions can lock. State is host-written and replicated;
/// the swing is animated locally on every client. It persists by the scene object's id, which the
/// editor keeps stable across loads.
/// </summary>
[Title( "HL2RP Door" ), Category( "HL2RP" ), Icon( "door_front" )]
public sealed class Door : Component, Component.IPressable
{
	/// <summary>How far a pawn may stand from the door and still work it.</summary>
	public const float Reach = 150f;

	[Property] public Angles OpenRotation { get; set; } = new( 0, 90, 0 );
	[Property] public float SwingSpeed { get; set; } = 6f;
	[Property] public bool StartsLocked { get; set; }

	[Sync( SyncFlags.FromHost )] public bool IsOpen { get; set; }
	[Sync( SyncFlags.FromHost )] public bool IsLocked { get; set; }

	private Rotation _closed;
	private bool _restored;

	protected override void OnStart() => _closed = LocalRotation;

	protected override void OnUpdate()
	{
		if ( !_restored ) HostRestore();
		var target = IsOpen ? _closed * OpenRotation.ToRotation() : _closed;
		LocalRotation = Rotation.Slerp( LocalRotation, target, Time.Delta * SwingSpeed );
	}

	/// <summary>
	/// Host: join the network session and adopt the saved state, or the authored default. This waits
	/// for the lobby, which opens after the scene has loaded: a door restored before then is not
	/// networked, so its synced state reaches nobody and its host requests have nothing to travel on.
	/// </summary>
	private void HostRestore()
	{
		if ( !Networking.IsHost || !Networking.IsActive || GameManager.Instance?.Roster is null ) return;
		_restored = true;
		if ( !Network.Active ) GameObject.NetworkSpawn();
		if ( GameManager.Instance.World.Doors.TryGetValue( GameObject.Id, out var saved ) )
		{
			IsOpen = saved.IsOpen;
			IsLocked = saved.IsLocked;
		}
		else
		{
			IsLocked = StartsLocked;
		}
	}

	bool IPressable.CanPress( IPressable.Event e ) => true;

	bool IPressable.Press( IPressable.Event e )
	{
		RequestUse();
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) => new IPressable.Tooltip(
		IsLocked ? "Locked door" : IsOpen ? "Close door" : "Open door",
		IsLocked ? "lock" : "door_front",
		"Press Reload to lock or unlock, if your faction may." );

	[Rpc.Host]
	public void RequestUse()
	{
		if ( Actor() is not { } actor ) return;
		if ( IsLocked )
		{
			Chat.Tell( Rpc.Caller, "The door is locked." );
			return;
		}
		IsOpen = !IsOpen;
		Persist();
	}

	[Rpc.Host]
	public void RequestLock()
	{
		if ( Actor() is not { } actor ) return;
		if ( actor.Faction?.CanLockDoors != true )
		{
			Chat.Tell( Rpc.Caller, "Your faction cannot lock doors." );
			return;
		}
		IsLocked = !IsLocked;
		if ( IsLocked ) IsOpen = false;
		Persist();
	}

	/// <summary>
	/// The caller's pawn, if it has a character in the city and stands within reach. The host
	/// measures this itself; a client saying "I am at the door" counts for nothing.
	/// </summary>
	private Player? Actor()
	{
		if ( !Networking.IsHost || Rpc.Caller is not { } caller ) return null;
		var actor = Scene.GetAllComponents<Player>().FirstOrDefault( value => value.Network.Owner == caller );
		if ( actor?.HostCharacter is null ) return null;
		var nearest = GameObject.GetBounds().ClosestPoint( actor.WorldPosition );
		if ( nearest.Distance( actor.WorldPosition ) <= Reach ) return actor;
		Chat.Tell( caller, "You are too far from the door." );
		return null;
	}

	private void Persist()
	{
		if ( GameManager.Instance is not { } game ) return;
		game.World.Doors[GameObject.Id] = new DoorData { IsOpen = IsOpen, IsLocked = IsLocked };
		game.SaveWorld();
	}
}
