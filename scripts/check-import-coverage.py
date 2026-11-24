#!/usr/bin/env python3
"""Fails unless the bulk-import code has at least the given line coverage (default 90%) in a Cobertura report."""
import sys
import xml.etree.ElementTree as ET

report = sys.argv[1] if len(sys.argv) > 1 else "tests/Bluestaq.Notes.Api.UnitTests/coverage.cobertura.xml"
threshold = float(sys.argv[2]) if len(sys.argv) > 2 else 90.0

lines = {}
def add(path, elements):
    for line in elements:
        key = (path, line.get("number"))
        lines[key] = lines.get(key, False) or int(line.get("hits", "0")) > 0


for cls in ET.parse(report).iter("class"):
    path = "/" + cls.get("filename", "").replace("\\", "/")
    if "/Import/" in path or path.endswith("/ImportContracts.cs"):
        add(path, cls.iter("line"))
    elif path.endswith("/NotesController.cs"):
        # The import actions (async bodies compile to nested classes like NotesController/<ImportCsv>d__9).
        in_async_import = "<Import" in cls.get("name", "")
        for method in cls.iter("method"):
            if in_async_import or method.get("name", "").startswith("Import"):
                add(path, method.iter("line"))

if not lines:
    sys.exit(f"No import code found in {report}")
covered = 100.0 * sum(lines.values()) / len(lines)
print(f"Import line coverage: {covered:.2f}% ({sum(lines.values())}/{len(lines)} lines), target {threshold:.0f}%")
sys.exit(0 if covered >= threshold else 1)
