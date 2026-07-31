#nullable enable

namespace HL2RP.V2.Domain;

public interface IHL2RPWhitelistService
{
	HL2RPWhitelist GetWhitelists( AccountId accountId );
}

public sealed class NoRestrictedWhitelists : IHL2RPWhitelistService
{
	public HL2RPWhitelist GetWhitelists( AccountId accountId ) => HL2RPWhitelist.None;
}

/// <summary>
/// Computes every schema-owned creation value from the authenticated account and
/// allowlisted creation fields. Balance, CID, whitelist and Combine identity are
/// never accepted from a client payload.
/// </summary>
public sealed class HL2RPCharacterStateFactory : ICharacterStateFactory
{
	private static readonly IReadOnlySet<string> Origins = new HashSet<string>( StringComparer.Ordinal )
	{
		"city_17",
		"relocated",
		"outlands"
	};

	private readonly IHL2RPWhitelistService _whitelists;

	public HL2RPCharacterStateFactory( IHL2RPWhitelistService? whitelists = null ) =>
		_whitelists = whitelists ?? new NoRestrictedWhitelists();

	public OperationResult<CharacterStatePlan> Create( CharacterCreationContext context )
	{
		if ( context.NowUtc.Offset != TimeSpan.Zero )
			return Failure( ErrorCode.InvalidArgument, "Character creation time must be UTC." );
		var age = RequireInteger( context.Request, HL2RPIds.CreationFields.Age );
		if ( age.Failed ) return Failure( age.Error!.Code, age.Error.Message );
		if ( age.Value is < 18 or > 80 )
			return Failure( ErrorCode.PolicyDenied, "Character age must be between 18 and 80." );

		var pronouns = RequireString( context.Request, HL2RPIds.CreationFields.Pronouns );
		if ( pronouns.Failed ) return Failure( pronouns.Error!.Code, pronouns.Error.Message );
		var normalizedPronouns = pronouns.Value.Trim();
		if ( normalizedPronouns.Length is < 1 or > 32 || normalizedPronouns.Any( char.IsControl ) )
			return Failure( ErrorCode.InvalidArgument, "Pronouns must contain 1 to 32 printable characters." );

		var origin = RequireChoice( context.Request, HL2RPIds.CreationFields.Origin );
		if ( origin.Failed ) return Failure( origin.Error!.Code, origin.Error.Message );
		if ( !Origins.Contains( origin.Value ) )
			return Failure( ErrorCode.InvalidArgument, "Character origin is not registered." );

		var factionValidation = ValidateFactionAndClass( context.Request.Faction, context.Request.Class );
		if ( factionValidation.Failed )
			return Failure( factionValidation.Error!.Code, factionValidation.Error.Message );

		var whitelist = _whitelists.GetWhitelists( context.AccountId );
		var requiredWhitelist = HL2RPAccountEntitlements.RequiredForFaction( context.Request.Faction );
		if ( requiredWhitelist != HL2RPWhitelist.None && (whitelist & requiredWhitelist) != requiredWhitelist )
			return Failure( ErrorCode.Unauthorized, "Authenticated account is not whitelisted for the selected faction." );

		var characterState = new HL2RPCharacterState
		{
			CitizenId = CreateCitizenId( context.AccountId, context.Slot ),
			Age = checked((int)age.Value),
			Pronouns = normalizedPronouns,
			Origin = origin.Value,
			Whitelists = whitelist,
			CivicRecord = new CivicRecordState
			{
				Points = 0,
				Priority = CivicPriorityStatus.None
			},
			CombineIdentity = CreateCombineIdentity(
				context.AccountId,
				context.Request.Faction,
				context.Request.Class )
		};
		return OperationResult<CharacterStatePlan>.Success( new CharacterStatePlan(
			HL2RPPersistence.Payload( HL2RPPersistence.CharacterState, characterState ),
			StartingBalance( context.Request.Faction ) ) );
	}

	public static string CreateCitizenId( AccountId accountId, int slot )
	{
		if ( slot < 0 ) throw new ArgumentOutOfRangeException( nameof(slot) );
		return $"C17-{accountId.Value:D20}-{slot:D2}";
	}

	private static OperationResult ValidateFactionAndClass( FactionId faction, ClassId? characterClass )
	{
		if ( faction.Value == HL2RPIds.Factions.Citizen )
			return characterClass is null
				? OperationResult.Success()
				: OperationResult.Failure( ErrorCode.PolicyDenied, "Citizen characters cannot select a Combine class." );
		if ( faction.Value == HL2RPIds.Factions.CivilProtection )
		{
			if ( characterClass is null || !CivilProtectionClasses.Contains( characterClass.Value.Value ) )
				return OperationResult.Failure( ErrorCode.PolicyDenied, "Civil Protection class is invalid." );
			return OperationResult.Success();
		}
		if ( faction.Value is HL2RPIds.Factions.Overwatch or HL2RPIds.Factions.CityAdministration )
			return characterClass is null
				? OperationResult.Success()
				: OperationResult.Failure( ErrorCode.PolicyDenied, "Selected faction does not expose creation classes." );
		return OperationResult.Failure( ErrorCode.UnknownDefinition, "Faction is not registered by HL2RP." );
	}

	private static IReadOnlySet<string> CivilProtectionClasses { get; } = new HashSet<string>( StringComparer.Ordinal )
	{
		HL2RPIds.Classes.Recruit,
		HL2RPIds.Classes.Unit,
		HL2RPIds.Classes.Elite,
		HL2RPIds.Classes.Scanner
	};

	private static long StartingBalance( FactionId faction ) => faction.Value switch
	{
		HL2RPIds.Factions.Citizen => 25,
		HL2RPIds.Factions.CityAdministration => 5_000,
		_ => 0
	};

	private static CombineIdentityState? CreateCombineIdentity(
		AccountId accountId,
		FactionId faction,
		ClassId? characterClass )
	{
		var identity = (faction.Value, characterClass?.Value) switch
		{
			(HL2RPIds.Factions.CivilProtection, HL2RPIds.Classes.Recruit) =>
				(CombineRank.Recruit, CombineDivision.Protection, "CCA-RCT"),
			(HL2RPIds.Factions.CivilProtection, HL2RPIds.Classes.Unit) =>
				(CombineRank.Unit, CombineDivision.Protection, "CCA-UNIT"),
			(HL2RPIds.Factions.CivilProtection, HL2RPIds.Classes.Elite) =>
				(CombineRank.Elite, CombineDivision.Protection, "CCA-EPU"),
			(HL2RPIds.Factions.CivilProtection, HL2RPIds.Classes.Scanner) =>
				(CombineRank.Scanner, CombineDivision.Scanner, "CCA-SCN"),
			(HL2RPIds.Factions.Overwatch, _) =>
				(CombineRank.OverwatchSoldier, CombineDivision.Overwatch, "OTA-OWS"),
			(HL2RPIds.Factions.CityAdministration, _) =>
				(CombineRank.Administrator, CombineDivision.Administration, "ADMIN"),
			_ => (CombineRank.None, CombineDivision.None, "")
		};
		if ( identity.Item1 == CombineRank.None ) return null;
		var unitNumber = accountId.Value % 100_000;
		return new CombineIdentityState
		{
			Rank = identity.Item1,
			Division = identity.Item2,
			ServiceName = $"C17.{identity.Item3}.{unitNumber:D5}"
		};
	}

	private static OperationResult<long> RequireInteger( CharacterCreationRequest request, string id )
	{
		if ( !request.Fields.TryGetValue( id, out var value ) || value.Kind != CreationValueKind.Integer )
			return OperationResult<long>.Failure( ErrorCode.InvalidArgument, $"Creation field '{id}' must be an integer." );
		return OperationResult<long>.Success( value.IntegerValue );
	}

	private static OperationResult<string> RequireString( CharacterCreationRequest request, string id )
	{
		if ( !request.Fields.TryGetValue( id, out var value ) || value.Kind != CreationValueKind.String )
			return OperationResult<string>.Failure( ErrorCode.InvalidArgument, $"Creation field '{id}' must be a string." );
		return OperationResult<string>.Success( value.StringValue );
	}

	private static OperationResult<string> RequireChoice( CharacterCreationRequest request, string id )
	{
		if ( !request.Fields.TryGetValue( id, out var value ) || value.Kind != CreationValueKind.Choice )
			return OperationResult<string>.Failure( ErrorCode.InvalidArgument, $"Creation field '{id}' must be a choice." );
		return OperationResult<string>.Success( value.StringValue );
	}

	private static OperationResult<CharacterStatePlan> Failure( ErrorCode code, string message ) =>
		OperationResult<CharacterStatePlan>.Failure( code, message );
}

public static class HL2RPInitializers
{
	public static IReadOnlyList<ICharacterInitializer> CreateDefault() => new ICharacterInitializer[]
	{
		new HL2RPCitizenIdInitializer(),
		new HL2RPLoadoutInitializer()
	};
}

public sealed class HL2RPCitizenIdInitializer : ICharacterInitializer
{
	public const string InitializerId = "hl2rp.cid";
	public const int InitializerOrder = 100;

	public string Id => InitializerId;
	public int Order => InitializerOrder;

	public OperationResult<CharacterInitializerContribution> Build(
		CharacterCreationContext context,
		CharacterStatePlan state )
	{
		var decoded = DecodeState( state );
		if ( decoded.Failed ) return OperationResult<CharacterInitializerContribution>.Failure(
			decoded.Error!.Code, decoded.Error.Message );
		var cid = decoded.Value.CitizenId;
		return OperationResult<CharacterInitializerContribution>.Success( new CharacterInitializerContribution
		{
			Items = new[]
			{
				new ItemGrantPlan
				{
					Definition = new DefinitionId( HL2RPIds.Items.CitizenIdCard ),
					Traits = new Dictionary<string, TypedPayload>( StringComparer.Ordinal )
					{
						["cid"] = HL2RPPersistence.Payload(
							HL2RPPersistence.CitizenIdCard,
							new CitizenIdCardItemState
							{
								CitizenId = cid,
								IssuedName = context.Request.Name,
								IssuedAtUtc = context.NowUtc,
								Priority = CivicPriorityStatus.None
							} )
					}
				}
			},
			Reservations = new[]
			{
				new UniqueReservationPlan( HL2RPIds.ReservationNamespaces.CitizenId, cid )
			}
		} );
	}

	internal static OperationResult<HL2RPCharacterState> DecodeState( CharacterStatePlan state )
	{
		if ( state.State.TypeId.Value != HL2RPIds.PersistedTypes.CharacterState ||
			state.State.TypeVersion != HL2RPPersistence.CharacterState.CurrentVersion )
			return OperationResult<HL2RPCharacterState>.Failure(
				ErrorCode.PersistedTypeInvalid, "Initializer received incompatible HL2RP character state." );
		try
		{
			return OperationResult<HL2RPCharacterState>.Success(
				HL2RPPersistence.CharacterState.Deserialize( state.State.Data, state.State.TypeVersion ) );
		}
		catch ( Exception )
		{
			return OperationResult<HL2RPCharacterState>.Failure(
				ErrorCode.PersistedTypeInvalid, "Initializer could not decode HL2RP character state." );
		}
	}
}

public sealed class HL2RPLoadoutInitializer : ICharacterInitializer
{
	public const string InitializerId = "hl2rp.loadout";
	public const int InitializerOrder = 200;

	public string Id => InitializerId;
	public int Order => InitializerOrder;

	public OperationResult<CharacterInitializerContribution> Build(
		CharacterCreationContext context,
		CharacterStatePlan state )
	{
		var decoded = HL2RPCitizenIdInitializer.DecodeState( state );
		if ( decoded.Failed ) return OperationResult<CharacterInitializerContribution>.Failure(
			decoded.Error!.Code, decoded.Error.Message );
		var items = context.Request.Faction.Value switch
		{
			HL2RPIds.Factions.Citizen => CitizenLoadout(),
			HL2RPIds.Factions.CivilProtection => CivilProtectionLoadout( context.Request.Class ),
			HL2RPIds.Factions.Overwatch => ArmedCombineLoadout( includeLockKit: false ),
			HL2RPIds.Factions.CityAdministration => AdministrationLoadout(),
			_ => Array.Empty<ItemGrantPlan>()
		};
		return OperationResult<CharacterInitializerContribution>.Success( new CharacterInitializerContribution
		{
			Items = items
		} );
	}

	private static IReadOnlyList<ItemGrantPlan> CitizenLoadout() => new[]
	{
		RequestDevice(),
		new ItemGrantPlan
		{
			Definition = new DefinitionId( HL2RPIds.Items.Suitcase ),
			Bag = new BagInventoryPlan
			{
				Width = 3,
				Height = 3,
				Items = new[]
				{
					new ItemGrantPlan { Definition = new DefinitionId( HL2RPIds.Items.CivicHandbook ) }
				}
			}
		}
	};

	private static IReadOnlyList<ItemGrantPlan> CivilProtectionLoadout( ClassId? characterClass ) =>
		characterClass?.Value == HL2RPIds.Classes.Scanner
			? new[] { Radio(), RequestDevice(), Vest() }
			: ArmedCombineLoadout( includeLockKit: true );

	private static IReadOnlyList<ItemGrantPlan> ArmedCombineLoadout( bool includeLockKit )
	{
		var items = new List<ItemGrantPlan>
		{
			Radio(),
			RequestDevice(),
			new() { Definition = new DefinitionId( HL2RPIds.Items.ZipTie ) },
			new()
			{
				Definition = new DefinitionId( HL2RPIds.Items.Pistol ),
				Traits = Trait( "pistol", HL2RPPersistence.Payload(
					HL2RPPersistence.Pistol,
					new PistolItemState
					{
						MagazineRounds = PistolItemState.MagazineCapacity,
						Equipped = false,
						Raised = false,
						LastFiredAtUtc = null
					} ) )
			},
			new()
			{
				Definition = new DefinitionId( HL2RPIds.Items.PistolAmmunition ),
				Traits = Trait( "ammunition", HL2RPPersistence.Payload(
					HL2RPPersistence.PistolAmmunition,
					new PistolAmmunitionItemState { Rounds = PistolItemState.MagazineCapacity } ) )
			},
			Vest()
		};
		if ( includeLockKit )
		{
			items.Add( new ItemGrantPlan
			{
				Definition = new DefinitionId( HL2RPIds.Items.CombineLockKit ),
				Traits = Trait( "lock_kit", HL2RPPersistence.Payload(
					HL2RPPersistence.CombineLockKit,
					new CombineLockKitItemState { RemainingInstallations = 1 } ) )
			} );
		}
		return items;
	}

	private static IReadOnlyList<ItemGrantPlan> AdministrationLoadout() => new[]
	{
		Radio(),
		RequestDevice(),
		new ItemGrantPlan { Definition = new DefinitionId( HL2RPIds.Items.CivicHandbook ) }
	};

	private static ItemGrantPlan Radio() => new()
	{
		Definition = new DefinitionId( HL2RPIds.Items.Radio ),
		Traits = Trait( "radio", HL2RPPersistence.Payload(
			HL2RPPersistence.Radio,
			new RadioItemState { Frequency = "100.0", Powered = true } ) )
	};

	private static ItemGrantPlan RequestDevice() => new()
	{
		Definition = new DefinitionId( HL2RPIds.Items.RequestDevice ),
		Traits = Trait( "request_device", HL2RPPersistence.Payload(
			HL2RPPersistence.RequestDevice,
			new RequestDeviceItemState { Powered = true, LastRequestAtUtc = null } ) )
	};

	private static ItemGrantPlan Vest() => new()
	{
		Definition = new DefinitionId( HL2RPIds.Items.ProtectiveVest ),
		Traits = Trait( "vest", HL2RPPersistence.Payload(
			HL2RPPersistence.ProtectiveVest,
			new ProtectiveVestItemState
			{
				Durability = 100,
				DamageReductionPermille = 300,
				Equipped = false
			} ) )
	};

	private static IReadOnlyDictionary<string, TypedPayload> Trait( string id, TypedPayload payload ) =>
		new Dictionary<string, TypedPayload>( StringComparer.Ordinal ) { [id] = payload };
}
