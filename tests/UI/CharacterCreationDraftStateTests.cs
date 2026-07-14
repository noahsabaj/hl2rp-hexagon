#nullable enable

using System.Collections.Immutable;
using System.Linq;
using Hexagon.V2.Domain;
using Hexagon.V2.Networking;
using HL2RP.UI;

namespace HL2RP.V2.Tests.UI;

[TestClass]
public sealed class CharacterCreationDraftStateTests
{
	private static readonly FactionId Citizen = new( "citizen" );
	private static readonly FactionId Combine = new( "combine" );
	private static readonly DefinitionId CitizenModelA = new( "citizen-a" );
	private static readonly DefinitionId CitizenModelB = new( "citizen-b" );
	private static readonly DefinitionId CombineModel = new( "combine-model" );
	private static readonly ClassId CitizenClass = new( "citizen-class" );
	private static readonly ClassId CombineClass = new( "combine-class" );

	[TestMethod]
	public void FinalRequiredTextEditImmediatelyMakesCompleteDraftSubmittable()
	{
		var draft = new CharacterCreationDraftState();
		draft.Reconcile( StandardCreation() );
		draft.SetFieldText( "age", "30" );

		Assert.IsFalse( draft.CanSubmit( "Alex Citizen", "Average height and build." ) );

		draft.SetFieldText( "pronouns", "they/them" );

		Assert.IsTrue( draft.CanSubmit( "Alex Citizen", "Average height and build." ) );
	}

	[TestMethod]
	public void StructurallyEquivalentProjectionRefreshPreservesEntireInProgressDraft()
	{
		var draft = new CharacterCreationDraftState();
		draft.Reconcile( StandardCreation() );
		draft.SelectModel( CitizenModelB );
		draft.SelectClass( null );
		draft.SetFieldText( "age", "34" );
		draft.SetFieldText( "pronouns", "she/her" );
		draft.SetChoice( "origin", "relocated" );

		draft.Reconcile( StandardCreation() );

		Assert.AreEqual( Citizen, draft.SelectedFaction );
		Assert.AreEqual( CitizenModelB, draft.SelectedModel );
		Assert.IsNull( draft.SelectedClass );
		Assert.AreEqual( "34", draft.GetFieldText( "age" ) );
		Assert.AreEqual( "she/her", draft.GetFieldText( "pronouns" ) );
		Assert.AreEqual( "relocated", draft.GetChoice( "origin" ) );
		Assert.IsTrue( draft.CanSubmit( "Alex Citizen", "Average height and build." ) );
	}

	[TestMethod]
	public void ChangedSchemaAndAvailabilityPreserveCompatibleValuesAndReconcileInvalidState()
	{
		var draft = new CharacterCreationDraftState();
		var initial = new CharacterCreationViewModel(
			ImmutableArray.Create(
				new ModelChoice( CitizenModelA, Citizen, "Citizen A", string.Empty ),
				new ModelChoice( CombineModel, Combine, "Combine", string.Empty ) ),
			ImmutableArray.Create(
				new FactionChoice( Citizen, "Citizen", string.Empty ),
				new FactionChoice( Combine, "Combine", string.Empty ) ),
			ImmutableArray.Create(
				new ClassChoice( CitizenClass, Citizen, "Citizen class", string.Empty ),
				new ClassChoice( CombineClass, Combine, "Combine class", string.Empty ) ),
			ImmutableArray.Create(
				Field( "kept", SnapshotValueKind.String ),
				Field( "changed", SnapshotValueKind.String ),
				ChoiceField( "origin", "old", "legacy" ) ),
			48,
			512 );
		draft.Reconcile( initial );
		draft.SelectFaction( Combine );
		draft.SetFieldText( "kept", "preserve me" );
		draft.SetFieldText( "changed", "incompatible text" );
		draft.SetChoice( "origin", "legacy" );

		var changed = new CharacterCreationViewModel(
			ImmutableArray.Create(
				new ModelChoice( CitizenModelA, Citizen, "Citizen A", string.Empty, false ),
				new ModelChoice( CitizenModelB, Citizen, "Citizen B", string.Empty ),
				new ModelChoice( CombineModel, Combine, "Combine", string.Empty, false ) ),
			ImmutableArray.Create(
				new FactionChoice( Citizen, "Citizen", string.Empty ),
				new FactionChoice( Combine, "Combine", string.Empty, false ) ),
			ImmutableArray.Create(
				new ClassChoice( CitizenClass, Citizen, "Citizen class", string.Empty ) ),
			ImmutableArray.Create(
				Field( "kept", SnapshotValueKind.String ),
				Field( "changed", SnapshotValueKind.Boolean ),
				ChoiceField( "origin", "new" ) ),
			48,
			512 );

		draft.Reconcile( changed );

		Assert.AreEqual( Citizen, draft.SelectedFaction );
		Assert.AreEqual( CitizenModelB, draft.SelectedModel );
		Assert.AreEqual( CitizenClass, draft.SelectedClass );
		Assert.AreEqual( "preserve me", draft.GetFieldText( "kept" ) );
		Assert.AreEqual( string.Empty, draft.GetFieldText( "changed" ) );
		Assert.IsFalse( draft.GetBoolean( "changed" ) );
		Assert.AreEqual( "new", draft.GetChoice( "origin" ) );
	}

	[TestMethod]
	public void CurrentLimitsAndInvariantIntegerParsingFailClosedAndBuildTheSamePayload()
	{
		var draft = new CharacterCreationDraftState();
		draft.Reconcile( StandardCreation() );
		draft.SetFieldText( "age", "not-an-int64" );
		draft.SetFieldText( "pronouns", "they/them" );

		Assert.IsFalse( draft.CanSubmit( "Alex", "Short description" ) );

		draft.SetFieldText( "age", " 30 " );
		Assert.IsTrue( draft.CanSubmit( "Alex", "Short description" ) );
		Assert.AreEqual( 30L, draft.BuildFields()["age"].IntegerValue );

		var tightened = StandardCreation() with { NameLimit = 4, DescriptionLimit = 5 };
		draft.Reconcile( tightened );
		Assert.IsFalse( draft.CanSubmit( " Alex ", "Short description" ) );
		Assert.IsTrue( draft.CanSubmit( " Alex ", "  brief  " ) );
	}

	[TestMethod]
	public void StaleWrongKindAndInvalidOptionMutationsCannotReintroduceRemovedState()
	{
		var draft = new CharacterCreationDraftState();
		draft.Reconcile( new CharacterCreationViewModel(
			ImmutableArray.Create( new ModelChoice( CitizenModelA, Citizen, "Citizen", string.Empty ) ),
			ImmutableArray.Create( new FactionChoice( Citizen, "Citizen", string.Empty ) ),
			ImmutableArray<ClassChoice>.Empty,
			ImmutableArray.Create(
				Field( "removed", SnapshotValueKind.String ),
				Field( "flag", SnapshotValueKind.Boolean ),
				ChoiceField( "origin", "city_17" ) ),
			48,
			512 ) );
		Assert.IsTrue( draft.SetFieldText( "removed", "old callback" ) );

		draft.Reconcile( new CharacterCreationViewModel(
			ImmutableArray.Create( new ModelChoice( CitizenModelA, Citizen, "Citizen", string.Empty ) ),
			ImmutableArray.Create( new FactionChoice( Citizen, "Citizen", string.Empty ) ),
			ImmutableArray<ClassChoice>.Empty,
			ImmutableArray.Create(
				Field( "flag", SnapshotValueKind.Boolean ),
				ChoiceField( "origin", "relocated" ) ),
			48,
			512 ) );

		Assert.IsFalse( draft.SetFieldText( "removed", "late value" ) );
		Assert.IsFalse( draft.SetFieldText( "flag", "wrong kind" ) );
		Assert.IsFalse( draft.ToggleBoolean( "origin" ) );
		Assert.IsFalse( draft.SetChoice( "origin", "city_17" ) );
		Assert.AreEqual( string.Empty, draft.GetFieldText( "removed" ) );
		Assert.AreEqual( "relocated", draft.GetChoice( "origin" ) );
		Assert.IsFalse( draft.FieldValues.ContainsKey( "removed" ) );
	}

	private static CharacterCreationViewModel StandardCreation() => new(
		ImmutableArray.Create(
			new ModelChoice( CitizenModelA, Citizen, "Citizen A", string.Empty ),
			new ModelChoice( CitizenModelB, Citizen, "Citizen B", string.Empty ) ),
		ImmutableArray.Create( new FactionChoice( Citizen, "Citizen", string.Empty ) ),
		ImmutableArray.Create( new ClassChoice( CitizenClass, Citizen, "Citizen class", string.Empty ) ),
		ImmutableArray.Create(
			Field( "age", SnapshotValueKind.Integer ),
			Field( "pronouns", SnapshotValueKind.String ),
			ChoiceField( "origin", "city_17", "relocated" ) ),
		48,
		512 );

	private static CreationFieldViewModel Field( string id, SnapshotValueKind kind ) =>
		new( id, id, string.Empty, kind, true, ImmutableArray<CreationFieldOption>.Empty );

	private static CreationFieldViewModel ChoiceField( string id, params string[] options ) =>
		new( id, id, string.Empty, SnapshotValueKind.Choice, true,
			options.Select( option => new CreationFieldOption( option, option, string.Empty ) ).ToImmutableArray() );
}
