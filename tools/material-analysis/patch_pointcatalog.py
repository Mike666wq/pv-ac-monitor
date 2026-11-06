import io

p = r"C:\Users\bbben\Desktop\BMS\ExperimentMonitorDemo\PointCatalog.cs"
t = io.open(p, encoding="utf-8").read()

n1 = t.count(',"U16 / I16",')
t = t.replace(',"U16 / I16",', ',"INT16",')

n2 = t.count(',"UINT16",')
t = t.replace(',"UINT16",', ',"INT16",')

old_dc = ("            // 0x0000 电压 int16 ×0.1V；0x0003 电流 int16 ×0.001A；0x000F 视在功率 int16 ×1VA。\n")
new_dc = ("            // 0x0000 电压 int16 ×0.1V；0x0003 电流 int16 ×0.001A；0x000F 视在功率 int16 ×1VA。\n"
          "            // 表里写的是 Int（有符号）：反向电流/夜间倒灌是负数，用 UINT16 会显示成 65xxx。\n")
n3 = t.count(old_dc)
t = t.replace(old_dc, new_dc)

old_ctrl = "            // 控制/设定：可写候选，但仍需 control-config.json 的 confirmed+allowWrite 才允许 06。\n"
new_ctrl = ("            // 控制/设定：可写候选，但仍需 control-config.json 的 confirmed+allowWrite 才允许 06。\n"
            "            // 16 位点统一按 INT16（FX 系列 D 寄存器是“最高位为符号”的 16 位有符号数），否则 Decode 会走\n"
            "            // “U16 / I16”兜底分支、只出文本不出数值，趋势/历史/导出里全是空值。\n")
n4 = t.count(old_ctrl)
t = t.replace(old_ctrl, new_ctrl)

n5 = t.count('Add("0006H","原寄存器","诊断"')
t = t.replace('Add("0006H","原寄存器","诊断"', 'Add("0006H","原寄存器(位串)","诊断"')

io.open(p, "w", encoding="utf-8", newline="\r\n").write(t)
print("U16/I16 -> INT16:", n1, "| UINT16 -> INT16:", n2, "| dc comment:", n3, "| ctrl comment:", n4, "| 0006H:", n5)
