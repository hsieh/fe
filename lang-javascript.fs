\ ===== lang-javascript.fs — JavaScript syntax rules (data from vis lua/lexers/javascript.lua)=====
create js-kwbuf 1024 allot  variable js-kwlen 0 js-kwlen !
create js-tybuf 512 allot   variable js-tylen 0 js-tylen !
create js-fnbuf 512 allot   variable js-fnlen 0 js-fnlen !
: js-init
  s"  abstract async await boolean break byte case catch char class const continue debugger default delete do double else enum export extends false final finally float for function get goto if implements import in instanceof int interface let long native new null of package private protected public return set short static super switch synchronized this throw throws transient true try typeof var void volatile while with yield " js-kwbuf 1024 js-kwlen kw-put
  s"  Object Function Boolean Symbol Error AggregateError Array ArrayBuffer AsyncFunction BigInt BigInt64Array BigInt128Array DataView Date FinalizationRegistry Float32Array Float64Array Generator Map Math Number Promise Proxy RegExp Set String WeakMap WeakRef WeakSet " js-tybuf 512 js-tylen kw-put
  s"  console alert prompt confirm document window parseInt parseFloat isNaN isFinite encodeURI decodeURI encodeURIComponent decodeURIComponent JSON " js-fnbuf 512 js-fnlen kw-put ;
js-init
create js-dslash  47 c, 47 c,          \ //
create js-dslash* 47 c, 42 c,          \ /*
create js-*slash  42 c, 47 c,          \ */
create js-dq      34 c,                \ "
create js-sq      39 c,                \ '
create js-bq      96 c,                \ `
: js-kw$  js-kwbuf js-kwlen @ ;
: js-ty$  js-tybuf js-tylen @ ;
: js-fn$  js-fnbuf js-fnlen @ ;
: rules-javascript
  rules-clear
  js-kw$               4 rule-kw
  js-ty$               5 rule-kw
  js-fn$               6 rule-kw
                       3 rule-num
  js-dslash 2  0 0     1 rule-range     \ // line comment
  js-dslash* 2  js-*slash 2  1 rule-range \ /* */ block comment
  js-dq 1  js-dq 1     2 rule-range     \ " string
  js-sq 1  js-sq 1     2 rule-range     \ ' string
  js-bq 1  js-bq 1     2 rule-range     \ ` template string
                       0 rule-word ;
: register-language-javascript  s" js" ['] rules-javascript register-lang  s" javascript" ['] rules-javascript register-lang-name ;
register-language-javascript
