#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Kernel.Policies;
using Hexagon.V2.Kernel.Schema;
using HL2RP.V2.Features;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Schema;

[TestClass]
public sealed class HL2RPSchemaTests
{
	[TestMethod]
	public void SchemaCompilesWithEightExplicitAcyclicModules()
	{
		var compiled = Compile();

		Assert.AreEqual( HL2RPIds.Schema, compiled.Id );
		AssertSetEquals(
			new[]
			{
				HL2RPIds.Modules.CivicIdentity,
				HL2RPIds.Modules.Combine,
				HL2RPIds.Modules.Communications,
				HL2RPIds.Modules.Commerce,
				HL2RPIds.Modules.Documents,
				HL2RPIds.Modules.Restraint,
				HL2RPIds.Modules.Scanner,
				HL2RPIds.Modules.Combat
			},
			compiled.Modules.Select( module => module.Id ) );
		AssertDependencies( compiled, HL2RPIds.Modules.CivicIdentity );
		AssertDependencies( compiled, HL2RPIds.Modules.Combine, HL2RPIds.Modules.CivicIdentity );
		AssertDependencies( compiled, HL2RPIds.Modules.Communications, HL2RPIds.Modules.CivicIdentity );
		AssertDependencies( compiled, HL2RPIds.Modules.Commerce, HL2RPIds.Modules.CivicIdentity );
		AssertDependencies( compiled, HL2RPIds.Modules.Documents, HL2RPIds.Modules.CivicIdentity );
		AssertDependencies(
			compiled,
			HL2RPIds.Modules.Restraint,
			HL2RPIds.Modules.CivicIdentity,
			HL2RPIds.Modules.Combine );
		AssertDependencies(
			compiled,
			HL2RPIds.Modules.Scanner,
			HL2RPIds.Modules.Combine,
			HL2RPIds.Modules.Communications );
		AssertDependencies( compiled, HL2RPIds.Modules.Combat, HL2RPIds.Modules.Combine );
	}

	[TestMethod]
	public void SchemaRegistersOnlyCuratedFactionsAndCivilProtectionClasses()
	{
		var compiled = Compile();

		AssertSetEquals(
			new[]
			{
				HL2RPIds.Factions.Citizen,
				HL2RPIds.Factions.CivilProtection,
				HL2RPIds.Factions.Overwatch,
				HL2RPIds.Factions.CityAdministration
			},
			compiled.Factions.All.Select( faction => faction.Id ) );
		AssertSetEquals(
			new[]
			{
				HL2RPIds.Classes.Recruit,
				HL2RPIds.Classes.Unit,
				HL2RPIds.Classes.Elite,
				HL2RPIds.Classes.Scanner
			},
			compiled.Classes.All.Select( characterClass => characterClass.Id ) );
		Assert.IsTrue( compiled.Classes.All.All(
			characterClass => characterClass.FactionId == HL2RPIds.Factions.CivilProtection ) );
		Assert.IsTrue( compiled.Factions.Require( HL2RPIds.Factions.Citizen ).Value.IsDefault );
		Assert.AreEqual(
			HL2RPIds.Classes.Recruit,
			compiled.Factions.Require( HL2RPIds.Factions.CivilProtection ).Value.DefaultClassId );
	}

	[TestMethod]
	public void CreationChannelsCommandsPermissionsPanelsAndInitializersAreExplicit()
	{
		var compiled = Compile();

		AssertSetEquals(
			new[]
			{
				HL2RPIds.CreationFields.Age,
				HL2RPIds.CreationFields.Pronouns,
				HL2RPIds.CreationFields.Origin
			},
			compiled.CharacterFields.All.Select( field => field.Id ) );
		Assert.IsTrue( compiled.CharacterFields.All.All( field => field.ShowInCreation && field.Required ) );
		AssertSetEquals(
			new[]
			{
				HL2RPIds.Channels.InCharacter,
				HL2RPIds.Channels.OutOfCharacter,
				HL2RPIds.Channels.LocalOutOfCharacter,
				HL2RPIds.Channels.Whisper,
				HL2RPIds.Channels.Yell,
				HL2RPIds.Channels.Emote,
				HL2RPIds.Channels.Radio,
				HL2RPIds.Channels.Request,
				HL2RPIds.Channels.Dispatch
			},
			compiled.ChatChannels.All.Select( channel => channel.Id ) );
		AssertSetEquals(
			new[]
			{
				HL2RPIds.Commands.CivicData,
				HL2RPIds.Commands.CityObjectives,
				HL2RPIds.Commands.Priority,
				HL2RPIds.Commands.RadioFrequency,
				HL2RPIds.Commands.Introduce,
				HL2RPIds.Commands.DoorOwnership,
				HL2RPIds.Commands.AdministrationAudit,
				HL2RPIds.Commands.EntitlementQuery,
				HL2RPIds.Commands.EntitlementGrant,
				HL2RPIds.Commands.EntitlementRevoke,
				HL2RPIds.Commands.CommerceBuy,
				HL2RPIds.Commands.CommerceSell,
				HL2RPIds.Commands.PermitPurchase,
				HL2RPIds.Commands.NoteWrite,
				HL2RPIds.Commands.RestraintSet,
				HL2RPIds.Commands.ScannerIntent,
				HL2RPIds.Commands.CombatRespawn
			},
			compiled.Commands.All.Select( command => command.Id ) );
		Assert.AreEqual( CommandCostClass.Expensive,
			compiled.Commands.Require( HL2RPIds.Commands.CityObjectives ).Value.Cost );
		Assert.AreEqual( CommandCostClass.Expensive,
			compiled.Commands.Require( HL2RPIds.Commands.EntitlementGrant ).Value.Cost );
		Assert.AreEqual( CommandCostClass.Cheap,
			compiled.Commands.Require( HL2RPIds.Commands.CivicData ).Value.Cost );
		Assert.AreEqual( CommandCostClass.Cheap,
			compiled.Commands.Require( HL2RPIds.Commands.ScannerIntent ).Value.Cost );
		Assert.IsTrue( compiled.Commands.All.All( command => Enum.IsDefined( command.Cost ) ) );
		Assert.AreEqual( 12, compiled.Permissions.Count );
		Assert.AreEqual( 21, compiled.Panels.Count );
		Assert.AreEqual( 2, compiled.Initializers.Count );
		Assert.AreEqual(
			typeof(HL2RPCitizenIdInitializer),
			compiled.Initializers.Require( HL2RPCitizenIdInitializer.InitializerId ).Value.InitializerType );
		Assert.AreEqual(
			typeof(HL2RPLoadoutInitializer),
			compiled.Initializers.Require( HL2RPLoadoutInitializer.InitializerId ).Value.InitializerType );
		Assert.IsLessThan(
			HL2RPLoadoutInitializer.InitializerOrder,
			HL2RPCitizenIdInitializer.InitializerOrder );
	}

	[TestMethod]
	public void EveryUsedApplicationContextHasExactlyOneBuiltInPolicy()
	{
		var compiled = Compile();

		AssertPipeline<CharacterCreationContext>( compiled );
		AssertPipeline<CharacterDeletionContext>( compiled );
		AssertPipeline<InventoryTransferContext>( compiled );
		AssertPipeline<WorldDropContext>( compiled );
		AssertPipeline<WorldPickupContext>( compiled );
		AssertPipeline<CharacterMutationContext>( compiled );
		AssertPipeline<ItemTraitMutationContext>( compiled );
		AssertPipeline<SceneEntityStateMutationContext>( compiled );
		AssertPipeline<ItemActionContext>( compiled );
		AssertPipeline<ChatSendContext>( compiled );
		AssertPipeline<ServerInteractionContext>( compiled );
		AssertPipeline<HL2RPFeaturePolicyContext>( compiled );
	}

	[TestMethod]
	public void DurableRuntimeConfigurationIsExplicitTypedAndUsesMandatedDefaults()
	{
		var compiled = Compile();

		AssertSetEquals(
			new[]
			{
				HL2RPIds.Configs.CharacterInventoryWidth,
				HL2RPIds.Configs.CharacterInventoryHeight,
				HL2RPIds.Configs.InteractionIdleSeconds,
				HL2RPIds.Configs.ChatRateCapacity,
				HL2RPIds.Configs.ChatRateWindowSeconds
			},
			compiled.Configs.All.Select( value => value.Id ) );
		Assert.AreEqual( 8, compiled.Configs.Require<int>( HL2RPIds.Configs.CharacterInventoryWidth ).Value.DefaultValue );
		Assert.AreEqual( 6, compiled.Configs.Require<int>( HL2RPIds.Configs.CharacterInventoryHeight ).Value.DefaultValue );
		Assert.AreEqual( 60, compiled.Configs.Require<int>( HL2RPIds.Configs.InteractionIdleSeconds ).Value.DefaultValue );
		Assert.AreEqual( 4, compiled.Configs.Require<int>( HL2RPIds.Configs.ChatRateCapacity ).Value.DefaultValue );
		Assert.AreEqual( 5, compiled.Configs.Require<int>( HL2RPIds.Configs.ChatRateWindowSeconds ).Value.DefaultValue );
		Assert.IsTrue( compiled.Configs.Require<int>( HL2RPIds.Configs.ChatRateCapacity ).Value.Validate( 5 ).Failed );
		Assert.IsTrue( compiled.Configs.Require<int>( HL2RPIds.Configs.InteractionIdleSeconds ).Value.Validate( 61 ).Failed );
	}

	[TestMethod]
	public void FeaturePolicyPipelineComposesRuntimeAuthorizationDenial()
	{
		var compiled = Compile();
		var runtimeCalls = 0;
		var pipeline = compiled.CreatePolicyPipeline<HL2RPFeaturePolicyContext>( new[]
		{
			new PolicyHandler<HL2RPFeaturePolicyContext>(
				"hl2rp.runtime.authorization",
				new DelegatePolicy<HL2RPFeaturePolicyContext>( _ =>
				{
					runtimeCalls++;
					return PolicyDecision.Deny( "Runtime role denied the feature.", ErrorCode.Unauthorized );
				} ),
				100 )
		} );

		Assert.IsTrue( pipeline.Succeeded, pipeline.Error?.Message );
		var result = pipeline.Value.Evaluate( new HL2RPFeaturePolicyContext
		{
			Actor = new InventoryActor( ConnectionId.New(), new AccountId( 1 ), CharacterId.New() ),
			Operation = HL2RPFeatureOperation.OpenBag,
			ItemId = ItemId.New()
		} );

		Assert.IsTrue( result.Failed );
		Assert.AreEqual( ErrorCode.Unauthorized, result.Error!.Code );
		Assert.AreEqual( 1, runtimeCalls );
	}

	internal static CompiledSchema Compile()
	{
		var result = SchemaCompiler.Compile( new HL2RPSchema() );
		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		return result.Value;
	}

	internal static void AssertSetEquals( IEnumerable<string> expected, IEnumerable<string> actual ) =>
		CollectionAssert.AreEquivalent( expected.ToArray(), actual.ToArray() );

	private static void AssertDependencies(
		CompiledSchema schema,
		string moduleId,
		params string[] expected )
	{
		var module = schema.Modules.Single( candidate => candidate.Id == moduleId );
		AssertSetEquals( expected, module.Dependencies );
	}

	private static void AssertPipeline<TContext>( CompiledSchema schema )
	{
		var pipeline = schema.CreatePolicyPipeline<TContext>();
		Assert.IsTrue( pipeline.Succeeded, pipeline.Error?.Message );
	}

	private sealed class DelegatePolicy<TContext> : IPolicy<TContext>
	{
		private readonly Func<TContext, PolicyDecision> _evaluate;

		public DelegatePolicy( Func<TContext, PolicyDecision> evaluate ) => _evaluate = evaluate;

		public PolicyDecision Evaluate( TContext context ) => _evaluate( context );
	}
}
