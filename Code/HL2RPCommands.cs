
public static class HL2RPCommands
{
	private static string _serverObjectives = "";

	public static void Register()
	{
		RegisterDataCommand();
		RegisterObjectivesCommand();
		RegisterSetPriorityCommand();
	}

	private static void RegisterDataCommand()
	{
		CommandManager.Register( new HexCommand
		{
			Name = "data",
			Description = "View or edit a citizen's data notes.",
			Arguments = new[]
			{
				Arg.Player( "target" ),
				Arg.Optional( Arg.String( "text", remainder: true ) )
			},
			PermissionFunc = ( caller ) =>
			{
				return caller.Character != null && CombineUtils.IsCombine( caller.Character );
			},
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "target" );
				if ( target?.Character == null )
					return "Target has no active character.";

				var targetData = (HL2RPCharacter)target.Character.Data;

				if ( ctx.Has( "text" ) )
				{
					var text = ctx.Get<string>( "text" );
					var maxLength = HexConfig.Get<int>( "hl2rp.chardata.maxlength", 750 );
					if ( text.Length > maxLength )
						return $"Data text exceeds maximum length of {maxLength} characters.";

					targetData.CharData = text;
					target.Character.MarkDirty( "CharData" );
					return $"Updated data for {targetData.Name}.";
				}

				var charData = string.IsNullOrEmpty( targetData.CharData )
					? "Points:\nInfractions:\n"
					: targetData.CharData;

				return $"[{targetData.Name} - CID #{targetData.CIDNumber}]\n{charData}";
			}
		} );
	}

	private static void RegisterObjectivesCommand()
	{
		_serverObjectives = DatabaseManager.Load<string>( "hl2rp", "objectives" ) ?? "";

		CommandManager.Register( new HexCommand
		{
			Name = "objectives",
			Description = "View or edit server objectives.",
			Arguments = new[]
			{
				Arg.Optional( Arg.String( "text", remainder: true ) )
			},
			OnRun = ( caller, ctx ) =>
			{
				if ( ctx.Has( "text" ) )
				{
					if ( !caller.Character.HasFlag( 'a' ) && !caller.Character.HasFlag( 's' ) )
						return "You do not have permission to edit objectives.";

					var text = ctx.Get<string>( "text" );
					var maxLength = HexConfig.Get<int>( "hl2rp.chardata.maxlength", 750 );
					if ( text.Length > maxLength )
						return $"Objectives text exceeds maximum length of {maxLength} characters.";

					_serverObjectives = text;
					DatabaseManager.Save( "hl2rp", "objectives", _serverObjectives );
					return "Server objectives updated.";
				}

				if ( string.IsNullOrEmpty( _serverObjectives ) )
					return "No objectives set.";

				return $"[Server Objectives]\n{_serverObjectives}";
			}
		} );
	}

	private static void RegisterSetPriorityCommand()
	{
		CommandManager.Register( new HexCommand
		{
			Name = "setpriority",
			Description = "Set CWU priority status on a citizen's CID.",
			Arguments = new[]
			{
				Arg.Player( "target" ),
				Arg.String( "value" )
			},
			PermissionFunc = ( caller ) =>
			{
				return caller.Character != null && CombineUtils.IsCombine( caller.Character );
			},
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "target" );
				if ( target?.Character == null )
					return "Target has no active character.";

				var value = ctx.Get<string>( "value" );
				bool priority = value.ToLower() is "true" or "1" or "yes";

				if ( !CIDSystem.SetPriority( target.Character, priority ) )
					return $"{target.CharacterName} does not have a CID card.";

				var status = priority ? "granted" : "revoked";
				return $"CWU priority {status} for {target.CharacterName}.";
			}
		} );
	}
}
