
public class RequestChatClass : IChatClass
{
	public string Name => "Request";
	public string Prefix => "/request";
	public float Range => 0f;
	public Color Color => HexConfig.Get<Color>( "hl2rp.chat.requestColor", new Color( 0.9f, 0.7f, 0.2f ) );

	private readonly Dictionary<string, DateTime> _cooldowns = new();

	public bool CanSay( HexPlayerComponent speaker, string message )
	{
		if ( !speaker.HasActiveCharacter || speaker.Character == null )
			return false;

		var inventories = InventoryManager.LoadForCharacter( speaker.Character.Id );
		bool hasDevice = false;
		foreach ( var inv in inventories )
		{
			if ( inv.HasItem( "request_device" ) )
			{
				hasDevice = true;
				break;
			}
		}

		if ( !hasDevice )
			return false;

		var charId = speaker.Character.Id;
		var cooldown = HexConfig.Get<float>( "hl2rp.request.cooldown", 5f );
		if ( _cooldowns.TryGetValue( charId, out var lastUse ) )
		{
			if ( (DateTime.UtcNow - lastUse).TotalSeconds < cooldown )
				return false;
		}

		_cooldowns[charId] = DateTime.UtcNow;
		return true;
	}

	public bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener )
	{
		if ( !listener.HasActiveCharacter || listener.Character == null )
			return false;

		return CombineUtils.IsCombine( listener.Character );
	}

	public string Format( HexPlayerComponent speaker, string message )
	{
		var data = (HL2RPCharacter)speaker.Character.Data;
		return $"[DEVICE, CID {data.CIDNumber}] {message}";
	}
}
