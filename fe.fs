#! /bin/sh
: ## ; 0 [if]
# fe - minimal Forth editor (embedded same-process Forth REPL)
# deps: gforth 1.0 (0.7.9 may also work); first run needs a C compiler (libcc compiles termios bindings, then cached)
# usage: fe [file...]  (or via symlink: ln -s ~/local/fe/fe.fs ~/bin/fe)
# dual interpretation: shell runs first (resolve symlink + locate dir + pick gforth-fast + exec self),
# then Gforth re-reads the same file; the hash-bang line is a comment alias, and the
# conditional-skip pair below makes Gforth ignore the shell part.
self=$0
while [ -L "$self" ]; do
    link=$(readlink "$self")
    case "$link" in
        /*) self=$link ;;
        *)  self=$(dirname "$self")/$link ;;
    esac
done
DIR=$(CDPATH= cd -- "$(dirname -- "$self")" && pwd)
# no cd: keep caller's CWD. add script dir to include search path via fpath+.
GF=gforth
command -v gforth-fast >/dev/null 2>&1 && GF=gforth-fast
# gforth 1.0's libcc prints speculative dlopen probes to stderr at startup; capture stderr
# and replay it only if the editor exits abnormally (normal quit discards the noise).
FEERR=$(mktemp /tmp/fe-err.XXXXXX) 2>/dev/null || FEERR=/dev/null
trap '[ "$FEERR" != /dev/null ] && rm -f "$FEERR"' EXIT
"$GF" -e "2variable fe-incdir  s\" $DIR\" fe-incdir 2!" \
      -e "fpath+ \"$DIR\"" "$self" "$@" 2>"$FEERR"
rc=$?
if [ $rc -ne 0 ] && [ "$FEERR" != /dev/null ]; then
    cat "$FEERR" >&2
fi
exit $rc
[then]
\ ===== fe.fs - minimal editor (Forth Editor)=====
\ phases 1-3: file I/O + cursor + command set + REPL loop
\ Depends on: gapbuf.fs (gap buffer), rawmode.fs (terminal raw mode)

\ temp-file helper: /tmp/fe-<base>.<pid> (safe across instances; each process has its own file)
create tmp-buf 256 allot
variable tmp-len 0 tmp-len !
\ append an unsigned decimal to tmp-buf; arithmetic only — <# #s #> would clobber the shared hold area,
\ which may hold a user string left on the REPL stack across evaluations
\ iterative, no locals, no recurse (deep-chain safe: locals/recurse corrupt the return stack)
create tpu-buf 24 allot
variable tpu-i 0 tpu-i !
variable tpu-n 0 tpu-n !
: tmp-ud ( n -- )
  tpu-n !
  0 tpu-i !
  begin
    tpu-n @ 10 mod 48 +  tpu-buf tpu-i @ + c!
    1 tpu-i +!
    tpu-n @ 10 / tpu-n !
    tpu-n @ 0= until
  begin tpu-i @ 0> while
    -1 tpu-i +!
    tpu-buf tpu-i @ + c@  tmp-buf tmp-len @ + c!
    1 tmp-len +!
  repeat ;
: tmp-file { a u -- ta tu }
  s" /tmp/fe-" tmp-buf swap move  8 tmp-len !
  a tmp-buf tmp-len @ + u move  u tmp-len +!
  46 tmp-buf tmp-len @ + c!  1 tmp-len +!      \ '.'
  getpid tmp-ud
  tmp-buf tmp-len @ ;

\ startup warning redirect: warnings such as redefined produced while including modules go into a temp file first,
\ the fe entry point reads them back into *messages* (avoids warnings flashing on the terminal at startup).
variable fe-warn-fid  0 fe-warn-fid !
variable fe-warn-saved 0 fe-warn-saved !
s" start-warn" tmp-file  w/o create-file throw fe-warn-fid !
debug-fid fe-warn-saved !
fe-warn-fid @ TO debug-fid

include state.fs
include gapbuf.fs
variable tab-width  8 tab-width !   \ configurable: tab display width (tab stop); used by utf8.fs rendering
include utf8.fs
include rawmode.fs
include keywrap.fs
require ansi.fs

\ ===== state (cursor/viewport in wctx, content/metadata in gctx; see state.fs)=====
variable tmp-c       \ temporary character (for insertion)
variable confirm-quit  0 confirm-quit !   \ quit confirmation pending
variable vert      0 vert !              \ whether this round moved vertically
variable wp        0 wp !                \ temporary candidate position for word/file actions
variable wc        0 wc !                \ temporary character class for word/file actions

include undo.fs

\ ===== character access is gapbuf.fs g-char-at =====

\ ===== offset <-> line/col (column = display width, not bytes; accelerated by the line index)=====
\ start byte offset of line (returns g-size when the line does not exist)
: line-start { line }
  lix-build
  line 0< if 0 exit then
  line lix-n @ < if  line lix@  else  g-size  then ;

\ byte length of line (excluding the newline)
: line-length { line }
  line line-start { ls }
  line 1+ line-start { ne }
  ne ls > if
    ne 1- g-char-at 10 = if ne 1- ls - else ne ls - then
  else
    0
  then ;

\ display width of line
: line-dwidth { line }
  line line-start { off }
  line line-length { len }
  off len u8-width ;

\ binary search for the line containing offset
: offset-line { offset }
  lix-build
  0 { lo }  lix-n @ { hi }
  begin lo hi < while
    lo hi + 2/ { mid }
    mid lix@ offset <= if mid 1+ to lo else mid to hi then
  repeat
  lo 1- ;

: offset->linecol { offset }
  offset offset-line { line }
  line line-start { ls }
  ls  offset ls -  dup  u8-col
  line swap ;

: linecol->offset { line dcol }
  line line-start { off }
  line line-length { len }
  off len dcol u8-bi  off + ;


\ ===== file I/O =====
\ EOL: on load normalize \r\n to \n (remembering the original format), restore it on save
: eol-normalize { addr len }
  0 { j } 0 { i }
  begin i len < while
    addr i + c@ 13 = if
      i 1+ len < if
        addr i 1+ + c@ 10 = if
          1 eol-crlf !
          i 1+ to i                     \ skip the \r
        then
      then
    then
    addr i + c@  addr j + c!
    i 1+ to i   j 1+ to j
  repeat
  j ;

: eol-expand { addr len }
  0  len 0 ?do  i addr + c@ 10 = if 1+ then  loop  { extra }   \ count the number of \n
  len extra + allocate throw { buf2 }
  0 { j }
  len 0 ?do
    addr i + c@ { b }
    b 10 = if 13 buf2 j + c!  j 1+ to j then            \ insert \r before \n
    b buf2 j + c!  j 1+ to j
  loop
  buf2  len extra + ;

defer file-loaded-xt                        \ ( -- ) called after load-file completes (lsp uses it to re-bind + fetch diagnostics)
' noop is file-loaded-xt

: load-file { addr len }
  addr len r/o open-file if drop exit then
  { fh }
  fh file-size throw d>s { fsize }
  fsize allocate throw { buf }
  buf fsize fh read-file throw drop
  fh close-file throw
  0 eol-crlf !
  buf fsize eol-normalize { blen }
  0 g-move-gap
  1 loading? !
  buf blen g-insert
  0 loading? !
  buf free throw
  0 cursor !
  0 top-line !
  0 top-sub !
  0 modified !
  file-loaded-xt ;

: save-file { addr len }
  g-contents { buf blen }
  eol-crlf @ if
    buf blen eol-expand { buf2 blen2 }
    buf free throw
    buf2 to buf   blen2 to blen
  then
  addr len w/o create-file throw { fh }
  buf blen fh write-file throw
  fh close-file throw
  buf free throw
  urp @ saved-urp !
  0 modified ! ;

\ ===== frame buffer (double buffering: current frame + previous-frame shadow, used for line-level incremental diff)=====
variable fb    0 fb !       \ current frame
variable fcap  0 fcap !
variable fblen 0 fblen !
variable pb    0 pb !       \ previous frame (shadow)
variable pcap  0 pcap !
variable pblen 0 pblen !

2048 constant max-rows
create rowcur  max-rows cells allot   \ per-line start offset of the current frame
create rowprev max-rows cells allot   \ per-line start offset of the shadow frame
variable nrows  0 nrows !
variable pnrows 0 pnrows !

: buf-ensure { bufv capv n -- }
  n capv @ u<= if exit then
  bufv @ 0<> if bufv @ free throw then
  n 2* 4096 max  dup capv !  allocate throw bufv ! ;

: fb-ensure ( n -- )
  dup fb fcap rot buf-ensure
  dup pb pcap rot buf-ensure
  drop ;

: fb-reset  0 fblen !  0 nrows ! ;
: fb-c ( c -- )  fb @ fblen @ + c!  1 fblen +! ;
: fb-str { addr len -- }  addr fb @ fblen @ + len move  len fblen +! ;
: fb-ud. { n -- }                              \ append an unsigned decimal
  n 10 >= if n 10 / recurse then
  n 10 mod 48 + fb-c ;
\ append a cursor position (\e[row;colH; row/col 0-based)
: fb-at-xy { col row -- }
  27 fb-c  91 fb-c  row 1+ fb-ud.  59 fb-c  col 1+ fb-ud.  72 fb-c ;

\ ===== viewport (counted in visual lines, supports soft wrapping)=====
variable scr-cols  80 scr-cols !
variable scr-rows  24 scr-rows !
variable sc-old-cols 80 sc-old-cols !
variable sc-old-rows 24 sc-old-rows !
variable size-changed 0 size-changed !      \ this redraw detected a terminal resize

\ current window geometry (win-x/win-y = window top-left, win-cols/win-rows = width/height).
\ with a single window it is the full-screen text area; after splitting, layout-windows fills it per window rectangle.
\ the text render/scroll path only reads these four (not the full-screen scr-cols/scr-rows).
variable win-x     0 win-x !
variable win-y     0 win-y !
variable win-cols 80 win-cols !
variable win-rows 24 win-rows !
variable windows-first 0 windows-first !   \ window linked-list head (leaf iteration)
variable windows-root  0 windows-root !    \ split-tree root
variable win-active 0 win-active !         \ active window (saved/restored during rendering)
variable win-render-active 0 win-render-active !   \ 1 while rendering the active window (the overlay is drawn only once)
variable win-status-on? 0 win-status-on? ! \ 1 with multiple windows: each window draws its own statusline
8 constant max-windows
0 constant WK-LEAF
1 constant WK-VSPLIT
2 constant WK-HSPLIT
variable wc-n 0 wc-n !

\ count windows (leaf count)
: win-count ( -- n )
  0 wc-n !
  windows-first @ begin dup while 1 wc-n +! W:NEXT + @ repeat drop
  wc-n @ ;

\ text-area rows = window height - statusline (1 with multiple windows, 0 with a single window)
: win-text-rows ( -- n )  win-rows @  win-status-on? @ - ;

\ refresh the geometry cache from wctx's rectangle fields
: win-geometry ( -- )
  wctx @ W:X2 + @  wctx @ W:X1 + @ - 1+  win-cols !
  wctx @ W:Y2 + @  wctx @ W:Y1 + @ - 1+  win-rows !
  wctx @ W:X1 + @  win-x !
  wctx @ W:Y1 + @  win-y ! ;

\ ( w x1 y1 x2 y2 -- ) set a leaf's rectangle
: win-set-rect { w x1 y1 x2 y2 -- }
  x1 w W:X1 + !  y1 w W:Y1 + !
  x2 w W:X2 + !  y2 w W:Y2 + ! ;

\ recursive layout: assign the rectangle (x1 y1 x2 y2) to the subtree rooted at node n
: win-layout-node { n x1 y1 x2 y2 }
  0 0 0 { half rx ry }
  n x1 y1 x2 y2  win-set-rect          \ the node's own rectangle (used by leaves; splits use it for the divider)
  n W:KIND + @ 0= if exit then
  n W:KIND + @ 1 = if
    \ vertical: left (x1..x1+half-1), right (x1+half+1..x2), one divider column in between
    x2 x1 - 1+ 2 / to half
    x1 half + 1+ to rx
    n W:LEFT + @   x1     y1  x1 half + 1-  y2  recurse
    n W:RIGHT + @  rx     y1  x2            y2  recurse
  else
    \ horizontal: top (y1..y1+half-1), bottom (y1+half..y2), no divider (the reverse-video statusline acts as the divider)
    y2 y1 - 1+ 2 / to half
    y1 half + to ry
    n W:LEFT + @   x1  y1     x2  y1 half + 1-  recurse
    n W:RIGHT + @  x1  ry     x2  y2          recurse
  then ;

\ layout: recursively assign rectangles over the split tree; root = windows-root. The global status line occupies the bottom row scr-rows (outside all windows).
: layout-windows ( -- )
  windows-root @ 0 0  scr-cols @ 1-  scr-rows @ 1-  win-layout-node
  win-count 1 > 1 and  win-status-on? !   \ store 1/0 (not -1/0); win-text-rows subtracts it
  win-geometry ;

\ whether another window (≠ wctx) still shows buffer b
: other-window-shows? { b -- flag }
  windows-first @
  begin dup while
    dup wctx @ <> if
      dup W:B + @ b = if drop true exit then
    then
    W:NEXT + @
  repeat
  drop false ;

: update-screen-size
  term-cols 16 max 2000 min  scr-cols !              \ clamp: avoid negative/overflow
  term-rows 3 max 1-  max-rows 2 - min  scr-rows !   \ otherwise nrows>256 would write past rowcur/rowprev
  0 size-changed !
  scr-rows @ sc-old-rows @ <>  scr-cols @ sc-old-cols @ <>  or if
    1 size-changed !
    0 pnrows !                                       \ shadow frame invalidated -> full redraw
  then
  scr-rows @ sc-old-rows !  scr-cols @ sc-old-cols !
  layout-windows ;

\ digit count (at least 1)
: ndigits { n }  1 begin n 10 >= while n 10 / to n 1+ repeat ;

\ line-number gutter (width varies with the visible line numbers)
variable gutter-w  4 gutter-w !
variable gutter-w-xt    \ ( base -- w ) adjust the gutter width (0 = hide line numbers)
: gutter-w-default ( base -- base ) ;
' gutter-w-default gutter-w-xt !
variable text-cols 76 text-cols !
\ nlines / nlines-sz are buffer fields (state.fs)
: update-nlines
  g-size nlines-sz @ <> if  g-nlines nlines !  g-size nlines-sz !  then ;
: update-gutter
  update-nlines
  nlines @ ndigits 1+  gutter-w-xt @ execute  gutter-w !                 \ by the total line count (stable, does not jump while scrolling)
  win-cols @ gutter-w @ - 1 max  text-cols ! ;     \ clamp: a negative text-cols would make the chunks division blow up

\ output a number right-aligned into the frame (width width)
: fb-num-pad { num width }
  width num ndigits - 0 max 0 ?do 32 fb-c loop
  num fb-ud. ;

\ visual lines of display width w under a text-cols width (at least 1)
: chunks { w }
  w 0= if 1 exit then
  w text-cols @ 1- + text-cols @ / ;

\ visual lines of a line (the actual segment count: wide characters wrap early at a segment end, one more than the chunks estimate)
: line-vrows { line -- n }
  line line-start { off }
  line line-length { lw }
  0 { s } 0 { b } 0 { c }
  begin b lw < while
    off b + c u8-wl { cw cn }
    c 0> c cw + text-cols @ > and if s 1+ to s 0 to c then
    c cw + to c
    b cn + to b
  repeat
  s 1+ ;

\ visual line number of display column dcol + column within the segment (consistent with render-seg segmentation)
: dcol->sub-col { line dcol }
  line line-start { loff }
  line line-length { lw }
  0 { sub } 0 { b } 0 { c } 0 { acc } 0 { seg0 }
  begin b lw <  acc dcol <=  and while
    loff b + acc u8-wl { cw cn }
    c 0>  c cw + text-cols @ >  and if
      sub 1+ to sub  0 to c  acc to seg0
    then
    c cw + to c
    acc cw + to acc
    b cn + to b
  repeat
  sub  dcol seg0 - ;

\ start (bi, dcol0) of visual segment sub; consistent with dcol->sub-col / render-seg segmentation
: sub->seg { off lw sub -- bi dcol0 }
  0 { s } 0 { b } 0 { c } 0 { segbi } 0 { segcol }
  begin b lw <  s sub <  and while
    off b + c u8-wl { cw cn }
    c 0>  c cw + text-cols @ >  and if
      s 1+ to s
      b to segbi  c to segcol
      0 to c
    then
    c cw + to c
    b cn + to b
  repeat
  segbi segcol ;

\ compute the visual line of (line,sub) relative to the viewport top (<0 means above the viewport)
: vrow-of { line sub }
  line top-line @ < if -1 exit then
  line top-line @ = if sub top-sub @ - exit then
  0 { acc }
  top-line @ line-vrows top-sub @ -  acc +  to acc
  line top-line @ 1+ ?do  i line-vrows acc +  to acc  loop
  acc sub + ;

\ move the viewport top down by d visual lines
: advance { d -- }
  begin d 0> while
    top-line @ line-vrows { vr }
    vr top-sub @ - { rem }
    d rem < if
      top-sub @ d + top-sub !
      0 to d
    else
      rem negate d + to d
      top-line @ 1+ top-line !
      0 top-sub !
    then
  repeat ;

\ walk back n visual lines from (line,sub) to get the new top-line/top-sub (O(viewport lines))
: scroll-up-by { line sub n -- }
  line { l }  sub { s }  n { rem }
  begin
    rem 0>  l 0>  and
  while
    s 0> if
      s 1- to s
    else
      l 1- to l
      l line-vrows 1- to s
    then
    rem 1- to rem
  repeat
  l top-line !  s top-sub ! ;

\ cursor (line,sub) below the viewport: walk back win-rows-1 visual lines to get the new top-line/top-sub.
: scroll-below { line sub -- }
  line sub win-text-rows 1- scroll-up-by ;

\ ensure the cursor is inside the viewport (scroll if necessary)
: ensure-visible
  cursor @ offset->linecol { cl col }
  cl col dcol->sub-col { csub incol }
  \ above (O(1))
  cl top-line @ <  cl top-line @ = csub top-sub @ < and or if
    cl top-line !  csub top-sub !  exit
  then
  \ clearly below (cl is at least win-text-rows lines past top-line) → position directly by walking back
  cl top-line @ win-text-rows + > if
    cl csub scroll-below  exit
  then
  \ inside the viewport or just below (distance <= win-text-rows) → compute precisely
  cl csub vrow-of { srow }
  srow win-text-rows >= if srow win-text-rows - 1+ advance then ;

\ scroll the cursor line to the viewport center (viewport-only, does not move the cursor; naturally clamps at the file head/tail, same as zz)
: center-cursor ( -- )
  cursor @ offset->linecol { cl col }
  cl col dcol->sub-col { csub incol }
  cl csub win-text-rows 2 / scroll-up-by ;

\ ===== transient messages (shown on the status line; writing them directly to the terminal would pollute the screen)=====
create msgbuf 256 allot
variable msglen  0 msglen !
: msg! { addr len }   len 255 min msglen !   addr msgbuf msglen @ move ;
: msg+ { addr len }   len 255 msglen @ - min { n }   addr msgbuf msglen @ + n move   msglen @ n + msglen ! ;
: msg-clear  0 msglen ! ;

\ ===== message/debug buffers (*messages* / *trace*)=====
variable messages-buf  0 messages-buf !
variable trace-buf     0 trace-buf !
create nl-buf 1 allot  10 nl-buf c!      \ newline (buffer)

\ append to the end of a buffer (temporarily switch gctx, without touching the window)
: buffer-append { b addr len -- }
  gctx @ { saved-g }
  b gctx !
  g-size g-move-gap
  addr len g-insert
  saved-g gctx ! ;

: msg-log { addr len -- }   \ write *messages* (one line per entry, automatic newline)
  messages-buf @ { b }
  b 0<> if  b addr len buffer-append  b nl-buf 1 buffer-append  then ;

: trace { addr len -- }     \ write *trace*; skip while *trace* is being viewed (anti-recursion)
  trace-buf @ { b }
  b 0<> gctx @ b <> and if  b addr len buffer-append  then ;

: msg-say { addr len -- }   \ transient message + record into *messages*
  addr len msg!
  addr len msg-log ;

\ ===== exception → *trace* =====
variable quit-flag  0 quit-flag !
: trace-exception { n -- }        \ "exception: <message>\n" written to *trace*
  s" exception: " trace
  n error$ trace
  nl-buf 1 trace ;

\ ===== runtime warning routing (flag-gated, no polling, no fd leak)=====
\ Gforth warnings go through ?warning -> >stderr -> debug-fid. At runtime debug-fid points at a temp file;
\ the ?warning override sets warn-pending, and idle-poll drains the file into *messages* ONLY when that flag
\ is set. (The first attempt drained every 30ms and leaked fds; this one only touches the file on a real warning.)
variable warn-pending 0 warn-pending !
variable rt-warn-fid 0 rt-warn-fid !
: rt-warn-drain ( -- )
  warn-pending @ 0= if exit then
  0 warn-pending !
  rt-warn-fid @ 0= if exit then
  rt-warn-fid @ flush-file drop
  rt-warn-fid @ close-file throw                   \ close the write fd (fixes the fd leak)
  0 rt-warn-fid !
  s" rt-warn" tmp-file r/o open-file throw { rf }
  rf slurp-fid { a u }
  rf close-file throw
  u 0> if
    a u s" no current location" search nip nip if
      2drop                                       \ drop the spurious locate warning
    else
      a u msg-log
    then
    a free throw
  then
  s" rt-warn" tmp-file delete-file throw
  s" rt-warn" tmp-file w/o create-file throw  rt-warn-fid !
  rt-warn-fid @ TO debug-fid ;



\ ===== Mode structure (modeled on vis's Mode)=====
\ each field is 1 cell:
\  0 keymap    keymap pointer
\  1 parent    parent Mode (keymap fallback), or 0
\  2 input     default handling for unmatched keys ( c -- ), or 0
\  3 enter     enter hook ( -- ), or 0
\  4 leave     leave hook ( -- ), or 0
\  5 status    xt of the status-line string ( -- addr u ), or 0
\  6 flags     bit0 = insert mode (bar cursor)
10 constant /mode
1 constant MF-INSERT

: m-keymap  ( m -- a )  ;
: m-parent  ( m -- m )  cell+ ;
: m-input   ( m -- xt ) 2 cells + ;
: m-enter   ( m -- xt ) 3 cells + ;
: m-leave   ( m -- xt ) 4 cells + ;
: m-status  ( m -- xt ) 5 cells + ;
: m-flags   ( m -- a )  6 cells + ;
: m-insert? ( m -- f )  m-flags @ MF-INSERT and 0<> ;

\ cur-mode / prev-mode: per-window (the active window's mode + prefix-mode stack)
: cur-mode   wctx @ W:MODE + ;
: prev-mode  wctx @ W:PREVMODE + ;

: mode-set { m -- }                       \ switch to m: run the old mode's leave first, then the new mode's enter
  cur-mode @ ?dup if  m-leave @ ?dup if execute then  then
  m cur-mode !
  m m-enter @ ?dup if execute then ;
: mode-push { m -- }  cur-mode @ prev-mode !  m mode-set ;
: mode-pop  prev-mode @ ?dup if mode-set then ;
: cur-insert? ( -- f )  cur-mode @ m-insert? ;
\ status-line string of the current mode (0 0 if none); the status field is an xt ( -- addr u )
: mode-status$ ( -- addr u )
  cur-mode @ m-status @ ?dup if execute else 0 0 then ;

\ ===== status line / render hooks =====
\ Hooks: called by core, replaceable by custom. Invoked indirectly through xt variables (Forth binds early; redefinition does not change already-compiled call sites).
\ Hooks: called by core, replaceable by custom. Invoked indirectly through xt variables (Forth binds early; redefinition does not change already-compiled call sites).
variable status-line-xt        \ ( -- )           draw the whole status line (combined statusline+minibuffer for a single window)
variable win-status-xt        \ ( -- )           per-window statusline (multiple windows); must set ws-w = content display width
variable minibuf-xt          \ ( -- )           global minibuffer (multiple windows: mode + transient message)
variable showcmd-xt         \ ( -- addr len )  showcmd string (0 0 = none); core right-aligns it
variable ws-w       0 ws-w !   \ content display width filled by win-status-xt (core uses it to pad spaces)
variable hl-xt                 \ ( a -- hlend attr ) end offset + style of the highlight segment at a (a 0 if none)
variable cursor-place-xt       \ ( -- flag )     true = the cursor has already been positioned
variable buffer-enter-xt       \ ( -- )  called after buffer-switch changes buffers (0 = none)
variable key-pre-xt           \ ( c -- ) called before each key dispatch (for observation/recording; 0 = none)
variable last-key  0 last-key !   \ the key currently being dispatched (custom's generic prefix enter uses it to look up key→mode)
variable panel-xt             \ ( i -- ) the i-th picker row above the status line; 0 = none
variable panel-rows           \ picker row count (the buffer render yields this many rows)
variable panel-attr  <A Blue >BG White >FG Bold A> panel-attr !   \ selected row (fzf cursor row)
variable match-attr  <A Green >FG A> match-attr !               \ matched-character highlight (fzf hl: green)
variable match-sel-attr  <A Blue >BG Green >FG Bold A> match-sel-attr !  \ matched characters on the selected row (fzf hl+: bold green on blue)
variable bracket-attr  <A Magenta >BG Dim A> bracket-attr !       \ bracket match: paired brackets (magenta background + dim)
variable sym-attr  <A Cyan >FG Underline A> sym-attr !           \ symbol highlight in refs picker content (cyan underline, distinct from the green match)
variable status-attr  Invers status-attr !                        \ active window statusline bar (bright reverse)
variable status-inactive-attr  Invers Dim or status-inactive-attr !   \ inactive window statusline bar (reverse + dim; SGR2 ignored by rxvt/old VTE — override via theme)
variable shutdown-xt  0 shutdown-xt !   \ ( -- ) pre-exit hook (lsp uses it to shut down the server); 0 = none
variable overlay-xt    0 overlay-xt !     \ ( i -- ) draw overlay row i (i∈[0,overlay-rows)); 0 = no overlay
variable overlay-rows  0 overlay-rows !   \ overlay row count (0 = not shown)
variable overlay-vrow  0 overlay-vrow !   \ overlay anchor screen row (in buffer visual-row coordinates)
variable overlay-vcol  0 overlay-vcol !   \ overlay anchor column (display column, 0-based)
variable overlay-width 0 overlay-width !  \ overlay box width (display columns)
variable overlay-float?  1 overlay-float? !   \ 1 = overlay on top (default); 0 = push-down insertion
variable key-inter-xt  0 key-inter-xt !   \ ( c -- f ) key interceptor; true = swallow the key and skip the keymap
defer idle-poll        \ ( -- ) called on each idle poll (lsp uses it: debounce didChange)

\ default: minimal status line (name/mode/position/[modified]); custom replaces it with the full version (with message/search/prompt/confirmation)
: status-line-default
  32 fb-c
  gctx @ B:NAMELEN + @ 0> if  gctx @ B:NAME +  gctx @ B:NAMELEN + @  else s" [no name]" then  fb-str
  s"   " fb-str
  mode-status$ fb-str
  s"   " fb-str
  cursor @ offset->linecol { cl col }
  cl 1+ fb-ud.  58 fb-c  col 1+ fb-ud.
  bflags @ BF_NOFILE and 0= if  modified @ if s"   [modified]" fb-str then then ;
\ per-window statusline: name + line:col + [modified] (no mode); records ws-w for core padding
: win-status-default ( -- )
  0 ws-w !
  32 fb-c  1 ws-w +!
  gctx @ B:NAMELEN + @ 0> if
    gctx @ B:NAME +  gctx @ B:NAMELEN + @  2dup fb-str  x-width ws-w +!
  else
    s" [no name]" fb-str  9 ws-w +!
  then
  s"   " fb-str  2 ws-w +!
  cursor @ offset->linecol { cl col }
  cl 1+ fb-ud.  cl 1+ ndigits ws-w +!
  58 fb-c  1 ws-w +!
  col 1+ fb-ud.  col 1+ ndigits ws-w +!
  bflags @ BF_NOFILE and 0= if  modified @ if s"   [modified]" fb-str  12 ws-w +! then then ;
\ global minibuffer: by default only shows the mode (transient messages are appended by custom overrides)
: minibuf-default ( -- )  mode-status$ fb-str ;
: showcmd-default ( -- a u )  0 0 ;
: hl-default ( a -- hlend attr )  1+ 0 ;
: cursor-place-default ( -- flag ) 0 ;

\ gutter hook: core only reserves gutter-w width and calls it; custom decides what to draw
variable gutter-xt      \ ( line sub -- )  line = logical line number (0-based), sub = 0 first line / >0 soft-wrap continuation
: gutter-default { line sub -- }             \ default: right-aligned absolute line number + space; blank for continuation lines
  sub 0= if  line 1+ gutter-w @ 1- fb-num-pad  32 fb-c
        else  gutter-w @ 0 ?do 32 fb-c loop
  then ;
' gutter-default   gutter-xt !

' status-line-default  status-line-xt !
' win-status-default   win-status-xt !
' minibuf-default      minibuf-xt !
' showcmd-default      showcmd-xt !
' hl-default          hl-xt !
' cursor-place-default cursor-place-xt !

\ ===== display (visual-line rendering + soft wrapping + status line)=====
\ whether row i differs from the shadow frame
: rows-differ? { i -- flag }
  i pnrows @ >= if true exit then
  i cells rowcur + @ { cs }
  i 1+ nrows @ < if i 1+ cells rowcur + @ else fblen @ then { ce }
  i cells rowprev + @ { ps }
  i 1+ pnrows @ < if i 1+ cells rowprev + @ else pblen @ then { pe }
  ce cs - { clen }  pe ps - { plen }
  clen plen <> if true exit then
  clen 0= if false exit then
  fb @ cs + clen  pb @ ps + plen  compare 0<> ;

\ output only changed rows (row content already embeds at-xy positioning; no extra positioning needed)
\ emit \e[0m before each row: the incremental diff re-emits only some rows; without the reset, styles can leak across rows (occasional)
\ when panel-dirty=1, only diff "the union of the picker regions + the status line" (the text area is untouched, eliminating buffer flicker on picker interaction):
\   range = [min(previous-frame picker start, current-frame picker start), nrows), i.e. from the earlier picker start of the two frames to the end
\ but if the frame shadow is already invalid (pnrows=0, e.g. after a picker Enter jump causes a buffer-switch full redraw), a full diff is required,
\ otherwise the text area (above the picker region) is not redrawn, and after the jump only a few picker rows are visible.
variable panel-dirty 0 panel-dirty !
variable prev-panel-rows 0 prev-panel-rows !
variable pfirst 0 pfirst !
: emit-changes
  panel-dirty @ pnrows @ 0<> and if
    scr-rows @ prev-panel-rows @ -  scr-rows @ panel-rows @ -  min pfirst !
    nrows @ 0 ?do
      i pfirst @ >= if
        i rows-differ? if
          27 emit 91 emit 48 emit 109 emit      \ \e[0m reset
          i cells rowcur + @ { cs }
          i 1+ nrows @ < if i 1+ cells rowcur + @ else fblen @ then  cs -
          fb @ cs +  swap  type
        then
      then
    loop
    0 panel-dirty !
  else
    nrows @ 0 ?do
      i rows-differ? if
        27 emit 91 emit 48 emit 109 emit      \ \e[0m reset
        i cells rowcur + @ { cs }
        i 1+ nrows @ < if i 1+ cells rowcur + @ else fblen @ then  cs -
        fb @ cs +  swap  type
      then
    loop
    0 panel-dirty !                          \ also clear any leftover panel-dirty when pnrows=0
  then ;

\ output this segment from in-line byte offset bi (not exceeding text-cols display columns; wide characters do not straddle segments), return the new bi
\ ===== style table (syntax highlighting): reusing ansi.fs color/attribute encoding =====
<A Cyan >FG A>              Constant S-COMMENT
<A Green >FG A>             Constant S-STRING
<A Magenta >FG A>           Constant S-NUMBER
<A Blue >FG Bold A>         Constant S-KEYWORD
<A Cyan >FG Bold A>         Constant S-CONST
<A Yellow >FG A>            Constant S-BUILTIN
<A Cyan >FG A>               Constant S-TYPE
<A Magenta >FG Bold A>       Constant S-PREPROC
Dim                         Constant S-LINENR     \ line number (dim)
<A Yellow >FG A>            Constant S-CURLINENR  \ current line number (bright)
Invers                      Constant S-SELECT   \ selection
Invers                      Constant S-SEARCH   \ search match (reverse video)

\ style -> SGR bytes (written into fb; 0 = reset); the trailing ';' is rewritten to 'm'
\ no locals (Gforth's locals frames crash in some call contexts; style>fb is called extremely often)
: style>fb ( attr -- )
  27 fb-c 91 fb-c 48 fb-c                          \ \e[0 reset first (otherwise attributes accumulate)
  dup Bold and if      59 fb-c 49 fb-c then
  dup Dim and if       59 fb-c 50 fb-c then
  dup Italic and if    59 fb-c 51 fb-c then
  dup Underline and if 59 fb-c 52 fb-c then
  dup Blink and if     59 fb-c 53 fb-c then
  dup Invers and if    59 fb-c 55 fb-c then
  dup BG> ?dup if  59 fb-c $F xor 40 + fb-ud.  then
  dup FG> ?dup if  59 fb-c $F xor 30 + fb-ud.  then
  drop  109 fb-c ;                                 \ m
\ same as style>fb but WITHOUT the \e[0 reset: appends/overrides onto the current SGR state
\ (stacks a foreground/underline style on top of an existing background; no locals — same hot-path constraint)
\ the first parameter must not be ';'-prefixed (style>fb uses the leading 0 for that)
variable sfb-sep
: sfb-param ( n -- )   sfb-sep @ if 59 fb-c then  1 sfb-sep !  fb-ud. ;
: style>fb+ ( attr -- )
  27 fb-c 91 fb-c               \ \e[
  0 sfb-sep !
  dup Bold and if      1 sfb-param  then
  dup Dim and if       2 sfb-param  then
  dup Italic and if    3 sfb-param  then
  dup Underline and if 4 sfb-param  then
  dup Blink and if     5 sfb-param  then
  dup Invers and if    7 sfb-param  then
  dup BG> ?dup if  $F xor 40 + sfb-param  then
  dup FG> ?dup if  $F xor 30 + sfb-param  then
  drop  109 fb-c ;                                 \ m

\ output this segment from in-line byte bi / absolute column dcol0, return (new bi, new absolute column); tabs expand to spaces
variable rs-c 0 rs-c !
: render-seg { loff lw bi dcol0 }
  bi { b }  dcol0 { dcol }  0 { hlend }  0 { hlattr }  0 { curattr }
  begin
    b lw <  dcol dcol0 - text-cols @ <  and
  while
    loff b + { a }
    a hlend >= if                                   \ currently not in a highlight region; ask the hook for a new match
      a hl-xt @ execute  to hlattr to hlend
    then
    a hlend < if  hlattr  else  0  then { eff }   \ effective style (0 outside the region)
    eff curattr <> if  eff style>fb  eff to curattr  then
    loff b + dcol u8-wl { cw cn }
    dcol dcol0 - 0>  dcol dcol0 - cw + text-cols @ >  and if
      curattr if 0 style>fb then  b dcol exit
    then
    loff b + g-char-at rs-c !
    rs-c @ 9 =  rs-c @ 13 =  or if
      cw 0 ?do 32 fb-c loop              \ tab/\r -> cw spaces (\r is turned into spaces to avoid returning to the column start and clearing the line)
    else
      cn 0 ?do  loff b + i + g-char-at fb-c  loop
    then
    dcol cw + to dcol
    b cn + to b
  repeat
  curattr if 0 style>fb then
  b dcol ;

\ cursor hide/show (hides the cursor "passing over" the status line during updates)
: cursor-hide  27 emit 91 emit 63 emit 50 emit 53 emit 108 emit ;   \ \e[?25l
: cursor-show  27 emit 91 emit 63 emit 50 emit 53 emit 104 emit ;   \ \e[?25h

\ cursor shape (DECSCUSR): block in command mode, bar in insert mode
variable cur-shape  -1 cur-shape !
: cursor-shape { n }
  n cur-shape @ <> if
    n cur-shape !
    27 emit 91 emit  n 48 + emit  32 emit 113 emit   \ \e[<n> q
  then ;

\ render the current window's (wctx) text rows into fb; each row starts with fb-at-xy(win-x, win-y+srow).
\ row end: use \e[K when the right edge is the terminal's right edge, otherwise pad spaces to the window right edge (to avoid erasing a neighbor window).
: win-render-text ( -- )
  top-line @ { tl }
  top-line @ 0 linecol->offset { loff }
  top-line @ line-length { lw }
  loff lw top-sub @ sub->seg { bi dcol0 }
  top-sub @ { ts }
  0 { srow }
  begin
    srow win-text-rows <  srow scr-rows @ panel-rows @ - <  and   \ when a picker is open the buffer yields the bottom (including the status line)
  while
    \ push mode: first insert the menu rows at the anchor (pushing everything below) — only for the active window
    win-render-active @ if
      overlay-float? @ 0= if
        srow overlay-vrow @ =  overlay-rows @ 0>  and  overlay-xt @ 0<>  and if
          overlay-rows @ 0 ?do
            fblen @  nrows @ cells rowcur +  !
            win-x @  win-y @ srow +  fb-at-xy
            i overlay-xt @ execute
            27 fb-c 91 fb-c 75 fb-c             \ \e[K
            nrows @ 1+ nrows !
            srow 1+ to srow
          loop
        then
      then
    then
    srow win-text-rows <  srow scr-rows @ panel-rows @ - <  and if
      fblen @  nrows @ cells rowcur +  !   \ record this row's start offset
      win-x @  win-y @ srow +  fb-at-xy    \ position to this window's row start
      \ line-number gutter + text (always rendered, so the buffer state advances)
      tl ts gutter-xt @ execute
      dcol0 { seg0 }                    \ segment start (absolute column); after rendering, dcol0 = segment-end absolute column
      loff lw bi dcol0 render-seg to dcol0 to bi
      \ overlay-covered rows: do not clear the row after the text; the menu overlays it + clear the tail; ordinary rows clear at the row end
      win-render-active @
      overlay-float? @
      srow overlay-vrow @ >=
      srow overlay-vrow @ overlay-rows @ + <
      overlay-xt @ 0<>
      and and and and if
        \ menu overlay (at-xy + content + pad spaces to the box width)
        srow overlay-vrow @ -  overlay-xt @ execute
        \ tail clear: clear from max(text end, box end) to the window right edge (prevents ghosting; text right of the box is preserved)
        win-x @ win-cols @ +  scr-cols @ = if
          27 fb-c 91 fb-c
          srow 1+ fb-ud.
          59 fb-c
          dcol0 seg0 -  overlay-vcol @ overlay-width @ +  max  gutter-w @ + 1+ fb-ud.
          72 fb-c
          27 fb-c 91 fb-c 75 fb-c             \ \e[K
        else
          text-cols @  dcol0 seg0 -  overlay-vcol @ overlay-width @ +  max  -  0 max 0 ?do 32 fb-c loop
        then
      else
        win-x @ win-cols @ +  scr-cols @ = if
          27 fb-c 91 fb-c 75 fb-c           \ right edge = terminal right edge: \e[K
        else
          text-cols @ dcol0 seg0 - - 0 max 0 ?do 32 fb-c loop   \ pad spaces to the window right edge (by segment width)
        then
      then
      nrows @ 1+ nrows !
      srow 1+ to srow
      ts 1+ to ts
      bi lw >= if                          \ this row finished rendering -> next row
        lw 1+ loff + to loff
        tl 1+ to tl
        0 to bi  0 to dcol0  0 to ts
        tl line-length to lw
      then
    then
  repeat
  \ per-window statusline (with multiple windows)
  win-status-on? @ if
    fblen @  nrows @ cells rowcur +  !
    win-x @  win-y @ win-rows @ + 1-  fb-at-xy
    win-render-active @ if  status-attr @ style>fb  else  status-inactive-attr @ style>fb  then  \ active = bright bar, inactive = dim bar (theme-configurable)
    win-status-xt @ execute
    \ showcmd right-aligned (dynamic width: win-cols changes on resize/split)
    showcmd-xt @ ?dup if execute else 0 0 then { sa sl }
    sl 0>  win-cols @ ws-w @ -  sa sl x-width -  0>  and if
      win-cols @ ws-w @ -  sa sl x-width -  0 ?do 32 fb-c loop
      sa sl fb-str
    else
      win-cols @ ws-w @ - 0 max 0 ?do 32 fb-c loop
    then
    0 style>fb                            \ reset
    nrows @ 1+ nrows !
  then ;

\ vertical divider (vim fillchars vert style, three UTF-8 bytes, single display width)
: fb-fill-v    226 fb-c 148 fb-c 130 fb-c ;   \ │ (U+2502)
\ reverse-video space (at statusline crossings: joins the reverse-video statuslines into one band)
: fb-fill-vsp  Invers style>fb  32 fb-c  0 style>fb ;

\ collect all V-split nodes (for drawing │)
7 constant max-divs
create div-x1   max-divs cells allot
create div-y1   max-divs cells allot
create div-x2   max-divs cells allot
create div-y2   max-divs cells allot
variable ndivs 0 ndivs !

\ collect the statusline rows (y2) of the leaves
8 constant max-sl
create sl-rows max-sl cells allot
variable nsl 0 nsl !
variable wslf 0 wslf !

: div-col { i -- c }  i cells div-x2 + @  i cells div-x1 + @ - 1+ 2 /  i cells div-x1 + @ + ;

: collect-divs { n -- }
  n W:KIND + @ 0= if exit then
  n W:KIND + @ 1 = if
    n W:X1 + @    div-x1 ndivs @ cells + !
    n W:Y1 + @    div-y1 ndivs @ cells + !
    n W:X2 + @    div-x2 ndivs @ cells + !
    n W:Y2 + @    div-y2 ndivs @ cells + !
    1 ndivs +!
  then
  n W:LEFT + @  recurse
  n W:RIGHT + @  recurse ;

: collect-sl { n -- }
  n W:KIND + @ 0= if
    n W:Y2 + @  sl-rows nsl @ cells + !
    1 nsl +!
    exit
  then
  n W:LEFT + @  recurse
  n W:RIGHT + @  recurse ;

: sl-row? { r -- f }
  0 wslf !
  nsl @ 0 ?do
    i cells sl-rows + @ r = if 1 wslf ! leave then
  loop
  wslf @ ;

\ draw a divider character at (col,row) (xt is a ( -- ) output word)
: fb-div-cell { col row xt -- }
  fblen @  nrows @ cells rowcur +  !
  col row fb-at-xy
  xt execute
  nrows @ 1+ nrows ! ;

\ render vertical dividers: │ on text rows, reverse-video spaces on statusline rows (joining the reverse-video statuslines into a band)
: render-divs ( -- )
  0 ndivs !  0 nsl !
  windows-root @ collect-divs
  windows-root @ collect-sl
  ndivs @ 0 ?do
    i div-col { c }
    i cells div-y1 + @ { dy }
    begin dy i cells div-y2 + @ <= while
      dy sl-row? if
        c dy ['] fb-fill-vsp fb-div-cell
      else
        c dy ['] fb-fill-v fb-div-cell
      then
      dy 1+ to dy
    repeat
  loop ;

: win-render-dividers ( -- )
  render-divs ;

: redraw
  update-screen-size
  scr-rows @ 1+  scr-cols @ 24 * 64 +  *  128 + fb-ensure
  fb-reset
  wctx @ win-active !                 \ remember the active window
  windows-first @
  begin dup while
    dup wctx !                         \ current window being rendered
    dup W:B + @ gctx !                 \ its buffer
    win-geometry
    update-gutter
    ensure-visible
    update-gutter                      \ ensure-visible may have changed top-line
    win-active @ over = win-render-active !   \ draw the overlay only for the active window
    win-render-text
    W:NEXT + @
  repeat drop
  win-active @ wctx !                  \ restore the active window
  wctx @ W:B + @ gctx !
  win-geometry
  update-gutter                        \ the active window's gutter (used by cursor-place)
  win-render-dividers                  \ dividers (when split)
  \ picker rows (above the status line, full global width)
  panel-xt @ ?dup if
    panel-rows @ 0 ?do                     \ the picker only occupies the bottom panel-rows rows (the buffer above stays visible)
      fblen @  nrows @ cells rowcur +  !
      0  scr-rows @ panel-rows @ - i +  fb-at-xy
      0 style>fb                          \ row-start reset (prevents style leakage from the previous row/frame)
      i panel-xt @ execute
      0 style>fb                          \ row-end reset (\e[K already moved into lp-row: the selected row is filled, non-selected rows clear their tail)
      nrows @ 1+ nrows !
    loop
  then
  \ global row: minibuffer with multiple windows (mode + transient); combined statusline with a single window
  fblen @  nrows @ cells rowcur +  !
  0 scr-rows @ fb-at-xy
  win-status-on? @ if  minibuf-xt @ execute  else  status-line-xt @ execute  then
  27 fb-c 91 fb-c 75 fb-c               \ \e[K clears the status-line tail
  size-changed @ if                     \ terminal shrank: clear the old content below the new status line
    27 fb-c 91 fb-c 74 fb-c             \ \e[J
  then
  nrows @ 1+ nrows !
  \ === incremental output (write only changed rows) ===
  cursor-hide
  cur-insert? if 6 else 2 then cursor-shape   \ command = block, insert = bar
  emit-changes
  \ swap the shadow frame
  fb @ pb @ fb ! pb !
  fblen @ pblen @ fblen ! pblen !
  fcap @ pcap @ fcap ! pcap !
  rowcur rowprev  nrows @ cells move
  nrows @ pnrows !
  panel-rows @ prev-panel-rows !
  \ position the cursor (during search/prompt, position it after the status-line prompt string)
  cursor-place-xt @ execute 0= if
    cursor @ offset->linecol { cl col }
    cl col dcol->sub-col { csub incol }
    cl csub vrow-of { crow }
    crow 0 max win-text-rows 1- min to crow          \ clamp: an out-of-range row would be clamped to the status-line row by the terminal
    gutter-w @ incol + { ccol }
    ccol 0 max win-cols @ 1- min to ccol
    ccol win-x @ +  crow win-y @ +  at-xy
  then
  cursor-show ;


\ ===== multi-buffer management =====
variable buffers-first  0 buffers-first !

: buf-name@ ( buf -- addr )  B:NAME + ;
: buf-namelen@ ( buf -- n )  B:NAMELEN + @ ;

: name= { buf addr len }
  buf buf-namelen@ len = if
    buf buf-name@  len  addr  len  compare 0=
  else false then ;

: buffer-find { addr len -- buf|0 }
  buffers-first @
  begin dup while
    dup addr len name= if exit then
    B:NEXT + @
  repeat ;

\ temporarily switch gctx to buf and execute xt (restore afterwards); only switches the buffer context, not the window (wctx).
\ Note: uses execute internally; execute crashes under the `!`/REPL repl stack (sp! switch), so it can only be used in contexts compiled into commands,
\ in `!`, use dedicated commands like cmd-syntax-buffer-toggle-named that do not rely on execute.
variable wb-buf  0 wb-buf !
variable wb-xt   0 wb-xt !
variable wb-save 0 wb-save !
: with-buffer ( buf xt -- )
  wb-xt !  wb-buf !
  gctx @ wb-save !
  wb-buf @ gctx !
  wb-xt @ execute
  wb-save @ gctx ! ;
\ with-buffer after finding the buffer by name (silently skip if not found)
: with-buffer-named { a u xt -- }
  a u buffer-find ?dup 0= if drop exit then
  swap with-buffer ;

: buffer-new { name len flags -- buf }
  /buf allocate throw { b }
  gctx @ { saved-g }
  b gctx !
  b /buf erase
  flags bflags !
  -1 nlines-sz !   1 lix-dirty !   1 ubroken !   0 unrec !   0 urp !
  0 saved-urp !
  -1 linenr !
  -1 b B:SYNTAX + !      \ syntax highlighting: default follows the global switch
  0 b B:LANGOVR + !       \ language: default automatic (by filename)
  max-undorec 6 * cells allocate throw  urecs !
  1024 g-init
  len path-max @ 1- min { n }
  n b B:NAMELEN + !
  name b B:NAME + n move
  b B:NAME + b B:FNAME + !           \ fname points at this buffer's name
  n b B:FLEN + !
  buffers-first @  b B:NEXT +  !
  b buffers-first !
  saved-g gctx !
  saved-g ?dup if  B:FLAGS + @ bflags !  then   \ restore bflags (gctx=0 for the first buffer); ?dup already kept saved-g, do not add another saved-g (stack leak)
  b ;

\ unlink b from the list (b must be in the list)
: buffer-unlink { b -- }
  buffers-first @ b = if
    b B:NEXT + @ buffers-first ! exit
  then
  buffers-first @
  begin  dup B:NEXT + @ b <> while  B:NEXT + @  repeat
  b B:NEXT + @  swap B:NEXT + ! ;

\ free a buffer and its sub-allocations (no switch, no confirmation; the caller guarantees it is neither a system buffer nor the current buffer)
: buffer-free { b -- }
  b buffer-free-xt
  b buffer-unlink
  b B:GBUF + @ free throw
  b B:LIX + @ ?dup if free throw then
  b B:URECS + @ free throw
  b B:DIAGS + @ ?dup if free throw then
  b B:URI   + @ ?dup if free throw then
  b free throw ;

\ switch: save the old buffer's saved view, switch gctx, restore the new buffer's saved view
: buffer-switch { b -- }
  gctx @ 0<>  gctx @ b <>  and if
    gctx @ other-window-shows? 0= if
      cursor @      gctx @ B:SVCURSOR   + !
      top-line @    gctx @ B:SVTOPLINE  + !
      top-sub @     gctx @ B:SVTOPSUB   + !
      want-col @    gctx @ B:SVWANTCOL  + !
      1             gctx @ B:SVVALID    + !
    then
  then
  b gctx !
  1 snap-dirty !                      \ invalidate the gap snapshot after a buffer switch (otherwise regex highlighting uses the old buffer's snap)
  b B:FLAGS + @ bflags !           \ sync bflags (otherwise buffer-new leaves the new buffer's flags behind)
  b wctx @ W:B + !
  b B:SVVALID + @ if
    b B:SVCURSOR + @   g-size min  cursor !
    b B:SVTOPLINE + @  g-size min  top-line !
    b B:SVTOPSUB + @   top-sub !
    b B:SVWANTCOL + @  want-col !
  else
    0 cursor !  0 top-line !  0 top-sub !  0 want-col !
  then
  buffer-enter-xt @ ?dup if execute then
  0 pnrows ! ;                        \ frame shadow invalidated (full redraw)

: buffer-open { addr len -- }
  addr len buffer-find { b0 }
  b0 if b0 buffer-switch exit then
  addr len BF_SAVELOG buffer-new { b }
  1 loading? !            \ prevent buffer-enter-all from sending didOpen for an empty buffer (content not loaded yet)
  b buffer-switch
  addr len load-file
  0 loading? ! ;


\ whether there is a "file-backed and modified" buffer (no-file buffers are excluded)
: any-file-modified? ( -- flag )
  buffers-first @
  begin dup while
    dup B:FLAGS + @ BF_NOFILE and 0= if        \ file-backed
      dup B:MODIFIED + @ if drop true exit then
    then
    B:NEXT + @
  repeat
  drop false ;

\ save all "file-backed and modified" buffers
: save-all-modified
  gctx @ { saved-g }
  buffers-first @ { b }
  begin b while
    b B:FLAGS + @ BF_NOFILE and 0= if
      b B:MODIFIED + @ if
        b gctx !
        fname @ flen @ save-file
      then
    then
    b B:NEXT + @ to b
  repeat
  saved-g gctx ! ;

\ ===== keymap (data-driven dispatch)=====
\ table of key → command xt. ekey key codes come in two classes: 0..255 (bytes) and 0x80000000+ (extended keys);
\ key-idx compresses both into a 0..511 index. Each Mode has one table, searchable up the parent chain.
768 constant /keymap
create keymap-command  /keymap cells allot
create keymap-insert   /keymap cells allot   \ insert mode
create keymap-base     /keymap cells allot   \ base mode (the common parent of command/insert)
variable leave-loop  0 leave-loop !    \ request to leave the main loop

\ key code -> keymap index. ekey key codes:
\   0..255           ordinary byte
\   0x80000000+k     extended key (k<256, K-LEFT/K-DELETE/K-F1..)
\   c | 0x40000000   Ctrl+ASCII (Ctrl-Enter etc.) → 512+c
\   other modifier masks (shift/alt etc.) → 0 (unbound, to prevent an index out of range)
: key-idx ( c -- i )
  dup 0 256 within if exit then
  dup 0x80000000 and if                         \ extended key
    0x7FFFFFFF and  dup 256 < if 256 + exit then  drop 0 exit
  then
  dup 0x70000000 and if                         \ modifier+ASCII (Ctrl/Shift/Alt+character)
    0x0FFFFFFF and  dup 256 < if 512 + exit then  drop 0 exit
  then
  drop 0 ;

: bind-command { c xt -- }  xt  c key-idx cells keymap-command +  ! ;
: bind-insert  { c xt -- }  xt  c key-idx cells keymap-insert  +  ! ;
: bind-base    { c xt -- }  xt  c key-idx cells keymap-base    +  ! ;
: bind-mode    { c xt m -- }  xt  c key-idx cells m m-keymap @ +  ! ;   \ bind to any mode

\ look up along the parent chain; return the xt if found, else 0
: km-find { c m -- }
  begin m while
    m m-keymap @  c key-idx cells + @  ?dup if exit then
    m m-parent @  to m
  repeat  0 ;

\ look up and execute; return true on a hit
: run-keymap { c km -- hit }
  km c key-idx cells + @ ?dup if execute true else false then ;

\ dispatch by state: the current mode keymap (with parent fallback); unmatched keys go to the current mode's input default
\ a prefix key (such as g) is just an ordinary mode — no more special pending case
: dispatch-key { c -- }
  c last-key !                          \ record the current key (the generic prefix enter uses it to look up key→mode)
  c cur-mode @ km-find ?dup if execute exit then
  cur-mode @ m-input @ ?dup if c swap execute then ;

\ ===== event waiting (poll over multiple fds)=====
\ core only provides "wait for events": wait for the keyboard / registered extra fds / timeout.
\   - keyboard: fd 0 (but first ask Gforth's own input buffer via key?, to avoid reading fd0 twice)
\   - extra fds: registered via add-watcher (subprocess pipes, sockets…); the callback runs when readable/hung up
\   - timeout: also check for terminal resizes (on some platforms Gforth's ekey does not return k-winch on SIGWINCH)
\ the facility comes entirely from Gforth's bundled poll(2) (unix/libc.fs, loaded by default).
\ Note: Gforth's select is an "either-or integer" (kernel/prim.fs), not select(2) — always use poll.
\ Convention: the callback xt drains the data itself (and self-remove-watcher on EOF/HUP); returning means "redraw needed".
\ Also: if stdin is EOF (redirected/closed), ekey keeps returning k-eof — if unhandled it would spin at 100%;
\ once detected here, drop fd0 from the poll set (set -1), keeping only the watchers + a 30ms tick.
8 constant max-watch                                   \ number of extra fds that can be watched simultaneously
create wt-fds  max-watch cells allot
create wt-xts  max-watch cells allot
create wt-pfds max-watch 1+ pollfd * allot
variable nwatch  0 nwatch !
variable input-closed 0 input-closed !                 \ stdin has reached EOF

variable wt-i   variable wt-f   variable wt-x
: wt-remove-at ( i -- )  wt-i !                      \ leave a hole: fd=-1 (ignored by poll), xt=0
  -1 wt-fds wt-i @ cells + !
   0 wt-xts wt-i @ cells + ! ;
: clear-watchers  0 nwatch ! ;
: remove-watcher ( fd -- )
  nwatch @ 0 ?do
    wt-fds i cells + @ over = if  i wt-remove-at  then
  loop  drop ;
: add-watcher ( fd xt -- )                           \ prefer reusing a hole, otherwise append
  wt-x !  wt-f !
  nwatch @ 0 ?do
    wt-xts i cells + @ 0= if
      wt-f @ wt-fds i cells + !
      wt-x @ wt-xts i cells + !
      unloop exit
    then
  loop
  nwatch @ max-watch >= if exit then
  wt-f @ wt-fds nwatch @ cells + !
  wt-x @ wt-xts nwatch @ cells + !
  1 nwatch +! ;

\ assemble the pollfd array: index 0 is always stdin (-1 when closed, ignored by poll), followed by the registered fds in order.
\ the indices always correspond one-to-one, so wt-revents uses i+1 for the i-th watcher's revents.
\ Note: deliberately no locals here — Gforth's locals frames corrupt the return stack in a deep call chain (a known project pitfall).
variable wt-p
: wt-build
  wt-pfds wt-p !
  input-closed @ 0= if 0 else -1 then  POLLIN wt-p @ fds!+ wt-p !
  nwatch @ 0 ?do
    wt-fds i cells + @ POLLIN wt-p @ fds!+ wt-p !
  loop
  wt-pfds nwatch @ 1+ ;
: wt-revents ( i -- mask )  1+ pollfd * wt-pfds + revents w@ ;

\ handle one poll round: run at most one watcher callback; return true = a watcher fired (redraw needed)
variable wt-hit 0 wt-hit !
: wt-wait ( -- redraw? )
  0 wt-hit !
  wt-build 30 poll drop
  nwatch @ 0 ?do                                \ snapshot count: safe even if the callback modifies the table
    wt-hit @ 0= if
      i cells wt-xts + @ 0<> if
        i wt-revents 0<> if
          wt-xts i cells + @ ?dup if execute  wt-hit !  then   \ the callback returns a flag: whether to redraw
        then
      then
    then
  loop
  wt-hit @ ;

variable wk-rows  0 wk-rows !
variable wk-cols  0 wk-cols !
: wk-sync     rows wk-rows !  cols wk-cols ! ;
: wk-resized? rows wk-rows @ <>  cols wk-cols @ <>  or ;

\ wait for events; returns the key code; 0 = redraw needed (terminal resize / a watcher fired)
: wait-key ( -- c )
  begin
    input-closed @ 0= if
      key? if
        read-key dup k-eof = if  drop 1 input-closed !     \ stdin EOF: stop asking fd0
        else  dup 0<> if exit then  drop  then             \ non-0 = a real key → return; 0 = a terminal reply, skip
      then
    then
    wt-wait if 0 exit then                        \ a watcher fired → let the main loop redraw
    idle-poll                                      \ idle poll (lsp debounces didChange)
    rt-warn-drain                                  \ drain runtime Gforth warnings (flag-gated)
    wk-resized? if 0 exit then                    \ terminal resize → redraw
  again ;

\ ===== main loop (command/insert are decided by mode + keymap)=====
\ ===== main loop =====
\ key-value global (no locals: this word is a deep chain + loop; locals corrupt the return stack)
variable el-key 0 el-key !
: editor-loop
  begin
    raw-mode            \ re-apply raw each round (ekey's first call re-sets termios and may enable IXON, swallowing Ctrl-s)
    0 leave-loop !
    0 vert !
    wk-sync
    redraw
    wait-key el-key !                        \ 0 = terminal resize -> go back up to redraw
    el-key @ 0<> if
      msg-clear
      confirm-quit @ if
        el-key @ 113 = if 1 leave-loop ! then                \ q: discard and quit
        el-key @ 19  = if save-all-modified  any-file-modified? 0= if 1 leave-loop ! then then  \ C-s: save all and quit
        leave-loop @ 0= if 0 confirm-quit ! then      \ any other key cancels
      else
        el-key @ key-inter-xt @ ?dup if execute else drop false then
        if
          \ intercepted (an overlay/modal consumed the key)
        else
          el-key @ key-pre-xt @ ?dup if execute then
          el-key @ dispatch-key
        then
      then
      vert @ 0= if cursor @ offset->linecol nip want-col ! then   \ if not a vertical move, sync the target column
    then
    leave-loop @
  until ;

\ load the config layers at runtime (~/.fe + ./.fe.fs); custom.fs provides the implementation (to avoid fe.fs referencing custom.fs before it exists)
defer load-layers-xt   ' noop is load-layers-xt
\ ===== reload the extension layer (custom.fs)=====
\ mechanism belongs to core: re-include custom.fs (its end calls init-keymaps to rebuild the tables).
\ errors are only reported, not fatal; the old keymap is kept (so the user can retry after fixing).
\ Gforth warnings (redefined etc.) go through ?warning -> >stderr -> debug-fid,
\ here debug-fid is pointed at a temp file, and read back into *messages* after the reload.
: reload-extension
  sp0 @ sp!                              \ reset the data stack (to prevent a dirty stack from causing an include underflow)
  s" warn" tmp-file  w/o create-file throw { wf }
  debug-fid { saved-fid }
  wf TO debug-fid
  s" custom.fs" ['] included catch ?dup if
    trace-exception
    0 pnrows !                              \ frame shadow invalidated (full redraw)
    s" reload failed (see *trace*)" msg-say
  else
    0 pnrows !
    s" custom.fs reloaded" msg-say
  then
  saved-fid TO debug-fid
  wf close-file throw
  load-layers-xt                       \ reload ~/.fe + ./.fe.fs after the reload
  \ read the warnings back into *messages*
  s" warn" tmp-file  r/o open-file throw { rf }
  rf slurp-fid { a u }
  rf close-file throw
  u 0> if  a u msg-log  a free throw  then
  s" warn" tmp-file  delete-file throw ;

\ ===== multiple windows (split tree + focus switching)=====

\ create a leaf (inherits the current window's buffer + view)
: win-new ( -- w )
  /win allocate throw { w }
  w /win erase
  wctx @ W:B + @  w W:B + !
  cursor @    w W:CURSOR + !
  top-line @  w W:TOPLINE + !
  top-sub @   w W:TOPSUB + !
  want-col @  w W:WANTCOL + !
  wctx @ W:LINENR + @  w W:LINENR + !   \ inherit the line-number mode (vim :split inherits local options)
  wctx @ W:MODE + @      w W:MODE + !       \ inherit the mode (per-window)
  wctx @ W:PREVMODE + @  w W:PREVMODE + !
  w ;

\ insert leaf w after the current leaf (wctx) in the leaf iteration list
: win-insert-after { w -- }
  wctx @ W:NEXT + @  w W:NEXT + !
  w  wctx @ W:NEXT + ! ;

\ the leftmost leaf of the subtree
: win-first-leaf { n -- leaf }
  begin n W:KIND + @ 0<> while n W:LEFT + @ to n repeat
  n ;

\ split the current leaf: wctx becomes left/top, the new leaf becomes right/bottom and gains focus
: win-split { kind -- }
  win-count max-windows >= if exit then
  kind WK-HSPLIT = if
    win-rows @ 4 < if exit then      \ at least split into 2+2 (including the statusline, no divider row)
  else
    win-cols @ 12 < if exit then     \ the statusline must at least fit a name + position
  then
  win-new { w2 }
  /win allocate throw { s }
  s /win erase
  kind s W:KIND + !
  wctx @ s W:LEFT + !
  w2 s W:RIGHT + !
  wctx @ W:PARENT + @ { p }
  p 0= if
    s windows-root !
  else
    p W:LEFT + @ wctx @ = if  s p W:LEFT + !  else  s p W:RIGHT + !  then
  then
  s wctx @ W:PARENT + !
  s w2 W:PARENT + !
  p s W:PARENT + !
  w2 win-insert-after
  w2 wctx !
  w2 W:B + @ gctx !
  0 pnrows ! ;

: split-v ( -- )  WK-VSPLIT win-split ;
: split-s ( -- )  WK-HSPLIT win-split ;

\ close the active leaf: the sibling takes the parent's place, free this leaf + the parent split node; focus = the leftmost leaf of the sibling subtree
: win-close ( -- )
  win-count 1 <= if exit then
  wctx @ { w }
  w W:PARENT + @ { p }
  p 0= if exit then
  p W:LEFT + @ w = if p W:RIGHT + @ else p W:LEFT + @ then { sib }
  p W:PARENT + @ { gp }
  gp 0= if
    sib windows-root !
  else
    gp W:LEFT + @ p = if  sib gp W:LEFT + !  else  sib gp W:RIGHT + !  then
  then
  gp sib W:PARENT + !
  \ remove w from the leaf list
  windows-first @ w = if
    w W:NEXT + @ windows-first !
  else
    windows-first @
    begin dup W:NEXT + @ w <> while W:NEXT + @ repeat
    w W:NEXT + @  swap W:NEXT + !
  then
  w free throw
  p free throw
  sib win-first-leaf { fl }
  fl wctx !
  fl W:B + @ gctx !
  buffer-enter-xt @ ?dup if execute then
  0 pnrows ! ;

\ focus the next leaf (circular); run buffer-enter only when the buffer changed
: win-focus-next ( -- )
  windows-first @ 0= if exit then
  wctx @ W:NEXT + @ ?dup 0= if windows-first @ then
  wctx !
  gctx @ wctx @ W:B + @ <> if
    wctx @ W:B + @ gctx !
    buffer-enter-xt @ ?dup if execute then
  then
  0 pnrows ! ;

\ directional focus (1=h left 2=j down 3=k up 4=l right); find the nearest leaf in that direction
: win-focus-dir { dir }
  0 0 0 0 0 0 0 0 0 0 0 0 0 0 { cx1 cx2 cy1 cy2 cxc cyc best bestd d wx1 wx2 wy1 wy2 w }
  wctx @ W:X1 + @ to cx1
  wctx @ W:X2 + @ to cx2
  wctx @ W:Y1 + @ to cy1
  wctx @ W:Y2 + @ to cy2
  cx1 cx2 + 2 / to cxc
  cy1 cy2 + 2 / to cyc
  0 to best  0 to bestd
  windows-first @ to w
  begin w while
    w wctx @ <> if
      w W:X1 + @ to wx1
      w W:X2 + @ to wx2
      w W:Y1 + @ to wy1
      w W:Y2 + @ to wy2
      0 to d
      dir 1 = if
        wx2 cx1 < if
          cx1 wx2 - 1000 *  cyc wy1 wy2 + 2 / - abs + to d
        then
      else dir 4 = if
        wx1 cx2 > if
          wx1 cx2 - 1000 *  cyc wy1 wy2 + 2 / - abs + to d
        then
      else dir 3 = if
        wy2 cy1 < if
          cy1 wy2 - 1000 *  cxc wx1 wx2 + 2 / - abs + to d
        then
      else
        wy1 cy2 > if
          wy1 cy2 - 1000 *  cxc wx1 wx2 + 2 / - abs + to d
        then
      then then then
      d 0<> if
        best 0=  d bestd <  or if
          w to best  d to bestd
        then
      then
    then
    w W:NEXT + @ to w
  repeat
  best if
    best wctx !
    gctx @ wctx @ W:B + @ <> if
      wctx @ W:B + @ gctx !
      buffer-enter-xt @ ?dup if execute then
    then
    0 pnrows !
  then ;

\ extension layer: commands + bindings (reloadable at runtime via Ctrl-x)
\ create the window early: cur-mode/prev-mode are per-window accessors; mode-set in init-keymaps needs a valid wctx
: win-init
  /win allocate throw wctx !
  wctx @ /win erase
  -1 wctx @ W:LINENR + !
  wctx @ windows-first !
  wctx @ windows-root !
  0 wctx @ W:NEXT + ! ;
win-init

include custom.fs

\ ===== terminal title (tmux window title etc.): OSC 0 = icon + window title =====
: set-term-title ( addr len -- )
  27 emit 93 emit 48 emit 59 emit   \ ESC ] 0 ;
  type
  7 emit ;                          \ BEL

\ ===== runtime warning routing (flag-gated: set warn-pending, fall through to debug-fid)=====
\ Trivial on purpose: this runs in Gforth's warning path (potentially a deep chain), so no locals / no $tmp.
:noname ( f xt -- )
  swap warnings @ and if
    1 warn-pending !
    >stderr cr ." warning: " execute exit
  then
  drop ;
is ?warning


\ ===== entry point =====
: fe
  s" *scratch*" BF_NOFILE buffer-new buffer-switch
  s" *messages*" BF_NOFILE BF_READONLY or buffer-new  messages-buf !
  s" *trace*"    BF_NOFILE BF_READONLY or buffer-new  trace-buf !
  \ read the startup warnings back into *messages*, then keep debug-fid redirected to a runtime warn file
  fe-warn-fid @ close-file throw
  s" start-warn" tmp-file r/o open-file throw { rf }
  rf slurp-fid { a u }
  rf close-file throw
  u 0> if  a u msg-log  a free throw  then
  s" start-warn" tmp-file delete-file throw
  s" rt-warn" tmp-file w/o create-file throw rt-warn-fid !
  rt-warn-fid @ TO debug-fid
  next-arg dup if                        \ open all files from the command-line arguments
    buffer-open
    gctx @                               \ remember the first (staged on the stack)
    begin  next-arg dup  while  buffer-open  repeat  2drop
    buffer-switch                        \ back to the first
  else 2drop then
  6 s" C.UTF-8" setlocale drop     \ force the UTF-8 locale so wcwidth returns 2 for CJK
  6 s" en_US.UTF-8" setlocale drop
  key? drop           \ warm-up: let Gforth's ekey save the "normal" terminal state first (otherwise it would restore raw on exit)
  raw-mode
  mouse-off           \ turn off mouse tracking possibly left on by the environment (otherwise moving the mouse floods the screen)
  s" fe" set-term-title  \ terminal/tmux window title defaults to fe
  load-layers-xt        \ load ~/.fe + ./.fe.fs at runtime (not frozen into a save-image)
  0 quit-flag !
  begin
    ['] editor-loop catch
    ?dup if  trace-exception  else  1 quit-flag !  then
    quit-flag @
  until
  shutdown-xt @ ?dup if execute then
  mouse-off
  0 cursor-shape
  sane-mode
  cursor-show
  page ." bye" cr ;

fe
bye
