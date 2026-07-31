#nullable enable

using HL2RP.V2.Features;

namespace HL2RP.V2.Schema;

internal sealed class HL2RPAllowPolicy<TContext> : IPolicy<TContext>
{
	public PolicyDecision Evaluate( TContext context ) => PolicyDecision.Allow();
}

internal sealed class HL2RPCharacterDeletionPolicy : IPolicy<CharacterDeletionContext>
{
	public PolicyDecision Evaluate( CharacterDeletionContext context ) =>
		context.ActorAccountId == context.Character.AccountId
			? PolicyDecision.Allow()
			: PolicyDecision.Deny( "Character deletion actor does not own the character.", ErrorCode.Unauthorized );
}

/// <summary>
/// Enforces the context invariants shared by every showcase feature before any
/// runtime authorization rule consults live repositories or role state.
/// </summary>
internal sealed class HL2RPFeatureStructuralPolicy : IPolicy<HL2RPFeaturePolicyContext>
{
	public PolicyDecision Evaluate( HL2RPFeaturePolicyContext context )
	{
		if ( context is null )
			return PolicyDecision.Deny( "Feature policy context is required.", ErrorCode.InvalidArgument );
		if ( context.Actor.ConnectionId.Value == Guid.Empty ||
			context.Actor.AccountId.Value == 0 ||
			context.Actor.CharacterId.Value == Guid.Empty )
			return PolicyDecision.Deny( "Feature policy actor identity is incomplete.", ErrorCode.Unauthorized );
		if ( !Enum.IsDefined( context.Operation ) )
			return PolicyDecision.Deny( "Feature policy operation is unknown.", ErrorCode.InvalidArgument );

		return PolicyDecision.Allow();
	}
}

internal static class HL2RPPolicies
{
	private const string BuiltInId = "hl2rp.builtin";

	public static void Register( SchemaBuilder builder )
	{
		builder.RegisterBuiltInPolicy( BuiltInId, new HL2RPAllowPolicy<CharacterCreationContext>() );
		builder.RegisterBuiltInPolicy( BuiltInId, new HL2RPCharacterDeletionPolicy() );
		builder.RegisterBuiltInPolicy( BuiltInId, new HL2RPAllowPolicy<InventoryTransferContext>() );
		builder.RegisterBuiltInPolicy( BuiltInId, new HL2RPAllowPolicy<WorldDropContext>() );
		builder.RegisterBuiltInPolicy( BuiltInId, new HL2RPAllowPolicy<WorldPickupContext>() );
		builder.RegisterBuiltInPolicy( BuiltInId, new HL2RPAllowPolicy<CharacterMutationContext>() );
		builder.RegisterBuiltInPolicy( BuiltInId, new HL2RPAllowPolicy<ItemTraitMutationContext>() );
		builder.RegisterBuiltInPolicy( BuiltInId, new HL2RPAllowPolicy<SceneEntityStateMutationContext>() );
		builder.RegisterBuiltInPolicy( BuiltInId, new HL2RPAllowPolicy<ItemActionContext>() );
		builder.RegisterBuiltInPolicy( BuiltInId, new HL2RPAllowPolicy<ChatSendContext>() );
		builder.RegisterBuiltInPolicy( BuiltInId, new HL2RPAllowPolicy<ServerInteractionContext>() );
		builder.RegisterBuiltInPolicy( BuiltInId, new HL2RPFeatureStructuralPolicy() );
	}
}
