#nullable enable

using HL2RP.V2.Schema;

namespace HL2RP.V2.Domain;

public sealed record DoorEntityState
{
	public required bool CombineLocked { get; init; }
	public required bool IsOpen { get; init; }
}

public sealed record StorageEntityState
{
	public required InventoryId InventoryId { get; init; }
	public required bool Locked { get; init; }
}

public sealed record VendorStockEntry
{
	public required DefinitionId Definition { get; init; }
	public required int Quantity { get; init; }
	public required long UnitPrice { get; init; }
}

public sealed record VendorEntityState
{
	public required BusinessPermitKind RequiredPermit { get; init; }
	public IReadOnlyList<VendorStockEntry> Stock { get; init; } = Array.Empty<VendorStockEntry>();
}

public sealed record MachineEntityState
{
	public required long Stock { get; init; }
	public required long UnitPrice { get; init; }
	public required double CooldownSeconds { get; init; }
	public required DateTimeOffset? CooldownUntilUtc { get; init; }
}

public sealed record ForcefieldEntityState
{
	public required bool Enabled { get; init; }
	public required bool CombineOnly { get; init; }
}

public static class ForcefieldEntityRules
{
	public static bool IsVisible( ForcefieldEntityState state ) => state.Enabled;
	public static bool IsSolid( ForcefieldEntityState state ) => state.Enabled && state.CombineOnly;
	public static bool IsPassageAuthorized( ForcefieldEntityState state, FactionId faction ) =>
		!state.Enabled || !state.CombineOnly || faction.Value is
			HL2RPIds.Factions.CivilProtection or HL2RPIds.Factions.Overwatch or
			HL2RPIds.Factions.CityAdministration;
}

public sealed record ScannerPhotoMetadata
{
	public required Guid PhotoId { get; init; }
	public required CharacterId PilotCharacterId { get; init; }
	public required DateTimeOffset CapturedAtUtc { get; init; }
	public required float PositionX { get; init; }
	public required float PositionY { get; init; }
	public required float PositionZ { get; init; }
}

public sealed record ScannerEntityState
{
	/// <summary>
	/// Stable scene link between a scanner dock and its drone. The dock points at
	/// the drone and the drone points back at its dock; only the drone may own a
	/// pilot session.
	/// </summary>
	public SceneEntityId? LinkedEntityId { get; init; }
	public CharacterId? PilotCharacterId { get; init; }
	public required bool SpotlightEnabled { get; init; }
	public required long LastAcceptedInputSequence { get; init; }
	public required DateTimeOffset? PhotoCooldownUntilUtc { get; init; }
	public IReadOnlyList<ScannerPhotoMetadata> Photos { get; init; } =
		Array.Empty<ScannerPhotoMetadata>();
}

public sealed record ScannerSceneLinkState(
	SceneEntityId EntityId,
	string Kind,
	SceneEntityId? LinkedEntityId );

public static class ScannerSceneLinkTopology
{
	public static OperationResult Validate( IEnumerable<ScannerSceneLinkState> links )
	{
		ArgumentNullException.ThrowIfNull( links );
		var materialized = links.ToArray();
		if ( materialized.Select( value => value.EntityId ).Distinct().Count() != materialized.Length )
			return OperationResult.Failure( ErrorCode.DuplicateRegistration, "Scanner scene identities are duplicated." );
		var byId = materialized.ToDictionary( value => value.EntityId );
		foreach ( var link in materialized )
		{
			if ( link.Kind is not ("scanner_dock" or "scanner_drone") )
				return OperationResult.Failure( ErrorCode.ConfigurationInvalid, "Scanner link has an unknown entity kind." );
			if ( link.LinkedEntityId is not SceneEntityId linkedId ||
				!byId.TryGetValue( linkedId, out var reciprocal ) ||
				reciprocal.LinkedEntityId != link.EntityId ||
				reciprocal.Kind == link.Kind )
				return OperationResult.Failure(
					ErrorCode.ConfigurationInvalid, $"Scanner scene link '{link.EntityId}' is missing, same-kind, or non-reciprocal." );
		}
		return OperationResult.Success();
	}
}

public sealed record CombatTargetEntityState
{
	public required long MaximumHealth { get; init; }
	public required long CurrentHealth { get; init; }
	public required DateTimeOffset? LastHitAtUtc { get; init; }
}

public sealed record CityObjectiveState
{
	public required string Id { get; init; }
	public required string Title { get; init; }
	public required string Detail { get; init; }
	public required bool Completed { get; init; }
	public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record CityObjectiveContent( string Title, string Detail );

public sealed record CityObjectiveUpdate(
	string? ObjectiveId,
	CityObjectiveContent Content,
	bool Completed );

public enum CityObjectiveChangeKind
{
	Upsert = 0,
	Delete = 1
}

/// <summary>
/// A validated command intent. Upserts carry the shared title/detail contract;
/// deletes carry only the stable server-issued identifier they remove.
/// </summary>
public sealed record CityObjectiveChange
{
	public required CityObjectiveChangeKind Kind { get; init; }
	public required string? ObjectiveId { get; init; }
	public CityObjectiveContent? Content { get; init; }
	public bool Completed { get; init; }

	public static CityObjectiveChange Upsert( CityObjectiveUpdate update )
	{
		ArgumentNullException.ThrowIfNull( update );
		return new CityObjectiveChange
		{
			Kind = CityObjectiveChangeKind.Upsert,
			ObjectiveId = update.ObjectiveId,
			Content = update.Content,
			Completed = update.Completed
		};
	}

	public static CityObjectiveChange Delete( string objectiveId ) => new()
	{
		Kind = CityObjectiveChangeKind.Delete,
		ObjectiveId = objectiveId
	};
}

public sealed record CityObjectiveChangeResult(
	string ObjectiveId,
	CityObjectiveChangeKind Kind,
	IReadOnlyList<CityObjectiveState> Objectives );

public static class CityObjectiveContract
{
	public const int MaximumObjectives = 32;
	public const int MaximumTitleLength = 96;
	public const int MaximumDetailLength = 512;

	public static OperationResult<CityObjectiveContent> CreateContent( string? title, string? detail )
	{
		var normalizedTitle = (title ?? string.Empty).Trim();
		var normalizedDetail = NormalizeLineEndings( detail ?? string.Empty ).Trim();
		if ( normalizedTitle.Length is 0 or > MaximumTitleLength ||
			normalizedTitle.Any( char.IsControl ) )
			return OperationResult<CityObjectiveContent>.Failure(
				ErrorCode.InvalidArgument,
				$"Objective title must contain 1-{MaximumTitleLength} non-control characters." );
		if ( normalizedDetail.Length > MaximumDetailLength ||
			normalizedDetail.Any( value => value != '\n' && char.IsControl( value ) ) )
			return OperationResult<CityObjectiveContent>.Failure(
				ErrorCode.InvalidArgument,
				$"Objective detail must contain at most {MaximumDetailLength} characters and only line-feed controls." );
		return OperationResult<CityObjectiveContent>.Success(
			new CityObjectiveContent( normalizedTitle, normalizedDetail ) );
	}

	public static OperationResult Validate( IReadOnlyList<CityObjectiveState> objectives )
	{
		ArgumentNullException.ThrowIfNull( objectives );
		if ( objectives.Count > MaximumObjectives )
			return OperationResult.Failure(
				ErrorCode.InvalidArgument, $"At most {MaximumObjectives} city objectives are allowed." );
		var ids = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var objective in objectives )
		{
			if ( objective is null || string.IsNullOrWhiteSpace( objective.Id ) || !ids.Add( objective.Id ) )
				return OperationResult.Failure( ErrorCode.InvalidArgument, "City objective IDs must be non-empty and unique." );
			try
			{
				StableIdentifier.Require( objective.Id, nameof(objectives) );
			}
			catch ( ArgumentException )
			{
				return OperationResult.Failure( ErrorCode.InvalidArgument, "City objective ID is invalid." );
			}
			var content = CreateContent( objective.Title, objective.Detail );
			if ( content.Failed || content.Value.Title != objective.Title || content.Value.Detail != objective.Detail )
				return OperationResult.Failure(
					ErrorCode.InvalidArgument, content.Error?.Message ?? "City objective content is not normalized." );
			if ( objective.UpdatedAtUtc == default || objective.UpdatedAtUtc.Offset != TimeSpan.Zero )
				return OperationResult.Failure( ErrorCode.InvalidArgument, "City objective timestamp must be a non-default UTC value." );
		}
		return OperationResult.Success();
	}

	/// <summary>
	/// Applies one command against the exact state read for the pending commit.
	/// A supplied identifier is update-only and therefore must already exist;
	/// only an omitted identifier allocates a new server-owned identifier.
	/// </summary>
	public static OperationResult<CityObjectiveChangeResult> Apply(
		IReadOnlyList<CityObjectiveState> current,
		CityObjectiveChange change,
		DateTimeOffset updatedAtUtc,
		Func<Guid> newId )
	{
		ArgumentNullException.ThrowIfNull( current );
		ArgumentNullException.ThrowIfNull( change );
		ArgumentNullException.ThrowIfNull( newId );
		var currentValidation = Validate( current );
		if ( currentValidation.Failed )
			return OperationResult<CityObjectiveChangeResult>.Failure(
				currentValidation.Error!.Code, currentValidation.Error.Message );
		if ( updatedAtUtc == default || updatedAtUtc.Offset != TimeSpan.Zero )
			return OperationResult<CityObjectiveChangeResult>.Failure(
				ErrorCode.InvalidArgument, "Objective timestamp must be a non-default UTC value." );

		if ( change.Kind == CityObjectiveChangeKind.Delete )
		{
			var deleteId = ValidateIdentifier( change.ObjectiveId );
			if ( deleteId.Failed )
				return OperationResult<CityObjectiveChangeResult>.Failure(
					deleteId.Error!.Code, deleteId.Error.Message );
			if ( !current.Any( value => string.Equals( value.Id, deleteId.Value, StringComparison.Ordinal ) ) )
				return OperationResult<CityObjectiveChangeResult>.Failure(
					ErrorCode.NotFound, "City objective was not found." );
			return OperationResult<CityObjectiveChangeResult>.Success( new CityObjectiveChangeResult(
				deleteId.Value,
				CityObjectiveChangeKind.Delete,
				current.Where( value => !string.Equals( value.Id, deleteId.Value, StringComparison.Ordinal ) ).ToArray() ) );
		}

		if ( change.Kind != CityObjectiveChangeKind.Upsert || change.Content is null )
			return OperationResult<CityObjectiveChangeResult>.Failure(
				ErrorCode.InvalidArgument, "City objective change is invalid." );
		var content = CreateContent( change.Content.Title, change.Content.Detail );
		if ( content.Failed || content.Value != change.Content )
			return OperationResult<CityObjectiveChangeResult>.Failure(
				ErrorCode.InvalidArgument, content.Error?.Message ?? "City objective content is not normalized." );

		string objectiveId;
		if ( change.ObjectiveId is not null )
		{
			var suppliedId = ValidateIdentifier( change.ObjectiveId );
			if ( suppliedId.Failed )
				return OperationResult<CityObjectiveChangeResult>.Failure(
					suppliedId.Error!.Code, suppliedId.Error.Message );
			objectiveId = suppliedId.Value;
			if ( !current.Any( value => string.Equals( value.Id, objectiveId, StringComparison.Ordinal ) ) )
				return OperationResult<CityObjectiveChangeResult>.Failure(
					ErrorCode.NotFound, "City objective was not found." );
		}
		else
		{
			if ( current.Count >= MaximumObjectives )
				return OperationResult<CityObjectiveChangeResult>.Failure(
					ErrorCode.InvalidArgument, $"At most {MaximumObjectives} city objectives are allowed." );
			objectiveId = string.Empty;
			for ( var attempt = 0; attempt < MaximumObjectives; attempt++ )
			{
				var candidate = $"objective.{newId():N}";
				if ( current.All( value => !string.Equals( value.Id, candidate, StringComparison.Ordinal ) ) )
				{
					objectiveId = candidate;
					break;
				}
			}
			if ( objectiveId.Length == 0 )
				return OperationResult<CityObjectiveChangeResult>.Failure(
					ErrorCode.Conflict, "A unique city objective identifier could not be allocated." );
		}

		var objectives = current
			.Where( value => !string.Equals( value.Id, objectiveId, StringComparison.Ordinal ) )
			.Append( new CityObjectiveState
			{
				Id = objectiveId,
				Title = content.Value.Title,
				Detail = content.Value.Detail,
				Completed = change.Completed,
				UpdatedAtUtc = updatedAtUtc
			} )
			.ToArray();
		var candidateValidation = Validate( objectives );
		return candidateValidation.Succeeded
			? OperationResult<CityObjectiveChangeResult>.Success( new CityObjectiveChangeResult(
				objectiveId, CityObjectiveChangeKind.Upsert, objectives ) )
			: OperationResult<CityObjectiveChangeResult>.Failure(
				candidateValidation.Error!.Code, candidateValidation.Error.Message );
	}

	public static OperationResult<string> ValidateIdentifier( string? objectiveId )
	{
		var normalized = objectiveId?.Trim();
		if ( string.IsNullOrEmpty( normalized ) || !string.Equals( normalized, objectiveId, StringComparison.Ordinal ) )
			return OperationResult<string>.Failure( ErrorCode.InvalidArgument, "Objective ID is invalid." );
		try
		{
			StableIdentifier.Require( normalized, nameof(objectiveId) );
		}
		catch ( ArgumentException )
		{
			return OperationResult<string>.Failure( ErrorCode.InvalidArgument, "Objective ID is invalid." );
		}
		return OperationResult<string>.Success( normalized );
	}

	private static string NormalizeLineEndings( string value ) =>
		value.Replace( "\r\n", "\n", StringComparison.Ordinal ).Replace( '\r', '\n' );
}

public sealed record CityEntityState
{
	public IReadOnlyList<CityObjectiveState> Objectives { get; init; } =
		Array.Empty<CityObjectiveState>();
}
