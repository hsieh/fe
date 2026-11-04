\ ===== test/test-gapbuf.fs — gap buffer core operations =====

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

\ content with newlines (s\" multi-line string): lix line-start offsets + line count
: test-gap-lix
  s\" ab\ncd\nef" tb-set                 \ "ab\ncd\nef" = 8 bytes
  assert( g-size 8 = )                 \ "ab\ncd\nef" = 8 bytes
  lix-build
  assert( lix-n @ 3 = )
  assert( 0 lix@ 0 = )
  assert( 1 lix@ 3 = )
  assert( 2 lix@ 6 = )
  assert( g-nlines 3 = )
  t-ok ;

\ g-contents concatenates front + back (correct even when the gap is in the middle)
: test-gap-contents
  s" hello world" tb-set
  \ move the gap to the middle (offset 5), then the contents should be unchanged
  5 g-move-gap
  g-contents { a u }
  assert( u 11 = )
  assert( a 11 s" hello world" compare 0= )
  a free throw
  t-ok ;

\ g-move-gap does not change the logical contents
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

\ deletion: forward/backward after moving the gap to a position
: test-gap-delete
  s" abcdef" tb-set
  3 g-move-gap                  \ gap after c (offset 3)
  2 g-delete-backward           \ delete b c -> "adef"
  assert( g-size 4 = )
  assert( 0 g-char-at [char] a = )
  assert( 1 g-char-at [char] d = )
  assert( 3 g-char-at [char] f = )
  t-ok ;

: test-gap-insert-mid
  s" ac" tb-set
  1 g-move-gap                  \ gap after a
  s" b" g-insert                \ "abc"
  assert( g-size 3 = )
  assert( 1 g-char-at [char] b = )
  assert( 2 g-char-at [char] c = )
  t-ok ;

\ lix incremental edit: a newline-free insert only shifts later line offsets (no rebuild)
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

\ g-gap-size / g-grow (inserting past the initial 1024 triggers growth)
create bigbuf 2048 allot
: test-gap-grow
  tb-init
  assert( g-gap-size 1024 = )
  bigbuf 2048 32 fill
  1 loading? !
  bigbuf 2048 g-insert          \ g-insert grows via g-grow internally
  0 loading? !
  assert( g-size 2048 = )
  assert( g-cap @ 2048 >= )
  assert( 0 g-char-at 32 = )
  assert( 2047 g-char-at 32 = )
  t-ok ;

\ g-delete-forward: delete bytes after the gap (the back region)
: test-gap-delete-forward
  s" abcdef" tb-set
  3 g-move-gap                  \ gap after c, back="def"
  2 g-delete-forward            \ delete d e -> "abcf"
  assert( g-size 4 = )
  assert( 0 g-char-at [char] a = )
  assert( 2 g-char-at [char] c = )
  assert( 3 g-char-at [char] f = )
  t-ok ;

\ lix-nl-count: count \n in [pos,pos+len)
: test-gap-lix-nl-count
  s\" ab\ncd\nef" tb-set             \ "ab\ncd\nef" two \n
  assert( 0 8 lix-nl-count 2 = )
  assert( 0 3 lix-nl-count 1 = )
  assert( 3 3 lix-nl-count 1 = )     \ bytes 3..5 = c d \n
  assert( 3 2 lix-nl-count 0 = )     \ bytes 3..4 = c d
  t-ok ;

\ lix-find-gt: index of the first lix@i > epos
: test-gap-lix-find-gt
  s\" ab\ncd\nef" tb-set
  lix-build
  assert( 2 lix-find-gt 1 = )
  assert( 3 lix-find-gt 2 = )
  assert( 6 lix-find-gt 3 = )     \ none > 6, returns lix-n
  t-ok ;

\ lix-shift-tail: add delta to every entry in the tail [start, lix-n)
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

\ g-insert sets lix-dirty (edit-invalidation flag)
: test-gap-lix-touch
  s" abc" tb-set
  lix-build
  assert( lix-dirty @ 0= )
  0 loading? !  1 g-move-gap  s" x" g-insert
  assert( lix-dirty @ 1 = )
  t-ok ;
