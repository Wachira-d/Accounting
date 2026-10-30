#!/usr/bin/env python3
"""ทะเบียนช่องความลับ (SecretReprotectPass) ต้องครอบทุกช่องที่ถูกเขียนแบบเข้ารหัส — ฝ่ายค้านรอบ 219 รอบ 2 (ข้อ 6)

ที่มา: B2 เปลี่ยนแหล่งคีย์ ⇒ ค่าที่เข้ารหัสด้วยคีย์เดิมต้องถูก SecretReprotectJob ย้ายไปคีย์ปัจจุบัน — ช่องที่ไม่อยู่ในทะเบียน
จะค้างบนคีย์เดิมตลอดกาล (ถอดได้เพราะคีย์เดิมยังอยู่ในรายการลอง แต่วันที่ถอดคีย์เดิมออก = ความลับหายเงียบ) ·
รอบแรกล็อกทะเบียนด้วย required_call_site_check แค่ "มีชื่อตาราง" ⇒ เพิ่มช่องใหม่ที่เขียนผ่าน Protect โดยไม่ลงทะเบียนก็ยังเขียว
(และพบจริงแล้ว: SitePaymentGateway.ApiKeyEncrypted/SecretKeyEncrypted ไม่อยู่ในทะเบียน)

ตรวจ:
  1. ทุกจุดใน Accounting/**/*.cs ที่เขียนค่าจาก `<secrets>.Protect(` หรือ `EncryptionHelper.Encrypt(` ลงช่อง (`X = ...`) ⇒ X ต้องอยู่ในทะเบียน
     (`Col<T>(r => r.X)` ใน Services/Background/SecretReprotectJob.cs)
  2. ส่งผ่าน delegate (เช่น `setSecret(_secrets.Protect(...))`) ⇒ ต้องอยู่ใน INDIRECT พร้อมช่องปลายทาง และช่องเหล่านั้นต้องมี lambda
     `v => s.X = v` อยู่ในไฟล์จริง (กันตารางค้าง)
  3. ทะเบียนห้ามมีช่องที่ไม่มีผู้เขียน เว้นแต่อยู่ใน READ_ONLY_REGISTERED (ช่องที่ถูกถอดแต่ไม่มีผู้เขียนในเรพแล้ว — ค่าเดิมยังต้องย้าย)

--self-test: ฉีดผู้เขียนช่องใหม่/ delegate ที่ไม่รู้จัก/ ช่องทะเบียนค้าง ลงสำเนาเรพแล้วต้องฟ้อง · ของจริงต้องผ่าน
"""
import os
import re
import shutil
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REGISTRY = os.path.join("Accounting", "Services", "Background", "SecretReprotectJob.cs")
# ผู้ถือคีย์เอง (ไม่ใช่ช่องเก็บความลับของธุรกิจ)
OWNER_FILES = {
    os.path.join("Accounting", "Helpers", "SecretProtector.cs"),
    os.path.join("Accounting", "Helpers", "EncryptedColumnConverter.cs"),
    os.path.join("Accounting", "Helpers", "EncryptionHelper.cs"),
    REGISTRY,
}
# delegate ที่รับค่าเข้ารหัสแล้วไปตั้งช่องจริง: (ไฟล์, ชื่อ delegate) → ช่องปลายทาง
INDIRECT = {
    (os.path.join("Accounting", "Controllers", "AdminController.cs"), "setSecret"):
        ["GoogleClientSecret", "FacebookAppSecret", "LineLoginChannelSecret"],
}
# อยู่ในทะเบียนได้แม้ไม่มีผู้เขียนในเรพ — ถอดด้วย SecretProtector (CmsWebhookController) · ค่าเดิมในฐานต้องย้ายคีย์
READ_ONLY_REGISTERED = {"WebhookSecret"}

CALL = re.compile(r"\b(?:_secrets|secrets|_secretProtector|_secretsProtector)\.Protect\(|\bEncryptionHelper\.Encrypt\(")
ASSIGN = re.compile(r"(\w+)\s*=(?![=>])")
INVOKER = re.compile(r"(\w+)\(\s*$")


def strip_comments(t):
    t = re.sub(r"/\*.*?\*/", lambda m: " " * len(m.group(0)), t, flags=re.S)
    return re.sub(r"(?<!:)//[^\n]*", lambda m: " " * len(m.group(0)), t)


def writers(root):
    found, errs = {}, []
    base = os.path.join(root, "Accounting")
    for d, dirs, files in os.walk(base):
        dirs[:] = [x for x in dirs if x not in ("bin", "obj")]
        for f in files:
            if not f.endswith(".cs"):
                continue
            path = os.path.join(d, f)
            rel = os.path.relpath(path, root)
            if rel in OWNER_FILES:
                continue
            text = strip_comments(open(path, encoding="utf-8").read())
            for m in CALL.finditer(text):
                # ประโยคปัจจุบัน = หลัง ; หรือ { ตัวล่าสุด (object initializer เริ่มที่ {)
                start = max(text.rfind(";", 0, m.start()), text.rfind("{", 0, m.start())) + 1
                chunk = text[start:m.start()]
                line = text.count("\n", 0, m.start()) + 1
                inv = INVOKER.search(chunk)
                if inv and inv.group(1)[:1].islower() and inv.group(1) not in ("if", "while", "return"):
                    key = (rel, inv.group(1))
                    if key not in INDIRECT:
                        errs.append(f"{rel}:{line}: เขียนค่าเข้ารหัสผ่าน delegate `{inv.group(1)}(` — ระบุช่องปลายทางใน INDIRECT ของ checker นี้")
                        continue
                    for col in INDIRECT[key]:
                        if not re.search(r"=>\s*\w+\." + col + r"\s*=\s*\w+", text):
                            errs.append(f"{rel}: INDIRECT `{inv.group(1)}` ระบุ {col} แต่ไม่พบ lambda `v => s.{col} = v` ในไฟล์ (ตารางค้าง)")
                        found.setdefault(col, []).append(f"{rel}:{line}")
                    continue
                targets = ASSIGN.findall(chunk)
                if not targets:
                    errs.append(f"{rel}:{line}: เรียก Protect/Encrypt แต่หาช่องที่เก็บไม่เจอ — เก็บลงช่องตรง ๆ (`X = ...`) หรือเพิ่มใน INDIRECT")
                    continue
                found.setdefault(targets[-1], []).append(f"{rel}:{line}")
    return found, errs


def registry(root):
    text = open(os.path.join(root, REGISTRY), encoding="utf-8").read()
    return set(re.findall(r"Col<\w+>\(\s*r\s*=>\s*r\.(\w+)\s*\)", text))


def check(root):
    found, errs = writers(root)
    reg = registry(root)
    if not reg:
        errs.append(f"{REGISTRY}: ไม่พบทะเบียน Col<T>(r => r.X) — checker อ่านทะเบียนไม่ได้")
    for col, where in sorted(found.items()):
        if col not in reg:
            errs.append(f"ช่อง {col} ถูกเขียนแบบเข้ารหัสที่ {', '.join(where)} แต่ไม่อยู่ในทะเบียน SecretReprotectPass "
                        f"⇒ ค่าเดิมจะค้างบนคีย์เดิม — เพิ่ม Col<T>(r => r.{col}) ใน {REGISTRY}")
    for col in sorted(reg - set(found) - READ_ONLY_REGISTERED):
        errs.append(f"ทะเบียน SecretReprotectPass มีช่อง {col} แต่ไม่มีผู้เขียนแบบเข้ารหัสในเรพ — ลบออก หรือเพิ่มใน READ_ONLY_REGISTERED พร้อมเหตุผล")
    return errs, found, reg


def self_test():
    fail = 0
    errs, found, reg = check(ROOT)
    if errs:
        print("self-test ล้ม: เรพจริงต้องผ่าน\n  " + "\n  ".join(errs)); fail = 1
    if len(found) < 20:
        print(f"self-test ล้ม: พบผู้เขียนเพียง {len(found)} ช่อง (คาด ≥ 20) — regex พลาด"); fail = 1

    def with_copy(mutate, label, expect_fail=True):
        nonlocal fail
        t = tempfile.mkdtemp()
        try:
            shutil.copytree(os.path.join(ROOT, "Accounting"), os.path.join(t, "Accounting"),
                            ignore=shutil.ignore_patterns("bin", "obj", "wwwroot"))
            mutate(t)
            e, _, _ = check(t)
            if expect_fail and not e:
                print(f"self-test ล้ม: {label} ต้องถูกฟ้อง"); fail = 1
            if not expect_fail and e:
                print(f"self-test ล้ม: {label} ต้องไม่ถูกฟ้อง: {e}"); fail = 1
        finally:
            shutil.rmtree(t)

    def add_file(t, body):
        with open(os.path.join(t, "Accounting", "Services", "Implementations", "ZzSelfTest.cs"), "w", encoding="utf-8") as fh:
            fh.write(body)

    with_copy(lambda t: add_file(t, "class Z { void F() { x.BrandNewApiSecret = _secrets.Protect(v); } }"), "ช่องใหม่ผ่าน Protect ที่ไม่ลงทะเบียน")
    with_copy(lambda t: add_file(t, "class Z { void F() { var g = new G { NewKeyEnc = EncryptionHelper.Encrypt(k, key), }; } }"), "ช่องใหม่ผ่าน EncryptionHelper.Encrypt")
    with_copy(lambda t: add_file(t, "class Z { void F() { sink(_secrets.Protect(v)); } }"), "delegate ที่ไม่รู้จัก")
    with_copy(lambda t: add_file(t, "class Z { void F() { x.Secret = _secrets.Protect(v); } }"), "ช่องที่ลงทะเบียนแล้ว", expect_fail=False)
    with_copy(lambda t: add_file(t, "class Z { void F() { // x.BrandNewApiSecret = _secrets.Protect(v);\n } }"), "คอมเมนต์", expect_fail=False)

    def drop_registry_col(t):
        p = os.path.join(t, REGISTRY)
        s = open(p, encoding="utf-8").read()
        s2 = s.replace("Col<SitePaymentGateway>(r => r.ApiKeyEncrypted),", "")
        assert s2 != s
        open(p, "w", encoding="utf-8").write(s2)
    with_copy(drop_registry_col, "ถอด ApiKeyEncrypted ออกจากทะเบียน")

    def stale_registry_col(t):
        p = os.path.join(t, REGISTRY)
        s = open(p, encoding="utf-8").read()
        s2 = s.replace("Col<ExternalIntegration>(r => r.SecretKey));", "Col<ExternalIntegration>(r => r.SecretKey), Col<ExternalIntegration>(r => r.GhostColumn));")
        assert s2 != s
        open(p, "w", encoding="utf-8").write(s2)
    with_copy(stale_registry_col, "ทะเบียนมีช่องที่ไม่มีผู้เขียน")

    if fail == 0:
        print("self-test: ผ่าน")
    return fail


def main():
    if "--self-test" in sys.argv:
        return self_test()
    errs, found, reg = check(ROOT)
    if errs:
        print("secret_registry_check: ❌")
        for e in errs:
            print("  " + e)
        return 1
    print(f"secret_registry_check: ✅ ผู้เขียนความลับ {len(found)} ช่อง อยู่ในทะเบียนครบ ({len(reg)} ช่อง)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
