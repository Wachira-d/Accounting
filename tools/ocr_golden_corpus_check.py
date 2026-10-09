#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ocr_golden_corpus_check.py — กระดาษตัวอย่างทุกใบของเส้น OCR ต้องมี golden row ล็อกคำตอบ ("มีเคสใหม่ = ต้องเพิ่ม golden row")

ที่มา (2026-10-09 · มอบหมายเจ้าของ: "มีเคสมาให้แก้ต้องพัฒนาขึ้นเรื่อยๆ ไม่ทำให้การแก้ครั้งก่อนที่เสร็จสมบูรณ์เปลี่ยนแปลงไปจากเดิม")
----------------------------------------------------------------------------------------------------------------------
เรพมี replay harness (`Accounting.Tests/OcrReplayHarness.cs` + `OcrReplay*GoldenTests.cs`) ที่เล่นกระดาษจริงผ่านตัวตัดสิน pure
ทุกตัวแล้วล็อกคำตอบเป็นตัวอักษร — แต่ไม่มีอะไรบังคับว่า **กระดาษใบใหม่** ที่ถูกเพิ่มใน `OcrPaperSamples.cs` (หรือ e-Tax XML ใน
`EtaxFixtures.cs`) จะถูกใส่เข้า Corpus และมีแถว golden · เคสจริง: ใบ Makro/Shopee/Lazada ของรอบ 192 ถูกเพิ่มเป็นตัวอย่างและเทสต์
เฉพาะ helper ที่แก้ แต่ใบร้านวัสดุ/ซูเปอร์ (รอบ 190) **ไม่มีแถวระดับบรรทัด**เลยจนรอบนี้ ⇒ การแก้ตัวกระจายส่วนลด/ตัวเฉลี่ย VAT
รอบไหนก็เปลี่ยนบรรทัดของสองใบนั้นได้เงียบ ๆ (ยอดรวมยังตรง) — defect class เดียวกับ RG-01..04 (CLAUDE.md กฎเหล็ก #4 §H)

กติกา (ratchet · ฝั่ง "ไม่มีแถว" ต้องแม่น — ฟ้องผิดแม้จุดเดียว = checker พัง):
  1. ทุก `public const string` ใน `OcrPaperSamples.cs` ต้องถูกอ้างใน Corpus ของ `OcrReplayHarness.cs`
     (`new ReplayPaper("<ชื่อใบ>", OcrPaperSamples.<ชื่อ>`)
  2. ทุกใบใน Corpus (ทั้งที่อ้าง OcrPaperSamples และข้อความ inline) ต้องมี "<ชื่อใบ>" ปรากฏใน `OcrReplay*GoldenTests.cs`
     อย่างน้อยหนึ่งไฟล์ (นอกคอมเมนต์) — คือมี golden row ล็อกคำตอบ
  3. ทุก fixture ที่เป็น "เอกสาร" ใน `EtaxFixtures.cs` (raw string `\"\"\"` · หรือ literal หลายบรรทัด/มี \\n) ต้องถูกอ้าง
     `EtaxFixtures.<ชื่อ>` ในไฟล์เทสต์อื่นอย่างน้อยหนึ่งไฟล์ (นอกคอมเมนต์) · ค่าคงที่สั้น ๆ (BOM) ไม่นับเป็นเอกสาร

สิ่งที่ checker นี้ **ทำไม่ได้** (จดตรง ๆ): ไม่รู้ว่า golden row "ครบช่อง" ไหม (ใบที่มีแค่ Assert ช่องเดียวก็ผ่าน) — ความครบของช่อง
ล็อกด้วยเทสต์ `ทุกใบในชุด_มีแถวระดับบรรทัดครบ…` ใน OcrReplayLineGoldenTests ซึ่งวนทุกใบใน Corpus เอง

ใช้: python3 tools/ocr_golden_corpus_check.py            # ตรวจเรพ
     python3 tools/ocr_golden_corpus_check.py --self-test  # negative test (ไฟล์สังเคราะห์: ใบที่ไม่มีแถวต้องถูกฟ้อง · ใบที่มีต้องไม่ถูกฟ้อง)
"""
import glob
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TESTS = os.path.join(ROOT, "Accounting.Tests")

SAMPLES_FILE = "OcrPaperSamples.cs"
HARNESS_FILE = "OcrReplayHarness.cs"
GOLDEN_GLOB = "OcrReplay*GoldenTests.cs"
ETAX_FILE = "EtaxFixtures.cs"

CONST_RE = re.compile(r"public\s+const\s+string\s+(\w+)\s*=\s*", re.M)
# ทุก ReplayPaper ถูกนับ — ไม่ว่าข้อความมาจาก OcrPaperSamples · literal inline · หรือแหล่งอื่น (ฝ่ายค้าน 2026-10-09 ข้อ 5: รุ่นแรกข้ามแหล่งอื่นเงียบ)
PAPER_RE = re.compile(r"new\s+ReplayPaper\(\s*\"(?P<name>[^\"]+)\"\s*,\s*(?P<src>[^,\n]*)", re.M)
SAMPLE_SRC_RE = re.compile(r"OcrPaperSamples\.(\w+)")
# golden row = ชื่อใบเป็นอาร์กิวเมนต์แรกของ Val("…") หรืออยู่ในอาร์เรย์ชื่อใบ new[] { "…", … } ที่วน Val(paper, …) —
# ไม่ใช่ literal ตัวพิมพ์เล็กใด ๆ ("ok" · "ambiguous" เป็นค่าของช่อง ไม่ใช่ชื่อใบ)
VAL_RE = re.compile(r"\bVal\(\s*\"([^\"]+)\"")
NAME_ARRAY_RE = re.compile(r"new\s*\[\]\s*\{([^}]*)\}", re.S)


def strip_comments(text):
    """ตัด /* */ และ // (ไม่แตะ // ใน URL เช่น https://) — เหมือน checker อื่นในเรพ"""
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"(?<!:)//[^\n]*", "", text)


def read(path):
    with open(path, encoding="utf-8") as fh:
        return fh.read()


def sample_consts(samples_text):
    return [m.group(1) for m in CONST_RE.finditer(samples_text)]


def corpus_papers(harness_text):
    """คืน list ของ (ชื่อใบ, ชื่อ sample หรือ None เมื่อเป็นข้อความ inline)"""
    out = []
    for m in PAPER_RE.finditer(strip_comments(harness_text)):
        sm = SAMPLE_SRC_RE.search(m.group("src"))
        out.append((m.group("name"), sm.group(1) if sm else None))
    return out


def golden_names(golden_texts):
    """ชื่อใบทุกตัวที่ปรากฏเป็น string literal ในไฟล์ golden (นอกคอมเมนต์)"""
    names = set()
    for t in golden_texts:
        code = strip_comments(t)
        names.update(VAL_RE.findall(code))
        for arr in NAME_ARRAY_RE.findall(code):
            names.update(re.findall(r"\"([^\"]+)\"", arr))
    return names


def etax_document_consts(etax_text):
    """fixture ที่เป็นเอกสาร: raw string หรือ literal ที่กินหลายบรรทัด/มี \\n — ค่าคงที่สั้น ๆ (BOM) ไม่นับ"""
    out = []
    for m in CONST_RE.finditer(etax_text):
        rest = etax_text[m.end():m.end() + 400]
        if rest.startswith('"""'):
            out.append(m.group(1))
            continue
        lit = re.match(r'"(?:[^"\\]|\\.)*"', rest)
        if lit is None:
            continue
        body = lit.group(0)
        after = rest[lit.end():lit.end() + 20].lstrip()
        if "\\n" in body or after.startswith("+"):
            out.append(m.group(1))
    return out


def collect(tests_dir=TESTS):
    """คืน list ของข้อความฟ้อง (ว่าง = ผ่าน)"""
    problems = []
    samples_path = os.path.join(tests_dir, SAMPLES_FILE)
    harness_path = os.path.join(tests_dir, HARNESS_FILE)
    golden_paths = sorted(glob.glob(os.path.join(tests_dir, GOLDEN_GLOB)))
    if not (os.path.isfile(samples_path) and os.path.isfile(harness_path)):
        return [f"ไม่พบ {SAMPLES_FILE}/{HARNESS_FILE} ใน {tests_dir} — glob/ชื่อไฟล์เปลี่ยน?"]
    if not golden_paths:
        return [f"ไม่พบไฟล์ golden ({GOLDEN_GLOB}) ใน {tests_dir}"]

    samples_text = read(samples_path)
    papers = corpus_papers(read(harness_path))
    golden = golden_names(read(g) for g in golden_paths)
    used_samples = {s for _, s in papers if s}

    for c in sample_consts(samples_text):
        if c not in used_samples:
            line = samples_text[:samples_text.index(c)].count("\n") + 1
            problems.append(
                f"Accounting.Tests/{SAMPLES_FILE}:{line}: `OcrPaperSamples.{c}` ไม่อยู่ใน OcrReplayHarness.Corpus"
                " → เพิ่ม `new ReplayPaper(\"<ชื่อใบ>\", OcrPaperSamples." + c + ", …)` แล้วล็อกคำตอบใน OcrReplay*GoldenTests")
    for name, sample in papers:
        if name not in golden:
            src = f"OcrPaperSamples.{sample}" if sample else "ข้อความ inline"
            problems.append(
                f"Accounting.Tests/{HARNESS_FILE}: ใบ \"{name}\" ({src}) ไม่มี golden row ในไฟล์ {GOLDEN_GLOB}"
                " → เพิ่ม Assert ที่อ้าง Val(\"" + name + "\", …) อย่างน้อยหนึ่งช่อง (ทั้งหัวใบและระดับบรรทัด)")

    etax_path = os.path.join(tests_dir, ETAX_FILE)
    if os.path.isfile(etax_path):
        etax_text = read(etax_path)
        others = [p for p in glob.glob(os.path.join(tests_dir, "*.cs"))
                  if os.path.basename(p) != ETAX_FILE]
        corpus = "\n".join(strip_comments(read(p)) for p in others)
        for c in etax_document_consts(etax_text):
            if not re.search(r"(?<![A-Za-z0-9_])EtaxFixtures\." + re.escape(c) + r"(?![A-Za-z0-9_])", corpus):
                line = etax_text[:etax_text.index(c)].count("\n") + 1
                problems.append(
                    f"Accounting.Tests/{ETAX_FILE}:{line}: `EtaxFixtures.{c}` ไม่มีเทสต์ไหนอ้างถึง (นอกคอมเมนต์)"
                    " → เพิ่มเทสต์ที่ล็อกคำตอบของเอกสารนี้ (บรรทัด/ยอด/ส่วนลด) ก่อน commit")
    return problems


# ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

def self_test():
    import shutil
    import tempfile
    tmp = tempfile.mkdtemp()
    ok = True

    def expect(cond, msg):
        nonlocal ok
        print(("✅ " if cond else "❌ ") + msg)
        if not cond:
            ok = False

    try:
        def w(name, text):
            with open(os.path.join(tmp, name), "w", encoding="utf-8") as fh:
                fh.write(text)

        w(SAMPLES_FILE,
          "namespace X;\npublic static class OcrPaperSamples\n{\n"
          "    public const string Covered = \"a\\n\" +\n        \"b\";\n"
          "    public const string NoCorpusRow = \"x\\ny\";\n"
          "    public const string InCorpusNoGolden = \"p\\nq\";\n"
          "    public static readonly decimal[] CoveredLineAmounts = { 1m };\n}\n")
        w(HARNESS_FILE,
          "public static class OcrReplayHarness\n{\n    public static IReadOnlyList<ReplayPaper> Corpus { get; } = new[]\n    {\n"
          "        new ReplayPaper(\"covered-paper\", OcrPaperSamples.Covered,\n            EngineSubTotal: 1m),\n"
          "        new ReplayPaper(\"no-golden-paper\", OcrPaperSamples.InCorpusNoGolden),\n"
          "        new ReplayPaper(\"inline-covered\",\n            \"ใบเสร็จ\\nรวม 1.00\"),\n"
          "        new ReplayPaper(\"inline-no-golden\", \"ใบเสร็จ\\nรวม 2.00\"),\n"
          "        // new ReplayPaper(\"commented-out\", OcrPaperSamples.Nope),\n"
          "        new ReplayPaper(\"ok\", \"ใบที่ชื่อเหมือนค่าช่อง\\nรวม 3.00\"),\n"
          "        new ReplayPaper(\"external-source\", OtherFixtures.SomeText),\n"
          "        new ReplayPaper(\"external-covered\", OtherFixtures.OtherText),\n"
          "    };\n}\n")
        w("OcrReplayGoldenTests.cs",
          "public class OcrReplayGoldenTests\n{\n"
          "    [Fact] public void A() => Assert.Equal(\"1.00\", Val(\"covered-paper\", \"HeaderTotal\"));\n"
          "    [Fact] public void B() => Assert.Equal(\"1.00\", Val(\"inline-covered\", \"HeaderTotal\"));\n"
          "    // Val(\"inline-no-golden\", \"X\")  ← อยู่ในคอมเมนต์ ไม่นับ\n"
          "    [Fact] public void C() => Assert.Equal(\"ok\", Val(\"covered-paper\", \"IntegrityGaps\"));   // \"ok\" เป็นค่าช่อง ไม่ใช่ชื่อใบ\n"
          "    [Fact] public void D() { foreach (var paper in new[] { \"external-covered\" }) Assert.Equal(\"1.00\", Val(paper, \"HeaderTotal\")); }\n"
          "}\n")
        w(ETAX_FILE,
          "public static class EtaxFixtures\n{\n"
          "    public const string Bom = \"\\uFEFF\";\n"
          "    public const string UsedXml = \"\"\"\n<rsm:CrossIndustryInvoice/>\n\"\"\";\n"
          "    public const string OrphanXml = \"\"\"\n<rsm:CrossIndustryInvoice/>\n\"\"\";\n"
          "    public const string CommentOnlyXml = \"<a>\\n</a>\";\n}\n")
        w("EtaxSomethingTests.cs",
          "public class EtaxSomethingTests\n{\n"
          "    [Fact] public void T() { var x = EtaxFixtures.UsedXml; Assert.NotNull(x); }\n"
          "    // EtaxFixtures.CommentOnlyXml ← คอมเมนต์ไม่นับ\n"
          "}\n")

        problems = collect(tmp)
        joined = "\n".join(problems)
        expect("OcrPaperSamples.NoCorpusRow" in joined, "sample ที่ไม่อยู่ใน Corpus ถูกฟ้อง")
        expect("\"no-golden-paper\"" in joined, "ใบใน Corpus ที่ไม่มี golden row ถูกฟ้อง")
        expect("\"inline-no-golden\"" in joined, "ใบ inline ที่ไม่มี golden row ถูกฟ้อง (ชื่อในคอมเมนต์ไม่นับ)")
        expect("commented-out" not in joined, "ReplayPaper ที่ถูกคอมเมนต์ไว้ไม่ถูกนับเป็นใบ")
        expect("Covered" not in joined.replace("CoveredLineAmounts", ""), "sample ที่มีทั้ง Corpus และ golden ไม่ถูกฟ้อง")
        expect("\"covered-paper\"" not in joined and "\"inline-covered\"" not in joined, "ใบที่มี golden row ไม่ถูกฟ้อง")
        expect("EtaxFixtures.OrphanXml" in joined, "e-Tax fixture ที่ไม่มีเทสต์อ้างถูกฟ้อง")
        expect("EtaxFixtures.CommentOnlyXml" in joined, "e-Tax fixture ที่ถูกอ้างแค่ในคอมเมนต์ถูกฟ้อง")
        expect("EtaxFixtures.UsedXml" not in joined, "e-Tax fixture ที่มีเทสต์อ้างไม่ถูกฟ้อง")
        expect("EtaxFixtures.Bom" not in joined, "ค่าคงที่สั้น (BOM) ไม่นับเป็นเอกสาร")
        expect("\"ok\"" in joined, "ใบที่ชื่อเหมือนค่าช่อง (\"ok\") ไม่ผ่านเพราะ literal ค่าช่อง — ต้องมี Val(\"ok\", …) จริง")
        expect("\"external-source\"" in joined, "ใบที่อ้างข้อความจากแหล่งอื่น (ไม่ใช่ OcrPaperSamples) ไม่ถูกข้าม — ไม่มี golden ต้องถูกฟ้อง")
        expect("\"external-covered\"" not in joined, "ใบแหล่งอื่นที่มีชื่อในอาร์เรย์ที่วน Val(paper, …) ถือว่ามี golden row")
        expected_count = 7
        expect(len(problems) == expected_count, f"จำนวนข้อฟ้องตรงที่คาด ({len(problems)}/{expected_count})")
        # ไฟล์จริงของเรพต้องผ่าน (checker ที่ฟ้องผิด = checker ที่พัง)
        real = collect()
        expect(not real, "เรพจริงไม่มีข้อฟ้อง" + ("" if not real else ": " + "; ".join(real)))
        return 0 if ok else 1
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main():
    if "--self-test" in sys.argv:
        return self_test()
    problems = collect()
    print()
    for p in problems:
        print(p)
    samples = len(sample_consts(read(os.path.join(TESTS, SAMPLES_FILE)))) if os.path.isfile(os.path.join(TESTS, SAMPLES_FILE)) else 0
    papers = len(corpus_papers(read(os.path.join(TESTS, HARNESS_FILE)))) if os.path.isfile(os.path.join(TESTS, HARNESS_FILE)) else 0
    print(f"\nกระดาษตัวอย่าง {samples} ใบ · ใบใน Corpus {papers} ใบ · ที่ไม่มี golden row/ไม่อยู่ใน Corpus {len(problems)} ข้อ")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
