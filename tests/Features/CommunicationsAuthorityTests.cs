#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Runtime;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Features;

[TestClass]
public sealed class CommunicationsAuthorityTests
{
	[TestMethod]
	public async Task RequestDeliveryOccursExactlyPostCommitAndFailedCommitConsumesNoAdmission()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor();
		var character = environment.Character( actor );
		var firstDevice = Device( powered: true );
		var secondDevice = Device( powered: true );
		var inventory = environment.Inventory( actor.CharacterId, new[]
		{
			new InventoryPlacement( firstDevice.Id, 0, 0 ),
			new InventoryPlacement( secondDevice.Id, 1, 0 )
		} );
		await environment.SeedAsync( unit =>
		{
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unit.Create( environment.Repositories.Items, DomainKeys.Item( firstDevice.Id ), firstDevice );
			unit.Create( environment.Repositories.Items, DomainKeys.Item( secondDevice.Id ), secondDevice );
			unit.Create( environment.Repositories.Inventories, DomainKeys.Inventory( inventory.Id ), inventory );
		} );
		environment.Grant( actor, inventory.Id, InventoryCapability.View | InventoryCapability.Use );

		var officialConnection = ConnectionId.New();
		var officialCharacter = CharacterId.New();
		var bystanderConnection = ConnectionId.New();
		var authorities = new CanonicalChatAuthorityDirectory();
		authorities.Publish( 1, new[]
		{
			Authority( actor.ConnectionId, actor.AccountId, actor.CharacterId, HL2RPIds.Factions.Citizen ),
			Authority( officialConnection, new AccountId( 43 ), officialCharacter,
				HL2RPIds.Factions.CivilProtection, HL2RPIds.Permissions.DispatchChat ),
			Authority( bystanderConnection, new AccountId( 44 ), CharacterId.New(),
				HL2RPIds.Factions.Citizen, HL2RPIds.Permissions.DispatchChat )
		} );
		var liveInventory = new RecordingLiveInventory( new LiveInventorySnapshot(
			1, new[] { Radio( officialCharacter, "100.0", true ) } ) );
		var sink = new RecordingChatSink( () => environment.Provider.Health.Sequence );
		var handler = new RequestChatDeliveryHandler(
			new HL2RPRadioRecipientResolver( authorities ),
			liveInventory,
			sink,
			() => Guid.Parse( "10000000-0000-4000-8000-000000000001" ) );
		var events = new PostCommitEventBus<RequestFact>( new[]
		{
			new EventHandlerRegistration<RequestFact>( "delivery", handler )
		} );
		var service = new RequestDeviceService(
			environment.Repositories,
			environment.Access,
			environment.Clock,
			FeatureTestEnvironment.AllowPolicy(),
			events );

		var first = await service.SendAsync(
			actor, inventory.Id, firstDevice.Id, "  Need assistance  " );
		var multiDeviceBypass = await service.SendAsync(
			actor, inventory.Id, secondDevice.Id, "Second device" );

		Assert.IsTrue( first.Succeeded, first.Error?.Message );
		Assert.AreEqual( ErrorCode.Conflict, multiDeviceBypass.Error!.Code );
		Assert.HasCount( 1, sink.Deliveries );
		Assert.AreEqual( first.Value.CommitSequence, sink.Deliveries[0].ObservedSequence );
		Assert.AreEqual( "Need assistance", sink.Deliveries[0].Delivery.Text );
		CollectionAssert.AreEquivalent(
			new[] { actor.ConnectionId, officialConnection },
			sink.Deliveries[0].Delivery.Recipients.ToArray() );
		Assert.AreEqual( 1, liveInventory.CaptureCount );

		environment.Clock.Advance( RequestDeviceService.RequestChannelRateLimit.RefillPeriod );
		environment.Provider.FailNextCommit();
		var failed = await service.SendAsync(
			actor, inventory.Id, secondDevice.Id, "Commit must fail" );
		var retry = await service.SendAsync(
			actor, inventory.Id, secondDevice.Id, "Retry after failed commit" );

		Assert.IsTrue( failed.Failed );
		Assert.IsTrue( retry.Succeeded, retry.Error?.Message );
		Assert.HasCount( 2, sink.Deliveries );
		Assert.AreEqual( retry.Value.CommitSequence, sink.Deliveries[1].ObservedSequence );
		Assert.AreEqual( 2, liveInventory.CaptureCount );
	}

	[TestMethod]
	public async Task UnpoweredRequestDeviceFailsBeforeCommitAdmissionOrDelivery()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor();
		var character = environment.Character( actor );
		var device = Device( powered: false );
		var inventory = environment.Inventory(
			actor.CharacterId, new[] { new InventoryPlacement( device.Id, 0, 0 ) } );
		await environment.SeedAsync( unit =>
		{
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unit.Create( environment.Repositories.Items, DomainKeys.Item( device.Id ), device );
			unit.Create( environment.Repositories.Inventories, DomainKeys.Inventory( inventory.Id ), inventory );
		} );
		environment.Grant( actor, inventory.Id, InventoryCapability.View | InventoryCapability.Use );
		var events = new RecordingHandler<RequestFact>();
		var service = new RequestDeviceService(
			environment.Repositories, environment.Access, environment.Clock,
			FeatureTestEnvironment.AllowPolicy(), events.Bus() );
		var before = environment.Provider.Health.Sequence;

		var result = await service.SendAsync( actor, inventory.Id, device.Id, "No power" );

		Assert.AreEqual( ErrorCode.PolicyDenied, result.Error!.Code );
		Assert.AreEqual( before, environment.Provider.Health.Sequence );
		Assert.IsEmpty( events.Events );
	}

	[TestMethod]
	public async Task GenericChatAndRequestDeviceShareTheGlobalAdmissionBucket()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor();
		var character = environment.Character( actor );
		var device = Device( powered: true );
		var inventory = environment.Inventory(
			actor.CharacterId, new[] { new InventoryPlacement( device.Id, 0, 0 ) } );
		await environment.SeedAsync( unit =>
		{
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unit.Create( environment.Repositories.Items, DomainKeys.Item( device.Id ), device );
			unit.Create( environment.Repositories.Inventories, DomainKeys.Inventory( inventory.Id ), inventory );
		} );
		environment.Grant( actor, inventory.Id, InventoryCapability.View | InventoryCapability.Use );

		var authorities = new CanonicalChatAuthorityDirectory();
		authorities.Publish( 1, new[]
		{
			Authority( actor.ConnectionId, actor.AccountId, actor.CharacterId, HL2RPIds.Factions.Citizen )
		} );
		var liveInventory = new CanonicalLiveInventoryView();
		liveInventory.Publish( 1, Array.Empty<LiveInventoryItemView>() );
		var admission = new ChatAdmissionService();
		var globalLimit = ChatRateLimit.Default;
		var generic = new ChatService(
			environment.Schema,
			HL2RPChatChannelRules.Create(),
			authorities,
			new HL2RPRadioRecipientResolver( authorities ),
			liveInventory,
			environment.Clock,
			new PolicyPipeline<ChatSendContext>(
				new PolicyHandler<ChatSendContext>( "built_in", new AllowChatPolicy() ),
				new[]
				{
					new PolicyHandler<ChatSendContext>( "runtime", new HL2RPChatRuntimePolicy() )
				} ),
			globalRateLimit: globalLimit,
			admission: admission );
		var requests = new RecordingHandler<RequestFact>();
		var requestDevice = new RequestDeviceService(
			environment.Repositories,
			environment.Access,
			environment.Clock,
			FeatureTestEnvironment.AllowPolicy(),
			requests.Bus(),
			admission: admission,
			globalRateLimit: globalLimit );

		for ( var index = 0; index < globalLimit.Capacity; index++ )
		{
			var sent = generic.Send(
				actor, character, HL2RPIds.Channels.OutOfCharacter, $"generic {index}" );
			Assert.IsTrue( sent.Succeeded, sent.Error?.Message );
		}
		var beforeRequest = environment.Provider.Health.Sequence;
		var limited = await requestDevice.SendAsync(
			actor, inventory.Id, device.Id, "Need assistance" );

		Assert.AreEqual( ErrorCode.Conflict, limited.Error!.Code );
		Assert.AreEqual( beforeRequest, environment.Provider.Health.Sequence );
		Assert.IsEmpty( requests.Events );
		var persisted = environment.Repositories.Items.Find( DomainKeys.Item( device.Id ) )!;
		var state = HL2RPFeaturePersistence.Decode(
			persisted.Value.Traits["request_device"], HL2RPPersistence.RequestDevice );
		Assert.IsTrue( state.Succeeded, state.Error?.Message );
		Assert.IsNull( state.Value.LastRequestAtUtc );
	}

	[TestMethod]
	public async Task InterleavedInventoryMoveRejectsRequestWithoutFactOrCooldown()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor();
		var character = environment.Character( actor );
		var device = Device( powered: true );
		var inventory = environment.Inventory(
			actor.CharacterId, new[] { new InventoryPlacement( device.Id, 0, 0 ) } );
		await environment.SeedAsync( unit =>
		{
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unit.Create( environment.Repositories.Items, DomainKeys.Item( device.Id ), device );
			unit.Create( environment.Repositories.Inventories, DomainKeys.Inventory( inventory.Id ), inventory );
		} );
		environment.Grant( actor, inventory.Id, InventoryCapability.View | InventoryCapability.Use );
		var requests = new RecordingHandler<RequestFact>();
		var service = new RequestDeviceService(
			environment.Repositories,
			environment.Access,
			environment.Clock,
			FeatureTestEnvironment.AllowPolicy(),
			requests.Bus() );
		var itemBefore = environment.Repositories.Items.Find( DomainKeys.Item( device.Id ) )!;
		environment.Provider.InterleaveNextCommit( async () =>
		{
			var current = environment.Repositories.Inventories.Find( DomainKeys.Inventory( inventory.Id ) )!;
			await using var move = environment.Provider.BeginUnitOfWork();
			var editor = move.Edit( environment.Repositories.Inventories, current )!;
			editor.Replace( current.Value with { Placements = Array.Empty<InventoryPlacement>() } );
			move.Save( editor );
			var committed = await move.CommitAsync();
			Assert.IsTrue( committed.Succeeded, committed.Error?.Message );
		} );

		var result = await service.SendAsync(
			actor, inventory.Id, device.Id, "This membership becomes stale" );

		Assert.AreEqual( ErrorCode.Conflict, result.Error!.Code );
		Assert.IsEmpty( requests.Events );
		Assert.IsEmpty( environment.Repositories.Inventories.Find(
			DomainKeys.Inventory( inventory.Id ) )!.Value.Placements );
		var persisted = environment.Repositories.Items.Find( DomainKeys.Item( device.Id ) )!;
		Assert.AreEqual( itemBefore.Revision, persisted.Revision );
		var state = HL2RPFeaturePersistence.Decode(
			persisted.Value.Traits["request_device"], HL2RPPersistence.RequestDevice );
		Assert.IsTrue( state.Succeeded, state.Error?.Message );
		Assert.IsNull( state.Value.LastRequestAtUtc );
	}

	[TestMethod]
	public void RequestAndDispatchUseCurrentRoleAuthorityNotPossessedDevicesOrRadios()
	{
		var sender = new InventoryActor( ConnectionId.New(), new AccountId( 50 ), CharacterId.New() );
		var cp = Authority( ConnectionId.New(), new AccountId( 51 ), CharacterId.New(),
			HL2RPIds.Factions.CivilProtection, HL2RPIds.Permissions.DispatchChat );
		var overwatch = Authority( ConnectionId.New(), new AccountId( 52 ), CharacterId.New(),
			HL2RPIds.Factions.Overwatch, HL2RPIds.Permissions.DispatchChat );
		var administrator = Authority( ConnectionId.New(), new AccountId( 53 ), CharacterId.New(),
			HL2RPIds.Factions.CityAdministration, HL2RPIds.Permissions.DispatchChat );
		var possessingCitizen = Authority( ConnectionId.New(), new AccountId( 54 ), CharacterId.New(),
			HL2RPIds.Factions.Citizen, HL2RPIds.Permissions.DispatchChat );
		var senderAuthority = Authority(
			sender.ConnectionId, sender.AccountId, sender.CharacterId, HL2RPIds.Factions.Citizen );
		var authorities = new CanonicalChatAuthorityDirectory();
		authorities.Publish( 1, new[] { senderAuthority, cp, overwatch, administrator, possessingCitizen } );
		var resolver = new HL2RPRadioRecipientResolver( authorities );
		var inventory = new LiveInventorySnapshot( 1, new[]
		{
			RequestDevice( possessingCitizen.CharacterId ),
			Radio( possessingCitizen.CharacterId, "100.0", true ),
			Radio( sender.CharacterId, "100.0", true )
		} );
		var senderCharacter = Character( sender, HL2RPIds.Factions.Citizen );

		var request = resolver.Resolve(
			new ChatSendContext( sender, senderCharacter, HL2RPIds.Channels.Request, "Help" ),
			new ChatChannelRule { Id = HL2RPIds.Channels.Request },
			inventory );
		var dispatchActor = new InventoryActor(
			administrator.ConnectionId, administrator.AccountId, administrator.CharacterId );
		var dispatch = resolver.Resolve(
			new ChatSendContext(
				dispatchActor,
				Character( dispatchActor, HL2RPIds.Factions.CityAdministration ),
				HL2RPIds.Channels.Dispatch,
				"Directive" ),
			new ChatChannelRule { Id = HL2RPIds.Channels.Dispatch },
			inventory );

		CollectionAssert.AreEquivalent(
			new[] { sender.ConnectionId, cp.ConnectionId, overwatch.ConnectionId, administrator.ConnectionId },
			request.ToArray() );
		CollectionAssert.AreEquivalent(
			new[] { cp.ConnectionId, overwatch.ConnectionId, administrator.ConnectionId },
			dispatch.ToArray() );
		CollectionAssert.DoesNotContain( request.ToArray(), possessingCitizen.ConnectionId );
		CollectionAssert.DoesNotContain( dispatch.ToArray(), possessingCitizen.ConnectionId );

		var switchedCitizen = Authority(
			overwatch.ConnectionId, overwatch.AccountId, CharacterId.New(), HL2RPIds.Factions.Citizen );
		authorities.Publish( 2, new[]
		{
			senderAuthority,
			Authority( cp.ConnectionId, cp.AccountId, cp.CharacterId, HL2RPIds.Factions.Citizen ),
			switchedCitizen,
			administrator,
			possessingCitizen
		} );
		var afterRoleChange = resolver.Resolve(
			new ChatSendContext( sender, senderCharacter, HL2RPIds.Channels.Request, "Help" ),
			new ChatChannelRule { Id = HL2RPIds.Channels.Request },
			inventory );
		CollectionAssert.AreEquivalent(
			new[] { sender.ConnectionId, administrator.ConnectionId },
			afterRoleChange.ToArray() );
	}

	[TestMethod]
	public void RadioRequiresPoweredMatchingRadiosAndGenericRequestPolicyAlwaysDenies()
	{
		var actor = new InventoryActor( ConnectionId.New(), new AccountId( 60 ), CharacterId.New() );
		var matching = Authority(
			ConnectionId.New(), new AccountId( 61 ), CharacterId.New(), HL2RPIds.Factions.Citizen );
		var authorities = new CanonicalChatAuthorityDirectory();
		authorities.Publish( 1, new[]
		{
			Authority( actor.ConnectionId, actor.AccountId, actor.CharacterId, HL2RPIds.Factions.Citizen ),
			matching
		} );
		var resolver = new HL2RPRadioRecipientResolver( authorities );
		var context = new ChatSendContext(
			actor, Character( actor, HL2RPIds.Factions.Citizen ),
			HL2RPIds.Channels.Radio, "Check" );

		var unpowered = resolver.Resolve(
			context,
			new ChatChannelRule { Id = HL2RPIds.Channels.Radio },
			new LiveInventorySnapshot( 1, new[]
			{
				Radio( actor.CharacterId, "100.0", false ),
				Radio( matching.CharacterId, "100.0", true )
			} ) );
		var powered = resolver.Resolve(
			context,
			new ChatChannelRule { Id = HL2RPIds.Channels.Radio },
			new LiveInventorySnapshot( 2, new[]
			{
				Radio( actor.CharacterId, "100.0", true ),
				Radio( matching.CharacterId, "100.0", true )
			} ) );

		Assert.IsEmpty( unpowered );
		CollectionAssert.AreEquivalent(
			new[] { actor.ConnectionId, matching.ConnectionId }, powered.ToArray() );
		Assert.IsFalse( new HL2RPChatRuntimePolicy().Evaluate(
			context with { ChannelId = HL2RPIds.Channels.Request } ).Allowed );
	}

	[TestMethod]
	public void GenericChatRequestIsDeniedAndDispatchSenderPermissionTracksLiveRoleSwitch()
	{
		var actor = new InventoryActor( ConnectionId.New(), new AccountId( 70 ), CharacterId.New() );
		var authorities = new CanonicalChatAuthorityDirectory();
		authorities.Publish( 1, new[]
		{
			Authority( actor.ConnectionId, actor.AccountId, actor.CharacterId,
				HL2RPIds.Factions.CivilProtection, HL2RPIds.Permissions.DispatchChat )
		} );
		var inventory = new CanonicalLiveInventoryView();
		inventory.Publish( 1, Array.Empty<LiveInventoryItemView>() );
		var schema = SchemaCompiler.Compile( new HL2RPSchema() ).Value;
		var service = new ChatService(
			schema,
			HL2RPChatChannelRules.Create(),
			authorities,
			new HL2RPRadioRecipientResolver( authorities ),
			inventory,
			new FixedClock(),
			new PolicyPipeline<ChatSendContext>(
				new PolicyHandler<ChatSendContext>( "built_in", new AllowChatPolicy() ),
				new[]
				{
					new PolicyHandler<ChatSendContext>( "runtime", new HL2RPChatRuntimePolicy() )
				} ) );
		var character = Character( actor, HL2RPIds.Factions.CivilProtection );

		var request = service.Send( actor, character, HL2RPIds.Channels.Request, "Bypass" );
		var dispatch = service.Send( actor, character, HL2RPIds.Channels.Dispatch, "Directive" );
		authorities.Publish( 2, new[]
		{
			Authority( actor.ConnectionId, actor.AccountId, actor.CharacterId, HL2RPIds.Factions.Citizen )
		} );
		var revoked = service.Send( actor, character, HL2RPIds.Channels.Dispatch, "Stale role" );

		Assert.AreEqual( ErrorCode.PolicyDenied, request.Error!.Code );
		Assert.IsTrue( dispatch.Succeeded, dispatch.Error?.Message );
		Assert.AreEqual( ErrorCode.Unauthorized, revoked.Error!.Code );
	}

	private static ItemRecord Device( bool powered ) => new()
	{
		Id = ItemId.New(),
		Definition = new DefinitionId( HL2RPIds.Items.RequestDevice ),
		Traits = new Dictionary<string, TypedPayload>
		{
			["request_device"] = HL2RPPersistence.Payload(
				HL2RPPersistence.RequestDevice,
				new RequestDeviceItemState { Powered = powered, LastRequestAtUtc = null } )
		}
	};

	private static LiveChatAuthority Authority(
		ConnectionId connection,
		AccountId account,
		CharacterId character,
		string faction,
		params string[] permissions ) =>
		new( connection, account, character, new FactionId( faction ), permissions );

	private static CharacterRecord Character( InventoryActor actor, string faction ) => new()
	{
		Id = actor.CharacterId,
		AccountId = actor.AccountId,
		Slot = 0,
		Name = "Test communicator",
		Description = "A sufficiently detailed communications authority test character.",
		Model = new DefinitionId( HL2RPIds.Models.Citizen01 ),
		Faction = new FactionId( faction ),
		Balance = 0,
		CreatedAt = DateTimeOffset.UnixEpoch,
		LastPlayedAt = DateTimeOffset.UnixEpoch,
		SchemaState = HL2RPPersistence.Payload(
			HL2RPPersistence.CharacterState,
			new HL2RPCharacterState
			{
				CitizenId = "C17-TEST-00",
				Age = 28,
				Pronouns = "they/them",
				Origin = "city_17",
				Whitelists = HL2RPWhitelist.None,
				CivicRecord = new CivicRecordState
				{
					Points = 0,
					Priority = CivicPriorityStatus.None
				}
			} )
	};

	private static LiveInventoryItemView Radio(
		CharacterId owner,
		string frequency,
		bool powered ) => new(
		ItemId.New(),
		owner,
		new DefinitionId( HL2RPIds.Items.Radio ),
		new Dictionary<string, TypedPayload>
		{
			["radio"] = HL2RPPersistence.Payload(
				HL2RPPersistence.Radio,
				new RadioItemState { Frequency = frequency, Powered = powered } )
		} );

	private static LiveInventoryItemView RequestDevice( CharacterId owner ) => new(
		ItemId.New(),
		owner,
		new DefinitionId( HL2RPIds.Items.RequestDevice ),
		new Dictionary<string, TypedPayload>
		{
			["request_device"] = HL2RPPersistence.Payload(
				HL2RPPersistence.RequestDevice,
				new RequestDeviceItemState { Powered = true, LastRequestAtUtc = null } )
		} );

	private sealed class RecordingLiveInventory : ILiveInventoryView
	{
		private readonly LiveInventorySnapshot _snapshot;
		public RecordingLiveInventory( LiveInventorySnapshot snapshot ) => _snapshot = snapshot;
		public int CaptureCount { get; private set; }
		public LiveInventorySnapshot Capture()
		{
			CaptureCount++;
			return _snapshot;
		}
	}

	private sealed class RecordingChatSink : ICommittedChatDeliverySink
	{
		private readonly Func<long> _sequence;
		public RecordingChatSink( Func<long> sequence ) => _sequence = sequence;
		public List<ObservedDelivery> Deliveries { get; } = new();
		public void Deliver( ChatDelivery delivery, CharacterRecord author ) =>
			Deliveries.Add( new ObservedDelivery( delivery, author, _sequence() ) );
	}

	private sealed record ObservedDelivery(
		ChatDelivery Delivery,
		CharacterRecord Author,
		long ObservedSequence );

	private sealed class FixedClock : IHexClock
	{
		public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
	}

	private sealed class AllowChatPolicy : IPolicy<ChatSendContext>
	{
		public PolicyDecision Evaluate( ChatSendContext context ) => PolicyDecision.Allow();
	}
}
