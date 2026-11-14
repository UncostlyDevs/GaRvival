"""Minimal UnityFS (Unity 5.x asset bundle) reader: lists and extracts the
files inside a bundle. Pure standard library; LZ4 implemented here."""
import io
import lzma
import struct
import sys


def lz4_block(src, size):
    dst = bytearray()
    i = 0
    n = len(src)
    while i < n:
        token = src[i]
        i += 1
        lit = token >> 4
        if lit == 15:
            while True:
                b = src[i]
                i += 1
                lit += b
                if b != 255:
                    break
        dst += src[i:i + lit]
        i += lit
        if i >= n:
            break
        off = src[i] | (src[i + 1] << 8)
        i += 2
        ml = token & 15
        if ml == 15:
            while True:
                b = src[i]
                i += 1
                ml += b
                if b != 255:
                    break
        ml += 4
        start = len(dst) - off
        if off >= ml:
            dst += dst[start:start + ml]
        else:
            for k in range(ml):
                dst.append(dst[start + k])
    if len(dst) != size:
        raise ValueError("lz4 size mismatch %d != %d" % (len(dst), size))
    return bytes(dst)


def lzma_unity(src, size):
    props = src[0]
    dict_size = struct.unpack("<I", src[1:5])[0]
    lc = props % 9
    props //= 9
    lp = props % 5
    pb = props // 5
    dec = lzma.LZMADecompressor(format=lzma.FORMAT_RAW, filters=[{"id": lzma.FILTER_LZMA1, "dict_size": dict_size, "lc": lc, "lp": lp, "pb": pb}])
    return dec.decompress(src[5:], max_length=size)


def decompress(data, flags, size):
    kind = flags & 0x3F
    if kind == 0:
        return data
    if kind == 1:
        return lzma_unity(data, size)
    if kind in (2, 3):
        return lz4_block(data, size)
    raise ValueError("unknown compression %d" % kind)


def cstr(f):
    out = bytearray()
    while True:
        c = f.read(1)
        if not c or c == b"\0":
            return out.decode("utf-8", "replace")
        out += c


def read_bundle(path):
    with open(path, "rb") as f:
        sig = cstr(f)
        assert sig == "UnityFS", sig
        version = struct.unpack(">I", f.read(4))[0]
        cstr(f)
        engine = cstr(f)
        total, csize, usize, flags = struct.unpack(">qIII", f.read(20))
        if flags & 0x80:
            pos = f.tell()
            f.seek(total - csize)
            info = f.read(csize)
            f.seek(pos)
        else:
            info = f.read(csize)
        info = decompress(info, flags, usize)
        r = io.BytesIO(info)
        r.read(16)
        (block_count,) = struct.unpack(">i", r.read(4))
        blocks = [struct.unpack(">IIH", r.read(10)) for _ in range(block_count)]
        (node_count,) = struct.unpack(">i", r.read(4))
        nodes = []
        for _ in range(node_count):
            off, size, nflags = struct.unpack(">qqI", r.read(20))
            nodes.append((off, size, nflags, cstr(r)))
        return version, engine, flags, blocks, nodes, f.tell()


def extract(path, out_dir=None):
    version, engine, flags, blocks, nodes, data_start = read_bundle(path)
    with open(path, "rb") as f:
        f.seek(data_start)
        data = bytearray()
        for usize, csize, bflags in blocks:
            data += decompress(f.read(csize), bflags, usize)
    files = {}
    for off, size, _, name in nodes:
        files[name] = bytes(data[off:off + size])
    return files


if __name__ == "__main__":
    version, engine, flags, blocks, nodes, _ = read_bundle(sys.argv[1])
    kinds = {}
    for _, _, bf in blocks:
        kinds[bf & 0x3F] = kinds.get(bf & 0x3F, 0) + 1
    print("format", version, engine, "flags", hex(flags), "blocks", len(blocks), "compression kinds", kinds)
    print("uncompressed total", sum(b[0] for b in blocks))
    for off, size, nf, name in nodes:
        print("node", name, size, hex(nf))
