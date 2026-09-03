#!/usr/bin/env python3
"""ชั้นกลางของการรับชำระเงินต้องไม่รั่ว — ชื่อ provider อยู่ได้เฉพาะใน adapter

ที่มา (PAYMENT_GATEWAY_DESIGN.md §7)
    ระบบมี 4 เส้นทางรับเงินแบบสลิปที่ต่างคนต่างเขียน · ถ้าต่อ gateway ทีละทางจะได้
    สำเนา 4 ชุดที่ drift แน่นอน — และเมื่อวันหนึ่งต้องเปลี่ยนเจ้า (หรือเพิ่มเจ้าที่สอง)
    จะต้องไล่แก้ทุกทางเข้าอีกรอบ.  ทุกทางเข้าจึงต้องเดินผ่าน `PaymentIntent` +
    `IPaymentProvider` และ **ไม่รู้จักชื่อเจ้าเลย**

    เกณฑ์ผ่านของเฟส 6 ในเอกสารเขียนไว้ตรง ๆ ว่า: "เขียน adapter ตัวที่สองโดย
    **ไม่แตะ**ทางเข้าและ intent service — ถ้าต้องแก้ไฟล์นอกโฟลเดอร์ adapter
    = abstraction รั่ว"  checker นี้คือสิ่งที่ทำให้ข้อนั้นวัดได้จริง

สิ่งที่ฟ้อง
    1. ไฟล์ **นอก** `Services/Payments/Providers/**` ที่มีร่องรอยการเชื่อมต่อจริงกับเจ้าใด
       (โดเมน API/CDN เช่น `api.omise.co` · prefix ของคีย์ เช่น `pkey_`/`skey_`/`sk_live_`)
       — จับเฉพาะสิ่งที่โผล่โดยบังเอิญไม่ได้ · **ไม่จับการ "เอ่ยชื่อ"** เพราะชื่อเจ้าปรากฏ
       ในตารางคำสำคัญของ OCR/สเตทเมนต์ธนาคารอย่างถูกต้อง (ดูหมายเหตุที่ PROVIDER_TOKENS)
    2. DTO/พารามิเตอร์ที่มีชื่อคล้าย **เลขบัตร/CVV/วันหมดอายุ** ที่ไหนก็ตาม —
       เลขบัตรต้องไม่เคยผ่านเซิร์ฟเวอร์ของเรา (PCI-DSS SAQ-A) มีได้แค่ token

ทำไมเป็น checker ได้
    เป็น **รูปทรงของโค้ด** (มีชื่อนี้อยู่ในไฟล์นี้ไหม) ไม่ใช่ taint — ตอบได้จากตัวบท
    ไม่ต้องรู้ว่าค่ามาจากไหน จึงไม่มี false positive ประเภทที่ทำให้ checker พัง

negative test: `python3 tools/payment_provider_boundary_check.py --self-test`
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Accounting"

ADAPTER_DIR = "Services/Payments/Providers/"

# ── สิ่งที่ฟ้อง: **ร่องรอยของการเชื่อมต่อ** ไม่ใช่การ "เอ่ยชื่อ" ──
#
# รุ่นแรกใช้ชื่อเจ้าเปล่า ๆ (`\bomise\b`, `\bstripe\b`) แล้ว **ฟ้องผิด 16 จุดบนโค้ดที่
# ถูกต้องทั้งหมด**:
#   • `stripe` ใน PdfGenerationService = การสลับสีแถวตาราง (zebra stripe) ไม่เกี่ยว
#     กับการจ่ายเงินเลย
#   • `omise`/`2c2p`/`gbprimepay`/`stripe` ใน SystemOcrKnowledgeSeeder และ
#     BankFlowClassifier = **ตารางคำสำคัญ** สำหรับจำแนกรายการในสเตทเมนต์ธนาคาร
#     ("OMISE PAYOUT" คือเงินโอนจาก gateway → ลงบัญชี 5503) ซึ่งเป็นความรู้เชิงโดเมน
#     ที่ต้องอยู่ตรงนั้น ไม่ใช่การเชื่อมต่อ
# ตามกฎ "checker ที่ฟ้องผิด = checker ที่พังแล้ว ต้องแก้ทันที ห้ามเลี่ยงโค้ด"
# จึงเปลี่ยนมาจับเฉพาะสิ่งที่ **เกิดขึ้นไม่ได้เลยถ้าไม่ได้กำลังเชื่อมต่อจริง**:
# โดเมน API/CDN ของเจ้า และ prefix ของคีย์ — สองอย่างนี้ไม่มีทางโผล่โดยบังเอิญ
PROVIDER_TOKENS = [
    r"api\.omise\.co", r"cdn\.omise\.co", r"vault\.omise\.co",
    r"\bpkey_(test|live)?", r"\bskey_(test|live)?",
    r"\bpkey_", r"\bskey_",
    r"api\.2c2p\.com", r"api\.gbprimepay\.com", r"api\.stripe\.com",
    r"\bsk_(test|live)_", r"\bpk_(test|live)_",
]
RE_PROVIDER = re.compile("|".join(PROVIDER_TOKENS), re.IGNORECASE)

# ชื่อฟิลด์ที่แปลว่า "เรากำลังถือข้อมูลบัตรเอง" — ห้ามมีที่ไหนเลย
RE_CARD_DATA = re.compile(
    r"\b(CardNumber|Pan|PrimaryAccountNumber|Cvv|Cvc|SecurityCode"
    r"|ExpiryMonth|ExpiryYear|ExpMonth|ExpYear)\b")

# ไม่ต้องมีรายการยกเว้นอีกแล้ว — เกณฑ์ปัจจุบันจับเฉพาะโดเมน/คีย์ที่โผล่โดยบังเอิญไม่ได้
ALLOWED_PROVIDER_MENTION: set[str] = set()


def strip_comments_and_strings_keep_lines(text: str) -> str:
    """ตัดคอมเมนต์ออกแต่ **คงสตริงไว้** และคงจำนวนบรรทัด

    ต่างจาก checker อื่น: ที่นี่เราต้องการจับ URL/prefix ที่อยู่ **ในสตริง** ด้วย
    (`"https://api.omise.co"`) จึงตัดเฉพาะคอมเมนต์ · และต้องแยกสถานะในสตริง/
    นอกสตริง ไม่งั้นจะไปกิน `//` ของ `https://` ที่อยู่ในสตริงเอง
    (บทเรียนจาก csp_external_ref_check)
    """
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if c == "/" and nxt == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c == "/" and nxt == "*":
            i += 2
            while i < n - 1 and not (text[i] == "*" and text[i + 1] == "/"):
                if text[i] == "\n":
                    out.append("\n")
                i += 1
            i = min(i + 2, n)
            continue
        if c == '"':
            # คงสตริงไว้ทั้งก้อน แต่ต้องเดินให้ถูกเพื่อไม่ให้ " ในสตริงทำให้หลุด
            if text.startswith('"""', i):
                j = text.find('"""', i + 3)
                j = n if j < 0 else j + 3
                out.append(text[i:j])
                i = j
                continue
            verbatim = i > 0 and text[i - 1] == "@"
            j = i + 1
            while j < n:
                if verbatim:
                    if text[j] == '"':
                        if j + 1 < n and text[j + 1] == '"':
                            j += 2
                            continue
                        j += 1
                        break
                else:
                    if text[j] == "\\":
                        j += 2
                        continue
                    if text[j] == '"' or text[j] == "\n":
                        j += 1
                        break
                j += 1
            out.append(text[i:j])
            i = j
            continue
        out.append(c)
        i += 1
    return "".join(out)


def scan_text(rel: str, text: str):
    findings = []
    code = strip_comments_and_strings_keep_lines(text)
    in_adapter = rel.startswith(ADAPTER_DIR)
    for lineno, line in enumerate(code.splitlines(), start=1):
        if not in_adapter and rel not in ALLOWED_PROVIDER_MENTION:
            m = RE_PROVIDER.search(line)
            if m:
                findings.append((rel, lineno,
                                 f'อ้างชื่อผู้ให้บริการ "{m.group(0)}" นอก {ADAPTER_DIR} '
                                 f'— ทางเข้าต้องไม่รู้จักชื่อเจ้า'))
        m2 = RE_CARD_DATA.search(line)
        if m2:
            findings.append((rel, lineno,
                             f'มีฟิลด์ข้อมูลบัตร "{m2.group(0)}" — เลขบัตรต้องไม่ผ่านเซิร์ฟเวอร์ '
                             f'(PCI-DSS SAQ-A) ใช้ token จากสคริปต์ของ provider เท่านั้น'))
    return findings


def scan_repo():
    findings = []
    for path in sorted(SRC.rglob("*.cs")):
        rel = path.relative_to(SRC).as_posix()
        findings.extend(scan_text(rel, path.read_text(encoding="utf-8", errors="replace")))
    return findings


GOOD_ADAPTER = '''
public class OmisePaymentProvider : IPaymentProvider
{
    private const string BaseUrl = "https://api.omise.co";
    public string ProviderCode => "omise";
}
'''

GOOD_ENTRY = '''
public class SomeController
{
    // ทางเข้าไม่รู้จักชื่อเจ้า — ขอผ่านชั้นกลางอย่างเดียว
    public Task Pay() => _intents.StartAsync(companyId, request);
}
'''

# เคสที่ **เคยฟ้องผิด** — ต้องเงียบตลอดไป (regression guard)
GOOD_INNOCENT_MENTIONS = '''
public class Renderer
{
    // สลับสีแถวตาราง ไม่เกี่ยวกับการจ่ายเงิน
    void Compose(Color stripe) => ComposeItemsTable(col, doc, stripe);
    // ตารางคำสำคัญสำหรับจำแนกรายการในสเตทเมนต์ธนาคาร
    static readonly string[] Keywords = { "omise", "2c2p", "gbprimepay", "stripe" };
}
'''

BAD_ENTRY = '''
public class LeakyController
{
    private const string Url = "https://api.omise.co/charges";
    public string CardNumber { get; set; }
}
'''


def self_test() -> int:
    ok = True
    good_adapter = scan_text(ADAPTER_DIR + "OmisePaymentProvider.cs", GOOD_ADAPTER)
    if good_adapter:
        ok = False
        print("❌ negative test ล้ม: ฟ้อง adapter ที่อยู่ในโฟลเดอร์ที่อนุญาต")
        for f in good_adapter:
            print("   ", f)

    good_entry = scan_text("Controllers/SomeController.cs", GOOD_ENTRY)
    if good_entry:
        ok = False
        print("❌ negative test ล้ม: ฟ้องทางเข้าที่เขียนถูกต้อง")
        for f in good_entry:
            print("   ", f)

    innocent = scan_text("Services/Implementations/Renderer.cs", GOOD_INNOCENT_MENTIONS)
    if innocent:
        ok = False
        print("❌ negative test ล้ม: ฟ้องการเอ่ยชื่อที่ไม่ใช่การเชื่อมต่อ (บั๊กของ checker รุ่นแรก)")
        for f in innocent:
            print("   ", f)

    bad = scan_text("Controllers/LeakyController.cs", BAD_ENTRY)
    if len(bad) != 2:
        ok = False
        print(f"❌ negative test ล้ม: ควรจับได้ 2 จุด (ชื่อเจ้า + เลขบัตร) แต่จับได้ {len(bad)}")
        for f in bad:
            print("   ", f)

    if ok:
        print("✅ negative test ผ่าน")
    return 0 if ok else 1


def main() -> int:
    if "--self-test" in sys.argv:
        return self_test()
    findings = scan_repo()
    if not findings:
        print("✅ payment_provider_boundary_check: ชั้นกลางไม่รั่ว · ไม่มีข้อมูลบัตรในเซิร์ฟเวอร์")
        return 0
    print(f"❌ payment_provider_boundary_check: พบ {len(findings)} จุด")
    for rel, lineno, msg in findings:
        print(f"  Accounting/{rel}:{lineno}  {msg}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
