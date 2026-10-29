// Song Select, written from scratch in LumenScript (an ActionScript 2 subset compiled by LumenStudio).
//
// Movie -> Waddamburo: the global Wd object (genres and songs of the catalog, previews, sounds, and
// Wd.select, which starts the song). Waddamburo -> movie: wdStart(genre, song), registered below with
// ExternalInterface, tells where the cursor starts. Input: Key.isDown with the drum mapped by
// Waddamburo to A (left Ka), S (right Ka) and Z (Don).
import flash.external.ExternalInterface;

var KEY_LEFT = 65;
var KEY_RIGHT = 83;
var KEY_DON = 90;
var ROWS = 9;
var CENTER = 4;
var ROW_HEIGHT = 62;
var LIST_X = 70;
var LIST_Y = 96;
var COURSE_X = [800, 900, 1000, 1100];
var BACK_X = 690;

var genre = 0;          // cursor: genre and song (-1: the genre's header row)
var song = -1;
var scroll = 0;         // pixels the rows still have to travel after a move
var mode = "music";     // music | course | starting
var course = 3;         // course cursor: -1 back, 0 Easy .. 3 Oni
var heldLeft = 0;
var heldRight = 0;
var heldDon = 0;
var still = 0;          // frames since the last move: the preview starts after a pause
var previewed = "";
var rows = [];

function songs(g) { return Wd.songCount(g); }

// The row `steps` away from (g, s), walking across genres (each opens with its header).
function walk(g, s, steps) {
    var genres = Wd.genreCount();
    while (steps > 0) {
        if (s + 1 < songs(g)) { s++; } else { g = (g + 1) % genres; s = -1; }
        steps--;
    }
    while (steps < 0) {
        if (s > -1) { s--; } else { g = (g + genres - 1) % genres; s = songs(g) - 1; }
        steps++;
    }
    return [g, s];
}

function stars(level) {
    if (level <= 0) return "";
    return "★" + level;
}

function refresh() {
    for (var i = 0; i < ROWS; i++) {
        var at = walk(genre, song, i - CENTER);
        var row = rows[i];
        var header = at[1] < 0;
        row.header._visible = header;
        row.idle._visible = !header && i != CENTER;
        row.focus._visible = !header && i == CENTER;
        if (header) {
            row.title.text = "◆ " + Wd.genreName(at[0]) + "  (" + songs(at[0]) + ")";
            row.level.text = "";
        } else {
            row.title.text = Wd.songTitle(at[0], at[1]);
            row.level.text = stars(Wd.songLevel(at[0], at[1], 3));
        }
    }
    genre_label();
    courses();
}

function genre_label() {
    _root.genreText.text = Wd.genreName(genre);
    _root.infoText.text = song < 0 ? "Don: open the genre" : Wd.songSubtitle(genre, song);
}

// The four course boards: levels above them, the chosen one raised, unavailable ones dim.
function courses() {
    for (var c = 0; c < 4; c++) {
        var board = _root["board" + c];
        var available = song >= 0 && Wd.hasCourse(genre, song, c);
        _root["lv" + c].text = available ? stars(Wd.songLevel(genre, song, c)) : "";
        board._alpha = mode == "music" ? (available ? 70 : 25) : (available ? 100 : 25);
    }
    _root.back._visible = mode != "music";
    _root.cursor._visible = mode != "music";
}

function move(direction) {
    var at = walk(genre, song, direction);
    genre = at[0];
    song = at[1];
    scroll += direction * ROW_HEIGHT;
    still = 0;
    Wd.drum(1);
    refresh();
}

// Frames a key has been held; -1 for a key already down when the scene opened (ignored until released).
function hold(code, held) {
    if (!Key.isDown(code)) return 0;
    return held < 0 ? -1 : held + 1;
}

// A held Ka repeats after a short delay, as on the cabinet.
function repeats(held) { return held == 1 || (held > 20 && held % 6 == 0); }

function openCourse() {
    mode = "course";
    course = 3;
    while (course > 0 && !Wd.hasCourse(genre, song, course)) course--;
    _root.titleMusic.gotoAndPlay("fadeout");
    _root.titleCourse.gotoAndPlay("fadein");
    _root.cursor.gotoAndPlay("solo_undecided");
    placeCourseCursor();
    Wd.drum(0);
    refresh();
}

function closeCourse() {
    mode = "music";
    _root.titleCourse.gotoAndPlay("fadeout");
    _root.titleMusic.gotoAndPlay("fadein");
    for (var c = 0; c < 4; c++) _root["board" + c].gotoAndStop("init");
    Wd.drum(0);
    refresh();
}

function placeCourseCursor() {
    _root.cursor._x = course < 0 ? BACK_X : COURSE_X[course];
    for (var c = 0; c < 4; c++) {
        var board = _root["board" + c];
        if (c == course) board.gotoAndPlay("top"); else board.gotoAndStop("init");
    }
}

function stepCourse(direction) {
    var next = course + direction;
    while (next >= 0 && next < 4 && !Wd.hasCourse(genre, song, next)) next += direction;
    if (next < -1 || next > 3) return;
    course = next;
    Wd.drum(1);
    placeCourseCursor();
}

function confirm() {
    if (mode == "music") {
        if (song < 0) { move(1); return; }       // a header opens onto its first song
        openCourse();
        return;
    }
    if (course < 0) { closeCourse(); return; }
    if (Wd.select(genre, song, course)) {
        mode = "starting";
        _root.cursor.gotoAndPlay("solo_decided");
        Wd.drum(0);
        refresh();
    }
}

var firstTick = true;
var hostReady = false;   // Wd.ready(): the scene's header, name board and counter are set up

function tick() {
    // Children run their own frame-0 scripts (a stop()) after ours: start their animations a tick later.
    if (firstTick) {
        firstTick = false;
        titleMusic.gotoAndPlay("fadein");
        heldLeft = Key.isDown(KEY_LEFT) ? -1 : 0;
        heldRight = Key.isDown(KEY_RIGHT) ? -1 : 0;
        heldDon = Key.isDown(KEY_DON) ? -1 : 0;
        return;
    }
    if (!hostReady) hostReady = Wd.ready();
    heldLeft = hold(KEY_LEFT, heldLeft);
    heldRight = hold(KEY_RIGHT, heldRight);
    heldDon = hold(KEY_DON, heldDon);
    if (mode == "music") {
        if (repeats(heldLeft)) move(-1);
        else if (repeats(heldRight)) move(1);
        if (heldDon == 1) confirm();
    } else if (mode == "course") {
        if (heldLeft == 1) stepCourse(-1);
        else if (heldRight == 1) stepCourse(1);
        if (heldDon == 1) confirm();
    }
    // Rows ease towards their places; the preview waits until the cursor rests.
    scroll = Math.abs(scroll) < 0.5 ? 0 : scroll * 0.7;
    for (var i = 0; i < ROWS; i++) {
        var y = LIST_Y + i * ROW_HEIGHT + scroll;
        rows[i]._y = y;
        rows[i]._x = LIST_X + (i == CENTER ? 16 : 0);
        rows[i]._visible = y > LIST_Y - 20 && y < LIST_Y + (ROWS - 1) * ROW_HEIGHT + 20;
    }
    still++;
    var key = genre + ":" + song;
    if (still == 24 && song >= 0 && previewed != key) {
        previewed = key;
        Wd.preview(genre, song);
    }
}

for (var i = 0; i < ROWS; i++) rows.push(_root.list.attachMovie("SongRow", "row" + i, i + 1));
back.label.text = "Back";
titleCourse.gotoAndStop("init");
for (var c = 0; c < 4; c++) _root["board" + c].gotoAndStop("init");

if (Wd == undefined || Wd.version() == undefined) {
    // Not Waddamburo with the Wd API (Green's executable, an older Waddamburo, a plain viewer).
    genreText.text = "This Song Select needs Waddamburo with the Wd API";
    infoText.text = "(LumenStudio\\build\\waddamburo)";
} else {
    ExternalInterface.addCallback("wdStart", this, function (g, s) {
        genre = g;
        song = s;
        refresh();
    });
    refresh();
    this.onEnterFrame = tick;
    Wd.log("Song Select PoC ready:", Wd.genreCount(), "genres");
}
