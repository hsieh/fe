#!/bin/sh
# 运行栈效应 + 边界拼接回归测试（avy-status$ / showcmd$ / 有界拼接）。
# 需要完整 fe 定义 + 首次运行需 C 编译器编译 libcc 绑定。
set -e
cd "$(dirname "$0")/.."
trap 'rm -f fe-strip.fs' EXIT
sed '$d' fe.fs | sed '$d' > fe-strip.fs
gforth-fast -e "2variable fe-incdir s\" $(pwd)\" fe-incdir 2!" \
            -e "include fe-strip.fs" \
            -e "include test/test-fe-stack.fs" \
            -e "include test/test-bounds.fs"
