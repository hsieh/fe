\ ===== test/harness.fs — unit-test infrastructure =====
\ depends on Gforth's assert.fs; the modules under test only include the pure-logic layer (state/gapbuf/utf8/regex/search),
\ no terminal (rawmode), no libcc termios, and never enter the fe main loop.
\ usage: cd project-root && gforth-fast test/run-all.fs

require assert.fs

include ../state.fs
include ../gapbuf.fs
include ../regex.fs
include ../search.fs

\ tab-width is defined in fe.fs; utf8.fs's u8-wl needs it, so the tests provide it separately
variable tab-width  8 tab-width !

\ test buffer: allocate a standalone gctx of /buf bytes + a wctx of /win bytes (bypassing fe's multi-buffer machinery),
\ zero the fields + g-init; enough for gapbuf/utf8/search/undo.
: tb-init ( -- )
  /buf allocate throw gctx !
  gctx @ /buf erase
  /win allocate throw wctx !
  wctx @ /win erase
  1024 g-init ;

\ rebuild the test buffer and insert content (a u) (set loading? to avoid recording edit-dirty)
: tb-set ( a u -- )
  tb-init
  0 g-move-gap
  1 loading? !
  g-insert
  0 loading? ! ;

\ simple test counter (a failed assert aborts; here we only count passes for the output)
variable t-n  0 t-n !
: t-ok  1 t-n +! ;
