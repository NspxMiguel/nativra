"""Lists what a Windows binary asks the operating system for.

The translation layer has to answer exactly these, so the first question is how
many there are and which modules they come from.
"""
import struct
import sys
from collections import defaultdict


def rva_to_offset(sections, rva):
    for va, vsize, raw, rawsize in sections:
        if va <= rva < va + max(vsize, rawsize):
            return raw + (rva - va)
    return None


def imports(path):
    data = open(path, "rb").read()
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    assert data[e_lfanew:e_lfanew + 4] == b"PE\0\0", "not a PE"

    machine, nsections = struct.unpack_from("<HH", data, e_lfanew + 4)
    opt_size = struct.unpack_from("<H", data, e_lfanew + 20)[0]
    opt = e_lfanew + 24
    magic = struct.unpack_from("<H", data, opt)[0]
    pe32plus = magic == 0x20B
    dir_off = opt + (112 if pe32plus else 96)
    import_rva, _ = struct.unpack_from("<II", data, dir_off + 8)

    sec = opt + opt_size
    sections = []
    for i in range(nsections):
        base = sec + i * 40
        vsize, va, rawsize, raw = struct.unpack_from("<IIII", data, base + 8)
        sections.append((va, vsize, raw, rawsize))

    out = defaultdict(list)
    if import_rva == 0:
        return out, pe32plus
    off = rva_to_offset(sections, import_rva)
    while True:
        oft, _, _, name_rva, first = struct.unpack_from("<IIIII", data, off)
        if name_rva == 0:
            break
        name_off = rva_to_offset(sections, name_rva)
        module = data[name_off:data.index(b"\0", name_off)].decode("ascii", "replace")
        thunk_rva = oft or first
        t = rva_to_offset(sections, thunk_rva)
        step = 8 if pe32plus else 4
        fmt = "<Q" if pe32plus else "<I"
        while True:
            value = struct.unpack_from(fmt, data, t)[0]
            if value == 0:
                break
            ordinal_flag = 1 << (63 if pe32plus else 31)
            if value & ordinal_flag:
                out[module].append(f"#{value & 0xFFFF}")
            else:
                hint = rva_to_offset(sections, value & 0x7FFFFFFF)
                fn = data[hint + 2:data.index(b"\0", hint + 2)].decode("ascii", "replace")
                out[module].append(fn)
            t += step
        off += 20
    return out, pe32plus


if __name__ == "__main__":
    grand = defaultdict(set)
    for path in sys.argv[1:]:
        table, pe32plus = imports(path)
        total = sum(len(v) for v in table.values())
        print(f"{path.split('/')[-1]}: {'x64' if pe32plus else 'x86'}, {total} imports, {len(table)} modules")
        for module, fns in sorted(table.items(), key=lambda kv: -len(kv[1])):
            print(f"   {module:<28} {len(fns)}")
            grand[module.lower()].update(fns)
    print("\n=== total por modulo ===")
    for module, fns in sorted(grand.items(), key=lambda kv: -len(kv[1])):
        print(f"{module:<30} {len(fns)}")
    print("funcoes distintas no total:", sum(len(v) for v in grand.values()))
