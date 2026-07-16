#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Kernel;

namespace HL2RP.V2.Runtime;

/// <summary>Fail-closed admission policy for persistent scene-backed features.</summary>
public static class HL2RPSceneFeatureAdmission
{
	public static OperationResult<bool> Evaluate( SceneIdentityResolution? resolution )
	{
		if ( resolution is { Provenance: SceneIdentityProvenance.RuntimeOrNetwork } )
			return OperationResult<bool>.Success( false );
		if ( resolution is { Enabled: true, Provenance: SceneIdentityProvenance.EditorAuthored, EffectiveId: not null } )
			return OperationResult<bool>.Success( true );
		return OperationResult<bool>.Failure(
			ErrorCode.ConfigurationInvalid,
			resolution?.FatalDiagnostic ??
			"HL2RP authored scene feature has no published persistent identity resolution." );
	}
}
