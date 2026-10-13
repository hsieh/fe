\ ===== pty.fs — pseudo-terminal (forkpty) bindings + a minimal subprocess API =====
\ Purpose: the foundation for the REPL / subprocess layer (P1b: openpty/forkpty via c-library bindings).
\ Rule: this file is an opt-in dependency (needs a C compiler + libutil, and libcc generates a cached module on first use),
\       so it is not on fe.fs's main load chain. The REPL layer (P2) includes this file when needed.
\ Depends on: libcc.fs (bundled with Gforth); read/write/close are provided by unix/libc.fs (bundled with Gforth).

require libcc.fs

c-library ptybind
  s" util" add-lib
  \c #ifdef __APPLE__
  \c #include <util.h>   /* macOS: openpty/forkpty */
  \c #else
  \c #include <pty.h>    /* Linux: openpty/forkpty */
  \c #endif
  \c #include <sys/wait.h>
  \c #include <unistd.h>
  \c #include <stdlib.h>
  \c #include <string.h>
  \c #include <errno.h>
  \c #include <fcntl.h>
  c-function forkpty forkpty a a a a -- n   \ ( master* name termp winp -- pid )
  c-function waitpid waitpid n a n -- n     \ ( pid status* options -- r )
  \ Child side: argv is built in C (more robust than hand-assembling in Forth). execvp does not return on success; on failure errno is written into the pty.
  \c static int pty_exec(char *cmd) {  /* returns only if execvp fails */
  \c   char *argv[4];
  \c   argv[0]="sh"; argv[1]="-c"; argv[2]=cmd; argv[3]=0;
  \c   execvp("sh", argv);
  \c   { int e=errno; char *m=strerror(e);
  \c     write(2,"execvp: ",8); write(2,m,strlen(m)); write(2,"\r\n",2); }
  \c   return -1;
  \c }
  c-function pty-exec pty_exec s -- n       \ ( cmd-addr cmd-u -- ret )
  \ interactive shell (for REPL): no -c, argv = {"sh", NULL}
  \c static int pty_shell(void) {  /* returns only if execvp fails */
  \c   char *argv[2];
  \c   argv[0]="sh"; argv[1]=0;
  \c   execvp("sh", argv);
  \c   { int e=errno; char *m=strerror(e);
  \c     write(2,"execvp: ",8); write(2,m,strlen(m)); write(2,"\r\n",2); }
  \c   return -1;
  \c }
  \c static void pty_nonblock(int fd) {  /* O_NONBLOCK: the drain loop stops at EAGAIN */
  \c   fcntl(fd, F_SETFL, fcntl(fd, F_GETFL, 0) | O_NONBLOCK);
  \c }
  c-function pty-shell pty_shell -- n       \ ( -- ret )
  c-function pty-nonblock pty_nonblock n -- void
  c-function _exit _exit n -- void          \ ( code -- ) child-process exit: does not flush stdio
end-c-library

1 constant WNOHANG          \ waitpid non-blocking flag

\ --- pty handle layout (24 bytes): master fd | child pid | exit status ---
 0 constant P:MASTER
 8 constant P:PID
16 constant P:STATUS
24 constant /PTY

: pty-master ( pty -- addr ) P:MASTER + ;
: pty-pid    ( pty -- addr ) P:PID    + ;
: pty-status ( pty -- addr ) P:STATUS + ;

\ A single handle. For multiple REPLs in P2, change to one /PTY block per buffer (or a registry).
create pty0 /PTY allot

\ command-string staging (written before fork, the child inherits it and reads it back; avoids stack-order differences around fork corrupting the stack).
variable pty-cmd-a  0 pty-cmd-a !
variable pty-cmd-u  0 pty-cmd-u !

\ --- minimal API ---

\ pty-spawn ( c-addr u -- pid )  start sh -c with stdin/stdout/stderr all attached to the pty.
\ Returns the child pid (from the parent's perspective).
: pty-spawn ( c-addr u -- pid )
  pty-cmd-u !  pty-cmd-a !
  pty0 pty-master 0 0 0 forkpty
  dup pty0 pty-pid !
  dup 0= if
    pty-cmd-a @ pty-cmd-u @ pty-exec drop
    127 _exit
  then ;

\ pty-write ( c-addr u -- n )  write to the master = send to the child's stdin.
: pty-write ( c-addr u -- n )  pty0 pty-master @ -rot write ;

\ pty-read ( c-addr u -- n )  read child output from the master.
\ n<0 = EIO (child exited / pty torn down); 0 = EOF; >0 = bytes read.
: pty-read ( c-addr u -- n )  pty0 pty-master @ -rot read ;

\ pty-alive? ( -- f )  non-blocking check whether the child is still running (also reaps it and stores the status in the handle).
: pty-alive? ( -- f )  pty0 pty-pid @ pty0 pty-status WNOHANG waitpid 0= ;

\ pty-close ( -- )  blocking reap + close the master fd.
: pty-close ( -- )
  pty0 pty-pid @ pty0 pty-status 0 waitpid drop
  pty0 pty-master @ close drop ;

\ pty-exit-code ( -- n )  exit code after the most recent reap (0 if not exited).
: pty-exit-code ( -- n )  pty0 pty-status @ 8 rshift ;

\ pty-spawn-shell ( -- pid )  start an interactive sh (for REPL): no -c, stdin/stdout/stderr attached to the pty.
: pty-spawn-shell ( -- pid )
  pty0 pty-master 0 0 0 forkpty
  dup pty0 pty-pid !
  dup 0= if  pty-shell drop  127 _exit  then ;

\ pty-nonblock-master ( -- )  set O_NONBLOCK on the master for a drain loop that stops at EAGAIN.
: pty-nonblock-master ( -- )  pty0 pty-master @ pty-nonblock ;
