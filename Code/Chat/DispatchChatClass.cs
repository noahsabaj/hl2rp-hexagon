
public class DispatchChatClass : IChatClass
{
	public string Name => "Dispatch";
	public string Prefix => "/dispatch";
	public float Range => 0f;
	public Color Color => HexConfig.Get<Color>( "hl2rp.chat.dispatchColor", new Color( 0.75f, 0.22f, 0.17f ) );

	public bool CanSay( HexPlayerComponent speaker, string message )
	{
		if ( !speaker.HasActiveCharacter || speaker.Character == null )
			return false;

		return CombineUtils.IsDispatch( speaker.Character );
	}

	public bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener )
	{
		if ( !listener.HasActiveCharacter || listener.Character == null )
			return false;

		return CombineUtils.IsCombine( listener.Character );
	}

	public string Format( HexPlayerComponent speaker, string message )
	{
		return $"<:: {speaker.CharacterName} dispatches \"{message}\"";
	}
}
