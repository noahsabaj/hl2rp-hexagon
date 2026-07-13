#nullable enable

using System.IO;
using System.Text.Json;

namespace HL2RP.V2.Tests.Architecture;

[TestClass]
public sealed class ShowcaseArchitectureTests
{
	private static readonly string[] RequiredSceneTypes =
	[
		"Hexagon.V2.Runtime.HexagonBootstrapComponent",
		"Sandbox.SpawnPoint",
		"HL2RP.V2.World.HL2RPDoorComponent",
		"HL2RP.V2.World.HL2RPStorageComponent",
		"HL2RP.V2.World.HL2RPVendorComponent",
		"HL2RP.V2.World.HL2RPRationDispenserComponent",
		"HL2RP.V2.World.HL2RPVendingMachineComponent",
		"HL2RP.V2.World.HL2RPForcefieldComponent",
		"HL2RP.V2.World.HL2RPScannerDockComponent",
		"HL2RP.V2.World.HL2RPScannerDroneComponent",
		"HL2RP.V2.World.HL2RPCombatTargetComponent",
		"HL2RP.V2.World.HL2RPWorldItemAreaComponent"
	];

	[TestMethod]
	public void MainSceneOwnsTheCompleteStableIdentityShowcase()
	{
		var scenePath = Path.Combine( FindRoot(), "Assets", "scenes", "main.scene" );
		using var scene = JsonDocument.Parse( File.ReadAllText( scenePath ) );
		var types = new List<string>();
		var persistentIds = new List<Guid>();
		Collect( scene.RootElement, types, persistentIds );

		foreach ( var required in RequiredSceneTypes )
			Assert.HasCount( 1, types.Where( value => value == required ),
				$"Scene must contain exactly one {required}." );
		Assert.HasCount( 10, types.Where( value => value == "Hexagon.V2.Runtime.PersistentSceneEntity" ) );
		Assert.HasCount( 10, persistentIds );
		Assert.AreEqual( persistentIds.Count, persistentIds.Distinct().Count(),
			"Persistent showcase identities must be unique." );
		Assert.IsFalse( types.Any( value => value.StartsWith( "Hexagon.", StringComparison.Ordinal ) &&
			!value.StartsWith( "Hexagon.V2.", StringComparison.Ordinal ) ),
			"The scene retains a legacy Hexagon component." );
		var sceneText = File.ReadAllText( scenePath );
		StringAssert.Contains( sceneText, "\"DronePersistentId\": \"08000000-0000-4000-8000-000000000001\"" );
		StringAssert.Contains( sceneText, "\"CooldownSeconds\": 30" );
		StringAssert.Contains( sceneText, "\"CooldownSeconds\": 2" );
	}

	[TestMethod]
	public void ForcefieldCollisionRulesProvideACombineOnlyPhysicalBypass()
	{
		var collision = File.ReadAllText( Path.Combine( FindRoot(), "ProjectSettings", "Collision.config" ) );
		StringAssert.Contains( collision, "\"a\": \"forcefield\"" );
		StringAssert.Contains( collision, "\"b\": \"combine\"" );
		StringAssert.Contains( collision, "\"r\": \"Ignore\"" );
		var world = File.ReadAllText( Path.Combine( FindRoot(), "Code", "World", "ShowcaseComponents.cs" ) );
		StringAssert.Contains( world, "ForcefieldEntityRules.IsVisible( state )" );
		StringAssert.Contains( world, "ForcefieldEntityRules.IsSolid( state )" );
	}

	[TestMethod]
	public void UiReadsSnapshotsAndCommandsOnly()
	{
		var uiRoot = Path.Combine( FindRoot(), "Code", "UI" );
		var forbidden = new[]
		{
			"CharacterRecord",
			"InventoryRecord",
			"ItemRecord",
			"WorldItemRecord",
			"TypedPayload",
			"DomainRepositories",
			"IPersistenceProvider",
			"Rpc.Caller",
			"FileSystem.Data",
			"DatabaseManager",
			"CharacterManager",
			"InventoryManager"
		};
		var violations = Directory.GetFiles( uiRoot, "*", SearchOption.AllDirectories )
			.Where( path => Path.GetExtension( path ) is ".cs" or ".razor" )
			.SelectMany( path => File.ReadLines( path )
				.Select( (line, index) => (Path: path, Line: line, Number: index + 1) ) )
			.Where( value => forbidden.Any( token => value.Line.Contains( token, StringComparison.Ordinal ) ) )
			.Select( value => $"{Path.GetRelativePath( FindRoot(), value.Path )}:{value.Number}" )
			.ToArray();

		Assert.IsEmpty( violations,
			$"UI crossed the snapshot/command boundary: {string.Join( ", ", violations )}" );
	}

	[TestMethod]
	public void AssetsAreNamespacedExceptForTheOwnedStartupScene()
	{
		var assetsRoot = Path.Combine( FindRoot(), "Assets" );
		var violations = Directory.GetFiles( assetsRoot, "*", SearchOption.AllDirectories )
			.Select( path => Path.GetRelativePath( assetsRoot, path ).Replace( '\\', '/' ) )
			.Where( path => path is not "scenes/main.scene" and not "scenes/main.scene_c" )
			.Where( path => !path.StartsWith( "hl2rp/", StringComparison.OrdinalIgnoreCase ) )
			.ToArray();

		Assert.IsEmpty( violations,
			$"Game assets outside the hl2rp namespace: {string.Join( ", ", violations )}" );
	}

	[TestMethod]
	public void LegacyRuntimeAndDesignPlansAreRetired()
	{
		var root = FindRoot();
		foreach ( var relative in new[]
		{
			"Code/HL2RPCharacter.cs",
			"Code/HL2RPPlugin.cs",
			"Code/HL2RPHooks.cs",
			"Code/HL2RPCommands.cs",
			"Code/Systems/CIDSystem.cs",
			"Code/Systems/ScannerPilotSystem.cs",
			"docs/plans/2026-02-13-hl2rp-full-design.md",
			"docs/plans/2026-02-13-hl2rp-implementation-plan.md"
		})
			Assert.IsFalse( File.Exists( Path.Combine( root, relative.Replace( '/', Path.DirectorySeparatorChar ) ) ),
				$"Obsolete HL2RP surface remains: {relative}" );

		var scene = File.ReadAllText( Path.Combine( root, "Assets", "scenes", "main.scene" ) );
		Assert.IsFalse( scene.Contains( "Hexagon.Core", StringComparison.Ordinal ) );
	}

	[TestMethod]
	public void PressableWorldAdaptersSubmitTargetsWithoutMutatingAuthorityState()
	{
		var path = Path.Combine( FindRoot(), "Code", "World", "ShowcaseComponents.cs" );
		var source = File.ReadAllText( path );
		StringAssert.Contains( source, "Component.IPressable" );
		StringAssert.Contains( source, "BeginInteractionAsync" );
		foreach ( var forbidden in new[]
		{
			"DomainRepositories",
			"BeginUnitOfWork",
			"[Rpc.Host]",
			"FileSystem.Data",
			"Rpc.Caller"
		})
			Assert.IsFalse( source.Contains( forbidden, StringComparison.Ordinal ),
				$"Presentation adapter references authoritative surface: {forbidden}" );
	}

	private static void Collect( JsonElement element, ICollection<string> types, ICollection<Guid> persistentIds )
	{
		if ( element.ValueKind == JsonValueKind.Object )
		{
			foreach ( var property in element.EnumerateObject() )
			{
				if ( property.NameEquals( "__type" ) && property.Value.ValueKind == JsonValueKind.String )
					types.Add( property.Value.GetString()! );
				if ( property.NameEquals( "PersistentId" ) && property.Value.ValueKind == JsonValueKind.String )
				{
					Assert.IsTrue( Guid.TryParse( property.Value.GetString(), out var id ) && id != Guid.Empty,
						"Persistent scene identity is blank or malformed." );
					persistentIds.Add( id );
				}
				Collect( property.Value, types, persistentIds );
			}
		}
		else if ( element.ValueKind == JsonValueKind.Array )
		{
			foreach ( var child in element.EnumerateArray() ) Collect( child, types, persistentIds );
		}
	}

	private static string FindRoot()
	{
		var directory = new DirectoryInfo( AppContext.BaseDirectory );
		while ( directory is not null && !File.Exists( Path.Combine( directory.FullName, "hl2rp.sbproj" ) ) )
			directory = directory.Parent;
		Assert.IsNotNull( directory, "Could not locate the HL2RP repository root." );
		return directory.FullName;
	}
}
