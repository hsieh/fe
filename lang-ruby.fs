\ ===== lang-ruby.fs — Ruby syntax rules =====
create rb-kwbuf 1024 allot  variable rb-kwlen 0 rb-kwlen !
create rb-fnbuf 512 allot  variable rb-fnlen 0 rb-fnlen !
: rb-init
  s"  alias and begin break case class def defined do else elsif end ensure false for if in module next nil not or redo rescue retry return self super then true undef unless until when while yield " rb-kwbuf 1024 rb-kwlen kw-put
  s"  puts print p require load raise lambda proc attr_accessor attr_reader attr_writer " rb-fnbuf 512 rb-fnlen kw-put ;
rb-init
create rb-hash 35 c,     \ #
create rb-dq 34 c,       \ "
create rb-sq 39 c,       \ '
: rb-kw$  rb-kwbuf rb-kwlen @ ;
: rb-fn$  rb-fnbuf rb-fnlen @ ;
: rules-ruby
  rules-clear
  rb-kw$          4 rule-kw
  rb-fn$          6 rule-kw
                  3 rule-num
  rb-hash 1 0 0   1 rule-range
  rb-dq 1 rb-dq 1 2 rule-range
  rb-sq 1 rb-sq 1 2 rule-range
                  0 rule-word ;
: register-language-ruby  s" rb" ['] rules-ruby register-lang  s" ruby" ['] rules-ruby register-lang-name ;
register-language-ruby
