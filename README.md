# LumenStudio

> This branch holds only **LumenStudio** and the Waddamburo libraries it is built on. The game itself
> (Waddamburo) lives on [`master`](https://github.com/LucaSilva-r/Waddamburo-public); no original game
> data, fonts or other Bandai Namco files are included here.

LumenStudio is a Lumen (LMB) movie editor and texture editor for Gen3 *Taiko no Tatsujin* arcade data
(Green Ver.). It is built on Waddamburo's open Lumen runtime, parsers and bgfx renderer, and it writes
files the original PS3-based System 357 executable can load.

**Original game files are never modified.** Every change is written to an *overlay* folder that holds
only the archives you changed, in the game's own folder structure: copy it over the game to install.

## Quick start

1. Run `LumenStudio.exe`. *File › New project*:
   - **Original game data**: the unmodified `USRDIR` (or its `data/lumendata/packed`).
   - **Output (overlay)**: an empty folder (or your existing translation overlay). A `USRDIR` folder
     receives `data/lumendata/packed/…` and `data/nutdata/…` like the game.
   - **Project file**: where the `.lstudio` file is stored.
2. The clone index is built (a few seconds). Double-click a movie in **Archives** to open it.
3. Press **Space** to play. **Click anything** on the stage: playback pauses and the element under the
   cursor is selected (its texture opens in the **Texture** tab). The breadcrumb above the stage shows its
   nesting; click a level to select a parent.
4. Edit:
   - **Texture › Edit** (Ctrl+E): text layers in the game's outlined style (fill, inner and outer outline,
     shadow, letter spacing, horizontal squeeze), image layers, erase/fill, eyedropper, **any resolution**
     (Size…, 2× upscale), or *Edit externally* (Photoshop/GIMP; saved changes reload automatically).
   - **Replace** (Ctrl+R): any image of any size. Higher resolutions keep their on-screen size.
   - **Element** tab and stage: drag to move (whole track or current key), corner to resize,
     position/scale/rotation/opacity/blend/mask in the property grid.
   - **Timeline**: scrub, select tracks, F6 / Shift+F6 keys, Ctrl+T tweens, labels, scripts, duration.
   - **Text** tab: dynamic text fields' HTML. **Strings** tab: the string pool (script text), with usages.
   - **Scripts**: the AVM1 editor (lossless assembly with a decompiled view).
5. **Save all** (Ctrl+S) writes the changed archives to the overlay. **Tools › Validate** checks them.
6. **Animations** (bar above the timeline): a movie's labelled parts are separate animations. Pick one and
   only that part plays, looping (untick *Loop* to stop at its end). Animations sharing a name stem
   (`in_1p` / `in_2p`, `select_on` / `select_off`) are one animation with a **Variant** selector.
   **Child states** forces the state of child sprites that scripts would choose (for example
   `name_bg` = `off` / `1p_on` / `2p_on` / `1p_2p_on`); each forced state loops within its own part.
7. **F5** opens the movie (with unsaved edits) in Waddamburo; **Ctrl+F5** starts the whole game on a
   preview folder made of links to the original data plus your overlay.

## Clone propagation ("most recent edit wins")

Many textures are copies of each other under different names (normal song select vs. battle, ghost and
waiwai modes, nutdata packs, …). LumenStudio groups textures whose **original** pixels are identical.

- Editing a texture also changes every linked clone (checkbox in the editor; Revert works the same way).
- **Texture › Sync clones** finds families whose members disagree and copies the most recently edited
  member to the others. Edits made with other tools count too (their archive's file date), but
  re-encoding noise without visible change is ignored.
- Blank/solid images are never propagated automatically; any texture can be *unlinked* (Texture tab or
  clone browser) to keep it independent.

## Linked layers

*File › Project settings › Add linked layer* adds a separately modded copy of some archives (for example a
custom vertical song select). Its movies are protected by default: only clone texture pixels change, and
its archives are written to the layer's own output folder.

## Hardware compatibility

Every save is checked against invariants measured on all 229 original archives (see
`docs/lumenstudio/format-findings.md`): NTP3 v1 layout, 16-byte texture alignment, texture count and
ranges, F007 texture sizes, LMB references, the 4096-pixel limit and — important for resized textures —
the DDP trailer word holding the largest texture entry, which the original executable uses as its load
buffer size. Waddamburo previews cannot prove behaviour on real hardware: test the overlay there before
replacing a working installation.

## Command line

`lumenstudio-cli` offers `new`, `scan`, `sync [--apply]`, `replace`, `export`, `link` and `validate`
for batch work (run it without arguments for help).

## New movies from scratch (Lumen Author)

*File › Lumen Author* (or `LumenStudio.exe movie.lumen.json`) creates movies of our own: symbols and
timelines in a JSON project, logic in **LumenScript** (an ActionScript 2 subset), art either our own PNGs
or sprites borrowed from the installed game at build time (nothing of Namco's is stored in the project).
F5 compiles and plays the movie in the editor; F6 runs it in Waddamburo, which gives authored movies the
documented `Wd` host API. `samples/song-select-poc` is a working Song Select written this way. Reference:
`docs/lumen-authoring.md`; command line: `lumenstudio-cli lumen-build | lumen-preview | lumen-inspect |
lumen-render | lumen-script`.

## Building from source

LumenStudio is a Windows Forms app (`net10.0-windows`); the CLI, the authoring library and the tests
also build on Linux.

1. Install the .NET SDK **10.0.201** exactly (pinned by `global.json`, `rollForward: disable`).
2. Fetch the native libraries of the latest Waddamburo release (bgfx, FreeType, FFmpeg and the texture
   codec), which the stage view loads:

   ```powershell
   ./eng/fetch-native.ps1        # Windows (Linux: ./eng/fetch-native.sh)
   ```

   To build them yourself instead, see [native builds](docs/development/native-builds.md)
   (`native/` holds the CMake sources and presets).
3. Build, test and run:

   ```sh
   dotnet restore LumenStudio.slnx --locked-mode
   dotnet build LumenStudio.slnx -c Release --no-restore
   dotnet test LumenStudio.slnx -c Release --no-build
   dotnet run --project src/LumenStudio -c Release
   dotnet run --project src/LumenStudio.Cli -c Release -- validate <overlay>
   ```

   Two authoring tests also round-trip your original Song Select when `LUMENSTUDIO_SONG_SELECT_DDP` points
   to its `data/lumendata/packed/song_select/packeddata.ddp`; without it they are skipped silently.

**F5 / Ctrl+F5 / F6** start Waddamburo. LumenStudio looks for `waddamburo\Waddamburo.exe` next to
`LumenStudio.exe`, or for the executable chosen in *File › Project settings › Waddamburo executable*.
Running authored movies with the `Wd` host API needs a Waddamburo build that includes the authored-movie
binding described in [Lumen authoring](docs/lumen-authoring.md).

## Repository layout

| Path | Contents |
| --- | --- |
| `src/LumenStudio` | The desktop editor: stage, timeline, inspector, texture editor, scripts, clone sync, Waddamburo launcher. |
| `src/LumenStudio.Core` | Projects and sessions, texture clone index and propagation, archive rebuilding (no UI). |
| `src/LumenStudio.Authoring` | Lumen Author: JSON movie projects, LumenScript compiler, LMB/NUT/DDP builders, preview host. |
| `src/LumenStudio.Cli` | `lumenstudio-cli` for batch and authoring commands. |
| `src/Waddamburo.Formats` | DDP / NDP / LMB / NUT readers and writers, AVM1 assembly. |
| `src/Waddamburo.Lumen` | The Lumen runtime: AVM1 interpreter, movie/scene player and render snapshots. |
| `src/Waddamburo.Platform.Sdl` | SDL3 + bgfx renderer and text rasterizers used by the stage view. |
| `src/Waddamburo.Upscale` | PNG reading/writing and the 2× texture upscaler. |
| `tests/` | Authoring, format and Lumen runtime tests. |
| `tools/Probe` | Format probe used to measure the original archives (`docs/lumenstudio/format-findings.md`). |
| `samples/song-select-poc` | A Song Select written from scratch with Lumen Author. |
| `native/`, `eng/` | Native library sources and the fetch / shader build scripts. |

## Documentation

- [Lumen authoring](docs/lumen-authoring.md): project format, LumenScript, the `Wd` host API, CLI.
- [Format findings](docs/lumenstudio/format-findings.md): invariants measured on the original archives.
- Formats: [DDP archives](docs/development/ddp-archives.md), [LMB records](docs/development/lmb-records.md),
  [NUT textures](docs/development/nut-textures.md), [parser safety](docs/development/parser-safety.md).
- Runtime: [Lumen runtime](docs/development/lumen-runtime.md), [Lumen patches](docs/development/lumen-patches.md),
  [rendering](docs/development/rendering.md), [native builds](docs/development/native-builds.md),
  [FFmpeg](docs/development/ffmpeg.md).
- Legal: [provenance](docs/legal/provenance.md), [evidence policy](docs/legal/evidence-policy.md),
  [release content policy](docs/legal/release-content-policy.md), [notice](NOTICE.md),
  [third-party notices](THIRD_PARTY_NOTICES.md), [license](LICENSE).
