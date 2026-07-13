#nullable enable

using System.IO;

namespace HL2RP.V2.Tests.Runtime;

[TestClass]
public sealed class HeadlessVerificationSourceTests
{
	[TestMethod]
	public void HeadlessProbeUsesAnIsolatedActorButTheProductionInteractionAuthority()
	{
		var root = FindRoot();
		var host = File.ReadAllText( Path.Combine( root, "Code", "Runtime", "HL2RPHostApplication.cs" ) );
		foreach ( var marker in new[]
		{
			"VerificationActorBinding", "ResolveInteractionActorState",
			"LoadVerificationProbeCharacter", "_interactions!.Begin(",
			"PurchaseFromMachineAsync", "VerificationConnectionId", "VerificationAccountId"
		} ) StringAssert.Contains( host, marker );
		StringAssert.Contains( host, "if ( _clients.Count == 0 && _verificationActor is null ) return;" );

		var world = File.ReadAllText( Path.Combine( root, "Code", "Runtime", "HL2RPSceneRuntime.cs" ) );
		StringAssert.Contains( world, "HL2RPInteractionActorState" );
		StringAssert.Contains( world, "ActorPosition = new WorldPoint" );
		StringAssert.Contains( world, "HasLineOfSight" );
	}

	[TestMethod]
	public void SmokeWaitsForHexagonTypesBeforeApplyingRuntimeOverridesAndStartingPlay()
	{
		var root = FindRoot();
		var script = File.ReadAllText( Path.GetFullPath(
			Path.Combine( root, "..", "hexagon", "tools", "smoke-sbox.ps1" ) ) );
		var hotload = script.IndexOf( "name = 'get_component_type'", StringComparison.Ordinal );
		var console = script.IndexOf( "name = 'console_command'", hotload, StringComparison.Ordinal );
		var play = script.IndexOf( "name = 'play_start'", console, StringComparison.Ordinal );
		Assert.IsGreaterThanOrEqualTo( 0, hotload );
		Assert.IsGreaterThan( hotload, console );
		Assert.IsGreaterThan( console, play );
		StringAssert.Contains( script, "$typePayload -match 'HexagonBootstrapComponent'" );
		StringAssert.Contains( script, "$typePayload -match 'Hexagon\\.V2\\.Runtime'" );
		StringAssert.Contains( script, "$consolePayload -match '(?i)Unknown Command'" );
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
