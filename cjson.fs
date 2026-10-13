\ ===== cjson.fs — cJSON bindings (used for LSP JSON-RPC decoding)=====
\ the vendored cJSON.c/cJSON.h live in the project directory; the fe script stores the project directory in fe-incdir,
\ here add-incdir lets libcc's C compiler find cJSON.c.
require libcc.fs
also c-lib      \ add-incdir / c-flags are in the c-lib vocabulary (previous-ed away after require)

c-library cjson
  \ c-library calls clear-libs and clears c-flags, so add-incdir must come after c-library and before end-c-library
  [defined] fe-incdir [if]  fe-incdir 2@ add-incdir  [then]
  \c #include "vendor/cJSON.c"
  c-function cj-parse   cJSON_Parse a -- a
  c-function cj-delete  cJSON_Delete a -- void
  c-function cj-get     cJSON_GetObjectItemCaseSensitive a a -- a
  c-function cj-asize   cJSON_GetArraySize a -- n
  c-function cj-aitem   cJSON_GetArrayItem a n -- a
  c-function cj-str     cJSON_GetStringValue a -- a
  c-function c-strlen   strlen a -- n
  \c long long cj_strcpy(void *item, char *buf, long long cap) {
  \c   char *s = cJSON_GetStringValue(item);
  \c   if (!s) return 0;
  \c   long long len = strlen(s);
  \c   if (len > cap) len = cap;
  \c   memcpy(buf, s, len);
  \c   return len;
  \c }
  c-function cj-strcpy cj_strcpy a a n -- n
  c-function cj-num     cJSON_GetNumberValue a -- r
  c-function cj-is-str  cJSON_IsString a -- n
  c-function cj-is-num  cJSON_IsNumber a -- n
  c-function cj-is-arr  cJSON_IsArray a -- n
  c-function cj-is-obj  cJSON_IsObject a -- n
  c-function cj-is-null cJSON_IsNull a -- n
end-c-library

\ key buffer: cJSON key names need a NUL terminator (Gforth's s" is not NUL-terminated)
\ also used for long file-exists?/spawn paths (toolchain paths are long), so 1024 is reserved
create ck 1024 allot
: ck-set { a u -- }   \ copy (a u) into ck and append a NUL
  u 1023 min { n }  a ck n move  0 ck n + c! ;
