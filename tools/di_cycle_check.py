# -*- coding: utf-8 -*-
"""ตรวจวงกลม DI แบบเดียวกับที่ ASP.NET ทำตอน start (ValidateOnBuild)

รันจาก root ของ repo:  python3 tools/di_cycle_check.py   (--self-test = negative test อย่างเดียว)

ทำไมต้องมี: วงกลมใน DI ไม่ใช่ compile error — โค้ดคอมไพล์ผ่านหมด แล้วไป
ระเบิดตอน start ด้วย stack trace ยาวเป็นหน้า ("A circular dependency was
detected") ซึ่งอ่านหาต้นตอยาก. สคริปต์นี้จำลองการตรวจแบบเดียวกัน: อ่าน
registration จาก Program.cs → หา ctor ของ implementation แต่ละตัว → สร้าง
กราฟ → หา cycle แล้วพิมพ์เส้นทางที่วนออกมาให้ตรง ๆ

รอบ 229 (ฝ่ายค้าน B1): รุ่นเดิมมีจุดบอดสองจุดที่ทำให้วง
QuotaAddOnPurchaseService → IPlatformPaymentService → IPaymentIntentService → IEnumerable<IPaymentCompletionHandler>
→ QuotaAddOnPaymentHandler → IQuotaAddOnPurchaseService หลุด: (1) service ที่ register หลายตัว (handler ทุกตัวของ
IPaymentCompletionHandler) ถูกเก็บแค่ตัวสุดท้าย (2) พารามิเตอร์ IEnumerable<T> ถูกตัดเหลือ "IEnumerable" ⇒ ไม่มีเส้น ·
รุ่นนี้เก็บทุก implementation ต่อ service และแกะ IEnumerable<T>/IReadOnlyList<T> เป็น T · negative test ในตัวรันทุกครั้ง

วิธีแก้เมื่อเจอ: ตัวใดตัวหนึ่งในวงต้องเลิก inject อีกตัวผ่าน ctor แล้วไป
resolve ตอนเรียกใช้แทน (inject IServiceProvider) — ดูตัวอย่างที่
ApprovalService และ PlatformBillingDocumentIssuer
"""
import re, os, sys
from collections import defaultdict

ENUM_WRAPPERS = ('IEnumerable', 'IReadOnlyList', 'IReadOnlyCollection', 'IList', 'ICollection', 'Lazy')


def registrations(prog: str):
    reg = defaultdict(set)
    # AddScoped<IFoo, Foo>() / AddSingleton<IFoo, Foo>() / AddTransient<...>
    for m in re.finditer(r'Add(?:Scoped|Singleton|Transient)<\s*([\w\.]+)\s*,\s*([\w\.]+)\s*>', prog):
        reg[m.group(1).split('.')[-1]].add(m.group(2).split('.')[-1])
    # AddScoped<Foo>() — self-registered
    for m in re.finditer(r'Add(?:Scoped|Singleton|Transient)<\s*([\w\.]+)\s*>\s*\(\s*\)', prog):
        t = m.group(1).split('.')[-1]
        reg[t].add(t)
    return reg


def split_params(s: str):
    parts, depth, cur = [], 0, ''
    for ch in s:
        if ch == '<': depth += 1
        elif ch == '>': depth -= 1
        if ch == ',' and depth == 0:
            parts.append(cur); cur = ''
        else:
            cur += ch
    if cur.strip(): parts.append(cur)
    return parts


def param_type(part: str):
    part = re.sub(r'\[[^\]]*\]', '', part).strip()        # [FromServices] ฯลฯ
    part = part.split('=')[0].strip()
    if not part: return None
    m = re.match(r'([\w\.]+)\s*<\s*([\w\.]+)\??\s*>', part)
    if m and m.group(1).split('.')[-1] in ENUM_WRAPPERS:
        return m.group(2).split('.')[-1]
    return part.split()[0].split('<')[0].rstrip('?').split('.')[-1]


def ctor_deps(cls, impl_files):
    deps = []
    for _, s in impl_files.get(cls, []):
        for m in re.finditer(rf'public\s+{cls}\s*\(([^)]*)\)', s):
            body = re.sub(r'//[^\n]*', '', m.group(1))
            for part in split_params(body):
                t = param_type(part)
                if t: deps.append(t)
    return deps


def find_cycles(reg, impl_files):
    graph = defaultdict(set)
    for svc, impls in reg.items():
        for impl in impls:
            for d in ctor_deps(impl, impl_files):
                if d in reg:            # นับเฉพาะ service ที่ register ไว้
                    graph[svc].add(d)
    cycles, state, stack = [], {}, []

    def dfs(n):
        state[n] = 1; stack.append(n)
        for m in sorted(graph.get(n, ())):
            if state.get(m) == 1:
                cycles.append(stack[stack.index(m):] + [m])
            elif state.get(m, 0) == 0:
                dfs(m)
        stack.pop(); state[n] = 2

    for n in sorted(reg):
        if state.get(n, 0) == 0: dfs(n)
    return graph, cycles


def scan_impls():
    impl_files = {}
    for root, _, files in os.walk('Accounting'):
        for fn in files:
            if fn.endswith('.cs'):
                p = os.path.join(root, fn)
                try: s = open(p, encoding='utf-8').read()
                except OSError: continue
                for m in re.finditer(r'public\s+(?:sealed\s+|partial\s+)?class\s+(\w+)', s):
                    impl_files.setdefault(m.group(1), []).append((p, s))
    return impl_files


def self_test():
    """negative test: วงผ่าน IEnumerable<handler> ที่ register หลายตัว (แบบ B1 รอบ 229) ต้องถูกจับ · ตัดวงด้วย IServiceProvider ต้องผ่าน"""
    fails = []
    prog = ('builder.Services.AddScoped<IPay, Pay>(); builder.Services.AddScoped<IIntent, Intent>();'
            'builder.Services.AddScoped<IHandler, HandlerA>(); builder.Services.AddScoped<IHandler, HandlerQ>();'
            'builder.Services.AddScoped<IPurchase, Purchase>();')
    def impls(purchase_ctor):
        return {
            'Pay': [('x', 'public Pay(IIntent i) {}')],
            'Intent': [('x', 'public Intent(Db db, IEnumerable<IHandler> handlers) {}')],
            'HandlerA': [('x', 'public HandlerA(Db db) {}')],
            'HandlerQ': [('x', 'public HandlerQ(IPurchase p, ILogger<HandlerQ> l) {}')],
            'Purchase': [('x', purchase_ctor)],
        }
    _, c = find_cycles(registrations(prog), impls('public Purchase(Db db, IPay pay, Dictionary<string, int> x = null) {}'))
    if not c: fails.append('self-test: วง IEnumerable<IHandler> (B1 รอบ 229) ไม่ถูกจับ')
    _, c = find_cycles(registrations(prog), impls('public Purchase(Db db, IServiceProvider services) {}'))
    if c: fails.append('self-test: ตัดวงด้วย IServiceProvider แล้วยังถูกฟ้อง')
    return fails


if __name__ == '__main__':
    st = self_test()
    for f in st: print('❌ ' + f)
    if st: sys.exit(1)
    if '--self-test' in sys.argv:
        print('self-test: ผ่าน'); sys.exit(0)
    prog = open('Accounting/Program.cs', encoding='utf-8').read()
    reg = registrations(prog)
    graph, cycles = find_cycles(reg, scan_impls())
    print(f'service ที่ register: {len(reg)} · เส้นพึ่งพา: {sum(len(v) for v in graph.values())}')
    if cycles:
        seen = set()
        for c in cycles:
            key = tuple(sorted(set(c)))
            if key in seen: continue
            seen.add(key)
            print('❌ วงกลม: ' + ' → '.join(c))
        sys.exit(1)
    print('✅ ไม่มีวงกลมใน DI graph')
