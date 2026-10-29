\ ===== grep.fs — project grep / live grep (rg → git grep → grep)=====
\ Depends on: lsp.fs lsp-kill/lsp-read/lsp-nonblock/lsp-closefd (libcc already set up),
\      fe.fs add-watcher/remove-watcher, and custom.fs list-panel / fe-locate-jump / fe-resolve-file.

require libcc.fs
also c-lib
c-library grepbind
  \c #include <unistd.h>
  \c #include <stdlib.h>
  \c long long sh_spawn(char *cmd, long long *out_r) {
  \c   int out[2];
  \c   if (pipe(out) != 0) return -1;
  \c   pid_t pid = fork();
  \c   if (pid == 0) {
  \c     dup2(out[1], 1);
  \c     close(out[0]); close(out[1]);
  \c     execlp("sh", "sh", "-c", cmd, (char*)NULL);
  \c     _exit(127);
  \c   }
  \c   close(out[1]);
  \c   out_r[0] = out[0];
  \c   return pid;
  \c }
  c-function sh-spawn sh_spawn a a -- n
end-c-library

variable grep-buf   0 grep-buf !       \ *grep* buffer
variable refs-buf   0 refs-buf !       \ *refs* buffer (dump of gr/gi/gd/gy results)
\ *refs* jump snapshot: copied from ref-* on dump (gd/gr/gi reuse and overwrite ref-*; the snapshot keeps Enter jumps valid)
create refs-file  max-refs 256 * allot
create refs-line  max-refs cells allot
create refs-col   max-refs cells allot
create refs-flen  max-refs cells allot
variable refs-n 0 refs-n !
variable grep-pid   0 grep-pid !       \ current live rg process (0 = none)
variable grep-rfd   0 grep-rfd !       \ live rg read fd
variable grep-pending 0 grep-pending ! \ 1 = new rg results are pending replacement of the old flist (prevents the picker from flashing as a full-screen buffer while typing)
variable grep-fixed 0 grep-fixed !     \ 1 = -F literal; 0 = regex (default, same as fzf)

create grep-cmd 1024 allot
variable grep-clen 0 grep-clen !

create grep-spawn-cmd 1024 allot
create grep-rdbuf 4096 allot

\ append a plain string to grep-cmd (clamped to 1024 to prevent a long query from overflowing into adjacent buffers)
: gput ( a u -- )
  { a u }
  u 0> if
    1024 grep-clen @ - 0 max u min { n }
    a grep-cmd grep-clen @ + n move
    n grep-clen +!
  then ;

\ single-quote wrapping + ' -> '\'' (fzf QuoteEntry), appended into grep-cmd
\ clamp u at entry to "worst case 4u+2 bytes still fits in 1024"; avoids an overflow from a long/quote-heavy query
: gq-put ( a u -- )
  { a u }
  grep-clen @ 1020 >= if 0 to u else
    1024 grep-clen @ - 2 - 4 / 0 max u min to u
  then
  u 0= if exit then
  39 grep-cmd grep-clen @ + c!  1 grep-clen +!     \ '
  0 { i }
  begin i u < while
    a i + c@ 39 = if
      [char] ' grep-cmd grep-clen @ + c!
      [char] \ grep-cmd grep-clen @ + 1+ c!
      [char] ' grep-cmd grep-clen @ + 2 + c!
      [char] ' grep-cmd grep-clen @ + 3 + c!
      4 grep-clen +!
    else
      a i + c@ grep-cmd grep-clen @ + c!  1 grep-clen +!
    then
    1 +to i
  repeat
  39 grep-cmd grep-clen @ + c!  1 grep-clen +! ;   \ '

\ build the command: fall back only when rg is absent (rg with no matches = empty result, no longer waiting on slow grep -rn); a u = query
: grep-build ( a u -- )
  { qa qu }
  0 grep-clen !
  s" if command -v rg >/dev/null 2>&1; then rg --line-number --no-heading --color=never " gput
  grep-fixed @ if s" -F " else s" --smart-case " then gput
  s" -- " gput  qa qu gq-put  s"  2>/dev/null" gput
  s" ; else git grep -nI " gput
  grep-fixed @ if s" -F " gput then
  s" -- " gput  qa qu gq-put  s"  2>/dev/null" gput
  s"  || grep -rn " gput
  grep-fixed @ if s" -F " else s" -E " then gput
  s" -- " gput  qa qu gq-put  s"  2>/dev/null" gput
  s" ; fi" gput ;

\ append raw bytes to flist (flist is \n-delimited, so rg output fits naturally)
: flist-append ( a u -- )
  { a u }
  flist-len @ u + flist-ensure
  a flist @ flist-len @ + u move
  u flist-len +! ;

\ ===== *grep* buffer =====
: grep-ensure ( -- )
  grep-buf @ 0= if  s" *grep*" buffer-find grep-buf !  then
  grep-buf @ 0<> if exit then
  s" *grep*" BF_NOFILE BF_READONLY or buffer-new { b }
  b grep-buf ! ;

\ replace the *grep* buffer contents (clear + insert + reset the saved view so the next switch starts from the top)
\ Note: grep-ensure→buffer-new fails on a non-empty data stack (a Gforth allocate quirk),
\ so store ( a u ) in module variables first, create the buffer with an empty stack, then restore.
variable gbr-a 0 gbr-a !
variable gbr-u 0 gbr-u !
: grep-buffer-replace ( a u -- )
  gbr-u !  gbr-a !
  grep-ensure
  grep-buf @ { b }
  gctx @ { saved }
  b gctx !
  g-size 0> if  g-size g-delete-backward  then
  g-size 0> if  g-size g-delete-forward   then
  1 lix-ecomplex !
  g-size g-move-gap
  gbr-a @ gbr-u @ g-insert
  1 lix-ecomplex !
  0 b B:SVVALID + !
  saved gctx ! ;

\ ===== *grep* buffer syntax (path:line: prefix + buffer-scope match highlight)=====
\ path:line: regex (precompiled at startup; a pattern starting with ^ = anchored to the line start)
create grep-pl-pat 32 allot
variable grep-pl-plen 0 grep-pl-plen !
variable grep-pl-re 0 grep-pl-re !
: grep-pl-init ( -- )
  s" ^[^:]*:[0-9]+:" { pa pl }
  pa grep-pl-pat pl move
  pl grep-pl-plen !
  pa pl 0 re-anchor-compile grep-pl-re ! ;
grep-pl-init

\ escaping buffers (used on dump/sync, not on the render path)
create grep-q 256 allot
variable grep-qlen 0 grep-qlen !
create gq-re 512 allot
variable gq-relen 0 gq-relen !
create gq-cbuf 1 allot
variable gq-i 0 gq-i !
create gq-meta
  46 c, 92 c, 91 c, 93 c, 94 c, 36 c, 42 c, 43 c,
  63 c, 40 c, 41 c, 123 c, 125 c, 124 c,
14 constant gq-meta-len

\ buffer-scope: each *grep* buffer stores its own compiled match pointer (supports multiple *grep* buffers)
16 constant max-gq
create gq-bufs max-gq cells allot
create gq-res  max-gq cells allot
variable ngq 0 ngq !
variable gq-re-ptr 0 gq-re-ptr !

\ buffer-scope query-word table (gq-find has no ?do: gq-free runs on the buffer-free deep chain where ?do+unloop would corrupt the return stack)
variable gq-fb 0 gq-fb !
variable gq-fi 0 gq-fi !

: gq-find ( b -- idx|-1 )
  gq-fb !  0 gq-fi !
  begin gq-fi @ ngq @ < while
    gq-bufs gq-fi @ cells + @ gq-fb @ = if  gq-fi @ exit  then
    1 gq-fi +!
  repeat
  -1 ;

: gq-set { b re -- }
  b gq-find dup 0>= if
    gq-res swap cells + !
  else
    drop
    ngq @ max-gq >= if  drop drop exit  then
    b gq-bufs ngq @ cells + !
    re gq-res  ngq @ cells + !
    1 ngq +!
  then ;

\ on buffer free, clean up its gq entries (free the compiled pointer + compact-shift) + clear a dangling grep-buf
: gq-free ( b -- )
  dup grep-buf @ = if  0 grep-buf !  then
  dup refs-buf @ = if  0 refs-buf !  then
  gq-find gq-i !
  gq-i @ 0>= if
    gq-res gq-i @ cells + @ ?dup if re-ptr-free then
    begin gq-i @ ngq @ 1- < while
      gq-bufs gq-i @ 1+ cells + @  gq-bufs gq-i @ cells + !
      gq-res  gq-i @ 1+ cells + @  gq-res  gq-i @ cells + !
      1 gq-i +!
    repeat
    -1 ngq +!
  then ;
' gq-free is buffer-free-xt

\ on dump/sync: escape the query + precompile + store into the buffer-scope table (free the old pointer)
: grep-q-save ( -- )
  0 lang-buf !                     \ make the next ensure-language re-run set-language (rebuild the match rules)
  pbuflen @ 255 min grep-qlen !      \ grep-q is 256: clamp the saved (highlight/jump) query
  pbuf grep-q grep-qlen @ move
  0 gq-relen !  0 gq-i !
  begin gq-i @ grep-qlen @ < while
    grep-q gq-i @ + c@ gq-cbuf c!
    gq-meta gq-meta-len gq-cbuf 1 search nip nip if
      92 gq-re gq-relen @ + c!  1 gq-relen +!
    then
    gq-cbuf c@ gq-re gq-relen @ + c!  1 gq-relen +!
    1 gq-i +!
  repeat
  gq-relen @ 0> if  gq-re gq-relen @ 1 re-anchor-compile  else  0  then { re }
  grep-buf @ gq-find { idx }
  idx 0>= if
    gq-res idx cells + @ ?dup if re-ptr-free then
    re gq-res idx cells + !
  else
    grep-buf @ re gq-set
  then ;

\ *grep* buffer syntax: path:line: prefix (comment color) + match (green, consistent with the picker)
\ all rules are added with precompiled pointers, no C calls, no locals (set-language runs on the render deep chain)
: rules-grep
  rules-clear
  R-REGEX  grep-pl-pat grep-pl-plen  grep-pl-re @  0  1  rule
  gctx @ gq-find gq-i !
  gq-i @ 0>= if
    gq-res gq-i @ cells + @ gq-re-ptr !
    gq-re-ptr @ 0<> if  R-REGEX  0 0  gq-re-ptr @  0 2 rule  then
  then ;

s" *grep*" ' rules-grep register-filename

\ parse "path:line[:content]" → open the file and jump to the matched column of the query
\ the byte offset is found in content with ci-search (plain-memory safe); after opening, the display column is derived via the gap buffer's u8-col
: grep-jump-line ( a u qa qu -- )
  { a u qa qu }
  a u 58 scan  { r1 r1l }             \ the first ':' and everything after it
  r1l 1 < if exit then
  r1 1+ r1l 1- 58 scan  { r2 r2l }    \ the second ':' and everything after it (may be absent, in which case to the end of the string)
  r1 1+  r1l r2l - 1-  s>number? 0= if 2drop exit then
  drop 1- { line }                    \ 0-based
  u r1l - { flen }                    \ file-segment length
  \ byte offset of the query in content (same as the in-line byte offset)
  0 { boff }
  qu 0>  r2l 1 >  and if
    r2 1+  r2l 1-  qa qu  ci-search if
      drop  r2 1+ -  to boff
    else
      2drop
    then
  then
  a flen fe-resolve-file { fa fu }
  ctx-push
  fa fu buffer-open
  \ byte offset → display column (u8-col reads the gap buffer and correctly handles tabs/wide characters)
  line line-start  line line-length  boff  u8-col { col }
  line col linecol->offset cursor !
  center-cursor
  0 pnrows ! ;

\ picker selection: jump (column = matched column of the query) + return to NORMAL
: grep-jump ( a u -- )
  pbuf pbuflen @ grep-jump-line
  mode-command prev-mode ! ;

\ snapshot ref-* → refs-* (called on dump; gd/gr/gi later overwrite ref-*)
variable rsn-i 0 rsn-i !
: refs-snapshot ( -- )
  nrefs @ refs-n !
  0 rsn-i !
  begin rsn-i @ nrefs @ < while
    ref-file rsn-i @ 256 * +  refs-file rsn-i @ 256 * +  256 move
    ref-line rsn-i @ cells + @  refs-line rsn-i @ cells + !
    ref-col  rsn-i @ cells + @  refs-col  rsn-i @ cells + !
    ref-flen rsn-i @ cells + @  refs-flen rsn-i @ cells + !
    1 rsn-i +!
  repeat ;

\ jump to the idx-th *refs* item (using the refs-* snapshot, not overwritten by gd/gr)
: refs-jump ( idx -- )
  rf-idx !
  rf-idx @ 0>=  rf-idx @ refs-n @ <  and if
    refs-file rf-idx @ 256 * +  refs-flen rf-idx @ cells + @  cwd-shorten { fa fu }
    fa fu buffer-find ?dup if  buffer-switch  else  fa fu buffer-open  then
    refs-line rf-idx @ cells + @  refs-col rf-idx @ cells + @  u16-byte
    refs-line rf-idx @ cells + @ line-start +  cursor !
    center-cursor
    0 pnrows !
  then ;

\ Enter in the *grep* buffer: jump to the current line
: grep-jump-at-cursor ( -- )
  gctx @ grep-buf @ <> if
    gctx @ refs-buf @ <> if #bell emit exit then
    \ *refs*: the current line number is the ref index (collect-refs fills flist in order); jump to file:line:col (using the snapshot)
    ctx-push
    cursor @ offset->linecol drop { idx }
    idx 0>=  idx refs-n @ <  and if
      idx refs-jump
    else
      #bell emit
    then
  else
    cursor @ offset->linecol drop repl-line-bytes grep-q grep-qlen @ grep-jump-line
  then ;

\ ===== synchronous grep (one-shot → *grep* buffer, replacing)=====
: grep-sync-accept ( -- )
  grep-q-save                       \ save the query (for match highlighting)
  pbuf pbuflen @ grep-build
  grep-cmd grep-clen @ r/o open-pipe throw { f }
  f slurp-fid { a u }
  f close-pipe drop
  u 0> if  a u grep-buffer-replace  then
  a ?dup if free throw then
  s" (grep done)" msg! ;

: grep-sync ( -- )
  grep-ensure
  s" grep: " ['] grep-sync-accept prompt-open ;

\ ===== live grep (async spawn)=====
\ kill the old process + remove the watcher
: grep-kill ( -- )
  grep-pid @ ?dup if  9 lsp-kill drop  0 grep-pid !  then
  grep-rfd @ ?dup if  remove-watcher  lsp-closefd drop  0 grep-rfd !  then
  0 grep-pending ! ;

\ a picker key (Ctrl-g): replace *grep* with the current flist results and switch to it (visible feedback)
: grep-dump ( -- )
  grep-ensure                       \ ensure the *grep* buffer first (grep-q-save needs grep-buf)
  grep-q-save                       \ save the query (for match highlighting)
  flist @ flist-len @  grep-buffer-replace
  grep-kill                          \ kill rg + remove the watcher
  0 prompting !  0 panel-xt !  0 panel-rows !
  mode-pop                           \ close the picker (back to NORMAL)
  grep-buf @ buffer-switch           \ switch to *grep*
  s" dumped to *grep*" msg! ;


\ watcher callback: non-blocking read → append to flist → recompute → redraw; returns true = data arrived (redraw needed)
variable gr-read 0 gr-read !
: grep-read ( -- f )
  0 gr-read !
  grep-pending @ if                   \ first batch of a new rg: replace the old flist
    0 grep-pending !
    0 flist-len !
  then
  begin
    grep-rfd @ grep-rdbuf 4096 lsp-read dup 0> while
      grep-rdbuf swap flist-append
      1 gr-read !
  repeat
  dup 0= if                           \ EOF: rg finished
    grep-rfd @ remove-watcher
    grep-rfd @ lsp-closefd drop  0 grep-rfd !
    0 grep-pid !
    1 gr-read !
  else drop then
  gr-read @ if
    lp-filter-all
    lp-update
  then
  gr-read @ ;

\ spawn a command (a u = complete shell command); stdout → pipe, register a watcher
: grep-spawn ( a u -- )
  { a u }
  u 1023 min { n }
  a grep-spawn-cmd n move
  0 grep-spawn-cmd n + c!            \ NUL
  grep-spawn-cmd grep-rfd sh-spawn grep-pid !
  grep-pid @ 0< if  0 grep-pid !  0 grep-rfd !  exit  then
  grep-rfd @ lsp-nonblock drop
  grep-rfd @ ['] grep-read add-watcher ;

\ lp-recollect-xt: rerun on input change; keep the old results until the first batch of the new rg arrives (prevents the picker from flashing as a full-screen buffer)
: grep-recollect ( -- )
  grep-kill
  pbuflen @ 0> if
    1 grep-pending !                  \ replace the old flist only when the new results arrive
    pbuf pbuflen @ grep-build
    grep-cmd grep-clen @ grep-spawn
  else
    0 flist-len !  0 lp-nmatches !    \ empty query: just clear
    0 grep-pending !
  then
  lp-filter-all ;

\ \ key: open the live grep picker
: grep-live ( -- )

  grep-kill
  0 flist-len !
  lp-meta-clear                        \ clear the optional metadata (grep does not fill it; allot is not initialized)
  ['] grep-recollect lp-recollect-xt !
  ['] grep-jump      lp-accept-xt !
  ['] grep-kill      lp-exit-xt !
  ['] grep-dump      lp-dump-xt !
  ['] noop           lp-collect-xt !
  0 lp-close-xt !
  s" GREP" lp-set-title
  0 pbuflen !  0 pbpos !  0 lp-sel !  0 lp-top !  0 lp-nmatches !
  lp-refresh                          \ empty query → grep-recollect only clears
  s" > " lp-prompt swap move  2 prompt-prefix-len !  lp-prompt prompt-prefix !
  1 prompting !
  mode-listpanel mode-push ;

\ ===== *refs* buffer (dump of gr/gi/gd/gy results)=====
: refs-ensure ( -- )
  refs-buf @ 0= if  s" *refs*" buffer-find refs-buf !  then
  refs-buf @ 0<> if exit then
  s" *refs*" BF_NOFILE BF_READONLY or buffer-new { b }
  b refs-buf ! ;

\ replace the *refs* buffer contents (clear + insert; same as grep-buffer-replace)
variable rbr-a 0 rbr-a !
variable rbr-u 0 rbr-u !
: refs-buffer-replace ( a u -- )
  rbr-u !  rbr-a !
  refs-ensure
  refs-buf @ { b }
  gctx @ { saved }
  b gctx !
  g-size 0> if g-size g-delete-backward then
  g-size 0> if g-size g-delete-forward then
  1 lix-ecomplex !
  g-size g-move-gap
  rbr-a @ rbr-u @ g-insert
  1 lix-ecomplex !
  0 b B:SVVALID + !
  saved gctx ! ;

\ escape ref-sym + precompile + store into buffer-scope (reusing the gq mechanism; not set when ref-symlen=0)
: refs-sym-save ( -- )
  0 lang-buf !
  ref-symlen @ 0> if
    0 gq-relen !  0 gq-i !
    begin gq-i @ ref-symlen @ < while
      ref-sym gq-i @ + c@ gq-cbuf c!
      gq-meta gq-meta-len gq-cbuf 1 search nip nip if
        92 gq-re gq-relen @ + c!  1 gq-relen +!
      then
      gq-cbuf c@ gq-re gq-relen @ + c!  1 gq-relen +!
      1 gq-i +!
    repeat
    gq-re gq-relen @ 0 re-anchor-compile { re }
    refs-buf @ gq-find { idx }
    idx 0>= if
      gq-res idx cells + @ ?dup if re-ptr-free then
      re gq-res idx cells + !
    else
      refs-buf @ re gq-set
    then
  then ;

\ *refs* buffer syntax: reuse rules-grep (path:line: prefix + match highlight)
s" *refs*" ' rules-grep register-filename

\ Enter in the *refs* buffer: reuse grep-jump-at-cursor (already extended there with ctx-push)

\ picker Ctrl-g: flist → *refs* + symbol highlight + close the picker + switch
: refs-dump ( -- )
  refs-ensure
  refs-sym-save
  refs-snapshot
  flist @ flist-len @  refs-buffer-replace
  0 prompting !  0 panel-xt !  0 panel-rows !
  mode-pop
  refs-buf @ buffer-switch
  s" dumped to *refs*" msg! ;

\ lsp.fs is included before grep.fs; wiring goes through a variable (defined in lsp.fs)
' refs-dump refs-dump-xt !
