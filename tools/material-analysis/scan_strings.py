import re, sys
from pathlib import Path
root = Path(r"C:\Users\bbben\Desktop\BMS\ExperimentMonitorDemo\test-output\material-extract")
keywords = ["风机","档","模式","设定","温度","水泵","制冷","风速","高","中","低","启动","停止","远程","本地"]
def strings(data):
    out=[]
    # try gb18030 decode of contiguous printable-ish runs
    for m in re.finditer(rb"[\x20-\x7e\xa1-\xfe]{4,}", data):
        b=m.group(0)
        for enc in ("gb18030","utf-8"):
            try:
                s=b.decode(enc)
                out.append(s); break
            except Exception: pass
    return out
for p in list(root.glob("doc/*.drw")) + [root/"db/ConfSave.mdb", root/"db/par03.dic", root/"db/syscfg.txt"]:
    if not p.exists(): continue
    data=p.read_bytes()
    ss=strings(data)
    hits=[s for s in ss if any(k in s for k in keywords)]
    print(f"== {p.name} ({len(data)} bytes, {len(ss)} strings, {len(hits)} hits)")
    for s in hits[:40]:
        print("   ", s[:80])
