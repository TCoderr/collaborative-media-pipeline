"""Fail closed on NuGet audit diagnostics or any known vulnerable package."""
import json
import sys
with open(sys.argv[1], "rb") as stream:
    audit = json.load(stream)
if audit.get("logs"):
    raise SystemExit("NuGet audit returned diagnostics; inspect the audit before proceeding.")
if not audit.get("projects"):
    raise SystemExit("NuGet audit did not inspect any projects.")
findings = []
for project in audit["projects"]:
    for framework in project.get("frameworks", []):
        for group in ("topLevelPackages", "transitivePackages"):
            for package in framework.get(group, []):
                if package.get("vulnerabilities"):
                    findings.append(package["id"])
if findings:
    raise SystemExit("Vulnerable NuGet packages: " + ", ".join(sorted(set(findings))))
print("NuGet vulnerability audit passed.")
