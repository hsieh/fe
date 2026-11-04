\ ===== test/test-regex.fs — gap buffer contiguous snapshot (snap) =====
\ regex.fs's libcc part (POSIX regex) is out of unit-test scope; only the pure-logic snap is tested.
\ (regex.fs/search.fs are already included in harness.fs, to avoid duplicate redefines)

: test-snap-build
  s" hello world" tb-set
  5 g-move-gap                    \ gap moved to the middle (front=5 back=6)
  assert( snap-dirty @ 1 = )      \ lix-touch sets snap-dirty after g-insert
  snap-build
  assert( snap-len @ 11 = )
  assert( snap @ 11 s" hello world" compare 0= )
  assert( snap-dirty @ 0= )
  \ build again (not edited) should exit immediately, contents unchanged
  snap-build
  assert( snap @ 11 s" hello world" compare 0= )
  t-ok ;

: test-snap-ensure
  s" ab" tb-set
  100 snap-ensure
  assert( snap-cap @ 100 >= )
  t-ok ;
