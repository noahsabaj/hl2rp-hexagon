#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Hexagon.V2.Domain;
using Hexagon.V2.Networking;

namespace HL2RP.UI;

/// <summary>
/// Engine-neutral state for one in-progress character registration. Projection
/// refreshes reconcile against this draft so equivalent host snapshots cannot
/// erase user input, while removed or incompatible choices fail closed.
/// </summary>
public sealed class CharacterCreationDraftState
{
	private readonly Dictionary<string, string> _fieldText = new( StringComparer.Ordinal );
	private readonly Dictionary<string, SnapshotValue> _fieldValues = new( StringComparer.Ordinal );
	private readonly Dictionary<string, SnapshotValueKind> _fieldKinds = new( StringComparer.Ordinal );
	private CharacterCreationViewModel? _creation;
	private bool _initialized;

	public DefinitionId? SelectedModel { get; private set; }
	public FactionId? SelectedFaction { get; private set; }
	public ClassId? SelectedClass { get; private set; }
	public IReadOnlyDictionary<string, SnapshotValue> FieldValues => _fieldValues;

	public void Reconcile( CharacterCreationViewModel creation )
	{
		ArgumentNullException.ThrowIfNull( creation );
		var firstReconcile = !_initialized;
		_creation = creation;

		var fields = creation.Fields.ToDictionary( field => field.Id, StringComparer.Ordinal );
		foreach ( var id in _fieldKinds.Keys.Where( id => !fields.ContainsKey( id ) ).ToArray() )
			RemoveField( id );

		foreach ( var field in creation.Fields )
		{
			if ( _fieldKinds.TryGetValue( field.Id, out var previousKind ) && previousKind != field.Kind )
				RemoveField( field.Id );
			_fieldKinds[field.Id] = field.Kind;

			switch ( field.Kind )
			{
				case SnapshotValueKind.String:
				case SnapshotValueKind.Integer:
					_fieldValues.Remove( field.Id );
					break;
				case SnapshotValueKind.Boolean:
					_fieldText.Remove( field.Id );
					if ( !_fieldValues.TryGetValue( field.Id, out var boolean ) ||
						boolean.Kind != SnapshotValueKind.Boolean )
						_fieldValues[field.Id] = SnapshotValue.Boolean( false );
					break;
				default:
					_fieldText.Remove( field.Id );
					var currentChoice = _fieldValues.TryGetValue( field.Id, out var choice ) &&
						choice.Kind == SnapshotValueKind.Choice
						? choice.StringValue
						: string.Empty;
					if ( !field.Options.Any( option => option.Value == currentChoice ) )
					{
						if ( field.Options.Length == 0 ) _fieldValues.Remove( field.Id );
						else _fieldValues[field.Id] = SnapshotValue.Choice( field.Options[0].Value );
					}
					break;
			}
		}

		var factionValid = SelectedFaction is FactionId faction &&
			creation.Factions.Any( choice => choice.Enabled && choice.Id == faction );
		if ( firstReconcile || !factionValid )
			SelectedFaction = creation.Factions.FirstOrDefault( choice => choice.Enabled )?.Id;

		ReconcileModel();
		ReconcileClass( chooseDefaultWhenNull: firstReconcile || !factionValid );
		_initialized = true;
	}

	public void Reset( CharacterCreationViewModel creation )
	{
		_fieldText.Clear();
		_fieldValues.Clear();
		_fieldKinds.Clear();
		SelectedFaction = null;
		SelectedModel = null;
		SelectedClass = null;
		_creation = null;
		_initialized = false;
		Reconcile( creation );
	}

	public bool CanSubmit( string name, string description )
	{
		if ( _creation is null ) return false;
		var normalizedName = (name ?? string.Empty).Trim();
		var normalizedDescription = (description ?? string.Empty).Trim();
		return normalizedName.Length is > 0 && normalizedName.Length <= _creation.NameLimit &&
			normalizedDescription.Length is > 0 && normalizedDescription.Length <= _creation.DescriptionLimit &&
			SelectedModel is not null &&
			SelectedFaction is not null &&
			_creation.Fields.Where( field => field.Required ).All( HasFieldValue );
	}

	public bool HasFieldValue( CreationFieldViewModel field )
	{
		ArgumentNullException.ThrowIfNull( field );
		if ( field.Kind == SnapshotValueKind.String )
			return _fieldText.TryGetValue( field.Id, out var text ) && !string.IsNullOrWhiteSpace( text );
		if ( field.Kind == SnapshotValueKind.Integer )
			return _fieldText.TryGetValue( field.Id, out var integer ) &&
				long.TryParse( integer.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _ );
		return _fieldValues.ContainsKey( field.Id );
	}

	public string GetFieldText( string id ) => _fieldText.GetValueOrDefault( id, string.Empty );
	public bool SetFieldText( string id, string value )
	{
		var field = FindField( id );
		if ( field is null || field.Kind is not (SnapshotValueKind.String or SnapshotValueKind.Integer) )
			return false;
		_fieldText[id] = value ?? string.Empty;
		return true;
	}
	public bool GetBoolean( string id ) =>
		_fieldValues.TryGetValue( id, out var value ) && value.Kind == SnapshotValueKind.Boolean && value.BooleanValue;
	public bool ToggleBoolean( string id )
	{
		if ( FindField( id )?.Kind != SnapshotValueKind.Boolean ) return false;
		_fieldValues[id] = SnapshotValue.Boolean( !GetBoolean( id ) );
		return true;
	}
	public string GetChoice( string id ) =>
		_fieldValues.TryGetValue( id, out var value ) && value.Kind == SnapshotValueKind.Choice
			? value.StringValue : string.Empty;
	public bool SetChoice( string id, string value )
	{
		var field = FindField( id );
		if ( field?.Kind != SnapshotValueKind.Choice ||
			!field.Options.Any( option => option.Value == value ) ) return false;
		_fieldValues[id] = SnapshotValue.Choice( value );
		return true;
	}

	public IReadOnlyDictionary<string, SnapshotValue> BuildFields()
	{
		var fields = new Dictionary<string, SnapshotValue>( _fieldValues, StringComparer.Ordinal );
		if ( _creation is null ) return fields;
		foreach ( var field in _creation.Fields )
		{
			if ( field.Kind == SnapshotValueKind.String )
				fields[field.Id] = SnapshotValue.String( GetFieldText( field.Id ).Trim() );
			else if ( field.Kind == SnapshotValueKind.Integer && long.TryParse(
				GetFieldText( field.Id ).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number ) )
				fields[field.Id] = SnapshotValue.Integer( number );
		}
		return fields;
	}

	public void SelectFaction( FactionId factionId )
	{
		if ( _creation is null || !_creation.Factions.Any( choice => choice.Enabled && choice.Id == factionId ) )
			return;
		SelectedFaction = factionId;
		SelectedModel = null;
		SelectedClass = null;
		ReconcileModel();
		ReconcileClass( chooseDefaultWhenNull: true );
	}

	public void SelectModel( DefinitionId? modelId )
	{
		if ( modelId is null )
		{
			SelectedModel = null;
			return;
		}
		if ( _creation is not null && SelectedFaction is FactionId faction &&
			_creation.Models.Any( choice => choice.Enabled && choice.FactionId == faction && choice.Id == modelId.Value ) )
			SelectedModel = modelId;
	}

	public void SelectClass( ClassId? classId )
	{
		if ( classId is null )
		{
			SelectedClass = null;
			return;
		}
		if ( _creation is not null && SelectedFaction is FactionId faction &&
			_creation.Classes.Any( choice => choice.Enabled && choice.FactionId == faction && choice.Id == classId.Value ) )
			SelectedClass = classId;
	}

	private void ReconcileModel()
	{
		if ( _creation is null || SelectedFaction is not FactionId faction )
		{
			SelectedModel = null;
			return;
		}
		if ( SelectedModel is DefinitionId model && _creation.Models.Any( choice =>
			choice.Enabled && choice.FactionId == faction && choice.Id == model ) ) return;
		SelectedModel = _creation.Models.FirstOrDefault( choice =>
			choice.Enabled && choice.FactionId == faction )?.Id;
	}

	private void ReconcileClass( bool chooseDefaultWhenNull )
	{
		if ( _creation is null || SelectedFaction is not FactionId faction )
		{
			SelectedClass = null;
			return;
		}
		if ( SelectedClass is ClassId characterClass && _creation.Classes.Any( choice =>
			choice.Enabled && choice.FactionId == faction && choice.Id == characterClass ) ) return;
		if ( SelectedClass is null && !chooseDefaultWhenNull ) return;
		SelectedClass = _creation.Classes.FirstOrDefault( choice =>
			choice.Enabled && choice.FactionId == faction )?.Id;
	}

	private void RemoveField( string id )
	{
		_fieldText.Remove( id );
		_fieldValues.Remove( id );
		_fieldKinds.Remove( id );
	}

	private CreationFieldViewModel? FindField( string id ) =>
		_creation?.Fields.FirstOrDefault( field => string.Equals( field.Id, id, StringComparison.Ordinal ) );
}
