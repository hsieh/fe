\ ===== test/test-gapbuf.fs — gap buffer 核心操作 =====

: test-gap-init
  tb-init
  assert( g-size 0= )
  assert( g-front @ 0= )
  assert( g-back @ 0= )
  t-ok ;

: test-gap-insert-char-at
  s" abc" tb-set
  assert( g-size 3 = )
  assert( 0 g-char-at [char] a = )
  assert( 1 g-char-at [char] b = )
  assert( 2 g-char-at [char] c = )
  t-ok ;

\ 内容含换行（s" 多行字符串）：lix 行起始偏移 + 行数
: test-gap-lix
  s\" ab\ncd\nef" tb-set                 \ "ab\ncd\nef" = 8 字节
  assert( g-size 8 = )                 \ "ab\ncd\nef" = 8 字节
  lix-build
  assert( lix-n @ 3 = )
  assert( 0 lix@ 0 = )
  assert( 1 lix@ 3 = )
  assert( 2 lix@ 6 = )
  assert( g-nlines 3 = )
  t-ok ;

\ g-contents 拼接 front + back（gap 在中间时也正确）
: test-gap-contents
  s" hello world" tb-set
  \ 把 gap 移到中间（offset 5），再取内容应不变
  5 g-move-gap
  g-contents { a u }
  assert( u 11 = )
  assert( a 11 s" hello world" compare 0= )
  a free throw
  t-ok ;

\ g-move-gap 不改变逻辑内容
: test-gap-move-gap
  s" abcdef" tb-set
  3 g-move-gap
  assert( 0 g-char-at [char] a = )
  assert( 1 g-char-at [char] b = )
  assert( 2 g-char-at [char] c = )
  assert( 3 g-char-at [char] d = )
  assert( 5 g-char-at [char] f = )
  0 g-move-gap
  assert( 0 g-char-at [char] a = )
  assert( 5 g-char-at [char] f = )
  t-ok ;

\ 删除：gap 移到位置后 forward/backward 删除
: test-gap-delete
  s" abcdef" tb-set
  3 g-move-gap                  \ gap 在 c 后（offset 3）
  2 g-delete-backward           \ 删 b c -> "adef"
  assert( g-size 4 = )
  assert( 0 g-char-at [char] a = )
  assert( 1 g-char-at [char] d = )
  assert( 3 g-char-at [char] f = )
  t-ok ;

: test-gap-insert-mid
  s" ac" tb-set
  1 g-move-gap                  \ gap 在 a 后
  s" b" g-insert                \ "abc"
  assert( g-size 3 = )
  assert( 1 g-char-at [char] b = )
  assert( 2 g-char-at [char] c = )
  t-ok ;

\ lix 增量编辑：无换行插入只平移后续行偏移（不重建）
: test-gap-lix-incremental
  s\" ab\ncd" tb-set                      \ lix[0]=0 lix[1]=3
  lix-build
  assert( 1 lix@ 3 = )
  1 g-move-gap
  s" X" g-insert                 \ "aXb\ncd" -> lix[1]=4
  lix-build
  assert( lix-n @ 2 = )
  assert( 1 lix@ 4 = )
  t-ok ;

\ g-gap-size / g-grow（插入超过初始 1024 触发扩容）
create bigbuf 2048 allot
: test-gap-grow
  tb-init
  assert( g-gap-size 1024 = )
  bigbuf 2048 32 fill
  1 loading? !
  bigbuf 2048 g-insert          \ g-insert 内部 g-grow 扩容
  0 loading? !
  assert( g-size 2048 = )
  assert( g-cap @ 2048 >= )
  assert( 0 g-char-at 32 = )
  assert( 2047 g-char-at 32 = )
  t-ok ;

\ g-delete-forward：删 gap 后（back 区）字节
: test-gap-delete-forward
  s" abcdef" tb-set
  3 g-move-gap                  \ gap 在 c 后，back="def"
  2 g-delete-forward            \ 删 d e -> "abcf"
  assert( g-size 4 = )
  assert( 0 g-char-at [char] a = )
  assert( 2 g-char-at [char] c = )
  assert( 3 g-char-at [char] f = )
  t-ok ;

\ lix-nl-count：数 [pos,pos+len) 里的 \n
: test-gap-lix-nl-count
  s\" ab\ncd\nef" tb-set             \ "ab\ncd\nef" 两个 \n
  assert( 0 8 lix-nl-count 2 = )
  assert( 0 3 lix-nl-count 1 = )
  assert( 3 3 lix-nl-count 1 = )     \ 字节 3..5 = c d \n
  assert( 3 2 lix-nl-count 0 = )     \ 字节 3..4 = c d
  t-ok ;

\ lix-find-gt：第一个 lix@i > epos 的索引
: test-gap-lix-find-gt
  s\" ab\ncd\nef" tb-set
  lix-build
  assert( 2 lix-find-gt 1 = )
  assert( 3 lix-find-gt 2 = )
  assert( 6 lix-find-gt 3 = )     \ 无 > 6，返回 lix-n
  t-ok ;

\ lix-shift-tail：尾部 [start, lix-n) 每个 +delta
: test-gap-lix-shift-tail
  s\" ab\ncd\nef" tb-set
  lix-build
  1 2 lix-shift-tail
  assert( 0 lix@ 0 = )
  assert( 1 lix@ 5 = )
  assert( 2 lix@ 8 = )
  t-ok ;

\ lix-ensure / lix-lines
: test-gap-lix-ensure
  s\" ab\ncd" tb-set
  1000 lix-ensure
  assert( lix-cap @ 1000 >= )
  assert( lix-lines 2 = )
  t-ok ;

\ g-insert 置 lix-dirty（编辑失效标记）
: test-gap-lix-touch
  s" abc" tb-set
  lix-build
  assert( lix-dirty @ 0= )
  0 loading? !  1 g-move-gap  s" x" g-insert
  assert( lix-dirty @ 1 = )
  t-ok ;
