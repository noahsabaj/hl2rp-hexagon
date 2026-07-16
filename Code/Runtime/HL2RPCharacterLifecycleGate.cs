#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;

namespace HL2RP.V2.Runtime;

/// <summary>
/// Per-connection last-intent-wins fence for asynchronous character lifecycle work.
/// Opaque operation epochs prevent same-ID reconnect ABA after Close/Open.
/// </summary>
public sealed class HL2RPCharacterLifecycleGate
{
	private readonly object _sync = new();
	private readonly Dictionary<ConnectionId, Guid> _current = new();
	private readonly Dictionary<ConnectionId, Guid> _operations = new();
	private readonly Dictionary<CharacterId, CharacterReservationState> _characterReservations = new();

	public void Open( ConnectionId connectionId )
	{
		lock ( _sync )
		{
			RemoveConnectionState( connectionId );
			_current[connectionId] = Guid.NewGuid();
		}
	}

	public bool IsCurrent( ConnectionId connectionId, Guid epoch )
	{
		lock ( _sync )
			return epoch != Guid.Empty && _current.TryGetValue( connectionId, out var current ) && current == epoch;
	}

	/// <summary>
	/// Begins one exclusive lifecycle operation for a live connection and, when a
	/// target is supplied, reserves that character across its durable portion.
	/// Rejecting overlap keeps an older operation from committing after a newer
	/// same-connection request and prevents cross-connection duplicate loads.
	/// </summary>
	public bool TryBeginOperation(
		ConnectionId connectionId,
		CharacterId? characterId,
		out Guid lifecycleEpoch,
		out IDisposable? operation )
	{
		lock ( _sync )
		{
			lifecycleEpoch = Guid.Empty;
			operation = null;
			if ( !_current.ContainsKey( connectionId ) || _operations.ContainsKey( connectionId ) )
				return false;
			if ( characterId is CharacterId target && _characterReservations.ContainsKey( target ) )
				return false;

			var token = Guid.NewGuid();
			lifecycleEpoch = Guid.NewGuid();
			_current[connectionId] = lifecycleEpoch;
			_operations[connectionId] = token;
			if ( characterId is CharacterId reservedCharacter )
				_characterReservations[reservedCharacter] = new CharacterReservationState(
					connectionId, token );
			operation = new OperationLease( this, connectionId, characterId, token );
			return true;
		}
	}

	public void Close( ConnectionId connectionId )
	{
		lock ( _sync ) RemoveConnectionState( connectionId );
	}

	private void RemoveConnectionState( ConnectionId connectionId )
	{
		_current.Remove( connectionId );
		_operations.Remove( connectionId );
		foreach ( var characterId in _characterReservations
			.Where( pair => pair.Value.ConnectionId == connectionId )
			.Select( pair => pair.Key )
			.ToArray() )
			_characterReservations.Remove( characterId );
	}

	private void ReleaseOperation(
		ConnectionId connectionId,
		CharacterId? characterId,
		Guid token )
	{
		lock ( _sync )
		{
			if ( _operations.TryGetValue( connectionId, out var currentOperation ) &&
				currentOperation == token ) _operations.Remove( connectionId );
			if ( characterId is CharacterId target &&
				_characterReservations.TryGetValue( target, out var currentCharacter ) &&
				currentCharacter.Token == token ) _characterReservations.Remove( target );
		}
	}

	private readonly record struct CharacterReservationState(
		ConnectionId ConnectionId,
		Guid Token );

	private sealed class OperationLease : IDisposable
	{
		private HL2RPCharacterLifecycleGate? _owner;
		private readonly ConnectionId _connectionId;
		private readonly CharacterId? _characterId;
		private readonly Guid _token;

		public OperationLease(
			HL2RPCharacterLifecycleGate owner,
			ConnectionId connectionId,
			CharacterId? characterId,
			Guid token )
		{
			_owner = owner;
			_connectionId = connectionId;
			_characterId = characterId;
			_token = token;
		}

		public void Dispose()
		{
			var owner = System.Threading.Interlocked.Exchange( ref _owner, null );
			owner?.ReleaseOperation( _connectionId, _characterId, _token );
		}
	}
}
