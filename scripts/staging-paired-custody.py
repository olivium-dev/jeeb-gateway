#!/usr/bin/env python3
"""Local receipt and durable one-use journal. Never stores specs or credentials."""
from datetime import datetime, timezone
import contextlib
import fcntl
import json
import os
from pathlib import Path
import re
import stat
import sys

PHASES = ("prepared", "gateway-submission-pending", "gateway-verified",
          "delivery-submission-pending", "delivery-verified", "complete")
SECRET_NAME = "jeeb-staging-delivery-service-auth-v1"


@contextlib.contextmanager
def held_lock(home, owner):
    """Same inode/protocol as normal routes, without truncation or stale takeover."""
    require(re.fullmatch(r'[0-9a-f]{64}', owner))
    home = Path(home)
    require(home.is_absolute() and home.resolve() == home)
    current = os.open('/', os.O_RDONLY | os.O_DIRECTORY)
    opened = [current]
    lock_fd = None
    created = False
    try:
        for part in home.parts[1:]:
            current = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=current)
            opened.append(current)
            info = os.fstat(current)
            require(info.st_uid in (0, os.getuid()) and not info.st_mode & 0o022)
        require(os.fstat(current).st_uid == os.getuid())
        for part in ('.jeeb-deploy', 'locks'):
            try:
                os.mkdir(part, 0o700, dir_fd=current)
                os.fsync(current)
            except FileExistsError:
                pass
            current = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=current)
            opened.append(current)
            info = os.fstat(current)
            require(info.st_uid == os.getuid() and stat.S_IMODE(info.st_mode) == 0o700)
        lock_fd = os.open('jeeb-staging-gateway.lock', os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW,
                          0o600, dir_fd=current)
        info = os.fstat(lock_fd)
        require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and info.st_uid == os.getuid()
                and stat.S_IMODE(info.st_mode) == 0o600)
        os.fsync(current)
        fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        fd = os.open('jeeb-staging-gateway.owner', os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                     0o600, dir_fd=current)
        created = True
        with os.fdopen(fd, 'w') as stream:
            stream.write(owner + '\n')
            stream.flush()
            os.fsync(stream.fileno())
        os.fsync(current)
        yield
    finally:
        try:
            if created:
                fd = os.open('jeeb-staging-gateway.owner', os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=current)
                try:
                    info = os.fstat(fd)
                    require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and info.st_uid == os.getuid()
                            and stat.S_IMODE(info.st_mode) == 0o600 and os.read(fd, 66).decode() == owner + '\n')
                finally:
                    os.close(fd)
                os.unlink('jeeb-staging-gateway.owner', dir_fd=current)
                os.fsync(current)
        finally:
            if lock_fd is not None:
                os.close(lock_fd)
            for fd in reversed(opened):
                os.close(fd)


def require(value):
    if not value:
        raise ValueError("paired custody guard")


def sync_directory(path):
    with open_directory(path) as fd:
        os.fsync(fd)


@contextlib.contextmanager
def open_directory(path):
    """Pin every ancestor using openat; no path-based second read follows links."""
    path = Path(os.path.abspath(path))
    fd = os.open('/', os.O_RDONLY | os.O_DIRECTORY)
    try:
        for part in path.parts[1:]:
            next_fd = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
            os.close(fd)
            fd = next_fd
        yield fd
    finally:
        os.close(fd)


def unique_object(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value)
        value[key] = item
    return value


def directory(path, create=False):
    path = Path(path)
    with open_directory(path.parent) as parent:
        if create:
            try:
                os.mkdir(path.name, 0o700, dir_fd=parent)
                os.fsync(parent)
            except FileExistsError:
                pass
        fd = os.open(path.name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=parent)
        try:
            info = os.fstat(fd)
            require(stat.S_ISDIR(info.st_mode) and info.st_uid == os.getuid()
                    and stat.S_IMODE(info.st_mode) == 0o700)
        finally:
            os.close(fd)
    return path


def custody_root(home):
    home = Path(home)
    info = home.lstat()
    require(home.is_absolute() and home.resolve() == home and stat.S_ISDIR(info.st_mode)
            and info.st_uid == os.getuid() and not info.st_mode & 0o022)
    deploy = directory(home / ".jeeb-deploy")
    return directory(deploy / "paired-releases", create=True)


def read_private(path, mode):
    path = Path(path)
    with open_directory(path.parent) as parent:
        info = os.fstat(parent)
        require(info.st_uid == os.getuid() and stat.S_IMODE(info.st_mode) == 0o700)
        fd = os.open(path.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=parent)
    try:
        info = os.fstat(fd)
        require(stat.S_ISREG(info.st_mode) and info.st_uid == os.getuid()
                and stat.S_IMODE(info.st_mode) == mode and info.st_nlink == 1 and info.st_size <= 32768)
        with os.fdopen(fd, "r", closefd=False) as stream:
            return json.load(stream, object_pairs_hook=unique_object)
    finally:
        os.close(fd)


def write_exclusive(path, value, mode=0o400):
    encoded = (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()
    require(len(encoded) <= 32768)
    path = Path(path)
    with open_directory(path.parent) as parent:
        info = os.fstat(parent)
        require(info.st_uid == os.getuid() and stat.S_IMODE(info.st_mode) == 0o700)
        fd = os.open(path.name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, mode, dir_fd=parent)
        try:
            with os.fdopen(fd, "wb", closefd=False) as stream:
                stream.write(encoded)
                stream.flush()
                os.fsync(fd)
        finally:
            os.close(fd)
        os.fsync(parent)


def utc(value):
    require(isinstance(value, str) and re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", value))
    return datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc).timestamp()


def validate_receipt(receipt, expected, image, daemon_id, home, now):
    require(receipt["schemaVersion"] == 1 and type(receipt["schemaVersion"]) is int)
    require(receipt["repository"] == "olivium-dev/delivery-service")
    require(receipt["workflowPath"] == ".github/workflows/jeeb-staging-paired-prepare.yml")
    require(re.fullmatch(r"[0-9a-f]{64}", receipt["receiptNonce"]))
    for field in ("sourceCommit", "sourceTree", "probeHelperBlobSha"):
        require(re.fullmatch(r"[0-9a-f]{40}", receipt[field]) and receipt[field] == expected[field])
    for field in ("runId", "attempt"):
        require(re.fullmatch(r"[1-9][0-9]*", receipt[field]) and receipt[field] == expected[field])
    require(re.fullmatch(r"ghcr\.io/olivium-dev/delivery-service@sha256:[0-9a-f]{64}", receipt["imageDigest"]))
    require(receipt["imageDigest"] == expected["imageDigest"] and receipt["imageDigest"] in image["RepoDigests"])
    require(re.fullmatch(r"sha256:[0-9a-f]{64}", receipt["imageId"]) and receipt["imageId"] == image["Id"])
    labels = image["Config"]["Labels"]
    require(labels["org.opencontainers.image.revision"] == receipt["sourceCommit"])
    require(labels["jeeb.source.tree"] == receipt["sourceTree"])
    require(labels["org.opencontainers.image.source"] == "https://github.com/olivium-dev/delivery-service")
    require(receipt["daemonId"] == daemon_id and receipt["sshUid"] == os.getuid()
            and type(receipt["sshUid"]) is int and receipt["canonicalHome"] == str(Path(home).resolve()))
    issued, expires = utc(receipt["createdAt"]), utc(receipt["expiresAt"])
    require(expires - issued == 900 and issued <= now < expires)


def assert_shared_lock(home, owner):
    custody_root(home)
    require(re.fullmatch(r"[0-9a-f]{64}", owner))
    root = directory(Path(home) / ".jeeb-deploy/locks")
    owner_path = root / "jeeb-staging-gateway.owner"
    fd = os.open(owner_path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        info = os.fstat(fd)
        require(stat.S_ISREG(info.st_mode) and info.st_uid == os.getuid()
                and stat.S_IMODE(info.st_mode) == 0o600 and info.st_nlink == 1)
        require(os.read(fd, 66).decode().strip() == owner)
    finally:
        os.close(fd)
    fd = os.open(root / "jeeb-staging-gateway.lock", os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        info = os.fstat(fd)
        require(stat.S_ISREG(info.st_mode) and info.st_uid == os.getuid()
                and stat.S_IMODE(info.st_mode) == 0o600 and info.st_nlink == 1)
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return
        raise ValueError("stale shared lock owner")
    finally:
        os.close(fd)


class Journal:
    """Append-only phase files: interruption never needs replace/delete recovery."""
    def __init__(self, home):
        self.root = custody_root(home)
        self.claims = directory(self.root / "consumed", create=True)
        self.path = self.root / SECRET_NAME

    def begin(self, metadata):
        allowed = {"gatewaySource", "gatewayTree", "gatewayImage", "deliverySource", "deliveryTree",
                   "deliveryImage", "deliveryRun", "deliveryAttempt", "gatewayRun", "gatewayAttempt",
                   "gatewayServiceId", "deliveryServiceId", "gatewayVersion", "deliveryVersion", "secretId", "receiptNonce",
                   "gatewayBuildRun", "gatewayBuildAttempt"}
        require(set(metadata) == allowed)
        for key in ("gatewaySource", "gatewayTree", "deliverySource", "deliveryTree"):
            require(re.fullmatch(r"[0-9a-f]{40}", metadata[key]))
        for role, repository in (("gateway", "jeeb-gateway"), ("delivery", "delivery-service")):
            require(re.fullmatch(r"ghcr\.io/olivium-dev/" + repository + r"@sha256:[0-9a-f]{64}", metadata[role + "Image"]))
            for key in ("Run", "Attempt"):
                require(re.fullmatch(r"[1-9][0-9]*", metadata[role + key]))
            require(re.fullmatch(r"[a-z0-9]{25}", metadata[role + "ServiceId"]))
            require(type(metadata[role + "Version"]) is int and metadata[role + "Version"] > 0)
        require(re.fullmatch(r"[a-z0-9]{25}", metadata["secretId"]))
        require(re.fullmatch(r"[0-9a-f]{64}", metadata["receiptNonce"]))
        for field in ('gatewayBuildRun', 'gatewayBuildAttempt'):
            require(re.fullmatch(r'[1-9][0-9]*', metadata[field]))
        # A successful mkdir but interrupted first write is itself a permanent
        # incomplete history. No retry may silently recreate or reset it.
        with open_directory(self.root) as parent:
            os.mkdir(self.path.name, 0o700, dir_fd=parent)
            os.fsync(parent)
        claim = self.claims / (metadata["receiptNonce"] + ".json")
        write_exclusive(claim, {"activation": SECRET_NAME, "gatewayRun": metadata["gatewayRun"]})
        write_exclusive(self.path / "00-prepared.json", {"phase": "prepared", **metadata})

    def advance(self, phase):
        require(phase in PHASES[1:])
        directory(self.path)
        index = PHASES.index(phase)
        expected_names = {f"{i:02d}-{value}.json" for i, value in enumerate(PHASES[:index])}
        with open_directory(self.path) as parent:
            require(set(os.listdir(parent)) == expected_names)
        previous = read_private(self.path / f"{index - 1:02d}-{PHASES[index - 1]}.json", 0o400)
        require(previous["phase"] == PHASES[index - 1])
        write_exclusive(self.path / f"{index:02d}-{phase}.json", {**previous, "phase": phase})


if __name__ == '__main__':
    try:
        require(sys.argv[1:] == ['hold-lock'])
        owner = sys.stdin.readline(66).strip()
        with held_lock(Path.home(), owner):
            print('LOCKED', flush=True)
            require(sys.stdin.readline(66).strip() == owner)
    except Exception:
        print('Paired shared lock failed closed.', file=sys.stderr)
        sys.exit(1)
