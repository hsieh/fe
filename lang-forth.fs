\ ===== lang-forth.fs — Forth syntax rules (pure data)=====
\ engine in syntax.fs; to add a language, copy this file and edit the rule table (the engine is untouched).

\ ===== language definition (data): Forth =====
\ rule order is priority: keyword > number > line comment > block comment > string
: kws$  s"  #> #s ' */ */mod +loop , . .r /mod 0< 0<> 0= 0> 1+ 1- 2! 2* 2/ 2>r 2@ 2drop 2dup 2over 2r> 2r@ 2swap : :noname ; <# <> >body >in >number >r ?do ?dup @ [] [char] ] abort abs accept action-of again align aligned allocate allot and ansi.fs base begin bl buffer: c! c, c@ case cell+ cells char char+ chars compile, constant constant, count cr create decimal defer defer! defer@ depth do does> drop dup else emit endcase endof environment? erase evaluate execute exit false fill find fm/mod free here hex hold holds i if immediate in include invert is j key leave literal loop lshift m* marker max min mod move negate nip of or over pad parse parse-name pick postpone quit r> r@ recurse refill require resize restore-input roll rot rshift s>d save-input sign sm/rem source source-id space spaces state swap then throw to true tuck type u. u.r u> u< um* um/mod unloop until unused value variable while within word xor [ ['] [compile] +to +! -rot 2nip bounds defers slurp-fid outfile-execute ?dup-0=-if u>= u<= u<> \\ ;" ;
\ single-character delimiters (created with create to avoid s" escaping issues)
create d-bs  92 c,       \ backslash
create d-lp  40 c,       \ (
create d-rp  41 c,       \ )
create d-dq  34 c,       \ "
create d-sq  115 c, 34 c,   \ s"
create d-dotq 46 c, 34 c,   \ ."
create d-sbs  115 c, 92 c, 34 c,   \ s\"
create d-dotbs 46 c, 92 c, 34 c,   \ .\"

: rules-forth
  rules-clear
  kws$               SI-KEYWORD rule-kw
                     SI-NUMBER rule-num
  d-sbs 3  d-dq 1    SI-STRING rule-range      \ s\" ... " (Gforth escaping; before \ comments)
  d-dotbs 3  d-dq 1  SI-STRING rule-range      \ .\" ... "
  d-bs 1  0 0        SI-COMMENT rule-range-wb  \ \ to end of line (only an independent token is a comment)
  d-lp 1  d-rp 1     SI-COMMENT rule-range     \ ( ... ) comment (may span lines)
  d-sq 2  d-dq 1     SI-STRING rule-range      \ s" ... " string
  d-dotq 2  d-dq 1   SI-STRING rule-range      \ ." ... " string
  d-dq 1  d-dq 1     SI-STRING rule-range      \ " ... " string
                     SI-NONE rule-word ;     \ identifier (consumes the word, uncolored)

\ register: extension "fs" -> rules-forth
: register-language  s" fs" ['] rules-forth register-lang  s" forth" ['] rules-forth register-lang-name ;
register-language
