\ ===== lang-bash.fs — Shell/Bash syntax rules (data from vis lua/lexers/bash.lua)=====
create sh-kwbuf 512 allot  variable sh-kwlen 0 sh-kwlen !
create sh-fnbuf 512 allot  variable sh-fnlen 0 sh-fnlen !
: sh-init
  s"  if then elif else fi time for in until while do done case esac coproc select function " sh-kwbuf 512 sh-kwlen kw-put
  s"  break cd continue eval exec exit export getopts hash pwd readonly return shift test times trap umask unset alias bind builtin caller command declare echo enable help let local logout mapfile printf read readarray source type typeset ulimit unalias " sh-fnbuf 512 sh-fnlen kw-put ;
sh-init
create sh-hash  35 c,                 \ #
create sh-dq    34 c,                 \ "
create sh-sq    39 c,                 \ '
: sh-kw$  sh-kwbuf sh-kwlen @ ;
: sh-fn$  sh-fnbuf sh-fnlen @ ;
: rules-bash
  rules-clear
  sh-kw$              4 rule-kw
  sh-fn$              6 rule-kw
                      3 rule-num
  sh-hash 1  0 0      1 rule-range    \ # comment
  sh-dq 1  sh-dq 1    2 rule-range    \ " string
  sh-sq 1  sh-sq 1    2 rule-range    \ ' string
                      0 rule-word ;
: register-language-bash  s" sh" ['] rules-bash register-lang  s" bash" ['] rules-bash register-lang-name ;
register-language-bash
