#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Composition;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Policies;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Schema;
using HL2RP.V2.Tests.Schema;

namespace HL2RP.V2.Tests.Domain;

[TestClass]
public sealed class CharacterCreationTests
{
	private static readonly DateTimeOffset Now =
		new( 2030, 1, 2, 3, 4, 5, TimeSpan.Zero );

	[TestMethod]
	public void CitizenStateUsesAuthenticatedIdentityAndServerComputedBalance()
	{
		var account = new AccountId( 76561198000000001 );
		var request = Request( HL2RPIds.Factions.Citizen );
		var context = new CharacterCreationContext( account, request, 2, Now );

		var result = new HL2RPCharacterStateFactory().Create( context );

		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		Assert.AreEqual( 25L, result.Value.StartingBalance );
		var state = Decode( result.Value );
		Assert.AreEqual( HL2RPCharacterStateFactory.CreateCitizenId( account, 2 ), state.CitizenId );
		Assert.AreEqual( 28, state.Age );
		Assert.AreEqual( "they/them", state.Pronouns );
		Assert.AreEqual( "city_17", state.Origin );
		Assert.AreEqual( HL2RPWhitelist.None, state.Whitelists );
		Assert.IsNull( state.CombineIdentity );
		Assert.AreEqual( 0L, state.CivicRecord.Points );
		Assert.AreEqual( CivicPriorityStatus.None, state.CivicRecord.Priority );
	}

	[TestMethod]
	public void RestrictedFactionFailsClosedWithoutAuthenticatedWhitelist()
	{
		var context = new CharacterCreationContext(
			new AccountId( 42 ),
			Request( HL2RPIds.Factions.CivilProtection, HL2RPIds.Classes.Recruit ),
			0,
			Now );

		var result = new HL2RPCharacterStateFactory().Create( context );

		Assert.AreEqual( ErrorCode.Unauthorized, result.Error!.Code );
	}

	[TestMethod]
	public void CombineRankDivisionAndServiceNameAreGeneratedFromTypedDefinitions()
	{
		var account = new AccountId( 42 );
		var context = new CharacterCreationContext(
			account,
			Request( HL2RPIds.Factions.CivilProtection, HL2RPIds.Classes.Elite ) with
			{
				Name = "Forged C17.CCA-CMD.99999"
			},
			0,
			Now );
		var factory = new HL2RPCharacterStateFactory( new FixedWhitelists(
			HL2RPWhitelist.CivilProtection ) );

		var result = factory.Create( context );

		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		var identity = Decode( result.Value ).CombineIdentity!;
		Assert.AreEqual( CombineRank.Elite, identity.Rank );
		Assert.AreEqual( CombineDivision.Protection, identity.Division );
		Assert.AreEqual( "C17.CCA-EPU.00042", identity.ServiceName );
		Assert.AreNotEqual( context.Request.Name, identity.ServiceName );
	}

	[TestMethod]
	public void InvalidCreationFieldsAreRejectedByTheStateFactory()
	{
		var account = new AccountId( 42 );
		var underage = Request( HL2RPIds.Factions.Citizen ) with
		{
			Fields = Fields( age: 17 )
		};
		var unknownOrigin = Request( HL2RPIds.Factions.Citizen ) with
		{
			Fields = Fields( origin: "unknown" )
		};
		var wrongClass = Request( HL2RPIds.Factions.Citizen ) with
		{
			Class = new ClassId( HL2RPIds.Classes.Recruit )
		};
		var factory = new HL2RPCharacterStateFactory();

		var ageResult = factory.Create( new CharacterCreationContext( account, underage, 0, Now ) );
		var originResult = factory.Create( new CharacterCreationContext( account, unknownOrigin, 0, Now ) );
		var classResult = factory.Create( new CharacterCreationContext( account, wrongClass, 0, Now ) );

		Assert.AreEqual( ErrorCode.PolicyDenied, ageResult.Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument, originResult.Error!.Code );
		Assert.AreEqual( ErrorCode.PolicyDenied, classResult.Error!.Code );
	}

	[TestMethod]
	public void CidAndLoadoutInitializersAreOrderedPurePlansWithUniqueReservation()
	{
		var account = new AccountId( 42 );
		var request = Request( HL2RPIds.Factions.CivilProtection, HL2RPIds.Classes.Recruit );
		var context = new CharacterCreationContext( account, request, 0, Now );
		var state = new HL2RPCharacterStateFactory( new FixedWhitelists(
			HL2RPWhitelist.CivilProtection ) ).Create( context ).Value;
		var initializers = HL2RPInitializers.CreateDefault();

		var cid = initializers[0].Build( context, state );
		var loadout = initializers[1].Build( context, state );

		Assert.AreEqual( HL2RPCitizenIdInitializer.InitializerId, initializers[0].Id );
		Assert.AreEqual( HL2RPLoadoutInitializer.InitializerId, initializers[1].Id );
		Assert.IsLessThan( initializers[1].Order, initializers[0].Order );
		Assert.IsTrue( cid.Succeeded, cid.Error?.Message );
		Assert.HasCount( 1, cid.Value.Reservations );
		Assert.AreEqual( HL2RPIds.ReservationNamespaces.CitizenId, cid.Value.Reservations[0].Namespace );
		Assert.AreEqual( Decode( state ).CitizenId, cid.Value.Reservations[0].Value );
		Assert.HasCount( 1, cid.Value.Items );
		Assert.AreEqual( HL2RPIds.Items.CitizenIdCard, cid.Value.Items[0].Definition.Value );
		Assert.AreEqual(
			HL2RPIds.PersistedTypes.CitizenIdCard,
			cid.Value.Items[0].Traits["cid"].TypeId.Value );
		Assert.IsTrue( loadout.Succeeded, loadout.Error?.Message );
		HL2RPSchemaTests.AssertSetEquals(
			new[]
			{
				HL2RPIds.Items.Radio,
				HL2RPIds.Items.RequestDevice,
				HL2RPIds.Items.ZipTie,
				HL2RPIds.Items.Pistol,
				HL2RPIds.Items.PistolAmmunition,
				HL2RPIds.Items.ProtectiveVest,
				HL2RPIds.Items.CombineLockKit
			},
			loadout.Value.Items.Select( item => item.Definition.Value ) );
	}

	[TestMethod]
	public async Task CharacterServiceCommitsStateCidLoadoutBagAndReservationsTogether()
	{
		var schema = HL2RPSchemaTests.Compile();
		var registry = new PersistedTypeRegistry().RegisterHexagonDomainTypes();
		var binding = SchemaPersistenceAdapter.Bind( schema, registry, HL2RPPersistence.Codecs );
		Assert.IsTrue( binding.Succeeded, binding.Error?.Message );
		await using var provider = new InMemoryPersistenceProvider( binding.Value.Types );
		await provider.InitializeAsync();
		var repositories = new DomainRepositories( provider );
		var service = new CharacterService(
			repositories,
			schema,
			new AllowModels(),
			new HL2RPCharacterStateFactory(),
			HL2RPInitializers.CreateDefault(),
			new RandomAggregateIdGenerator(),
			new FixedClock(),
			new InventoryLayoutService( new SchemaItemShapeCatalog( schema, repositories ) ),
			schema.CreatePolicyPipeline<CharacterCreationContext>().Value,
			schema.CreatePolicyPipeline<CharacterDeletionContext>().Value );
		var account = new AccountId( 42 );

		var first = await service.CreateAsync( account, Request( HL2RPIds.Factions.Citizen ) );
		var second = await service.CreateAsync( account, Request( HL2RPIds.Factions.Citizen ) with
		{
			Name = "Second Citizen"
		} );

		Assert.IsTrue( first.Succeeded, first.Error?.Message );
		Assert.IsTrue( second.Succeeded, second.Error?.Message );
		Assert.AreEqual( 0, first.Value.Character.Slot );
		Assert.AreEqual( 1, second.Value.Character.Slot );
		// Per character: the schema's citizen-ID reservation and the framework's name reservation.
		Assert.HasCount( 4, repositories.UniqueReservations.All() );
		Assert.HasCount( 2, repositories.UniqueReservations.All()
			.Where( document => document.Value.Namespace == "hexagon.character-name" ).ToArray() );
		Assert.HasCount( 4, repositories.Inventories.All() );
		Assert.HasCount( 8, repositories.Items.All() );
		var firstState = HL2RPPersistence.CharacterState.Deserialize(
			first.Value.Character.SchemaState.Data,
			first.Value.Character.SchemaState.TypeVersion );
		var firstCidItem = first.Value.Items.Single( item =>
			item.Definition.Value == HL2RPIds.Items.CitizenIdCard );
		var cardState = HL2RPPersistence.CitizenIdCard.Deserialize(
			firstCidItem.Traits["cid"].Data,
			firstCidItem.Traits["cid"].TypeVersion );
		Assert.AreEqual( firstState.CitizenId, cardState.CitizenId );
		Assert.AreNotEqual(
			firstState.CitizenId,
			Decode( new CharacterStatePlan(
				second.Value.Character.SchemaState,
				second.Value.Character.Balance ) ).CitizenId );
	}

	[TestMethod]
	public async Task CharacterServiceRejectsClientSuppliedManagedCreationField()
	{
		var schema = HL2RPSchemaTests.Compile();
		var registry = new PersistedTypeRegistry().RegisterHexagonDomainTypes();
		var binding = SchemaPersistenceAdapter.Bind( schema, registry, HL2RPPersistence.Codecs );
		await using var provider = new InMemoryPersistenceProvider( binding.Value.Types );
		await provider.InitializeAsync();
		var repositories = new DomainRepositories( provider );
		var service = new CharacterService(
			repositories,
			schema,
			new AllowModels(),
			new HL2RPCharacterStateFactory(),
			HL2RPInitializers.CreateDefault(),
			new RandomAggregateIdGenerator(),
			new FixedClock(),
			new InventoryLayoutService( new SchemaItemShapeCatalog( schema, repositories ) ),
			schema.CreatePolicyPipeline<CharacterCreationContext>().Value,
			schema.CreatePolicyPipeline<CharacterDeletionContext>().Value );
		var fields = new Dictionary<string, CreationValue>( Fields(), StringComparer.Ordinal )
		{
			["money"] = CreationValue.Integer( 999_999 ),
			["rank"] = CreationValue.String( "commander" ),
			["cid"] = CreationValue.String( "forged" )
		};

		var result = await service.CreateAsync(
			new AccountId( 42 ),
			Request( HL2RPIds.Factions.Citizen ) with { Fields = fields } );

		Assert.AreEqual( ErrorCode.InvalidArgument, result.Error!.Code );
		Assert.IsEmpty( repositories.Characters.All() );
		Assert.IsEmpty( repositories.UniqueReservations.All() );
	}

	[TestMethod]
	public async Task CharacterDeletionClearsIndexedDoorRecognitionAndRestraintReferences()
	{
		var schema = HL2RPSchemaTests.Compile();
		var registry = new PersistedTypeRegistry().RegisterHexagonDomainTypes();
		var binding = SchemaPersistenceAdapter.Bind( schema, registry, HL2RPPersistence.Codecs );
		await using var provider = new InMemoryPersistenceProvider( binding.Value.Types );
		await provider.InitializeAsync();
		var repositories = new DomainRepositories( provider );
		var service = new CharacterService(
			repositories,
			schema,
			new AllowModels(),
			new HL2RPCharacterStateFactory(),
			HL2RPInitializers.CreateDefault(),
			new RandomAggregateIdGenerator(),
			new FixedClock(),
			new InventoryLayoutService( new SchemaItemShapeCatalog( schema, repositories ) ),
			schema.CreatePolicyPipeline<CharacterCreationContext>().Value,
			schema.CreatePolicyPipeline<CharacterDeletionContext>().Value );
		var account = new AccountId( 42 );
		var created = await service.CreateAsync( account, Request( HL2RPIds.Factions.Citizen ) );
		Assert.IsTrue( created.Succeeded, created.Error?.Message );
		var characterId = created.Value.Character.Id;
		// A distinct name: character names are reserved globally, so two citizens cannot share one.
		var related = await service.CreateAsync(
			new AccountId( 43 ), Request( HL2RPIds.Factions.Citizen ) with { Name = "Related Citizen" } );
		Assert.IsTrue( related.Succeeded, related.Error?.Message );
		var relatedCharacter = related.Value.Character.Id;
		var sceneEntity = SceneEntityId.New();
		await using ( var unitOfWork = provider.BeginUnitOfWork() )
		{
			unitOfWork.Create(
				repositories.SceneEntities,
				DomainKeys.SceneEntity( sceneEntity ),
				new PersistentSceneEntityRecord
				{
					Id = sceneEntity,
					Kind = "door",
					State = HL2RPPersistence.Payload(
						HL2RPPersistence.DoorState,
						new DoorEntityState { CombineLocked = false, IsOpen = false } )
				} );
			var committed = await unitOfWork.CommitAsync();
			Assert.IsTrue( committed.Succeeded, committed.Error?.Message );
		}
		var references = new CharacterReferenceMutationService( repositories );
		var door = await references.UpsertAsync(
			"door-owner",
			new CharacterReferenceRecord
			{
				Category = "door_ownership",
				CharacterId = characterId,
				SceneEntityId = sceneEntity,
				State = HL2RPPersistence.Payload(
					HL2RPPersistence.DoorOwnership,
					new DoorOwnershipReferenceState { AcquiredAtUtc = Now } )
			} );
		var recognition = await references.UpsertAsync(
			"recognition",
			new CharacterReferenceRecord
			{
				Category = "recognition",
				CharacterId = relatedCharacter,
				RelatedCharacterId = characterId,
				State = HL2RPPersistence.Payload(
					HL2RPPersistence.Recognition,
					new RecognitionReferenceState
					{
						IntroducedName = "Test Citizen",
						IntroducedAtUtc = Now
					} )
			} );
		var restraint = await references.UpsertAsync(
			"restraint",
			new CharacterReferenceRecord
			{
				Category = "restraint",
				CharacterId = characterId,
				RelatedCharacterId = relatedCharacter,
				State = HL2RPPersistence.Payload(
					HL2RPPersistence.Restraint,
					new RestraintReferenceState { RestrainedAtUtc = Now, Active = true } )
			} );
		Assert.IsTrue( door.Succeeded, door.Error?.Message );
		Assert.IsTrue( recognition.Succeeded, recognition.Error?.Message );
		Assert.IsTrue( restraint.Succeeded, restraint.Error?.Message );

		var deleted = await service.DeleteAsync( account, characterId );

		Assert.IsTrue( deleted.Succeeded, deleted.Error?.Message );
		Assert.IsEmpty( repositories.CharacterReferences.All() );
		Assert.HasCount( 1, repositories.SceneEntities.All() );
		Assert.AreEqual(
			HL2RPIds.PersistedTypes.DoorState,
			repositories.SceneEntities.Find( DomainKeys.SceneEntity( sceneEntity ) )!.Value.State.TypeId.Value );
	}

	private static CharacterCreationRequest Request( string faction, string? characterClass = null ) => new()
	{
		Name = "Test Citizen",
		Description = "A sufficiently detailed City 17 character description.",
		Model = new DefinitionId( "citizen_model" ),
		Faction = new FactionId( faction ),
		Class = characterClass is null ? null : new ClassId( characterClass ),
		Fields = Fields()
	};

	private static IReadOnlyDictionary<string, CreationValue> Fields(
		long age = 28,
		string origin = "city_17" ) => new Dictionary<string, CreationValue>( StringComparer.Ordinal )
	{
		[HL2RPIds.CreationFields.Age] = CreationValue.Integer( age ),
		[HL2RPIds.CreationFields.Pronouns] = CreationValue.String( "  they/them  " ),
		[HL2RPIds.CreationFields.Origin] = CreationValue.Choice( origin )
	};

	private static HL2RPCharacterState Decode( CharacterStatePlan plan ) =>
		HL2RPPersistence.CharacterState.Deserialize( plan.State.Data, plan.State.TypeVersion );

	private sealed class FixedWhitelists : IHL2RPWhitelistService
	{
		private readonly HL2RPWhitelist _whitelists;

		public FixedWhitelists( HL2RPWhitelist whitelists ) => _whitelists = whitelists;

		public HL2RPWhitelist GetWhitelists( AccountId accountId ) => _whitelists;
	}

	private sealed class AllowModels : ICharacterModelCatalog
	{
		public bool IsAllowed( DefinitionId model, FactionId faction, ClassId? characterClass ) => true;
	}

	private sealed class FixedClock : IHexClock
	{
		public DateTimeOffset UtcNow => Now;
	}
}
