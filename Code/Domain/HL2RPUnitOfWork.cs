#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Persistence;

namespace HL2RP.V2.Domain;

/// <summary>
/// Whitelist-safe unit-of-work finalization. C# await-using lowers through
/// forbidden exception-rethrow helpers, which s&amp;box intentionally rejects; every HL2RP
/// transaction therefore commits and disposes explicitly through this helper.
/// </summary>
public static class HL2RPUnitOfWork
{
	public static async ValueTask<PersistenceResult<CommitReceipt>> CommitAndDisposeAsync(
		IUnitOfWork unitOfWork,
		CancellationToken cancellationToken = default)
	{
		var committed = await unitOfWork.CommitAsync(cancellationToken);
		await unitOfWork.DisposeAsync();
		return committed;
	}

	public static ValueTask DisposeAsync(IUnitOfWork unitOfWork) => unitOfWork.DisposeAsync();
}
