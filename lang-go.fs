\ ===== lang-go.fs — Go syntax rules (data from vis lua/lexers/go.lua)=====
create g-dslash  47 c, 47 c,          \ //
create g-dslash* 47 c, 42 c,          \ /*
create g-*slash  42 c, 47 c,          \ */
create g-dq      34 c,                \ "
create g-bq      96 c,                \ ` raw string
\ keyword table: use a persistent buffer (created with create), not `: word s" ..." ;`
\ — that style returns a temporary buffer; successive calls overwrite each other, and storing it into rules leaves a wild pointer.
256 constant g-kwmax
create g-kwbuf  g-kwmax allot  variable g-kwlen  0 g-kwlen !
create g-tybuf  g-kwmax allot  variable g-tylen  0 g-tylen !
create g-fnbuf  g-kwmax allot  variable g-fnlen  0 g-fnlen !
create g-cobuf  g-kwmax allot  variable g-colen  0 g-colen !
: go-put ( a u buf lenvar -- )               \ copy a,u into buf, record the length into lenvar
  >r  swap  g-kwmax min  dup  r> !  move ;
: go-init
  s"  break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var "  g-kwbuf g-kwlen go-put
  s"  any bool byte comparable complex128 complex64 error float32 float64 int int16 int32 int64 int8 rune string uint uint16 uint32 uint64 uint8 uintptr "  g-tybuf g-tylen go-put
  s"  append cap clear close complex copy delete imag len make max min new panic print println real recover "  g-fnbuf g-fnlen go-put
  s"  true false iota nil "  g-cobuf g-colen go-put ;
go-init
: g-kw$     g-kwbuf g-kwlen @ ;
: g-ty$     g-tybuf g-tylen @ ;
: g-fn$     g-fnbuf g-fnlen @ ;
: g-co$     g-cobuf g-colen @ ;
: rules-go
  rules-clear
  g-kw$                   4 rule-kw
  g-ty$                   5 rule-kw
  g-fn$                   6 rule-kw
  g-co$                   7 rule-kw
                          3 rule-num
  g-dslash  2  0 0        1 rule-range
  g-dslash* 2  g-*slash 2 1 rule-range
  g-dq  1  g-dq 1         2 rule-range
  g-bq  1  g-bq 1         2 rule-range
                          0 rule-word ;
\ line-start directives of go.mod / go.work / go.sum (Go module syntax, not Go language keywords).
\ directives only match at the line start: use a ^ regex + R-REGEX; try-regex matches a ^-prefixed pattern only at si=0,
\ so go/require fragments inside dependency paths (the go in github.com/go-redis/...) are not falsely highlighted.
create gm-pl-pat 128 allot
variable gm-pl-plen 0 gm-pl-plen !
variable gm-pl-re 0 gm-pl-re !
: gm-pl-init
  s" ^(module|go|require|replace|exclude|retract|toolchain|godebug|use)[[:space:]]" { pa pl }
  pa gm-pl-pat pl move  pl gm-pl-plen !
  pa pl 0 re-anchor-compile gm-pl-re ! ;
gm-pl-init
\ go.mod syntax: line-start directives + // comments + "strings"; no block comments / raw strings / numbers (version strings vX.Y.Z are not code numbers, so no number highlighting)
: rules-gomod
  rules-clear
  R-REGEX  gm-pl-pat gm-pl-plen  gm-pl-re @  0  4  rule
  g-dslash  2  0 0        1 rule-range
  g-dq  1  g-dq 1         2 rule-range
                          0 rule-word ;
: register-language-go
  s" go" ['] rules-go register-lang
  s" go"    ['] rules-go    register-lang-name
  s" gomod" ['] rules-gomod register-lang-name
  s" go.mod"  ['] rules-gomod register-filename
  s" go.work" ['] rules-gomod register-filename
  s" go.sum"  ['] rules-gomod register-filename ;
register-language-go
