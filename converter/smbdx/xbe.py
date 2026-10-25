"""Read data tables out of your own Deluxe Default.xbe (Xbox executable)."""
import struct


class Xbe:
    def __init__(self, data):
        if data[:4] != b"XBEH":
            raise ValueError("not an XBE")
        self.data = data
        base = struct.unpack_from("<I", data, 0x104)[0]
        count, headers = struct.unpack_from("<II", data, 0x11C)
        self.sections = []
        for i in range(count):
            _, va, _, raw, size = struct.unpack_from("<5I", data, headers - base + i * 56)
            self.sections.append((va, raw, size))

    def offset(self, va):
        for sva, raw, size in self.sections:
            if sva <= va < sva + size:
                return raw + va - sva
        return None

    def address(self, offset):
        for sva, raw, size in self.sections:
            if raw <= offset < raw + size:
                return sva + offset - raw
        return None

    def cstring(self, va):
        o = self.offset(va)
        if o is None:
            return None
        return self.data[o:self.data.index(b"\0", o)].decode("latin-1")


def sound_table(xbe):
    """The game's sound request table, as dicts in table order.

    Each 16-byte row: s16 macro (index in its group's bank, -1 = same as the row above),
    s16 pad, u32 name, u16 flags (0x200 = one row per monkey, in boy/girl/baby/goli order),
    s16 unused, s16 sound id (what the game code asks for). Rows named GRP* start a group.
    """
    name_off = xbe.data.find(b"\0GRPse04\0") + 1
    name_va = xbe.address(name_off)
    needle = struct.pack("<I", name_va)
    start = -1
    pos = xbe.data.find(needle)
    while pos >= 0:
        if pos >= 4 and struct.unpack_from("<h", xbe.data, pos + 8)[0] == -1:
            start = pos - 4
            break
        pos = xbe.data.find(needle, pos + 1)
    if start < 0:
        raise ValueError("sound table not found in Default.xbe")
    rows, group, last_macro = [], None, -1
    o = start
    while True:
        macro, _, name_ptr, flags, _, sound_id = struct.unpack_from("<hhIHhh", xbe.data, o)
        o += 16
        name = xbe.cstring(name_ptr) or ""
        if name.startswith("GRP"):
            if name == "GRPend":
                break
            group = name
            continue
        if macro == -1:
            macro = last_macro
        last_macro = macro
        rows.append({"group": group, "name": name, "macro": macro, "flags": flags, "sound_id": sound_id})
    return rows
