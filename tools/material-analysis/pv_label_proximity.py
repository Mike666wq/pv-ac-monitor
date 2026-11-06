"""把力控 .drw Contents 里的 PV 引用与邻近的中文标签配对，验证"标签 ↔ 点位"绑定。

用法: python pv_label_proximity.py <Contents.bin> [半径字节]
输出: 每个 PV 引用命中位置的前后中文串（按距离排序）+ 命中处的十六进制片段。
"""
import re
import sys
from pathlib import Path


def gbk_runs(data):
    runs = []
    i = 0
    while i < len(data) - 1:
        b = data[i]
        if 0x81 <= b <= 0xFE and 0x40 <= data[i + 1] <= 0xFE:
            j = i
            buf = b""
            while j < len(data) - 1 and 0x81 <= data[j] <= 0xFE and 0x40 <= data[j + 1] <= 0xFE:
                buf += data[j:j + 2]
                j += 2
            try:
                s = buf.decode("gb18030")
            except Exception:
                s = ""
            if len(s) >= 2:
                runs.append((i, s))
            i = j
        else:
            i += 1
    return runs


def main():
    path = Path(sys.argv[1])
    radius = int(sys.argv[2]) if len(sys.argv) > 2 else 400
    data = path.read_bytes()
    runs = gbk_runs(data)
    pvs = [(m.start(), m.group().decode()) for m in re.finditer(rb"[A-Za-z0-9_]+\.PV", data)]
    print(f"{path.name}: {len(data)} bytes, {len(pvs)} PV refs, {len(runs)} GBK runs")
    for off, name in pvs:
        near = sorted(((abs(o - off), o - off, o, t) for o, t in runs if abs(o - off) <= radius),
                      key=lambda x: x[0])
        print(f"\n== {name} @0x{off:06X}")
        for _, delta, o, t in near[:6]:
            print(f"   [{delta:+6d}] @0x{o:06X} {t}")
        start = max(0, off - 48)
        chunk = data[start:off + 48]
        print("   hex:", " ".join(f"{b:02X}" for b in chunk))
        print("   txt:", "".join(chr(b) if 32 <= b < 127 else "." for b in chunk))


if __name__ == "__main__":
    main()
