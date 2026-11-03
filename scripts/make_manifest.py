#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-only
"""Adds one plugin version to a Jellyfin repository manifest.json.

Usage: make_manifest.py <build.yaml> <plugin.zip> <version> <source_url> <manifest.json> [keep]
The manifest is created if missing. A version that is already listed is replaced.
Only the newest <keep> versions stay listed (default 1).
"""
import datetime
import hashlib
import json
import os
import sys

import yaml

meta_path, zip_path, version, source_url, manifest_path = sys.argv[1:6]
keep = int(sys.argv[6]) if len(sys.argv) > 6 else 1
if keep < 1:
    sys.exit("keep must be at least 1")

with open(meta_path, encoding="utf-8") as f:
    meta = yaml.safe_load(f)

with open(zip_path, "rb") as f:
    checksum = hashlib.md5(f.read()).hexdigest()

manifest = []
if os.path.exists(manifest_path):
    with open(manifest_path, encoding="utf-8") as f:
        manifest = json.load(f)

plugin = next((p for p in manifest if p["guid"] == meta["guid"]), None)
if plugin is None:
    plugin = {"guid": meta["guid"], "versions": []}
    manifest.append(plugin)

plugin.update(
    name=meta["name"],
    description=meta["description"].strip(),
    overview=meta["overview"],
    owner=meta["owner"],
    category=meta["category"],
)

entry = {
    "version": version,
    "changelog": str(meta.get("changelog", "")).strip(),
    "targetAbi": meta["targetAbi"],
    "sourceUrl": source_url,
    "checksum": checksum,
    "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}
plugin["versions"] = [v for v in plugin["versions"] if v["version"] != version]
plugin["versions"].insert(0, entry)
plugin["versions"].sort(key=lambda v: tuple(int(x) for x in v["version"].split(".")), reverse=True)
plugin["versions"] = plugin["versions"][:keep]

with open(manifest_path, "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")

print(f"{meta['name']} {version} md5 {checksum}")
