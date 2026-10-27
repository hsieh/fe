\ ===== lsp.fs — LSP client (JSON-RPC over stdio, talks to a language server)=====
\ Transport: socketpair + fork + execlp (the child's stdin/stdout go over the socket)
\ Depends on cjson.fs (response decoding)

require libcc.fs
also c-lib
c-library lsp
  \c #include <unistd.h>
  \c #include <stdlib.h>
  \c #include <limits.h>
  \c #include <fcntl.h>
  \c /* two pipes: in=parent writes child reads, out=child writes parent reads (the standard LSP stdin/stdout split) */
  \c long long lsp_spawn(char *prog, long long *out_w, long long *out_r) {
  \c   int in[2], out[2];
  \c   if (pipe(in) != 0 || pipe(out) != 0) return -1;
  \c   pid_t pid = fork();
  \c   if (pid == 0) {
  \c     dup2(in[0], 0); dup2(out[1], 1);
  \c     close(in[0]); close(in[1]); close(out[0]); close(out[1]);
  \c     execlp(prog, prog, (char*)NULL);
  \c     _exit(127);
  \c   }
  \c   close(in[0]); close(out[1]);
  \c   out_w[0] = in[1];   /* parent writes */
  \c   out_r[0] = out[0];  /* parent reads */
  \c   return pid;
  \c }
  \c long long lsp_write(long long fd, char *buf, long long n) { return write((int)fd, buf, n); }
  \c long long lsp_read(long long fd, char *buf, long long n) { return read((int)fd, buf, n); }
  \c long long lsp_nonblock(long long fd) {
  \c   int fl = fcntl((int)fd, F_GETFL, 0);
  \c   if (fl < 0) return -1;
  \c   return fcntl((int)fd, F_SETFL, fl | O_NONBLOCK);
  \c }
  \c #include <signal.h>
  \c long long lsp_kill(long long pid, long long sig) { return kill((pid_t)pid, (int)sig); }
  \c long long lsp_closefd(long long fd) { return close((int)fd); }
  \c long long lsp_no_sigpipe(void) { signal(SIGPIPE, SIG_IGN); return 0; }
  \c #include <string.h>
  \c long long lsp_getcwd(char *buf, long long n) {
  \c   if (getcwd(buf, n)) return strlen(buf);
  \c   return 0;
  \c }
  \c long long lsp_fexists(char *path) { return access(path, F_OK) == 0; }
  \c long long lsp_realpath(char *path, char *buf, long long n) {
  \c   char tmp[PATH_MAX];
  \c   char *r = realpath(path, tmp);
  \c   if (!r) return 0;
  \c   long long len = strlen(tmp);
  \c   if (len > n) len = n;
  \c   memcpy(buf, tmp, len);
  \c   return len;
  \c }
  c-function lsp-spawn lsp_spawn a a a -- n
  c-function lsp-write lsp_write n a n -- n
  c-function lsp-read  lsp_read  n a n -- n
  c-function lsp-nonblock lsp_nonblock n -- n
  c-function lsp-kill lsp_kill n n -- n
  c-function lsp-closefd lsp_closefd n -- n
  c-function lsp-no-sigpipe lsp_no_sigpipe -- n
  c-function lsp-getcwd lsp_getcwd a n -- n
  c-function lsp-fexists lsp_fexists a -- n
  c-function lsp-realpath lsp_realpath a a n -- n
end-c-library

variable lsp-wfd 0 lsp-wfd !     \ write fd (server stdin)
variable lsp-rfd 0 lsp-rfd !     \ read fd (server stdout)
variable lsp-id 0 lsp-id !       \ JSON-RPC request-id counter
variable lsp-cid 0 lsp-cid !     \ current completion request id
variable lsp-wid 0 lsp-wid !     \ warmup request id (temporary)
variable lsp-pid 0 lsp-pid !     \ server process pid (killed on exit)
variable rbuf 0 rbuf !
variable rbuf-cap 0 rbuf-cap !

\ next request id
: next-id ( -- n )  lsp-id @ 1+ dup lsp-id ! ;

\ build the "id":N match string into idbuf (for search)
create idbuf 16 allot
variable idbuf-len 0 idbuf-len !
variable idn-a 0 idn-a !
variable idn-u 0 idn-u !
\ format unsigned decimal n into numbuf; return ( addr len ) — arithmetic only, no <# #s #>
\ (<# #s #> would clobber the shared hold area, which may hold a user string left on the REPL stack)
\ iterative + no locals + no recurse: locals/recurse in the deep LSP chain corrupt the return stack
create numbuf 24 allot
variable numlen 0 numlen !
variable numn 0 numn !
variable numi 0 numi !
create numtmp 24 allot
: num>str ( n -- addr len )
  numn !
  0 numi !
  begin
    numn @ 10 mod 48 +  numtmp numi @ + c!
    1 numi +!
    numn @ 10 / numn !
    numn @ 0= until
  0 numlen !
  begin numi @ 0> while
    -1 numi +!
    numtmp numi @ + c@  numbuf numlen @ + c!
    1 numlen +!
  repeat
  numbuf numlen @ ;
: idbuf-set ( n -- )
  n num>str idn-u ! idn-a !      \ digit string
  s\" \"id\":" idbuf swap move        \ "id":" prefix (5 bytes)
  idn-a @ idbuf 5 + idn-u @ move  \ append the digits
  idn-u @ 5 + idbuf-len ! ;

\ small-integer parsing
: pn ( a u -- n )  0 0 2swap >number 2drop drop ;

\ fd read/write (fd first, matching the c-function argument order)
: fd-write { a u -- n }  lsp-wfd @ a u lsp-write ;
: fd-read  { a u -- n }  lsp-rfd @ a u lsp-read ;

\ ===== JSON request construction =====
variable jbuf 0 jbuf !
variable jcap 0 jcap !
variable jlen 0 jlen !

: jensure { n -- }   \ ensure there is room for jlen+n (grows dynamically)
  jlen @ n + jcap @ > if
    jlen @ n + 2* 65536 max { newcap }
    jbuf @ if  jbuf @ newcap resize throw jbuf !  else  newcap allocate throw jbuf !  then
    newcap jcap !
  then ;

: jadd { a u -- }  jlen @ u + jensure  a jbuf @ jlen @ + u move  u jlen +! ;
: jaddc { c -- }  1 jensure  c jbuf @ jlen @ + c!  1 jlen +! ;
: jnum ( n -- )  num>str jadd ;

\ append a JSON string with escaping (no surrounding quotes)
\ append a JSON string with escaping (no surrounding quotes); all globals (locals inside the loop corrupt the return stack)
variable js-a 0 js-a !
variable js-u 0 js-u !
variable js-i 0 js-i !
variable js-c 0 js-c !
: js-esc ( a u -- )
  js-u !  js-a !
  0 js-i !
  begin js-i @ js-u @ < while
    js-a @ js-i @ + c@ js-c !
    js-c @ 34 = if s\" \\\"" jadd else
    js-c @ 92 = if s\" \\\\" jadd else
    js-c @ 10 = if s\" \\n" jadd else
    js-c @ 9  = if s\" \\t" jadd else
    js-c @ 13 = if s\" \\r" jadd else
    js-c @ 32 < if else js-c @ jaddc then
    then then then then then
    1 js-i +!
  repeat ;

\ JSON string literal (with quotes + escaping)
: jstr { a u -- }  34 jaddc  a u js-esc  34 jaddc ;

\ send a frame: Content-Length: N\r\n\r\n<body>
: frame-send { a u -- }
  s" Content-Length: " fd-write drop
  u num>str fd-write drop
  s\" \r\n\r\n" fd-write drop
  a u fd-write drop ;

\ ===== LSP frame reading (non-blocking + state accumulation)=====
\ the read fd is set O_NONBLOCK; frames may arrive in chunks, so accumulate into rinc and consume only when complete
variable rinc 0 rinc !
variable rinc-cap 0 rinc-cap !

: rinc-ensure { n -- }  n rinc-cap @ > if
  n 2* 65536 max { nc }
  rinc @ if  rinc @ nc resize throw rinc !  else  nc allocate throw rinc !  then
  nc rinc-cap !
then ;

: rbuf-ensure { n -- }  n rbuf-cap @ > if
  n 2* 65536 max { nc }
  rbuf @ if  rbuf @ nc resize throw rbuf !  else  nc allocate throw rbuf !  then
  nc rbuf-cap !
then ;
variable rinclen 0 rinclen !
variable lsp-eof 0 lsp-eof !    \ 1 = the server's read end is closed
variable fr-n    0 fr-n !       \ frame-related temporaries (all globals, per the deep-chain convention)
variable fr-pos  0 fr-pos !
variable fr-hlen 0 fr-hlen !
variable fr-cl   0 fr-cl !
variable dr-n    0 dr-n !
variable lr-i 0 lr-i !    \ linear-scan index (for begin...while; no ?do+unloop, to avoid return-stack corruption in a deep multi-layer locals chain)
variable lr-lim 0 lr-lim !   \ loop limit (temporarily stores results like cj-asize)

\ non-blocking read: read all currently readable bytes into rinc (stop at EAGAIN/EOF)
: lsp-drain ( -- )
  begin
    rinclen @ 65536 + rinc-ensure
    lsp-rfd @ rinc @ rinclen @ +  rinc-cap @ rinclen @ -  lsp-read dr-n !
    dr-n @ 0= if 1 lsp-eof ! exit then
    dr-n @ 0< if exit then
    dr-n @ rinclen +!
  again ;

\ skip leading spaces (a u -> a' u')
: skip-sp ( a u -- a u )
  begin 2dup 0> swap c@ 32 = and while
    1- swap 1+ swap
  repeat ;

\ extract the Content-Length value from the header (rinc fr-hlen); 0 = failure
: header-clen ( -- n )
  rinc @ fr-hlen @ s" Content-Length:" search if
    swap 15 + swap 15 -   \ Gforth search returns the match start; jump past the colon
    skip-sp               \ skip the space after the colon
    pn
  else
    2drop 0
  then ;

\ try to extract one frame from rinc: copy the body into rbuf, return its length; 0 = incomplete
: frame-take ( -- n )
  0 fr-hlen !
  0 lr-i !
  begin lr-i @ rinclen @ 2 - 0 max <  fr-hlen @ 0=  and  while
    rinc @ lr-i @ + 4 s\" \r\n\r\n" compare 0= if
      lr-i @ 4 + fr-hlen !
    then
    1 lr-i +!
  repeat
  fr-hlen @ 0= if 0 exit then
  header-clen fr-cl !
  fr-cl @ 0> if
    rinclen @ fr-hlen @ fr-cl @ + >= if
      fr-cl @ 1+ rbuf-ensure
      rinc @ fr-hlen @ + rbuf @ fr-cl @ move
      rinc @ fr-hlen @ fr-cl @ + + rinc @ rinclen @ fr-hlen @ fr-cl @ + - move
      rinclen @ fr-hlen @ fr-cl @ + - rinclen !
      0 rbuf @ fr-cl @ + c!
      fr-cl @ exit
    then
  then
  0 ;

\ non-blocking get one frame: 0 = no complete frame yet (the watcher retries on the next POLLIN)
: frame-nb ( -- n )  lsp-drain  frame-take ;

\ blocking get one frame: poll until complete (used on the synchronous initialize/warmup path; returns 0 on EOF)
: frame-block ( -- n )
  begin
    lsp-drain
    frame-take ?dup if exit then
    lsp-eof @ if 0 exit then
    5 ms
  again ;

variable lsp-open? 0 lsp-open? !   \ whether the server is started + didOpen sent
variable lsp-gbuf  0 lsp-gbuf !    \ the buffer bound to LSP
create lsp-uri 512 allot
variable lsp-uri-len 0 lsp-uri-len !

create cwd-buf 512 allot
\ build a file:// URI (absolute path)
create lsp-tmp 512 allot

: lsp-build-uri ( -- )
  0 lsp-uri-len !
  s" file://" lsp-uri lsp-uri-len @ + swap move  7 lsp-uri-len +!
  fname @ c@ 47 = if
    \ already an absolute path
    fname @ flen @  lsp-uri lsp-uri-len @ + swap move  flen @ lsp-uri-len +!
  else
    \ relative path: join cwd + "/" + fname, realpath resolves ../ and symlinks
    cwd-buf 511 lsp-getcwd { clen }
    clen 1+ flen @ + 512 < if
      cwd-buf lsp-tmp clen move
      47 lsp-tmp clen + c!
      fname @ lsp-tmp clen 1+ + flen @ move
      0 lsp-tmp clen 1+ flen @ + + c!
      lsp-tmp  lsp-uri lsp-uri-len @ +  505  lsp-realpath
      dup 0> if lsp-uri-len +! else drop then
    then
  then ;

\ ===== multi-language mapping: extension -> (languageId, server command) =====
32 constant max-lsp
create lsp-exts   max-lsp 16 * allot
create lsp-elens  max-lsp cells allot
create lsp-langs  max-lsp 32 * allot
create lsp-llens  max-lsp cells allot
create lsp-servs  max-lsp 64 * allot
create lsp-slens  max-lsp cells allot
variable nlsp 0 nlsp !

\ per-language root markers (space-separated; lsp-find-root tries these first, then the global fallback)
create lsp-rmarks  max-lsp 64 * allot
create lsp-rlens   max-lsp cells allot
: lsp-rmarks-init  0 lr-i !  begin lr-i @ max-lsp < while  0 lsp-rlens lr-i @ cells + !  1 lr-i +!  repeat ;
lsp-rmarks-init

: lsp-register { ext eu lang lu serv su -- }
  nlsp @ max-lsp >= if exit then
  ext lsp-exts nlsp @ 16 * + eu move
  eu lsp-elens nlsp @ cells + !
  lang lsp-langs nlsp @ 32 * + lu move
  lu lsp-llens nlsp @ cells + !
  serv lsp-servs nlsp @ 64 * + su move
  su lsp-slens nlsp @ cells + !
  1 nlsp +! ;

\ register root markers for an extension (space-separated)
: lsp-register-roots { ext eu markers mu -- }
  0 lr-i !
  begin lr-i @ nlsp @ < while
    lr-i @ cells lsp-elens + @ eu = if
      ext eu lsp-exts lr-i @ 16 * + eu compare 0= if
        markers lsp-rmarks lr-i @ 64 * + mu move
        mu lsp-rlens lr-i @ cells + !
        exit
      then
    then
    1 lr-i +!
  repeat ;

\ ext -> root markers (returns 0 0 if not registered)
: lsp-roots-of { a u -- ma mu }
  0 lr-i !
  begin lr-i @ nlsp @ < while
    lr-i @ cells lsp-elens + @ u = if
      a u lsp-exts lr-i @ 16 * + u compare 0= if
        lsp-rmarks lr-i @ 64 * +  lsp-rlens lr-i @ cells + @  exit
      then
    then
    1 lr-i +!
  repeat
  0 0 ;

\ ext -> (languageId len server len); returns 0 0 0 0 if not registered
: lsp-of { a u -- la lu sa su }
  0 lr-i !
  begin lr-i @ nlsp @ < while
    lr-i @ cells lsp-elens + @ u = if
      a u lsp-exts lr-i @ 16 * + u compare 0= if
        lsp-langs lr-i @ 32 * + lsp-llens lr-i @ cells + @
        lsp-servs lr-i @ 64 * + lsp-slens lr-i @ cells + @
        exit
      then
    then
    1 lr-i +!
  repeat
  0 0 0 0 ;

\ current file -> languageId (returns empty if not registered)
: lsp-language-id ( -- a u )
  fname @ flen @ ext-of lsp-of 2drop ;

\ current file -> server command (returns empty if not registered)
: lsp-server-cmd ( -- a u )
  fname @ flen @ ext-of lsp-of 2swap 2drop ;

\ ===== workspace root detection (search upward for a marker)=====
create lsp-root 512 allot
variable lsp-root-len 0 lsp-root-len !
variable lsp-marker 0 lsp-marker !
variable lsp-marker-len 0 lsp-marker-len !

: file-exists? ( a u -- f )
  ck-set  ck lsp-fexists ;

\ store the path's directory (drop the last / and everything after) into lsp-root
: lsp-dir-of ( a u -- )
  lsp-root-len !  lsp-root lsp-root-len @ move
  begin  lsp-root-len @ 0>  lsp-root lsp-root-len @ 1- + c@ 47 <>  and  while
    -1 lsp-root-len +!
  repeat
  lsp-root-len @ 0> if -1 lsp-root-len +! then ;

\ shrink to the parent directory
: lsp-dir-up ( -- )
  begin  lsp-root-len @ 0>  lsp-root lsp-root-len @ 1- + c@ 47 <>  and  while
    -1 lsp-root-len +!
  repeat
  lsp-root-len @ 0> if -1 lsp-root-len +! then ;

\ join root/marker and check existence
: lsp-try-marker ( marker mu -- f )
  lsp-marker-len !  lsp-marker !
  lsp-root-len @ lsp-marker-len @ + 2 + 512 > if false exit then
  lsp-root lsp-tmp lsp-root-len @ move
  47 lsp-tmp lsp-root-len @ + c!
  lsp-marker @ lsp-tmp lsp-root-len @ 1+ + lsp-marker-len @ move
  lsp-tmp lsp-root-len @ lsp-marker-len @ + 1+ file-exists? ;

\ iterate the space-separated markers; true if any hits
: lsp-try-marker-list { a u -- f }
  0 0 0 0 { p n s mlen }
  a to p  u to n
  begin n 0> while
    begin n 0>  p c@ 32 =  and while  p 1+ to p  n 1- to n  repeat
    n 0<= if false exit then
    p to s  0 to mlen
    begin n 0>  p c@ 32 <>  and while  p 1+ to p  n 1- to n  mlen 1+ to mlen  repeat
    s mlen lsp-try-marker if true exit then
  repeat
  false ;

\ search upward for the workspace root (a fixed marker list)
: lsp-find-root ( -- )
  0 lsp-root-len !
  fname @ c@ 47 = if
    fname @ flen @ lsp-dir-of
  else
    cwd-buf 511 lsp-getcwd  cwd-buf swap lsp-dir-of
  then
  begin
    lsp-root-len @ 0= if exit then
    \ try the current language's markers first (only if registered)
    fname @ flen @ ext-of lsp-roots-of { ma mu }
    ma mu lsp-try-marker-list if exit then
    \ global fallback markers
    s" go.mod"        lsp-try-marker if exit then
    s" go.work"       lsp-try-marker if exit then
    s" package.json"  lsp-try-marker if exit then
    s" package-lock.json" lsp-try-marker if exit then
    s" yarn.lock"     lsp-try-marker if exit then
    s" Cargo.toml"    lsp-try-marker if exit then
    s" pyproject.toml" lsp-try-marker if exit then
    s" requirements.txt" lsp-try-marker if exit then
    s" CMakeLists.txt" lsp-try-marker if exit then
    s" .git"          lsp-try-marker if exit then
    lsp-dir-up
  again ;

\ registry
s" go"   s" go"          s" gopls" lsp-register
s" py"   s" python"      s" pylsp" lsp-register
s" rs"   s" rust"        s" rust-analyzer" lsp-register
s" js"   s" javascript"  s" typescript-language-server" lsp-register
s" ts"   s" typescript"  s" typescript-language-server" lsp-register
s" c"    s" c"           s" clangd" lsp-register
s" h"    s" c"           s" clangd" lsp-register
s" cpp"  s" cpp"         s" clangd" lsp-register
s" cc"   s" cpp"         s" clangd" lsp-register
s" cxx"  s" cpp"         s" clangd" lsp-register
s" lua"  s" lua"         s" lua-language-server" lsp-register
s" sh"   s" shellscript" s" bash-language-server" lsp-register
s" bash" s" shellscript" s" bash-language-server" lsp-register
s" rb"   s" ruby"        s" solargraph" lsp-register
s" css"  s" css"         s" vscode-css-language-server" lsp-register
s" html" s" html"        s" vscode-html-language-server" lsp-register
s" java" s" java"        s" jdtls" lsp-register
s" json" s" json"        s" vscode-json-language-server" lsp-register
s" md"   s" markdown"    s" marksman" lsp-register

\ per-language root markers (space-separated; find-root tries these first)
s" go"   s" go.mod go.work"                     lsp-register-roots
s" py"   s" pyproject.toml requirements.txt"     lsp-register-roots
s" rs"   s" Cargo.toml"                           lsp-register-roots
s" js"   s" package.json package-lock.json yarn.lock" lsp-register-roots
s" ts"   s" package.json package-lock.json yarn.lock" lsp-register-roots
s" c"    s" CMakeLists.txt"                       lsp-register-roots
s" h"    s" CMakeLists.txt"                       lsp-register-roots
s" cpp"  s" CMakeLists.txt"                       lsp-register-roots
s" cc"   s" CMakeLists.txt"                       lsp-register-roots
s" cxx"  s" CMakeLists.txt"                       lsp-register-roots
s" java" s" pom.xml build.gradle"                  lsp-register-roots
s" rb"   s" Gemfile"                               lsp-register-roots
s" lua"  s" .luarc.json"                           lsp-register-roots

\ the last path segment of root (the workspaceFolders name)
: lsp-basename ( -- a u )
  lsp-root-len @ { n }
  begin  n 0>  lsp-root n 1- + c@ 47 =  and  while  n 1- to n  repeat
  n 0= if  lsp-root 0 exit then
  0 { s }
  0 lr-i !
  begin lr-i @ n < while
    lsp-root lr-i @ + c@ 47 = if  lr-i @ 1+ to s  then
    1 lr-i +!
  repeat
  lsp-root s +  n s - ;

\ send didOpen (current buffer)
: lsp-didopen ( -- )
  snap-build
  lsp-build-uri
  0 jlen !
  s\" {\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":" jadd
  lsp-uri lsp-uri-len @ jstr
  s\" ,\"languageId\":" jadd
  lsp-language-id jstr
  s\" ,\"version\":1,\"text\":" jadd
  snap @ snap-len @ jstr
  s\" }}}" jadd
  jbuf @ jlen @ frame-send ;

\ warmup: after didOpen send a completion(0,0) to make gopls load packages in the background
variable lsp-parsediag-xt 0 lsp-parsediag-xt !   \ the xt of lsp-parse-diagnostics (defined later)
\ UTF-16 col within line -> byte offset (relative to the line start; the inverse of u16-col)
variable ub-line 0 ub-line !
variable ub-col  0 ub-col !
variable ub-ls   0 ub-ls !
variable ub-ll   0 ub-ll !
variable ub-b    0 ub-b !
variable ub-n    0 ub-n !
variable ub-b0   0 ub-b0 !
: u16-byte ( line col -- boff )
  ub-col !  ub-line !
  ub-line @ line-start ub-ls !
  ub-line @ line-length ub-ll !
  0 ub-b !  0 ub-n !
  begin ub-b @ ub-ll @ <  ub-n @ ub-col @ <  and while
    ub-ls @ ub-b @ + g-char-at ub-b0 !
    ub-b0 @ 0xF0 >= if 2 else 1 then ub-n +!
    ub-ls @ ub-b @ + u8-n ub-b +!
  repeat
  ub-b @ ;

\ ===== diagnostics (publishDiagnostics)=====
variable dg-root 0 dg-root !
variable dg-p    0 dg-p !
variable dg-arr  0 dg-arr !
variable dg-it   0 dg-it !
variable dg-rg   0 dg-rg !
variable dg-st   0 dg-st !
variable dg-en   0 dg-en !
variable dg-l0   0 dg-l0 !
variable dg-c0   0 dg-c0 !
variable dg-l1   0 dg-l1 !
variable dg-c1   0 dg-c1 !
variable dg-sv   0 dg-sv !
variable dg-noise 0 dg-noise !
variable dg-code  0 dg-code !

: lsp-parse-diagnostics ( n -- f )
  drop   \ n is unused (rbuf is global); returns true = diagnostics were filled (redraw needed)
  gctx @ lsp-gbuf @ <> if false exit then   \ the current buffer is not the LSP-bound one (e.g. *refs*) → drop the diagnostics, to keep them from leaking into another buffer's render
  rbuf @ cj-parse ?dup 0= if false exit then dg-root !
  s" params" ck-set  dg-root @ ck cj-get ?dup 0= if dg-root @ cj-delete false exit then dg-p !
  \ uri filter: only handle diagnostics for the current file (gopls sends diagnostics for the whole workspace)
  s" uri" ck-set  dg-p @ ck cj-get ?dup if
    cj-str ?dup if
      dup c-strlen  lsp-uri lsp-uri-len @  compare 0= 0= if
        dg-root @ cj-delete false exit
      then
    then
  then
  s" diagnostics" ck-set  dg-p @ ck cj-get ?dup 0= if dg-root @ cj-delete false exit then dg-arr !
  0 ndiags !
  dg-arr @ cj-asize max-diags min lr-lim !
  0 lr-i !
  begin lr-i @ lr-lim @ < while
    dg-arr @ lr-i @ cj-aitem dg-it !
    s" range" ck-set  dg-it @ ck cj-get dg-rg !
    s" start" ck-set  dg-rg @ ck cj-get dg-st !
    s" line" ck-set  dg-st @ ck cj-get cj-num f>s dg-l0 !
    s" character" ck-set  dg-st @ ck cj-get cj-num f>s dg-c0 !
    s" end" ck-set  dg-rg @ ck cj-get dg-en !
    s" line" ck-set  dg-en @ ck cj-get cj-num f>s dg-l1 !
    s" character" ck-set  dg-en @ ck cj-get cj-num f>s dg-c1 !
    s" severity" ck-set  dg-it @ ck cj-get ?dup if cj-num f>s else 1 then dg-sv !
    dg-l0 @ dg-c0 @ u16-byte  dg-l0 @ line-start +  diag-b0 ndiags @ cells + !
    dg-l1 @ dg-c1 @ u16-byte  dg-l1 @ line-start +  diag-b1 ndiags @ cells + !
    \ zero-length diagnostics (start==end, e.g. an expected selector) → make at least 1 character visible
    diag-b1 ndiags @ cells + @  diag-b0 ndiags @ cells + @  <= if
      diag-b0 ndiags @ cells + @ 1+  diag-b1 ndiags @ cells + !
    then
    dg-l0 @ diag-line ndiags @ cells + !
    dg-sv @ diag-sev ndiags @ cells + !
    1 ndiags +!
    1 lr-i +!
  repeat
  dg-root @ cj-delete
  true ;

' lsp-parse-diagnostics lsp-parsediag-xt !

\ ===== pending-request table (id → handler-xt) + unified dispatch =====
8 constant max-lsp-wl
create lsp-wl-id max-lsp-wl cells allot
create lsp-wl-xt max-lsp-wl cells allot
variable lsp-nwl 0 lsp-nwl !
variable lsp-ready? 0 lsp-ready? !   \ initialize responded + didOpen sent
variable lsp-init-id 0 lsp-init-id !     \ initialize request id (for matching the async handshake)
2variable lsp-init-start                  \ initialize send time (utime microseconds)
variable lsp-init-timeout 20000000 lsp-init-timeout !   \ initialization timeout 20s (microseconds)

: lsp-wl-add ( id xt -- )
  lsp-nwl @ max-lsp-wl >= if drop drop exit then
  swap                        \ ( xt id ); store id first, then xt
  lsp-wl-id lsp-nwl @ cells + !
  lsp-wl-xt lsp-nwl @ cells + !
  1 lsp-nwl +! ;

: lsp-wl-take ( id -- xt | 0 )
  0 lr-i !
  begin lr-i @ lsp-nwl @ < while
    lsp-wl-id lr-i @ cells + @ over = if
      -1 lsp-wl-id lr-i @ cells + !
      lsp-wl-xt lr-i @ cells + @
      nip exit
    then
    1 lr-i +!
  repeat
  drop 0 ;

\ extract the N of "id":N from rbuf[0:n]; returns -1 when there is no id (a notification)
\ scan manually with compare (Gforth search's 2>r/2r> crashes on runtime strings inside compiled definitions)
create fi-pat 8 allot
s\" \"id\":" fi-pat swap move
variable fi-n 0 fi-n !
variable fi-i 0 fi-i !
variable fi-p 0 fi-p !
variable fi-id 0 fi-id !
: frame-id ( n -- id )
  fi-n !
  0 fi-i !
  -1 fi-id !
  begin fi-i @ fi-n @ 5 - <= while
    rbuf @ fi-i @ + 5 fi-pat 5 compare 0= if
      fi-i @ 5 + fi-p !
      rbuf @ fi-p @ +  fi-n @ fi-p @ -  skip-sp  pn  fi-id !
      fi-id @ exit
    then
    fi-i @ 1+ fi-i !
  repeat
  fi-id @ ;

\ warmup response: ignore
: lsp-on-warm ( n -- )  drop ;

variable lsp-wn 0 lsp-wn !
\ non-blocking warmup: after didOpen send a completion(0,0) to make the server load packages in the background
: lsp-send-warmup ( -- )
  next-id lsp-wid !
  0 jlen !
  s\" {\"jsonrpc\":\"2.0\",\"id\":" jadd
  lsp-wid @ jnum
  s\" ,\"method\":\"textDocument/completion\",\"params\":{\"textDocument\":{\"uri\":" jadd
  lsp-uri lsp-uri-len @ jstr
  s\" },\"position\":{\"line\":0,\"character\":0}}}" jadd
  jbuf @ jlen @ frame-send ;

\ initialize response (async handshake): send initialized + didOpen + warmup
: lsp-on-init ( -- )
  0 jlen !  s\" {\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}" jadd  jbuf @ jlen @ frame-send
  lsp-didopen
  lsp-send-warmup
  1 lsp-ready? !
  gctx @ lsp-gbuf !
  s" lsp: gopls ok" msg! ;

\ initialize response: send initialized + didOpen + warmup
\ didChange full sync (called after an edit and before completion so the server sees the latest content)
variable lsp-doc-version 0 lsp-doc-version !
variable lsp-cver 0 lsp-cver !

: lsp-didchange ( -- )
  lsp-ready? @ 0= if exit then   \ do not send when LSP is not started/ready (otherwise lsp-wfd=0 and frame-send writes to fd0=terminal, flashing jsonrpc)
  1 lsp-doc-version +!
  snap-build
  lsp-build-uri
  0 jlen !
  s\" {\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{\"textDocument\":{\"uri\":" jadd
  lsp-uri lsp-uri-len @ jstr
  s\" ,\"version\":1},\"contentChanges\":[{\"text\":" jadd
  snap @ snap-len @ jstr
  s\" }]}}" jadd
  jbuf @ jlen @ frame-send ;

: lsp-after-complete ( -- )  0 edit-dirty !  lsp-didchange ;

\ idle debounce: after an edit, ~600ms idle automatically sends didChange (for live diagnostics)
variable lsp-sync-count 0 lsp-sync-count !
variable lsp-pending 0 lsp-pending !    \ 1 = a completion request is in flight

\ initialization timeout abort: close fd + remove the watcher + reset
: lsp-init-abort ( -- )
  lsp-rfd @ ?dup if dup remove-watcher  lsp-closefd drop  then
  lsp-wfd @ ?dup if lsp-closefd drop then
  lsp-pid @ ?dup if 9 lsp-kill drop then
  0 lsp-open? !  0 lsp-ready? !  0 lsp-pending !
  0 lsp-wfd !  0 lsp-rfd !  0 lsp-pid !
  s" lsp: init timeout" msg! ;

: lsp-idle-sync
  lsp-open? @ 0= if exit then
  lsp-ready? @ 0= if
    \ wait for the initialize response; abort on timeout
    utime lsp-init-start 2@ d-  lsp-init-timeout @ s>d  d> if
      lsp-init-abort
    then
    exit
  then
  lsp-pending @ 0> if exit then     \ A: do not send didChange while a completion is in flight
  edit-dirty @ 0= if exit then
  lsp-sync-count @ 1+ lsp-sync-count !
  lsp-sync-count @ 20 >= if
    0 lsp-sync-count !  0 edit-dirty !
    lsp-didchange
  then ;
' lsp-idle-sync is idle-poll

\ debounced sync after an edit (for live diagnostics): mark dirty on edit, send didChange after 500ms idle
\ startup: spawn + initialize (async; the response is dispatched by the watcher)
variable lsp-onresp-xt 0 lsp-onresp-xt !   \ the xt of lsp-on-response (defined later, to avoid a forward reference)
: lsp-open ( -- )
  lsp-open? @ if exit then
  \ clean up the old server remnants (fd + watcher left when it was killed externally; otherwise the old watcher keeps firing, causing flicker/EOF)
  lsp-rfd @ ?dup if dup remove-watcher  lsp-closefd drop  then
  lsp-wfd @ ?dup if lsp-closefd drop then
  0 lsp-rfd !  0 lsp-wfd !
  lsp-no-sigpipe drop
  lsp-server-cmd { sa su }
  su 0= if s" lsp: no server for this file type" msg! exit then
  sa su ck-set  ck lsp-wfd lsp-rfd lsp-spawn dup 0< if s" lsp: spawn fail" msg! drop exit then
  lsp-pid !
  0 rinclen !  0 lsp-eof !
  lsp-rfd @ lsp-nonblock drop
  lsp-find-root
  0 jlen !
  s\" {\"jsonrpc\":\"2.0\",\"id\":" jadd
  next-id dup lsp-init-id ! jnum
  s\" ,\"method\":\"initialize\",\"params\":{\"processId\":null" jadd
  lsp-root-len @ if
    s\" ,\"rootUri\":\"file://" jadd
    lsp-root lsp-root-len @ js-esc
    34 jaddc
    s\" ,\"workspaceFolders\":[{\"uri\":\"file://" jadd
    lsp-root lsp-root-len @ js-esc
    s\" \",\"name\":\"" jadd
    lsp-basename js-esc
    s\" \"}]" jadd
  then
  s\" ,\"capabilities\":{}}}" jadd
  jbuf @ jlen @ frame-send
  utime lsp-init-start 2!
  lsp-rfd @ lsp-onresp-xt @ add-watcher
  1 lsp-open? !
  0 lsp-ready? !
  s" lsp: starting" msg! ;

\ re-fetch diagnostics when switching back to the LSP file (buffer-enter clears ndiags)
\ current buffer differs from the LSP-bound one → re-didOpen the current buffer (LSP stays usable after switching to another file)
: lsp-bind-buf ( -- )
  lsp-ready? @ 0= if exit then
  bflags @ BF_NOFILE and if exit then   \ non-file buffers (REPL/messages) are not re-bound
  loading? @ if exit then
  gctx @ lsp-gbuf @ = if exit then
  1 snap-dirty !
  lsp-didopen
  gctx @ lsp-gbuf ! ;

: lsp-refresh-diags ( -- )
  lsp-ready? @ 0= if exit then
  bflags @ BF_NOFILE and if exit then   \ non-file buffers do not fetch diagnostics
  loading? @ if exit then
  gctx @ lsp-gbuf @ <> if
    lsp-bind-buf           \ new buffer → didOpen (which triggers diagnostics)
  else
    1 snap-dirty !        \ force a fresh snapshot (to avoid staleness)
    lsp-didchange
  then ;

\ send completion and fill cands/clens/ncands (globals, to avoid locals inside a loop)
variable lc-it 0 lc-it !
variable lc-ln 0 lc-ln !
variable lc-k  0 lc-k  !
variable lc-dl 0 lc-dl !
variable lc-cl 0 lc-cl !
variable lc-ch 0 lc-ch !

\ UTF-16 column conversion: LSP position.character is a UTF-16 code unit (not a byte, not a display column)
\ 4-byte code points (U+10000..) = 2 units, the rest = 1; the loop uses only globals (per the deep-chain convention)
variable u16-off 0 u16-off !
variable u16-lim 0 u16-lim !
variable u16-n   0 u16-n !
variable u16-i   0 u16-i !
variable u16-b0  0 u16-b0 !

: u16-col ( off lim -- n )    \ number of UTF-16 code units in [off, off+lim)
  u16-lim ! u16-off !
  0 u16-n ! 0 u16-i !
  begin u16-i @ u16-lim @ < while
    u16-off @ u16-i @ + g-char-at u16-b0 !
    u16-b0 @ 0xF0 >= if 2 else 1 then u16-n +!
    u16-b0 @ 0x80 < if 1
    else u16-b0 @ 0xC0 < if 1
    else u16-b0 @ 0xE0 < if 2
    else u16-b0 @ 0xF0 < if 3 else 4 then then then then
    u16-i +!
  repeat
  u16-n @ ;



\ send one completion and parse it (fill cands); returns 0 if there are no candidates
\ parse the completion response (rbuf n), fill cands
: lsp-parse-completion ( n -- )
  drop   \ n is unused (rbuf is global)
  rbuf @ cj-parse ?dup 0= if exit then { root }
  s" result" ck-set  root ck cj-get ?dup 0= if root cj-delete exit then
  s" items" ck-set  ck cj-get ?dup 0= if root cj-delete exit then { items }
  items cj-asize max-cands min lr-lim !
  0 lr-i !
  begin lr-i @ lr-lim @ < while
    items lr-i @ cj-aitem lc-it !
    \ prefer insertText (the text actually inserted); fall back to label (the display text) when empty/missing
    0 lc-ln !
    s" insertText" ck-set  lc-it @ ck cj-get ?dup if
      cands ncands @ /cand * + /cand cj-strcpy lc-ln !
    then
    lc-ln @ 0= if
      s" label" ck-set  lc-it @ ck cj-get ?dup if
        cands ncands @ /cand * + /cand cj-strcpy lc-ln !
      then
    then
    lc-ln @ 0> if
      \ prefix filter: a candidate must start with cbase (the already-typed prefix), otherwise skip it
      lc-ln @ cbase-len @ >= if
        cands ncands @ /cand * + cbase-len @  cbase cbase-len @  compare 0= if
      \ kind (integer; 0 when missing)
      0 lc-k !
      s" kind" ck-set  lc-it @ ck cj-get ?dup if cj-num f>s lc-k ! then
      lc-k @ ckinds ncands @ cells + !
      \ detail (string; empty when missing)
      0 lc-dl !
      s" detail" ck-set  lc-it @ ck cj-get ?dup if
        cdetails ncands @ /cand * + /cand cj-strcpy lc-dl !
      then
      lc-dl @ cdlens ncands @ cells + !
      \ candidate text
      lc-ln @ clens ncands @ cells + !
      1 ncands +!
        then
      then
    then
    1 lr-i +!
  repeat
  root cj-delete ;

variable lsp-req-cur -1 lsp-req-cur !   \ cursor position the candidate corresponds to
variable lsp-fid 0 lsp-fid !            \ frame id (for dispatch)

\ completion response: parse and fill cands
: lsp-on-comp ( n -- )
  lsp-doc-version @ lsp-cver @ = if   \ B: accept only when the versions match, drop stale ones
    0 ncands !
    lsp-parse-completion
  else
    drop
  then
  0 lsp-pending ! ;

\ watcher callback: drain all complete frames; responses are looked up by id, notifications dispatched by method
variable lsp-changed 0 lsp-changed !
: lsp-on-response ( -- f )
  0 lsp-changed !
  lsp-drain
  begin
    frame-take fr-n !
  fr-n @ 0> while
    fr-n @ frame-id fi-id !
    fi-id @ lsp-init-id @ =  lsp-ready? @ 0=  and if
      lsp-on-init
      1 lsp-changed !
    else
      fi-id @ lsp-wid @ = if
        \ warmup response: ignore
      else
        rbuf @ fr-n @ idbuf idbuf-len @ search nip nip if
          fr-n @ lsp-on-comp
          1 lsp-changed !
        then
        rbuf @ fr-n @ s" textDocument/publishDiagnostics" search nip nip if
          fr-n @ lsp-parse-diagnostics  if 0 pnrows ! 1 lsp-changed ! then
        then
      then
    then
  repeat
  lsp-eof @ if
    0 lsp-pending ! 0 lsp-open? ! 0 lsp-ready? !
    lsp-rfd @ ?dup if dup remove-watcher  lsp-closefd drop  then   \ remove the watcher after EOF, otherwise it keeps firing and causes idle redraws
    lsp-wfd @ ?dup if lsp-closefd drop then
    0 lsp-rfd !  0 lsp-wfd !
    s" lsp: server closed" msg!
    1 lsp-changed !
  then
  lsp-changed @ ;

' lsp-on-response lsp-onresp-xt !

\ send a completion request (non-blocking)
: lsp-send-completion ( -- )
  lsp-doc-version @ lsp-cver !
  next-id lsp-cid !
  0 jlen !
  s\" {\"jsonrpc\":\"2.0\",\"id\":" jadd
  lsp-cid @ jnum
  s\" ,\"method\":\"textDocument/completion\",\"params\":{\"textDocument\":{\"uri\":" jadd
  lsp-uri lsp-uri-len @ jstr
  s\" },\"position\":{\"line\":" jadd
  lc-cl @ jnum
  s\" ,\"character\":" jadd
  lc-ch @ jnum
  s\" }}}" jadd
  jbuf @ jlen @ frame-send
  lsp-cid @ idbuf-set
  1 lsp-pending ! ;

\ completion source: async. The first Tab sends the request (returns 0 0); after the response arrives the next Tab fetches a candidate
: lsp-complete-next ( a u -- cand clen | 0 0 )
  wn-u !  wn-a !
  lsp-ready? @ 0= if 0 0 exit then
  lsp-bind-buf
  cursor @ cp-cur @ =  cindex @ 0>=  and  ncands @ 0>  and if
    1 cindex +!                       \ cycle (insert at the cursor)
  else
    wn-a @ cbase wn-u @ move  wn-u @ cbase-len !
    0 cins-len !
    0 cindex !
    cursor @ lsp-req-cur @ =  ncands @ 0>  and if
      \ candidate already ready (at the current position): use it directly
    else
      0 ncands !
      cursor @ offset->linecol drop { cl }
      cl line-start { ls }
      cl lc-cl !
      ls cursor @ ls - u16-col lc-ch !   \ byte offset -> UTF-16 code unit (LSP position)
      lsp-didchange
      lsp-send-completion
      cursor @ lsp-req-cur !
    then
  then
  ncands @ 0= if 0 0 exit then
  cindex @ ncands @ mod cindex !
  cands cindex @ /cand * +  clens cindex @ cells + @ ;

\ ===== goto-definition (blocking, waits synchronously for the response)=====
variable gd-l -1 gd-l !   \ definition line (-1 = none)
variable gd-c 0 gd-c !    \ definition character
variable gd-found 0 gd-found !
create gd-uri-buf 512 allot
variable gd-uri-len 0 gd-uri-len !

\ target symbol (records the cursor word when gd/gy/gr/gi starts; used to highlight the symbol in content)
create ref-sym 64 allot
variable ref-symlen 0 ref-symlen !
\ the xt of refs-dump (defined later in grep.fs; lsp.fs is included before grep.fs, so use a variable to avoid a forward reference)
variable refs-dump-xt 0 refs-dump-xt !

\ record the cursor word as the target symbol (called in lsp-pos)
: lsp-record-sym ( -- )
  0 ref-symlen !
  cursor-word  ref-symlen !  ref-sym ref-symlen @ move ;

\ fill the cursor position into lc-cl / lc-ch (globals, reused by each request)
: lsp-pos ( -- )
  lsp-bind-buf
  cursor @ offset->linecol drop { cl }
  cl line-start { ls }
  cl lc-cl !
  ls cursor @ ls - u16-col lc-ch !
  lsp-record-sym ;

\ ===== references=====
256 constant max-refs
variable nrefs 0 nrefs !
create ref-line  max-refs cells allot
create ref-col   max-refs cells allot
create ref-flen  max-refs cells allot
create ref-file  max-refs 256 * allot

variable rf-root 0 rf-root !
variable rf-arr  0 rf-arr !
variable rf-loc  0 rf-loc !
variable rf-rg   0 rf-rg !
variable rf-st   0 rf-st !
variable rf-base 0 rf-base !
variable rf-len  0 rf-len !
variable rf-i    0 rf-i !
variable rf-cnt  0 rf-cnt !
variable rf-idx  0 rf-idx !
variable rf-ua   0 rf-ua !
variable rf-ulen 0 rf-ulen !
create rf-disp 512 allot

\ parse a single location (rf-loc) → the current ref-* array slot
: rf-add-loc ( -- )
  ref-file nrefs @ 256 * + rf-base !
  0 ref-flen nrefs @ cells + !
  \ uri: Location.uri or LocationLink.targetUri (3.14+)
  s" uri" ck-set  rf-loc @ ck cj-get
  dup 0= if drop  s" targetUri" ck-set  rf-loc @ ck cj-get  then
  ?dup if
    rf-base @ 256 cj-strcpy ref-flen nrefs @ cells + !
    ref-flen nrefs @ cells + @ 7 - rf-len !
    rf-len @ 0> if
      rf-base @ 7 +  rf-base @  rf-len @  move
      rf-len @ ref-flen nrefs @ cells + !
    then
  then
  \ range: Location.range or LocationLink.targetRange / targetSelectionRange (3.14+)
  s" range" ck-set  rf-loc @ ck cj-get
  dup 0= if drop  s" targetRange" ck-set  rf-loc @ ck cj-get  then
  dup 0= if drop  s" targetSelectionRange" ck-set  rf-loc @ ck cj-get  then
  rf-rg !
  rf-rg @ 0= if exit then
  s" start" ck-set  rf-rg @ ck cj-get rf-st !
  s" line" ck-set  rf-st @ ck cj-get cj-num f>s  ref-line nrefs @ cells + !
  s" character" ck-set  rf-st @ ck cj-get cj-num f>s  ref-col nrefs @ cells + !
  1 nrefs +! ;

\ parse a references/implementation response: result may be an array or a single location
: lsp-parse-references ( n -- )
  drop
  rbuf @ cj-parse ?dup 0= if exit then rf-root !
  0 nrefs !
  s" result" ck-set  rf-root @ ck cj-get ?dup 0= if rf-root @ cj-delete exit then rf-arr !
  rf-arr @ cj-is-arr if
    rf-arr @ cj-asize max-refs min lr-lim !
    0 lr-i !
    begin lr-i @ lr-lim @ < while
      rf-arr @ lr-i @ cj-aitem rf-loc !
      rf-loc @ 0= if  lr-lim @ lr-i !  else  rf-add-loc  then
      1 lr-i +!
    repeat
  else
    rf-arr @ rf-loc !
    rf-add-loc
  then
  rf-root @ cj-delete ;

\ picker source: fill flist (path:line:content + symbol-highlight metadata)
\ same-file cache (avoids re-reading the disk when many references are in the same file)
128 constant max-lc          \ content max bytes
create cf-file 512 allot
variable cf-flen 0 cf-flen !
variable cf-buf 0 cf-buf !
variable cf-len 0 cf-len !
variable cf-valid 0 cf-valid !
variable rlc-fd 0 rlc-fd !
variable rlc-line 0 rlc-line !
variable rlc-i 0 rlc-i !
variable rlc-c 0 rlc-c !
variable rlc-start 0 rlc-start !
variable rlc-a 0 rlc-a !
variable rlc-u 0 rlc-u !
create rlc-out max-lc 1+ allot
variable rlc-olen 0 rlc-olen !

: cf-free ( -- )  cf-buf @ ?dup if free drop  0 cf-buf !  then ;

\ read the whole file (a u) into the cache; on failure → cf-valid=0
: cf-load ( a u -- )
  511 min cf-flen !
  cf-file cf-flen @ move
  cf-buf @ ?dup if free drop  0 cf-buf !  then
  cf-file cf-flen @ r/o open-file if  0 cf-valid !  exit  then
  rlc-fd !
  rlc-fd @ slurp-fid  cf-len !  cf-buf !
  rlc-fd @ close-file drop
  1 cf-valid ! ;

\ cache hit? (a u = path)
: cf-hit? ( a u -- f )
  rlc-u ! rlc-a !
  cf-valid @ 0= if false exit then
  rlc-u @ cf-flen @ <> if false exit then
  rlc-a @ rlc-u @  cf-file cf-flen @  compare 0= ;

\ read line (0-based) of file (a u) → rlc-out rlc-olen (truncated to max-lc)
: read-line-content ( a u line -- ca cu )
  rlc-line !  rlc-u !  rlc-a !
  rlc-a @ rlc-u @ cf-hit? 0= if  rlc-a @ rlc-u @ cf-load  then
  cf-valid @ 0= if 0 0 exit then
  0 rlc-i !  0 rlc-c !
  begin rlc-c @ rlc-line @ <  rlc-i @ cf-len @ <  and while
    cf-buf @ rlc-i @ + c@ 10 = if 1 rlc-c +! then
    1 rlc-i +!
  repeat
  rlc-i @ cf-len @ >= if 0 0 exit then
  rlc-i @ rlc-start !
  begin rlc-i @ cf-len @ <  cf-buf @ rlc-i @ + c@ 10 <>  and while
    1 rlc-i +!
  repeat
  rlc-i @ rlc-start @ - rlc-olen !
  rlc-olen @ 0> if  cf-buf @ rlc-i @ 1- + c@ 13 = if -1 rlc-olen +!  then  then
  rlc-olen @ max-lc min rlc-olen !
  cf-buf @ rlc-start @ +  rlc-out  rlc-olen @  move
  rlc-out rlc-olen @ ;

\ symbol marking (word boundary): search ref-sym within content(msym-a msym-u); the hit offsets (relative to the item start = cstart) go into lp-symmarks
variable msym-cstart 0 msym-cstart !
variable msym-a 0 msym-a !
variable msym-u 0 msym-u !
variable msym-i 0 msym-i !
variable msym-j 0 msym-j !
variable msym-hit 0 msym-hit !
variable msym-lb 0 msym-lb !
variable msym-rb 0 msym-rb !
variable msym-n 0 msym-n !

: mark-sym ( cstart a u -- )
  msym-u ! msym-a ! msym-cstart !
  0 msym-n !
  ref-symlen @ 0= if exit then
  msym-u @ ref-symlen @ < if exit then
  0 msym-i !
  begin msym-i @ msym-u @ ref-symlen @ - <= while
    1 msym-hit !
    0 msym-j !
    begin msym-j @ ref-symlen @ < while
      msym-a @ msym-i @ msym-j @ + + c@  ref-sym msym-j @ + c@  <> if
        0 msym-hit !
      then
      1 msym-j +!
    repeat
    msym-hit @ if
      msym-i @ 0= if 1 else msym-a @ msym-i @ 1- + c@ word-char? 0= then msym-lb !
      msym-i @ ref-symlen @ + msym-u @ = if 1 else msym-a @ msym-i @ ref-symlen @ + + c@ word-char? 0= then msym-rb !
      msym-lb @ msym-rb @ and if
        msym-n @ ref-symlen @ + fz-max-n <= if
          0 msym-j !
          begin msym-j @ ref-symlen @ < while
            msym-cstart @ msym-i @ msym-j @ + +  lp-symmarks rf-i @ fz-max-n * +  msym-n @ msym-j @ + +  c!
            1 msym-j +!
          repeat
          msym-n @ ref-symlen @ + msym-n !
        then
      then
    then
    1 msym-i +!
  repeat
  msym-n @ lp-symlens rf-i @ cells + ! ;

\ picker source: fill flist (path:line:content) + lp-cstarts/lp-symmarks metadata
variable cr-ca 0 cr-ca !
variable cr-cu 0 cr-cu !

: collect-refs ( -- )
  0 flist-len !
  cf-free  0 cf-valid !
  0 rf-i !
  begin rf-i @ nrefs @ < while
    ref-file rf-i @ 256 * +  ref-flen rf-i @ cells + @  rf-len !
    ref-file rf-i @ 256 * +  rf-len @  cwd-shorten  dup rf-len !  rf-disp swap move
    58 rf-disp rf-len @ + c!  1 rf-len +!
    ref-line rf-i @ cells + @ 1+ num>str  rf-ulen ! rf-ua !
    rf-ua @  rf-disp rf-len @ +  rf-ulen @  move
    rf-len @ rf-ulen @ + rf-len !
    rf-len @ lp-cstarts rf-i @ cells + !
    \ content segment
    ref-file rf-i @ 256 * +  ref-flen rf-i @ cells + @  ref-line rf-i @ cells + @  read-line-content  cr-cu ! cr-ca !
    cr-cu @ 0> if
      58 rf-disp rf-len @ + c!  1 rf-len +!
      cr-ca @  rf-disp rf-len @ +  cr-cu @  move
      rf-len @ cr-cu @ + rf-len !
    then
    rf-disp rf-len @ flist-add
    \ symbol marking (within the content segment, relative to the item start)
    lp-cstarts rf-i @ cells + @  rf-disp lp-cstarts rf-i @ cells + @ +  rf-len @ lp-cstarts rf-i @ cells + @ -  mark-sym
    1 rf-i +!
  repeat ;

\ flist offset → original item index (count the preceding newlines)
: flist-idx ( off -- idx )
  rf-i !
  0 rf-idx !  0 rf-cnt !
  begin rf-cnt @ rf-i @ < while
    flist @ rf-cnt @ + c@ 10 = if 1 rf-idx +! then
    1 rf-cnt +!
  repeat
  rf-idx @ ;

\ jump to the idx-th reference/implementation (does not push the stack)
: rf-jump ( idx -- )
  rf-idx !
  rf-idx @ 0>=  rf-idx @ nrefs @ <  and if
    ref-file rf-idx @ 256 * +  ref-flen rf-idx @ cells + @  cwd-shorten { fa fu }
    fa fu buffer-find ?dup if
      buffer-switch
    else
      fa fu buffer-open
    then
    ref-line rf-idx @ cells + @  ref-col rf-idx @ cells + @  u16-byte
    ref-line rf-idx @ cells + @ line-start +  cursor !
    center-cursor
    0 pnrows !
  then ;

\ picker selection: jump to that reference (push the return point before jumping)
: do-ref-jump ( a u -- )
  2drop
  lp-starts lp-sel @ cells + @ flist-idx rf-idx !
  rf-idx @ 0>=  rf-idx @ nrefs @ <  and if
    ctx-push
    rf-idx @ rf-jump
  then ;

\ picker selection: jump to that position (gd/gy already pushed the stack above; do not push again)
: do-ref-jump-nopush ( a u -- )
  2drop
  lp-starts lp-sel @ cells + @ flist-idx rf-idx !
  rf-idx @ 0>=  rf-idx @ nrefs @ <  and if
    rf-idx @ rf-jump
  then ;

\ parse a definition/typeDefinition response: multiple results are collected into ref-*/nrefs (reusing the references parser)
: lsp-parse-definition ( n -- )  lsp-parse-references ;

\ gd: jump to the definition of the symbol under the cursor (jump directly when single, open a picker when multiple; push the return point)
: lsp-goto-def ( -- )
  lsp-ready? @ 0= if s" lsp: not ready" msg! exit then
  lsp-pos
  next-id lsp-cid !
  0 jlen !
  s\" {\"jsonrpc\":\"2.0\",\"id\":" jadd
  lsp-cid @ jnum
  s\" ,\"method\":\"textDocument/definition\",\"params\":{\"textDocument\":{\"uri\":" jadd
  lsp-uri lsp-uri-len @ jstr
  s\" },\"position\":{\"line\":" jadd
  lc-cl @ jnum
  s\" ,\"character\":" jadd
  lc-ch @ jnum
  s\" }}}" jadd
  jbuf @ jlen @ frame-send
  lsp-cid @ idbuf-set
  0 nrefs !
  20 0 do
    frame-block ?dup 0= if leave then
    fr-n !
    rbuf @ fr-n @ idbuf idbuf-len @ search nip nip if
      fr-n @ lsp-parse-definition
      leave
    then
    rbuf @ fr-n @ s" textDocument/publishDiagnostics" search nip nip if
      fr-n @ lsp-parse-diagnostics
    then
  loop
  nrefs @ 0= if s" lsp: no definition" msg! exit then
  ctx-push
  nrefs @ 1 = if
    0 rf-jump
  else
    s" DEFINITIONS" lp-set-title  ['] collect-refs ['] do-ref-jump-nopush 0 listpanel-open  refs-dump-xt @ lp-dump-xt !
  then ;

\ gy: jump to the type definition of the expression under the cursor (textDocument/typeDefinition)
: lsp-goto-typedef ( -- )
  lsp-ready? @ 0= if s" lsp: not ready" msg! exit then
  lsp-pos
  next-id lsp-cid !
  0 jlen !
  s\" {\"jsonrpc\":\"2.0\",\"id\":" jadd
  lsp-cid @ jnum
  s\" ,\"method\":\"textDocument/typeDefinition\",\"params\":{\"textDocument\":{\"uri\":" jadd
  lsp-uri lsp-uri-len @ jstr
  s\" },\"position\":{\"line\":" jadd
  lc-cl @ jnum
  s\" ,\"character\":" jadd
  lc-ch @ jnum
  s\" }}}" jadd
  jbuf @ jlen @ frame-send
  lsp-cid @ idbuf-set
  0 nrefs !
  20 0 do
    frame-block ?dup 0= if leave then
    fr-n !
    rbuf @ fr-n @ idbuf idbuf-len @ search nip nip if
      fr-n @ lsp-parse-definition
      leave
    then
    rbuf @ fr-n @ s" textDocument/publishDiagnostics" search nip nip if
      fr-n @ lsp-parse-diagnostics
    then
  loop
  nrefs @ 0= if s" lsp: no type definition" msg! exit then
  ctx-push
  nrefs @ 1 = if
    0 rf-jump
  else
    s" TYPE DEFS" lp-set-title  ['] collect-refs ['] do-ref-jump-nopush 0 listpanel-open  refs-dump-xt @ lp-dump-xt !
  then ;

\ ===== hover=====
variable hv-node 0 hv-node !
create hover-buf 512 allot
variable hover-len 0 hover-len !
variable hv-i 0 hv-i !
variable hv-s 0 hv-s !

\ display hover: strip the ``` fence lines, take the signature line
: hv-show ( -- )
  0 hv-s !
  hover-buf 3 s" ```" compare 0= if
    0 hv-i !
    begin  hv-i @ hover-len @ <  hover-buf hv-i @ + c@ 10 <>  and  while
      1 hv-i +!
    repeat
    hv-i @ 1+ hv-s !
  then
  hv-s @ hv-i !
  begin  hv-i @ hover-len @ <  hover-buf hv-i @ + c@ 10 <>  and  while
    1 hv-i +!
  repeat
  hover-buf hv-s @ +  hv-i @ hv-s @ -  text-cols @ 1- min  msg! ;

\ parse the hover response: the text of result.contents (MarkupContent.value or a plain string)
: lsp-parse-hover ( n -- )
  drop
  rbuf @ cj-parse ?dup 0= if exit then { root }
  0 hover-len !
  s" result" ck-set  root ck cj-get ?dup 0= if root cj-delete exit then { res }
  s" contents" ck-set  res ck cj-get ?dup 0= if root cj-delete exit then hv-node !
  hv-node @ cj-is-str if
    hv-node @ hover-buf 512 cj-strcpy hover-len !
  else
    s" value" ck-set  hv-node @ ck cj-get ?dup if
      hover-buf 512 cj-strcpy hover-len !
    then
  then
  root cj-delete ;

\ K: show the hover documentation for the symbol under the cursor
: lsp-hover ( -- )
  lsp-ready? @ 0= if s" lsp: not ready" msg! exit then
  lsp-pos
  next-id lsp-cid !
  0 jlen !
  s\" {\"jsonrpc\":\"2.0\",\"id\":" jadd
  lsp-cid @ jnum
  s\" ,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":" jadd
  lsp-uri lsp-uri-len @ jstr
  s\" },\"position\":{\"line\":" jadd
  lc-cl @ jnum
  s\" ,\"character\":" jadd
  lc-ch @ jnum
  s\" }}}" jadd
  jbuf @ jlen @ frame-send
  lsp-cid @ idbuf-set
  0 hover-len !
  20 0 do
    frame-block ?dup 0= if leave then
    fr-n !
    rbuf @ fr-n @ idbuf idbuf-len @ search nip nip if
      fr-n @ lsp-parse-hover
      leave
    then
    rbuf @ fr-n @ s" textDocument/publishDiagnostics" search nip nip if
      fr-n @ lsp-parse-diagnostics
    then
  loop
  hover-len @ 0= if s" lsp: no hover" msg! exit then
  hv-show ;

\ gr: find references → list picker
: lsp-references ( -- )
  lsp-ready? @ 0= if s" lsp: not ready" msg! exit then
  lsp-pos
  next-id lsp-cid !
  0 jlen !
  s\" {\"jsonrpc\":\"2.0\",\"id\":" jadd
  lsp-cid @ jnum
  s\" ,\"method\":\"textDocument/references\",\"params\":{\"textDocument\":{\"uri\":" jadd
  lsp-uri lsp-uri-len @ jstr
  s\" },\"position\":{\"line\":" jadd
  lc-cl @ jnum
  s\" ,\"character\":" jadd
  lc-ch @ jnum
  s\" },\"context\":{\"includeDeclaration\":true}}}" jadd
  jbuf @ jlen @ frame-send
  lsp-cid @ idbuf-set
  0 nrefs !
  20 0 do
    frame-block ?dup 0= if leave then
    fr-n !
    rbuf @ fr-n @ idbuf idbuf-len @ search nip nip if
      fr-n @ lsp-parse-references
      leave
    then
    rbuf @ fr-n @ s" textDocument/publishDiagnostics" search nip nip if
      fr-n @ lsp-parse-diagnostics
    then
  loop
  nrefs @ 0= if s" lsp: no references" msg! exit then
  s" REFERENCES" lp-set-title  ['] collect-refs ['] do-ref-jump 0 listpanel-open  refs-dump-xt @ lp-dump-xt ! ;

\ gi: jump to the implementation (jump directly when single, open a picker when multiple)
: lsp-goto-impl ( -- )
  lsp-ready? @ 0= if s" lsp: not ready" msg! exit then
  lsp-pos
  next-id lsp-cid !
  0 jlen !
  s\" {\"jsonrpc\":\"2.0\",\"id\":" jadd
  lsp-cid @ jnum
  s\" ,\"method\":\"textDocument/implementation\",\"params\":{\"textDocument\":{\"uri\":" jadd
  lsp-uri lsp-uri-len @ jstr
  s\" },\"position\":{\"line\":" jadd
  lc-cl @ jnum
  s\" ,\"character\":" jadd
  lc-ch @ jnum
  s\" }}}" jadd
  jbuf @ jlen @ frame-send
  lsp-cid @ idbuf-set
  0 nrefs !
  20 0 do
    frame-block ?dup 0= if leave then
    fr-n !
    rbuf @ fr-n @ idbuf idbuf-len @ search nip nip if
      fr-n @ lsp-parse-references
      leave
    then
    rbuf @ fr-n @ s" textDocument/publishDiagnostics" search nip nip if
      fr-n @ lsp-parse-diagnostics
    then
  loop
  nrefs @ 0= if s" lsp: no implementation" msg! exit then
  ctx-push
  nrefs @ 1 = if
    0 rf-jump
  else
    s" IMPLEMENTATIONS" lp-set-title  ['] collect-refs ['] do-ref-jump 0 listpanel-open  refs-dump-xt @ lp-dump-xt !
  then ;

\ ===== signature-help (function signature)=====
: lsp-parse-signature ( n -- )
  drop
  rbuf @ cj-parse ?dup 0= if exit then { root }
  0 hover-len !
  s" result" ck-set  root ck cj-get ?dup 0= if root cj-delete exit then { res }
  s" signatures" ck-set  res ck cj-get ?dup 0= if root cj-delete exit then { arr }
  arr cj-is-arr 0= if root cj-delete exit then
  arr 0 cj-aitem { sig }
  sig 0= if root cj-delete exit then
  s" label" ck-set  sig ck cj-get ?dup if
    hover-buf 512 cj-strcpy hover-len !
  then
  root cj-delete ;

\ gs: show the signature of the function call under the cursor
: lsp-signature-help ( -- )
  lsp-ready? @ 0= if s" lsp: not ready" msg! exit then
  lsp-pos
  next-id lsp-cid !
  0 jlen !
  s\" {\"jsonrpc\":\"2.0\",\"id\":" jadd
  lsp-cid @ jnum
  s\" ,\"method\":\"textDocument/signatureHelp\",\"params\":{\"textDocument\":{\"uri\":" jadd
  lsp-uri lsp-uri-len @ jstr
  s\" },\"position\":{\"line\":" jadd
  lc-cl @ jnum
  s\" ,\"character\":" jadd
  lc-ch @ jnum
  s\" }}}" jadd
  jbuf @ jlen @ frame-send
  lsp-cid @ idbuf-set
  0 hover-len !
  20 0 do
    frame-block ?dup 0= if leave then
    fr-n !
    rbuf @ fr-n @ idbuf idbuf-len @ search nip nip if
      fr-n @ lsp-parse-signature
      leave
    then
    rbuf @ fr-n @ s" textDocument/publishDiagnostics" search nip nip if
      fr-n @ lsp-parse-diagnostics
    then
  loop
  hover-len @ 0= if s" lsp: no signature" msg! exit then
  hover-buf hover-len @  text-cols @ 1- min  msg! ;

\ exit cleanup: shutdown/exit notifications + close pipes + kill as a fallback
: lsp-close ( -- )
  lsp-open? @ 0= if exit then
  0 jlen !  s\" {\"jsonrpc\":\"2.0\",\"method\":\"shutdown\",\"params\":{}}" jadd  jbuf @ jlen @ frame-send
  0 jlen !  s\" {\"jsonrpc\":\"2.0\",\"method\":\"exit\",\"params\":{}}" jadd  jbuf @ jlen @ frame-send
  lsp-wfd @ ?dup if lsp-closefd drop then
  lsp-rfd @ ?dup if dup remove-watcher  lsp-closefd drop  then
  lsp-pid @ ?dup if 9 lsp-kill drop then
  0 lsp-open? !  0 lsp-ready? !  0 lsp-pending !  0 lsp-wfd !  0 lsp-rfd !  0 lsp-pid ! ;

' lsp-close shutdown-xt !
