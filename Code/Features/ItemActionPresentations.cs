#nullable enable

using System.Collections.ObjectModel;
using Hexagon.V2.Application;
using Hexagon.V2.Networking;
using HL2RP.V2.Domain;

namespace HL2RP.V2.Features;

/// <summary>
/// Converts host-decoded item state into bounded client-safe receipts. These
/// receipts are attached to the atomic action plan and published only after the
/// action transaction commits.
/// </summary>
public static class HL2RPItemActionPresentations
{
	public static class Fields
	{
		public const string Body = "body";
		public const string CitizenId = "citizen_id";
		public const string Edition = "edition";
		public const string ExpiresAtUnixMilliseconds = "expires_at_unix_milliseconds";
		public const string IssuedAtUnixMilliseconds = "issued_at_unix_milliseconds";
		public const string IssuedName = "issued_name";
		public const string OwnerCharacterId = "owner_character_id";
		public const string PermitKind = "permit_kind";
		public const string Priority = "priority";
		public const string Status = "status";
		public const string UpdatedAtUnixMilliseconds = "updated_at_unix_milliseconds";
	}

	public const string HandbookText =
		"Carry valid identification and present it when requested by Civil Protection.\n\n" +
		"Observe ration schedules, registered commerce rules, and authorized radio frequencies.\n\n" +
		"Report hazards and civic emergencies through an approved request device. " +
		"Follow posted city objectives and lawful directives.";

	public static ItemActionPresentationReceipt CitizenIdCard( CitizenIdCardItemState state )
	{
		ArgumentNullException.ThrowIfNull( state );
		return Receipt(
			ItemActionPresentationKind.IdentityDocument,
			"Citizen Identification",
			(Fields.CitizenId, SnapshotValue.String( state.CitizenId )),
			(Fields.IssuedName, SnapshotValue.String( state.IssuedName )),
			(Fields.IssuedAtUnixMilliseconds, SnapshotValue.Integer( state.IssuedAtUtc.ToUnixTimeMilliseconds() )),
			(Fields.Priority, SnapshotValue.Choice( state.Priority.ToString().ToLowerInvariant() )) );
	}

	public static ItemActionPresentationReceipt CivicHandbook() => Receipt(
		ItemActionPresentationKind.ReferenceDocument,
		"Civic Handbook",
		(Fields.Edition, SnapshotValue.Integer( 1 )),
		(Fields.Body, SnapshotValue.String( HandbookText )) );

	public static ItemActionPresentationReceipt Note( NoteItemState state )
	{
		ArgumentNullException.ThrowIfNull( state );
		return Receipt(
			ItemActionPresentationKind.PersonalNote,
			"Note",
			(Fields.Body, SnapshotValue.String( state.Text )),
			(Fields.OwnerCharacterId, SnapshotValue.String(
				state.OwnerCharacterId?.Value.ToString( "D" ) ?? string.Empty )),
			(Fields.UpdatedAtUnixMilliseconds, SnapshotValue.Integer( state.UpdatedAtUtc.ToUnixTimeMilliseconds() )) );
	}

	public static ItemActionPresentationReceipt BusinessPermit( BusinessPermitItemState state )
	{
		ArgumentNullException.ThrowIfNull( state );
		return Receipt(
			ItemActionPresentationKind.PermitCredential,
			"Business Permit",
			(Fields.PermitKind, SnapshotValue.Choice( state.Kind.ToString().ToLowerInvariant() )),
			(Fields.OwnerCharacterId, SnapshotValue.String( state.OwnerCharacterId.Value.ToString( "D" ) )),
			(Fields.IssuedAtUnixMilliseconds, SnapshotValue.Integer( state.IssuedAtUtc.ToUnixTimeMilliseconds() )),
			(Fields.ExpiresAtUnixMilliseconds, SnapshotValue.Integer(
				state.ExpiresAtUtc?.ToUnixTimeMilliseconds() ?? -1 )),
			(Fields.Status, SnapshotValue.Choice( state.Revoked ? "revoked" : "valid" )) );
	}

	private static ItemActionPresentationReceipt Receipt(
		ItemActionPresentationKind kind,
		string title,
		params (string Key, SnapshotValue Value)[] fields )
	{
		var values = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal );
		foreach ( var field in fields ) values.Add( field.Key, field.Value );
		return new ItemActionPresentationReceipt(
			kind,
			title,
			new ReadOnlyDictionary<string, SnapshotValue>( values ) );
	}
}
