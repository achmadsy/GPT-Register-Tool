import json
import os

import pytest

from sms_tool.accounts import local_session_import


class ImportHarness:
    def __init__(self):
        self.saved = []

    def upsert(self, payload, *, json_path, runtime_config=None):
        self.saved.append((payload, json_path))
        return True


def test_imports_top_level_session_locally_without_echoing_tokens(tmp_path, monkeypatch):
    token = "access-secret-value"
    source = tmp_path / "source.json"
    source.write_text(json.dumps({"email": "User@example.com", "access_token": token}), encoding="utf-8")
    destination = tmp_path / "sessions"
    harness = ImportHarness()
    monkeypatch.setattr(local_session_import, "get_account_record", lambda *args, **kwargs: {})
    monkeypatch.setattr(local_session_import, "upsert_account", harness.upsert)

    result = local_session_import.import_local_sessions([str(source)], session_dir=destination)

    assert result == {
        "ok": True,
        "imported": 1,
        "skipped": 0,
        "results": [{"file": "File 1", "status": "imported"}],
    }
    assert len(harness.saved) == 1
    payload, path = harness.saved[0]
    assert payload["email"] == "User@example.com"
    assert payload["access_token"] == token
    assert token not in json.dumps(result)
    # Windows (NTFS) does not expose POSIX permission bits, so the 0600
    # chmod in the importer is only observable on POSIX systems.
    if os.name != "nt":
        assert os.stat(path).st_mode & 0o077 == 0


def test_imports_nested_auth_session_and_deduplicates(tmp_path, monkeypatch):
    source_a = tmp_path / "a.json"
    source_b = tmp_path / "b.json"
    nested = {"auth_session": {"user": {"email": "nested@example.com"}, "refreshToken": "refresh-secret"}}
    source_a.write_text(json.dumps(nested), encoding="utf-8")
    source_b.write_text(json.dumps(nested), encoding="utf-8")
    harness = ImportHarness()
    monkeypatch.setattr(local_session_import, "get_account_record", lambda *args, **kwargs: {})
    monkeypatch.setattr(local_session_import, "upsert_account", harness.upsert)

    result = local_session_import.import_local_sessions(
        [str(source_a), str(source_b)], session_dir=tmp_path / "sessions"
    )

    assert result["imported"] == 1
    assert result["skipped"] == 1
    assert result["results"][1]["reason"] == "Account already exists"


def test_skips_invalid_json_without_leaking_content(tmp_path, monkeypatch):
    secret = "malformed-secret-value"
    source = tmp_path / "broken.json"
    source.write_text("not-json-" + secret, encoding="utf-8")
    monkeypatch.setattr(local_session_import, "get_account_record", lambda *args, **kwargs: {})

    result = local_session_import.import_local_sessions([str(source)], session_dir=tmp_path / "sessions")

    assert result["ok"] is False
    assert result["skipped"] == 1
    assert result["results"][0]["reason"] == "Unreadable or invalid JSON file"
    assert secret not in json.dumps(result)


def test_rejects_missing_email_or_token(tmp_path, monkeypatch):
    missing_email = tmp_path / "missing-email.json"
    missing_email.write_text(json.dumps({"access_token": "secret"}), encoding="utf-8")
    missing_token = tmp_path / "missing-token.json"
    missing_token.write_text(json.dumps({"email": "missing-token@example.com"}), encoding="utf-8")
    monkeypatch.setattr(local_session_import, "get_account_record", lambda *args, **kwargs: {})

    result = local_session_import.import_local_sessions(
        [str(missing_email), str(missing_token)], session_dir=tmp_path / "sessions"
    )

    assert result["imported"] == 0
    assert result["skipped"] == 2
    assert result["results"][0]["reason"] == "Missing valid account email"
    assert result["results"][1]["reason"] == "Missing access, refresh, or session token"
