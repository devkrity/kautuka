#!/usr/bin/env bash
set -euo pipefail

src_dir="${1:-src}"
out_file="${2:-codebase-size.csv}"

echo "Project,Lines" > "$out_file"

find "$src_dir" -type f -name "*.csproj" \
  ! -path "*/tests/*" \
  ! -path "*/client/*" \
  ! -path "*/.kautuka-workspace/*" \
  -print0 |
while IFS= read -r -d '' csproj; do
  project_dir="$(dirname "$csproj")"
  project_name="$(basename "$project_dir")"

  lines=0

  while IFS= read -r -d '' file; do
    file_lines=$(
      awk '
        BEGIN { in_block = 0 }
        {
          line = $0
          gsub(/\r$/, "", line)

          if (in_block) {
            if (match(line, /\*\//)) {
              line = substr(line, RSTART + RLENGTH)
              in_block = 0
            } else next
          }

          while (match(line, /\/\*/)) {
            pre = substr(line, 1, RSTART - 1)
            post = substr(line, RSTART + RLENGTH)
            if (match(post, /\*\//)) {
              post = substr(post, RSTART + RLENGTH)
              line = pre post
            } else {
              line = pre
              in_block = 1
              break
            }
          }

          sub(/\/\/.*/, "", line)
          sub(/^[[:space:]]+/, "", line)
          sub(/[[:space:]]+$/, "", line)

          if (line == "") next
          if (line ~ /^using[[:space:]]+.*;$/) next
          if (line ~ /^namespace[[:space:]]+/) next
          if (line ~ /^[{}]$/) next
          if (line ~ /^};?$/) next
          if (line ~ /^\[[^]]+\]$/) next

          print
        }
      ' "$file" | wc -l
    )
    lines=$((lines + file_lines))
  done < <(
    find "$project_dir" \
      \( -type d \( -name bin -o -name obj \) -prune \) -o \
      \( -type f -name "*.cs" \
         ! -name "*.g.cs" \
         ! -name "*.generated.cs" \
         ! -name "*.designer.cs" \
         ! -name "*.AssemblyInfo.cs" \
         -print0 \)
  )

  printf '"%s",%s\n' "$project_name" "$lines" >> "$out_file"
done

tmp_file="$(mktemp)"
{
  head -n 1 "$out_file"
  tail -n +2 "$out_file" | sort -t',' -k2 -nr
} > "$tmp_file"
mv "$tmp_file" "$out_file"

echo "Wrote $out_file"