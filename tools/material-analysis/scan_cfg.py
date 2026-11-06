from pathlib import Path
root = Path(r"C:\Users\bbben\Desktop\BMS\ExperimentMonitorDemo\test-output\material-extract")
for name in ["db/DevMan3.dat","db/portconf.dat","db/DevCfg_PLC.dat","PLC_Modicon_MODBUS/CommPar.dat","db/syscfg.txt","db/par03.dic"]:
    p=root/name
    if not p.exists(): continue
    data=p.read_bytes()
    print(f"=== {name} ({len(data)} bytes)")
    # print ascii + hex of first 256 bytes
    asc=''.join(chr(b) if 32<=b<127 else '.' for b in data[:256])
    print("ASCII:", asc)
