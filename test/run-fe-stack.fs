\ ===== test/run-fe-stack.fs — run stack-effect + bounds regression tests (pure Forth) =====
\ usage: cd project-root && gforth-fast test/run-fe-stack.fs
\ first run needs a C compiler for the libcc bindings (termios/regex/cjson/...).

\ fe-incdir = project root (cjson's add-incdir uses it to find vendor/cJSON.c)
2variable fe-incdir
create dirbuf 512 allot
dirbuf 511 get-dir  fe-incdir 2!

\ build fe-strip.fs = fe.fs minus its last 2 lines (the "fe" call and "bye"), pure Forth
variable fs-src  0 fs-src !
variable fs-dst  0 fs-dst !
variable fs-nl   0 fs-nl !
variable fs-i    0 fs-i !
: fe-strip-build ( -- )
  s" fe.fs" r/o open-file throw fs-src !
  0 fs-nl !
  begin  pad 8192 fs-src @ read-line throw  while  1 fs-nl +!  repeat
  fs-src @ close-file throw
  s" fe.fs" r/o open-file throw fs-src !
  s" fe-strip.fs" w/o create-file throw fs-dst !
  0 fs-i !
  begin  pad 8192 fs-src @ read-line throw  while
    fs-i @ fs-nl @ 2 - < if
      pad swap fs-dst @ write-line throw
    else
      drop
    then
    1 fs-i +!
  repeat
  fs-src @ close-file throw
  fs-dst @ close-file throw ;

\ join fe-incdir + "/" + name into fs-path (absolute path; included resolves relative to this file)
variable fs-na 0 fs-na !
variable fs-nu 0 fs-nu !
variable fs-dl 0 fs-dl !
create fs-path 520 allot
variable fs-plen 0 fs-plen !
: fs-abs ( name-a name-u -- )
  fs-nu !  fs-na !
  fe-incdir 2@ nip fs-dl !
  fe-incdir 2@ drop  fs-path  fs-dl @  move
  fs-dl @ fs-plen !
  47 fs-path fs-plen @ + c!  1 fs-plen +!
  fs-na @  fs-path fs-plen @ +  fs-nu @  move
  fs-nu @ fs-plen +! ;

fe-strip-build
s" /fe-strip.fs" fs-abs  fs-path fs-plen @ included
s" /test/test-fe-stack.fs" fs-abs  fs-path fs-plen @ included
s" /test/test-bounds.fs" fs-abs  fs-path fs-plen @ included
s" fe-strip.fs" delete-file drop
