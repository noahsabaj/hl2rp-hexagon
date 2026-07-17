#nullable enable

using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

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
		var world = HL2RPTestSource.WithoutComments( Path.Combine( FindRoot(), "Code", "World", "ShowcaseComponents.cs" ) );
		StringAssert.Contains( world, "ForcefieldEntityRules.IsVisible( state )" );
		StringAssert.Contains( world, "ForcefieldEntityRules.IsSolid( state )" );
	}

	[TestMethod]
	[TestCategory( "WiringLint" )]
	public void PlatformChatAndClientObjectMutationAreFailClosed()
	{
		var root = FindRoot();
		var platform = File.ReadAllText( Path.Combine( root, "ProjectSettings", "Platform.config" ) );
		StringAssert.Contains( platform, "\"ChatEnabled\": false" );
		StringAssert.Contains( platform, "\"ChatShowUI\": false" );
		var networking = File.ReadAllText( Path.Combine( root, "ProjectSettings", "Networking.config" ) );
		foreach ( var permission in new[]
		{
			"\"ClientsCanSpawnObjects\": false",
			"\"ClientsCanRefreshObjects\": false",
			"\"ClientsCanDestroyObjects\": false",
			// Host migration would hand authority to a machine with no host application,
			// no domain services, and no persistence lease; both flags must stay closed.
			"\"DestroyLobbyWhenHostLeaves\": true",
			"\"AutoSwitchToBestHost\": false"
		} ) StringAssert.Contains( networking, permission );
		StringAssert.Contains( networking, "\"UpdateRate\": 30" );
		var source = HL2RPTestSource.WithoutComments(
			Path.Combine( root, "Code", "Runtime", "HL2RPSchemaSourceSystem.cs" ) );
		StringAssert.Contains( source, "Component, IChatEvent" );
		Assert.IsTrue( Regex.IsMatch( source,
			@"void\s+IChatEvent\.OnChatMessage\(\s*ChatMessageEvent\s+message\s*\)\s*=>\s*message\.Suppress\s*=\s*true\s*;" ),
			"The platform chat suppressor must remain an unconditional expression-bodied Suppress assignment." );
	}

	[TestMethod]
	[TestCategory( "WiringLint" )]
	public void SpatialAuthorityUsesOnlyTheHostSimulatedBody()
	{
		var root = FindRoot();
		var authorityFiles = new[]
		{
			Path.Combine( root, "Code", "Runtime", "HL2RPHostApplication.cs" ),
			Path.Combine( root, "Code", "Runtime", "HL2RPSandboxBoundaries.cs" ),
			Path.Combine( root, "Code", "Runtime", "HL2RPSceneRuntime.cs" )
		};
		var source = string.Join( "\n", authorityFiles.Select( HL2RPTestSource.WithoutComments ) );
		StringAssert.Contains( source, "AuthoritativeBody" );
		Assert.IsFalse( source.Contains( "PlayableBody", StringComparison.Ordinal ) );
		Assert.IsFalse( source.Contains( "Player.GameObject", StringComparison.Ordinal ) );
		StringAssert.Contains( source, "controller.EyeAngles.ToRotation().Forward" );
		StringAssert.Contains( source, "ConfigureAuthoritativePlayerBody" );
		StringAssert.Contains( source, "controller.BodyCollisionTags = new TagSet()" );
		StringAssert.Contains( source, "TryGetUsableAuthoritativeBody" );
		StringAssert.Contains( source, "WithoutTags( \"prediction\" )" );
		StringAssert.Contains( source, "HL2RPObjectHierarchy.Contains" );
		StringAssert.Contains( source, "HL2RPCharacterLifecycleGate" );
		StringAssert.Contains( source, "_access.OpenConnection( connectionId )" );
		StringAssert.Contains( source, "HL2RPSceneFeatureAdmission.Evaluate" );
		var showcase = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "World", "ShowcaseComponents.cs" ) );
		var worldItems = HL2RPTestSource.WithoutComments( Path.Combine( root, "Code", "Runtime", "HL2RPWorldItemPressable.cs" ) );
		StringAssert.Contains( showcase, "HexPlayerBody.IsLocalPredictionSource( e.Source )" );
		StringAssert.Contains( worldItems, "HexPlayerBody.IsLocalPredictionSource( e.Source )" );
	}

	[TestMethod]
	[TestCategory( "WiringLint" )]
	public void HostLifecycleAndWorldEffectsAreOwnedAndDrained()
	{
		var source = HL2RPTestSource.WithoutComments(
			Path.Combine( FindRoot(), "Code", "Runtime", "HL2RPHostApplication.cs" ) );
		StringAssert.Contains( source, "IHexHostApplication, IWorldItemReconciliationBoundary" );
		StringAssert.Contains( source, "AsyncOperationRegistry _lifecycleOperations" );
		StringAssert.Contains( source, "_worldReconciler.ReconcileStartupAsync" );
		StringAssert.Contains( source, "_worldReconciler.ReconcileCommittedAsync" );
		StringAssert.Contains( source, "ErrorCode.ReconciliationPending" );
		Assert.IsFalse( source.Contains( "RestoreWorldItem", StringComparison.Ordinal ) );
		Assert.IsFalse( source.Contains( "_pendingLifecycle", StringComparison.Ordinal ) );

		// The ordering scan is bounded to the DisposeAsync body so a later member cannot
		// satisfy it by file layout, and the recovery marker is the snapshot assignment
		// that genuinely executes inside the body.
		var dispose = source.IndexOf( "public async ValueTask DisposeAsync()", StringComparison.Ordinal );
		Assert.IsGreaterThanOrEqualTo( 0, dispose );
		var disposeEnd = source.IndexOf( "public OperationResult CompleteQuiescedShutdown(", dispose, StringComparison.Ordinal );
		Assert.IsGreaterThanOrEqualTo( 0, disposeEnd, "CompleteQuiescedShutdown must remain the member bounding DisposeAsync." );
		var body = source[dispose..disposeEnd];
		var stopAdmission = body.IndexOf( "_lifecycleOperations.StopAdmission()", StringComparison.Ordinal );
		var lifecycleDrain = body.IndexOf( "_lifecycleOperations.DrainAsync()", StringComparison.Ordinal );
		var scannerDrain = body.IndexOf( "_scanner.DrainCleanupAsync()", StringComparison.Ordinal );
		var pistolDrain = body.IndexOf( "_pistol.ReconcileRaisedPistolsAsync()", StringComparison.Ordinal );
		var worldDrain = body.IndexOf( "_worldReconciler.DrainAsync()", StringComparison.Ordinal );
		var recoveryMarker = body.IndexOf( "_pendingShutdownSnapshot = CaptureRecoverySnapshot()", StringComparison.Ordinal );
		Assert.IsGreaterThanOrEqualTo( 0, stopAdmission );
		Assert.IsGreaterThanOrEqualTo( 0, recoveryMarker,
			"DisposeAsync must capture the recovery snapshot inside its own body." );
		Assert.IsLessThan( lifecycleDrain, stopAdmission, "Lifecycle admission must close before drain." );
		Assert.IsLessThan( scannerDrain, lifecycleDrain, "Lifecycle work must drain before scanner cleanup." );
		Assert.IsLessThan( pistolDrain, scannerDrain, "Scanner cleanup must precede pistol reconciliation." );
		Assert.IsLessThan( worldDrain, pistolDrain, "Pistol reconciliation must precede world drain." );
		Assert.IsLessThan( recoveryMarker, worldDrain, "Recovery evidence must follow world drain." );

		StringAssert.Contains( source, "if ( !gameObject.NetworkSpawn( new NetworkSpawnOptions" );
		StringAssert.Contains( source, "StartEnabled = false" );
		StringAssert.Contains( source, "OwnerTransfer = OwnerTransfer.Fixed" );
		StringAssert.Contains( source, "_worldObjects[itemId] = gameObject" );
	}

	[TestMethod]
	[TestCategory( "WiringLint" )]
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
	[TestCategory( "WiringLint" )]
	public void PressableWorldAdaptersSubmitTargetsWithoutMutatingAuthorityState()
	{
		var path = Path.Combine( FindRoot(), "Code", "World", "ShowcaseComponents.cs" );
		var source = HL2RPTestSource.WithoutComments( path );
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
