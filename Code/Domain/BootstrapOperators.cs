#nullable enable

using System.Collections.ObjectModel;
using System.Globalization;

namespace HL2RP.V2.Domain;

/// <summary>
/// Non-persisted, host-authored bootstrap authority. It exists only to break the
/// first-administrator loop; durable account entitlements remain in persistence.
/// </summary>
public sealed class HL2RPBootstrapOperatorDirectory
{
	private readonly IReadOnlySet<AccountId> _accounts;

	private HL2RPBootstrapOperatorDirectory( IReadOnlySet<AccountId> accounts ) => _accounts = accounts;

	public IReadOnlyCollection<AccountId> Accounts =>
		new ReadOnlyCollection<AccountId>( _accounts.OrderBy( value => value.Value ).ToArray() );

	public bool Contains( AccountId accountId ) => _accounts.Contains( accountId );

	public static OperationResult<HL2RPBootstrapOperatorDirectory> Parse( string? configuredAccountIds )
	{
		var accounts = new HashSet<AccountId>();
		foreach ( var token in (configuredAccountIds ?? string.Empty).Split(
			new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries ) )
		{
			if ( !ulong.TryParse( token, NumberStyles.None, CultureInfo.InvariantCulture, out var value ) || value == 0 )
				return OperationResult<HL2RPBootstrapOperatorDirectory>.Failure(
					ErrorCode.ConfigurationInvalid, $"Bootstrap operator account '{token}' is not a non-zero unsigned account ID." );
			accounts.Add( new AccountId( value ) );
		}
		return OperationResult<HL2RPBootstrapOperatorDirectory>.Success(
			new HL2RPBootstrapOperatorDirectory( accounts ) );
	}
}
