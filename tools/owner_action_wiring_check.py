#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ด่าน "เจ้าของเท่านั้น / คีย์ห้าม" ต้องอยู่ที่จุดเรียกจริง — ล็อก call site ของรอบ 193 ทีม W (หลังฝ่ายค้าน)

═══ ที่มา ═══
ฝ่ายค้านรอบ 193 (erp-review/2026-09-24/review193-W.md): เทสต์ 7 ไฟล์ของทีม W เรียกแค่ helper
(`OwnerActionGuard` · `ApiAccessPolicy` · `RegistrationPolicy` · ฯลฯ) ⇒ **ถอดบรรทัดที่เรียก helper ออกจาก controller/
middleware/service แล้วเทสต์ยังเขียวทุกตัว** = ด่านที่ไม่มีอะไรฟ้องเมื่อหาย (F2 ข้อ 2 "มี ≠ ถูกเรียก" · ข้อ 6)
env นี้ไม่มี .NET SDK จึงเขียน integration test ไม่ได้ — checker นี้ล็อก "ด่านอยู่ในเมธอดที่ต้องอยู่" แบบแคบ (รายชื่อตายตัว
ต่อไฟล์/เมธอด · ไม่กวาดทั้งเรพ — F4 ข้อ 3)

═══ กติกา ═══
แต่ละแถวของ RULES = (ไฟล์, เมธอด, ชนิด, สิ่งที่ต้องมี):
  * 'attr' — attribute เหนือเมธอด (action) ต้องมี pattern (เช่น `[RejectApiKey(…)]` บนเส้นทำลายหลักฐาน/ตั้งนโยบาย)
  * 'body' — เนื้อเมธอดต้องมี pattern (เช่น `RequireOwnerAsync(` · `OwnerActionGuard.EnsureNotApiKey(`)
เมธอดที่หาไม่เจอ หรือมีชื่อซ้ำหลายตัวในไฟล์ = ล้ม (ให้คนแก้แถวนี้ ไม่ใช่เงียบ) · คอมเมนต์ถูกตัดก่อนตรวจ (ด่านในคอมเมนต์ไม่นับ)

เพิ่มแถวได้เมื่อเพิ่มด่านใหม่ · **ห้ามลบแถวเพื่อให้เขียว** — ถ้าด่านถูกย้ายโดยตั้งใจ ให้แก้แถวให้ชี้ที่ใหม่ในคอมมิตเดียวกัน

ใช้: python3 tools/owner_action_wiring_check.py [--self-test]
  --self-test = ถอดด่านทีละแถวออกจากไฟล์จริง (ในหน่วยความจำ) แล้วต้องฟ้องทุกแถว + ด่านในคอมเมนต์ต้องไม่นับ
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
C = 'Accounting/Controllers/'
S = 'Accounting/Services/Implementations/'
REJECT = r'\bRejectApiKey\s*\('

RULES = [
    # ---- W-C1: เส้นทำลายหลักฐาน — คีย์ห้าม (เจ้าของถือคีย์ก็ห้าม) ----
    (C + 'DocumentController.cs', 'PurgeDocument', 'attr', REJECT),
    (C + 'WithholdingTaxCertController.cs', 'Delete', 'attr', REJECT),
    (C + 'PdpaController.cs', 'DsrErase', 'attr', REJECT),
    # ---- W-C2: endpoint ที่ตั้งนโยบาย/ค่าตั้งของบริษัท ----
    (C + 'SettingsController.cs', 'UpdateSettings', 'attr', REJECT),
    (C + 'SettingsController.cs', 'UploadLogo', 'attr', REJECT),
    (C + 'SettingsController.cs', 'DeleteLogo', 'attr', REJECT),
    (C + 'SettingsController.cs', 'UploadStamp', 'attr', REJECT),
    (C + 'SettingsController.cs', 'DeleteStamp', 'attr', REJECT),
    (C + 'SettingsController.cs', 'CreateNumberSeries', 'attr', REJECT),
    (C + 'SettingsController.cs', 'UpdateNumberSeries', 'attr', REJECT),
    (C + 'EmailConfigController.cs', 'Update', 'attr', REJECT),
    (C + 'EmailConfigController.cs', 'Update', 'attr', r'\bRequirePermission\s*\('),
    (C + 'LineConfigController.cs', 'Update', 'attr', REJECT),
    (C + 'LineConfigController.cs', 'Update', 'attr', r'\bRequirePermission\s*\('),
    (C + 'EtaxController.cs', 'UpdateConfig', 'attr', REJECT),
    (C + 'PayrollController.cs', 'UpsertTaxRuleConfig', 'attr', REJECT),
    (C + 'PayrollController.cs', 'DeleteTaxRuleConfig', 'attr', REJECT),
    # ---- W-C3: แพ็กเกจ/บิลลิ่ง ----
    (C + 'AccountSubscriptionController.cs', 'StartTrial', 'attr', REJECT),
    (C + 'AccountSubscriptionController.cs', 'Attach', 'attr', REJECT),
    (C + 'AccountSubscriptionController.cs', 'Detach', 'attr', REJECT),
    (C + 'SubscriptionController.cs', 'ExtendTrial', 'body', r'\bRequireOwnerAsync\s*\('),
    (C + 'SubscriptionController.cs', 'ConvertTrial', 'body', r'\bRequireOwnerAsync\s*\('),
    (C + 'SubscriptionController.cs', 'ChangePlan', 'body', r'\bRequireOwnerAsync\s*\('),
    (C + 'SubscriptionController.cs', 'Cancel', 'body', r'\bRequireOwnerAsync\s*\('),
    (C + 'SubscriptionController.cs', 'RequireOwnerAsync', 'body', r'\bOwnerActionGuard\.DenyResult\s*\('),
    (S + 'SubscriptionService.cs', 'ExtendTrialAsync', 'body', r'\bTrialExtensionPolicy\.ResolveDays\s*\('),
    # ---- C1 (รอบแรก): ด่านเจ้าของปฏิเสธคีย์ ----
    ('Accounting/Filters/RejectApiKeyAttribute.cs', 'OnAuthorization', 'body', r'\bOwnerActionGuard\.DenyResult\s*\('),
    (S + 'CompanyService.cs', 'EnsureOwnerAccessAsync', 'body', r'\bOwnerActionGuard\.EnsureNotApiKey\s*\('),
    (S + 'RolePermissionService.cs', 'EnsureOwnerAccessAsync', 'body', r'\bOwnerActionGuard\.EnsureNotApiKey\s*\('),
    (C + 'IntegrationController.cs', 'RequireOwnerAsync', 'body', r'\bOwnerActionGuard\.IsApiKeyRequest\s*\('),
    # ---- S-04: สวิตช์ EnableApiAccess มีผลกับคีย์ acc_ ที่ออกแล้ว ----
    ('Accounting/Middleware/ApiKeyMiddleware.cs', 'InvokeAsync', 'body', r'\bApiAccessPolicy\.EvaluateAccountKey\s*\('),
    # ---- S-08: สวิตช์เปิดรับสมัคร — ทุกทางสร้างบัญชี/บริษัท ----
    (S + 'AuthService.cs', 'RegisterAsync', 'body', r'\bEvaluateRegistrationAsync\s*\('),
    (S + 'AuthService.cs', 'SsoLoginAsync', 'body', r'\bEvaluateRegistrationAsync\s*\('),
    (S + 'CompanyService.cs', 'CreateAsync', 'body', r'\bRegistrationPolicy\.EvaluateNewCompany\s*\('),
    # ---- S-12: หัวเอกสารของช่องทางส่ง = หัวเดียวกับ PDF ----
    (S + 'DocumentEmailService.cs', 'SendDocumentEmailAsync', 'body', r'\bResolveDocumentHeadingAsync\s*\('),
    (S + 'DocumentEmailService.cs', 'SendEtaxByEmailAsync', 'body', r'\bResolveDocumentHeadingAsync\s*\('),
    (S + 'DocumentEmailService.cs', 'GetDefaultTemplateAsync', 'body', r'\bResolveDocumentHeadingAsync\s*\('),
    (S + 'DocumentLineDeliveryService.cs', 'SendDocumentLineAsync', 'body', r'\bResolveDocumentHeadingAsync\s*\('),
    (S + 'EmailScheduleService.cs', 'EnqueueDocumentAsync', 'body', r'\bResolveDocumentHeadingAsync\s*\('),
    # ---- P3: หมุนคีย์รุ่นเก่า = ย้ายเป็นนโยบายใหม่ ----
    (S + 'IntegrationService.cs', 'RegenerateApiKeyAsync', 'body', r'\bIsLegacyKey\s*=\s*false\b'),
]

MODIFIERS = r'(?:public|private|protected|internal|static|async|override|virtual|sealed|new|unsafe|extern)'
DECL_RE_TMPL = r'(?m)^[ \t]*(?:{mods}\s+)+[\w<>\[\],.?() ]*?\b{name}\s*\('


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
        if c == '"':
            verbatim = i > 0 and text[i - 1] == '@'
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
                    if text[j] == '"' or text[j] == '\n':
                        break
                j += 1
            out.append(text[i:j + 1])
            i = j + 1
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def _match_close(text, i, open_ch, close_ch):
    depth = 0
    for j in range(i, len(text)):
        if text[j] == open_ch:
            depth += 1
        elif text[j] == close_ch:
            depth -= 1
            if depth == 0:
                return j
    return -1


def find_method(text, name):
    """คืน list ของ dict (attrs · body · attr_span · body_span) ต่อการประกาศเมธอดชื่อ name"""
    out = []
    for m in re.finditer(DECL_RE_TMPL.format(mods=MODIFIERS, name=re.escape(name)), text):
        paren = text.find('(', m.end() - 1)
        close = _match_close(text, paren, '(', ')')
        if close < 0:
            continue
        rest = text[close + 1:]
        mm = re.match(r'\s*(?:where[^{=]*)?(\{|=>)', rest)
        if not mm:
            continue                                         # การเรียก ไม่ใช่การประกาศ / abstract
        if mm.group(1) == '{':
            b0 = close + 1 + mm.start(1)
            b1 = _match_close(text, b0, '{', '}')
            b_end = b1 + 1 if b1 > 0 else len(text)
        else:
            b0 = close + 1 + mm.end(1)
            b_end = text.find(';', b0) + 1
        body = text[b0:b_end]
        # attributes: บรรทัดเหนือการประกาศที่ขึ้นต้นด้วย [ (ต่อกันไม่มีบรรทัดอื่นคั่น)
        line_start = text.rfind('\n', 0, m.start()) + 1
        attrs = []
        k = line_start
        while k > 0:
            prev_start = text.rfind('\n', 0, k - 1) + 1
            line = text[prev_start:k].strip()
            if line.startswith('[') or (attrs and line == ''):
                if line:
                    attrs.append(line)
                k = prev_start
                continue
            break
        out.append({'attrs': text[k:line_start], 'body': body,
                    'attr_span': (k, line_start), 'body_span': (b0, b_end)})
    return out


def check_rule(texts, rule):
    rel, method, kind, pat = rule
    text = texts.get(rel)
    if text is None:
        return f'{rel}: ไม่พบไฟล์'
    decls = find_method(text, method)
    if not decls:
        return f'{rel}: ไม่พบเมธอด {method} (ย้าย/เปลี่ยนชื่อ? — แก้แถวใน RULES ให้ชี้ที่ใหม่ อย่าลบแถว)'
    if len(decls) > 1:
        return f'{rel}: เมธอด {method} มี {len(decls)} ตัว — ระบุไม่ได้ว่าตัวไหนต้องมีด่าน (แก้ RULES ให้แคบลง)'
    where = decls[0]['attrs'] if kind == 'attr' else decls[0]['body']
    if not re.search(pat, where):
        return (f'{rel}: {method} ไม่มี {"attribute" if kind == "attr" else "การเรียก"} ที่ตรง /{pat}/ — '
                f'ด่านเจ้าของ/คีย์หายจากจุดเรียกจริง (เทสต์ของ helper ไม่ฟ้องเรื่องนี้)')
    return None


def load_texts(root):
    texts = {}
    for rel in {r[0] for r in RULES}:
        path = os.path.join(root, rel)
        if os.path.exists(path):
            with open(path, encoding='utf-8', errors='replace') as fh:
                texts[rel] = strip_comments(fh.read())
    return texts


def run(texts):
    return [e for e in (check_rule(texts, r) for r in RULES) if e]


def self_test(root):
    ok = True
    texts = load_texts(root)
    base = run(texts)
    if base:
        print('❌ self-test: เรพจริงไม่ผ่านตั้งแต่ต้น — ' + base[0])
        return False
    # negative test กับไฟล์จริง: ถอดด่านของแต่ละแถวออก (ในหน่วยความจำ) แล้วแถวนั้นต้องฟ้อง
    for rule in RULES:
        rel, method, kind, pat = rule
        d = find_method(texts[rel], method)[0]
        a, b = d['attr_span'] if kind == 'attr' else d['body_span']
        mutated = dict(texts)
        mutated[rel] = texts[rel][:a] + re.sub(pat, 'REMOVED(', texts[rel][a:b]) + texts[rel][b:]
        if check_rule(mutated, rule) is None:
            print(f'❌ self-test: ถอด /{pat}/ จาก {rel}:{method} แล้วไม่ฟ้อง')
            ok = False
    # ด่านในคอมเมนต์ไม่นับ
    fake = {'X.cs': strip_comments('class X {\n  // [RejectApiKey("x")]\n  public IActionResult Purge(Guid id) { return Ok(); }\n'
                                   '  public void Guarded() { /* RequireOwnerAsync( */ }\n}\n')}
    if check_rule(fake, ('X.cs', 'Purge', 'attr', REJECT)) is None:
        print('❌ self-test: attribute ในคอมเมนต์ถูกนับ')
        ok = False
    if check_rule(fake, ('X.cs', 'Guarded', 'body', r'\bRequireOwnerAsync\s*\(')) is None:
        print('❌ self-test: การเรียกในคอมเมนต์ถูกนับ')
        ok = False
    # ทิศตรงข้าม: ด่านที่อยู่จริงต้องผ่าน · การ "เรียก" เมธอดชื่อเดียวกันไม่ใช่การประกาศ
    good = {'Y.cs': 'class Y {\n  [HttpDelete]\n  [RejectApiKey("ลบ")]\n  public async Task<IActionResult> Purge(Guid id)\n'
                    '  {\n    return await Purge2(id);\n  }\n  void Z() { Purge(Guid.Empty); }\n}\n'}
    if check_rule(good, ('Y.cs', 'Purge', 'attr', REJECT)) is not None:
        print('❌ self-test: ด่านที่มีจริงถูกฟ้อง (ฟ้องผิด = checker พัง)')
        ok = False
    return ok


def main(argv):
    if '--self-test' in argv:
        ok = self_test(ROOT)
        print(f'✅ self-test ผ่าน (ถอดด่าน {len(RULES)} แถวจากไฟล์จริง = ฟ้องครบ · คอมเมนต์ไม่นับ · ด่านจริงไม่ถูกฟ้อง)'
              if ok else '❌ self-test ล้ม')
        return 0 if ok else 1
    errs = run(load_texts(ROOT))
    if errs:
        print('❌ ด่านเจ้าของ/คีย์ที่ต้องอยู่ที่จุดเรียกจริงหายไป:')
        for e in errs:
            print('   ' + e)
        return 1
    print(f'✅ ด่านเจ้าของ/คีย์อยู่ครบ {len(RULES)} จุด')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
