#nullable enable

using Hexagon.V2.Persistence;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HL2RP.V2.Domain;

/// <summary>
/// Concrete, versioned codecs for every schema-owned payload that can enter a
/// character, item trait or persistent scene-entity envelope.
/// </summary>
public static class HL2RPPersistence
{
	/// <summary>
	/// Persisted payload bytes are an envelope implementation detail. A payload
	/// that already uses the registered type and current version has been decoded
	/// and validated by its codec and must not be rewritten merely because a JSON
	/// round trip chose a different equivalent representation.
	/// </summary>
	public static bool RequiresVersionMigration( TypedPayload persisted, TypedPayload normalized )
	{
		ArgumentNullException.ThrowIfNull( persisted );
		ArgumentNullException.ThrowIfNull( normalized );
		return persisted.TypeId != normalized.TypeId ||
			persisted.TypeVersion != normalized.TypeVersion;
	}

	public static IPersistedTypeCodec<HL2RPAccountEntitlementRecord> AccountEntitlement { get; } =
		Codec<HL2RPAccountEntitlementRecord>(
			HL2RPIds.PersistedTypes.AccountEntitlement, PublishAccountEntitlement );
	public static IPersistedTypeCodec<HL2RPCharacterState> CharacterState { get; } =
		Codec<HL2RPCharacterState>( HL2RPIds.PersistedTypes.CharacterState, PublishCharacterState );
	public static IPersistedTypeCodec<CitizenIdCardItemState> CitizenIdCard { get; } =
		ImmutableCodec<CitizenIdCardItemState>( HL2RPIds.PersistedTypes.CitizenIdCard );
	public static IPersistedTypeCodec<RadioItemState> Radio { get; } =
		ImmutableCodec<RadioItemState>( HL2RPIds.PersistedTypes.Radio );
	public static IPersistedTypeCodec<FlashlightItemState> Flashlight { get; } =
		ImmutableCodec<FlashlightItemState>( HL2RPIds.PersistedTypes.Flashlight );
	public static IPersistedTypeCodec<RequestDeviceItemState> RequestDevice { get; } =
		new JsonPersistedTypeCodec<RequestDeviceItemState>(
			new PersistedTypeKey( HL2RPIds.PersistedTypes.RequestDevice ),
			2,
			PersistedValuePublication.Immutable,
			upgrades: new Dictionary<int, Func<JsonElement, JsonElement>>
			{
				[1] = UpgradeRequestDeviceV1
			} );
	public static IPersistedTypeCodec<NoteItemState> Note { get; } =
		ImmutableCodec<NoteItemState>( HL2RPIds.PersistedTypes.Note );
	public static IPersistedTypeCodec<BusinessPermitItemState> BusinessPermit { get; } =
		ImmutableCodec<BusinessPermitItemState>( HL2RPIds.PersistedTypes.BusinessPermit );
	public static IPersistedTypeCodec<PistolItemState> Pistol { get; } =
		ImmutableCodec<PistolItemState>( HL2RPIds.PersistedTypes.Pistol );
	public static IPersistedTypeCodec<PistolAmmunitionItemState> PistolAmmunition { get; } =
		ImmutableCodec<PistolAmmunitionItemState>( HL2RPIds.PersistedTypes.PistolAmmunition );
	public static IPersistedTypeCodec<ProtectiveVestItemState> ProtectiveVest { get; } =
		ImmutableCodec<ProtectiveVestItemState>( HL2RPIds.PersistedTypes.ProtectiveVest );
	public static IPersistedTypeCodec<CombineLockKitItemState> CombineLockKit { get; } =
		ImmutableCodec<CombineLockKitItemState>( HL2RPIds.PersistedTypes.CombineLockKit );
	public static IPersistedTypeCodec<TokenStackItemState> TokenStack { get; } =
		ImmutableCodec<TokenStackItemState>( HL2RPIds.PersistedTypes.TokenStack );
	public static IPersistedTypeCodec<RecognitionReferenceState> Recognition { get; } =
		ImmutableCodec<RecognitionReferenceState>( HL2RPIds.PersistedTypes.Recognition );
	public static IPersistedTypeCodec<RestraintReferenceState> Restraint { get; } =
		ImmutableCodec<RestraintReferenceState>( HL2RPIds.PersistedTypes.Restraint );
	public static IPersistedTypeCodec<DoorOwnershipReferenceState> DoorOwnership { get; } =
		ImmutableCodec<DoorOwnershipReferenceState>( HL2RPIds.PersistedTypes.DoorOwnership );
	public static IPersistedTypeCodec<DoorEntityState> DoorState { get; } =
		ImmutableCodec<DoorEntityState>( HL2RPIds.PersistedTypes.DoorState );
	public static IPersistedTypeCodec<StorageEntityState> StorageState { get; } =
		ImmutableCodec<StorageEntityState>( HL2RPIds.PersistedTypes.StorageState );
	public static IPersistedTypeCodec<VendorEntityState> VendorState { get; } =
		Codec<VendorEntityState>( HL2RPIds.PersistedTypes.VendorState, PublishVendorState );
	public static IPersistedTypeCodec<MachineEntityState> MachineState { get; } =
		new JsonPersistedTypeCodec<MachineEntityState>(
			new PersistedTypeKey( HL2RPIds.PersistedTypes.MachineState ),
			2,
			PublishMachineState,
			upgrades: new Dictionary<int, Func<JsonElement, JsonElement>>
			{
				[1] = UpgradeMachineStateV1
			} );
	public static IPersistedTypeCodec<ForcefieldEntityState> ForcefieldState { get; } =
		ImmutableCodec<ForcefieldEntityState>( HL2RPIds.PersistedTypes.ForcefieldState );
	public static IPersistedTypeCodec<ScannerEntityState> ScannerState { get; } =
		new JsonPersistedTypeCodec<ScannerEntityState>(
			new PersistedTypeKey( HL2RPIds.PersistedTypes.ScannerState ),
			2,
			PublishScannerState,
			upgrades: new Dictionary<int, Func<JsonElement, JsonElement>>
			{
				[1] = UpgradeScannerStateV1
			} );
	public static IPersistedTypeCodec<CombatTargetEntityState> CombatTargetState { get; } =
		ImmutableCodec<CombatTargetEntityState>( HL2RPIds.PersistedTypes.CombatTargetState );
	public static IPersistedTypeCodec<CityEntityState> CityState { get; } =
		new JsonPersistedTypeCodec<CityEntityState>(
			new PersistedTypeKey( HL2RPIds.PersistedTypes.CityState ),
			3,
			PublishCityState );

	public static IReadOnlyList<IPersistedTypeCodec> Codecs { get; } = new IPersistedTypeCodec[]
	{
		AccountEntitlement,
		CharacterState,
		CitizenIdCard,
		Radio,
		Flashlight,
		RequestDevice,
		Note,
		BusinessPermit,
		Pistol,
		PistolAmmunition,
		ProtectiveVest,
		CombineLockKit,
		TokenStack,
		Recognition,
		Restraint,
		DoorOwnership,
		DoorState,
		StorageState,
		VendorState,
		MachineState,
		ForcefieldState,
		ScannerState,
		CombatTargetState,
		CityState
	};

	public static TypedPayload Payload<T>( IPersistedTypeCodec<T> codec, T value ) where T : class
	{
		ArgumentNullException.ThrowIfNull( codec );
		ArgumentNullException.ThrowIfNull( value );
		return new TypedPayload
		{
			TypeId = new PersistedTypeId( codec.Key.Value ),
			TypeVersion = codec.CurrentVersion,
			Data = codec.Serialize( value )
		};
	}

	private static IPersistedTypeCodec<T> ImmutableCodec<T>( string id ) where T : class =>
		Codec<T>( id, PersistedValuePublication.Immutable );

	private static IPersistedTypeCodec<T> Codec<T>( string id, Func<T, T> prepareForPublication ) where T : class =>
		new JsonPersistedTypeCodec<T>( new PersistedTypeKey( id ), 1, prepareForPublication );

	private static HL2RPAccountEntitlementRecord PublishAccountEntitlement(
		HL2RPAccountEntitlementRecord value )
	{
		if ( value.AccountId.Value == 0 || value.UpdatedByAccountId.Value == 0 )
			throw new InvalidOperationException( "Entitlement accounts must be authenticated non-zero IDs." );
		if ( !HL2RPAccountEntitlements.IsValidSet( value.Flags ) )
			throw new InvalidOperationException( "Entitlement document contains unknown flags." );
		if ( value.UpdatedAtUtc.Offset != TimeSpan.Zero )
			throw new InvalidOperationException( "Entitlement timestamp must be UTC." );
		return value with { };
	}

	private static HL2RPCharacterState PublishCharacterState( HL2RPCharacterState value ) => value with
	{
		CivicRecord = value.CivicRecord with
		{
			Infractions = PersistedValuePublication.ReadOnlyList( value.CivicRecord.Infractions )
		}
	};

	private static VendorEntityState PublishVendorState( VendorEntityState value ) => value with
	{
		Stock = PersistedValuePublication.ReadOnlyList( value.Stock )
	};

	private static MachineEntityState PublishMachineState( MachineEntityState value )
	{
		if ( !double.IsFinite( value.CooldownSeconds ) || value.CooldownSeconds is < 0 or > 300 )
			throw new InvalidOperationException( "Machine cooldown must be finite and between zero and 300 seconds." );
		return value with { };
	}

	private static ScannerEntityState PublishScannerState( ScannerEntityState value ) => value with
	{
		Photos = PersistedValuePublication.ReadOnlyList( value.Photos )
	};

	private static CityEntityState PublishCityState( CityEntityState value )
	{
		var objectives = PersistedValuePublication.ReadOnlyList( value.Objectives );
		var valid = CityObjectiveContract.Validate( objectives );
		if ( valid.Failed ) throw new InvalidOperationException( valid.Error!.Message );
		return value with { Objectives = objectives };
	}

	private static JsonElement UpgradeMachineStateV1( JsonElement payload )
	{
		var root = JsonNode.Parse( payload.GetRawText() )?.AsObject()
			?? throw new JsonException( "Machine state v1 payload must be an object." );
		// Legacy v1 did not persist the authored value. Two seconds is the safe
		// historical default; all newly seeded entities persist their exact setting.
		root["cooldownSeconds"] = 2d;
		return JsonSerializer.SerializeToElement( root );
	}

	private static JsonElement UpgradeRequestDeviceV1( JsonElement payload )
	{
		var root = JsonNode.Parse( payload.GetRawText() )?.AsObject()
			?? throw new JsonException( "Request-device state v1 payload must be an object." );
		root["powered"] = true;
		return JsonSerializer.SerializeToElement( root );
	}

	private static JsonElement UpgradeScannerStateV1( JsonElement payload )
	{
		var root = JsonNode.Parse( payload.GetRawText() )?.AsObject()
			?? throw new JsonException( "Scanner state v1 payload must be an object." );
		root["linkedEntityId"] = null;
		return JsonSerializer.SerializeToElement( root );
	}
}
