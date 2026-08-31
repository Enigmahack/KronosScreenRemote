#!/usr/bin/env python3
"""Build an EXs option-file catalog without downloading the multi-GB zips.

For every EXs number we fetch only the tiny Sxxx option file out of the remote zip using
HTTP Range requests against Korg's CDN (Accept-Ranges: bytes):
    HEAD zip -> tail (EOCD + central directory) -> local header -> the entry's ~60 bytes.
~4 requests / ~10 KB per pack instead of 150 MB - 4 GB.

Outputs (into --out dir):
    ExsCatalog.json  { "10": "<verbatim S-file text>", ... }   <- what the app consumes
    Inventory.txt    "EXs10 -> Ricky Lawson's ..."             <- same format as before
"""
import argparse, json, os, re, struct, sys, urllib.error, urllib.request, zlib
from concurrent.futures import ThreadPoolExecutor

BASE = "https://storage.korg.com/korgms/sound_libraries/Kronos_Nautilus"
UA = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"}


def _open(url, method="GET", rng=None):
    req = urllib.request.Request(url, headers=dict(UA), method=method)
    if rng:
        req.add_header("Range", f"bytes={rng[0]}-{rng[1]}")
    return urllib.request.urlopen(req, timeout=30)


def head(url):
    """(status, content-length) - None if the object doesn't exist."""
    try:
        with _open(url, "HEAD") as r:
            return r.status, int(r.headers.get("Content-Length", 0))
    except urllib.error.HTTPError:
        return None, 0
    except Exception:
        return None, 0


def get_range(url, start, end):
    with _open(url, rng=(start, end)) as r:
        # A 200 here means the server ignored the Range header and is about to hand us the
        # whole multi-GB body. Never read it.
        if r.status != 206:
            raise RuntimeError(f"range ignored (HTTP {r.status})")
        return r.read()


def _zip64_local_offset(extra):
    """Local-header offset out of a central-directory Zip64 extra field (id 0x0001).

    Needed because the Sxxx entry is the LAST one in these archives, i.e. the one sitting past
    4 GB in the big packs, where the classic 32-bit offset field is 0xFFFFFFFF.
    """
    p = 0
    while p + 4 <= len(extra):
        hid, hsz = struct.unpack("<HH", extra[p:p + 4])
        body = extra[p + 4:p + 4 + hsz]
        if hid == 0x0001:
            # Fields appear only when their 32-bit counterpart was 0xFFFFFFFF, in the order
            # uncompressed size, compressed size, local-header offset. The Sxxx entry's sizes are
            # tiny, so in practice the offset is the only value present - if a future pack ever
            # ships a zip64 archive where that isn't true, this reads the wrong field.
            if len(body) >= 8:
                return struct.unpack("<Q", body[:8])[0]
        p += 4 + hsz
    raise RuntimeError("zip64 extra field missing local-header offset")


def peek_s_file(url, total):
    """The verbatim Sxxx option-file text out of the remote zip, via Range requests."""
    tail_len = min(65557, total)
    tail_at = total - tail_len
    tail = get_range(url, tail_at, total - 1)
    i = tail.rfind(b"PK\x05\x06")
    if i < 0:
        raise RuntimeError("no end-of-central-directory record")
    cd_size, cd_off = struct.unpack("<II", tail[i + 12:i + 20])
    if cd_size == 0xFFFFFFFF or cd_off == 0xFFFFFFFF:
        j = tail.rfind(b"PK\x06\x06")
        if j < 0:
            raise RuntimeError("no zip64 end-of-central-directory record")
        cd_size, cd_off = struct.unpack("<QQ", tail[j + 40:j + 56])

    # Some of the >4 GB packs (e.g. EXs318, 4.6 GB) carry NO zip64 records at all and a 32-bit
    # central-directory offset that no longer points anywhere real. The directory itself is still
    # physically the last thing before the EOCD, so trust that position and treat the difference
    # as a uniform shift applied to every local-header offset in the directory too.
    real_cd_off = tail_at + i - cd_size
    delta = real_cd_off - cd_off
    cd = get_range(url, real_cd_off, real_cd_off + cd_size - 1)
    if cd[:4] != b"PK\x01\x02":     # not where we assumed - trust the EOCD after all
        delta = 0
        cd = get_range(url, cd_off, cd_off + cd_size - 1)
    p = 0
    while p < len(cd) and cd[p:p + 4] == b"PK\x01\x02":
        method, = struct.unpack("<H", cd[p + 10:p + 12])
        csize, = struct.unpack("<I", cd[p + 20:p + 24])
        nlen, elen, clen = struct.unpack("<HHH", cd[p + 28:p + 34])
        lho, = struct.unpack("<I", cd[p + 42:p + 46])
        name = cd[p + 46:p + 46 + nlen].decode("utf-8", "replace")
        extra = cd[p + 46 + nlen:p + 46 + nlen + elen]
        if re.match(r"^S\d+$", name.rsplit("/", 1)[-1]):
            lho = _zip64_local_offset(extra) if lho == 0xFFFFFFFF else lho + delta
            lh = get_range(url, lho, lho + 29)
            if lh[:4] != b"PK\x03\x04":
                raise RuntimeError(f"no local header for {name} at {lho}")
            lnlen, lelen = struct.unpack("<HH", lh[26:30])
            data = lho + 30 + lnlen + lelen
            blob = get_range(url, data, data + csize - 1)
            raw = blob if method == 0 else zlib.decompress(blob, -15)
            return raw.decode("latin-1")
        p += 46 + nlen + elen + clen
    raise RuntimeError("no Sxxx entry in central directory")


def fetch(n):
    """(n, text, note) for one EXs number - text None when nothing is published there."""
    if head(f"{BASE}/EXs{n}/")[0] != 200:
        return n, None, "no directory"
    # Three published variants of the same pack number: bare = Kronos, K2_ = Kronos 2,
    # N_ = Nautilus-only (the packs the old inventory was missing entirely). Any one of them
    # carries the same Sxxx option file, so the first that answers wins.
    for fname in (f"EXs{n}.zip", f"K2_EXs{n}.zip", f"N_EXs{n}.zip"):
        url = f"{BASE}/EXs{n}/{fname}"
        status, total = head(url)
        if status != 200 or total == 0:
            continue
        try:
            return n, peek_s_file(url, total), fname
        except Exception as exc:
            return n, None, f"{fname}: {exc}"
    return n, None, "directory exists, no EXs/K2_EXs zip"


def seed_from_local(options_dir):
    """Free seed: the Sxxx files an earlier full-download pass already extracted locally."""
    out = {}
    if not os.path.isdir(options_dir):
        return out
    for name in os.listdir(options_dir):
        m = re.match(r"^S(\d+)(?:_\d+)?$", name)
        if not m:
            continue
        with open(os.path.join(options_dir, name), "r", encoding="latin-1") as f:
            text = f.read()
        n = int(m.group(1))
        # Keep the first (unsuffixed) copy; _2/_3 are duplicate packs sharing a number.
        out.setdefault(n, text)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=".")
    ap.add_argument("--options-dir", default=r"F:\KRONOS EXs\Options")
    ap.add_argument("--first", type=int, default=10)
    ap.add_argument("--last", type=int, default=500)
    ap.add_argument("--workers", type=int, default=16)
    ap.add_argument("--no-local-seed", action="store_true")
    ap.add_argument("--refetch-all", action="store_true", help="ignore the local seed as a skip list")
    args = ap.parse_args()

    catalog = {} if args.no_local_seed else seed_from_local(args.options_dir)
    print(f"seeded {len(catalog)} option file(s) from {args.options_dir}")

    todo = [n for n in range(args.first, args.last + 1) if args.refetch_all or n not in catalog]
    found = notes = 0
    with ThreadPoolExecutor(max_workers=args.workers) as ex:
        for n, text, note in ex.map(fetch, todo):
            if text:
                catalog[n] = text
                found += 1
                print(f"  [{n:>3}] {note}: {text.splitlines()[1] if len(text.splitlines()) > 1 else '?'}")
            elif note != "no directory":
                notes += 1
                print(f"  [{n:>3}] {note}")

    os.makedirs(args.out, exist_ok=True)
    cat_path = os.path.join(args.out, "ExsCatalog.json")
    with open(cat_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump({str(k): catalog[k] for k in sorted(catalog)}, f, indent=1, ensure_ascii=False)

    inv_path = os.path.join(args.out, "Inventory.txt")
    with open(inv_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("Inventory:\n=============================================\n")
        for n in sorted(catalog):
            lines = catalog[n].splitlines()
            if len(lines) >= 2:
                f.write(f"{lines[0].strip()} -> {lines[1].strip()}\n")

    print(f"\n{len(catalog)} entries ({found} fetched over the network, {notes} problem(s))")
    print(f"wrote {cat_path}\nwrote {inv_path}")


if __name__ == "__main__":
    main()
