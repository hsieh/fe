\ ===== test/test-utf8.fs — UTF-8 width/byte/offset conversion =====
\ depends on gapbuf's g-char-at and Gforth's xc-width (= libc wcwidth, EAW).

include ../utf8.fs

: test-u8-n
  s" a中" tb-set                  \ 'a' 1 byte; '中' 3 bytes
  assert( 0 u8-n 1 = )
  assert( 1 u8-n 3 = )
  t-ok ;

: test-u8-wl-ascii
  s" abc" tb-set
  0 0 u8-wl { wa na }  assert( wa 1 = )  assert( na 1 = )
  2 2 u8-wl { wb nb }  assert( wb 1 = )  assert( nb 1 = )
  t-ok ;

: test-u8-wl-wide
  s" a中" tb-set                  \ '中' width 2, 3 bytes
  0 0 u8-wl { wa2 na2 }  assert( wa2 1 = )  assert( na2 1 = )
  1 1 u8-wl { wb2 nb2 }  assert( wb2 2 = )  assert( nb2 3 = )
  t-ok ;

\ tab: to the next multiple-of-8 tab stop (tab-width=8)
: test-u8-wl-tab
  s" a	b" tb-set                  \ a + tab + b
  0 0 u8-wl { wa3 na3 }  assert( wa3 1 = )  assert( na3 1 = )
  1 1 u8-wl { wb3 nb3 }  assert( wb3 7 = )  assert( nb3 1 = )   \ col 1 -> next 8, width 7
  t-ok ;

: test-u8-width-bi-col
  s" a中b" tb-set                 \ width 1+2+1 = 4; bytes 1+3+1 = 5
  0 5 u8-width { twid }  assert( twid 4 = )
  0 5 0 u8-bi { tbid0 }  assert( tbid0 0 = )      \ col 0 -> byte 0
  0 5 1 u8-bi { tbid1 }  assert( tbid1 1 = )      \ col 1 -> byte 1 ('中')
  0 5 3 u8-bi { tbid3 }  assert( tbid3 4 = )      \ col 3 -> byte 4 ('b')
  0 5 4 u8-col { tcol4 }  assert( tcol4 3 = )   \ byte 4 -> col 3 ('b')
  0 5 1 u8-col { tcol1 }  assert( tcol1 1 = )   \ byte 1 -> col 1 ('中')
  t-ok ;

: test-u8-prev
  s" a中" tb-set
  assert( 1 u8-prev 0 = )         \ before '中' is 'a'
  assert( 4 u8-prev 1 = )         \ the char before the end, '中', starts at 1
  t-ok ;

\ utf8-emit: code point → UTF-8 byte stream
create emit-buf 8 allot
variable emit-len 0 emit-len !
: emit-collect ( c -- )  emit-buf emit-len @ + c!  1 emit-len +! ;
: emit-utf8 ( cp -- a u )  0 emit-len !  ['] emit-collect utf8-emit  emit-buf emit-len @ ;

: test-utf8-emit
  0x41 emit-utf8 2dup assert( dup 1 = ) assert( emit-buf c@ 0x41 = ) 2drop
  0x4E2D emit-utf8                      \ 中 U+4E2D = E4 B8 AD
  assert( emit-len @ 3 = )
  assert( emit-buf c@ 0xE4 = )
  assert( emit-buf 1+ c@ 0xB8 = )
  assert( emit-buf 2 + c@ 0xAD = )
  0x1F600 emit-utf8                     \ 😀 U+1F600 = F0 9F 98 80
  assert( emit-len @ 4 = )
  assert( emit-buf c@ 0xF0 = )
  assert( emit-buf 3 + c@ 0x80 = )
  t-ok ;
