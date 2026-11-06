#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ห้ามอ้างเลขมาตรา พ.ร.บ.การบัญชี 2543 ผิดซ้ำ (ratchet · baseline 0)

═══ ที่มา (รอบ 250 WP-06 · erp-review/2026-11-06/RETENTION_LAW_CHECK.md §2 + ภาคผนวก + ฝ่ายค้าน GO ทั้ง A–D) ═══
~150 จุดในโค้ด/UI/เทสต์/CLAUDE.md อ้าง "พ.ร.บ.การบัญชี ม.10" เป็นระยะเก็บเอกสาร 5 ปี — ตัวบทจริง: ม.10 = ปิดบัญชี/รอบบัญชี ·
ม.14 = เก็บ ≥ 5 ปีนับแต่วันปิดบัญชี · ม.11 = จัดทำ/ยื่นงบ · ม.19 = ต้องมีผู้ทำบัญชี (คุณสมบัติตาม ม.7(6)) · "ม.11 ทวิ" ไม่มีในตัวบท
(ฐานการเก็บอิเล็กทรอนิกส์คือ พ.ร.บ.ธุรกรรมทางอิเล็กทรอนิกส์ 2544 ม.12) · "ท.ป.4/2528" เป็นคำสั่งอัตราหัก ณ ที่จ่าย ไม่ใช่ระยะเก็บ
ฝ่ายค้าน WP-06 MED-2: รุ่นแรกจับได้ ~72% ของ defect เดิม (INTENT ข้ามทุกบรรทัดที่มี "ห้าม/ไม่ใช่" · ไม่รู้จัก "มาตรา 10"/"ม. 10"/เลขไทย/
"Sec. 10" · ไม่มีกฎ ม.11 รอบบัญชี / ม.7 ผู้ทำบัญชี · ไม่สแกน DOCUMENT_FLOW/ACCOUNT_STRUCTURE/TEST_PLAN) ⇒ รุ่นนี้ · self-test ใช้บรรทัดที่พลาดจริง

═══ กติกา (ระดับบรรทัด · ระยะใกล้ · F4 ข้อ 3 — ไม่ตาม taint) ═══
  A  ม.10/มาตรา 10 (เลขไทยได้ · เว้นวรรคได้) ใกล้คำเก็บรักษา/หลักฐาน/§87/3/ชื่อ พ.ร.บ.การบัญชี — เว้นเมื่อใกล้คำปิดบัญชี/รอบบัญชี (= ม.10 ถูก)
     หรือเป็นมาตราของ พ.ร.บ.ธุรกรรมทางอิเล็กทรอนิกส์
  A4 EN: Accounting Act Section/Sec./s. 10
  B  ม.11 ใกล้คำรอบบัญชี/ปิดบัญชี/เปลี่ยนรอบ — เว้นเมื่อบรรทัดพูดเรื่องยื่นงบ (DBD · งบการเงิน · AGM · ที่ประชุมใหญ่ = ม.11 ถูก)
  C  "ม.11 ทวิ"
  D  ม.7 ที่ไม่ตามด้วย "(" ใกล้คำผู้ทำบัญชี/CPD/Bookkeeper · EN Section 7 + bookkeeper
  T  ท.ป.4/2528 ใกล้คำ "เก็บ"
ข้อยกเว้นเดียว: การอ้างเชิงปฏิเสธที่ตัวที่จับเอง — ภายใน `DoesNotContain(` หรือหลังคำ "ไม่ใช่"/"เดิมเขียน"/"~~" ในระยะ 30 ตัวอักษร
(ไม่ข้ามทั้งบรรทัดอีกต่อไป) · ข้อความฉบับ 1.0 ที่แช่แข็งใน `LegalDocumentDefaults` (hash pin ใน LegalReferenceTests) ไม่สแกน
สแกน: Accounting/** (.cs .html .js) · Accounting.Tests/** · CLAUDE.md · DOCUMENT_FLOW.md · ACCOUNT_STRUCTURE.md · TEST_PLAN.md
audit row เก่าที่เก็บ LegalReference เดิมไว้ใน DB ไม่อยู่ในขอบเขต (append-only + hash chain — ห้าม migrate · กฎ M)

ใช้: python3 tools/legal_citation_check.py [--self-test] [--root <dir>]
"""
import os
import re
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCAN = [("Accounting", (".cs", ".html", ".js")), ("Accounting.Tests", (".cs",))]
FILES = ["CLAUDE.md", "DOCUMENT_FLOW.md", "ACCOUNT_STRUCTURE.md", "TEST_PLAN.md"]
SKIP_DIRS = {"bin", "obj", ".claude", "node_modules", ".git", "lib", "vendor"}
FROZEN = re.compile(r'private const string (?:PlatformPrivacyV1|PlatformTermsV1)(?:En)? = """\n.*?\n""";', re.S)

D = r"(?![0-9๐-๙/])"
SEC = lambda n, th: r"(?:ม\.|มาตรา)\s*(?:" + n + "|" + th + ")"
SEC10 = re.compile(SEC("10", "๑๐") + D)
SEC11 = re.compile(SEC("11", "๑๑") + r"(?![0-9๐-๙/]|\s*ทวิ)")
SEC7 = re.compile(SEC("7", "๗") + r"(?![0-9๐-๙/(])")
ACT = r"พ\.ร\.บ\.\s*(?:การ)?บัญชี"
STRONG = re.compile(r"เก็บ|5 ปี|ห้าปี|retention|Retention|หลักฐาน|legal.?hold|87/3|ร่องรอย|ตรวจสอบย้อนหลัง")
ACT_RX = re.compile(ACT)
CLOSING = re.compile(r"ปิดบัญชี|(?<!สิ้น)รอบบัญชี|(?<!สิ้น)รอบปีบัญชี|เปลี่ยนรอบ|12 เดือนพอดี|สิบสองเดือน|ปิดปี|ปิดงวด|เดือนเริ่มรอบ|รอบปกติ|เดือนของรอบ|fiscal|Fiscal")
ETA = re.compile(r"ธุรกรรมทางอิเล็กทรอนิกส์|ETA")
FILING = re.compile(r"ยื่นงบ|งบการเงิน|DBD|AGM|ที่ประชุมใหญ่|XBRL")
BOOKKEEPER = re.compile(r"ผู้ทำบัญชี|CPD|Bookkeeper|bookkeeper")
NEG_FAR = re.compile(r"DoesNotContain\(")          # ในระยะ 45 ตัวอักษร (สตริงในเทสต์ทิศตรงข้าม)
NEG_NEAR = re.compile(r"(?:ไม่ใช่|เดิมเขียน|~~)\s*\(?\s*$")   # ติดหน้าตัวที่จับ ("ไม่ใช่ ม.10") — ไม่ใช่ "ไม่ใช่" ที่ไหนก็ได้บนบรรทัด
OTHER = [
    ("C", re.compile(r"(?:ม\.|มาตรา)\s*(?:11|๑๑)\s*ทวิ")),
    ("A4", re.compile(r"Accounting Act,?\s*(?:Section|Sec\.|s\.)\s*10\b|(?:Section|Sec\.|s\.)\s*10 of the Accounting Act")),
    ("D-EN", re.compile(r"Section 7\b(?!\s*\()[^\n]{0,140}(?:bookkeeper|Bookkeeper)|(?:bookkeeper|Bookkeeper)[^\n]{0,140}Section 7\b(?!\s*\()")),
    ("T", re.compile(r"ท\.ป\.\s*4/2528[^\n]{0,40}เก็บ")),
]


def win(line, m, before=60, after=60):
    return line[max(0, m.start() - before):m.end() + after]


def negated(line, m, span=False):
    """การอ้างเชิงปฏิเสธที่ตัวที่จับเอง — คำปฏิเสธภายใน 45 ตัวอักษรก่อนหน้า (หรือภายในช่วงที่จับ สำหรับกฎ T ที่กินถึงคำ "เก็บ")"""
    return (bool(NEG_FAR.search(line[max(0, m.start() - 45):m.start()])) or bool(NEG_NEAR.search(line[max(0, m.start() - 12):m.start()]))
            or (span and "ไม่ใช่" in m.group(0)))


def scan_line(line):
    for m in SEC10.finditer(line):
        w = win(line, m)
        if negated(line, m) or ETA.search(win(line, m, 60, 10)):
            continue
        # ม.10 ถูกเมื่อพูดถึงปิดบัญชี/รอบบัญชี · ผิดเมื่อพูดถึงการเก็บรักษา หรืออ้างชื่อ พ.ร.บ. ลอย ๆ โดยไม่มีบริบทรอบบัญชีบนบรรทัด
        if STRONG.search(w) and not CLOSING.search(w):
            return "A"
        if ACT_RX.search(win(line, m, 40, 0)) and not CLOSING.search(line):
            return "A"
    for m in SEC11.finditer(line):
        if negated(line, m) or FILING.search(line):
            continue
        # ม.11 ถูกเฉพาะเรื่องยื่นงบ ⇒ อ้างชื่อ พ.ร.บ. + ม.11 โดยไม่มีบริบทยื่นงบบนบรรทัด = ผิด (รอบบัญชีคือ ม.10)
        if CLOSING.search(win(line, m)) or ACT_RX.search(win(line, m, 40, 0)):
            return "B"
    for m in SEC7.finditer(line):
        if negated(line, m):
            continue
        # ม.7 เป็นอำนาจอธิบดีหลายอนุมาตรา ⇒ ต้องระบุอนุมาตราเสมอ (ม.7(4) เอกสารประกอบ · ม.7(6) คุณสมบัติผู้ทำบัญชี) · หน้าที่มีผู้ทำบัญชี = ม.19
        if BOOKKEEPER.search(win(line, m, 80, 80)) or ACT_RX.search(win(line, m, 40, 0)):
            return "D"
    for code, rx in OTHER:
        for m in rx.finditer(line):
            if not negated(line, m, span=True):
                return code
    return None


RETENTION_FILE = re.compile(r"Retention|Purge|Erasure|Disposal", re.I)
TP4 = re.compile(r"ท\.ป\.\s*4/2528")


def scan_text(text, path=""):
    text = FROZEN.sub(lambda m: "\n" * m.group(0).count("\n"), text)
    retention_file = bool(RETENTION_FILE.search(os.path.basename(path)))
    hits = []
    for i, line in enumerate(text.split("\n"), 1):
        code = scan_line(line)
        # ไฟล์ตัวตัดสินระยะเก็บ/การลบ: ท.ป.4/2528 (อัตราหัก ณ ที่จ่าย) ไม่ใช่ที่มาของ legal hold ใด ๆ
        if not code and retention_file:
            m = TP4.search(line)
            if m and not negated(line, m):
                code = "T"
        if code:
            hits.append((i, code, line.strip()[:160]))
    return hits


def iter_files(root):
    for top, exts in SCAN:
        for d, dirs, files in os.walk(os.path.join(root, top)):
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
        try:
            text = open(p, encoding="utf-8").read()
        except (UnicodeDecodeError, OSError):
            continue
        for ln, code, s in scan_text(text, p):
            hits.append((os.path.relpath(p, root), ln, code, s))
    return hits


# บรรทัดจริงก่อน WP-06 (66677bea~1) ที่รุ่นแรกพลาด (ฝ่ายค้าน MED-2) + รูปแบบที่หลุด — ต้องฟ้องทุกบรรทัด
MUST_FLAG = [
    'if (HasIssuedDocuments) parts.Add("เอกสารที่ออกแล้ว (พ.ร.บ.บัญชี ม.10 · §87/3)");',
    'if (HasPostedJournals) parts.Add("รายการบัญชีที่ลงแล้ว (พ.ร.บ.บัญชี ม.10)");',
    '? $"ห้ามลบถาวร — เอกสารอยู่ในช่วงเก็บรักษาตามกฎหมาย (§87/3 · พ.ร.บ.การบัญชี ม.10) ถึง {until:dd/MM/yyyy} ({names}). " +',
    '/// error) · ผู้เรียกต้องตัดสินเรื่องระยะเก็บรักษาก่อนเรียก (พ.ร.บ.การบัญชี ม.10)</summary>',
    '// ฝ่ายค้านรอบสอง (R2-C4): สแกนที่ลง JE ตรง (ไฟล์ยังเป็น OcrScan) คือหลักฐานของ JE — ห้ามลบ (ม.10 · §87/3) ·',
    '// ไฟล์เดิมลบหลังบันทึกสำเร็จ · รูป QR ไม่ใช่เอกสารทางบัญชี (ไม่อยู่ใต้ระยะเก็บ ม.10) — ประวัติอยู่ใน audit (hash)',
    '// งานของแอดมินแพลตฟอร์ม · IgnoreQueryFilters รวมแถวที่ถอดจากรายการแต่ต้องเก็บไฟล์ (ม.10)',
    '/// per §87/3 + ม.10). ห้ามลบจริงก่อนวันนี้ (legal hold). คำนวณตอน approve.</summary>',
    '/// (พ.ร.บ.การบัญชี ม.10 · ป.รัษฎากร §87/3 — เกณฑ์เดียวกับ <c>PdpaService.ApplyErasureAsync</c>)',
    '/// (พ.ร.บ.การบัญชี ม.10 · §87/3) · และ retry สร้างแถวใหม่ที่ชี้',
    '// สแกนที่**ลงเป็น JE ตรง** ⇒ JE ที่โพสต์แล้วเสียเอกสารประกอบใน 30 วัน (ขัด ม.10 · §87/3)',
    '/// <summary>รอบ 200 ทีม R · B-06 — ด่านลบถาวร §87/3 / ม.10 ต้องไม่เป็น no-op กับใบที่ไม่ได้ผ่าน ApproveDocumentAsync',
    '// โครงบัญชีที่ต้องคงไว้ตาม พ.ร.บ.การบัญชี ม.10',
    'ต้องเก็บ 5 ปีตาม พ.ร.บ.การบัญชี มาตรา 10',
    'เก็บเอกสาร 5 ปี (พ.ร.บ.การบัญชี ม. 10)',
    'เก็บเอกสาร 5 ปี (พ.ร.บ.การบัญชี มาตรา ๑๐)',
    '| Accounts | **5 years** | Accounting Act Section 10 |',
    'retained for 5 years (Accounting Act, Sec. 10)',
    'retained for 5 years (Accounting Act, s.10)',
    '- [ ] **ม.10** เก็บเอกสาร **5 ปี** (ขยายได้ถึง 7 ปี)',
    '/// audit hash chain — control ตาม พ.ร.บ.การบัญชี ม.11 ทวิ / SOC2',
    'parts.Add("(ท.ป.4/2528 เก็บ 5 ปี)");',
    '// พ.ร.บ.การบัญชี ม.11 — รอบปกติต้องเป็น 12 เดือนพอดี',
    'public const string LegalReference = "พ.ร.บ.การบัญชี 2543 ม.11 (ปิดบัญชีทุกรอบสิบสองเดือน)";',
    '<h3 class="card-title">👤 ผู้ทำบัญชี (พ.ร.บ.การบัญชี ม.7)</h3>',
    '"⛔ พ.ร.บ.การบัญชี ม.7: ต้องระบุ \'ชื่อผู้ทำบัญชี + เลขทะเบียน CPD\' " +',
    '- Under Section 7 of the Accounting Act B.E. 2543 (2000), a person required to prepare accounts must engage a **qualified bookkeeper**',
    '+ YearEndReopenRule.WayForward + " (พ.ร.บ.การบัญชี 2543 ม.11)";',
    'legalReference = "พ.ร.บ.การบัญชี 2543 ม.11",',
    '/// CompanySettings ก่อน (พ.ร.บ.การบัญชี ม.7) — ไม่งั้น throw.</summary>',
    '/// ใช้เป็นหลักฐานประกอบการลงบัญชีตาม พ.ร.บ.การบัญชี ม.7 ไม่ได้',
    '/// <summary>ระยะเก็บรักษาเอกสารที่ออกแล้ว — §87/3 (รายงานภาษี 5 ปี) + พ.ร.บ.การบัญชี ม.10 (5 ปีนับจากสิ้นรอบบัญชี)',
    '/// เก็บ <see cref="RetentionYears"/> ปีนับจากวันแนบ</b> (5 ปีหลังสิ้นรอบบัญชี ม.10 — วันแนบอยู่ในรอบที่สิ้นไม่เกิน 12 เดือน',
]
# ถูกต้อง — ต้องไม่ฟ้อง
MUST_PASS = [
    '/// ข้อมูลบัญชีไม่ถูกลบจริง (พ.ร.บ.การบัญชี ม.14 เก็บ 5 ปี)',
    'public const string LegalReference = "พ.ร.บ.การบัญชี 2543 ม.10 (ปิดบัญชีทุกรอบสิบสองเดือน)";',
    '"รายการบัญชีของใบนี้อยู่ในงวด/ปีที่ปิดบัญชีแล้ว (พ.ร.บ.การบัญชี ม.10) — ลบแล้วงบของงวดที่ปิดจะเปลี่ยน"',
    '/// ม.11 + ประกาศ DBD เรื่องวิธีการยื่นงบการเงินทางอิเล็กทรอนิกส์',
    '- [ ] **DBD submission (ม.11)** — บริษัท: 1 เดือนจาก AGM approve (AGM ≤ 4 เดือนตาม ป.พ.พ. ม.1197) · ห้างฯ/อื่น ≤ 5 เดือนจากวันปิดบัญชี;',
    'Assert.DoesNotContain("ม.10", TrialDataRetentionPolicy.LegalReference);',
    'Assert.Contains("ม.14", r.Reason);   // รอบ 250 WP-06: ระยะเก็บ = ม.14 (ม.10 คือปิดบัญชี)',
    '/// เกณฑ์ 1,000 บาท (ท.ป.4/2528 ข้อ 12)',
    '| Accounts | **5 years** | Accounting Act, Section 14 |',
    '- Under Section 19 of the Accounting Act B.E. 2543 (2000), ... prescribed by the Director-General under Section 7(6)',
    '// ผู้ทำบัญชี (พ.ร.บ.การบัญชี ม.19 (คุณสมบัติตาม ม.7(6)))',
    '/// ใช้เป็นหลักฐานประกอบการลงบัญชีตาม พ.ร.บ.การบัญชี ม.7(4) ไม่ได้',
    'พ.ร.บ.ว่าด้วยธุรกรรมทางอิเล็กทรอนิกส์ พ.ศ. 2544 ม.12 ประกอบ ม.10 (ต้นฉบับ) · ระยะเก็บ พ.ร.บ.การบัญชี ม.14',
    '"การเปลี่ยนรอบปีบัญชีต้องได้รับอนุญาตจากสารวัตรใหญ่บัญชี/สารวัตรบัญชี (พ.ร.บ.การบัญชี 2543 ม.10) "',
    '| ระยะเก็บ = ม.14 (ไม่ใช่ ม.10/ท.ป.4/2528 ที่เคยอ้าง) |',
    '| CLS-12 | ทุกเดือนเริ่มรอบได้ 12 เดือนพอดี (พ.ร.บ.บัญชี ม.10) |',
]


def self_test():
    fail = 0
    for s in MUST_FLAG:
        if not scan_line(s):
            print("self-test ล้ม: ต้องฟ้อง —", s); fail = 1
    for s in MUST_PASS:
        if scan_line(s):
            print(f"self-test ล้ม: ไม่ควรฟ้อง [{scan_line(s)}] —", s); fail = 1
    # ข้อความ 1.0 ที่แช่แข็งไม่ถูกสแกน · นอกบล็อกแช่แข็งยังสแกน
    frozen = ('    private const string PlatformTermsV1 = """\n| x | **5 ปี** | พ.ร.บ.การบัญชี ม.10 |\n""";\n'
              '    private const string PlatformTermsNextDraft = """\n| x | **5 ปี** | พ.ร.บ.การบัญชี ม.10 |\n""";\n')
    if not scan_text('if (HasIssuedWhtCerts) parts.Add("หนังสือรับรอง 50 ทวิ ที่ออกแล้ว (ท.ป.4/2528)");', "Helpers/TrialDataRetentionPolicy.cs"):
        print("self-test ล้ม: ท.ป.4/2528 ในไฟล์ตัวตัดสินระยะเก็บต้องฟ้อง"); fail = 1
    if scan_text('/// เกณฑ์ 1,000 บาท (ท.ป.4/2528 ข้อ 12)', "Helpers/ThaiWhtRateTable.cs"):
        print("self-test ล้ม: ท.ป.4/2528 นอกไฟล์ระยะเก็บไม่ควรฟ้อง"); fail = 1
    hits = scan_text(frozen)
    if [h[0] for h in hits] != [5]:
        print("self-test ล้ม: บล็อกแช่แข็งต้องข้าม · บล็อกร่างต้องฟ้อง —", hits); fail = 1
    with tempfile.TemporaryDirectory() as t:
        os.makedirs(os.path.join(t, "Accounting", "Helpers"))
        with open(os.path.join(t, "Accounting", "Helpers", "X.cs"), "w", encoding="utf-8") as fh:
            fh.write("\n".join(MUST_PASS + [MUST_FLAG[0]]))
        with open(os.path.join(t, "TEST_PLAN.md"), "w", encoding="utf-8") as fh:
            fh.write(MUST_FLAG[1])
        if len(run(t)) != 2:
            print("self-test ล้ม: สแกนไดเรกทอรีต้องเจอ 2 จุด (โค้ด + TEST_PLAN)", run(t)); fail = 1
    print("self-test: ผ่าน" if not fail else "self-test: ล้ม")
    return fail


def main():
    if "--self-test" in sys.argv:
        sys.exit(self_test())
    root = ROOT
    if "--root" in sys.argv:
        root = sys.argv[sys.argv.index("--root") + 1]
    hits = run(root)
    if "--root" in sys.argv:
        for p, ln, code, s in hits:
            print(f"  {p}:{ln} [{code}] {s}")
        print(f"{len(hits)} จุด")
        return
    if hits:
        print(f"❌ legal_citation_check: {len(hits)} จุด (baseline 0) — อ้างเลขมาตรา พ.ร.บ.การบัญชีผิด "
              "(ระยะเก็บ = ม.14 · ปิดบัญชี/รอบบัญชี = ม.10 · ยื่นงบ = ม.11 · ผู้ทำบัญชี = ม.19 (ม.7(6)) · ไฟล์อิเล็กทรอนิกส์ = ETA 2544 ม.12)")
        for p, ln, code, s in hits:
            print(f"  {p}:{ln} [{code}] {s}")
        sys.exit(1)
    print("✅ legal_citation_check: 0 จุด")


if __name__ == "__main__":
    main()
