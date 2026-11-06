#!/usr/bin/env python3
"""ห้ามต่อ "ข้อความที่ผู้ใช้/OCR/คู่ค้าคุมได้" เข้า HTML ด้วย `${x.field}` ดิบ ๆ (CLAUDE.md #4 C · XSS ⇒ ขโมย JWT ใน localStorage)

ที่มา (รอบ 250 WP-03 · ผลตรวจ D §2.1 · §3.7 · §6.5 · §8.5 · §9.5): หน้าทะเบียนสินทรัพย์ต่อ `${a.name}` `${a.assetCode}` `${a.category}`
`${a.accountCode} - ${a.accountName}` ลง innerHTML ตรง ๆ — ชื่อสินทรัพย์มาจาก OCR/ใบผู้ขาย · หน้าเอกสารต่อ `${d.certifierName}` ·
หน้าผู้ติดต่อ `${c.email}` · ตารางส่งอีเมล `${r.documentType}` · พอร์ทัล `src="${ppQr}"` — ทั้งหมดเป็น defect class เดียวกับ
"XSS ใน line.Description/ชื่อบริษัท" ที่ CLAUDE.md #4 C บันทึกไว้แล้ว แต่ไม่มีตัวกันไม่ให้เกิดตัวที่สาม

กติกา (scope แคบตาม F4 ข้อ 3 — ไม่กวาดทั้งเรพ): ในไฟล์ที่อยู่ใน `FILES` · `${ident.field}` (หรือ `${ident.field || '...'}`) ที่ field อยู่ใน
`TEXT_FIELDS` และ**ไม่ได้อยู่ในการเรียกฟังก์ชัน** = ฟ้อง · ทางผ่าน: ห่อด้วย `esc(`/`Layout.esc(`/`escapeHtml(`/`jsArg(` (นิพจน์ไม่ใช่
member ดิบอีกต่อไป) หรือบรรทัดที่ตั้ง `.textContent =` (ไม่ใช่ HTML) หรือหมายเหตุ `esc-ok` บนบรรทัด (ต้องมีเหตุผล)

ratchet: `BASELINE` ต่อไฟล์ (รอบ 250 = 0 ทุกไฟล์) — ลดได้ ห้ามเพิ่ม
`--self-test`: ไฟล์สังเคราะห์ที่มีรูปดิบ 6 จุดต้องถูกฟ้อง · รูปที่ห่อแล้ว/textContent/esc-ok ต้องไม่ถูกฟ้อง
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
W = os.path.join("Accounting", "wwwroot")
FILES = [
    os.path.join(W, "pages", "fixed-assets.html"),
    os.path.join(W, "pages", "recurring.html"),
    os.path.join(W, "pages", "contacts.html"),
    os.path.join(W, "pages", "email-schedule.html"),
    os.path.join(W, "pages", "notifications.html"),
    os.path.join(W, "portal.html"),
    os.path.join(W, "pay-link.html"),
]
# documents.html (14k บรรทัด) ตรวจเฉพาะฟิลด์ที่ผลตรวจ D ยกมา — ฟิลด์อื่นยังมีจุดดิบอีกมาก (ratchet แยกในอนาคต)
DOCS_FILE = os.path.join(W, "pages", "documents.html")
DOCS_FIELDS = {"certifierName", "certifierPosition", "witnessName", "witnessPosition"}

TEXT_FIELDS = {
    "name", "code", "assetCode", "category", "location", "serialNumber", "accountCode", "accountName",
    "email", "documentType", "documentNumber", "description", "contactName", "lessorName", "projectNumber",
    "certifierName", "certifierPosition", "witnessName", "witnessPosition", "label", "reasoning", "notes",
    "subject", "toEmail", "bankName", "bankAccountName", "bankAccountNumber", "promptPayId", "key",
}

BASELINE = {f: 0 for f in FILES + [DOCS_FILE]}

PAT = re.compile(r"\$\{\s*([A-Za-z_$][\w$]*)\.([A-Za-z_$][\w$]*)\s*(?:\|\|\s*('[^']*'|\"[^\"]*\"|-)\s*)?\}")


def scan_text(text, fields):
    hits = []
    for no, line in enumerate(text.splitlines(), 1):
        if "esc-ok" in line or re.search(r"\.(?:textContent|title)\s*=", line):
            continue
        for m in PAT.finditer(line):
            if m.group(2) in fields:
                hits.append((no, m.group(0), line.strip()[:140]))
    return hits


def scan_repo():
    total_bad = False
    report = []
    for rel in FILES + [DOCS_FILE]:
        path = os.path.join(ROOT, rel)
        if not os.path.exists(path):
            report.append(f"⚠️ ไม่พบไฟล์ {rel} (ย้ายแล้ว? แก้ FILES)")
            total_bad = True
            continue
        text = open(path, encoding="utf-8").read()
        hits = scan_text(text, DOCS_FIELDS if rel == DOCS_FILE else TEXT_FIELDS)
        if len(hits) > BASELINE[rel]:
            total_bad = True
            report.append(f"❌ {rel}: {len(hits)} จุด (baseline {BASELINE[rel]}) — ห่อด้วย Layout.esc()/esc()/escapeHtml() ก่อนต่อเข้า HTML")
            for no, frag, line in hits[:20]:
                report.append(f"   :{no}  {frag}   ← {line}")
        elif len(hits) < BASELINE[rel]:
            report.append(f"ℹ️ {rel}: {len(hits)} < baseline {BASELINE[rel]} — ลด BASELINE ลง (ratchet)")
    return total_bad, report


SELF_BAD = """
tbody.innerHTML = items.map(a => `<td>${a.name}</td><td>${a.assetCode}</td>`);
host.innerHTML = `<div>${c.email || '-'}</div>`;
opts = `<option value="${x.id}">${x.accountCode} - ${x.accountName}</option>`;
box.innerHTML = `<b>${r.documentType}</b>`;
"""
SELF_OK = """
tbody.innerHTML = items.map(a => `<td>${Layout.esc(a.name)}</td><td>${esc(a.assetCode)}</td>`);
host.innerHTML = `<div>${Layout.esc(c.email || '-')}</div>`;
el.textContent = `${a.assetCode} - ${a.name}`;
x = `${a.name}`; // esc-ok: ใช้เป็น title ของ confirm (ไม่ใช่ HTML)
y = `${a.id} ${Layout.money(a.purchaseCost)}`;
"""


def self_test():
    bad = scan_text(SELF_BAD, TEXT_FIELDS)
    ok = scan_text(SELF_OK, TEXT_FIELDS)
    fails = []
    if len(bad) != 6:
        fails.append(f"รูปดิบต้องถูกฟ้อง 6 จุด ได้ {len(bad)}: {[b[1] for b in bad]}")
    if ok:
        fails.append(f"รูปที่ห่อแล้ว/textContent/esc-ok ต้องไม่ถูกฟ้อง แต่ได้ {[o[1] for o in ok]}")
    # ใส่บั๊กกลับลงสำเนาไฟล์จริง: ถอด Layout.esc ของชื่อสินทรัพย์ในตาราง ⇒ ต้องฟ้อง
    fa = os.path.join(ROOT, FILES[0])
    if os.path.exists(fa):
        t = open(fa, encoding="utf-8").read()
        injected = t.replace("${Layout.esc(a.name)}${a.isUnderAcquisition", "${a.name}${a.isUnderAcquisition", 1)
        if injected == t:
            fails.append("หาจุดฉีดใน fixed-assets.html ไม่เจอ (โค้ดเปลี่ยน — แก้ self-test)")
        elif len(scan_text(injected, TEXT_FIELDS)) <= len(scan_text(t, TEXT_FIELDS)):
            fails.append("ฉีด ${a.name} ดิบลงสำเนา fixed-assets.html แล้วไม่ถูกฟ้อง")
    if fails:
        print("❌ self-test html_interpolation_escape_check:\n  " + "\n  ".join(fails))
        return 1
    print("✅ self-test html_interpolation_escape_check ผ่าน (ดิบ 6 จุดถูกฟ้อง · ห่อแล้ว 0 · ฉีดลงไฟล์จริงถูกจับ)")
    return 0


if __name__ == "__main__":
    if "--self-test" in sys.argv:
        sys.exit(self_test())
    bad, report = scan_repo()
    for r in report:
        print(r)
    if bad:
        sys.exit(1)
    print(f"✅ html_interpolation_escape_check: {len(FILES) + 1} ไฟล์ ไม่มี ${{x.field}} ดิบเกิน baseline")
    st = self_test()
    sys.exit(st)
