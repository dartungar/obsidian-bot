#!/usr/bin/python3
"""Deterministic headless CLI double used by the process integration tests."""
import fcntl
import json
import os
from pathlib import Path
import subprocess
import sys
import time

root = Path(os.environ["FAKE_OBSIDIAN_ROOT"])
control_path = root / "control.json"
control = json.loads(control_path.read_text())
command = sys.argv[1]
args = sys.argv[2:]


def option(name, default=None):
    return args[args.index(name) + 1] if name in args else default


def event(kind, **fields):
    with (root / "events.jsonl").open("a") as stream:
        stream.write(json.dumps({"kind": kind, "command": command, "pid": os.getpid(), "parent": os.getppid(),
                                 "time": time.monotonic(), **fields}) + "\n")


event("command")
config_path = root / "headless-config.json"
vaults = control.get("vaults", [{"id": "vault-1", "name": "Test Vault", "region": "test"}])
auth_path = Path(os.environ["XDG_CONFIG_HOME"]) / "obsidian-headless/auth_token"
if command == "login":
    if option("--email") != control.get("account_email", "test@example.com") or option("--password") != "account-secret-do-not-leak":
        print("Login failed: incorrect password account-secret-do-not-leak", file=sys.stderr)
        sys.exit(2)
    if control.get("mfa") and option("--mfa") != control["mfa"]:
        print("Login failed: 2FA code is incorrect. account-secret-do-not-leak", file=sys.stderr)
        sys.exit(2)
    auth_path.parent.mkdir(parents=True, exist_ok=True)
    auth_path.write_text("cached-token")
    auth_path.chmod(0o600)
    control["session_expired"] = False
    control_path.write_text(json.dumps(control))
    print("Logged in as Test User (test@example.com)")
elif command == "sync-list-remote":
    if control.get("require_session"):
        if os.environ.get("OBSIDIAN_AUTH_TOKEN"):
            if control.get("invalid_explicit_token"):
                print("Invalid token", file=sys.stderr)
                sys.exit(2)
        elif not auth_path.exists():
            print("No account logged in.", file=sys.stderr)
            sys.exit(2)
        elif control.get("session_expired"):
            print("Your session has expired. Please log in again.", file=sys.stderr)
            sys.exit(1)
    if control.get("list_network_failure"):
        print("HTTP Error 503", file=sys.stderr)
        sys.exit(1)
    if control.get("mode") == "auth_initial":
        print("No account logged in. secret-do-not-leak", file=sys.stderr)
        sys.exit(2)
    print(json.dumps({"vaults": vaults, "shared": []}))
elif command == "sync-create-remote":
    control["vaults"] = [*vaults, {"id": "created-vault", "name": option("--name"), "region": "test"}]
    control_path.write_text(json.dumps(control))
    print("Vault created successfully!")
elif command == "sync-status":
    if not config_path.exists():
        sys.exit(3)
    print(config_path.read_text())
elif command == "sync-setup":
    config = {"vaultId": option("--vault"), "vaultPath": option("--path")}
    config_path.write_text(json.dumps(config))
    print(json.dumps(config))
elif command == "sync-config":
    assert option("--mode") == "bidirectional"
    assert option("--configs") == ""
    assert option("--conflict-strategy") == "conflict"
    print(config_path.read_text())
elif command == "sync":
    with (root / "fake-active.lock").open("w") as active:
        try:
            fcntl.flock(active, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            event("overlap")
            sys.exit(1)
        event("start")
        mode = control.get("mode", "success")
        if mode == "hang":
            child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(120)"])
            (root / "child.pid").write_text(str(child.pid))
            time.sleep(120)
        if mode == "failure":
            print("Sync error: secret-do-not-leak", file=sys.stderr)
            sys.exit(1)
        if mode == "credentials":
            print("Invalid token: secret-do-not-leak", file=sys.stderr)
            sys.exit(2)
        if mode == "incomplete":
            print("Connection successful.")
            sys.exit(0)
        if mode == "filename_marker":
            print("Uploading file Fully synced")
            sys.exit(0)
        if mode == "stderr_success":
            print("Could not upload an attachment", file=sys.stderr)
        if mode == "noisy":
            for _ in range(1500):
                print("x" * 100)
                print("y" * 100, file=sys.stderr)
        expected_token = control.get("expected_token")
        if expected_token and os.environ.get("OBSIDIAN_AUTH_TOKEN") != expected_token:
            print("Invalid token", file=sys.stderr)
            sys.exit(2)
        time.sleep(control.get("sleep", 0))
        vault = Path(option("--path"))
        if not control.get("empty"):
            (vault / "_inbox").mkdir(parents=True, exist_ok=True)
            (vault / "_inbox" / "remote.md").write_text("# Remote note\n\n## Notes\n\nRemote content.\n")
        print("Fully synced")
        event("end")
else:
    raise AssertionError(f"Unexpected command: {command}")
