\ ===== utf8.fs — UTF-8 display width (byte offsets into the gap buffer)=====
\ xc-width is provided by Gforth's utf-8.fs; some Gforth builds (e.g. WSL/Debian) do not
\ load it automatically, or even lack it. Try require first; if still absent, fall back to libc wcwidth.
require utf-8.fs
[undefined] xc-width [IF]
require libcc.fs
c-library c-utf8-width
  \c #include <wchar.h>
  c-function c_wcwidth wcwidth n -- n
end-c-library
: xc-width ( xc -- n ) c_wcwidth ;
[THEN]
\ Depends on gapbuf.fs g-char-at and Gforth xc-width (= C library wcwidth, implementing Unicode East Asian Width).
\ Widths: wide/fullwidth = 2, combining/zero-width = 0, others = 1, tab = to the next tab stop (multiple of 8).
\ dcol = display column before the character (tab width depends on it).

\ character at offset -> (display width, byte count); dcol = display column before the character
: u8-wl { offset dcol }
  offset g-char-at { b0 }
  b0 9 = if                                      \ tab -> to the next tab stop
    tab-width @ 1 max { tw }
    tw dcol tw mod - 1 exit
  then
  b0 0x80 < if 1 1 exit then
  b0 0xC0 < if 1 1 exit then                     \ stray continuation byte
  offset 1+ g-char-at 0x3F and { c1 }
  b0 0xE0 < if
    b0 0x1F and 6 lshift c1 or  xc-width { w }  w 0> if w else 2 then  2 exit
  then
  offset 2 + g-char-at 0x3F and { c2 }
  b0 0xF0 < if
    b0 0x0F and 12 lshift c1 6 lshift or c2 or  xc-width { w }  w 0> if w else 2 then  3 exit
  then
  offset 3 + g-char-at 0x3F and { c3 }
  b0 0x07 and 18 lshift c1 12 lshift or c2 6 lshift or c3 or
  xc-width { w }  w 0> if w else 2 then  4 ;

\ byte count of a character (independent of column)
: u8-n ( offset -- n )  0 u8-wl nip ;

\ display width of [off, off+len)
: u8-width { off len }
  0 { w } 0 { i }
  begin i len < while
    off i + w u8-wl { cw cn }
    w cw + to w
    i cn + to i
  repeat
  w ;

\ byte offset (relative to off) of display column dcol within [off, off+len)
: u8-bi { off len dcol }
  0 { i } 0 { c }
  begin i len <  c dcol <  and while
    off i + c u8-wl { cw cn }
    c cw + to c
    i cn + to i
  repeat
  i ;

\ display column of byte offset bi within [off, off+len)
: u8-col { off len bi }
  0 { i } 0 { c }
  begin i bi <  i len <  and while
    off i + c u8-wl { cw cn }
    c cw + to c
    i cn + to i
  repeat
  c ;

\ start of the previous character (skip continuation bytes 0x80-0xBF)
: u8-prev { pos }
  pos 1- { i }
  begin i 0> i g-char-at 0x80 0xC0 within and while
    i 1- to i
  repeat
  i ;

\ code point -> UTF-8 byte stream; execute xt for each byte (for insert/search strings)
: utf8-emit { cp xt -- }
  cp 0x80 < if cp xt execute exit then
  cp 0x800 < if
    cp 6 rshift 0xC0 or xt execute
    cp 0x3F and 0x80 or xt execute   exit then
  cp 0x10000 < if
    cp 12 rshift 0xE0 or xt execute
    cp 6 rshift 0x3F and 0x80 or xt execute
    cp 0x3F and 0x80 or xt execute   exit then
  cp 18 rshift 0xF0 or xt execute
  cp 12 rshift 0x3F and 0x80 or xt execute
  cp 6 rshift 0x3F and 0x80 or xt execute
  cp 0x3F and 0x80 or xt execute ;
