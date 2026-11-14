#!/usr/bin/env bash
# Builds Sweepfella with the C# compiler that ships with Windows (.NET Framework 4.x); nothing to install.
# Usage: ./build.sh            -> bin/Sweepfella.exe (windowed)
#        ./build.sh --console  -> bin/Sweepfella-console.exe (same app with a console, for --analyze etc.)
set -euo pipefail
cd "$(dirname "$0")"

CSC=/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe
target=winexe
out=bin/Sweepfella.exe
if [[ "${1:-}" == "--console" ]]; then
  target=exe
  out=bin/Sweepfella-console.exe
fi

mkdir -p bin
sources=()
for f in src/*.cs; do sources+=("$(wslpath -w "$f")"); done
# Each lang/<code>.txt is embedded as the resource "lang.<code>.txt".
for f in lang/*.txt; do sources+=("/resource:$(wslpath -w "$f"),lang.$(basename "$f")"); done

"$CSC" /nologo /codepage:65001 /optimize+ /platform:x64 /target:$target \
  /out:"$(wslpath -w "$out")" \
  /win32manifest:"$(wslpath -w app.manifest)" \
  /win32icon:"$(wslpath -w sweepfella.ico)" /resource:"$(wslpath -w sweepfella.ico)",sweepfella.ico \
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.VisualBasic.dll \
  "${sources[@]}" | iconv -f cp850 -t utf-8 | sed 's/^.*\\src\\/src\//'

chmod +x "$out"
echo "OK -> $out"
