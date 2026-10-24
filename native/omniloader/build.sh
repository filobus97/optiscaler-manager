#!/usr/bin/env bash
# Builds omniloader.dll with MinGW-w64. With "test", also builds mock FidelityFX builds and
# a test program, lays them out the way an install would, and runs them under Wine. With
# "kit", also packs the zip testers unpack beside OptiScaler.
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

if [[ "${1:-}" == "kit" ]]; then
  # The zip testers unpack into OptiScaler's folder: the stand-in under the upscaler's
  # name, an example list to edit, and the README.
  k="$out/kit"
  rm -rf "$k" "$out/omniloader-test-kit.zip"
  mkdir -p "$k/omni/upscaler"
  cp "$out/omniloader.dll" "$k/amd_fidelityfx_upscaler_dx12.dll"
  cp "$here/README.md" "$k/"
  printf '%s\r\n' \
    '; One line per build, in order. The first answers whenever nothing picks a version.' \
    '; Put the builds in omni\upscaler and change these names to match. Keep every name' \
    '; starting with amd_fidelityfx_upscaler_dx12_ (the README says why).' \
    '[Modules]' \
    'omni\upscaler\amd_fidelityfx_upscaler_dx12_official.dll = Official' \
    'omni\upscaler\amd_fidelityfx_upscaler_dx12_shader-overrides.dll = Shader overrides' \
    > "$k/amd_fidelityfx_upscaler_dx12.omni.ini"
  (cd "$k" && zip -qr "$out/omniloader-test-kit.zip" .)
  echo "built $out/omniloader-test-kit.zip"
  exit 0
fi

[[ "${1:-}" == "test" ]] || exit 0

t="$out/test"
rm -rf "$t"
mkdir -p "$t/omni/upscaler"
for v in 1 2 3 4; do
  "$CXX" "${FLAGS[@]}" -shared -DMOCK_VARIANT=$v -o "$t/omni/upscaler/amd_fidelityfx_upscaler_dx12_build-$v.dll" "$here/tests/mock_module.cpp"
done
# Driver files are renamed on disk, as an install does, so they never collide with the copy
# OptiScaler loads by the name amdxcffx64.dll.
"$CXX" "${FLAGS[@]}" -shared -o "$t/omni/upscaler/amdxcffx64_driver-test.dll" "$here/tests/driver_style.cpp"
"$CXX" "${FLAGS[@]}" -shared -o "$t/omni/upscaler/amdxcffx64_driver-refusing.dll" "$here/tests/driver_refusing.cpp"
"$CXX" "${FLAGS[@]}" -shared -o "$t/omni/upscaler/not-an-upscaler.dll" "$here/tests/not_upscaler.cpp"
"$CXX" "${FLAGS[@]}" -o "$t/test_omniloader.exe" "$here/tests/test_omniloader.cpp"

# The same binary under two FidelityFX names, each with its own list.
cp "$out/omniloader.dll" "$t/amd_fidelityfx_upscaler_dx12.dll"
cp "$out/omniloader.dll" "$t/amd_fidelityfx_framegeneration_dx12.dll"
cat > "$t/amd_fidelityfx_upscaler_dx12.omni.ini" <<'INI'
; first build answers when nothing picks a version
[Modules]
omni\upscaler\amd_fidelityfx_upscaler_dx12_build-1.dll = Build A
omni\upscaler\amd_fidelityfx_upscaler_dx12_build-2.dll = Build B
omni\upscaler\amdxcffx64_driver-test.dll = Test driver
omni\upscaler\amdxcffx64_driver-refusing.dll = Refusing driver
omni\upscaler\not-an-upscaler.dll = Not an upscaler
omni\upscaler\missing.dll = Missing
omni\upscaler\amd_fidelityfx_upscaler_dx12_build-3.dll = Build C
omni\upscaler\amd_fidelityfx_upscaler_dx12_build-4.dll = Build D
INI
cat > "$t/amd_fidelityfx_framegeneration_dx12.omni.ini" <<'INI'
[Modules]
omni\upscaler\amd_fidelityfx_upscaler_dx12_build-2.dll = Build B
INI

export WINEDEBUG=-all WINEPREFIX="${WINEPREFIX:-$out/wineprefix}"
status=0
WINE=${WINE:-$(command -v wine64 || echo /usr/lib/wine/wine64)}
"$WINE" "$t/test_omniloader.exe" "Z:${t//\//\\}" || status=$?
echo "--- what the stand-in logged ---"
cat "$t/amd_fidelityfx_upscaler_dx12.omni.log" | sed -n '1,30p'
exit $status
