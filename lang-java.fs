\ ===== lang-java.fs — Java syntax rules (data from vis lua/lexers/java.lua)=====
create jv-kwbuf 1024 allot  variable jv-kwlen 0 jv-kwlen !
create jv-tybuf 512 allot   variable jv-tylen 0 jv-tylen !
create jv-cobuf 512 allot   variable jv-colen 0 jv-colen !
: jv-init
  s"  abstract assert break case catch class const continue default do else enum extends final finally for goto if implements import instanceof interface native new package private protected public return static strictfp super switch synchronized this throw throws transient try while volatile " jv-kwbuf 1024 jv-kwlen kw-put
  s"  boolean byte char double float int long short void String Object Integer Long Short Byte Character Double Float Boolean Class Enum " jv-tybuf 512 jv-tylen kw-put
  s"  true false null " jv-cobuf 512 jv-colen kw-put ;
jv-init
create jv-dslash  47 c, 47 c,          \ //
create jv-dslash* 47 c, 42 c,          \ /*
create jv-*slash  42 c, 47 c,          \ */
create jv-dq      34 c,                \ "
create jv-sq      39 c,                \ '
: jv-kw$  jv-kwbuf jv-kwlen @ ;
: jv-ty$  jv-tybuf jv-tylen @ ;
: jv-co$  jv-cobuf jv-colen @ ;
: rules-java
  rules-clear
  jv-kw$               4 rule-kw
  jv-ty$               5 rule-kw
  jv-co$               7 rule-kw
                       3 rule-num
  jv-dslash 2  0 0     1 rule-range     \ // line comment
  jv-dslash* 2  jv-*slash 2  1 rule-range \ /* */ block comment
  jv-dq 1  jv-dq 1     2 rule-range     \ " string
  jv-sq 1  jv-sq 1     2 rule-range     \ ' character
                       0 rule-word ;
: register-language-java  s" java" ['] rules-java register-lang  s" java" ['] rules-java register-lang-name ;
register-language-java
