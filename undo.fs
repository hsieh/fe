\ ===== undo.fs — undo/redo (operation log)=====
\ Depends on gapbuf.fs (g-move-gap/g-insert/g-delete-forward) and host-provided cursor/modified.
\ Record: [type][pos][len][data][precur][postcur]
\   type: 0=insert 1=delete;   data: copy of the affected bytes
\   cursor after undo = precur, after redo = postcur
\ Consecutive adjacent operations of the same kind are merged into one group (typing / consecutive deletion = one undo step).

256 constant max-undorec
\ urecs / unrec / urp / ubroken are buffer fields (state.fs); urecs points to this buffer's array

: urec   ( i -- a ) 6 * cells  urecs @  + ;
: u-type ( i -- a ) urec ;
: u-pos  ( i -- a ) urec cell+ ;
: u-len  ( i -- a ) urec 2 cells + ;
: u-data ( i -- a ) urec 3 cells + ;
: u-pre  ( i -- a ) urec 4 cells + ;
: u-post ( i -- a ) urec 5 cells + ;

\ break merging (called on cursor movement / save, etc.)
: u-broken ( -- ) 1 ubroken ! ;

\ discard the redo branch
: u-trunc
  begin unrec @ urp @ > while
    unrec @ 1- u-data @ free throw
    -1 unrec +!
  repeat ;

\ drop the oldest record when full
: u-make-room
  unrec @ max-undorec < if exit then
  0 u-data @ free throw               \ free the data of the oldest urecs[0]
  urecs @ 6 cells +  urecs @  max-undorec 1- 6 * cells  move   \ shift left: urecs[1..] -> urecs[0..]
  -1 unrec +!  -1 urp +!
  saved-urp @ 0> if  -1 saved-urp +!  then ;

\ clear the undo stack (free all record data); the caller then sets saved-urp (0=in sync with disk, -1=out of sync)
: u-reset
  0 { i }
  begin i unrec @ < while
    i u-data @ free throw
    i 1+ to i
  repeat
  0 unrec !  0 urp ! ;

: u-push { type pos len data pre post }
  u-trunc  u-make-room
  type unrec @ u-type !  pos unrec @ u-pos !  len unrec @ u-len !
  data unrec @ u-data !  pre unrec @ u-pre !  post unrec @ u-post !
  1 unrec +!  unrec @ urp ! ;

\ append 1 byte to the end of data
: u-append { i c }
  i u-data @  i u-len @ 1+  resize throw  i u-data !
  c i u-data @  i u-len @ +  c!
  i u-len @ 1+ i u-len ! ;

\ record "insert character c at pos"
: urec-ins { pos c }
  unrec @ 0> ubroken @ 0= and
  unrec @ 1- u-type @ 0= and
  unrec @ 1- u-pos @ unrec @ 1- u-len @ + pos = and
  if
    unrec @ 1- c u-append
    unrec @ 1- u-pos @ unrec @ 1- u-len @ + unrec @ 1- u-post !
  else
    1 allocate throw { d }  c d c!
    0 pos 1 d pos pos 1 + u-push
  then
  0 ubroken ! ;

\ record "forward delete [pos, pos+len)"
: urec-del-fwd { pos len }
  unrec @ 0> ubroken @ 0= and
  unrec @ 1- u-type @ 1 = and
  unrec @ 1- u-pos @ pos = and
  if
    unrec @ 1- { j }
    j u-data @  j u-len @ len +  resize throw  j u-data !
    len 0 ?do  pos i + g-char-at  j u-data @  j u-len @ + i +  c!  loop
    j u-len @ len + j u-len !
  else
    len allocate throw { d }
    len 0 ?do  pos i + g-char-at  d i +  c!  loop
    1 pos len d pos pos u-push
  then
  0 ubroken ! ;

\ record "backspace delete [pos, pos+len)" (new character right edge == group left edge)
: urec-del-back { pos len }
  unrec @ 0> ubroken @ 0= and
  unrec @ 1- u-type @ 1 = and
  unrec @ 1- u-pos @  pos len + = and
  if
    unrec @ 1- { j }
    j u-data @  j u-len @ len +  resize throw  j u-data !
    j u-data @  j u-data @ len +  j u-len @  move     \ shift right to make room for the first byte
    len 0 ?do  pos i + g-char-at  j u-data @ i +  c!  loop
    j u-len @ len + j u-len !
    j u-pos @ len - j u-pos !
    j u-pos @ j u-len @ + j u-pre !
  else
    len allocate throw { d }
    len 0 ?do  pos i + g-char-at  d i +  c!  loop
    1 pos len d pos len + pos u-push
  then
  0 ubroken ! ;

\ undo record i
: u-invert { i }
  i u-type @ 0= if
    i u-pos @ g-move-gap   i u-len @ g-delete-forward
  else
    i u-pos @ g-move-gap   i u-data @ i u-len @ g-insert
  then
  i u-pre @ cursor ! ;

\ redo record i
: u-apply { i }
  i u-type @ 0= if
    i u-pos @ g-move-gap   i u-data @ i u-len @ g-insert
  else
    i u-pos @ g-move-gap   i u-len @ g-delete-forward
  then
  i u-post @ cursor ! ;

: undo
  urp @ 0> if
    urp @ 1- u-invert
    -1 urp +!
    urp @ saved-urp @ <> modified !
    u-broken
  then ;

: redo
  urp @ unrec @ < if
    urp @ u-apply
    1 urp +!
    urp @ saved-urp @ <> modified !
    u-broken
  then ;
