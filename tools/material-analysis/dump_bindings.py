import json, struct, sys
from pathlib import Path
SRC = Path(r"C:\Users\bbben\Desktop\BMS\ExperimentMonitorDemo\test-output\material-extract")
def read_dictionary(path):
    return [[f.strip() for f in line.split(";")] for line in path.read_text(encoding="gb18030").splitlines() if ";" in line]
def cstring(data, a, b):
    return data[a:b].split(b"\0",1)[0].decode("ascii", "replace")
fields = [r[1] for r in read_dictionary(SRC/"db/par00.dic")]
points = {}
for r in read_dictionary(SRC/"db/tag0000.dic"):
    if len(r) != len(fields): continue
    v = dict(zip(fields, r))
    points[v["NAME"]] = v
print("tags:", len(points))
rows = []
for dev in ["PLC","DS666","DDSU666","DJSF6682"]:
    p = SRC/"db"/f"link{dev}.dat"
    if not p.exists(): continue
    data = p.read_bytes()
    n = struct.unpack_from("<I", data)[0]
    for i in range(n):
        off = 4 + i*256
        rec = data[off:off+256]
        pv = cstring(rec,0,32)
        name = pv.rsplit(".",1)[0]
        conn = cstring(rec,64,96)
        native = struct.unpack_from("<9i", rec, 220)
        _, area, addr, _, width, _, _, _, flags = native
        desc = points.get(name,{}).get("DESC","")
        rows.append(dict(dev=dev,name=name,desc=desc,conn=conn,addr=addr,width=width,flags=flags,unit=points.get(name,{}).get("UNIT",""),eu=points.get(name,{}).get("EU","")))
rows.sort(key=lambda r:(r["dev"], r["addr"]))
for r in rows:
    print(f'{r["dev"]:7s} {r["name"]:8s} {r["desc"][:14]:14s} {r["conn"]:10s} addr={r["addr"]:5d} w={r["width"]:2d} flags={r["flags"]:6d} unit={r["unit"]} eu={r["eu"]}')
json.dump(rows, open("tools/material-analysis/new-bindings.json","w",encoding="utf-8"), ensure_ascii=False, indent=1)
