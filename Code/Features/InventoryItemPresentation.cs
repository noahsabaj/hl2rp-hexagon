#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel.Definitions;
using Hexagon.V2.Networking;
using HL2RP.V2.Domain;
using HL2RP.V2.Schema;
using HL2RP.V2.Showcase.Combat;

namespace HL2RP.V2.Features;

public static class HL2RPInventoryItemFields
{
	public const string AmmunitionRounds = "ammunition.rounds";
	public const string FlashlightChargePermille = "flashlight.charge_permille";
	public const string FlashlightPowered = "flashlight.powered";
	public const string NoteBody = "note.body";
	public const string NoteOwnerCharacterId = "note.owner_character_id";
	public const string NoteUpdatedAtUnixMilliseconds = "note.updated_at_unix_milliseconds";
	public const string PermitExpiresAtUnixMilliseconds = "permit.expires_at_unix_milliseconds";
	public const string PermitKind = "permit.kind";
	public const string PermitValid = "permit.valid";
	public const string PistolEquipped = "pistol.equipped";
	public const string PistolLastFiredAtUnixMilliseconds = "pistol.last_fired_at_unix_milliseconds";
	public const string PistolMagazineRounds = "pistol.magazine_rounds";
	public const string PistolRaised = "pistol.raised";
	public const string RadioFrequency = "radio.frequency";
	public const string RadioPowered = "radio.powered";
	public const string RequestReadyAtUnixMilliseconds = "request.ready_at_unix_milliseconds";
	public const string RequestPowered = "request.powered";
	public const string VestDamageReductionPermille = "vest.damage_reduction_permille";
	public const string VestDurability = "vest.durability";
	public const string VestEquipped = "vest.equipped";
	public const string VendorSellEnabled = "vendor.sell.enabled";
	public const string VendorSellPayout = "vendor.sell.payout";
	public const string VendorSellReason = "vendor.sell.reason";
}

/// <summary>
/// Projects only closed client-safe scalars for one item. The item identity and
/// its state travel in the same immutable snapshot, avoiding order-dependent
/// private-state aliases when an inventory contains multiple similar items.
/// </summary>
public static class HL2RPInventoryItemState
{
	public static IReadOnlyDictionary<string, SnapshotValue> Project(
		ItemRecord item,
		CharacterId viewerCharacterId,
		DateTimeOffset nowUtc )
	{
		ArgumentNullException.ThrowIfNull( item );
		var values = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal );
		try
		{
			switch ( item.Definition.Value )
			{
				case HL2RPIds.Items.Pistol when item.Traits.TryGetValue( "pistol", out var payload ):
				{
					var state = HL2RPPersistence.Pistol.Deserialize( payload.Data, payload.TypeVersion );
					values[HL2RPInventoryItemFields.PistolMagazineRounds] = SnapshotValue.Integer( state.MagazineRounds );
					values[HL2RPInventoryItemFields.PistolEquipped] = SnapshotValue.Boolean( state.Equipped );
					values[HL2RPInventoryItemFields.PistolRaised] = SnapshotValue.Boolean( state.Raised );
					values[HL2RPInventoryItemFields.PistolLastFiredAtUnixMilliseconds] = SnapshotValue.Integer(
						state.LastFiredAtUtc?.ToUnixTimeMilliseconds() ?? -1 );
					break;
				}
				case HL2RPIds.Items.PistolAmmunition when item.Traits.TryGetValue( "ammunition", out var payload ):
					values[HL2RPInventoryItemFields.AmmunitionRounds] = SnapshotValue.Integer(
						HL2RPPersistence.PistolAmmunition.Deserialize( payload.Data, payload.TypeVersion ).Rounds );
					break;
				case HL2RPIds.Items.ProtectiveVest when item.Traits.TryGetValue( "vest", out var payload ):
				{
					var state = HL2RPPersistence.ProtectiveVest.Deserialize( payload.Data, payload.TypeVersion );
					values[HL2RPInventoryItemFields.VestDurability] = SnapshotValue.Integer( state.Durability );
					values[HL2RPInventoryItemFields.VestDamageReductionPermille] = SnapshotValue.Integer( state.DamageReductionPermille );
					values[HL2RPInventoryItemFields.VestEquipped] = SnapshotValue.Boolean( state.Equipped );
					break;
				}
				case HL2RPIds.Items.Flashlight when item.Traits.TryGetValue( "flashlight", out var payload ):
				{
					var state = HL2RPPersistence.Flashlight.Deserialize( payload.Data, payload.TypeVersion );
					values[HL2RPInventoryItemFields.FlashlightPowered] = SnapshotValue.Boolean( state.Powered );
					values[HL2RPInventoryItemFields.FlashlightChargePermille] = SnapshotValue.Integer( state.ChargePermille );
					break;
				}
				case HL2RPIds.Items.Radio when item.Traits.TryGetValue( "radio", out var payload ):
				{
					var state = HL2RPPersistence.Radio.Deserialize( payload.Data, payload.TypeVersion );
					values[HL2RPInventoryItemFields.RadioFrequency] = SnapshotValue.String( state.Frequency );
					values[HL2RPInventoryItemFields.RadioPowered] = SnapshotValue.Boolean( state.Powered );
					break;
				}
				case HL2RPIds.Items.Note when item.Traits.TryGetValue( "note", out var payload ):
				{
					var state = HL2RPPersistence.Note.Deserialize( payload.Data, payload.TypeVersion );
					values[HL2RPInventoryItemFields.NoteBody] = SnapshotValue.String( state.Text );
					values[HL2RPInventoryItemFields.NoteOwnerCharacterId] = SnapshotValue.String(
						state.OwnerCharacterId?.Value.ToString( "D" ) ?? string.Empty );
					values[HL2RPInventoryItemFields.NoteUpdatedAtUnixMilliseconds] = SnapshotValue.Integer(
						state.UpdatedAtUtc.ToUnixTimeMilliseconds() );
					break;
				}
				case HL2RPIds.Items.BusinessPermit when item.Traits.TryGetValue( "permit", out var payload ):
				{
					var state = HL2RPPersistence.BusinessPermit.Deserialize( payload.Data, payload.TypeVersion );
					values[HL2RPInventoryItemFields.PermitKind] = SnapshotValue.Choice( state.Kind.ToString().ToLowerInvariant() );
					values[HL2RPInventoryItemFields.PermitExpiresAtUnixMilliseconds] = SnapshotValue.Integer(
						state.ExpiresAtUtc?.ToUnixTimeMilliseconds() ?? -1 );
					values[HL2RPInventoryItemFields.PermitValid] = SnapshotValue.Boolean(
						state.OwnerCharacterId == viewerCharacterId && !state.Revoked &&
						(state.ExpiresAtUtc is null || state.ExpiresAtUtc > nowUtc) );
					break;
				}
				case HL2RPIds.Items.RequestDevice when item.Traits.TryGetValue( "request_device", out var payload ):
				{
					var state = HL2RPPersistence.RequestDevice.Deserialize( payload.Data, payload.TypeVersion );
					values[HL2RPInventoryItemFields.RequestPowered] = SnapshotValue.Boolean( state.Powered );
					values[HL2RPInventoryItemFields.RequestReadyAtUnixMilliseconds] = SnapshotValue.Integer(
						state.LastRequestAtUtc is DateTimeOffset last
							? (last + RequestDeviceService.RequestCooldown).ToUnixTimeMilliseconds()
							: -1 );
					break;
				}
			}
		}
		catch ( Exception )
		{
			values.Clear();
		}
		return values;
	}
}

public sealed record CombineLockActionAvailability( bool Allowed, string? DisabledReason );

public sealed record HL2RPItemActionAvailabilityContext
{
	public required CharacterRecord Character { get; init; }
	public required InventoryRecord Inventory { get; init; }
	public required ItemRecord Item { get; init; }
	public required IReadOnlyDictionary<ItemId, ItemRecord> InventoryItems { get; init; }
	public required DateTimeOffset NowUtc { get; init; }
	public required bool HasUseCapability { get; init; }
	public required bool IsRestrained { get; init; }
	public required bool IsDead { get; init; }
	public CombatHealthSnapshot? Health { get; init; }
	public bool HasNestedBagInventory { get; init; }
	public CombineLockActionAvailability? CombineLock { get; init; }
}

public static class HL2RPItemActionAvailability
{
	public static ItemActionSnapshot Project(
		ItemActionSnapshot routed,
		HL2RPItemActionAvailabilityContext context )
	{
		ArgumentNullException.ThrowIfNull( routed );
		ArgumentNullException.ThrowIfNull( context );
		var invocation = IsDedicated( routed.ActionId.Value )
			? ItemActionInvocationKind.DedicatedPanel
			: ItemActionInvocationKind.ContextMenu;
		if ( !routed.Enabled ) return routed with { Invocation = invocation };
		if ( context.IsDead ) return Disabled( routed, "Character is deceased.", invocation );
		if ( context.IsRestrained ) return Disabled( routed, "Restrained", invocation );
		if ( !context.HasUseCapability ) return Disabled( routed, "Use capability is missing.", invocation );

		var action = routed.ActionId.Value;
		var definition = context.Item.Definition.Value;
		return (definition, action) switch
		{
			(HL2RPIds.Items.CitizenIdCard, HL2RPIds.Actions.Show) =>
				Trait( routed, context.Item, "cid", HL2RPPersistence.CitizenIdCard, invocation ),
			(HL2RPIds.Items.Ration, HL2RPIds.Actions.Open) =>
				context.Character.Balance <= long.MaxValue - HL2RPNonCombatItemActions.RationTokenReward
					? Enabled( routed, invocation )
					: Disabled( routed, "Ration token credit would overflow.", invocation ),
			(HL2RPIds.Items.Water, HL2RPIds.Actions.Consume) => Enabled( routed, invocation ),
			(HL2RPIds.Items.HealthVial, HL2RPIds.Actions.Consume) => HealthVial( routed, context, invocation ),
			(HL2RPIds.Items.Flashlight, HL2RPIds.Actions.Toggle) =>
				Trait( routed, context.Item, "flashlight", HL2RPPersistence.Flashlight, invocation ),
			(HL2RPIds.Items.Radio, HL2RPIds.Actions.Tune) =>
				Trait( routed, context.Item, "radio", HL2RPPersistence.Radio, invocation ),
			(HL2RPIds.Items.RequestDevice, HL2RPIds.Actions.Request) => Request( routed, context, invocation ),
			(HL2RPIds.Items.Note, HL2RPIds.Actions.Read) =>
				Trait( routed, context.Item, "note", HL2RPPersistence.Note, invocation ),
			(HL2RPIds.Items.Note, HL2RPIds.Actions.Write) => NoteWrite( routed, context, invocation ),
			(HL2RPIds.Items.CivicHandbook, HL2RPIds.Actions.Read) => Enabled( routed, invocation ),
			(HL2RPIds.Items.BusinessPermit, HL2RPIds.Actions.PresentPermit) => Permit( routed, context, invocation ),
			(HL2RPIds.Items.Suitcase, HL2RPIds.Actions.OpenBag) => context.HasNestedBagInventory
				? Enabled( routed, invocation )
				: Disabled( routed, "Bag must own exactly one nested inventory.", invocation ),
			(HL2RPIds.Items.CombineLockKit, HL2RPIds.Actions.Install) => CombineLock( routed, context, invocation ),
			(HL2RPIds.Items.TokenStack, HL2RPIds.Actions.Split) => TokenSplit( routed, context, invocation ),
			(HL2RPIds.Items.TokenStack, HL2RPIds.Actions.Combine) => TokenCombine( routed, context, invocation ),
			(HL2RPIds.Items.ZipTie, HL2RPIds.Actions.Restrain) => HasCombineRole( context.Character )
				? Enabled( routed, invocation )
				: Disabled( routed, "Only Civil Protection or Overwatch may use restraints.", invocation ),
			(HL2RPIds.Items.Pistol, _) => Pistol( routed, context, invocation ),
			(HL2RPIds.Items.PistolAmmunition, HL2RPIds.Actions.Replenish) => Ammunition( routed, context, invocation ),
			(HL2RPIds.Items.ProtectiveVest, _) => Vest( routed, context, invocation ),
			_ => Disabled( routed, "Action requirements are unavailable.", invocation )
		};
	}

	private static ItemActionSnapshot HealthVial( ItemActionSnapshot routed,
		HL2RPItemActionAvailabilityContext context, ItemActionInvocationKind invocation ) =>
		context.Health is not { IsDead: false } health || health.CurrentHealth >= health.MaximumHealth
			? Disabled( routed, "Health vial requires a living injured character.", invocation )
			: Enabled( routed, invocation );

	private static ItemActionSnapshot Request( ItemActionSnapshot routed,
		HL2RPItemActionAvailabilityContext context, ItemActionInvocationKind invocation )
	{
		var state = Decode( context.Item, "request_device", HL2RPPersistence.RequestDevice );
		if ( state.Failed ) return Disabled( routed, "Request device state is malformed.", invocation );
		if ( !state.Value.Powered ) return Disabled( routed, "Request device is powered off.", invocation );
		return state.Value.LastRequestAtUtc is DateTimeOffset last &&
			context.NowUtc - last < RequestDeviceService.RequestCooldown
			? Disabled( routed, "Request device is cooling down.", invocation )
			: Enabled( routed, invocation );
	}

	private static ItemActionSnapshot NoteWrite( ItemActionSnapshot routed,
		HL2RPItemActionAvailabilityContext context, ItemActionInvocationKind invocation )
	{
		var state = Decode( context.Item, "note", HL2RPPersistence.Note );
		if ( state.Failed ) return Disabled( routed, "Note state is malformed.", invocation );
		return state.Value.OwnerCharacterId is null || state.Value.OwnerCharacterId == context.Character.Id
			? Enabled( routed, invocation )
			: Disabled( routed, "Note belongs to another character.", invocation );
	}

	private static ItemActionSnapshot Permit( ItemActionSnapshot routed,
		HL2RPItemActionAvailabilityContext context, ItemActionInvocationKind invocation )
	{
		var state = Decode( context.Item, "permit", HL2RPPersistence.BusinessPermit );
		if ( state.Failed ) return Disabled( routed, "Business permit state is malformed.", invocation );
		if ( state.Value.OwnerCharacterId != context.Character.Id )
			return Disabled( routed, "Business permit belongs to another character.", invocation );
		if ( state.Value.Revoked ) return Disabled( routed, "Business permit is revoked.", invocation );
		return state.Value.ExpiresAtUtc is DateTimeOffset expires && expires <= context.NowUtc
			? Disabled( routed, "Business permit is expired.", invocation )
			: Enabled( routed, invocation );
	}

	private static ItemActionSnapshot CombineLock( ItemActionSnapshot routed,
		HL2RPItemActionAvailabilityContext context, ItemActionInvocationKind invocation )
	{
		if ( !HasCombineRole( context.Character ) )
			return Disabled( routed, "Only Civil Protection or Overwatch may install a Combine lock.", invocation );
		var state = Decode( context.Item, "lock_kit", HL2RPPersistence.CombineLockKit );
		if ( state.Failed ) return Disabled( routed, "Lock kit state is malformed.", invocation );
		if ( state.Value.RemainingInstallations <= 0 )
			return Disabled( routed, "Lock kit has no remaining installations.", invocation );
		return context.CombineLock is { Allowed: true }
			? Enabled( routed, invocation )
			: Disabled( routed, context.CombineLock?.DisabledReason ?? "A current unlocked door session is required.", invocation );
	}

	private static ItemActionSnapshot TokenSplit( ItemActionSnapshot routed,
		HL2RPItemActionAvailabilityContext context, ItemActionInvocationKind invocation )
	{
		var state = Decode( context.Item, "tokens", HL2RPPersistence.TokenStack );
		return state.Succeeded && state.Value.Amount > 1
			? Enabled( routed, invocation )
			: Disabled( routed, state.Failed ? "Token stack state is malformed." : "Token stack cannot be split.", invocation );
	}

	private static ItemActionSnapshot TokenCombine( ItemActionSnapshot routed,
		HL2RPItemActionAvailabilityContext context, ItemActionInvocationKind invocation )
	{
		var primary = Decode( context.Item, "tokens", HL2RPPersistence.TokenStack );
		if ( primary.Failed || primary.Value.Amount <= 0 )
			return Disabled( routed, "Token stack state is malformed.", invocation );
		foreach ( var candidate in context.InventoryItems.Values
			.Where( item => item.Id != context.Item.Id && item.Definition == context.Item.Definition ) )
		{
			var secondary = Decode( candidate, "tokens", HL2RPPersistence.TokenStack );
			if ( secondary.Failed || secondary.Value.Amount <= 0 ) continue;
			if ( primary.Value.Amount <= long.MaxValue - secondary.Value.Amount ) return Enabled( routed, invocation );
		}
		return Disabled( routed, "No compatible token stack can be combined without overflow.", invocation );
	}

	private static ItemActionSnapshot Pistol( ItemActionSnapshot routed,
		HL2RPItemActionAvailabilityContext context, ItemActionInvocationKind invocation )
	{
		var state = Decode( context.Item, "pistol", HL2RPPersistence.Pistol );
		if ( state.Failed || state.Value.MagazineRounds is < 0 or > PistolItemState.MagazineCapacity )
			return Disabled( routed, "Pistol state is malformed.", invocation );
		if ( routed.ActionId.Value == HL2RPIds.Actions.Equip )
		{
			if ( state.Value.Equipped ) return Disabled( routed, "Pistol is already equipped.", invocation );
			return AllPistolsValid( context.InventoryItems.Values )
				? Enabled( routed, invocation )
				: Disabled( routed, "Another pistol has malformed state.", invocation );
		}
		if ( routed.ActionId.Value == HL2RPIds.Actions.Unequip )
			return state.Value.Equipped ? Enabled( routed, invocation ) : Disabled( routed, "Pistol is not equipped.", invocation );
		if ( routed.ActionId.Value == HL2RPIds.Actions.Fire )
		{
			if ( !state.Value.Equipped ) return Disabled( routed, "Pistol is not equipped.", invocation );
			if ( state.Value.MagazineRounds <= 0 ) return Disabled( routed, "Pistol magazine is empty.", invocation );
			return state.Value.LastFiredAtUtc is DateTimeOffset last &&
				(context.NowUtc < last || context.NowUtc - last < PistolCombatService.DefaultFireInterval)
				? Disabled( routed, "Pistol is recovering between shots.", invocation )
				: Enabled( routed, invocation );
		}
		if ( routed.ActionId.Value == HL2RPIds.Actions.Reload )
		{
			if ( !state.Value.Equipped || state.Value.Raised )
				return Disabled( routed, "Pistol must be equipped and lowered before reloading.", invocation );
			if ( state.Value.MagazineRounds >= PistolItemState.MagazineCapacity )
				return Disabled( routed, "Pistol magazine is already full.", invocation );
			return HasValidReserveAmmunition( context.InventoryItems.Values )
				? Enabled( routed, invocation )
				: Disabled( routed, "No pistol ammunition is available.", invocation );
		}
		return Disabled( routed, "Pistol action is unsupported.", invocation );
	}

	private static ItemActionSnapshot Ammunition( ItemActionSnapshot routed,
		HL2RPItemActionAvailabilityContext context, ItemActionInvocationKind invocation )
	{
		var state = Decode( context.Item, "ammunition", HL2RPPersistence.PistolAmmunition );
		if ( state.Failed || state.Value.Rounds is < 0 or > PistolItemState.MagazineCapacity )
			return Disabled( routed, "Ammunition state is malformed.", invocation );
		return state.Value.Rounds < PistolItemState.MagazineCapacity
			? Enabled( routed, invocation )
			: Disabled( routed, "Ammunition is already replenished.", invocation );
	}

	private static ItemActionSnapshot Vest( ItemActionSnapshot routed,
		HL2RPItemActionAvailabilityContext context, ItemActionInvocationKind invocation )
	{
		var state = Decode( context.Item, "vest", HL2RPPersistence.ProtectiveVest );
		if ( state.Failed || state.Value.Durability < 0 || state.Value.DamageReductionPermille is < 0 or > 1000 )
			return Disabled( routed, "Protective vest state is malformed.", invocation );
		if ( routed.ActionId.Value == HL2RPIds.Actions.Equip )
		{
			if ( state.Value.Durability == 0 ) return Disabled( routed, "Destroyed armor cannot be equipped.", invocation );
			if ( state.Value.Equipped ) return Disabled( routed, "Protective vest is already equipped.", invocation );
			return AllVestsValid( context.InventoryItems.Values )
				? Enabled( routed, invocation )
				: Disabled( routed, "Another protective vest has malformed state.", invocation );
		}
		return state.Value.Equipped
			? Enabled( routed, invocation )
			: Disabled( routed, "Protective vest is not equipped.", invocation );
	}

	private static ItemActionSnapshot Trait<T>( ItemActionSnapshot routed, ItemRecord item, string trait,
		Hexagon.V2.Persistence.IPersistedTypeCodec<T> codec, ItemActionInvocationKind invocation ) where T : class =>
		Decode( item, trait, codec ).Succeeded
			? Enabled( routed, invocation )
			: Disabled( routed, $"{routed.Label} state is malformed.", invocation );

	private static OperationResult<T> Decode<T>( ItemRecord item, string trait,
		Hexagon.V2.Persistence.IPersistedTypeCodec<T> codec ) where T : class =>
		item.Traits.TryGetValue( trait, out var payload )
			? HL2RPFeaturePersistence.Decode( payload, codec )
			: OperationResult<T>.Failure( ErrorCode.PersistedTypeInvalid, $"Item state '{trait}' is missing." );

	private static bool AllPistolsValid( IEnumerable<ItemRecord> items ) => items
		.Where( item => item.Definition.Value == HL2RPIds.Items.Pistol )
		.All( item =>
		{
			var state = Decode( item, "pistol", HL2RPPersistence.Pistol );
			return state.Succeeded && state.Value.MagazineRounds is >= 0 and <= PistolItemState.MagazineCapacity;
		} );

	private static bool AllVestsValid( IEnumerable<ItemRecord> items ) => items
		.Where( item => item.Definition.Value == HL2RPIds.Items.ProtectiveVest )
		.All( item =>
		{
			var state = Decode( item, "vest", HL2RPPersistence.ProtectiveVest );
			return state.Succeeded && state.Value.Durability >= 0 &&
				state.Value.DamageReductionPermille is >= 0 and <= 1000;
		} );

	private static bool HasValidReserveAmmunition( IEnumerable<ItemRecord> items )
	{
		var found = false;
		foreach ( var item in items.Where( value => value.Definition.Value == HL2RPIds.Items.PistolAmmunition ) )
		{
			var state = Decode( item, "ammunition", HL2RPPersistence.PistolAmmunition );
			if ( state.Failed || state.Value.Rounds is < 0 or > PistolItemState.MagazineCapacity ) return false;
			found |= state.Value.Rounds > 0;
		}
		return found;
	}

	private static bool HasCombineRole( CharacterRecord character ) =>
		character.Faction.Value is HL2RPIds.Factions.CivilProtection or HL2RPIds.Factions.Overwatch;

	private static bool IsDedicated( string action ) => action is
		HL2RPIds.Actions.Tune or HL2RPIds.Actions.Request or HL2RPIds.Actions.Write or HL2RPIds.Actions.Restrain;

	private static ItemActionSnapshot Enabled( ItemActionSnapshot routed, ItemActionInvocationKind invocation ) =>
		routed with { Enabled = true, DisabledReason = null, Invocation = invocation };

	private static ItemActionSnapshot Disabled( ItemActionSnapshot routed, string reason,
		ItemActionInvocationKind invocation ) =>
		routed with { Enabled = false, DisabledReason = reason, Invocation = invocation };
}

public sealed record ItemDropAvailability( bool CanDrop, string? DisabledReason );

public static class HL2RPItemDropAvailability
{
	public static ItemDropAvailability Project(
		ItemDefinition definition,
		bool hasDropCapability,
		bool worldModelResolves,
		bool isRestrained )
	{
		ArgumentNullException.ThrowIfNull( definition );
		if ( isRestrained ) return new ItemDropAvailability( false, "Restrained characters cannot drop items." );
		if ( !hasDropCapability ) return new ItemDropAvailability( false, "Drop capability is missing." );
		if ( !definition.CanDrop || string.IsNullOrWhiteSpace( definition.WorldModel ) )
			return new ItemDropAvailability( false, "Item is explicitly non-droppable." );
		return worldModelResolves
			? new ItemDropAvailability( true, null )
			: new ItemDropAvailability( false, "Item world model does not resolve." );
	}
}

public sealed record VendorSellAvailability( bool Enabled, long Payout, string DisabledReason );

public static class HL2RPVendorSellAvailability
{
	public static VendorSellAvailability Project(
		CharacterRecord character,
		ItemRecord item,
		VendorEntityState vendor,
		bool hasSellCapability,
		bool hasNestedInventory,
		bool hasRequiredPermit,
		bool policyAllowed )
	{
		ArgumentNullException.ThrowIfNull( character );
		ArgumentNullException.ThrowIfNull( item );
		ArgumentNullException.ThrowIfNull( vendor );
		if ( !hasSellCapability ) return Disabled( "Sell capability is missing." );
		if ( hasNestedInventory ) return Disabled( "Container items with nested inventory cannot be sold." );
		if ( !hasRequiredPermit ) return Disabled( "Required business permit is missing or expired." );
		if ( vendor.Stock.Select( entry => entry.Definition ).Distinct().Count() != vendor.Stock.Count ||
			vendor.Stock.Any( entry => entry.Quantity < 0 || entry.UnitPrice < 0 ) )
			return Disabled( "Vendor stock is malformed." );
		var stock = vendor.Stock.SingleOrDefault( entry => entry.Definition == item.Definition );
		if ( stock is null ) return Disabled( "Vendor does not buy this item." );
		if ( stock.Quantity == int.MaxValue ) return Disabled( "Vendor stock would overflow." );
		var payout = stock.UnitPrice <= 1 ? stock.UnitPrice : stock.UnitPrice / 2;
		if ( character.Balance > long.MaxValue - payout ) return Disabled( "Character balance would overflow." );
		if ( !policyAllowed ) return Disabled( "Vendor policy denied this item." );
		return new VendorSellAvailability( true, payout, string.Empty );
	}

	private static VendorSellAvailability Disabled( string reason ) => new( false, 0, reason );
}
