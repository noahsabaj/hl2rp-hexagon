#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Persistence;
using Hexagon.V2.Runtime;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Showcase.Combat;
using HL2RP.V2.Showcase.Scanner;
using HL2RP.V2.World;
using Sandbox;

namespace HL2RP.V2.Runtime;

/// <summary>
/// Thin s&amp;box boundaries for scanner presentation and authoritative pistol
/// traces. Durable combat-target damage is staged in the pistol unit of work.
/// </summary>
internal sealed class HL2RPSandboxBoundaries :
	IScannerBodyBoundary,
	IScannerMotionBoundary,
	IScannerMotionLimitsProvider,
	IScannerEffectsBoundary,
	ITrustedScannerPoseProvider,
	IAuthoritativePistolRaycast,
	ICombatDamageBoundary
{
	private readonly Scene _scene;
	private readonly IReadOnlyDictionary<SceneEntityId, HL2RPSceneFeatureComponent> _features;
	private readonly Func<ConnectionId, (AccountId Account, HexPlayerBody Player, CharacterRecord Character)?> _actors;
	private readonly DomainRepositories _repositories;
	private readonly Func<GameObject, string?> _playerTargetToken;

	public HL2RPSandboxBoundaries(
		Scene scene,
		IReadOnlyDictionary<SceneEntityId, HL2RPSceneFeatureComponent> features,
		Func<ConnectionId, (AccountId, HexPlayerBody, CharacterRecord)?> actors,
		DomainRepositories repositories,
		Func<GameObject, string?> playerTargetToken )
	{
		_scene = scene;
		_features = features;
		_actors = actors;
		_repositories = repositories;
		_playerTargetToken = playerTargetToken;
	}

	public void EnterPilot( InventoryActor actor, SceneEntityId scannerId )
	{
		var state = _actors( actor.ConnectionId );
		if ( state?.Player.PlayableBody is GameObject body ) body.Enabled = false;
	}

	public void Restore( InventoryActor actor, SceneEntityId scannerId, string reason )
	{
		if ( _features.TryGetValue( scannerId, out var feature ) )
			feature.Components.Get<HL2RPScannerMotionController>()?.Stop();
		var state = _actors( actor.ConnectionId );
		if ( state?.Player.PlayableBody is GameObject body ) body.Enabled = true;
	}

	public void Apply( SceneEntityId scannerId, ScannerMotionCommand command )
	{
		if ( !_features.TryGetValue( scannerId, out var feature ) ||
			feature is not HL2RPScannerDroneComponent drone ) return;
		feature.GameObject.GetOrAddComponent<HL2RPScannerMotionController>()
			.SetCommand( command, drone.MaximumSpeed, drone.MaximumAcceleration );
	}

	public OperationResult<ScannerMotionLimits> Resolve( SceneEntityId scannerId )
	{
		if ( !_features.TryGetValue( scannerId, out var feature ) ||
			feature is not HL2RPScannerDroneComponent drone )
			return OperationResult<ScannerMotionLimits>.Failure(
				ErrorCode.NotFound, "Scanner motion limits require a linked drone scene object." );
		if ( !float.IsFinite( drone.MaximumSpeed ) || drone.MaximumSpeed <= 0 ||
			!float.IsFinite( drone.MaximumAcceleration ) || drone.MaximumAcceleration <= 0 )
			return OperationResult<ScannerMotionLimits>.Failure(
				ErrorCode.ConfigurationInvalid, "Scanner motion limits are invalid." );
		return OperationResult<ScannerMotionLimits>.Success( new ScannerMotionLimits(
			drone.MaximumSpeed,
			drone.MaximumAcceleration,
			HL2RPScannerMotionController.MaximumAngularSpeed ) );
	}

	public void SetSpotlight( SceneEntityId scannerId, bool enabled )
	{
		if ( _features.TryGetValue( scannerId, out var feature ) )
			feature.GameObject.GetOrAddComponent<HL2RPScannerVisualEffects>().SetSpotlight( enabled );
	}

	public void Flash( SceneEntityId scannerId )
	{
		if ( _features.TryGetValue( scannerId, out var feature ) )
			feature.GameObject.GetOrAddComponent<HL2RPScannerVisualEffects>().Flash();
	}

	public void PublishPhoto( SceneEntityId scannerId, ScannerPhotoMetadata metadata )
	{
		Flash( scannerId );
		Log.Info( $"Scanner photo metadata committed photo={metadata.PhotoId:D} scanner={scannerId} pilot={metadata.PilotCharacterId} at={metadata.CapturedAtUtc:O}" );
	}

	public OperationResult<TrustedScannerPose> Capture( SceneEntityId scannerId )
	{
		if ( !_features.TryGetValue( scannerId, out var feature ) )
			return OperationResult<TrustedScannerPose>.Failure( ErrorCode.NotFound, "Scanner scene object is unavailable." );
		var position = feature.GameObject.WorldPosition;
		return OperationResult<TrustedScannerPose>.Success( new TrustedScannerPose( position.x, position.y, position.z ) );
	}

	public OperationResult<AuthoritativeShot> Resolve( PistolFireIntent intent )
	{
		var state = _actors( intent.Actor.ConnectionId );
		if ( state is null || state.Value.Character.Id != intent.Actor.CharacterId ||
			state.Value.Player.PlayableBody is not GameObject body )
			return OperationResult<AuthoritativeShot>.Failure( ErrorCode.Unauthorized, "Authoritative firing body is unavailable." );
		var origin = body.WorldPosition + Vector3.Up * 56f;
		var end = origin + body.WorldTransform.Forward * 8_192f;
		var trace = _scene.Trace.Ray( origin, end ).IgnoreGameObjectHierarchy( body ).Run();
		var resolvedEnd = trace.Hit ? trace.EndPosition : end;
		var playerTarget = trace.Hit && trace.GameObject is not null ? _playerTargetToken( trace.GameObject ) : null;
		var target = trace.Hit && playerTarget is null
			? _features.FirstOrDefault( pair => pair.Value.GameObject == trace.GameObject ).Key
			: default;
		return OperationResult<AuthoritativeShot>.Success( new AuthoritativeShot(
			new WorldPoint( origin.x, origin.y, origin.z ),
			new WorldPoint( resolvedEnd.x, resolvedEnd.y, resolvedEnd.z ),
			playerTarget ?? (target == default ? null : target.Value.ToString( "D" )),
			trace.Hit ) );
	}

	public OperationResult<PreparedCombatDamage> Prepare( AuthoritativeShot shot, long damage ) =>
		damage > 0
			? OperationResult<PreparedCombatDamage>.Success( new PreparedCombatDamage( shot, damage ) )
			: OperationResult<PreparedCombatDamage>.Failure( ErrorCode.InvalidArgument, "Combat damage must be positive." );

	public OperationResult Stage( IUnitOfWork unitOfWork, PreparedCombatDamage damage )
	{
		ArgumentNullException.ThrowIfNull( unitOfWork );
		if ( !Guid.TryParse( damage.Shot.TargetToken, out var rawId ) || rawId == Guid.Empty )
			return OperationResult.Success();
		var id = new SceneEntityId( rawId );
		var document = _repositories.SceneEntities.Find( DomainKeys.SceneEntity( id ) );
		if ( document is null || document.Value.Kind != "combat_target" ) return OperationResult.Success();
		CombatTargetEntityState state;
		try { state = HL2RPPersistence.CombatTargetState.Deserialize( document.Value.State.Data, document.Value.State.TypeVersion ); }
		catch ( Exception )
		{
			return OperationResult.Failure( ErrorCode.PersistedTypeInvalid, "Combat target state is malformed." );
		}
		var editor = unitOfWork.Edit( _repositories.SceneEntities, document );
		if ( editor is null ) return OperationResult.Failure( ErrorCode.Conflict, "Combat target changed." );
		editor.Replace( document.Value with
		{
			State = HL2RPPersistence.Payload( HL2RPPersistence.CombatTargetState, state with
			{
				CurrentHealth = Math.Max( 0, state.CurrentHealth - damage.Damage ),
				LastHitAtUtc = DateTimeOffset.UtcNow
			} )
		} );
		unitOfWork.Save( editor );
		return OperationResult.Success();
	}

	public void Commit( PreparedCombatDamage damage ) { }
}

internal sealed class HL2RPScannerMotionController : Component
{
	public const float MaximumAngularSpeed = 90f;
	private const float CommandIdleTimeoutSeconds = 0.2f;
	private Vector3 _velocity;
	private Vector3 _targetVelocity;
	private float _yawRate;
	private float _pitchRate;
	private float _maximumSpeed = 1f;
	private float _maximumAcceleration = 1f;
	private float _lastCommandAt;

	public void SetCommand(
		ScannerMotionCommand command,
		float authoredMaximumSpeed,
		float authoredMaximumAcceleration )
	{
		_maximumSpeed = Math.Clamp( authoredMaximumSpeed, 1f, 1_000f );
		_maximumAcceleration = Math.Clamp(
			Math.Min( authoredMaximumAcceleration, command.MaximumAcceleration ), 1f, 4_000f );
		_targetVelocity = ClampMagnitude(
			new Vector3( command.VelocityX, command.VelocityY, command.VelocityZ ),
			_maximumSpeed );
		_yawRate = Math.Clamp( command.YawRate, -MaximumAngularSpeed, MaximumAngularSpeed );
		_pitchRate = Math.Clamp( command.PitchRate, -MaximumAngularSpeed, MaximumAngularSpeed );
		_lastCommandAt = Time.Now;
	}

	public void Stop()
	{
		_velocity = Vector3.Zero;
		_targetVelocity = Vector3.Zero;
		_yawRate = 0f;
		_pitchRate = 0f;
	}

	protected override void OnUpdate()
	{
		var delta = Math.Clamp( Time.Delta, 0f, 0.1f );
		if ( Time.Now - _lastCommandAt >= CommandIdleTimeoutSeconds )
		{
			_targetVelocity = Vector3.Zero;
			_yawRate = 0f;
			_pitchRate = 0f;
		}
		_velocity = MoveTowards(
			_velocity, _targetVelocity, _maximumAcceleration * delta );
		var transform = GameObject.WorldTransform;
		GameObject.WorldPosition +=
			(transform.Forward * _velocity.x + transform.Right * _velocity.y + Vector3.Up * _velocity.z) * delta;
		GameObject.WorldRotation *= Rotation.FromYaw( _yawRate * delta ) *
			Rotation.FromPitch( _pitchRate * delta );
	}

	private static Vector3 MoveTowards( Vector3 current, Vector3 target, float maximumDelta )
	{
		var delta = target - current;
		var length = delta.Length;
		return length <= maximumDelta || length <= 0.0001f
			? target
			: current + delta / length * maximumDelta;
	}

	private static Vector3 ClampMagnitude( Vector3 value, float maximum )
	{
		var length = value.Length;
		return length <= maximum || length <= 0.0001f ? value : value / length * maximum;
	}
}

/// <summary>
/// Applies durable scene-state notifications to their editor-authored objects.
/// It is registered only on post-commit buses, so a failed transaction can never
/// move a door or change a forcefield collider.
/// </summary>
internal sealed class HL2RPSceneStateEventHandler :
	IEventHandler<DoorStateChangedEvent>,
	IEventHandler<ForcefieldStateChangedEvent>
{
	private readonly IReadOnlyDictionary<SceneEntityId, HL2RPSceneFeatureComponent> _features;

	public HL2RPSceneStateEventHandler(
		IReadOnlyDictionary<SceneEntityId, HL2RPSceneFeatureComponent> features ) =>
		_features = features;

	public void Handle( DoorStateChangedEvent @event )
	{
		if ( !_features.TryGetValue( @event.SceneEntityId, out var component ) ||
			component is not HL2RPDoorComponent door )
			throw new InvalidOperationException( $"Door scene object '{@event.SceneEntityId}' is unavailable." );
		door.ApplyState( @event.State );
	}

	public void Handle( ForcefieldStateChangedEvent @event )
	{
		if ( !_features.TryGetValue( @event.SceneEntityId, out var component ) ||
			component is not HL2RPForcefieldComponent forcefield )
			throw new InvalidOperationException( $"Forcefield scene object '{@event.SceneEntityId}' is unavailable." );
		forcefield.ApplyState( @event.State );
	}

	public OperationResult ApplyCommittedState( DomainRepositories repositories )
	{
		foreach ( var pair in _features )
		{
			var document = repositories.SceneEntities.Find( DomainKeys.SceneEntity( pair.Key ) );
			if ( document is null )
				return OperationResult.Failure( ErrorCode.NotFound, $"Scene state '{pair.Key}' was not initialized." );
			try
			{
				switch ( pair.Value )
				{
					case HL2RPDoorComponent door:
						door.ApplyState( HL2RPPersistence.DoorState.Deserialize(
							document.Value.State.Data, document.Value.State.TypeVersion ) );
						break;
					case HL2RPForcefieldComponent forcefield:
						forcefield.ApplyState( HL2RPPersistence.ForcefieldState.Deserialize(
							document.Value.State.Data, document.Value.State.TypeVersion ) );
						break;
				}
			}
			catch ( Exception )
			{
				return OperationResult.Failure(
					ErrorCode.PersistedTypeInvalid, $"Scene state '{pair.Key}' cannot be applied." );
			}
		}
		return OperationResult.Success();
	}
}

internal sealed class HL2RPScannerVisualEffects : Component
{
	private SpotLight? _spotlight;
	private PointLight? _flash;
	private float _flashEndsAt;

	protected override void OnStart()
	{
		_spotlight = GameObject.GetOrAddComponent<SpotLight>();
		_flash = GameObject.GetOrAddComponent<PointLight>();
		_spotlight.Enabled = false;
		_flash.Enabled = false;
	}

	protected override void OnUpdate()
	{
		if ( _flash is not null && _flash.Enabled && Time.Now >= _flashEndsAt ) _flash.Enabled = false;
	}

	public void SetSpotlight( bool enabled )
	{
		_spotlight ??= GameObject.GetOrAddComponent<SpotLight>();
		_spotlight.Enabled = enabled;
	}

	public void Flash()
	{
		_flash ??= GameObject.GetOrAddComponent<PointLight>();
		_flash.Enabled = true;
		_flashEndsAt = Time.Now + 0.15f;
	}
}
