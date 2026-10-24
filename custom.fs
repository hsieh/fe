\ ===== custom.fs — the user-editable "commands + bindings" layer =====
\ Loaded after core (fe.fs). Core provides primitives/facilities; this file only says "what a key should do".
\ Future features (yank/paste/visual/replace/open-by-name/keyboard macros…) go here.
\ reload = re-include this file + rebuild the keymap (core handles clearing/rebuilding/error tolerance).

\ the feature library bundled with this layer (search is a "feature", so the layer loads it, not core)
include regex.fs
include search.fs
include cjson.fs

\ ===== mode objects (Mode); the fields are filled by init-keymaps =====
create base-mode     /mode cells allot
create mode-command  /mode cells allot
create mode-insert   /mode cells allot
create mode-search   /mode cells allot
create mode-prompt   /mode cells allot
create keymap-search /keymap cells allot
create keymap-prompt /keymap cells allot
create mode-op       /mode cells allot
create keymap-op       /keymap cells allot
variable leader-key 32 leader-key !   \ leader prefix key (default space); re-bound after set-leader-key changes it

\ moved up: referenced by register-prefix (the original definition was below; removed to avoid duplication)
: mst-prefix  s" NORMAL" ;   \ prefix modes have no standalone label (showcmd shows the prefix name)
: prefix-cancel  mode-pop ;  \ unmatched key -> cancel

\ ===== named-prefix registry (kak-style user modes; g/z/w/leader are all registered here)=====
64 constant max-prefix
create prefix-modes  max-prefix /mode cells * allot
create prefix-keys   max-prefix /keymap cells * allot
create prefix-names  max-prefix 16 * allot
create prefix-nlens  max-prefix cells allot
variable nprefix 0 nprefix !
variable rp-na 0 rp-na !
variable rp-nl 0 rp-nl !
variable rp-i  0 rp-i !

\ prefix-key → mode table (indexed by key-idx, 768 slots = all keys; the generic enter looks it up by last-key)
create prefix-key-modes  /keymap cells allot
\ generic prefix enter: look up the table by "the currently pressed key" (last-key recorded by dispatch-key) and push
: prefix-enter ( -- )
  last-key @ key-idx cells prefix-key-modes + @ ?dup if mode-push then ;
\ bind a prefix key: record key→mode + bind the generic enter
: bind-prefix { key mode -- }
  mode  key key-idx cells prefix-key-modes + !
  key ['] prefix-enter bind-command ;


\ register a named prefix: return the existing one on a duplicate, otherwise allocate + initialize a mode; return ( mode )
: register-prefix ( name len -- mode )
  rp-nl !  rp-na !
  0 rp-i !
  begin rp-i @ nprefix @ < while
    rp-i @ cells prefix-nlens + @ rp-nl @ = if
      rp-na @ rp-nl @  prefix-names rp-i @ 16 * +  rp-nl @  compare 0= if
        prefix-modes rp-i @ /mode cells * +  exit
      then
    then
    1 rp-i +!
  repeat
  nprefix @ max-prefix >= if 0 exit then
  nprefix @ { i }
  rp-na @ prefix-names i 16 * + rp-nl @ move
  rp-nl @ prefix-nlens i cells + !
  prefix-modes i /mode cells * + { m }
  prefix-keys i /keymap cells * + m m-keymap !
  0 m m-parent !
  ['] prefix-cancel m m-input !
  0 m m-enter !  0 m m-leave !
  ['] mst-prefix m m-status !
  0 m m-flags !
  1 nprefix +!
  m ;

\ registration results of g/z/w/leader (filled by register-prefix in init-keymaps)
variable pmode-g 0 pmode-g !
variable pmode-z 0 pmode-z !
variable pmode-w 0 pmode-w !
variable pmode-leader 0 pmode-leader !
create mode-repl     /mode cells allot
create keymap-repl   /keymap cells allot
create mode-qrep     /mode cells allot
create keymap-qrep   /keymap cells allot
: mst-op      s" OPERATOR PENDING" ;
: mst-cmd  s" NORMAL" ;
: mst-ins  s" INSERT" ;
: mst-search  s" SEARCH" ;
: mst-prompt  s" PROMPT" ;
: mst-qrep    s" REPLACE" ;

\ state used by search/prompt modes
variable search-start  0 search-start !
variable prompt-accept-xt  0 prompt-accept-xt !

\ ===== prompt-line (minibuffer) facilities (needed early by buffer-save-as / prompt-loop etc.)=====
variable prompting  0 prompting !
variable prompt-prefix  0 prompt-prefix !
variable prompt-prefix-len  0 prompt-prefix-len !
create pbuf 256 allot
variable pbuflen  0 pbuflen !
variable pbpos    0 pbpos !

: pb-add { c }  pbuflen @ 255 < if  c pbuf pbuflen @ + c!  1 pbuflen +!  then ;
: pb-back  0 pbuflen @ 0> if  -1 pbuflen +!  then ;
: pb-add-cp { cp }  cp ['] pb-add utf8-emit ;

\ prompt-mode commands/defaults (installed into mode-prompt)
: prompt-input { c -- }  c 32 >= if c pb-add-cp  pbuflen @ pbpos ! then ;
: cmd-prompt-cancel  0 prompting !  mode-pop ;
: cmd-prompt-accept  0 prompting !  mode-pop  prompt-accept-xt @ ?dup if execute then ;
: cmd-prompt-back    pb-back ;
\ the Tab action of a prompt (each prompt sets it; 0 = none)
variable prompt-tab-xt  0 prompt-tab-xt !
: cmd-prompt-tab  prompt-tab-xt @ ?dup if execute then ;

\ open a prompt: paddr/plen = prefix string, ax = callback executed on confirmation ( -- )
: prompt-open { paddr plen ax -- }
  paddr prompt-prefix !  plen prompt-prefix-len !
  0 pbuflen !  0 pbpos !  1 prompting !
  ax prompt-accept-xt !
  0 prompt-tab-xt !                    \ reset (each prompt sets it as needed, to prevent cross-prompt inheritance)
  mode-prompt mode-push ;

\ jump to a given line (# key; 1-based, clamped to the first/last line)
: goto-line-accept ( -- )
  pbuf pbuflen @ s>number? 0= if 2drop s" bad line" msg! exit then
  drop 1- { l }                       \ 0-based (s>number? returns a double; drop discards the high cell)
  l 0 max  g-nlines 1- min  to l
  l line-start cursor !
  center-cursor
  0 pnrows ! ;

: goto-line-prompt ( -- )
  s" goto line: " ['] goto-line-accept prompt-open ;


\ ===== editing operations =====
: fe-insert { c }
  bflags @ BF_READONLY and if exit then
  cursor @ c urec-ins           \ record (before insertion)
  cursor @ g-move-gap
  c tmp-c c!
  tmp-c 1 g-insert
  1 cursor +!
  1 modified ! ;

\ byte count of the current line's leading whitespace (spaces/tabs)
: line-indent-len { line }
  line line-start { ls }
  line line-length { ll }
  0 { n }
  begin n ll <  ls n + g-char-at dup 32 = swap 9 = or  while
    n 1+ to n
  repeat
  n ;

: fe-insert-newline
  cursor @ offset->linecol drop { line }
  line line-start { ls }
  line line-indent-len { ilen }
  10 fe-insert                               \ newline
  ilen 0 ?do  ls i + g-char-at fe-insert loop ;   \ copy the leading indentation

\ insert by code point (encoded as UTF-8; ekey returns code points for multibyte characters)
: fe-insert-cp { cp }  cp ['] fe-insert utf8-emit ;

: fe-delete
  bflags @ BF_READONLY and if exit then
  cursor @ g-size < if
    cursor @ { p }
    p u8-n { len }      \ byte count of a whole character
    p len urec-del-fwd
    p g-move-gap
    len g-delete-forward
    1 modified !
  then ;

: fe-backspace
  bflags @ BF_READONLY and if exit then
  cursor @ 0> if
    cursor @ u8-prev { s }             \ start of the previous character
    s u8-n { len }
    s len urec-del-back
    cursor @ g-move-gap
    len g-delete-backward
    len negate cursor +!
    1 modified !
  then ;

\ ===== cursor movement =====
: fe-left   u-broken  cursor @ 0> if cursor @ 1- g-char-at 10 <> if cursor @ u8-prev cursor ! then then ;
: fe-right  u-broken  cursor @ g-size < if cursor @ g-char-at 10 <> if cursor @ u8-n cursor +! then then ;

: fe-up
  u-broken  1 vert !
  cursor @ offset->linecol drop { line }
  line 0> if
    line 1- { tl }
    want-col @ tl line-dwidth min { tc }   \ use the target column (preserve the column)
    tl tc linecol->offset cursor !
  then ;

: fe-down
  u-broken  1 vert !
  cursor @ offset->linecol drop { line }
  line 1+ { tl }
  tl line-start g-size >= if exit then   \ last line / out of range: no-op
  want-col @ tl line-dwidth min { tc }     \ use the target column (preserve the column)
  tl tc linecol->offset cursor ! ;

: fe-line-start
  u-broken
  cursor @ offset->linecol drop 0 linecol->offset cursor ! ;

: fe-line-end
  u-broken
  cursor @ offset->linecol drop { line }
  line line-start { ls }
  line 1+ 0 linecol->offset { le }          \ next line start (after the line-end \n, or g-size)
  le 0> if le 1- g-char-at 10 = if le 1- else le then else 0 then { ce }  \ end of the line content
  ce ls > if ce u8-prev else ls then cursor ! ;   \ the last character; the line start for an empty line

\ vi file actions
: fe-file-start  u-broken  0 cursor ! ;
: fe-file-end
  u-broken
  g-size 0= if 0 cursor ! exit then
  g-size { e }
  e 1- g-char-at 10 = if e 1- to e then      \ drop the trailing \n
  e offset->linecol drop line-start cursor ! ;

\ character class: 0=whitespace 1=word character (alphanumeric/_/non-ASCII) 2=other
: char-class { off }
  off g-size >= if 0 exit then
  off g-char-at { c }
  c 32 = c 9 = or c 10 = or if 0 exit then
  c 0x80 >= if 1 exit then                     \ non-ASCII (CJK etc.) counts as a word character
  c 48 58 within if 1 exit then                 \ 0-9
  c 65 91 within if 1 exit then                 \ A-Z
  c 97 123 within if 1 exit then                \ a-z
  c 95 = if 1 exit then                         \ _
  2 ;

: fe-word-fwd
  u-broken
  cursor @ wp !
  wp @ g-size < if
    wp @ char-class wc !
    begin wp @ g-size < wp @ char-class wc @ = and while wp @ dup u8-n + wp ! repeat
  then
  begin wp @ g-size < wp @ char-class 0 = and while wp @ dup u8-n + wp ! repeat
  wp @ cursor ! ;

: fe-word-back
  u-broken
  cursor @ wp !
  wp @ 0> if wp @ u8-prev wp ! then
  begin wp @ 0> wp @ char-class 0 = and while wp @ u8-prev wp ! repeat
  wp @ char-class wc !
  begin
    wp @ 0> if wp @ u8-prev char-class wc @ = else false then
  while
    wp @ u8-prev wp !
  repeat
  wp @ cursor ! ;

: fe-word-end
  u-broken
  cursor @ wp !
  wp @ g-size < if wp @ dup u8-n + wp ! then
  begin wp @ g-size < wp @ char-class 0 = and while wp @ dup u8-n + wp ! repeat
  wp @ g-size < if
    wp @ char-class wc !
    begin wp @ g-size < wp @ char-class wc @ = and while wp @ dup u8-n + wp ! repeat
    wp @ u8-prev wp !
  then
  wp @ cursor ! ;

\ page up/down (needs scr-rows, so placed after the viewport section)
: fe-page-down
  u-broken  1 vert !
  cursor @ offset->linecol drop { line }
  line scr-rows @ + { tl }
  want-col @ tl line-dwidth min { tc }
  tl tc linecol->offset cursor ! ;

: fe-page-up
  u-broken  1 vert !
  cursor @ offset->linecol drop { line }
  line scr-rows @ - 0 max { tl }
  want-col @ tl line-dwidth min { tc }
  tl tc linecol->offset cursor ! ;

\ zt: cursor to viewport top; zb: cursor to viewport bottom; zz: cursor to viewport center (without moving the cursor, only the viewport scrolls)
: cmd-zt
  u-broken
  cursor @ offset->linecol { cl col }
  cl col dcol->sub-col { csub incol }
  cl top-line !  csub top-sub !
  0 pnrows !
  mode-pop ;

: cmd-zb
  u-broken
  cursor @ offset->linecol { cl col }
  cl col dcol->sub-col { csub incol }
  cl csub scroll-below
  0 pnrows !
  mode-pop ;

: cmd-zz
  u-broken
  center-cursor
  0 pnrows !
  mode-pop ;

\ ===== viewport scrolling (vim Ctrl-e / Ctrl-y): scroll the viewport; the cursor follows only when it leaves the viewport =====
\ visible text rows: yield the bottom when a picker is open (consistent with win-render-text's render cap)
: vis-rows ( -- n )
  win-text-rows  scr-rows @ panel-rows @ -  min  1 max ;

\ last logical line with content (0-based): the empty line after a trailing \n does not count (same semantics as fe-file-end)
: last-content-line ( -- line )
  g-size 0= if 0 exit then
  g-size 1- { e }
  e g-char-at 10 = if e 1- to e then
  e 0< if 0 exit then
  e offset-line ;

\ move the viewport down n visual lines (content moves up, viewport toward the file end), clamped to the file end
\ vim semantics (scrolloff=0): the last line (including soft-wrap sublines) can scroll to the viewport top, leaving blank space below (like vim's ~ lines)
: view-scroll-down { n -- }
  last-content-line { ll }
  ll line-vrows 1- { ls }
  n advance
  top-line @ ll >  top-line @ ll = top-sub @ ls > and  or if
    ll top-line !  ls top-sub !
  then ;

\ move the viewport up n visual lines (content moves down, viewport toward the file head), clamped to the file head
: view-scroll-up { n -- }
  top-line @ top-sub @ n scroll-up-by ;

\ place the cursor inside subline (line, sub), keeping want-col where possible (clamped to the subline's column range; with soft wrapping col 0 may be above the viewport)
\ Note: the dcol0 returned by sub->seg is the "column within the segment", not the absolute column; derive the absolute column from bi via u8-col
: cursor-on-subline { line sub -- }
  line line-start { ls }  line line-length { lw }
  ls lw sub sub->seg { bi dcol0 }
  ls lw bi u8-col { c0 }                       \ absolute column of the subline start
  line line-dwidth { dw }
  c0 text-cols @ +  dw min  1-  c0 max  { emax }   \ subline end column (inclusive)
  want-col @ emax min  c0 max  { tc }
  line tc linecol->offset cursor ! ;

\ cursor follow: after scrolling, if the cursor is out of the viewport, move it to the nearest visible subline (keeping the target column)
: cursor-into-view ( -- )
  cursor @ offset->linecol { cl col }
  cl col dcol->sub-col { csub incol }
  cl csub vrow-of { vr }
  vr 0< if
    top-line @ top-sub @ cursor-on-subline
  else
    vr vis-rows >= if
      top-line @ { tl }  top-sub @ { ts }
      vis-rows 1- advance                     \ temporarily compute the bottom visible subline
      top-line @ { bl }  top-sub @ { bs }
      tl top-line !  ts top-sub !             \ restore
      bl bs cursor-on-subline
    then
  then ;

\ Ctrl-e: scroll the viewport down one line (vim-style)
: cmd-scroll-down
  u-broken  1 vert !
  1 view-scroll-down
  cursor-into-view
  0 pnrows ! ;

\ Ctrl-y: scroll the viewport up one line (vim-style)
: cmd-scroll-up
  u-broken  1 vert !
  1 view-scroll-up
  cursor-into-view
  0 pnrows ! ;

\ ===== save the current file =====
: save-current
  u-broken
  bflags @ BF_NOFILE and if s" not saved (no file)" msg-say exit then
  fname @ 0= if s" no file" msg-say exit then
  fname @ flen @ save-file
  s" saved " msg!  fname @ flen @ msg+
  msgbuf msglen @ msg-log ;

\ ===== search =====
: pat-add-cp { cp }  cp ['] pat-add utf8-emit ;
: pat-back                        \ delete the last character (multibyte as a whole)
  patlen @ 0> if
    patlen @ 1- { i }
    begin i 0> pat i + c@ 0x80 0xC0 within and while  i 1- to i  repeat
    i patlen !
    regex-recompile
  then ;

: search-do { dir -- }
  patlen @ 0= if exit then
  0 re-timeout !
  dir 1 = if
    cursor @ 1+ search-fwd dup 0>= if cursor ! exit then drop
    0 search-fwd dup 0>= if cursor ! s" search wrapped" msg! else drop s" not found" msg! then
  else
    cursor @ 1- search-back dup 0>= if cursor ! exit then drop
    g-size search-back dup 0>= if cursor ! s" search wrapped" msg! else drop s" not found" msg! then
  then
  re-timeout @ if s" regex timeout" msg! then ;

\ search-mode commands/defaults (installed into mode-search)
: search-input { c -- }        \ append a printable character + incremental re-search from the start point
  c 32 >= if
    c pat-add-cp
    search-start @ cursor !
    search-dir @ search-do
  then ;
: cmd-search-cancel  search-start @ cursor !  mode-pop ;   \ ESC/Ctrl-C: back to the start point
: cmd-search-accept  mode-pop ;                            \ Enter: stay at the match
: cmd-search-bs      pat-back  search-start @ cursor !  search-dir @ search-do ;

\ enter search (command mode)
: cmd-search-fwd  pat-clear   1 search-dir !  cursor @ search-start !  mode-search mode-push ;
: cmd-search-bwd  pat-clear  -1 search-dir !  cursor @ search-start !  mode-search mode-push ;

\ get the word under the cursor (copied into the contiguous buffer cw-buf; 0 0 if empty)
create cw-buf 64 allot
: cursor-word ( -- a u | 0 0 )
  cursor @ { e }
  e { s }
  begin s 0>  s 1- g-char-at search-word-char?  and while  s 1- to s  repeat
  begin e g-size <  e g-char-at search-word-char?  and while  e 1+ to e  repeat
  s e = if 0 0 exit then
  e s - 64 min { u }
  0 { i }
  begin i u < while
    s i + g-char-at cw-buf i + c!
    i 1+ to i
  repeat
  cw-buf u ;

\ * search the word under the cursor (word boundary; no jump, n/N decides the direction)
: cmd-star ( -- )
  cursor-word { a u }
  u 0= if #bell emit exit then
  a pat u move  u patlen !
  1 word-boundary !   regex-recompile ;

\ g* same as *, but without word boundary (literal substring)
: cmd-gstar ( -- )
  mode-pop
  cursor-word { a u }
  u 0= if #bell emit exit then
  a pat u move  u patlen !
  0 word-boundary !   regex-recompile ;

\ delete [lo,hi) (one undo step: record and capture the deleted data via core's urec-del-fwd)
\ after deletion the cursor returns to the deletion start lo (same as vim); clamped into the line if past the new line end
: delete-range { lo hi -- }
  bflags @ BF_READONLY and if exit then
  hi lo > if
    hi lo - { len }
    lo len urec-del-fwd
    lo g-move-gap
    len g-delete-forward
    1 modified !
    lo cursor !
    cursor @ offset-line { ln }
    ln line-length { ll }
    ll 0> if
      lo ln line-start - ll >= if
        ln line-start ll 1- + cursor !
      then
    else
      ln line-start cursor !
    then
  then ;

\ ===== query-replace (qemacs style, single-key confirmation per match)=====
create rpl /pat allot
variable rpllen     0 rpllen !
variable nreps      0 nreps !
variable qrep-start 0 qrep-start !
variable qreplacing 0 qreplacing !

\ replace [pos, pos+patlen) with rpl (after delete-range the cursor is at pos; then insert byte by byte)
\ Note: delete-range clamps the cursor to the line end; here we must explicitly return to pos before inserting
: qrep-one { pos -- }
  pos match-at? { end }
  end 0<= if exit then
  pos end delete-range
  pos cursor !
  rpllen @ 0 ?do  rpl i + c@ fe-insert  loop ;

\ find the next match from cursor, move the cursor there; return false if none
: qrep-find ( -- found? )
  cursor @ search-fwd { pos }
  pos 0< if false exit then
  pos cursor !
  true ;

\ finish: leave the mode + report the count
create qmsg 32 allot
variable qmsglen 0 qmsglen !
: qmsg-append { a u }
  a qmsg qmsglen @ + u move
  u qmsglen +! ;
: qrep-finish
  mode-pop
  0 qreplacing !
  nreps @ 0= if s" no replacements" msg! exit then
  0 qmsglen !
  s" replaced " qmsg-append
  nreps @ 0 <# #s #> qmsg-append
  qmsg qmsglen @ msg! ;

\ key handling
: qrep-yes   cursor @ qrep-one  1 nreps +!  qrep-find 0= if qrep-finish then ;
: qrep-no    cursor @ match-at? { end }  end cursor !  qrep-find 0= if qrep-finish then ;
: qrep-dot   cursor @ qrep-one  1 nreps +!  qrep-finish ;
: qrep-all
  begin
    cursor @ qrep-one  1 nreps +!
    qrep-find 0= if qrep-finish exit then
  again ;
: qrep-quit   qrep-finish ;
: qrep-abort  qrep-start @ cursor !  qrep-finish ;

\ enter replace: find the first match, position the cursor, enter mode-qrep
: qrep-begin
  0 nreps !
  cursor @ qrep-start !
  1 qreplacing !
  mode-qrep mode-push
  qrep-find 0= if qrep-finish then ;

\ prompt flow: search string → replacement string → start
: qrep-accept-rpl
  pbuf pbuflen @ /pat min { m }
  pbuf rpl m move  m rpllen !
  qrep-begin ;

: qrep-accept-pat
  pbuf pbuflen @ /pat min { m }
  pbuf pat m move  m patlen !
  patlen @ 0= if s" empty pattern" msg! exit then
  s" with: " ['] qrep-accept-rpl prompt-open ;

\ enter query-replace (E): with a previous search string, only ask for the replacement; otherwise ask for the search string first
: cmd-qrep
  bflags @ BF_READONLY and if s" readonly" msg! exit then
  patlen @ 0> if
    s" with: " ['] qrep-accept-rpl prompt-open
  else
    s" replace: " ['] qrep-accept-pat prompt-open
  then ;

\ save as: prompt for a path -> associate it with the file, clear BF_NOFILE, save
: do-save-as
  pbuflen @ 511 min { n }
  n gctx @ B:NAMELEN + !
  pbuf  gctx @ B:NAME +  n move
  gctx @ B:NAME +  gctx @ B:FNAME + !
  n gctx @ B:FLEN + !
  bflags @ BF_NOFILE invert and  bflags !
  save-current ;
\ open by name: prompt for a path -> open (create a new empty buffer if it does not exist)
\ ===== path completion (B: recursive file list, find first + pure-Forth recursive fallback)=====
variable pdir-len 0 pdir-len !
variable pfx-len 0 pfx-len !
variable nmatch 0 nmatch !
create m1 256 allot
variable m1-len 0 m1-len !
create cpfx 256 allot
variable cpfx-len 0 cpfx-len !
variable pc-i 0 pc-i !
variable pc-cpi 0 pc-cpi !
variable pm-a 0 pm-a !
variable pm-u 0 pm-u !
variable fi 0 fi !
variable fs 0 fs !
variable fel 0 fel !

\ --- recursive file list (relative paths, newline-separated, no ./ prefix) ---
\ dynamically allocated: with many project files the path list exceeds 64K; a fixed buffer would silently drop files
variable flist 0 flist !
variable flist-cap 0 flist-cap !
variable flist-len 0 flist-len !

: flist-ensure { n -- }
  n flist-cap @ u<= if exit then
  n 2* 65536 max { newcap }
  \ resize keeps the existing contents (free+allocate would lose all previously collected entries)
  flist @ if
    flist @ newcap resize throw flist !
  else
    newcap allocate throw flist !
  then
  newcap flist-cap ! ;
create cur-dir 512 allot
variable cur-dir-len 0 cur-dir-len !
create saved-dir 512 allot
variable saved-dir-len 0 saved-dir-len !
create bpath 512 allot
variable bpath-len 0 bpath-len !

: flist-add { a u }
  flist-len @ u + 1+ flist-ensure
  a flist @ flist-len @ + u move  u flist-len +!
  10 flist @ flist-len @ + c!  1 flist-len +! ;

\ bpath = cur-dir + "/" + name (= name when cur-dir is empty)
: bpath-join ( a u -- )
  pm-u !  pm-a !
  0 bpath-len !
  cur-dir-len @ 0> if
    cur-dir bpath cur-dir-len @ move  cur-dir-len @ bpath-len !
    47 bpath bpath-len @ + c!  1 bpath-len +!
  then
  pm-a @ bpath bpath-len @ + pm-u @ move  pm-u @ bpath-len +! ;

\ whether the entry is "." or ".."
: dot-entry? ( a u -- f )
  pm-u !  pm-a !
  pm-u @ 0= if false exit then
  pm-a @ c@ 46 <> if false exit then
  pm-u @ 1 = if true exit then
  pm-u @ 2 =  pm-a @ 1+ c@ 46 =  and ;

\ skip .git (by name)
: git-dir? ( a u -- f )
  pm-u !  pm-a !
  pm-u @ 4 =  pm-a @ 4  s" .git" drop 4  compare 0=  and ;

defer walk-entry

: walk-dir { da dl }
  dl cur-dir-len !  da cur-dir dl move
  dl 0= if  s" ." ['] walk-entry traverse-dir
        else  da dl ['] walk-entry traverse-dir  then ;

:noname ( a u -- )
  2dup dot-entry? if 2drop exit then
  2dup git-dir? if 2drop exit then
  bpath-join
  bpath bpath-len @ open-dir ?dup if
    2drop  bpath bpath-len @ flist-add
  else
    close-dir drop
    cur-dir-len @ saved-dir-len !
    cur-dir saved-dir saved-dir-len @ move
    bpath bpath-len @ walk-dir
    saved-dir cur-dir saved-dir-len @ move  saved-dir-len @ cur-dir-len !
  then ; is walk-entry

\ pure-Forth recursive collection (fallback)
: forth-collect ( -- )
  0 flist-len !
  0 0 walk-dir ;

\ find collection: copy the output (./path\n) into flist, dropping the ./ prefix
: find-to-flist ( a u -- )
  pm-u !  pm-a !
  0 flist-len !
  0 fi !
  begin
    fi @ pm-u @ <
  while
    fi @ fs !
    \ skip the leading ./
    pm-a @ fi @ + c@ 46 =  pm-a @ fi @ + 1+ c@ 47 =  and if
      2 fi +!
      fi @ fs !
    then
    begin
      fi @ pm-u @ <  pm-a @ fi @ + c@ 10 <>  and
    while
      1 fi +!
    repeat
    fi @  fs @  -  fel !
    fel @ 0> if  pm-a @ fs @ +  fel @  flist-add  then
    1 fi +!
  repeat ;

\ run a command and collect (output goes into flist with the ./ prefix stripped); returns true = success
: cmd-collect ( ca cl -- ok? )
  r/o open-pipe throw { f }
  f slurp-fid { a u }
  f close-pipe 2drop
  u 0> if
    a u find-to-flist
    a free throw
    true
  else
    a ?dup if free throw then
    false
  then ;

\ file source: 0=auto (find preferred, includes untracked files) 1=find 2=git ls-files 3=pure Forth
variable file-source  0 file-source !

: collect-files ( -- )
  file-source @ case
    1 of  s" find . -name .git -prune -o -type f -print 2>/dev/null" cmd-collect if drop exit then  forth-collect  endof
    2 of  s" git ls-files 2>/dev/null" cmd-collect if drop exit then
          s" find . -name .git -prune -o -type f -print 2>/dev/null" cmd-collect if drop exit then  forth-collect  endof
    3 of  forth-collect  endof
    s" find . -name .git -prune -o -type f -print 2>/dev/null" cmd-collect if drop exit then
    forth-collect
  endcase ;

\ whether the first pfx-len bytes of entry (a u) equal pbuf
: pfx-match? ( a u -- f )
  pm-u !  pm-a !
  pm-u @ pfx-len @ < if false exit then
  0 pc-cpi !
  begin
    pc-cpi @ pfx-len @ <
  while
    pm-a @ pc-cpi @ + c@  pbuf pc-cpi @ + c@  <> if false exit then
    1 pc-cpi +!
  repeat
  true ;

\ replace the whole pbuf content with (ca cl)
: pbuf-set-prefix ( ca cl -- )
  pm-u !  pm-a !
  0 pbuflen !
  0 pc-i !
  begin
    pc-i @ pm-u @ <
  while
    pm-a @ pc-i @ + c@ pb-add  1 pc-i +!
  repeat ;

\ update the common prefix cpfx with entry (a u)
: update-cpfx ( a u -- )
  pm-u !  pm-a !
  cpfx-len @ pm-u @ min { n }
  0 pc-cpi !
  begin
    pc-cpi @ n <  cpfx pc-cpi @ + c@  pm-a @ pc-cpi @ + c@  =  and
  while
    1 pc-cpi +!
  repeat
  pc-cpi @ cpfx-len ! ;

\ ===== file-find mode (picker + dynamic filtering)=====
\ substring: whether haystack contains needle; empty needle = true (all globals, to avoid ?do+locals)
variable ss-ha 0 ss-ha !
variable ss-hl 0 ss-hl !
variable ss-na 0 ss-na !
variable ss-nl 0 ss-nl !
variable ss-i 0 ss-i !
variable ss-j 0 ss-j !
\ fuzzy: each needle character appears in haystack in order (skipping allowed, fzf-style)
variable fz-ha 0 fz-ha !
variable fz-hl 0 fz-hl !
variable fz-na 0 fz-na !
variable fz-nl 0 fz-nl !
variable fz-i 0 fz-i !
variable fz-j 0 fz-j !
create fz-marks 256 allot
variable fz-nmarks 0 fz-nmarks !
variable fz-total 0 fz-total !     \ total score (used by lp-filter for sorting)
variable fz-ta 0 fz-ta !           \ current term
variable fz-tu 0 fz-tu !
variable fz-ti 0 fz-ti !
variable fz-ts 0 fz-ts !
variable fz-prev 0 fz-prev !
variable fz-bonus 0 fz-bonus !
variable fz-cur 0 fz-cur !
variable fz-cur2 0 fz-cur2 !
variable fz-best -1 fz-best !
variable fz-start 0 fz-start !
variable fz-c 0 fz-c !
variable fz-pc 0 fz-pc !
variable fz-gap 0 fz-gap !
variable fz-n 0 fz-n !
variable fz-m 0 fz-m !
variable fz-gs 0 fz-gs !
variable fz-ps 0 fz-ps !
variable fz-pm 0 fz-pm !
variable fz-pd 0 fz-pd !
variable fz-sc 0 fz-sc !
variable fz-bj 0 fz-bj !
variable fz-lastD 0 fz-lastD !
variable fz-lastM 0 fz-lastM !
variable fz-curD 0 fz-curD !
variable fz-curM 0 fz-curM !
variable fz-rowstride 0 fz-rowstride !
variable fz-dval 0 fz-dval !
variable fz-mval 0 fz-mval !
variable fz-mreq 0 fz-mreq !
variable fz-found 0 fz-found !
create fz-cm 256 allot
variable fz-cn 0 fz-cn !
create fz-bm 256 allot
variable fz-bn 0 fz-bn !

\ ===== fzy scoring (jhawthorn/fzy affine-gap DP, ×1000 integers)=====
1000000 constant fz-score-max
-1000000 constant fz-score-min
-5  constant fz-gap-lead     \ SCORE_GAP_LEADING -0.005
-5  constant fz-gap-trail    \ SCORE_GAP_TRAILING -0.005
-10 constant fz-gap-inner    \ SCORE_GAP_INNER -0.01
1000 constant fz-match-cons  \ SCORE_MATCH_CONSECUTIVE 1.0
900  constant fz-match-slash
800  constant fz-match-word
700  constant fz-match-cap
600  constant fz-match-dot

512 constant fz-max-m
64  constant fz-max-n
create fz-D  fz-max-n fz-max-m * cells allot
create fz-Marr  fz-max-n fz-max-m * cells allot
create fz-bonarr  fz-max-m cells allot
create fz-lhay  fz-max-m allot
create fz-lnee  fz-max-n allot
variable fz-nc 0 fz-nc !
variable fz-pld 0 fz-pld !
variable fz-plm 0 fz-plm !
variable fz-pcd 0 fz-pcd !
variable fz-pcm 0 fz-pcm !
variable fz-pbon 0 fz-pbon !
variable fz-plh 0 fz-plh !

\ case-insensitive comparison
: fz-lower ( c -- c )  dup 65 91 within if 32 + then ;
: fz-ci= ( c1 c2 -- f )  fz-lower swap fz-lower = ;

\ position bonus (fz-bj = haystack index): based on the previous character
: fz-bonus-at ( j -- bonus )
  fz-bj !
  fz-bj @ 0= if fz-match-slash exit then
  fz-ha @ fz-bj @ 1- + c@ fz-pc !
  fz-ha @ fz-bj @ + c@ fz-c !
  fz-pc @ 47 =  fz-pc @ 92 =  or if fz-match-slash exit then
  fz-pc @ 45 =  fz-pc @ 95 = or  fz-pc @ 32 = or if fz-match-word exit then
  fz-pc @ 46 = if fz-match-dot exit then
  fz-pc @ 97 123 within  fz-c @ 65 91 within  and if fz-match-cap exit then
  0 ;

\ precompute the bonus for each haystack position (fzy precompute_bonus)
: fz-precompute-bonus ( -- )
  0 fz-j !
  begin fz-j @ fz-m @ < while
    fz-j @ fz-bonus-at  fz-bonarr fz-j @ cells + !
    1 fz-j +!
  repeat ;

\ precompute lowercased haystack/needle (the lowering in fzy setup_match_struct)
: fz-precompute-lower ( -- )
  0 fz-j !
  begin fz-j @ fz-m @ < while
    fz-ha @ fz-j @ + c@ fz-lower  fz-lhay fz-j @ + c!
    1 fz-j +!
  repeat
  0 fz-j !
  begin fz-j @ fz-n @ < while
    fz-ta @ fz-j @ + c@ fz-lower  fz-lnee fz-j @ + c!
    1 fz-j +!
  repeat ;

\ one DP row (row fz-i): read fz-lastD/fz-lastM, write fz-curD/fz-curM (pointerized)
: fz-row ( -- )
  fz-i @ fz-n @ 1- = if fz-gap-trail else fz-gap-inner then fz-gs !
  fz-lnee fz-i @ + c@ fz-nc !
  fz-score-min fz-ps !   fz-score-min fz-pm !   fz-score-min fz-pd !
  fz-lastD @ fz-pld !   fz-lastM @ fz-plm !
  fz-curD @ fz-pcd !    fz-curM @ fz-pcm !
  fz-bonarr fz-pbon !   fz-lhay fz-plh !
  0 fz-j !
  begin fz-j @ fz-m @ < while
    fz-plh @ c@  fz-nc @  = if
      fz-score-min fz-sc !
      fz-i @ 0= if
        fz-j @ fz-gap-lead *  fz-pbon @ @ +  fz-sc !
      else
        fz-j @ 0> if
          fz-pm @  fz-pbon @ @ +   fz-pd @ fz-match-cons +  max  fz-sc !
        then
      then
      fz-pld @ @ fz-pd !
      fz-plm @ @ fz-pm !
      fz-sc @ fz-pcd @ !
      fz-sc @  fz-ps @ fz-gs @ +  max  fz-ps !
      fz-ps @ fz-pcm @ !
    else
      fz-pld @ @ fz-pd !
      fz-plm @ @ fz-pm !
      fz-score-min fz-pcd @ !
      fz-ps @ fz-gs @ + fz-ps !
      fz-ps @ fz-pcm @ !
    then
    cell fz-pld +!  cell fz-plm +!  cell fz-pcd +!  cell fz-pcm +!  cell fz-pbon +!
    1 fz-plh +!  1 fz-j +!
  repeat ;

\ full DP: results into fz-D/fz-M, return M[n-1][m-1]
: fz-score-dp ( -- score )
  fz-m @ cells fz-rowstride !
  fz-D fz-lastD !   fz-Marr fz-lastM !
  fz-D fz-curD !    fz-Marr fz-curM !
  0 fz-i !  fz-row
  1 fz-i !
  begin fz-i @ fz-n @ < while
    fz-D  fz-i @ 1- fz-rowstride @ * +  fz-lastD !
    fz-Marr  fz-i @ 1- fz-rowstride @ * +  fz-lastM !
    fz-D  fz-i @ fz-rowstride @ * +  fz-curD !
    fz-Marr  fz-i @ fz-rowstride @ * +  fz-curM !
    fz-row
    1 fz-i +!
  repeat
  fz-Marr  fz-n @ 1- fz-rowstride @ * +  fz-m @ 1- cells +  @ ;

variable fz-xi 0 fz-xi !

\ backtrace the optimal match positions → fz-bm (fz-bn = fz-n is set by the caller)
: fz-backtrace ( -- )
  0 fz-mreq !
  fz-n @ 1- fz-i !
  fz-m @ 1- fz-j !
  begin
    0 fz-found !
    begin fz-j @ 0>=  fz-found @ 0=  and while
      fz-D  fz-i @ fz-rowstride @ * +  fz-j @ cells +  @ fz-dval !
      fz-Marr  fz-i @ fz-rowstride @ * +  fz-j @ cells +  @ fz-mval !
      fz-dval @ fz-score-min <> if
        fz-mreq @  fz-dval @ fz-mval @ =  or if
          fz-i @ 0>  fz-j @ 0>  and if
            fz-D  fz-i @ 1- fz-rowstride @ * +  fz-j @ 1- cells +  @ fz-match-cons +
            fz-mval @ =  fz-mreq !
          else
            0 fz-mreq !
          then
          fz-j @ fz-bm fz-i @ + c!
          -1 fz-j +!
          1 fz-found !
        then
      then
      fz-found @ 0= if -1 fz-j +! then
    repeat
    -1 fz-i +!
    fz-i @ 0< if exit then
  again ;

\ score one term: has_match quick filter + fzy DP + backtrace; positions go into fz-bm/fz-bn
: fz-score-term ( -- score )
  fz-tu @ fz-n !   fz-hl @ fz-m !
  fz-m @ fz-max-m >  fz-n @ fz-max-n >  or  fz-n @ fz-m @ >  or if  fz-score-min  exit  then
  fz-precompute-lower
  \ has_match: term is a subsequence of haystack (case-insensitive)
  0 fz-i !  0 fz-j !
  begin
    fz-j @ fz-m @ <  fz-i @ fz-n @ <  and
  while
    fz-lhay fz-j @ + c@  fz-lnee fz-i @ + c@  = if 1 fz-i +! then
    1 fz-j +!
  repeat
  fz-i @ fz-n @ <> if  fz-score-min  exit  then
  \ equal length + subsequence = exactly equal (fzy: match is only called after has_match)
  fz-n @ fz-m @ = if
    fz-n @ fz-xi !
    begin fz-xi @ 0> while
      -1 fz-xi +!
      fz-xi @  fz-bm fz-xi @ + c!
    repeat
    fz-n @ fz-bn !
    fz-score-max exit
  then
  fz-precompute-bonus
  fz-score-dp
  fz-backtrace
  fz-n @ fz-bn ! ;

\ highlight marks are sorted by ascending byte offset (insertion sort). n=fz-nmarks ≤256 and usually <30, far cheaper than the DP scoring below;
\ the render-side lp-char-marked? is a one-way merge scan and requires the mark array to be ascending by offset.
variable fz-si 0 fz-si !
variable fz-sj 0 fz-sj !
variable fz-sv 0 fz-sv !
: fz-sort-marks ( -- )
  0 fz-si !
  begin fz-si @ fz-nmarks @ < while
    fz-si @ fz-sj !
    fz-marks fz-sj @ + c@ fz-sv !
    begin fz-sj @ 0>  fz-marks fz-sj @ 1- + c@ fz-sv @ >  and while
      fz-marks fz-sj @ 1- + c@  fz-marks fz-sj @ + c!
      -1 fz-sj +!
    repeat
    fz-sv @ fz-marks fz-sj @ + c!
    1 fz-si +!
  repeat ;

\ multi-keyword: space-separated terms, each scored (AND + summed); the position union goes into fz-marks
: fuzzy-match? ( ha hl na nl -- f )
  fz-nl !  fz-na !  fz-hl !  fz-ha !
  0 fz-nmarks !  0 fz-total !
  fz-nl @ 0= if true exit then
  0 fz-ti !
  begin fz-ti @ fz-nl @ < while
    begin fz-ti @ fz-nl @ <  fz-na @ fz-ti @ + c@ 32 =  and while  1 fz-ti +!  repeat
    fz-ti @ fz-nl @ >= if  fz-sort-marks true exit  then
    fz-ti @ fz-ts !
    0 fz-tu !
    begin fz-ti @ fz-nl @ <  fz-na @ fz-ti @ + c@ 32 <>  and while  1 fz-ti +!  1 fz-tu +!  repeat
    fz-na @ fz-ts @ + fz-ta !
    fz-score-term fz-cur !
    fz-cur @ fz-score-min = if false exit then
    fz-cur @ fz-total +!
    fz-bm fz-marks fz-nmarks @ + fz-bn @ move
    fz-bn @ fz-nmarks +!
  repeat
  fz-sort-marks true ;

\ generic filter picker (list-panel): source/match/action hooks
\ matcher: fuzzy (needle matches in order)
create mode-listpanel  /mode cells allot
create keymap-listpanel /keymap cells allot
256 constant max-lp
create lp-starts max-lp cells allot
create lp-lens   max-lp cells allot
create lp-scores max-lp cells allot
create lp-mlens  max-lp cells allot
create lp-marks  max-lp fz-max-n * allot
\ per-item optional metadata (filled by the refs picker; grep/C-o/L do not fill it = 0, using the original path):
\   lp-cstarts[i] = content-segment start (0 = no content, fzy matches the whole item)
\   lp-symmarks[i]/lp-symlens[i] = symbol-range byte offsets (relative to the item start, ascending) / byte count
create lp-cstarts  max-lp cells allot
create lp-symlens  max-lp cells allot
create lp-symmarks max-lp fz-max-n * allot
\ clear the optional metadata (called before a new picker / re-collection; allot is not initialized, so stale garbage would be misread)
: lp-meta-clear ( -- )
  lp-cstarts  max-lp cells erase
  lp-symlens  max-lp cells erase
  lp-symmarks max-lp fz-max-n * erase ;
variable lp-item 0 lp-item !   \ flist item index (used by lp-filter iteration, aligned with the lp-cstarts index)
variable lp-cst  0 lp-cst !    \ content-segment start of the current item
variable lp-pos 0 lp-pos !
variable lp-mbase 0 lp-mbase !
variable lp-mlen 0 lp-mlen !
variable lp-nmatches 0 lp-nmatches !
variable lp-sel 0 lp-sel !
variable lp-top 0 lp-top !
variable lp-i 0 lp-i !
variable lp-s 0 lp-s !
variable lp-l 0 lp-l !
variable lp-collect-xt 0 lp-collect-xt !
variable lp-accept-xt  0 lp-accept-xt !
variable lp-close-xt   0 lp-close-xt !
variable lp-recollect-xt 0 lp-recollect-xt !  \ ( -- ) external re-collection (live grep); 0 = in-memory filtering
variable lp-exit-xt      0 lp-exit-xt !       \ ( -- ) picker-exit cleanup (live grep kills the process)
variable lp-dump-xt     0 lp-dump-xt !       \ ( -- ) action triggered by a key inside the picker (grep: dump to *grep*)
variable lp-vis 0 lp-vis !   \ visible list rows (panel-rows - the title row)
create lp-title 32 allot
variable lp-title-len 0 lp-title-len !
create lp-prompt 4 allot
: lp-set-title ( a u -- )  31 min lp-title-len !  lp-title lp-title-len @ move ;

\ filter flist: rows matching the pbuf substring go into lp-starts/lp-lens
: lp-filter
  0 lp-nmatches !
  0 lp-i !
  0 lp-item !
  begin
    lp-i @ flist-len @ <
  while
    lp-i @ lp-s !
    begin  lp-i @ flist-len @ <  flist @ lp-i @ + c@ 10 <>  and  while  1 lp-i +!  repeat
    lp-i @ lp-s @ - lp-l !
    lp-l @ 0> if
      \ fzy match scope: the refs picker only matches the path:line segment (lp-cstarts>0); otherwise the whole item (grep/C-o/L)
      lp-cstarts lp-item @ cells + @ lp-cst !
      lp-cst @ 0> if
        flist @ lp-s @ +  lp-cst @  pbuf pbuflen @  fuzzy-match?
      else
        flist @ lp-s @ + lp-l @  pbuf pbuflen @  fuzzy-match?
      then
      if
        lp-nmatches @ max-lp < if
          \ insert by descending score (fzf-style: best matches first)
          lp-nmatches @ lp-pos !
          begin lp-pos @ 0>  fz-total @  lp-scores lp-pos @ 1- cells + @  >  and while
            lp-starts lp-pos @ 1- cells + @  lp-starts lp-pos @ cells + !
            lp-lens   lp-pos @ 1- cells + @  lp-lens   lp-pos @ cells + !
            lp-scores lp-pos @ 1- cells + @  lp-scores lp-pos @ cells + !
            lp-mlens  lp-pos @ 1- cells + @  lp-mlens  lp-pos @ cells + !
            lp-marks  lp-pos @ 1- fz-max-n * +  lp-marks lp-pos @ fz-max-n * +  fz-max-n move
            lp-cstarts  lp-pos @ 1- cells + @  lp-cstarts  lp-pos @ cells + !
            lp-symlens  lp-pos @ 1- cells + @  lp-symlens  lp-pos @ cells + !
            lp-symmarks lp-pos @ 1- fz-max-n * +  lp-symmarks lp-pos @ fz-max-n * +  fz-max-n move
            -1 lp-pos +!
          repeat
          lp-s @ lp-starts lp-pos @ cells + !
          lp-l @ lp-lens   lp-pos @ cells + !
          fz-total @ lp-scores lp-pos @ cells + !
          fz-marks lp-marks lp-pos @ fz-max-n * +  fz-nmarks @ move
          fz-nmarks @ lp-mlens lp-pos @ cells + !
          lp-cst @ lp-cstarts lp-pos @ cells + !
          lp-symlens lp-item @ cells + @  lp-symlens lp-pos @ cells + !
          lp-symmarks lp-item @ fz-max-n * +  lp-symmarks lp-pos @ fz-max-n * +  fz-max-n move
          1 lp-nmatches +!
        then
      then
    then
    1 lp-i +!
    1 lp-item +!
  repeat ;

\ fill fz-marks with the byte offsets of the first literal hit of pbuf in line (a u); fz-nmarks = hit byte count
\ case-insensitive (aligned with rg --smart-case); ci-search returns ( match-addr rest-len flag )
\ no locals, no ?do: locals are forbidden on the collection path (inside the lp-filter-all loop)
variable ms-a 0 ms-a !
variable ms-u 0 ms-u !
variable ms-off 0 ms-off !
variable ms-n 0 ms-n !
variable ms-i 0 ms-i !

variable cs-ha 0 cs-ha !
variable cs-hu 0 cs-hu !
variable cs-na 0 cs-na !
variable cs-nl 0 cs-nl !
variable cs-i  0 cs-i !
variable cs-j  0 cs-j !
variable cs-max 0 cs-max !
variable cs-found 0 cs-found !

\ case-insensitive substring search (same interface as Gforth search; an empty needle matches at 0; returns addr u false when not found)
: ci-search ( ha hu na nl -- addr u flag )
  cs-nl ! cs-na ! cs-hu ! cs-ha !
  cs-hu @ cs-nl @ < if cs-ha @ cs-hu @ false exit then
  cs-hu @ cs-nl @ - cs-max !
  0 cs-i !
  begin
    cs-i @ cs-max @ <= while
      1 cs-found !
      0 cs-j !
      begin cs-j @ cs-nl @ < while
        cs-ha @ cs-i @ cs-j @ + + c@ fz-lower
        cs-na @ cs-j @ + c@ fz-lower
        <> if 0 cs-found ! then
        1 cs-j +!
      repeat
      cs-found @ if
        cs-ha @ cs-i @ +  cs-hu @ cs-i @ -  true exit
      then
      1 cs-i +!
    repeat
    cs-ha @ cs-hu @ false ;

: mark-substr ( a u -- )
  ms-u !  ms-a !
  0 fz-nmarks !
  ms-a @ ms-u @ pbuf pbuflen @ ci-search
  if
    drop  ms-a @ -  ms-off !         \ match offset
    pbuflen @ fz-max-n min  ms-n !   \ match length (≤64)
    ms-n @ fz-nmarks !
    ms-n @ ms-i !
    begin ms-i @ 0> while
      -1 ms-i +!
      ms-off @ ms-i @ +  fz-marks ms-i @ + c!
    repeat
  else
    2drop
  then ;

\ external filtering (live grep): rg already filtered, so all rows count as matches, recorded in flist order;
\ also compute the literal hits with mark-substr (highlight only, not for filtering)
: lp-filter-all
  0 lp-nmatches !
  0 lp-i !
  begin
    lp-i @ flist-len @ <
  while
    lp-i @ lp-s !
    begin  lp-i @ flist-len @ <  flist @ lp-i @ + c@ 10 <>  and  while  1 lp-i +!  repeat
    lp-i @ lp-s @ - lp-l !
    lp-l @ 0> if
      lp-nmatches @ max-lp < if
        lp-s @ lp-starts lp-nmatches @ cells + !
        lp-l @ lp-lens   lp-nmatches @ cells + !
        flist @ lp-s @ + lp-l @  mark-substr
        fz-marks lp-marks lp-nmatches @ fz-max-n * +  fz-nmarks @ move
        fz-nmarks @ lp-mlens lp-nmatches @ cells + !
        1 lp-nmatches +!
      then
    then
    1 lp-i +!
  repeat ;

\ input cursor + decode intermediates (reused by Gforth's line editor)
\ render picker row i (no locals: this word is called via execute inside ?do; locals corrupt the return stack)
variable lpr-row 0 lpr-row !
variable lpr-idx 0 lpr-idx !
variable lpr-s 0 lpr-s !
variable lpr-l 0 lpr-l !
variable lpr-sel? 0 lpr-sel? !
variable lpr-cr 0 lpr-cr !       \ content row number (after bottom-alignment offset; <0=top blank row 0=title >0=list item)
variable lps-i 0 lps-i !
variable lps-m 0 lps-m !
variable lps-n 0 lps-n !
variable lps-hit 0 lps-hit !
\ plain-memory UTF-8 first byte determines the character length (global pm-a; the deep-chain render path uses no locals)
: mb-len ( a -- n )
  pm-a !
  pm-a @ c@ dup 0x80 < if drop 1 exit then
  dup 0xE0 < if drop 2 exit then
  dup 0xF0 < if drop 3 exit then
  drop 4 ;

\ plain-memory character -> (display width, byte length) (truncation needs the display width; mb-len only gives the byte length)
\ no locals: the render path (?do-execute call) forbids locals; col is used to expand tabs to the next tab stop
variable mbw-a 0 mbw-a !
variable mbw-col 0 mbw-col !
variable mbw-b0 0 mbw-b0 !
variable mbw-cp 0 mbw-cp !
: mb-wl ( a col -- cw cn )
  mbw-col !  mbw-a !
  mbw-a @ c@ mbw-b0 !
  mbw-b0 @ 9 = if                        \ tab -> to the next tab stop
    tab-width @ 1 max  mbw-col @ over mod -  1 exit
  then
  mbw-b0 @ 0x80 < if 1 1 exit then
  mbw-b0 @ 0xC0 < if 1 1 exit then
  mbw-b0 @ 0xE0 < if
    mbw-b0 @ 0x1F and 6 lshift  mbw-a @ 1+ c@ 0x3F and or mbw-cp !
    mbw-cp @ xc-width dup 0< if drop 2 then  2 exit
  then
  mbw-b0 @ 0x0F and 12 lshift  mbw-a @ 1+ c@ 0x3F and 6 lshift or  mbw-a @ 2 + c@ 0x3F and or mbw-cp !
  mbw-cp @ xc-width dup 0< if drop 2 then  3 ;

\ ===== picker row-width truncation (the render path forbids locals; all globals)=====
\ no-locals versions of fb-str / fb-ud.: lp-row is called via execute inside ?do; locals corrupt the return stack
variable fbn-a 0 fbn-a !
variable fbn-l 0 fbn-l !
: fb-str-nl ( addr len -- )
  fbn-l !  fbn-a !
  fbn-a @  fb @ fblen @ +  fbn-l @  move
  fbn-l @ fblen +! ;
: fb-ud-nl ( n -- )  0 <# #s #> fb-str-nl ;
variable lp-w 0 lp-w !
variable lp-keep-right 0 lp-keep-right !   \ 1 = keep the right end when over-wide (fzf --keep-right)
variable lp-maxw 0 lp-maxw !
variable lp-totw 0 lp-totw !
variable lp-cut  0 lp-cut !
variable lp-start 0 lp-start !
variable lp-trunc? 0 lp-trunc? !
variable lp-budget 0 lp-budget !
variable lp-cw 0 lp-cw !
variable lp-cn 0 lp-cn !
variable lp-rend-start 0 lp-rend-start !
variable lp-rend-end   0 lp-rend-end !

: lp-ellipsis  226 fb-c 128 fb-c 166 fb-c ;   \ "…" (display width 1)

\ measure: total width lp-totw + head truncation point lp-cut (the byte offset where the width first exceeds maxw-1)
: lp-measure
  0 lp-totw !  0 lp-cut !  0 lps-i !
  begin lps-i @ lpr-l @ < while
    flist @ lpr-s @ + lps-i @ +  lp-totw @  mb-wl lp-cn ! lp-cw !
    lp-cw @ lp-totw +!
    lp-cut @ 0=  lp-totw @ lp-maxw @ 1 - >  and if  lps-i @ lp-cut !  then
    lp-cn @ lps-i +!
  repeat
  lp-cut @ 0= if lpr-l @ lp-cut ! then ;

\ keep-right start byte: first the total width, then find the offset where the accumulated width >= totw-(maxw-1)
: lp-measure-start
  lp-measure
  lp-totw @ lp-maxw @ 1 - - 0 max lp-budget !
  0 lps-i !  0 lp-totw !  0 lp-start !
  begin lps-i @ lpr-l @ < while
    flist @ lpr-s @ + lps-i @ +  lp-totw @  mb-wl lp-cn ! lp-cw !
    lp-cw @ lp-totw +!
    lp-start @ 0=  lp-totw @ lp-budget @ >=  and if  lps-i @ lp-start !  then
    lp-cn @ lps-i +!
  repeat
  lp-start @ 0= if 0 lp-start ! then ;

\ ===== ~ abbreviation / expansion ($HOME ↔ ~)=====
create homebuf 512 allot
variable home-len 0 home-len !
variable ha-home 0 ha-home !
variable hu-home 0 hu-home !
create tildebuf 512 allot
variable tilde-len 0 tilde-len !

\ ( a u -- a' u' ) abbreviate to ~/ when it starts with $HOME/; otherwise unchanged
: home-shorten
  pm-u !  pm-a !
  s" HOME" getenv hu-home ! ha-home !
  hu-home @ 0>  pm-u @ hu-home @ 1+ >=  and if
    pm-a @ pm-u @  ha-home @ hu-home @  string-prefix? if
      pm-a @ hu-home @ + c@ 47 = if
        126 homebuf c!
        pm-a @ hu-home @ +  homebuf 1 +  pm-u @ hu-home @ -  move
        pm-u @ hu-home @ - 1 + home-len !
        homebuf home-len @ exit
      then
    then
  then
  pm-a @ pm-u @ ;

\ a u → if the path is under cwd, drop the cwd prefix (relative path); otherwise unchanged
\ (LSP returns file:// absolute URIs; gd/gr make them relative to cwd when opening, consistent with command-line relative paths)
create csh-buf 512 allot
variable csh-a 0 csh-a !
variable csh-u 0 csh-u !
variable csh-len 0 csh-len !
: cwd-shorten ( a u -- a u )
  csh-u ! csh-a !
  csh-buf 511 get-dir csh-len !
  csh-len @ 0>  csh-u @ csh-len @ 1+ >=  and if
    csh-a @ csh-u @  csh-buf csh-len @  string-prefix? if
      csh-a @ csh-len @ + c@ 47 = if
        csh-a @ csh-len @ 1+ +  csh-u @ csh-len @ 1+ -  exit
      then
    then
  then
  csh-a @ csh-u @ ;

\ ( a u -- a' u' ) expand ~/ to $HOME/...; otherwise unchanged
: tilde-expand
  pm-u !  pm-a !
  pm-u @ 1 >  pm-a @ c@ 126 =  pm-a @ 1+ c@ 47 =  and  and if
    s" HOME" getenv hu-home ! ha-home !
    hu-home @ 0> if
      ha-home @ tildebuf hu-home @ move
      pm-a @ 1+  tildebuf hu-home @ +  pm-u @ 1-  move
      hu-home @ pm-u @ 1- + tilde-len !
      tildebuf tilde-len @ exit
    then
  then
  pm-a @ pm-u @ ;

\ whether the current character (at lps-i, length lps-n) contains a match byte; consume all marks within the character
: lp-char-marked? ( -- f )
  0 lps-hit !
  begin lps-m @ lp-mlen @ <  lp-mbase @ lps-m @ + c@ lps-i @ <  and while
    1 lps-m +!
  repeat
  lps-m @ lp-mlen @ < if
    lp-mbase @ lps-m @ + c@  lps-i @ lps-n @ +  < if
      1 lps-hit !
      begin lps-m @ lp-mlen @ <  lp-mbase @ lps-m @ + c@ lps-i @ lps-n @ +  <  and while
        1 lps-m +!
      repeat
    then
  then
  lps-hit @ ;
\ whether the current character is inside a symbol range (relative to the item start, ascending; independent of the fzy match range)
variable lss-m 0 lss-m !
variable lss-base 0 lss-base !
variable lss-mlen 0 lss-mlen !
variable lss-hit 0 lss-hit !
: lp-char-sym? ( -- f )
  lss-mlen @ 0= if false exit then
  0 lss-hit !
  begin lss-m @ lss-mlen @ <  lss-base @ lss-m @ + c@ lps-i @ <  and while
    1 lss-m +!
  repeat
  lss-m @ lss-mlen @ < if
    lss-base @ lss-m @ + c@  lps-i @ lps-n @ +  < if
      1 lss-hit !
      begin lss-m @ lss-mlen @ <  lss-base @ lss-m @ + c@ lps-i @ lps-n @ +  <  and while
        1 lss-m +!
      repeat
    then
  then
  lss-hit @ ;
\ output the current character (lps-i, lps-n bytes): tabs expand to spaces (a terminal tab does not overwrite the cell, leaving the previous frame's background)
variable lp-col 0 lp-col !
\ output the current character (lps-i, lps-n bytes): tabs expand to the next tab stop, \r becomes a space (to avoid the cursor returning to the line start), update lp-col
variable lp-emit-c 0 lp-emit-c !
: lp-emit ( -- )
  flist @ lpr-s @ + lps-i @ + c@ lp-emit-c !
  lp-emit-c @ 9 = if
    tab-width @ 1 max  lp-col @ over mod -  lp-cw !
    lp-cw @ lp-col +!
    begin lp-cw @ 0> while  -1 lp-cw +!  32 fb-c  repeat
  else
    lp-emit-c @ 13 = if
      32 fb-c  1 lp-col +!
    else
      flist @ lpr-s @ + lps-i @ +  lps-n @  fb-str-nl
      flist @ lpr-s @ + lps-i @ +  lp-col @  mb-wl drop  lp-col +!
    then
  then ;
\ render the current row text (lpr-s/lpr-l), highlighting matches per character (fzf hl); lpr-sel? decides the background
\ when over-wide, truncate by display width: by default keep the head + tail …; lp-keep-right=1 keeps the right end + head …
: lp-str
  0 lps-m !
  lp-marks lpr-idx @ fz-max-n * + lp-mbase !
  lp-mlens lpr-idx @ cells + @ lp-mlen !
  0 lss-m !
  lp-symmarks lpr-idx @ fz-max-n * + lss-base !
  lp-symlens lpr-idx @ cells + @ lss-mlen !
  scr-cols @ lp-maxw !
  lp-measure
  lp-totw @ lp-maxw @ > if
    true lp-trunc? !
    lp-keep-right @ if
      lp-measure-start
      lp-start @ lp-rend-start !
      lpr-l @ lp-rend-end !
      lp-ellipsis
    else
      0 lp-rend-start !
      lp-cut @ lp-rend-end !
    then
  else
    false lp-trunc? !
    0 lp-rend-start !
    lpr-l @ lp-rend-end !
  then
  \ render column: with keep-right truncation the head … already occupies 1 column, the rest starts from 0
  lp-trunc? @ lp-keep-right @ and if 1 else 0 then  lp-col !
  lp-rend-start @ lps-i !
  begin lps-i @ lp-rend-end @ < while
    flist @ lpr-s @ + lps-i @ + mb-len lps-n !
    lp-char-marked? if
      lpr-sel? @ if match-sel-attr @ else match-attr @ then  style>fb
      lp-emit
      lpr-sel? @ if panel-attr @ else 0 then style>fb
    else
      lp-char-sym? if
        sym-attr @ style>fb
        lp-emit
        lpr-sel? @ if panel-attr @ else 0 then style>fb
      else
        lp-emit
      then
    then
    lps-n @ lps-i +!
  repeat
  lp-trunc? @ if  lp-keep-right @ 0= if  lp-ellipsis  then  then ;

: lp-row ( row -- )
  lpr-row !
  lpr-row @ 0= if
    \ title: type [n/m] (the query is in the bottom minibuffer)
    lp-title lp-title-len @ fb-str-nl
    s"  [" fb-str-nl
    lp-sel @ 1+ fb-ud-nl
    s" /" fb-str-nl
    lp-nmatches @ fb-ud-nl
    s" ]" fb-str-nl
    27 fb-c 91 fb-c 75 fb-c             \ \e[K clears the title-row tail
    exit
  then
  lpr-row @ lp-top @ + 1- lpr-idx !
  lpr-idx @ lp-nmatches @ >= if
    27 fb-c 91 fb-c 75 fb-c             \ \e[K clears blank rows to the default background
    exit
  then
  lpr-idx @ lp-sel @ = lpr-sel? !
  lp-starts lpr-idx @ cells + @ lpr-s !
  lp-lens lpr-idx @ cells + @ lpr-l !
  lpr-sel? @ if
    \ selected row: first fill the whole row with the blue background (not relying on \e[K bce), then return to the row start and write the content
    panel-attr @ style>fb
    scr-cols @ lp-w !
    begin lp-w @ 0> while  32 fb-c  -1 lp-w +!  repeat
    \ back to the row start \e[row+1;1H (no locals: lp-row is called via execute inside ?do)
    27 fb-c  91 fb-c
    scr-rows @ panel-rows @ - lpr-row @ + 1+  fb-ud-nl
    59 fb-c  49 fb-c  72 fb-c
    panel-attr @ style>fb
  then
  lp-str
  lpr-sel? @ 0= if
    0 style>fb
    27 fb-c 91 fb-c 75 fb-c             \ \e[K clears non-selected rows to the default background
  then ;

\ update the picker row count/scroll/render hook from lp-nmatches (without re-filtering/collecting)
: lp-update
  lp-nmatches @ 0= if
    0 lp-sel !  0 lp-top !  0 panel-rows !
  else
    lp-sel @ lp-nmatches @ >= if  lp-nmatches @ 1- lp-sel !  then
    lp-nmatches @ 1+  scr-rows @ 1-  min panel-rows !   \ rows with content = title + list (the picker occupies the bottom, including the statusline)
    panel-rows @ 1-  1 max  lp-vis !                     \ visible list rows
    lp-sel @ lp-top @ < if  lp-sel @ lp-top !  then
    lp-sel @ lp-top @ lp-vis @ + >= if  lp-sel @ lp-vis @ 1- - 0 max lp-top !  then
  then
  ['] lp-row panel-xt !
  1 panel-dirty ! ;

: lp-refresh
  lp-recollect-xt @ ?dup if
    execute              \ external re-collection (live grep: rerun rg)
  else
    lp-filter            \ in-memory filtering
  then
  lp-update ;

: mst-listpanel  s" SELECT" ;

\ ===== C-o input cursor + readline-style editing =====
variable lp-kw-p 0 lp-kw-p !
variable lp-kw-q 0 lp-kw-q !
create lp-kill-buf 256 allot
variable lp-kill-len 0 lp-kill-len !

: pb-ins ( c -- )
  tmp-c !
  pbuflen @ 255 < if
    pbuflen @ lp-kw-p !
    begin lp-kw-p @ pbpos @ > while
      lp-kw-p @ 1- pbuf + c@  lp-kw-p @ pbuf + c!
      -1 lp-kw-p +!
    repeat
    tmp-c @ pbuf pbpos @ + c!
    1 pbuflen +!  1 pbpos +!
  then ;

: pb-ins-cp { cp }  cp ['] pb-ins utf8-emit ;

: pb-del-at
  pbpos @ pbuflen @ < if
    pbuf pbpos @ + c@  dup 0x80 < if drop 1 else dup 0xE0 < if drop 2 else dup 0xF0 < if drop 3 else drop 4 then then then  lp-kw-q !
    pbpos @ lp-kw-p !
    begin lp-kw-p @ pbuflen @ lp-kw-q @ - < while
      lp-kw-p @ lp-kw-q @ + pbuf + c@  lp-kw-p @ pbuf + c!
      1 lp-kw-p +!
    repeat
    pbuflen @ lp-kw-q @ - pbuflen !
  then ;

: lp-home  0 pbpos ! ;
: lp-end  pbuflen @ pbpos ! ;
: lp-left  pbpos @ 0> if
  -1 pbpos +!
  begin pbpos @ 0>  pbuf pbpos @ + c@ 0x80 0xC0 within  and while  -1 pbpos +!  repeat
  then ;
: lp-right  pbpos @ pbuflen @ < if
  pbuf pbpos @ + c@ dup 0x80 < if drop 1 else dup 0xE0 < if drop 2 else dup 0xF0 < if drop 3 else drop 4 then then then
  pbpos +!
  then ;
: lp-delete  pb-del-at  lp-refresh ;
: lp-clear  0 pbuflen !  0 pbpos !  lp-refresh ;

: lp-kill-end
  pbuflen @ pbpos @ > if
    pbuf pbpos @ +  lp-kill-buf  pbuflen @ pbpos @ -  move
    pbuflen @ pbpos @ - lp-kill-len !
    pbpos @ pbuflen !
    lp-refresh
  then ;

: lp-kill-start
  pbpos @ 0> if
    pbuf  lp-kill-buf  pbpos @  move
    pbpos @ lp-kill-len !
    pbuf  pbuf pbpos @ +  pbuflen @ pbpos @ -  move
    pbuflen @ pbpos @ - pbuflen !
    0 pbpos !
    lp-refresh
  then ;

: lp-kill-word
  pbpos @ lp-kw-p !
  begin lp-kw-p @ 0>  pbuf lp-kw-p @ 1- + c@ 32 =  and while  -1 lp-kw-p +!  repeat
  begin lp-kw-p @ 0>  pbuf lp-kw-p @ 1- + c@ 32 <>  and while  -1 lp-kw-p +!  repeat
  lp-kw-p @ lp-kw-q !
  pbpos @ lp-kw-q @ > if
    pbuf lp-kw-q @ +  lp-kill-buf  pbpos @ lp-kw-q @ -  move
    pbpos @ lp-kw-q @ - lp-kill-len !
    pbuf lp-kw-q @ +  pbuf pbpos @ +  pbuflen @ pbpos @ -  move
    pbuflen @ pbpos @ lp-kw-q @ - - pbuflen !
    lp-kw-q @ pbpos !
    lp-refresh
  then ;

: lp-yank
  lp-kill-len @ 0>  pbuflen @ lp-kill-len @ + 255 <  and if
    pbuf pbpos @ lp-kill-len @ + +  pbuf pbpos @ +  pbuflen @ pbpos @ -  move
    lp-kill-buf  pbuf pbpos @ +  lp-kill-len @  move
    lp-kill-len @ pbuflen +!
    lp-kill-len @ pbpos +!
    lp-refresh
  then ;

\ ===== readline editing words for the prompt (reuse pbuf/pbpos/lp-kill-*; do not use lp-refresh, to avoid lp-filter scanning stale flist)=====
: pb-delete  pb-del-at  0 pnrows ! ;
: pb-backspace  pbpos @ 0> if  lp-left  pb-del-at  0 pnrows !  then ;   \ delete before the cursor (readline-consistent; no-op at the line start)
: pb-clear  0 pbuflen !  0 pbpos !  0 pnrows ! ;

: pb-kill-end
  pbuflen @ pbpos @ > if
    pbuf pbpos @ +  lp-kill-buf  pbuflen @ pbpos @ -  move
    pbuflen @ pbpos @ - lp-kill-len !
    pbpos @ pbuflen !
    0 pnrows !
  then ;

: pb-kill-start
  pbpos @ 0> if
    pbuf  lp-kill-buf  pbpos @  move
    pbpos @ lp-kill-len !
    pbuf  pbuf pbpos @ +  pbuflen @ pbpos @ -  move
    pbuflen @ pbpos @ - pbuflen !
    0 pbpos !
    0 pnrows !
  then ;

: pb-kill-word
  pbpos @ lp-kw-p !
  begin lp-kw-p @ 0>  pbuf lp-kw-p @ 1- + c@ 32 =  and while  -1 lp-kw-p +!  repeat
  begin lp-kw-p @ 0>  pbuf lp-kw-p @ 1- + c@ 32 <>  and while  -1 lp-kw-p +!  repeat
  lp-kw-p @ lp-kw-q !
  pbpos @ lp-kw-q @ > if
    pbuf lp-kw-q @ +  lp-kill-buf  pbpos @ lp-kw-q @ -  move
    pbpos @ lp-kw-q @ - lp-kill-len !
    pbuf lp-kw-q @ +  pbuf pbpos @ +  pbuflen @ pbpos @ -  move
    pbuflen @ pbpos @ lp-kw-q @ - - pbuflen !
    lp-kw-q @ pbpos !
    0 pnrows !
  then ;

: pb-yank
  lp-kill-len @ 0>  pbuflen @ lp-kill-len @ + 255 <  and if
    pbuf pbpos @ lp-kill-len @ + +  pbuf pbpos @ +  pbuflen @ pbpos @ -  move
    lp-kill-buf  pbuf pbpos @ +  lp-kill-len @  move
    lp-kill-len @ pbuflen +!
    lp-kill-len @ pbpos +!
    0 pnrows !
  then ;

: lp-backspace  lp-left  pb-del-at  lp-refresh ;
: lp-up    lp-sel @ 0> if -1 lp-sel +!  lp-update  then ;
: lp-down  lp-sel @ lp-nmatches @ 1- < if 1 lp-sel +!  lp-update  then ;
: lp-accept
  lp-nmatches @ 0> if
    flist @ lp-starts lp-sel @ cells + @ +  lp-lens lp-sel @ cells + @  lp-accept-xt @ execute
  else
    pbuflen @ 0> if
      pbuf pbuflen @ lp-accept-xt @ execute
    then
  then
  lp-exit-xt @ ?dup if execute then
  0 prompting !  0 panel-xt !  0 panel-rows !  1 panel-dirty !  mode-pop ;
: lp-cancel  lp-exit-xt @ ?dup if execute then  0 prompting !  0 panel-xt !  0 panel-rows !  1 panel-dirty !  mode-pop ;
: lp-dump    lp-dump-xt @ ?dup if execute then ;
: lp-input { c -- }
  c 32 >= if c pb-ins-cp  lp-refresh  then ;

\ close the selected item (K-DELETE): only active when the picker has a close action and a selection; a no-op without a close action
: lp-close ( -- )
  lp-close-xt @ 0= if exit then
  lp-nmatches @ lp-sel @ > if
    flist @ lp-starts lp-sel @ cells + @ +  lp-lens lp-sel @ cells + @  lp-close-xt @ execute
    lp-collect-xt @ execute
    lp-refresh
  then ;

\ open a picker: collect = fill flist, accept = selection action, close = close action (0 = none)
: listpanel-open ( collect-xt accept-xt close-xt -- )
  lp-close-xt !  lp-accept-xt !  lp-collect-xt !
  0 lp-recollect-xt !  0 lp-dump-xt !  0 lp-exit-xt !   \ ordinary pickers: no external re-collection/dump/exit cleanup (live grep sets its own)
  lp-meta-clear                                            \ clear the optional metadata (collect refills it; allot is not initialized)
  lp-collect-xt @ execute
  0 pbuflen !  0 pbpos !  0 lp-sel !  0 lp-top !   \ enter even with an empty list: allows typing a new path (Ctrl-o in an empty directory creates a new file)
  lp-refresh
  s" > " lp-prompt swap move  2 prompt-prefix-len !  lp-prompt prompt-prefix !
  1 prompting !
  mode-listpanel mode-push ;

\ ===== sources: file / buffer =====
: do-buffer-open ( a u -- )  buffer-open ;
: do-buffer-switch ( a u -- )  tilde-expand buffer-find ?dup if buffer-switch then ;
\ collect buffer names (all, including system buffers; buffer-close rejects system buffers on d/x close)
\ no locals inside the loop (locals live on the return stack; loop + to corrupt it): use globals
variable cb-b 0 cb-b !
: collect-buffers ( -- )
  0 flist-len !
  buffers-first @ cb-b !
  begin cb-b @ while
    cb-b @ buf-name@ cb-b @ buf-namelen@ home-shorten flist-add
    cb-b @ B:NEXT + @ cb-b !
  repeat ;

: filefind-open  s" FILES" lp-set-title  ['] collect-files  ['] do-buffer-open  0  listpanel-open ;
: path-tab-complete
  pbuflen @ pfx-len !
  pfx-len @ 0= if s" type a filename prefix" msg! exit then
  collect-files
  flist-len @ 0= if s" no files" msg! exit then
  0 nmatch !
  0 fi !
  begin
    fi @ flist-len @ <
  while
    fi @ fs !
    begin
      fi @ flist-len @ <  flist fi @ + c@ 10 <>  and
    while
      1 fi +!
    repeat
    fi @ fs @ - fel !
    fel @ 0> if
      flist @ fs @ +  fel @  pfx-match? if
        nmatch @ 0= if
          flist @ fs @ +  m1  fel @ move  fel @ m1-len !
          flist @ fs @ +  cpfx  fel @ move  fel @ cpfx-len !
        else
          flist @ fs @ +  fel @  update-cpfx
        then
        1 nmatch +!
      then
    then
    1 fi +!
  repeat
  nmatch @ 0= if #bell emit  s" no match" msg! exit then
  nmatch @ 1 = if
    m1 m1-len @ pbuf-set-prefix
  else
    cpfx-len @ pfx-len @ > if
      cpfx cpfx-len @ pbuf-set-prefix
    else
      #bell emit  s" no further completion" msg!
    then
  then ;

variable path-complete-xt  ' path-tab-complete path-complete-xt !

: do-open  pbuf pbuflen @ buffer-open ;
: buffer-open-name  filefind-open ;

: buffer-save-as  s" save as: " ['] do-save-as prompt-open ;

\ ===== buffer list (*buffers*)=====
: buffer-next
  gctx @ B:NEXT + @ ?dup if buffer-switch exit then
  buffers-first @ ?dup if buffer-switch then ;

: cur-line-into { dst }        \ copy the current-line text into dst, return the length
  cursor @ offset->linecol drop { line }
  line line-start { ls }
  line line-length { ll }
  ll 0 ?do  ls i + g-char-at  dst i + c!  loop
  ll ;

variable buffers-buf  0 buffers-buf !      \ the *buffers* list buffer (set by list-buffers)

\ rebuild the *buffers* content (clear in place + refill; the caller guarantees the current buffer is *buffers*)
: list-buffers-rebuild
  gctx @ { b0 }
  0 g-front !  0 g-back !  1 lix-dirty !
  0 lix-epend !  0 lix-ecomplex !   \ full clear: invalidate the incremental edit tracking
  buffers-first @ { b }
  begin b while
    b0  b buf-name@ b buf-namelen@  buffer-append
    b0  nl-buf 1  buffer-append
    b B:NEXT + @ to b
  repeat ;

\ total buffer count
: buffers-count ( -- n )
  0  buffers-first @ { b }
  begin b while  1+  b B:NEXT + @ to b  repeat ;

: list-buffers
  s" *buffers*" buffer-find { b0 }
  b0 0= if  s" *buffers*" BF_NOFILE BF_READONLY or buffer-new to b0  then
  b0 buffer-switch
  b0 buffers-buf !
  0 cursor !
  list-buffers-rebuild ;

: buffer-list-select                  \ the buffer corresponding to the current-line name
  pbuf cur-line-into { n }
  n 0> if  pbuf n buffer-find ?dup if buffer-switch then  then ;

\ ===== jump-history ctx data (defined early: buffer-close-now needs to invalidate dangling entries)=====
64 constant max-ctx
create ctx-buf  max-ctx cells allot
create ctx-off  max-ctx cells allot
variable nctx 0 nctx !
variable ctx-b 0 ctx-b !
variable ctx-o 0 ctx-o !

\ invalidate jump-history entries pointing at b (B: called on buffer close, to keep gb from using a dangling pointer)
: ctx-invalidate { b -- }
  0 { ix }
  begin ix nctx @ < while
    ix cells ctx-buf + @ b = if  0 ix cells ctx-buf + !  then
    ix 1+ to ix
  repeat ;

\ ===== close a buffer =====
variable close-armed  0 close-armed !      \ the modified buffer awaiting close confirmation

\ close a buffer (no confirmation): if it is the current buffer, switch to a neighbor first
: buffer-close-now { b -- }
  b B:NEXT + @ ?dup 0= if buffers-first @ then { nb }
  nb 0<> nb b <> and if
    wctx @ { act }
    windows-first @ { w }
    begin w while
      w W:B + @ b = if
        w wctx !
        w W:B + @ gctx !
        nb buffer-switch
      then
      w W:NEXT + @ to w
    repeat
    act wctx !
    act W:B + @ gctx !
    0 pnrows !
  else
    s" cannot close last buffer" msg-say exit
  then
  b ctx-invalidate            \ B: clear the dangling entries in the jump history
  b buffer-free ;

\ close a buffer: system buffers are forbidden; a modified file needs a second confirmation; returns true = closed / false = not closed
: buffer-close { b }  \ ( b -- closed? )
  b B:FLAGS + @ BF_NOFILE and if
    s" system buffer: not closable" msg-say false exit
  then
  close-armed @ { armed }
  0 close-armed !
  b B:MODIFIED + @ if
    armed b <> if
      b close-armed !
      s" modified!  press again to discard & close" msg-say false exit
    then
  then
  b buffer-close-now
  true ;

\ picker d/x: close the selected buffer (look it up by name, then close)
: do-buffer-close ( a u -- )  tilde-expand buffer-find ?dup if buffer-close drop then ;
: buffind-open   s" BUFFERS" lp-set-title  ['] collect-buffers ['] do-buffer-switch ['] do-buffer-close  listpanel-open ;

: cmd-buffer-close  gctx @ 0<> if gctx @ buffer-close drop then ;

\ inside the *buffers* list: close the buffer of the current line (auto-refresh the list after closing, keep the cursor on the original line)
: buffer-list-close
  pbuf cur-line-into { n }
  n 0> if
    pbuf n buffer-find ?dup if
      cursor @ offset->linecol drop { line }
      buffer-close if
        list-buffers-rebuild
        buffers-count { cnt }
        line cnt 1- min  0 max  line-start  cursor !
      then
    then
  then ;

: cmd-quit       any-file-modified? 0= if 1 leave-loop ! else 1 confirm-quit ! then ;
\ prefix commands (g/z/w use the generic prefix-enter + the key→mode table; pmode-* are filled by register-prefix in init-keymaps)

\ config layer: change the leader key and re-bind (default space; e.g. char , set-leader-key)
: set-leader-key ( key -- )
  dup leader-key !
  pmode-leader @ bind-prefix ;

\ config layer: bind into the leader keymap (e.g. char b ' open-browser bind-leader)
: bind-leader ( key xt -- )  pmode-leader @ bind-mode ;
\ window commands: mode-pop first (leave the prefix mode) then act, to avoid leaking the prefix mode into the old window
: cmd-split-v        mode-pop split-v ;
: cmd-split-s        mode-pop split-s ;
: cmd-win-focus-next mode-pop win-focus-next ;
: cmd-win-close      mode-pop win-close ;
: cmd-win-focus-h    mode-pop 1 win-focus-dir ;
: cmd-win-focus-j    mode-pop 2 win-focus-dir ;
: cmd-win-focus-k    mode-pop 3 win-focus-dir ;
: cmd-win-focus-l    mode-pop 4 win-focus-dir ;
\ insert-mode selector: returns "the mode to enter". Default mode-insert; repl.fs replaces it with "*repl* → mode-repl".
: insert-mode-choice  mode-insert ;
variable insert-mode-xt  ' insert-mode-choice insert-mode-xt !
: cmd-enter-ins  insert-mode-xt @ execute mode-set ;
: cmd-append     fe-right  insert-mode-xt @ execute  mode-set ;
: cmd-open-below  fe-line-end  fe-right  fe-insert-newline  insert-mode-xt @ execute mode-set ;  \ o open a new line below
: cmd-open-above   \ O open a new line above (insert the indentation first, then the newline, to avoid ls becoming stale after a line-start insert)
  fe-line-start
  cursor @ offset->linecol drop { line }
  line line-start { ls }
  line line-indent-len { ilen }
  ilen 0 ?do  ls i + g-char-at fe-insert loop
  10 fe-insert
  ls cursor !   ilen cursor +!
  insert-mode-xt @ execute mode-set ;
\ move to the first non-blank of the line
: fe-line-first-nonblank
  u-broken
  cursor @ offset->linecol drop { line }
  line line-start { ls }
  line line-indent-len { il }
  ls il + cursor ! ;
: cmd-insert-line-start  fe-line-first-nonblank  insert-mode-xt @ execute mode-set ;  \ I insert at the line start (first non-blank)
: cmd-append-line-end    fe-line-end  fe-right  insert-mode-xt @ execute mode-set ;   \ A insert at the line end
: cmd-esc-ins    mode-command mode-set ;
: cmd-tab        9 fe-insert ;
: cmd-search-next patlen @ 0> if 1 search-do then ;
: cmd-search-prev patlen @ 0> if -1 search-do then ;
: cmd-toggle-case
  ignorecase @ 0= ignorecase !
  ignorecase @ if s" ignore case" else s" match case" then msg! ;

\ ===== status line (full version, installed into core's status-line-xt hook)=====
defer fname-display                        \ ( -- addr len ) display name (the lsp layer may override it to be relative to the project root)
: fname-display-default ( -- addr len )
  fname @ 0= if s" [no name]" exit then
  fname @ flen @ ;
' fname-display-default is fname-display

variable ft-a 0 ft-a !
variable ft-u 0 ft-u !
variable ft-i 0 ft-i !
variable ft-w 0 ft-w !
variable ft-max 0 ft-max !
variable ft-cn 0 ft-cn !
variable ft-cw 0 ft-cw !
\ when over-wide, show … + the last (maxw-1) columns; otherwise unchanged; return the actual width used
: fb-str-tail ( a u maxw -- usedw )
  ft-max !  ft-u !  ft-a !
  0 ft-w !  0 ft-i !
  begin ft-i @ ft-u @ < while
    ft-a @ ft-i @ +  ft-w @  mb-wl ft-cw !  ft-cn !
    ft-cw @ ft-w +!   ft-cn @ ft-i +!
  repeat
  ft-w @ { tw }
  tw ft-max @ <= if
    ft-a @ ft-u @ fb-str  tw exit
  then
  0 ft-w !  0 ft-i !
  begin ft-w @ tw ft-max @ 1- - < while
    ft-a @ ft-i @ +  ft-w @  mb-wl ft-cw !  ft-cn !
    ft-cw @ ft-w +!   ft-cn @ ft-i +!
  repeat
  s" …" fb-str  1 ft-w !
  begin ft-i @ ft-u @ <  ft-w @ ft-max @ <  and while
    ft-a @ ft-i @ +  ft-w @  mb-wl ft-cw !  ft-cn !
    ft-a @ ft-i @ +  ft-cn @  fb-str
    ft-cw @ ft-w +!   ft-cn @ ft-i +!
  repeat
  ft-w @ ;


: fb-status
  32 fb-c
  searching @ if
    search-dir @ 1 = if 47 else 63 then fb-c   \ / or ?
    pat patlen @ fb-str
  else
  prompting @ if
    prompt-prefix @ prompt-prefix-len @ fb-str
    pbuf pbuflen @ fb-str
  else
  qreplacing @ if
    s" replace " fb-str  pat patlen @ fb-str  s" -> " fb-str  rpl rpllen @ fb-str
    s"   y/n/!/.  q=done" fb-str
  else
  confirm-quit @ if
    s" unsaved changes!  q=discard & quit   C-s=save & quit   any key=cancel" fb-str
  else
    msglen @ 0> if
      msgbuf msglen @ fb-str
    else
      cursor @ offset->linecol { cl col }
      cl 1+ ndigits { ld }   col 1+ ndigits { cd }
      bflags @ BF_NOFILE and 0= if  modified @ else  0  then { modf }
      mode-status$ { ma mu }   ma mu x-width { mw }
      showcmd-xt @ ?dup if execute else 0 0 then { sa sl }   sa sl x-width { sw }
      1 3 + mw + 3 + ld + 1+ cd + modf if 12 + then { fixed }
      scr-cols @ fixed - sw - 0 max { avail }
      fname-display avail fb-str-tail { used }
      32 fb-c 32 fb-c 32 fb-c
      ma mu fb-str
      32 fb-c 32 fb-c 32 fb-c
      cl 1+ fb-ud.  58 fb-c  col 1+ fb-ud.
      modf if 32 fb-c 32 fb-c s" [modified]" fb-str then
      sl 0> if                              \ right-align showcmd (combined statusline for a single window)
        fixed used + { pos }
        scr-cols @ sw - pos - 0 max 0 ?do 32 fb-c loop
        sa sl fb-str
      then
    then
  then
  then
  then
  then ;

\ ===== per-window statusline (installed into core's win-status-xt hook)=====
\ name + line:col + [modified] (no mode); record ws-w for core padding
\ ===== truncate the filename by display columns (for the statusline; ellipsis at the tail when over-wide)=====
: fb-win-status
  0 ws-w !
  \ first compute the fixed-part width + the available filename width
  cursor @ offset->linecol { cl col }
  cl 1+ ndigits { ld }   col 1+ ndigits { cd }
  bflags @ BF_NOFILE and 0= if  modified @ else  0  then { modf }
  mode-status$ { ma mu }   ma mu x-width { mw }
  1 mw +  2 +  3 +  ld +  1 +  cd +  modf if 12 + then  3 +  { fixed }   \ +3 reserved for showcmd
  win-cols @ fixed - 0 max { avail }
  \ render
  32 fb-c  1 ws-w +!
  ma mu fb-str  mw ws-w +!
  32 fb-c 32 fb-c  2 ws-w +!
  fname-display avail fb-str-tail  ws-w +!
  32 fb-c 32 fb-c 32 fb-c  3 ws-w +!
  cl 1+ fb-ud.  ld ws-w +!
  58 fb-c  1 ws-w +!
  col 1+ fb-ud.  cd ws-w +!
  modf if 32 fb-c 32 fb-c s" [modified]" fb-str  12 ws-w +! then ;

\ ===== global minibuffer (installed into core's minibuf-xt hook)=====
\ mode + transient (search/prompt/replace/confirmation/message)
: fb-minibuf
  32 fb-c                    \ leading space: consistent with fb-status/fb-win-status, so cursor-place aligns
  searching @ if
    search-dir @ 1 = if 47 else 63 then fb-c  pat patlen @ fb-str
  else
  prompting @ if
    prompt-prefix @ prompt-prefix-len @ fb-str  pbuf pbuflen @ fb-str
  else
  qreplacing @ if
    s" replace " fb-str  pat patlen @ fb-str  s" -> " fb-str  rpl rpllen @ fb-str
    s"   y/n/!/.  q=done" fb-str
  else
  confirm-quit @ if
    s" unsaved changes!  q=discard & quit   C-s=save & quit   any key=cancel" fb-str
  else
    msglen @ 0> if
      msgbuf msglen @ fb-str
    then
  then
  then
  then
  then ;

\ ===== search highlight (installed into core's hl-xt hook)=======
\ if there is a match at a, return the match end offset (with the reverse-video range [a,end)); otherwise return a
: search-hl { a -- hlend attr }
  patlen @ 0> if
    a match-at? ?dup if  S-SEARCH exit  then
  then
  a 0 ;

\ ===== cursor positioning during search/prompt (installed into core's cursor-place-xt hook)=====
: cursor-place ( -- flag )
  searching @ if
    2  pat patlen @ x-width +   scr-rows @   at-xy  true exit
  then
  prompting @ if
    1 prompt-prefix-len @ +  pbuf pbpos @ x-width +   scr-rows @   at-xy  true exit
  then
  false ;


\ ===== register + visual selection + paste (new features added by the extension layer)=====
\ register (yank buffer); the visual selection is derived from vis-anchor and cursor
create yank-buf 65536 allot
variable yank-len   0 yank-len !
variable visual     0 visual !
variable vis-anchor 0 vis-anchor !

: vis-lo ( -- n )  cursor @ vis-anchor @ min ;
: vis-hi ( -- n )  cursor @ vis-anchor @ max ;

\ copy the bytes of [lo,hi) into the register
: yank-range { lo hi -- }
  hi lo -  0 max  65536 min  yank-len !
  yank-len @ 0 ?do  lo i + g-char-at  yank-buf i +  c!  loop ;

\ insert the register contents at the cursor
: yank-insert
  bflags @ BF_READONLY and if exit then
  yank-len @ 0 ?do  yank-buf i + c@ fe-insert  loop ;

include syntax.fs
include lang-forth.fs
include lang-c.fs
include lang-go.fs
include lang-python.fs
include lang-rust.fs
include lang-javascript.fs
include lang-lua.fs
include lang-bash.fs
include lang-java.fs
include lang-css.fs
include lang-ruby.fs
include lang-sql.fs
include lang-html.fs
include lang-markdown.fs
include lang-make.fs
include repl.fs

: next-match { a lim -- pos }          \ start of the next search match in [a,lim); lim if none
  regex-search @ re-valid @ and if
    snap-build
    snap @ snap-len @ a re-find-next
    dup 0< if drop lim exit then        \ -1 none / -2 timeout
    dup lim < if exit then drop lim
  else
    a begin dup lim < while  dup match-at? if exit then 1+  repeat drop lim
  then ;

\ ===== bracket matching (the bracket at the cursor → highlight its pair)=====
\ no locals in the highlight deep chain (a known project pitfall: locals frames corrupt the return stack) — all globals.
\ bracket pair character: 0 for a non-bracket   ( c -- peer|0 )
: bracket-peer
  dup 40 = if drop 41 exit then   dup 41 = if drop 40 exit then
  dup 91 = if drop 93 exit then   dup 93 = if drop 91 exit then
  dup 123 = if drop 125 exit then  dup 125 = if drop 123 exit then
  drop 0 ;

\ bracket direction: 1=open -1=close 0=non-bracket   ( c -- n )
: bracket-dir
  dup 40 = over 91 = or over 123 = or if drop 1 exit then
  dup 41 = over 93 = or over 125 = or if drop -1 exit then
  drop 0 ;

\ find the paired bracket from pos along dir (same-type nesting count); return the position or -1
\ globals: br-pos/br-dir are inputs, br-i/br-c/br-depth/br-c0/br-peer are temporaries
variable br-pos 0 br-pos !
variable br-dir 0 br-dir !
variable br-i 0 br-i !
variable br-c 0 br-c !
variable br-depth 0 br-depth !
variable br-c0 0 br-c0 !
variable br-peer 0 br-peer !
: bracket-match
  br-pos @ g-char-at br-c0 !
  br-c0 @ bracket-peer br-peer !
  0 br-depth !
  br-pos @ br-i !
  begin
    br-dir @ 1 = if br-i @ 1+ br-i ! else br-i @ 1- br-i ! then
    br-i @ 0>=  br-i @ g-size <  and
  while
    br-i @ g-char-at br-c !
    br-c @ br-c0 @ = if
      1 br-depth +!
    else
      br-c @ br-peer @ = if
        br-depth @ 0= if br-i @ exit then
        -1 br-depth +!
      then
    then
  repeat
  -1 ;

\ cache: recompute only when the cursor+size change (one scan per render)
variable match-pos  -1 match-pos !
variable match-cur  -1 match-cur !
variable match-gsz  -1 match-gsz !
: ensure-match
  match-cur @ cursor @ =  match-gsz @ g-size =  and if exit then
  cursor @ match-cur !
  g-size match-gsz !
  -1 match-pos !
  cursor @ g-size >= if exit then
  cursor @ g-char-at bracket-dir
  dup 0= if drop exit then
  cursor @ br-pos !  br-dir !
  bracket-match match-pos ! ;

\ ===== diagnostics (LSP publishDiagnostics; rendered as underlines)=====
256 constant max-diags
create diag-line max-diags cells allot
create diag-b0   max-diags cells allot
create diag-b1   max-diags cells allot
create diag-sev  max-diags cells allot
variable ndiags  0 ndiags !
: diag-touch  0 ndiags ! ;               \ after an edit the old diagnostic offsets are stale; clear them (wait for the server to resend)
' diag-touch is edit-touch-xt

\ diagnostic styles: error = white on red, warning = bright yellow underline, info = blue underline, hint = dim gray
variable diag-err-attr  <A Red >BG White >FG Bold A> diag-err-attr !
variable diag-warn-attr <A Yellow >FG Bold Underline A> diag-warn-attr !
variable diag-info-attr <A Blue >FG Underline A> diag-info-attr !
variable diag-hint-attr Dim diag-hint-attr !

\ merge syntax segments + diagnostic segments: return (hlend attr)
variable dm-a  0 dm-a !
variable dm-e  0 dm-e !
variable dm-at 0 dm-at !
variable dm-b0 0 dm-b0 !
: diag-merge ( a e at -- e' at' )
  dm-at !  dm-e !  dm-a !
  \ is a inside a diagnostic?
  ndiags @ 0 ?do
    dm-a @ diag-b0 i cells + @ >=  dm-a @ diag-b1 i cells + @ <  and if
      dm-e @ diag-b1 i cells + @ min dm-e !
      diag-sev i cells + @ 1 = if diag-err-attr @
      else diag-sev i cells + @ 2 = if diag-warn-attr @
      else diag-sev i cells + @ 3 = if diag-info-attr @
      else diag-hint-attr @ then then then
      dm-at @ or  dm-e @ swap
      unloop exit
    then
  loop
  \ does a diagnostic start inside [a, e)?
  ndiags @ 0 ?do
    diag-b0 i cells + @ dm-b0 !
    dm-b0 @ dm-a @ >  dm-b0 @ dm-e @ <  and if
      dm-b0 @ dm-at @  unloop exit
    then
  loop
  dm-e @ dm-at @ ;

: hl-attr { a -- hlend attr }
  win-render-active @ if
    visual @ 0<> if
      a vis-lo >= a vis-hi < and if  vis-hi S-SELECT exit  then
    then
  then
  a search-hl  dup 0<> if  exit  then  2drop   \ search-hl returns 2 values; drop 2 on a miss (otherwise it leaks 1 cell each time → stack overflow)
  ensure-match
  a syn-attr { e at }
  ndiags @ 0> if  a e at diag-merge to at to e  then
  win-render-active @ if
    visual @ 0<> if                                \ do not cross the selection boundary
      a vis-lo <  vis-lo e <  and if  vis-lo to e  then
      a vis-hi <  vis-hi e <  and if  vis-hi to e  then
    then
  then
  patlen @ 0> if  a e next-match to e  then      \ do not cross the next search match
  win-render-active @ if
    match-pos @ 0>= if                             \ brackets: only highlight the paired brackets (magenta + dim)
      a match-pos @ = if  a 1+ bracket-attr @ exit  then
      a match-pos @ <  match-pos @ e <  and if  match-pos @ to e  then
    then
  then
  e at ;

\ jump to the paired bracket (%): when the cursor is on a bracket, jump to its pair; pressing again jumps back and forth
: cmd-jump-match
  ensure-match
  match-pos @ 0>= if  match-pos @ cursor !  then ;

\ ===== keyboard macros (Q record/stop, @ replay)=====
256 constant /macro
create macro-buf  /macro allot
variable macro-len   0 macro-len !
variable recording   0 recording !
variable replaying   0 replaying !
variable replay-i    0 replay-i !
variable mk          0 mk !

\ recording hook (installed into key-pre-xt): while recording, store keys into the macro buffer; Q (the stop key) is not recorded
: macro-record-key { c -- }
  recording @ 0= if exit then
  c 81 = if exit then
  macro-len @ /macro < if
    c macro-buf macro-len @ + c!  1 macro-len +!
  then ;

\ Q: start/stop recording
: cmd-macro-toggle
  recording @ if
    0 recording !
    s" macro recorded" msg!
  else
    0 macro-len !
    1 recording !
    s" recording" msg!
  then ;

\ @: replay (synchronous loop; ignored while recording/replaying, to prevent recursion)
: cmd-macro-play
  recording @ replaying @ or if exit then
  macro-len @ 0= if s" no macro" msg! exit then
  1 replaying !
  0 replay-i !
  begin
    replay-i @ macro-len @ <
  while
    macro-buf replay-i @ + c@ mk !
    1 replay-i +!
    mk @ dispatch-key
  repeat
  0 replaying ! ;

\ ===== word completion (extensible to LSP completion)=====
\ architecture: the completion source = complete-next-xt ( prefix plen -- cand clen | 0 0 ), returning the next candidate.
\   the current source = word-complete-next (scans buffer words); to add LSP later, just replace this hook (LSP fetches the list asynchronously and picks by index).
64 constant /cand
64 constant max-cands
create cands max-cands /cand * allot        \ insertion text (LSP insertText / scanned word)
create clens max-cands cells allot          \ insertion-text length
create ckinds max-cands cells allot         \ LSP CompletionItemKind (integer; 0 for non-LSP sources)
create cdetails max-cands /cand * allot     \ detail (signature/type)
create cdlens max-cands cells allot         \ detail length
create cbase 64 allot
variable ncands     0 ncands !
variable cindex    -1 cindex !
variable cbase-len  0 cbase-len !
variable cins-len   0 cins-len !
variable cp-cur     -1 cp-cur !

\ word-character test (stack version, no locals — locals inside a loop corrupt the return stack)
: word-char? ( c -- f )
  dup 48 58 within if drop true exit then
  dup 65 91 within if drop true exit then
  dup 97 123 within if drop true exit then
  dup 95 = if drop true exit then
  0x80 >= ;

\ ===== completion implementation (all globals, no locals — locals in a deep chain corrupt the return stack)=====
create wpfx 64 allot
variable wpfx-len 0 wpfx-len !
variable wp-cur 0 wp-cur !
variable wp-s 0 wp-s !
variable wp-i 0 wp-i !
: word-prefix ( -- addr len )
  cursor @ wp-cur !
  wp-cur @ wp-s !
  begin
    wp-s @ 0>  if  wp-s @ 1- g-char-at word-char?  else  false  then
  while
    -1 wp-s +!
  repeat
  wp-cur @ wp-s @ - 64 min wpfx-len !
  0 wp-i !
  begin
    wp-i @ wpfx-len @ <
  while
    wp-s @ wp-i @ + g-char-at  wpfx wp-i @ + c!  1 wp-i +!
  repeat
  wpfx wpfx-len @ ;

\ compare the contiguous string ca against cu bytes at buffer offset bo (the gap buffer is non-contiguous, so compare cannot be used directly)
variable bs-ca 0 bs-ca !
variable bs-bo 0 bs-bo !
variable bs-cu 0 bs-cu !
variable bs-i 0 bs-i !
: buf-str= ( ca bo cu -- f )
  bs-cu !  bs-bo !  bs-ca !
  0 bs-i !
  begin
    bs-i @ bs-cu @ <
  while
    bs-bo @ bs-i @ + g-char-at  bs-ca @ bs-i @ + c@  <> if false exit then
    1 bs-i +!
  repeat
  true ;

\ whether the candidate already exists (a = buffer offset)
variable ce-a 0 ce-a !
variable ce-u 0 ce-u !
variable ce-i 0 ce-i !
: cand-exists? ( a u -- f )
  ce-u !  ce-a !
  0 ce-i !
  begin
    ce-i @ ncands @ <
  while
    clens ce-i @ cells + @ ce-u @ = if
      cands ce-i @ /cand * +  ce-a @  ce-u @  buf-str= if true exit then
    then
    1 ce-i +!
  repeat
  false ;

\ collect words starting with prefix and longer (deduplicated)
variable cc-ca 0 cc-ca !
variable cc-cu 0 cc-cu !
variable cc-ci 0 cc-ci !
variable cc-cws 0 cc-cws !
variable cc-cwl 0 cc-cwl !
variable cc-gs 0 cc-gs !
variable cc-k 0 cc-k !
: collect-candidates ( ca cu -- )
  cc-cu !  cc-ca !
  0 ncands !
  g-size cc-gs !
  0 cc-ci !
  begin
    cc-ci @ cc-gs @ <
  while
    cc-ci @ g-char-at word-char? if
      cc-ci @ cc-cws !
      begin
        cc-cws @ cc-gs @ <  if  cc-cws @ g-char-at word-char?  else  false  then
      while
        1 cc-cws +!
      repeat
      cc-cws @ cc-ci @ - cc-cwl !
      cc-cwl @ cc-cu @ > if
        cc-ca @ cc-ci @ cc-cu @ buf-str= if
          cc-ci @ cc-cwl @ cand-exists? 0= if
            ncands @ max-cands < if
              cc-cwl @ clens ncands @ cells + !
              0 cc-k !
              begin
                cc-k @ cc-cwl @ <
              while
                cc-ci @ cc-k @ + g-char-at  cands ncands @ /cand * + cc-k @ + c!  1 cc-k +!
              repeat
              1 ncands +!
            then
          then
        then
      then
      cc-cws @ cc-ci !
    else
      1 cc-ci +!
    then
  repeat ;

\ completion source: return the next candidate (full word); 0 0 if none
variable wn-a 0 wn-a !
variable wn-u 0 wn-u !
: word-complete-next ( a u -- cand clen | 0 0 )
  wn-u !  wn-a !
  cursor @ cp-cur @ =  cindex @ 0>=  and if
    1 cindex +!
  else
    wn-a @ cbase wn-u @ move  wn-u @ cbase-len !
    0 cins-len !
    wn-a @ wn-u @ collect-candidates
    0 cindex !
  then
  ncands @ 0= if 0 0 exit then
  cindex @ ncands @ mod cindex !
  cands cindex @ /cand * +  clens cindex @ cells + @ ;

variable complete-next-xt  ' word-complete-next complete-next-xt !

\ ===== context stack (jump history: pushed before gd/gr, popped by gb; the data is defined before "close a buffer")=====
: ctx-push ( -- )
  nctx @ max-ctx < if
    gctx @  ctx-buf nctx @ cells + !
    cursor @  ctx-off nctx @ cells + !
    1 nctx +!
  then ;

\ whether b is still in the buffers-first list (a closed/freed buffer returns false)
: buffer-alive? { b -- f }
  buffers-first @ { p }  false { f }
  begin p  f 0=  and while
    p b = if true to f then
    p B:NEXT + @ to p
  repeat
  f ;

: ctx-pop ( -- )
  nctx @ 0> if
    -1 nctx +!
    ctx-buf nctx @ cells + @ ctx-b !
    ctx-off nctx @ cells + @ ctx-o !
    \ A: switch only to a still-alive buffer (drop the history entry for 0 or dangling pointers)
    ctx-b @ 0<>  ctx-b @ buffer-alive?  and if
      gctx @ ctx-b @ <> if ctx-b @ buffer-switch then
      ctx-o @ g-size min cursor !
      ensure-visible
      0 pnrows !
    then
  then ;

include lsp.fs
' lsp-refresh-diags is file-loaded-xt

\ display name: inside the project root → path relative to the root; otherwise the full path (vim %f / emacs abbreviate-file-name style)
: fname-display-rel ( -- addr len )
  fname @ 0= if s" [no name]" exit then
  fname @ flen @ { a u }
  lsp-root-len @ { rl }
  rl 0>  u rl >  and if
    a u  lsp-root rl  string-prefix? if
      a rl + c@ 47 = if
        a rl + 1+  u rl - 1-  exit
      then
    then
  then
  a u ;
' fname-display-rel is fname-display

\ completion-source dispatch: use LSP when it is on, otherwise scan buffer words
: complete-dispatch ( a u -- cand clen | 0 0 )
  lsp-open? @ if
    lsp-ready? @ if  lsp-complete-next  else  0 0  then
  else
    word-complete-next
  then ;
' complete-dispatch complete-next-xt !

\ Tab: word completion (inserts a tab with no word prefix; repeated Tab cycles candidates)
variable wc-a 0 wc-a !
variable wc-u 0 wc-u !
variable wc-ca 0 wc-ca !
variable wc-cu 0 wc-cu !
variable wc-slen 0 wc-slen !
variable wc-i 0 wc-i !
variable wc-sel 0 wc-sel !

variable complete-after-xt 0 complete-after-xt !   \ ( -- ) called after a completion insert (lsp uses it: send didChange)
variable complete-live? 0 complete-live? !   \ 1 = live preview during display (no didChange)
\ set later (lsp-didchange is defined in lsp.fs)
' lsp-after-complete complete-after-xt !

\ insert candidate i: delete the old suffix + insert the cands[i] suffix + update cp-cur/cins-len
: complete-insert ( i -- )
  wc-sel !
  clens wc-sel @ cells + @ wc-cu !
  cands wc-sel @ /cand * + wc-ca !
  0 wc-i !
  begin
    wc-i @ cins-len @ <
  while
    fe-backspace  1 wc-i +!
  repeat
  wc-cu @ cbase-len @ - wc-slen !
  0 wc-i !
  begin
    wc-i @ wc-slen @ <
  while
    wc-ca @ cbase-len @ + wc-i @ + c@ fe-insert  1 wc-i +!
  repeat
  wc-slen @ cins-len !
  cursor @ cp-cur !
  0 pnrows !
  complete-live? @ 0= if
    complete-after-xt @ ?dup if execute then
  then ;

\ ===== completion display interface (pluggable backends)=====
variable cpres-open-xt   0 cpres-open-xt !   \ ( -- ) open once candidates are ready
variable cpres-key-xt    0 cpres-key-xt !    \ ( c -- f ) display-phase key; true = handled
variable cpres-close-xt  0 cpres-close-xt !  \ ( -- ) close

\ inline backend: immediately insert the current candidate (default, equivalent to the old behavior)
: cpres-inline-open  cindex @ complete-insert ;
' cpres-inline-open cpres-open-xt !

\ ===== overlay-menu backend (candidate overlay)=====
variable cm-i     0 cm-i !
variable cm-idx   0 cm-idx !
variable cm-sel   0 cm-sel !
variable cm-top   0 cm-top !
variable cm-vis   0 cm-vis !
variable cm-vrow  0 cm-vrow !
variable cm-vcol  0 cm-vcol !
variable cm-avail 0 cm-avail !
variable cm-used  0 cm-used !
variable cm-roww  0 cm-roww !
variable cm-width 0 cm-width !
variable cm-act   0 cm-act !
variable cm-cl    0 cm-cl !
variable cm-csub  0 cm-csub !
variable cm-seg0  0 cm-seg0 !    \ start column of the cursor's segment (line-relative)
variable cm-crow  0 cm-crow !

variable cw-a    0 cw-a !
variable cw-u    0 cw-u !
variable cw-max  0 cw-max !
variable cw-i    0 cw-i !
variable cw-w    0 cw-w !
variable cw-cn   0 cw-cn !
variable cw-cw   0 cw-cw !
variable cw-b0   0 cw-b0 !
variable cw-cp   0 cw-cp !

\ write (a u) by display width (plain memory), at most maxw columns; return the actual width used (the loop uses only globals)
: cm-write ( a u maxw -- usedw )
  cw-max !  cw-u !  cw-a !
  0 cw-i !  0 cw-w !
  begin cw-i @ cw-u @ <  cw-w @ cw-max @ <  and while
    \ decode the code point (plain-memory c@; the gap-buffer u8-wl/g-char-at cannot be used)
    cw-a @ cw-i @ + c@ cw-b0 !
    cw-b0 @ 0x80 < if
      cw-b0 @ cw-cp !  1 cw-cn !
    else cw-b0 @ 0xC0 < if
      cw-b0 @ cw-cp !  1 cw-cn !
    else cw-b0 @ 0xE0 < if
      cw-b0 @ 0x1F and 6 lshift
      cw-a @ cw-i @ + 1+ c@ 0x3F and or  cw-cp !  2 cw-cn !
    else cw-b0 @ 0xF0 < if
      cw-b0 @ 0x0F and 12 lshift
      cw-a @ cw-i @ + 1+ c@ 0x3F and 6 lshift or
      cw-a @ cw-i @ + 2 + c@ 0x3F and or  cw-cp !  3 cw-cn !
    else
      cw-b0 @ 0x07 and 18 lshift
      cw-a @ cw-i @ + 1+ c@ 0x3F and 12 lshift or
      cw-a @ cw-i @ + 2 + c@ 0x3F and 6 lshift or
      cw-a @ cw-i @ + 3 + c@ 0x3F and or  cw-cp !  4 cw-cn !
    then then then then
    \ width (control characters / negative widths count as 0)
    cw-cp @ xc-width cw-cw !
    cw-cw @ 0< if 0 cw-cw ! then
    cw-w @ cw-cw @ +  cw-max @ > if exit then
    cw-a @ cw-i @ +  cw-cn @  fb-str
    cw-cn @ cw-i +!
    cw-cw @ cw-w +!
  repeat
  cw-w @ ;

variable mw-a 0 mw-a !
variable mw-u 0 mw-u !
variable mw-i 0 mw-i !
variable mw-w 0 mw-w !
\ display width of a plain-memory string (reuses the cw-* decode globals; the loop uses globals)
: mb-width ( a u -- w )
  mw-u !  mw-a !
  0 mw-i !  0 mw-w !
  begin mw-i @ mw-u @ < while
    mw-a @ mw-i @ + c@ cw-b0 !
    cw-b0 @ 0x80 < if
      cw-b0 @ cw-cp !  1 cw-cn !
    else cw-b0 @ 0xC0 < if
      cw-b0 @ cw-cp !  1 cw-cn !
    else cw-b0 @ 0xE0 < if
      cw-b0 @ 0x1F and 6 lshift
      mw-a @ mw-i @ + 1+ c@ 0x3F and or  cw-cp !  2 cw-cn !
    else cw-b0 @ 0xF0 < if
      cw-b0 @ 0x0F and 12 lshift
      mw-a @ mw-i @ + 1+ c@ 0x3F and 6 lshift or
      mw-a @ mw-i @ + 2 + c@ 0x3F and or  cw-cp !  3 cw-cn !
    else
      cw-b0 @ 0x07 and 18 lshift
      mw-a @ mw-i @ + 1+ c@ 0x3F and 12 lshift or
      mw-a @ mw-i @ + 2 + c@ 0x3F and 6 lshift or
      mw-a @ mw-i @ + 3 + c@ 0x3F and or  cw-cp !  4 cw-cn !
    then then then then
    cw-cp @ xc-width cw-cw !
    cw-cw @ 0< if 0 cw-cw ! then
    cw-cw @ mw-w +!
    cw-cn @ mw-i +!
  repeat
  mw-w @ ;

\ kind -> single-character icon
: cm-kind-ic ( kind -- c )
  dup 3 = if drop [char] f exit then    \ Function
  dup 6 = if drop [char] m exit then    \ Method
  dup 4 = if drop [char] v exit then    \ Variable
  dup 5 = if drop [char] f exit then    \ Field
  dup 9 = if drop [char] p exit then    \ Module
  dup 21 = if drop [char] c exit then   \ Constant
  dup 22 = if drop [char] k exit then   \ Keyword
  dup 12 = if drop [char] v exit then   \ Value
  drop 32 ;

\ draw overlay row i (the overlay-xt callback; float=overlay / push=insert)
: cm-draw ( i -- )
  cm-i !
  cm-i @ cm-top @ + cm-idx !
  cm-idx @ ncands @ >= if
    overlay-float? @ 0= if gutter-w @ 0 ?do 32 fb-c loop then
    exit
  then
  overlay-float? @ if
    \ float: position to the menu column (1-indexed): ESC[row;colH (relative to the active window origin)
    27 fb-c 91 fb-c
    win-y @ overlay-vrow @ + cm-i @ + 1+ fb-ud.
    59 fb-c
    win-x @ gutter-w @ + cm-vcol @ + 1+ fb-ud.
    72 fb-c
  else
    \ push: space-padding alignment
    gutter-w @ cm-vcol @ + 0 ?do 32 fb-c loop
  then
  cm-idx @ cm-sel @ = if S-SELECT style>fb then
  win-cols @ gutter-w @ - cm-vcol @ - cm-avail !
  \ icon + space
  ckinds cm-idx @ cells + @ cm-kind-ic fb-c
  32 fb-c
  2 cm-roww !
  \ label
  cands cm-idx @ /cand * +  clens cm-idx @ cells + @  cm-avail @ cm-write cm-used !
  cm-used @ cm-roww +!
  \ detail: shown on every row
  cdlens cm-idx @ cells + @ 0> if
    s"  " fb-str
    2 cm-roww +!
    cdetails cm-idx @ /cand * +  cdlens cm-idx @ cells + @
    cm-avail @ cm-roww @ - 0 max  cm-write cm-used !
    cm-used @ cm-roww +!
  then
  \ float: pad spaces to the box width (text right of the box is preserved)
  overlay-float? @ if
    cm-width @ cm-roww @ - 0 max 0 ?do 32 fb-c loop
  then
  cm-idx @ cm-sel @ = if 0 style>fb then ;

\ scroll fix: keep cm-sel inside [cm-top, cm-top+cm-vis)
: cm-fix-scroll ( -- )
  cm-sel @ cm-top @ < if cm-sel @ cm-top ! exit then
  cm-sel @ cm-top @ cm-vis @ + >= if cm-sel @ cm-vis @ - 1+ cm-top ! then ;

\ close the menu
: cm-close ( -- )
  0 cm-act !
  0 overlay-xt !
  0 overlay-rows !
  0 key-inter-xt !
  0 pnrows ! ;

: cm-accept ( -- )  cm-sel @ complete-insert  cm-close ;
: cm-cancel ( -- )  cm-close ;
\ display-phase up/down: the selected candidate is inserted immediately (live preview), no didChange
: cm-insert-sel ( -- )  1 complete-live? !  cm-sel @ complete-insert  0 complete-live? ! ;
: cm-up    cm-sel @ 0> if -1 cm-sel +! cm-fix-scroll cm-insert-sel then ;
: cm-down  cm-sel @ ncands @ 1- < if 1 cm-sel +! cm-fix-scroll cm-insert-sel then ;
: cm-next  cm-sel @ 1+ ncands @ mod cm-sel ! cm-fix-scroll cm-insert-sel ;
: cm-prev  cm-sel @ 0> if -1 cm-sel +! else ncands @ 1- cm-sel ! then cm-fix-scroll cm-insert-sel ;

\ shift mask (ekey: k-shift-mask = $1 28 lshift)
$10000000 constant cm-shift

\ key handling (the key-inter-xt callback): handle menu keys; other keys close the menu and go to the keymap
: cm-key ( c -- f )
  dup 14 = if drop cm-down true exit then    \ Ctrl-n (char n ctrl-code = 14; the key-ctrl-n constant is defined later)
  dup 16 = if drop cm-up true exit then      \ Ctrl-p（char p ctrl-code = 16）
  dup K-UP = if drop cm-up true exit then            \ ↑
  dup K-DOWN = if drop cm-down true exit then        \ ↓
  dup 9 = if drop cm-next true exit then             \ Tab next item (circular)
  dup 9 cm-shift or = if drop cm-prev true exit then \ Shift-Tab previous item (circular)
  dup 13 = if drop cm-accept true exit then          \ Enter accept
  dup 27 = if drop cm-cancel true exit then          \ ESC cancel
  dup 3 = if drop cm-cancel true exit then           \ Ctrl-c cancel
  drop cm-cancel false ;

\ menu box width: icon+space+longest-candidate display width+detail; clamped to the text area, right edge flush (fzf/company style)
: cm-calc-width ( -- )
  0 cm-width !
  ncands @ 0 ?do
    cands i /cand * +  clens i cells + @  mb-width  4 +
    cdlens i cells + @ 0> if
      cdetails i /cand * +  cdlens i cells + @  mb-width  +
    then
    cm-width @ max  cm-width !
  loop
  win-cols @ gutter-w @ -  cm-width @ min  cm-width !      \ clamp to the text-area width
  cm-width @ 2 max cm-width !
  \ shift left: right edge flush with the window right edge (avoids the menu being cut off when the cursor is far right)
  cm-vcol @  win-cols @ gutter-w @ - cm-width @ -  min  cm-vcol !
  cm-vcol @ 0 max cm-vcol ! ;

\ open the menu (the cpres-open-xt callback)
: cm-open ( -- )
  ncands @ 0= if exit then
  cursor @ offset->linecol cm-vcol ! cm-cl !
  cm-cl @ cm-vcol @ dcol->sub-col drop cm-csub !
  cm-cl @ line-start  cm-cl @ line-length  cm-csub @ sub->seg nip  cm-seg0 !
  cm-vcol @ cm-seg0 @ - cm-vcol !            \ convert to a text-area-relative column (subtract the segment start when in the middle of a wrap)
  cm-cl @ cm-csub @ vrow-of cm-crow !
  cm-crow @ 1+ cm-vrow !                              \ below the cursor by default
  ncands @  win-text-rows panel-rows @ -  min  cm-vis !   \ at most the whole text area
  cm-vis @ 0= if exit then
  \ flip above when it does not fit below (the menu bottom touches the row above the cursor, not covering the input line)
  cm-vrow @ cm-vis @ +  win-text-rows panel-rows @ -  > if
    cm-crow @ cm-vis @ min cm-vis !            \ the full candidates do not fit above → show only what fits
    cm-vis @ 0= if exit then
    cm-crow @ cm-vis @ - cm-vrow !             \ bottom = cm-crow - 1
  then
  0 cm-sel !  0 cm-top !
  1 complete-live? !  cm-sel @ complete-insert  0 complete-live? !   \ immediately insert the first candidate on open (live preview)
  cm-calc-width
  cm-vrow @ overlay-vrow !
  cm-vcol @ overlay-vcol !
  cm-width @ overlay-width !
  cm-vis @ overlay-rows !
  ['] cm-draw overlay-xt !
  ['] cm-key key-inter-xt !
  1 cm-act !
  0 pnrows ! ;

\ switch the backend
: cpres-set-overlay  ['] cm-open cpres-open-xt ! ;
: cpres-set-inline   ['] cpres-inline-open cpres-open-xt ! ;

\ overlay mode: float=overlay (default) / push=push-down insertion
: overlay-float-mode  1 overlay-float? ! ;
: overlay-push-mode   0 overlay-float? ! ;

\ Tab: word completion (inserts a tab with no word prefix; the display backend decides insert/popup)
: cmd-word-complete
  bflags @ BF_READONLY and if exit then
  word-prefix wc-u !  wc-a !
  wc-u @ 0= if 9 fe-insert exit then
  wc-a @ wc-u @ complete-next-xt @ execute wc-cu !  wc-ca !
  wc-ca @ 0= if #bell emit exit then
  cpres-open-xt @ ?dup if execute then ;

\ --- commands ---
: cmd-visual                 \ v: enter/exit visual selection
  visual @ if  0 visual !  else  cursor @ vis-anchor !  1 visual !  then ;

: cmd-yank                   \ y: visual = yank the selection; otherwise yank the whole line
  visual @ if
    vis-lo vis-hi yank-range  0 visual !
  else
    cursor @ offset->linecol drop { line }
    line line-start { lo }
    line 1+ 0 linecol->offset { hi }      \ including the line-end \n
    lo hi yank-range
  then ;

: cmd-visual-delete          \ d: visual = delete the selection; otherwise delete the whole line
  visual @ if
    bflags @ BF_READONLY and 0= if
      vis-lo vis-hi yank-range             \ also copy into the register
      vis-lo vis-hi delete-range
    then
    0 visual !
  else
    cursor @ offset->linecol drop { line }
    line line-start { lo }
    line 1+ 0 linecol->offset { hi }
    bflags @ BF_READONLY and 0= if
      lo hi yank-range
      lo hi delete-range
    then
  then ;

: cmd-paste-after            \ p: paste after the cursor
  yank-len @ 0> if
    cursor @ g-size < if fe-right then
    yank-insert
  then ;

: cmd-paste-before           \ P: paste before the cursor
  yank-len @ 0> if yank-insert then ;

: cmd-visual-cancel          \ ESC: exit visual selection
  0 visual ! ;


\ ===== selection modes: MOVE / SELECT(Replace) / EXTEND =====
\ keeps the vim-style "single cursor + v visual"; this only makes Kakoune's Replace/Extend semantics into
\ a reusable layer: any "movement command" can be invoked as MOVE / SELECT / EXTEND.
\   MOVE   : pure movement (anchor = cursor, no selection)
\   SELECT : Replace — start a new selection from "the current cursor", then move (selection = the selection span)
\   EXTEND : keep the existing anchor and extend toward the target
0 constant SM-MOVE
1 constant SM-SELECT
2 constant SM-EXTEND

: sel-do { xt mode -- }
  mode SM-SELECT = if  cursor @ vis-anchor !  1 visual !  then
  mode SM-EXTEND = if  1 visual !  then
  mode SM-MOVE   = if  0 visual !  then
  xt execute ;

\ select/extend variants of word/line movement (bindings in init-keymaps; change them if you like)
: sel-word-fwd   ['] fe-word-fwd  SM-SELECT sel-do ;
: ext-word-fwd   ['] fe-word-fwd  SM-EXTEND sel-do ;
: sel-word-back  ['] fe-word-back SM-SELECT sel-do ;
: ext-word-back  ['] fe-word-back SM-EXTEND sel-do ;
: sel-word-end   ['] fe-word-end  SM-SELECT sel-do ;
: ext-word-end   ['] fe-word-end  SM-EXTEND sel-do ;

\ ===== prefix-key mode (g)=====
: cmd-gg         fe-file-start  mode-pop ;      \ gg = start of file
: cmd-gd         mode-pop  lsp-goto-def ;            \ gd = go to definition (pushes the stack internally, opens a picker for multiple results)
: cmd-gy         mode-pop  lsp-goto-typedef ;        \ gy = go to type definition
: cmd-gb         ctx-pop  mode-pop ;                      \ gb = jump back to the previous position
: cmd-gr         mode-pop  lsp-references ;         \ gr = find references (leave the prefix mode first, then open the picker)
: cmd-gi         mode-pop  lsp-goto-impl ;           \ gi = jump to the implementation (self-pushes the stack for a picker/single jump)
: cmd-gs         lsp-signature-help  mode-pop ;     \ gs = function signature
: cmd-hover      lsp-hover ;                       \ K = hover documentation

\ ===== syntax-highlighting switches (global + per-buffer override)=====
\ after toggling, force ensure-language to re-select + invalidate the cache + redraw
: syntax-refresh
  0 lang-buf !
  hlc-reset  0 lst-max !
  0 pnrows ! ;
\ global switch
: cmd-syntax-toggle
  syntax-on @ 0= syntax-on !
  syntax-refresh
  syntax-on @ if s" syntax on" else s" syntax off" then msg! ;
\ current-buffer override: -1 → invert the global; 0/1 → invert (the result normalizes to 1/0, so -1 is not mistaken for "follow global")
: cmd-syntax-buffer-toggle
  gctx @ B:SYNTAX + @ dup -1 = if
    drop syntax-on @ 0= 1 and
  else
    0= 1 and
  then
  gctx @ B:SYNTAX + !
  syntax-refresh
  gctx @ B:SYNTAX + @ -1 = if s" syntax: follow global"
  else syntax-effective if s" syntax: on (this buffer)" else s" syntax: off (this buffer)" then then
  msg! ;
\ set the current buffer's language (:syntax): auto=automatic (B:LANGOVR=0), name=forced language; turn highlighting off via cmd-syntax-toggle/buffer-toggle
: cmd-syntax-set { a u -- }
  a u s" auto" compare 0= if 0 gctx @ B:LANGOVR + ! syntax-refresh s" syntax auto" msg! exit then
  a u lang-name-of ?dup 0= if drop s" no such language" msg! exit then
  gctx @ B:LANGOVR + !  syntax-refresh  s" syntax set" msg! ;
\ set a buffer's syntax (for REPL use): buffer-name language|auto; no gctx switch, no execute, operates on the field address directly
: cmd-syntax-set-named { bn bl ln ll -- }
  bn bl buffer-find ?dup 0= if drop s" no such buffer" msg! exit then
  { b }
  ln ll s" auto" compare 0= if 0 b B:LANGOVR + ! syntax-refresh s" syntax auto" msg! exit then
  ln ll lang-name-of ?dup 0= if drop s" no such language" msg! exit then
  b B:LANGOVR + !  syntax-refresh  s" syntax set" msg! ;
\ toggle the syntax switch by buffer name (no gctx switch, no execute, usable in `!`/REPL; operates on the buffer field address directly)
: cmd-syntax-buffer-toggle-named { a u -- }
  a u buffer-find ?dup 0= if drop s" no such buffer" msg! exit then
  { b }
  b B:SYNTAX + @ dup -1 = if drop syntax-on @ 0= 1 and else 0= 1 and then
  b B:SYNTAX + !
  syntax-refresh
  b B:SYNTAX + @ -1 = if s" syntax: follow global"
  else b B:SYNTAX + @ if s" syntax: on" else s" syntax: off" then then
  msg! ;

\ ===== locate/view native jumps (replacing Gforth's fancy-after-l terminal pager)=====
\ only reuse Gforth's position resolution (view' / name>view / view>filename# / included-filename[] / view>line),
\ display goes through fe's buffer-open + line jump + center (same as LSP gd), naturally correct under splits.
\ Note: do not use view>filename (it goes through loadfilename#>str→filename>display and may return a $tmp dangling string),
\ use view>filename# + included-filename[] instead (the original filename, stable).

\ view → file:line (0-based); returns false when there is no source file
: fe-view-loc ( view -- file-addr len line true | false )
  dup view>filename# dup 0< if 2drop drop false exit then   \ no source file (interactive/terminal definition)
  included-filename[]                \ ( view addr u ) original filename
  rot view>line 1- true ;           \ Gforth line numbers are 1-based, fe uses 0-based

: fe-locate-resolve ( -- file-addr len line true | false )
  view' >name name>view              \ ( view ) — view' throws "Undefined word" when not found
  dup 0= if drop false exit then
  fe-view-loc ;

create fe-filebuf 1024 allot
variable fe-filelen 0 fe-filelen !

\ resolve the source filename: use it as-is when it opens relative to CWD; otherwise expand via Gforth fpath (expand-file).
\ e.g. where dup → "kernel/basics.fs" (a Gforth-relative path) → expanded to the full path.
: fe-resolve-file ( addr len -- addr' len' )
  { a u }
  a u r/o open-file if              \ cannot open relative to CWD → expand via fpath
    a u expand-file                 \ ( fulladdr fulllen ) $tmp (will be overwritten later; copy first)
    fe-filelen !
    fe-filebuf  fe-filelen @  move
    fe-filebuf fe-filelen @
  else
    close-file drop
    a u
  then ;

: fe-locate-jump ( file-addr len line -- )
  { line }
  fe-resolve-file                   \ ( addr' len' ) resolved path (fpath)
  ctx-push                          \ record the return point (gb jumps back to the REPL / original position)
  buffer-open                       \ open/switch to the source file
  line line-start cursor !          \ cursor to the line start
  center-cursor                     \ center (same as zz)
  0 pnrows ! ;

: (fe-locate-parse) ( -- )          \ runs in execute-parsing's input context
  parse-name 2drop                  \ skip the command word locate/view
  ['] fe-locate-resolve catch ?dup if
    drop s" locate: not found" msg! exit
  then
  if fe-locate-jump else s" locate: not found" msg! then ;

' (fe-locate-parse) is repl-locate-cmd

\ ===== where/browse multi-result native picker (capture Gforth where/browse output → parse file:line → picker)=====
\ run Gforth's where/browse directly (output captured to /tmp; Gforth decodes file:line:col correctly),
\ parse the first two segments "file:line" of each line into the list-panel; on selection → buffer-open + line jump + center.

variable fw-is-browse 0 fw-is-browse !
variable fw-oa 0 fw-oa !      \ output address
variable fw-ou 0 fw-ou !      \ output length
variable fw-li 0 fw-li !      \ current-line start
variable fw-p  0 fw-p !       \ scan cursor
variable fw-c1 0 fw-c1 !      \ the first ':'
variable fw-c2 0 fw-c2 !      \ the second ':'
create fw-entry 512 allot
variable fw-elen 0 fw-elen !

\ find the next ':' from fw-p (within the current line, up to \n); return its position, or -1
: fw-colon ( -- pos|-1 )
  begin
    fw-p @ fw-ou @ <
    fw-oa @ fw-p @ + c@ 10 <>  and
  while
    fw-oa @ fw-p @ + c@ 58 = if  fw-p @ exit  then
    1 fw-p +!
  repeat
  -1 ;

\ parse the current line (from fw-li) "file:line:..." → build "file:line" into fw-entry; true on success
: fw-parse-line ( -- f )
  fw-li @ fw-p !
  fw-colon dup 0< if drop false exit then  fw-c1 !
  1 fw-p +!
  fw-colon dup 0< if drop false exit then  fw-c2 !
  \ file = [fw-li, fw-c1)，line = [fw-c1+1, fw-c2)
  fw-oa @ fw-li @ +  fw-entry  fw-c1 @ fw-li @ -  move
  fw-c1 @ fw-li @ - fw-elen !
  58 fw-entry fw-elen @ + c!  1 fw-elen +!
  fw-oa @ fw-c1 @ + 1+  fw-entry fw-elen @ +  fw-c2 @ fw-c1 @ - 1-  move
  fw-c2 @ fw-c1 @ - 1- fw-elen +!
  true ;

\ parse the output (fw-oa/fw-ou) into flist
: fe-where-panel-collect ( -- )
  0 flist-len !
  0 fw-li !
  begin fw-li @ fw-ou @ < while
    fw-parse-line if  fw-entry fw-elen @ flist-add  then
    \ skip to the next line (past the \n)
    fw-li @ fw-p !
    begin
      fw-p @ fw-ou @ <
      fw-oa @ fw-p @ + c@ 10 <>  and
    while 1 fw-p +! repeat
    fw-p @ 1+ fw-li !
  repeat ;

\ picker selection: entry = "file:line" → open the file, jump to the line, center
: fe-where-accept ( a u -- )
  { a u }
  a u 58 scan  { laddr llen }           \ ':' and everything after it (including the ':')
  llen 1 < if s" where: bad line" msg! exit then
  laddr 1+ llen 1- s>number? 0= if 2drop s" where: bad line" msg! exit then
  drop 1- { line }                     \ discard the high cell of d, 0-based
  u llen - { flen }                     \ file-segment length (excluding the ':')
  ctx-push
  a flen fe-resolve-file buffer-open
  line line-start cursor !
  center-cursor
  mode-command prev-mode !       \ the picker mode-pops back to NORMAL (not REPL)
  0 pnrows ! ;

\ where/browse command entry: ( addr len -- ) = the full line "where foo" / "browse sub"
: (fe-where-parse) ( addr len -- )
  2dup s" browse " string-prefix? fw-is-browse !
  repl-capture  fw-ou !  fw-oa !        \ capture Gforth where/browse output
  fw-is-browse @ if s" BROWSE" else s" WHERE" then lp-set-title
  ['] fe-where-panel-collect  ['] fe-where-accept  0  listpanel-open
  fw-oa @ free throw ;

' (fe-where-parse) is repl-where-cmd

include grep.fs

\ ===== operator-pending mode (d / y + motion)=====
variable op-kind   0 op-kind !
variable op-start  0 op-start !
\ operator type (op-kind): named constants
1 constant OP-DELETE
2 constant OP-YANK
3 constant OP-CHANGE
: op-cancel   mode-pop ;                        \ unmatched key -> cancel
\ d/c (delete/change) in a read-only buffer: no action (no register copy, no delete), only leave the pending mode
: op-readonly-cancel? ( -- f )  op-kind @ OP-DELETE =  op-kind @ OP-CHANGE =  or  bflags @ BF_READONLY and  and ;
: op-apply { lo hi -- }                         \ act on [lo,hi) then exit
  op-readonly-cancel? if mode-pop exit then
  lo hi yank-range
  op-kind @ OP-DELETE =  op-kind @ OP-CHANGE =  or if
    lo hi delete-range
    op-kind @ OP-CHANGE = if  lo cursor !  then    \ change: the insert point = the deletion start (delete-range clamps the cursor to the last in-line character)
  else  op-start @ cursor !  then
  op-kind @ OP-CHANGE = if  insert-mode-xt @ execute mode-set  else  mode-pop  then ;   \ c enters insert, d/y return to command
: op-begin { kind -- }  kind op-kind !  cursor @ op-start !  mode-op mode-push ;
  : op-motion { xt -- }   \ exclusive motion (w/b/0/h/l): [min,max)
   xt execute  op-start @ cursor @ min  op-start @ cursor @ max  op-apply ;
  : op-motion-inc { xt -- }  \ inclusive motion (e/$): [min, max+1) (vim inclusive)
   xt execute  op-start @ cursor @ min  op-start @ cursor @ max 1+  g-size min  op-apply ;
\ ===== linewise（dd/yy/dj/dk/dG）=====
: last-line      g-size 0> if  g-size 1- offset-line  else 0 then ;
: linewise-done { lo -- }        \ cursor to the deletion-start line head; the last-line head when out of range
  lo g-size min cursor !
  cursor @ g-size >= if  fe-file-end  then ;
: op-lines { l0 l1 -- }          \ whole lines l0..l1 (inclusive)
  op-readonly-cancel? if mode-pop exit then
  l0 line-start  l1 1+ line-start  { lo hi }
  lo hi yank-range
  op-kind @ OP-DELETE =  op-kind @ OP-CHANGE =  or if
    lo hi delete-range  lo linewise-done
    op-kind @ OP-CHANGE = if  insert-mode-xt @ execute mode-set  else  mode-pop  then
  else
    mode-pop
  then ;
: op-line   op-start @ offset-line dup op-lines ;   \ dd / yy: the current line (linewise)

\ ===== unified op-motion table (any registered motion may follow d/y)=====
\ type: 1=exclusive 2=inclusive 3=linewise 4=self-handled (the motion calls op-apply itself)
create op-motion-xts /keymap cells allot
create op-motion-tps /keymap cells allot

: op-motion-register { key type xt -- }
  type key key-idx cells op-motion-tps + !
  xt   key key-idx cells op-motion-xts + ! ;

\ unmatched keys in mode-op: look up the op-motion table uniformly
: op-default { c -- }
  c key-idx cells op-motion-xts + @ { mx }
  mx 0= if  op-cancel  exit  then
  c key-idx cells op-motion-tps + @ { mt }
  mt 1 = if  mx op-motion  exit  then
  mt 2 = if  mx op-motion-inc  exit  then
  mt 3 = if
    mx execute
    op-start @ offset-line { l0 }
    cursor @ offset-line { l1 }
    l0 l1 min  l0 l1 max  op-lines  exit
  then
  mt 4 = if  mx execute  exit  then ;
\ the motions in operator mode
\ w as an operator target does not cross lines (vim): the target clamps to the current line end (before the newline; the last line = the buffer end)
: cur-line-end  op-start @ offset-line dup line-start swap line-length + ;
: op-w-motion
  cur-line-end
  fe-word-fwd  cursor @ over > if  dup cursor !  then drop ;
\ b backward exclusive: if the motion crosses lines, back off to the target line end per vim rules (the newline is not deleted)
: op-b-motion
  fe-word-back
  cursor @ op-start @  { lo hi }
  lo offset-line  hi offset-line  <> if
    lo offset-line dup line-start swap line-length + { le }
    hi le > if  le to hi  then
  then
  lo hi op-apply ;
\ d$ / y$: from op-start to the end of the line content (excluding the newline); an empty line = an empty range (the newline is not deleted)
: op-dollar
  op-start @ { lo }
  op-start @ offset->linecol drop { line }
  line line-start { ls }
  line line-length { ll }
  ls ll + { le }
  lo le min  lo le max  op-apply ;
\ d / y entry: act directly on the selection in visual mode, otherwise enter operator-pending
\ d in the *buffers* list = close the current-line buffer; otherwise = the original delete (motion)
: cmd-d
  gctx @ buffers-buf @ = if buffer-list-close else
    visual @ if
      bflags @ BF_READONLY and 0= if  vis-lo vis-hi yank-range vis-lo vis-hi delete-range  then
      0 visual !
    else OP-DELETE op-begin then
  then ;
\ c (change): visual = delete selection + insert; otherwise c+motion
: cmd-c
  visual @ if
    bflags @ BF_READONLY and 0= if  vis-lo vis-hi yank-range vis-lo vis-hi delete-range  then
    0 visual !  insert-mode-xt @ execute mode-set
  else OP-CHANGE op-begin then ;
\ C: change to end of line (same as c$) — note that Gforth word names are case-insensitive, so it cannot be called cmd-C (it would collide with cmd-c)
: cmd-change-eol  OP-CHANGE op-begin  op-dollar ;
\ D: delete to end of line (same as d$) — likewise it cannot be called cmd-D
: cmd-delete-eol  OP-DELETE op-begin  op-dollar ;
\ x in the *buffers* list = close the current-line buffer; otherwise = delete a character
: cmd-x  gctx @ buffers-buf @ = if buffer-list-close else fe-delete then ;
: cmd-y  visual @ if  vis-lo vis-hi yank-range 0 visual !  else OP-YANK op-begin then ;

\ control key codes: Ctrl-<letter> = letter - 96 (Ctrl-A=1 … Ctrl-Z=26)
: ctrl-code ( ch -- n ) 96 - ;
char s ctrl-code constant key-ctrl-s
char l ctrl-code constant key-ctrl-l
char r ctrl-code constant key-ctrl-r
char a ctrl-code constant key-ctrl-a
char d ctrl-code constant key-ctrl-d
char u ctrl-code constant key-ctrl-u
char y ctrl-code constant key-ctrl-y
char x ctrl-code constant key-ctrl-x
char c ctrl-code constant key-ctrl-c
char o ctrl-code constant key-ctrl-o
char n ctrl-code constant key-ctrl-n
char e ctrl-code constant key-ctrl-e
char f ctrl-code constant key-ctrl-f
char b ctrl-code constant key-ctrl-b
char j ctrl-code constant key-ctrl-j
char p ctrl-code constant key-ctrl-p
char k ctrl-code constant key-ctrl-k
char g ctrl-code constant key-ctrl-g
char t ctrl-code constant key-ctrl-t
char w ctrl-code constant key-ctrl-w

\ emergency cancel (Ctrl-C): clear all "input state"; always a way out
: cmd-panic
  mode-command mode-set
  0 confirm-quit !  0 searching !  0 prompting !  msg-clear
  s" cancelled" msg-say ;

\ default action for unbound keys in insert mode: insert printable characters (whole code points), otherwise ring the bell
: ins-default { c -- }
  c 32 >= c 0x80000000 < and if c fe-insert-cp else #bell emit then ;

\ search/prompt-mode enter/leave: set the searching/prompting flags (used by fb-status / cursor-place)
: search-enter  1 searching ! ;
: search-leave  0 searching ! ;
: prompt-enter  1 prompting ! ;
: prompt-leave  0 prompting ! ;

\ ===== line-number gutter (installed into core's gutter-xt / gutter-w-xt hooks)=====
\ mode: 0=off 1=absolute 2=relative (the cursor row shows 0, the rest |Δ|) 3=hybrid (cursor row absolute, the rest |Δ|)
\ colors: ordinary line numbers dim, the cursor line bright; soft-wrap continuations blank; the width reuses core's gutter-w
\ line-number mode: scope decides per-window or per-buffer (-1=uninitialized) < the filetype registry < the global default
variable linenr-mode     1 linenr-mode !      \ currently effective (at render time = the current scope's mode)
variable linenr-default  1 linenr-default !   \ global default (used when the filetype is not registered)
variable linenr-scope    0 linenr-scope !     \ 0=per-window (default) 1=per-buffer
variable ln-cur  0 ln-cur !

\ filetype registry: extension -> mode
16 constant max-lnft
create ln-exts   max-lnft 96 * allot
create ln-elens  max-lnft cells allot
create ln-modes  max-lnft cells allot
variable nlnft  0 nlnft !
: register-linenr { a u mode -- }              \ callable from lang-*.fs
  nlnft @ max-lnft >= if 2drop drop exit then
  a  ln-exts nlnft @ 96 * +  u move
  u  ln-elens nlnft @ cells + !
  mode ln-modes nlnft @ cells + !
  1 nlnft +! ;
: linenr-of { a u -- mode|0 }
  nlnft @ 0 ?do
    i cells ln-elens + @ u = if
      a u  ln-exts i 96 * +  u  compare 0= if
        i cells ln-modes + @  unloop exit
      then
    then
  loop -1 ;                                    \ -1 = not registered
\ address of the current scope's linenr field
: linenr-field ( -- a )
  linenr-scope @ 0= if win-linenr else linenr then ;

\ the current buffer's filetype default (0 for *repl*)
: linenr-default-of ( -- n )
  gctx @ repl-buf @ = if 0 exit then
  fname @ flen @ ext-of linenr-of
  dup -1 = if drop linenr-default @ then ;

\ effective mode: when the field = -1 (unset), read the global/filetype default live (no write-back; same as vim/emacs/lem's live global read)
: linenr-effective ( -- n )
  linenr-field { f }
  f @ 0< if  linenr-default-of  else  f @  then ;

: ensure-linenr
  linenr-effective linenr-mode ! ;

: gutter-w-custom ( base -- w )
  ensure-linenr                 \ apply the current buffer's line-number mode first (otherwise the first render uses a stale mode)
  linenr-mode @ 0= if drop 0 else ( base ) then ;

\ whether it ends with \n (the empty line after a trailing newline is not a real line; vim/vis do not show its number)
: trailing-nl? ( -- f )
  g-size 0> if  g-size 1- g-char-at 10 =  else  false  then ;

: gutter-colored { line sub -- }
  ensure-linenr
  gutter-w @ 0= if exit then
  sub 0<> if  gutter-w @ 0 ?do 32 fb-c loop  exit  then
  line nlines @  trailing-nl? 1 and  -  >= if  gutter-w @ 0 ?do 32 fb-c loop  exit  then   \ out-of-range rows / the trailing empty line show no number
  cursor @ offset-line ln-cur !
  line ln-cur @ = if  S-CURLINENR  else  S-LINENR  then  style>fb   \ color
  linenr-mode @ 1 = if                         \ absolute
    line 1+
  else
    line ln-cur @ = if                           \ cursor row: hybrid absolute, relative 0
      linenr-mode @ 3 = if  line 1+  else  0  then
    else                                         \ other rows: |Δ|
      line ln-cur @ - abs
    then
  then
  gutter-w @ 1- fb-num-pad
  0 style>fb  32 fb-c ;

: cmd-toggle-linenr                          \ Ctrl-n: off -> absolute -> relative -> hybrid -> off (starts from the effective value, writes the override)
  linenr-effective 1+ 4 mod { m }
  m linenr-field !
  m linenr-mode ! ;

: cmd-linenr-reset                          \ undo the override: back to "unset" (-1), live-read linenr-default
  -1 linenr-field !
  linenr-effective linenr-mode ! ;

\ ===== themes (8 syntax colors + 4 diagnostic colors + 1 picker background)=====
create theme-dark
  <A Cyan >FG A> , <A Green >FG A> , <A Magenta >FG A> , <A Blue >FG Bold A> ,
  <A Cyan >FG A> , <A Yellow >FG A> , <A Cyan >FG Bold A> , <A Magenta >FG Bold A> ,
  <A Red >BG White >FG Bold A> , <A Yellow >FG Bold Underline A> , <A Blue >FG Underline A> , Dim ,
  <A Blue >BG White >FG Bold A> , <A Green >FG A> , <A Magenta >BG Dim A> ,
  <A Blue >BG Green >FG Bold A> ,
  <A Cyan >FG Underline A> ,
  Invers , Invers Dim or ,

create theme-mono
  <A Magenta >FG A> , <A Yellow >FG A> , <A Magenta >FG Bold A> , <A Magenta >FG Bold A> ,
  <A Green >FG A> , <A Blue >FG A> , <A Cyan >FG Bold A> , <A Red >FG Bold A> ,
  <A Red >BG White >FG Bold A> , <A Yellow >FG Bold Underline A> , <A Blue >FG Underline A> , Dim ,
  Invers , Bold , <A Blue >BG Dim A> ,
  Invers Bold or ,
  <A Cyan >FG Underline A> ,
  Invers , Invers Dim or ,

: theme-apply ( a -- )
  8 0 ?do  dup i cells + @  sidx-attrs i 1+ cells + !  loop
  dup 8 cells + @ diag-err-attr !
  dup 9 cells + @ diag-warn-attr !
  dup 10 cells + @ diag-info-attr !
  dup 11 cells + @ diag-hint-attr !
  dup 12 cells + @ panel-attr !
  dup 13 cells + @ match-attr !
  dup 14 cells + @ bracket-attr !
  dup 15 cells + @ match-sel-attr !
  dup 16 cells + @ sym-attr !
  dup 17 cells + @ status-attr !
  dup 18 cells + @ status-inactive-attr !
  drop ;

variable theme-cur 0 theme-cur !   \ 0=dark 1=mono
: cmd-theme
  theme-cur @ 0= if theme-mono else theme-dark then theme-apply
  theme-cur @ 1 xor theme-cur !
  hlc-reset  0 pnrows !
  theme-cur @ 0= if s" theme: dark" else s" theme: mono" then msg! ;

\ buffer switch: REPL-mode switch + completion-state reset (avoids stale candidates/cursor across buffers) + terminal-title sync
: title-show-fname ( -- )        \ title = "fe - <current buffer name>"
  27 emit 93 emit 48 emit 59 emit  \ ESC ] 0 ;
  s" fe" type
  gctx @ buf-namelen@ 0> if
    32 emit 45 emit 32 emit         \ " - "
    gctx @ buf-name@  gctx @ buf-namelen@  type
  then
  7 emit ;                          \ BEL

: buffer-enter-all
  title-show-fname
  repl-buffer-enter
  cm-act @ if cm-close then
  -1 cp-cur !  -1 cindex !  0 ncands !  -1 lsp-req-cur !
  0 ndiags !
  lsp-refresh-diags ;

\ showcmd: op mode shows d/y; prefixes show g/^W (the right end of the statusline)
create leader-showbuf 4 allot
: showcmd$ ( -- addr len )
  cur-mode @ mode-op = if
    op-kind @ OP-DELETE = if s" d" else  op-kind @ OP-CHANGE = if s" c" else s" y" then  then exit
  then
  \ look up the prefix registry: which named prefix cur-mode is → show its name (no locals: render path)
  0 rp-i !
  begin rp-i @ nprefix @ < while
    cur-mode @  prefix-modes rp-i @ /mode cells * +  = if
      prefix-names rp-i @ 16 * +  prefix-nlens rp-i @ cells + @  exit
    then
    1 rp-i +!
  repeat
  0 0 ;

\ ===== code formatting (external formatter, output to stdout)=====
16 constant max-fmt
create fmt-exts   max-fmt 16 * allot
create fmt-elens  max-fmt cells allot
create fmt-cmds   max-fmt 64 * allot
create fmt-clens  max-fmt cells allot
variable nfmt 0 nfmt !

: fmt-register { ext eu cmd cu -- }
  nfmt @ max-fmt >= if exit then
  ext fmt-exts nfmt @ 16 * + eu move
  eu fmt-elens nfmt @ cells + !
  cmd fmt-cmds nfmt @ 64 * + cu move
  cu fmt-clens nfmt @ cells + !
  1 nfmt +! ;

: fmt-of { a u -- ca cu }
  nfmt @ 0 ?do
    i cells fmt-elens + @ u = if
      a u fmt-exts i 16 * + u compare 0= if
        fmt-cmds i 64 * +  fmt-clens i cells + @  unloop exit
      then
    then
  loop
  0 0 ;

s" go"   s" gofmt"                 fmt-register
s" rs"   s" rustfmt --emit stdout" fmt-register
s" js"   s" prettier"              fmt-register
s" ts"   s" prettier"              fmt-register
s" css"  s" prettier"              fmt-register
s" html" s" prettier"              fmt-register
s" json" s" prettier"              fmt-register
s" c"    s" clang-format"          fmt-register
s" h"    s" clang-format"          fmt-register
s" cpp"  s" clang-format"          fmt-register
s" cc"   s" clang-format"          fmt-register
s" cxx"  s" clang-format"          fmt-register
s" java" s" clang-format"          fmt-register

create fmt-tmp 256 allot
variable fmt-tlen 0 fmt-tlen !
create fmt-cmd 256 allot
variable fmt-clen 0 fmt-clen !

\ format the current buffer (= key)
: cmd-format
  fname @ flen @ ext-of { ea eu }
  ea eu fmt-of { ca cu }
  cu 0= if s" no formatter for this file type" msg! exit then
  cursor @ offset->linecol { old-line old-col }   \ record the line + display column (kept after formatting)
  top-line @ { old-top }                        \ record the viewport top (keep the visible range after formatting)
  \ temp file /tmp/fe-fmt.<ext>
  s" /tmp/fe-fmt" fmt-tmp swap move  11 fmt-tlen !
  eu 0> if
    46 fmt-tmp fmt-tlen @ + c!  1 fmt-tlen +!
    ea fmt-tmp fmt-tlen @ + eu move  eu fmt-tlen +!
  then
  \ save the current buffer to the temp file (with EOL handling)
  fmt-tmp fmt-tlen @ save-file
  \ command: <cmd> <tmp>
  ca fmt-cmd cu move  cu fmt-clen !
  32 fmt-cmd fmt-clen @ + c!  1 fmt-clen +!
  fmt-tmp fmt-cmd fmt-clen @ + fmt-tlen @ move  fmt-tlen @ fmt-clen +!
  \ run + read stdout
  fmt-cmd fmt-clen @ r/o open-pipe throw { fid }
  fid slurp-fid { out olen }
  fid close-pipe throw drop
  olen 0= if  out free throw  s" format failed (empty output)" msg! exit then
  \ replace the buffer
  0 g-front !  0 g-back !  1 lix-dirty !  0 eol-crlf !
  0 lix-epend !  0 lix-ecomplex !
  0 g-move-gap
  out olen g-insert
  1 lix-ecomplex !     \ force a full rebuild of the line index
  out free throw
  \ restore the viewport + cursor (line/column clamped; top-sub reset because the line contents changed; u8-bi auto-clamps an over-long column to the line end)
  old-top 0 max  g-nlines 1- min  top-line !
  0 top-sub !
  old-line 0 max  g-nlines 1- min  old-col  linecol->offset cursor !
  u-reset               \ clear undo (old records are invalid relative to the new content)
  -1 saved-urp !        \ content differs from disk; the next save resets it to urp
  1 modified !
  s" formatted" msg! ;

: init-keymaps
  keymap-base    /keymap cells erase      \ clear (allot is not initialized; reload also resets here)
  keymap-search  /keymap cells erase
  keymap-prompt  /keymap cells erase
  keymap-command /keymap cells erase
  keymap-insert  /keymap cells erase
  prefix-keys max-prefix /keymap cells * erase
  keymap-op       /keymap cells erase
  op-motion-xts   /keymap cells erase
  op-motion-tps   /keymap cells erase
  keymap-listpanel /keymap cells erase
  keymap-qrep     /keymap cells erase
  \ register the named prefixes g/z/w/leader (all via register-prefix; returns the mode)
  0 nprefix !
  s" g" register-prefix pmode-g !
  s" z" register-prefix pmode-z !
  s" w" register-prefix pmode-w !
  s" leader" register-prefix pmode-leader !
  \ --- base mode (the common parent of command/insert; only keys shared by both) ---
  keymap-base base-mode m-keymap !
  0 base-mode m-parent !  0 base-mode m-input !
  0 base-mode m-enter  !  0 base-mode m-leave !
  ['] mst-cmd base-mode m-status !   \ status is only a placeholder (base is not shown alone)
  0 base-mode m-flags !
  K-LEFT   ['] fe-left     bind-base
  K-RIGHT  ['] fe-right    bind-base
  K-UP     ['] fe-up       bind-base
  K-DOWN   ['] fe-down     bind-base
  K-HOME   ['] fe-line-start bind-base
  K-END    ['] fe-line-end   bind-base
  K-PRIOR  ['] fe-page-up    bind-base
  K-NEXT   ['] fe-page-down  bind-base
  key-ctrl-f ['] fe-page-down bind-base   \ C-f page down (vim/vis)
  key-ctrl-b ['] fe-page-up   bind-base   \ C-b page up
  K-DELETE ['] fe-delete     bind-base
  key-ctrl-s ['] save-current bind-base
  key-ctrl-l ['] redraw       bind-base
  key-ctrl-x ['] reload-extension bind-base
  key-ctrl-c ['] cmd-panic        bind-base
  key-ctrl-o ['] buffer-open-name  bind-base   \ Ctrl-o open by name
  key-ctrl-n ['] cmd-toggle-linenr  bind-base   \ Ctrl-n cycle the line-number mode
  key-ctrl-p ['] cmd-linenr-reset   bind-base   \ Ctrl-p undo the override, back to the live linenr-default
  key-ctrl-e ['] cmd-scroll-down    bind-base   \ Ctrl-e scroll the viewport down (vim)
  key-ctrl-y ['] cmd-scroll-up      bind-base   \ Ctrl-y scroll the viewport up (vim)
  \ --- command mode: parent = base mode ---
  keymap-command mode-command m-keymap !
  base-mode mode-command m-parent !
  0 mode-command m-input !  0 mode-command m-enter !  0 mode-command m-leave !
  ['] mst-cmd mode-command m-status !
  0 mode-command m-flags !
  [char] 0 ['] fe-line-start  bind-command      \ 0 line start
  [char] $ ['] fe-line-end    bind-command      \ $ line end
  [char] w ['] fe-word-fwd    bind-command      \ w next word
  [char] b ['] fe-word-back   bind-command      \ b previous word
  [char] e ['] fe-word-end    bind-command      \ e word end
  [char] G ['] fe-file-end    bind-command      \ G end of file
  [char] g pmode-g @ bind-prefix      \ g (prefix key -> mode)
  [char] z pmode-z @ bind-prefix      \ z (prefix key -> scroll mode: zz/zt/zb)
  [char] q ['] cmd-quit       bind-command      \ q quit
  [char] h ['] fe-left        bind-command      \ h left
  [char] l ['] fe-right       bind-command      \ l right
  [char] k ['] fe-up          bind-command      \ k up
  [char] j ['] fe-down        bind-command      \ j down
  [char] x ['] cmd-x          bind-command      \ x delete after the cursor (in the *buffers* list = close the current line)
  [char] v ['] cmd-visual          bind-command  \ v visual selection
  [char] s ['] sel-word-fwd        bind-command  \ s SELECT to the next word start (Replace)
  [char] S ['] ext-word-fwd        bind-command  \ S EXTEND to the next word start
  [char] f ['] sel-word-end        bind-command  \ f SELECT to the word end
  [char] F ['] ext-word-end        bind-command  \ F EXTEND to the word end
  [char] r ['] sel-word-back       bind-command  \ r SELECT to the previous word start
  [char] R ['] ext-word-back       bind-command  \ R EXTEND to the previous word start
  [char] y ['] cmd-y               bind-command  \ y (visual = yank the selection; otherwise y+motion)
  [char] d ['] cmd-d               bind-command  \ d (visual = delete the selection; otherwise d+motion)
  [char] c ['] cmd-c               bind-command  \ c (visual = delete the selection + insert; otherwise c+motion)
  [char] C ['] cmd-change-eol      bind-command  \ C = c$ change to end of line
  [char] D ['] cmd-delete-eol      bind-command  \ D = d$ delete to end of line
  [char] p ['] cmd-paste-after     bind-command  \ p paste after the cursor
  [char] P ['] cmd-paste-before    bind-command  \ P paste before the cursor
  #esc     ['] cmd-visual-cancel   bind-command  \ ESC exit visual selection
  [char] B ['] buffer-next        bind-command  \ B next buffer
  [char] K ['] cmd-hover          bind-command  \ K hover documentation
  [char] W ['] buffer-save-as     bind-command  \ W save as
  [char] L ['] buffind-open       bind-command  \ L buffer filter picker
  [char] u ['] undo            bind-command     \ u undo
  key-ctrl-r ['] redo          bind-command     \ Ctrl-r redo
  [char] / ['] cmd-search-fwd  bind-command     \ / forward search
  [char] ? ['] cmd-search-bwd  bind-command     \ ? backward search
  [char] * ['] cmd-star        bind-command     \ * search the word under the cursor (word boundary, no jump)
  [char] n ['] cmd-search-next bind-command     \ n next
  [char] N ['] cmd-search-prev bind-command     \ N previous
  [char] ~ ['] cmd-toggle-case bind-command     \ ~ toggle search ignore-case
  [char] I ['] cmd-insert-line-start bind-command \ I insert at the line start
  [char] A ['] cmd-append-line-end bind-command   \ A insert at the line end
  [char] i ['] cmd-enter-ins   bind-command     \ i insert before the cursor
  [char] a ['] cmd-append      bind-command     \ a insert after the cursor
  [char] o ['] cmd-open-below  bind-command     \ o open a new line below
  [char] O ['] cmd-open-above  bind-command     \ O open a new line above
  key-ctrl-g ['] repl-open      bind-command     \ Ctrl-g open/switch the REPL (was Ctrl-e, yielded to viewport scrolling)
  key-ctrl-t ['] lsp-open       bind-command     \ Ctrl-t start LSP (gopls)
  key-ctrl-w pmode-w @ bind-prefix     \ Ctrl-w window-command prefix
  K-F8       ['] cmd-theme       bind-command     \ F8 switch the theme
  [char] !   ['] repl-eval      bind-command     \ ! expression evaluation (minibuffer)
  [char] #   ['] goto-line-prompt bind-command     \ # jump to a given line
  key-ctrl-j ['] repl-eval-line bind-command     \ Ctrl-j evaluate the current line
  key-ctrl-k ['] cmd-buffer-close bind-command    \ Ctrl-k close the current buffer
  [char] E   ['] cmd-qrep         bind-command    \ E query-replace
  [char] %   ['] cmd-jump-match    bind-command    \ % jump to the paired bracket
  [char] =   ['] cmd-format        bind-command    \ = format the current buffer
  [char] Q   ['] cmd-macro-toggle  bind-command    \ Q record/stop a macro
  [char] @   ['] cmd-macro-play    bind-command    \ @ replay a macro
  92          ['] grep-live         bind-command    \ \ live grep
  #cr         ['] grep-jump-at-cursor bind-command   \ Enter in the *grep* buffer jumps (other buffers ring the bell)
  \ prefix mode g: the second-key table (gg = start of file); unmatched keys cancel
  \ prefix-keymap bindings (the mode field is already initialized by register-prefix)
  [char] g ['] cmd-gg  pmode-g @ bind-mode
  [char] * ['] cmd-gstar pmode-g @ bind-mode
  [char] d ['] cmd-gd  pmode-g @ bind-mode
  [char] y ['] cmd-gy  pmode-g @ bind-mode
  [char] b ['] cmd-gb  pmode-g @ bind-mode
  [char] r ['] cmd-gr  pmode-g @ bind-mode
  [char] i ['] cmd-gi  pmode-g @ bind-mode
  [char] s ['] cmd-gs  pmode-g @ bind-mode
  \ prefix z: scrolling (zz center / zt top / zb bottom)
  [char] z ['] cmd-zz  pmode-z @ bind-mode
  [char] t ['] cmd-zt  pmode-z @ bind-mode
  [char] b ['] cmd-zb  pmode-z @ bind-mode
  \ prefix w: window commands (C-w v split; C-w w / C-w C-w switch focus)
  [char] v ['] cmd-split-v        pmode-w @ bind-mode
  [char] s ['] cmd-split-s        pmode-w @ bind-mode
  [char] w ['] cmd-win-focus-next pmode-w @ bind-mode
  [char] c ['] cmd-win-close      pmode-w @ bind-mode
  [char] h ['] cmd-win-focus-h    pmode-w @ bind-mode
  [char] j ['] cmd-win-focus-j    pmode-w @ bind-mode
  [char] k ['] cmd-win-focus-k    pmode-w @ bind-mode
  [char] l ['] cmd-win-focus-l    pmode-w @ bind-mode
  key-ctrl-w ['] cmd-win-focus-next pmode-w @ bind-mode
  \ leader: the keymap bindings are added by the config layer via bind-leader (empty by default)
  \ operator-pending mode d/y + motion (the unified op-motion table: unmatched keys look it up)
  keymap-op mode-op m-keymap !
  0 mode-op m-parent !  0 mode-op m-enter !  0 mode-op m-leave !
  ['] op-default mode-op m-input !
  ['] mst-op mode-op m-status !
  0 mode-op m-flags !
  [char] d ['] op-line mode-op bind-mode
  [char] y ['] op-line mode-op bind-mode
  [char] c ['] op-line mode-op bind-mode      \ cc = change the whole line (same as dd/yy)
  #esc      ['] op-cancel mode-op bind-mode
  \ register op motions (key type motion-xt; 1=exclusive 2=inclusive 3=linewise 4=self-handled)
  [char] w 1 ['] op-w-motion    op-motion-register
  [char] b 4 ['] op-b-motion    op-motion-register
  [char] e 2 ['] fe-word-end    op-motion-register
  [char] % 2 ['] cmd-jump-match op-motion-register
  [char] h 1 ['] fe-left        op-motion-register
  [char] l 1 ['] fe-right       op-motion-register
  [char] 0 1 ['] fe-line-start  op-motion-register
  [char] $ 4 ['] op-dollar      op-motion-register
  [char] G 3 ['] fe-file-end    op-motion-register
  [char] j 3 ['] fe-down        op-motion-register
  [char] k 3 ['] fe-up          op-motion-register
  \ --- insert mode: parent = base mode; unmatched keys default to ins-default ---
  keymap-insert mode-insert m-keymap !
  base-mode mode-insert m-parent !
  0 mode-insert m-enter !  0 mode-insert m-leave !
  ['] ins-default mode-insert m-input !
  ['] mst-ins mode-insert m-status !
  MF-INSERT mode-insert m-flags !
  #tab     ['] cmd-word-complete bind-insert   \ Tab word completion (inserts a tab with no word prefix)
  #esc     ['] cmd-esc-ins      bind-insert   \ ESC back to command mode
  #bs      ['] fe-backspace     bind-insert   \ Backspace
  #del     ['] fe-backspace     bind-insert   \ DEL
  #cr      ['] fe-insert-newline bind-insert  \ Enter
  
  \ --- search mode (modal, no parent; unmatched keys append to pat) ---
  keymap-search mode-search m-keymap !
  0 mode-search m-parent !
  ['] search-enter mode-search m-enter !
  ['] search-leave mode-search m-leave !
  ['] search-input mode-search m-input !
  ['] mst-search mode-search m-status !
  0 mode-search m-flags !
  #esc       ['] cmd-search-cancel mode-search bind-mode
  key-ctrl-c ['] cmd-search-cancel mode-search bind-mode
  #cr        ['] cmd-search-accept mode-search bind-mode
  #bs        ['] cmd-search-bs     mode-search bind-mode
  #del       ['] cmd-search-bs     mode-search bind-mode
  \ --- prompt mode (modal, no parent; unmatched keys append to pbuf) ---
  keymap-prompt mode-prompt m-keymap !
  0 mode-prompt m-parent !
  ['] prompt-enter mode-prompt m-enter !
  ['] prompt-leave mode-prompt m-leave !
  ['] prompt-input mode-prompt m-input !
  ['] mst-prompt mode-prompt m-status !
  0 mode-prompt m-flags !
  #esc       ['] cmd-prompt-cancel mode-prompt bind-mode
  key-ctrl-c ['] cmd-prompt-cancel mode-prompt bind-mode
  #cr        ['] cmd-prompt-accept mode-prompt bind-mode
  #bs        ['] pb-backspace     mode-prompt bind-mode
  #del       ['] pb-backspace     mode-prompt bind-mode
  #tab       ['] cmd-prompt-tab    mode-prompt bind-mode
  \ readline-style (cursor movement reuses lp-*; editing uses pb-* without lp-refresh)
  key-ctrl-a ['] lp-home       mode-prompt bind-mode
  key-ctrl-e ['] lp-end        mode-prompt bind-mode
  key-ctrl-b ['] lp-left       mode-prompt bind-mode
  key-ctrl-f ['] lp-right      mode-prompt bind-mode
  key-ctrl-d ['] pb-delete     mode-prompt bind-mode
  key-ctrl-k ['] pb-kill-end   mode-prompt bind-mode
  key-ctrl-u ['] pb-kill-start mode-prompt bind-mode
  key-ctrl-w ['] pb-kill-word  mode-prompt bind-mode
  key-ctrl-y ['] pb-yank       mode-prompt bind-mode
  key-ctrl-l ['] pb-clear      mode-prompt bind-mode
  \ --- list-picker mode (generic: file/buffer filtering) ---
  keymap-listpanel mode-listpanel m-keymap !
  0 mode-listpanel m-parent !
  ['] lp-input mode-listpanel m-input !
  0 mode-listpanel m-enter !  0 mode-listpanel m-leave !
  ['] mst-listpanel mode-listpanel m-status !
  0 mode-listpanel m-flags !
  #esc     ['] lp-cancel    mode-listpanel bind-mode
  #cr      ['] lp-accept    mode-listpanel bind-mode
  #bs      ['] lp-backspace mode-listpanel bind-mode
  #del     ['] lp-backspace mode-listpanel bind-mode
  K-UP     ['] lp-up        mode-listpanel bind-mode
  K-DOWN   ['] lp-down      mode-listpanel bind-mode
  key-ctrl-n ['] lp-down    mode-listpanel bind-mode
  key-ctrl-p ['] lp-up      mode-listpanel bind-mode
  key-ctrl-c ['] lp-cancel  mode-listpanel bind-mode
  \ readline-style: cursor movement + deletion + kill/yank
  key-ctrl-a ['] lp-home       mode-listpanel bind-mode
  key-ctrl-e ['] lp-end        mode-listpanel bind-mode
  key-ctrl-b ['] lp-left       mode-listpanel bind-mode
  key-ctrl-f ['] lp-right      mode-listpanel bind-mode
  key-ctrl-d ['] lp-delete     mode-listpanel bind-mode
  key-ctrl-k ['] lp-kill-end   mode-listpanel bind-mode
  key-ctrl-u ['] lp-kill-start mode-listpanel bind-mode
  key-ctrl-w ['] lp-kill-word  mode-listpanel bind-mode
  key-ctrl-y ['] lp-yank       mode-listpanel bind-mode
  key-ctrl-l ['] lp-clear      mode-listpanel bind-mode
  K-DELETE ['] lp-close mode-listpanel bind-mode
  key-ctrl-g ['] lp-dump  mode-listpanel bind-mode   \ live grep: dump to *grep*
  \ --- query-replace mode (qemacs style, single-key confirmation) ---
  keymap-qrep mode-qrep m-keymap !
  0 mode-qrep m-parent !
  0 mode-qrep m-enter !  0 mode-qrep m-leave !
  0 mode-qrep m-input !          \ unmatched keys are ignored
  ['] mst-qrep mode-qrep m-status !
  0 mode-qrep m-flags !
  [char] y ['] qrep-yes    mode-qrep bind-mode
  [char] n ['] qrep-no     mode-qrep bind-mode
  [char] ! ['] qrep-all    mode-qrep bind-mode
  [char] . ['] qrep-dot    mode-qrep bind-mode
  [char] q ['] qrep-quit   mode-qrep bind-mode
  #cr      ['] qrep-quit   mode-qrep bind-mode
  #esc     ['] qrep-quit   mode-qrep bind-mode
  key-ctrl-g ['] qrep-abort mode-qrep bind-mode
  \ --- REPL mode (type Forth directly; Enter evaluates, Backspace deletes, ESC returns to command) ---
  keymap-repl mode-repl m-keymap !
  0 mode-repl m-parent !
  ['] ins-default mode-repl m-input !
  0 mode-repl m-enter !  0 mode-repl m-leave !
  ['] mst-repl mode-repl m-status !
  MF-INSERT mode-repl m-flags !
  #esc ['] repl-to-command mode-repl bind-mode
  #cr  ['] repl-enter-eval mode-repl bind-mode
  #bs  ['] fe-backspace   mode-repl bind-mode
  #del ['] fe-backspace   mode-repl bind-mode
  K-UP   ['] repl-hist-up   mode-repl bind-mode
  K-DOWN ['] repl-hist-down mode-repl bind-mode
  #tab   ['] repl-complete  mode-repl bind-mode
\ install the render hooks (reinstalled on reload)
  ['] fb-status     status-line-xt !
  ['] fb-win-status win-status-xt !
  ['] fb-minibuf    minibuf-xt !
  ['] showcmd$      showcmd-xt !
  ['] gutter-colored gutter-xt !
  ['] gutter-w-custom gutter-w-xt !
  ['] hl-attr       hl-xt !
  ['] cursor-place  cursor-place-xt !
  ['] buffer-enter-all buffer-enter-xt !
  ['] macro-record-key key-pre-xt !        \ keyboard-macro recording hook
  s" *repl*" buffer-find repl-buf !     \ re-associate the *repl* buffer after reload
  mode-command mode-set ;

\ takes effect on load; reload re-includes this file and runs this again to rebuild the keymap
theme-dark theme-apply      \ apply the dark theme at startup (including the picker background)
init-keymaps

\ ===== layered config: layer 0 core / 1 default(custom.fs) / 2 user(~/.fe) / 3 project(./.fe.fs) =====
\ load order = priority (later layers override earlier ones); a single layer's error is catch-isolated and does not break the rest.
\ example (in any layer):  4 tab-width !                    \ tab width 4
\                 0 linenr-default !                 \ turn off line numbers by default
\                 char Z ' cmd-qrep bind-command     \ rebind (the config is interpreted: use char/' instead of [char]/['])
16 constant max-layers
create layer-buf   max-layers 256 * allot
create layer-lens  max-layers cells allot
variable nlayers 0 nlayers !
create cfgpath 256 allot
variable cfgpath-len 0 cfgpath-len !

\ append a layer (addr len; truncated at >255)
: add-layer { addr len -- }
  nlayers @ max-layers >= if exit then
  len 255 min { n }
  addr  layer-buf nlayers @ 256 * +  n move
  n  layer-lens nlayers @ cells + !
  1 nlayers +! ;

\ build $HOME/.fe; cfgpath-len=0 when HOME is unset
: cfg-build
  0 cfgpath-len !
  s" HOME" getenv { a u }
  u 0= if exit then
  a cfgpath u move  u cfgpath-len !
  s" /.fe" { sa sl }
  sa cfgpath cfgpath-len @ + sl move
  sl cfgpath-len +! ;

create cwdbuf 512 allot

\ build the layer list: user (~/.fe) + project ($CWD/.fe.fs, absolute, to avoid a relative include resolving into the fe directory)
: layers-build
  0 nlayers !
  cfg-build
  cfgpath-len @ 0> if  cfgpath cfgpath-len @ add-layer  then
  cwdbuf 511 get-dir { ca clen }
  clen 0> if
    ca cfgpath clen move
    s" /.fe.fs" { sa sl }
    sa cfgpath clen + sl move
    cfgpath clen sl + add-layer
  then ;

\ load a layer: skip if the file does not exist; catch and report errors (without breaking other layers)
: layer-load { addr len -- }
  addr len r/o open-file if drop exit then
  close-file drop
  addr len ['] included catch
  ?dup if  error$ msg!  then ;

: load-layers
  nlayers @ 0 ?do
    layer-buf i 256 * +  layer-lens i cells + @  layer-load
  loop ;

\ Note: layers-build / load-layers are not called at the top level here — they moved to fe.fs's fe entry point (at runtime),
\ so a save-image snapshot does not freeze ~/.fe; config changes take effect on the next startup.
\ wired through the load-layers-xt hook (fe.fs comes before this file, so it cannot be referenced directly).
: load-layers-impl  layers-build  load-layers ;
' load-layers-impl is load-layers-xt
