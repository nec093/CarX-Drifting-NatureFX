#!/usr/bin/env bash
# Builds kino/mods/NatureFX/naturefx.bundle.
#
# The Unity project lives outside the repo (Documents is under Windows Controlled Folder Access, which blocks
# Unity from writing there). This script copies unity/Assets/NatureFX into that project and runs the batch build.
#
#   tools/build_bundle.sh [unity-project-dir]
#
# Env overrides: UNITY_EXE, GAME_DIR, NFX_FREE_THREADS (logical CPUs kept free, default 2),
#                NFX_VARIANTS=full (build every shader variant, ignoring unity/variants/recorded_variants.txt),
#                NFX_HDRP=all (feature-complete HDRP assets instead of the game's, unity/variants/game_hdrp_*.json)
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
project="${1:-$HOME/NatureFX-Unity}"
unity="${UNITY_EXE:-/c/Program Files/Unity/Hub/Editor/2023.2.20f1/Editor/Unity.exe}"
game="${GAME_DIR:-C:\\Program Files (x86)\\Steam\\steamapps\\common\\CarX Drift Racing Online}"
log="$project/Logs/naturefx_build.log"

if [ ! -f "$project/Packages/manifest.json" ]; then
	echo "Unity project not found: $project" >&2
	echo "Create it with Unity 2023.2.x and add com.unity.render-pipelines.high-definition to Packages/manifest.json" >&2
	exit 1
fi

# Sync sources (keeps Generated/ and Settings/ that Unity creates inside the project)
mkdir -p "$project/Assets/NatureFX"
for d in Editor ShaderLibrary Shaders; do
	mkdir -p "$project/Assets/NatureFX/$d"
	# recursive, keeping sub folders (Shaders/DX12 ...)
	(cd "$repo/unity/Assets/NatureFX/$d" && find . -type f ! -name '*.meta' ! -name '.keep' | while read -r f; do
		mkdir -p "$project/Assets/NatureFX/$d/$(dirname "$f")"
		cp -f "$f" "$project/Assets/NatureFX/$d/$f"
	done)
done

# Keyword sets, recorded variants and the game's HDRP settings exported from the game, see unity/variants/README.md
rm -rf "$project/Assets/NatureFX/Variants"
mkdir -p "$project/Assets/NatureFX/Variants"
find "$repo/unity/variants" -maxdepth 1 -type f \( -name '*.txt' -o -name '*.json' \) -exec cp -f {} "$project/Assets/NatureFX/Variants/" \; 2>/dev/null || true

echo "Building bundle (log: $log) ..."
# Leave NFX_FREE_THREADS logical CPUs (default 2) to the rest of the PC: Unity and its shader compiler
# workers run on the others, at below normal priority (tools/run_limited.ps1)
export NFX_RUN_ARGS="-batchmode -nographics -projectPath \"$(cygpath -w "$project")\" -executeMethod NatureFX.Editor.BuildNatureFXBundle.BatchBuild -gameDir \"$game\" -logFile \"$(cygpath -w "$log")\" -quit"
set +e
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$repo/tools/run_limited.ps1")" \
	-Exe "$(cygpath -w "$unity")" -FreeThreads "${NFX_FREE_THREADS:-2}"
status=$?
set -e

grep -E '\[NatureFX\]|error|Shader error' "$log" | grep -v -E 'warning' | tail -40 || true
exit $status
