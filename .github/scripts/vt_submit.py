#!/usr/bin/env python3
"""Submit release binaries to VirusTotal so the website's hash lookup resolves.

The site renders a scan by asking VirusTotal for GET /api/v3/files/{sha256},
using the hash of the GitHub release asset. That 404s until VT has actually
seen and analysed the file, so a brand-new release shows a link-only fallback.
This uploads the binary on publish to close that window.

Stdlib only, so the workflow needs no pip install.
"""

import argparse
import hashlib
import json
import os
import random
import shutil
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid

API = "https://www.virustotal.com/api/v3"
GUI = "https://www.virustotal.com/gui/file/"

CHUNK = 1024 * 1024

# Free tier allows 4 requests/minute, so retries are deliberately slow.
RETRY_STATUSES = (429, 503, 504)
MAX_ATTEMPTS = 5
BACKOFF_BASE = 20

POLL_INTERVAL = 30
POLL_TIMEOUT = 600


class VtError(Exception):
    pass


def log(msg):
    print(msg, flush=True)


def request(url, method="GET", data=None, headers=None, timeout=600):
    """Call VirusTotal, retrying on rate limits and transient server errors.

    Returns (status, body_bytes). 404 comes back as a normal result rather than
    an exception because "VT has never seen this file" is the expected path.
    """
    api_key = os.environ["VT_API_KEY"]

    for attempt in range(MAX_ATTEMPTS):
        # data may be a file handle; rewind so retries resend from the start.
        if data is not None and hasattr(data, "seek"):
            data.seek(0)

        req = urllib.request.Request(url, data=data, method=method)
        req.add_header("x-apikey", api_key)
        req.add_header("accept", "application/json")
        for key, value in (headers or {}).items():
            req.add_header(key, value)

        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                return resp.status, resp.read()
        except urllib.error.HTTPError as err:
            body = err.read()
            if err.code == 404:
                return 404, body
            if err.code == 401:
                raise VtError(
                    "VirusTotal rejected the API key (401). Check the "
                    "VIRUSTOTAL_API_KEY repo secret."
                ) from err
            if err.code in RETRY_STATUSES and attempt < MAX_ATTEMPTS - 1:
                delay = BACKOFF_BASE * (2**attempt) + random.uniform(0, 5)
                log(f"  HTTP {err.code}, retrying in {delay:.0f}s "
                    f"({attempt + 1}/{MAX_ATTEMPTS - 1})")
                time.sleep(delay)
                continue
            raise VtError(
                f"HTTP {err.code} from {url}: {body[:400].decode(errors='replace')}"
            ) from err
        except urllib.error.URLError as err:
            if attempt < MAX_ATTEMPTS - 1:
                delay = BACKOFF_BASE * (2**attempt) + random.uniform(0, 5)
                log(f"  Network error ({err.reason}), retrying in {delay:.0f}s")
                time.sleep(delay)
                continue
            raise VtError(f"Network error calling {url}: {err.reason}") from err

    raise VtError(f"Giving up on {url} after {MAX_ATTEMPTS} attempts")


def request_json(url, **kwargs):
    status, body = request(url, **kwargs)
    if status == 404:
        return 404, None
    try:
        return status, json.loads(body)
    except json.JSONDecodeError as err:
        raise VtError(
            f"Unparseable response from {url}: {body[:400].decode(errors='replace')}"
        ) from err


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(CHUNK), b""):
            digest.update(block)
    return digest.hexdigest()


def parse_sums(path):
    """Parse sha256sum output: '<hash>  <filename>', two spaces."""
    sums = {}
    with open(path, "r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            parts = line.split(None, 1)
            if len(parts) != 2:
                raise VtError(f"Malformed line in {path}: {line!r}")
            digest, name = parts
            sums[os.path.basename(name.lstrip("*"))] = digest.lower()
    return sums


def verify(paths, sums_path, skip):
    """Hash every asset, check it against SHA256SUMS, then group paths by hash.

    The release ships two byte-identical exes (a versioned name and a stable
    one), so this collapses to a single upload.
    """
    by_hash = {}
    sums = None if skip else parse_sums(sums_path)
    listed = {digest: name for name, digest in (sums or {}).items()}
    failures = []

    for path in paths:
        name = os.path.basename(path)
        digest = sha256_file(path)
        size_mb = os.path.getsize(path) / (1024 * 1024)
        log(f"{name}  {digest}  ({size_mb:.1f} MB)")

        if sums is not None:
            expected = sums.get(name)
            if expected == digest:
                log("  verified against SHA256SUMS")
            elif expected is not None:
                failures.append(
                    f"{name}: SHA256SUMS says {expected}, file hashes to {digest}"
                )
            elif digest in listed:
                # Releases before the stable-name asset existed list only the
                # versioned exe. An unlisted file that is byte-identical to a
                # listed one is still verified: same bytes, same hash.
                log(f"  not listed, but byte-identical to {listed[digest]}, "
                    "which verified")
            else:
                failures.append(
                    f"{name}: hashes to {digest}, which matches nothing in "
                    f"{os.path.basename(sums_path)}"
                )

        by_hash.setdefault(digest, []).append(path)

    if failures:
        raise VtError(
            "Release asset verification FAILED:\n  "
            + "\n  ".join(failures)
            + "\nRefusing to submit. The published assets and SHA256SUMS disagree."
        )

    if sums is None:
        log("SHA256SUMS verification skipped (--skip-sums-check)")

    return by_hash


def build_multipart(path):
    """Write a multipart body to a temp file so the 163 MB exe never sits in memory.

    urllib streams a file handle as long as Content-Length is set explicitly.
    """
    boundary = "----vt" + uuid.uuid4().hex
    name = os.path.basename(path)
    preamble = (
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="file"; filename="{name}"\r\n'
        f"Content-Type: application/octet-stream\r\n\r\n"
    ).encode()
    epilogue = f"\r\n--{boundary}--\r\n".encode()

    body = tempfile.NamedTemporaryFile(suffix=".multipart")
    body.write(preamble)
    with open(path, "rb") as src:
        shutil.copyfileobj(src, body, CHUNK)
    body.write(epilogue)
    body.flush()

    length = body.tell()
    body.seek(0)
    return body, boundary, length


def upload(path):
    """Files over 32 MB can't POST to /files -- get a one-time signed URL first."""
    log("  Requesting upload URL (file is over the 32 MB /files limit)")
    _, payload = request_json(f"{API}/files/upload_url")
    target = payload["data"]

    size_mb = os.path.getsize(path) / (1024 * 1024)
    log(f"  Uploading {size_mb:.1f} MB")

    body, boundary, length = build_multipart(path)
    try:
        _, payload = request_json(
            target,
            method="POST",
            data=body,
            headers={
                "Content-Type": f"multipart/form-data; boundary={boundary}",
                "Content-Length": str(length),
            },
        )
    finally:
        body.close()

    analysis_id = payload["data"]["id"]
    log(f"  Uploaded, analysis id {analysis_id}")
    return analysis_id


def poll(analysis_id):
    """Wait for analysis to finish; /files/{sha256} 404s until it completes."""
    log(f"  Polling for completion (up to {POLL_TIMEOUT // 60} min)")
    deadline = time.monotonic() + POLL_TIMEOUT

    while time.monotonic() < deadline:
        time.sleep(POLL_INTERVAL)
        _, payload = request_json(f"{API}/analyses/{analysis_id}")
        status = payload["data"]["attributes"]["status"]
        log(f"    status={status}")
        if status == "completed":
            return payload["data"]["attributes"].get("stats")

    log("  Still analysing after the timeout. VirusTotal will finish in the "
        "background and the site will pick it up.")
    return None


def ratio(stats):
    if not stats:
        return None
    flagged = stats.get("malicious", 0) + stats.get("suspicious", 0)
    total = sum(v for v in stats.values() if isinstance(v, int))
    return f"{flagged} / {total} engines flagged" if total else None


def submit(digest, names, path):
    log(f"\n{', '.join(names)}")
    log(f"  sha256 {digest}")

    status, payload = request_json(f"{API}/files/{digest}")

    if status == 200:
        log("  Already known to VirusTotal, skipping upload")
        stats = payload["data"]["attributes"].get("last_analysis_stats")
        try:
            # data=b"" so urllib sends an explicit Content-Length: 0.
            request(f"{API}/files/{digest}/analyse", method="POST", data=b"")
            log("  Requested a rescan to refresh the report")
        except VtError as err:
            log(f"  Rescan request failed (report is still valid): {err}")
    else:
        log("  Not known to VirusTotal yet")
        stats = poll(upload(path))

    verdict = ratio(stats)
    if verdict:
        log(f"  {verdict}")
    log(f"  {GUI}{digest}")
    return verdict


def summary(results, tag):
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not path:
        return
    with open(path, "a", encoding="utf-8") as out:
        out.write(f"## VirusTotal - {tag}\n\n")
        for digest, names, verdict in results:
            out.write(f"### {', '.join(names)}\n\n")
            out.write(f"- **SHA-256:** `{digest}`\n")
            out.write(f"- **Result:** {verdict or 'analysis still running'}\n")
            out.write(f"- **Report:** {GUI}{digest}\n\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dir", required=True, help="directory of downloaded assets")
    parser.add_argument("--sums", required=True, help="path to the SHA256SUMS asset")
    parser.add_argument("--tag", required=True, help="release tag, for the summary")
    parser.add_argument(
        "--skip-sums-check",
        action="store_true",
        help="skip verification, for older releases published without SHA256SUMS",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="verify hashes and dedupe, but make no API calls",
    )
    args = parser.parse_args()

    paths = sorted(
        os.path.join(args.dir, name)
        for name in os.listdir(args.dir)
        if name.lower().endswith(".exe")
    )
    if not paths:
        raise VtError(f"No .exe assets found in {args.dir}")

    if not args.skip_sums_check and not os.path.exists(args.sums):
        raise VtError(
            f"{args.sums} not found. Older releases predate that asset, so "
            "re-run with skip_sums_check enabled if that's expected."
        )

    by_hash = verify(paths, args.sums, args.skip_sums_check)
    log(f"\n{len(paths)} asset(s), {len(by_hash)} unique file(s) to submit")

    if args.dry_run:
        for digest, group in by_hash.items():
            names = ", ".join(os.path.basename(p) for p in group)
            log(f"  would submit {digest} ({names})")
        return

    if not os.environ.get("VT_API_KEY"):
        raise VtError("VT_API_KEY is empty. Is the VIRUSTOTAL_API_KEY secret set?")

    results = []
    for digest, group in by_hash.items():
        names = [os.path.basename(p) for p in group]
        results.append((digest, names, submit(digest, names, group[0])))
    summary(results, args.tag)


if __name__ == "__main__":
    try:
        main()
    except VtError as err:
        print(f"\nERROR: {err}", file=sys.stderr)
        sys.exit(1)
