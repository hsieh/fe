\ ===== gap buffer core (Forth version, Gforth locals)=====
\ Memory layout: [ front text ][ gap ][ back text ]
\          0         front          cap-back     cap
\ fields g-buf/g-cap/g-front/g-back and lix* are defined in state.fs (fields of the current buffer)

variable snap-dirty  1 snap-dirty !   \ contiguous text snapshot invalidated (used by regex search; set to 1 on edit)
variable hl-dirty    1 hl-dirty !     \ syntax highlighting invalidated (used by syn-attr; set to 1 on edit)
variable edit-dirty  0 edit-dirty !   \ set to 1 on every edit (used by lsp to debounce didChange)
variable loading? 0 loading? !   \ file is loading (not a user edit, does not set edit-dirty)
: lix-touch
  1 lix-dirty !  1 snap-dirty !  1 hl-dirty !
  loading? @ 0= if  1 edit-dirty !  edit-touch-xt  then ;

\ incremental edit tracking (per-buffer fields); called by g-insert/g-delete
\ count \n in [pos, pos+len) (gap-aware inline; used for edit tracking)
: lix-nl-count { pos len -- n }
  g-buf @ { buf }  g-front @ { front }  g-cap @ g-front @ - g-back @ - { gap }
  0
  len 0 ?do
    pos i + front < if  buf pos i + + c@  else  buf gap + pos i + + c@  then
    10 = if 1+ then
  loop ;

: lix-edit-note { pos delta nl -- }
  lix-epend @ 0= if
    pos lix-epos !  delta lix-edelta !  nl lix-enl !
  else
    1 lix-ecomplex !
  then
  1 lix-epend +! ;

: g-gap-size  g-cap @ g-front @ - g-back @ - ;
: g-size      g-front @ g-back @ + ;
: g-init ( cap -- )  dup allocate throw g-buf !  g-cap !  0 g-front ! 0 g-back ! ;
: g-free g-buf @ free throw ;

\ move the gap to pos
: g-move-gap { pos -- }
  pos g-front @ = if exit then
  pos g-front @ < if
    \ shift left: move [pos,front) to before back, len = front - pos
    g-front @ pos - { len }
    g-buf @ pos +                    \ src
    g-cap @ g-back @ - len - g-buf @ + \ dst
    len move
    pos g-front !
    len g-back +!
  else
    \ shift right: move len bytes before back to front, len = pos - front
    pos g-front @ - { len }
    g-cap @ g-back @ - g-buf @ +     \ src
    g-front @ g-buf @ +              \ dst
    len move
    pos g-front !
    len negate g-back +!
  then ;

\ ensure the gap can hold at least need bytes (grow if not, keeping the gap position unchanged)
: g-grow { need -- }
  need g-gap-size <= if exit then
  g-cap @ { oldcap }
  oldcap 2*  g-size need +  max  { newcap }
  g-buf @ newcap resize throw g-buf !
  g-back @ 0> if
    g-back @ { back }
    g-buf @ oldcap + back -          \ src = old back start
    g-buf @ newcap + back -          \ dst = new back start
    back move
  then
  newcap g-cap ! ;

\ insert at the front of the gap (the gap is already at the insertion point)
: g-insert { addr len -- }
  len 0<= if exit then
  len g-grow
  g-front @ { pos }  0 { nl }
  len 0 ?do  addr i + c@ 10 = if  nl 1+ to nl  then  loop
  pos len nl lix-edit-note
  addr g-buf @ g-front @ + len move
  len g-front +!
  lix-touch ;

\ delete len bytes forward
: g-delete-forward ( len -- )
  g-back @ min { n }
  n 0<= if exit then
  g-front @ { pos }
  pos n lix-nl-count { nl }
  pos n negate nl lix-edit-note
  n negate g-back +!
  lix-touch ;
\ delete len bytes backward
: g-delete-backward ( len -- )
  g-front @ min { n }
  n 0<= if exit then
  g-front @ n - { pos }
  pos n lix-nl-count { nl }
  pos n negate nl lix-edit-note
  n negate g-front +!
  lix-touch ;

\ read the whole text into (addr len) (temporary allocation, caller frees)
: g-contents { -- addr len }
  g-size allocate throw { tmp }
  g-front @ { flen }
  g-buf @ tmp flen move
  g-cap @ g-back @ - g-buf @ +  tmp flen +  g-back @ move
  tmp g-size ;

\ read the character at offset (skipping the gap)
: g-char-at { offset }
  offset g-front @ < if
    g-buf @ offset + c@
  else
    g-buf @ offset + g-gap-size + c@
  then ;

\ line count (number of \n + 1); inline gap-aware byte access (same as lix-build) to avoid a g-char-at call per byte
: g-nlines
  g-buf @ { buf }  g-front @ { front }  g-cap @ g-front @ - g-back @ - { gap }
  g-size { gs }
  0
  gs 0 ?do
    i front < if  buf i + c@  else  buf gap + i + c@  then
    10 = if  1+  then
  loop
  1+ ;

\ ===== line-start offset index (speeds up offset<->line/col and line operations)=====
\ lix / lix-cap / lix-n are defined in state.fs (fields of the current buffer)

: lix-ensure { n -- }
  n lix-cap @ u<= if exit then
  lix @ 0<> if lix @ free throw then
  n 2* 64 max  dup lix-cap !  cells allocate throw lix ! ;

: lix@ ( i -- offset )  cells lix @ + @ ;

\ binary search: index of the first lix@i > epos (not found = lix-n)
: lix-find-gt { epos -- i }
  0 { lo }  lix-n @ { hi }  0 { mid }
  begin lo hi < while
    lo hi + 2 / to mid
    mid lix@ epos > if  mid to hi  else  mid 1+ to lo  then
  repeat
  lo ;

\ add delta to each offset in the tail [start, lix-n)
: lix-shift-tail { start delta -- }
  lix-n @ start ?do
    i cells lix @ +  dup @ delta +  swap !
  loop ;

\ rebuild when dirty (one entry per line-start offset); a single edit without newlines uses an incremental shift, the rest are rebuilt in full
: lix-build
  lix-dirty @ 0= if exit then
  lix-epend @ 1 =  lix-ecomplex @ 0=  and  lix-enl @ 0=  and if
    lix-epos @ lix-find-gt { i }
    i lix-edelta @ lix-shift-tail
    0 lix-dirty !  0 lix-epend !  exit
  then
  g-size 2 + lix-ensure
  0 lix @ !
  1 lix-n !
  g-buf @ { buf }  g-front @ { front }  g-cap @ g-front @ - g-back @ - { gap }
  g-size { gs }
  gs 0 ?do
    i front < if  buf i + c@  else  buf gap + i + c@  then
    10 = if
      i 1+ lix @ lix-n @ cells + !
      lix-n @ 1+ lix-n !
    then
  loop
  0 lix-dirty !  0 lix-epend !  0 lix-ecomplex ! ;

: lix-lines  lix-build  lix-n @ ;
