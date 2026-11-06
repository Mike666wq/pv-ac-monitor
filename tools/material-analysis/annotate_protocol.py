import json

p = r"C:\Users\bbben\Desktop\BMS\ExperimentMonitorDemo\protocol.json"
d = json.load(open(p, encoding="utf-8"))
d["status"] = ("工程配置已还原；2026-10-03 与 2026-11-06 现场串口抓包确认 "
               "PLC/DS666/DJSF6682 的帧与 float32=ABCD 解码成立；"
               "DDSU666(站号6) 两次均未取得有效响应（站号或接线待查）；不作为完整已验证协议发布")
fb = d["float32_byte_order"]
fb["hardware_verified"] = True
fb["evidence"] = ("data/serial/20261003-144532：读 D125 起 2 字，RX payload 41 C9 99 9C，"
                  "ABCD 解码=25.2 ℃；DS666 0x2000 起 2 字 payload 43 66 E6 66=230.9 V，均与物理量一致")
fb["warning"] = ("FX2N 编程手册印刷页 60 写“高位为大的号码”（按字面即线上 CDAB），"
                 "但本机 PLC 程序实际按高字在前存储（线上 ABCD）；以现场抓包为准，换 PLC 程序需重新核对")
open(p, "w", encoding="utf-8", newline="\n").write(json.dumps(d, ensure_ascii=False, indent=2))
print("ok")
