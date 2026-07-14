#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Client;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;
using HL2RP.UI;

namespace HL2RP.V2.Tests.UI;

[TestClass]
public sealed class CharacterSessionReturnCoordinatorTests
{
	[TestMethod]
	public async Task ActiveRequestCallsAuthenticatedControllerUnloadOnceAndExposesBusyState()
	{
		var completion = new TaskCompletionSource<OperationResult>( TaskCreationOptions.RunContinuationsAsynchronously );
		var controller = new UnloadSpyController( _ => new ValueTask<OperationResult>( completion.Task ) );
		var coordinator = new CharacterSessionReturnCoordinator( controller );

		var pending = coordinator.ReturnToCharacterSelectionAsync().AsTask();

		Assert.IsTrue( coordinator.IsBusy );
		Assert.AreEqual( 1, controller.UnloadCalls );
		completion.SetResult( OperationResult.Success() );
		Assert.IsTrue( (await pending).Succeeded );
		Assert.IsFalse( coordinator.IsBusy );
	}

	[TestMethod]
	public async Task DuplicateInputIsRejectedWithoutSendingASecondUnloadAndAHostFailureCanBeRetried()
	{
		var firstCompletion = new TaskCompletionSource<OperationResult>( TaskCreationOptions.RunContinuationsAsynchronously );
		var attempt = 0;
		var controller = new UnloadSpyController( _ =>
		{
			attempt++;
			return attempt == 1
				? new ValueTask<OperationResult>( firstCompletion.Task )
				: ValueTask.FromResult( OperationResult.Success() );
		} );
		var coordinator = new CharacterSessionReturnCoordinator( controller );

		var first = coordinator.ReturnToCharacterSelectionAsync().AsTask();
		var duplicate = await coordinator.ReturnToCharacterSelectionAsync();

		Assert.AreEqual( ErrorCode.Conflict, duplicate.Error?.Code );
		Assert.AreEqual( 1, controller.UnloadCalls );
		firstCompletion.SetResult( OperationResult.Failure( ErrorCode.PolicyDenied, "Injected host denial." ) );
		Assert.AreEqual( ErrorCode.PolicyDenied, (await first).Error?.Code );
		Assert.IsFalse( coordinator.IsBusy );

		Assert.IsTrue( (await coordinator.ReturnToCharacterSelectionAsync()).Succeeded );
		Assert.AreEqual( 2, controller.UnloadCalls );
	}

	[TestMethod]
	public async Task UnexpectedControllerFailureBecomesUserSafeErrorAndReleasesBusyState()
	{
		var controller = new UnloadSpyController( _ => throw new InvalidOperationException( "sensitive transport detail" ) );
		var coordinator = new CharacterSessionReturnCoordinator( controller );

		var result = await coordinator.ReturnToCharacterSelectionAsync();

		Assert.AreEqual( ErrorCode.InternalError, result.Error?.Code );
		Assert.IsFalse( result.Error?.Message.Contains( "sensitive", StringComparison.Ordinal ) );
		Assert.IsFalse( coordinator.IsBusy );
		Assert.AreEqual( 1, controller.UnloadCalls );
	}

	[TestMethod]
	public void CharacterEpochResetClearsEveryCharacterScopedPresentationChoice()
	{
		var state = new CharacterSessionPresentationState
		{
			Workspace = ShowcaseWorkspace.Scanner,
			ScoreboardOpen = true,
			Notifications = ImmutableArray.Create( new NotificationViewModel(
				Guid.NewGuid(), "Prior character", "Character-scoped result", NotificationTone.Neutral,
				DateTimeOffset.UtcNow.AddMinutes( 1 ) ) ),
			DismissedItemPresentationSequence = 42,
			SelectedNoteItemId = ItemId.New(),
			SelectedRadioItemId = ItemId.New()
		};

		state.Reset();

		Assert.AreEqual( ShowcaseWorkspace.None, state.Workspace );
		Assert.IsFalse( state.ScoreboardOpen );
		Assert.IsTrue( state.Notifications.IsEmpty );
		Assert.AreEqual( 0, state.DismissedItemPresentationSequence );
		Assert.IsNull( state.SelectedNoteItemId );
		Assert.IsNull( state.SelectedRadioItemId );
	}

	[TestMethod]
	public async Task RealControllerUnloadRouteAppliesNoCharacterEpochAndProjectsCharacterSelection()
	{
		var connectionId = ConnectionId.New();
		var characterId = CharacterId.New();
		var nonce = ClientSessionNonce.New();
		var connectionEpoch = ConnectionEpoch.New();
		var scope = new ClientSessionScope( nonce, connectionEpoch );
		var store = new HexClientStore();
		store.PrepareSession( nonce );
		Assert.IsTrue( store.AcceptHello( new ClientSessionHello( scope ) ) );
		Assert.IsTrue( store.ApplyState( scope, State( connectionEpoch, connectionId, characterId, 1, 1 ) ) );
		var projection = new HL2RPShowcaseProjection();
		var active = projection.Build(
			store, ShowcaseWorkspace.Inventory, true, ImmutableArray<NotificationViewModel>.Empty );
		Assert.IsNotNull( active.Hud );
		Assert.IsNull( active.CharacterMenu );

		var transport = new StateApplyingTransport( command =>
		{
			Assert.IsInstanceOfType<UnloadCharacterCommand>( command );
			Assert.IsTrue( store.ApplyState( scope, State( connectionEpoch, connectionId, null, 2, 2 ) ) );
			return OperationResult.Success();
		} );
		using var controller = new HexClientController( transport );
		var coordinator = new CharacterSessionReturnCoordinator( controller );

		var result = await coordinator.ReturnToCharacterSelectionAsync();
		var selecting = projection.Build(
			store, ShowcaseWorkspace.None, false, ImmutableArray<NotificationViewModel>.Empty );

		Assert.IsTrue( result.Succeeded );
		Assert.HasCount( 1, transport.Commands );
		Assert.IsInstanceOfType<UnloadCharacterCommand>( transport.Commands[0] );
		Assert.AreEqual( ClientLifecycleState.Connected, store.Lifecycle );
		Assert.AreEqual( 2, store.StateEpoch?.Character );
		Assert.IsNull( selecting.Hud );
		Assert.IsNotNull( selecting.CharacterMenu );
	}

	private static ClientStateSnapshot State(
		ConnectionEpoch connectionEpoch,
		ConnectionId connectionId,
		CharacterId? characterId,
		long characterEpoch,
		long revision ) => new(
		new ClientStateEpoch( connectionEpoch, characterEpoch, revision ),
		new PlayerPublicSnapshot(
			connectionId,
			76561198000000001UL,
			"Local Player",
			characterId,
			characterId is null ? string.Empty : "Citizen 40291",
			characterId is null ? string.Empty : "Observable description",
			characterId is null ? null : new DefinitionId( HL2RP.V2.Schema.HL2RPIds.Models.Citizen01 ),
			characterId is null ? null : new FactionId( HL2RP.V2.Schema.HL2RPIds.Factions.Citizen ),
			null,
			false,
			false ),
		characterId is CharacterId activeCharacter
			? new PlayerPrivateSnapshot( activeCharacter, 200, null )
			: null,
		new PlayerRosterSnapshot( revision, Array.Empty<PlayerRosterRowSnapshot>() ),
		null,
		null,
		null );

	private sealed class StateApplyingTransport : IClientCommandTransport
	{
		private readonly Func<ClientCommand, OperationResult> _send;

		public StateApplyingTransport( Func<ClientCommand, OperationResult> send ) => _send = send;

		public List<ClientCommand> Commands { get; } = new();

		public ValueTask<OperationResult> SendAsync(
			ClientCommand command,
			CancellationToken cancellationToken = default )
		{
			cancellationToken.ThrowIfCancellationRequested();
			Commands.Add( command );
			return ValueTask.FromResult( _send( command ) );
		}
	}

	private sealed class UnloadSpyController : IHexClientController
	{
		private readonly Func<CancellationToken, ValueTask<OperationResult>> _unload;

		public UnloadSpyController( Func<CancellationToken, ValueTask<OperationResult>> unload ) =>
			_unload = unload;

		public int UnloadCalls { get; private set; }

		public ValueTask<OperationResult> UnloadCharacterAsync( CancellationToken cancellationToken = default )
		{
			UnloadCalls++;
			return _unload( cancellationToken );
		}

		public ValueTask<OperationResult> RequestCharactersAsync( CancellationToken cancellationToken = default ) => Unexpected();
		public ValueTask<OperationResult> CreateCharacterAsync( CharacterCreationInput input, CancellationToken cancellationToken = default ) => Unexpected();
		public ValueTask<OperationResult> LoadCharacterAsync( CharacterId characterId, CancellationToken cancellationToken = default ) => Unexpected();
		public ValueTask<OperationResult> DeleteCharacterAsync( CharacterId characterId, CancellationToken cancellationToken = default ) => Unexpected();
		public ValueTask<OperationResult> MoveItemAsync( InventoryId sourceId, InventoryId targetId, ItemId itemId, int x, int y, CancellationToken cancellationToken = default ) => Unexpected();
		public ValueTask<OperationResult> MoveItemAsync( InventoryId sourceId, InventoryId targetId, ItemId itemId, InventoryGridPosition position, CancellationToken cancellationToken = default ) => Unexpected();
		public ValueTask<OperationResult> RunItemActionAsync( InventoryId inventoryId, ItemId itemId, ActionId actionId, CancellationToken cancellationToken = default ) => Unexpected();
		public ValueTask<OperationResult> RunItemActionAsync( InventoryId inventoryId, ItemId itemId, ActionId actionId, IReadOnlyDictionary<string, SnapshotValue> arguments, CancellationToken cancellationToken = default ) => Unexpected();
		public ValueTask<OperationResult> DropItemAsync( InventoryId sourceId, ItemId itemId, CancellationToken cancellationToken = default ) => Unexpected();
		public ValueTask<OperationResult> PickUpItemAsync( ItemId itemId, InventoryId destinationId, CancellationToken cancellationToken = default ) => Unexpected();
		public ValueTask<OperationResult> SendChatAsync( string channelId, string text, CancellationToken cancellationToken = default ) => Unexpected();
		public ValueTask<OperationResult> CancelActionAsync( Guid instanceId, CancellationToken cancellationToken = default ) => Unexpected();
		public ValueTask<OperationResult> BeginInteractionAsync( InteractionTargetInput target, CancellationToken cancellationToken = default ) => Unexpected();
		public ValueTask<OperationResult> ContinueInteractionAsync( InteractionSessionId sessionId, InteractionTargetInput target, CancellationToken cancellationToken = default ) => Unexpected();
		public ValueTask<OperationResult> CloseInteractionAsync( InteractionSessionId sessionId, CancellationToken cancellationToken = default ) => Unexpected();
		public ValueTask<OperationResult> RunSchemaCommandAsync( string commandId, IReadOnlyDictionary<string, SnapshotValue>? arguments = null, CancellationToken cancellationToken = default ) => Unexpected();

		private static ValueTask<OperationResult> Unexpected() =>
			throw new AssertFailedException( "The return-to-selection route called an unrelated controller method." );
	}
}
