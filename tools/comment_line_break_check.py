#!/usr/bin/env python3
"""comment_line_break_check — คอมเมนต์ `//` / `///` ที่ถูกตัดบรรทัดกลางประโยค (CS1519/CS1010)

ที่มา (รอบ 199 · CI แดง 2599df78): ทีม K3 เขียน doc-comment ที่ยกข้อความจริงของใบ Scommerce ซึ่งมี **ขึ้นบรรทัดใหม่
อยู่ในข้อความ** ⇒ ครึ่งหลัง ("อิเล็กทรอนิกส์ฉบับใหม่แทน") หลุดออกมาเป็นบรรทัดที่ไม่มี `///` นำหน้า ⇒ คอมไพเลอร์อ่านเป็นโค้ด
⇒ build ล้มทั้ง solution. checker ทุกตัวในเรพผ่านเพราะไม่มีใครดู "บรรทัดโค้ด" ที่ขึ้นต้นด้วยอักษรไทย

กติกา (lexical ล้วน ไม่ต้องรู้ชนิด — F4 ข้อ 1): บรรทัดที่อักขระแรกเป็น**อักษรไทย** และบรรทัดก่อนหน้าเป็นคอมเมนต์ `//`
(ไม่อยู่ในสตริงหลายบรรทัด) ⇒ ฟ้อง. โค้ด C# จริงไม่มีทางขึ้นต้นบรรทัดด้วยอักษรไทย ยกเว้นเนื้อสตริงหลายบรรทัด
(verbatim `@"…"` / raw `\"\"\"…\"\"\"`) ซึ่งบรรทัดก่อนหน้าไม่ใช่คอมเมนต์

`--self-test` = ใส่บั๊กกลับแล้วต้องฟ้อง · ของถูกต้องไม่ฟ้อง
"""
import os
import re
import sys
import tempfile

ROOTS = ["Accounting", "Accounting.Tests"]
THAI_START = re.compile(r"^\s*[฀-๿]")
COMMENT = re.compile(r"^\s*//")


def scan_text(text: str):
    """คืนเลขบรรทัด (1-based) ที่เป็นครึ่งหลังของคอมเมนต์ที่ถูกตัด"""
    bad = []
    lines = text.split("\n")
    for i in range(1, len(lines)):
        if THAI_START.match(lines[i]) and COMMENT.match(lines[i - 1]):
            bad.append(i + 1)
    return bad


def scan_repo(root_dir: str):
    errors = []
    for root in ROOTS:
        base = os.path.join(root_dir, root)
        for dp, dns, fns in os.walk(base):
            dns[:] = [d for d in dns if d not in ("bin", "obj", "node_modules")]
            for fn in fns:
                if not fn.endswith(".cs"):
                    continue
                p = os.path.join(dp, fn)
                try:
                    text = open(p, encoding="utf-8").read()
                except (UnicodeDecodeError, OSError):
                    continue
                for ln in scan_text(text):
                    errors.append(f"{os.path.relpath(p, root_dir)}:{ln}")
    return errors


def self_test() -> int:
    bad = '    /// ("…และออกใบกำกับภาษี\nอิเล็กทรอนิกส์ฉบับใหม่แทน") — ยึด\n    private int X;\n'
    ok = ('    /// ("…และออกใบกำกับภาษี⏎อิเล็กทรอนิกส์ฉบับใหม่แทน")\n'
          '    private const string S = @"บรรทัดแรก\nบรรทัดสองภาษาไทย";\n'
          '    // คอมเมนต์ปกติ\n    // บรรทัดต่อที่มี // นำหน้า\n')
    fails = []
    if scan_text(bad) != [2]:
        fails.append("self-test: คอมเมนต์ที่ถูกตัดบรรทัดต้องถูกฟ้อง")
    if scan_text(ok):
        fails.append("self-test: ของถูกต้องถูกฟ้องผิด")
    with tempfile.TemporaryDirectory() as t:
        os.makedirs(os.path.join(t, "Accounting"))
        open(os.path.join(t, "Accounting", "Bad.cs"), "w", encoding="utf-8").write(bad)
        if not scan_repo(t):
            fails.append("self-test: สแกนไฟล์จริงไม่เจอบั๊กที่ใส่กลับ")
    for f in fails:
        print("❌ " + f)
    if not fails:
        print("self-test: ผ่าน")
    return 1 if fails else 0


def main() -> int:
    if "--self-test" in sys.argv:
        return self_test()
    root_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    errors = scan_repo(root_dir)
    if errors:
        print("❌ คอมเมนต์ // ถูกตัดบรรทัด — ครึ่งหลังกลายเป็นโค้ด (CS1519/CS1010):")
        for e in errors:
            print("   " + e)
        return 1
    print("✅ comment_line_break_check: ไม่มีคอมเมนต์ที่ถูกตัดบรรทัด")
    return 0


if __name__ == "__main__":
    sys.exit(main())
