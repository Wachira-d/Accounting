#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ค่าตั้งที่ "เก็บ + echo ครบ" แต่ไม่มีใครอ่านไปใช้ — ratchet กับ baseline (รอบ 193 ทีม W · ข้อเสนอ §5 ของ
erp-review/2026-09-24/audit-settings.md)

═══ ที่มา (บั๊กจริง) ═══
ทีมตรวจ S พบค่าตั้งที่ผู้ใช้ตั้งได้บนหน้าจอแต่ไม่มีผลเลย 18 ตัว (`AutoCloseMonthEnd` · `InvoiceNotes` ·
`EtaxByEmailAutoSendOnApprove` · platform `MaintenanceMode` ฯลฯ) และแบบ API-only อีก ~65 ตัว · การตรวจ
round-trip ของรอบก่อน "ผ่านหมด" เพราะวัดแค่ว่า ฟอร์ม → DTO → entity → response → hydrate ครบ (echo) ไม่ได้วัดว่า
**มีโค้ดตัวไหนอ่านค่าไปตัดสินอะไร** ⇒ "เก็บ+echo ครบ ≠ มีผล" (ญาติของ F2 ข้อ 2 "มี ≠ ถูกเรียก")

═══ กติกา ═══
property ค่าตั้งของ entity ที่อยู่ใน `ENTITIES` ต้องมี **ผู้อ่านอย่างน้อย 1 จุด** นอก:
  * ไฟล์ entity (`Models/Entities`) · DTO (`Models/DTOs`) · DbContext · migration helper
  * บรรทัดที่ "เขียน" ค่านั้น (`x.P = …` · `P = …` ใน object initializer)
  * การอ่านจากตัวแปรคำขอ (`request.P` · `req.P` · `dto.P` …) — ชื่อเดียวกันแต่เป็นของ DTO ไม่ใช่ของ entity
  * การ echo ออก response (ภายใน `new XxxResponse(…)`/`new XxxDto(…)`/`new XxxSummary(…)` · anonymous `new { … }` ·
    target-typed `new(…)` ของ mapper)
  * หน้าเว็บที่เป็น "หน้าแก้ค่านั้นเอง" (ช่องที่ 3 ของ `ENTITIES`) — hydrate ฟอร์มไม่ใช่การใช้ค่า
ผู้อ่านที่นับ: C# `x.P` / `x?.P` / property pattern `is { P: … }` · JS `.p` (camelCase) ในหน้าที่ไม่ใช่หน้าแก้ ·
  object initializer ของชนิดที่ไม่ใช่ echo/entity (`new PricingInput { Mask = s.Mask }` = ส่งค่าเข้าตัวคำนวณ) ·
  raw SQL ที่อ่านคอลัมน์ (`"SELECT \"P\" …"` · `WHERE "P" …`) · ตัวแปรชื่อคำขอ (`model`/`input`/…) ที่ประกาศเป็นชนิด entity
  (ฝ่ายค้านรอบ 193 §3.1 — ฟ้องผิด 3 แบบนี้ถูกปิดแล้ว พร้อมกรณีใน --self-test)

  * baseline (`tools/settings_reader_baseline.txt`) = ของที่ไม่มีผู้อ่าน ณ วันเขียน · **ห้ามเพิ่มแถว** · ตัดออกเมื่อ
    ต่อสาย/ลบ — checker ล้มเมื่อมีตัว**ใหม่**ที่ไม่มีผู้อ่าน (ฟิลด์ค่าตั้งใหม่ที่ไม่ต่อสาย หรือถอดผู้อ่านตัวสุดท้าย) ·
    แถวที่กลับมามีผู้อ่านแล้วถูกรายงาน (ℹ️ ไม่ล้ม — แบบเดียวกับ dead_helper_check) ให้ตัดออกในคอมมิตเดียวกัน
  * over-approximate ฝั่ง "มีผู้อ่าน" ได้ (ชื่อสั้นอย่าง `Name`/`IsActive` แมตช์ที่อื่นได้ = ถือว่ามีผู้อ่าน) ·
    ฝั่ง "ไม่มีผู้อ่าน" ต้องแม่น — checker ที่ฟ้องผิด = checker ที่พัง (F2 ข้อ 6)

ข้อจำกัดที่ต้องรู้: checker ตอบได้แค่ "มีผู้อ่านไหม" ไม่ได้ตอบ "อ่านครบทุกทางเข้าไหม" (S-01 สถานะ VAT สองธง ·
S-02 ค่าตั้งที่บังคับแค่ใน ApproveDocumentAsync) — กลุ่มนั้นต้องพึ่ง resolver กลาง + เทสต์ ไม่ใช่ checker นี้

ใช้: python3 tools/settings_reader_check.py [--self-test] [--write-baseline] [--all]
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BASELINE = os.path.join(ROOT, 'tools', 'settings_reader_baseline.txt')
SKIP_DIRS = {'bin', 'obj', 'node_modules', '.git', '.claude'}

# entity → (ไฟล์, ชุด property ที่นับ (None = ทุกตัวที่เป็นค่าตั้ง), หน้าเว็บที่เป็นหน้าแก้ค่าของ entity นี้)
ENTITIES = {
    'CompanySettings': ('Accounting/Models/Entities/CompanySettings.cs', None,
                        ['Accounting/wwwroot/pages/settings.html']),
    # Company: เฉพาะส่วนที่เป็น "ค่าตั้ง" (ธงทะเบียน/ภาษี/รอบบัญชี) — ชื่อ/ที่อยู่/ติดต่อเป็นข้อมูลทะเบียน ไม่ใช่ค่าตั้ง
    'Company': ('Accounting/Models/Entities/Company.cs',
                {'IsVatRegistered', 'VatRate', 'IsRetailApproved', 'PhoR06ApprovedDate', 'PaidUpCapital',
                 'IsWhtRegistered', 'IsSocialSecurityRegistered', 'SocialSecurityAccountNo',
                 'FiscalYearStartMonth', 'BaseCurrency', 'BusinessType', 'IndustryType'},
                ['Accounting/wwwroot/pages/settings.html', 'Accounting/wwwroot/pages/company.html']),
    'SiteSettings': ('Accounting/Models/Entities/SiteSettings.cs', None,
                     ['Accounting/wwwroot/admin/site-settings.html', 'Accounting/wwwroot/admin/ocr-config.html',
                      'Accounting/wwwroot/admin/ai-config.html', 'Accounting/wwwroot/admin/sso-config.html',
                      'Accounting/wwwroot/admin/system-email.html', 'Accounting/wwwroot/pages/admin-ocr.html']),
    'LodgingProperty': ('Accounting/Models/Entities/Lodging.cs', None,
                        ['Accounting/wwwroot/pages/lodging-settings.html']),
    'SiteCommerceConfig': ('Accounting/Models/Entities/CmsSite.cs', None, []),
    'DocumentTemplate': ('Accounting/Models/Entities/DocumentTemplate.cs', None,
                         ['Accounting/wwwroot/pages/document-templates.html']),
    'TrialConfig': ('Accounting/Models/Entities/Subscription.cs', None, ['Accounting/wwwroot/admin/plans.html']),
    # payroll.html = หน้าแก้ตารางภาษีเงินได้ (แท็บ tax-rule) — ช่องที่แสดงค่าแบบล็อกไว้ไม่ใช่ผู้อ่าน (ฝ่ายค้านรอบ 193 W-C6)
    'TaxRuleConfig': ('Accounting/Models/Entities/Payroll.cs', None, ['Accounting/wwwroot/pages/payroll.html']),
}

# property ที่ไม่ใช่ "ค่าตั้ง" ของทุก entity (กุญแจ · audit · navigation)
NOT_SETTINGS = {'Id', 'CompanyId', 'Company', 'CreatedAt', 'UpdatedAt', 'CreatedBy', 'UpdatedBy', 'IsDeleted',
                'SiteId', 'Site', 'SubscriptionId', 'Subscription', 'PropertyId', 'BranchId', 'Branch'}

# property "สถานะ" ที่ระบบเขียนเอง (ผลทดสอบการเชื่อมต่อ ฯลฯ) — ไม่ใช่ค่าที่ผู้ใช้ตั้ง จึงไม่อยู่ในขอบเขต
STATUS_PROP_RE = re.compile(r'(LastTestedAt|LastTestStatus|Configured|LastFeedbackTrainingAt)$')

# ไฟล์ที่ไม่นับเป็นผู้อ่าน (entity · DTO · DbContext · migration)
EXCLUDED_CS = re.compile(r'(^Accounting/Models/Entities/|^Accounting/Models/DTOs/|'
                         r'^Accounting/Data/AccountingDbContext[^/]*\.cs$|^Accounting/Data/DatabaseMigrationHelper[^/]*\.cs$|'
                         r'/Migrations/)')

# ตัวแปรที่ถือ "คำขอ" (DTO) — ชื่อ property ซ้ำกับ entity แต่ไม่ใช่การอ่านค่าตั้ง
REQUEST_RECEIVERS = {'request', 'req', 'dto', 'body', 'payload', 'input', 'model', 'cmd', 'command', 'update', 'upd'}

# ชนิดที่สร้างเพื่อ "ส่งออก" (echo) — ค่าที่อยู่ในนั้นไม่ใช่การใช้ค่า
# `new XResponse(` · `new XResponse {` · `new XResponse() {`
ECHO_TYPE_RE = re.compile(r'new\s+[\w.]*(Response|Dto|DTO|Summary)\s*(<[^<>]*>)?\s*(\(\s*\))?\s*$')
# anonymous `new {`
ANON_NEW_RE = re.compile(r'new\s*$')
# target-typed `new(` / `new() {` — เป็น echo **เฉพาะเมื่อ**เมธอดที่ครอบคืนชนิด Response/Dto/Summary (mapper) ·
# ถ้าไม่ใช่ (เช่น `InputFor(...) => new(… Property.WeekendDaysMask …)` ส่งค่าเข้า LodgingPricingEngine) = ผู้อ่านจริง
TARGET_NEW_RE = re.compile(r'new\s*(\(\s*\))?\s*$')
METHOD_SIG_RE = re.compile(r'([\w<>\[\],.?]+)\s+\w+\s*\([^;{}]*\)\s*(?:=>|\{)')
ECHO_RETURN_RE = re.compile(r'(Response|Dto|DTO|Summary)(<[^<>]*>)?\??>?\??$')

PROP_RE = re.compile(r'public\s+(?:virtual\s+|required\s+)*([\w<>\[\]?,. ]+?)\s+(\w+)\s*\{\s*get;')
NAV_TYPE_RE = re.compile(r'^(ICollection|List|IList|IEnumerable|HashSet)<')


def strip_comments(text):
    """ตัด // และ /* */ (คงจำนวนบรรทัด) โดยไม่ตีความเครื่องหมายในสตริงเป็นคอมเมนต์"""
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        nx = text[i + 1] if i + 1 < n else ''
        if c == '/' and nx == '/':
            j = text.find('\n', i)
            i = n if j < 0 else j
            continue
        if c == '/' and nx == '*':
            j = text.find('*/', i + 2)
            seg = text[i:(n if j < 0 else j + 2)]
            out.append('\n' * seg.count('\n'))
            i = n if j < 0 else j + 2
            continue
        if c == '"' or c == "'":
            verbatim = c == '"' and i > 0 and text[i - 1] == '@'
            j = i + 1
            while j < n:
                if verbatim:
                    if text[j] == '"':
                        if j + 1 < n and text[j + 1] == '"':
                            j += 2
                            continue
                        break
                else:
                    if text[j] == '\\':
                        j += 2
                        continue
                    if text[j] == c or text[j] == '\n':
                        break
                j += 1
            out.append(text[i:j + 1])
            i = j + 1
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def class_body(text, cls):
    m = re.search(r'\b(class|record)\s+' + cls + r'\b[^{;]*\{', text)
    if not m:
        return ''
    i, depth = m.end() - 1, 0
    for j in range(i, len(text)):
        if text[j] == '{':
            depth += 1
        elif text[j] == '}':
            depth -= 1
            if depth == 0:
                return text[i + 1:j]
    return text[i + 1:]


def entity_class_names(root):
    """ชื่อคลาส entity ทั้งหมด — property ที่ชนิดเป็น entity = navigation (ค่าตั้งคือ FK `…Id` ไม่ใช่ตัว navigation)"""
    names = set()
    d = os.path.join(root, 'Accounting/Models/Entities')
    if os.path.isdir(d):
        for fn in os.listdir(d):
            if fn.endswith('.cs'):
                with open(os.path.join(d, fn), encoding='utf-8', errors='replace') as fh:
                    names.update(re.findall(r'\bclass\s+(\w+)', fh.read()))
    return names


def entity_props(root, cls, rel, only, entity_names=frozenset()):
    with open(os.path.join(root, rel), encoding='utf-8') as fh:
        body = class_body(strip_comments(fh.read()), cls)
    # เฉพาะ property ระดับบนของคลาส (ไม่รวมคลาสซ้อน)
    props = []
    depth = 0
    for line in body.split('\n'):
        if depth == 0:
            m = PROP_RE.search(line)
            if m:
                typ, name = m.group(1).strip(), m.group(2)
                if (name not in NOT_SETTINGS and not STATUS_PROP_RE.search(name) and not NAV_TYPE_RE.match(typ)
                        and typ.rstrip('?') not in entity_names and (only is None or name in only)):
                    props.append((name, typ))
        depth += line.count('{') - line.count('}')
        # `{ get; set; }` บนบรรทัดเดียวกันหักล้างกันเอง
    return props


def load_corpus(root):
    cs, web = {}, {}
    for base in ('Accounting',):
        for dp, dns, fns in os.walk(os.path.join(root, base)):
            dns[:] = [d for d in dns if d not in SKIP_DIRS]
            for fn in fns:
                path = os.path.join(dp, fn)
                rel = os.path.relpath(path, root).replace(os.sep, '/')
                if fn.endswith('.cs'):
                    if EXCLUDED_CS.search(rel):
                        continue
                    with open(path, encoding='utf-8', errors='replace') as fh:
                        cs[rel] = strip_comments(fh.read())
                elif fn.endswith(('.js', '.html')) and '/wwwroot/' in '/' + rel:
                    with open(path, encoding='utf-8', errors='replace') as fh:
                        web[rel] = fh.read()
    return cs, web


def enclosing_opener(text, pos):
    """หาเครื่องหมายเปิด ( { [ ที่ยังไม่ปิดซึ่งครอบตำแหน่ง pos · คืน (อักขระ, ข้อความก่อนหน้า 120 ตัว)"""
    depth = {'(': 0, '{': 0, '[': 0}
    pairs = {')': '(', '}': '{', ']': '['}
    i = pos - 1
    while i >= 0:
        c = text[i]
        if c in pairs:
            depth[pairs[c]] += 1
        elif c in depth:
            if depth[c] == 0:
                return c, text[max(0, i - 120):i], i
            depth[c] -= 1
        elif c == ';' and all(v == 0 for v in depth.values()):
            return None, '', -1
        i -= 1
    return None, '', -1


def is_echo(text, pos):
    opener, before, at = enclosing_opener(text, pos)
    if opener is None:
        return False
    before = before.rstrip()
    if opener in '({' and ECHO_TYPE_RE.search(before):
        return True
    if opener == '{' and ANON_NEW_RE.search(before):                 # anonymous new { … }
        return True
    if opener in '({' and TARGET_NEW_RE.search(before):              # target-typed — ดูชนิดที่เมธอดคืน
        # ลายเซ็นเมธอดอยู่ "ก่อน new(" ไม่ใช่ก่อนตำแหน่งที่อ่าน (mapper ยาวหลายสิบบรรทัด)
        window = text[max(0, at - 400):at]
        sigs = list(METHOD_SIG_RE.finditer(window))
        return bool(sigs) and bool(ECHO_RETURN_RE.search(sigs[-1].group(1)))
    return False


_RECV_RE = re.compile(r'(\w+)\s*\??\s*$')


# ชื่อคลาส entity (ตั้งใน analyse) — ใช้แยก "ตัวแปรชื่อคำขอแต่ชนิด entity" และ "initializer ที่ clone entity"
_ENTITY_NAMES = set()
_NEW_TYPE_RE = re.compile(r'new\s+([\w.]+)\s*(?:<[^<>]*>)?\s*(?:\(\s*\))?\s*$')
# raw SQL ที่ "อ่าน" คอลัมน์: ชื่อคอลัมน์ในเครื่องหมายคำพูด (\"P\" · ""P"" · "P" ใน raw string) โดยมีคำสั่งอ่านอยู่ก่อนหน้า
_SQL_READ_KW = re.compile(r'\b(SELECT|WHERE|JOIN|RETURNING|ORDER\s+BY|GROUP\s+BY|CASE\s+WHEN)\b')  # ตัวใหญ่ = SQL ไม่ใช่ .Where(


def _receiver_is_entity(text, recv):
    """ตัวแปรชื่อคำขอ (`model`/`input`/…) ที่ประกาศในไฟล์เป็นชนิด entity = ของ entity ไม่ใช่ของคำขอ"""
    for m in re.finditer(r'\b([A-Z]\w*)\??\s+' + recv + r'\b\s*[,)=;]', text):
        if m.group(1) in _ENTITY_NAMES:
            return True
    return False


def _initializer_target(text, pos):
    """ถ้า pos อยู่ใน object initializer `new T { … }` คืนชื่อ T (ไม่ใช่ = None)"""
    opener, before, _at = enclosing_opener(text, pos)
    if opener != '{':
        return None
    m = _NEW_TYPE_RE.search(before.rstrip())
    return m.group(1).split('.')[-1] if m else None


def cs_reads(text, name):
    """ตำแหน่งที่อ่าน property `name` ในไฟล์ C# ที่ผ่านการตัดคอมเมนต์แล้ว (generator — ผู้เรียกหยุดที่ตัวแรกได้)"""
    for m in re.finditer(r'\.\s*' + name + r'\b(?!\s*(?:=(?![=>])|\+=|-=|\?\?=))(?!\s*\()', text):
        rm = _RECV_RE.search(text[max(0, m.start() - 40):m.start()])
        if rm and rm.group(1) in REQUEST_RECEIVERS and not _receiver_is_entity(text, rm.group(1)):
            continue
        before = text[max(0, m.start() - 160):m.start()]
        # คัดลอกผ่านแบบคำสั่ง (`x.P = y.P`) — ค่าไหลจากคำขอ/entity หนึ่งไปอีก entity ไม่ได้ถูกใช้ตัดสินอะไร
        # (`p.AutoConfirmOnDeposit = d.AutoConfirmOnDeposit` ใน LodgingService คือตัวเขียน ไม่ใช่ผู้อ่าน)
        if re.search(r'\.\s*' + name + r'\s*=(?![=>])\s*\(?\s*\w+\s*\??\s*$', before):
            continue
        # คัดลอกผ่านแบบ initializer (`P = y.P`) — ไม่นับเฉพาะเมื่อเป้าหมายเป็น entity (clone) · echo ให้ is_echo ตัดสิน ·
        # ชนิดอื่น (input ของตัวคำนวณ) = ผู้อ่านจริง (ฝ่ายค้านรอบ 193: `new PricingInput { WeekendMask = s.WeekendMask }`)
        if re.search(r'(?<![.\w])' + name + r'\s*=(?![=>])\s*\(?\s*\w+\s*\??\s*$', before):
            target = _initializer_target(text, m.start())
            if target is not None and target in _ENTITY_NAMES:
                continue
        if is_echo(text, m.start()):
            continue
        yield m.start()
    # raw SQL (`"SELECT \"P\" …"`) — มองไม่เห็นด้วยกฎ `.P` (ฝ่ายค้านรอบ 193)
    for m in re.finditer(r'(?:\\"|"")?"?' + name + r'(?:\\"|"")"?|"' + name + r'"', text):
        window = text[max(0, m.start() - 300):m.start()]
        window = window[window.rfind(';') + 1:]            # เฉพาะคำสั่งเดียวกัน (UPDATE … SET "P" ถัดจาก SELECT ไม่นับ)
        if _SQL_READ_KW.search(window):
            yield m.start()
    # property pattern: `is { P: … }` / `is X { P: … }` / `, P: …` ภายใน pattern
    for m in re.finditer(r'\bis\s+(?:not\s+)?(?:[\w.]+\s*)?\{[^{}]*\b' + name + r'\s*:', text):
        yield m.start()


def web_reads(text, name):
    camel = name[0].lower() + name[1:]
    for m in re.finditer(r'\.' + camel + r'\b(?!\s*=(?!=))', text):
        yield m.start()


def analyse(root, cs=None, web=None):
    if cs is None or web is None:
        cs, web = load_corpus(root)
    unread = []
    entity_names = entity_class_names(root)
    _ENTITY_NAMES.clear()
    _ENTITY_NAMES.update(entity_names)
    for cls, (rel, only, editors) in ENTITIES.items():
        if not os.path.exists(os.path.join(root, rel)):
            continue
        editors = set(editors)
        for name, _typ in entity_props(root, cls, rel, only, entity_names):
            found = any(next(cs_reads(t, name), None) is not None for t in cs.values() if name in t)
            if not found:
                camel = name[0].lower() + name[1:]
                found = any(next(web_reads(t, name), None) is not None
                            for f, t in web.items() if f not in editors and camel in t)
            if not found:
                unread.append(f'{cls}.{name}')
    return sorted(set(unread))


def read_baseline(path):
    if not os.path.exists(path):
        return set()
    with open(path, encoding='utf-8') as fh:
        return {l.strip() for l in fh if l.strip() and not l.startswith('#')}


def write_baseline(path, unread):
    with open(path, 'w', encoding='utf-8') as fh:
        fh.write('# settings_reader_check baseline — property ค่าตั้งที่ยังไม่มีผู้อ่าน (นอก entity/DTO/mapper/echo) ณ วันเขียน\n')
        fh.write('# ห้ามเพิ่มแถวเพื่อให้ checker เขียว — ตัดออกเมื่อ "ต่อสาย หรือ ลบ" แล้ว (คำตัดสินเจ้าของ Q3 ของ audit-settings)\n')
        for k in unread:
            fh.write(k + '\n')


SELF_TEST_ENTITY = '''namespace X;
public class CompanySettings : TenantEntity
{
    public bool WiredFlag { get; set; }
    public bool EchoOnly { get; set; }
    public bool PatternRead { get; set; }
    public string? JsOnly { get; set; }
    public bool RequestOnly { get; set; }
    public int TargetTypedBiz { get; set; }
    public int TargetTypedEcho { get; set; }
    public bool CopyOnly { get; set; }
    public int InitIntoCalc { get; set; }
    public int CloneOnly { get; set; }
    public bool SqlRead { get; set; }
    public bool SqlWriteOnly { get; set; }
    public bool ModelEntityRead { get; set; }
    public Thing? Nav { get; set; }
    public ICollection<Thing> Things { get; set; } = new List<Thing>();
}
public class Thing
{
}
'''
SELF_TEST_SVC = '''class S {
    CompanySettingsResponse Map(CompanySettings s) => new CompanySettingsResponse(
        s.EchoOnly, s.WiredFlag);
    object Anon(CompanySettings s) => new { echo = s.EchoOnly, js = s.JsOnly };
    void Update(CompanySettings settings, Req request) {
        if (request.RequestOnly) settings.RequestOnly = request.RequestOnly;
        settings.EchoOnly = true;
        settings.JsOnly = "x";
    }
    bool Use(CompanySettings s) => s.WiredFlag && x;
    bool Pat(CompanySettings? s) => s is { PatternRead: true };
    PricingInput InputFor(CompanySettings p) => new(
        1, 2, p.TargetTypedBiz);
    private static SettingsResponse MapIt(CompanySettings p) => new(
        p.TargetTypedEcho);
    void Copy(CompanySettings p, Other d) { p.CopyOnly = d.CopyOnly; }
    PricingInput Calc(CompanySettings s) => new PricingInput { Mask = 1, InitIntoCalc = s.InitIntoCalc };
    CompanySettings Clone(CompanySettings s) => new CompanySettings { CloneOnly = s.CloneOnly };
    string Sql = "SELECT \\"SqlRead\\" FROM \\"CompanySettings\\" WHERE 1=1";
    string Sql2 = "UPDATE \\"CompanySettings\\" SET \\"SqlWriteOnly\\" = true";
    bool M(CompanySettings model) => model.ModelEntityRead;
    // s.EchoOnly ในคอมเมนต์ไม่นับ
}
'''
SELF_TEST_WEB_EDITOR = "document.getElementById('a').checked = s.echoOnly; s.requestOnly;"
SELF_TEST_WEB_OTHER = "if (settings.jsOnly) show();"


def self_test():
    import tempfile
    ok = True
    with tempfile.TemporaryDirectory() as td:
        os.makedirs(os.path.join(td, 'Accounting/Models/Entities'))
        with open(os.path.join(td, 'Accounting/Models/Entities/CompanySettings.cs'), 'w', encoding='utf-8') as fh:
            fh.write(SELF_TEST_ENTITY)
        saved = dict(ENTITIES)
        ENTITIES.clear()
        ENTITIES['CompanySettings'] = ('Accounting/Models/Entities/CompanySettings.cs', None,
                                       ['Accounting/wwwroot/pages/settings.html'])
        try:
            cs = {'Accounting/Services/S.cs': strip_comments(SELF_TEST_SVC)}
            web = {'Accounting/wwwroot/pages/settings.html': SELF_TEST_WEB_EDITOR,
                   'Accounting/wwwroot/pages/documents.html': SELF_TEST_WEB_OTHER}
            got = analyse(td, cs, web)
            # TargetTypedBiz = อ่านจริง (ส่งเข้า target-typed new ของ input ธุรกิจ — บั๊กของ checker ที่เจอตอนเขียน:
            # LodgingProperty.WeekendDaysMask ถูกฟ้องผิด) · Nav = navigation ไม่ใช่ค่าตั้ง
            # ฝ่ายค้านรอบ 193 §3.1: InitIntoCalc (initializer ของ input) · SqlRead (raw SQL) · ModelEntityRead
            # (ตัวแปรชื่อ model ชนิด entity) = ผู้อ่านจริง · CloneOnly (initializer ของ entity) · SqlWriteOnly (UPDATE) = ไม่ใช่
            want = ['CompanySettings.CloneOnly', 'CompanySettings.CopyOnly', 'CompanySettings.EchoOnly',
                    'CompanySettings.RequestOnly', 'CompanySettings.SqlWriteOnly', 'CompanySettings.TargetTypedEcho']
            if got != want:
                print(f'❌ self-test: คาด {want} ได้ {got}')
                ok = False
            # negative: ถอดผู้อ่านตัวเดียวของ WiredFlag ⇒ ต้องฟ้อง
            cs2 = {k: v.replace('s.WiredFlag && x', 'x') for k, v in cs.items()}
            if 'CompanySettings.WiredFlag' not in analyse(td, cs2, web):
                print('❌ self-test: ถอดผู้อ่านแล้วไม่ฟ้อง')
                ok = False
            # JS ในหน้าแก้เองไม่นับ — ย้ายการอ่าน jsOnly ไปหน้าแก้ ⇒ ต้องฟ้อง
            web2 = {'Accounting/wwwroot/pages/settings.html': SELF_TEST_WEB_EDITOR + SELF_TEST_WEB_OTHER}
            if 'CompanySettings.JsOnly' not in analyse(td, cs, web2):
                print('❌ self-test: การอ่านในหน้าแก้ค่าเองถูกนับเป็นผู้อ่าน')
                ok = False
        finally:
            ENTITIES.clear()
            ENTITIES.update(saved)
    return ok


def real_negative_test(root):
    """ข้อกำหนดของงาน: ลบผู้อ่านของ `AutoAttachWhtCertPdf` ในเรพจริงแล้วต้องฟ้อง"""
    cs, web = load_corpus(root)
    if 'CompanySettings.AutoAttachWhtCertPdf' in analyse(root, cs, web):
        print('❌ negative test: AutoAttachWhtCertPdf ควรมีผู้อ่านในเรพ (WithholdingTaxCertService) แต่ไม่พบ')
        return False
    cs2 = {k: re.sub(r'\.\s*AutoAttachWhtCertPdf\b', '.Removed_AutoAttach', v) if 'Services/' in k else v
           for k, v in cs.items()}
    web2 = {k: v.replace('.autoAttachWhtCertPdf', '.removed') for k, v in web.items()}
    if 'CompanySettings.AutoAttachWhtCertPdf' not in analyse(root, cs2, web2):
        print('❌ negative test: ถอดผู้อ่านของ AutoAttachWhtCertPdf แล้ว checker ไม่ฟ้อง')
        return False
    return True


def main(argv):
    if '--self-test' in argv:
        ok = self_test() and real_negative_test(ROOT)
        print('✅ self-test ผ่าน (ถอดผู้อ่าน=ฟ้อง · echo/คำขอ/เขียน/หน้าแก้=ไม่นับ · negative จริง AutoAttachWhtCertPdf)'
              if ok else '❌ self-test ล้ม')
        return 0 if ok else 1
    if not self_test():
        print('❌ settings_reader_check: self-test ของตัวเองล้ม — checker ไม่น่าเชื่อถือ')
        return 1
    unread = analyse(ROOT)
    if '--write-baseline' in argv:
        write_baseline(BASELINE, unread)
        print(f'เขียน baseline {len(unread)} แถว → {os.path.relpath(BASELINE, ROOT)}')
        return 0
    baseline = read_baseline(BASELINE)
    new = [k for k in unread if k not in baseline]
    revived = sorted(k for k in baseline if k not in unread)
    if '--all' in argv:
        for k in unread:
            print(f'  {k}{"" if k in baseline else "  ← ใหม่"}')
    rc = 0
    if new:
        print('❌ ค่าตั้งที่ไม่มีผู้อ่าน (ใหม่ — ไม่อยู่ใน baseline):')
        for k in new:
            print(f'   {k}')
        print('   → ต่อสายให้มีโค้ดอ่านค่าไปใช้จริง (ไม่ใช่แค่ echo ออก response) หรือถ้ายังไม่รองรับ: ล็อกช่องบนหน้า + ป้าย '
              '"ยังไม่รองรับ" แล้วถามเจ้าของ "ต่อสาย หรือ ลบ" — ห้ามเพิ่มแถว baseline เพื่อให้เขียว')
        rc = 1
    for k in revived:
        # ไม่ล้ม (แบบเดียวกับ dead_helper_check) — ทีมที่ต่อสายไม่ควรถูกกันไม่ให้ merge · แต่ควรตัดแถวออกในคอมมิตเดียวกัน
        print(f'ℹ️  กลับมามีผู้อ่านแล้ว — ตัดออกจาก tools/settings_reader_baseline.txt ได้: {k}')
    if rc == 0:
        print(f'✅ ค่าตั้งทุกตัวนอก baseline มีผู้อ่าน (baseline {len(baseline)} แถวรอตัดสิน "ต่อสาย หรือ ลบ")')
    return rc


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
