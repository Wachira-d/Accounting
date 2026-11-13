#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""slip_secret_check.py — ค่าจริงจากสลิปโอนเงินของเจ้าของห้ามอยู่ในเรพ (รอบ 264 P2 · ฝ่ายค้าน H1)

ที่มา (บั๊กจริง): e6caad8c ใส่เลขที่รายการ K+ **จริง** (ยังไม่ redact) ไว้ใน doc-comment ของ `Helpers/ThaiSlipQr.cs` ทั้งที่ชุดทดสอบ
`Accounting.Tests/SlipCorpusRound264.cs` แทนเลขอ้างอิงไว้แล้ว — ใครก็ประกอบ QR ตรวจสลิปของจริงกลับได้ (โครงสร้าง + CRC อยู่ในโค้ดเดียวกัน)
แล้วส่งให้บริการตรวจสลิปของธนาคาร ⇒ ได้ชื่อผู้โอน/ผู้รับ + ยอด (PDPA ม.27)

ตรวจทุกไฟล์ข้อความที่ git ติดตาม:
1. **แฮช** — โทเค็นทุกตัว (ตัวอักษร/ตัวเลขละติน ≥ 8 · อักษรไทย ≥ 5) ตัดให้ตัวพิมพ์ใหญ่แล้ว SHA-256 ตรงกับ `tools/slip_secret_hashes.txt`
   (เลขอ้างอิงจริง · QR payload จริง · Ref ของบิล · MID/TID · ชื่อ) ⇒ ฟ้อง — ไฟล์แฮชเก็บแค่แฮช (ค่าจริงไม่เข้าเรพ)
2. **รูปร่าง** — เลขที่รายการแบบ K+ (`0` + 11 หลัก + อักษร 3 ตัว + เลข 5 หลัก · 20 ตัว) หรือ QR ตรวจสลิป (`00LL000600000101…`)
   ที่เลขอ้างอิง**ไม่ใช่จุดตรึงของตัวแทนค่า** `redact()` (ตัวเดียวกับที่สร้างชุดทดสอบ) ⇒ ฟ้อง (ค่าจริงใหม่ที่ยังไม่อยู่ในรายการแฮช)

เลขบัญชีจริงเต็มหลัก: เจ้าของส่งมาแต่แบบที่ธนาคารปิดแล้ว (≤ 4 หลัก) ⇒ ไม่มีค่าเต็มให้ตรวจ — ชั้นโค้ดกันไว้แล้วที่ `OcrSlipText.ClassifyMask`
(เลขที่ไม่มีตัวปิดไม่ถูกเก็บ · เก็บไม่เกิน 4 หลัก) + เทสต์ `OcrSlipTextPartiesTests`

รันเปล่า = ตรวจทั้งเรพ (+ negative test ในตัวทุกครั้ง) · `--self-test` = negative test อย่างเดียว
"""
import hashlib
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HASHES = os.path.join(ROOT, "tools", "slip_secret_hashes.txt")
TOKEN_RE = re.compile(r"[A-Za-z0-9]{8,}|[฀-๿]{5,}")
KPLUS_REF_RE = re.compile(r"(?<![A-Za-z0-9])0\d{11}[A-Z]{3}\d{5}(?![A-Za-z0-9])")
SLIP_QR_RE = re.compile(r"(?<![0-9])00\d\d000600000101\d{2}[0-9A-Za-z]{6,}")
TEXT_EXT = (".cs", ".md", ".py", ".js", ".html", ".json", ".txt", ".sh", ".yml", ".yaml", ".csproj", ".css", ".sql", ".xml")
SKIP = {"tools/slip_secret_hashes.txt", "tools/slip_secret_check.py"}   # ตัวเองมีค่าปลอมของ negative test


def load_hashes(path=HASHES):
    if not os.path.isfile(path):
        return set()
    return {l.strip() for l in open(path, encoding="utf-8") if re.fullmatch(r"[0-9a-f]{64}", l.strip())}


def redact(r):
    """ตัวแทนค่าเดียวกับที่ใช้สร้างชุดทดสอบ: คงหน้า 3 ท้าย 2 · กลางแทนตามตำแหน่งด้วยชนิดเดิม (เลข/ตัวเล็ก/ตัวใหญ่)"""
    d, lo, up = "13579024680", "abcdefghij", "QWERTYUIOP"
    out = list(r)
    for i in range(3, len(r) - 2):
        c = r[i]
        out[i] = d[i % len(d)] if c.isdigit() else lo[i % len(lo)] if c.islower() else up[i % len(up)] if c.isupper() else c
    return "".join(out)


def is_redacted(token):
    """ค่าที่แทนแล้วเป็นจุดตรึงของ redact() (แทนซ้ำได้ค่าเดิม) — ค่าจริงแทบไม่มีทางเป็น"""
    return redact(token) == token


def qr_ref(payload):
    """เลขอ้างอิง (แท็ก 02 ในแท็ก 00) ของ QR ตรวจสลิป · รูปไม่ตรง = None"""
    try:
        n = int(payload[2:4]); inner = payload[4:4 + n]; i = 0
        while i + 4 <= len(inner):
            t, l = inner[i:i + 2], int(inner[i + 2:i + 4])
            if t == "02":
                return inner[i + 4:i + 4 + l]
            i += 4 + l
    except ValueError:
        return None
    return None


def h(token):
    return hashlib.sha256(token.upper().encode("utf-8")).hexdigest()


def scan_text(rel, text, hashes):
    """คืนรายการปัญหา (rel, บรรทัด, ชนิด) — ไม่พิมพ์ค่าที่เจอ (ค่าจริงต้องไม่ไปอยู่ใน log ของ CI)"""
    out = []
    for no, line in enumerate(text.split("\n"), 1):
        for t in TOKEN_RE.findall(line):
            if h(t) in hashes:
                out.append((rel, no, "ค่าจริงจากสลิป (ตรงแฮช)"))
        for m in KPLUS_REF_RE.finditer(line):
            if not is_redacted(m.group(0)):
                out.append((rel, no, "รูปเลขที่รายการ K+ 20 ตัวที่ยังไม่ได้แทนค่า"))
        for m in SLIP_QR_RE.finditer(line):
            ref = qr_ref(m.group(0))
            if ref is None or not is_redacted(ref):
                out.append((rel, no, "รูป QR ตรวจสลิปที่เลขอ้างอิงยังไม่ได้แทนค่า"))
    return out


def tracked_files():
    try:
        r = subprocess.run(["git", "-C", ROOT, "ls-files"], capture_output=True, text=True, check=True)
        return [f for f in r.stdout.split("\n") if f.endswith(TEXT_EXT) and f not in SKIP]
    except (OSError, subprocess.CalledProcessError):
        return []


def collect(hashes):
    bad = []
    for rel in tracked_files():
        p = os.path.join(ROOT, rel)
        try:
            text = open(p, encoding="utf-8").read()
        except (OSError, UnicodeDecodeError):
            continue
        bad += scan_text(rel, text, hashes)
    return bad


def self_test(verbose=False):
    ok = True
    fake = "Zq7Kx9Pm2Wn4"   # ค่าปลอม — ใส่แฮชในชุดชั่วคราว
    hs = {h(fake)}
    if not scan_text("x.cs", "/// ref " + fake.lower() + " ท้าย", hs):
        print("❌ self-test: ค่าที่ตรงแฮช (ต่างตัวพิมพ์) ไม่ถูกฟ้อง"); ok = False
    if scan_text("x.cs", "/// ref Zq7Kx9Pm2Wn5", hs):
        print("❌ self-test: ค่าที่ไม่ตรงแฮชถูกฟ้องผิด"); ok = False
    if not scan_text("x.cs", '"012345678901ABC12345"', set()):
        print("❌ self-test: รูปเลขที่รายการ K+ ที่ไม่ได้แทนไม่ถูกฟ้อง"); ok = False
    if scan_text("x.cs", '"016790246801ERT90209"', set()):
        print("❌ self-test: ค่าที่ redact แล้วถูกฟ้องผิด"); ok = False
    if scan_text("x.cs", "0038000600000101030060217A31790g468abc5efe5102TH91045885", set()):
        print("❌ self-test: QR ที่ redact แล้ว (ตัวเล็กปน) ถูกฟ้องผิด"); ok = False
    if not scan_text("x.cs", "0041000600000101030040220012345678901ABC123455102TH9104ABCD", set()):
        print("❌ self-test: QR ตรวจสลิปที่ไม่ได้แทนไม่ถูกฟ้อง"); ok = False
    if scan_text("x.cs", "0041000600000101030040220016790246801ERT902095102TH91049A55", set()):
        print("❌ self-test: QR ที่ redact แล้วถูกฟ้องผิด"); ok = False
    thai = "สมมติชื่อปลอม"
    if not scan_text("x.md", "ผู้โอน " + thai, {h(thai)}):
        print("❌ self-test: ชื่อไทยที่ตรงแฮชไม่ถูกฟ้อง"); ok = False
    if len(load_hashes()) < 20:
        print("❌ self-test: tools/slip_secret_hashes.txt หาย/ว่าง (ด่านไม่มีของให้ตรวจ)"); ok = False
    if ok and verbose:
        print("✅ self-test: ตรงแฮช/รูป K+/รูป QR ถูกฟ้อง · ค่าที่ redact แล้ว/ไม่ตรงแฮช ไม่ฟ้อง")
    return ok


def main():
    if "--self-test" in sys.argv:
        return 0 if self_test(verbose=True) else 1
    hashes = load_hashes()
    bad = collect(hashes)
    for rel, no, kind in bad:
        print(f"{rel}:{no}: {kind} — แทนด้วยค่าจากชุดที่ redact แล้ว (Accounting.Tests/SlipCorpusRound264.cs) หรือ <ref> · ห้ามพิมพ์ค่าจริงลง log")
    print(f"ตรวจค่าจริงจากสลิป: แฮช {len(hashes)} ค่า · พบ {len(bad)} จุด")
    st = self_test()
    return 1 if (bad or not st) else 0


if __name__ == "__main__":
    sys.exit(main())
