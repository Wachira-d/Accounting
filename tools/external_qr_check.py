#!/usr/bin/env python3
"""ห้ามหน้าเว็บสร้าง QR ผ่านบริการภายนอก (ส่งเลขพร้อมเพย์/ยอดเงิน/ลิงก์ของลูกค้าไปบุคคลที่สาม)

ที่มา (รอบ 250 WP-03 · A G-01 · D §9.1 · คำตัดสิน §2 #15): หน้าร้าน (storefront) เลิกใช้ `api.qrserver.com` ไปแล้วในรอบ 209
แต่พอร์ทัลลูกค้ายัง `<img src="https://api.qrserver.com/...?data=<payload พร้อมเพย์+ยอด>">` อยู่อีก 1 จุด — checker ตัวเดิม
(`csp_external_ref_check`) ดูแค่ `<script src>`/`<link href>` จึงไม่จับ `<img src>` ⇒ ตกหล่นรอดมา 40 รอบ
QR ทุกจุดต้องสร้างที่เซิร์ฟเวอร์ (`PromptPayService` / `PaymentQrService` → data URI)

กติกา: ทุกไฟล์ใน `Accounting/wwwroot/**` (.html/.js) ห้ามมีชื่อโฮสต์บริการสร้าง QR ในรายการ `HOSTS` (รวมในคอมเมนต์ —
ให้ `grep -rn qrserver Accounting/wwwroot` = 0 ตาม Definition of done ของ WP-03)
`--self-test`: ไฟล์สังเคราะห์ที่มีโฮสต์แต่ละตัวต้องถูกฟ้อง · ข้อความที่ไม่ใช่โฮสต์ต้องไม่ถูกฟ้อง · ฉีดลงสำเนา portal.html จริงแล้วต้องถูกจับ
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WWWROOT = os.path.join(ROOT, "Accounting", "wwwroot")

HOSTS = [
    r"qrserver\.com", r"api\.qrserver", r"chart\.googleapis\.com/chart\?[^\"'`]*cht=qr", r"quickchart\.io/qr",
    r"api\.qrcode-monkey\.com", r"qrcode\.tec-it\.com", r"chart\.apis\.google\.com/chart\?[^\"'`]*cht=qr", r"goqr\.me",
]
PAT = re.compile("|".join(HOSTS), re.I)


def scan_text(text):
    return [(no, line.strip()[:140]) for no, line in enumerate(text.splitlines(), 1) if PAT.search(line)]


def scan_repo():
    hits = []
    for dp, dn, fn in os.walk(WWWROOT):
        dn[:] = [d for d in dn if d not in ("node_modules", "lib", "vendor")]
        for f in fn:
            if not f.endswith((".html", ".js")):
                continue
            p = os.path.join(dp, f)
            try:
                t = open(p, encoding="utf-8", errors="ignore").read()
            except OSError:
                continue
            for no, line in scan_text(t):
                hits.append(f"{os.path.relpath(p, ROOT)}:{no}  {line}")
    return hits


def self_test():
    fails = []
    bad = [
        '<img src="https://api.qrserver.com/v1/create-qr-code/?data=000201">',
        "const u = `https://chart.googleapis.com/chart?chs=200x200&cht=qr&chl=${p}`;",
        "img.src = 'https://quickchart.io/qr?text=' + payload;",
    ]
    for b in bad:
        if not scan_text(b):
            fails.append(f"ต้องถูกฟ้อง: {b}")
    ok = ['<img src="data:image/png;base64,iVBOR">', "// QR สร้างที่เซิร์ฟเวอร์ (PromptPayService)", "https://www.google.com/maps"]
    for o in ok:
        if scan_text(o):
            fails.append(f"ต้องไม่ถูกฟ้อง: {o}")
    portal = os.path.join(WWWROOT, "portal.html")
    if os.path.exists(portal):
        t = open(portal, encoding="utf-8").read()
        injected = t.replace("const ppQr = dataQr;", "const ppQr = 'https://api.qrserver.com/v1/create-qr-code/?data=' + ppId;", 1)
        if injected == t:
            fails.append("หาจุดฉีดใน portal.html ไม่เจอ (โค้ดเปลี่ยน — แก้ self-test)")
        elif not scan_text(injected):
            fails.append("ฉีด api.qrserver.com ลงสำเนา portal.html แล้วไม่ถูกฟ้อง")
    if fails:
        print("❌ self-test external_qr_check:\n  " + "\n  ".join(fails))
        return 1
    print("✅ self-test external_qr_check ผ่าน (โฮสต์ QR ภายนอก 3 แบบถูกฟ้อง · data URI/คอมเมนต์ไม่ถูกฟ้อง · ฉีดลง portal.html ถูกจับ)")
    return 0


if __name__ == "__main__":
    if "--self-test" in sys.argv:
        sys.exit(self_test())
    hits = scan_repo()
    if hits:
        print("❌ external_qr_check: หน้าเว็บสร้าง QR ผ่านบริการภายนอก (ส่งข้อมูลการเงินของลูกค้าไปบุคคลที่สาม) — ใช้ data URI จากเซิร์ฟเวอร์ (PromptPayService/PaymentQrService) แทน")
        for h in hits[:30]:
            print("   " + h)
        sys.exit(1)
    print("✅ external_qr_check: ไม่มีบริการสร้าง QR ภายนอกใน wwwroot")
    sys.exit(self_test())
