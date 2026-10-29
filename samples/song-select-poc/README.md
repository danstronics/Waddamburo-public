# Song Select proof of concept

A Song Select written from scratch for Waddamburo: `song_select.lumen.json` (symbols, layout, intro
animation) and `main.as` (LumenScript: list, genre headers, smooth scrolling, previews, difficulty choice,
`Wd.select`). It borrows Green's background, mode titles, course boards and 1P cursor from the installed
game when it is built; no game file is stored here.

```
lumenstudio-cli lumen-render  <built.ddp> song_select/song_select.lm --frames=200 --keys=50:S,100:Z   # no game needed to play
lumenstudio-cli lumen-preview song_select.lumen.json --game=<USRDIR> --preview=<folder> \
    --run=<LumenStudio>/build/waddamburo/Waddamburo.App.exe --args="--start-scene=song-select"
```

or open `song_select.lumen.json` in LumenStudio (F5 preview, F6 Waddamburo). Drum: Ka moves through the
list (genres open with a header row), Don opens the difficulty step, Ka chooses a course (or Back), Don
starts the song. See `docs/lumen-authoring.md`.
