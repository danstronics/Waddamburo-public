# Authoring Lumen movies from scratch

LumenStudio builds Lumen movies (LMB + NUT textures in a Green `LM_NUT_TYPE1` archive) from a source
project of our own: a JSON description of symbols and timelines, plus scripts in **LumenScript**, an
ActionScript 2 subset compiled to Lumen's AVM1 bytecode. A project may *borrow* sprites, animations and
textures from the installed game's movies; they are read from the game folder at build time, so a
project (and any repository holding it) contains no Namco data.

Waddamburo recognises the result (the marker string `wd:authored/1`) and gives it the `Wd` host API
instead of Green's original Song Select protocol.

```
movie.lumen.json + *.as ──lumenstudio-cli lumen-build──▶ <archive>.ddp ──▶ Waddamburo (Wd API)
        ▲                     (or Lumen Author: F5 preview, F6 run in Waddamburo)
        └── borrowed symbols: ${game}/data/lumendata/packed/<archive>.ddp (read only)
```

The working example is `samples/song-select-poc`: a vertical Song Select written from scratch (song list,
genre headers, smooth scrolling, previews, difficulty choice) on Green's own background, titles, course
boards and 1P cursor. Choosing a course starts the song in Waddamburo.

## Tools

| | |
|---|---|
| `LumenStudio.exe movie.lumen.json` or *File › Lumen Author* | The editor (below). |
| `lumenstudio-cli lumen-build <movie.lumen.json> --game=<USRDIR> [--out=x.ddp]` | Build the archive (default `build/<archive>` next to the project). |
| `lumenstudio-cli lumen-preview <movie.lumen.json> --game=<USRDIR> --preview=<folder> [--run=<Waddamburo.App.exe>] [--args="--start-scene=song-select"]` | Build, place it over a junction mirror of the game (the game folder is never written) and optionally start Waddamburo. |
| `lumenstudio-cli lumen-inspect <archive.ddp> [movie] [--all]` | A movie's sprites: export names, labels, frame counts, named children: what can be borrowed. |
| `lumenstudio-cli lumen-render <archive.ddp> <movie> --frames=N [--shots=a,b] [--keys=40:S,80:Z+3]` | Headless playback with a stand-in `Wd` catalog; software-rendered PNGs. |
| `lumenstudio-cli lumen-script <file.as>` | Compile a script and print its disassembly. |

Waddamburo must be a build with the `Wd` API (the copy in `LumenStudio/source`, published to
`build/waddamburo`). Useful Waddamburo switches: `--start-scene=song-select`, `--press=K@200,F@300`
(scripted keys by tick), `--ticks=N --screenshot=out.png` (headless).

## Lumen Author (the editor)

- **Tree**: the movie, its imports (game archives), images and symbols; sprites list their layers and
  frame scripts. The **property grid** edits the selected item (layer keyframes through the *Keys*
  collection editor).
- **Insert**: *Symbol from a game archive* lists an archive's exported sprites and adds the chosen ones as
  `import` symbols; images (PNG, copied to `art/`), rectangles, text fields, sprites, layers, frame scripts.
- **Script** tab: the selected frame script (`@file.as` scripts are edited in their file). Ctrl+S saves.
- **F5** saves, compiles and plays the movie on the stage with the stand-in catalog. Click the stage, then
  Left/A and Right/S are the Ka rims, Space/Z the Don. `Wd` calls and runtime warnings go to *Build log*.
- **F6** builds over a mirror of the game folder (*File › Game folder*) and starts Waddamburo on Song Select.

## Project file (`*.lumen.json`)

Comments and trailing commas are allowed. Paths are relative to the project file; `${game}` is the game's
USRDIR.

```jsonc
{
  "archive": "song_select/packeddata.ddp",      // written to data/lumendata/packed/<archive>
  "movie": "song_select/song_select.lm",        // the movie's name inside the archive
  "width": 1280, "height": 720, "fps": 60,
  "imports":  [ { "id": "ss", "archive": "${game}/data/lumendata/packed/song_select/packeddata.ddp",
                  "movie": "song_select/song_select.lm", "stripScripts": false } ],
  "textures": [ { "id": "panel", "file": "art/panel.png", "format": "bc3" } ],   // bc1, bc2, bc3, argb
  "symbols":  [ ... ],
  "root": "Main"                                // the sprite the movie plays
}
```

Symbols (`kind`):

| kind | fields | |
|---|---|---|
| `import` | `from: "importId:ExportName"` or `"importId:#characterId"` | A sprite of a game movie with everything it places (shapes, sprites, text fields, frame scripts, labels, seek snapshots, textures). A layer may also name `"importId:ExportName"` directly. |
| `image` | `texture`, optional `region: [x, y, w, h]`, `width`, `height` | A textured quad. |
| `rect` | `width`, `height`, `color` (#RRGGBB or #AARRGGBB) | A solid quad. |
| `text` | `width`, `height`, `size`, `align` (left/center/right), `color` | A dynamic text field: scripts set `field.text`; Waddamburo draws it with the game's font. |
| `sprite` | `frames`, `labels: {name: frame}`, `layers`, `scripts: {frame: code or "@file.as"}`, `export` | A timeline. `export` is the linkage name `attachMovie` uses. |

`originX` / `originY` (0–1) place a symbol's origin within its box (0,0 top-left, 0.5,0.5 centre).

Layers (bottom to top) place one symbol each: `name` (the instance name scripts use), `symbol`, `from` /
`to` (frames on stage, default the first key to the end), `blend` (normal, multiply, screen, add) and
`keys`: `frame`, `x`, `y`, `scaleX`, `scaleY`, `rotation` (degrees), `alpha` (0–1), `tint` (#RRGGBB
multiply) and `tween` (to the next key: `linear` by default, `easeIn`, `easeOut`, `easeInOut`, `hold`).
Values a key leaves out carry over. Tweens are baked into per-frame moves, as Lumen stores them.

Every declared symbol is built, placed or not (scripts may attach exports by name).

## LumenScript

An ActionScript 2 subset compiled to the AVM1 opcodes Waddamburo's interpreter executes:

- `var`, `function name(a, b) { }`, function expressions, `return`, `if / else`, `while`, `do … while`,
  `for (;;)`, `switch / case / default`, `break`, `continue`, blocks; AS2 type annotations are ignored.
- Operators: `+ - * / %`, comparisons (`== != === !== < > <= >=`), `&& ||` (short-circuit), `! ~ -`,
  `& | ^ << >> >>>`, `?:`, `= += -= *= /= %= …`, `++ --`.
- Literals: numbers, strings (`\n \t \uXXXX`), `true false null undefined`, arrays `[…]`, objects `{a: 1}`.
- Member access and calls (`a.b`, `a[i]`, `obj.method(…)`), `new Name(…)` / `new a.B(…)`, `this`,
  `_root`, `_parent`, `_global`.
- `stop()`, `play()`, `gotoAndPlay(x)`, `gotoAndStop(x)`, `trace(x)` on the current timeline (labels or
  1-based frame numbers); on clips: `clip.gotoAndPlay("label")` etc.
- `import flash.external.ExternalInterface;` makes `ExternalInterface` an alias of the full path.

Not supported (the interpreter has no opcode for them): `typeof`, `delete`, `for … in`, `with`,
`try`, classes. Assignments inside expressions work on variables only.

What the runtime offers (Waddamburo's Lumen player): MovieClip properties `_x _y _xscale _yscale
_rotation _alpha _visible _currentframe _totalframes _name`, `attachMovie(export, name, depth)`,
`createEmptyMovieClip`, `removeMovieClip`, `swapDepths`, `getNextHighestDepth`, `onEnterFrame`,
`Key.isDown(code)`, `Math.*`, `Array` (`push pop shift join length`), `String` (`charAt charCodeAt length`),
`parseInt`, `flash.geom.ColorTransform`, `ExternalInterface.addCallback / call`.

Input: Waddamburo maps the drum to Flash key codes: left Ka **65** (A), right Ka **83** (S), Don **90** (Z)
(player 2: 68, 70, 67).

Child sprites run their own frame-0 scripts after the parent's; to start a borrowed animation that stops
itself on frame 0 (most of Green's do), call its `gotoAndPlay` from the first `onEnterFrame` instead.

## The `Wd` host API (Song Select)

Movie → Waddamburo (global object `Wd`):

| call | returns |
|---|---|
| `Wd.version()` | 1 |
| `Wd.ready()` | Poll each frame until true: Waddamburo sets up the scene's header legend, name boards and song counter. |
| `Wd.player()` | The player's drum side (0 left, 1 right). |
| `Wd.genreCount()`, `Wd.genreName(g)`, `Wd.genreColor(g)` | Genres are the catalog's folders that hold songs; colour 0xRRGGBB or -1. |
| `Wd.songCount(g)`, `Wd.songTitle(g, s)`, `Wd.songSubtitle(g, s)` | Titles in English when the song has one. |
| `Wd.hasCourse(g, s, c)`, `Wd.songLevel(g, s, c)` | Courses 0 Easy, 1 Normal, 2 Hard, 3 Oni, 4 Ura; level 0 when absent. |
| `Wd.preview(g, s)`, `Wd.stopPreview()` | The song's preview audio. |
| `Wd.select(g, s, c)` | Starts the song (rainbow, chart loading, gameplay); true when accepted. |
| `Wd.sound(bank, cue)`, `Wd.drum(kind)` | A sound-bank cue; the drum's Don (0) / Ka (1) sound. |
| `Wd.log(…)` | A line in Waddamburo's console. |

Waddamburo → movie: callbacks the movie registers with
`ExternalInterface.addCallback("wdStart", this, function (genre, song) { … })`; Waddamburo calls
`wdStart` once the movie is loaded, with the genre and song the cursor starts on (the last song played).

Implementation: `src/Waddamburo.Game/Lumen/AuthoredSongSelectBinding.cs` (the host),
`LayerHostFactory.Create(layer, content)` (chosen by the marker string), and `GameShell.patch` /
`SongSelectLayoutPreparation` (Green's layout patches skip authored movies).

## How it is built (LumenStudio.Authoring)

- `Lmb/LmbBuilder` writes LMB from scratch: deduplicated pools (strings, colours, matrices, translations,
  bounds, texture sizes, actions), shapes, text fields, sprites (labels, frames, seek snapshots) in Green's
  record order.
- `Lmb/MovieImporter` copies a character and everything it places from a game movie, remapping ids, pool
  entries and textures; `Lmb/Avm1Rewriter` re-encodes imported bytecode with the new string indices
  (8-bit pushes widen, branches and function bodies relocate; identity rewriting reproduces all of
  song_select's actions byte for byte).
- `Script/` is the LumenScript lexer, parser and code generator.
- `Packaging/ArchiveWriter` writes the DDP (preamble, movie directory, texture table, trailer, payloads);
  `NutBuilder` makes NTP3 textures from RGBA; `PreviewRoot` makes the junction mirror for playtests.
- `Project/ProjectCompiler` ties it together and validates the result with Waddamburo's own reader.

Tests: `tests/LumenStudio.Authoring.Tests` (scripts run on Waddamburo's interpreter, timelines, attached
exports and text, host calls both ways, archive round trip; with the game data present, byte-exact
re-encoding of song_select's actions, borrowed sprites, and the sample Song Select driven to a selection).

## Limits

- Only Waddamburo runs authored movies (the original executable does not know the `Wd` API); the PS3 has not
  been a target here.
- Borrowed sprites keep their original frame scripts; calls into Green's script classes (`Startup`,
  `FinishTween`…) return undefined in our movies (warnings in the log), which is harmless for the sprites
  the sample uses. `stripScripts` drops a whole import's scripts except plain `stop()`.
- No visual timeline editing of authored symbols yet: keys are edited in the property grid or the JSON.
