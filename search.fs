\ ===== search.fs — search (naive substring + opt-in POSIX regex)=====
\ Depends on gapbuf.fs (g-size / g-char-at)

256 constant /pat
create pat /pat allot
variable patlen     0 patlen !
variable search-dir 1 search-dir !   \ 1=forward -1=backward
variable searching  0 searching !    \ whether a search string is being typed
variable ignorecase 0 ignorecase !   \ 1=ignore case
variable regex-search 0 regex-search !   \ 1=POSIX regex (otherwise naive substring)
variable re-timeout 0 re-timeout !     \ whether the last regex timed out (used by search-do to report an error)
variable re-valid 0 re-valid !         \ whether the current pattern is a valid regex (otherwise fall back to a literal)
variable word-boundary 0 word-boundary !   \ 1=naive matching requires word boundaries (used by * to search the word under the cursor)

\ word-character test (inlined: search.fs comes before custom.fs word-char?)
: search-word-char? ( c -- f )
  dup 48 58 within if drop true exit then
  dup 65 91 within if drop true exit then
  dup 97 123 within if drop true exit then
  dup 95 = if drop true exit then
  0x80 >= ;

\ ASCII lowercasing (when ignorecase is on)
: lc { c }  ignorecase @ c 65 91 within and if c 32 + else c then ;

\ (re)compile the current pattern in regex mode
: regex-recompile
  regex-search @ if  pat patlen @ ignorecase @ re-compile re-valid !  else 1 re-valid ! then ;

\ anchored regex match at pos -> end offset, or 0 if none
: match-re { pos -- end|0 }
  snap-build
  snap @ snap-len @ pos re-match-at
  dup -2 = if drop 0 then ;

: pat-clear 0 patlen !  0 word-boundary !  regex-recompile ;
: pat-add { c }  patlen @ /pat < if  c pat patlen @ + c!  1 patlen +!  then  regex-recompile ;
\ does pattern match at pos -> match end offset, or 0 if none
: match-at? { pos }
  regex-search @ re-valid @ and if
    pos match-re
  else
    patlen @ 0= if 0 exit then
    word-boundary @ if
      pos 0> if  pos 1- g-char-at search-word-char? if 0 exit then  then   \ left boundary
    then
    patlen @ 0 ?do
      pos i + g-size >= if unloop 0 exit then
      pos i + g-char-at lc   pat i + c@ lc   <> if unloop 0 exit then
    loop
    word-boundary @ if
      pos patlen @ + g-size < if  pos patlen @ + g-char-at search-word-char? if 0 exit then  then   \ right boundary
    then
    pos patlen @ +
  then ;

\ search forward from from (inclusive); return the position, or -1 if not found
: search-fwd { from }
  regex-search @ re-valid @ and if
    snap-build
    snap @ snap-len @ from re-find-next
    dup -2 = if drop -1  1 re-timeout ! then
  else
    from
    begin
      dup patlen @ + g-size <=
    while
      dup match-at? if exit then
      1+
    repeat
    drop -1
  then ;

\ search backward from from (inclusive); return the position, or -1 if not found
: search-back { from }
  from
  begin
    dup 0>=
  while
    dup match-at? if exit then
    1-
  repeat
  drop -1 ;
