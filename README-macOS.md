# CyberCAT for macOS

Native macOS port of [Deweh/CyberCAT-SimpleGUI](https://github.com/Deweh/CyberCAT-SimpleGUI), using Avalonia and the upstream WolvenKit save parser. The original Windows Forms application remains in `CP2077SaveEditor`.

## Run

Download the ZIP for your Mac from the fork's release or a successful **macOS** Actions run:

- `osx-arm64`: Apple Silicon (M1 and later).
- `osx-x64`: Intel.

Extract `CyberCAT.app` and open it. The app includes .NET; installing a runtime is unnecessary. These builds are ad-hoc signed, without Apple Developer ID notarization. A downloaded build may require approval in macOS **System Settings → Privacy & Security** before its first launch.

Open `sav.dat` inside a save slot. The native Mac game normally stores saves here:

```text
~/Library/Application Support/CD Projekt Red/Cyberpunk 2077/saves/
```

`⌘O` opens a save, `⌘S` writes changes, and `⇧⌘S` saves into another slot folder. **Save as…** copies available `metadata.9.json` and `screenshot.png` into the destination if they are absent, then switches the editor to the new file. To create a separate game slot, use a new `ManualSave-N` folder inside the saves directory.

Every replacement creates a unique timestamped `.bak` file beside `sav.dat`. Writes use a temporary file in the destination directory; the app re-parses that file before replacing the destination. A save changed by another process since opening is protected from overwrite. Close the game before editing a slot so it cannot overwrite your changes.

The header distinguishes a **Game save slot** from an **External copy** and shows the full path. A file saved outside the game's saves folder will not appear in the game's Load Game menu. Inventory quantities and flags apply immediately; after **Save changes**, load the edited slot in the game.

## Features and scope

- Player attributes, levels, skills and unspent attribute/perk/relic points.
  Zero-valued point fields omitted by the game are restored when edited, with package offsets updated while preserving other fields.
- All inventory containers, item search, quantities (including `Items.money`) and item flags.
- Quest fact search and editing, including unknown numeric hashes and new fact names.
- Vehicle garage unlocks.
- Appearance `.v2preset` import/export and advanced JSON editing for the same body type.
- Unlock fast travel points when the script package can be safely rewritten.
- Existing upstream mod type registrations are reused when reading modded saves.

The initial macOS port does not include the Windows-only item creation, item mod-tree/stat editor, legacy `.preset` conversion, or body/voice switching. Game compatibility follows the pinned WolvenKit parser with additional 2.5 optimized stats schemas. It requires saves from 2.0 or newer and does not guarantee every future patch or modded save. Build 2500 was round-trip tested on a local Mac save, including inventory, player attributes, synchronized forced modifiers, quest facts and appearance; this is a file-level check, not an in-game load test.

## Build from source

Requires macOS, Xcode Command Line Tools (`xcode-select --install`) and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Both Intel and Apple Silicon build outputs contain a universal Kraken library built from source. CMake and the game's proprietary Oodle library are unnecessary.

```sh
git clone --branch macos https://github.com/EvtuProd/CyberCAT-SimpleGUI.git
cd CyberCAT-SimpleGUI
git submodule update --init
dotnet run --project CyberCAT.Mac
```

Build a self-contained `.app` and ZIP:

```sh
bash scripts/build-macos.sh osx-arm64
bash scripts/build-macos.sh osx-x64
```

Outputs are in `artifacts/<rid>/`. Set `DOTNET=/path/to/dotnet` if the SDK is outside `PATH`. The native library is rebuilt automatically when its sources change.

Run integration checks:

```sh
dotnet run --project CyberCAT.SmokeTests -c Release -p:NuGetAudit=false
# Optionally validate a real save; it is only read, and outputs go to a temporary directory:
dotnet run --project CyberCAT.SmokeTests -c Release -p:NuGetAudit=false -- /path/to/slot/sav.dat
```

Tests cover native decompression, item name resolution, fact and inventory edits, backups, invalid input, and optional real-save/preset round-trips. They do not modify the supplied real save. Test outputs remain in the printed temporary directory for inspection. Never commit personal save files.

## Architecture

`CyberCAT.Core` provides save operations without UI dependencies. `CyberCAT.Mac` implements the desktop UI. WolvenKit is pinned at the original editor's commit; the macOS build overrides its Intel-only Kraken binary and removes unused Windows native content through `Directory.Build.targets`, without changing the WolvenKit checkout. `native/kraken` and `native/simde` pin the native sources used by `scripts/build-kraken.sh`.

Untouched leaf nodes retain their original bytes. Scalar package edits patch existing fields in place when possible, retaining unsupported data. A structural package change is refused if its parser skipped fields. Optimized player stats use the `valuesOptimized`/`sharedBufferSlices`/`sharedDataBuffer` layout and only patch a unique, unshared forced modifier. The layout was cross-checked against current [WolvenKit schemas](https://github.com/WolvenKit/WolvenKit) and [Cyberpunk25SaveEditor](https://github.com/desfpc/Cyberpunk25SaveEditor).

The original MIT license and upstream credits are preserved. See `LICENSE`, `licenses/`, the WolvenKit license and SIMDe's `COPYING` for dependency notices.
