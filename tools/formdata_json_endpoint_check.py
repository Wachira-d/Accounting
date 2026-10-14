#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""formdata_json_endpoint_check.py — หน้าเว็บห้ามส่ง FormData (multipart) ไป endpoint ที่รับ `[FromBody]` (JSON)

═══ ที่มา (บั๊กจริง · IMPORT_ETL_REVIEW 2026-10-14 §1 · รอบ 205 ทีม E) ═══
modal "นำเข้า CSV/Excel" ของ `contacts.html` · `products.html` · `journals.html` สร้าง `FormData` (file + entityType) แล้ว
`API.upload(…/import-export/validate)` และ `…/import-export/import` — ทั้งสอง action รับ `[FromBody] ImportRequest` (JSON)
⇒ ASP.NET ตอบ **415 Unsupported Media Type ทุกครั้ง** และหน้ายังอ่าน `validCount/preview/importedCount` ที่ `ImportResult` ไม่มี ·
ปุ่มนำเข้าของสามหน้าหลักตายตั้งแต่วันที่เขียน — ไม่มี compiler/เทสต์/`node --check` ตัวไหนเห็น เพราะทั้งสองฝั่ง "ถูกไวยากรณ์" คนละไฟล์
(รูปแบบเดียวกับ `upload_route_check` / `deep_link_param_check` ที่โยงสองที่คนละไฟล์เข้าหากัน)

═══ กติกา ═══
ทุกการส่ง FormData ใน `wwwroot/**/*.html|*.js` ที่มองเห็น URL เป็น literal:
  • `API.upload(<url>, …)`                     (verb = POST)
  • `API.request('<VERB>', <url>, <data>, true …)` (isFormData = true)
ต้องไม่ไปตกที่ action ของ controller ที่ verb + route ตรงกัน **และ** มีพารามิเตอร์ `[FromBody]` (ไม่มี `IFormFile`/`[FromForm]`)
route จับคู่ทีละ segment: `${…}` ใน URL และ `{…}` ใน route = ตัวแทนอะไรก็ได้ · ส่วนหน้า `/api/companies/${…}/` หรือ `${base}/` ถูกตัด
(api.js สร้าง URL จาก `${base}` = `/api/companies/{companyId}`) · URL ที่เป็นตัวแปร (มองไม่เห็น literal) ข้าม — ช่องโหว่ที่ยอมรับ

negative test รันทุกครั้ง: (1) controller สังเคราะห์ `[FromBody]` + หน้า `API.upload` ไปหา ⇒ ต้องฟ้อง · controller รับ `IFormFile`/`[FromForm]`
⇒ ต้องไม่ฟ้อง · `API.request('POST', …, false)` (JSON) ⇒ ต้องไม่ฟ้อง (2) หน้า `contacts.html` ก่อนแก้ (git show 300620c1) กับ
controller จริงของเรพ ⇒ ต้องฟ้อง ≥ 2 จุด (validate + import) — ถ้าเครื่องนี้ไม่มีคอมมิตนั้น (clone ตื้น) แจ้งแล้วข้ามเฉพาะข้อ (2)
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONTROLLERS = os.path.join(ROOT, "Accounting", "Controllers")
WWW = os.path.join(ROOT, "Accounting", "wwwroot")
SKIP_DIRS = {"lib", "node_modules", "vendor", "bin", "obj"}

CLASS_ROUTE = re.compile(r'\[Route\(\s*"([^"]*)"\s*\)\]\s*(?:\[[^\]]*\]\s*)*public\s+(?:sealed\s+|partial\s+)*class\s+(\w+)')
HTTP_ATTR = re.compile(r'\[Http(Get|Post|Put|Delete|Patch)(?:\(\s*"([^"]*)"[^)]*\))?\]')
URL_LIT = r'(`[^`]*`|\'[^\']*\'|"[^"]*")'
UPLOAD_CALL = re.compile(r'\bAPI\.upload\(\s*' + URL_LIT)
REQUEST_CALL = re.compile(r'\bAPI\.request\(\s*[\'"](GET|POST|PUT|PATCH|DELETE)[\'"]\s*,\s*' + URL_LIT + r'\s*,\s*[^,()]+\s*,\s*true\b')


def strip_cs_comments(text):
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
    return re.sub(r'(?<!:)//[^\n]*', '', text)


def norm_segments(path):
    """แยก path เป็น segment ตัวเล็ก · `{…}`/`${…}` = '*' · ตัด query"""
    path = path.split('?')[0].strip('/')
    out = []
    for seg in path.split('/'):
        if not seg:
            continue
        if seg.startswith('{') or '${' in seg:
            out.append('*')
        else:
            out.append(seg.lower())
    return out


def strip_api_prefix(segs):
    """ตัด `api/companies/*` (ทั้งจาก route ของ controller และ URL ของหน้า) ให้เหลือส่วนที่ต่างกันจริง"""
    if segs[:3] == ['api', 'companies', '*']:
        return segs[3:]
    return segs


def scan_controllers(files=None):
    """[(verb, segs, file, method)] ของ action ที่รับ [FromBody] และไม่รับไฟล์"""
    acts = []
    paths = files
    if paths is None:
        paths = []
        for dp, dirs, fns in os.walk(CONTROLLERS):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
            paths += [os.path.join(dp, f) for f in fns if f.endswith('.cs')]
    for path in paths:
        text = strip_cs_comments(open(path, encoding='utf-8').read())
        classes = [(m.start(), m.group(1), m.group(2)) for m in CLASS_ROUTE.finditer(text)]
        for m in HTTP_ATTR.finditer(text):
            owner = [c for c in classes if c[0] < m.start()]
            if not owner:
                continue
            _, base, cls = owner[-1]
            tmpl = m.group(2)
            if tmpl is None:
                full = base
            elif tmpl.startswith('/') or tmpl.startswith('~/'):
                full = tmpl.lstrip('~/')
            else:
                full = base.rstrip('/') + '/' + tmpl
            full = full.replace('[controller]', re.sub(r'Controller$', '', cls))
            # ลายเซ็นเมธอดตัวถัดไป: จาก attribute ถึง ')' ที่ปิดรายการพารามิเตอร์ตัวแรกหลังชื่อเมธอด
            sig_m = re.compile(r'public\s+[^;{=]*?\b(\w+)\s*\(').search(text, m.end())
            if not sig_m:
                continue
            depth, i = 1, sig_m.end()
            while i < len(text) and depth:
                depth += {'(': 1, ')': -1}.get(text[i], 0)
                i += 1
            params = text[sig_m.end():i - 1]
            if '[FromBody]' not in params:
                continue
            if 'IFormFile' in params or '[FromForm]' in params:
                continue
            acts.append((m.group(1).upper(), strip_api_prefix(norm_segments(full)), os.path.relpath(path, ROOT), sig_m.group(1)))
    return acts


def url_segments(lit):
    url = lit[1:-1]
    # ${base}/… (api.js) หรือ /api/companies/${…}/… (หน้าเว็บ)
    url = re.sub(r'^\$\{base\}', '/api/companies/${cid}', url)
    if not url.startswith('/api/'):
        return None
    return strip_api_prefix(norm_segments(url))


def scan_pages(sources=None):
    """[(file, line, verb, segs, url)] ของการส่ง FormData ที่เห็น URL"""
    out = []
    items = sources
    if items is None:
        items = []
        for dp, dirs, fns in os.walk(WWW):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
            for f in fns:
                if f.endswith(('.html', '.js')):
                    p = os.path.join(dp, f)
                    items.append((os.path.relpath(p, ROOT), open(p, encoding='utf-8', errors='replace').read()))
    for name, text in items:
        for m in UPLOAD_CALL.finditer(text):
            segs = url_segments(m.group(1))
            if segs is not None:
                out.append((name, text.count('\n', 0, m.start()) + 1, 'POST', segs, m.group(1)))
        for m in REQUEST_CALL.finditer(text):
            segs = url_segments(m.group(2))
            if segs is not None:
                out.append((name, text.count('\n', 0, m.start()) + 1, m.group(1).upper(), segs, m.group(2)))
    return out


def seg_match(a, b):
    return len(a) == len(b) and all(x == y or x == '*' or y == '*' for x, y in zip(a, b))


def judge(pages, acts):
    errs = []
    for name, line, verb, segs, url in pages:
        for averb, asegs, afile, ameth in acts:
            if averb == verb and seg_match(segs, asegs):
                errs.append(f"{name}:{line}: ส่ง FormData ({verb} {url}) ไปที่ {afile} {ameth}() ซึ่งรับ [FromBody] JSON ⇒ 415 ทุกครั้ง "
                            f"— ส่ง JSON (API.post/put) หรือให้ action รับ IFormFile/[FromForm]")
    return errs


SELF_CTRL = '''
[ApiController]
[Route("api/companies/{companyId:guid}/thing")]
[Authorize]
public class ThingController : ControllerBase
{
    [HttpPost("json-only")]
    public async Task<ActionResult> JsonOnly(Guid companyId, [FromBody] Req request) => Ok();

    [HttpPost("upload")]
    public async Task<ActionResult> Upload(Guid companyId, IFormFile? file, [FromForm] string? x) => Ok();

    // [HttpPost("commented")] public async Task<ActionResult> Commented(Guid companyId, [FromBody] Req r) => Ok();

    [HttpPut("{id:guid}/json")]
    public async Task<ActionResult> PutJson(Guid companyId, Guid id, [FromBody] Req r) => Ok();
}
'''

SELF_PAGE = '''
const fd = new FormData();
await API.upload(`/api/companies/${Layout.getCompanyId()}/thing/json-only`, fd);
await API.upload(`/api/companies/${cid}/thing/upload`, fd);
await API.request('PUT', `${base}/thing/${id}/json`, fd, true);
await API.request('PUT', `${base}/thing/${id}/json`, { a: 1 });
await API.post(`/api/companies/${cid}/thing/json-only`, { a: 1 });
'''


def self_test():
    import tempfile
    fails = []
    with tempfile.NamedTemporaryFile('w', suffix='.cs', delete=False, encoding='utf-8') as f:
        f.write(SELF_CTRL)
        tmp = f.name
    try:
        acts = scan_controllers([tmp])
    finally:
        os.unlink(tmp)
    names = {a[3] for a in acts}
    if names != {'JsonOnly', 'PutJson'}:
        fails.append(f"self-test: action [FromBody] ที่ตรวจได้ผิด — ได้ {sorted(names)} (คาด JsonOnly · PutJson · Upload รับไฟล์ต้องไม่นับ · คอมเมนต์ไม่นับ)")
    errs = judge(scan_pages([('page.html', SELF_PAGE)]), acts)
    hit_lines = sorted(int(e.split(':')[1]) for e in errs)
    if hit_lines != [3, 5]:
        fails.append(f"self-test: ฟ้องบรรทัด {hit_lines} (คาด [3, 5] — upload ไป JSON + request(…, true) ไป JSON · "
                     "upload ไป IFormFile / request JSON / API.post ต้องไม่ฟ้อง)")
    # (2) baseline จริงจาก git: contacts.html ก่อนแก้ (modal 415) กับ controller จริง
    try:
        old = subprocess.run(['git', '-C', ROOT, 'show', '300620c1:Accounting/wwwroot/pages/contacts.html'],
                             capture_output=True, text=True, timeout=30)
    except (OSError, subprocess.SubprocessError):
        old = None
    if old is not None and old.returncode == 0:
        real = judge(scan_pages([('contacts.html@300620c1', old.stdout)]), scan_controllers())
        if len([e for e in real if 'import-export' in e]) < 2:
            fails.append("self-test: contacts.html ก่อนแก้ (FormData → import-export/validate|import) ต้องถูกฟ้อง ≥ 2 จุด — ได้ "
                         f"{len(real)}")
    else:
        print("ℹ️  self-test (2) ข้าม: ไม่มีคอมมิต 300620c1 ในเครื่องนี้ (clone ตื้น) — ข้อ (1) ยังรัน")
    return fails


def main():
    fails = self_test()
    errs = judge(scan_pages(), scan_controllers())
    for e in fails + errs:
        print("❌ " + e)
    if fails or errs:
        return 1
    print("✅ ไม่มีหน้าที่ส่ง FormData ไป endpoint [FromBody] JSON (negative test ผ่าน)")
    return 0


if __name__ == '__main__':
    sys.exit(main())
