#!/usr/bin/env python3
"""ตรวจ "รายการแท็บที่ต้องซ่อน" ที่ลืมใส่แท็บใหม่ — แท็บค้างบนจอทับแท็บอื่น

ที่มา (บั๊กจริง): `settings.html` สลับแท็บด้วยการไล่ซ่อนทุก panel จาก **อาร์เรย์
ที่พิมพ์มือ** แล้วค่อยโชว์ตัวที่เลือก:

    ['tabCompany','tabLicense', ... ,'tabSignature'].forEach(
        id => document.getElementById(id)?.classList.add('hidden'));

เพิ่มการ์ด `tabSecurity` เข้ามาแล้ว **ลืมเติมชื่อในอาร์เรย์** ⇒ ไม่มีใครซ่อนมัน
⇒ เปิดแท็บ "ความปลอดภัยบัญชี" ครั้งหนึ่ง แล้วสลับไปแท็บอื่น การ์ดนั้นยังค้าง
อยู่บนจอทับเนื้อหาแท็บใหม่ — เป็น defect class เดียวกับ `MENU_SECTIONS`
("รายการที่คัดลอกมาด้วยมือ = drift แน่นอน แค่รอเวลา")

ทำไมต้องเป็น checker แยก: มันเป็น **สตริงที่ถูกไวยากรณ์ทุกประการ** ⇒
`node --check` ไม่เห็น · checker ฝั่ง C# ไม่เกี่ยว · `js_dup_method_check`
ดูคีย์ซ้ำ ไม่ได้ดูว่า id ใน DOM ครบในอาร์เรย์ไหม. ตัวตัดสินอยู่คนละที่กับ
markup จึงต้องโยงสองฝั่งเข้าหากันเหมือน `upload_route_check`/`csp_external_ref_check`

กติกา:
  • หา element ใน markup ที่มี id ขึ้นต้น `tab` + ตัวใหญ่ (= panel ของแท็บ)
    — **ไม่กรองด้วย class `hidden`** เพราะ panel ที่เปิดอยู่ตอนโหลดหน้าก็ต้อง
    อยู่ในรายการเหมือนกัน (ไม่งั้นสลับออกจากมันแล้วมันค้าง = บั๊กตัวเดียวกัน)
  • หาอาร์เรย์ id ที่ตามด้วย `.forEach(... classList.add('hidden') ...)`
  • id ที่เป็น panel แต่ไม่อยู่ในอาร์เรย์ใดเลย = ไม่มีใครซ่อน → ฟ้อง

ข้อควรระวังที่เจอตอนเขียน (เก็บไว้กันคนถัดไปทำซ้ำ):
  • ต้องตัดคอมเมนต์ HTML ก่อน ไม่งั้นตัวอย่าง markup ในคอมเมนต์ถูกนับเป็น panel
    (ซ้ำรอย localstorage_key_check ที่เคยฟ้องเอกสารของตัวเอง)
  • ต้องคงจำนวนบรรทัดไว้ตอนตัด ไม่งั้นเลขบรรทัดที่ฟ้องเพี้ยน
  • ระยะระหว่าง `.forEach(` กับ `classList.add` ใช้ `[\s\S]` ไม่ใช่ `[^)]` —
    รุ่นแรกใช้ `[^)]` แล้ว **จับไม่ได้เลย** เพราะข้อความจริงมี `)` คั่นอยู่
    (`getElementById(id)?.classList…`) ⇒ อาร์เรย์ว่าง ⇒ ข้ามไฟล์ ⇒ ผ่านทั้งที่มีบั๊ก
    (negative test จับได้ตรงนี้ — ถ้าไม่รัน จะ ship checker ที่ไม่มีด่านจริง)
  • หน้าที่ไม่มีอาร์เรย์แบบนี้เลย = ไม่ได้ใช้กลไกนี้ → ข้ามทั้งไฟล์ ห้ามฟ้อง
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WWWROOT = os.path.join(ROOT, "Accounting", "wwwroot")

# id ของ panel: ขึ้นต้น tab แล้วตามด้วยตัวใหญ่ (tabCompany, tabSecurity)
PANEL_ID = re.compile(r'id\s*=\s*"(tab[A-Z]\w*)"')
# อาร์เรย์สตริงที่ถูกไล่ซ่อนด้วย classList.add('hidden')
HIDE_ARRAY = re.compile(
    r"\[([^\[\]]*?)\]\s*\.forEach\s*\([\s\S]{0,200}?classList\.add\(\s*['\"]hidden['\"]",
    re.S,
)
STR_IN_ARRAY = re.compile(r"['\"]([A-Za-z_$][\w$]*)['\"]")


def strip_html_comments(src: str) -> str:
    """ตัด <!-- ... --> โดยคงจำนวนบรรทัดไว้เท่าเดิม"""
    out = []
    i = 0
    while True:
        j = src.find("<!--", i)
        if j < 0:
            out.append(src[i:])
            break
        out.append(src[i:j])
        k = src.find("-->", j)
        if k < 0:
            out.append("\n" * src.count("\n", j))
            break
        out.append("\n" * src.count("\n", j, k + 3))
        i = k + 3
    return "".join(out)


def check_file(path: str):
    with open(path, encoding="utf-8", errors="replace") as fh:
        raw = fh.read()
    src = strip_html_comments(raw)

    listed = set()
    for m in HIDE_ARRAY.finditer(src):
        listed.update(STR_IN_ARRAY.findall(m.group(1)))
    if not listed:
        return []                      # หน้านี้ไม่ได้ใช้กลไกนี้

    problems = []
    for m in PANEL_ID.finditer(src):
        pid = m.group(1)
        if pid in listed:
            continue
        line = src.count("\n", 0, m.start()) + 1
        problems.append((line, pid))
    return problems


def main() -> int:
    target = sys.argv[1] if len(sys.argv) > 1 else WWWROOT
    hits = 0
    files = []
    if os.path.isfile(target):
        files = [target]
    else:
        for dirpath, _dirs, names in os.walk(target):
            files.extend(os.path.join(dirpath, n) for n in names if n.endswith(".html"))

    for path in sorted(files):
        for line, pid in check_file(path):
            hits += 1
            rel = os.path.relpath(path, ROOT)
            print(f"{rel}:{line}: panel '{pid}' ไม่อยู่ในรายการที่ถูกซ่อนตอนสลับแท็บ "
                  f"— เปิดแล้วจะค้างทับแท็บอื่น (เติมชื่อลงอาร์เรย์ .forEach ที่ add('hidden'))")

    if hits:
        print(f"\n✗ พบ {hits} จุด")
        return 1
    print("✓ ทุก panel ของแท็บอยู่ในรายการที่ถูกซ่อน")
    return 0


if __name__ == "__main__":
    sys.exit(main())
