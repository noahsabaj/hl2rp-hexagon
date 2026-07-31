#nullable enable

using System.Collections.Generic;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Domain;

/// <summary>
/// Exact schema payload contract consumed by Hexagon before HL2RP host
/// construction. Keep this declaration aligned with every schema initializer,
/// item factory, reference service, and scene-state initializer.
/// </summary>
public static class HL2RPPersistenceInvariants
{
	public static SchemaPersistenceInvariantProfile Profile { get; } = new(
		new PersistedTypeId( HL2RPIds.PersistedTypes.CharacterState ),
		new[]
		{
			ItemPersistenceContract.WithTrait( HL2RPIds.Items.CitizenIdCard, "cid", HL2RPIds.PersistedTypes.CitizenIdCard ),
			ItemPersistenceContract.WithoutTraits( HL2RPIds.Items.Ration ),
			ItemPersistenceContract.WithoutTraits( HL2RPIds.Items.Water ),
			ItemPersistenceContract.WithoutTraits( HL2RPIds.Items.HealthVial ),
			ItemPersistenceContract.WithTrait( HL2RPIds.Items.Flashlight, "flashlight", HL2RPIds.PersistedTypes.Flashlight ),
			ItemPersistenceContract.WithTrait( HL2RPIds.Items.Radio, "radio", HL2RPIds.PersistedTypes.Radio ),
			ItemPersistenceContract.WithTrait( HL2RPIds.Items.RequestDevice, "request_device", HL2RPIds.PersistedTypes.RequestDevice ),
			ItemPersistenceContract.WithTrait( HL2RPIds.Items.Note, "note", HL2RPIds.PersistedTypes.Note ),
			ItemPersistenceContract.WithoutTraits( HL2RPIds.Items.CivicHandbook ),
			ItemPersistenceContract.WithoutTraits( HL2RPIds.Items.Suitcase ),
			ItemPersistenceContract.WithTrait( HL2RPIds.Items.BusinessPermit, "permit", HL2RPIds.PersistedTypes.BusinessPermit ),
			ItemPersistenceContract.WithoutTraits( HL2RPIds.Items.ZipTie ),
			ItemPersistenceContract.WithTrait( HL2RPIds.Items.Pistol, "pistol", HL2RPIds.PersistedTypes.Pistol ),
			ItemPersistenceContract.WithTrait( HL2RPIds.Items.PistolAmmunition, "ammunition", HL2RPIds.PersistedTypes.PistolAmmunition ),
			ItemPersistenceContract.WithTrait( HL2RPIds.Items.ProtectiveVest, "vest", HL2RPIds.PersistedTypes.ProtectiveVest ),
			ItemPersistenceContract.WithTrait( HL2RPIds.Items.CombineLockKit, "lock_kit", HL2RPIds.PersistedTypes.CombineLockKit ),
			ItemPersistenceContract.WithTrait( HL2RPIds.Items.TokenStack, "tokens", HL2RPIds.PersistedTypes.TokenStack )
		},
		new Dictionary<string, PersistedTypeId>( System.StringComparer.Ordinal )
		{
			["recognition"] = new PersistedTypeId( HL2RPIds.PersistedTypes.Recognition ),
			["restraint"] = new PersistedTypeId( HL2RPIds.PersistedTypes.Restraint ),
			["door_ownership"] = new PersistedTypeId( HL2RPIds.PersistedTypes.DoorOwnership )
		},
		new Dictionary<string, PersistedTypeId>( System.StringComparer.Ordinal )
		{
			["door"] = new PersistedTypeId( HL2RPIds.PersistedTypes.DoorState ),
			["storage"] = new PersistedTypeId( HL2RPIds.PersistedTypes.StorageState ),
			["vendor"] = new PersistedTypeId( HL2RPIds.PersistedTypes.VendorState ),
			["ration_dispenser"] = new PersistedTypeId( HL2RPIds.PersistedTypes.MachineState ),
			["vending_machine"] = new PersistedTypeId( HL2RPIds.PersistedTypes.MachineState ),
			["forcefield"] = new PersistedTypeId( HL2RPIds.PersistedTypes.ForcefieldState ),
			["scanner_dock"] = new PersistedTypeId( HL2RPIds.PersistedTypes.ScannerState ),
			["scanner_drone"] = new PersistedTypeId( HL2RPIds.PersistedTypes.ScannerState ),
			["combat_target"] = new PersistedTypeId( HL2RPIds.PersistedTypes.CombatTargetState ),
			["city"] = new PersistedTypeId( HL2RPIds.PersistedTypes.CityState )
		} );
}
