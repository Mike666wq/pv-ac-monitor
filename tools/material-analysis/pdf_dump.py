"""PDF 辅助：导出文本或渲染页面为 PNG。

用法：
  python pdf_dump.py text   <pdf> [起始页 结束页]
  python pdf_dump.py render <pdf> <outdir> <页1,页2,...> [缩放,默认2.0]
"""
import sys, os
sys.path.insert(0, r"C:\Users\bbben\Desktop\BMS\ExperimentMonitorDemo\test-output\pylibs")
import pymupdf


def main():
    cmd = sys.argv[1]
    pdf = sys.argv[2]
    doc = pymupdf.open(pdf)
    if cmd == "text":
        first = int(sys.argv[3]) if len(sys.argv) > 3 else 1
        last = int(sys.argv[4]) if len(sys.argv) > 4 else doc.page_count
        for i in range(first - 1, min(last, doc.page_count)):
            page = doc[i]
            print(f"===== PAGE {i + 1} =====")
            print(page.get_text("text"))
    elif cmd == "render":
        outdir = sys.argv[3]
        pages = [int(x) for x in sys.argv[4].split(",")]
        zoom = float(sys.argv[5]) if len(sys.argv) > 5 else 2.0
        os.makedirs(outdir, exist_ok=True)
        mat = pymupdf.Matrix(zoom, zoom)
        for p in pages:
            page = doc[p - 1]
            pix = page.get_pixmap(matrix=mat)
            path = os.path.join(outdir, f"p{p:03d}.png")
            pix.save(path)
            print(path, pix.width, "x", pix.height)
    elif cmd == "info":
        print("pages:", doc.page_count)
        for i in (0,):
            print(doc[i].get_fonts())
    doc.close()


if __name__ == "__main__":
    main()
