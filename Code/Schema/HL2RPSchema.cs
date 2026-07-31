#nullable enable

using System;
using Hexagon.V2.Application;
using Hexagon.V2.Kernel.Configuration;

namespace HL2RP.V2.Schema;

public sealed class HL2RPSchema : IHexSchema
{
	public string Id => HL2RPIds.Schema;

	public void Configure( SchemaBuilder builder )
	{
		builder.AddModule( new CivicIdentityModule() );
		builder.AddModule( new CombineModule() );
		builder.AddModule( new CommunicationsModule() );
		builder.AddModule( new CommerceModule() );
		builder.AddModule( new DocumentsModule() );
		builder.AddModule( new RestraintModule() );
		builder.AddModule( new ScannerModule() );
		builder.AddModule( new CombatModule() );

		foreach ( var codec in HL2RPPersistence.Codecs )
		{
			builder.RegisterPersistedType( new PersistedTypeRegistration(
				codec.Key.Value,
				codec.ClrType,
				codec.CurrentVersion ) );
		}

		builder.RegisterConfig( PositiveInt( HL2RPIds.Configs.CharacterInventoryWidth, 8, 4, 16 ) );
		builder.RegisterConfig( PositiveInt( HL2RPIds.Configs.CharacterInventoryHeight, 6, 4, 16 ) );
		builder.RegisterConfig( PositiveInt( HL2RPIds.Configs.InteractionIdleSeconds, 60, 5, 60 ) );
		builder.RegisterConfig( PositiveInt( HL2RPIds.Configs.ChatRateCapacity, 4, 1, 4 ) );
		builder.RegisterConfig( PositiveInt( HL2RPIds.Configs.ChatRateWindowSeconds, 5, 5, 60 ) );

		// How close two character names may be before the second is refused. Strict by default;
		// an operator loosens it if the look-alike folding proves too aggressive against real
		// player names. It governs only how CLOSE names may be - what makes a name well-formed at
		// all stays a persistence invariant, so tightening this can never leave a store unable to
		// pass its own startup validation.
		builder.RegisterConfig( new ConfigDefinition<string>(
			HL2RPIds.Configs.CharacterNameUniqueness,
			nameof( CharacterRules.NameUniqueness.Skeleton ),
			ConfigCodecs.String,
			value => Enum.TryParse<CharacterRules.NameUniqueness>( value, true, out _ )
				? OperationResult.Success()
				: OperationResult.Failure(
					ErrorCode.ConfigurationInvalid,
					"Configuration '" + HL2RPIds.Configs.CharacterNameUniqueness +
					"' must be Skeleton, Exact or None." ) ) );

		HL2RPPolicies.Register( builder );
	}

	private static ConfigDefinition<int> PositiveInt(
		string id,
		int defaultValue,
		int minimum,
		int maximum ) => new(
		id,
		defaultValue,
		ConfigCodecs.Int32,
		value => value >= minimum && value <= maximum
			? OperationResult.Success()
			: OperationResult.Failure(
				ErrorCode.ConfigurationInvalid,
				$"Configuration '{id}' must be between {minimum} and {maximum}." ) );
}
