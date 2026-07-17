#nullable enable

using System.IO;
using System.Text.Json;

namespace HL2RP.V2.Tests.Architecture;

[TestClass]
public sealed class AccountEntitlementIntegrationTests
{
	[TestMethod]
	public void SceneOwnsOneExplicitNonPersistedBootstrapOperatorSource()
	{
		var root = FindRoot();
		var scenePath = Path.Combine( root, "Assets", "scenes", "main.scene" );
		using var scene = JsonDocument.Parse( File.ReadAllText( scenePath ) );
		var components = scene.RootElement.GetProperty( "GameObjects" )
			.EnumerateArray()
			.SelectMany( gameObject => gameObject.GetProperty( "Components" ).EnumerateArray() )
			.Where( component => component.GetProperty( "__type" ).GetString() ==
				"HL2RP.V2.Runtime.HL2RPBootstrapOperatorsComponent" )
			.ToArray();
		Assert.HasCount( 1, components );
		Assert.IsTrue( components[0].TryGetProperty( "AccountIds", out _ ) );
	}

	[TestMethod]
	[TestCategory( "WiringLint" )]
	public void HostUsesAuthenticatedRpcAccountBeforeCharacterRequirementAndPublishesReceiverAvailability()
	{
		var root = FindRoot();
		var host = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "Runtime", "HL2RPHostApplication.cs" ) );
		// The entitlement-before-character-requirement ordering is proven behaviorally by
		// HL2RPCommandSinkTests against the neutral schema sink; this test retains only
		// the wiring markers below.
		foreach ( var marker in new[]
		{
			"new HL2RPEntitlementAdministrator( rpc.AccountId, characterId )",
			"_bootstrapOperators.Contains( administrator.AccountId )",
			"BuildCreationAvailabilityView( pair.Key, pair.Value.AccountId",
			"Expected exactly one HL2RP bootstrap-operator source",
			"_entitlements.ValidateAll()"
		} ) StringAssert.Contains( host, marker );
	}

	[TestMethod]
	[TestCategory( "WiringLint" )]
	public void ShippedUiExposesRevisionAwareGrantAndRevokeWithoutClientAuthority()
	{
		var root = FindRoot();
		var panel = File.ReadAllText( Path.Combine(
			root, "Code", "UI", "EntitlementAdministrationPanel.razor" ) );
		foreach ( var marker in new[]
		{
			"HL2RPIds.Commands.EntitlementQuery",
			"HL2RPIds.Commands.EntitlementGrant",
			"HL2RPIds.Commands.EntitlementRevoke",
			"ViewModel.QueriedRevision",
			"civil_protection",
			"overwatch",
			"city_administration"
		} ) StringAssert.Contains( panel, marker );
		Assert.IsFalse( panel.Contains( "AccountId( ", StringComparison.Ordinal ) );
		var rootPanel = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "UI", "HL2RPShowcaseRoot.razor" ) );
		StringAssert.Contains( rootPanel, "ShowcaseWorkspace.Entitlements" );
		var characterMenu = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "UI", "CharacterMenuPanel.razor" ) );
		StringAssert.Contains( characterMenu, "EntitlementAdministrationPanel" );
	}

	private static string FindRoot()
	{
		var directory = new DirectoryInfo( AppContext.BaseDirectory );
		while ( directory is not null && !File.Exists( Path.Combine( directory.FullName, "hl2rp.sbproj" ) ) )
			directory = directory.Parent;
		Assert.IsNotNull( directory );
		return directory.FullName;
	}
}
