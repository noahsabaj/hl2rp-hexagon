#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Client;
using Hexagon.V2.Kernel;

namespace HL2RP.UI;

/// <summary>
/// Owns the authenticated client request that releases the active character.
/// The UI can disable its return action while the request is in flight, and
/// duplicate input cannot emit a second host command.
/// </summary>
public sealed class CharacterSessionReturnCoordinator
{
	private readonly IHexClientController _controller;
	private int _busy;

	public CharacterSessionReturnCoordinator( IHexClientController controller ) =>
		_controller = controller ?? throw new ArgumentNullException( nameof(controller) );

	public bool IsBusy => Volatile.Read( ref _busy ) != 0;

	public async ValueTask<OperationResult> ReturnToCharacterSelectionAsync(
		CancellationToken cancellationToken = default )
	{
		if ( Interlocked.CompareExchange( ref _busy, 1, 0 ) != 0 )
			return OperationResult.Failure(
				ErrorCode.Conflict,
				"A return to character selection is already in progress." );

		try
		{
			return await _controller.UnloadCharacterAsync( cancellationToken );
		}
		catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested )
		{
			return OperationResult.Failure(
				ErrorCode.Conflict,
				"The return to character selection was cancelled." );
		}
		catch ( Exception )
		{
			return OperationResult.Failure(
				ErrorCode.InternalError,
				"The return to character selection failed unexpectedly." );
		}
		finally
		{
			Volatile.Write( ref _busy, 0 );
		}
	}
}
