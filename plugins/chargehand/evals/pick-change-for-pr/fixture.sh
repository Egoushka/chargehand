#!/usr/bin/env bash
set -euo pipefail
mkdir -p src
cat > src/cli.py <<'EOF'
import sys
from export import export_all


def main(argv):
    if len(argv) > 1 and argv[1] == "export":
        export_all("out")


if __name__ == "__main__":
    main(sys.argv)
EOF
cat > src/export.py <<'EOF'
import os


def export_all(target):
    os.makedirs(target, exist_ok=True)
    with open(os.path.join(target, "data.csv"), "w") as f:
        f.write("id,name\n1,example\n")
EOF
