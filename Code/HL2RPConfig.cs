public static class HL2RPConfig
{
	public static void Register()
	{
		// Rank model paths (placeholder values — swap for real assets later)
		HexConfig.Add( "hl2rp.model.citizen", "models/citizen/male_01.vmdl", "Default citizen model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.cp.rct", "models/police.vmdl", "CP Recruit model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.cp.unit", "models/police.vmdl", "CP Unit model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.cp.ofc", "models/police.vmdl", "CP Officer model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.cp.epu", "models/police.vmdl", "CP Elite model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.cp.dvl", "models/police.vmdl", "CP Divisional model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.cp.sec", "models/police.vmdl", "CP Sectoral model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.cp.scn", "models/scanner.vmdl", "CP Scanner model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.ow.ows", "models/combine_soldier.vmdl", "OW Soldier model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.ow.owe", "models/combine_soldier.vmdl", "OW Elite model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.ow.opg", "models/combine_soldier.vmdl", "OW Prison Guard model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.ow.sgs", "models/combine_soldier.vmdl", "OW Shotgunner model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.ow.spg", "models/combine_soldier.vmdl", "OW Special Guard model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.admin", "models/breen.vmdl", "City Administrator model", "HL2RP Models" );
		HexConfig.Add( "hl2rp.model.vortigaunt", "models/vortigaunt.vmdl", "Vortigaunt model", "HL2RP Models" );

		// Faction pay rates (per payment period)
		HexConfig.Add( "hl2rp.pay.citizen", 0, "Citizen pay per period", "HL2RP Economy" );
		HexConfig.Add( "hl2rp.pay.cca", 25, "Civil Protection pay per period", "HL2RP Economy" );
		HexConfig.Add( "hl2rp.pay.ota", 30, "Overwatch pay per period", "HL2RP Economy" );
		HexConfig.Add( "hl2rp.pay.cityadmin", 40, "City Admin pay per period", "HL2RP Economy" );
		HexConfig.Add( "hl2rp.pay.vortigaunt", 0, "Vortigaunt pay per period", "HL2RP Economy" );

		// Gameplay
		HexConfig.Add( "hl2rp.dispenser.cooldown", 300f, "Ration dispenser cooldown in seconds", "HL2RP Gameplay" );
		HexConfig.Add( "hl2rp.request.cooldown", 5f, "Request device cooldown in seconds", "HL2RP Gameplay" );
		HexConfig.Add( "hl2rp.chardata.maxlength", 750, "Max length of character data notes", "HL2RP Gameplay" );

		// Chat
		HexConfig.Add( "hl2rp.chat.radioRange", 280f, "Radio chat physical range", "HL2RP Chat" );
		HexConfig.Add( "hl2rp.chat.dispatchColor", new Color( 0.75f, 0.22f, 0.17f ), "Dispatch chat color", "HL2RP Chat" );
		HexConfig.Add( "hl2rp.chat.radioColor", new Color( 0.3f, 0.5f, 0.9f ), "Radio chat color", "HL2RP Chat" );
		HexConfig.Add( "hl2rp.chat.requestColor", new Color( 0.9f, 0.7f, 0.2f ), "Request chat color", "HL2RP Chat" );

		// Combine
		HexConfig.Add( "hl2rp.combine.cpPrefix", "CP-", "Civil Protection name prefix", "HL2RP Combine" );
		HexConfig.Add( "hl2rp.combine.owPrefix", "OT-", "Overwatch name prefix", "HL2RP Combine" );
		HexConfig.Add( "hl2rp.combine.digitLength", 5, "Number of digits in combine ID", "HL2RP Combine" );
		HexConfig.Add( "hl2rp.combine.cpArmor", 50, "CP spawn armor percentage", "HL2RP Combine" );
		HexConfig.Add( "hl2rp.combine.owArmor", 100, "OW spawn armor percentage", "HL2RP Combine" );

		// Sounds (placeholder paths)
		HexConfig.Add( "hl2rp.sound.radioOn.cp", "sounds/radio/cp_on.vsnd", "CP radio on beep", "HL2RP Sounds" );
		HexConfig.Add( "hl2rp.sound.radioOff.cp", "sounds/radio/cp_off.vsnd", "CP radio off beep", "HL2RP Sounds" );
		HexConfig.Add( "hl2rp.sound.radioOn.ow", "sounds/radio/ow_on.vsnd", "OW radio on beep", "HL2RP Sounds" );
		HexConfig.Add( "hl2rp.sound.radioOff.ow", "sounds/radio/ow_off.vsnd", "OW radio off beep", "HL2RP Sounds" );
	}
}
