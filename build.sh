#!/usr/bin/env bash
# Linux/macOS build of a .NET Framework 4.0 target using the Roslyn compiler shipped
# with the modern .NET SDK plus the net40 reference assemblies. Forced by the hard
# constraint "net40 + classic csproj" while developing on a non-Windows machine:
# MSBuild cannot build a v4.0 project here, but csc can, and it produces the same PE.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$ROOT/build"
REFS="$OUT/refs"
REFPKG_VERSION="1.0.3"
REFPKG_URL="https://www.nuget.org/api/v2/package/Microsoft.NETFramework.ReferenceAssemblies.net40/$REFPKG_VERSION"

find_csc() {
  # "dotnet --list-sdks" prints lines like: 10.0.200 [/usr/share/dotnet/sdk]
  local line version root candidate
  line="$(dotnet --list-sdks 2>/dev/null | tail -1)"
  if [ -n "$line" ]; then
    version="${line%% *}"
    root="${line#*[}"
    root="${root%]}"
    candidate="$root/$version/Roslyn/bincore/csc.dll"
    if [ -f "$candidate" ]; then echo "$candidate"; return 0; fi
  fi

  local found
  found="$(find /usr/share/dotnet/sdk "$HOME/.dotnet/sdk" -maxdepth 4 -name csc.dll -path '*Roslyn/bincore*' 2>/dev/null | sort -V | tail -1)"
  [ -n "$found" ] || { echo "csc.dll not found; install the .NET SDK" >&2; exit 1; }
  echo "$found"
}

fetch_refs() {
  if [ -f "$REFS/.ok" ]; then return 0; fi
  echo "==> fetching net40 reference assemblies ($REFPKG_VERSION)"
  mkdir -p "$REFS"
  curl -sSL -o "$OUT/net40ref.nupkg" "$REFPKG_URL"
  python3 - "$OUT/net40ref.nupkg" "$REFS" <<'PY'
import sys, zipfile, os
pkg, dest = sys.argv[1], sys.argv[2]
prefix = 'build/.NETFramework/v4.0/'
z = zipfile.ZipFile(pkg)
n = 0
for name in z.namelist():
    if name.startswith(prefix) and name.lower().endswith('.dll') and '/' not in name[len(prefix):]:
        open(os.path.join(dest, os.path.basename(name)), 'wb').write(z.read(name))
        n += 1
print('    extracted %d reference assemblies' % n)
PY
  rm -f "$OUT/net40ref.nupkg"
  touch "$REFS/.ok"
}

CSC="$(find_csc)"
fetch_refs

# The shipped product is ONE exe (see docs/DESIGN.md): the App links the Core sources
# directly rather than referencing Core.dll, so there is nothing to copy alongside it.
# Core.csproj still exists and is still built here -- deliberately WITHOUT the WinForms
# reference -- so that "Core must have no WinForms reference" is enforced by the compiler.
CORE_SRC=$(find "$ROOT/src/MoveToNewPC.Core" -name '*.cs' | sort)
APP_SRC=$(find "$ROOT/src/MoveToNewPC.App" -name '*.cs' | sort)
TEST_SRC=$(find "$ROOT/tests/MoveToNewPC.Tests" -name '*.cs' | sort)
TOOL_SRC=$(find "$ROOT/tools/MakeTestTree" -name '*.cs' | sort)

R="$REFS"
COMMON=(-noconfig -nostdlib+ -langversion:4 -platform:anycpu -nowarn:1701,1702,1591,649,169,414 -warnaserror+ -utf8output
        -define:NET40
        "-r:$R/mscorlib.dll" "-r:$R/System.dll" "-r:$R/System.Core.dll"
        "-r:$R/System.Security.dll" "-r:$R/System.Xml.dll")
UI=("-r:$R/System.Windows.Forms.dll" "-r:$R/System.Drawing.dll")

run_csc() { dotnet exec "$CSC" "$@"; }

echo "==> MoveToNewPC.Core.dll  (headless check: no WinForms reference on the command line)"
run_csc "${COMMON[@]}" -target:library -out:"$OUT/MoveToNewPC.Core.dll" \
        -doc:"$OUT/MoveToNewPC.Core.xml" $CORE_SRC

echo "==> MoveToNewPC.exe"
run_csc "${COMMON[@]}" "${UI[@]}" -target:winexe -out:"$OUT/MoveToNewPC.exe" \
        -win32manifest:"$ROOT/src/MoveToNewPC.App/app.manifest" \
        $CORE_SRC $APP_SRC

echo "==> MoveToNewPC.Tests.exe"
# -main: because the test project deliberately has two entry points: Program (full run,
# Windows only) and PureProgram (portable subset, used by tools/verify-pure.sh).
run_csc "${COMMON[@]}" -target:exe -out:"$OUT/MoveToNewPC.Tests.exe" \
        -main:MoveToNewPC.Tests.Program \
        -win32manifest:"$ROOT/tests/MoveToNewPC.Tests/app.manifest" \
        $CORE_SRC $TEST_SRC

echo "==> MakeTestTree.exe"
run_csc "${COMMON[@]}" -target:exe -out:"$OUT/MakeTestTree.exe" \
        -win32manifest:"$ROOT/tools/MakeTestTree/app.manifest" \
        $CORE_SRC $TOOL_SRC

echo
echo "Build OK -> $OUT"
ls -la "$OUT"/*.exe "$OUT"/*.dll 2>/dev/null | sed 's|'"$ROOT"'/||'
