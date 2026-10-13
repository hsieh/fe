\ minimal raw mode (termios bindings) + terminal size (ioctl TIOCGWINSZ)

require libcc.fs
c-library term
  \c #include <termios.h>
  \c #include <unistd.h>
  c-function tcgetattr tcgetattr n a -- n ( fd termios -- r )
  c-function tcsetattr tcsetattr n n a -- n ( fd opt termios -- r )
  c-function cfmakeraw cfmakeraw a -- void ( termios -- )
  \ disable VDISCARD (Ctrl-o is swallowed on some platforms/terminals; cfmakeraw does not always clear it)
  \c void loc_noflush(void *tp) { ((struct termios*)tp)->c_cc[VDISCARD] = _POSIX_VDISABLE; }
  c-function loc-noflush loc_noflush a -- void
end-c-library

256 constant /termios

create termios-old /termios allot
create termios-raw /termios allot
variable raw-saved  0 raw-saved !

\ Set raw. Save the "original" terminal state only on the first call (otherwise resetting raw each loop iteration would store the raw state
\ as termios-old by mistake, and sane-mode would restore raw on exit, requiring a terminal reset).
: raw-mode ( -- )
  raw-saved @ 0= if
    0 termios-old tcgetattr drop
    1 raw-saved !
  then
  termios-old termios-raw /termios move
  termios-raw cfmakeraw
  termios-raw loc-noflush
  0 0 termios-raw tcsetattr drop ;

: sane-mode ( -- )
  raw-saved @ if  0 0 termios-old tcsetattr drop  then ;

\ Turn off mouse tracking: the environment may leave it on (xterm etc.), otherwise moving the mouse sends \e[<b;x;yM.
\ read-key can also swallow such sequences, but turning it off at startup is simpler.
: mouse-off
  s\" \e[?9l\e[?1000l\e[?1002l\e[?1003l\e[?1005l\e[?1006l\e[?1007l\e[?1015l\e[?1016l" type ;

\ Terminal size: use Gforth's rows/cols.
\ Gforth's implementation (engine/signals.c:get_winsize) is itself ioctl(TIOCGWINSZ),
\ with $LINES/$COLUMNS fallback, defaults, and automatic SIGWINCH updates — more complete than a hand-written ioctl,
\ and it also does not interact with the terminal. Clamping is done in fe.fs update-screen-size.
: term-rows ( -- rows ) rows ;
: term-cols ( -- cols ) cols ;
