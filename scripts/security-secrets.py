"""Scan every tracked or publishable untracked file, without scanning local caches."""
from pathlib import Path
import os
import shutil
import subprocess
import tempfile

root = Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip()).resolve()
os.chdir(root)
files = subprocess.check_output([
    "git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"
]).decode("utf-8").split("\0")
files = sorted(set(filter(None, files)))
if not files:
    raise SystemExit("No publication files found; secret verification is incomplete.")

(root / ".local").mkdir(exist_ok=True)
with tempfile.TemporaryDirectory(prefix="secret-scan-", dir=root / ".local") as temporary:
    snapshot = Path(temporary)
    for name in files:
        source = root / name
        if source.is_symlink() or not source.resolve().is_relative_to(root):
            raise SystemExit(f"Publication file escapes repository: {name}")
        destination = snapshot / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, destination)
    print(f"Scanning {len(files)} publication files for secrets.", flush=True)
    subprocess.run([
        str(root / ".local/security-tools/trivy"), "fs", "--scanners", "secret",
        "--ignorefile", os.devnull, "--exit-code", "1", "--format", "json",
        "--output", str(root / ".local/secret-audit.json"), str(snapshot)
    ], check=True)
print("Publication secret scan passed.")
