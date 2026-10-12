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
| Items: ration and water to use up, and a bandage that treats a wounded arm or leg, on yourself or on someone else; a radio; a keycard and a key, each cut to a lock when it is made; tokens, which pile up to a hundred and are money only because people take them; a zip tie that grants `person.restrain`; a pistol, which kills outright with a shot to the head, and its rounds, which stack | `Assets/items` |
| Sound: the hard breathing of someone badly hurt or down, heard by them and by anyone close | `Assets/sounds`, set in `ProjectSettings/Systems.config` |
| The city: spawn; Door 1, whose lock is cut to the city's pattern, `city`, and Door 2, a flat with its own, `flat2`, which starts locked; a crate stocked with rations, water, bandages and forty tokens; a locker whose lock is cut to `city`, holding a pistol, thirty rounds, two ties, a radio, a city keycard, the flat's key and a Civil Protection uniform with boots; HUD | `Assets/scenes/main.scene` |

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
crate and the locker; an officer appointed by staff who puts the uniform on; wounds and a bandage;
a restart, a death, a body stripped of its mask, a retirement and a restore, and what the journal
remembered.

## Credits

- `Assets/sounds/breathing_hard.wav` is ["breathing tired.wav"](https://opengameart.org/content/breathing-tired)
  by mikeask, from OpenGameArt, released under CC0. It was made mono and normalised.
