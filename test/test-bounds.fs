\ ===== test/test-bounds.fs — bounded path/command concatenation regression tests =====
\ must be included on top of the full fe definitions (fe-strip.fs), like test-fe-stack.fs.
\ run: see run-fe-stack.fs (executed from the project root).

require assert.fs
require unix/libc.fs   \ setenv (for the long-HOME cfg-build test)

variable t-n  0 t-n !
: t-ok  1 t-n +! ;

\ long fixtures
create long-name 300 allot
: long-name-init  300 0 ?do  121 long-name i + c!  loop ;   \ 'y'
long-name-init
create big-query 10000 allot
: big-query-init  10000 0 ?do  122 big-query i + c!  loop ;  \ 'z'
big-query-init
create big-home 4096 allot
: big-home-init  4096 0 ?do  104 big-home i + c!  loop ;     \ 'h'
big-home-init
create big-ext 100 allot
create big-cmd 100 allot
: big-ext-init  100 0 ?do  101 big-ext i + c!  loop ;        \ 'e'
big-ext-init
: big-cmd-init  100 0 ?do  99 big-cmd i + c!  loop ;         \ 'c'
big-cmd-init

\ bpath-join: long cur-dir + long name must stay within the buffer
: test-bpath-bounds
  path-max @ 256 + { cap }
  cur-dir  cap 1-  120 fill          \ fill cur-dir with cap-1 'x'
  cap 1- cur-dir-len !
  long-name 300 bpath-join
  assert( bpath-len @ cap <= )
  t-ok ;

\ grep-build: a huge query must stay within grep-cmd (1024)
: test-grep-bounds
  big-query 10000 grep-build
  assert( grep-clen @ 1024 <= )
  t-ok ;

\ fmt-register: over-long ext/cmd are clamped to their slots (16 / 64)
: test-fmt-register-bounds
  big-ext 100  big-cmd 100  fmt-register
  nfmt @ 1- { i }
  assert( fmt-elens i cells + @ 16 <= )
  assert( fmt-clens i cells + @ 64 <= )
  t-ok ;

\ cfg-build: an over-long HOME is clamped to path-max (+ the /.fe suffix)
: test-cfg-build-bounds
  s" HOME" big-home 4096 1 setenv drop
  cfg-build
  assert( cfgpath-len @  path-max @ 4 +  <= )
  t-ok ;

test-bpath-bounds
test-grep-bounds
test-fmt-register-bounds
test-cfg-build-bounds

cr ." bounds tests passed (" t-n @ . ." assertions ok)" cr
