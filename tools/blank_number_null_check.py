#!/usr/bin/env python3
"""ตรวจ "ช่องตัวเลขว่าง → ส่ง null" ในฟอร์มฝั่งหน้าเว็บ

ที่มา (ผู้ใช้รายงาน 2026-09-21 · ภาพหน้าจอตอนสร้างประเภทห้อง "Nordic Tent"):
ช่อง "เตียงเสริมสูงสุด" เว้นว่าง → หน้าเว็บส่ง `maxExtraBeds: null` →
System.Text.Json แปลง `null` เข้า `int` (ไม่ใช่ `int?`) **ไม่ได้** →
**โยนทั้ง body ทิ้ง** → ผู้ใช้เห็น error สองชั้นพร้อมกัน:

    One or more validation errors occurred.
      — dto: The dto field is required.
      — $.maxExtraBeds: The JSON value could not be converted to System.Int32.

และ **ไม่มีอะไรถูกบันทึกเลย** ทั้งที่ช่องนั้นไม่บังคับ · บรรทัดที่สองคือ
ตัวจริง บรรทัดแรกเป็นผลพวง (ไม่มีช่องชื่อ "dto" บนหน้าจอให้แก้)

ทางที่ถูก: **ตัดคีย์ทิ้ง** เมื่อช่องตัวเลขว่าง → ค่า default ที่ DTO ประกาศไว้
ทำงาน (`MaxExtraBeds = 0` · `StandardOccupancy = 2`) · ช่องที่ "ว่าง = ศูนย์"
ประกาศที่ตัวช่องเองด้วย `data-blank="0"` — **ห้ามเก็บเป็นลิสต์ชื่อฟิลด์ในโค้ด**
เพราะลิสต์คือสำเนาที่สองของกติกาใน DTO ที่ไม่มีใครอัปเดตตอนเพิ่มฟิลด์ใหม่
(F2 ข้อ 4) — ของเดิมใน `lodging-settings.html` มีลิสต์แบบนั้น 2 ชุดใน `readProp`
ส่วน `readForm` ของ 6 modal **ไม่มีเลย** ⇒ ทุก modal พังทันทีที่เว้นช่องว่าง

═══ ขอบเขตแคบโดยตั้งใจ (F4 ข้อ 1/3) ═══
ไม่ resolve ชนิดใด ๆ — จับเฉพาะรูปแบบข้อความตรงตัวใน `wwwroot/`:
ตัวอ่านฟอร์มที่เช็ค `type === 'number'` (หรือ `type == "number"`) แล้วให้ค่าเป็น
`null` เมื่อสตริงว่าง · ไม่ยุ่งกับช่องข้อความ (blank → null เป็นกติกาของเรพนี้
สำหรับ "ล้างค่า" ตาม กฎเหล็ก #4 B และถูกต้องแล้ว)
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WWWROOT = ROOT / "Accounting" / "wwwroot"

# `el.type === 'number'` … `=== '' ? null` (ในบรรทัดเดียวกัน)
PATTERN = re.compile(
    r"""type\s*===?\s*['"]number['"].*?==?=\s*['"]['"]\s*\?\s*null""")


def strip_comments(text: str) -> str:
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return "\n".join(re.sub(r"(^|[^:])//.*$", r"\1", ln) for ln in text.splitlines())


def check_file(path: Path):
    problems = []
    body = strip_comments(path.read_text(encoding="utf-8", errors="replace"))
    for line_no, line in enumerate(body.splitlines(), 1):
        if PATTERN.search(line):
            problems.append((line_no, line.strip()))
    return problems


SELF_TEST_BAD = """
      readForm(id) {
        for (const el of f.elements) {
          if (el.type === 'checkbox') d[el.name] = el.checked;
          else if (el.type === 'number') d[el.name] = el.value === '' ? null : +el.value;
          else d[el.name] = el.value === '' ? null : el.value;
        }
      },
"""
SELF_TEST_OK = """
      _readNumberInto(d, el) {
        if (el.value !== '') { d[el.name] = +el.value; return; }
        if (el.dataset.blank === '0') { d[el.name] = 0; return; }
        delete d[el.name];
      },
      readForm(id) {
        for (const el of f.elements) {
          if (el.type === 'number') this._readNumberInto(d, el);
          else d[el.name] = el.value === '' ? null : el.value;   // ช่องข้อความ: ถูกแล้ว
          // else if (el.type === 'number') d[el.name] = el.value === '' ? null : +el.value;
        }
      },
"""


def self_test() -> int:
    """negative test 3 ทิศ — ด่านที่ไม่มีเทสต์ทิศตรงข้าม = ไม่มีด่าน (F2 ข้อ 6)"""
    import tempfile
    ok = True
    with tempfile.TemporaryDirectory() as d:
        root = Path(d)
        bad = root / "bad.html"
        bad.write_text(SELF_TEST_BAD, encoding="utf-8")
        good = root / "good.html"
        good.write_text(SELF_TEST_OK, encoding="utf-8")

        if not check_file(bad):
            print("❌ self-test: ไม่จับ 'ช่องตัวเลขว่าง → null'")
            ok = False
        hits = check_file(good)
        if hits:
            print(f"❌ self-test: ฟ้องไฟล์ที่ถูกแล้ว (บรรทัดข้อความ/คอมเมนต์) — {hits}")
            ok = False
    print("✅ self-test ผ่าน 3 ทิศ (จับ · ช่องข้อความไม่ฟ้อง · คอมเมนต์ไม่ฟ้อง)"
          if ok else "❌ self-test ไม่ผ่าน")
    return 0 if ok else 1


def main():
    if "--self-test" in sys.argv:
        return self_test()

    targets = [Path(a) for a in sys.argv[1:] if not a.startswith("--")]
    if not targets:
        targets = sorted(WWWROOT.rglob("*.html")) + sorted(WWWROOT.rglob("*.js"))

    total = 0
    for path in targets:
        if not path.is_file() or path.suffix not in (".html", ".js"):
            continue
        for line_no, text in check_file(path):
            total += 1
            rel = path.relative_to(ROOT) if ROOT in path.parents else path
            print(f"{rel}:{line_no}: ช่องตัวเลขที่เว้นว่างถูกส่งเป็น `null`\n    {text}")
            print("    → null เข้า int/decimal ที่ไม่ใช่ nullable ไม่ได้ ⇒ ทั้ง body ถูกโยนทิ้ง "
                  "⇒ ไม่มีอะไรถูกบันทึกเลย · ให้ **ตัดคีย์ทิ้ง** (`delete d[el.name]`) "
                  "แล้วปล่อยให้ค่า default ของ DTO ทำงาน · ช่องที่ \"ว่าง = 0\" ใส่ "
                  "`data-blank=\"0\"` ที่ตัว input")

    print(f"\nตรวจ {len(targets)} ไฟล์ · ช่องตัวเลขที่ส่ง null {total} จุด")
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())
