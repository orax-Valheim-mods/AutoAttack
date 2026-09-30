# AutoAttack

Press a key to start continuous attacking — the mod keeps the attack button held for you — and press it again to stop. No more holding the attack button through long fights.

Client-side only: neither the server nor other players need the mod.

## Features

- **Toggle on/off** with a keyboard shortcut (default `Mouse1 + LeftAlt`) or a gamepad combo (default `JoyLBumper, JoyRBumper` = L1 + R1).
- **Pause**: holding a configured button (block, jump, secondary attack, movement by default) temporarily pauses the toggle; it resumes automatically when you release it.
- **Cancel**: pressing a configured button stops the toggle completely.
- **Weapon change protection**: automatically cancels when the equipped weapon changes (can be disabled).
- **Bows**: keeps drawing and releases the shot automatically once fully drawn.
- **Crossbows**: waits for the reload instead of holding through it.
- **On-screen messages** for on/off/cancelled, with configurable texts (empty text = hidden) and position.
- **Per-button configuration**: every input button the game registers gets its own on/off entry, split into keyboard and gamepad sections and generated from the game's own input registry at startup.
- **Master switch** (`[General] Enabled`) to turn the mod fully off and get vanilla behavior.

## Installation

### Using a mod manager (recommended)

Install via NexusMods r2modman, Gale or the Thunderstore Mod Manager. BepInExPack is installed automatically as a dependency.

### Manual

1. Install [BepInExPack for Valheim](https://thunderstore.io/package/denikson/BepInExPack_Valheim/).
2. Drop `AutoAttack.dll` into `<Valheim>/BepInEx/plugins/`.

## Configuration

Config file: `BepInEx/config/orax.AutoAttack.cfg` — every entry is documented inline.

- `[General]` > Master switch, cancel-on-weapon-change
- `[Keyboard]` > Toggle shortcut (Unity `KeyCode` names, e.g. `Mouse1 + LeftAlt`)
- `[Gamepad]` > Toggle combo (ZInput button names, e.g. `JoyLBumper, JoyRBumper`)
- `[Keyboard cancel buttons]` > `[Gamepad cancel buttons]` > Press to cancel the toggle
- `[Keyboard pause buttons]` > `[Gamepad pause buttons]` > Hold to pause the toggle
- `[HUD]`, `[Messages]` > Message display, position and texts

Notes:

- The gamepad button list is generated at startup from the game's own input registry and reflects the controller layout active at that moment; restart the game after changing the layout to regenerate it.
- Unknown button names simply never trigger, and a warning is logged once.

## Misc

Source code: [GitHub repository](https://github.com/orax-Valheim-mods/AutoAttack)
