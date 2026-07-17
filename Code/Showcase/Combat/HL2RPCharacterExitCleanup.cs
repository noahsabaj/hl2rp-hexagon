#nullable enable

using System;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;

namespace HL2RP.V2.Showcase.Combat;

/// <summary>
/// The one canonical character-exit cleanup, applied uniformly by every lifecycle exit:
/// unload, disconnect, the inline load-switch path, and deletion. Combat health and the
/// death lifecycle are transient host state; an exit path that leaves either behind lets
/// the next load of the same character adopt stale damage or a stale death instead of
/// the fresh 100/100 every load is supposed to establish.
/// </summary>
public static class HL2RPCharacterExitCleanup
{
	public static CombatHealthRemoval Run(
		ConnectionId connectionId,
		CharacterId characterId,
		InventoryAccessService access,
		InteractionAuthorityService? interactions,
		CombatIntentService? combatIntent,
		CombatLifecycleService? combatLifecycle,
		CanonicalCombatHealthDirectory combatHealth)
	{
		ArgumentNullException.ThrowIfNull(access);
		ArgumentNullException.ThrowIfNull(combatHealth);
		access.RevokeCharacter(connectionId, characterId);
		interactions?.CharacterChanged(connectionId, characterId);
		combatIntent?.ClearCharacter(characterId);
		combatLifecycle?.ClearLifecycle(characterId);
		return combatHealth.Remove(characterId);
	}
}
