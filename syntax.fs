\ ===== syntax.fs — syntax-highlighting engine (rule table + generic interpreter)=====
\ The engine knows no language; a language = a set of rules (data), see lang-*.fs.
\ Interface: core provides hl-xt( a -- hlend attr ) + style>fb( attr -- ); this file provides syn-attr.

\ ===== syntax highlighting: rule table + generic engine (tier 2: data-driven)=====
\ a language = a rule table (data); the engine knows no language.
\   rule: ( kind a1 a2 style )
\   kind: 0=keyword set 1=number 2=range(begin,end,may span lines) 3=identifier 4=single character
\ Multiline: when a range is unclosed, sen = rule number + 1; the next line continues with the same sst at its start.
variable hlmap 0 hlmap !          \ current-line highlight map (dynamic, grows with the line length)
variable hlmap-cap 0 hlmap-cap !
variable hlmaplen  0 hlmaplen !

: hlmap-ensure { n -- }
  n hlmap-cap @ u<= if exit then
  n 2* 256 max { newcap }
  hlmap @ if
    hlmap @ newcap resize throw hlmap !
  else
    newcap allocate throw hlmap !
  then
  newcap hlmap-cap ! ;

\ ===== line-highlight window cache (P1)=====
\ each slot stores (line number, hlmap pointer, length, capacity); line mod /hlcache maps directly,
\ sequential scrolling hits adjacent lines and skips build-line. Invalidated wholesale on edit / buffer switch.
64 constant /hlcache
create hlc-line /hlcache cells allot
create hlc-ptr  /hlcache cells allot
create hlc-len  /hlcache cells allot
create hlc-cap  /hlcache cells allot
variable hlc-s 0 hlc-s !
variable hl-was-dirty 0 hl-was-dirty !

variable hlc-i 0 hlc-i !
: hlc-reset   \ no ?do (?do corrupts the return stack in a deep catch chain)
  0 hlc-i !
  begin hlc-i @ /hlcache < while
    -1 hlc-line hlc-i @ cells + !
    1 hlc-i +!
  repeat ;
hlc-reset

\ activate slot hlc-s: hlmap/hlmap-cap/hlmaplen point at that slot's buffers
: hlc-activate ( -- )
  hlc-ptr hlc-s @ cells + @ hlmap !
  hlc-cap  hlc-s @ cells + @ hlmap-cap !
  hlc-len  hlc-s @ cells + @ hlmaplen ! ;

\ store the current hlmap trio back into slot hlc-s
: hlc-save ( -- )
  hlmap @     hlc-s @ cells hlc-ptr + !
  hlmap-cap @ hlc-s @ cells hlc-cap  + !
  hlmaplen @  hlc-s @ cells hlc-len  + ! ;

variable lstate  0 lstate !        \ per-line start state (dynamic, grows with the line count)
variable lstate-cap 0 lstate-cap !
: lstate-ensure { n -- }
  n lstate-cap @ u<= if exit then
  n 2* 4096 max { newcap }
  \ resize keeps the already-computed state (free+allocate would lose the contents);
  \ only the first allocation needs clearing (lstate[0] must be 0)
  lstate @ if
    lstate @ newcap resize throw lstate !
  else
    newcap allocate throw lstate !
    lstate @ newcap erase
  then
  newcap lstate-cap ! ;

variable lst-max  0 lst-max !
variable es-k  0 es-k !
variable eo-e  0 eo-e !
variable sa-e  0 sa-e !
variable sls 0 sls !   variable slw 0 slw !   variable si  0 si !
variable stok 0 stok ! variable stln 0 stln ! variable sst 0 sst !  variable sen 0 sen !
variable sidx 0 sidx ! variable sk  0 sk !   variable rr 0 rr !    variable rech 0 rech !
variable rcur 0 rcur ! variable rhit 0 rhit !
create ktmp  96 allot
create ktok  96 allot
92 constant ktok-cap                        \ usable ktok limit (< 96, leaving headroom)

24 constant max-rules
create rules  max-rules 6 * cells allot
variable nrules  0 nrules !
0 constant R-KW  1 constant R-NUM  2 constant R-RANGE  3 constant R-WORD  4 constant R-CHAR
5 constant R-REGEX   \ anchored regex (a1=pattern addr a2=len a3=compiled pointer)
6 constant R-RANGE-WB   \ same as R-RANGE, but the first character of the start string must be at a word boundary (preceded by whitespace/line start; Forth's \ comments)

\ style index (what hlmap stores); mapped to core's S-* attributes
0 constant SI-NONE
1 constant SI-COMMENT
2 constant SI-STRING
3 constant SI-NUMBER
4 constant SI-KEYWORD
5 constant SI-TYPE
6 constant SI-BUILTIN
7 constant SI-CONST
8 constant SI-PREPROC
create sidx-attrs
  0 , S-COMMENT , S-STRING , S-NUMBER , S-KEYWORD ,
  S-TYPE , S-BUILTIN , S-CONST , S-PREPROC ,
: sidx>attr ( idx -- attr )
  dup 0>= over 8 <= and if  cells sidx-attrs + @  else drop 0  then ;

\ rule record 6 cells: ( kind a1 a2 a3 a4 style )
\   R-KW    a1=keyword string addr a2=len
\   R-NUM / R-WORD  -
\   R-CHAR  a1=character
\   R-RANGE a1=start string addr a2=start string len a3=end string addr a4=end string len
\           (end string len=0 means "to end of line, no crossing")
: rule ( kind a1 a2 a3 a4 style -- )
  nrules @ max-rules >= if  2drop 2drop 2drop exit then
  nrules @ 6 * cells rules + >r
  r@ 5 cells + !  r@ 4 cells + !  r@ 3 cells + !  r@ 2 cells + !  r@ 1 cells + !  r@ !
  r> drop  1 nrules +! ;
: rule@ { r k -- v }  rules r 6 * k + cells + @ ;
: rules-clear  0 nrules ! ;
\ language data construction: copy (a u) into buf (clamped to cap), record the length into lenvar
: kw-put { a u buf cap lenvar -- }
  u cap min { n }
  a buf n move
  n lenvar ! ;
\ convenience constructors (used by language files)
: rule-kw    { a u style -- }          R-KW    a u 0 0 style rule ;
: rule-num   { style -- }              R-NUM   0 0 0 0 style rule ;
: rule-word  { style -- }              R-WORD  0 0 0 0 style rule ;
: rule-char  { c style -- }            R-CHAR  c 0 0 0 style rule ;
: rule-regex { a u style -- }           \ compile an anchored regex into the rule; skip on failure
  a u 0 re-anchor-compile ?dup 0= if exit then
  >r  R-REGEX a u r> 0 style rule ;
: rule-range { ba bu ea eu style -- }  R-RANGE ba bu ea eu style rule ;
: rule-range-wb { ba bu ea eu style -- }  R-RANGE-WB ba bu ea eu style rule ;

: sc ( -- c )  sls @ si @ + g-char-at ;
: tch { k -- c }  sls @ stok @ + k + g-char-at ;
: syn-digit? ( c -- f )  dup 48 >= swap 57 <= and ;
: syn-space? ( c -- f )  dup 32 = swap 9 = or ;
: idchar? ( c -- f )                  \ letter/digit/underscore
  dup 48 >= over 57 <= and  swap
  dup 65 >= over 90 <= and  swap
  dup 97 >= over 122 <= and  swap
  95 =  or or or ;
variable tl-n  0 tl-n !
: num-len ( -- )                    \ length of the number at the current position (optional leading '-') -> tl-n
  0 tl-n !
  si @ slw @ >= if exit then
  sc 45 = if
    \ leading '-': only counts when followed by a digit (otherwise -- / -x are not numbers)
    si @ 1+ slw @ >= if exit then
    sls @ si @ 1+ + g-char-at syn-digit? 0= if exit then
    1 tl-n !
  then
  begin si @ tl-n @ + slw @ < while
    sls @ si @ + tl-n @ + g-char-at syn-digit? 0= if exit then
    tl-n @ 1+ tl-n !
  repeat ;
: word-len ( -- )                    \ length of the identifier at the current position -> tl-n
  0 tl-n !
  begin si @ tl-n @ + slw @ < while
    sls @ si @ + tl-n @ + g-char-at idchar? 0= if exit then
    tl-n @ 1+ tl-n !
  repeat ;
: paint { idx a b -- }  b a ?do  idx hlmap @ i + c!  loop ;
: skip-token  si @ stok !
  begin si @ slw @ < while  sc syn-space? 0= while  si @ 1+ si !  repeat then ;
: tok>ktok                            \ copy the token into ktok; kTok is only 96B and must be clamped
  stln @ ktok-cap min stln !
  stln @ 0 ?do  i tch  ktok i + c!  loop ;
: numtok? ( -- f )                    \ token = -?[0-9]+
  0 tch 45 = if 1 else 0 then sk !
  sk @ stln @ >= if false exit then
  true  stln @ sk @ ?do  i tch syn-digit? 0= if  drop false unloop exit  then  loop ;
\ look for (na,n) within (ka,ku); return true if found.
\ Do not use Gforth's search: it crashes in a deep call chain (return-stack/locals interaction). Pure byte comparison, bounds-safe.
\ Optimization: candidate strings are space-separated (kwmatch? already wraps the token in spaces),
\ so first do a two-level quick filter ("candidate start is a space + first character matches"), then compare.
variable kf-a  0 kf-a !   variable kf-u  0 kf-u !
variable kf-na 0 kf-na !  variable kf-nn 0 kf-nn !
: kw-find ( ka ku na n -- f )
  kf-nn !  kf-na !  kf-u !  kf-a !
  kf-nn @ 0= if false exit then
  kf-u @ kf-nn @ < if false exit then
  kf-u @ kf-nn @ - 1+ 0 ?do
    kf-a @ i + c@ 32 = if
      kf-a @ i + 1+ c@  kf-na @ 1+ c@  = if
        kf-a @ i +  kf-nn @  kf-na @  kf-nn @  compare 0= if  true unloop exit  then
      then
    then
  loop false ;

: kwmatch? { a u ka ku -- f }
  u 0= u 76 > or if false exit then
  32 ktmp c!  a ktmp 1+ u move  32 ktmp 1+ u + c!
  ka ku  ktmp u 2 +  kw-find ;
: match-here? { a u -- f }            \ does the current position start with string a u
  u 0= if false exit then
  si @ u + slw @ > if false exit then
  true  u 0 ?do  a i + c@  sls @ si @ + i + g-char-at  <> if  drop false unloop exit  then  loop ;

\ current rule fields (global, to avoid deep locals chains)
variable r-ba  variable r-bu  variable r-ea  variable r-eu  variable r-sty
variable r-esc 0 r-esc !           \ string rule: \ escapes the next character
: load-rule
  rcur @ 1 rule@ r-ba !  rcur @ 2 rule@ r-bu !
  rcur @ 3 rule@ r-ea !  rcur @ 4 rule@ r-eu !
  rcur @ 5 rule@ r-sty !
  r-sty @ SI-STRING = r-esc ! ;

\ --- each kind (on a hit, return true, write hlmap, and advance si) ---
: try-kw
  \ (1) identifier-style tokens (C/Go: return; -> return)
  word-len  tl-n @ 0> if
    si @ stok !  tl-n @ stln !  tok>ktok
    ktok stln @  rcur @ 1 rule@  rcur @ 2 rule@  kwmatch? if
      rcur @ 5 rule@  stok @  si @ tl-n @ +  paint
      si @ tl-n @ + si !  true exit
    then
  then
  \ (2) space-separated tokens (Forth: : ; +loop ?dup ...)
  si @ stok !  skip-token  si @ stok @ - stln !
  stln @ 0> if
    tok>ktok
    ktok stln @  rcur @ 1 rule@  rcur @ 2 rule@  kwmatch? if
      rcur @ 5 rule@  stok @  si @  paint  true exit
    then
  then
  stok @ si !  false ;
: try-num
  num-len  tl-n @ 0= if false exit then
  rcur @ 5 rule@  si @  si @ tl-n @ +  paint
  si @ tl-n @ + si !  true ;
: try-word
  word-len  tl-n @ 0= if false exit then
  rcur @ 5 rule@  si @  si @ tl-n @ +  paint
  si @ tl-n @ + si !  true ;
: try-char
  sc  rcur @ 1 rule@  <> if false exit then
  rcur @ 5 rule@  si @  si @ 1+  paint  si @ 1+ si !  true ;
variable trg-rp 0 trg-rp !
variable trg-mlen 0 trg-mlen !
: try-regex ( -- hit )                \ anchored match at sls+si (globals, so the deep chain does not corrupt the return stack)
  \ a pattern starting with ^ = anchored to the line start (only matches at si=0 to avoid false matches in the line content)
  rcur @ 1 rule@ ?dup if  c@ 94 = if  si @ 0<> if false exit then  then  then
  rcur @ 3 rule@ ?dup 0= if false exit then
  trg-rp !
  snap-build
  trg-rp @  snap @  sls @ slw @ +  sls @ si @ +  re-ptr-match   \ len = end of the current line; limit the match range to this line
  trg-mlen !
  trg-mlen @ 0= if false exit then
  slw @ si @ - trg-mlen @ min trg-mlen !
  trg-mlen @ 0<= if false exit then
  rcur @ 5 rule@  si @  si @ trg-mlen @ +  paint
  si @ trg-mlen @ + si !  true ;
\ does the current position hit the end string; on a hit, write the color + advance si, return true
: range-end? ( -- f )
  r-ea @ r-eu @ match-here? 0= if false exit then
  r-eu @ 0 ?do  r-sty @ hlmap @ si @ + c!  si @ 1+ si !  loop
  true ;

\ string escape: if the current char is \, color both \ and the next character and skip (return true)
: range-esc ( -- f )
  r-esc @ 0= if false exit then
  si @ slw @ >= if false exit then
  sls @ si @ + g-char-at 92 <> if false exit then
  r-sty @ hlmap @ si @ + c!   si @ 1+ si !      \ color the \
  si @ slw @ < if
    r-sty @ hlmap @ si @ + c!   si @ 1+ si !    \ color the escaped character
  then
  true ;

: try-range ( -- hit )
  load-rule
  rcur @ 0 rule@ R-RANGE-WB = if        \ WB: the first character of the start string must be preceded by whitespace/line start (Forth \ comments)
    si @ 0> if
      si @ 1- g-char-at syn-space? 0= if false exit then
    then
  then
  r-ba @ r-bu @ match-here? 0= if false exit then
  r-bu @ 0 ?do  r-sty @ hlmap @ si @ + c!  si @ 1+ si !  loop
  begin si @ slw @ < while
    range-esc 0= if
      range-end? if true exit then
      r-sty @ hlmap @ si @ + c!  si @ 1+ si !
    then
  repeat
  r-eu @ 0= if true exit then          \ empty end string = to end of line, no crossing
  rcur @ 1+ sen !  true ;
: carry-range ( -- )                   \ continuation at line start
  rr @ 3 rule@ r-ea !  rr @ 4 rule@ r-eu !  rr @ 5 rule@ r-sty !
  r-sty @ SI-STRING = r-esc !
  begin si @ slw @ < while
    range-esc 0= if
      range-end? if exit then
      r-sty @ hlmap @ si @ + c!  si @ 1+ si !
    then
  repeat
  sst @ sen !  slw @ si ! ;
: run-rules
  0 si !  0 sen !
  sst @ 0> if  sst @ 1- rr !  carry-range  then
  begin si @ slw @ < while
    0 rhit !
    nrules @ 0 ?do
      rhit @ 0= if
        i rcur !
        rcur @ 0 rule@
        dup R-KW    = if  drop  try-kw   rhit !  else
        dup R-NUM   = if  drop  try-num  rhit !  else
        dup R-RANGE = if  drop  try-range rhit !  else
        dup R-RANGE-WB = if  drop  try-range rhit !  else
        dup R-REGEX = if  drop  try-regex rhit !  else
        dup R-WORD  = if  drop  try-word rhit !  else
            drop  try-char rhit !
        then then then then then then
      then
    loop
    rhit @ 0= if  si @ 1+ si !  then
  repeat ;
: build-line { ln -- }               \ process line ln with the rule-table engine (sst already set)
  ln line-start sls !
  ln line-length slw !
  slw @ 0 max hlmaplen !
  slw @ 1+ hlmap-ensure
  slw @ 0 ?do  0 hlmap @ i + c!  loop
  run-rules ;
\ general: whether the line contains a multiline-range start (/* or `); no longer only scan for /* (otherwise Go raw strings corrupt the state chain)
\ Note: cannot call match-here? here (it uses locals + ?do; the deep chain corrupts the return stack); do a direct byte scan.
: line-has-csstart? ( -- f )
  slw @ 0 ?do
    sls @ i + g-char-at 96 = if true unloop exit then   \ ` (Go raw string)
  loop
  slw @ 1- 0 max 0 ?do
    sls @ i + g-char-at 47 =
    sls @ i + 1+ g-char-at 42 = and if
      true unloop exit
    then
  loop
  false ;

: ensure-state { ln -- }            \ ensure lstate[ln] is known (compute forward from the frontier)
  ln 1+ lstate-ensure
  begin lst-max @ ln < while
    lst-max @ es-k !
    es-k @ lstate @ + c@ sst !
    \ Fast path: state is 0 and the line has no "/*" (the only block-comment start that can span lines) → the state stays 0,
    \ skip the full highlighting engine (only skips keyword matching, not state correctness: lines containing "/*" still go through the full engine)
    sst @ 0= if
      es-k @ line-start sls !
      es-k @ line-length slw !
      line-has-csstart? 0= if
        0 sen !
      else
        es-k @ build-line
      then
    else
      es-k @ build-line
    then
    sen @  es-k @ 1+ lstate @ + c!
    lst-max @ 1+ lst-max !
  repeat ;
\ ===== language registration (automatic selection by file extension)=====
variable nlangs  0 nlangs !
variable rl-u  0 rl-u !
16 constant max-langs
create lang-exts   max-langs 96 * allot
create lang-elens  max-langs cells allot
create lang-xts    max-langs cells allot
: register-lang ( a u xt -- )              \ register: extension (without the dot) + rule-word xt
  nlangs @ max-langs >= if  2drop drop exit  then
  >r  dup rl-u !                            \ r: xt, stack: ( a u )
  lang-exts nlangs @ 96 * +  swap move
  rl-u @  lang-elens nlangs @ cells + !
  r>       lang-xts   nlangs @ cells + !
  1 nlangs +! ;

\ ===== language name -> rule word (used by :syntax go to pick a language)=====
16 constant max-lnames
create lname-bufs  max-lnames 32 * allot
create lname-lens  max-lnames cells allot
create lname-xts   max-lnames cells allot
variable nlname 0 nlname !
: register-lang-name ( a u xt -- )        \ register: language name (e.g. go/gomod/forth) + rule-word xt
  nlname @ max-lnames >= if  2drop drop exit  then
  >r  dup rl-u !
  lname-bufs nlname @ 32 * +  swap move
  rl-u @  lname-lens nlname @ cells + !
  r>       lname-xts  nlname @ cells + !
  1 nlname +! ;
: lang-name-of { a u -- xt|0 }            \ look up the rule word by language name
  nlname @ 0 ?do
    i cells lname-lens + @ u = if
      a u  lname-bufs i 32 * +  u  compare 0= if
        i cells lname-xts + @  unloop exit
      then
    then
  loop  0 ;

\ ===== filename special cases (no extension / special filenames: Makefile / go.mod / Dockerfile ...)=====
\ Takes priority over extension matching: go.mod's extension is mod (ambiguous), so .mod must not be registered as go.
variable nfnames  0 nfnames !
variable fname-u  0 fname-u !
16 constant max-fnames
create fname-names  max-fnames 128 * allot
create fname-lens   max-fnames cells allot
create fname-xts    max-fnames cells allot
: register-filename ( a u xt -- )         \ register: exact filename + rule-word xt
  nfnames @ max-fnames >= if  2drop drop exit  then
  >r  dup fname-u !                        \ r: xt, stack: ( a u )
  fname-names nfnames @ 128 * +  swap move
  fname-u @  fname-lens nfnames @ cells + !
  r>         fname-xts   nfnames @ cells + !
  1 nfnames +! ;
: filename-lang-of { a u -- xt }          \ exact filename match; 0 if none
  nfnames @ 0 ?do
    i cells fname-lens + @ u = if
      a u  fname-names i 128 * +  u  compare 0= if
        i cells fname-xts + @  unloop exit
      then
    then
  loop  0 ;
: ext-of { a u -- ea eu }                  \ the part after the last '.'
  u eo-e !
  begin eo-e @ 0> while
    eo-e @ 1- a + c@ 46 = if  a eo-e @ +  u eo-e @ -  exit  then
    eo-e @ 1- eo-e !
  repeat  a 0 ;
\ take the part after the last '/'; unchanged if no '/'. No locals / no ?do (set-language runs in the render deep chain)
variable pb-a  0 pb-a !
variable pb-u  0 pb-u !
variable pb-e  0 pb-e !
: path-base ( a u -- ba bu )
  pb-u !  pb-a !
  pb-u @ pb-e !
  begin pb-e @ 0> while
    pb-e @ 1- pb-a @ + c@ 47 = if
      pb-a @ pb-e @ +  pb-u @ pb-e @ -  exit
    then
    pb-e @ 1- pb-e !
  repeat
  pb-a @ pb-u @ ;
: lang-of { a u -- xt }                    \ look up the rule-word xt by extension; 0 if none
  nlangs @ 0 ?do
    i cells lang-elens + @ u = if
      a u  lang-exts i 96 * +  u  compare 0= if
        i cells lang-xts + @  unloop exit
      then
    then
  loop  0 ;
variable lang-buf  0 lang-buf !           \ the buffer the current rule table belongs to
variable syntax-on  1 syntax-on !        \ global syntax-highlighting switch (0=off 1=on)
\ effective syntax switch of the current buffer (B:SYNTAX = -1 follows the global switch, otherwise the override)
: syntax-effective ( -- f )
  gctx @ B:SYNTAX + @ dup -1 = if drop syntax-on @ then ;
\ rule word of the current buffer (B:LANGOVR override takes priority, otherwise automatic by filename)
: lang-xt-effective ( -- xt|0 )
  gctx @ B:LANGOVR + @ ?dup if exit then
  fname @ flen @ path-base 2dup filename-lang-of
  ?dup if  nip nip exit  then
  ext-of lang-of ;
: set-language                             \ select the language by the current buffer filename (override takes priority)
  syntax-effective 0= if rules-clear exit then
  lang-xt-effective ?dup if execute else rules-clear then ;
: ensure-language                          \ re-select only when the buffer changed (and invalidate the cache)
  gctx @ lang-buf @ <> if
    gctx @ lang-buf !
    hlc-reset  0 lst-max !
    set-language
  then ;

: syn-attr { a -- hlend attr }      \ syntax style (per-line cache + window cache)
  ensure-language
  hl-dirty @ hl-was-dirty !           \ edit detection (lix-dirty is cleared early by ensure-visible, so use the dedicated hl-dirty)
  0 hl-dirty !
  a offset-line { ln }
  hl-was-dirty @ if                    \ edited: invalidate both the highlight cache and the state chain
    hlc-reset  0 lst-max !
  then
  ln ensure-state
  ln /hlcache mod hlc-s !
  hlc-line hlc-s @ cells + @ ln = if
    hlc-activate                       \ hit: reuse that line's hlmap
  else
    hlc-activate                       \ miss: load into that slot and rebuild
    ln lstate @ + c@ sst !
    ln build-line
    hlc-save
    ln hlc-line hlc-s @ cells + !
  then
  a ln line-start - { off }
  off 0 max  hlmaplen @ min { o }
  o hlmap @ + c@ { idx }
  o sa-e !
  begin  sa-e @ 1+ hlmaplen @ <  sa-e @ 1+ hlmap @ + c@ idx =  and
  while  sa-e @ 1+ sa-e !  repeat
  ln line-start sa-e @ + 1 +
  idx sidx>attr ;
