\ ===== test/test-regex.fs — gap buffer 连续快照（snap）=====
\ regex.fs 的 libcc 部分（POSIX 正则）不在单测范围；只测纯逻辑的 snap。
\ （regex.fs/search.fs 已在 harness 里 include，避免重复 redefined）

: test-snap-build
  s" hello world" tb-set
  5 g-move-gap                    \ gap 移到中间（front=5 back=6）
  assert( snap-dirty @ 1 = )      \ g-insert 后 lix-touch 置 snap-dirty
  snap-build
  assert( snap-len @ 11 = )
  assert( snap @ 11 s" hello world" compare 0= )
  assert( snap-dirty @ 0= )
  \ 再 build（未编辑）应直接退出，内容不变
  snap-build
  assert( snap @ 11 s" hello world" compare 0= )
  t-ok ;

: test-snap-ensure
  s" ab" tb-set
  100 snap-ensure
  assert( snap-cap @ 100 >= )
  t-ok ;
