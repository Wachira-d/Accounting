#!/usr/bin/env python3
"""ตรวจว่า **โฟลเดอร์อัปโหลดรูปทุกตัวเสิร์ฟผ่าน static handler ได้จริง**

ที่มา (เคสจริง): อัปโหลดโลโก้ "ชื่อทางการค้า" สำเร็จ ไฟล์ถูกเขียนลง
`wwwroot/uploads/brand-logos/{companyId}/…` และ DB เก็บ URL ถูกต้อง — แต่หน้าเว็บ
ขึ้นรูปแตก เพราะ middleware ใน `Program.cs` เป็น **allow-list**: อะไรที่ขึ้นต้นด้วย
`/uploads` แต่ไม่อยู่ในลิสต์ → ตอบ 404 ทิ้ง. โฟลเดอร์ `brand-logos` ไม่เคยถูกเพิ่ม
เข้าลิสต์ ⇒ โลโก้แบรนด์ 404 ทุกไฟล์ตั้งแต่วันแรก (เช่นเดียวกับ `/uploads/slips`
ของสลิปค่าบริการ)

อาการที่ผู้ใช้เห็นคือ "รูปไม่ขึ้น" ซึ่งไล่ย้อนกลับมาถึง middleware ตัวนี้ยากมาก —
ทั้งฝั่งอัปโหลดและฝั่ง DB ถูกต้องหมด ผิดแค่ "เส้นทางที่ยอมให้เสิร์ฟ"

กติกา: ทุก web prefix ที่ส่งเข้า `ProcessAndSaveAsync(...)` (ตัวประมวลผลรูปที่คืน
URL ให้เบราว์เซอร์โหลด) ต้องอยู่ใน `publicUploadPrefixes` ของ `Program.cs`
ยกเว้นโฟลเดอร์ที่ตั้งใจให้เป็นส่วนตัวและมี endpoint ตรวจสิทธิ์ของตัวเอง
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Accounting"
PROGRAM = SRC / "Program.cs"

# โฟลเดอร์ที่ **ตั้งใจ** ไม่เสิร์ฟตรง — มี endpoint ตรวจ JWT + CompanyId ของตัวเอง
PRIVATE_BY_DESIGN = {
    "/uploads/attachments",   # ดาวน์โหลดผ่าน /attachments/{id}/download
    # สลิปโอนเงินของแขกที่พัก — มีชื่อผู้โอน/เลขบัญชี/ยอด = PII
    # เสิร์ฟผ่าน `reservations/{id|token}/slip` ที่ตรวจสิทธิ์ (LDG-P2-06)
    "/uploads/lodging-slips",
}


def public_prefixes() -> set[str]:
    src = PROGRAM.read_text(encoding="utf-8", errors="replace")
    m = re.search(r"var publicUploadPrefixes = new\[\]\s*\{(.*?)\};", src, re.S)
    if not m:
        print("!! หา publicUploadPrefixes ใน Program.cs ไม่เจอ — checker ล้าสมัย")
        sys.exit(2)
    body = re.sub(r"//[^\n]*", "", m.group(1))
    return set(re.findall(r'"([^"]+)"', body))


def main():
    allowed = public_prefixes()
    problems = []

    for f in sorted(SRC.rglob("*.cs")):
        if any(part in {"obj", "bin", ".claude"} for part in f.parts):
            continue
        src = f.read_text(encoding="utf-8", errors="replace")
        if "ProcessAndSaveAsync(" not in src:
            continue
        # เก็บตัวแปรที่ถือ web prefix ไว้ในไฟล์นี้ (web/relDir/webBase ฯลฯ)
        var_urls = dict(re.findall(r'\b(\w+)\s*=\s*\$?"(/?uploads/[a-z0-9-]+)', src))
        for m in re.finditer(r"ProcessAndSaveAsync\(([^;]*?)\)", src, re.S):
            args = m.group(1)
            # อาร์กิวเมนต์ webBaseUrl = ตัวที่ 5 (stream, contentType, fileName, dir, web, …)
            parts = [a.strip() for a in re.split(r",(?![^(]*\))", args)]
            if len(parts) < 5:
                continue
            web = parts[4]
            hit = re.search(r'"(/?uploads/[a-z0-9-]+)', web)
            if hit:
                prefix = hit.group(1)
            else:
                name = re.fullmatch(r'"?\+?\s*"?/?"?\s*\+?\s*(\w+)', web.replace('"/" +', "").strip())
                key = name.group(1) if name else web.strip()
                if key not in var_urls:
                    continue
                prefix = var_urls[key]
            if not prefix.startswith("/"):
                prefix = "/" + prefix
            if prefix in PRIVATE_BY_DESIGN or prefix in allowed:
                continue
            line = src[:m.start()].count("\n") + 1
            problems.append((f.relative_to(ROOT), line, prefix))

    for rel, line, prefix in problems:
        print(f"{rel}:{line}: อัปโหลดรูปลง {prefix} แต่ static handler ไม่ยอมเสิร์ฟ → 404 (รูปไม่ขึ้น)")
        print(f"    → เพิ่ม \"{prefix}\" ใน publicUploadPrefixes ของ Program.cs "
              "(หรือใส่ใน PRIVATE_BY_DESIGN ถ้าตั้งใจให้ดาวน์โหลดผ่าน endpoint ที่ตรวจสิทธิ์)")

    print(f"\nตรวจเส้นทางอัปโหลด · โฟลเดอร์ที่เขียนได้แต่เสิร์ฟไม่ได้ {len(problems)} จุด")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
