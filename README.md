# HL2RP

A Half-Life 2 roleplay gamemode for s&box, built on the
[Hexagon](https://github.com/noahsabaj/hexagon) roleplay framework.

This project is the setting and nothing else. It currently contains no code: everything that
runs is Hexagon, and HL2RP supplies assets.

| What | Where |
| --- | --- |
| Faction: Civil Protection. A faction is people: it grants nothing and changes nothing about its members, who are appointed and handed a uniform. A citizen is anyone who belongs to none, which is everyone as they arrive | `Assets/factions` |
| Clothes: a wardrobe to arrive in (flat cap, fedora, shirt and waistcoat, flannel shirt, cardigan, the blue jumpsuit, trousers, jeans and black boots), and a Civil Protection uniform of mask, jacket and trousers, which nobody arrives in | `Assets/items` |
| Bodies and hair a new character may choose: the citizen, the engine's seven human skins, and nine hairstyles and beards | `ProjectSettings/Systems.config` |
| Papers: an identification card, a printed form with blanks for a name, an occupation and a residence, which every arrival carries with its name filled in and the Civil Authority's stamp on it; paper, sixteen lines to a sheet and ten to a pile; a pen, which grants `paper.write`; the Civil Authority's stamp; a housing certificate, a form with blanks for a residence, its holder and the date it was issued; and the manifest a supply train brings, under the stamp of Combine Overwatch Logistics | `Assets/items`, the arrival's in `ProjectSettings/Systems.config` |
| Items: ration and water to use up, and a bandage that treats a wounded arm or leg, on yourself or on someone else; a radio; a keycard and a key, each cut to a lock when it is made; a lock to fit, cut the same way, and a key blank to copy a key onto; tokens, which pile up to a hundred and are money only because people take them, and the blanks they are struck from; a die, which strikes a token from a blank in hand; a loupe, which grants `item.compare`, to tell whether tokens were made alike; scrap metal, and a crowbar, which grants `site.salvage`; a fuel can, which fills a truck's tank by a third; a zip tie that grants `person.restrain`; a pistol, which kills outright with a shot to the head, and its rounds, which stack | `Assets/items` |
| Sound: the hard breathing of someone badly hurt or down, heard by them and by anyone close | `Assets/sounds`, set in `ProjectSettings/Systems.config` |
| The city: spawn; Door 1, whose lock is cut to the city's pattern, `city`, and Door 2, Flat 2's door, with a lock of its own, `flat2`, which starts locked, and whose title the Civil Authority holds until whoever carries its stamp sells it; a crate stocked with rations, water, bandages and forty tokens; a locker whose lock is cut to `city`, holding a pistol, thirty rounds, two ties, a radio, a city keycard, the flat's key, a Civil Protection uniform with boots, the Civil Authority's stamp, the armbands of every rank, twenty token blanks and a loupe; a box of paper and pens anyone may help themselves to; the Civil Authority's housing counter, locked to `city`, holding five housing certificates, where the money for a flat goes; a planning table beside a case holding the city map and grease pencils; the city's token press, locked to `city`, which strikes ten tokens from ten blanks; and a little way off, a forger's crude press anyone can work, beside a crate of blanks, a die and a loupe; south of the city, the supply train's station, where it stands at six in the morning and six in the evening with sixteen rations, sixteen waters, forty token blanks and two fuel cans, its wagons locked to `city`, and takes away what is loaded on its outbound wagon; beside the line, a scrap yard worked with a crowbar from the toolbox there, five pieces at a time, up to ten, growing back ten an hour, and a cutter that turns two pieces of scrap into five blanks; beside the toolbox, which holds four key blanks too, a locksmith's bench that makes a lock and two keys from two pieces of scrap, all cut alike to a pattern nobody has, and copies a key onto a blank; by the station, the Combine truck, its ignition and bed cut to `city`, a third of a tank in it, beside a crate holding a can of fuel; HUD | `Assets/scenes/main.scene` |

Code belongs here only when it is about this setting, such as the Combine. Anything another
roleplay game would also want belongs in Hexagon.

## Setup

Hexagon is compiled into the game from a sibling checkout.

1. Clone `hexagon` next to this folder.
2. Link it in, from this folder:

```bash
pwsh -Command "New-Item -ItemType Junction -Path Libraries/hexagon -Target (Resolve-Path ../hexagon)"
```

3. Open `hl2rp.sbproj` in the s&box editor.

## Checking it

The tools live in Hexagon and default to this game.

```bash
pwsh ../hexagon/tools/verify.ps1
```

```bash
pwsh ../hexagon/tools/playtest.ps1
```

The play test boots the real editor and plays this game: characters made with a body, hair and
clothes, and the one-life rule; chat, inventory, clothes put on and taken off, keys and locks, the
crate and the locker; papers filled in, written on and stamped, and a letter that survives the
restart; an officer appointed by staff who puts the uniform on; wounds and a bandage;
a restart, a death, a body stripped of its mask, a retirement and a restore, and what the journal
remembered. The two-client test, `../hexagon/tools/playtest-server.ps1`, plays the rest between two
players on a dedicated server, among it a flat sold over the counter, its lock changed and its key
copied at the locksmith's bench, and the flat handed back to the Civil Authority.

## Credits

- `Assets/sounds/breathing_hard.wav` is ["breathing tired.wav"](https://opengameart.org/content/breathing-tired)
  by mikeask, from OpenGameArt, released under CC0. It was made mono and normalised.
