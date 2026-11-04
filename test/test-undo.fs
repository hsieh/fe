\ ===== test/test-undo.fs — undo/redo operation log =====
\ depends on gapbuf + undo.fs + host cursor/modified (via harness's gctx/wctx).

include ../undo.fs

\ initialize a test buffer with an undo stack
: tb-init-undo ( -- )
  tb-init
  max-undorec 6 * cells allocate throw urecs ! ;

create tu-buf 1 allot

\ simulate inserting a char (record undo + actually insert + advance the cursor)
: tu-insert-char ( c -- )
  dup  cursor @ swap urec-ins
  tu-buf c!
  cursor @ g-move-gap
  tu-buf 1 g-insert
  1 cursor +! ;

\ simulate a forward delete of a char (record undo + actually delete)
: tu-delete-char ( -- )
  cursor @ 1 urec-del-fwd
  cursor @ g-move-gap
  1 g-delete-forward ;

: test-undo-insert-merge
  tb-init-undo
  [char] a tu-insert-char
  [char] b tu-insert-char
  assert( g-size 2 = )
  assert( cursor @ 2 = )
  assert( unrec @ 1 = )              \ consecutive same-kind edits merge into one group
  undo
  assert( g-size 0 = )
  assert( cursor @ 0 = )
  redo
  assert( g-size 2 = )
  assert( cursor @ 2 = )
  t-ok ;

: test-undo-delete
  s" abc" tb-set
  max-undorec 6 * cells allocate throw urecs !
  2 cursor !                         \ cursor before 'c'
  tu-delete-char                     \ delete the 'c' at the cursor
  assert( g-size 2 = )
  undo
  assert( g-size 3 = )
  assert( 2 g-char-at [char] c = )
  t-ok ;

\ u-broken breaks the merge: insert after a cursor move = a new group
: test-undo-broken
  tb-init-undo
  [char] a tu-insert-char
  u-broken                           \ break
  [char] b tu-insert-char
  assert( unrec @ 2 = )              \ two groups
  undo
  assert( g-size 1 = )               \ only b undone
  assert( 0 g-char-at [char] a = )
  t-ok ;

\ when full, drop the oldest (after inserting more than max-undorec entries, the oldest is dropped)
: test-undo-make-room
  tb-init-undo
  max-undorec 1+ 0 ?do
    [char] x tu-insert-char
    u-broken                         \ each entry independent (no merge)
  loop
  assert( unrec @ max-undorec = )    \ full
  assert( g-size max-undorec 1+ = )
  t-ok ;
