#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <semver>" >&2
  exit 64
fi

version="$1"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "Invalid semantic version: ${version}" >&2
  exit 64
fi

if grep -q "^version: \"${version}\"$" addon/config.yaml; then
  echo "addon/config.yaml already has version ${version}"
  exit 0
fi

perl -0pi -e "s/^version: \".*\"$/version: \"${version}\"/m" addon/config.yaml

if ! grep -q "^## ${version}$" addon/CHANGELOG.md; then
  tmp_file="$(mktemp)"
  {
    sed -n '1p' addon/CHANGELOG.md
    printf '\n## %s\n\n- Release %s.\n\n' "$version" "$version"
    sed '1d' addon/CHANGELOG.md
  } > "$tmp_file"
  mv "$tmp_file" addon/CHANGELOG.md
fi
