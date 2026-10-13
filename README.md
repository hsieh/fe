English | [简体中文](README_cn.md)

<p align="center">
  <img src="4.jpg" alt="fe — a Forth-powered, programmable terminal editor" width="500">
</p>

# fe — A Forth-powered, programmable terminal editor

`fe` is a terminal text editor built in and programmable with **Forth (Gforth)**. The editor and the Forth interpreter run in the **same process and share the same dictionary** — so you can reach into the editor's internal words and variables, and define new commands live.

It pairs that with a familiar, complete editing experience: **vi-style modal editing**, fuzzy-matching pickers, async live grep, an LSP client, multi-window splits, and undo/redo.

## Why fe?

Three design choices set it apart:

1. **Forth-native, not just Forth-themed.** The editor *is* a live Forth system. From the embedded REPL you can call the editor's internal words and variables directly, and `:` define new commands on the fly — not a C program with a Forth skin on top.
2. **A familiar, complete daily driver.** vi-style modal editing, LSP, live grep, fuzzy file/buffer pickers, multiple windows, undo/redo, and keyboard macros. You don't give up ordinary editor capabilities to use Forth.
3. **The editor itself is modifiable on the fly.** Configuration, command definitions, key bindings, and the extension mechanism are all Forth — they are the core of the project, not an implementation detail. Edit `~/.fe`, then press `Ctrl-x` to reload.

- Depends on **Gforth ≥ 0.7.9** plus a C compiler (only needed on first run to compile the termios/width bindings; afterwards the result is cached).
- live grep needs one of `rg`, `git`, or `grep` (`rg` is preferred; if absent it falls back to `git grep` → `grep`).
- macOS / Linux only, ANSI / xterm terminals.

---

## Installation

### 1. Install Gforth

**macOS**

```sh
brew install gforth
```

**Debian / Ubuntu**

```sh
sudo apt install gforth
```

Check the version (it must be ≥ 0.7.9; if your distro's package is too old, see "Building from source" below):

```sh
gforth --version
```

**Building from source (generic, any Linux)**

```sh
# Download gforth-0.7.9.tar.gz from https://gforth.org or a GNU mirror, extract it, then:
./configure
make
sudo make install
```

> On the first run, Gforth's libcc compiles a few small bindings (termios, wcwidth, etc.)
> using your local C compiler; the result is then cached and later startups no longer need a
> compiler. Make sure `cc`/`gcc`/`clang` is on your PATH.

### 2. Get and run fe

```sh
cd forth                # the directory containing fe.fs
./fe                    # start (fe is a symlink to fe.fs; fe.fs is a dual shell/gforth script)
./fe a.c b.txt          # open multiple files (stops on the first)

# or directly:
./fe.fs a.c
# or:
gforth fe.fs a.c
```

It's convenient to link `fe` into your PATH, e.g.:

```sh
ln -s "$PWD/fe" ~/bin/fe
```

---

## Configuration (layered: `~/.fe` + `./.fe.fs`)

At startup (and on `Ctrl-x` reload), configuration layers are loaded by priority: **`~/.fe` (user) → `./.fe.fs` (project root; the latter can override)**. Each layer is a **Forth snippet** — it can set variables and rebind keys:

```forth
4 tab-width !                          \ tab display width (default 8)
0 linenr-default !                     \ turn off line numbers by default (0=off 1=absolute 2=relative 3=hybrid)
1 regex-search !                       \ use POSIX regex for search (default 0 = literal substring)
1 lp-keep-right !                      \ pickers keep the right end of over-wide entries (default keeps head + tail …)
char Z ' cmd-qrep bind-command         \ rebind query-replace to Z (config is interpreted: use char/' instead of [char]/['])
```

Commonly configurable variables:

| Variable | Default | Meaning |
|---|---|---|
| `tab-width` | 8 | tab display width |
| `linenr-default` | 1 | global line-number mode: 0=off 1=absolute 2=relative 3=hybrid |
| `linenr-scope` | 0 | line-number scope: 0=per window 1=per buffer |
| `regex-search` | 0 | 1=search uses POSIX regex; 0=literal substring |
| `lp-keep-right` | 0 | 1=pickers keep the right end of over-wide entries |

Key rebinding uses `bind-command` (command mode), `bind-insert` (insert mode), `bind-mode` (any mode).
A configuration error in one layer is isolated (reported on the status line; it doesn't break the other layers).

---

## Default key bindings

### Command mode (NORMAL; this is the startup mode)

| Key | Action |
|---|---|
| `h` / `l` | move cursor left / right (whole characters, no line crossing) |
| `k` / `j` | move cursor up / down (logical lines, keeping the target column) |
| `←` `→` `↑` `↓` | arrow-key movement |
| `Home` / `End` | start / end of line |
| `PgUp` / `PgDn` / `Ctrl-f` / `Ctrl-b` | page up / down |
| `0` / `$` | start / end of line |
| `w` / `b` / `e` | next word start / previous word start / word end |
| `gg` / `G` | start / end of file |
| `Ctrl-e` / `Ctrl-y` | scroll viewport down / up one line (cursor follows only if it leaves the viewport) |
| `zz` / `zt` / `zb` | scroll cursor to viewport center / top / bottom (without moving the cursor) |

| Key | Action |
|---|---|
| `i` | enter insert mode before the cursor |
| `a` | enter insert mode after the cursor |
| `I` | enter insert mode at the first non-blank of the line |
| `A` | enter insert mode at end of line |
| `o` / `O` | open a new line below / above and enter insert mode (auto-indent) |
| `x` | delete the character after the cursor |
| `Delete` | delete the character after the cursor |
| `u` / `Ctrl-r` | undo / redo |
| `p` / `P` | paste after / before the cursor |
| `v` | enter / exit visual selection (movement keys extend the selection, `ESC` cancels) |
| `y` | yank: visual = selection; otherwise `y`+motion (`yy` = whole line) |
| `d` | delete: visual = selection; otherwise `d`+motion (`dd` = whole line) |
| `c` | change: visual = delete selection + insert; otherwise `c`+motion (`cc` = whole line, `cw`/`c$`/`c%`) |
| `C` | change to end of line (same as `c$`) |
| `D` | delete to end of line (same as `d$`) |
| `%` | jump to the matching bracket (press again to jump back) |
| `=` | format the current buffer with an external formatter |

| Key | Action |
|---|---|
| `/` / `?` | forward / backward search (incremental; `n`/`N` next/previous, `~` toggles ignore-case) |
| `E` | query-replace (qemacs style, single-key confirmation per match) |
| `Q` / `@` | start/stop recording a keyboard macro / replay the macro |
| `Ctrl-s` | save |
| `Ctrl-l` | redraw |
| `Ctrl-x` | reload configuration (`~/.fe`/`./.fe.fs`/key bindings) |
| `q` | quit (asks for confirmation if there are unsaved changes) |

### Prefix keys

| Prefix | Second key |
|---|---|
| `g` | `g`=start of file, `d`=go to definition, `y`=go to type definition, `r`=references, `i`=implementation, `s`=signature, `b`=jump back |
| `z` | `z`=center, `t`=top, `b`=bottom |
| `Ctrl-w` | window commands (see "Multiple windows") |

An unmatched second key cancels the prefix mode.

### Insert mode (entered with `i`/`a`)

| Key | Action |
|---|---|
| any printable character | insert it (including `q`/`h`/`j`/`k`/`l`/`x`; multibyte characters) |
| `Tab` | word completion (inserts a tab if there is no word prefix) |
| `Backspace` / `DEL` | delete the character before the cursor |
| `Delete` | delete the character after the cursor |
| `Enter` | newline (copies the current line's leading indentation) |
| `←` `→` `↑` `↓` / `Home` / `End` | movement |
| `PgUp` / `PgDn` / `Ctrl-f` / `Ctrl-b` | page up / down |
| `Ctrl-s` | save |
| `Ctrl-x` | reload configuration |
| `ESC` | return to command mode |

---

## Main features

### live grep (`\`)

Press `\` to open the async grep picker: each keystroke re-runs `rg` (falls back to `git grep` → `grep` when `rg` is absent).

- `Enter`: jump to the selected result (to the matched column)
- `Ctrl-g`: dump the current results into the `*grep*` buffer (`path:line:` prefix highlighted, matched text highlighted in green, case-insensitive); press `Enter` inside `*grep*` to jump
- `ESC`: cancel
- After `Ctrl-g` the results go into `*grep*`, where you can keep browsing or use `n`/`N`

### File / buffer pickers

- `Ctrl-o`: file picker (recursively lists files, filters as you type, `Enter` opens)
- `L`: buffer picker (filters as you type, `Enter` switches, `Delete` closes)

Both pickers use fuzzy matching: space-separated terms, AND, sorted by score, with matched characters highlighted in green.

<p align="center">
  <img src="fzy_demo.png" alt="fuzzy file/buffer picker" width="640">
</p>

### Embedded Forth REPL (`Ctrl-g`)

`Ctrl-g` opens/switches to the `*repl*` buffer — not a shell, but the editor's own Forth interpreter (same process, same dictionary), so you can directly access fe's words and variables and define new commands with `:`.

Inside `*repl*`:

| Key | Action |
|---|---|
| any character | insert Forth source |
| `Enter` | evaluate the current line; result/error is appended to the buffer |
| `Tab` | complete a Forth word |
| `↑` / `↓` | history replay |
| `Backspace` / `DEL` | delete character |
| `ESC` | return to command mode |

In other buffers (command mode): `!` evaluates an expression, `Ctrl-j` evaluates the current line/selection — results are appended to `*repl*`.

The REPL also supports `locate <word>` / `view <word>` (open the source file centered on the word's definition), `where <word>` / `browse <substring>` (multi-result picker), and `see <word>` (disassembly).

<p align="center">
  <img src="repl_demo.png" alt="embedded Forth REPL" width="640">
</p>

### LSP client (`Ctrl-t`)

`Ctrl-t` starts a language server for the current file (gopls / pylsp / rust-analyzer / typescript-language-server / clangd, etc., mapped by file extension).

- Completion: insert-mode `Tab` dispatches to LSP (candidate menu with kind icons + detail + live preview, ↑↓ switches and inserts instantly)
- Diagnostics: error/warn/info/hint squiggle highlighting
- Navigation: `gd` definition, `gy` type, `gr` references, `gi` implementation (multi-result picker with `path:line:content`); `gb` jumps back
- Hover / signature: `K` hover, `gs` signature

<p align="center">
  <img src="lsp_demo.png" alt="LSP completion and diagnostics" width="640">
</p>

### Multiple windows (`Ctrl-w`)

`Ctrl-w` enters window commands:

| Key | Action |
|---|---|
| `v` / `s` | vertical / horizontal split |
| `w` / `Ctrl-w` | focus next window |
| `h` / `j` / `k` / `l` | directional focus |
| `c` | close the current window |

Each window has its own cursor / viewport / mode (`:split` semantics).

### Search / query-replace / macros

- Search: `/` `?` incremental search, `n`/`N` repeat, `~` toggles ignore-case, matches highlighted in reverse video.
- Query-replace (`E`): per match `y` replace / `n` skip / `!` all / `.` replace and stop / `q`/`Enter`/`ESC` stop.
- Keyboard macros: `Q` record, `@` replay (single register, records key sequences).

### undo / redo

`u` undo, `Ctrl-r` redo. Based on an operation log; consecutive edits of the same kind are merged (typing one word = one undo step).

### Quit protection

If there are unsaved changes, `q` doesn't exit directly; the status line shows:

- press `q` again → discard and quit
- `Ctrl-s` → save and quit
- any other key → cancel

---

## Configuration example

```forth
\ ~/.fe example
4 tab-width !               \ tab width 4
0 linenr-default !          \ turn off line numbers by default
char Z ' cmd-qrep bind-command   \ rebind query-replace to Z
```

---

## License

Except for dependency-related code, all code in this project is released into the **public domain** (see `LICENSE`, the Unlicense text).

Dependencies and third-party components (each under its own license): Gforth, `rg`/`git`/`grep`, the various LSP servers, external formatters (gofmt/rustfmt/prettier/clang-format, etc.), and `vendor/cJSON` (see `vendor/cJSON-LICENSE`).
