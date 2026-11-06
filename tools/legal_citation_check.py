#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ห้ามอ้างเลขมาตรา พ.ร.บ.การบัญชี 2543 ผิดซ้ำ (ratchet · baseline 0)

═══ ที่มา (รอบ 250 WP-06 · erp-review/2026-11-06/RETENTION_LAW_CHECK.md §2 + ภาคผนวก + ฝ่ายค้าน GO ทั้ง A–D) ═══
~150 จุดในโค้ด/UI/เทสต์/CLAUDE.md อ้าง "พ.ร.บ.การบัญชี ม.10" เป็นระยะเก็บเอกสาร 5 ปี — ตัวบทจริง: ม.10 = ปิดบัญชี/รอบบัญชี ·
ม.14 = เก็บ ≥ 5 ปีนับแต่วันปิดบัญชี · ม.11 = จัดทำ/ยื่นงบ · ม.19 = ต้องมีผู้ทำบัญชี (คุณสมบัติตาม ม.7(6)) · "ม.11 ทวิ" ไม่มีในตัวบท
(ฐานการเก็บอิเล็กทรอนิกส์คือ พ.ร.บ.ธุรกรรมทางอิเล็กทรอนิกส์ 2544 ม.12) · "ท.ป.4/2528" เป็นคำสั่งอัตราหัก ณ ที่จ่าย ไม่ใช่ระยะเก็บ
— คำอ้างผิดถูก copy ต่อจากต้นฉบับเดียว (CLAUDE.md §H) จนกระจายทั้งเรพ ⇒ ตัวตรวจนี้กันตัวที่ 151

═══ กติกา (ระดับบรรทัด · F4 ข้อ 3 — ไม่ตาม taint) ═══
ฟ้องบรรทัดใน Accounting/** (.cs .html .js) · Accounting.Tests/** (.cs) · CLAUDE.md ที่:
  R1 อ้าง "พ.ร.บ.(การ)บัญชี … ม.10" บนบรรทัดเดียวกับคำเรื่องเก็บรักษา (เก็บ · 5 ปี · retention · หลักฐาน · legal hold)
  R2 "ม.11 ทวิ" (ไม่มีในตัวบท)
  R3 "ท.ป.4/2528 … เก็บ" (ใช้เป็นที่มาระยะเก็บ)
  R4 "Accounting Act, Section 10" / "Section 10 of the Accounting Act" (ฉบับอังกฤษของ R1)
  R5 "ม.10" ไม่มีชื่อ พ.ร.บ. ติดคำ "เก็บ" ในระยะใกล้ (รูปใน CLAUDE.md §H เดิม)
ไม่ฟ้อง: ม.10 เรื่องปิดบัญชี/รอบบัญชี (ถูกต้อง) · ม.11 เรื่องยื่นงบ DBD (ถูกต้อง) · ไฟล์นี้เอง ·
บรรทัดเทสต์/คอมเมนต์ที่ตั้งใจอ้างคำผิดเพื่อห้าม (มี DoesNotContain/ห้าม/ไม่ใช่ บนบรรทัดเดียวกัน)
audit row เก่าที่เก็บ LegalReference เดิมไว้ใน DB ไม่อยู่ในขอบเขต (append-only + hash chain — ห้าม migrate · กฎ M)

ใช้: python3 tools/legal_citation_check.py [--self-test]
"""
import os
import re
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCAN = [("Accounting", (".cs", ".html", ".js")), ("Accounting.Tests", (".cs",))]
FILES = ["CLAUDE.md"]
SKIP_DIRS = {"bin", "obj", ".claude", "node_modules", ".git", "lib", "vendor"}

ACT = r"พ\.ร\.บ\.(?:การ)?บัญชี"
KEEP = r"(?:เก็บ|5 ปี|retention|Retention|หลักฐาน|legal hold|legal_hold)"
RULES = [
    ("R1", re.compile(ACT + r"[^\n]{0,80}?ม\.10(?![0-9/])[^\n]*" + KEEP + r"|" + KEEP + r"[^\n]*" + ACT + r"[^\n]{0,20}?ม\.10(?![0-9/])")),
    ("R2", re.compile(r"ม\.11 ทวิ")),
    ("R3", re.compile(r"ท\.ป\.4/2528[^\n]{0,40}เก็บ")),
    ("R4", re.compile(r"Accounting Act, Section 10\b|Section 10 of the Accounting Act")),
    # R5 ม.10 ไม่มีชื่อ พ.ร.บ. แต่ติดคำ "เก็บ" (เช่น CLAUDE.md §H "**ม.10** เก็บเอกสาร") — ระยะใกล้เท่านั้น · มี ม.14 คั่น = อธิบายการแก้ ไม่ฟ้อง
    ("R5", re.compile(r"ม\.10(?![0-9/])[^\n]{0,15}?เก็บ|เก็บ(?:(?!ม\.14)[^\n]){0,25}?ม\.10(?![0-9/])")),
]
INTENT = re.compile(r"DoesNotContain|ห้าม|ไม่ใช่|forbid")
BASELINE = 0   # ratchet: ห้ามเพิ่ม (WP-06 แก้จนเหลือ 0)


def scan_text(text):
    hits = []
    for i, line in enumerate(text.split("\n"), 1):
        if INTENT.search(line):
            continue
        for code, rx in RULES:
            if rx.search(line):
                hits.append((i, code, line.strip()[:160]))
                break
    return hits


def iter_files(root):
    for top, exts in SCAN:
        base = os.path.join(root, top)
        for d, dirs, files in os.walk(base):
            dirs[:] = [x for x in dirs if x not in SKIP_DIRS]
            for f in files:
                if f.endswith(exts):
                    yield os.path.join(d, f)
    for f in FILES:
        p = os.path.join(root, f)
        if os.path.exists(p):
            yield p


def run(root):
    hits = []
    for p in iter_files(root):
        if os.path.abspath(p) == os.path.abspath(__file__):
            continue
        try:
            text = open(p, encoding="utf-8").read()
        except (UnicodeDecodeError, OSError):
            continue
        for ln, code, s in scan_text(text):
            hits.append((os.path.relpath(p, root), ln, code, s))
    return hits


def self_test():
    bad = [
        "/// ข้อมูลบัญชีไม่ถูกลบจริง (พ.ร.บ.การบัญชี ม.10 เก็บ 5 ปี)",
        "// หลักฐานประกอบรายการต้องเก็บ 5 ปี (พ.ร.บ.บัญชี ม.10 · §87/3)",
        "/// tamper-evidence ตาม พ.ร.บ.การบัญชี ม.11 ทวิ",
        "parts.Add(\"(ท.ป.4/2528 เก็บ 5 ปี)\");",
        "| Accounts | **5 years** | Accounting Act, Section 10 |",
        "- [ ] **ม.10** เก็บเอกสาร **5 ปี** (ขยายได้ถึง 7 ปี)",
    ]
    good = [
        "/// ข้อมูลบัญชีไม่ถูกลบจริง (พ.ร.บ.การบัญชี ม.14 เก็บ 5 ปี)",
        "public const string LegalReference = \"พ.ร.บ.การบัญชี 2543 ม.10 (ปิดบัญชีทุกรอบสิบสองเดือน)\";",   # ม.10 = ปิดบัญชี ถูก
        "/// ม.11 + ประกาศ DBD เรื่องวิธีการยื่นงบการเงินทางอิเล็กทรอนิกส์",                               # DBD ยื่นงบ ถูก — ห้ามแตะ
        "Assert.DoesNotContain(\"ม.10\", TrialDataRetentionPolicy.LegalReference); // พ.ร.บ.การบัญชี ม.10 เก็บ",
        "/// เกณฑ์ 1,000 บาท (ท.ป.4/2528 ข้อ 12)",
        "| Accounts | **5 years** | Accounting Act, Section 14 |",
    ]
    fail = 0
    for s in bad:
        if not scan_text(s):
            print("self-test ล้ม: ต้องฟ้อง —", s); fail = 1
    for s in good:
        if scan_text(s):
            print("self-test ล้ม: ไม่ควรฟ้อง —", s); fail = 1
    # ทั้งเรพ: ใส่บรรทัดผิดลงสำเนาไฟล์จริงแล้วต้องถูกนับ
    with tempfile.TemporaryDirectory() as t:
        os.makedirs(os.path.join(t, "Accounting", "Helpers"))
        with open(os.path.join(t, "Accounting", "Helpers", "X.cs"), "w", encoding="utf-8") as fh:
            fh.write("\n".join(good + [bad[0]]))
        if len(run(t)) != 1:
            print("self-test ล้ม: สแกนไดเรกทอรีต้องเจอ 1 จุด"); fail = 1
    print("self-test: ผ่าน" if not fail else "self-test: ล้ม")
    return fail


def main():
    if "--self-test" in sys.argv:
        sys.exit(self_test())
    hits = run(ROOT)
    if len(hits) > BASELINE:
        print(f"❌ legal_citation_check: {len(hits)} จุด (baseline {BASELINE}) — อ้างเลขมาตรา พ.ร.บ.การบัญชีผิด "
              "(ระยะเก็บ = ม.14 · ปิดบัญชี/รอบบัญชี = ม.10 · ยื่นงบ = ม.11 · ผู้ทำบัญชี = ม.19 · ไฟล์อิเล็กทรอนิกส์ = ETA 2544 ม.12)")
        for p, ln, code, s in hits:
            print(f"  {p}:{ln} [{code}] {s}")
        sys.exit(1)
    if len(hits) < BASELINE:
        print(f"ℹ️ legal_citation_check: เหลือ {len(hits)} < baseline {BASELINE} — ลด BASELINE ลงให้เท่าจริง")
        sys.exit(1)
    print(f"✅ legal_citation_check: 0 จุด")


if __name__ == "__main__":
    main()
