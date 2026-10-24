"""CRI ADX (4-bit ADPCM) decoder and ACX sound-bank splitter. Deluxe's ADX files are unencrypted."""
import math
import struct
import wave


def split_acx(data):
    """Return the ADX blobs in an .acx bank (big-endian: u32 0, u32 count, then offset/size pairs)."""
    count = struct.unpack_from(">I", data, 4)[0]
    out = []
    for i in range(count):
        off, size = struct.unpack_from(">II", data, 8 + i * 8)
        out.append(data[off:off + size])
    return out


def adx_info(data):
    if data[:2] != b"\x80\x00":
        raise ValueError("not an ADX stream")
    data_off, enc, block, bits, ch, rate, total, cutoff, ver, flags = struct.unpack_from(">HBBBBIIHBB", data, 2)
    if enc != 3 or bits != 4:
        raise ValueError(f"unsupported ADX encoding {enc}/{bits}-bit")
    if flags & 0x08:
        raise ValueError("encrypted ADX")
    return {"start": data_off + 4, "block": block, "channels": ch, "rate": rate,
            "samples": total, "cutoff": cutoff}


def decode_adx(data):
    """Return (rate, channels, interleaved int16 sample list). Matches ffmpeg's adpcm_adx output."""
    info = adx_info(data)
    ch, block, total = info["channels"], info["block"], info["samples"]
    a = math.sqrt(2.0) - math.cos(2.0 * math.pi * info["cutoff"] / info["rate"])
    b = math.sqrt(2.0) - 1.0
    c = (a - math.sqrt((a + b) * (a - b))) / b
    c1 = int(c * 8192)
    c2 = int(-(c * c) * 4096)
    per_block = (block - 2) * 2
    hist1 = [0] * ch
    hist2 = [0] * ch
    out = [0] * (total * ch)
    pos = info["start"]
    done = 0
    while done < total:
        n = min(per_block, total - done)
        for c_ in range(ch):
            blk = data[pos:pos + block]
            pos += block
            if len(blk) < block:
                return info["rate"], ch, out
            scale = (blk[0] << 8) | blk[1]
            h1, h2 = hist1[c_], hist2[c_]
            o = done * ch + c_
            for i in range(n):
                byte = blk[2 + (i >> 1)]
                nib = (byte >> 4) if not (i & 1) else (byte & 15)
                if nib >= 8:
                    nib -= 16
                s = nib * scale + ((c1 * h1 + c2 * h2) >> 12)
                s = 32767 if s > 32767 else (-32768 if s < -32768 else s)
                out[o] = s
                o += ch
                h2, h1 = h1, s
            hist1[c_], hist2[c_] = h1, h2
        done += n
    return info["rate"], ch, out


def write_wav(path, rate, channels, samples):
    with wave.open(path, "wb") as w:
        w.setnchannels(channels)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(struct.pack(f"<{len(samples)}h", *samples))
