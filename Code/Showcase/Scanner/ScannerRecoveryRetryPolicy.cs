#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;

namespace HL2RP.V2.Showcase.Scanner;

/// <summary>
/// Backoff schedule for maintenance-driven retries of scanner cleanup recovery handles.
/// The handles are the fail-closed record of a cleanup that could not commit; without a
/// runtime consumer they wedge the scanner until process restart. The maintenance tick
/// feeds the live handle set through <see cref="SelectDue"/> and retries whatever comes
/// back. <c>StorageLimitExceeded</c> handles are never selected — that non-retry is
/// deliberate, tested behavior (storage pressure needs an operator, not a loop).
/// </summary>
public sealed class ScannerRecoveryRetryPolicy
{
	public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);
	public static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(2);

	private readonly Dictionary<InteractionSessionId, Schedule> _schedules = new();

	/// <summary>
	/// Returns the handles due for a retry at <paramref name="nowUtc"/>, arming newly
	/// observed handles at <see cref="InitialDelay"/>, doubling the delay of each handle
	/// it selects (capped at <see cref="MaximumDelay"/>), and forgetting schedules whose
	/// handles have healed so a later failure starts a fresh backoff.
	/// </summary>
	public IReadOnlyList<ScannerCleanupRecoveryHandle> SelectDue(
		IReadOnlyList<ScannerCleanupRecoveryHandle> handles,
		DateTimeOffset nowUtc)
	{
		ArgumentNullException.ThrowIfNull(handles);
		var live = new HashSet<InteractionSessionId>();
		var due = new List<ScannerCleanupRecoveryHandle>();
		foreach (var handle in handles)
		{
			live.Add(handle.SessionId);
			if (handle.LastError.Code == ErrorCode.StorageLimitExceeded) continue;
			if (!_schedules.TryGetValue(handle.SessionId, out var schedule))
			{
				_schedules[handle.SessionId] = new Schedule
				{
					Delay = InitialDelay,
					NextAttemptAtUtc = nowUtc + InitialDelay
				};
				continue;
			}
			if (nowUtc < schedule.NextAttemptAtUtc) continue;
			due.Add(handle);
			schedule.Delay = TimeSpan.FromTicks(Math.Min(schedule.Delay.Ticks * 2, MaximumDelay.Ticks));
			schedule.NextAttemptAtUtc = nowUtc + schedule.Delay;
		}
		foreach (var healed in _schedules.Keys.Where(id => !live.Contains(id)).ToArray())
			_schedules.Remove(healed);
		return due;
	}

	private sealed class Schedule
	{
		public TimeSpan Delay { get; set; }
		public DateTimeOffset NextAttemptAtUtc { get; set; }
	}
}
