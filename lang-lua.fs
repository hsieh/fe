\ ===== lang-lua.fs — Lua syntax rules (data from vis lua/lexers/lua.lua)=====
create lua-kwbuf 512 allot  variable lua-kwlen 0 lua-kwlen !
create lua-fnbuf 512 allot  variable lua-fnlen 0 lua-fnlen !
: lua-init
  s"  and break do else elseif end false for function if in local nil not or repeat return then true until while goto " lua-kwbuf 512 lua-kwlen kw-put
  s"  assert collectgarbage dofile error getmetatable ipairs load loadfile next pairs pcall print rawequal rawget rawlen rawset require select setmetatable tonumber tostring type xpcall " lua-fnbuf 512 lua-fnlen kw-put ;
lua-init
create lua-dash   45 c, 45 c,          \ --
create lua-dashlb 45 c, 45 c, 91 c, 91 c,  \ --[[
create lua-rb     93 c, 93 c,          \ ]]
create lua-dq     34 c,                \ "
create lua-sq     39 c,                \ '
: lua-kw$  lua-kwbuf lua-kwlen @ ;
: lua-fn$  lua-fnbuf lua-fnlen @ ;
: rules-lua
  rules-clear
  lua-kw$              4 rule-kw
  lua-fn$              6 rule-kw
                       3 rule-num
  lua-dashlb 4  lua-rb 2  1 rule-range  \ --[[ ]] block comment
  lua-dash 2  0 0        1 rule-range   \ -- line comment
  lua-dq 1  lua-dq 1     2 rule-range   \ " string
  lua-sq 1  lua-sq 1     2 rule-range   \ ' string
                       0 rule-word ;
: register-language-lua  s" lua" ['] rules-lua register-lang  s" lua" ['] rules-lua register-lang-name ;
register-language-lua
