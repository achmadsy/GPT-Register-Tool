"""Import existing account sessions into local storage without network access."""

from __future__ import annotations

import hashlib
import json
import os
import tempfile
from pathlib import Path
from typing import Any

from ..config import ConfigInput
from ..paths import output_dir
from ..storage import database_path, get_account_record, upsert_account


def _text(value: Any) -> str:
    return value.strip() if isinstance(value, str) else ""


def _session_payload(data: Any) -> dict[str, Any]:
    if isinstance(data, list):
        raise ValueError("Expected a single session JSON object, not an array; select one account per file")
    if not isinstance(data, dict):
        raise ValueError("Expected a single session JSON object")
    auth = data.get("auth_session") if isinstance(data.get("auth_session"), dict) else {}
    user = data.get("user") if isinstance(data.get("user"), dict) else {}
    auth_user = auth.get("user") if isinstance(auth.get("user"), dict) else {}
    email = next((value for value in (
        _text(data.get("email")), _text(user.get("email")),
        _text(auth.get("email")), _text(auth_user.get("email"))
    ) if value), "")
    access_token = next((value for value in (
        _text(data.get("access_token")), _text(data.get("accessToken")),
        _text(auth.get("access_token")), _text(auth.get("accessToken"))
    ) if value), "")
    refresh_token = next((value for value in (
        _text(data.get("oauth_refresh_token")), _text(data.get("refresh_token")),
        _text(auth.get("refresh_token")), _text(auth.get("refreshToken"))
    ) if value), "")
    session_token = next((value for value in (
        _text(data.get("session_token")), _text(data.get("sessionToken")),
        _text(auth.get("session_token")), _text(auth.get("sessionToken"))
    ) if value), "")
    if not email or email.count("@") != 1 or any(char.isspace() for char in email):
        raise ValueError("Missing valid account email")
    if not (access_token or refresh_token or session_token):
        raise ValueError("Missing access, refresh, or session token")
    return {
        "email": email,
        "access_token": access_token,
        "oauth_refresh_token": refresh_token,
        "session_token": session_token,
        "cookie_header": _text(data.get("cookie_header")),
        "auth_session": auth,
        "source": "local_session_import",
        "registration_state": "active",
        "status": "imported",
        "success": True,
    }


def import_local_sessions(paths: list[str], *, runtime_config: ConfigInput = None, session_dir: str | Path | None = None) -> dict[str, Any]:
    """Keep imported credentials local; never replace an existing account."""
    if session_dir is not None:
        destination = Path(session_dir)
    else:
        config = runtime_config.as_dict() if hasattr(runtime_config, "as_dict") else (runtime_config or {})
        destination = output_dir(config)
    imported = 0
    skipped = 0
    results: list[dict[str, str]] = []
    seen: set[str] = set()
    for index, filename in enumerate(paths, start=1):
        source = Path(filename)
        label = f"File {index}"
        try:
            data = json.loads(source.read_text(encoding="utf-8-sig"))
            payload = _session_payload(data)
            email = payload["email"]
            key = email.casefold()
            if key in seen or get_account_record(email, runtime_config=runtime_config):
                skipped += 1
                results.append({"file": label, "status": "skipped", "reason": "Account already exists"})
                continue
            seen.add(key)
            destination.mkdir(parents=True, exist_ok=True)
            with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=destination,
                                             prefix=".session_import_", suffix=".tmp", delete=False) as handle:
                temp_path = Path(handle.name)
                os.chmod(temp_path, 0o600)
                json.dump(payload, handle, ensure_ascii=False)
            target = destination / ("session_import_" + hashlib.sha256(key.encode("utf-8")).hexdigest()[:24] + ".json")
            created = False
            try:
                if target.exists():
                    raise ValueError("Session file already exists")
                temp_path.replace(target)
                created = True
                if not upsert_account(payload, json_path=str(target), runtime_config=runtime_config):
                    raise ValueError("Could not save account")
            except Exception:
                if created:
                    target.unlink(missing_ok=True)
                raise
            finally:
                temp_path.unlink(missing_ok=True)
            imported += 1
            results.append({"file": label, "status": "imported"})
        except (OSError, UnicodeError, ValueError, TypeError) as exc:
            skipped += 1
            # Never echo untrusted exception text: it can contain session contents.
            reason = str(exc) if isinstance(exc, ValueError) and str(exc) in {
                "Expected a single session JSON object",
                "Expected a single session JSON object, not an array; select one account per file",
                "Missing valid account email",
                "Missing access, refresh, or session token", "Session file already exists",
                "Could not save account",
            } else "Unreadable or invalid JSON file"
            results.append({"file": label, "status": "skipped", "reason": reason})
    return {
        "ok": imported > 0,
        "imported": imported,
        "skipped": skipped,
        "results": results,
        # Safe path metadata so the desktop can show (and cross-check) where
        # imported sessions and the SQLite index live. Paths only, no tokens.
        "session_dir": str(destination),
        "database_path": str(database_path(runtime_config)),
    }
