#!/usr/bin/env bash
set -eo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export PATH="$HOME/.local/bin:$PATH"
CHECK=0 SKIP=0 RESET=0 YES=0 REPOS=(kautuka-auth narad)
while (($#)); do arg="${1//$'\r'/}"; case "$arg" in
  --check) CHECK=1;; --skip-install) SKIP=1;; --reset) RESET=1;; --yes) YES=1;;
  --repos) shift; IFS=',' read -r -a REPOS <<< "${1:-}";; --help|-h) echo 'Usage: ./setup_the_world.sh [--check] [--skip-install] [--reset] [--yes] [--repos kautuka-auth,narad]'; exit 0;;
  *) echo "Unknown option: $1" >&2; exit 2;; esac; shift; done
if (( CHECK && RESET )); then echo '--check cannot be combined with --reset.' >&2; exit 2; fi
NODE_EXPECTED="$(tr -d '\r\n' < "$ROOT/.nvmrc")"
PNPM_EXPECTED="$(node -p "require('$ROOT/package.json').packageManager.split('@')[1]" 2>/dev/null || sed -nE 's/.*"packageManager"[[:space:]]*:[[:space:]]*"pnpm@([^\"]+)".*/\1/p' "$ROOT/package.json" | head -1)"
DOTNET_EXPECTED="$(sed -nE 's/.*"version"[[:space:]]*:[[:space:]]*"([^\"]+)".*/\1/p' "$ROOT/global.json" | head -1)"
DOTNET_ROLL_FORWARD="$(sed -nE 's/.*"rollForward"[[:space:]]*:[[:space:]]*"([^\"]+)".*/\1/p' "$ROOT/global.json" | head -1)"
PY_EXPECTED="$(tr -d '\r\n' < "$ROOT/.python-version")"
OS="$(uname -s)"
node_os=''; node_arch=''
case "$OS" in Linux) node_os=linux;; Darwin) node_os=darwin;; esac
case "$(uname -m)" in x86_64|amd64) node_arch=x64;; arm64|aarch64) node_arch=arm64;; esac
node_bin=''
[[ -n "$node_os" && -n "$node_arch" ]] && node_bin="$HOME/.local/share/kautuka/node/node-v${NODE_EXPECTED}-${node_os}-${node_arch}/bin"
export PATH="$HOME/.dotnet:$HOME/.local/bin${node_bin:+:$node_bin}:$PATH"
if (( RESET )); then
  candidates=(); while IFS= read -r p; do candidates+=("$p"); done < <(find "$ROOT" -type d \( -name node_modules -o -name bin -o -name obj -o -name .pytest_cache -o -name .venv -o -name __pycache__ \) -not -path '*/node_modules/*/node_modules/*' -prune -print 2>/dev/null)
  targets=(); skipped_targets=()
  for p in "${candidates[@]}"; do
    [[ "$p" == "$ROOT"/* ]] || { skipped_targets+=("$p (outside repository)"); continue; }
    rel="${p#"$ROOT"/}"
    if ! git -C "$ROOT" check-ignore -q -- "$rel"; then skipped_targets+=("$p (not ignored by Git)"); continue; fi
    if [[ -n "$(git -C "$ROOT" ls-files -- "$rel")" ]]; then skipped_targets+=("$p (contains tracked files)"); continue; fi
    targets+=("$p")
  done
  databases=(); for name in kautuka.db kautuka.db-wal kautuka.db-shm kautuka-usage.db kautuka-usage.db-wal kautuka-usage.db-shm; do [[ -f "$HOME/.kautuka/runtime/$name" ]] && databases+=("$HOME/.kautuka/runtime/$name"); done
  printf 'Reset will delete these Git-ignored build/dependency/cache directories with no tracked files:\n'; printf '  %s\n' "${targets[@]:-<none>}"
  printf 'Reset will skip these directories because they are not ignored or contain tracked files:\n'; printf '  %s\n' "${skipped_targets[@]:-<none>}"
  printf 'Reset will delete these Kautuka development database files:\n'; printf '  %s\n' "${databases[@]:-<none>}"
  if (( ! YES )); then read -r -p 'Continue? [y/N] ' answer; [[ "$answer" =~ ^[Yy]([Ee][Ss])?$ ]] || { echo 'Reset cancelled.'; exit 1; }; fi
  for p in "${targets[@]:-}"; do [[ -n "$p" ]] || continue; [[ "$p" == "$ROOT"/* ]] || { echo "Refusing outside repo: $p" >&2; exit 1; }; rm -rf "$p"; done
  for p in "${databases[@]:-}"; do [[ -n "$p" ]] || continue; case "$(basename "$p")" in kautuka.db|kautuka.db-wal|kautuka.db-shm|kautuka-usage.db|kautuka-usage.db-wal|kautuka-usage.db-shm) [[ "$(dirname "$p")" == "$HOME/.kautuka/runtime" ]] || { echo "Refusing unexpected data path: $p" >&2; exit 1; }; rm -f "$p";; *) echo "Refusing unexpected data file: $p" >&2; exit 1;; esac; done
fi
# Bootstrap is intentionally explicit before any privileged package-manager operation.
install_pkg() { local pkg="$1"; case "$OS" in Darwin) brew install "$pkg";; Linux) if command -v apt-get >/dev/null; then case "$pkg" in dotnet-sdk) pkg=dotnet-sdk-10.0;; dotnet-icu) pkg=libicu-dev;; node) pkg=nodejs;; uv) pkg=uv;; python3) pkg=python3;; podman) pkg=podman;; esac; echo "Installing $pkg requires apt and sudo."; read -r -p 'Run sudo apt-get update/install now? [y/N] ' a; [[ "$a" =~ ^[Yy] ]] || return 1; sudo apt-get update && sudo apt-get install -y "$pkg"; elif command -v dnf >/dev/null; then case "$pkg" in dotnet-sdk) pkg=dotnet-sdk-10.0;; dotnet-icu) pkg=libicu;; node) pkg=nodejs;; uv) pkg=uv;; python3) pkg=python3;; podman) pkg=podman;; esac; echo "Installing $pkg requires dnf and sudo."; read -r -p 'Run sudo dnf install now? [y/N] ' a; [[ "$a" =~ ^[Yy] ]] || return 1; sudo dnf install -y "$pkg"; else echo 'Install required tools with your distribution package manager.' >&2; return 1; fi;; *) echo 'Use setup_the_world.ps1 on Windows.' >&2; return 1;; esac; }
found() { command -v "$1" >/dev/null 2>&1 || echo '<missing>'; }
version() { local c="$1"; shift; command -v "$c" >/dev/null 2>&1 && "$c" "$@" 2>/dev/null | head -1 || echo '<missing>'; }
install_exact_node() {
  local arch os package root dir archive
  case "$(uname -m)" in x86_64|amd64) arch=x64;; arm64|aarch64) arch=arm64;; *) echo 'Unsupported Node architecture; install the pinned Node.js version manually.' >&2; return 1;; esac
  case "$OS" in Linux) os=linux; package="node-v${NODE_EXPECTED}-${os}-${arch}.tar.xz";; Darwin) os=darwin; package="node-v${NODE_EXPECTED}-${os}-${arch}.tar.gz";; *) return 1;; esac
  root="$HOME/.local/share/kautuka/node"; dir="$root/node-v${NODE_EXPECTED}-${os}-${arch}"; mkdir -p "$root" "$HOME/.local/bin"
  if [[ ! -x "$dir/bin/node" ]]; then archive="$(mktemp)"; curl -fsSL "https://nodejs.org/dist/v${NODE_EXPECTED}/$package" -o "$archive"; tar -xf "$archive" -C "$root"; rm -f -- "$archive"; fi
  export PATH="$dir/bin:$HOME/.local/bin:$PATH"
}
install_exact_dotnet() {
  mkdir -p "$HOME/.dotnet" "$HOME/.local/bin"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$HOME/.local/bin/dotnet-install.sh"
  bash "$HOME/.local/bin/dotnet-install.sh" --version "$DOTNET_EXPECTED" --install-dir "$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$HOME/.local/bin:$PATH"
}
install_uv() {
  local installer
  mkdir -p "$HOME/.local/bin"
  installer="$(mktemp)"
  curl -fsSL https://astral.sh/uv/install.sh -o "$installer"
  UV_INSTALL_DIR="$HOME/.local/bin" UV_NO_MODIFY_PATH=1 sh "$installer"
  rm -f -- "$installer"
  export PATH="$HOME/.local/bin:$PATH"
}
persist_tool_path() {
  local line profile node_suffix
  node_suffix=''
  if [[ -n "$node_os" && -n "$node_arch" ]]; then node_suffix=':$HOME/.local/share/kautuka/node/node-v'"${NODE_EXPECTED}-${node_os}-${node_arch}"'/bin'; fi
  line='export PATH="$HOME/.dotnet:$HOME/.local/bin'"$node_suffix"':$PATH"'
  for profile in "$HOME/.profile" "$HOME/.bashrc"; do
    [[ -e "$profile" ]] || : > "$profile"
    grep -Fqx -- "$line" "$profile" || printf '\n%s\n' "$line" >> "$profile"
  done
}
missing=()
add_check() { local name="$1" expected="$2" actual="$3" status color reset; status=OK; color=''; reset=''; [[ -t 1 ]] && { color=$'\033[32m'; reset=$'\033[0m'; }; if [[ "$actual" != "$expected" ]]; then status='MISSING / WRONG'; missing+=("$name"); [[ -t 1 ]] && color=$'\033[31m'; fi; printf '%-24s expected %-15s found %-20s %s%s%s\n' "$name" "$expected" "$actual" "$color" "$status" "$reset"; }
add_present() { local name="$1" command_name="$2" actual status color reset; actual="$(version "$command_name" --version)"; status=OK; color=''; reset=''; [[ -t 1 ]] && { color=$'\033[32m'; reset=$'\033[0m'; }; if [[ "$actual" == '<missing>' ]]; then status='MISSING / WRONG'; missing+=("$name"); [[ -t 1 ]] && color=$'\033[31m'; fi; printf '%-24s expected %-15s found %-20s %s%s%s\n' "$name" 'installed' "$actual" "$color" "$status" "$reset"; }
dotnet_version_ok() { local found="$1" want_major want_minor want_patch got_major got_minor got_patch; [[ "$found" == "$DOTNET_EXPECTED" ]] && return 0; [[ "$DOTNET_ROLL_FORWARD" == latestFeature && "$found" == *.*.* && "$found" != *[^0-9.]* ]] || return 1; IFS=. read -r want_major want_minor want_patch <<< "$DOTNET_EXPECTED"; IFS=. read -r got_major got_minor got_patch <<< "$found"; [[ "$want_major" == "$got_major" && "$want_minor" == "$got_minor" ]] && (( 10#$got_patch >= 10#$want_patch )); }
add_present 'Git' git
dotnet_found="$(dotnet --version 2>/dev/null || echo '<missing>')"; dotnet_status=OK; dotnet_expected="$DOTNET_EXPECTED"; [[ "$DOTNET_ROLL_FORWARD" == latestFeature ]] && dotnet_expected+=" (latestFeature)"; dotnet_version_ok "$dotnet_found" || { dotnet_status='MISSING / WRONG'; missing+=('.NET SDK'); }; printf '%-24s expected %-15s found %-20s %s\n' '.NET SDK' "$dotnet_expected" "$dotnet_found" "$dotnet_status"
add_check 'Node.js' "v$NODE_EXPECTED" "$(version node --version)"
add_check 'pnpm (Corepack)' "$PNPM_EXPECTED" "$(pnpm --version 2>/dev/null || echo '<missing>')"
add_present 'uv' uv
python_path="$(uv python find "$PY_EXPECTED" 2>/dev/null || true)"
add_check 'Python (uv)' "Python $PY_EXPECTED" "$(if [[ -n "$python_path" ]]; then "$python_path" --version 2>/dev/null | sed -E 's/^(Python [0-9]+\.[0-9]+).*/\1/'; else echo '<missing>'; fi)"
add_present 'Podman' podman
add_check 'VS Code extensions' 'present' "$(test -f "$ROOT/.vscode/extensions.json" && echo present || echo '<missing>')"
if (( CHECK )); then ((${#missing[@]}==0)) || exit 1; exit 0; fi
if (( ! SKIP && ${#missing[@]} )); then
  for item in "${missing[@]}"; do pkg=''; case "$item" in
    'Git') install_pkg git || { echo 'Install Git manually and rerun.' >&2; exit 1; };;
    '.NET SDK') install_exact_dotnet; if [[ "$OS" == Linux ]]; then install_pkg dotnet-icu || { echo 'Install the .NET ICU runtime dependency and rerun.' >&2; exit 1; }; fi;;
    'Node.js') install_exact_node;;
    'pnpm (Corepack)'|'Python (uv)') :;; 'uv') install_uv;; 'Podman') pkg=podman;; *) continue;; esac
    [[ -z "$pkg" ]] || install_pkg "$pkg" || { echo "Could not install $item. Install it manually and rerun." >&2; exit 1; }
  done
  if ! command -v corepack >/dev/null; then mkdir -p "$HOME/.local/share/kautuka/corepack" "$HOME/.local/bin"; npm install --prefix "$HOME/.local/share/kautuka/corepack" corepack; export PATH="$HOME/.local/share/kautuka/corepack/node_modules/.bin:$HOME/.local/bin:$PATH"; fi
  corepack enable --install-directory "$HOME/.local/bin"
  corepack prepare "pnpm@$PNPM_EXPECTED" --activate
  command -v uv >/dev/null 2>&1 && uv python install "$PY_EXPECTED"
  persist_tool_path
fi
if (( SKIP )) && ((${#missing[@]})); then echo 'Tool installation skipped. Install missing/wrong-version tools and rerun.' >&2; exit 1; fi
command -v dotnet >/dev/null && command -v pnpm >/dev/null && command -v uv >/dev/null || { echo 'Prerequisites missing; use --skip-install only after installing them.' >&2; exit 1; }
missing=()
printf '\nDoctor summary after prerequisite setup\n'
add_present 'Git' git
dotnet_found="$(dotnet --version 2>/dev/null || echo '<missing>')"; dotnet_status=OK; dotnet_expected="$DOTNET_EXPECTED"; [[ "$DOTNET_ROLL_FORWARD" == latestFeature ]] && dotnet_expected+=" (latestFeature)"; dotnet_version_ok "$dotnet_found" || { dotnet_status='MISSING / WRONG'; missing+=('.NET SDK'); }; printf '%-24s expected %-15s found %-20s %s\n' '.NET SDK' "$dotnet_expected" "$dotnet_found" "$dotnet_status"
add_check 'Node.js' "v$NODE_EXPECTED" "$(version node --version)"
add_check 'pnpm (Corepack)' "$PNPM_EXPECTED" "$(pnpm --version 2>/dev/null || echo '<missing>')"
add_present 'uv' uv
python_path="$(uv python find "$PY_EXPECTED" 2>/dev/null || true)"
add_check 'Python (uv)' "Python $PY_EXPECTED" "$(if [[ -n "$python_path" ]]; then "$python_path" --version 2>/dev/null | sed -E 's/^(Python [0-9]+\.[0-9]+).*/\1/'; else echo '<missing>'; fi)"
add_present 'Podman' podman
add_check 'VS Code extensions' 'present' "$(test -f "$ROOT/.vscode/extensions.json" && echo present || echo '<missing>')"
((${#missing[@]}==0)) || { echo "Tool versions still missing/wrong: ${missing[*]}" >&2; exit 1; }
new_repos=()
for repo in "${REPOS[@]}"; do case "$repo" in kautuka-auth|narad) url="https://github.com/devkrity/$repo.git";; *) echo "Unknown repo: $repo" >&2; exit 2;; esac; dest="$(dirname "$ROOT")/$repo"; if [[ -e "$dest" ]]; then [[ -d "$dest/.git" ]] || { echo "Path exists but is not a Git repository; leaving it untouched: $dest" >&2; exit 1; }; git -C "$dest" fetch --all --prune; else git clone "$url" "$dest"; new_repos+=("$repo"); fi; done
for repo in "${new_repos[@]:-}"; do [[ -n "$repo" ]] || continue; dest="$(dirname "$ROOT")/$repo"; case "$repo" in kautuka-auth) if node -e "process.exit(require('$dest/package.json').scripts && require('$dest/package.json').scripts.setup ? 0 : 1)"; then npm --prefix "$dest" run setup; else npm --prefix "$dest" ci; fi;; narad) if node -e "process.exit(require('$dest/package.json').scripts && require('$dest/package.json').scripts.setup ? 0 : 1)"; then pnpm --dir "$dest" run setup; else pnpm --dir "$dest" install --frozen-lockfile; fi;; esac; done
for example in "$ROOT"/.env.example "$ROOT"/src/backend/Kautuka.Gateway/.env.example "$(dirname "$ROOT")/kautuka-auth/.dev.vars.example"; do [[ -f "$example" ]] || continue; target="${example%.example}.local"; [[ "$example" == */.dev.vars.example ]] && target="${example%.example}"; [[ -e "$target" ]] || cp "$example" "$target"; done
(cd "$ROOT" && dotnet restore src/Kautuka.slnx && pnpm install && dotnet tool restore && uv sync --project mcps/kautuka-web-tools && pnpm --dir src/frontend/web build && dotnet build src/Kautuka.slnx --no-restore)
if ! podman image exists localhost/kautuka-base:latest; then
  echo 'Building the local Kautuka sandbox base image required by Manager.'
  (cd "$ROOT" && KAUTUKA_BASE_LOCAL_TAG=localhost/kautuka-base:latest bash scripts/build-kautuka-base.sh)
fi
(cd "$ROOT" && dotnet husky install)
if [[ -f "$(dirname "$ROOT")/kautuka-auth/.dev.vars" ]]; then echo 'Review blank auth settings as needed: GOOGLE_CLIENT_ID/SECRET, MICROSOFT_CLIENT_ID/SECRET, GOOGLE_LOGIN_CLIENT_ID/SECRET, MICROSOFT_LOGIN_CLIENT_ID/SECRET, AUTH_TICKET_HMAC_KEY, REFRESH_TOKEN_KEYS, REFRESH_TOKEN_ACTIVE_KID.'; fi
echo 'Kautuka provider credentials are entered in the first-run settings UI; setup never retrieves them.'
if command -v code >/dev/null 2>&1 && [[ -t 0 ]]; then read -r -p 'Install VS Code recommended extensions now? [y/N] ' answer; if [[ "$answer" =~ ^[Yy]([Ee][Ss])?$ ]]; then while IFS= read -r ext; do [[ -n "$ext" ]] && code --install-extension "$ext"; done < <(node -e "for (const id of require('$ROOT/.vscode/extensions.json').recommendations) console.log(id)"); fi; fi
printf '\nTip: sign commits off with: git commit -s\n'
missing=()
printf '\nFinal doctor summary\n'
add_present 'Git' git
dotnet_found="$(dotnet --version 2>/dev/null || echo '<missing>')"; dotnet_status=OK; dotnet_expected="$DOTNET_EXPECTED"; [[ "$DOTNET_ROLL_FORWARD" == latestFeature ]] && dotnet_expected+=" (latestFeature)"; dotnet_version_ok "$dotnet_found" || { dotnet_status='MISSING / WRONG'; missing+=('.NET SDK'); }; printf '%-24s expected %-15s found %-20s %s\n' '.NET SDK' "$dotnet_expected" "$dotnet_found" "$dotnet_status"
add_check 'Node.js' "v$NODE_EXPECTED" "$(version node --version)"
add_check 'pnpm (Corepack)' "$PNPM_EXPECTED" "$(pnpm --version 2>/dev/null || echo '<missing>')"
add_present 'uv' uv
python_path="$(uv python find "$PY_EXPECTED" 2>/dev/null || true)"
add_check 'Python (uv)' "Python $PY_EXPECTED" "$(if [[ -n "$python_path" ]]; then "$python_path" --version 2>/dev/null | sed -E 's/^(Python [0-9]+\.[0-9]+).*/\1/'; else echo '<missing>'; fi)"
add_present 'Podman' podman
add_check 'VS Code extensions' 'present' "$(test -f "$ROOT/.vscode/extensions.json" && echo present || echo '<missing>')"
((${#missing[@]}==0)) || { echo "Final doctor found missing/wrong tools: ${missing[*]}" >&2; exit 1; }
printf 'Next: Open in VS Code and press F5 (Launch Kautuka), or run npm run launch.\n'
