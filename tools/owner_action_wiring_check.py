#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ด่าน "เจ้าของเท่านั้น / คีย์ห้าม / ชั้นความลับ" ต้องอยู่ที่จุดเรียกจริง **และผลของด่านต้องถูกใช้** — รอบ 193 ทีม W

═══ ที่มา ═══
ฝ่ายค้านรอบ 193 (review193-W.md): เทสต์ของทีม W เรียกแค่ helper (`OwnerActionGuard` · `ApiAccessPolicy` · `RegistrationPolicy`)
⇒ ถอดบรรทัดที่เรียก helper ออกจาก controller/middleware/service แล้วเทสต์ยังเขียว (F2 ข้อ 2 · ข้อ 6) · env นี้ไม่มี .NET SDK
จึงเขียน integration test ไม่ได้ — checker นี้ล็อกแบบแคบ (รายชื่อตายตัวต่อไฟล์/เมธอด · ไม่กวาดทั้งเรพ — F4 ข้อ 3)

ฝ่ายค้านรอบสอง (review193-r2-W.md §4) ลองกลายพันธุ์รุ่นแรกแล้วพบว่า **จับ "ถอด" ได้ แต่ไม่จับ "ยังเรียกแต่ทิ้งผล"**
(M7 `_ = await RequireOwnerAsync(...)` · M11 `if (false && …)` · M8 attribute เรียก DenyResult แต่ไม่ตั้ง `context.Result` ·
M6 ชื่อ attribute อยู่ในสตริง) และ**ฟ้องผิด** 3 แบบ (M5 `[RejectApiKey]` ไม่มีวงเล็บ · M9 คอมเมนต์คั่นระหว่าง attribute กับ
เมธอด · M12 attribute ระดับคลาส) ⇒ รุ่นนี้: แถว body ใช้ regex ที่บังคับ "รูปการใช้ผล" (`is { } x) return x` ·
`context.Result = x` · `if (!x.Allowed) throw/…`) · ตัดทั้งคอมเมนต์และ**เนื้อสตริง**ก่อนตรวจ · ยอมรับ attribute ระดับคลาส

═══ กติกา ═══
แต่ละแถวของ RULES = (ไฟล์, เมธอด, ชนิด, pattern):
  * 'attr' — attribute เหนือเมธอด (หรือเหนือคลาสที่ครอบ) ต้องตรง pattern
  * 'body' — เนื้อเมธอดต้องตรง pattern (regex บนโค้ดที่ตัดคอมเมนต์+เนื้อสตริงแล้ว)
เมธอดหาไม่เจอ หรือชื่อซ้ำหลายตัวในไฟล์ = ล้ม (ให้คนแก้แถว ไม่ใช่เงียบ) · **ห้ามลบแถวเพื่อให้เขียว**

ใช้: python3 tools/owner_action_wiring_check.py [--self-test]
  --self-test = ถอดด่านทีละแถวจากไฟล์จริง (ในหน่วยความจำ) ต้องฟ้องครบ + การกลายพันธุ์ M5–M12 ของฝ่ายค้านรอบสอง
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
C = 'Accounting/Controllers/'
S = 'Accounting/Services/Implementations/'
F = 'Accounting/Filters/'

# attribute: มี/ไม่มีวงเล็บก็ได้ (ctor มีค่า default · M5) · ต้องอยู่ในวงเล็บเหลี่ยมของ attribute
REJECT = r'[\[,]\s*(?:Accounting\.Filters\.)?RejectApiKey\s*(?:\(|\]|,)'
OWNER = r'[\[,]\s*(?:Accounting\.Filters\.)?RequireOwner\s*(?:\(|\]|,)'
PERM = r'[\[,]\s*(?:Accounting\.Filters\.)?RequirePermission\s*\('
CALL_ARGS = r'\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)'   # อาร์กิวเมนต์ที่มีวงเล็บซ้อนได้ 2 ชั้น


def gate_used(call):
    """`if (await <call>(…) is { } x) return x;` — ผลของด่านถูกคืนจริง (ไม่ใช่ `_ =` · ไม่ใช่ `false &&`)"""
    return (r'if\s*\(\s*await\s+' + call + r'\s*' + CALL_ARGS + r'\s*is\s*\{\s*\}\s*(\w+)\s*\)\s*return\s+\1\s*;')


def decision_used(call, then=r'throw\b'):
    """`var x = <call>(…); if (!x.Allowed) <then>` — ผลการตัดสินถูกใช้ทันที"""
    return (r'var\s+(\w+)\s*=\s*' + call + r'\s*' + CALL_ARGS + r'\s*;\s*if\s*\(\s*!\s*\1\s*\.\s*Allowed\s*\)\s*' + then)


def value_used(call):
    """`var x = [await] <call>(…); … x …` — ค่าที่ได้ถูกใช้ต่อ (ไม่ใช่เรียกแล้วทิ้ง)"""
    return r'var\s+(\w+)\s*=\s*(?:await\s+)?' + call + r'\s*' + CALL_ARGS + r'\s*;[\s\S]*?\b\1\b'


HEADING = value_used(r'PdfGenerationService\.ResolveDocumentHeadingAsync')
SENSITIVE = gate_used(r'DenySensitiveAsync').replace(r'return\s+\1\s*;', r'return\s+Forbid403\s*<\w+>\s*\(\s*\1\s*\)\s*;')

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
    (C + 'EmailConfigController.cs', 'Update', 'attr', PERM),
    (C + 'LineConfigController.cs', 'Update', 'attr', REJECT),
    (C + 'LineConfigController.cs', 'Update', 'attr', PERM),
    (C + 'EtaxController.cs', 'UpdateConfig', 'attr', REJECT),
    (C + 'PayrollController.cs', 'UpsertTaxRuleConfig', 'attr', REJECT),
    (C + 'PayrollController.cs', 'DeleteTaxRuleConfig', 'attr', REJECT),
    # ---- W2-C6: ตารางกฎหมายประกันสังคม (ชุดเดียวกับ tax-rule-config) ----
    (C + 'PayrollController.cs', 'UpsertSsoConfig', 'attr', REJECT),
    (C + 'PayrollController.cs', 'DeleteSsoConfig', 'attr', REJECT),
    # ---- W-C3: แพ็กเกจ/บิลลิ่ง ----
    (C + 'AccountSubscriptionController.cs', 'StartTrial', 'attr', REJECT),
    (C + 'AccountSubscriptionController.cs', 'Attach', 'attr', REJECT),
    (C + 'AccountSubscriptionController.cs', 'Detach', 'attr', REJECT),
    (C + 'SubscriptionController.cs', 'ExtendTrial', 'body', gate_used(r'RequireOwnerAsync')),
    (C + 'SubscriptionController.cs', 'ConvertTrial', 'body', gate_used(r'RequireOwnerAsync')),
    (C + 'SubscriptionController.cs', 'ChangePlan', 'body', gate_used(r'RequireOwnerAsync')),
    (C + 'SubscriptionController.cs', 'Cancel', 'body', gate_used(r'RequireOwnerAsync')),
    (C + 'SubscriptionController.cs', 'RequireOwnerAsync', 'body',
     r'=>\s*await\s+(?:Accounting\.Filters\.)?RequireOwnerAttribute\.DenyAsync\s*\('),
    (S + 'SubscriptionService.cs', 'ExtendTrialAsync', 'body', value_used(r'(?:Accounting\.Helpers\.)?TrialExtensionPolicy\.ResolveDays')),
    # ---- W2-C3/C4/C5: gateway · สิทธิ์เอกสารลับ · กฎการอนุมัติ ----
    (C + 'PaymentSettingsController.cs', 'Save', 'attr', REJECT),
    (C + 'PaymentSettingsController.cs', 'Save', 'attr', OWNER),
    (C + 'PaymentSettingsController.cs', 'Test', 'attr', REJECT),
    (C + 'PaymentSettingsController.cs', 'Test', 'attr', OWNER),
    (C + 'PaymentSettingsController.cs', 'SetMode', 'attr', REJECT),
    (C + 'PaymentSettingsController.cs', 'SetMode', 'attr', OWNER),
    (C + 'SensitivityController.cs', 'SetRule', 'attr', REJECT),
    (C + 'SensitivityController.cs', 'SetRule', 'attr', OWNER),
    (C + 'ApprovalController.cs', 'CreateRule', 'attr', REJECT),
    (C + 'ApprovalController.cs', 'CreateRule', 'attr', PERM),
    (C + 'ApprovalController.cs', 'UpdateRule', 'attr', REJECT),
    (C + 'ApprovalController.cs', 'UpdateRule', 'attr', PERM),
    (C + 'ApprovalController.cs', 'DeleteRule', 'attr', REJECT),
    (C + 'ApprovalController.cs', 'DeleteRule', 'attr', PERM),
    (C + 'AuditTrailController.cs', 'VerifyHashChain', 'attr', REJECT),
    (C + 'AuditTrailController.cs', 'VerifyHashChain', 'attr', PERM),
    # ---- W2-P6: ชั้นความลับของทางส่งอีเมล (ด่านเดียวกับหน้าเอกสาร) ----
    (C + 'DocumentController.cs', 'SendEmail', 'body', SENSITIVE),
    (C + 'DocumentController.cs', 'GetEmailTemplate', 'body', SENSITIVE),
    (C + 'DocumentController.cs', 'DenySensitiveAsync', 'body', r'\bCanViewAsync\s*\('),
    # ---- ตัว attribute เอง: ต้อง "ตั้ง context.Result" จากผลของตัวตัดสิน (M8) ----
    (F + 'RejectApiKeyAttribute.cs', 'OnAuthorization', 'body',
     r'if\s*\(\s*OwnerActionGuard\.DenyResult\s*' + CALL_ARGS + r'\s*is\s*\{\s*\}\s*(\w+)\s*\)\s*context\s*\.\s*Result\s*=\s*\1\s*;'),
    (F + 'RequireOwnerAttribute.cs', 'OnAuthorizationAsync', 'body',
     r'if\s*\(\s*await\s+DenyAsync\s*' + CALL_ARGS + r'\s*is\s*\{\s*\}\s*(\w+)\s*\)\s*ctx\s*\.\s*Result\s*=\s*\1\s*;'),
    (F + 'RequireOwnerAttribute.cs', 'DenyAsync', 'body', r'return\s+OwnerGateDecision\.Decide\s*' + CALL_ARGS + r'\s*switch'),
    (F + 'RequireOwnerAttribute.cs', 'DenyAsync', 'body', r'var\s+isKey\s*=\s*OwnerActionGuard\.IsApiKeyRequest\s*\('),
    # ---- C1 (รอบแรก): ด่านเจ้าของปฏิเสธคีย์ — เป็นคำสั่งระดับบนของเมธอด (ไม่อยู่ใต้ if) ----
    (S + 'CompanyService.cs', 'EnsureOwnerAccessAsync', 'body',
     r'(?m)^\s*(?:Accounting\.Helpers\.)?OwnerActionGuard\.EnsureNotApiKey\s*\('),
    (S + 'RolePermissionService.cs', 'EnsureOwnerAccessAsync', 'body',
     r'(?m)^\s*(?:Accounting\.Helpers\.)?OwnerActionGuard\.EnsureNotApiKey\s*\('),
    (C + 'IntegrationController.cs', 'RequireOwnerAsync', 'body',
     r'if\s*\(\s*OwnerActionGuard\.IsApiKeyRequest\s*\(\s*HttpContext\s*\)\s*\)\s*return\b'),
    # ---- S-04: สวิตช์ EnableApiAccess มีผลกับคีย์ acc_ ที่ออกแล้ว ----
    ('Accounting/Middleware/ApiKeyMiddleware.cs', 'InvokeAsync', 'body',
     decision_used(r'(?:Accounting\.)?Helpers\.ApiAccessPolicy\.EvaluateAccountKey', r'\{(?:[^{}]|\{[^{}]*\})*\breturn\s*;')),
    # ---- S-08: สวิตช์เปิดรับสมัคร — ทุกทางสร้างบัญชี/บริษัท ----
    (S + 'AuthService.cs', 'RegisterAsync', 'body', decision_used(r'await\s+EvaluateRegistrationAsync')),
    (S + 'AuthService.cs', 'SsoLoginAsync', 'body', decision_used(r'await\s+EvaluateRegistrationAsync')),
    (S + 'CompanyService.cs', 'CreateAsync', 'body', decision_used(r'(?:Accounting\.Helpers\.)?RegistrationPolicy\.EvaluateNewCompany')),
    # ---- S-12: หัวเอกสารของช่องทางส่ง = หัวเดียวกับ PDF ----
    (S + 'DocumentEmailService.cs', 'SendDocumentEmailAsync', 'body', HEADING),
    (S + 'DocumentEmailService.cs', 'SendEtaxByEmailAsync', 'body', HEADING),
    (S + 'DocumentEmailService.cs', 'GetDefaultTemplateAsync', 'body', HEADING),
    (S + 'DocumentLineDeliveryService.cs', 'SendDocumentLineAsync', 'body', HEADING),
    (S + 'EmailScheduleService.cs', 'EnqueueDocumentAsync', 'body', HEADING),
    # ---- P3: หมุนคีย์รุ่นเก่า = ย้ายเป็นนโยบายใหม่ ----
    (S + 'IntegrationService.cs', 'RegenerateApiKeyAsync', 'body', r'\bintegration\s*\.\s*IsLegacyKey\s*=\s*false\s*;'),
]

MODIFIERS = r'(?:public|private|protected|internal|static|async|override|virtual|sealed|new|unsafe|extern)'
DECL_RE_TMPL = r'(?m)^[ \t]*(?:{mods}\s+)+[\w<>\[\],.?() ]*?\b{name}\s*\('


def strip_code(text):
    """ตัด // และ /* */ และ**เนื้อในสตริง** (คงเครื่องหมายคำพูดและจำนวนบรรทัด) — ด่าน/attribute ที่อยู่ในคอมเมนต์หรือ
    สตริงต้องไม่ถูกนับ (M3/M6) · รองรับ verbatim `@"…""…"` และ interpolated `$"…"` (เนื้อถูกตัดทั้งก้อน)"""
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
        if c == "'" and i + 2 < n:
            j = i + 1
            if text[j] == '\\':
                j += 1
            k = text.find("'", j + 1)
            if 0 < k - i <= 8:
                out.append("' '")
                i = k + 1
                continue
        if c == '"':
            verbatim = i > 0 and text[i - 1] == '@' or (i > 1 and text[i - 2:i] in ('$@', '@$'))
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
            body = text[i + 1:j]
            out.append('"' + ''.join('\n' if ch == '\n' else ' ' for ch in body) + '"')
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


def _attr_span_above(text, line_start):
    """ช่วง attribute เหนือบรรทัดที่ line_start — ข้ามบรรทัดว่าง (คอมเมนต์ถูกตัดเป็นบรรทัดว่างแล้ว · M9)"""
    k = line_start
    top = line_start
    while k > 0:
        prev_start = text.rfind('\n', 0, k - 1) + 1
        line = text[prev_start:k].strip()
        if line.startswith('[') or line.endswith(']'):
            top = prev_start
            k = prev_start
            continue
        if line == '':
            k = prev_start
            continue
        break
    return top, line_start


def find_method(text, name):
    """คืน list ของ dict (attrs · body · attr_span · body_span · class_attrs) ต่อการประกาศเมธอดชื่อ name"""
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
            b0 = close + 1 + mm.start(1)
            b_end = text.find(';', b0) + 1
        line_start = text.rfind('\n', 0, m.start()) + 1
        a0, a1 = _attr_span_above(text, line_start)
        # attribute ระดับคลาสที่ครอบเมธอดนี้ (M12 — ด่านกว้างกว่าเดิมถือว่าผ่าน)
        # ทุกคลาสที่ "ครอบ" เมธอดนี้จริง (ไม่ใช่ record ซ้อนที่ประกาศไว้ก่อนเมธอด)
        class_attrs = ''
        for cm in re.finditer(r'(?m)^[ \t]*(?:\w+\s+)*(?:class|record)\s+\w+[^{;]*\{', text[:m.start()]):
            c_open = cm.end() - 1
            c_close = _match_close(text, c_open, '{', '}')
            if c_open < m.start() and (c_close < 0 or c_close > m.start()):
                cls_line = text.rfind('\n', 0, cm.start() + 1) + 1
                c0, c1 = _attr_span_above(text, cls_line)
                class_attrs += text[c0:c1]
        out.append({'attrs': text[a0:a1], 'body': text[b0:b_end], 'attr_span': (a0, a1),
                    'body_span': (b0, b_end), 'class_attrs': class_attrs})
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
    d = decls[0]
    if kind == 'attr':
        if re.search(pat, d['attrs']) or re.search(pat, d['class_attrs']):
            return None
        return f'{rel}: {method} ไม่มี attribute ที่ตรง /{pat}/ (ทั้งที่เมธอดและที่คลาส) — ด่านหายจากจุดเรียกจริง'
    if re.search(pat, d['body']):
        return None
    return (f'{rel}: {method} ไม่พบรูป /{pat}/ — ด่านถูกถอด หรือยังเรียกแต่ไม่ได้ใช้ผล '
            f'(`_ =` · `if (false && …)` · ไม่ตั้ง context.Result) — เทสต์ของ helper ไม่ฟ้องเรื่องนี้')


def load_texts(root):
    texts = {}
    for rel in {r[0] for r in RULES}:
        path = os.path.join(root, rel)
        if os.path.exists(path):
            with open(path, encoding='utf-8', errors='replace') as fh:
                texts[rel] = strip_code(fh.read())
    return texts


def run(texts):
    return [e for e in (check_rule(texts, r) for r in RULES) if e]


# การกลายพันธุ์ของฝ่ายค้านรอบสอง (review193-r2-W.md §4) — (ป้าย, ไฟล์, หา, แทน, ต้องฟ้อง?) บนไฟล์จริง (ก่อนตัดสตริง)
REVIEWER_CASES = [
    ('M7', C + 'SubscriptionController.cs', 'if (await RequireOwnerAsync(companyId, "ขยายเวลาทดลองใช้") is { } deny) return deny;',
     '_ = await RequireOwnerAsync(companyId, "ขยายเวลาทดลองใช้");', True),
    ('M11', C + 'SubscriptionController.cs', 'if (await RequireOwnerAsync(companyId, "ยกเลิก subscription") is { } deny) return deny;',
     'if (false && await RequireOwnerAsync(companyId, "ยกเลิก subscription") is { } deny) return deny;', True),
    ('M8', F + 'RejectApiKeyAttribute.cs', 'context.Result = deny;', '_ = deny;', True),
    ('M6', C + 'DocumentController.cs', '[Accounting.Filters.RejectApiKey("ลบเอกสารถาวร")]',
     '[System.Obsolete("RejectApiKey(ลบ)")]', True),
    ('M5', C + 'DocumentController.cs', '[Accounting.Filters.RejectApiKey("ลบเอกสารถาวร")]',
     '[Accounting.Filters.RejectApiKey]', False),
    ('M9', C + 'DocumentController.cs', '[Accounting.Filters.RejectApiKey("ลบเอกสารถาวร")]',
     '[Accounting.Filters.RejectApiKey("ลบเอกสารถาวร")]\n    // คอมเมนต์คั่นระหว่าง attribute กับเมธอด', False),
    ('SENS', C + 'DocumentController.cs', 'return Forbid403<EmailTemplate>(hidden);', 'Console.WriteLine(hidden);', True),
    ('HEAD', S + 'DocumentLineDeliveryService.cs', 'var heading = await PdfGenerationService.ResolveDocumentHeadingAsync(',
     '_ = await PdfGenerationService.ResolveDocumentHeadingAsync(', True),
]


def self_test(root):
    ok = True
    raw = {}
    for rel in {r[0] for r in RULES} | {c[1] for c in REVIEWER_CASES}:
        with open(os.path.join(root, rel), encoding='utf-8', errors='replace') as fh:
            raw[rel] = fh.read()
    texts = {k: strip_code(v) for k, v in raw.items()}
    base = run(texts)
    if base:
        print('❌ self-test: เรพจริงไม่ผ่านตั้งแต่ต้น — ' + base[0])
        return False
    # (ก) ถอดด่านของแต่ละแถวออก (ในหน่วยความจำ) แล้วแถวนั้นต้องฟ้อง
    for rule in RULES:
        rel, method, kind, pat = rule
        d = find_method(texts[rel], method)[0]
        a, b = d['attr_span'] if kind == 'attr' else d['body_span']
        mutated = dict(texts)
        mutated[rel] = texts[rel][:a] + re.sub(pat, ' REMOVED ', texts[rel][a:b]) + texts[rel][b:]
        if kind == 'attr' and re.search(pat, d['class_attrs']):
            continue
        if check_rule(mutated, rule) is None:
            print(f'❌ self-test: ถอด /{pat}/ จาก {rel}:{method} แล้วไม่ฟ้อง')
            ok = False
    # (ข) การกลายพันธุ์ของฝ่ายค้านรอบสอง
    for tag, rel, old, new, expect in REVIEWER_CASES:
        if old not in raw[rel]:
            print(f'❌ self-test {tag}: หา `{old[:50]}` ใน {rel} ไม่เจอ (โค้ดขยับ — ปรับเคสให้ตรง)')
            ok = False
            continue
        mutated = dict(texts)
        mutated[rel] = strip_code(raw[rel].replace(old, new, 1))
        fired = bool(run(mutated))
        if fired != expect:
            print(f'❌ self-test {tag}: {"ต้องฟ้องแต่ไม่ฟ้อง" if expect else "ฟ้องผิด (โค้ดถูกต้อง)"} ใน {rel}')
            ok = False
    # (ค) M12 attribute ระดับคลาส = ผ่าน · คอมเมนต์/สตริงไม่นับ · การ "เรียก" ชื่อเดียวกันไม่ใช่การประกาศ
    cls = {'Z.cs': strip_code('[ApiController]\n[Accounting.Filters.RejectApiKey("ทั้งคลาส")]\npublic class Z : ControllerBase\n{\n'
                              '    [HttpDelete("x")]\n    public async Task<IActionResult> Purge(Guid id) { return Ok(); }\n}\n')}
    if check_rule(cls, ('Z.cs', 'Purge', 'attr', REJECT)) is not None:
        print('❌ self-test M12: attribute ระดับคลาสถูกฟ้อง (ฟ้องผิด)')
        ok = False
    fake = {'X.cs': strip_code('class X {\n  // [RejectApiKey("x")]\n  public IActionResult Purge(Guid id) { return Ok(); }\n'
                               '  public void Guarded() { /* if (await RequireOwnerAsync(c, "v") is { } d) return d; */ }\n}\n')}
    if check_rule(fake, ('X.cs', 'Purge', 'attr', REJECT)) is None:
        print('❌ self-test: attribute ในคอมเมนต์ถูกนับ')
        ok = False
    if check_rule(fake, ('X.cs', 'Guarded', 'body', gate_used('RequireOwnerAsync'))) is None:
        print('❌ self-test: การเรียกในคอมเมนต์ถูกนับ')
        ok = False
    good = {'Y.cs': strip_code('class Y {\n  [HttpDelete]\n  [RejectApiKey("ลบ")]\n  public async Task<IActionResult> Purge(Guid id)\n'
                               '  {\n    return await Purge2(id);\n  }\n  void Z() { Purge(Guid.Empty); }\n}\n')}
    if check_rule(good, ('Y.cs', 'Purge', 'attr', REJECT)) is not None:
        print('❌ self-test: ด่านที่มีจริงถูกฟ้อง (ฟ้องผิด = checker พัง)')
        ok = False
    return ok


def main(argv):
    if '--self-test' in argv:
        ok = self_test(ROOT)
        print(f'✅ self-test ผ่าน (ถอดด่าน {len(RULES)} แถวจากไฟล์จริง = ฟ้องครบ · กลายพันธุ์ฝ่ายค้าน {len(REVIEWER_CASES)} เคส · '
              'ระดับคลาส/คอมเมนต์/สตริง ถูกต้อง)' if ok else '❌ self-test ล้ม')
        return 0 if ok else 1
    errs = run(load_texts(ROOT))
    if errs:
        print('❌ ด่านเจ้าของ/คีย์/ชั้นความลับที่ต้องอยู่ที่จุดเรียกจริง (และใช้ผล) หายไป:')
        for e in errs:
            print('   ' + e)
        return 1
    print(f'✅ ด่านเจ้าของ/คีย์/ชั้นความลับอยู่ครบและใช้ผลจริง {len(RULES)} จุด')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
