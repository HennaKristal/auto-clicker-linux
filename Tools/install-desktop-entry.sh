#!/usr/bin/env bash
set -euo pipefail

# Install just our development entry so KDE portal dialogs have a stable name and icon.
project_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
python3 - "$project_directory" <<'PY'
import os
import sys
from pathlib import Path

project = Path(sys.argv[1])
application_id = "org.hennakristal.AutoClicker"
data_directory = Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local/share")
entry = data_directory / "applications" / (application_id + ".desktop")
if entry.exists() and "X-AutoClicker-Development=true" not in entry.read_text():
    raise SystemExit("Refusing to replace a desktop entry that was not created by this installer: " + str(entry))
executable = project / "bin/Debug/net10.0/AutoClicker"
if not executable.exists():
    raise SystemExit("Run dotnet build first.")
def quote_argument(value):
    return '"' + str(value).replace('\\', '\\\\').replace('"', '\\"').replace('`', '\\`').replace('$', '\\$').replace('%', '%%') + '"'
template = (project / "Assets/Linux" / (application_id + ".desktop")).read_text()
content = template.replace("__EXECUTABLE__", quote_argument(executable)).replace("__ICON__", str(project / "Assets/Icons/icon.ico"))
entry.parent.mkdir(parents=True, exist_ok=True)
entry.write_text(content)
print(entry)
PY
