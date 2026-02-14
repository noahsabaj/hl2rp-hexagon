
/// <summary>
/// Plays radio beep sounds when combine players speak in IC or Yell chat.
/// Attach this Component to the same GameObject as HexagonFramework.
/// </summary>
public class CombineVoiceHook : Component, IChatMessageListener
{
	public void OnChatMessage( HexPlayerComponent sender, IChatClass chatClass, string rawMessage, string formattedMessage )
	{
		if ( sender?.Character == null )
			return;

		if ( !CombineUtils.IsCombine( sender.Character ) )
			return;

		var chatName = chatClass.Name;
		if ( chatName != "In-Character" && chatName != "Yell" )
			return;

		PlayRadioBeep( sender, true );
		PlayRadioBeep( sender, false );
	}

	private void PlayRadioBeep( HexPlayerComponent player, bool isOn )
	{
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
