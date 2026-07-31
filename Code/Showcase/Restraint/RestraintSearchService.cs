#nullable enable

using System;
using System.Linq;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Showcase.Restraint;

public sealed record RestraintSearchSession(
	InteractionSessionId SessionId,
	CharacterId SearcherCharacterId,
	CharacterId TargetCharacterId,
	InventoryId TargetInventoryId);

/// <summary>
/// Shared host-side restraint authority. A persisted role is necessary but not
/// sufficient: the actor must also hold the restraint permission in the current
/// active-character authority snapshot, so switching characters revokes access.
/// </summary>
public sealed class RestraintPermissionAuthorizer
{
	private readonly DomainRepositories _repositories;
	private readonly IPermissionAuthorizer _permissions;

	public RestraintPermissionAuthorizer(
		DomainRepositories repositories,
		IPermissionAuthorizer permissions)
	{
		_repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
		_permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
	}

	public OperationResult Authorize(AccountId accountId, CharacterId characterId)
	{
		var character = _repositories.Characters.Find(DomainKeys.Character(characterId));
		if (character is null || character.Value.AccountId != accountId)
			return OperationResult.Failure(ErrorCode.Unauthorized,
				"Restraint actor binding is not canonical.");
		if (character.Value.Faction.Value is not (
			HL2RPIds.Factions.CivilProtection or HL2RPIds.Factions.Overwatch))
			return OperationResult.Failure(ErrorCode.PolicyDenied,
				"Only Civil Protection or Overwatch characters may search restraints.");
		return _permissions.HasPermission(accountId, characterId, HL2RPIds.Permissions.Restraint)
			? OperationResult.Success()
			: OperationResult.Failure(ErrorCode.Unauthorized,
				"The active character does not hold restraint permission.");
	}
}

/// <summary>
/// Character interaction surface shared by timed restraint actions and
/// continuing searches. It issues search capabilities only after current
/// restraint authority and target restraint have both been proven.
/// </summary>
public sealed class CharacterRestraintInteractable : IHexInteractable
{
	private readonly CharacterId _characterId;
	private readonly DomainRepositories _repositories;
	private readonly IRestraintStateReader _restraints;
	private readonly RestraintPermissionAuthorizer _authorization;

	public CharacterRestraintInteractable(
		CharacterId characterId,
		DomainRepositories repositories,
		IRestraintStateReader restraints,
		RestraintPermissionAuthorizer authorization)
	{
		_characterId = characterId;
		_repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
		_restraints = restraints ?? throw new ArgumentNullException(nameof(restraints));
		_authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
	}

	public InteractionTarget Target => InteractionTarget.Character(_characterId);
	public InteractionPolicy Policy => new() { SessionKind = InteractionSessionKind.CharacterSearch };

	public OperationResult<InteractionOffer> Authorize(ServerInteractionContext context)
	{
		var authorized = _authorization.Authorize(context.AccountId, context.CharacterId);
		if (authorized.Failed)
			return OperationResult<InteractionOffer>.Failure(
				authorized.Error!.Code, authorized.Error.Message);
		if (context.CharacterId == _characterId)
			return OperationResult<InteractionOffer>.Failure(ErrorCode.PolicyDenied,
				"A character cannot search themselves.");
		if (!_restraints.IsRestrained(_characterId))
			return OperationResult<InteractionOffer>.Failure(ErrorCode.PolicyDenied,
				"Search requires an actively restrained target.");
		var ownerIndex = _repositories.OwnerInventories.Find( DomainKeys.OwnerInventory(
			InventoryOwner.Character( _characterId ), InventoryRoles.Main ) );
		var inventory = ownerIndex is null ? null : _repositories.Inventories.Find(
			DomainKeys.Inventory( ownerIndex.Value.InventoryId ) )?.Value;
		if (inventory is null)
			return OperationResult<InteractionOffer>.Failure(ErrorCode.NotFound,
				"Restrained character inventory was not found.");
		return OperationResult<InteractionOffer>.Success(new InteractionOffer
		{
			SessionKind = InteractionSessionKind.CharacterSearch,
			InventoryGrants = new[]
			{
				new InteractionInventoryGrant(
					inventory.Id,
					InventoryCapability.View | InventoryCapability.Move | InventoryCapability.TransferOut,
					InventoryGrantKind.InteractionSession)
			}
		});
	}
}

public sealed class RestraintSearchService
{
	private static readonly InventoryCapability RequiredCapabilities =
		InventoryCapability.View | InventoryCapability.Move | InventoryCapability.TransferOut;

	private readonly DomainRepositories _repositories;
	private readonly InteractionAuthorityService _authority;
	private readonly InventoryAccessService _access;
	private readonly IRestraintStateReader _restraints;
	private readonly RestraintPermissionAuthorizer _authorization;

	public RestraintSearchService(
		DomainRepositories repositories,
		InteractionAuthorityService authority,
		InventoryAccessService access,
		IRestraintStateReader restraints,
		RestraintPermissionAuthorizer authorization)
	{
		_repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
		_authority = authority ?? throw new ArgumentNullException(nameof(authority));
		_access = access ?? throw new ArgumentNullException(nameof(access));
		_restraints = restraints ?? throw new ArgumentNullException(nameof(restraints));
		_authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
	}

	public OperationResult<RestraintSearchSession> Open(
		InventoryActor actor,
		CharacterId targetCharacterId,
		InventoryId targetInventoryId)
	{
		var authorized = _authorization.Authorize(actor.AccountId, actor.CharacterId);
		if (authorized.Failed)
			return OperationResult<RestraintSearchSession>.Failure(
				authorized.Error!.Code, authorized.Error.Message);
		if (targetCharacterId == actor.CharacterId)
			return OperationResult<RestraintSearchSession>.Failure(ErrorCode.PolicyDenied,
				"A character cannot search themselves.");
		if (!_restraints.IsRestrained(targetCharacterId))
			return OperationResult<RestraintSearchSession>.Failure(ErrorCode.PolicyDenied,
				"Search requires an actively restrained target.");
		var targetInventory = _repositories.Inventories.Find(DomainKeys.Inventory(targetInventoryId));
		if (targetInventory is null || targetInventory.Value.Owner != InventoryOwner.Character(targetCharacterId))
			return OperationResult<RestraintSearchSession>.Failure(ErrorCode.NotFound,
				"Target inventory is not the restrained character's main inventory.");
		var opened = _authority.Begin(actor.ConnectionId, actor.AccountId, actor.CharacterId,
			InteractionTarget.Character(targetCharacterId));
		if (opened.Failed)
			return OperationResult<RestraintSearchSession>.Failure(opened.Error!.Code, opened.Error.Message);
		if (opened.Value.Session is not InteractionSession session ||
			session.Kind != InteractionSessionKind.CharacterSearch ||
			!_access.Has(actor.ConnectionId, actor.CharacterId, targetInventoryId, RequiredCapabilities))
		{
			if (opened.Value.Session is not null) _authority.Close(opened.Value.Session.Id);
			return OperationResult<RestraintSearchSession>.Failure(ErrorCode.Unauthorized,
				"Search interactable did not grant the required session capabilities.");
		}
		return OperationResult<RestraintSearchSession>.Success(new RestraintSearchSession(
			session.Id, actor.CharacterId, targetCharacterId, targetInventoryId));
	}

	public OperationResult Continue(InventoryActor actor, RestraintSearchSession search)
	{
		ArgumentNullException.ThrowIfNull(search);
		var authorized = _authorization.Authorize(actor.AccountId, actor.CharacterId);
		if (authorized.Failed)
		{
			_authority.Close(search.SessionId);
			return OperationResult.Failure(authorized.Error!.Code, authorized.Error.Message);
		}
		if (search.SearcherCharacterId != actor.CharacterId || !_restraints.IsRestrained(search.TargetCharacterId))
		{
			_authority.Close(search.SessionId);
			return OperationResult.Failure(ErrorCode.Unauthorized,
				"Search session is stale or the target is no longer restrained.");
		}
		var continued = _authority.Continue(search.SessionId, actor.ConnectionId, actor.AccountId,
			actor.CharacterId, InteractionTarget.Character(search.TargetCharacterId));
		if (continued.Failed) return OperationResult.Failure(continued.Error!.Code, continued.Error.Message);
		return _access.Has(actor.ConnectionId, actor.CharacterId, search.TargetInventoryId, RequiredCapabilities)
			? OperationResult.Success()
			: OperationResult.Failure(ErrorCode.Unauthorized, "Search inventory capability was revoked.");
	}
}
