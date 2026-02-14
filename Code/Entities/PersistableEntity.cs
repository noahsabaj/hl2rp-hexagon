
/// <summary>
/// Base class for world entities with persistent state.
/// Handles PersistenceId generation, save/load lifecycle, and proxy checks.
/// </summary>
public abstract class PersistableEntity<TSaveData> : Component where TSaveData : class, new()
{
	[Property] public string PersistenceId { get; set; } = "";

	/// <summary>
	/// The DatabaseManager collection name for this entity type.
	/// </summary>
	protected abstract string CollectionName { get; }

	protected override void OnEnabled()
	{
		if ( IsProxy ) return;

		if ( string.IsNullOrEmpty( PersistenceId ) )
			PersistenceId = DatabaseManager.NewId();

		var saved = DatabaseManager.Load<TSaveData>( CollectionName, PersistenceId );
		if ( saved != null )
			OnLoadState( saved );

		OnStateLoaded();
	}

	protected override void OnDisabled()
	{
		if ( IsProxy ) return;
		SaveState();
	}

	protected void SaveState()
	{
		DatabaseManager.Save( CollectionName, PersistenceId, CreateSaveData() );
	}

	/// <summary>
	/// Create the save data object for persistence.
	/// </summary>
	protected abstract TSaveData CreateSaveData();

	/// <summary>
	/// Apply loaded state from persistence.
	/// </summary>
	protected abstract void OnLoadState( TSaveData data );

	/// <summary>
	/// Called after state is loaded. Override for post-load initialization.
	/// </summary>
	protected virtual void OnStateLoaded() { }
}
