#nullable enable

using Hexagon.V2.Composition;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Features;
using HL2RP.V2.Runtime;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Features;

[TestClass]
public sealed class AccountEntitlementServiceTests
{
	[TestMethod]
	public async Task CurrentCommittedGrantsAuthorizeOnlyTheirFactionAndRevokeBlocksFutureCreation()
	{
		await using var provider = await CreateProviderAsync();
		var clock = new MutableClock();
		var administrator = new AccountId( 10 );
		var target = new AccountId( 20 );
		var attacker = new AccountId( 30 );
		var known = new HashSet<AccountId> { target };
		var changes = new RecordingHandler<HL2RPAccountEntitlementChanged>();
		var audits = new RecordingHandler<AdminAuditFact>();
		var service = new HL2RPAccountEntitlementService(
			provider, clock,
			actor => actor.AccountId == administrator,
			known.Contains,
			changes.Bus(), audits.Bus() );
		var factory = new HL2RPCharacterStateFactory( service );

		Assert.AreEqual( HL2RPWhitelist.None, service.GetWhitelists( target ) );
		Assert.IsTrue( factory.Create( Context( target, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Recruit ) ).Failed );

		var denied = await service.GrantAsync(
			new HL2RPEntitlementAdministrator( attacker, null ), target,
			HL2RPWhitelist.CivilProtection, DocumentRevision.None );
		Assert.IsTrue( denied.Failed );
		Assert.AreEqual( ErrorCode.Unauthorized, denied.Error?.Code );

		var adminActor = new HL2RPEntitlementAdministrator( administrator, null );
		var civilProtection = await service.GrantAsync(
			adminActor, target, HL2RPWhitelist.CivilProtection, DocumentRevision.None );
		Assert.IsTrue( civilProtection.Succeeded, civilProtection.Error?.Message );
		var originalPlan = factory.Create( Context(
			target, HL2RPIds.Factions.CivilProtection, HL2RPIds.Classes.Recruit ) );
		Assert.IsTrue( originalPlan.Succeeded, originalPlan.Error?.Message );
		Assert.IsTrue( factory.Create( Context( target, HL2RPIds.Factions.Overwatch ) ).Failed );

		var overwatch = await service.GrantAsync(
			adminActor, target, HL2RPWhitelist.Overwatch, civilProtection.Value.Revision );
		Assert.IsTrue( overwatch.Succeeded, overwatch.Error?.Message );
		Assert.IsTrue( factory.Create( Context( target, HL2RPIds.Factions.Overwatch ) ).Succeeded );

		var cityAdministration = await service.GrantAsync(
			adminActor, target, HL2RPWhitelist.CityAdministration, overwatch.Value.Revision );
		Assert.IsTrue( cityAdministration.Succeeded, cityAdministration.Error?.Message );
		Assert.IsTrue( factory.Create( Context( target, HL2RPIds.Factions.CityAdministration ) ).Succeeded );

		var revoked = await service.RevokeAsync(
			adminActor, target, HL2RPWhitelist.CivilProtection, cityAdministration.Value.Revision );
		Assert.IsTrue( revoked.Succeeded, revoked.Error?.Message );
		Assert.IsTrue( factory.Create( Context(
			target, HL2RPIds.Factions.CivilProtection, HL2RPIds.Classes.Recruit ) ).Failed );
		var originalState = HL2RPPersistence.CharacterState.Deserialize(
			originalPlan.Value.State.Data, originalPlan.Value.State.TypeVersion );
		Assert.IsTrue( originalState.Whitelists.HasFlag( HL2RPWhitelist.CivilProtection ),
			"Revocation must not rewrite an already-created character state." );
		Assert.HasCount( 4, changes.Events );
		Assert.HasCount( 4, audits.Events );
		Assert.IsTrue( changes.Events.Zip( audits.Events ).All( pair =>
			pair.First.CommitSequence == pair.Second.CommitSequence ) );
	}

	[TestMethod]
	public async Task BootstrapOperatorCanGrantFirstAdministratorButArbitraryClientCannotSelfGrant()
	{
		await using var provider = await CreateProviderAsync();
		var operators = HL2RPBootstrapOperatorDirectory.Parse( "1001, 1002" );
		Assert.IsTrue( operators.Succeeded );
		Assert.IsTrue( operators.Value.Contains( new AccountId( 1001 ) ) );
		Assert.IsTrue( HL2RPBootstrapOperatorDirectory.Parse( "1001,not-an-account" ).Failed );
		var target = new AccountId( 2001 );
		var audits = new RecordingHandler<AdminAuditFact>();
		var service = new HL2RPAccountEntitlementService(
			provider, new MutableClock(),
			actor => operators.Value.Contains( actor.AccountId ),
			account => account == target,
			audit: audits.Bus() );

		var selfGrant = await service.GrantAsync(
			new HL2RPEntitlementAdministrator( target, null ), target,
			HL2RPWhitelist.CityAdministration, DocumentRevision.None );
		Assert.AreEqual( ErrorCode.Unauthorized, selfGrant.Error?.Code );
		var bootstrapGrant = await service.GrantAsync(
			new HL2RPEntitlementAdministrator( new AccountId( 1001 ), null ), target,
			HL2RPWhitelist.CityAdministration, DocumentRevision.None );
		Assert.IsTrue( bootstrapGrant.Succeeded, bootstrapGrant.Error?.Message );
		Assert.HasCount( 1, audits.Events );
		Assert.IsNull( audits.Events[0].ActorCharacterId );
		Assert.AreEqual( HL2RPFeatureOperation.GrantAccountEntitlement, audits.Events[0].Operation );
	}

	[TestMethod]
	public async Task UnknownTargetsFlagsStaleRevisionsAndFailedCommitsFailClosedWithoutEvents()
	{
		var inner = new InMemoryPersistenceProvider( CreateTypes() );
		var provider = new FaultPersistenceProvider( inner );
		await provider.InitializeAsync();
		await using var disposal = provider;
		var administrator = new AccountId( 10 );
		var target = new AccountId( 20 );
		var changes = new RecordingHandler<HL2RPAccountEntitlementChanged>();
		var audits = new RecordingHandler<AdminAuditFact>();
		var service = new HL2RPAccountEntitlementService(
			provider, new MutableClock(),
			actor => actor.AccountId == administrator,
			account => account == target,
			changes.Bus(), audits.Bus() );
		var actor = new HL2RPEntitlementAdministrator( administrator, CharacterId.New() );

		var unknownTarget = await service.GrantAsync(
			actor, new AccountId( 999 ), HL2RPWhitelist.CivilProtection, DocumentRevision.None );
		Assert.AreEqual( ErrorCode.NotFound, unknownTarget.Error?.Code );
		var unknownFlag = await service.GrantAsync(
			actor, target, (HL2RPWhitelist)8, DocumentRevision.None );
		Assert.AreEqual( ErrorCode.InvalidArgument, unknownFlag.Error?.Code );

		provider.FailNextCommit();
		var failedCommit = await service.GrantAsync(
			actor, target, HL2RPWhitelist.CivilProtection, DocumentRevision.None );
		Assert.IsTrue( failedCommit.Failed );
		Assert.AreEqual( HL2RPWhitelist.None, service.GetWhitelists( target ) );
		Assert.IsEmpty( changes.Events );
		Assert.IsEmpty( audits.Events );

		// A durability failure makes the provider fatal, so use a healthy provider
		// to prove optimistic revision rejection independently.
		await using var healthy = await CreateProviderAsync();
		var revisions = new HL2RPAccountEntitlementService(
			healthy, new MutableClock(), _ => true, account => account == target );
		var granted = await revisions.GrantAsync(
			actor, target, HL2RPWhitelist.CivilProtection, DocumentRevision.None );
		Assert.IsTrue( granted.Succeeded );
		var stale = await revisions.GrantAsync(
			actor, target, HL2RPWhitelist.Overwatch, DocumentRevision.None );
		Assert.AreEqual( ErrorCode.Conflict, stale.Error?.Code );
		Assert.AreEqual( HL2RPWhitelist.CivilProtection, revisions.GetWhitelists( target ) );
	}

	[TestMethod]
	public async Task EntitlementRoundTripsThroughWalCheckpointRecoveryAndDigest()
	{
		var storage = new InMemoryPersistenceStorage();
		var target = new AccountId( 20 );
		string digest;
		DocumentRevision revision;
		await using ( var first = CreateFileProvider( storage ) )
		{
			await first.InitializeAsync();
			var service = new HL2RPAccountEntitlementService(
				first, new MutableClock(), _ => true, account => account == target );
			var granted = await service.GrantAsync(
				new HL2RPEntitlementAdministrator( new AccountId( 10 ), null ), target,
				HL2RPWhitelist.Overwatch, DocumentRevision.None );
			Assert.IsTrue( granted.Succeeded, granted.Error?.Message );
			revision = granted.Value.Revision;
			digest = HL2RPRuntimeProjection.RecoveryDigest( new DomainRepositories( first ) );
		}

		await using var recovered = CreateFileProvider( storage );
		await recovered.InitializeAsync();
		var recoveredService = new HL2RPAccountEntitlementService(
			recovered, new MutableClock(), _ => false, _ => false );
		var observed = recoveredService.Observe( target );
		Assert.IsTrue( observed.Succeeded, observed.Error?.Message );
		Assert.AreEqual( HL2RPWhitelist.Overwatch, observed.Value.Flags );
		Assert.AreEqual( revision, observed.Value.Revision );
		Assert.AreEqual( digest, HL2RPRuntimeProjection.RecoveryDigest( new DomainRepositories( recovered ) ) );
	}

	[TestMethod]
	public async Task StartupValidationRejectsWrongKeyAndCodecRejectsUnknownFlagsAndNonUtc()
	{
		await using var provider = await CreateProviderAsync();
		var repository = provider.Repository<HL2RPAccountEntitlementRecord>( HL2RPAccountEntitlements.Collection );
		var unit = provider.BeginUnitOfWork();
		unit.Create( repository, "wrong-key", Record( new AccountId( 20 ), HL2RPWhitelist.CivilProtection ) );
		var committed = await HL2RPUnitOfWork.CommitAndDisposeAsync( unit );
		Assert.IsTrue( committed.Succeeded );
		var service = new HL2RPAccountEntitlementService(
			provider, new MutableClock(), _ => false, _ => false );
		Assert.AreEqual( ErrorCode.PersistedTypeInvalid, service.ValidateAll().Error?.Code );

		Assert.Throws<InvalidOperationException>( () =>
			HL2RPPersistence.AccountEntitlement.PrepareForPublication(
				Record( new AccountId( 21 ), (HL2RPWhitelist)8 ) ) );
		Assert.Throws<InvalidOperationException>( () =>
			HL2RPPersistence.AccountEntitlement.PrepareForPublication(
				Record( new AccountId( 22 ), HL2RPWhitelist.Overwatch ) with
				{
					UpdatedAtUtc = new DateTimeOffset( 2030, 1, 2, 3, 4, 5, TimeSpan.FromHours( 1 ) )
				} ) );
	}

	private static CharacterCreationContext Context(
		AccountId accountId,
		string faction,
		string? characterClass = null ) => new(
		accountId,
		new CharacterCreationRequest
		{
			Name = "Verification Citizen",
			Description = "A sufficiently detailed character description.",
			Model = new DefinitionId( faction switch
			{
				HL2RPIds.Factions.CivilProtection => HL2RPIds.Models.CivilProtectionUnit,
				HL2RPIds.Factions.Overwatch => HL2RPIds.Models.OverwatchSoldier,
				HL2RPIds.Factions.CityAdministration => HL2RPIds.Models.CityAdministrator,
				_ => HL2RPIds.Models.Citizen01
			} ),
			Faction = new FactionId( faction ),
			Class = characterClass is null ? null : new ClassId( characterClass ),
			Fields = new Dictionary<string, CreationValue>( StringComparer.Ordinal )
			{
				[HL2RPIds.CreationFields.Age] = CreationValue.Integer( 30 ),
				[HL2RPIds.CreationFields.Pronouns] = CreationValue.String( "they/them" ),
				[HL2RPIds.CreationFields.Origin] = CreationValue.Choice( "city_17" )
			}
		},
		0,
		new DateTimeOffset( 2030, 1, 2, 3, 4, 5, TimeSpan.Zero ) );

	private static HL2RPAccountEntitlementRecord Record( AccountId accountId, HL2RPWhitelist flags ) => new()
	{
		AccountId = accountId,
		Flags = flags,
		UpdatedByAccountId = new AccountId( 10 ),
		UpdatedByCharacterId = null,
		UpdatedAtUtc = new DateTimeOffset( 2030, 1, 2, 3, 4, 5, TimeSpan.Zero )
	};

	private static async Task<InMemoryPersistenceProvider> CreateProviderAsync()
	{
		var provider = new InMemoryPersistenceProvider( CreateTypes() );
		await provider.InitializeAsync();
		return provider;
	}

	private static FileSystemPersistenceProvider CreateFileProvider( IPersistenceStorage storage ) => new(
		storage,
		new FileSystemPersistenceOptions( HL2RPIds.Schema )
		{
			CheckpointEveryCommits = 1,
			RetainedCheckpointGenerations = 2
		},
		CreateTypes() );

	private static PersistedTypeRegistry CreateTypes()
	{
		var compiled = SchemaCompiler.Compile( new HL2RPSchema() );
		Assert.IsTrue( compiled.Succeeded, compiled.Error?.Message );
		var binding = SchemaPersistenceAdapter.Bind(
			compiled.Value,
			new PersistedTypeRegistry().RegisterHexagonDomainTypes(),
			HL2RPPersistence.Codecs );
		Assert.IsTrue( binding.Succeeded, binding.Error?.Message );
		return binding.Value.Types;
	}
}
