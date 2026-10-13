\ ===== lang-markdown.fs — Markdown syntax rules (v2, based on vis lexers/markdown.lua)=====
\ vis categories: HEADING/BOLD/ITALIC/LINK/CODE/STRING(quote)/LIST/REFERENCE.
\ the fe engine only supports range/range-wb/regex; colors map to the 8 preset styles.

create md-star1   42 c,                \ *
create md-star    42 c, 42 c,          \ **
create md-tick    96 c,                \ `
create md-tick3   96 c, 96 c, 96 c,    \ ```
create md-dashlb  60 c, 33 c, 45 c, 45 c,  \ <!--
create md-rb      45 c, 45 c, 62 c,    \ -->

\ line-start block elements (^ + R-REGEX matches only at si=0; matches to end of line)
create md-hd-pat 32 allot
variable md-hd-plen 0 md-hd-plen !
variable md-hd-re 0 md-hd-re !
create md-qt-pat 32 allot
variable md-qt-plen 0 md-qt-plen !
variable md-qt-re 0 md-qt-re !
create md-li-pat 64 allot
variable md-li-plen 0 md-li-plen !
variable md-li-re 0 md-li-re !
: md-re-init
  s" ^#{1,6}[[:space:]].*" { pa1 pl1 }
    pa1 md-hd-pat pl1 move  pl1 md-hd-plen !  pa1 pl1 0 re-anchor-compile md-hd-re !
  s" ^>[[:space:]]?.*" { pa2 pl2 }
    pa2 md-qt-pat pl2 move  pl2 md-qt-plen !  pa2 pl2 0 re-anchor-compile md-qt-re !
  s" ^([-+*]|[0-9]+\\.)[[:space:]].*" { pa3 pl3 }
    pa3 md-li-pat pl3 move  pl3 md-li-plen !  pa3 pl3 0 re-anchor-compile md-li-re ! ;
md-re-init

\ style mapping: heading=4(Blue Bold) quote=2(Green, vis STRING) list=6(Yellow)
\            code=2(Green) bold=7(Cyan Bold) italic=5(Cyan) comment=1(Cyan)
: rules-markdown
  rules-clear
  R-REGEX  md-hd-pat md-hd-plen  md-hd-re @  0  4  rule   \ heading
  R-REGEX  md-qt-pat md-qt-plen  md-qt-re @  0  2  rule   \ quote >
  R-REGEX  md-li-pat md-li-plen  md-li-re @  0  6  rule   \ list -/*/number.
  md-tick3 3  md-tick3 3  2 rule-range   \ ``` code block (may span lines)
  md-tick  1  md-tick  1  2 rule-range   \ ` inline code
  md-star  2  md-star  2  7 rule-range   \ ** bold
  md-star1 1  md-star1 1  5 rule-range   \ * italic
  md-dashlb 4  md-rb 3     1 rule-range   \ <!-- HTML comment
                          0 rule-word ;
: register-language-markdown
  s" md" ['] rules-markdown register-lang
  s" markdown" ['] rules-markdown register-lang-name ;
register-language-markdown
