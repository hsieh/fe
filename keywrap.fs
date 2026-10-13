\ ===== keywrap.fs — keep ekey; the outer layer consumes "unknown escape sequences" =====
\ Background: Gforth's ekey/esc-prefix, when it fails to match a known key sequence, unkeys the bytes it has read back into
\ the input buffer and then returns #esc(27); those bytes are then read back as ordinary keys. Terminal replies
\ (such as xterm's \e[?64;...c) leak out as ?, 6, 4, ... this way.
\ Countermeasure (same as qemacs/termkey): outside ekey, if what follows ESC looks like CSI/SS3/OSC,
\ consume the whole sequence and return 0 (ignore); otherwise push the first byte back and return 27 (a real ESC).
\ Note: ekey returns K-* directly for "known" sequences such as arrow keys; they never reach here.

create esc1 1 allot

create csibuf 16 allot
variable csilen 0 csilen !

\ After reading CSI's '[': read the sequence, recognize Ctrl-Enter (CSI-u "13;5u" / modifyOtherKeys "27;5;13~"),
\ return the key code (13|ctrl-mask) or 0 (other unknown CSI, swallowed)
: read-csi ( -- c )
  0 csilen !
  32 0 ?do
    key? 0= if unloop 0 exit then
    key { b }
    b 0x40 >= b 0x7E <= and if           \ terminator byte
      b csibuf csilen @ + c!  1 csilen +!
      unloop                            \ exit the loop first, then decide the return value
      csibuf csilen @  s" 13;5u"    compare 0= if  13 k-ctrl-mask or  exit  then
      csibuf csilen @  s" 27;5;13~" compare 0= if  13 k-ctrl-mask or  exit  then
      csibuf csilen @  s" 13;2u"    compare 0= if  13 k-shift-mask or  exit  then
      csibuf csilen @  s" 27;2;13~" compare 0= if  13 k-shift-mask or  exit  then
      0 exit
    then
    b csibuf csilen @ + c!  1 csilen +!
    csilen @ 15 >= if unloop 0 exit then
  loop
  0 ;

\ After reading SS3's 'O': swallow one more byte
: consume-ss3 ( -- )  key? if key drop then ;

\ After reading OSC's ']': swallow up to BEL(7) or ST(ESC \), bounded
: consume-osc ( -- )
  256 0 ?do
    key? 0= if unloop exit then
    key { b }
    b 7 = if unloop exit then
    b 27 = if key? if key drop then unloop exit then
  loop ;

\ Read a key: the result of ekey; if it is an escape sequence ekey does not know, swallow it and return 0

: read-key ( -- c )
  ekey { c }
  c k-winch = if 0 exit then            \ terminal resize: not a key (the main loop polls the size and redraws)
  c 27 <> if c exit then
  key? 0= if 27 exit then                 \ no byte after ESC = a real ESC
  key { d }
  d 91 = if read-csi exit then              \ '['  CSI (recognize Ctrl-Enter, swallow others)
  d 79 = if consume-ss3 0 exit then       \ 'O'  SS3
  d 93 = if consume-osc 0 exit then       \ ']'  OSC
  \ Otherwise: push d back and return ESC (preserving Alt-key / repeated-ESC cases)
  d esc1 c!  esc1 1 unkeys
  27 ;
