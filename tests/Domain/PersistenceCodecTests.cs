#nullable enable

using Hexagon.V2.Composition;
using Hexagon.V2.Domain;
using Hexagon.V2.Persistence;
using HL2RP.V2.Domain;
using HL2RP.V2.Schema;
using HL2RP.V2.Tests.Schema;
using System.Text.Json;

namespace HL2RP.V2.Tests.Domain;

[TestClass]
public sealed class PersistenceCodecTests
{
	[TestMethod]
	public void EverySchemaPersistedTypeHasOneMatchingExplicitCodec()
	{
		var schema = HL2RPSchemaTests.Compile();

		Assert.AreEqual( HL2RPPersistence.Codecs.Count, schema.PersistedTypes.Count );
		Assert.AreEqual(
			HL2RPPersistence.Codecs.Count,
			HL2RPPersistence.Codecs.Select( codec => codec.Key.Value ).Distinct( StringComparer.Ordinal ).Count() );
		Assert.AreEqual(
			HL2RPPersistence.Codecs.Count,
			HL2RPPersistence.Codecs.Select( codec => codec.ClrType ).Distinct().Count() );
		foreach ( var codec in HL2RPPersistence.Codecs )
		{
			var registration = schema.PersistedTypes.Require( codec.Key.Value );
			Assert.IsTrue( registration.Succeeded, registration.Error?.Message );
			Assert.AreEqual( codec.ClrType, registration.Value.ClrType );
			Assert.AreEqual( codec.CurrentVersion, registration.Value.Version );
		}
	}

	[TestMethod]
	public void SchemaPersistenceAdapterBindsEveryCodecWithoutReflectionFallback()
	{
		var schema = HL2RPSchemaTests.Compile();
		var registry = new PersistedTypeRegistry().RegisterHexagonDomainTypes();

		var binding = SchemaPersistenceAdapter.Bind( schema, registry, HL2RPPersistence.Codecs );

		Assert.IsTrue( binding.Succeeded, binding.Error?.Message );
		foreach ( var codec in HL2RPPersistence.Codecs )
		{
			var resolved = binding.Value.Types.Resolve( new PersistedTypeKey( codec.Key.Value ) );
			Assert.AreEqual( codec.ClrType, resolved.ClrType );
		}
	}

	[TestMethod]
	public void EveryCharacterTraitReferenceAndEntityPayloadRoundTrips()
	{
		var samples = CreateSamples().ToDictionary( sample => sample.GetType() );
		Assert.HasCount( HL2RPPersistence.Codecs.Count, samples );

		foreach ( var codec in HL2RPPersistence.Codecs )
		{
			Assert.IsTrue( samples.TryGetValue( codec.ClrType, out var sample ), $"No sample for {codec.ClrType}." );
			var serialized = codec.Serialize( sample! );
			var decoded = codec.Deserialize( serialized, codec.CurrentVersion );
			var reserialized = codec.Serialize( decoded );
			Assert.AreEqual( serialized.GetRawText(), reserialized.GetRawText(), codec.Key.Value );
		}
	}

	[TestMethod]
	public void CurrentPayloadRepresentationDifferencesDoNotTriggerStartupMigration()
	{
		using var persistedDocument = JsonDocument.Parse(
			"""{"stock":1,"unitPrice":5,"cooldownSeconds":2,"cooldownUntilUtc":"2030-01-02T03:04:05+00:00"}""" );
		using var normalizedDocument = JsonDocument.Parse(
			"""{"cooldownUntilUtc":"2030-01-02T03:04:05Z","cooldownSeconds":2.0,"unitPrice":5,"stock":1}""" );
		var persisted = new TypedPayload
		{
			TypeId = new PersistedTypeId( HL2RPIds.PersistedTypes.MachineState ),
			TypeVersion = HL2RPPersistence.MachineState.CurrentVersion,
			Data = persistedDocument.RootElement.Clone()
		};
		var normalized = persisted with { Data = normalizedDocument.RootElement.Clone() };

		Assert.IsFalse( HL2RPPersistence.RequiresVersionMigration( persisted, normalized ) );
		Assert.IsTrue( HL2RPPersistence.RequiresVersionMigration(
			persisted with { TypeVersion = persisted.TypeVersion - 1 }, normalized ) );
		Assert.IsTrue( HL2RPPersistence.RequiresVersionMigration(
			persisted with { TypeId = new PersistedTypeId( HL2RPIds.PersistedTypes.CityState ) }, normalized ) );
	}

	[TestMethod]
	public void CityStateV1MigrationAddsAnExplicitNonFabricatedObjectiveTimestamp()
	{
		using var document = JsonDocument.Parse(
			"""{"objectives":[{"id":"objective.legacy","text":"Legacy directive","completed":false}]}""" );

		var migrated = HL2RPPersistence.CityState.Deserialize( document.RootElement, 1 );

		Assert.AreEqual( 2, HL2RPPersistence.CityState.CurrentVersion );
		Assert.HasCount( 1, migrated.Objectives );
		Assert.AreEqual( DateTimeOffset.UnixEpoch, migrated.Objectives[0].UpdatedAtUtc );
	}

	[TestMethod]
	public void RequestMachineAndScannerV1MigrationsAddSafeExplicitFields()
	{
		using var requestDocument = JsonDocument.Parse(
			"""{"lastRequestAtUtc":null}""" );
		using var machineDocument = JsonDocument.Parse(
			"""{"stock":4,"unitPrice":8,"cooldownUntilUtc":null}""" );
		using var scannerDocument = JsonDocument.Parse(
			"""{"pilotCharacterId":null,"spotlightEnabled":false,"lastAcceptedInputSequence":0,"photoCooldownUntilUtc":null,"photos":[]}""" );

		var request = HL2RPPersistence.RequestDevice.Deserialize( requestDocument.RootElement, 1 );
		var machine = HL2RPPersistence.MachineState.Deserialize( machineDocument.RootElement, 1 );
		var scanner = HL2RPPersistence.ScannerState.Deserialize( scannerDocument.RootElement, 1 );

		Assert.AreEqual( 2, HL2RPPersistence.RequestDevice.CurrentVersion );
		Assert.IsTrue( request.Powered );
		Assert.AreEqual( 2, HL2RPPersistence.MachineState.CurrentVersion );
		Assert.AreEqual( 2d, machine.CooldownSeconds );
		Assert.AreEqual( 2, HL2RPPersistence.ScannerState.CurrentVersion );
		Assert.IsNull( scanner.LinkedEntityId );
	}

	[TestMethod]
	public void PersistedScannerLinksRemainReciprocalAfterRestartCodecRoundTrip()
	{
		var dock = SceneEntityId.New();
		var drone = SceneEntityId.New();
		var dockState = new ScannerEntityState
		{
			LinkedEntityId = drone,
			PilotCharacterId = null,
			SpotlightEnabled = false,
			LastAcceptedInputSequence = 0,
			PhotoCooldownUntilUtc = null
		};
		var droneState = dockState with { LinkedEntityId = dock };

		var recoveredDock = HL2RPPersistence.ScannerState.Deserialize(
			HL2RPPersistence.ScannerState.Serialize( dockState ),
			HL2RPPersistence.ScannerState.CurrentVersion );
		var recoveredDrone = HL2RPPersistence.ScannerState.Deserialize(
			HL2RPPersistence.ScannerState.Serialize( droneState ),
			HL2RPPersistence.ScannerState.CurrentVersion );

		Assert.IsTrue( ScannerSceneLinkTopology.Validate( new[]
		{
			new ScannerSceneLinkState( dock, "scanner_dock", recoveredDock.LinkedEntityId ),
			new ScannerSceneLinkState( drone, "scanner_drone", recoveredDrone.LinkedEntityId )
		} ).Succeeded );
		Assert.IsTrue( ScannerSceneLinkTopology.Validate( new[]
		{
			new ScannerSceneLinkState( dock, "scanner_dock", recoveredDock.LinkedEntityId ),
			new ScannerSceneLinkState( drone, "scanner_drone", null )
		} ).Failed );
	}

	[TestMethod]
	public void DoorOwnershipRecognitionAndRestraintUseIndexedCharacterReferences()
	{
		var owner = CharacterId.New();
		var related = CharacterId.New();
		var sceneEntity = SceneEntityId.New();
		var now = DateTimeOffset.UnixEpoch;
		var door = new CharacterReferenceRecord
		{
			Category = "door_ownership",
			CharacterId = owner,
			SceneEntityId = sceneEntity,
			State = HL2RPPersistence.Payload(
				HL2RPPersistence.DoorOwnership,
				new DoorOwnershipReferenceState { AcquiredAtUtc = now } )
		};
		var recognition = new CharacterReferenceRecord
		{
			Category = "recognition",
			CharacterId = owner,
			RelatedCharacterId = related,
			State = HL2RPPersistence.Payload(
				HL2RPPersistence.Recognition,
				new RecognitionReferenceState
				{
					IntroducedName = "Citizen",
					IntroducedAtUtc = now
				} )
		};
		var restraint = new CharacterReferenceRecord
		{
			Category = "restraint",
			CharacterId = related,
			RelatedCharacterId = owner,
			State = HL2RPPersistence.Payload(
				HL2RPPersistence.Restraint,
				new RestraintReferenceState { RestrainedAtUtc = now, Active = true } )
		};

		Assert.AreEqual( owner, door.CharacterId );
		Assert.AreEqual( sceneEntity, door.SceneEntityId );
		Assert.AreEqual( related, recognition.RelatedCharacterId );
		Assert.AreEqual( owner, restraint.RelatedCharacterId );
		Assert.AreEqual( HL2RPIds.PersistedTypes.DoorOwnership, door.State.TypeId.Value );
		Assert.AreEqual( HL2RPIds.PersistedTypes.Recognition, recognition.State.TypeId.Value );
		Assert.AreEqual( HL2RPIds.PersistedTypes.Restraint, restraint.State.TypeId.Value );
	}

	private static IReadOnlyList<object> CreateSamples()
	{
		var account = new AccountId( 42 );
		var character = CharacterId.New();
		var related = CharacterId.New();
		var linkedSceneEntity = SceneEntityId.New();
		var inventory = InventoryId.New();
		var now = new DateTimeOffset( 2030, 1, 2, 3, 4, 5, TimeSpan.Zero );
		return new object[]
		{
			new HL2RPAccountEntitlementRecord
			{
				AccountId = account,
				Flags = HL2RPWhitelist.CivilProtection | HL2RPWhitelist.Overwatch,
				UpdatedByAccountId = new AccountId( 99 ),
				UpdatedByCharacterId = related,
				UpdatedAtUtc = now
			},
			new HL2RPCharacterState
			{
				CitizenId = "C17-00000000000000000042-00",
				Age = 28,
				Pronouns = "they/them",
				Origin = "city_17",
				Whitelists = HL2RPWhitelist.CivilProtection,
				CivicRecord = new CivicRecordState
				{
					Points = 2,
					Priority = CivicPriorityStatus.Watch,
					Infractions = new[]
					{
						new CivicInfractionState
						{
							Code = "17-1",
							Summary = "Test infraction",
							Points = 2,
							IssuedAtUtc = now,
							IssuedBy = account
						}
					}
				},
				CombineIdentity = new CombineIdentityState
				{
					Rank = CombineRank.Unit,
					Division = CombineDivision.Protection,
					ServiceName = "C17.CCA-UNIT.00042"
				}
			},
			new CitizenIdCardItemState
			{
				CitizenId = "C17-00000000000000000042-00",
				IssuedName = "Citizen",
				IssuedAtUtc = now,
				Priority = CivicPriorityStatus.None
			},
			new RadioItemState { Frequency = "100.0", Powered = true },
			new FlashlightItemState { Powered = false, ChargePermille = 1_000 },
			new RequestDeviceItemState { Powered = true, LastRequestAtUtc = now },
			new NoteItemState { Text = "Test", OwnerCharacterId = character, UpdatedAtUtc = now },
			new BusinessPermitItemState
			{
				Kind = BusinessPermitKind.Food,
				OwnerCharacterId = character,
				IssuedAtUtc = now,
				ExpiresAtUtc = now.AddDays( 30 ),
				Revoked = false
			},
			new PistolItemState
			{
				MagazineRounds = 12,
				Equipped = true,
				Raised = false,
				LastFiredAtUtc = now
			},
			new PistolAmmunitionItemState { Rounds = 18 },
			new ProtectiveVestItemState { Durability = 90, DamageReductionPermille = 300, Equipped = true },
			new CombineLockKitItemState { RemainingInstallations = 1 },
			new TokenStackItemState { Amount = 25 },
			new RecognitionReferenceState { IntroducedName = "Citizen", IntroducedAtUtc = now },
			new RestraintReferenceState { RestrainedAtUtc = now, Active = true },
			new DoorOwnershipReferenceState { AcquiredAtUtc = now },
			new DoorEntityState { CombineLocked = true, IsOpen = false },
			new StorageEntityState { InventoryId = inventory, Locked = false },
			new VendorEntityState
			{
				RequiredPermit = BusinessPermitKind.General,
				Stock = new[]
				{
					new VendorStockEntry
					{
						Definition = new DefinitionId( HL2RPIds.Items.Water ),
						Quantity = 4,
						UnitPrice = 5
					}
				}
			},
			new MachineEntityState { Stock = 5, UnitPrice = 10, CooldownSeconds = 7.5, CooldownUntilUtc = now },
			new ForcefieldEntityState { Enabled = true, CombineOnly = true },
			new ScannerEntityState
			{
				LinkedEntityId = linkedSceneEntity,
				PilotCharacterId = character,
				SpotlightEnabled = true,
				LastAcceptedInputSequence = 4,
				PhotoCooldownUntilUtc = now.AddSeconds( 15 ),
				Photos = new[]
				{
					new ScannerPhotoMetadata
					{
						PhotoId = Guid.NewGuid(),
						PilotCharacterId = character,
						CapturedAtUtc = now,
						PositionX = 1,
						PositionY = 2,
						PositionZ = 3
					}
				}
			},
			new CombatTargetEntityState { MaximumHealth = 100, CurrentHealth = 75, LastHitAtUtc = now },
			new CityEntityState
			{
				Objectives = new[]
				{
					new CityObjectiveState { Id = "test", Text = "Test objective", Completed = false, UpdatedAtUtc = now }
				}
			}
		};
	}
}
