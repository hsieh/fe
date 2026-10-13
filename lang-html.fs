\ ===== lang-html.fs — HTML syntax rules (v2: regex-matched tag names + comments/strings)=====
\ R-REGEX: anchored regex matches at the current position (tag/attribute names can be expressed as regexes)
create html-dashlb 60 c, 33 c, 45 c, 45 c,    \ <!--
create html-rb     45 c, 45 c, 62 c,          \ -->
create html-dq     34 c,                       \ "
create html-sq     39 c,                       \ '
: rules-html
  rules-clear
                      3 rule-num
  s" </?[a-zA-Z][a-zA-Z0-9]*" 5 rule-regex    \ tag name (<div </div <script)
  html-dashlb 4 html-rb 3  1 rule-range       \ <!-- --> comment
  html-dq 1 html-dq 1      2 rule-range       \ " attribute string
  html-sq 1 html-sq 1      2 rule-range       \ ' attribute string
                      0 rule-word ;
: register-language-html  s" html" ['] rules-html register-lang  s" html" ['] rules-html register-lang-name ;
register-language-html
