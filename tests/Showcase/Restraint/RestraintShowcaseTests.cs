#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;
using HL2RP.V2.Showcase.Restraint;

namespace HL2RP.V2.Tests.Showcase.Restraint;

[TestClass]
public sealed class RestraintShowcaseTests
{
	[TestMethod]
	public async Task ThreeSecondTicketRevalidatesAndCommitFailureConsumesNothing()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var tie = ShowcaseTestEnvironment.ZipTie();
		var officer = await environment.SeedCharacterAsync(200, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Unit, tie);
		var target = await environment.SeedCharacterAsync(201);
		var fixture = new InteractionFixture(environment, officer.Actor,
			InteractionTarget.Character(target.Actor.CharacterId));
		var service = new RestraintService(environment.Repositories, environment.Access,
			environment.Layout, fixture.Authority, environment.Clock);

		var ticket = service.Begin(officer.Actor, target.Actor.CharacterId, officer.InventoryId, tie.Id);
		Assert.IsTrue(ticket.Succeeded, ticket.Error?.Message);
		var early = await service.CompleteAsync(ticket.Value.TicketId, officer.Actor);
		Assert.AreEqual(ErrorCode.Conflict, early.Error!.Code);
		Assert.IsNotNull(environment.Repositories.Items.Find(DomainKeys.Item(tie.Id)));

		environment.Clock.Advance(RestraintService.RestraintDuration);
		fixture.World.LineOfSight = false;
		var occluded = await service.CompleteAsync(ticket.Value.TicketId, officer.Actor);
		Assert.AreEqual(ErrorCode.PolicyDenied, occluded.Error!.Code);
		Assert.IsFalse(service.IsRestrained(target.Actor.CharacterId));
		Assert.IsNotNull(environment.Repositories.Items.Find(DomainKeys.Item(tie.Id)));

		fixture.World.LineOfSight = true;
		var failedTicket = service.Begin(officer.Actor, target.Actor.CharacterId, officer.InventoryId, tie.Id).Value;
		environment.Clock.Advance(RestraintService.RestraintDuration);
		environment.Provider.FailNextCommit();
		var failedCommit = await service.CompleteAsync(failedTicket.TicketId, officer.Actor);
		Assert.IsTrue(failedCommit.Failed);
		Assert.IsFalse(service.IsRestrained(target.Actor.CharacterId));
		Assert.IsNotNull(environment.Repositories.Items.Find(DomainKeys.Item(tie.Id)));

		var committedTicket = service.Begin(officer.Actor, target.Actor.CharacterId, officer.InventoryId, tie.Id).Value;
		var impersonated = await service.CompleteAsync(committedTicket.TicketId,
			officer.Actor with { ConnectionId = ConnectionId.New() });
		Assert.AreEqual(ErrorCode.Unauthorized, impersonated.Error!.Code);
		environment.Clock.Advance(RestraintService.RestraintDuration);
		var committed = await service.CompleteAsync(committedTicket.TicketId, officer.Actor);
		Assert.IsTrue(committed.Succeeded, committed.Error?.Message);
		Assert.IsTrue(service.IsRestrained(target.Actor.CharacterId));
		Assert.IsNull(environment.Repositories.Items.Find(DomainKeys.Item(tie.Id)));
		Assert.IsNull(environment.Repositories.Inventories.Find(DomainKeys.Inventory(officer.InventoryId))!.Value.Find(tie.Id));
	}

	[TestMethod]
	public async Task InterleavedAccessRevocationRejectsTimedRestraintCompletionAtomically()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var tie = ShowcaseTestEnvironment.ZipTie();
		var officer = await environment.SeedCharacterAsync(
			205, HL2RPIds.Factions.CivilProtection, HL2RPIds.Classes.Unit, tie);
		var target = await environment.SeedCharacterAsync(206);
		var fixture = new InteractionFixture(environment, officer.Actor,
			InteractionTarget.Character(target.Actor.CharacterId));
		var service = new RestraintService(environment.Repositories, environment.Access,
			environment.Layout, fixture.Authority, environment.Clock);
		var ticket = service.Begin(
			officer.Actor, target.Actor.CharacterId, officer.InventoryId, tie.Id).Value;
		environment.Clock.Advance(RestraintService.RestraintDuration);
		environment.Provider.InterleaveNextCommit(() =>
		{
			environment.Access.RevokeConnection(officer.Actor.ConnectionId);
			return Task.CompletedTask;
		});

		var result = await service.CompleteAsync(ticket.TicketId, officer.Actor);

		Assert.AreEqual(ErrorCode.Conflict, result.Error!.Code);
		Assert.IsFalse(service.IsRestrained(target.Actor.CharacterId));
		Assert.IsNotNull(environment.Repositories.Items.Find(DomainKeys.Item(tie.Id)));
		Assert.IsNotNull(environment.Repositories.Inventories.Find(
			DomainKeys.Inventory(officer.InventoryId))!.Value.Find(tie.Id));
		Assert.IsNull(environment.Repositories.CharacterReferences.Find(
			RestraintService.DocumentKey(target.Actor.CharacterId)));
	}

	[TestMethod]
	public async Task RoleSelfRangeAndSearchCapabilitiesFailClosedAndRevokeOnUnrestrain()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var citizenTie = ShowcaseTestEnvironment.ZipTie();
		var citizen = await environment.SeedCharacterAsync(202, items: new[] { citizenTie });
		var officerTie = ShowcaseTestEnvironment.ZipTie();
		var officer = await environment.SeedCharacterAsync(203, HL2RPIds.Factions.Overwatch, null, officerTie);
		var target = await environment.SeedCharacterAsync(204);
		var fixture = new InteractionFixture(environment, officer.Actor,
			InteractionTarget.Character(target.Actor.CharacterId));
		var service = new RestraintService(environment.Repositories, environment.Access,
			environment.Layout, fixture.Authority, environment.Clock);

		var deniedRole = service.Begin(citizen.Actor, target.Actor.CharacterId, citizen.InventoryId, citizenTie.Id);
		Assert.AreEqual(ErrorCode.PolicyDenied, deniedRole.Error!.Code);
		var self = service.Begin(officer.Actor, officer.Actor.CharacterId, officer.InventoryId, officerTie.Id);
		Assert.AreEqual(ErrorCode.PolicyDenied, self.Error!.Code);
		fixture.World.TargetPosition = new WorldPoint(131, 0, 0);
		var distant = service.Begin(officer.Actor, target.Actor.CharacterId, officer.InventoryId, officerTie.Id);
		Assert.AreEqual(ErrorCode.PolicyDenied, distant.Error!.Code);
		fixture.World.TargetPosition = new WorldPoint(10, 0, 0);
		var ticket = service.Begin(officer.Actor, target.Actor.CharacterId, officer.InventoryId, officerTie.Id).Value;
		environment.Clock.Advance(RestraintService.RestraintDuration);
		Assert.IsTrue((await service.CompleteAsync(ticket.TicketId, officer.Actor)).Succeeded);

		var reader = new RestraintStateReader(environment.Repositories);
		var guard = new RestrainedActionGuard(reader);
		Assert.AreEqual(ErrorCode.PolicyDenied,
			guard.Authorize(target.Actor.CharacterId, RestrainedActionKind.Inventory).Error!.Code);
		fixture.Interactable.Offer = new InteractionOffer
		{
			SessionKind = InteractionSessionKind.CharacterSearch,
			InventoryGrants = new[]
			{
				new InteractionInventoryGrant(target.InventoryId,
					InventoryCapability.View | InventoryCapability.Move | InventoryCapability.TransferOut,
					InventoryGrantKind.InteractionSession)
			}
		};
		fixture.Interactable.Policy = fixture.Interactable.Policy with
		{
			SessionKind = InteractionSessionKind.CharacterSearch
		};
		var searchAuthorization = new RestraintPermissionAuthorizer(
			environment.Repositories, new FixedPermissionAuthorizer(true));
		var searchService = new RestraintSearchService(environment.Repositories, fixture.Authority,
			environment.Access, reader, searchAuthorization);
		var search = searchService.Open(officer.Actor, target.Actor.CharacterId, target.InventoryId);
		Assert.IsTrue(search.Succeeded, search.Error?.Message);
		Assert.IsTrue(environment.Access.Has(officer.Actor.ConnectionId, officer.Actor.CharacterId,
			target.InventoryId, InventoryCapability.View | InventoryCapability.TransferOut));

		var forged = searchService.Continue(officer.Actor with { ConnectionId = ConnectionId.New() }, search.Value);
		Assert.AreEqual(ErrorCode.Unauthorized, forged.Error!.Code);
		Assert.IsTrue(environment.Access.Has(officer.Actor.ConnectionId, officer.Actor.CharacterId,
			target.InventoryId, InventoryCapability.View));
		Assert.IsTrue(searchService.Continue(officer.Actor, search.Value).Succeeded);
		fixture.World.TargetPosition = new WorldPoint(131, 0, 0);
		var remoteRelease = await service.UnrestrainAsync(officer.Actor, target.Actor.CharacterId);
		Assert.AreEqual(ErrorCode.PolicyDenied, remoteRelease.Error!.Code);
		Assert.IsTrue(reader.IsRestrained(target.Actor.CharacterId));
		Assert.IsTrue(environment.Access.Has(officer.Actor.ConnectionId, officer.Actor.CharacterId,
			target.InventoryId, InventoryCapability.View));
		fixture.World.TargetPosition = new WorldPoint(10, 0, 0);
		fixture.World.LineOfSight = false;
		var occludedRelease = await service.UnrestrainAsync(officer.Actor, target.Actor.CharacterId);
		Assert.AreEqual(ErrorCode.PolicyDenied, occludedRelease.Error!.Code);
		Assert.IsTrue(reader.IsRestrained(target.Actor.CharacterId));
		fixture.World.LineOfSight = true;
		var released = await service.UnrestrainAsync(officer.Actor, target.Actor.CharacterId);
		Assert.IsTrue(released.Succeeded, released.Error?.Message);
		Assert.IsFalse(reader.IsRestrained(target.Actor.CharacterId));
		Assert.IsFalse(environment.Access.Has(officer.Actor.ConnectionId, officer.Actor.CharacterId,
			target.InventoryId, InventoryCapability.View));
	}

	[TestMethod]
	public async Task GenericAndDirectSearchRequireCurrentActiveRestraintAuthority()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var citizen = await environment.SeedCharacterAsync(210);
		var civilProtection = await environment.SeedCharacterAsync(
			211, HL2RPIds.Factions.CivilProtection, HL2RPIds.Classes.Unit);
		var overwatch = await environment.SeedCharacterAsync(212, HL2RPIds.Factions.Overwatch);
		var target = await environment.SeedCharacterAsync(213);
		var stored = await new CharacterReferenceMutationService(environment.Repositories).UpsertAsync(
				RestraintService.DocumentKey(target.Actor.CharacterId),
				new CharacterReferenceRecord
				{
					Category = RestraintService.ReferenceCategory,
					CharacterId = target.Actor.CharacterId,
					RelatedCharacterId = civilProtection.Actor.CharacterId,
					State = HL2RPPersistence.Payload(
						HL2RPPersistence.Restraint,
						new RestraintReferenceState
						{
							RestrainedAtUtc = environment.Clock.UtcNow,
							Active = true
						})
				});
		Assert.IsTrue(stored.Succeeded, stored.Error?.Message);

		var authorities = new CanonicalChatAuthorityDirectory();
		authorities.Publish(1, new[]
		{
			LiveAuthority(citizen, HL2RPIds.Factions.Citizen),
			LiveAuthority(civilProtection, HL2RPIds.Factions.CivilProtection,
				HL2RPIds.Permissions.Restraint),
			LiveAuthority(overwatch, HL2RPIds.Factions.Overwatch,
				HL2RPIds.Permissions.Restraint)
		});
		var reader = new RestraintStateReader(environment.Repositories);
		var authorization = new RestraintPermissionAuthorizer(environment.Repositories, authorities);
		var interactable = new CharacterRestraintInteractable(
			target.Actor.CharacterId, environment.Repositories, reader, authorization);

		InteractionAuthorityService AuthorityFor(InventoryActor actor)
		{
			var sessions = new InteractionSessionService(environment.Clock);
			return new InteractionAuthorityService(
				new FakeWorld(actor, interactable.Target),
				new FakeDirectory(interactable),
				sessions,
				environment.Access,
				ShowcaseTestEnvironment.AllowPolicy<ServerInteractionContext>(),
				environment.Clock);
		}

		var citizenAuthority = AuthorityFor(citizen.Actor);
		var hostileGeneric = citizenAuthority.Begin(
			citizen.Actor.ConnectionId,
			citizen.Actor.AccountId,
			citizen.Actor.CharacterId,
			interactable.Target);
		Assert.AreEqual(ErrorCode.PolicyDenied, hostileGeneric.Error!.Code);
		var citizenSearch = new RestraintSearchService(
			environment.Repositories, citizenAuthority, environment.Access, reader, authorization);
		var hostileDirect = citizenSearch.Open(
			citizen.Actor, target.Actor.CharacterId, target.InventoryId);
		Assert.AreEqual(ErrorCode.PolicyDenied, hostileDirect.Error!.Code);
		Assert.IsFalse(environment.Access.Has(
			citizen.Actor.ConnectionId,
			citizen.Actor.CharacterId,
			target.InventoryId,
			InventoryCapability.View));

		var civilProtectionAuthority = AuthorityFor(civilProtection.Actor);
		var cpGeneric = civilProtectionAuthority.Begin(
			civilProtection.Actor.ConnectionId,
			civilProtection.Actor.AccountId,
			civilProtection.Actor.CharacterId,
			interactable.Target);
		Assert.IsTrue(cpGeneric.Succeeded, cpGeneric.Error?.Message);
		Assert.IsTrue(environment.Access.Has(
			civilProtection.Actor.ConnectionId,
			civilProtection.Actor.CharacterId,
			target.InventoryId,
			InventoryCapability.View | InventoryCapability.TransferOut));
		civilProtectionAuthority.Close(cpGeneric.Value.Session!.Id);
		Assert.IsFalse(environment.Access.Has(
			civilProtection.Actor.ConnectionId,
			civilProtection.Actor.CharacterId,
			target.InventoryId,
			InventoryCapability.View));

		var overwatchAuthority = AuthorityFor(overwatch.Actor);
		var overwatchSearch = new RestraintSearchService(
			environment.Repositories, overwatchAuthority, environment.Access, reader, authorization);
		var opened = overwatchSearch.Open(overwatch.Actor, target.Actor.CharacterId, target.InventoryId);
		Assert.IsTrue(opened.Succeeded, opened.Error?.Message);
		Assert.IsTrue(environment.Access.Has(
			overwatch.Actor.ConnectionId,
			overwatch.Actor.CharacterId,
			target.InventoryId,
			InventoryCapability.View | InventoryCapability.TransferOut));

		authorities.Publish(2, new[]
		{
			LiveAuthority(citizen, HL2RPIds.Factions.Citizen),
			LiveAuthority(civilProtection, HL2RPIds.Factions.CivilProtection,
				HL2RPIds.Permissions.Restraint),
			new LiveChatAuthority(
				overwatch.Actor.ConnectionId,
				overwatch.Actor.AccountId,
				CharacterId.New(),
				new FactionId(HL2RPIds.Factions.Citizen))
		});
		var switched = overwatchSearch.Continue(overwatch.Actor, opened.Value);
		Assert.AreEqual(ErrorCode.Unauthorized, switched.Error!.Code);
		Assert.IsFalse(environment.Access.Has(
			overwatch.Actor.ConnectionId,
			overwatch.Actor.CharacterId,
			target.InventoryId,
			InventoryCapability.View));

		var released = await new CharacterReferenceMutationService(environment.Repositories).DeleteAsync(
			RestraintService.DocumentKey(target.Actor.CharacterId));
		Assert.IsTrue(released.Succeeded, released.Error?.Message);
		var unrestrainedGeneric = civilProtectionAuthority.Begin(
			civilProtection.Actor.ConnectionId,
			civilProtection.Actor.AccountId,
			civilProtection.Actor.CharacterId,
			interactable.Target);
		Assert.AreEqual(ErrorCode.PolicyDenied, unrestrainedGeneric.Error!.Code);
		Assert.IsFalse(environment.Access.Has(
			civilProtection.Actor.ConnectionId,
			civilProtection.Actor.CharacterId,
			target.InventoryId,
			InventoryCapability.View));
	}

	[TestMethod]
	public async Task RestraintTicketCanOnlyBeCancelledByItsBoundActor()
	{
		await using var environment = await ShowcaseTestEnvironment.CreateAsync();
		var tie = ShowcaseTestEnvironment.ZipTie();
		var officer = await environment.SeedCharacterAsync(205, HL2RPIds.Factions.CivilProtection,
			HL2RPIds.Classes.Unit, tie);
		var target = await environment.SeedCharacterAsync(206);
		var fixture = new InteractionFixture(environment, officer.Actor,
			InteractionTarget.Character(target.Actor.CharacterId));
		var service = new RestraintService(environment.Repositories, environment.Access,
			environment.Layout, fixture.Authority, environment.Clock);
		var ticket = service.Begin(officer.Actor, target.Actor.CharacterId, officer.InventoryId, tie.Id).Value;

		Assert.AreEqual(ErrorCode.Unauthorized,
			service.Cancel(ticket.TicketId, officer.Actor with { ConnectionId = ConnectionId.New() }).Error!.Code);
		Assert.IsTrue(service.Cancel(ticket.TicketId, officer.Actor).Succeeded);
		environment.Clock.Advance(RestraintService.RestraintDuration);
		Assert.AreEqual(ErrorCode.Unauthorized,
			(await service.CompleteAsync(ticket.TicketId, officer.Actor)).Error!.Code);
		Assert.IsNotNull(environment.Repositories.Items.Find(DomainKeys.Item(tie.Id)));
	}

	private sealed class InteractionFixture
	{
		public InteractionFixture(ShowcaseTestEnvironment environment, InventoryActor actor, InteractionTarget target)
		{
			World = new FakeWorld(actor, target);
			Interactable = new FakeInteractable(target);
			var sessions = new InteractionSessionService(environment.Clock);
			Authority = new InteractionAuthorityService(World, new FakeDirectory(Interactable), sessions,
				environment.Access, ShowcaseTestEnvironment.AllowPolicy<ServerInteractionContext>(), environment.Clock);
		}

		public FakeWorld World { get; }
		public FakeInteractable Interactable { get; }
		public InteractionAuthorityService Authority { get; }
	}

	private sealed class FakeWorld : IServerInteractionWorld
	{
		private readonly InventoryActor _actor;
		private readonly InteractionTarget _target;

		public FakeWorld(InventoryActor actor, InteractionTarget target)
		{
			_actor = actor;
			_target = target;
		}

		public bool LineOfSight { get; set; } = true;
		public WorldPoint TargetPosition { get; set; } = new(10, 0, 0);

		public bool TryBuildContext(ConnectionId connectionId, CharacterId characterId,
			InteractionTarget target, out ServerInteractionContext context)
		{
			if (connectionId != _actor.ConnectionId || characterId != _actor.CharacterId || target != _target)
			{
				context = default!;
				return false;
			}
			context = new ServerInteractionContext
			{
				ConnectionId = connectionId,
				AccountId = _actor.AccountId,
				CharacterId = characterId,
				Target = target,
				ActorPosition = new WorldPoint(0, 0, 0),
				TargetPosition = TargetPosition,
				IsAlive = true,
				IsRestrained = false
			};
			return true;
		}

		public bool HasLineOfSight(ServerInteractionContext context) => LineOfSight;
	}

	private static LiveChatAuthority LiveAuthority(
		SeededCharacter character,
		string faction,
		params string[] permissions) => new(
			character.Actor.ConnectionId,
			character.Actor.AccountId,
			character.Actor.CharacterId,
			new FactionId(faction),
			permissions);

	private sealed class FixedPermissionAuthorizer : IPermissionAuthorizer
	{
		private readonly bool _allowed;
		public FixedPermissionAuthorizer(bool allowed) => _allowed = allowed;
		public bool HasPermission(AccountId accountId, CharacterId characterId, string permissionId) =>
			_allowed;
	}

	private sealed class FakeDirectory : IInteractionDirectory
	{
		private readonly IHexInteractable _interactable;
		public FakeDirectory(IHexInteractable interactable) => _interactable = interactable;
		public bool TryResolve(InteractionTarget target, out IHexInteractable interactable)
		{
			interactable = _interactable;
			return target == _interactable.Target;
		}
	}

	private sealed class FakeInteractable : IHexInteractable
	{
		public FakeInteractable(InteractionTarget target)
		{
			Target = target;
			Policy = new InteractionPolicy();
		}

		public InteractionTarget Target { get; }
		public InteractionPolicy Policy { get; set; }
		public InteractionOffer Offer { get; set; } = new();
		public OperationResult<InteractionOffer> Authorize(ServerInteractionContext context) =>
			OperationResult<InteractionOffer>.Success(Offer);
	}
}
