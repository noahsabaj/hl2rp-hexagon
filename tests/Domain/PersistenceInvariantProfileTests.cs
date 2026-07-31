#nullable enable

using HL2RP.V2.Domain;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.Domain;

[TestClass]
public sealed class PersistenceInvariantProfileTests
{
	[TestMethod]
	public void ProfileCoversEveryCuratedItemReferenceAndSceneKindExactly()
	{
		var schema = HL2RP.V2.Tests.Schema.HL2RPSchemaTests.Compile();
		var profile = HL2RPPersistenceInvariants.Profile;

		CollectionAssert.AreEquivalent(
			schema.Items.All.Select( item => item.Id ).ToArray(),
			profile.Items.Keys.Select( definition => definition.Value ).ToArray() );
		Assert.AreEqual( HL2RPIds.PersistedTypes.CharacterState, profile.CharacterState.Value );
		CollectionAssert.AreEquivalent(
			new[] { "recognition", "restraint", "door_ownership" },
			profile.CharacterReferences.Keys.ToArray() );
		CollectionAssert.AreEquivalent(
			new[]
			{
				"door", "storage", "vendor", "ration_dispenser", "vending_machine",
				"forcefield", "scanner_dock", "scanner_drone", "combat_target", "city"
			},
			profile.SceneEntities.Keys.ToArray() );
		Assert.IsTrue( profile.CharacterReferences.Values.All( contract =>
			!contract.AllowMissingCharacter &&
			!contract.AllowMissingRelatedCharacter &&
			!contract.AllowMissingSceneEntity ) );
	}

	[TestMethod]
	public void StatefulItemsRequireOnlyTheirCanonicalTypedTrait()
	{
		var profile = HL2RPPersistenceInvariants.Profile;
		AssertTrait( profile, HL2RPIds.Items.CitizenIdCard, "cid", HL2RPIds.PersistedTypes.CitizenIdCard );
		AssertTrait( profile, HL2RPIds.Items.Radio, "radio", HL2RPIds.PersistedTypes.Radio );
		AssertTrait( profile, HL2RPIds.Items.Pistol, "pistol", HL2RPIds.PersistedTypes.Pistol );
		AssertTrait( profile, HL2RPIds.Items.TokenStack, "tokens", HL2RPIds.PersistedTypes.TokenStack );
		Assert.IsEmpty( profile.Items[new DefinitionId( HL2RPIds.Items.Ration )].Traits );
		Assert.IsEmpty( profile.Items[new DefinitionId( HL2RPIds.Items.Suitcase )].Traits );
	}

	private static void AssertTrait(
		SchemaPersistenceInvariantProfile profile,
		string item,
		string trait,
		string persistedType )
	{
		var contract = profile.Items[new DefinitionId( item )];
		Assert.HasCount( 1, contract.Traits );
		Assert.AreEqual( persistedType, contract.Traits[trait].Value );
	}
}
