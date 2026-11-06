\ ===== test/test-fe-stack.fs — stack-effect unit tests (avy-status$ / showcmd$) =====
\ must be included on top of the full fe definitions (fe-strip.fs = fe.fs minus the trailing fe call and bye).
\ run: see run-fe-stack.fs (executed from the project root).

require assert.fs

variable t-n  0 t-n !
: t-ok  1 t-n +! ;
variable stk0  0 stk0 !

\ data-stack depth unchanged (the word net-consumes 0 inputs, returns 2 values that 2drop discards)
: stk-balance? ( -- flag )  depth stk0 @ = ;

\ non-avy state: avy-status$ returns 0 0 (2 values), net-consumes 0 inputs
: test-avy-status-stack
  0 avy-active !  0 avy-char !  0 avy-second !
  depth stk0 !
  avy-status$  2drop
  assert( stk-balance? )
  t-ok ;

\ avy state: avy-status$ returns a u (non-zero), net-consumes 0 inputs
: test-avy-status-active
  1 avy-active !  0 avy-char !  0 avy-second !
  depth stk0 !
  avy-status$  2drop
  assert( stk-balance? )
  t-ok ;

\ command mode, non-op, non-avy: showcmd$ returns 0 0 (2 values), net-consumes 0 inputs
\ (this test guards against a 2drop/drop-style stack underflow in showcmd$: before the fix, the non-avy 2drop underflowed and stole 1 value)
: test-showcmd-stack
  mode-command mode-set
  0 op-kind !
  0 avy-active !  0 avy-char !  0 avy-second !
  depth stk0 !
  showcmd$  2drop
  assert( stk-balance? )
  t-ok ;

test-avy-status-stack
test-avy-status-active
test-showcmd-stack

cr ." fe-stack tests passed (" t-n @ . ." assertions ok)" cr
