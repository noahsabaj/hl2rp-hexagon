#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace HL2RP.V2.Runtime;

internal enum HL2RPTimedActionCancelOutcome
{
	NotFound,
	Cancelled,
	CommitOwned
}

internal readonly record struct HL2RPTimedActionCompletion(
	bool Owned,
	bool PublishFailure,
	bool LifecycleCleanupRequested );

/// <summary>
/// Serializes cancellation, lifecycle invalidation, and the durable commit
/// boundary for one kind of timed action. A pending action may be cancelled;
/// once commit ownership is claimed, cancellation can suppress transport but
/// cannot remove the action or invalidate its eventual receipt.
/// </summary>
internal sealed class HL2RPTimedActionOwnership<TKey, TAction>
	where TKey : notnull
	where TAction : class
{
	private readonly object _sync = new();
	private readonly Dictionary<TKey, Entry> _entries = new();

	public bool TryAdd( TKey key, TAction action )
	{
		ArgumentNullException.ThrowIfNull( action );
		lock ( _sync ) return _entries.TryAdd( key, new Entry( action ) );
	}

	public bool TryGet( TKey key, [NotNullWhen( true )] out TAction? action )
	{
		lock ( _sync )
		{
			if ( _entries.TryGetValue( key, out var entry ) )
			{
				action = entry.Action;
				return true;
			}
			action = null;
			return false;
		}
	}

	public bool Contains( TKey key )
	{
		lock ( _sync ) return _entries.ContainsKey( key );
	}

	public bool TryClaimCommit( TKey key, TAction action )
	{
		ArgumentNullException.ThrowIfNull( action );
		lock ( _sync )
		{
			if ( !_entries.TryGetValue( key, out var entry ) ||
				!ReferenceEquals( entry.Action, action ) ) return false;
			if ( entry.Phase == TimedActionPhase.Committing ) return true;
			if ( entry.Phase != TimedActionPhase.Pending ) return false;
			entry.Phase = TimedActionPhase.Committing;
			return true;
		}
	}

	public HL2RPTimedActionCancelOutcome TryCancel(
		TKey key,
		Func<TAction, bool> matches,
		out TAction? action )
	{
		ArgumentNullException.ThrowIfNull( matches );
		lock ( _sync )
		{
			if ( !_entries.TryGetValue( key, out var entry ) || !matches( entry.Action ) )
			{
				action = null;
				return HL2RPTimedActionCancelOutcome.NotFound;
			}
			if ( entry.Phase == TimedActionPhase.Committing )
			{
				action = null;
				return HL2RPTimedActionCancelOutcome.CommitOwned;
			}
			if ( entry.Phase != TimedActionPhase.Pending )
			{
				action = null;
				return HL2RPTimedActionCancelOutcome.NotFound;
			}
			entry.Phase = TimedActionPhase.Cancelled;
			_entries.Remove( key );
			action = entry.Action;
			return HL2RPTimedActionCancelOutcome.Cancelled;
		}
	}

	public TAction[] CancelForLifecycle( TKey key, Func<TAction, bool>? matches = null )
	{
		lock ( _sync )
		{
			if ( !_entries.TryGetValue( key, out var entry ) ||
				matches is not null && !matches( entry.Action ) ) return Array.Empty<TAction>();
			return CancelForLifecycleLocked( key, entry );
		}
	}

	public TAction[] CancelAllForLifecycle()
	{
		lock ( _sync )
		{
			var cancelled = new List<TAction>();
			foreach ( var pair in new List<KeyValuePair<TKey, Entry>>( _entries ) )
				cancelled.AddRange( CancelForLifecycleLocked( pair.Key, pair.Value ) );
			return cancelled.ToArray();
		}
	}

	public HL2RPTimedActionCompletion Complete( TKey key, TAction action, bool durableSuccess )
	{
		ArgumentNullException.ThrowIfNull( action );
		lock ( _sync )
		{
			if ( !_entries.TryGetValue( key, out var entry ) ||
				!ReferenceEquals( entry.Action, action ) ) return default;
			entry.Phase = TimedActionPhase.Completed;
			_entries.Remove( key );
			return new HL2RPTimedActionCompletion(
				true,
				!durableSuccess && !entry.SuppressDirectPublication,
				entry.LifecycleCleanupRequested );
		}
	}

	public TAction[] SnapshotValues()
	{
		lock ( _sync )
		{
			var values = new TAction[_entries.Count];
			var index = 0;
			foreach ( var entry in _entries.Values ) values[index++] = entry.Action;
			return values;
		}
	}

	private TAction[] CancelForLifecycleLocked( TKey key, Entry entry )
	{
		if ( entry.Phase == TimedActionPhase.Pending )
		{
			entry.Phase = TimedActionPhase.Cancelled;
			_entries.Remove( key );
			return new[] { entry.Action };
		}
		if ( entry.Phase == TimedActionPhase.Committing )
		{
			entry.SuppressDirectPublication = true;
			entry.LifecycleCleanupRequested = true;
		}
		return Array.Empty<TAction>();
	}

	private enum TimedActionPhase
	{
		Pending,
		Committing,
		Cancelled,
		Completed
	}

	private sealed class Entry
	{
		public Entry( TAction action ) => Action = action;
		public TAction Action { get; }
		public TimedActionPhase Phase { get; set; }
		public bool SuppressDirectPublication { get; set; }
		public bool LifecycleCleanupRequested { get; set; }
	}
}
