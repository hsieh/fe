\ ===== state.fs — window/buffer context and field access (the foundation for multiple windows/buffers)=====
\ Two contexts: wctx = current window, gctx = current buffer.
\ Field offsets are auto-computed by the local fld: helper (a running offset), not hardcoded.
\ B:NAME's size is configurable via the FE_PATH_MAX environment variable (default 512, 8-aligned, [8,4096]).
\ field-access words return "the address of that field in the current context", so call sites of X @ / X ! / X +! all stay unchanged.

variable wctx  0 wctx !
variable gctx  0 gctx !

defer edit-touch-xt                        \ ( -- ) called after every edit (the custom layer uses it to clear stale diagnostics)
' noop is edit-touch-xt
defer buffer-free-xt                       \ ( b -- ) called before a buffer is freed (the custom layer cleans up per-buffer resources)
' noop is buffer-free-xt

\ --- buffer flags ---
1 constant BF_SYSTEM     \ system buffer (not user-visible)
2 constant BF_READONLY
4 constant BF_SAVELOG
8 constant BF_NOFILE     \ not file-backed (no associated file: no save/prompt/modified display)

\ --- configurable name-field size (B:NAME) ---
variable path-max  512 path-max !          \ default 512 (name field size)
: path-max-init ( -- )
  s" FE_PATH_MAX" getenv  dup if
    s>number? if  d>s  8 max  4096 min  path-max !  else  2drop  then
  else
    2drop
  then ;
path-max-init

\ --- field helper: define <name> as a constant = the current offset, then advance by <size> ---
variable fld-off  0 fld-off !
: fld: ( size "name" -- )
  fld-off @  over  fld-off +!  constant ;

\ --- Window fields (view) ---
0 fld-off !
8 fld: W:B
8 fld: W:CURSOR
8 fld: W:WANTCOL
8 fld: W:TOPLINE
8 fld: W:TOPSUB
8 fld: W:FLAGS
8 fld: W:X1
8 fld: W:Y1
8 fld: W:X2
8 fld: W:Y2
8 fld: W:NEXT
8 fld: W:LEFT      \ split node: left/top child; unused by leaves
8 fld: W:RIGHT     \ split node: right/bottom child; unused by leaves
8 fld: W:KIND      \ 0=leaf 1=vertical split 2=horizontal split
8 fld: W:PARENT    \ parent node (0 = tree root)
8 fld: W:LINENR    \ line-number mode (-1=uninitialized; used by per-window scope)
8 fld: W:MODE      \ current mode (per-window)
8 fld: W:PREVMODE  \ prefix-mode stack (per-window)
fld-off @ constant /WIN

\ --- Buffer fields (content + metadata + saved view) ---
0 fld-off !
8 fld: B:GBUF
8 fld: B:GCAP
8 fld: B:GFRONT
8 fld: B:GBACK
8 fld: B:FLAGS
8 fld: B:MODIFIED
8 fld: B:EOLCRLF
8 fld: B:NLINES
8 fld: B:NLINESSZ
8 fld: B:LIX
8 fld: B:LIXCAP
8 fld: B:LIXN
8 fld: B:LIXDIRTY
8 fld: B:URECS
8 fld: B:UNREC
8 fld: B:URP
8 fld: B:UBROKEN
8 fld: B:SVCURSOR
8 fld: B:SVTOPLINE
8 fld: B:SVTOPSUB
8 fld: B:SVWANTCOL
8 fld: B:SVVALID
8 fld: B:NEXT
8 fld: B:NAMELEN
8 fld: B:FNAME
8 fld: B:FLEN
path-max @ 7 + -8 and  fld: B:NAME   \ name array (configurable size, 8-aligned)
8 fld: B:LEPOS     \ incremental line index: edit start
8 fld: B:LEDELTA   \ net byte change
8 fld: B:LENL      \ net newline-count change
8 fld: B:LEPEND    \ number of pending edits
8 fld: B:LECMPLX   \ complex edit (needs a full rebuild)
8 fld: B:LINENR    \ line-number mode (-1=uninitialized, defaults by filetype on first use)
8 fld: B:SAVEDURP  \ undo pointer urp at save time (-1 = content differs from disk and there is no undo save point)
8 fld: B:SYNTAX    \ syntax-highlight override: -1=follow global 0=force off 1=force on
8 fld: B:LANGOVR   \ language override: 0=automatic; otherwise = rule-word xt (forced by :syntax)
8 fld: B:NDIAGS    \ LSP diagnostics: active count for this buffer
8 fld: B:DIAGS     \ LSP diagnostics: pointer to the 4-array block (line/b0/b1/sev), 0 = none (lazy)
8 fld: B:URI       \ cached absolute file:// URI (pointer, 0 = not built); freed on buffer-free, invalidated on rename
8 fld: B:URILEN    \ cached URI length
fld-off @ constant /BUF

\ --- window field-access words ---
: cursor     wctx @ W:CURSOR   + ;
: want-col   wctx @ W:WANTCOL  + ;
: top-line   wctx @ W:TOPLINE  + ;
: top-sub    wctx @ W:TOPSUB   + ;
: win-linenr wctx @ W:LINENR   + ;

\ --- buffer field-access words ---
: g-buf      gctx @ B:GBUF     + ;
: g-cap      gctx @ B:GCAP     + ;
: g-front    gctx @ B:GFRONT   + ;
: g-back     gctx @ B:GBACK    + ;
: bflags     gctx @ B:FLAGS    + ;
: modified   gctx @ B:MODIFIED + ;
: eol-crlf   gctx @ B:EOLCRLF  + ;
: nlines     gctx @ B:NLINES   + ;
: nlines-sz  gctx @ B:NLINESSZ + ;
: lix        gctx @ B:LIX      + ;
: lix-cap    gctx @ B:LIXCAP   + ;
: lix-n      gctx @ B:LIXN     + ;
: lix-dirty  gctx @ B:LIXDIRTY + ;
: lix-epos    gctx @ B:LEPOS   + ;
: lix-edelta  gctx @ B:LEDELTA + ;
: lix-enl     gctx @ B:LENL    + ;
: lix-epend   gctx @ B:LEPEND  + ;
: lix-ecomplex gctx @ B:LECMPLX + ;
: linenr      gctx @ B:LINENR  + ;
: saved-urp   gctx @ B:SAVEDURP + ;
: urecs      gctx @ B:URECS    + ;
: unrec      gctx @ B:UNREC    + ;
: urp        gctx @ B:URP      + ;
: ubroken    gctx @ B:UBROKEN  + ;
: fname      gctx @ B:FNAME    + ;
: flen       gctx @ B:FLEN     + ;
