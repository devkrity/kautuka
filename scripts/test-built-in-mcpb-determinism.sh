#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
mkdir "$work/bin"
cat > "$work/bin/uv" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
cat > "$work/bin/mcpb" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == pack ]] || exit 0
python3 - "$3" <<'PY'
import os, sys, time, zipfile
with zipfile.ZipFile(sys.argv[1], 'w') as archive:
    for name, mode in [('z.txt', 0o100755), ('a.txt', 0o100644)]:
        entry = zipfile.ZipInfo(name, time.localtime()[:6])
        entry.external_attr = mode << 16
        entry.compress_type = zipfile.ZIP_DEFLATED
        archive.writestr(entry, name.encode())
if os.environ.get('TEST_SIGNED'):
    with open(sys.argv[1], 'ab') as output: output.write(b'MCPB_SIG_END')
PY
EOF
chmod +x "$work/bin/uv" "$work/bin/mcpb"
export PATH="$work/bin:$PATH"
bash "$script_dir/stage-built-in-mcpb.sh" --output "$work/first.mcpb" >/dev/null
sleep 3
bash "$script_dir/stage-built-in-mcpb.sh" --output "$work/second.mcpb" >/dev/null
cmp "$work/first.mcpb" "$work/second.mcpb"
python3 - "$work/first.mcpb" <<'PY'
import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as archive:
    assert archive.namelist() == ['a.txt', 'z.txt']
    for item, mode in zip(archive.infolist(), (0o100644, 0o100755)):
        assert item.date_time == (2000, 1, 1, 0, 0, 0)
        assert item.external_attr >> 16 == mode and item.compress_type == zipfile.ZIP_DEFLATED
        assert archive.read(item) == item.filename.encode()
PY
if TEST_SIGNED=1 bash "$script_dir/stage-built-in-mcpb.sh" --output "$work/signed.mcpb" >/dev/null 2>&1; then
    echo 'Signed MCPB was rewritten' >&2
    exit 1
fi
echo 'MCPB archive normalization is deterministic and preserves entries.'
