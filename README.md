# HL2RP

A Half-Life 2 roleplay gamemode for s&box, built on the
[Hexagon](https://github.com/noahsabaj/hexagon) roleplay framework.

This project is the setting and nothing else. It currently contains no code: everything that
runs is Hexagon, and HL2RP supplies assets.

| What | Where |
| --- | --- |
| Factions: Citizen, in the blue jumpsuit; and Civil Protection, masked and in black, which needs an operator whitelist. A faction is people and a look: it grants nothing, and its members arrive with only their clothes | `Assets/factions` |
| Items: ration, water and bandage to use up; a radio; a keycard and a key, each cut to a lock when it is made; tokens, which pile up to a hundred and are money only because people take them; a zip tie that grants `person.restrain`; a pistol and its rounds, which stack | `Assets/items` |
| The city: spawn; Door 1, whose lock is cut to the city's pattern, `city`, and Door 2, a flat with its own, `flat2`, which starts locked; a crate stocked with rations, water, bandages and forty tokens; a locker whose lock is cut to `city`, holding a pistol, thirty rounds, two ties, a radio, a city keycard and the flat's key; game manager, HUD | `Assets/scenes/main.scene` |

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

The play test boots the real editor and plays this game: characters and the one-life rule, the
Civil Protection whitelist, chat, inventory, keys and locks, the crate and the locker, a restart, a
death, a retirement and a restore, and what the journal remembered.
