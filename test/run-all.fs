\ ===== test/run-all.fs — 运行全部单元测试 =====
\ 用法：gforth-fast test/run-all.fs
\ include 相对本文件目录（Gforth 语义），所以任意 cwd 都能跑。

include harness.fs
include test-state.fs
include test-gapbuf.fs
include test-utf8.fs
include test-undo.fs
include test-regex.fs
include test-search.fs

\ --- 执行全部测试（断言失败会 abort 并打印位置）---
test-state-buffer-fields
test-state-window-fields
test-gap-init
test-gap-insert-char-at
test-gap-lix
test-gap-contents
test-gap-move-gap
test-gap-delete
test-gap-insert-mid
test-gap-lix-incremental
test-gap-grow
test-gap-delete-forward
test-gap-lix-nl-count
test-gap-lix-find-gt
test-gap-lix-shift-tail
test-gap-lix-ensure
test-gap-lix-touch
test-u8-n
test-u8-wl-ascii
test-u8-wl-wide
test-u8-wl-tab
test-u8-width-bi-col
test-u8-prev
test-utf8-emit
test-undo-insert-merge
test-undo-delete
test-undo-broken
test-undo-make-room
test-snap-build
test-snap-ensure
test-search-match
test-search-fwd-back
test-search-word-boundary
test-search-ignorecase

cr ." all tests passed (" t-n @ . ." assertions ok)" cr
bye
