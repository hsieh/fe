\ ===== regex.fs — POSIX regex (libcc) + contiguous snapshot of the gap buffer =====
\ Engine primitives, decoupled from search; any libcc call that needs "contiguous text" can use snap.
\ Used by search.fs; future R-REGEX syntax highlighting reuses this too.
\ Depends on gapbuf.fs (g-buf/g-front/g-back/g-cap/g-size, and the snap-dirty invalidation flag).

\ the gap buffer is non-contiguous but regex needs contiguous memory: invalidated on edit (gapbuf lix-touch sets snap-dirty), rebuilt on demand
variable snap 0 snap !
variable snap-cap 0 snap-cap !
variable snap-len 0 snap-len !

: snap-ensure { n -- }
  n snap-cap @ u<= if exit then
  snap @ if snap @ free throw then
  n 1+ 2* 64 max dup snap-cap ! allocate throw snap ! ;   \ +1 for the NUL terminator (re_ptr_match relies on it)

: snap-build
  snap-dirty @ 0= if exit then
  g-size snap-ensure
  g-size snap-len !
  g-buf @ snap @ g-front @ move
  g-buf @ g-cap @ + g-back @ - snap @ g-front @ + g-back @ move
  0 snap @ snap-len @ + c!    \ NUL terminator so regexec does not run past the end
  0 snap-dirty ! ;

require libcc.fs
c-library regex-c
  \c #include <regex.h>
  \c #include <stdlib.h>
  \c #include <string.h>
  \c #include <setjmp.h>
  \c #include <signal.h>
  \c #include <sys/time.h>
  \c static regex_t *re = 0;         /* unanchored: find the next one */
  \c static regex_t *re_anchor = 0;  /* anchored version: position match */
  \c static int anchor_in_pat = 0;   /* pattern contains ^ or $ (line anchoring) */
  \c static int caret_in_pat = 0;    /* pattern contains ^ (line start; must guard against a false match at a substring start) */
  \c static void re_free(regex_t **r) { if (*r) { regfree(*r); free(*r); *r = 0; } }
  \c static int re_line_start(char *text, int pos) { return pos == 0 || text[pos-1] == '\n'; }
  \c /* ReDoS protection: interrupt regexec after a 500ms timeout; after a timeout re_broken=1 until the next re_compile */
  \c static sigjmp_buf re_env;
  \c static volatile sig_atomic_t re_timed_out = 0;
  \c static volatile sig_atomic_t re_broken = 0;
  \c static void on_alarm(int sig) { re_timed_out = 1; siglongjmp(re_env, 1); }
  \c static void timeout_start(struct sigaction *old_sa, struct itimerval *old_tv) {
  \c   struct sigaction sa;
  \c   sa.sa_handler = on_alarm;
  \c   sigemptyset(&sa.sa_mask);
  \c   sa.sa_flags = 0;
  \c   sigaction(SIGALRM, &sa, old_sa);
  \c   struct itimerval tv;
  \c   tv.it_interval.tv_sec = 0; tv.it_interval.tv_usec = 0;
  \c   tv.it_value.tv_sec = 0; tv.it_value.tv_usec = 500000;
  \c   setitimer(ITIMER_REAL, &tv, old_tv);
  \c   re_timed_out = 0;
  \c }
  \c static void timeout_end(struct sigaction *old_sa, struct itimerval *old_tv) {
  \c   setitimer(ITIMER_REAL, old_tv, 0);
  \c   sigaction(SIGALRM, old_sa, 0);
  \c }
  \c long long re_compile(char *pat, int len, int icase) {
  \c   /* REG_NEWLINE: ^/$ = line start/end, . does not cross newlines (same as vim) */
  \c   int flags = REG_EXTENDED | REG_NEWLINE | (icase ? REG_ICASE : 0);
  \c   char *p = malloc(len+1); memcpy(p, pat, len); p[len] = 0;
  \c   re_broken = 0;
  \c   anchor_in_pat = 0; caret_in_pat = 0;
  \c   for (int i = 0; i < len; i++) {
  \c     if (pat[i] == '^') { anchor_in_pat = 1; caret_in_pat = 1; }
  \c     if (pat[i] == '$') anchor_in_pat = 1;
  \c   }
  \c   re_free(&re); re = malloc(sizeof(regex_t));
  \c   if (regcomp(re, p, flags)) { re_free(&re); free(p); return 0; }  /* 0 = invalid regex */
  \c   re_free(&re_anchor);
  \c   if (!anchor_in_pat) {
  \c     /* fast path: ^(...) anchored; without REG_NEWLINE, otherwise ^ would also match at line starts inside a substring */
  \c     re_anchor = malloc(sizeof(regex_t));
  \c     char *pa = malloc(len+4); pa[0]='^'; pa[1]='('; memcpy(pa+2, pat, len); pa[len+2]=')'; pa[len+3]=0;
  \c     int af = REG_EXTENDED | (icase ? REG_ICASE : 0);
  \c     if (regcomp(re_anchor, pa, af)) re_free(&re_anchor);
  \c     free(pa);
  \c   } else {
  \c     /* contains ^/$: as-is + REG_NEWLINE; find/match both use REG_STARTEND over the whole buffer to preserve line start/end semantics */
  \c     re_anchor = malloc(sizeof(regex_t));
  \c     if (regcomp(re_anchor, p, flags)) re_free(&re_anchor);
  \c   }
  \c   free(p);
  \c   return 1;
  \c }
  \c long long re_find_next(char *text, int len, int pos) {
  \c   if (!re || re_broken) return -2;
  \c   if (pos > len) pos = len;
  \c   struct sigaction old_sa; struct itimerval old_tv;
  \c   regmatch_t pm[1];
  \c   timeout_start(&old_sa, &old_tv);
  \c   if (sigsetjmp(re_env, 1) != 0) { timeout_end(&old_sa, &old_tv); re_broken = 1; return -2; }
  \c   long long result = -1;
  \c   if (anchor_in_pat) {
  \c     /* whole buffer + REG_STARTEND: ^/$ = real line start/end.
  \c        REG_STARTEND treats the substring start (pos) as the ^ anchor: when the pattern contains ^, if the match lands at pos and pos is not a line start,
  \c        skip that false match and search again from pos+1. */
  \c     int p = pos;
  \c     while (p <= len) {
  \c       pm[0].rm_so = p; pm[0].rm_eo = len;
  \c       if (regexec(re, text, 1, pm, REG_STARTEND) != 0) break;
  \c       long long s = pm[0].rm_so;
  \c       if (caret_in_pat && s == p && p > 0 && text[p-1] != '\n') { p = s + 1; continue; }
  \c       result = s;
  \c       break;
  \c     }
  \c   } else if (regexec(re, text+pos, 1, pm, 0) == 0) {
  \c     result = pos + pm[0].rm_so;
  \c   }
  \c   timeout_end(&old_sa, &old_tv);
  \c   return result;
  \c }
  \c long long re_match_at(char *text, int len, int pos) {
  \c   if (!re_anchor || re_broken) return 0;
  \c   if (pos >= len) return 0;
  \c   struct sigaction old_sa; struct itimerval old_tv;
  \c   regmatch_t pm[1];
  \c   timeout_start(&old_sa, &old_tv);
  \c   if (sigsetjmp(re_env, 1) != 0) { timeout_end(&old_sa, &old_tv); re_broken = 1; return 0; }
  \c   long long result = 0;
  \c   if (anchor_in_pat) {
  \c     /* whole buffer + REG_STARTEND: preserve ^/$ = real line start/end.
  \c        when the pattern contains ^: a match landing at pos is valid only if pos is a real line start, otherwise it is a false match at a substring start. */
  \c     pm[0].rm_so = pos; pm[0].rm_eo = len;
  \c     if (regexec(re_anchor, text, 1, pm, REG_STARTEND) == 0 && pm[0].rm_so == pos
  \c         && (!caret_in_pat || re_line_start(text, pos)))
  \c       result = pm[0].rm_eo;
  \c   } else if (regexec(re_anchor, text+pos, 1, pm, 0) == 0) {
  \c     result = pos + pm[0].rm_eo;
  \c   }
  \c   timeout_end(&old_sa, &old_tv);
  \c   return result;
  \c }
  c-function re-compile re_compile a n n -- n
  c-function re-find-next re_find_next a n n -- n
  c-function re-match-at re_match_at a n n -- n
  \c void *re_anchor_compile(char *pat, int len, int icase) {
  \c   /* for highlighting: compile an anchored ^(...) regex, return the pointer (stored in the rule); return NULL on failure.
  \c      REG_NEWLINE: . does not cross newlines, ^/$ = line start/end (with re_ptr_match's REG_STARTEND line range) */
  \c   int flags = REG_EXTENDED | REG_NEWLINE | (icase ? REG_ICASE : 0);
  \c   char *pa = malloc(len+4); pa[0]='^'; pa[1]='('; memcpy(pa+2, pat, len); pa[len+2]=')'; pa[len+3]=0;
  \c   regex_t *r = malloc(sizeof(regex_t));
  \c   if (regcomp(r, pa, flags)) { regfree(r); free(r); free(pa); return 0; }
  \c   free(pa);
  \c   return r;
  \c }
  \c long long re_ptr_match(void *rp, char *text, int len, int pos) {
  \c   regex_t *r = (regex_t*)rp;
  \c   if (!r || pos >= len) return 0;
  \c   regmatch_t pm[1];
  \c   pm[0].rm_so = 0; pm[0].rm_eo = len - pos;   /* limit the range to the current line to prevent cross-line matching */
  \c   if (regexec(r, text+pos, 1, pm, REG_STARTEND) == 0) return pm[0].rm_eo;
  \c   return 0;
  \c }
  \c void re_ptr_free(void *rp) { if (rp) { regfree((regex_t*)rp); free(rp); } }
  c-function re-anchor-compile re_anchor_compile a n n -- a
  c-function re-ptr-match re_ptr_match a a n n -- n
  c-function re-ptr-free re_ptr_free a -- void
end-c-library
