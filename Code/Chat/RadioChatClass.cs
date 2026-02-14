
public class RadioChatClass : IChatClass
{
	public string Name => "Radio";
	public string Prefix => "/r";
	public float Range => 0f;
	public Color Color => HexConfig.Get<Color>( "hl2rp.chat.radioColor", new Color( 0.3f, 0.5f, 0.9f ) );

	private static readonly string[] RadioItems = { "radio", "static_radio", "pager" };

	public bool CanSay( HexPlayerComponent speaker, string message )
	{
		if ( !speaker.HasActiveCharacter )
			return false;

		return speaker.Character.HasAnyItem( RadioItems );
	}

	public bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener )
	{
		if ( !listener.HasActiveCharacter )
			return false;

		if ( !listener.Character.HasAnyItem( RadioItems ) )
			return false;

		var speakerFreq = GetFrequency( speaker.Character );
		var listenerFreq = GetFrequency( listener.Character );

		if ( speakerFreq == listenerFreq )
			return true;

		var range = HexConfig.Get<float>( "hl2rp.chat.radioRange", 280f );
		var distance = Vector3.DistanceBetween(
			speaker.WorldPosition,
			listener.WorldPosition
		);

		return distance <= range;
	}

	public string Format( HexPlayerComponent speaker, string message )
	{
		var freq = GetFrequency( speaker.Character );
		return $"[FREQ {freq}] {speaker.CharacterName} says \"{message}\"";
	}

	private string GetFrequency( HexCharacter character )
	{
		var radio = character.FindAnyItem( RadioItems );
		return radio?.GetData<string>( "frequency", "100.0" ) ?? "100.0";
	}
}
