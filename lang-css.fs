\ ===== lang-css.fs — CSS syntax rules =====
create css-kwbuf 1024 allot  variable css-kwlen 0 css-kwlen !
: css-init
  s"  align-content align-items align-self animation background border bottom color display flex float font grid height justify-content left margin max-width min-width opacity overflow padding position right top transform transition width z-index " css-kwbuf 1024 css-kwlen kw-put ;
css-init
create css-dslash* 47 c, 42 c,    \ /*
create css-*slash  42 c, 47 c,    \ */
create css-dq 34 c,               \ "
create css-sq 39 c,               \ '
: css-kw$  css-kwbuf css-kwlen @ ;
: rules-css
  rules-clear
  css-kw$               5 rule-kw
                        3 rule-num
  css-dslash* 2 css-*slash 2 1 rule-range
  css-dq 1 css-dq 1     2 rule-range
  css-sq 1 css-sq 1     2 rule-range
                        0 rule-word ;
: register-language-css  s" css" ['] rules-css register-lang  s" css" ['] rules-css register-lang-name ;
register-language-css
