\ ===== state.fs — window/buffer context and field access (the foundation for multiple windows/buffers)=====
\ Two contexts: wctx = current window, gctx = current buffer.
\ field-access words return "the address of that field in the current context", so call sites of X @ / X ! / X +! all stay unchanged.
\ with a single window wctx has only one instance; for multiple windows, wctx just needs a linked list/geometry.

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

\ --- Window fields (view) ---
 0 constant W:B
 8 constant W:CURSOR
16 constant W:WANTCOL
24 constant W:TOPLINE
32 constant W:TOPSUB
40 constant W:FLAGS
48 constant W:X1
56 constant W:Y1
64 constant W:X2
72 constant W:Y2
80 constant W:NEXT
88 constant W:LEFT      \ split node: left/top child; unused by leaves
96 constant W:RIGHT     \ split node: right/bottom child; unused by leaves
104 constant W:KIND      \ 0=leaf 1=vertical split 2=horizontal split
112 constant W:PARENT    \ parent node (0 = tree root)
120 constant W:LINENR    \ line-number mode (-1=uninitialized; used by per-window scope)
128 constant W:MODE      \ current mode (per-window)
136 constant W:PREVMODE  \ prefix-mode stack (per-window)
144 constant /WIN

\ --- Buffer fields (content + metadata + saved view) ---
  0 constant B:GBUF
  8 constant B:GCAP
 16 constant B:GFRONT
 24 constant B:GBACK
 32 constant B:FLAGS
 40 constant B:MODIFIED
 48 constant B:EOLCRLF
 56 constant B:NLINES
 64 constant B:NLINESSZ
 72 constant B:LIX
 80 constant B:LIXCAP
 88 constant B:LIXN
 96 constant B:LIXDIRTY
104 constant B:URECS
112 constant B:UNREC
120 constant B:URP
128 constant B:UBROKEN
136 constant B:SVCURSOR
144 constant B:SVTOPLINE
152 constant B:SVTOPSUB
160 constant B:SVWANTCOL
168 constant B:SVVALID
176 constant B:NEXT
184 constant B:NAMELEN
192 constant B:FNAME
200 constant B:FLEN
208 constant B:NAME
720 constant B:LEPOS     \ incremental line index: edit start (B:NAME enlarged to 512 for long paths)
728 constant B:LEDELTA   \ net byte change
736 constant B:LENL      \ net newline-count change
744 constant B:LEPEND    \ number of pending edits
752 constant B:LECMPLX   \ complex edit (needs a full rebuild)
760 constant B:LINENR    \ line-number mode (-1=uninitialized, defaults by filetype on first use)
768 constant B:SAVEDURP  \ undo pointer urp at save time (-1 = content differs from disk and there is no undo save point)
776 constant B:SYNTAX    \ syntax-highlight override: -1=follow global 0=force off 1=force on
784 constant B:LANGOVR   \ language override: 0=automatic; otherwise = rule-word xt (forced by :syntax)
792 constant B:NDIAGS    \ LSP diagnostics: active count for this buffer
800 constant B:DIAGS     \ LSP diagnostics: pointer to the 4-array block (line/b0/b1/sev), 0 = none (lazy)
808 constant B:URI       \ cached absolute file:// URI (pointer, 0 = not built); freed on buffer-free, invalidated on rename
816 constant B:URILEN    \ cached URI length
824 constant /BUF

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
