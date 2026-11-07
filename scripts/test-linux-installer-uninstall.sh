#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
prompt_capture="$(mktemp)"
test_root=''
trap 'rm -f "$prompt_capture"; [[ -z "$test_root" ]] || rm -rf "$test_root"' EXIT

actual_delete_data="$(KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 bash -c '
  source "$1/scripts/kautuka-install-linux.sh.in"
  confirm_uninstall true <<<"UNINSTALL"
' _ "$repo_root" 2>"$prompt_capture")"
[[ "$actual_delete_data" == 'true' ]] || {
  echo "Expected --delete-data confirmation to return only true; got: $actual_delete_data" >&2
  exit 1
}
grep -Fq 'The preserved Kautuka databases and agent workspace files will also be deleted.' "$prompt_capture" || {
  echo '--delete-data did not tell the user that persistent data will be deleted.' >&2
  exit 1
}
grep -Fq 'Type UNINSTALL to continue, or press Enter to cancel.' "$prompt_capture" || {
  echo '--delete-data did not show its explicit uninstall confirmation.' >&2
  exit 1
}

actual_preserve_data="$(printf 'n\nUNINSTALL\n' | KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 bash -c '
  source "$1/scripts/kautuka-install-linux.sh.in"
  confirm_uninstall false
' _ "$repo_root" 2>/dev/null)"
[[ "$actual_preserve_data" == 'false' ]] || {
  echo "Expected the default confirmation to return only false; got: $actual_preserve_data" >&2
  exit 1
}

actual_yes_data="$(KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 bash -c '
  source "$1/scripts/kautuka-install-linux.sh.in"
  resolve_uninstall_delete_data false true
' _ "$repo_root")"
[[ "$actual_yes_data" == 'true' ]] || {
  echo "Expected --yes to confirm data deletion; got: $actual_yes_data" >&2
  exit 1
}

actual_explicit_delete_data="$(KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 bash -c '
  source "$1/scripts/kautuka-install-linux.sh.in"
  resolve_uninstall_delete_data true false
' _ "$repo_root")"
[[ "$actual_explicit_delete_data" == 'true' ]] || {
  echo "Expected --delete-data to remain affirmative; got: $actual_explicit_delete_data" >&2
  exit 1
}

test_root="$(mktemp -d)"

KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 bash -c '
  set -euo pipefail
  source "$1/scripts/kautuka-install-linux.sh.in"
  test_root="$2"
  guard_uninstall_transaction() { :; }
  uninstall_container_layout() { printf "false\n"; }

  best_effort_remove_tree() {
    case "$1" in
      "$test_root"/*) rm -rf -- "$1" ;;
      *) : ;;
    esac
  }
  best_effort_remove_file() { :; }
  best_effort_remove_dir_if_empty() { :; }
  best_effort_systemctl() { :; }
  best_effort_user_systemctl() { :; }
  best_effort_loginctl() { :; }
  clear_package_import_temp_state() { :; }
  remove_service_account_if_safe() { :; }

  internal="$test_root/internal"
  visible="$test_root/visible"
  install="$test_root/install"
  delete_data="$(resolve_uninstall_delete_data false true)"
  mkdir -p "$internal" "$visible/workspaces/agents/demo/.kautuka" "$install"
  printf state > "$internal/kautuka.db"
  printf workspace > "$visible/workspaces/agents/demo/notes.txt"

  output="$(uninstall_release "$install" "$internal" "$visible" test.service kautuka-test "$delete_data")"
  [[ ! -e "$internal" && ! -e "$visible" ]] || {
    echo "--delete-data left Kautuka data behind: $internal $visible" >&2
    exit 1
  }
  [[ "$output" == *"Deleted Kautuka data: $internal"* && "$output" == *"Deleted Kautuka data: $visible"* ]] || {
    echo "--delete-data did not report successful deletion: $output" >&2
    exit 1
  }

  mkdir -p "$internal" "$visible/workspaces/agents/demo/.kautuka"
  mkdir -p "$internal/gateway-data/nested"
  printf key > "$internal/gateway-data/gateway-auth.key"
  printf registry > "$internal/gateway-data/kautuka.db"
  printf content > "$internal/gateway-data/nested/application-state"
  printf state > "$internal/kautuka.db"
  printf workspace > "$visible/workspaces/agents/demo/notes.txt"
  output="$(uninstall_release "$install" "$internal" "$visible" test.service kautuka-test false)"
  [[ -f "$internal/kautuka.db" && -f "$visible/workspaces/agents/demo/notes.txt" ]] || {
    echo "Default uninstall did not preserve Kautuka data." >&2
    exit 1
  }
  [[ "$(cat "$internal/gateway-data/gateway-auth.key")" == key && "$(cat "$internal/gateway-data/nested/application-state")" == content ]]
  [[ "$(cat "$internal/gateway-data/kautuka.db")" == registry && ! -e "$internal/gateway-data/gateway-data" ]]
  [[ ! -e "$visible/workspaces/agents/demo/.kautuka" ]] || {
    echo "Default uninstall left generated workspace state behind." >&2
    exit 1
  }

  shared="$test_root/shared"
  mkdir -p "$shared/workspaces/agents/demo"
  printf state > "$shared/kautuka.db"
  output="$(uninstall_release "$install" "$shared" "$shared" test.service kautuka-test true)"
  [[ ! -e "$shared" && "$output" == *"Deleted Kautuka data: $shared"* ]] || {
    echo "Uninstall did not remove a shared internal/visible data root: $shared" >&2
    exit 1
  }
' _ "$repo_root" "$test_root"

echo 'Linux installer uninstall confirmation tests passed.'
