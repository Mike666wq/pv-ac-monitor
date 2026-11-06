"""手动下载并解包 PyMuPDF wheel（pip 在本沙箱里无法完成 unpack/rename）。"""
import os, re, sys, ssl, zipfile, urllib.request, urllib.parse

TARGET = r"C:\Users\bbben\Desktop\BMS\ExperimentMonitorDemo\test-output\pylibs"
MIRRORS = [
    "https://pypi.tuna.tsinghua.edu.cn/simple/pymupdf/",
    "https://pypi.org/simple/pymupdf/",
    "https://mirrors.aliyun.com/pypi/simple/pymupdf/",
]
WHEEL_RE = re.compile(r'href="([^"#]+\.whl)[^"]*"')

def fetch(url, ctx):
    req = urllib.request.Request(url, headers={"User-Agent": "python/3.12"})
    with urllib.request.urlopen(req, timeout=60, context=ctx) as r:
        return r.read()

def main():
    os.makedirs(TARGET, exist_ok=True)
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    html = None
    base = None
    for mirror in MIRRORS:
        try:
            html = fetch(mirror, ctx).decode("utf-8", "replace")
            base = mirror
            print("index ok:", mirror)
            break
        except Exception as e:
            print("index fail:", mirror, e)
    if not html:
        sys.exit(1)
    links = [l for l in WHEEL_RE.findall(html) if "cp311-abi3-win_amd64" in l]
    if not links:
        links = [l for l in WHEEL_RE.findall(html) if "win_amd64" in l]
    if not links:
        sys.exit("no win_amd64 wheel")
    url = urllib.parse.urljoin(base, links[-1])
    print("wheel:", url)
    data = fetch(url, ctx)
    whl = os.path.join(TARGET, "pymupdf.whl")
    with open(whl, "wb") as f:
        f.write(data)
    print("downloaded", len(data), "bytes")
    with zipfile.ZipFile(whl) as z:
        z.extractall(TARGET)
    print("extracted to", TARGET)
    sys.path.insert(0, TARGET)
    import fitz
    print("fitz ok:", fitz.version if hasattr(fitz, "version") else fitz.__doc__)

if __name__ == "__main__":
    main()
