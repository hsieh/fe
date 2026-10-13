\ ===== lang-python.fs — Python syntax rules (data from vis lua/lexers/python.lua)=====
\ engine in syntax.fs; style indexes in SI-*.
create py-kwbuf 1024 allot  variable py-kwlen 0 py-kwlen !
create py-fnbuf 2048 allot  variable py-fnlen 0 py-fnlen !
create py-cobuf 512 allot   variable py-colen 0 py-colen !
: py-init
  s"  and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield " py-kwbuf 1024 py-kwlen kw-put
  s"  abs all any ascii bin bool bytearray bytes callable chr classmethod compile complex delattr dict dir divmod enumerate eval exec filter float format frozenset getattr globals hasattr hash help hex id input int isinstance issubclass iter len list locals map max memoryview min next object oct open ord pow print property range repr reversed round set setattr slice sorted staticmethod str sum super tuple type vars zip __import__ " py-fnbuf 2048 py-fnlen kw-put
  s"  True False None Ellipsis NotImplemented " py-cobuf 512 py-colen kw-put ;
py-init
create py-hash  35 c,                 \ #
create py-dq    34 c,                 \ "
create py-sq    39 c,                 \ '
create py-tdq   34 c, 34 c, 34 c,     \ """
create py-tsq   39 c, 39 c, 39 c,     \ '''
: py-kw$  py-kwbuf py-kwlen @ ;
: py-fn$  py-fnbuf py-fnlen @ ;
: py-co$  py-cobuf py-colen @ ;
: rules-python
  rules-clear
  py-kw$               4 rule-kw
  py-fn$               6 rule-kw
  py-co$               7 rule-kw
                       3 rule-num
  py-hash 1  0 0       1 rule-range     \ # comment
  py-tdq 3  py-tdq 3   2 rule-range     \ """ multiline string
  py-tsq 3  py-tsq 3   2 rule-range     \ ''' multiline string
  py-dq 1  py-dq 1     2 rule-range     \ " string
  py-sq 1  py-sq 1     2 rule-range     \ ' string
                       0 rule-word ;
: register-language-python  s" py" ['] rules-python register-lang  s" python" ['] rules-python register-lang-name ;
register-language-python
