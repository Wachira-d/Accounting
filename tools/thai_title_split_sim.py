# -*- coding: utf-8 -*-
"""จำลองการแยก "คำนำหน้า | ชื่อตัว | ชื่อสกุล" ของไฟล์ ภ.ง.ด.3 — สามยุค

⚠️ บทเรียนตอนเขียนรุ่นแรก: คอลัมน์ "ก่อนแก้" ของรุ่นเดิม **ไม่ใช่อัลกอริทึมเดิมจริง**
(มันคือ Split ตัวใหม่ที่ปิด guard บางส่วน) ⇒ ไม่เคยเทียบกับพฤติกรรมก่อนคอมมิต และ
พลาด regression "นายสมชาย" ไปทั้งดุ้น. รุ่นนี้ยุค A คัดลอกจากโค้ดก่อนคอมมิต a3b93f7 จริง
(git show a3b93f7~1:Accounting/Helpers/PndTextFileFormat.cs)

ยุค A = ก่อน a3b93f7      : StartsWith เปล่า ๆ บนลิสต์ 9 ตัว · คำนำหน้าถูก**ทิ้ง** (ไม่มี Col12)
ยุค B = a3b93f7..97d9be2  : ผ่าน ThaiTitleHelper.Split ที่มี guard กันชื่อร้าน
ยุค C = ตอนนี้            : คำนำหน้าที่ผู้ใช้ยืนยันแล้ว (Contact.TitleTh) ตัดแบบ deterministic
                            ถ้าไม่มี จึงตกไปใช้ยุค B
"""
ALL = [
 ("นาย", ["Mr.", "Mr"]), ("นาง", ["Mrs.", "Mrs"]),
 ("นางสาว", ["น.ส.", "น.ส", "นส.", "Miss", "Ms.", "Ms"]),
 ("เด็กชาย", ["ด.ช.", "ด.ช", "ดช.", "Master"]), ("เด็กหญิง", ["ด.ญ.", "ด.ญ", "ดญ."]),
 ("ดร.", ["ดร", "Dr.", "Dr"]),
 ("บริษัท", ["บจก.", "บมจ.", "บริษัทมหาชนจำกัด"]),
 ("ห้างหุ้นส่วนจำกัด", ["หจก.", "หจก"]), ("ห้างหุ้นส่วนสามัญ", ["หสน.", "หสน"]),
 ("คณะบุคคล", []), ("มูลนิธิ", []), ("สมาคม", []),
]
JURISTIC = {"บริษัท", "ห้างหุ้นส่วนจำกัด", "ห้างหุ้นส่วนสามัญ", "คณะบุคคล", "มูลนิธิ", "สมาคม"}
# ลิสต์ที่ PndTextFileFormat ถือเองก่อน a3b93f7 (ไม่มี เด็กชาย/เด็กหญิง — บั๊กที่ยุค B แก้)
OLD_TITLES = ["นางสาว", "น.ส.", "นาง", "นาย", "ดร.", "Mr.", "Mrs.", "Miss", "Ms."]


def cands():
    out = []
    for thai, al in ALL:
        for f in al + [thai]:
            out.append((f, thai, f == thai))
    return sorted(out, key=lambda x: -len(x[0]))


def forms_of(canon):
    for thai, al in ALL:
        if canon == thai or canon in al:
            return sorted(al + [thai], key=len, reverse=True)
    return []


def comb(c):
    o = ord(c)
    return o == 0x0E31 or 0x0E33 <= o <= 0x0E3A or 0x0E47 <= o <= 0x0E4E


def helper_split(name):
    """ThaiTitleHelper.Split ตัวปัจจุบัน (ยุค B/C)"""
    name = (name or "").strip()
    if not name:
        return ("", "")
    for form, canon, iscanon in cands():
        if not name.lower().startswith(form.lower()):
            continue
        rest = name[len(form):].lstrip()
        if not rest:
            continue
        if not name[len(form)].isspace():
            if ' ' not in rest:
                continue                      # ชื่อร้าน — ห้ามผ่า
            if not iscanon and not form.endswith('.'):
                continue
            if comb(rest[0]):
                continue
        return (canon, rest)
    return ("", name)


def era_a(name, juristic, known):
    """ก่อน a3b93f7 — ตัดด้วย StartsWith เปล่า ๆ แล้ว **ทิ้งคำนำหน้า** (ไม่มี Col12)"""
    n = (name or "").strip()
    if juristic or not n:
        return ("", n, "")
    for t in OLD_TITLES:
        if n.startswith(t):
            n = n[len(t):].lstrip()
            break
    sp = n.find(' ')
    return ("", n, "") if sp < 0 else ("", n[:sp].strip(), n[sp + 1:].strip())


def era_b(name, juristic, known):
    n = (name or "").strip()
    if juristic or not n:
        return ("", n, "")
    title, rest = helper_split(n)
    if title and title not in JURISTIC:
        n = rest
    sp = n.find(' ')
    first, last = (n, "") if sp < 0 else (n[:sp].strip(), n[sp + 1:].strip())
    return (title if title and title not in JURISTIC else "", first, last)


def era_c(name, juristic, known):
    n = (name or "").strip()
    if juristic or not n:
        return ("", n, "")
    col12 = ""
    if known:
        col12 = known
        for f in forms_of(known):
            if n.lower().startswith(f.lower()):
                rest = n[len(f):].lstrip()
                if rest:
                    n = rest
                break
    else:
        title, rest = helper_split(n)
        if title and title not in JURISTIC:
            n, col12 = rest, title
    sp = n.find(' ')
    first, last = (n, "") if sp < 0 else (n[:sp].strip(), n[sp + 1:].strip())
    return (col12, first, last)


# (ชื่อบน Contact, TitleTh ที่ผู้ใช้ยืนยัน, นิติบุคคล?, ผลที่ต้องได้ (Col12, Col4, Col5), เหตุผล)
CASES = [
 ("ดรุณี ใจดี",        "",        False, ("", "ดรุณี", "ใจดี"),          "ชื่อจริงขึ้นต้น ดร — ห้ามผ่า"),
 ("Drake Co Ltd",      "",        True,  ("", "Drake Co Ltd", ""),       "นิติบุคคล — ชื่อเต็มอยู่ Col4"),
 ("นางสาวสมหญิง ใจดี", "",        False, ("นางสาว", "สมหญิง", "ใจดี"),   "รูปเต็มติดชื่อ + มีนามสกุล"),
 ("น.ส.สมหญิง ใจดี",   "",        False, ("นางสาว", "สมหญิง", "ใจดี"),   "ตัวย่อมีจุดติดชื่อ"),
 ("นาย สมชาย ใจดี",    "",        False, ("นาย", "สมชาย", "ใจดี"),       "มีช่องว่าง"),
 ("เด็กชาย สมชาย ใจดี", "",       False, ("เด็กชาย", "สมชาย", "ใจดี"),   "ผู้เยาว์ — ลิสต์เก่าไม่มี (ยุค A พัง)"),
 ("นายช่างการไฟฟ้า",   "",        False, ("", "นายช่างการไฟฟ้า", ""),    "ชื่อร้าน ไม่รู้คำนำหน้า — ห้ามผ่า"),
 ("นางเลิ้งพาณิชย์",    "",        False, ("", "นางเลิ้งพาณิชย์", ""),     "ชื่อร้าน — ห้ามผ่า"),
 # ── regression ที่รุ่นเดิมของ sim ไม่มีเคสเลย ──
 ("นายสมชาย",          "นาย",     False, ("นาย", "สมชาย", ""),           "คำนำหน้ายืนยันแล้ว + ไม่มีนามสกุล"),
 ("น.ส.สมหญิง",        "นางสาว",  False, ("นางสาว", "สมหญิง", ""),       "ยืนยัน 'นางสาว' แต่ชื่อเขียนย่อ"),
 ("นายสมชาย ใจดี",     "นาย",     False, ("นาย", "สมชาย", "ใจดี")     ,  "ยืนยันแล้ว + มีนามสกุล"),
 ("สมชาย ใจดี",        "นาย",     False, ("นาย", "สมชาย", "ใจดี"),       "ชื่อไม่มีคำนำหน้าอยู่แล้ว — ห้ามตัดมั่ว"),
 ("นายสมชาย",          "",        False, ("", "นายสมชาย", ""),           "ไม่รู้คำนำหน้า + คำเดียว = ไม่เดา (รายงานแทน)"),
]

for label, fn in (("ยุค A (ก่อน a3b93f7)", era_a),
                  ("ยุค B (a3b93f7..97d9be2)", era_b),
                  ("ยุค C (ตอนนี้)", era_c)):
    print("=== %s ===" % label)
    bad = 0
    for name, known, jur, exp, why in CASES:
        got = fn(name, jur, known)
        ok = got == exp
        if not ok:
            bad += 1
        print(("  OK  " if ok else "  **  ") + "%-22r known=%-8r -> %r  (คาด %r) %s"
              % (name, known, got, exp, why))
    print("  ผิด %d/%d" % (bad, len(CASES)))
