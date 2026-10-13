\ ===== lang-rust.fs — Rust syntax rules (data from vis lua/lexers/rust.lua)=====
create rs-kwbuf 1024 allot  variable rs-kwlen 0 rs-kwlen !
create rs-tybuf 512 allot   variable rs-tylen 0 rs-tylen !
create rs-fnbuf 512 allot   variable rs-fnlen 0 rs-fnlen !
: rs-init
  s"  as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type union unsafe use where while " rs-kwbuf 1024 rs-kwlen kw-put
  s"  array bool char f32 f64 i8 i16 i32 i64 i128 isize pointer reference slice str tuple u8 u16 u32 u64 u128 unit usize String Vec Option Result Box " rs-tybuf 512 rs-tylen kw-put
  s"  assert assert_eq assert_ne cfg column compile_error concat dbg debug_assert env eprint eprintln file format format_args include include_bytes include_str line matches module_path option_env panic print println stringify thread_local todo unimplemented unreachable vec write writeln " rs-fnbuf 512 rs-fnlen kw-put ;
rs-init
create rs-dslash  47 c, 47 c,          \ //
create rs-dslash* 47 c, 42 c,          \ /*
create rs-*slash  42 c, 47 c,          \ */
create rs-dq      34 c,                \ "
create rs-sq      39 c,                \ '
: rs-kw$  rs-kwbuf rs-kwlen @ ;
: rs-ty$  rs-tybuf rs-tylen @ ;
: rs-fn$  rs-fnbuf rs-fnlen @ ;
: rules-rust
  rules-clear
  rs-kw$               4 rule-kw
  rs-ty$               5 rule-kw
  rs-fn$               6 rule-kw
                       3 rule-num
  rs-dslash 2  0 0     1 rule-range     \ // line comment
  rs-dslash* 2  rs-*slash 2  1 rule-range \ /* */ block comment
  rs-dq 1  rs-dq 1     2 rule-range     \ " string
  rs-sq 1  rs-sq 1     2 rule-range     \ ' character
                       0 rule-word ;
: register-language-rust  s" rs" ['] rules-rust register-lang  s" rust" ['] rules-rust register-lang-name ;
register-language-rust
