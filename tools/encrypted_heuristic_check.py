#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ห้ามมีสำเนาที่สองของ "เดาจากรูปว่าเข้ารหัสแล้ว" นอก Helpers/EncryptionHelper.cs — รอบ 231 (บั๊ก production)

ที่มา: `EncryptionHelper.IsEncrypted` รุ่นก่อนรอบ 231 เดาจาก "ยาว ≥ 40 + เป็น base64 + ≥ 28 ไบต์" ⇒ Channel Access Token ของ LINE
(base64 ยาว ~170 ตัว) ถูกเข้าใจว่าเป็น ciphertext ⇒ `SecretProtector.Protect` "ไม่เข้ารหัสซ้ำ" เก็บค่าดิบ ⇒ ตอนอ่านถอดค่าดิบไม่ออก ⇒
`IsUsable=false` ⇒ ระบบทิ้งค่าตั้ง LINE OA กลางทั้งชุด (webhook 401) + หน้าจอฟ้อง "คีย์เข้ารหัสของระบบถูกเปลี่ยน" ทั้งที่คีย์ไม่เคยเปลี่ยน ·
แก้แล้ว: ciphertext มีคำนำหน้า `enc1:` · `IsEncrypted` = คำนำหน้าเท่านั้น · การเดารูปเหลือไว้เฉพาะฝั่งอ่านค่าเก่าใน OWNER file ตัวเดียว
(`LooksLikeLegacyCiphertext`) — checker นี้กันไม่ให้ใครเขียนตัวเดาใหม่ขึ้นมาอีก (กฎ F2 ข้อ 4 "ตัวตั้งตัวเดียว")

กติกา (สแกน Accounting/**/*.cs ยกเว้น OWNER · ตัดคอมเมนต์/สตริงก่อน):
  R1  นิยามเมธอดชื่อรูป Is/Looks/Seems/Appears…Encrypted/Cipher/Ciphertext(  นอก OWNER ⇒ ฟ้อง (ตัวเดาตัวที่สอง)
  R2  `FromBase64String(` / `TryFromBase64String(` ที่อยู่ห่างจากการเทียบ `.Length` กับตัวเลข ≤ 6 บรรทัดในเมธอดเดียวกัน ⇒ ฟ้อง
      (รูปของ heuristic เดิม: "ยาวพอ + เป็น base64 = ciphertext")
  R3  ไฟล์ที่แตะชั้นความลับ (อ้าง `EncryptionHelper.` หรือ `ISecretProtector`) เรียก `FromBase64String(` เอง ⇒ ฟ้อง
      (การตัดสินว่า "เป็น ciphertext ไหม" เป็นของ EncryptionHelper เท่านั้น — ผู้เรียกถาม IsEncrypted/Inspect)

ratchet: `tools/encrypted_heuristic_baseline.txt` = จุดเดิมที่ไม่ใช่ชั้นความลับ (ตรวจ hash รหัสผ่าน · ตรวจรูปลายเซ็น) ระบุเป็น `ไฟล์::เมธอด`
(ไม่ผูกเลขบรรทัด) — เพิ่มได้เฉพาะเมื่ออธิบายได้ว่าไม่ใช่ตัวตัดสิน "เข้ารหัสแล้ว" · เมธอดใหม่ในไฟล์เดิมไม่ได้รับยกเว้น

ใช้: python3 tools/encrypted_heuristic_check.py [--self-test]
negative test: --self-test (ฉีด heuristic เดิมกลับ 3 รูป ต้องจับได้ทุกรูป · ตัวถอดไฟล์แนบ/OWNER/คอมเมนต์ ต้องไม่ถูกฟ้อง · baseline ยกเว้นเฉพาะเมธอดที่ระบุ)
"""
import os
import re
import shutil
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OWNER = os.path.join("Accounting", "Helpers", "EncryptionHelper.cs")
BASELINE = os.path.join(ROOT, "tools", "encrypted_heuristic_baseline.txt")

DEF_RE = re.compile(r"\b(?:static\s+|private\s+|public\s+|internal\s+)*bool\s+(?:Is|Looks|Seems|Appears)\w*(?:Encrypted|Ciphertext|Cipher)\w*\s*\(")
B64_RE = re.compile(r"\b(?:Convert\.)?(?:Try)?FromBase64String\s*\(")
LEN_RE = re.compile(r"\.Length\s*(?:>=|<=|<|>)\s*\d+")
SECRET_LAYER_RE = re.compile(r"\bEncryptionHelper\.|\bISecretProtector\b")
METHOD_HEAD_RE = re.compile(r"^\s*(?:\[[^\]]*\]\s*)*(?:public|private|internal|protected)[\w\s<>\[\],?]*?\b(\w+)\s*\(")


def enclosing_method(lines, i):
    for j in range(i - 1, -1, -1):
        m = METHOD_HEAD_RE.match(lines[j])
        if m:
            return m.group(1)
    return "?"


def load_baseline(path=BASELINE):
    if not os.path.exists(path):
        return set()
    with open(path, encoding="utf-8") as fh:
        return {ln.strip() for ln in fh if ln.strip() and not ln.startswith("#")}


def strip(text):
    text = re.sub(r"/\*.*?\*/", lambda m: re.sub(r"[^\n]", " ", m.group(0)), text, flags=re.S)
    text = re.sub(r"(?<!:)//[^\n]*", lambda m: " " * len(m.group(0)), text)
    text = re.sub(r'@"(?:[^"]|"")*"', lambda m: re.sub(r"[^\n]", " ", m.group(0)), text)
    text = re.sub(r'\$?"(?:\\.|[^"\\\n])*"', lambda m: " " * len(m.group(0)), text)
    return text


def scan_file(rel, text):
    hits = []
    lines = strip(text).split("\n")
    for i, line in enumerate(lines, 1):
        if DEF_RE.search(line):
            hits.append((rel, i, "R1 ตัวเดา 'เข้ารหัสแล้ว' ตัวที่สอง — ใช้ EncryptionHelper.IsEncrypted (คำนำหน้า enc1) แทน", enclosing_method(lines, i)))
    touches_secret_layer = SECRET_LAYER_RE.search(" ".join(lines)) is not None
    for i, line in enumerate(lines, 1):
        if not B64_RE.search(line):
            continue
        if touches_secret_layer:
            hits.append((rel, i, "R3 ไฟล์ที่แตะชั้นความลับถอด base64 เอง — การตัดสิน 'เป็น ciphertext ไหม' เป็นของ EncryptionHelper", enclosing_method(lines, i)))
            continue
        lo, hi = max(1, i - 6), min(len(lines), i + 6)
        window = lines[lo - 1:hi]
        # ขอบเมธอด: ถ้ามีหัวเมธอดใหม่ระหว่างทาง ไม่นับข้ามเมธอด
        for j, w in enumerate(window, lo):
            if not LEN_RE.search(w):
                continue
            seg = lines[min(i, j) - 1:max(i, j)]
            if any(METHOD_HEAD_RE.match(x) for x in seg[1:]):
                continue
            hits.append((rel, i, f"R2 base64 + เทียบความยาว (บรรทัด {j}) = heuristic 'ดูเหมือน ciphertext' — เป็นของ EncryptionHelper.LooksLikeLegacyCiphertext เท่านั้น", enclosing_method(lines, i)))
            break
    return hits


def scan(root, baseline=frozenset()):
    hits = []
    base = os.path.join(root, "Accounting")
    for d, dirs, files in os.walk(base):
        dirs[:] = [x for x in dirs if x not in ("bin", "obj", "wwwroot")]
        for f in files:
            if not f.endswith(".cs"):
                continue
            path = os.path.join(d, f)
            rel = os.path.relpath(path, root)
            if rel == OWNER:
                continue
            with open(path, encoding="utf-8", errors="ignore") as fh:
                for h in scan_file(rel, fh.read()):
                    if f"{rel.replace(os.sep, '/')}::{h[3]}" not in baseline:
                        hits.append(h)
    return hits


def self_test():
    fail = 0
    tmp = tempfile.mkdtemp(prefix="enc_heur_")
    try:
        def put(rel, body):
            p = os.path.join(tmp, rel)
            os.makedirs(os.path.dirname(p), exist_ok=True)
            with open(p, "w", encoding="utf-8") as fh:
                fh.write(body)

        # OWNER — heuristic เดิมอยู่ที่นี่ได้
        put(OWNER, "class EncryptionHelper {\n public static bool IsEncrypted(string v){ var b = Convert.FromBase64String(v); return b.Length >= 28; }\n}\n")
        # R1: ตัวเดาตัวที่สอง
        put("Accounting/Services/X1.cs", "class X1 {\n private static bool LooksEncrypted(string v) => v.Length > 40;\n}\n")
        # R2: base64 + เทียบความยาว ในเมธอดเดียว (รูปของ heuristic เดิม)
        put("Accounting/Services/X2.cs", "class X2 {\n public bool Check(string v){\n if (v.Length < 40) return false;\n try { var b = Convert.FromBase64String(v); return b.Length >= 28; } catch { return false; }\n }\n}\n")
        # R3: ไฟล์ที่ถือ ISecretProtector ถอด base64 เอง
        put("Accounting/Controllers/X3.cs", "class X3 { private readonly ISecretProtector _s;\n public void A(string v){ var b = Convert.FromBase64String(v); }\n}\n")
        # ต้องไม่ฟ้อง: ตัวถอดไฟล์แนบธรรมดา (ไม่มีเทียบความยาว · ไม่แตะชั้นความลับ)
        put("Accounting/Services/Ok1.cs", "class Ok1 {\n public byte[] A(string v){ return Convert.FromBase64String(v); }\n}\n")
        # ต้องไม่ฟ้อง: เทียบความยาวอยู่คนละเมธอด
        put("Accounting/Services/Ok2.cs", "class Ok2 {\n public bool A(string v){ return v.Length > 40; }\n public byte[] B(string v){ return Convert.FromBase64String(v); }\n}\n")
        # ต้องไม่ฟ้อง: อยู่ในคอมเมนต์/สตริง
        put("Accounting/Services/Ok3.cs", "class Ok3 {\n // private static bool IsEncrypted(string v) => Convert.FromBase64String(v).Length >= 28;\n string s = \"Convert.FromBase64String(x).Length >= 40\";\n}\n")
        # baseline: ยกเว้นเฉพาะเมธอด Legacy ใน Ok4 — เมธอด Fresh ในไฟล์เดียวกันต้องยังถูกฟ้อง
        put("Accounting/Services/Ok4.cs", "class Ok4 {\n private static bool Legacy(string v){ var b = Convert.FromBase64String(v); return b.Length > 17; }\n private static bool Fresh(string v){ var b = Convert.FromBase64String(v); return b.Length > 28; }\n}\n")
        hits = scan(tmp, frozenset({"Accounting/Services/Ok4.cs::Legacy"}))
        got = {(h[0].replace(os.sep, "/"), h[2][:2]) for h in hits}
        ok4 = [h for h in hits if h[0].replace(os.sep, "/") == "Accounting/Services/Ok4.cs"]
        if [h[3] for h in ok4] != ["Fresh"]:
            print(f"self-test ล้ม: baseline ต้องยกเว้นเฉพาะ Legacy — ได้ {[h[3] for h in ok4]}"); fail = 1
        for want in [("Accounting/Services/X1.cs", "R1"), ("Accounting/Services/X2.cs", "R2"), ("Accounting/Controllers/X3.cs", "R3")]:
            if want not in got:
                print(f"self-test ล้ม: ต้องฟ้อง — {want}"); fail = 1
        for bad in ["Accounting/Services/Ok1.cs", "Accounting/Services/Ok2.cs", "Accounting/Services/Ok3.cs", OWNER.replace(os.sep, "/")]:
            if any(f == bad for f, _ in got):
                print(f"self-test ล้ม: ต้องไม่ฟ้อง — {bad}"); fail = 1
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print("✅ self-test encrypted_heuristic_check ผ่าน" if not fail else "❌ self-test ล้ม")
    return fail


def main():
    if "--self-test" in sys.argv:
        return self_test()
    hits = scan(ROOT, frozenset(load_baseline()))
    if hits:
        print(f"❌ encrypted_heuristic_check: พบ heuristic 'เดาว่าเข้ารหัสแล้ว' นอก {OWNER} {len(hits)} จุด (ไม่อยู่ใน baseline)")
        for rel, ln, why, method in hits:
            print(f"  {rel}:{ln} ({method}): {why}")
        return 1
    print(f"✅ encrypted_heuristic_check: ไม่มีตัวเดา 'เข้ารหัสแล้ว' นอก {OWNER}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
