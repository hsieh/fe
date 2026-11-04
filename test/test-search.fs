\ ===== test/test-search.fs — plain search (match/direction/word-boundary/ignore-case) =====
\ only the plain branch of search.fs (regex-search=0) is tested; no POSIX regex.
\ (regex.fs/search.fs are already included in harness.fs)

\ set a plain pattern
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
  assert( 11 search-fwd -1 = )     \ no more
  assert( 11 search-back 8 = )
  assert( 3 search-back 0 = )
  assert( 0 search-back 0 = )
  t-ok ;

: test-search-word-boundary
  s" foobar foo" tb-set
  s" foo" ss-pat
  1 word-boundary !               \ require a word boundary
  assert( 7 match-at? 10 = )      \ "foo" (the second word)
  assert( 0 match-at? 0 = )       \ "foobar"'s foo: the trailing boundary is 'b' (a word char) → no match
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
