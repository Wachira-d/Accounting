// ===== PlanDisplay — วิธี "แสดง" ค่าแพ็กเกจที่แอดมินตั้ง (ตัวเดียวของทุกหน้า) =====
// ที่มา (2026-09-10): หน้าแรก · หน้าสมัคร · หน้าแพ็กเกจของลูกค้า ต่างคนต่างพิมพ์
// ตารางฟีเจอร์/ตัวเลข/"ทดลอง 14 วัน"/"+7 วัน" ไว้ตายตัว ⇒ แอดมินแก้แพ็กเกจแล้วหน้าเว็บ
// ไม่เปลี่ยน (ผู้ใช้รายงาน). ตัวนี้รับ **ข้อมูลจาก API เท่านั้น**
// (`/api/subscription/plans` + `/api/subscription/feature-catalog`) แล้วแปลงเป็นข้อความ —
// ห้ามพิมพ์ชื่อฟีเจอร์ · จำนวน · วัน ลงในหน้าไหนอีก
//
// เกณฑ์ "ไม่จำกัด": ค่าที่ seed/แอดมินใช้แทนไม่จำกัดคือ 999 (ผู้ใช้) · 99 (บริษัท) ·
// 99999 (เอกสาร/รายการ) — เก็บไว้ที่นี่ที่เดียว (เดิม subscription.html พิมพ์ซ้ำเอง)
const PlanDisplay = {
  UNLIMITED_AT: { users: 999, companies: 99, docs: 99999, journals: 99999 },

  esc(s) {
    return String(s ?? '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  },

  /// เรียงตามลำดับ enum ของแพ็กเกจ (FreeTrial → Basic → Pro → Enterprise) — ไม่ใช่ตามชื่อ
  rank(p) { return { FreeTrial: 0, Basic: 1, Pro: 2, Enterprise: 3 }[p?.plan] ?? 9; },
  sort(plans) { return (plans || []).slice().sort((a, b) => this.rank(a) - this.rank(b)); },

  isUnlimited(n, kind) { return Number(n) >= (this.UNLIMITED_AT[kind] || Infinity); },

  /// ตัวเลขลิมิต → ข้อความ ("ไม่จำกัด" เมื่อถึงค่าแทน)
  limit(n, kind, unlimitedLabel) {
    if (this.isUnlimited(n, kind)) return unlimitedLabel;
    return Number(n || 0).toLocaleString();
  },

  storage(bytes) {
    const b = Number(bytes || 0);
    if (b >= 1024 * 1024 * 1024) return (b / (1024 * 1024 * 1024)).toFixed(0) + ' GB';
    return (b / (1024 * 1024)).toFixed(0) + ' MB';
  },

  /// ข้อความ "ขยายเวลาทดลอง" จากค่าที่แอดมินตั้ง — ไม่มีสิทธิ์ขยาย = noneLabel (ไม่แต่งตัวเลข)
  trialExtension(p, fmt, noneLabel) {
    if (!p || p.isPermanentFree || !(p.trialMaxExtensions > 0) || !(p.trialExtensionDays > 0)) return noneLabel;
    return fmt(p.trialMaxExtensions, p.trialExtensionDays);
  },

  has(p, featureName) { return (p?.enabledFeatureNames || []).includes(featureName); },

  label(f, lang) { return (lang === 'en' && f.labelEn) ? f.labelEn : f.labelTh; },

  /// ฟีเจอร์ "เด่น" ของแพ็กเกจ — เลือกจากแคตตาล็อกตามลำดับหมวด (AI → ขั้นสูง → CMS →
  /// การดำเนินงาน → หลายบริษัท → รายงาน → เชื่อมต่อ → หลัก) เอา `take` ตัวแรกที่แพ็กเกจเปิด
  highlights(p, catalog, take, lang, excludeSet) {
    if (!catalog?.features) return [];
    const order = ['ai', 'advanced', 'cms', 'operations', 'multi', 'reporting', 'integration', 'core'];
    const feats = catalog.features.slice().sort((a, b) => order.indexOf(a.category) - order.indexOf(b.category));
    return feats.filter(f => this.has(p, f.name) && !(excludeSet && excludeSet.has(f.name)))
      .slice(0, take).map(f => this.label(f, lang));
  },

  /// ฟีเจอร์ที่แพ็กเกจถัดไปมีแต่แพ็กเกจนี้ไม่มี (ไว้ขึ้น ✗ บนการ์ด) — คำนวณจากข้อมูลจริง
  missingVsNext(p, next, catalog, take, lang) {
    if (!next || !catalog?.features) return [];
    const order = ['ai', 'advanced', 'cms', 'operations', 'multi', 'reporting', 'integration', 'core'];
    const feats = catalog.features.slice().sort((a, b) => order.indexOf(a.category) - order.indexOf(b.category));
    return feats.filter(f => this.has(next, f.name) && !this.has(p, f.name)).slice(0, take).map(f => this.label(f, lang));
  },

  /// บรรทัดบนการ์ดราคา: ลิมิต → จำนวนฟีเจอร์ → ฟีเจอร์เด่น ✓ → ที่แพ็กเกจถัดไปมี ✗ → ทดลอง
  /// L = พจนานุกรมข้อความ (หน้าเรียกส่งมาจาก I18n ของตัวเอง)
  featureLines(p, catalog, nextPlan, L) {
    const lines = [];
    lines.push({ check: true, text: this.isUnlimited(p.maxUsers, 'users') ? L.unlimitedUsers : L.users(p.maxUsers) });
    if (this.isUnlimited(p.maxCompanies, 'companies') || p.maxCompanies > 1)
      lines.push({ check: true, text: L.companies(this.limit(p.maxCompanies, 'companies', L.unlimited)) });
    lines.push({ check: true, text: this.isUnlimited(p.maxDocumentsPerMonth, 'docs') ? L.unlimitedDocs : L.docs(Number(p.maxDocumentsPerMonth || 0).toLocaleString()) });
    lines.push({ check: true, text: L.storage(this.storage(p.maxStorageBytes)) });
    const count = (p.enabledFeatureNames || []).length;
    if (count) lines.push({ check: true, text: L.featureCount(count) });
    for (const h of this.highlights(p, catalog, 4, L.lang)) lines.push({ check: true, text: h });
    for (const m of this.missingVsNext(p, nextPlan, catalog, 2, L.lang)) lines.push({ check: false, text: m });
    if (!p.isPermanentFree && p.trialDurationDays > 0) lines.push({ check: true, text: L.trial(p.trialDurationDays) });
    return lines;
  },
};
