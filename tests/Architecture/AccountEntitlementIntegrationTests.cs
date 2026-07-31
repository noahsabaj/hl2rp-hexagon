#nullable enable

using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HL2RP.V2.Tests.Architecture;

[TestClass]
public sealed class AccountEntitlementIntegrationTests
{
	/// <summary>
	/// Operator authority is deployment configuration (the hl2rp-operator-accounts ConVar), never
	/// scene content. This guards the CLASS rather than the old component: a scene is committed and
	/// forked, so any platform account id serialized into one leaks a real person's identity. It is a
	/// real regression, not a hypothetical - a runtime edit to the previous scene component was
	/// accidentally saved and put an account id into this very file.
	/// </summary>
	[TestMethod]
	public void SceneNeverCarriesOperatorAccountIdentity()
	{
		var root = FindRoot();
		var scenePath = Path.Combine( root, "Assets", "scenes", "main.scene" );
		var raw = File.ReadAllText( scenePath );

		var steamId = new Regex( @"7656119\d{10}" );
		Assert.IsFalse( steamId.IsMatch( raw ),
			"A platform account ID is serialized into main.scene. Operator accounts belong in the " +
			"hl2rp-operator-accounts ConVar, not in committed scene content." );

		using var scene = JsonDocument.Parse( raw );
		var operatorComponents = scene.RootElement.GetProperty( "GameObjects" )
			.EnumerateArray()
			.SelectMany( gameObject => gameObject.GetProperty( "Components" ).EnumerateArray() )
			.Where( component => component.GetProperty( "__type" ).GetString()?
				.Contains( "BootstrapOperators", StringComparison.Ordinal ) == true )
			.ToArray();
		Assert.IsEmpty( operatorComponents,
			"The scene-authored operator component was replaced by deployment configuration." );
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
			"_bootstrapOperators.Contains( administrator.AccountId )",
			"BuildCreationAvailabilityView( pair.Key, pair.Value.AccountId",
			"HL2RPOperatorAccounts.Resolve()",
			"_entitlements.ValidateAll()"
		} ) StringAssert.Contains( host, marker );
		// The entitlement route body moved to the engine-neutral command execution
		// core; the administrator identity construction is pinned there.
		var execution = HL2RPTestSource.WithoutComments(
			Path.Combine( root, "Code", "Runtime", "HL2RPCommandExecution.cs" ) );
		StringAssert.Contains( execution, "new HL2RPEntitlementAdministrator( accountId, characterId )" );
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
