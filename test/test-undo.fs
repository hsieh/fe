\ ===== test/test-undo.fs — undo/redo 操作日志 =====
\ 依赖 gapbuf + undo.fs + 宿主 cursor/modified（经 harness 的 gctx/wctx）。

include ../undo.fs

\ 初始化带 undo 栈的测试 buffer
: tb-init-undo ( -- )
  tb-init
  max-undorec 6 * cells allocate throw urecs ! ;

create tu-buf 1 allot

\ 模拟插入字符（记录 undo + 实际插入 + 光标前移）
: tu-insert-char ( c -- )
  dup  cursor @ swap urec-ins
  tu-buf c!
  cursor @ g-move-gap
  tu-buf 1 g-insert
  1 cursor +! ;

\ 模拟正向删除字符（记录 undo + 实际删除）
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
  assert( unrec @ 1 = )              \ 连续同型合并成一组
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
  2 cursor !                         \ 光标在 'c' 前
  tu-delete-char                     \ 删光标处 'c'
  assert( g-size 2 = )
  undo
  assert( g-size 3 = )
  assert( 2 g-char-at [char] c = )
  t-ok ;

\ u-broken 打断合并：光标移动后再插入 = 新一组
: test-undo-broken
  tb-init-undo
  [char] a tu-insert-char
  u-broken                           \ 打断
  [char] b tu-insert-char
  assert( unrec @ 2 = )              \ 两组
  undo
  assert( g-size 1 = )               \ 只撤 b
  assert( 0 g-char-at [char] a = )
  t-ok ;

\ 满时丢最旧一条（插入超过 max-undorec 条后，最旧的被丢弃）
: test-undo-make-room
  tb-init-undo
  max-undorec 1+ 0 ?do
    [char] x tu-insert-char
    u-broken                         \ 每条独立（不合并）
  loop
  assert( unrec @ max-undorec = )    \ 已满
  assert( g-size max-undorec 1+ = )
  t-ok ;
