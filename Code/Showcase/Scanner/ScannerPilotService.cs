#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Showcase.Scanner;

public sealed record ScannerMotionCommand(
	float VelocityX,
	float VelocityY,
	float VelocityZ,
	float YawRate,
	float PitchRate,
	float MaximumAcceleration);

public sealed record ScannerMotionLimits(
	float MaximumSpeed,
	float MaximumAcceleration,
	float MaximumAngularSpeed);

public sealed record ScannerInputIntent(
	InteractionSessionId SessionId,
	long Sequence,
	float Forward,
	float Right,
	float Up,
	float Yaw,
	float Pitch);

public sealed record TrustedScannerPose(float X, float Y, float Z);

public interface IScannerBodyBoundary
{
	/// <summary>Stores and hides the player body after the pilot commit.</summary>
	void EnterPilot(InventoryActor actor, SceneEntityId scannerId);
	/// <summary>Must be idempotent and infallible; restores the body on every exit path.</summary>
	void Restore(InventoryActor actor, SceneEntityId scannerId, string reason);
}

public interface IScannerMotionBoundary
{
	void Apply(SceneEntityId scannerId, ScannerMotionCommand command);
}

public interface IScannerMotionLimitsProvider
{
	OperationResult<ScannerMotionLimits> Resolve(SceneEntityId scannerId);
}

public interface IScannerEffectsBoundary
{
	void SetSpotlight(SceneEntityId scannerId, bool enabled);
	void Flash(SceneEntityId scannerId);
	void PublishPhoto(SceneEntityId scannerId, ScannerPhotoMetadata metadata);
}

public interface ITrustedScannerPoseProvider
{
	OperationResult<TrustedScannerPose> Capture(SceneEntityId scannerId);
}

public sealed record ScannerPilotSession(
	InteractionSessionId SessionId,
	InventoryActor Actor,
	SceneEntityId ScannerId,
	long LastAcceptedSequence,
	DateTimeOffset? LastInputAtUtc);

public sealed record ScannerPilotEntryReceipt(
	ScannerPilotSession Session,
	long CommitSequence,
	CommitReceipt Commit,
	OperationError? BoundaryError = null) : IHL2RPCommittedOperation;
public sealed record ScannerSpotlightReceipt(
	SceneEntityId ScannerId,
	bool Enabled,
	long CommitSequence,
	CommitReceipt Commit,
	OperationError? BoundaryError = null) : IHL2RPCommittedOperation;
public sealed record ScannerInputReceipt(
	long Sequence, ScannerMotionCommand Motion, long CommitSequence, CommitReceipt Commit,
	OperationError? BoundaryError = null) : IHL2RPCommittedOperation;
public sealed record ScannerPhotoReceipt(
	ScannerPhotoMetadata Metadata, DateTimeOffset CooldownUntilUtc,
	long CommitSequence, CommitReceipt Commit,
	OperationError? BoundaryError = null) : IHL2RPCommittedOperation;
public sealed record ScannerPilotCleanupReceipt(
	ScannerPilotSession Session,
	string Reason,
	long CommitSequence,
	CommitReceipt Commit,
	OperationError? BoundaryError = null) : IHL2RPCommittedOperation;

/// <summary>
/// Synchronous post-commit boundary for lifecycle cleanup that has no command result to carry its
/// provider-issued receipt. Implementations must not throw; the persistence commit is already durable.
/// </summary>
public interface IScannerCommitSink
{
	void Observe(ScannerPilotCleanupReceipt receipt);
}

/// <summary>
/// Host-bound scanner coordinator. Input is normalized intent; speed, ordering,
/// ray-independent effects, trusted photo metadata, and persistence are host-owned.
/// </summary>
public sealed class ScannerPilotService
{
	public static readonly TimeSpan MinimumInputInterval = TimeSpan.FromMilliseconds(50);
	public static readonly TimeSpan PhotoCooldown = TimeSpan.FromSeconds(15);
	public static readonly TimeSpan CleanupRetryDelay = TimeSpan.FromMilliseconds(250);
	public const int MaximumStoredPhotos = 128;
	public const int CleanupMaximumAttempts = 8;

	private readonly DomainRepositories _repositories;
	private readonly InteractionAuthorityService _authority;
	private readonly InteractionSessionService _sessions;
	private readonly IHexClock _clock;
	private readonly IScannerBodyBoundary _body;
	private readonly IScannerMotionBoundary _motion;
	private readonly IScannerMotionLimitsProvider _motionLimits;
	private readonly IScannerEffectsBoundary _effects;
	private readonly ITrustedScannerPoseProvider _poses;
	private readonly IScannerCommitSink _commitSink;
	private readonly Func<Guid> _createPhotoId;
	private readonly Func<TimeSpan, Task> _cleanupDelay;
	private readonly Dictionary<InteractionSessionId, ScannerPilotSession> _active = new();
	private readonly object _cleanupSync = new();
	private readonly HashSet<InteractionSessionId> _terminating = new();
	private readonly Dictionary<InteractionSessionId, CleanupOperation> _cleanupOperations = new();

	public ScannerPilotService(
		DomainRepositories repositories,
		InteractionAuthorityService authority,
		InteractionSessionService sessions,
		IHexClock clock,
		IScannerBodyBoundary body,
		IScannerMotionBoundary motion,
		IScannerMotionLimitsProvider motionLimits,
		IScannerEffectsBoundary effects,
		ITrustedScannerPoseProvider poses,
		IScannerCommitSink commitSink,
		Func<Guid>? createPhotoId = null,
		Func<TimeSpan, Task>? cleanupDelay = null)
	{
		_repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
		_authority = authority ?? throw new ArgumentNullException(nameof(authority));
		_sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
		_body = body ?? throw new ArgumentNullException(nameof(body));
		_motion = motion ?? throw new ArgumentNullException(nameof(motion));
		_motionLimits = motionLimits ?? throw new ArgumentNullException(nameof(motionLimits));
		_effects = effects ?? throw new ArgumentNullException(nameof(effects));
		_poses = poses ?? throw new ArgumentNullException(nameof(poses));
		_commitSink = commitSink ?? throw new ArgumentNullException(nameof(commitSink));
		_createPhotoId = createPhotoId ?? Guid.NewGuid;
		_cleanupDelay = cleanupDelay ?? Task.Delay;
		_sessions.SessionRevoked += OnSessionRevoked;
	}

	public IReadOnlyList<ScannerPilotSession> ActiveSessions
	{
		get
		{
			lock (_cleanupSync)
				return _active.Values.Where(value => !_terminating.Contains(value.SessionId)).ToArray();
		}
	}

	public int PendingCleanupCount
	{
		get
		{
			lock (_cleanupSync) return _cleanupOperations.Count;
		}
	}

	public int TerminatingSessionCount
	{
		get
		{
			lock (_cleanupSync) return _terminating.Count;
		}
	}

	public async ValueTask<OperationResult> ReconcilePersistedPilotsAsync(
		CancellationToken cancellationToken = default)
	{
		var stale = new List<(DocumentSnapshot<PersistentSceneEntityRecord> Document, ScannerEntityState State)>();
		foreach (var document in _repositories.SceneEntities.All()
			.Where(value => value.Value.Kind is "scanner_dock" or "scanner_drone"))
		{
			var decoded = ScannerPersistence.Decode(document.Value.State, HL2RPPersistence.ScannerState);
			if (decoded.Failed) return OperationResult.Failure(decoded.Error!.Code, decoded.Error.Message);
			if (decoded.Value.PilotCharacterId is not null) stale.Add((document, decoded.Value));
		}
		if (stale.Count == 0) return OperationResult.Success();
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		foreach (var entry in stale)
		{
			var editor = unitOfWork.Edit(_repositories.SceneEntities, entry.Document);
			if (editor is null)
			{
				await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
				return OperationResult.Failure(ErrorCode.Conflict,
					"Scanner state changed during startup reconciliation.");
			}
			editor.Replace(editor.Value with
			{
				State = HL2RPPersistence.Payload(HL2RPPersistence.ScannerState,
					entry.State with { PilotCharacterId = null, SpotlightEnabled = false })
			});
			unitOfWork.Save(editor);
		}
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		return committed.Succeeded ? OperationResult.Success() : ScannerPersistence.Failure(committed.Error!);
	}

	public async ValueTask DrainCleanupAsync()
	{
		Task<OperationResult>[] pending;
		lock (_cleanupSync) pending = _cleanupOperations.Values.Select(value => value.Completion.Task).ToArray();
		foreach (var cleanup in pending)
			await cleanup;

		ScannerPilotSession[] retry;
		lock (_cleanupSync)
			retry = _active.Values
				.Where(value => _terminating.Contains(value.SessionId) && !_cleanupOperations.ContainsKey(value.SessionId))
				.ToArray();
		foreach (var active in retry)
			await BeginCleanup(active, "cleanup_retry");
	}

	public async ValueTask<OperationResult<ScannerPilotEntryReceipt>> EnterAsync(
		InventoryActor actor,
		SceneEntityId scannerId,
		CancellationToken cancellationToken = default)
	{
		var character = _repositories.Characters.Find(DomainKeys.Character(actor.CharacterId));
		var scanner = _repositories.SceneEntities.Find(DomainKeys.SceneEntity(scannerId));
		if (character is null || scanner is null)
			return OperationResult<ScannerPilotEntryReceipt>.Failure(ErrorCode.NotFound,
				"Pilot character or scanner was not found.");
		if (character.Value.AccountId != actor.AccountId)
			return OperationResult<ScannerPilotEntryReceipt>.Failure(ErrorCode.Unauthorized,
				"Authenticated pilot binding is invalid.");
		if (scanner.Value.Kind != "scanner_drone")
			return OperationResult<ScannerPilotEntryReceipt>.Failure(ErrorCode.PolicyDenied,
				"Interaction target is not a pilotable scanner drone.");
		if (character.Value.Class?.Value != HL2RPIds.Classes.Scanner)
			return OperationResult<ScannerPilotEntryReceipt>.Failure(ErrorCode.PolicyDenied,
				"Active character is not assigned to the scanner class.");
		var state = ScannerPersistence.Decode(scanner.Value.State, HL2RPPersistence.ScannerState);
		if (state.Failed) return Failure<ScannerPilotEntryReceipt>(state.Error!);
		if (state.Value.LastAcceptedInputSequence < 0 || state.Value.Photos.Count > MaximumStoredPhotos)
			return OperationResult<ScannerPilotEntryReceipt>.Failure(ErrorCode.PersistedTypeInvalid,
				"Scanner sequence or photo state is outside registered limits.");
		bool scannerAddressed;
		lock (_cleanupSync) scannerAddressed = _active.Values.Any(value => value.ScannerId == scannerId);
		if (state.Value.PilotCharacterId is not null || scannerAddressed)
			return OperationResult<ScannerPilotEntryReceipt>.Failure(ErrorCode.Conflict, "Scanner already has a pilot.");

		var opened = _authority.Begin(actor.ConnectionId, actor.AccountId, actor.CharacterId,
			InteractionTarget.SceneEntity(scannerId));
		if (opened.Failed) return Failure<ScannerPilotEntryReceipt>(opened.Error!);
		if (opened.Value.Session is not InteractionSession session || session.Kind != InteractionSessionKind.Scanner)
		{
			if (opened.Value.Session is not null) _authority.Close(opened.Value.Session.Id);
			return OperationResult<ScannerPilotEntryReceipt>.Failure(ErrorCode.Unauthorized,
				"Scanner target did not grant a scanner session.");
		}
		var sessionProof = _sessions.Prove(session);
		if (sessionProof is null)
		{
			_authority.Close(session.Id);
			return OperationResult<ScannerPilotEntryReceipt>.Failure(ErrorCode.Unauthorized,
				"Scanner session changed before pilot state could be staged.");
		}

		var nextState = state.Value with { PilotCharacterId = actor.CharacterId, SpotlightEnabled = false };
		var pilot = new ScannerPilotSession(session.Id, actor, scannerId,
			nextState.LastAcceptedInputSequence, null);
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require(sessionProof);
		HL2RPUnitOfWork.RequireActorState(unitOfWork, _repositories, character);
		var editor = unitOfWork.Edit(_repositories.SceneEntities, scanner);
		if (editor is null)
		{
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			_authority.Close(session.Id);
			return OperationResult<ScannerPilotEntryReceipt>.Failure(ErrorCode.Conflict, "Scanner state changed.");
		}
		editor.Replace(editor.Value with
		{
			State = HL2RPPersistence.Payload(HL2RPPersistence.ScannerState, nextState)
		});
		unitOfWork.Save(editor);
		lock (_cleanupSync) _active.Add(session.Id, pilot);
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		if (!committed.Succeeded)
		{
			lock (_cleanupSync)
			{
				if (!_terminating.Contains(session.Id)) _active.Remove(session.Id);
			}
			_authority.Close(session.Id);
			return ScannerPersistence.Failure<ScannerPilotEntryReceipt>(committed.Error!);
		}

		OperationError? boundaryError = null;
		var cleanupRequired = false;
		lock (_cleanupSync)
		{
			if (_active.ContainsKey(session.Id) && !_terminating.Contains(session.Id) &&
				sessionProof.IsCurrent())
				boundaryError = ObserveBoundary(
					() => _body.EnterPilot(actor, scannerId),
					"Scanner pilot committed, but player-body transition failed.");
			else
				cleanupRequired = true;
		}
		if (cleanupRequired)
			await BeginCleanup(pilot, "post_commit_session_revoked");
		return OperationResult<ScannerPilotEntryReceipt>.Success(new ScannerPilotEntryReceipt(
			pilot, committed.Value!.Sequence, committed.Value, boundaryError));
	}

	public async ValueTask<OperationResult<ScannerInputReceipt>> ApplyInputAsync(
		InventoryActor actor,
		ScannerInputIntent intent,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(intent);
		var active = ValidateSession(actor, intent.SessionId);
		if (active.Failed) return Failure<ScannerInputReceipt>(active.Error!);
		if (intent.Sequence <= 0 || intent.Sequence <= active.Value.LastAcceptedSequence)
			return OperationResult<ScannerInputReceipt>.Failure(ErrorCode.Unauthorized,
				"Scanner input sequence is stale or out of order.");
		var now = _clock.UtcNow;
		if (active.Value.LastInputAtUtc is DateTimeOffset last && now - last < MinimumInputInterval)
			return OperationResult<ScannerInputReceipt>.Failure(ErrorCode.PolicyDenied,
				"Scanner input rate exceeds 20 Hz.");
		var limits = _motionLimits.Resolve(active.Value.ScannerId);
		if (limits.Failed) return Failure<ScannerInputReceipt>(limits.Error!);
		var normalized = ValidateAndScale(intent, limits.Value);
		if (normalized.Failed) return Failure<ScannerInputReceipt>(normalized.Error!);
		var continued = Continue(active.Value);
		if (continued.Failed) return Failure<ScannerInputReceipt>(continued.Error!);

		var scanner = ResolveState(active.Value.ScannerId);
		if (scanner.Failed) return Failure<ScannerInputReceipt>(scanner.Error!);
		if (scanner.Value.State.PilotCharacterId != actor.CharacterId ||
			intent.Sequence <= scanner.Value.State.LastAcceptedInputSequence)
			return OperationResult<ScannerInputReceipt>.Failure(ErrorCode.Unauthorized,
				"Persisted scanner pilot or sequence no longer matches the session.");
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require(continued.Value);
		var editor = unitOfWork.Edit(_repositories.SceneEntities, scanner.Value.Document);
		if (editor is null)
		{
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return OperationResult<ScannerInputReceipt>.Failure(ErrorCode.Conflict, "Scanner state changed.");
		}
		var nextState = scanner.Value.State with { LastAcceptedInputSequence = intent.Sequence };
		editor.Replace(editor.Value with { State = HL2RPPersistence.Payload(HL2RPPersistence.ScannerState, nextState) });
		unitOfWork.Save(editor);
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		if (!committed.Succeeded) return ScannerPersistence.Failure<ScannerInputReceipt>(committed.Error!);

		var stillActive = false;
		var cleanupRequired = false;
		OperationError? boundaryError = null;
		lock (_cleanupSync)
		{
			stillActive = !_terminating.Contains(intent.SessionId) &&
				_active.ContainsKey(intent.SessionId) &&
				continued.Value.IsCurrent();
			if (stillActive)
			{
				_active[intent.SessionId] = active.Value with
				{
					LastAcceptedSequence = intent.Sequence,
					LastInputAtUtc = now
				};
				boundaryError = ObserveBoundary(
					() => _motion.Apply(active.Value.ScannerId, normalized.Value),
					"Scanner input committed, but world motion failed.");
			}
			else cleanupRequired = true;
		}
		// A concurrent lifecycle cleanup can win immediately after this commit. The
		// input receipt must still be returned because its sequence was durably
		// acknowledged; only the now-stale world motion is suppressed.
		if (cleanupRequired)
			_ = BeginCleanup(active.Value, "post_commit_session_revoked");
		return OperationResult<ScannerInputReceipt>.Success(new ScannerInputReceipt(
			intent.Sequence, normalized.Value, committed.Value!.Sequence, committed.Value, boundaryError));
	}

	public async ValueTask<OperationResult<ScannerSpotlightReceipt>> ToggleSpotlightAsync(
		InventoryActor actor,
		InteractionSessionId sessionId,
		CancellationToken cancellationToken = default)
	{
		var active = ValidateSession(actor, sessionId);
		if (active.Failed) return Failure<ScannerSpotlightReceipt>(active.Error!);
		var continued = Continue(active.Value);
		if (continued.Failed) return Failure<ScannerSpotlightReceipt>(continued.Error!);
		var scanner = ResolveState(active.Value.ScannerId);
		if (scanner.Failed) return Failure<ScannerSpotlightReceipt>(scanner.Error!);
		if (scanner.Value.State.PilotCharacterId != actor.CharacterId)
			return OperationResult<ScannerSpotlightReceipt>.Failure(ErrorCode.Unauthorized, "Scanner pilot changed.");
		var enabled = !scanner.Value.State.SpotlightEnabled;
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require(continued.Value);
		var editor = unitOfWork.Edit(_repositories.SceneEntities, scanner.Value.Document);
		if (editor is null)
		{
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return OperationResult<ScannerSpotlightReceipt>.Failure(ErrorCode.Conflict, "Scanner state changed.");
		}
		editor.Replace(editor.Value with
		{
			State = HL2RPPersistence.Payload(HL2RPPersistence.ScannerState,
				scanner.Value.State with { SpotlightEnabled = enabled })
		});
		unitOfWork.Save(editor);
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		if (!committed.Succeeded) return ScannerPersistence.Failure<ScannerSpotlightReceipt>(committed.Error!);
		OperationError? boundaryError = null;
		var cleanupRequired = false;
		lock (_cleanupSync)
		{
			if (_active.ContainsKey(sessionId) && !_terminating.Contains(sessionId) &&
				continued.Value.IsCurrent())
				boundaryError = ObserveBoundary(
					() => _effects.SetSpotlight(active.Value.ScannerId, enabled),
					"Scanner spotlight committed, but the world effect failed.");
			else cleanupRequired = true;
		}
		if (cleanupRequired)
			_ = BeginCleanup(active.Value, "post_commit_session_revoked");
		return OperationResult<ScannerSpotlightReceipt>.Success(new ScannerSpotlightReceipt(
			active.Value.ScannerId, enabled, committed.Value!.Sequence, committed.Value, boundaryError));
	}

	public OperationResult Flash(InventoryActor actor, InteractionSessionId sessionId)
	{
		var active = ValidateSession(actor, sessionId);
		if (active.Failed) return OperationResult.Failure(active.Error!.Code, active.Error.Message);
		var continued = Continue(active.Value);
		if (continued.Failed) return OperationResult.Failure(continued.Error!.Code, continued.Error.Message);
		lock (_cleanupSync)
		{
			if (!_active.ContainsKey(sessionId) || _terminating.Contains(sessionId) ||
				!continued.Value.IsCurrent())
				return OperationResult.Failure(ErrorCode.Unauthorized,
					"Scanner session changed before the flash effect could run.");
			_effects.Flash(active.Value.ScannerId);
		}
		return OperationResult.Success();
	}

	public async ValueTask<OperationResult<ScannerPhotoReceipt>> TakePhotoAsync(
		InventoryActor actor,
		InteractionSessionId sessionId,
		CancellationToken cancellationToken = default)
	{
		var active = ValidateSession(actor, sessionId);
		if (active.Failed) return Failure<ScannerPhotoReceipt>(active.Error!);
		var continued = Continue(active.Value);
		if (continued.Failed) return Failure<ScannerPhotoReceipt>(continued.Error!);
		var scanner = ResolveState(active.Value.ScannerId);
		if (scanner.Failed) return Failure<ScannerPhotoReceipt>(scanner.Error!);
		var now = _clock.UtcNow;
		if (scanner.Value.State.PhotoCooldownUntilUtc is DateTimeOffset cooldown && now < cooldown)
			return OperationResult<ScannerPhotoReceipt>.Failure(ErrorCode.PolicyDenied,
				"Scanner photo cooldown has not completed.");
		var pose = _poses.Capture(active.Value.ScannerId);
		if (pose.Failed) return Failure<ScannerPhotoReceipt>(pose.Error!);
		if (!float.IsFinite(pose.Value.X) || !float.IsFinite(pose.Value.Y) || !float.IsFinite(pose.Value.Z))
			return OperationResult<ScannerPhotoReceipt>.Failure(ErrorCode.InvalidArgument,
				"Trusted scanner pose is not finite.");
		var photoId = _createPhotoId();
		if (photoId == Guid.Empty)
			return OperationResult<ScannerPhotoReceipt>.Failure(ErrorCode.InternalError,
				"Scanner photo identity is invalid.");
		var metadata = new ScannerPhotoMetadata
		{
			PhotoId = photoId,
			PilotCharacterId = actor.CharacterId,
			CapturedAtUtc = now,
			PositionX = pose.Value.X,
			PositionY = pose.Value.Y,
			PositionZ = pose.Value.Z
		};
		var photos = scanner.Value.State.Photos
			.Append(metadata)
			.TakeLast(MaximumStoredPhotos)
			.ToArray();
		var nextCooldown = now + PhotoCooldown;
		var nextState = scanner.Value.State with
		{
			Photos = photos,
			PhotoCooldownUntilUtc = nextCooldown
		};
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require(continued.Value);
		var editor = unitOfWork.Edit(_repositories.SceneEntities, scanner.Value.Document);
		if (editor is null)
		{
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return OperationResult<ScannerPhotoReceipt>.Failure(ErrorCode.Conflict, "Scanner state changed.");
		}
		editor.Replace(editor.Value with { State = HL2RPPersistence.Payload(HL2RPPersistence.ScannerState, nextState) });
		unitOfWork.Save(editor);
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		if (!committed.Succeeded) return ScannerPersistence.Failure<ScannerPhotoReceipt>(committed.Error!);
		OperationError? boundaryError = null;
		var cleanupRequired = false;
		lock (_cleanupSync)
		{
			if (_active.ContainsKey(sessionId) && !_terminating.Contains(sessionId) &&
				continued.Value.IsCurrent())
				boundaryError = ObserveBoundary(
					() => _effects.PublishPhoto(active.Value.ScannerId, metadata),
					"Scanner photo committed, but the world effect failed.");
			else cleanupRequired = true;
		}
		if (cleanupRequired)
			_ = BeginCleanup(active.Value, "post_commit_session_revoked");
		return OperationResult<ScannerPhotoReceipt>.Success(new ScannerPhotoReceipt(
			metadata, nextCooldown, committed.Value!.Sequence, committed.Value, boundaryError));
	}

	public ValueTask<OperationResult> ExitAsync(
		InventoryActor actor,
		InteractionSessionId sessionId,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var active = FindAddressableSession(actor, sessionId);
		return active.Failed
			? ValueTask.FromResult(OperationResult.Failure(active.Error!.Code, active.Error.Message))
			: ExitCoreAsync(active.Value, "exit", cancellationToken);
	}

	public async ValueTask DisconnectAsync(ConnectionId connectionId, CancellationToken cancellationToken = default)
	{
		ScannerPilotSession[] matching;
		lock (_cleanupSync) matching = _active.Values.Where(value => value.Actor.ConnectionId == connectionId).ToArray();
		foreach (var session in matching)
		{
			cancellationToken.ThrowIfCancellationRequested();
			await ExitCoreAsync(session, "disconnect", cancellationToken);
		}
	}

	public async ValueTask DestroyedAsync(SceneEntityId scannerId, CancellationToken cancellationToken = default)
	{
		ScannerPilotSession[] matching;
		lock (_cleanupSync) matching = _active.Values.Where(value => value.ScannerId == scannerId).ToArray();
		foreach (var session in matching)
		{
			cancellationToken.ThrowIfCancellationRequested();
			await ExitCoreAsync(session, "destroyed", cancellationToken);
		}
	}

	private async ValueTask<OperationResult> ExitCoreAsync(
		ScannerPilotSession active,
		string reason,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var cleanup = BeginCleanup(active, reason);
		_authority.Close(active.SessionId);
		return await cleanup;
	}

	private void OnSessionRevoked(InteractionSession revoked)
	{
		ScannerPilotSession? active;
		lock (_cleanupSync)
		{
			if (_terminating.Contains(revoked.Id)) return;
			_active.TryGetValue(revoked.Id, out active);
		}
		if (active is not null)
			_ = BeginCleanup(active, revoked.RevokeReason ?? "session_revoked");
	}

	private Task<OperationResult> BeginCleanup(ScannerPilotSession active, string reason)
	{
		CleanupOperation operation;
		var restoreBody = false;
		lock (_cleanupSync)
		{
			if (_cleanupOperations.TryGetValue(active.SessionId, out var existing))
				return existing.Completion.Task;
			restoreBody = _terminating.Add(active.SessionId);
			operation = new CleanupOperation(active, reason);
			_cleanupOperations.Add(active.SessionId, operation);
		}

		if (restoreBody)
		{
			operation.BoundaryError = MergeBoundaryErrors(
				ObserveBoundary(
					() => _body.Restore(active.Actor, active.ScannerId, reason),
					"Scanner cleanup started, but player-body restoration failed."),
				ObserveBoundary(
					() => _effects.SetSpotlight(active.ScannerId, false),
					"Scanner cleanup started, but spotlight reset failed."));
		}
		_ = CompleteCleanupAsync(operation);
		return operation.Completion.Task;
	}

	private async Task CompleteCleanupAsync(CleanupOperation operation)
	{
		OperationResult result;
		try
		{
			while (true)
			{
				result = await ClearPersistedPilotWithRetryAsync(
					operation.Session, operation.Reason, operation.BoundaryError);
				if (result.Succeeded || !CanRetryCleanup(result)) break;
				await _cleanupDelay(CleanupRetryDelay);
			}
		}
		catch (Exception exception)
		{
			result = OperationResult.Failure(ErrorCode.InternalError,
				$"Scanner pilot cleanup failed unexpectedly: {exception.Message}");
		}

		lock (_cleanupSync)
		{
			if (result.Succeeded)
			{
				_active.Remove(operation.Session.SessionId);
				_terminating.Remove(operation.Session.SessionId);
			}
			_cleanupOperations.Remove(operation.Session.SessionId);
		}
		operation.Completion.TrySetResult(result);
	}

	private bool CanRetryCleanup(OperationResult result)
	{
		if (result.Error!.Code == ErrorCode.Conflict) return true;
		return result.Error.Code == ErrorCode.InternalError &&
			_repositories.Provider.Health.Status != PersistenceHealthStatus.Fatal;
	}

	private async Task<OperationResult> ClearPersistedPilotWithRetryAsync(
		ScannerPilotSession active,
		string reason,
		OperationError? boundaryError)
	{
		OperationResult result = OperationResult.Failure(ErrorCode.Conflict, "Scanner cleanup did not run.");
		for (var attempt = 0; attempt < CleanupMaximumAttempts; attempt++)
		{
			result = await ClearPersistedPilotOnceAsync(active, reason, boundaryError);
			if (result.Succeeded || !CanRetryCleanup(result)) return result;
		}
		return result;
	}

	private async Task<OperationResult> ClearPersistedPilotOnceAsync(
		ScannerPilotSession active,
		string reason,
		OperationError? boundaryError)
	{
		var scanner = ResolveState(active.ScannerId);
		if (scanner.Failed)
			return scanner.Error!.Code == ErrorCode.NotFound
				? OperationResult.Success()
				: OperationResult.Failure(scanner.Error.Code, scanner.Error.Message);
		if (scanner.Value.State.PilotCharacterId is null ||
			scanner.Value.State.PilotCharacterId != active.Actor.CharacterId)
			return OperationResult.Success();
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		var editor = unitOfWork.Edit(_repositories.SceneEntities, scanner.Value.Document);
		if (editor is null)
		{
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return OperationResult.Failure(ErrorCode.Conflict, "Scanner state changed during pilot cleanup.");
		}
		editor.Replace(editor.Value with
		{
			State = HL2RPPersistence.Payload(HL2RPPersistence.ScannerState,
				scanner.Value.State with { PilotCharacterId = null, SpotlightEnabled = false })
		});
		unitOfWork.Save(editor);
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork);
		if (!committed.Succeeded) return ScannerPersistence.Failure(committed.Error!);
		_commitSink.Observe(new ScannerPilotCleanupReceipt(
			active, reason, committed.Value!.Sequence, committed.Value, boundaryError));
		return OperationResult.Success();
	}

	private OperationResult<ScannerPilotSession> ValidateSession(
		InventoryActor actor,
		InteractionSessionId sessionId)
	{
		lock (_cleanupSync)
		{
			if (!_active.TryGetValue(sessionId, out var active) || active.Actor != actor ||
				_terminating.Contains(sessionId))
				return OperationResult<ScannerPilotSession>.Failure(ErrorCode.Unauthorized,
					"Scanner session is stale, terminating, or bound to another pilot.");
			return OperationResult<ScannerPilotSession>.Success(active);
		}
	}

	private OperationResult<ScannerPilotSession> FindAddressableSession(
		InventoryActor actor,
		InteractionSessionId sessionId)
	{
		lock (_cleanupSync)
		{
			if (!_active.TryGetValue(sessionId, out var active) || active.Actor != actor)
				return OperationResult<ScannerPilotSession>.Failure(ErrorCode.Unauthorized,
					"Scanner cleanup is stale or bound to another pilot.");
			return OperationResult<ScannerPilotSession>.Success(active);
		}
	}

	private OperationResult<InteractionSessionProof> Continue(ScannerPilotSession active)
	{
		var continued = _authority.Continue(active.SessionId, active.Actor.ConnectionId,
			active.Actor.AccountId, active.Actor.CharacterId, InteractionTarget.SceneEntity(active.ScannerId));
		if (continued.Failed)
			return OperationResult<InteractionSessionProof>.Failure(
				continued.Error!.Code, continued.Error.Message);
		var proof = _sessions.Prove(continued.Value);
		return proof is not null
			? OperationResult<InteractionSessionProof>.Success(proof)
			: OperationResult<InteractionSessionProof>.Failure(
				ErrorCode.Unauthorized, "Scanner session changed during continuation.");
	}

	private OperationResult<ResolvedScanner> ResolveState(SceneEntityId scannerId)
	{
		var document = _repositories.SceneEntities.Find(DomainKeys.SceneEntity(scannerId));
		if (document is null)
			return OperationResult<ResolvedScanner>.Failure(ErrorCode.NotFound, "Scanner state was not found.");
		var state = ScannerPersistence.Decode(document.Value.State, HL2RPPersistence.ScannerState);
		return state.Succeeded
			? OperationResult<ResolvedScanner>.Success(new ResolvedScanner(document, state.Value))
			: Failure<ResolvedScanner>(state.Error!);
	}

	private static OperationResult<ScannerMotionCommand> ValidateAndScale(
		ScannerInputIntent intent,
		ScannerMotionLimits limits)
	{
		if (!float.IsFinite(limits.MaximumSpeed) || limits.MaximumSpeed <= 0 ||
			!float.IsFinite(limits.MaximumAcceleration) || limits.MaximumAcceleration <= 0 ||
			!float.IsFinite(limits.MaximumAngularSpeed) || limits.MaximumAngularSpeed <= 0)
			return OperationResult<ScannerMotionCommand>.Failure(ErrorCode.InvalidArgument,
				"Authored scanner motion limits must be finite and positive.");
		var values = new[] { intent.Forward, intent.Right, intent.Up, intent.Yaw, intent.Pitch };
		if (values.Any(value => !float.IsFinite(value) || Math.Abs(value) > 1f))
			return OperationResult<ScannerMotionCommand>.Failure(ErrorCode.InvalidArgument,
				"Scanner input axes must be finite and normalized to [-1, 1].");
		var linearMagnitudeSquared = intent.Forward * intent.Forward + intent.Right * intent.Right + intent.Up * intent.Up;
		if (linearMagnitudeSquared > 1.0001f)
			return OperationResult<ScannerMotionCommand>.Failure(ErrorCode.InvalidArgument,
				"Scanner linear input exceeds the normalized movement cap.");
		return OperationResult<ScannerMotionCommand>.Success(new ScannerMotionCommand(
			intent.Forward * limits.MaximumSpeed,
			intent.Right * limits.MaximumSpeed,
			intent.Up * limits.MaximumSpeed,
			intent.Yaw * limits.MaximumAngularSpeed,
			intent.Pitch * limits.MaximumAngularSpeed,
			limits.MaximumAcceleration));
	}

	private static OperationResult<T> Failure<T>(OperationError error) =>
		OperationResult<T>.Failure(error.Code, error.Message);

	private static OperationError? ObserveBoundary(Action action, string message)
	{
		try
		{
			action();
			return null;
		}
		catch (Exception exception)
		{
			return new OperationError(ErrorCode.InternalError, $"{message} {exception.Message}");
		}
	}

	private static OperationError? MergeBoundaryErrors(OperationError? first, OperationError? second)
	{
		if (first is null) return second;
		if (second is null) return first;
		return new OperationError(ErrorCode.InternalError, $"{first.Message} {second.Message}");
	}

	private sealed record ResolvedScanner(
		DocumentSnapshot<PersistentSceneEntityRecord> Document,
		ScannerEntityState State);

	private sealed class CleanupOperation
	{
		public CleanupOperation(ScannerPilotSession session, string reason)
		{
			Session = session;
			Reason = reason;
			Completion = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		public ScannerPilotSession Session { get; }
		public string Reason { get; }
		public TaskCompletionSource<OperationResult> Completion { get; }
		public OperationError? BoundaryError { get; set; }
	}
}

internal static class ScannerPersistence
{
	public static OperationResult<T> Decode<T>(TypedPayload payload, IPersistedTypeCodec<T> codec) where T : class
	{
		if (payload.TypeId.Value != codec.Key.Value || payload.TypeVersion != codec.CurrentVersion)
			return OperationResult<T>.Failure(ErrorCode.PersistedTypeInvalid,
				$"Expected '{codec.Key}' v{codec.CurrentVersion}.");
		try
		{
			return OperationResult<T>.Success(codec.Deserialize(payload.Data, payload.TypeVersion));
		}
		catch (Exception)
		{
			return OperationResult<T>.Failure(ErrorCode.PersistedTypeInvalid,
				$"Payload '{codec.Key}' is malformed.");
		}
	}

	public static OperationResult Failure(PersistenceError error) =>
		OperationResult.Failure(Map(error.Code), error.Message);

	public static OperationResult<T> Failure<T>(PersistenceError error) =>
		OperationResult<T>.Failure(Map(error.Code), error.Message);

	private static ErrorCode Map(PersistenceErrorCode code) => code switch
	{
		PersistenceErrorCode.NotFound => ErrorCode.NotFound,
		PersistenceErrorCode.AlreadyExists or PersistenceErrorCode.RevisionConflict => ErrorCode.Conflict,
		PersistenceErrorCode.TypeNotRegistered or PersistenceErrorCode.CollectionTypeMismatch => ErrorCode.PersistedTypeInvalid,
		PersistenceErrorCode.InvalidOperation => ErrorCode.InvalidArgument,
		_ => ErrorCode.InternalError
	};
}
