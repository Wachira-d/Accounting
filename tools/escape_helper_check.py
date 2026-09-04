#!/usr/bin/env python3
"""ตัวหนี HTML ที่เขียนเองต้องหนีครบ 5 ตัว — ไม่งั้นคือ XSS ที่ดูเหมือนปลอดภัยแล้ว

ที่มา (บั๊กจริง · ผลตรวจ G-03):
`Layout.esc` ถูกยกเครื่องให้หนีครบ `& < > " '` ไปแล้ว แต่ทั้งเรพยังมี **สำเนามือ
อีกหลายสิบตัว** ที่หนีแค่ 2-3 ตัว:

    _esc(s) { return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;'); }

ค่าที่ผ่านตัวนี้แล้วเอาไปวางใน attribute (`title="${_esc(v)}"`) ยัง**แตกออกจาก
attribute ได้ด้วย `"`** แล้วเติม `onmouseover=` ต่อ ⇒ XSS เต็มรูป · JWT อยู่ใน
localStorage ⇒ ขโมย token (CLAUDE.md กฎเหล็ก #4 C)

อันตรายกว่าการไม่มีตัวหนีเลย เพราะคนอ่านโค้ดเห็น `_esc(...)` แล้วเชื่อว่าปลอดภัยแล้ว
— defect class เดียวกับ "ด่านที่เขียนไว้ครึ่งเดียว"

กติกาที่ตรวจ (shape-based): **โซ่ `.replace(...)` ที่ต่อกัน** โซ่ใดมี `&` → `&amp;`
แปลว่า "ตั้งใจเป็นตัวหนี HTML" ⇒ โซ่นั้นต้องหนีครบทั้ง 5 ตัว
ยกเว้นเมื่อ**มอบต่อ**ให้ตัวกลาง (`Layout.esc` / `AdminLayout.esc`) ซึ่งไม่มีโซ่อยู่แล้ว

⚠️ ต้องมองโซ่ **ข้ามบรรทัด** — `Layout.esc` เขียนบรรทัดละ `.replace()` ตัวเดียว
(รุ่นแรกของ checker ตัวนี้สแกนทีละบรรทัดแล้วไปฟ้อง `Layout.esc`/`jsArg`/`export.js`
ซึ่งถูกต้องอยู่แล้ว 7 จุด — "checker ที่ฟ้องผิด = checker ที่พังแล้ว")

หมายเหตุ: `jsArg` หนี `'` แบบ JS (`\\'`) ไม่ใช่ entity — ยอมรับทั้งสองรูป
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WWWROOT = ROOT / "Accounting" / "wwwroot"

# .replace( /re/flags , 'ค่า' )  — พอสำหรับโซ่หนีอักขระซึ่งเป็นรูปแบบง่าย ๆ เสมอ
CALL = re.compile(
    r"\.replace\(\s*/(?:\\.|\[[^\]]*\]|[^/\\])+/[a-z]*\s*,\s*"
    r"(?:'(?:\\.|[^'\\])*'|\"(?:\\.|[^\"\\])*\"|[^()]*(?:\([^()]*\)[^()]*)*)\)"
)
AMP = re.compile(r"/&/[a-z]*\s*,\s*['\"]&amp;['\"]")
NEEDLES = {
    "<": re.compile(r"/</[a-z]*\s*,\s*['\"]&lt;['\"]"),
    ">": re.compile(r"/>/[a-z]*\s*,\s*['\"]&gt;['\"]"),
    '"': re.compile(r"""/"/[a-z]*\s*,\s*['"]&quot;['"]"""),
    # `'` หนีได้สองแบบ: entity (HTML) หรือ backslash (JS string ของ jsArg)
    "'": re.compile(r"""/'/[a-z]*\s*,\s*(?:['"]&(?:#39|apos);['"]|"\\\\'")"""),
}
# รูปตาราง `.replace(/[&<>"']/g, c => ({...})[c])` — หนีครบในตัวมันเอง
TABLE_FORM = re.compile(r"""/\[[^\]]*&[^\]]*<[^\]]*\]/[a-z]*""")


def chains(text: str):
    """คืน (ตำแหน่งเริ่ม, ข้อความของโซ่) ของทุกโซ่ .replace() ที่ต่อกัน
    (คั่นด้วยช่องว่าง/ขึ้นบรรทัด/คอมเมนต์ท้ายบรรทัดได้)"""
    gap = re.compile(r"(?:\s|//[^\n]*\n)*")
    out, i, n = [], 0, len(text)
    while i < n:
        m = CALL.search(text, i)
        if not m:
            break
        start, end = m.start(), m.end()
        while True:
            g = gap.match(text, end)
            nxt = CALL.match(text, g.end())
            if not nxt:
                break
            end = nxt.end()
        out.append((start, text[start:end]))
        i = end
    return out


def scan(path: Path):
    text = path.read_text(encoding="utf-8", errors="replace")
    hits = []
    for pos, chain in chains(text):
        if not AMP.search(chain) or TABLE_FORM.search(chain):
            continue
        missing = [c for c, rx in NEEDLES.items() if not rx.search(chain)]
        if missing:
            lineno = text.count("\n", 0, pos) + 1
            snippet = " ".join(chain.split())[:120]
            hits.append((lineno, missing, snippet))
    return hits


def main() -> int:
    targets = sys.argv[1:] or [str(WWWROOT)]
    files = []
    for t in targets:
        p = Path(t)
        files.extend(sorted(list(p.rglob("*.html")) + list(p.rglob("*.js"))) if p.is_dir() else [p])

    total = 0
    for f in files:
        for lineno, missing, snippet in scan(f):
            total += 1
            rel = f.relative_to(ROOT) if str(f).startswith(str(ROOT)) else f
            print(f"{rel}:{lineno}: ตัวหนี HTML ไม่ครบ — ขาด {' '.join(missing)}")
            print(f"    => {snippet}")
            print("    → ใช้ Layout.esc / AdminLayout.esc (ตัวกลาง) หรือหนีให้ครบทั้ง 5 ตัว")

    print(f"\nตรวจ {len(files)} ไฟล์ · ตัวหนีที่ไม่ครบ {total} จุด")
    return 1 if total else 0


if __name__ == "__main__":
    raise SystemExit(main())
