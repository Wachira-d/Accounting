#!/usr/bin/env python3
"""ปุ่มส่งออกบนหน้าเว็บห้าม "ขอหน้าเดียวขนาดใหญ่" แล้วถือว่าได้ครบ (EXPORT_AUDIT 2026-10-14 §X2)

ที่มา: ปุ่ม Export ของหน้าเอกสาร/ซื้อสินค้า/สมุดรายวันขอ `?page=1&pageSize=10000` แต่เซิร์ฟเวอร์บีบเหลือ 200
(`PagedRequest.PageSize = Math.Clamp(PageSize, 1, 200)`) ⇒ ไฟล์ขาดแถวเงียบ ๆ ทุกบริษัทที่มีข้อมูลเกิน 200 แถว

กติกา: ในฟังก์ชันของหน้าเว็บที่ชื่อมีคำว่า export (exportAll · exportSelectedPur · exportList …) ห้ามมี `pageSize=N` ที่ N > 200
— ทางที่ถูก: ส่งออกจากเซิร์ฟเวอร์ (เช่น `GET document/export` ที่วนทุกหน้าเอง) หรือวนดึงทีละหน้า (`pageSize=200` + `totalPages`)

baseline = จุดเดิมของทีมอื่นที่ยังไม่แก้ (ratchet: แก้แล้วต้องตัดออก — ตัวตรวจบอกเอง) · negative test ในตัวทุกครั้งที่รัน
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
PAGES = ROOT / "Accounting" / "wwwroot" / "pages"
CAP = 200

# ไฟล์:ชื่อฟังก์ชัน ที่ยังขอหน้าเดียวขนาดใหญ่ — ว่าง (journals.html:exportAll ทีม X3 แก้แล้ว) · ห้ามเพิ่มของใหม่
BASELINE: set = set()

FUNC_RE = re.compile(r"(?:async\s+)?(?:function\s+)?([A-Za-z_$][\w$]*)\s*\([^)]*\)\s*\{")
PAGESIZE_RE = re.compile(r"pageSize=(\d+)")


def strip_comments(src: str) -> str:
    src = re.sub(r"/\*.*?\*/", lambda m: "\n" * m.group(0).count("\n"), src, flags=re.S)
    return re.sub(r"(?m)(^|[^:'\"`\\])//[^\n]*", r"\1", src)


def body_end(src: str, open_idx: int) -> int:
    depth = 0
    for i in range(open_idx, len(src)):
        ch = src[i]
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return i
    return len(src)


def scan(text: str):
    """[(ชื่อฟังก์ชัน, N)] ที่ผิดกติกา"""
    src = strip_comments(text)
    hits = []
    for m in FUNC_RE.finditer(src):
        name = m.group(1)
        if "export" not in name.lower() or name in ("if", "for", "while", "switch", "catch"):
            continue
        start = m.end() - 1
        body = src[start:body_end(src, start) + 1]
        for p in PAGESIZE_RE.finditer(body):
            if int(p.group(1)) > CAP:
                hits.append((name, int(p.group(1))))
    return hits


def self_test():
    bug = """
      async exportAll(format) {
        const api = Layout.api(); if (!api) return;
        let q = '?page=1&pageSize=10000';
        const res = await api.getDocuments(q);
      },
    """
    ok_loop = """
      async exportList(format) {
        for (let page = 1; page <= 500; page++) {
          const res = await API.get(`/x?page=${page}&pageSize=200`);
          if (page >= (res.data.totalPages || 1)) break;
        }
      },
      async load() { const r = await api.getAccounts('?pageSize=1000'); },
    """
    assert scan(bug) == [("exportAll", 10000)], scan(bug)
    assert scan(ok_loop) == [], scan(ok_loop)
    # คอมเมนต์ที่เล่าบั๊กเดิมต้องไม่ถูกนับ
    assert scan("async exportAll(f) {\n  // เดิมขอ pageSize=10000\n  return 1;\n}") == []


def main() -> int:
    self_test()
    bad = []
    seen = set()
    for f in sorted(PAGES.glob("*.html")):
        for name, n in scan(f.read_text(encoding="utf-8")):
            key = f"{f.name}:{name}"
            seen.add(key)
            if key not in BASELINE:
                bad.append(f"{f.relative_to(ROOT)} · {name}() ขอ pageSize={n} (เซิร์ฟเวอร์ให้สูงสุด {CAP}) ⇒ ไฟล์ส่งออกขาดแถวเงียบ")
    for stale in sorted(BASELINE - seen):
        print(f"ℹ️  แก้แล้ว — ตัดออกจาก BASELINE ได้: {stale}")
    if bad:
        print("❌ ปุ่มส่งออกขอหน้าเดียวขนาดใหญ่ (ได้แค่ 200 แถว):")
        for b in bad:
            print("   " + b)
        print("   → ส่งออกจากเซิร์ฟเวอร์ (วนทุกหน้าเอง) หรือวน pageSize=200 จนครบ totalPages")
        return 1
    print(f"✅ ปุ่มส่งออกไม่ขอหน้าเดียวเกิน {CAP} (baseline ของทีมอื่น {len(BASELINE & seen)} จุด)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
