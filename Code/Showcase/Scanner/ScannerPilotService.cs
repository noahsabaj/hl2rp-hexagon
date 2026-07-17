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

public enum ScannerInputDurability
{
	Pending = 0,
	Persisted = 1
}

public sealed record ScannerInputReceipt(
	long Sequence,
	ScannerMotionCommand Motion,
	ScannerInputDurability Durability,
	CommitReceipt? Commit,
	OperationError? BoundaryError = null)
{
	public long? CommitSequence => Commit?.Sequence;
}

public sealed record ScannerInputPersistenceReceipt(
	ScannerPilotSession Session,
	long Sequence,
	long CommitSequence,
	CommitReceipt Commit) : IHL2RPCommittedOperation;
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

public sealed record ScannerCleanupRecoveryHandle(
	InteractionSessionId SessionId,
	InventoryActor Actor,
	SceneEntityId ScannerId,
	string Reason,
	int Attempts,
	OperationError LastError,
	DateTimeOffset CreatedAtUtc,
	OperationError? BoundaryError = null);

public sealed record ScannerInputFlushFailure(
	InteractionSessionId SessionId,
	SceneEntityId ScannerId,
	long Sequence,
	OperationError Error);

public sealed record ScannerCleanupDrainResult(
	IReadOnlyList<ScannerCleanupRecoveryHandle> RecoveryHandles,
	IReadOnlyList<ScannerInputFlushFailure> InputFlushFailures)
{
	public bool Succeeded => RecoveryHandles.Count == 0 && InputFlushFailures.Count == 0;
}

/// <summary>
/// Synchronous post-commit boundary for lifecycle cleanup that has no command result to carry its
/// provider-issued receipt. Implementations must not throw; the persistence commit is already durable.
/// </summary>
public interface IScannerCommitSink
{
	void Observe(ScannerInputPersistenceReceipt receipt);
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
	/// <summary>
	/// Accepted input updates replay state immediately and may lag durable storage by at most this
	/// interval during normal operation. A crash can lose that final sequence window; startup
	/// reconciliation clears scanner sessions that did not survive the process.
	/// </summary>
	public static readonly TimeSpan InputWriteBehindInterval = TimeSpan.FromMilliseconds(250);
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
	private readonly Func<TimeSpan, CancellationToken, Task> _cleanupDelay;
	private readonly Func<TimeSpan, CancellationToken, Task> _inputFlushDelay;
	private readonly Dictionary<InteractionSessionId, ScannerPilotSession> _active = new();
	private readonly object _cleanupSync = new();
	private readonly HashSet<InteractionSessionId> _terminating = new();
	private readonly Dictionary<InteractionSessionId, CleanupOperation> _cleanupOperations = new();
	private readonly Dictionary<InteractionSessionId, ScannerCleanupRecoveryHandle> _recoveryHandles = new();
	private readonly Dictionary<InteractionSessionId, InputWriteBehindState> _inputWriteBehind = new();
	private long _nextInputWriteGeneration;

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
		Func<TimeSpan, CancellationToken, Task>? cleanupDelay = null,
		Func<TimeSpan, CancellationToken, Task>? inputFlushDelay = null)
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
		_cleanupDelay = cleanupDelay ?? ((duration, cancellationToken) =>
			Task.Delay(duration, cancellationToken));
		_inputFlushDelay = inputFlushDelay ?? ((duration, cancellationToken) =>
			Task.Delay(duration, cancellationToken));
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

	public IReadOnlyList<ScannerCleanupRecoveryHandle> RecoveryHandles
	{
		get
		{
			lock (_cleanupSync) return Array.AsReadOnly(_recoveryHandles.Values.ToArray());
		}
	}

	public int PendingRecoveryCount
	{
		get
		{
			lock (_cleanupSync) return _recoveryHandles.Count;
		}
	}

	public int PendingInputWriteCount
	{
		get
		{
			lock (_cleanupSync) return _inputWriteBehind.Values.Count(value => value.HasPending);
		}
	}

	public int ScheduledInputFlushCount
	{
		get
		{
			lock (_cleanupSync) return _inputWriteBehind.Values.Count(value => value.Scheduled is not null);
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

	public async ValueTask<ScannerCleanupDrainResult> DrainCleanupAsync(
		CancellationToken cancellationToken = default)
	{
		var inputFlushFailures = await DrainInputWriteBehindAsync(cancellationToken);
		while (true)
		{
			Task<OperationResult>[] pending;
			lock (_cleanupSync)
				pending = _cleanupOperations.Values.Select(value => value.Completion.Task).ToArray();
			if (pending.Length == 0) break;
			foreach (var cleanup in pending)
				await AwaitCleanupAsync(cleanup, cancellationToken);
		}

		lock (_cleanupSync)
			return new ScannerCleanupDrainResult(
				Array.AsReadOnly(_recoveryHandles.Values.ToArray()),
				inputFlushFailures);
	}

	public ValueTask<OperationResult> RetryCleanupAsync(
		ScannerCleanupRecoveryHandle recovery,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(recovery);
		cancellationToken.ThrowIfCancellationRequested();
		CleanupOperation operation;
		lock (_cleanupSync)
		{
			if (_cleanupOperations.TryGetValue(recovery.SessionId, out var existing))
				return new ValueTask<OperationResult>(existing.Completion.Task);
			if (!_recoveryHandles.TryGetValue(recovery.SessionId, out var current) || current != recovery)
				return ValueTask.FromResult(OperationResult.Failure(
					ErrorCode.Conflict, "Scanner cleanup recovery handle is stale."));
			if (!_active.TryGetValue(recovery.SessionId, out var active) ||
				active.Actor != recovery.Actor || active.ScannerId != recovery.ScannerId)
				return ValueTask.FromResult(OperationResult.Failure(
					ErrorCode.NotFound, "Scanner cleanup recovery session is no longer addressable."));

			operation = new CleanupOperation(
				active,
				recovery.Reason,
				cancellationToken,
				recovery.CreatedAtUtc)
			{
				BoundaryError = recovery.BoundaryError
			};
			_recoveryHandles.Remove(recovery.SessionId);
			_cleanupOperations.Add(recovery.SessionId, operation);
		}

		_ = CompleteCleanupAsync(operation);
		return new ValueTask<OperationResult>(operation.Completion.Task);
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

	public ValueTask<OperationResult<ScannerInputReceipt>> ApplyInputAsync(
		InventoryActor actor,
		ScannerInputIntent intent,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(ApplyInput(actor, intent, cancellationToken));

	private OperationResult<ScannerInputReceipt> ApplyInput(
		InventoryActor actor,
		ScannerInputIntent intent,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(intent);
		cancellationToken.ThrowIfCancellationRequested();
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
		cancellationToken.ThrowIfCancellationRequested();
		InputFlushOperation? scheduled = null;
		var accepted = false;
		var cleanupRequired = false;
		ScannerPilotSession? current = null;
		OperationError? acceptanceError = null;
		OperationError? boundaryError = null;
		lock (_cleanupSync)
		{
			if (_terminating.Contains(intent.SessionId) ||
				!_active.TryGetValue(intent.SessionId, out current) ||
				current.Actor != actor ||
				!continued.Value.IsCurrent())
			{
				cleanupRequired = true;
				acceptanceError = new OperationError(
					ErrorCode.Unauthorized,
					"Scanner session changed before input could be accepted.");
			}
			else if (intent.Sequence <= current.LastAcceptedSequence)
				acceptanceError = new OperationError(
					ErrorCode.Unauthorized,
					"Scanner input sequence is stale or out of order.");
			else if (current.LastInputAtUtc is DateTimeOffset currentLast &&
				now - currentLast < MinimumInputInterval)
				acceptanceError = new OperationError(
					ErrorCode.PolicyDenied,
					"Scanner input rate exceeds 20 Hz.");
			else
			{
				accepted = true;
				_active[intent.SessionId] = current! with
				{
					LastAcceptedSequence = intent.Sequence,
					LastInputAtUtc = now
				};
				scheduled = QueueInputWriteUnsafe(intent.SessionId, intent.Sequence);
				boundaryError = ObserveBoundary(
					() => _motion.Apply(active.Value.ScannerId, normalized.Value),
					"Scanner input was accepted, but world motion failed.");
			}
		}
		if (!accepted)
		{
			if (cleanupRequired)
				_ = BeginCleanup(active.Value, "post_commit_session_revoked");
			return OperationResult<ScannerInputReceipt>.Failure(
				acceptanceError!.Code,
				acceptanceError.Message);
		}
		if (scheduled is not null) StartInputFlush(scheduled);
		return OperationResult<ScannerInputReceipt>.Success(new ScannerInputReceipt(
			intent.Sequence,
			normalized.Value,
			ScannerInputDurability.Pending,
			null,
			boundaryError));
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
		var pendingInput = CapturePendingInput(sessionId);
		var acceptedSequence = Math.Max(
			scanner.Value.State.LastAcceptedInputSequence,
			pendingInput?.Sequence ?? active.Value.LastAcceptedSequence);
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
				scanner.Value.State with
				{
					LastAcceptedInputSequence = acceptedSequence,
					SpotlightEnabled = enabled
				})
		});
		unitOfWork.Save(editor);
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		if (!committed.Succeeded) return ScannerPersistence.Failure<ScannerSpotlightReceipt>(committed.Error!);
		if (pendingInput is not null)
			MarkInputPersisted(sessionId, pendingInput.Generation, acceptedSequence);
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
		var pendingInput = CapturePendingInput(sessionId);
		var acceptedSequence = Math.Max(
			scanner.Value.State.LastAcceptedInputSequence,
			pendingInput?.Sequence ?? active.Value.LastAcceptedSequence);
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
			LastAcceptedInputSequence = acceptedSequence,
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
		if (pendingInput is not null)
			MarkInputPersisted(sessionId, pendingInput.Generation, acceptedSequence);
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

	private InputFlushOperation? QueueInputWriteUnsafe(
		InteractionSessionId sessionId,
		long sequence)
	{
		if (!_inputWriteBehind.TryGetValue(sessionId, out var state))
		{
			state = new InputWriteBehindState();
			_inputWriteBehind.Add(sessionId, state);
		}
		state.Generation = checked(++_nextInputWriteGeneration);
		state.Sequence = sequence;
		state.HasPending = true;
		if (state.Scheduled is not null) return null;
		return CreateInputFlushUnsafe(sessionId, state);
	}

	private InputFlushOperation CreateInputFlushUnsafe(
		InteractionSessionId sessionId,
		InputWriteBehindState state)
	{
		var operation = new InputFlushOperation(sessionId, state.Generation);
		state.Scheduled = operation;
		return operation;
	}

	private void StartInputFlush(InputFlushOperation operation) =>
		_ = CompleteInputFlushAsync(operation);

	private async Task CompleteInputFlushAsync(InputFlushOperation operation)
	{
		InputFlushAttempt attempt;
		try
		{
			await _inputFlushDelay(InputWriteBehindInterval, operation.Cancellation.Token);
			attempt = await FlushPendingInputOnceAsync(
				operation.SessionId, operation.Cancellation.Token);
		}
		catch (OperationCanceledException)
		{
			attempt = InputFlushAttempt.Canceled(operation.ScheduledGeneration);
		}
		catch (Exception exception)
		{
			attempt = InputFlushAttempt.Failure(
				operation.ScheduledGeneration,
				0,
				OperationResult.Failure(
					ErrorCode.InternalError,
					$"Scanner input write-behind failed unexpectedly: {exception.Message}"));
		}

		InputFlushOperation? next = null;
		lock (_cleanupSync)
		{
			if (_inputWriteBehind.TryGetValue(operation.SessionId, out var state) &&
				ReferenceEquals(state.Scheduled, operation))
			{
				state.Scheduled = null;
				var hasNewerInput = state.HasPending && state.Generation > attempt.CapturedGeneration;
				if (state.HasPending && !_terminating.Contains(operation.SessionId) &&
					!attempt.WasCanceled && (attempt.Result.Succeeded || hasNewerInput))
					next = CreateInputFlushUnsafe(operation.SessionId, state);
				else if (!state.HasPending)
					_inputWriteBehind.Remove(operation.SessionId);
			}
		}
		operation.Cancellation.Dispose();
		operation.Completion.TrySetResult(attempt.Result);
		if (next is not null) StartInputFlush(next);
	}

	private async Task<InputFlushAttempt> FlushPendingInputOnceAsync(
		InteractionSessionId sessionId,
		CancellationToken cancellationToken)
	{
		PendingInputSnapshot pending;
		ScannerPilotSession active;
		lock (_cleanupSync)
		{
			if (!_inputWriteBehind.TryGetValue(sessionId, out var state) || !state.HasPending)
				return InputFlushAttempt.Success(0, 0, null);
			pending = new PendingInputSnapshot(state.Generation, state.Sequence);
			if (_terminating.Contains(sessionId))
				return InputFlushAttempt.Success(pending.Generation, pending.Sequence, null);
			if (!_active.TryGetValue(sessionId, out active!))
				return InputFlushAttempt.Failure(
					pending.Generation,
					pending.Sequence,
					OperationResult.Failure(
						ErrorCode.NotFound,
						"Scanner input write-behind session is no longer addressable."));
		}

		cancellationToken.ThrowIfCancellationRequested();
		var continued = Continue(active);
		if (continued.Failed)
			return InputFlushAttempt.Failure(
				pending.Generation,
				pending.Sequence,
				OperationResult.Failure(continued.Error!.Code, continued.Error.Message));
		var scanner = ResolveState(active.ScannerId);
		if (scanner.Failed)
			return InputFlushAttempt.Failure(
				pending.Generation,
				pending.Sequence,
				OperationResult.Failure(scanner.Error!.Code, scanner.Error.Message));
		if (scanner.Value.State.PilotCharacterId != active.Actor.CharacterId)
		{
			RemoveInputWriteBehind(sessionId);
			return InputFlushAttempt.Success(pending.Generation, pending.Sequence, null);
		}
		if (scanner.Value.State.LastAcceptedInputSequence >= pending.Sequence)
		{
			MarkInputPersisted(sessionId, pending.Generation, pending.Sequence);
			return InputFlushAttempt.Success(pending.Generation, pending.Sequence, null);
		}

		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Require(continued.Value);
		var editor = unitOfWork.Edit(_repositories.SceneEntities, scanner.Value.Document);
		if (editor is null)
		{
			await HL2RPUnitOfWork.DisposeAsync(unitOfWork);
			return InputFlushAttempt.Failure(
				pending.Generation,
				pending.Sequence,
				OperationResult.Failure(ErrorCode.Conflict, "Scanner state changed during input write-behind."));
		}
		editor.Replace(editor.Value with
		{
			State = HL2RPPersistence.Payload(
				HL2RPPersistence.ScannerState,
				scanner.Value.State with { LastAcceptedInputSequence = pending.Sequence })
		});
		unitOfWork.Save(editor);
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		if (!committed.Succeeded)
			return InputFlushAttempt.Failure(
				pending.Generation,
				pending.Sequence,
				ScannerPersistence.Failure(committed.Error!));

		MarkInputPersisted(sessionId, pending.Generation, pending.Sequence);
		_commitSink.Observe(new ScannerInputPersistenceReceipt(
			active,
			pending.Sequence,
			committed.Value!.Sequence,
			committed.Value));
		return InputFlushAttempt.Success(pending.Generation, pending.Sequence, committed.Value);
	}

	private PendingInputSnapshot? CapturePendingInput(InteractionSessionId sessionId)
	{
		lock (_cleanupSync)
			return _inputWriteBehind.TryGetValue(sessionId, out var state) && state.HasPending
				? new PendingInputSnapshot(state.Generation, state.Sequence)
				: null;
	}

	private void MarkInputPersisted(
		InteractionSessionId sessionId,
		long capturedGeneration,
		long persistedSequence)
	{
		InputFlushOperation? cancel = null;
		lock (_cleanupSync)
		{
			if (!_inputWriteBehind.TryGetValue(sessionId, out var state) || !state.HasPending ||
				state.Generation > capturedGeneration || state.Sequence > persistedSequence)
				return;
			state.HasPending = false;
			cancel = state.Scheduled;
			if (cancel is null) _inputWriteBehind.Remove(sessionId);
		}
		TryCancel(cancel);
	}

	private void CancelScheduledInputFlush(InteractionSessionId sessionId)
	{
		InputFlushOperation? operation;
		lock (_cleanupSync)
			operation = _inputWriteBehind.TryGetValue(sessionId, out var state)
				? state.Scheduled
				: null;
		TryCancel(operation);
	}

	private void RemoveInputWriteBehind(InteractionSessionId sessionId)
	{
		InputFlushOperation? operation;
		lock (_cleanupSync)
		{
			if (!_inputWriteBehind.Remove(sessionId, out var state)) return;
			operation = state.Scheduled;
		}
		TryCancel(operation);
	}

	private OperationResult RemoveInputWriteBehindAndSucceed(InteractionSessionId sessionId)
	{
		RemoveInputWriteBehind(sessionId);
		return OperationResult.Success();
	}

	private async Task<IReadOnlyList<ScannerInputFlushFailure>> DrainInputWriteBehindAsync(
		CancellationToken cancellationToken)
	{
		while (true)
		{
			InputFlushOperation[] scheduled;
			lock (_cleanupSync)
				scheduled = _inputWriteBehind.Values
					.Where(value => value.Scheduled is not null)
					.Select(value => value.Scheduled!)
					.ToArray();
			if (scheduled.Length == 0) break;
			foreach (var operation in scheduled) TryCancel(operation);
			foreach (var operation in scheduled)
				await AwaitCleanupAsync(operation.Completion.Task, cancellationToken);
		}

		InteractionSessionId[] pendingSessions;
		lock (_cleanupSync)
			pendingSessions = _inputWriteBehind
				.Where(value => value.Value.HasPending && !_terminating.Contains(value.Key))
				.Select(value => value.Key)
				.ToArray();
		var failures = new List<ScannerInputFlushFailure>();
		foreach (var sessionId in pendingSessions)
		{
			while (true)
			{
				var pending = CapturePendingInput(sessionId);
				if (pending is null) break;
				var flushed = await FlushPendingInputOnceAsync(sessionId, cancellationToken);
				if (flushed.Result.Failed)
				{
					bool terminating;
					SceneEntityId scannerId;
					lock (_cleanupSync)
					{
						terminating = _terminating.Contains(sessionId);
						scannerId = _active.TryGetValue(sessionId, out var active)
							? active.ScannerId
							: default;
					}
					if (!terminating && scannerId.Value != Guid.Empty)
						failures.Add(new ScannerInputFlushFailure(
							sessionId, scannerId, pending.Sequence, flushed.Result.Error!));
					break;
				}
				var remaining = CapturePendingInput(sessionId);
				if (remaining is null || remaining.Generation <= pending.Generation) break;
			}
		}
		return Array.AsReadOnly(failures.ToArray());
	}

	private static void TryCancel(InputFlushOperation? operation)
	{
		if (operation is null) return;
		try { operation.Cancellation.Cancel(); }
		catch (ObjectDisposedException) { }
	}

	private async ValueTask<OperationResult> ExitCoreAsync(
		ScannerPilotSession active,
		string reason,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		// The durable cleanup runs detached from the caller: a disconnecting pilot's
		// command-lease token could otherwise cancel the cleanup commit mid-flight and
		// convert a routine exit into a permanent recovery handle. The awaiting caller
		// observes its own cancellation through the wrapper while the cleanup continues
		// to completion; shutdown drains any cleanup still pending.
		var cleanup = BeginCleanup(active, reason);
		_authority.Close(active.SessionId);
		return await AwaitCleanupAsync(cleanup, cancellationToken);
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

	private Task<OperationResult> BeginCleanup(
		ScannerPilotSession active,
		string reason)
	{
		CleanupOperation operation;
		var restoreBody = false;
		lock (_cleanupSync)
		{
			if (_cleanupOperations.TryGetValue(active.SessionId, out var existing))
				return existing.Completion.Task;
			if (_recoveryHandles.TryGetValue(active.SessionId, out var recovery))
				return Task.FromResult(OperationResult.Failure(
					recovery.LastError.Code,
					recovery.LastError.Message,
					recovery.LastError.Details));
			restoreBody = _terminating.Add(active.SessionId);
			// Durability-critical work never inherits a caller token; the host drains
			// pending cleanups at shutdown, so nothing needs to cancel them mid-commit.
			operation = new CleanupOperation(active, reason, CancellationToken.None, null);
			_cleanupOperations.Add(active.SessionId, operation);
		}
		CancelScheduledInputFlush(active.SessionId);

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
		CleanupAttemptResult outcome;
		try
		{
			outcome = await ClearPersistedPilotWithRetryAsync(
				operation.Session,
				operation.Reason,
				operation.BoundaryError,
				operation.CancellationToken);
		}
		catch (Exception exception)
		{
			outcome = new CleanupAttemptResult(OperationResult.Failure(
				ErrorCode.InternalError,
				$"Scanner pilot cleanup failed before its retry round completed: {exception.Message}"), 0);
		}
		var result = outcome.Result;

		lock (_cleanupSync)
		{
			if (result.Succeeded)
			{
				_active.Remove(operation.Session.SessionId);
				_terminating.Remove(operation.Session.SessionId);
				_recoveryHandles.Remove(operation.Session.SessionId);
			}
			else
				_recoveryHandles[operation.Session.SessionId] = new ScannerCleanupRecoveryHandle(
					operation.Session.SessionId,
					operation.Session.Actor,
					operation.Session.ScannerId,
					operation.Reason,
					outcome.Attempts,
					result.Error!,
					operation.RecoveryCreatedAtUtc ?? _clock.UtcNow,
					operation.BoundaryError);
			_cleanupOperations.Remove(operation.Session.SessionId);
		}
		operation.Completion.TrySetResult(result);
	}

	private bool CanRetryCleanup(OperationResult result)
	{
		// StaleTransaction is routine and transient: a unit of work opened while a
		// tombstone-reclaiming checkpoint was in flight commits after the generation
		// bump, and the retry re-begins the unit under the fresh generation.
		if (result.Error!.Code is ErrorCode.Conflict or ErrorCode.StaleTransaction) return true;
		return result.Error.Code == ErrorCode.InternalError &&
			_repositories.Provider.Health.Status != PersistenceHealthStatus.Fatal;
	}

	private async Task<CleanupAttemptResult> ClearPersistedPilotWithRetryAsync(
		ScannerPilotSession active,
		string reason,
		OperationError? boundaryError,
		CancellationToken cancellationToken)
	{
		OperationResult result = OperationResult.Failure(ErrorCode.Conflict, "Scanner cleanup did not run.");
		for (var attempt = 1; attempt <= CleanupMaximumAttempts; attempt++)
		{
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				result = await ClearPersistedPilotOnceAsync(
					active, reason, boundaryError, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				return new CleanupAttemptResult(OperationResult.Failure(
					ErrorCode.InternalError,
					"Scanner pilot cleanup was canceled before durable recovery completed."), attempt);
			}
			catch (Exception exception)
			{
				result = OperationResult.Failure(ErrorCode.InternalError,
					$"Scanner pilot cleanup failed unexpectedly: {exception.Message}");
			}

			if (result.Succeeded || !CanRetryCleanup(result) || attempt == CleanupMaximumAttempts)
				return new CleanupAttemptResult(result, attempt);
			try
			{
				await _cleanupDelay(CleanupRetryDelay, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				return new CleanupAttemptResult(OperationResult.Failure(
					ErrorCode.InternalError,
					"Scanner pilot cleanup retry delay was canceled before durable recovery completed."), attempt);
			}
			catch (Exception exception)
			{
				return new CleanupAttemptResult(OperationResult.Failure(
					ErrorCode.InternalError,
					$"Scanner pilot cleanup retry delay failed: {exception.Message}"), attempt);
			}
		}
		return new CleanupAttemptResult(result, CleanupMaximumAttempts);
	}

	private async Task<OperationResult> ClearPersistedPilotOnceAsync(
		ScannerPilotSession active,
		string reason,
		OperationError? boundaryError,
		CancellationToken cancellationToken)
	{
		var scanner = ResolveState(active.ScannerId);
		if (scanner.Failed)
			return scanner.Error!.Code == ErrorCode.NotFound
				? RemoveInputWriteBehindAndSucceed(active.SessionId)
				: OperationResult.Failure(scanner.Error.Code, scanner.Error.Message);
		if (scanner.Value.State.PilotCharacterId is null ||
			scanner.Value.State.PilotCharacterId != active.Actor.CharacterId)
			return RemoveInputWriteBehindAndSucceed(active.SessionId);
		var pendingInput = CapturePendingInput(active.SessionId);
		var acceptedSequence = Math.Max(
			scanner.Value.State.LastAcceptedInputSequence,
			pendingInput?.Sequence ?? active.LastAcceptedSequence);
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
				scanner.Value.State with
				{
					PilotCharacterId = null,
					SpotlightEnabled = false,
					LastAcceptedInputSequence = acceptedSequence
				})
		});
		unitOfWork.Save(editor);
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync(unitOfWork, cancellationToken);
		if (!committed.Succeeded) return ScannerPersistence.Failure(committed.Error!);
		RemoveInputWriteBehind(active.SessionId);
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

	private static async Task<OperationResult> AwaitCleanupAsync(
		Task<OperationResult> cleanup,
		CancellationToken cancellationToken)
	{
		if (!cancellationToken.CanBeCanceled || cleanup.IsCompleted)
			return await cleanup;

		var completed = new TaskCompletionSource<OperationResult>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = cancellationToken.Register(
			static state => ((TaskCompletionSource<OperationResult>)state!).TrySetCanceled(),
			completed);
		_ = ObserveCleanupCompletionAsync(cleanup, completed);
		return await completed.Task;
	}

	private static async Task ObserveCleanupCompletionAsync(
		Task<OperationResult> cleanup,
		TaskCompletionSource<OperationResult> completion)
	{
		try { completion.TrySetResult(await cleanup); }
		catch (OperationCanceledException) { completion.TrySetCanceled(); }
		catch (Exception exception) { completion.TrySetException(exception); }
	}

	private sealed record ResolvedScanner(
		DocumentSnapshot<PersistentSceneEntityRecord> Document,
		ScannerEntityState State);

	private sealed record PendingInputSnapshot(long Generation, long Sequence);

	private sealed record InputFlushAttempt(
		OperationResult Result,
		long CapturedGeneration,
		long CapturedSequence,
		CommitReceipt? Commit,
		bool WasCanceled)
	{
		public static InputFlushAttempt Success(
			long generation,
			long sequence,
			CommitReceipt? commit) =>
			new(OperationResult.Success(), generation, sequence, commit, false);

		public static InputFlushAttempt Failure(
			long generation,
			long sequence,
			OperationResult result) =>
			new(result, generation, sequence, null, false);

		public static InputFlushAttempt Canceled(long generation) =>
			new(OperationResult.Success(), generation, 0, null, true);
	}

	private sealed class InputWriteBehindState
	{
		public long Generation { get; set; }
		public long Sequence { get; set; }
		public bool HasPending { get; set; }
		public InputFlushOperation? Scheduled { get; set; }
	}

	private sealed class InputFlushOperation
	{
		public InputFlushOperation(InteractionSessionId sessionId, long scheduledGeneration)
		{
			SessionId = sessionId;
			ScheduledGeneration = scheduledGeneration;
			Cancellation = new CancellationTokenSource();
			Completion = new TaskCompletionSource<OperationResult>(
				TaskCreationOptions.RunContinuationsAsynchronously);
		}

		public InteractionSessionId SessionId { get; }
		public long ScheduledGeneration { get; }
		public CancellationTokenSource Cancellation { get; }
		public TaskCompletionSource<OperationResult> Completion { get; }
	}

	private sealed record CleanupAttemptResult(OperationResult Result, int Attempts);

	private sealed class CleanupOperation
	{
		public CleanupOperation(
			ScannerPilotSession session,
			string reason,
			CancellationToken cancellationToken,
			DateTimeOffset? recoveryCreatedAtUtc)
		{
			Session = session;
			Reason = reason;
			CancellationToken = cancellationToken;
			RecoveryCreatedAtUtc = recoveryCreatedAtUtc;
			Completion = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		public ScannerPilotSession Session { get; }
		public string Reason { get; }
		public CancellationToken CancellationToken { get; }
		public DateTimeOffset? RecoveryCreatedAtUtc { get; }
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
		PersistenceErrorCode.InvariantViolation => ErrorCode.InvariantViolation,
		PersistenceErrorCode.LeaseUnavailable => ErrorCode.LeaseUnavailable,
		PersistenceErrorCode.StorageLimitExceeded => ErrorCode.StorageLimitExceeded,
		PersistenceErrorCode.StaleTransaction => ErrorCode.StaleTransaction,
		_ => ErrorCode.InternalError
	};
}
