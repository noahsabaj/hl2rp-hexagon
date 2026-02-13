# HL2RP Full Design — NutScript Port to Hexagon/s&box

**Date:** 2026-02-13
**Reference:** NutScript HL2RP at `D:\SteamLibrary\steamapps\common\GarrysMod\garrysmod\gamemodes\hl2rp`
**Target:** Hexagon framework on s&box (C#, .NET 10, Razor)
**Approach:** Asset-agnostic — all model/sound paths configurable, plug in real assets later

---

## Architecture

```
Code/
  HL2RPPlugin.cs          — main plugin, factions, classes, config, core hooks
  HL2RPCharacter.cs       — character data (expanded CharVars)
  HL2RPConfig.cs          — all config registrations

  Systems/
    RankSystem.cs          — name parsing -> rank -> model assignment
    CIDSystem.cs           — citizen ID generation, priority, validation
    CombineUtils.cs        — isCombine(), getCombineRank(), getDigits()
    LoadoutSystem.cs       — per-faction spawn loadout (weapons, armor, radio)

  Chat/
    RadioChatClass.cs      — frequency-based radio (/r)
    DispatchChatClass.cs   — combine elite broadcast (/dispatch)
    RequestChatClass.cs    — citizen -> combine device (/request)

  Items/
    (all 34+ item definitions organized by category)

  Entities/
    RationDispenser.cs     — scene component
    VendingMachine.cs      — scene component
    CombineLock.cs         — scene component
    Forcefield.cs          — scene component
    ScannerDrone.cs        — scene component (player-piloted)

  Plugins/
    TyingPlugin.cs         — zip tie restraint system
    FlashlightPlugin.cs    — flashlight item requirement
    PermitPlugin.cs        — business permit enforcement
    ShootlockPlugin.cs     — shoot locks off doors
    DoorKickPlugin.cs      — CP door kicking

  UI/
    CombineOverlay.razor   — tinted HUD for combine
    DispatchPanel.razor    — scrolling dispatch messages
```

Core HL2RP systems live in the main plugin. Self-contained features that could be toggled become their own `[HexPlugin]` classes. Everything stays in the hl2rp project; Hexagon handles the framework.

---

## Character Data (HL2RPCharacter)

```
Existing:
  Name        — string, MinLength=3, MaxLength=64, ShowInCreation
  Description — string, MinLength=16, MaxLength=512, ShowInCreation
  Model       — string, ShowInCreation
  Money       — int, Local, default 0

New:
  CharData    — string, Local, default "Points:\nInfractions:\n", MaxLength=750
                (combine-editable notes about the citizen)
  CIDNumber   — int, Local, default 0
                (auto-assigned on creation for citizens: random 10000-99999)
  Armor       — int, Local, default 0
                (set by loadout system on spawn)
```

---

## Rank System

Combine names follow strict patterns:
- CP: `CP-{RANK}.{DIGITS}` (e.g., CP-RCT.54321)
- OW: `OT-{RANK}.{DIGITS}` (e.g., OT-OWS.12345)

### CP Ranks
RCT, 05, 04, 03, 02, 01, OfC, EpU, DvL, SeC, SCN, CLAW.SCN

### OW Ranks
OWS, OWE, OPG, SGS, SPG

### Behavior
1. Parse rank tag from character name
2. Look up config-driven rank-to-model-path table
3. Force character model on spawn
4. Expose helpers: `GetCombineRank()`, `GetDigits()`, `IsEliteRank()`

Elite ranks (dispatch-eligible): EpU, DvL, SeC, SCN, CLAW.SCN

---

## CID System

- On citizen character creation: auto-generate random 5-digit CID number, store in CIDNumber CharVar
- CID item created and added to starting inventory with data: `{ name, id, cwu: false }`
- Combine can `/setpriority <player> true/false` to toggle CWU flag on citizen's CID item
- Dispensers check for CID item in inventory before dispensing
- Priority CID doubles ration dispenser output

---

## Combine Utilities (CombineUtils)

Static helper class:
- `IsCombineFaction(factionId)` — true for "cca" and "ota"
- `IsCombine(character)` — checks faction
- `GetCombineRank(character)` — parses name, returns rank string
- `GetDigits(character)` — extracts trailing 5-digit ID
- `IsDispatch(character)` — true for elite ranks
- `IsEliteRank(rank)` — rank check helper

---

## Loadout System

Per-faction spawn loadout:

| Faction | Weapons | Armor | Items |
|---------|---------|-------|-------|
| Citizen | — | 0% | CID card |
| CP | Stunstick | 50% | Radio |
| OW | (rank-dependent) | 100% | Radio |
| City Admin | — | 0% | — |
| Vortigaunt | — | 0% | — |

---

## Chat & Communication

### Radio Chat (/r, /radio)
- Requires: Radio, Pager, or Static Radio item in inventory
- Frequency-based: speaker and listener must have matching frequencies
- Range fallback: physical proximity (default 280 units) if no frequency match
- Static Radio: unlimited range, still needs frequency match
- Format: `[FREQ XXX.X] CharName says "message"`

### Dispatch Chat (/dispatch)
- Requires: Elite rank (EpU, DvL, SeC) or Scanner rank (SCN, CLAW.SCN)
- Global: all combine players hear regardless of distance
- Color: Red (192, 57, 43)
- Format: `<:: CharName dispatches "message"`
- No radio beeps

### Request Chat (/request)
- Requires: Request device item in inventory
- One-way: citizens send, only combine players receive
- Cooldown: 5 seconds between requests
- Format: `[DEVICE, CID XXXXX] message`

### Combine Voice System
Hook on existing IC/Yell chat. When combine player speaks:
1. Play random "radio on" beep sound before message
2. Play random "radio off" beep sound after
3. Different sound sets for CP vs OW
4. Listeners within range hear beeps spatially

---

## Items (34+)

### Identification
| Item | Base | Actions | Instance Data |
|------|------|---------|---------------|
| CID Card | ItemDefinition | Show, Assign (CP only) | name, id, cwu |

### Consumables
| Item | Actions | Notes |
|------|---------|-------|
| Ration Package | Open | Spawns: 1x water, 1x supplement, 10-15 tokens |
| Water | Drink | 5 tokens vendor price |
| Sparkling Water | Drink | 15 tokens |
| Special Water | Drink | 20 tokens |
| Supplement | Eat | Basic food |
| Large Supplement | Eat | Better food |
| Health Vial | Use | Small heal |
| Health Kit | Use | +50 HP, CP/OW only |
| Bleach | Use | Poison/cleaning |
| Vegetable Oil | Use | Cooking ingredient |

### Equipment
| Item | Instance Data | Notes |
|------|---------------|-------|
| Radio | frequency (000.0-999.9) | Tune action, enables /r, range 280 |
| Pager | frequency | Receive-only radio |
| Static Radio | frequency | Unlimited range |
| Request Device | — | Enables /request chat |
| Flashlight | on/off | Toggle, overrides default flashlight |
| Spraycan | — | Requires "misc" permit |

### Bags
| Item | Base | Size |
|------|------|------|
| Small Bag | BagItemDef | 2x2 |
| Large Bag | BagItemDef | 4x4 |
| Suitcase | BagItemDef | TBD |

### Armor
| Item | Base |
|------|------|
| Rebel Armor | OutfitItemDef |
| Modified Rebel Armor | OutfitItemDef |

### Ammo (7 types, all AmmoItemDef)
Pistol, SMG, Shotgun, .357, AR2, Crossbow Bolts, Rockets

### Weapons (8 types, all WeaponItemDef)
| Weapon | Ammo Type | Restrictions |
|--------|-----------|-------------|
| Crowbar | — (melee) | Blackmarket light |
| Pistol | Pistol | — |
| .357 Revolver | .357 | — |
| SMG | SMG | — |
| Shotgun | Shotgun | — |
| Crossbow | Crossbow | — |
| AR2 Pulse Rifle | AR2 | OW only, blackmarket heavy |
| RPG | Rockets | — |

### Permits (4 types, passive)
Food, Electronics, Literature, Misc

### Misc
| Item | Instance Data | Notes |
|------|---------------|-------|
| Note | text, owner | Read/Write, 1000 char limit |
| Book | — | Read action, predefined content |
| Zip Tie | — | Use on target -> restraint system |

### Blackmarket Flags
Items tagged as light ("y") or heavy ("Y") contraband. Used by permit/business system.

---

## World Entities

### Ration Dispenser
- State machine: IDLE -> CHECKING -> ARMING -> DISPENSE -> IDLE (or ERROR)
- `[Sync]` State, IsDisabled
- Cooldown per CID number (5 min, server-side)
- LED colors: Green=ready, Blue=checking, Orange=arming, Red=error
- Citizen + Use: check CID -> validate cooldown -> dispense ration
- Priority CID: doubles output
- CP + Use: toggle disabled
- Persisted via DatabaseManager

### Vending Machine
- `[Sync]` Stock per button (3 slots), IsActive
- Citizen + Use: select button -> check tokens -> dispense water, decrement stock
- CP + Use: refill (25 tokens) or toggle active
- Persisted via DatabaseManager

### Combine Lock
- `[Sync]` State (Locked/Unlocked/Error/Detonating), ParentDoor
- LED: Orange=locked, Green=unlocked, Red=error
- CP/OW + Use: toggle lock
- CP + Crouch+Use (hold): 10s detonation countdown -> blast force on door
- Non-combine blocked if locked
- Persisted via DatabaseManager

### Forcefield
- `[Sync]` IsActive
- Dual-model setup with physical collider
- CP/OW pass through (collision filter), citizens blocked
- Admin-only spawning
- Persisted via DatabaseManager

### Scanner Drone
- `[Sync]` PilotPlayer, Health
- CP with SCN/CLAW.SCN rank enters scanner mode
- Acceleration-based flight, hover height
- Flash photography, spotlight, scan sounds
- If destroyed: player ejected/ragdolled back to body
- Most complex entity, can be deferred

---

## Sub-Plugins

### Tying Plugin
- Zip tie item on target player -> 5s action -> player restrained
- Restrained: no movement, no inventory, no interaction
- Untying: another player -> 5s action
- `[Sync]` IsRestrained on player component

### Flashlight Plugin
- Flashlight key only works if player has flashlight item in inventory

### Permit Plugin
- Items tagged with permit category
- Buy/sell of tagged items requires matching permit in inventory

### Shootlock Plugin
- Shooting at locked doors -> enough damage breaks lock
- Does not affect combine locks

### Door Kick Plugin
- `/doorkick` command, CP only
- Kick animation, door forced open with physics impulse

---

## Commands

| Command | Permission | Description |
|---------|-----------|-------------|
| /doorkick | CP faction | Kick open a door |
| /data \<player\> | Combine | View/edit citizen CharData notes |
| /objectives | Admin | View/edit server objectives text |
| /setpriority \<player\> \<bool\> | Combine | Toggle CWU priority on CID |
| /request \<text\> | Citizen + device | Send to combine (5s cooldown) |
| /dispatch \<text\> | Elite combine | Broadcast to all combine |
| /radio \<text\> | Has radio item | Frequency-based message |

---

## UI/HUD (Razor Components)

### Combine HUD Overlay
- Tinted screen overlay for CP/OW (subtle blue/dark filter)
- In-world only, not in menus
- Toggled by faction check on character load

### Dispatch Message Panel
- Scrolling text in corner for combine players
- Typewriter effect (character-by-character reveal)
- Color-coded: dispatch=red, radio=blue, system=white
- Auto-fade, message queue

### Data Panel
- 280x380 popup showing citizen's CharData
- Combine can view and edit (750 char limit)
- Default template if empty

### Objectives Panel
- Server-wide objectives text, admin-editable
- 750 char limit

### Radio Tuning UI
- Frequency dial for radio items
- XXX.X format, up/down per digit
- Sound effects on adjustment

### Note Editor
- Text editor popup for note items
- 1000 char limit, owner can write, others read-only

---

## Implementation Phases

### Phase 1: Core Schema Enhancement
- Expand HL2RPCharacter (CharData, CIDNumber, Armor)
- RankSystem, CIDSystem, CombineUtils
- HL2RPConfig (all configurable values, model path tables)
- Loadout system
- /data, /objectives, /setpriority commands

### Phase 2: Chat & Communication
- RadioChatClass, DispatchChatClass, RequestChatClass
- Combine voice beep hook
- Radio tuning UI

### Phase 3: Items — Core
- CID card, Ration package, consumables
- Radio, Pager, Static Radio, Request device
- Flashlight, Spraycan
- Bags, Permits, Notes, Books, Zip Tie

### Phase 4: Items — Weapons & Ammo
- 7 ammo types, 8 weapon types (WeaponItemDef)
- Armor items
- Blackmarket flag system

### Phase 5: World Entities
- Ration Dispenser, Vending Machine
- Combine Lock, Forcefield

### Phase 6: Sub-Plugins
- Tying, Flashlight, Permit, Shootlock, Door Kick

### Phase 7: UI/HUD
- Combine overlay, Dispatch panel
- Data/Objectives panels, Note editor

### Phase 8: Scanner Drone
- Scanner entity, piloting system, flash/spotlight
