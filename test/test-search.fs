\ ===== test/test-search.fs — 朴素搜索（匹配/方向/词边界/忽略大小写）=====
\ 只测 search.fs 的朴素分支（regex-search=0），不碰 POSIX 正则。
\ （regex.fs/search.fs 已在 harness 里 include）

\ 设置朴素 pattern
: ss-pat ( a u -- )
  0 patlen !  0 regex-search !  0 word-boundary !  0 ignorecase !
  { a u }
  u 0 ?do  a i + c@ pat-add  loop ;

: test-search-match
  s" hello world" tb-set
  s" world" ss-pat
  assert( 6 match-at? 11 = )
  assert( 0 match-at? 0 = )
  assert( 5 match-at? 0 = )
  t-ok ;

: test-search-fwd-back
  s" foo bar foo" tb-set
  s" foo" ss-pat
  assert( 0 search-fwd 0 = )
  assert( 1 search-fwd 8 = )
  assert( 11 search-fwd -1 = )     \ 无更多
  assert( 11 search-back 8 = )
  assert( 3 search-back 0 = )
  assert( 0 search-back 0 = )
  t-ok ;

: test-search-word-boundary
  s" foobar foo" tb-set
  s" foo" ss-pat
  1 word-boundary !               \ 要求词边界
  assert( 7 match-at? 10 = )      \ "foo"（第二个词）
  assert( 0 match-at? 0 = )       \ "foobar" 的 foo：后边界是 'b'（词字符）→ 不匹配
  assert( 7 search-fwd 7 = )
  t-ok ;

: test-search-ignorecase
  s" Hello WORLD" tb-set
  s" hello" ss-pat
  1 ignorecase !
  assert( 0 match-at? 5 = )
  s" world" ss-pat
  1 ignorecase !
  assert( 6 match-at? 11 = )
  t-ok ;
