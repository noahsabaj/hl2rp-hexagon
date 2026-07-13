#nullable enable

using System.Collections.Immutable;
using Hexagon.V2.Client;
using Hexagon.V2.Domain;
using Hexagon.V2.Networking;
using HL2RP.UI;
using HL2RP.V2.Domain;
using HL2RP.V2.Runtime;
using HL2RP.V2.Schema;

namespace HL2RP.V2.Tests.UI;

[TestClass]
public sealed class EntitlementAvailabilityTests
{
	[TestMethod]
	public void HostAvailabilityEnablesOnlyGrantedRestrictedFactionClassAndModel()
	{
		var account = new AccountId( 1001 );
		var queried = new HL2RPAccountEntitlementSnapshot(
			new AccountId( 2001 ), HL2RPWhitelist.Overwatch, new Hexagon.V2.Persistence.DocumentRevision( 4 ), true );
		var view = HL2RPCreationAvailability.Build(
			7,
			new HL2RPAccountEntitlementSnapshot(
				account, HL2RPWhitelist.CivilProtection, new Hexagon.V2.Persistence.DocumentRevision( 2 ), true ),
			true,
			queried );
		var model = Project( view, account );
		var creation = model.CharacterMenu?.Creation ?? throw new AssertFailedException( "Character menu was not projected." );

		Assert.IsTrue( creation.Factions.Single( value => value.Id.Value == HL2RPIds.Factions.Citizen ).Enabled );
		Assert.IsTrue( creation.Factions.Single( value => value.Id.Value == HL2RPIds.Factions.CivilProtection ).Enabled );
		Assert.IsFalse( creation.Factions.Single( value => value.Id.Value == HL2RPIds.Factions.Overwatch ).Enabled );
		Assert.IsFalse( creation.Factions.Single( value => value.Id.Value == HL2RPIds.Factions.CityAdministration ).Enabled );
		Assert.IsTrue( creation.Classes.All( value => value.Enabled ) );
		Assert.IsTrue( creation.Models.Single( value => value.Id.Value == HL2RPIds.Models.CivilProtectionUnit ).Enabled );
		Assert.IsFalse( creation.Models.Single( value => value.Id.Value == HL2RPIds.Models.OverwatchSoldier ).Enabled );
		Assert.IsTrue( model.Entitlements?.CanManage );
		Assert.AreEqual( queried.AccountId, model.Entitlements?.QueriedAccountId );
		Assert.AreEqual( queried.Flags, model.Entitlements?.QueriedFlags );
		Assert.AreEqual( queried.Revision.Value, model.Entitlements?.QueriedRevision );
	}

	[TestMethod]
	public void MissingOrUnentitledHostViewFailsClosedForRestrictedCreation()
	{
		var account = new AccountId( 1001 );
		var noGrant = Project( HL2RPCreationAvailability.Build(
			1,
			new HL2RPAccountEntitlementSnapshot(
				account, HL2RPWhitelist.None, Hexagon.V2.Persistence.DocumentRevision.None, false ),
			false,
			null ), account );
		Assert.IsTrue( noGrant.CharacterMenu?.Creation.Factions.Single(
			value => value.Id.Value == HL2RPIds.Factions.Citizen ).Enabled );
		Assert.IsTrue( noGrant.CharacterMenu?.Creation.Factions.Where(
			value => value.Id.Value != HL2RPIds.Factions.Citizen ).All( value => !value.Enabled ) );
		Assert.IsTrue( noGrant.CharacterMenu?.Creation.Classes.All( value => !value.Enabled ) );

		var missing = Project( null, account );
		Assert.IsTrue( missing.CharacterMenu?.Creation.Factions.Where(
			value => value.Id.Value != HL2RPIds.Factions.Citizen ).All( value => !value.Enabled ) );
		Assert.IsNull( missing.Entitlements );
	}

	private static HL2RPShowcaseViewModel Project( SchemaViewSnapshot? view, AccountId account )
	{
		var connection = ConnectionId.New();
		var store = new HexClientStore();
		var state = new ClientStateSnapshot(
			new ClientStateEpoch( ConnectionEpoch.New(), 1, 1 ),
			new PlayerPublicSnapshot(
				connection, account.Value, "Entitlement Test", null,
				string.Empty, string.Empty, null, null, null, false, false ),
			null,
			new PlayerRosterSnapshot( 1, Array.Empty<PlayerRosterRowSnapshot>() ),
			view is null ? null : new[] { view },
			null,
			null );
		Assert.IsTrue( store.ApplyState( state ) );
		return new HL2RPShowcaseProjection().Build(
			store, ShowcaseWorkspace.None, false, ImmutableArray<NotificationViewModel>.Empty );
	}
}
