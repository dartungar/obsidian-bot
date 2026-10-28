"""Run against the built app: python3 -m unittest discover -s tests -v.

Uses real .NET processes, HTTP, filesystem locks, and child-process termination.
No Obsidian account, Telegram connection, or remote vault is used.
"""
from concurrent.futures import ThreadPoolExecutor
import fcntl
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import unittest
import shutil
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
import uuid
import zipfile

REPO = Path(__file__).resolve().parents[1]
DLL = REPO / "bin/Debug/net10.0/ObsidianBot.dll"
FAKE = REPO / "tests/fake_ob.py"


class SyncIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="obsidian-sync-test-")
        self.root = Path(self.temporary.name)
        self.processes = []
        self.logs = []
        self.control(mode="success")

    def tearDown(self):
        for process in reversed(self.processes):
            self.stop(process)
        for log in self.logs:
            log.close()
        self.temporary.cleanup()

    def control(self, **settings):
        path = self.root / "control.json"
        temporary = path.with_suffix(".tmp")
        temporary.write_text(json.dumps(settings))
        temporary.replace(path)

    def events(self, command=None, kind=None):
        path = self.root / "events.jsonl"
        events = [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []
        return [event for event in events if (command is None or event["command"] == command)
                and (kind is None or event["kind"] == kind)]

    def wait(self, predicate, timeout=10):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            result = predicate()
            if result:
                return result
            for process in self.processes:
                if process.poll() is not None and process.returncode not in (0, -15):
                    self.fail("App exited unexpectedly:\n" + "\n".join(path.read_text() for path in self.root.glob("app-*.log")))
            time.sleep(0.05)
        self.fail("Condition timed out. Status: " + str(self.status()) + "\n" +
                  "\n".join(path.read_text()[-6000:] for path in self.root.glob("app-*.log")))

    def status(self):
        try:
            return json.loads((self.root / "state/status.json").read_text())
        except (FileNotFoundError, json.JSONDecodeError):
            return {}

    def start(self, role="sync", **settings):
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        env = {key: value for key, value in os.environ.items()
               if not key.startswith(("OBSIDIAN_", "TELEGRAM_", "ASPNETCORE_"))}
        env.update({
            "ASPNETCORE_URLS": f"http://127.0.0.1:{port}",
            "OBSIDIAN_COMPONENT_ROLE": role,
            "OBSIDIAN_SYNC_ENABLED": "true",
            "OBSIDIAN_VAULT_PATH": str(self.root / "vault"),
            "OBSIDIAN_SYNC_STATE_DIRECTORY": str(self.root / "state"),
            "OBSIDIAN_SYNC_CONFIG_DIRECTORY": str(self.root / "private"),
            "OBSIDIAN_SYNC_EXECUTABLE": str(FAKE),
            "OBSIDIAN_SYNC_VAULT": "vault-1",
            "OBSIDIAN_AUTH_TOKEN": "secret-do-not-leak",
            "OBSIDIAN_SYNC_PASSWORD": "secret-do-not-leak",
            "OBSIDIAN_SYNC_INTERVAL_SECONDS": "1",
            "OBSIDIAN_SYNC_TIMEOUT_SECONDS": "2",
            "OBSIDIAN_SYNC_INITIAL_TIMEOUT_SECONDS": "3",
            "OBSIDIAN_SYNC_STALE_AFTER_SECONDS": "2",
            "OBSIDIAN_SYNC_MAX_BACKOFF_SECONDS": "4",
            "OBSIDIAN_SYNC_ATTENTION_RETRY_SECONDS": "4",
            "OBSIDIAN_SEARCH_DATABASE_PATH": str(self.root / f"{role}-search.db"),
            "OBSIDIAN_PROPOSAL_DATABASE_PATH": str(self.root / "proposals.db"),
            "OBSIDIAN_PROPOSAL_SNAPSHOT_DIRECTORY": str(self.root / "snapshots"),
            "OBSIDIAN_AGENT_API_TOKEN": "test-agent",
            "OBSIDIAN_REVIEW_API_TOKEN": "test-reviewer",
            "FAKE_OBSIDIAN_ROOT": str(self.root),
            "Logging__LogLevel__Microsoft.AspNetCore": "Warning",
        })
        env.update(settings)
        log = (self.root / f"app-{len(self.processes)}.log").open("w")
        self.logs.append(log)
        process = subprocess.Popen(["dotnet", str(DLL)], cwd=REPO, env=env, stdout=log, stderr=subprocess.STDOUT)
        self.processes.append(process)
        self.wait(lambda: self.request(port, "/healthz")[0] == 200)
        return process, port

    @staticmethod
    def stop(process):
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=8)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=3)

    @staticmethod
    def request(port, path, token=None, body=None):
        headers = {}
        if token:
            headers["Authorization"] = "Bearer " + token
        if body is not None:
            headers.update({"Content-Type": "application/json", "Idempotency-Key": str(uuid.uuid4())})
        request = Request(f"http://127.0.0.1:{port}" + path,
                          data=json.dumps(body).encode() if body is not None else None, headers=headers)
        try:
            with urlopen(request, timeout=8) as response:
                return response.status, json.load(response)
        except HTTPError as error:
            with error:
                return error.code, json.load(error)
        except (URLError, ConnectionError):
            return 0, {}

    def test_success_schedule_and_restart_preserve_identity_and_offline_availability(self):
        owner, _ = self.start()
        self.wait(lambda: len(self.events("sync", "end")) >= 2)
        rounds = self.events("sync", "start")
        ends = self.events("sync", "end")
        self.assertGreaterEqual(rounds[1]["time"] - ends[0]["time"], 0.95)
        self.assertTrue(self.status()["initialized"])
        self.stop(owner)
        previous_success = self.status()["lastSuccessfulSyncAt"]
        self.control(mode="failure")
        self.start()
        self.wait(lambda: self.status().get("consecutiveFailures", 0) >= 1)
        self.assertTrue(self.status()["initialized"])
        self.assertEqual(previous_success, self.status()["lastSuccessfulSyncAt"])
        self.assertEqual(1, len(self.events("sync-setup", "command")))

    def test_initial_readiness_and_authenticated_status_api(self):
        _, api = self.start("agent-api")
        self.assertEqual(503, self.request(api, "/readyz")[0])
        health = subprocess.run(["dotnet", str(DLL), "--health-check", f"http://127.0.0.1:{api}/readyz"], timeout=8)
        self.assertEqual(1, health.returncode)
        self.assertEqual(503, self.request(api, "/v1/notes?q=test", "test-agent")[0])
        self.assertEqual(401, self.request(api, "/v1/sync/status")[0])
        self.assertEqual(200, self.request(api, "/v1/sync/status", "test-reviewer")[0])
        self.start()
        self.wait(lambda: self.status().get("state") == "healthy")
        self.assertEqual(200, self.request(api, "/readyz")[0])
        health = subprocess.run(["dotnet", str(DLL), "--health-check", f"http://127.0.0.1:{api}/readyz"], timeout=8)
        self.assertEqual(0, health.returncode)
        self.assertEqual(200, self.request(api, "/v1/notes?q=test", "test-agent")[0])
        document = self.request(api, "/openapi/v1.json")[1]
        self.assertIn("/v1/sync/status", document["paths"])

    def test_exit_zero_without_exact_completion_is_unhealthy(self):
        for mode in ("incomplete", "filename_marker", "stderr_success", "noisy"):
            with self.subTest(mode=mode):
                self.control(mode=mode)
                attempts = len(self.events("sync", "start"))
                process, _ = self.start()
                self.wait(lambda: len(self.events("sync", "start")) > attempts)
                self.wait(lambda: self.status().get("state") == "degraded")
                self.assertEqual("SYNC_INCOMPLETE", self.status()["errorCode"])
                self.assertFalse(self.status()["initialized"])
                self.assertIsNone(self.status()["lastSuccessfulSyncAt"])
                self.stop(process)

    def test_timeout_kills_child_tree_and_recovery_initializes_vault(self):
        self.control(mode="hang")
        _, port = self.start(OBSIDIAN_SYNC_INITIAL_TIMEOUT_SECONDS="1")
        self.wait(lambda: self.status().get("errorCode") == "SYNC_TIMEOUT")
        child = int((self.root / "child.pid").read_text())
        proc = Path(f"/proc/{child}/stat")
        self.wait(lambda: not proc.exists() or proc.read_text().split()[2] == "Z")
        self.assertEqual(503, self.request(port, "/sync-healthz")[0])
        self.control(mode="success")
        self.wait(lambda: self.status().get("state") == "healthy")
        self.assertTrue(self.status()["initialized"])
        self.assertEqual(0, self.status()["consecutiveFailures"])

    def test_shutdown_terminates_inflight_child(self):
        self.control(mode="hang")
        owner, _ = self.start(OBSIDIAN_SYNC_INITIAL_TIMEOUT_SECONDS="120")
        self.wait(lambda: (self.root / "child.pid").exists())
        child = int((self.root / "child.pid").read_text())
        self.stop(owner)
        proc = Path(f"/proc/{child}/stat")
        self.wait(lambda: not proc.exists() or proc.read_text().split()[2] == "Z")

    def test_existing_vault_requires_opt_in_and_is_backed_up_before_adoption(self):
        vault = self.root / "vault"
        vault.mkdir()
        (vault / "precious.md").write_text("Keep this content.")
        owner, _ = self.start()
        self.wait(lambda: self.status().get("errorCode") == "SYNC_EXISTING_VAULT")
        self.assertEqual([], self.events("sync", "start"))
        self.stop(owner)
        self.start(OBSIDIAN_SYNC_ADOPT_EXISTING="true")
        self.wait(lambda: self.status().get("state") == "healthy")
        backup = next((self.root / "state").glob("initial-vault-backup-*.zip"))
        with zipfile.ZipFile(backup) as archive:
            self.assertEqual(b"Keep this content.", archive.read("precious.md"))
        self.assertEqual("Keep this content.", (vault / "precious.md").read_text())

    def test_remote_creation_is_explicit_encrypted_and_not_repeated(self):
        self.control(mode="success", vaults=[])
        owner, _ = self.start(OBSIDIAN_SYNC_VAULT="New Vault")
        self.wait(lambda: self.status().get("errorCode") == "SYNC_VAULT_SELECTION")
        self.assertEqual([], self.events("sync-create-remote", "command"))
        self.stop(owner)
        owner, _ = self.start(OBSIDIAN_SYNC_VAULT="New Vault", OBSIDIAN_SYNC_CREATE_REMOTE="true")
        self.wait(lambda: self.status().get("state") == "healthy")
        self.stop(owner)
        self.start(OBSIDIAN_SYNC_VAULT="New Vault", OBSIDIAN_SYNC_CREATE_REMOTE="true")
        self.wait(lambda: len(self.events("sync", "end")) >= 2)
        self.assertEqual(1, len(self.events("sync-create-remote", "command")))

    def test_changed_remote_identity_is_refused(self):
        owner, _ = self.start()
        self.wait(lambda: self.status().get("state") == "healthy")
        self.stop(owner)
        self.start(OBSIDIAN_SYNC_VAULT="different-vault")
        self.wait(lambda: self.status().get("errorCode") == "SYNC_IDENTITY_CHANGED")
        self.assertFalse(self.status()["initialized"])
        self.assertEqual(1, len(self.events("sync-setup", "command")))

    def test_empty_remote_prepares_writable_folders_for_first_capture(self):
        self.control(mode="success", empty=True)
        self.start(OBSIDIAN_AGENT_WRITABLE_FOLDERS="_inbox,projects/new")
        self.wait(lambda: self.status().get("state") == "healthy")
        self.assertTrue((self.root / "vault/_inbox").is_dir())
        self.assertTrue((self.root / "vault/projects/new").is_dir())
        self.assertTrue((self.root / "vault/.obsidian-bot/sync-instance").is_file())

    def test_missing_owner_is_stale_but_initialized_vault_remains_available(self):
        owner, _ = self.start()
        self.wait(lambda: self.status().get("state") == "healthy")
        self.stop(owner)
        _, api = self.start("agent-api")
        self.wait(lambda: self.request(api, "/v1/sync/status", "test-agent")[1].get("state") == "stale", timeout=8)
        self.assertEqual(200, self.request(api, "/readyz")[0])
        self.assertEqual(503, self.request(api, "/sync-healthz")[0])

    def test_replaced_local_volume_cannot_propagate_mass_deletions(self):
        owner, _ = self.start()
        self.wait(lambda: self.status().get("state") == "healthy")
        self.stop(owner)
        rounds = len(self.events("sync", "start"))
        shutil.rmtree(self.root / "vault")
        (self.root / "vault").mkdir()
        self.start()
        self.wait(lambda: self.status().get("errorCode") == "SYNC_LOCAL_VAULT_CHANGED")
        self.assertFalse(self.status()["initialized"])
        self.assertEqual(rounds, len(self.events("sync", "start")))

    def test_credentials_are_redacted_and_retried_slowly(self):
        self.control(mode="credentials")
        _, port = self.start()
        self.wait(lambda: self.status().get("state") == "needs-attention")
        self.assertEqual("SYNC_CREDENTIALS", self.status()["errorCode"])
        time.sleep(1.2)
        self.assertEqual(1, len(self.events("sync", "start")))
        self.assertEqual(503, self.request(port, "/sync-healthz")[0])
        self.assertNotIn("secret-do-not-leak", (self.root / "state/status.json").read_text())
        self.assertNotIn("secret-do-not-leak", "".join(path.read_text() for path in self.root.glob("app-*.log")))

    def test_token_file_is_reloaded_on_each_round(self):
        token = self.root / "token"
        token.write_text("first-token\n")
        self.control(mode="success", expected_token="first-token")
        self.start(OBSIDIAN_AUTH_TOKEN_FILE=str(token))
        self.wait(lambda: self.status().get("state") == "healthy")
        token.write_text("rotated-token\n")
        self.control(mode="success", expected_token="rotated-token")
        self.wait(lambda: len(self.events("sync", "end")) >= 2)
        self.assertEqual(0, self.status()["consecutiveFailures"])

    def test_two_owners_never_overlap_and_waiting_owner_takes_over(self):
        self.control(mode="success", sleep=0.3)
        first, _ = self.start()
        self.wait(lambda: self.status().get("state") == "healthy")
        second, _ = self.start()
        self.wait(lambda: len(self.events("sync", "end")) >= 2)
        self.assertTrue(all(event["parent"] == first.pid for event in self.events("sync", "start")))
        self.stop(first)
        previous = len(self.events("sync", "end"))
        self.wait(lambda: len(self.events("sync", "end")) > previous)
        self.assertEqual(second.pid, self.events("sync", "start")[-1]["parent"])
        self.assertEqual([], self.events(kind="overlap"))

    def test_sync_and_api_writes_honor_cross_process_vault_lock(self):
        owner, _ = self.start(OBSIDIAN_SYNC_INTERVAL_SECONDS="10")
        self.wait(lambda: self.status().get("state") == "healthy")
        self.stop(owner)
        _, api = self.start("agent-api")
        capabilities = self.request(api, "/v1/capabilities", "test-agent")[1]
        folder = capabilities["writableFolders"][0]["id"]
        body = {"operation": "create_note", "folderId": folder, "filename": "local.md",
                "contentMarkdown": "Local capture.", "rationale": "Integration test.", "onConflict": "reject",
                "origin": {"conversationId": "test", "requestExcerpt": "Create a local capture."}}
        with (self.root / "state/vault.lock").open("r+") as lease:
            fcntl.flock(lease, fcntl.LOCK_EX)
            with ThreadPoolExecutor() as pool:
                request = pool.submit(self.request, api, "/v1/note-changes", "test-agent", body)
                time.sleep(0.3)
                self.assertFalse(request.done(), "API write bypassed the vault lock")
                self.assertFalse((self.root / "vault/_inbox/local.md").exists())
                fcntl.flock(lease, fcntl.LOCK_UN)
                self.assertEqual(201, request.result()[0])
        self.assertIn("Local capture.", (self.root / "vault/_inbox/local.md").read_text())
        before = len(self.events("sync", "start"))
        with (self.root / "state/vault.lock").open("r+") as lease:
            fcntl.flock(lease, fcntl.LOCK_EX)
            self.start()
            time.sleep(0.3)
            self.assertEqual(before, len(self.events("sync", "start")))
            fcntl.flock(lease, fcntl.LOCK_UN)
        self.wait(lambda: len(self.events("sync", "start")) > before)
        self.assertFalse(list((self.root / "vault").rglob("*.obsidian-bot.lock")))

    def test_disabled_sync_preserves_standalone_operation(self):
        _, api = self.start("agent-api", OBSIDIAN_SYNC_ENABLED="false")
        self.assertEqual(200, self.request(api, "/readyz")[0])
        self.assertEqual("disabled", self.request(api, "/v1/sync/status", "test-agent")[1]["state"])
        self.assertEqual([], self.events())


if __name__ == "__main__":
    unittest.main()
