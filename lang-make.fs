\ ===== lang-make.fs — Makefile syntax rules =====
create mk-hash    35 c,                 \ #
create mk-dollar  36 c, 40 c,           \ $(
create mk-rp      41 c,                 \ )
create mk-dollar2 36 c, 123 c,          \ ${
create mk-rc      125 c,                \ }
: rules-makefile
  rules-clear
                      3 rule-num
  mk-hash 1 0 0       1 rule-range      \ # comment
  mk-dollar 2 mk-rp 1 7 rule-range      \ $(VAR)
  mk-dollar2 2 mk-rc 1 7 rule-range     \ ${VAR}
                      0 rule-word ;
: register-language-makefile
  s" mk" ['] rules-makefile register-lang  s" makefile" ['] rules-makefile register-lang-name
  s" Makefile"    ['] rules-makefile register-filename
  s" makefile"    ['] rules-makefile register-filename
  s" GNUmakefile" ['] rules-makefile register-filename ;
register-language-makefile
