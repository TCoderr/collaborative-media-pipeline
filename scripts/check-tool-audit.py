"""Require actual Go binary coverage and reject every HIGH/CRITICAL tool finding."""
import json
import sys
with open(sys.argv[1], "rb") as stream:
    audit = json.load(stream)
results = audit.get("Results", [])
if not any(result.get("Type") == "gobinary" and result.get("Packages") for result in results):
    raise SystemExit("Tool audit has no Go binary coverage; security verification is incomplete.")
findings = [v for result in results for v in result.get("Vulnerabilities", []) if v["Severity"] in ("HIGH", "CRITICAL")]
if findings:
    raise SystemExit(f"Tool security gate blocked: {len(findings)} HIGH/CRITICAL findings (" + ", ".join(sorted({v["VulnerabilityID"] for v in findings})) + ").")
print("Tool audit passed with verified binary coverage.")
