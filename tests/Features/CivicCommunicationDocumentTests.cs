#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Definitions;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Kernel.Policies;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Features;

[TestClass]
public sealed class CivicCommunicationDocumentTests
{
	[TestMethod]
	public async Task CivicMutationsDecodeTypedStateAndAuditOnlyCommittedFacts()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor();
		var targetActor = environment.Actor( 43 );
		var actorCharacter = environment.Character( actor );
		var target = environment.Character( targetActor );
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( actor.CharacterId ), actorCharacter );
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( target.Id ), target );
		} );
		var events = new RecordingHandler<CivicMutationReceipt>();
		var audit = new RecordingHandler<AdminAuditFact>();
		var service = new CivicService(
			environment.Repositories,
			environment.Clock,
			FeatureTestEnvironment.AllowPolicy(),
			events.Bus(),
			audit.Bus() );

		var infraction = await service.AddInfractionAsync(
			actor, target.Id, "17-1", "Civil violation", 3 );
		var priority = await service.UpdateRecordAsync(
			actor, target.Id, CivicPriorityStatus.Detain, "Verified civic observation." );
		environment.Provider.FailNextCommit();
		var failed = await service.AddInfractionAsync(
			actor, target.Id, "17-2", "Uncommitted", 5 );

		Assert.IsTrue( infraction.Succeeded, infraction.Error?.Message );
		Assert.IsTrue( priority.Succeeded, priority.Error?.Message );
		Assert.AreEqual( ErrorCode.InternalError, failed.Error!.Code );
		var stored = service.Read( target.Id ).Value;
		Assert.AreEqual( 3L, stored.CivicRecord.Points );
		Assert.AreEqual( CivicPriorityStatus.Detain, stored.CivicRecord.Priority );
		Assert.AreEqual( "Verified civic observation.", stored.CivicRecord.RecordText );
		Assert.HasCount( 1, stored.CivicRecord.Infractions );
		Assert.HasCount( 2, events.Events );
		Assert.HasCount( 2, audit.Events );
		Assert.IsTrue( audit.Events.All( fact => fact.CommitSequence > 0 ) );
	}

	[TestMethod]
	public async Task IntroductionsAndCityObjectivesUseTypedCommittedState()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor();
		var subjectActor = environment.Actor( 43 );
		var viewer = environment.Character( actor );
		var subject = environment.Character( subjectActor, name: "Known Citizen" );
		var cityId = SceneEntityId.New();
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( viewer.Id ), viewer );
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( subject.Id ), subject );
			unitOfWork.Create(
				environment.Repositories.SceneEntities,
				DomainKeys.SceneEntity( cityId ),
				new PersistentSceneEntityRecord
				{
					Id = cityId,
					Kind = "city",
					State = HL2RPPersistence.Payload(
						HL2RPPersistence.CityState,
						new CityEntityState() )
				} );
		} );
		var recognition = new RecognitionService(
			environment.Repositories,
			environment.Clock,
			new FixedEncounterAuthorizer( OperationResult.Success() ),
			FeatureTestEnvironment.AllowPolicy() );
		var objectives = new CityObjectiveService(
			environment.Repositories,
			environment.Clock,
			FeatureTestEnvironment.AllowPolicy() );

		var introduced = await recognition.IntroduceAsync( actor, subject.Id );
		var replaced = await objectives.ReplaceAsync(
			actor,
			cityId,
			new[]
			{
				new CityObjectiveState
				{
					Id = "maintain_order",
					Text = "Maintain civic order.",
					Completed = false,
					UpdatedAtUtc = environment.Clock.UtcNow
				}
			} );

		Assert.IsTrue( introduced.Succeeded, introduced.Error?.Message );
		Assert.AreEqual( "Known Citizen", introduced.Value.IntroducedName );
		Assert.HasCount( 1, environment.Repositories.CharacterReferences.All() );
		var reference = environment.Repositories.CharacterReferences.All()[0].Value;
		Assert.AreEqual( viewer.Id, reference.CharacterId );
		Assert.AreEqual( subject.Id, reference.RelatedCharacterId );
		Assert.IsTrue( replaced.Succeeded, replaced.Error?.Message );
		var city = environment.Repositories.SceneEntities.Find( DomainKeys.SceneEntity( cityId ) )!.Value;
		var state = HL2RPPersistence.CityState.Deserialize( city.State.Data, city.State.TypeVersion );
		Assert.HasCount( 1, state.Objectives );
		Assert.AreEqual( "maintain_order", state.Objectives[0].Id );
	}

	[TestMethod]
	public async Task RecognitionRequiresAnAuthoritativeEncounterProof()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor();
		var subjectActor = environment.Actor( 43 );
		var viewer = environment.Character( actor );
		var subject = environment.Character( subjectActor, name: "Remote Citizen" );
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( viewer.Id ), viewer );
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( subject.Id ), subject );
		} );
		var recognition = new RecognitionService(
			environment.Repositories,
			environment.Clock,
			new FixedEncounterAuthorizer( OperationResult.Failure( ErrorCode.Unauthorized, "out of range" ) ),
			FeatureTestEnvironment.AllowPolicy() );

		var result = await recognition.IntroduceAsync( actor, subject.Id );

		Assert.AreEqual( ErrorCode.Unauthorized, result.Error!.Code );
		Assert.IsEmpty( environment.Repositories.CharacterReferences.All() );
	}

	[TestMethod]
	public void RadioChatCapturesLiveInventoryOnceAndResolvesOnlyMatchingPoweredRadios()
	{
		var actor = new InventoryActor( ConnectionId.New(), new AccountId( 42 ), CharacterId.New() );
		var matching = CharacterId.New();
		var different = CharacterId.New();
		var actorConnection = actor.ConnectionId;
		var matchingConnection = ConnectionId.New();
		var differentConnection = ConnectionId.New();
		var inventory = new RecordingLiveInventory( new LiveInventorySnapshot(
			7,
			new[]
			{
				RadioView( actor.CharacterId, "100.0", true ),
				RadioView( matching, "100.0", true ),
				RadioView( different, "200.0", true )
			} ) );
		var directory = new FixedRadioDirectory( new Dictionary<CharacterId, ConnectionId>
		{
			[actor.CharacterId] = actorConnection,
			[matching] = matchingConnection,
			[different] = differentConnection
		} );
		var resolver = new HL2RPRadioRecipientResolver( directory );
		var compiled = SchemaCompiler.Compile( new HL2RPSchema() ).Value;
		var service = new ChatService(
			compiled,
			compiled.ChatChannels.All.Select( channel => new ChatChannelRule { Id = channel.Id } ),
			new AllowPermissions(),
			resolver,
			inventory,
			new FixedClock(),
			new PolicyPipeline<ChatSendContext>(
				new PolicyHandler<ChatSendContext>( "built_in", new AllowPolicy<ChatSendContext>() ) ) );
		var character = new FeatureTestCharacterFactory().Create( actor );

		var sent = service.Send( actor, character, HL2RPIds.Channels.Radio, "Radio check" );

		Assert.IsTrue( sent.Succeeded, sent.Error?.Message );
		Assert.AreEqual( 1, inventory.CaptureCount );
		CollectionAssert.AreEquivalent(
			new[] { actorConnection, matchingConnection },
			sent.Value.Recipients.ToArray() );
	}

	[TestMethod]
	public async Task RadioTuningUsesMembershipCapabilityAndAtomicTraitReplacement()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor();
		var character = environment.Character( actor );
		var radio = environment.Item(
			HL2RPIds.Items.Radio,
			new Dictionary<string, TypedPayload>
			{
				["radio"] = HL2RPPersistence.Payload(
					HL2RPPersistence.Radio,
					new RadioItemState { Frequency = "100.0", Powered = true } )
			} );
		var inventory = environment.Inventory(
			actor.CharacterId,
			new[] { new InventoryPlacement( radio.Id, 0, 0 ) } );
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unitOfWork.Create( environment.Repositories.Items, DomainKeys.Item( radio.Id ), radio );
			unitOfWork.Create( environment.Repositories.Inventories, DomainKeys.Inventory( inventory.Id ), inventory );
		} );
		environment.Grant( actor, inventory.Id, InventoryCapability.View | InventoryCapability.Use );
		var service = new RadioTuningService(
			environment.Repositories,
			environment.Access,
			environment.Clock,
			FeatureTestEnvironment.AllowPolicy() );

		var tuned = await service.ConfigureAsync( actor, inventory.Id, radio.Id, " 101.5 ", false );

		Assert.IsTrue( tuned.Succeeded, tuned.Error?.Message );
		Assert.AreEqual( "101.5", tuned.Value.Frequency );
		var stored = environment.Repositories.Items.Find( DomainKeys.Item( radio.Id ) )!.Value;
		var state = HL2RPPersistence.Radio.Deserialize(
			stored.Traits["radio"].Data,
			stored.Traits["radio"].TypeVersion );
		Assert.AreEqual( "101.5", state.Frequency );
		Assert.IsFalse( state.Powered );
	}

	[TestMethod]
	public async Task NoteAndPermitMutationsCommitThroughDedicatedTypedServices()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor();
		var ownerActor = environment.Actor( 43 );
		var actorCharacter = environment.Character( actor );
		var owner = environment.Character( ownerActor );
		var note = environment.Item(
			HL2RPIds.Items.Note,
			new Dictionary<string, TypedPayload>
			{
				["note"] = HL2RPPersistence.Payload(
					HL2RPPersistence.Note,
					new NoteItemState { Text = "", OwnerCharacterId = null, UpdatedAtUtc = environment.Clock.UtcNow } )
			} );
		var actorInventory = environment.Inventory(
			actor.CharacterId,
			new[] { new InventoryPlacement( note.Id, 0, 0 ) } );
		var ownerInventory = environment.Inventory( owner.Id );
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( actor.CharacterId ), actorCharacter );
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( owner.Id ), owner );
			unitOfWork.Create( environment.Repositories.Items, DomainKeys.Item( note.Id ), note );
			unitOfWork.Create( environment.Repositories.Inventories, DomainKeys.Inventory( actorInventory.Id ), actorInventory );
			unitOfWork.Create( environment.Repositories.Inventories, DomainKeys.Inventory( ownerInventory.Id ), ownerInventory );
		} );
		environment.Grant( actor, actorInventory.Id, InventoryCapability.View | InventoryCapability.Use );
		environment.Grant( actor, ownerInventory.Id, InventoryCapability.TransferIn );
		var service = new DocumentService(
			environment.Repositories,
			environment.Access,
			environment.Ids,
			environment.Layout,
			environment.Clock,
			FeatureTestEnvironment.AllowPolicy() );

		var edited = await service.EditNoteAsync( actor, actorInventory.Id, note.Id, "  Civic note  " );
		var permit = await service.IssuePermitAsync(
			actor, owner.Id, ownerInventory.Id, BusinessPermitKind.Food, environment.Clock.UtcNow.AddDays( 30 ) );

		Assert.IsTrue( edited.Succeeded, edited.Error?.Message );
		Assert.AreEqual( "Civic note", service.ReadNote( actor, actorInventory.Id, note.Id ).Value.Text );
		Assert.IsTrue( permit.Succeeded, permit.Error?.Message );
		var permitItem = environment.Repositories.Items.Find( DomainKeys.Item( permit.Value.PermitItemId ) )!.Value;
		var permitState = HL2RPPersistence.BusinessPermit.Deserialize(
			permitItem.Traits["permit"].Data,
			permitItem.Traits["permit"].TypeVersion );
		Assert.AreEqual( owner.Id, permitState.OwnerCharacterId );
		Assert.AreEqual( BusinessPermitKind.Food, permitState.Kind );
	}

	private static LiveInventoryItemView RadioView( CharacterId owner, string frequency, bool powered ) => new(
		ItemId.New(),
		owner,
		new DefinitionId( HL2RPIds.Items.Radio ),
		new Dictionary<string, TypedPayload>
		{
			["radio"] = HL2RPPersistence.Payload(
				HL2RPPersistence.Radio,
				new RadioItemState { Frequency = frequency, Powered = powered } )
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

	private sealed class FixedRadioDirectory : IChatAuthorityDirectory
	{
		private readonly ChatAuthoritySnapshot _snapshot;
		public FixedRadioDirectory( IReadOnlyDictionary<CharacterId, ConnectionId> connections ) =>
			_snapshot = new ChatAuthoritySnapshot( 1, connections.Select( value =>
				new LiveChatAuthority(
					value.Value,
					new AccountId( 42 ),
					value.Key,
					new FactionId( HL2RPIds.Factions.Citizen ) ) ) );
		public ChatAuthoritySnapshot Capture() => _snapshot;
	}

	private sealed class AllowPermissions : IPermissionAuthorizer
	{
		public bool HasPermission( AccountId accountId, CharacterId characterId, string permissionId ) => true;
	}

	private sealed class FixedEncounterAuthorizer : ICharacterEncounterAuthorizer
	{
		private readonly OperationResult _result;
		public FixedEncounterAuthorizer( OperationResult result ) => _result = result;
		public OperationResult Authorize( InventoryActor actor, CharacterId subjectCharacterId ) => _result;
	}

	private sealed class FixedClock : IHexClock
	{
		public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
	}

	private sealed class AllowPolicy<TContext> : IPolicy<TContext>
	{
		public PolicyDecision Evaluate( TContext context ) => PolicyDecision.Allow();
	}

	private sealed class FeatureTestCharacterFactory
	{
		public CharacterRecord Create( InventoryActor actor ) => new()
		{
			Id = actor.CharacterId,
			AccountId = actor.AccountId,
			Slot = 0,
			Name = "Radio Citizen",
			Description = "A sufficiently detailed radio test description.",
			Model = new DefinitionId( "model.citizen_01" ),
			Faction = new FactionId( HL2RPIds.Factions.Citizen ),
			Balance = 0,
			CreatedAt = DateTimeOffset.UnixEpoch,
			LastPlayedAt = DateTimeOffset.UnixEpoch,
			SchemaState = HL2RPPersistence.Payload(
				HL2RPPersistence.CharacterState,
				new HL2RPCharacterState
				{
					CitizenId = "C17-42-00",
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
	}
}
