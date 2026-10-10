#!/usr/bin/env bash
# Builds omniloader.dll with MinGW-w64. With "test", also builds mock FidelityFX builds and
# a test program, lays them out the way an install would, and runs them under Wine.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="$here/out"
CXX=${CXX:-x86_64-w64-mingw32-g++}
WINDRES=${WINDRES:-x86_64-w64-mingw32-windres}
FLAGS=(-std=c++17 -O2 -Wall -Wextra -static -static-libgcc -static-libstdc++)

mkdir -p "$out"
"$WINDRES" "$here/version.rc" -O coff -o "$out/version.o"
"$CXX" "${FLAGS[@]}" -shared -o "$out/omniloader.dll" "$here/omniloader.cpp" "$out/version.o"
echo "built $out/omniloader.dll"

[[ "${1:-}" == "test" ]] || exit 0

t="$out/test"
rm -rf "$t"
mkdir -p "$t/omni/upscaler"
for v in 1 2 3 4; do
  "$CXX" "${FLAGS[@]}" -shared -DMOCK_VARIANT=$v -o "$t/omni/upscaler/build-$v.dll" "$here/tests/mock_module.cpp"
done
"$CXX" "${FLAGS[@]}" -shared -o "$t/omni/upscaler/amdxcffx64.dll" "$here/tests/driver_style.cpp"
"$CXX" "${FLAGS[@]}" -o "$t/test_omniloader.exe" "$here/tests/test_omniloader.cpp"

# The same binary under two FidelityFX names, each with its own list.
cp "$out/omniloader.dll" "$t/amd_fidelityfx_upscaler_dx12.dll"
cp "$out/omniloader.dll" "$t/amd_fidelityfx_framegeneration_dx12.dll"
cat > "$t/amd_fidelityfx_upscaler_dx12.omni.ini" <<'INI'
; first build answers when nothing picks a version
[Modules]
omni\upscaler\build-1.dll = Build A
omni\upscaler\build-2.dll = Build B
omni\upscaler\amdxcffx64.dll = Driver-style
omni\upscaler\missing.dll = Missing
omni\upscaler\build-3.dll = Build C
omni\upscaler\build-4.dll = Build D
INI
cat > "$t/amd_fidelityfx_framegeneration_dx12.omni.ini" <<'INI'
[Modules]
omni\upscaler\build-2.dll = Build B
INI

export WINEDEBUG=-all WINEPREFIX="${WINEPREFIX:-$out/wineprefix}"
status=0
WINE=${WINE:-$(command -v wine64 || echo /usr/lib/wine/wine64)}
"$WINE" "$t/test_omniloader.exe" "Z:${t//\//\\}" || status=$?
echo "--- what the stand-in logged ---"
cat "$t/amd_fidelityfx_upscaler_dx12.omni.log" | sed -n '1,12p'
exit $status
