#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Runtime;
using HL2RP.V2.Tests.Features;

namespace HL2RP.V2.Tests.Runtime;

[TestClass]
public sealed class ObjectiveCommandRouterTests
{
	[TestMethod]
	public async Task RouteExecutesCreateUpdateDeleteAndRejectsUnknownSuppliedIds()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor( 8001 );
		var cityId = SceneEntityId.New();
		await SeedAsync( environment, actor, cityId );
		var factoryCalls = 0;
		var generated = Guid.Parse( "80b5981b-25a4-4982-a625-4619dc599e6d" );
		var service = new CityObjectiveService(
			environment.Repositories, environment.Clock, FeatureTestEnvironment.AllowPolicy() );
		var router = new HL2RPObjectiveCommandRouter(
			service,
			() => cityId,
			() =>
			{
				factoryCalls++;
				return generated;
			} );

		var created = await router.RouteAsync( actor, UpsertArguments(
			null, "  Maintain civic order.  ", "Line one\r\nLine two\rLine three", false ) );

		Assert.IsTrue( created.Command.Result.Succeeded, created.Command.Result.Error?.Message );
		Assert.IsNotNull( created.Receipt );
		var objectiveId = $"objective.{generated:N}";
		Assert.AreEqual( objectiveId, created.Receipt.ObjectiveId );
		Assert.AreEqual( CityObjectiveChangeKind.Upsert, created.Receipt.Kind );
		Assert.AreEqual( 1, factoryCalls );
		Assert.IsTrue( created.Command.Changes.Broadcast );
		Assert.AreEqual( created.Receipt.Persistence.CommitSequence, created.Receipt.ProjectionReceipt.Sequence );
		Assert.HasCount( 1, created.Receipt.ProjectionReceipt.Documents );
		Assert.HasCount( 1, created.Receipt.ProjectionRows );
		Assert.AreEqual( objectiveId,
			created.Receipt.ProjectionRows[0][HL2RP.UI.HL2RPPresentationFields.Objectives.ObjectiveId].StringValue );
		Assert.AreEqual( "Maintain civic order.",
			created.Receipt.ProjectionRows[0][HL2RP.UI.HL2RPPresentationFields.Objectives.Title].StringValue );
		Assert.AreEqual( "Line one\nLine two\nLine three",
			created.Receipt.ProjectionRows[0][HL2RP.UI.HL2RPPresentationFields.Objectives.Detail].StringValue );

		environment.Clock.Advance( TimeSpan.FromMinutes( 1 ) );
		var updated = await router.RouteAsync( actor, UpsertArguments(
			objectiveId, "Revised title", "Revised detail", true ) );
		Assert.IsTrue( updated.Command.Result.Succeeded, updated.Command.Result.Error?.Message );
		Assert.AreEqual( objectiveId, updated.Receipt!.ObjectiveId );
		Assert.AreEqual( 1, factoryCalls, "Updating a supplied server ID must not allocate another ID." );
		var afterUpdate = Read( environment, cityId );
		Assert.HasCount( 1, afterUpdate.Objectives );
		Assert.AreEqual( "Revised title", afterUpdate.Objectives[0].Title );
		Assert.AreEqual( "Revised detail", afterUpdate.Objectives[0].Detail );
		Assert.IsTrue( afterUpdate.Objectives[0].Completed );
		Assert.AreEqual( environment.Clock.UtcNow, afterUpdate.Objectives[0].UpdatedAtUtc );

		var sequenceBeforeUnknown = environment.Provider.Health.Sequence;
		var unknown = await router.RouteAsync( actor, UpsertArguments(
			"objective.unknown", "Must not be created", string.Empty, false ) );
		Assert.AreEqual( ErrorCode.NotFound, unknown.Command.Result.Error!.Code );
		Assert.IsNull( unknown.Receipt );
		Assert.IsTrue( unknown.Command.Changes.IsEmpty );
		Assert.AreEqual( sequenceBeforeUnknown, environment.Provider.Health.Sequence );
		Assert.HasCount( 1, Read( environment, cityId ).Objectives );

		var deleted = await router.RouteAsync( actor, DeleteArguments( objectiveId ) );
		Assert.IsTrue( deleted.Command.Result.Succeeded, deleted.Command.Result.Error?.Message );
		Assert.AreEqual( CityObjectiveChangeKind.Delete, deleted.Receipt!.Kind );
		Assert.AreEqual( objectiveId, deleted.Receipt.ObjectiveId );
		Assert.IsEmpty( deleted.Receipt.ProjectionRows );
		Assert.IsEmpty( Read( environment, cityId ).Objectives );

		var sequenceBeforeUnknownDelete = environment.Provider.Health.Sequence;
		var unknownDelete = await router.RouteAsync( actor, DeleteArguments( objectiveId ) );
		Assert.AreEqual( ErrorCode.NotFound, unknownDelete.Command.Result.Error!.Code );
		Assert.IsNull( unknownDelete.Receipt );
		Assert.IsTrue( unknownDelete.Command.Changes.IsEmpty );
		Assert.AreEqual( sequenceBeforeUnknownDelete, environment.Provider.Health.Sequence );
	}

	[TestMethod]
	public async Task RoutePublishesNoTypedOutcomeWhenTheCommitFails()
	{
		await using var environment = await FeatureTestEnvironment.CreateAsync();
		var actor = environment.Actor( 8002 );
		var cityId = SceneEntityId.New();
		await SeedAsync( environment, actor, cityId );
		var router = new HL2RPObjectiveCommandRouter(
			new CityObjectiveService(
				environment.Repositories, environment.Clock, FeatureTestEnvironment.AllowPolicy() ),
			() => cityId,
			Guid.NewGuid );
		environment.Provider.FailNextCommit();

		var outcome = await router.RouteAsync( actor, UpsertArguments(
			null, "Must not publish", "The commit will fail.", false ) );

		Assert.AreEqual( ErrorCode.InternalError, outcome.Command.Result.Error!.Code );
		Assert.IsNull( outcome.Receipt );
		Assert.IsTrue( outcome.Command.Changes.IsEmpty );
		Assert.IsEmpty( Read( environment, cityId ).Objectives );
	}

	[TestMethod]
	public void ContractCoversEveryTitleDetailIdentifierTimestampAndCapacityBoundary()
	{
		Assert.IsTrue( CityObjectiveContract.CreateContent( "x", string.Empty ).Succeeded );
		Assert.IsTrue( CityObjectiveContract.CreateContent(
			new string( 't', CityObjectiveContract.MaximumTitleLength ),
			new string( 'd', CityObjectiveContract.MaximumDetailLength ) ).Succeeded );
		Assert.AreEqual( ErrorCode.InvalidArgument,
			CityObjectiveContract.CreateContent( string.Empty, string.Empty ).Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument,
			CityObjectiveContract.CreateContent( "   ", string.Empty ).Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument, CityObjectiveContract.CreateContent(
			new string( 't', CityObjectiveContract.MaximumTitleLength + 1 ), string.Empty ).Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument,
			CityObjectiveContract.CreateContent( "line\nline", string.Empty ).Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument,
			CityObjectiveContract.CreateContent( "bad\0title", string.Empty ).Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument, CityObjectiveContract.CreateContent(
			"title", new string( 'd', CityObjectiveContract.MaximumDetailLength + 1 ) ).Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument,
			CityObjectiveContract.CreateContent( "title", "bad\tdetail" ).Error!.Code );
		var normalized = CityObjectiveContract.CreateContent( "  title  ", "  one\r\ntwo\rthree  " );
		Assert.IsTrue( normalized.Succeeded );
		Assert.AreEqual( "title", normalized.Value.Title );
		Assert.AreEqual( "one\ntwo\nthree", normalized.Value.Detail );

		Assert.IsTrue( CityObjectiveContract.ValidateIdentifier( new string( 'a', 96 ) ).Succeeded );
		Assert.AreEqual( ErrorCode.InvalidArgument,
			CityObjectiveContract.ValidateIdentifier( new string( 'a', 97 ) ).Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument,
			CityObjectiveContract.ValidateIdentifier( " Objective.valid" ).Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument,
			CityObjectiveContract.ValidateIdentifier( "Objective.invalid" ).Error!.Code );

		var timestamp = DateTimeOffset.UnixEpoch.AddMinutes( 1 );
		var full = Enumerable.Range( 0, CityObjectiveContract.MaximumObjectives )
			.Select( value => Objective( $"objective.{value}", timestamp ) )
			.ToArray();
		Assert.IsTrue( CityObjectiveContract.Validate( full ).Succeeded );
		var createAtCapacity = CityObjectiveContract.Apply(
			full,
			CityObjectiveChange.Upsert( new CityObjectiveUpdate(
				null, new CityObjectiveContent( "new", string.Empty ), false ) ),
			timestamp,
			Guid.NewGuid );
		Assert.AreEqual( ErrorCode.InvalidArgument, createAtCapacity.Error!.Code );
		var updateAtCapacity = CityObjectiveContract.Apply(
			full,
			CityObjectiveChange.Upsert( new CityObjectiveUpdate(
				"objective.0", new CityObjectiveContent( "updated", string.Empty ), true ) ),
			timestamp,
			Guid.NewGuid );
		Assert.IsTrue( updateAtCapacity.Succeeded, updateAtCapacity.Error?.Message );
		Assert.HasCount( CityObjectiveContract.MaximumObjectives, updateAtCapacity.Value.Objectives );
		Assert.AreEqual( ErrorCode.InvalidArgument, CityObjectiveContract.Validate(
			new[] { Objective( "objective.same", timestamp ), Objective( "objective.same", timestamp ) } ).Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument, CityObjectiveContract.Apply(
			Array.Empty<CityObjectiveState>(),
			CityObjectiveChange.Upsert( new CityObjectiveUpdate(
				null, new CityObjectiveContent( "title", string.Empty ), false ) ),
			default,
			Guid.NewGuid ).Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument, CityObjectiveContract.Apply(
			Array.Empty<CityObjectiveState>(),
			CityObjectiveChange.Upsert( new CityObjectiveUpdate(
				null, new CityObjectiveContent( "title", string.Empty ), false ) ),
			new DateTimeOffset( 2030, 1, 1, 0, 0, 0, TimeSpan.FromHours( 1 ) ),
			Guid.NewGuid ).Error!.Code );

		var collision = Guid.Parse( "f44ce9d5-70a5-4620-b3c4-4c6dd45379c5" );
		var collisionId = $"objective.{collision:N}";
		Assert.AreEqual( ErrorCode.Conflict, CityObjectiveContract.Apply(
			new[] { Objective( collisionId, timestamp ) },
			CityObjectiveChange.Upsert( new CityObjectiveUpdate(
				null, new CityObjectiveContent( "title", string.Empty ), false ) ),
			timestamp,
			() => collision ).Error!.Code );
	}

	[TestMethod]
	public void CommandParsingDefaultsToUpsertAndDeleteRequiresAStableIdentifier()
	{
		var upsert = HL2RPObjectiveCommand.ParseChange( new HL2RPCommandArguments(
			new Dictionary<string, SnapshotValue>
			{
				["title"] = SnapshotValue.String( "title" ),
				["detail"] = SnapshotValue.String( "first\r\nsecond" ),
				["completed"] = SnapshotValue.Boolean( false )
			} ) );
		Assert.IsTrue( upsert.Succeeded, upsert.Error?.Message );
		Assert.AreEqual( CityObjectiveChangeKind.Upsert, upsert.Value.Kind );
		Assert.IsNull( upsert.Value.ObjectiveId );
		Assert.AreEqual( "first\nsecond", upsert.Value.Content!.Detail );

		var deleted = HL2RPObjectiveCommand.ParseChange( DeleteArguments( "objective.valid" ) );
		Assert.IsTrue( deleted.Succeeded, deleted.Error?.Message );
		Assert.AreEqual( CityObjectiveChangeKind.Delete, deleted.Value.Kind );
		Assert.AreEqual( "objective.valid", deleted.Value.ObjectiveId );
		Assert.IsNull( deleted.Value.Content );

		Assert.AreEqual( ErrorCode.InvalidArgument, HL2RPObjectiveCommand.ParseChange(
			new HL2RPCommandArguments( new Dictionary<string, SnapshotValue>
			{
				["operation"] = SnapshotValue.Choice( HL2RPObjectiveCommand.DeleteOperation ),
				["objective"] = SnapshotValue.String( string.Empty )
			} ) ).Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument, HL2RPObjectiveCommand.ParseChange(
			new HL2RPCommandArguments( new Dictionary<string, SnapshotValue>
			{
				["operation"] = SnapshotValue.Choice( "replace-all" )
			} ) ).Error!.Code );
	}

	private static async Task SeedAsync(
		FeatureTestEnvironment environment,
		InventoryActor actor,
		SceneEntityId cityId )
	{
		var character = environment.Character( actor );
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create(
				environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unitOfWork.Create(
				environment.Repositories.CharacterLifecycleGuards,
				DomainKeys.CharacterLifecycleGuard( character.Id ),
				new CharacterLifecycleGuardRecord { CharacterId = character.Id, ReferenceRevision = 0 } );
			unitOfWork.Create(
				environment.Repositories.SceneEntities,
				DomainKeys.SceneEntity( cityId ),
				new PersistentSceneEntityRecord
				{
					Id = cityId,
					Kind = "city",
					State = HL2RPPersistence.Payload( HL2RPPersistence.CityState, new CityEntityState() )
				} );
		} );
	}

	private static CityEntityState Read( FeatureTestEnvironment environment, SceneEntityId cityId )
	{
		var document = environment.Repositories.SceneEntities.Find( DomainKeys.SceneEntity( cityId ) )!;
		return HL2RPPersistence.CityState.Deserialize( document.Value.State.Data, document.Value.State.TypeVersion );
	}

	private static CityObjectiveState Objective( string id, DateTimeOffset timestamp ) => new()
	{
		Id = id,
		Title = "title",
		Detail = string.Empty,
		Completed = false,
		UpdatedAtUtc = timestamp
	};

	private static HL2RPCommandArguments UpsertArguments(
		string? objectiveId,
		string title,
		string detail,
		bool completed ) => new( new Dictionary<string, SnapshotValue>
	{
		["objective"] = SnapshotValue.String( objectiveId ?? string.Empty ),
		["title"] = SnapshotValue.String( title ),
		["detail"] = SnapshotValue.String( detail ),
		["completed"] = SnapshotValue.Boolean( completed )
	} );

	private static HL2RPCommandArguments DeleteArguments( string objectiveId ) => new(
		new Dictionary<string, SnapshotValue>
		{
			["operation"] = SnapshotValue.Choice( HL2RPObjectiveCommand.DeleteOperation ),
			["objective"] = SnapshotValue.String( objectiveId )
		} );
}
