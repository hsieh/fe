\ ===== test/harness.fs — 单元测试基础设施 =====
\ 依赖 Gforth 的 assert.fs；被测模块只 include 纯逻辑层（state/gapbuf/utf8/regex/search），
\ 不碰终端（rawmode）、不碰 libcc 的 termios、不进入 fe 主循环。
\ 用法：cd 项目根 && gforth-fast test/run-all.fs

require assert.fs

include ../state.fs
include ../gapbuf.fs
include ../regex.fs
include ../search.fs

\ tab-width 在 fe.fs 定义；utf8.fs 的 u8-wl 需要，测试里单独提供
variable tab-width  8 tab-width !

\ 测试 buffer：分配一个 /buf 大小的独立 gctx + 一个 /win 的 wctx（不经过 fe 的多 buffer 机制），
\ 字段清零 + g-init，足够 gapbuf/utf8/search/undo 使用。
: tb-init ( -- )
  /buf allocate throw gctx !
  gctx @ /buf erase
  /win allocate throw wctx !
  wctx @ /win erase
  1024 g-init ;

\ 重建测试 buffer 并插入内容 (a u)（置 loading? 避免记 edit-dirty）
: tb-set ( a u -- )
  tb-init
  0 g-move-gap
  1 loading? !
  g-insert
  0 loading? ! ;

\ 简单测试计数器（assert 失败即 abort，这里只计数通过项用于输出）
variable t-n  0 t-n !
: t-ok  1 t-n +! ;
