import fitz
from pathlib import Path
src = Path(r"C:\Users\bbben\Desktop\BMS\ForceControlAnalysis\物料")
out = Path(r"C:\Users\bbben\Desktop\BMS\ExperimentMonitorDemo\test-output\material-extract\text")
out.mkdir(parents=True, exist_ok=True)
for pdf in sorted(src.glob("*.pdf")):
    try:
        doc = fitz.open(pdf)
    except Exception as e:
        print("FAIL", pdf.name, e); continue
    text = []
    for i, page in enumerate(doc):
        text.append(f"\n===== PAGE {i+1} =====\n" + page.get_text())
    dst = out / (pdf.stem + ".txt")
    dst.write_text("".join(text), encoding="utf-8")
    print(f"{pdf.name}: pages={doc.page_count} chars={sum(len(t) for t in text)} -> {dst.name}")
