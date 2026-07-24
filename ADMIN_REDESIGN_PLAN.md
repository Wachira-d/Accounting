# ADMIN_REDESIGN_PLAN.md — แผน redesign ระบบ Admin (จัดการ user/บริษัท/การชำระเงิน/ต่ออายุ)

> เอกสาร handoff สำหรับรอบพัฒนา (Opus 4.8) — วิเคราะห์จากการสำรวจโค้ดจริงทั้งระบบ
> (23 ก.ค. 2026). อ่านคู่กับ CLAUDE.md (กฎเหล็ก) และ DOCUMENT_FLOW.md

---

## ✅ สถานะการพัฒนา (อัปเดต 24 ก.ค. 2026)

| WP | สถานะ | หมายเหตุ |
|---|---|---|
| **A1** readonly-after-expiry | ✅ เสร็จ | middleware + kill-switch `Subscription:Enforcement:Mode` (Off/LogOnly/**Enforce**), default LogOnly |
| **A2** บังคับ Suspended | ✅ เสร็จ | +SuspendReason/SuspendedAt + endpoint ตั้งเหตุผล + UI prompt |
| **A3** cascade + jobs | ✅ เสร็จ | license→company cascade + ProcessExpired/Notifications รันรายวันแล้ว |
| **A4** role ตาย | ✅ เสร็จ | "SystemAdmin,Admin"→"SystemAdmin" (7 จุด) |
| **C1** manual/waived payment | ✅ เสร็จ | RecordManualPaymentAsync → เส้น approve เดิม + UI modal |
| **B2** ใบเสร็จ/ใบกำกับ | ✅ เสร็จ | gen ตอน approve + เก็บ + อีเมล + ดาวน์โหลด; §86/4 เมื่อ platform จด VAT |
| **B1** ใบแจ้งหนี้ต่ออายุ | ✅ เสร็จ | 15 วันก่อนหมดอายุ ผ่าน job (idempotent ต่อ EndDate) |
| **B3** ลูกค้าโหลดเอง | 🟡 endpoint เสร็จ | หน้า subscription.html tenant ยังไม่เพิ่มปุ่ม (endpoint พร้อม) |
| **B4** เลขรันแยก | ✅ เสร็จ | RCPT/TINV/SINV แยกจากเอกสาร tenant |
| **C2** dunning | ✅ ครอบแล้ว | ProcessSubscriptionNotifications (ทวง 3 ระดับ) + A3 flip อัตโนมัติ |
| **F1** revenue dashboard | ✅ เสร็จ | หน้า revenue.html + endpoint (MRR/ARR/กราฟ/expiring/PastDue/conversion/คิวสลิป) |
| **D1** สร้าง user | ✅ เสร็จ | + ผูกบริษัท/role + set-password link |
| **D2** reset password | ✅ เสร็จ | token 24 ชม. + อีเมล/copy-link |
| **C4** payments redesign | ⬜ ค้าง | filters/ยอดรวม/aging/export/bulk approve |
| **E1** company 360° | ⬜ ค้าง | หน้า detail รวม subscription/usage/members/activity |
| **C3/D3/D4/E2/E3/F2/F3** | ⬜ ค้าง | Phase 4 |

> ⚠️ **ต้องทดสอบบน Windows/Postgres ก่อนเปิด Enforce**: CI ไม่มี Postgres —
> enforcement gate, renewal, receipt generation ผ่าน brace-check + node --check
> เท่านั้น. เปลี่ยน `Subscription:Enforcement:Mode` เป็น `Enforce` หลัง monitor
> log 1-2 สัปดาห์.

---

## 0. สรุปสถานะปัจจุบัน (จากการสำรวจโค้ดจริง)

### มีแล้ว (ใช้งานได้)
| ส่วน | รายละเอียด | ไฟล์หลัก |
|---|---|---|
| Admin panel 16 หน้า | dashboard, customers, users, account-subscriptions, plans, payments (สลิป), integrations, coa-template, site-settings, system-email, ocr-config, ai-config, audit-log, error-log, background-jobs, login | `wwwroot/admin/*` + `AdminController.cs` (~40 endpoints, `[Authorize(Roles="SystemAdmin")]`) |
| Subscription 2 ชั้น | `Subscription` (ต่อบริษัท) + `AccountSubscription` (License ต่อ user คลุมหลายบริษัท) + `PlanTemplate` + `SubscriptionHistory` | `Models/Entities/Subscription.cs`, `AccountSubscription.cs`, `SubscriptionService.cs` (1,964 บรรทัด) |
| ชำระเงินด้วยสลิป | ลูกค้า submit + upload สลิป → admin review approve/reject → อนุมัติแล้ว auto ต่ออายุ (Subscription + cascade AccountSubscription) | `SubscriptionPayment`, `SubscriptionController.cs:147-198`, `SubscriptionService.cs:1313-1600`, `admin/payments.html` |
| เตือนหมดอายุ | `AccountPlanExpiryReminderJob` (ทุก 6 ชม.) อีเมล 7/3/1 วัน + วันหมดอายุ (bitmask กันส่งซ้ำ) + flip AccountSubscription → Expired | `Services/Background/AccountPlanExpiryReminderJob.cs` |
| จัดการมือโดย admin | เปลี่ยน plan/status/dates/limits ต่อบริษัท, extend License, attach/detach บริษัทเข้า License, provision License ให้ user (enterprise) | `AdminController.cs:690-1022`, `AdminAccountSubscriptionController.cs` |
| Permission ต่อบริษัท | PermissionKeys + RequirePermission + CompanyRole (custom role) — Owner/SystemAdmin auto-pass | `Filters/RequirePermissionAttribute.cs`, `PermissionKeys.cs` |
| เชิญ user เข้าบริษัท | CompanyInvitation + token accept + ลิงก์ manual เมื่อไม่มี SMTP | `CompanyController.cs:66`, `InvitationController.cs` |

### ❌ ช่องโหว่/ขาด (เรียงตามความรุนแรง)
1. **🔴 หมดอายุแล้วระบบไม่บล็อกอะไรเลย (soft expiry = รั่วรายได้)** — `GetEffectivePlanAsync` คำนวณ `IsActive/InGrace` ครบ แต่**ไม่มี middleware ไหนใช้บล็อก write**. `SubscriptionMiddleware` บล็อกเฉพาะ `Cancelled/Suspended` — `Expired/PastDue` ใช้งานต่อได้ปกติไม่จำกัด. `TrialConfig.BlockAccessOnExpiry` + comment "readonly mode" ในโค้ด = ไม่มีจริง
2. **🔴 `CompanyStatus.Suspended` ไม่ถูกบังคับ** — admin กด suspend บริษัทได้ แต่ผู้ใช้บริษัทนั้นใช้งานต่อได้ทุกอย่าง
3. **🟠 ไม่มีใบแจ้งหนี้/ใบเสร็จ/ใบกำกับภาษีของค่าบริการ SaaS** — `SubscriptionPayment` มีแค่เลขภายใน SP-xxxx; ลูกค้าที่จ่ายเงินไม่ได้เอกสารอะไรเลย (platform เป็นผู้ประกอบการ ต้องออกใบเสร็จ/ใบกำกับตามกฎหมายเมื่อลูกค้าเป็น VAT registrant)
4. **🟠 ต่ออายุแบบ manual ไม่มี payment record** — ปุ่ม extend/changeDates/changeStatus แก้ DB ตรง ๆ ไม่สร้าง `SubscriptionPayment` → รายรับที่รับนอกระบบ (โอน+แจ้ง LINE) ไม่มีร่องรอยการเงิน ตรวจสอบย้อนไม่ได้
5. **🟠 Job หมดอายุไม่ cascade** — flip เฉพาะ `AccountSubscription.Status` ไม่แตะ `Subscription` ต่อบริษัทใต้ License → สถานะสองชั้นไม่ตรงกัน; และ `ProcessExpiredSubscriptionsAsync`/`ProcessSubscriptionNotificationsAsync` (per-company) **ไม่มี scheduled job เรียก** — ต้องกด manual ที่ background-jobs.html
6. **🟡 Admin จัดการ user ไม่ครบ** — สร้าง user ไม่ได้ / สั่ง reset รหัสผ่านไม่ได้ / ลบจริง (PDPA) ไม่ได้ / resend invite ไม่ได้ — ทำได้แค่ disable + toggle SystemAdmin
7. **🟡 Role string `"Admin"` ตาย** — `[Authorize(Roles="SystemAdmin,Admin")]` ใน NotificationController + OcrController 6 จุด: enum ไม่มีค่า Admin → grant นี้ไม่มีวัน match
8. **🟡 ไม่มี dashboard ธุรกิจ** — admin dashboard เป็น KPI ทั่วไป ไม่มี MRR / รายการใกล้หมดอายุ / past-due / trial conversion / คิวสลิปค้าง
9. ⚪ ไม่มี audit เฉพาะ action ของ admin (เปลี่ยน plan/extend ใครทำเมื่อไร — มี SubscriptionHistory บ้างแต่ไม่ครบทุก endpoint)
10. ⚪ ไม่มี impersonation ("เข้าดูในนามลูกค้า") สำหรับ support

---

## 1. หลักการ redesign

1. **เงินต้องมีร่องรอยเสมอ** — ทุกการต่ออายุ (สลิป/manual/goodwill) ต้องเกิด `SubscriptionPayment` record (แม้ amount=0 กรณี goodwill ก็ record ชนิด Waived) — เลิกแก้วันที่ตรง ๆ แบบไร้เอกสาร
2. **Enforcement ต้องจริง** — expiry/suspend ที่ประกาศไว้ในโค้ดต้องมีผลจริงใน middleware ชั้นเดียว (จุดเดียวคุมหมด) พร้อม grace ตาม config เดิม (`GracePeriodDays=7`, `DeactivationDaysAfterExpiry=14`)
3. **ลูกค้า self-service ให้มากสุด** — เห็นใบแจ้งหนี้/สถานะ/ประวัติจ่าย/ดาวน์โหลดใบเสร็จเอง → ลดงาน admin
4. **Reuse ของที่มี** — PDF engine (`PdfGenerationService`), OCR stack (อ่านสลิป), NotificationEngine, AuditLog hash-chain, pattern `StatutoryRemittance` (dashboard→จ่าย→JE→แนบใบเสร็จ) ใช้เป็นแม่แบบ UX ได้เลย
5. **แยก release ได้ทีละ WP** — แต่ละ work package จบในตัว ทดสอบได้อิสระ

---

## 2. Work Packages (แบ่งส่วนงานละเอียด)

### 🔴 WP-A — Enforcement Core (ทำก่อนสุด: อุดรั่วรายได้)
> ไฟล์หลัก: `Middleware/SubscriptionMiddleware.cs`, `SubscriptionService.cs`, `AccountPlanExpiryReminderJob.cs`

- **A1. Readonly-after-expiry middleware**
  - ใช้ `GetEffectivePlanAsync` (มีอยู่แล้ว `SubscriptionService.cs:512`) ใน `SubscriptionCheckMiddleware`
  - นโยบาย: `Expired/PastDue` เกิน grace → บล็อก **write** (POST/PUT/PATCH/DELETE) ต่อ route ที่มี `{companyId}` → 402/403 พร้อม message ชี้หน้า renew; **read ยังได้** (ตาม `TrialConfig.BlockAccessOnExpiry=false` default = read-only ไม่ใช่ block ทั้งหมด)
  - Whitelist เสมอ: `/subscription/*`, `/payments*` (จ่ายเพื่อต่ออายุ), `/auth/*`, `/me`, export ข้อมูลตัวเอง (PDPA)
  - อยู่ใน grace → ใส่ header + banner เตือน (ไม่บล็อก)
  - Cache ผล effective-plan ต่อ request/สั้น ๆ กัน N+1 ทุก request
  - ⚠️ ระวัง: integration API (TakeTime) โดนบล็อกด้วยเมื่อหมดอายุ — ตั้งใจให้โดน (คือ point ของ enforcement) แต่ response ต้องเป็น JSON ชัดเจนให้ partner จัดการ retry
- **A2. บังคับ `CompanyStatus.Suspended`** — เช็คใน middleware เดียวกัน: Suspended → บล็อกทุกอย่างยกเว้น read เจ้าของ + หน้า billing; เพิ่มช่อง SuspendReason + โชว์เหตุผลใน 403
- **A3. Cascade + scheduled jobs** — `AccountPlanExpiryReminderJob`: ตอน flip Expired ให้ cascade `Subscription` rows ใต้ License → Expired ด้วย; เพิ่ม hosted job เรียก `ProcessExpiredSubscriptionsAsync` + `ProcessSubscriptionNotificationsAsync` (per-company) รายวัน (ตอนนี้ manual-only)
- **A4. เก็บกวาด role ตาย** — แทน `"SystemAdmin,Admin"` → `"SystemAdmin"` (Notification 1 + Ocr 6 จุด) หรือนิยาม Admin จริงถ้าต้องการชั้นรอง
- **Acceptance**: บริษัทหมดอายุเกิน grace → สร้างเอกสารไม่ได้แต่เปิดดูได้ + จ่ายเงินต่ออายุได้; suspend → ใช้ไม่ได้; งวดหมดอายุ per-company flip เองไม่ต้องกดปุ่ม

### 🟠 WP-B — เอกสารค่าบริการ SaaS (ใบแจ้งหนี้/ใบเสร็จ/ใบกำกับ)
> ใหม่: entity `SaasBillingDocument` (หรือขยาย `SubscriptionPayment`) + PDF

- **B1. ใบแจ้งหนี้ต่ออายุอัตโนมัติ** — X วันก่อนหมดอายุ (config, default 15) gen ใบแจ้งหนี้ (เลขรัน `SINV-YYYYMM-####`) ยอดตาม PlanTemplate + BillingCycle ปัจจุบัน → แนบลิงก์จ่ายในอีเมลเตือน (ผูกกับ reminder job เดิม)
- **B2. ใบเสร็จ/ใบกำกับภาษีเมื่ออนุมัติจ่าย** — hook ท้าย `ReviewPaymentAsync` (approve): gen PDF ใบเสร็จรับเงิน (+ใบกำกับภาษีเต็มรูปเมื่อ platform จด VAT — ใช้ template §86/4 ที่มีอยู่ใน `PdfGenerationService` เป็นแม่แบบ, ข้อมูลผู้ขาย = platform จาก SiteSettings/ENV) → เก็บไฟล์ + อีเมลให้ลูกค้า
- **B3. ลูกค้าดาวน์โหลดเอง** — หน้า subscription ฝั่ง tenant: ประวัติจ่าย + ปุ่มดาวน์โหลดใบแจ้งหนี้/ใบเสร็จ; admin resend ได้
- **B4. เลขรันแยกระบบ** — ห้ามปนกับเลขเอกสาร tenant (คนละ sequence, ไม่มี companyId — เป็นเอกสารของ platform)
- **Acceptance**: จ่าย → ได้ใบเสร็จ PDF ในอีเมล + โหลดซ้ำได้; ก่อนหมดอายุได้ใบแจ้งหนี้แนบลิงก์จ่าย
- หมายเหตุ: การลงบัญชีฝั่ง platform เอง (ถ้า platform ใช้ NextAcc ทำบัญชีตัวเอง) = out of scope WP นี้ — บันทึกเป็น option ไว้ (สร้างเอกสารเข้า company ของ platform อัตโนมัติผ่าน integration API ตัวเอง)

### 🟠 WP-C — Payment & Renewal ครบวงจร
- **C1. Admin "บันทึกรับเงิน (manual)"** — ปุ่มใน payments.html + account-subscriptions.html: กรอกยอด/วันที่/ช่องทาง/หมายเหตุ → สร้าง `SubscriptionPayment` (Status=Approved, Method=ManualByAdmin) → วิ่งเข้า `ReviewPaymentAsync` เส้นเดิม (ต่ออายุ+ประวัติ+ใบเสร็จ WP-B2 อัตโนมัติ) → **แทนที่** การใช้ extend/changeDates เปล่า ๆ; ปุ่ม extend เดิมคงไว้แต่บังคับเลือกเหตุผล (Goodwill/Compensation/Correction) + สร้าง payment record ยอด 0 ชนิด Waived
- **C2. Dunning ค่าบริการ** — หลัง EndDate: status → PastDue อัตโนมัติ (job WP-A3) + อีเมลทวง 3 ระดับ (หมดอายุ/7 วัน/สิ้น grace ก่อนบล็อก) — reuse pattern `OverdueDunningJob` (มี idempotency ต่อใบแล้ว)
- **C3. สลิป OCR assist** — ตอนลูกค้า upload สลิป: อ่านยอด/วันที่/เลขอ้างอิงด้วย OCR stack เดิม → prefill + เทียบยอดกับใบแจ้งหนี้ → badge "ยอดตรง ✓" ในคิว review (ลดเวลา admin; **ตามกฎเหล็ก #1**: ผ่าน `IAiOrchestrator` + distillation ถ้าใช้ AI)
- **C4. payments.html redesign** — filters (สถานะ/เดือน/plan), ยอดรวมต่อเดือน, aging ของคิวค้าง review, export MiniExcel, bulk approve (เฉพาะยอดตรง)
- **Acceptance**: ทุกบาทที่เข้ามี record + เอกสาร; ไม่มีทางต่ออายุแบบไร้ร่องรอย

### 🟡 WP-D — Admin User Management ครบ
- **D1. สร้าง user จาก admin** — form สร้าง + ส่งอีเมล verification/ตั้งรหัส (reuse invitation pattern); เลือกผูกบริษัท+role ได้เลย
- **D2. Admin สั่ง reset รหัสผ่าน** — ปุ่มใน users.html → gen `PasswordResetToken` (field มีแล้ว `User.cs:32`) + ส่งอีเมล/copy link
- **D3. ลบ/anonymize user (PDPA ม.30+ข้อจำกัด พ.ร.บ.บัญชี)** — guard: เป็น Owner บริษัทเดียว → ห้ามลบจนโอน ownership; ลบ = anonymize ข้อมูลส่วนตัว (ชื่อ/อีเมล/เบอร์→hash) คง FK/audit (ตรงกฎ MAX(retention) ใน CLAUDE.md)
- **D4. หน้า user detail 360°** — บริษัท+role ทั้งหมด, last login, sessions (revoke refresh token), invitation ค้าง + resend, License ที่ถือ
- **Acceptance**: วงจรชีวิต user ทำจาก admin ได้ครบโดยไม่ต้องแตะ DB

### 🟡 WP-E — Company/Tenant 360° + Support
- **E1. หน้า company detail (admin)** — รวมในหน้าเดียว: subscription/License, usage vs limit (docs/JE/storage/OCR — มี `GetAggregateUsage` แล้ว), สมาชิก+role, integrations, กิจกรรมล่าสุด, ปุ่ม suspend (พร้อมเหตุผล → WP-A2)/attach License/บันทึกรับเงิน (WP-C1)
- **E2. Usage alert** — ใกล้เต็ม limit (>85%) → แจ้ง admin + ลูกค้า (upsell opportunity)
- **E3. Impersonation** — "เข้าดูในนามลูกค้า" แบบ read-only token อายุสั้น + ลง AuditLog ทุกครั้ง (banner แดงบนจอตลอด session) — เพื่อ support; **ต้อง review security ก่อน merge**
- **Acceptance**: ตอบคำถาม support ลูกค้า 1 ราย ครบจากหน้าเดียว

### ⚪ WP-F — Business Dashboard & Ops
- **F1. Revenue dashboard** — MRR/ARR (คำนวณจาก subscription active × ราคา), ใกล้หมดอายุ 7/30 วัน (ตาราง+ปุ่มบันทึกรับเงิน), PastDue list, trial→paid conversion, คิวสลิปค้าง, กราฟรายรับต่อเดือนจาก `SubscriptionPayment` approved
- **F2. background-jobs.html ยกระดับ** — ตาราง job: ชื่อ/รอบ/last run/ผลล่าสุด/ปุ่ม run (เพิ่ม entity `JobRunLog` เก็บผลรัน) แทนปุ่มเปล่า
- **F3. Admin action audit** — ทุก endpoint แก้ subscription/user/company ต้องเขียน AuditLog (hash-chain เดิม) + ปิดช่อง `SubscriptionHistory` ที่ endpoint ไหนยังไม่เขียน
- **Acceptance**: เจ้าของธุรกิจเปิด dashboard เดียวรู้: เงินเข้าเดือนนี้ / ใครกำลังหลุด / คิวอะไรค้าง

---

## 3. ลำดับทำ + dependency

```
Phase 1 (อุดรั่ว):      WP-A ทั้งหมด                          ← ทำก่อนสุด
Phase 2 (การเงินครบ):   WP-C1 → WP-B2 → WP-B1/B3 → WP-C2     (C1 ต้องมาก่อน B2 เพื่อให้ manual ก็ได้ใบเสร็จ)
Phase 3 (ops รายวัน):   WP-C4 → WP-F1 → WP-D1/D2 → WP-E1
Phase 4 (เสริม):        WP-C3 (OCR สลิป) → WP-D3/D4 → WP-E2/E3 → WP-F2/F3
```

## 4. ข้อควรระวังตอน implement (จากบริบทโปรเจกต์)
- **CI ไม่มี Postgres** — enforcement middleware + ReviewPayment flow ต้องเทสต์ DB จริงฝั่ง Windows; เขียน pure-logic test ได้เฉพาะส่วนคำนวณ (effective plan, เลขรันเอกสาร, ยอด MRR)
- Schema ใหม่ทุกตัวผ่าน `DatabaseMigrationHelper.ApplyMissingColumns` (ADD COLUMN IF NOT EXISTS) — ห้าม EF Migrations
- Excel export ใช้ MiniExcel เท่านั้น; frontend vanilla HTML+Tailwind ตามเดิม
- ถ้าแตะ AI (WP-C3) → กฎเหล็ก #1 เต็มรูป (orchestrator + distillation + kill-switch)
- อีเมล: ระบบมี fallback "copy link" เมื่อไม่มี SMTP — ทุกฟีเจอร์ที่ส่งอีเมลต้องมี fallback เดียวกัน
- Middleware enforcement (WP-A1) คือจุดเสี่ยงสุดของทั้งแผน — ผิด = ลูกค้าจ่ายแล้วใช้ไม่ได้ทั้งระบบ; ต้องมี kill-switch config (`Enforcement:Enabled=false`) + ทยอยเปิด (log-only mode ก่อน 1-2 สัปดาห์)

---
_อ้างอิงการสำรวจ: AdminController.cs (~40 endpoints), SubscriptionService.cs:512 (GetEffectivePlanAsync ที่ไม่ถูกใช้บังคับ), SubscriptionMiddleware.cs:167 (บล็อกแค่ Cancelled/Suspended), ReviewPaymentAsync:1440-1570 (เส้นต่ออายุที่จะ reuse), AccountPlanExpiryReminderJob.cs (job เดียวที่ schedule จริง)_
