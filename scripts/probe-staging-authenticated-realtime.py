#!/usr/bin/env python3
"""Fail-closed staging descriptor and Phoenix WebSocket contract probe.

The probe consumes only the dedicated staging HMAC key file. Short-lived
Guardian and membership credentials are kept in memory and are never printed.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import http.client
import json
import os
import re
import secrets
import socket
import ssl
import struct
import time
import uuid
from datetime import datetime, timezone
from urllib.parse import quote, urlsplit


HOST = "app.jeeb.fds-1.com"
DESCRIPTOR_PATH = "/internal/ops/staging/realtime-probe-descriptor"
EXACT_SOCKET_URL = f"wss://{HOST}/socket/websocket"
KEY_FILE_ENV = "STAGING_REALTIME_PROBE_KEY_FILE"
MAX_HTTP_BODY_BYTES = 1024 * 1024
MAX_WS_MESSAGE_BYTES = 1024 * 1024
WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
RFC3339_TIMESTAMP = re.compile(
    r"^(?P<date>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})"
    r"(?:\.(?P<fraction>\d{1,7}))?"
    r"(?P<offset>Z|[+-]\d{2}:\d{2})$"
)


def parse_rfc3339(value: object) -> datetime:
    """Parse the strict RFC3339 shape emitted by .NET on Python 3.10+."""
    if not isinstance(value, str):
        raise RuntimeError("descriptor expiry was not an RFC3339 string")
    match = RFC3339_TIMESTAMP.fullmatch(value)
    if match is None:
        raise RuntimeError("descriptor expiry was not strict RFC3339")

    fraction = match.group("fraction")
    normalized = match.group("date")
    if fraction is not None:
        normalized += "." + (fraction + "000000")[:6]
    normalized += "+00:00" if match.group("offset") == "Z" else match.group("offset")
    try:
        return datetime.fromisoformat(normalized)
    except ValueError as error:
        raise RuntimeError(
            "descriptor expiry was not a valid RFC3339 timestamp"
        ) from error


def descriptor_request(headers: dict[str, str]) -> tuple[int, dict[str, str], bytes]:
    connection = http.client.HTTPSConnection(
        HOST,
        443,
        timeout=20,
        context=ssl.create_default_context(),
    )
    try:
        connection.request("POST", DESCRIPTOR_PATH, body=b"", headers=headers)
        response = connection.getresponse()
        body = response.read(MAX_HTTP_BODY_BYTES + 1)
        if len(body) > MAX_HTTP_BODY_BYTES:
            raise RuntimeError("descriptor response exceeded the validation bound")
        response_headers = {
            name.lower(): value for name, value in response.getheaders()
        }
        return response.status, response_headers, body
    finally:
        connection.close()


def signed_headers(key: bytes, nonce: str, timestamp: str) -> dict[str, str]:
    canonical = f"v1\nPOST\n{DESCRIPTOR_PATH}\n{timestamp}\n{nonce}".encode()
    signature = hmac.new(key, canonical, hashlib.sha256).hexdigest()
    return {
        "X-Jeeb-Staging-Probe-Timestamp": timestamp,
        "X-Jeeb-Staging-Probe-Nonce": nonce,
        "X-Jeeb-Staging-Probe-Signature": signature,
    }


class PhoenixWebSocket:
    def __init__(self, socket_url: str, token: str):
        parsed = urlsplit(socket_url)
        if (
            parsed.scheme != "wss"
            or parsed.hostname != HOST
            or parsed.port not in (None, 443)
            or parsed.path != "/socket/websocket"
            or parsed.query
            or parsed.fragment
        ):
            raise RuntimeError("descriptor socket URL is outside the exact contract")

        raw = socket.create_connection((HOST, 443), timeout=20)
        self._socket = ssl.create_default_context().wrap_socket(
            raw, server_hostname=HOST
        )
        self._socket.settimeout(20)
        self._buffer = bytearray()
        websocket_key = base64.b64encode(secrets.token_bytes(16)).decode("ascii")
        request_target = (
            parsed.path
            + "?token="
            + quote(token, safe="")
            + "&vsn=2.0.0"
        )
        request = (
            f"GET {request_target} HTTP/1.1\r\n"
            f"Host: {HOST}\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            f"Sec-WebSocket-Key: {websocket_key}\r\n"
            "Sec-WebSocket-Version: 13\r\n"
            f"Origin: https://{HOST}\r\n"
            "\r\n"
        ).encode("ascii")
        self._socket.sendall(request)
        response_head = self._read_until(b"\r\n\r\n", 64 * 1024)
        lines = response_head.decode("iso-8859-1").split("\r\n")
        if lines[0] != "HTTP/1.1 101 Switching Protocols":
            raise RuntimeError("authenticated WebSocket upgrade did not return 101")
        response_headers: dict[str, str] = {}
        for line in lines[1:]:
            if not line:
                continue
            name, separator, value = line.partition(":")
            if not separator:
                raise RuntimeError("WebSocket upgrade returned a malformed header")
            response_headers[name.strip().lower()] = value.strip()
        if response_headers.get("upgrade", "").lower() != "websocket":
            raise RuntimeError("WebSocket upgrade response omitted Upgrade: websocket")
        if "upgrade" not in {
            item.strip().lower()
            for item in response_headers.get("connection", "").split(",")
        }:
            raise RuntimeError("WebSocket upgrade response omitted Connection: Upgrade")
        if response_headers.get("x-jeeb-realtime-proxy") != "gateway":
            raise RuntimeError("WebSocket upgrade did not traverse the gateway proxy")
        expected_accept = base64.b64encode(
            hashlib.sha1((websocket_key + WS_GUID).encode("ascii")).digest()
        ).decode("ascii")
        if not hmac.compare_digest(
            response_headers.get("sec-websocket-accept", ""), expected_accept
        ):
            raise RuntimeError("WebSocket upgrade accept proof was invalid")

    def close(self) -> None:
        try:
            self._send_frame(0x8, b"\x03\xe8")
        except (OSError, RuntimeError):
            pass
        finally:
            self._socket.close()

    def join(self, topic: str, ticket: str, reference: str) -> dict[str, object]:
        message = json.dumps(
            [reference, reference, topic, "phx_join", {"ticket": ticket}],
            separators=(",", ":"),
        ).encode("utf-8")
        self._send_frame(0x1, message)
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            payload = self._read_text_frame()
            try:
                decoded = json.loads(payload)
            except json.JSONDecodeError as error:
                raise RuntimeError("Phoenix returned a non-JSON text frame") from error
            if not isinstance(decoded, list) or len(decoded) != 5:
                continue
            join_ref, reply_ref, reply_topic, event, body = decoded
            if (
                event == "phx_reply"
                and reply_ref == reference
                and join_ref == reference
                and reply_topic == topic
                and isinstance(body, dict)
            ):
                if set(body) != {"status", "response"}:
                    raise RuntimeError("Phoenix join reply fields drifted")
                if body.get("status") not in ("ok", "error"):
                    raise RuntimeError("Phoenix join reply had an invalid status")
                if not isinstance(body.get("response"), dict):
                    raise RuntimeError("Phoenix join reply response was not an object")
                return body
        raise RuntimeError("Phoenix join did not return a bounded reply")

    def _read_until(self, marker: bytes, maximum: int) -> bytes:
        while marker not in self._buffer:
            chunk = self._socket.recv(4096)
            if not chunk:
                raise RuntimeError("WebSocket peer closed during upgrade")
            self._buffer.extend(chunk)
            if len(self._buffer) > maximum:
                raise RuntimeError("WebSocket upgrade headers exceeded the bound")
        end = self._buffer.index(marker) + len(marker)
        result = bytes(self._buffer[:end])
        del self._buffer[:end]
        return result

    def _read_exactly(self, length: int) -> bytes:
        while len(self._buffer) < length:
            chunk = self._socket.recv(min(65536, length - len(self._buffer)))
            if not chunk:
                raise RuntimeError("WebSocket peer closed unexpectedly")
            self._buffer.extend(chunk)
        result = bytes(self._buffer[:length])
        del self._buffer[:length]
        return result

    def _read_text_frame(self) -> str:
        while True:
            first, second = self._read_exactly(2)
            final = (first & 0x80) != 0
            opcode = first & 0x0F
            masked = (second & 0x80) != 0
            length = second & 0x7F
            if masked:
                raise RuntimeError("server WebSocket frame was unexpectedly masked")
            if length == 126:
                length = struct.unpack("!H", self._read_exactly(2))[0]
            elif length == 127:
                length = struct.unpack("!Q", self._read_exactly(8))[0]
            if length > MAX_WS_MESSAGE_BYTES:
                raise RuntimeError("WebSocket message exceeded the validation bound")
            payload = self._read_exactly(length)
            if not final:
                raise RuntimeError("fragmented WebSocket frames are outside the probe contract")
            if opcode == 0x8:
                raise RuntimeError("WebSocket closed before the Phoenix join reply")
            if opcode == 0x9:
                self._send_frame(0xA, payload)
                continue
            if opcode == 0xA:
                continue
            if opcode != 0x1:
                raise RuntimeError("unexpected non-text WebSocket data frame")
            try:
                return payload.decode("utf-8")
            except UnicodeDecodeError as error:
                raise RuntimeError("WebSocket text frame was not valid UTF-8") from error

    def _send_frame(self, opcode: int, payload: bytes) -> None:
        if len(payload) > MAX_WS_MESSAGE_BYTES:
            raise RuntimeError("outbound WebSocket message exceeded the validation bound")
        mask = secrets.token_bytes(4)
        first = bytes([0x80 | opcode])
        length = len(payload)
        if length < 126:
            header = first + bytes([0x80 | length])
        elif length <= 0xFFFF:
            header = first + bytes([0x80 | 126]) + struct.pack("!H", length)
        else:
            header = first + bytes([0x80 | 127]) + struct.pack("!Q", length)
        masked = bytes(value ^ mask[index % 4] for index, value in enumerate(payload))
        self._socket.sendall(header + mask + masked)


def assert_join(
    websocket: PhoenixWebSocket,
    ticket: str,
    topic: str,
    expected_status: str,
    expected_response: dict[str, str],
    reference: str,
) -> None:
    actual = websocket.join(topic, ticket, reference)
    if actual["status"] != expected_status:
        raise RuntimeError("Phoenix join authorization status drifted")
    if actual["response"] != expected_response:
        raise RuntimeError("Phoenix join authorization response drifted")


def mutate_compact_token(token: str) -> str:
    parts = token.split(".")
    if len(parts) != 3 or not parts[2]:
        raise RuntimeError("descriptor ticket was malformed")
    # Flip a complete six-bit base64url symbol, not the final symbol whose
    # padding bits could decode to the same signature bytes.
    replacement = "A" if parts[2][0] != "A" else "B"
    parts[2] = replacement + parts[2][1:]
    return ".".join(parts)


def different_uuid(value: str) -> str:
    if len(value) != 36:
        raise RuntimeError("descriptor nonce was not a canonical UUID")
    replacement = "a" if value[0] != "a" else "b"
    return replacement + value[1:]


def main() -> None:
    key_file = os.environ.get(KEY_FILE_ENV, "")
    if not key_file:
        raise RuntimeError("dedicated descriptor key file was not provided")
    with open(key_file, "rb") as key_stream:
        key = key_stream.read(4097)
    if not 32 <= len(key) <= 4096:
        raise RuntimeError("dedicated descriptor key length is outside the contract")

    malformed_status, _, _ = descriptor_request({})
    if malformed_status != 400:
        raise RuntimeError("malformed descriptor request did not return 400")

    timestamp = str(int(time.time()))
    forged_nonce = str(uuid.uuid4())
    forged_status, _, _ = descriptor_request(
        {
            "X-Jeeb-Staging-Probe-Timestamp": timestamp,
            "X-Jeeb-Staging-Probe-Nonce": forged_nonce,
            "X-Jeeb-Staging-Probe-Signature": "0" * 64,
        }
    )
    if forged_status != 403:
        raise RuntimeError("forged descriptor signature did not return 403")

    nonce = str(uuid.uuid4())
    timestamp = str(int(time.time()))
    headers = signed_headers(key, nonce, timestamp)
    status, response_headers, body = descriptor_request(headers)
    if status != 200:
        raise RuntimeError("valid descriptor request did not return 200")
    if "no-store" not in {
        directive.strip().lower()
        for directive in response_headers.get("cache-control", "").split(",")
    }:
        raise RuntimeError("descriptor response omitted Cache-Control: no-store")
    if response_headers.get("content-type", "").split(";", 1)[0].lower() != "application/json":
        raise RuntimeError("descriptor response was not application/json")

    descriptor = json.loads(body)
    expected_fields = {
        "conversationId",
        "topic",
        "roleInConvo",
        "socketUrl",
        "token",
        "ticket",
        "expiresAt",
    }
    if set(descriptor) != expected_fields:
        raise RuntimeError("descriptor response fields drifted")
    conversation_id = "edge-probe-" + nonce
    topic = "jeeb:chat:" + conversation_id
    if descriptor["conversationId"] != conversation_id or descriptor["topic"] != topic:
        raise RuntimeError("descriptor conversation/topic binding drifted")
    if descriptor["roleInConvo"] != "client":
        raise RuntimeError("descriptor role was not subscribe-only client")
    if descriptor["socketUrl"] != EXACT_SOCKET_URL:
        raise RuntimeError("descriptor socket URL drifted")
    for credential in ("token", "ticket"):
        if not isinstance(descriptor[credential], str) or not descriptor[credential].strip():
            raise RuntimeError("descriptor returned an empty credential")
    expires_at = parse_rfc3339(descriptor["expiresAt"])
    ttl = (expires_at.astimezone(timezone.utc) - datetime.now(timezone.utc)).total_seconds()
    if not 30 <= ttl <= 900:
        raise RuntimeError("descriptor credential TTL was outside the contract")

    token = descriptor["token"]
    ticket = descriptor["ticket"]
    cross_topic = "jeeb:chat:edge-probe-" + different_uuid(nonce)
    websocket = PhoenixWebSocket(EXACT_SOCKET_URL, token)
    try:
        assert_join(
            websocket,
            ticket,
            cross_topic,
            "error",
            {"reason": "forbidden"},
            "1",
        )
        assert_join(
            websocket,
            mutate_compact_token(ticket),
            topic,
            "error",
            {"reason": "not_in_membership"},
            "2",
        )
        assert_join(
            websocket,
            ticket,
            topic,
            "ok",
            {"conversation_id": conversation_id, "role": "client"},
            "3",
        )
    finally:
        websocket.close()

    replay_status, _, _ = descriptor_request(headers)
    if replay_status != 409:
        raise RuntimeError("replayed descriptor request did not return 409")

    print("staging_authenticated_realtime_contract=ok")


if __name__ == "__main__":
    main()
