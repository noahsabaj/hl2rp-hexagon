#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using HL2RP.V2.Runtime;

namespace HL2RP.V2.Tests.Runtime;

[TestClass]
public sealed class SceneFeatureAdmissionTests
{
	[TestMethod]
	public void InjectedRuntimeFeatureIsIgnoredWithoutPoisoningStartup()
	{
		var result = HL2RPSceneFeatureAdmission.Evaluate( new SceneIdentityResolution(
			"runtime",
			SceneEntityId.New(),
			null,
			SceneIdentityProvenance.RuntimeOrNetwork,
			false,
			false,
			"runtime identity disabled" ) );

		Assert.IsTrue( result.Succeeded );
		Assert.IsFalse( result.Value );
	}

	[TestMethod]
	public void InvalidAuthoredFeatureStillFailsClosed()
	{
		var result = HL2RPSceneFeatureAdmission.Evaluate( new SceneIdentityResolution(
			"authored",
			null,
			null,
			SceneIdentityProvenance.EditorAuthored,
			false,
			false,
			"missing authored identity" ) );

		Assert.IsTrue( result.Failed );
	}
}
