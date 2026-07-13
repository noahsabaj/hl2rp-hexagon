#nullable enable

namespace HL2RP.V2.Features;

public static class HL2RPNonCombatItemActions
{
	public const long RationTokenReward = 10;

	public static IReadOnlyList<IItemActionHandler> Create( IHexClock clock )
	{
		ArgumentNullException.ThrowIfNull( clock );
		return new IItemActionHandler[]
		{
			Handler( HL2RPIds.Actions.Show, Show ),
			Handler( HL2RPIds.Actions.Open, Open ),
			Handler( HL2RPIds.Actions.Consume, Consume ),
			Handler( HL2RPIds.Actions.Toggle, Toggle ),
			Handler( HL2RPIds.Actions.Read, Read ),
			Handler( HL2RPIds.Actions.PresentPermit, context => PresentPermit( context, clock.UtcNow ) )
		};
	}

	private static OperationResult<ItemActionPlan> Show( ItemActionContext context )
	{
		if ( context.Item.Definition.Value != HL2RPIds.Items.CitizenIdCard ||
			!context.Item.Traits.TryGetValue( "cid", out var payload ) )
			return Denied( "Show is not meaningful for this item." );
		var state = HL2RPFeaturePersistence.Decode( payload, HL2RPPersistence.CitizenIdCard );
		return state.Succeeded
			? OperationResult<ItemActionPlan>.Success( new ItemActionPlan
			{
				Presentation = HL2RPItemActionPresentations.CitizenIdCard( state.Value )
			} )
			: OperationResult<ItemActionPlan>.Failure( state.Error!.Code, state.Error.Message );
	}

	private static OperationResult<ItemActionPlan> Open( ItemActionContext context )
	{
		if ( context.Item.Definition.Value != HL2RPIds.Items.Ration )
			return Denied( "Open is not meaningful for this item." );
		var credited = CurrencyService.Credit( context.Character, RationTokenReward );
		if ( credited.Failed )
			return OperationResult<ItemActionPlan>.Failure( credited.Error!.Code, credited.Error.Message );
		return OperationResult<ItemActionPlan>.Success( new ItemActionPlan
		{
			DeletedItems = new HashSet<ItemId> { context.Item.Id },
			UpdatedCharacter = credited.Value
		} );
	}

	private static OperationResult<ItemActionPlan> Consume( ItemActionContext context )
	{
		if ( context.Item.Definition.Value != HL2RPIds.Items.Water )
			return Denied( "This consumable requires a host health or effect service." );
		return OperationResult<ItemActionPlan>.Success( new ItemActionPlan
		{
			DeletedItems = new HashSet<ItemId> { context.Item.Id }
		} );
	}

	private static OperationResult<ItemActionPlan> Toggle( ItemActionContext context )
	{
		if ( context.Item.Definition.Value != HL2RPIds.Items.Flashlight ||
			!context.Item.Traits.TryGetValue( "flashlight", out var payload ) )
			return Denied( "Toggle is not meaningful for this item." );
		var state = HL2RPFeaturePersistence.Decode( payload, HL2RPPersistence.Flashlight );
		if ( state.Failed )
			return OperationResult<ItemActionPlan>.Failure( state.Error!.Code, state.Error.Message );
		var traits = new Dictionary<string, TypedPayload>( context.Item.Traits, StringComparer.Ordinal )
		{
			["flashlight"] = HL2RPPersistence.Payload(
				HL2RPPersistence.Flashlight,
				state.Value with { Powered = !state.Value.Powered } )
		};
		return OperationResult<ItemActionPlan>.Success( new ItemActionPlan
		{
			UpdatedItems = new Dictionary<ItemId, ItemRecord>
			{
				[context.Item.Id] = context.Item with { Traits = traits }
			}
		} );
	}

	private static OperationResult<ItemActionPlan> Read( ItemActionContext context )
	{
		if ( context.Item.Definition.Value == HL2RPIds.Items.CivicHandbook )
			return OperationResult<ItemActionPlan>.Success( new ItemActionPlan
			{
				Presentation = HL2RPItemActionPresentations.CivicHandbook()
			} );
		if ( context.Item.Definition.Value != HL2RPIds.Items.Note ||
			!context.Item.Traits.TryGetValue( "note", out var payload ) )
			return Denied( "Read is not meaningful for this item." );
		var decoded = HL2RPFeaturePersistence.Decode( payload, HL2RPPersistence.Note );
		return decoded.Succeeded
			? OperationResult<ItemActionPlan>.Success( new ItemActionPlan
			{
				Presentation = HL2RPItemActionPresentations.Note( decoded.Value )
			} )
			: OperationResult<ItemActionPlan>.Failure( decoded.Error!.Code, decoded.Error.Message );
	}

	private static OperationResult<ItemActionPlan> PresentPermit(
		ItemActionContext context,
		DateTimeOffset nowUtc )
	{
		if ( context.Item.Definition.Value != HL2RPIds.Items.BusinessPermit ||
			!context.Item.Traits.TryGetValue( "permit", out var payload ) )
			return Denied( "Present permit is not meaningful for this item." );
		var permit = HL2RPFeaturePersistence.Decode( payload, HL2RPPersistence.BusinessPermit );
		if ( permit.Failed )
			return OperationResult<ItemActionPlan>.Failure( permit.Error!.Code, permit.Error.Message );
		if ( permit.Value.OwnerCharacterId != context.Actor.CharacterId || permit.Value.Revoked ||
			permit.Value.ExpiresAtUtc is not null && permit.Value.ExpiresAtUtc <= nowUtc )
			return OperationResult<ItemActionPlan>.Failure(
				ErrorCode.Unauthorized, "Business permit is not valid for the active character." );
		return OperationResult<ItemActionPlan>.Success( new ItemActionPlan
		{
			Presentation = HL2RPItemActionPresentations.BusinessPermit( permit.Value )
		} );
	}

	private static IItemActionHandler Handler(
		string id,
		Func<ItemActionContext, OperationResult<ItemActionPlan>> plan ) =>
		new DelegateItemActionHandler( new ActionId( id ), plan );

	private static OperationResult<ItemActionPlan> Denied( string reason ) =>
		OperationResult<ItemActionPlan>.Failure( ErrorCode.PolicyDenied, reason );

	private sealed class DelegateItemActionHandler : IItemActionHandler
	{
		private readonly Func<ItemActionContext, OperationResult<ItemActionPlan>> _plan;

		public DelegateItemActionHandler(
			ActionId id,
			Func<ItemActionContext, OperationResult<ItemActionPlan>> plan )
		{
			Id = id;
			_plan = plan;
		}

		public ActionId Id { get; }
		public OperationResult<ItemActionPlan> Plan( ItemActionContext context ) => _plan( context );
	}
}
