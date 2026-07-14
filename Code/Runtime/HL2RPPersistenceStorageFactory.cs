#nullable enable

using Hexagon.V2.Persistence;
using Sandbox;

namespace HL2RP.V2.Runtime;

internal static class HL2RPPersistenceStorageFactory
{
	public static IPersistenceStorage Create( BaseFileSystem fileSystem )
	{
#if SERVER
		return new HL2RPPhysicalPersistenceStorage( fileSystem );
#else
		return new HL2RPSandboxPersistenceStorage( fileSystem );
#endif
	}
}
