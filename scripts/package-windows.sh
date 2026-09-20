#!/usr/bin/env bash
set -euo pipefail

script_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
exec bash "$script_root/package-app.sh" win-x64 windows-x64 electron.exe horizon-forge.exe
