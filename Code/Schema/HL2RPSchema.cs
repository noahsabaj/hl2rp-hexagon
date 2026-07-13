#nullable enable

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
