#!/usr/bin/env bash
# 一键把本目录的 Assets/BatchOptimizer 同步到 Unity 工程。
# 源 = 当前工作目录，副本 = Unity 工程目录。
set -e

SRC="$(cd "$(dirname "$0")" && pwd)/Assets/BatchOptimizer"
DST="E:/work/SVN_2022/北理工-虚拟车辆-unity工程/Virtual Vehicle Engineering/Assets/BatchOptimizer"

mkdir -p "$DST"
cp -r "$SRC/." "$DST/"

echo "[OK] 已同步 -> $DST"
