
/// <summary>
/// Plays radio beep sounds when combine players speak in IC or Yell chat.
/// "On" beep plays immediately, "off" beep plays after a delay proportional
/// to message length. Attach this Component to any GameObject in the scene.
/// </summary>
public class CombineVoiceHook : Component, IHexChatEvent
{
	private readonly List<(HexPlayerComponent Player, float PlayAt)> _pendingOffBeeps = new();

	void IHexChatEvent.OnChatMessage( HexPlayerComponent sender, IChatClass chatClass, string rawMessage, string formattedMessage )
	{
		if ( sender?.Character == null )
			return;

		if ( !CombineUtils.IsCombine( sender.Character ) )
			return;

		var chatName = chatClass.Name;
		if ( chatName != "In-Character" && chatName != "Yell" )
			return;

		// Play "on" beep immediately
		PlayRadioBeep( sender, true );

		// Schedule "off" beep after a delay based on message length (0.5s–3s)
		var delay = Math.Min( 0.5f + rawMessage.Length * 0.02f, 3f );
		_pendingOffBeeps.Add( (sender, Time.Now + delay) );
	}

	protected override void OnFixedUpdate()
	{
		for ( int i = _pendingOffBeeps.Count - 1; i >= 0; i-- )
		{
			if ( Time.Now >= _pendingOffBeeps[i].PlayAt )
			{
				PlayRadioBeep( _pendingOffBeeps[i].Player, false );
				_pendingOffBeeps.RemoveAt( i );
			}
		}
	}

	private void PlayRadioBeep( HexPlayerComponent player, bool isOn )
	{
		if ( player?.Character == null )
			return;

		var isCP = CombineUtils.IsCP( player.Character );
		var configKey = (isOn, isCP) switch
		{
			(true, true) => "hl2rp.sound.radioOn.cp",
			(true, false) => "hl2rp.sound.radioOn.ow",
			(false, true) => "hl2rp.sound.radioOff.cp",
			(false, false) => "hl2rp.sound.radioOff.ow"
		};

		var soundPath = HexConfig.Get<string>( configKey, "" );
		if ( string.IsNullOrEmpty( soundPath ) )
			return;

		// TODO: Wire up actual sound playback when assets are available
		// Sound.Play( soundPath, player.WorldPosition );
	}
}
