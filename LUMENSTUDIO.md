# LumenStudio

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
