\ ===== lang-c.fs — C syntax rules (data from vis lua/lexers/c.lua)=====
\ engine in syntax.fs; style indexes in SI-* (1=comment 2=string 3=number 4=keyword 5=type 6=builtin 7=constant 8=preprocessor)
create c-dslash  47 c, 47 c,          \ //
create c-dslash* 47 c, 42 c,          \ /*
create c-*slash  42 c, 47 c,          \ */
create c-dq      34 c,                \ "
create c-sq      39 c,                \ '
create c-hash    35 c,                \ #
: c-kw$     s"  _Atomic _Generic _Static_assert __asm __asm__ __attribute__ __declspec __inline __inline__ __restrict__ alignas alignof asm auto break case const continue default do else enum extern false for goto if inline noreturn register restrict return sizeof static switch thread_local true typedef volatile while " ;
: c-ty$     s"  FILE _Bool _Complex _Imaginary bool char clock_t complex div_t double float fpos_t imaginary int int16_t int32_t int64_t int8_t int_fast16_t int_fast32_t int_fast64_t int_fast8_t int_least16_t int_least32_t int_least64_t int_least8_t intmax_t intptr_t lconv ldiv_t long max_align_t ptrdiff_t short signed size_t struct time_t timespec tm uint16_t uint32_t uint64_t uint8_t uint_fast16_t uint_fast32_t uint_fast64_t uint_fast8_t uint_least16_t uint_least32_t uint_least64_t uint_least8_t uintmax_t uintptr_t union unsigned va_list void " ;
: c-fn$     s"  CMPLX INT16_C INT32_C INT64_C INT8_C INTMAX_C UINT16_C UINT32_C UINT64_C UINT8_C UINTMAX_C _Exit abort abs acos acosh aligned_alloc asctime asin asinh assert at_quick_exit atan atan2 atanh atexit atof atoi atol bsearch cabs cacos cacosh calloc carg casin casinh catan catanh cbrt ccos ccosh ceil cexp cimag clearerr clock conj copysign cos cosh cpow cproj creal csin csinh ctan ctanh ctime difftime div erf erfc exit exp exp2 expm1 fabs fclose fdim feof ferror fflush fgetc fgetpos fgets floor fma fmax fmin fmod fopen fprintf fputc fread free freopen frexp fscanf fseek fsetpos ftell fwide fwrite getc getchar getenv gets gmtime hypot ilogb isalnum isalpha isblank iscntrl isdigit isfinite isgraph isgreater isgreaterequal isinf isless islessequal islessgreater islower isnan isnormal isprint ispunct isspace isunordered isupper isxdigit ldexp lgamma localeconv localtime log log10 log1p log2 logb longjmp malloc mblen mbrlen mbsinit memchr memcmp memcpy memmove memset mktime modf nan nearbyint nextafter nexttoward perror pow printf putc putchar puts qsort quick_exit raise rand realloc remainder remove remquo rename rewind rint round scalbn scanf setbuf setjmp setlocale setvbuf signal signbit sin sinh sprintf sqrt srand sscanf strcat strchr strcmp strcoll strcpy strcspn strerror strlen strncat strncmp strncpy strpbrk strrchr strspn strstr strtod strtoimax strtok strtol strtoul strtoumax strxfrm system tan tanh tgamma time timespec_get tmpfile tmpnam tolower toupper trunc ungetc va_arg va_end va_start vfprintf vprintf vsprintf " ;
: c-co$     s"  BUFSIZ CHAR_BIT CHAR_MAX CHAR_MIN CLOCKS_PER_SEC DBL_MAX DBL_MIN E2BIG EACCES EADDRINUSE EADDRNOTAVAIL EAFNOSUPPORT EAGAIN EALREADY EBADF EBADMSG EBUSY ECANCELED ECHILD ECONNABORTED ECONNREFUSED ECONNRESET EDEADLK EDESTADDRREQ EDOM EDQUOT EEXIST EFAULT EFBIG EHOSTUNREACH EIDRM EILSEQ EINPROGRESS EINTR EINVAL EIO EISCONN EISDIR ELOOP EMFILE EMLINK EMSGSIZE EMULTIHOP ENAMETOOLONG ENETDOWN ENETRESET ENETUNREACH ENFILE ENOBUFS ENODATA ENODEV ENOENT ENOEXEC ENOLCK ENOLINK ENOMEM ENOMSG ENOPROTOOPT ENOSPC ENOSR ENOSTR ENOSYS ENOTCONN ENOTDIR ENOTEMPTY ENOTRECOVERABLE ENOTSOCK ENOTSUP ENOTTY ENXIO EOF EOPNOTSUPP EOVERFLOW EOWNERDEAD EPERM EPIPE EPROTO EPROTONOSUPPORT EPROTOTYPE ERANGE EROFS ESPIPE ESRCH ESTALE ETIME ETIMEDOUT ETXTBSY EWOULDBLOCK EXDEV EXIT_FAILURE EXIT_SUCCESS FILENAME_MAX FLT_MAX FLT_MIN FOPEN_MAX HUGE_VAL INFINITY INT16_MAX INT16_MIN INT32_MAX INT32_MIN INT64_MAX INT64_MIN INT8_MAX INT8_MIN INTMAX_MAX INTMAX_MIN INTPTR_MAX INTPTR_MIN INT_FAST16_MAX INT_FAST16_MIN INT_FAST32_MAX INT_FAST32_MIN INT_FAST64_MAX INT_FAST64_MIN INT_FAST8_MAX INT_FAST8_MIN INT_LEAST16_MAX INT_LEAST16_MIN INT_LEAST32_MAX INT_LEAST32_MIN INT_LEAST64_MAX INT_LEAST64_MIN INT_LEAST8_MAX INT_LEAST8_MIN INT_MAX INT_MIN LC_ALL LC_COLLATE LC_CTYPE LC_MONETARY LC_NUMERIC LC_TIME LDBL_MAX LDBL_MIN LLONG_MIN LONG_MAX LONG_MIN MB_LEN_MAX NAN NULL PTRDIFF_MAX PTRDIFF_MIN RAND_MAX SCHAR_MAX SCHAR_MIN SEEK_CUR SEEK_END SEEK_SET SHRT_MAX SHRT_MIN SIGABRT SIGFPE SIGILL SIGINT SIGSEGV SIGTERM SIG_ATOMIC_MAX SIG_ATOMIC_MIN SIG_DFL SIG_ERR SIG_IGN SIZE_MAX TMP_MAX UCHAR_MAX UINT16_MAX UINT32_MAX UINT64_MAX UINT8_MAX UINTMAX_MAX UINTPTR_MAX UINT_FAST16_MAX UINT_FAST32_MAX UINT_FAST64_MAX UINT_FAST8_MAX UINT_LEAST16_MAX UINT_LEAST32_MAX UINT_LEAST64_MAX UINT_LEAST8_MAX UINT_MAX ULLONG_MAX ULONG_MAX USHRT_MAX WCHAR_MAX WCHAR_MIN WINT_MAX WINT_MIN _IOFBF _IOLBF _IONBF __DATE__ __FILE__ __LINE__ __TIME__ __func__ errno stderr stdin stdout " ;
: rules-c
  rules-clear
  c-kw$                   4 rule-kw
  c-ty$                   5 rule-kw
  c-fn$                   6 rule-kw
  c-co$                   7 rule-kw
                          3 rule-num
  c-dslash  2  0 0        1 rule-range     \ // line comment
  c-dslash* 2  c-*slash 2 1 rule-range     \ /* */ block comment (may span lines)
  c-dq  1  c-dq 1         2 rule-range     \ " ... "
  c-sq  1  c-sq 1         2 rule-range     \ ' ... '
  c-hash  1  0 0          8 rule-range     \ # to end of line (whole-line preprocessor)
                          0 rule-word ;
: register-language-c
  s" c" ['] rules-c register-lang
  s" c" ['] rules-c register-lang-name
  s" h" ['] rules-c register-lang ;
register-language-c
