# HL2RP

A Half-Life 2 roleplay gamemode for s&box, built on the
[Hexagon](https://github.com/noahsabaj/hexagon) roleplay framework.

This project is the setting and nothing else. It currently contains no code: everything that
runs is Hexagon, and HL2RP supplies assets.

| What | Where |
| --- | --- |
| Factions: Citizen, in the blue jumpsuit; and Civil Protection, masked and in black, which needs an operator whitelist, has `door.lock`, is paid a wage and is issued a radio, a pistol, thirty rounds and two ties | `Assets/factions` |
| Items: ration, water and bandage to use up; radio; a keycard that grants `door.lock`; a zip tie that grants `person.restrain`; a pistol and its rounds, which stack | `Assets/items` |
| The city: spawn, two doors (one for sale), a crate, a ration vendor, game manager, HUD | `Assets/scenes/main.scene` |

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

The play test boots the real editor and plays this game: characters, the Civil Protection
whitelist, chat, inventory, doors, the keycard, a restart, and what the journal remembered.
