#nullable enable

using Hexagon.V2.Kernel;
using HL2RP.V2.Domain;
using Sandbox;

namespace HL2RP.V2.Runtime;

/// <summary>
/// Accounts holding out-of-character operator authority: the first entitlement grant, and the
/// administrative kill command.
/// <para>
/// This is a per-DEPLOYMENT fact, not a per-scene one, so it lives in server configuration rather
/// than in committed scene content. The previous scene component forced every operator change to be
/// an edit to a shipped asset, which meant an operator's platform account id ended up serialized
/// into a map that other people fork - and it did, by accident, which is what prompted the move.
/// </para>
/// <para>
/// Values are authenticated platform account ids, never character names or any client-authored
/// identity. Empty means nobody, which is the correct default: operator authority must be granted
/// deliberately, never inherited by whoever happens to boot the server.
/// </para>
/// </summary>
public static class HL2RPOperatorAccounts
{
	// A literal, not string.Empty: the ConVar source generator copies this initialiser into a
	// generated attribute argument, and an attribute argument must be a compile-time constant.
	// string.Empty is a static readonly field and fails the engine compile with CS0182.
	[ConVar( "hl2rp-operator-accounts", ConVarFlags.Server,
		Help = "Comma, semicolon, or whitespace separated authenticated platform account IDs granted " +
			"out-of-character operator authority. Empty grants nobody." )]
	public static string Accounts { get; set; } = "";

	/// <summary>
	/// Parses the configured accounts. A malformed value fails closed rather than degrading to an
	/// empty directory, so a typo is a refused boot instead of a server that silently has no operators.
	/// </summary>
	public static OperationResult<HL2RPBootstrapOperatorDirectory> Resolve() =>
		HL2RPBootstrapOperatorDirectory.Parse( Accounts );
}
