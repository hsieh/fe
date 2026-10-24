\ ===== repl.fs — embedded Forth REPL (same instance, shared dictionary)=====
\ No subprocess: Forth source is evaluated directly inside the fe process, sharing the editor's dictionary.
\ Output is redirected to a temporary file via outfile-execute, read back, and appended to the *repl* buffer.
\ Depends on: core buffer-append, prompt-open, and the buffer-enter-xt hook.

variable repl-buf  0 repl-buf !
create repl-send-buf 4096 allot
variable repl-fid 0 repl-fid !
variable repl-wfid 0 repl-wfid !             \ warning file id (redefined etc. go through debug-fid so they don't pollute the screen)
variable repl-saved-debug 0 repl-saved-debug !   \ saved debug-fid
variable repl-msg-flag 0 repl-msg-flag !   \ 1 = also msg! the evaluation result (the ! path: no buffer switch but the result is visible)

\ ===== independent data stack (only sp/sp0 are switched, not up/rp/fp/lp; state/dp/dictionary are shared with the editor)=====
\ Switching only the data stack keeps 1 2 persistent; not switching up avoids breaking the per-task sourceview of see/locate/view.
create repl-stack 65536 allot
repl-stack 65536 + constant repl-sp0   \ REPL stack bottom (top of the region, empty stack)
variable repl-sp  repl-sp0 repl-sp !    \ current pointer, initially = empty
variable rt-sv-sp0 0 rt-sv-sp0 !
variable rt-sv-sp 0 rt-sv-sp !
variable rt-ta 0 rt-ta !
variable rt-tu 0 rt-tu !
variable rt-save-sp 0 rt-save-sp !
variable rt-err 0 rt-err !

\ pure evaluation (runs in the task context, reads rt-ta/rt-tu; on an interpretive error the task data stack is rolled back)
: (repl-do-eval) ( -- )
  sp@ rt-save-sp !
  rt-ta @ rt-tu @ ['] evaluate catch ?dup if
    s" ERROR: " type  error$ type
    rt-save-sp @ sp!  0 state !
  else
    s" ok" type
  then cr ;

\ captured inside the task (create-file + outfile-execute; exceptions are handled by an outer catch)
\ Also redirect debug-fid to the warning file: Gforth's redefined warnings go through ?warning→debug-fid (default stderr=terminal),
\ without the redirect they would pollute the TUI directly (same handling as reload-extension).
: (repl-do-capture) ( -- )
  s" forth-out" tmp-file  w/o create-file throw repl-fid !
  s" forth-warn" tmp-file  w/o create-file throw repl-wfid !
  debug-fid repl-saved-debug !
  repl-wfid @ TO debug-fid
  ['] (repl-do-eval) repl-fid @ outfile-execute
  repl-saved-debug @ TO debug-fid
  repl-wfid @ close-file drop
  repl-fid @ close-file drop ;

\ ===== strip ANSI escapes (CSI/OSC/ESC): colored output from locate/see/help does not pollute the buffer =====
\ strip in place (write pointer <= read pointer): returns the original buffer, caller frees; large output (multi-line where) does not overflow.
variable ansi-state 0 ansi-state !
variable ansi-addr 0 ansi-addr !
variable ansi-len 0 ansi-len !
variable ansi-i 0 ansi-i !
variable ansi-c 0 ansi-c !
variable ansi-w 0 ansi-w !
: ansi-emit ( c -- )  ansi-addr @ ansi-w @ + c!  1 ansi-w +! ;
: strip-ansi ( addr len -- addr' len' )
  ansi-len !  ansi-addr !
  0 ansi-state !  0 ansi-w !  0 ansi-i !
  begin
    ansi-i @ ansi-len @ <
  while
    ansi-addr @ ansi-i @ + c@ ansi-c !
    ansi-state @ case
      0 of  ansi-c @ 27 = if 1 ansi-state !  else ansi-c @ ansi-emit then  endof
      1 of  ansi-c @ 91 = if 2 ansi-state !  else ansi-c @ 93 = if 3 ansi-state ! else 0 ansi-state ! then then  endof
      2 of  ansi-c @ 64 >= ansi-c @ 126 <= and if 0 ansi-state ! then  endof
      3 of  ansi-c @ 7 = if 0 ansi-state ! then  endof
    endcase
    1 ansi-i +!
  repeat
  ansi-addr @ ansi-w @ ;

\ check whether the first word of addr len is locate/view (these two would enter the fancy-after-l interactive pager)
: is-locate? ( addr len -- f )
  2dup s" locate " string-prefix?  if 2drop true exit then
  2dup s" view "   string-prefix?  if 2drop true exit then
  2dup s" locate"  compare 0=      if 2drop true exit then
  2dup s" view"    compare 0=      if 2drop true exit then
  2drop false ;

\ locate/view native-jump hook: runs in the ( addr len ) input context; the implementation is bound later in custom.fs
Defer repl-locate-cmd  ' noop is repl-locate-cmd

\ where/browse multi-result picker hook: ( addr len -- ) capture the evaluation + parse the output + open a picker
Defer repl-where-cmd  ' 2drop is repl-where-cmd

\ check whether the first word of addr len is where/browse (multi-result commands)
: is-where? ( addr len -- f )
  2dup s" where "  string-prefix?  if 2drop true exit then
  2dup s" browse " string-prefix? if 2drop true exit then
  2dup s" where"   compare 0=     if 2drop true exit then
  2dup s" browse"  compare 0=     if 2drop true exit then
  2drop false ;

\ capture-evaluate (addr len): switch sp/sp0 → redirect output → read back and strip ANSI → return (a u) (caller frees)
\ No locals (locals + catch + sp! in a deep chain corrupt the return stack → heap corruption); use module variables.
variable rc-a 0 rc-a !
variable rc-u 0 rc-u !
: repl-capture ( addr len -- a u )
  rt-tu ! rt-ta !
  sp0 @ rt-sv-sp0 !  sp@ rt-sv-sp !
  repl-sp0 sp0 !  repl-sp @ sp!
  ['] (repl-do-capture) catch rt-err !
  sp@ repl-sp !
  rt-sv-sp0 @ sp0 !  rt-sv-sp @ sp!
  s" forth-out" tmp-file  r/o open-file throw repl-fid !
  repl-fid @ slurp-fid rc-u ! rc-a !
  repl-fid @ close-file drop
  s" forth-out" tmp-file delete-file drop
  rt-err @ ?dup if  rc-a @ free throw  then
  rc-a @ rc-u @ strip-ansi ;

\ Evaluate addr len:
\   locate/view → native jump (no terminal pager)
\   where/browse → multi-result picker (capture + parse file:line)
\   everything else → capture-evaluate after switching only the data stack (sp/sp0) (independent stack; up/state/dp/dictionary shared with the editor)
: repl-eval-string ( addr len -- )
  2dup is-locate? if
    ['] repl-locate-cmd execute-parsing   \ locate/view → parse + jump (no pager)
  else 2dup is-where? if
    repl-where-cmd                        \ where/browse → capture the output and open a picker
  else
    rt-tu ! rt-ta !
    sp0 @ rt-sv-sp0 !  sp@ rt-sv-sp !
    repl-sp0 sp0 !  repl-sp @ sp!
    ['] (repl-do-capture) catch rt-err !
    sp@ repl-sp !
    rt-sv-sp0 @ sp0 !  rt-sv-sp @ sp!
    s" forth-out" tmp-file  r/o open-file throw repl-fid !
    repl-fid @ slurp-fid { a u }
    repl-fid @ close-file drop
    s" forth-out" tmp-file delete-file drop
    u 0> if  a u strip-ansi  dup 0> if
        repl-msg-flag @ if
          2dup
          dup 0> if  over over + 1- c@ 10 = if  1-  then  then   \ strip the trailing newline from the copy (the status line must not carry a \n)
          msg!  0 repl-msg-flag !
        then
        repl-buf @ -rot buffer-append
      else 2drop then  then
    a free throw
    \ warnings (redefined etc.): read back into *messages* (same handling as Ctrl-x reload-extension)
    s" forth-warn" tmp-file  r/o open-file throw repl-fid !
    repl-fid @ slurp-fid { wa wu }
    repl-fid @ close-file drop
    s" forth-warn" tmp-file delete-file drop
    wu 0> if  wa wu msg-log  then
    wa free throw
    rt-err @ ?dup if throw then
  then then ;

\ ensure *repl* exists (without switching the current buffer): for background opening via C-j / !
: repl-ensure
  repl-buf @ 0= if  s" *repl*" buffer-find repl-buf !  then   \ re-associate after reload
  repl-buf @ 0<> if exit then
  s" *repl*" BF_NOFILE buffer-new { b }
  b repl-buf !
  s" repl: type Forth + Enter, ESC for commands, ! to eval" msg-say ;

\ open and switch to *repl* (C-e)
: repl-open
  repl-ensure
  repl-buf @ buffer-switch ;

\ get the bytes of line number line
: repl-line-bytes ( line -- addr len )
  dup line-start { ls }
  line-length { ll }
  ll 4096 min { n }
  0 { i }
  begin i n < while
    ls i + g-char-at repl-send-buf i + c!
    1 +to i
  repeat
  repl-send-buf n ;

\ get the bytes of the range [lo,hi)
: repl-range-bytes ( lo hi -- addr len )
  { lo hi }  hi lo - 4096 min { n }
  n 0 ?do  lo i + g-char-at  repl-send-buf i + c!  loop
  repl-send-buf n ;

\ ===== history (↑↓ replay)=====
16 constant max-hist
256 constant hist-sz
\ reload preserves history: these variables are only defined the first time ([undefined] skips redefinition so old pointers/counts survive)
[undefined] repl-hist [IF]
variable repl-hist   0 repl-hist !
variable repl-hlens  0 repl-hlens !
variable hist-saved  0 hist-saved !
variable nhist   0 nhist !
[THEN]
variable hist-saved-len  0 hist-saved-len !
variable hist-i  -1 hist-i !   \ -1 = not browsing

\ allocated only on first use (the old history array survives reload)
: repl-hist-ensure
  repl-hist @ 0= if
    max-hist hist-sz * allocate throw repl-hist !
    max-hist cells allocate throw repl-hlens !
    256 allocate throw hist-saved !
  then ;

\ push into history (addr len): skip if empty or identical to the previous entry; drop the oldest when full
: hist-push ( addr len -- )
  { addr len }
  repl-hist-ensure
  len 0= if exit then
  len hist-sz 1- min { nlen }
  nhist @ 0> if
    repl-hlens @ nhist @ 1- cells + @ nlen = if
      addr nlen  repl-hist @ nhist @ 1- hist-sz * +  nlen  compare 0= if exit then
    then
  then
  nhist @ max-hist >= if
    max-hist 1- 0 ?do
      repl-hist @ i 1+ hist-sz * +  repl-hist @ i hist-sz * +  hist-sz move
      repl-hlens @ i 1+ cells + @  repl-hlens @ i cells + !
    loop
    max-hist 1- nhist !
  then
  addr repl-hist @ nhist @ hist-sz * + nlen move
  nlen repl-hlens @ nhist @ cells + !
  1 nhist +! ;

\ replace the current input line with (addr len)
: repl-line-replace ( addr len -- )
  { a u }
  cursor @ offset->linecol drop { line }
  line line-start { ls }
  line line-length { ll }
  ls ls ll + delete-range
  u 0 ?do  a i + c@ fe-insert  loop ;

\ replace the current input line with history entry i
: hist-recall ( i -- )
  { hi }
  repl-hist @ hi hist-sz * +  repl-hlens @ hi cells + @  repl-line-replace ;

\ save the line "being typed" (on the first ↑)
: hist-save-line
  cursor @ offset->linecol drop repl-line-bytes
  hist-sz 1- min hist-saved-len !
  hist-saved @ hist-saved-len @ move ;

: repl-hist-up
  repl-hist-ensure
  hist-i @ -1 = if
    hist-save-line
    nhist @ 1- hist-i !
  else
    hist-i @ 0> if -1 hist-i +! then
  then
  hist-i @ 0>= if hist-i @ hist-recall then ;

: repl-hist-down
  repl-hist-ensure
  hist-i @ -1 = if exit then
  hist-i @ nhist @ 1- < if
    1 hist-i +!
    hist-i @ hist-recall
  else
    -1 hist-i !
    hist-saved @ hist-saved-len @ repl-line-replace
  then ;

\ ===== completion (Tab)=====
\ Use Gforth's bundled search-prefix (history.fs, loaded by default): fast and correct.
variable comp-prefix-len 0 comp-prefix-len !
create comp-prefix 64 allot
variable comp-w 0 comp-w !
variable comp-len 0 comp-len !   \ total length inserted by the last completion (suffix+space), 0 = no active completion
variable comp-pos -1 comp-pos ! \ cursor position after the last completion (to detect whether it was "committed")

\ Extract the word prefix before the cursor (the last word of the current line).
\ Note: no locals/?do — locals+?do in a deep chain corrupt the return stack (a known project pitfall).
variable cp-ls 0 cp-ls !
variable cp-ll 0 cp-ll !
variable cp-i 0 cp-i !
variable cp-b 0 cp-b !
: collect-prefix ( -- )
  0 comp-prefix-len !
  cursor @ offset->linecol drop repl-line-bytes   ( a u )  \ current-line bytes (into repl-send-buf)
  cp-ll !  cp-ls !
  cp-ls @ comp-w !
  cp-ll @ cp-i !
  begin
    cp-i @ 0>
  while
    -1 cp-i +!
    cp-ls @ cp-i @ + c@ cp-b !
    cp-b @ 32 = cp-b @ 9 = or if  cp-ls @ cp-i @ + 1+  comp-w !  then
  repeat
  cp-ls @ cp-ll @ + comp-w @ -  63 min  comp-prefix-len !
  comp-w @  comp-prefix  comp-prefix-len @  move ;

\ insert the string byte by byte
: repl-insert-string ( a u -- )
  begin dup 0> while
    over c@ fe-insert
    1 /string
  repeat 2drop ;

\ Tab: complete/cycle candidates (search-prefix finds the next candidate by prefix)
: repl-complete
  cursor @ comp-pos @ <> if 0 comp-len ! then   \ cursor moved → new completion
  comp-len @ 0 ?do fe-backspace loop            \ undo the last completion, back to the prefix
  0 comp-len !
  collect-prefix
  comp-prefix-len @ 0= if #bell emit exit then
  comp-prefix comp-prefix-len @ search-prefix   ( a u )
  prefix-found @ 0= if 2drop s" no match" msg-say exit then
  dup 1+ comp-len !                            \ suffix + space
  repl-insert-string
  32 fe-insert
  cursor @ comp-pos ! ;

\ Enter in mode-repl: commit the current line and evaluate it
: repl-enter-eval
  cursor @ offset->linecol drop repl-line-bytes { a u }
  a u hist-push
  nl-buf 1 repl-buf @ -rot buffer-append
  a u repl-eval-string
  repl-buf @ gctx @ = if g-size cursor ! then ;

\ minibuffer evaluation (!)
: repl-eval-accept
  pbuf pbuflen @ hist-push
  pbuf pbuflen @ repl-buf @ -rot buffer-append
  nl-buf 1 repl-buf @ -rot buffer-append
  1 repl-msg-flag !                    \ ! evaluation: show the result via msg! (no buffer switch)
  pbuf pbuflen @ repl-eval-string
  repl-buf @ gctx @ = if g-size cursor ! then ;
\ ===== Forth word completion for the ! prompt (reuses Gforth search-prefix: returns the suffix)=====

\ collect the word prefix before the pbuf cursor (Forth word names = non-blank tokens, including punctuation; same space-delimited semantics as the REPL)
\ no locals: locals + to inside a loop in a deep chain corrupt the return stack
variable pcp-s 0 pcp-s !
: prompt-collect-prefix ( -- a u )
  pbpos @ pcp-s !
  begin
    pcp-s @ 0>  pbuf pcp-s @ 1- + c@  dup 32 = swap 9 = or  0=  and
  while
    pcp-s @ 1- pcp-s !
  repeat
  pbuf pcp-s @ +  pbpos @ pcp-s @ - ;

variable pcomp-len 0 pcomp-len !    \ length inserted by the last completion (suffix+space)
variable pcomp-pos -1 pcomp-pos !   \ pbpos after the last completion (to detect whether it was committed)

: prompt-forth-complete ( -- )
  pbpos @ pcomp-pos @ <> if  0 pcomp-len !  prefix-off  then   \ cursor moved → new completion
  pcomp-len @ { n }
  begin n 0> while                          \ undo the last completion, back to the prefix
    pbpos @ 0> if  lp-left  pb-del-at  then
    n 1- to n
  repeat
  0 pcomp-len !
  prompt-collect-prefix { pa pu }
  pu 0= if #bell emit exit then
  pa pu search-prefix
  prefix-found @ 0= if
    2drop  pa pu search-prefix            \ wrap back to the first at the end (search-prefix already prefix-off when empty)
    prefix-found @ 0= if 2drop s" no match" msg! exit then
  then
  \ insert the suffix + space
  0 { sl }
  begin dup 0> while
    over c@ pb-ins  sl 1+ to sl
    1 /string
  repeat 2drop
  32 pb-ins  sl 1+ to sl
  sl pcomp-len !
  pbpos @ pcomp-pos !
  0 pnrows ! ;

: repl-eval
  repl-ensure                          \ only ensure *repl* exists, do not switch the current buffer
  s" forth: " ['] repl-eval-accept prompt-open
  0 pcomp-len !  -1 pcomp-pos !        \ clear the completion state across prompts (to avoid wrongly undoing a stale suffix/cursor)
  ['] prompt-forth-complete prompt-tab-xt ! ;   \ Tab = Forth word completion

\ echo a u into *repl* and evaluate it
: repl-eval-bytes ( a u -- )
  2dup hist-push
  2dup repl-buf @ -rot buffer-append
  nl-buf 1 repl-buf @ -rot buffer-append
  repl-eval-string ;

\ extension is Forth (fs / 4th)
: forth-ext? ( a u -- f )
  2dup s" fs" compare 0=  if 2drop true exit then
       s" 4th" compare 0= ;

\ the current buffer is *repl* or a Forth file
: forth-buffer? ( -- f )
  gctx @ repl-buf @ = if true exit then
  fname @ flen @ ext-of forth-ext? ;

\ evaluate the current line or selection (Ctrl-j): only evaluates in *repl* / Forth files
: repl-eval-line
  forth-buffer? 0= if #bell emit exit then   \ check first: silently exit for non-Forth, without creating/prompting *repl*
  repl-ensure
  visual @ if
    vis-lo vis-hi repl-range-bytes  0 visual !
  else
    cursor @ offset->linecol drop repl-line-bytes
  then
  repl-eval-bytes ;

\ mode-repl / hooks / insert-mode selector
: mst-repl s" REPL" ;
: repl-to-command mode-command mode-set ;
: repl-buffer-enter
  gctx @ repl-buf @ = if g-size cursor ! mode-repl mode-set
  else cur-mode @ mode-repl = if mode-command mode-set then then ;
: repl-insert-mode
  gctx @ repl-buf @ = if g-size cursor ! mode-repl else mode-insert then ;
' repl-insert-mode insert-mode-xt !
