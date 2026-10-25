"""Unpack an original Xbox disc image (XISO or full Redump-style ISO) into a folder.

Usage: python extract_xiso.py <image.iso> <output dir>

Re-running skips files that are already extracted with the right size, so an
interrupted run can be resumed.
"""
import os
import struct
import sys

SECTOR = 2048
MAGIC = b"MICROSOFT*XBOX*MEDIA"
# Where the game partition starts: plain XISO, then the common full-disc layouts.
PARTITION_OFFSETS = (0, 0x18300000, 0x2080000, 0xFD90000)
CHUNK = 8 * 1024 * 1024


def find_partition(f):
    for base in PARTITION_OFFSETS:
        f.seek(base + 32 * SECTOR)
        if f.read(len(MAGIC)) == MAGIC:
            return base
    raise SystemExit("No Xbox file system found in this image.")


def list_files(f, base):
    """Yield (relative path, start sector, size) for every file on the disc."""
    f.seek(base + 32 * SECTOR + len(MAGIC))
    root_sector, root_size = struct.unpack("<II", f.read(8))
    dirs = [(root_sector, root_size, "")]
    while dirs:
        sector, size, path = dirs.pop()
        f.seek(base + sector * SECTOR)
        table = f.read(size)
        pending, seen = [0], set()
        # Each directory is a binary tree of entries addressed in 4-byte units.
        while pending:
            index = pending.pop()
            pos = index * 4
            if index in seen or pos + 14 > len(table):
                continue
            seen.add(index)
            left, right, start, length, attrs, name_len = struct.unpack("<HHIIBB", table[pos:pos + 14])
            if left == 0xFFFF:
                continue
            pending += [i for i in (left, right) if i]
            name = table[pos + 14:pos + 14 + name_len].decode("latin-1")
            rel = os.path.join(path, name)
            if attrs & 0x10:
                if length:
                    dirs.append((start, length, rel))
            else:
                yield rel, start, length


def main():
    image, out_dir = sys.argv[1], sys.argv[2]
    with open(image, "rb") as f:
        base = find_partition(f)
        files = sorted(list_files(f, base))
        total = sum(size for _, _, size in files)
        done = 0
        for rel, start, size in files:
            dst = os.path.join(out_dir, rel)
            done += size
            if os.path.exists(dst) and os.path.getsize(dst) == size:
                continue
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            f.seek(base + start * SECTOR)
            with open(dst + ".part", "wb") as out:
                left = size
                while left:
                    block = f.read(min(CHUNK, left))
                    out.write(block)
                    left -= len(block)
            os.replace(dst + ".part", dst)
            print(f"{done * 100 // max(total, 1):3d}%  {rel}", flush=True)
    print(f"Done: {len(files)} files, {total / 1e9:.2f} GB in {out_dir}")


if __name__ == "__main__":
    main()
