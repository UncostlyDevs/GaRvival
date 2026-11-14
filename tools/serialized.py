"""Reads the type trees of a Unity 5.4 SerializedFile (format 15) and prints
the fields of each MonoBehaviour script type."""
import io
import struct
import sys


def read_types(path):
    data = open(path, "rb").read()
    meta_size, file_size, version, data_offset = struct.unpack(">IIII", data[:16])
    endian = data[16]
    pos = 20
    e = ">" if endian else "<"
    r = io.BytesIO(data)
    r.seek(pos)

    def u8():
        return r.read(1)[0]

    def i32():
        return struct.unpack(e + "i", r.read(4))[0]

    def u32():
        return struct.unpack(e + "I", r.read(4))[0]

    def cstr():
        out = bytearray()
        while True:
            c = r.read(1)
            if c == b"\0":
                return out.decode("utf-8", "replace")
            out += c

    unity = cstr()
    platform = u32()
    type_tree = u8() if version >= 13 else 1
    count = i32()
    types = []
    for _ in range(count):
        class_id = i32()
        if version >= 13:
            if class_id < 0:
                r.read(16)
            r.read(16)
        nodes = []
        if type_tree:
            node_count = i32()
            buf_size = i32()
            raw = [struct.unpack(e + "HBBiiiii", r.read(24)) for _ in range(node_count)]
            strbuf = r.read(buf_size)

            def name(off):
                if off & 0x80000000:
                    return "common@%d" % (off & 0x7FFFFFFF)
                end = strbuf.index(b"\0", off)
                return strbuf[off:end].decode("utf-8", "replace")

            for ver, level, is_array, type_off, name_off, size, index, flags in raw:
                nodes.append((level, name(type_off), name(name_off), size))
        types.append((class_id, nodes))
    return version, unity, types


if __name__ == "__main__":
    version, unity, types = read_types(sys.argv[1])
    print("format", version, unity, "types", len(types))
    for class_id, nodes in types:
        if class_id >= 0:
            continue
        fields = [n for n in nodes if n[0] >= 1]
        # Skip the MonoBehaviour header fields every script has.
        own = [n for n in fields if n[2] not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name", "m_FileID", "m_PathID")]
        top = [n for n in own if n[0] == 1]
        print("script type %d: %d fields: %s" % (class_id, len(top), ", ".join("%s %s" % (n[1], n[2]) for n in top)))
