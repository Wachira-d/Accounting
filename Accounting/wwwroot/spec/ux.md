# NextAcc — Design System & UX/UI Master Spec

> เอกสาร master ของระบบ — กำกับ UX/UI, flow, design tokens, accessibility, ฯลฯ.
> ทุก page ใหม่ + การปรับปรุงต้องผ่าน checklist ที่กำหนดในเอกสารนี้
>
> เปิดอ่านได้ที่ `/spec/ux.md` (serve ผ่าน wwwroot/spec/)
>
> **Last updated**: 2026-06 · **Status**: Living document

---

## 1. หลักการ (Design Principles)

1. **ทำงานครบในจอเดียว** — ลด tab-hopping และ context switch ทุกที่ทำได้
   หน้า "เอกสาร" ต้องบอก lifecycle / aging / GL posting / ไฟล์แนบ / ใบ 50 ทวิ
   ครบในที่เดียว
2. **ความถูกต้องบัญชีไทยมาก่อน UX สวย** — ทุก confirm/refresh ต้องสะท้อน
   trial balance, AP/AR aging, VAT ภพ.30, WHT ภงด.1/3/53 ที่ถูกต้อง.
   ห้ามแสดง "สำเร็จ" ถ้า GL ไม่ balanced.
3. **เห็นเหตุผลทุก decision** — ทุกการคำนวณ (WHT, VAT, suggestion, OCR
   match) ต้องมี ReasoningTrace / hint / tooltip ให้ผู้ใช้คลิกดู
4. **One device, full power** — ทุกหน้าต้องใช้ได้บน mobile 360px →
   desktop 1920px เท่าเทียม. หน้าหลัก (Document, Payroll, Bank) ต้องมี
   touch-optimized mobile path
5. **Idempotent + reversible** — ทุก action อนุญาตให้ทำซ้ำได้โดยไม่
   เสียหาย (idempotency key, retry-safe), และมี undo/void path

---

## 2. Design Tokens (CSS Variables)

ตัวแปรกลางอยู่ที่ `wwwroot/css/style.css:root`. ห้ามใส่ค่า hex ตรงในหน้า
page-level ยกเว้นกรณีพิเศษ (ใช้ `var(--…)` เสมอ).

### 2.1 Color palette

| Token | ค่า | การใช้ |
|---|---|---|
| `--primary` | `#4F46E5` indigo-600 | ปุ่มหลัก, link, brand |
| `--primary-dark` | `#4338CA` | hover/active |
| `--secondary` | `#0EA5E9` sky-500 | accent secondary, info chip |
| `--accent` | `#F59E0B` amber-500 | highlight, warning ระดับ 1 |
| `--success` | `#10B981` emerald-500 | ✓ บันทึก/อนุมัติ/ชำระแล้ว |
| `--warning` | `#F59E0B` | กำหนดส่ง ภงด./รอ approve |
| `--danger` | `#EF4444` red-500 | error/void/reject/over-budget |
| `--info` | `#3B82F6` blue-500 | hint, info card |
| `--gray-50..900` | tailwind gray ramp | text/border/bg neutrals |

### 2.2 Spacing & shape

| Token | ค่า | การใช้ |
|---|---|---|
| `--radius` | `8px` | card, input, btn ปกติ |
| `--radius-lg` | `12px` | modal, panel |
| `--shadow-sm/-md/-lg/-xl` | ramp | elevation 1-4 |
| `--header-height` | `64px` | sticky top |
| `--sidebar-width` | `260px` | desktop nav |

### 2.3 Typography

```
--font-sans: 'Noto Sans Thai', 'Sarabun', 'Tahoma', 'Sukhumvit Set',
             'Inter', 'Helvetica Neue', Arial, system-ui, sans-serif;
```

Scale (rem):
| Class | Size | Line-height | การใช้ |
|---|---|---|---|
| `.page-title` | 1.75 / 1.5 (mobile) | 1.2 | h1 ของหน้า |
| `.page-subtitle` | .95 | 1.5 | ใต้ title |
| `.section-title` | 1.15 | 1.3 | h2 ของ card |
| body | .95 | 1.5 | default |
| `.text-sm` | .85 | 1.45 | meta, helper |
| `.text-xs` | .75 | 1.4 | badge, micro-copy |

### 2.4 Status pill recipe

ทุกหน้าใช้รูปแบบเดียวกัน (กลม + bg อ่อน + text เข้ม):
```html
<span class="pill pill-success">✓ ชำระแล้ว</span>
<span class="pill pill-warning">⚠ รอ approve</span>
<span class="pill pill-danger">✗ ยกเลิก</span>
<span class="pill pill-info">Draft</span>
```

---

## 3. Layout System

### 3.1 โครงสร้าง

```
┌─────────────────────────────────────────────────┐
│ Topbar (64px sticky)                            │
│  ☰ | logo | breadcrumb | search | actions | 👤 │
├──────────┬──────────────────────────────────────┤
│ Sidebar  │ Main content                         │
│ (260px,  │  ┌────────────────────────────────┐  │
│  desktop │  │ Page header (title/actions)    │  │
│  only)   │  │ Toolbar (filters)              │  │
│          │  │ Content cards                  │  │
│          │  │ Pagination                     │  │
└──────────┴──────────────────────────────────────┘
                                  Mobile bottom-nav
```

### 3.2 Breakpoints

| Token | Min width | Behavior |
|---|---|---|
| `mobile` | 0 → 480 | single column, bottom nav, FAB ขึ้นบน |
| `mobile-lg` | 481 → 720 | 2-column form, no sidebar |
| `tablet` | 721 → 1024 | sidebar collapsed → icon-only |
| `desktop` | 1025+ | full sidebar 260px |

### 3.3 Responsive rules

- **Hamburger** ต่ำกว่า 1024px → overlay sidebar
- **Bottom-nav** ต่ำกว่า 720px — 4 ปุ่มหลัก (Dashboard / Documents / Bank / More)
- **Tables** ต่ำกว่า 720px → ย่อเป็น card list (อย่าให้ผู้ใช้ scroll ขวา)
- **Modal** ต่ำกว่า 480px → fullscreen sheet (slide-up)
- **Touch target** ขั้นต่ำ 44 × 44px (Apple HIG) สำหรับทุก clickable

---

## 4. Page anatomy (มาตรฐาน)

ทุกหน้าใหม่ใช้โครงสร้างเดียวกัน:

```html
<div class="page-header">
  <div>
    <h1 class="page-title">ชื่อเรื่อง</h1>
    <p class="page-subtitle">subtitle อธิบาย scope หน้า</p>
  </div>
  <div class="page-actions">
    <!-- primary action -->
  </div>
</div>

<div class="toolbar">
  <!-- search + filter chips -->
</div>

<div class="card">
  <!-- main content (table / form / detail) -->
</div>

<div class="pagination"></div>
```

### 4.1 Empty state มาตรฐาน

```html
<div class="empty-state">
  <div class="empty-icon">📄</div>
  <h3>ยังไม่มี{ชนิด}</h3>
  <p>อธิบาย action ขั้นต่อไป</p>
  <button class="btn btn-primary">+ สร้าง{ชนิด}แรก</button>
</div>
```

### 4.2 Loading state

- **Skeleton row** (3 แถวเทาเรียบ) สำหรับ table
- **Spinner** เฉพาะปุ่มหลังกดส่งฟอร์ม
- **อย่าใช้ full-screen spinner** ยกเว้น initial load

### 4.3 Error state

```html
<div class="card error-card">
  <div class="error-icon">⚠</div>
  <p>โหลดข้อมูลไม่สำเร็จ</p>
  <p class="text-sm text-gray-500">รายละเอียด...</p>
  <button class="btn btn-secondary" onclick="...">ลองใหม่</button>
</div>
```

---

## 5. Form Conventions

### 5.1 Layout

- **Stack ตามแนวตั้ง** — label ด้านบน, input ด้านล่าง (ทั้ง mobile/desktop)
- **2-column** ใช้ `.form-row` (collapse เป็น 1-column ที่ ≤720px)
- **Required field** ติด `*` หลัง label + `aria-required="true"`
- **Helper text** ใน `.form-help` (สีเทา 11px) ใต้ input
- **Inline error** สีแดง ติดใต้ field ที่ผิด — อย่าใช้ alert popup

### 5.2 Input behavior

| Type | กฎ |
|---|---|
| Money | type=number, step=0.01, thousand-separator แสดงผล |
| Date | `<input type="date">` + accept พ.ศ./ค.ศ. (parse ทั้งสอง) |
| Tax ID | 13 หลัก auto-format `X-XXXX-XXXXX-XX-X` |
| Phone | mask `0XX-XXX-XXXX` |
| Address | ใช้ thai-address.js auto-complete (sub-district → district → province → postal) |
| File | drag-drop + click + paste — แสดง preview ก่อนอัปโหลด |

### 5.3 Validation

- Validate ทั้ง client + server (server เป็น source of truth)
- ปุ่ม submit ปิดไม่ได้ — ให้ user กดได้ตลอด แล้วบอก error ที่ field
- หลังบันทึก → toast เขียว + refresh data (อย่าเด้งกลับ list โดยไม่บอก)

---

## 6. Data Display

### 6.1 Tables

- **Sticky header** เสมอ
- **Sortable column** indicator `▲▼` + cursor pointer
- **Row hover** สี `--gray-50`
- **Striped** ห้าม — ทำให้อ่านยากเมื่อมี badge สีในแถว
- **Action cell** อยู่ขวาสุด, ปุ่มไอคอนเล็ก (24-28px)
- **Pagination** กลางล่าง + page-size selector มุมขวา
- **Total row** sticky ล่างเมื่อมียอดรวม (เช่น aging, GL)

### 6.2 Money

- **Negative** สีแดง + ครอบวงเล็บ `(1,234.56)` (มาตรฐานบัญชี)
- **Zero** สีเทา (อย่าใช้สีเดียวกับ positive)
- **Currency** วาง prefix `฿` หรือ suffix ตาม locale
- **2 ทศนิยม** เสมอ ใช้ `Intl.NumberFormat('th-TH')`

### 6.3 Date

- **List view**: `dd/MM/yyyy` (พ.ศ.)
- **Detail/PDF**: `1 มกราคม 2569`
- **Relative**: "5 นาทีที่แล้ว", "เมื่อวาน" (ใน notification, audit)
- **อย่าใช้** ISO format (`2026-06-13`) ในหน้า user-facing

---

## 7. Interaction Patterns

### 7.1 Confirm dialog

ใช้สำหรับ destructive action เท่านั้น (delete, void, reject):
- หัวข้อ verb + object: "ยกเลิกเอกสารนี้?"
- ผลที่ตาม: "GL จะถูก reverse, ไม่สามารถย้อนกลับได้"
- ปุ่ม danger สีแดงทางขวา (Western pattern) — primary action เป็น "ยกเลิก" สีเทา

### 7.2 Toast

| ประเภท | สี | อายุ | ตำแหน่ง |
|---|---|---|---|
| Success | green | 3s | bottom-right |
| Error | red | 6s + ปุ่ม "ดูรายละเอียด" | bottom-right |
| Warning | amber | 5s | bottom-right |
| Info | blue | 4s | bottom-right |

อย่าใช้ toast บอก validation error — ติดที่ field

### 7.3 Modal vs Drawer vs Inline

| Use case | UI |
|---|---|
| Create/edit ไม่กี่ field | Modal |
| Detail view / many tabs | Drawer (slide จากขวา) |
| Quick action ไม่เกิน 3 field | Inline expand row |
| Wizard 3+ steps | Full-page route (อย่ายัด modal) |
| Mobile | ทุกอย่าง → bottom sheet (slide up) |

### 7.4 Multi-select

- **Bulk action bar** ลอยที่บน toolbar เมื่อเลือก ≥ 1 แถว
- บอกจำนวนที่เลือก: "เลือก 5 รายการ"
- ปุ่ม "เลือกทั้งหมด" / "ยกเลิก"

---

## 8. Accessibility (A11Y)

ทุกหน้าต้องผ่าน checklist นี้:

- [ ] **Keyboard nav** — Tab, Enter, Esc ทำงานครบ
- [ ] **Focus ring** เห็นชัด (`outline: 2px solid var(--primary)`)
- [ ] **ARIA labels** บนปุ่ม icon-only
- [ ] **Color contrast** อย่างน้อย WCAG AA (4.5:1 text, 3:1 large)
- [ ] **Form labels** ผูก `<label for>` ทุก input
- [ ] **Error messages** อ่านได้ด้วย screen reader (`aria-describedby`)
- [ ] **Loading state** แสดง `aria-busy="true"`
- [ ] **Skip-to-content** ลิงก์แรกของ topbar
- [ ] **No color-only meaning** — ใช้ icon + text กำกับเสมอ
- [ ] **Reduced motion** เคารพ `prefers-reduced-motion`

---

## 9. Mobile-specific UX

### 9.1 Touch ergonomics

- **Bottom navigation** — primary actions อยู่ thumb zone
- **Pull to refresh** บน list views
- **Swipe-to-action** บน row (ลบ/แก้ไข) — ไอคอนปรากฏใต้
- **Long-press** เปิด context menu (multi-select)
- **Sticky FAB** สำหรับ "+ สร้าง" หน้าหลัก

### 9.2 Camera-first flows

หน้า OCR upload ต้องมี:
- **ถ่ายภาพทันที** ปุ่มใหญ่ — เปิดกล้องผ่าน `<input type="file" accept="image/*" capture="environment">`
- **Crop UI** หลังถ่าย (auto-detect document edges)
- **Multi-page** เพิ่มหน้า + reorder ก่อน submit
- **Resume** ถ้า upload ล้มเหลว — keep file local, retry button

### 9.3 Offline-first

หน้าจำเป็น (Document list, Quick Sale, mobile-expense):
- Service Worker cache last view
- Show "Offline" banner เมื่อ network ขาด
- Queue mutations → sync เมื่อ online กลับมา
- เคารพ "เห็นข้อมูล" > "ทำธุรกรรม" — read-only ok offline; write รอ online

---

## 10. Module-by-module flows

### 10.1 Documents (เอกสารฝั่งขาย/ซื้อ)

```
List → Filter (status/type/date) → Click row
  → Detail drawer (preview + edit inline + history + GL)
  → Approve → Pay → Issue WHT cert → e-Tax email → Done
```

**กฎสำคัญ:**
- การเปลี่ยน Status ต้องผ่าน confirm
- Lifecycle pill บอกขั้นถัดไป
- "การลงบัญชี (Dr./Cr.)" ฝัง footer ทุก doc + PDF
- เอกสารฝั่งซื้อ: แยก **ใบแจ้งหนี้ซื้อ (21210 trade)** ↔ **ใบบันทึกค่าใช้จ่าย (21220 non-trade)** ↔ **ใบสำคัญจ่าย (จ่ายจริง)**
- บังคับ:
  - PV เดี่ยว + Credit → reject
  - Expense + Cash → reject (ใช้ PV แทน)

### 10.2 OCR Flow (ตาม business diagram)

```
Upload → Detect type + RD compliance
       → Vendor match (Name+TaxID+Branch)
         → ถ้าไม่ match + auto-create ไม่ได้ → prompt "เพิ่ม Contact"
       → PO check
         → มี PO → "เลือก PO" (alias mapping)
       → แนะนำ Stock vs Expense
       → User เลือก target type → CreateDocument (pre-fill ครบ)
```

**API path** (int_ key) → autoCreate=true (เหมือนเดิม)
**Web path** → suggest only — user ยืนยันสร้าง

### 10.3 Payroll Flow

```
Setup employees (org-chart + salary + tax allowances §47/47ทวิ)
  → Create run → Calculate (FOR UPDATE lock)
  → Review per-employee detail
  → Approve (FOR UPDATE lock) → Pay (FOR UPDATE + fiscal period guard)
  → Auto-generate ภงด.1 + สปส.1-10 + payslips → attach to run
  → Year-end → 50ทวิ รายปี (Zip) + ภงด.1ก
```

**Severance**: Preview → Confirm → Post JE (Dr Severance / Cr Cash)
**Year-end carry-forward** leave: POST `/leaves/carry-forward/{year}`

### 10.4 Bank Reconciliation

```
Import statement (CSV / OFX / e-statement)
  → Auto-match (rules → AI fallback)
  → Review unmatched → manual match / create JE
  → Sign-off → period locked
```

### 10.5 Tax filing

```
Month end → Open "Tax Export" page
  → Choose period
  → Download: ภงด.1, ภงด.3, ภงด.53, สปส.1-10, ภพ.30 (Zip)
  → Upload to RD/SSO portal manually (auto-submit จะมาตามมา)
```

---

## 11. Performance Budgets

| Metric | Target | Critical pages |
|---|---|---|
| First Contentful Paint | < 1.5s | login, dashboard, documents |
| Time to Interactive | < 3s | all |
| API call p95 | < 500ms | list endpoints |
| Payroll calculate | < 5s | 100 employees |
| OCR scan complete | < 15s | per document |
| PDF generate | < 2s | per document |

### 11.1 Loading strategy

- **Critical CSS** inline (above-fold)
- **Defer JS** สำหรับ vendor + non-critical
- **Lazy load images** + skeleton placeholder
- **Pagination** default 20/page (cap 200)
- **Virtual scroll** สำหรับ table > 500 rows

---

## 12. Notification & Email

### 12.1 In-app

ส่งผ่าน `NotificationEngine` → กระดิ่งบน topbar.
**Audience routing**:
- Document approval → Approver(s) + Owner
- Payroll paid → HR + Owner + Accounting
- Bank tx unmatched > 7 days → Accounting

### 12.2 Email

- **System SMTP/Graph** ตั้งได้ที่ admin/system-email
- **Per-company override** ที่ settings (เปลี่ยน sender ได้)
- **Templates** อยู่ที่ NotificationTemplates table (multi-lang)
- **Send-as** รองรับ (Graph): from = distribution list, mailbox = licensed user

---

## 13. Internationalization

- **Default**: ไทย (`th-TH`)
- **Secondary**: English (`en-US`) — toggle ที่ topbar
- **Translations**: `wwwroot/js/translations.js` (key-value)
- **Date/Number format**: ใช้ `Intl` API + locale ตามตั้งค่า
- **Currency**: บาท default, รองรับสกุลอื่นที่ Document level
- **Buddhist Era** — toggle setting; default แสดง พ.ศ. ใน Thai locale

---

## 14. Security UX

- **Login** → MFA optional, remember 30 days
- **Sensitive data** (Payroll, salary) — `SensitivityKind.Payroll` gate
- **Audit log** ทุก action สำคัญ — เปิดดูที่ `/pages/audit.html`
- **Sign-out** clear localStorage + redirect
- **Idle timeout** 60 นาที (configurable)
- **Password reset** email link 1 ครั้ง / 30 นาที expire

---

## 15. Testing Checklist (ก่อน merge)

ทุก PR ของหน้าใหม่/major change ต้องผ่าน:

- [ ] เปิดบน Chrome / Safari / Firefox (mac + windows + ios + android)
- [ ] Test ที่ 320, 375, 768, 1024, 1440px
- [ ] Keyboard nav ครบ flow
- [ ] Screen reader (VoiceOver / NVDA) อ่านเข้าใจได้
- [ ] Slow 3G — โหลดได้ไม่เกิน 5s
- [ ] Empty state + Loading state + Error state มีครบ
- [ ] บันทึกแล้วเห็นผลทันที (ไม่ต้อง F5)
- [ ] GL balance ตรวจสอบ (ถ้ามี posting)
- [ ] หน้าเอกสาร: lifecycle pill + Dr/Cr footer ครบ

---

## 16. Code Conventions

### 16.1 Page structure

ทุก page ใหม่:
```html
<!DOCTYPE html>
<html lang="th">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>ชื่อหน้า — NextAcc</title>
  <link rel="stylesheet" href="/css/style.css">
  <script src="/js/layout.js" defer></script>
  <script src="/js/api.js" defer></script>
  <script src="/js/i18n.js" defer></script>
</head>
<body>
  <div id="app"></div>
  <script>
    const Page = { /* state + methods */ };
    document.addEventListener('DOMContentLoaded', () => Page.init?.());
  </script>
</body>
</html>
```

### 16.2 API call

ใช้ `Layout.api()` (auto-attach JWT + retry + error toast):
```js
const api = Layout.api();
const res = await api.getDocuments({ status: 'Approved' });
```

อย่าใช้ `fetch()` ตรง ๆ — ขาด error normalization + i18n + retry

### 16.3 ห้าม

- ❌ Inline `<style>` ขนาดใหญ่ — ย้ายไป style.css
- ❌ Inline `onclick="..."` ที่ logic ยาว — แยกเป็น method
- ❌ Hard-code endpoint URL — ใช้ api.js helper
- ❌ Color hex ในหน้า — ใช้ var(--…)
- ❌ Pixel-perfect ที่ wreck mobile — มี breakpoint ครบ
- ❌ Direct DB query จาก controller — ผ่าน Service layer เสมอ

---

## 17. Roadmap / Living items

ติดตามใน Issues + commit history. หัวข้อหลักรอบหน้า:
- Auto-email payslip + 50ทวิ ให้พนักงาน
- RD/SSO portal API submit (e-Filing)
- OCR formula evaluator
- LIFF / Mobile app (React Native wrapper)
- Multi-currency end-to-end
- Audit report builder (per-account, per-period)

---

**ลิขสิทธิ์**: เอกสารนี้เป็นส่วนหนึ่งของ NextAcc — ห้ามนำไปใช้นอกบริบทโดยไม่ระบุที่มา
