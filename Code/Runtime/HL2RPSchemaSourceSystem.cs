#nullable enable

using System;
using Hexagon.V2.Infrastructure;
using HL2RP.UI;
using HL2RP.V2.Domain;
using HL2RP.V2.Schema;
using Sandbox;

namespace HL2RP.V2.Runtime;

/// <summary>
/// The single explicit bridge between the game package and Hexagon. No reflected
/// discovery is used: schema, codecs, host composition and local UI are supplied
/// by this scene-scoped source.
/// </summary>
public sealed class HL2RPSchemaSourceSystem : GameObjectSystem<HL2RPSchemaSourceSystem>, IHexSchemaSource
{
	private HL2RPHostApplication? _host;
	private readonly GameObject _platformChatGuard;

	public HL2RPSchemaSourceSystem( Scene scene ) : base( scene )
	{
		_platformChatGuard = new GameObject( scene, true, "HL2RP Platform Chat Guard" )
		{
			NetworkMode = NetworkMode.Never
		};
		_platformChatGuard.AddComponent<HL2RPPlatformChatSuppressor>();
		Listen( Stage.FinishUpdate, 200, Tick, "HL2RP v2 host maintenance" );
	}

	void IHexSchemaSource.CollectSchemas( SchemaSourceCollector collector ) => collector.Register(
		new HexSchemaRuntimeDescriptor
		{
			Schema = new HL2RPSchema(),
			PersistenceInvariants = HL2RPPersistenceInvariants.Profile,
			PersistenceCodecs = HL2RPPersistence.Codecs,
			CreatePersistenceStorage = static () =>
				HL2RPPersistenceStorageFactory.Create( Sandbox.FileSystem.Data ),
			CreateHostApplication = context => _host = new HL2RPHostApplication( context ),
			ConfigureClient = context =>
			{
				var root = new GameObject( true, "HL2RP v2 Client UI" );
				root.AddComponent<ScreenPanel>();
				var presenter = root.AddComponent<HL2RPClientPresenter>();
				presenter.Bind( context.Store, context.Controller );
			}
		} );

	private void Tick()
	{
		_host?.RequestMaintenanceTick();
	}

	public override void Dispose()
	{
		if ( _platformChatGuard.IsValid() ) _platformChatGuard.Destroy();
		base.Dispose();
	}
}

/// <summary>Defense in depth when platform chat configuration drifts.</summary>
internal sealed class HL2RPPlatformChatSuppressor : Component, IChatEvent
{
	void IChatEvent.OnChatMessage( ChatMessageEvent message ) => message.Suppress = true;
}
