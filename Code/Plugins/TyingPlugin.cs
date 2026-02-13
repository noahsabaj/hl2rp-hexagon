
/// <summary>
/// Zip tie restraint system. Combine players can restrain citizens.
/// Restrained players cannot move, use inventory, or interact.
/// Uses a tying action with 5-second duration.
/// </summary>
[HexPlugin( "HL2RP - Tying",
	Description = "Zip tie restraint system for HL2RP",
	Author = "Noah Sabaj",
	Version = "0.1",
	Priority = 20 )]
public class TyingPlugin : IHexPlugin
{
	/// <summary>
	/// Tracks which characters are currently restrained.
	/// Key = character ID.
	/// </summary>
	private static readonly HashSet<string> _restrained = new();

	/// <summary>
	/// Tracks active tying actions in progress.
	/// Key = tying player's character ID, Value = target character ID.
	/// </summary>
	private static readonly Dictionary<string, string> _tyingInProgress = new();

	public void OnPluginLoaded()
	{
		RegisterCommands();
		Log.Info( "[HL2RP] TyingPlugin loaded." );
	}

	public void OnPluginUnloaded()
	{
		_restrained.Clear();
		_tyingInProgress.Clear();
	}

	// --- Public API ---

	/// <summary>
	/// Attempt to restrain a target player. Called from ZipTieItem.
	/// </summary>
	public static void BeginTying( HexPlayerComponent tyer, HexPlayerComponent target )
	{
		if ( tyer?.Character == null || target?.Character == null )
			return;

		if ( !CombineUtils.IsCombine( tyer.Character ) )
			return;

		if ( IsRestrained( target.Character.Id ) )
			return;

		var tyerCharId = tyer.Character.Id;
		var targetCharId = target.Character.Id;

		if ( _tyingInProgress.ContainsKey( tyerCharId ) )
			return;

		_tyingInProgress[tyerCharId] = targetCharId;

		// In a real implementation, this would be a timed action (5 seconds)
		// For now, restrain immediately
		CompleteRestraint( tyerCharId, targetCharId );
	}

	/// <summary>
	/// Complete the restraint action.
	/// </summary>
	private static void CompleteRestraint( string tyerCharId, string targetCharId )
	{
		_tyingInProgress.Remove( tyerCharId );
		_restrained.Add( targetCharId );
		Log.Info( $"[HL2RP] Character {targetCharId} has been restrained." );
	}

	/// <summary>
	/// Release a restrained character.
	/// </summary>
	public static void Untie( string characterId )
	{
		if ( _restrained.Remove( characterId ) )
		{
			Log.Info( $"[HL2RP] Character {characterId} has been released." );
		}
	}

	/// <summary>
	/// Check if a character is restrained.
	/// </summary>
	public static bool IsRestrained( string characterId )
	{
		return _restrained.Contains( characterId );
	}

	// --- Commands ---

	private void RegisterCommands()
	{
		CommandManager.Register( new HexCommand
		{
			Name = "untie",
			Description = "Release a restrained player.",
			Arguments = new[]
			{
				Arg.Player( "target" )
			},
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "target" );
				if ( target?.Character == null )
					return "Target has no active character.";

				if ( !IsRestrained( target.Character.Id ) )
					return $"{target.CharacterName} is not restrained.";

				Untie( target.Character.Id );
				return $"Released {target.CharacterName} from restraints.";
			}
		} );
	}
}
