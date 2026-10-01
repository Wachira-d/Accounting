# รอบ 201 ทีม PL — Platform / Audit / Security / Tools

ขอบเขต: BACKLOG §1.3 (A-PL1..A-PL11) + หมวด C-3 · C-4 (คำตัดสินข้อ 76/77) + หมวด B-9 (ส่วนที่ทำได้ก่อน) · ลำดับตาม §5 โดยทำ A-PL2 คู่กับ A-PL1
(คำสั่ง main agent: ให้ทีมอื่นมีที่รันเทสต์ DB ก่อน) · รวมงานทีม DV (merge `e97ba288`) ก่อนสร้าง baseline ของ checker ใหม่ตามคำสั่ง main agent

คอมมิต: `9d4f4033` (ชุด 1 audit) · `23d7a6de` (ชุด 2 tools/renderer/CMS) · `545cc3ea` (ชุด 3 C-3/C-4) · `d2aab79e` (merge DV + คำตัดสิน DV Q3) ·
`233ba81f` (ชุด 4 B-9) · คอมมิตนี้ = รายงาน + ติ๊กไฟล์ต้นทาง

## ตารางรายการ

| ID | สถานะ | ที่แก้ (file:method) | เทสต์ / ด่าน |
|---|---|---|---|
| A-PL1 | ✅ `9d4f4033` | `Data/AccountingDbContext.cs` `SaveChanges/SaveChangesAsync` → `DetachPendingAuditRows` → บันทึกข้อมูลหลัก → `LockAuditChain(Async)` (`pg_advisory_xact_lock(AdvisoryLockKey.AuditChain ต่อบริษัท · เรียงคีย์)`) → `ApplyAuditHashChain` → บันทึกแถว audit · ไม่มีธุรกรรม = เปิดเอง · `AddChainedAuditLog` ไม่ประทับตอน Add · ล้ม ⇒ คืนแถวเป็น Added แล้วโยนต่อ | `Db/AuditChainDbTests` (8 context พร้อมกัน ⇒ fork 0) · required_call_site ปรับ 3 + เพิ่ม 4 แถว (+ negative มือ: ถอดล็อก/เปลี่ยนเป็น session lock/ประทับตอน Add ⇒ ฟ้อง) |
| A-PL2 | ✅ `9d4f4033` | `.github/workflows/ci.yml` job `db-test` (service `postgres:16` · `--filter Category=Db` · ขนานกับ build ไม่ needs ใคร) · job `test` เดิมกรอง `Category!=Db` · `Accounting.Tests/Db/DbTestDatabase` (EnsureCreated ทั้งโมเดล · env `ACCOUNTING_TEST_PG` · `ACCOUNTING_DB_TEST_REQUIRED=1` ⇒ ไม่มีฐาน = ล้มดัง) | `AuditChainDbTests` 3 ข้อ รวมทิศตรงข้าม `Verifier_sees_fork_written_outside_the_lock` |
| A-PL3 | ✅ `9d4f4033` | `AuditChainVerifyJob.RunCycleAsync` + ตาราง `AuditChainCheckpoints` + `Helpers/AuditChainCheckpointPolicy` (Decide/Advance) · `VerifyHashChainAsync(companyId, afterId)` ค้น parent ก่อนช่วงในฐานจริง (`AuditHashChain.ExternalParents` → `Analyze(rows, anchors)`) · ตรวจเต็มทุก 28 วัน/เมื่อรอบก่อนพบปัญหา · พบปัญหา = ไม่ขยับ watermark | `AuditChainCheckpointTests` (8) · required_call_site 2 แถว |
| A-PL4 | ✅ `9d4f4033` (+ baseline หลัง merge DV `d2aab79e`) | 7 จุด → `AddChainedAuditLog` (MeteringAdmin · Admin ×2 · AuditMiddleware · AddOnPurchase · WithholdingTaxCert · Quota) · checker ใหม่ `tools/audit_direct_add_check.py` (ratchet ต่อไฟล์ · baseline 29 จุด/8 ไฟล์ของทีมอื่น · negative test ฉีดลงไฟล์จริงในตัว) · **ทุกจุดที่ยัง Add ตรงถูกประทับใน SaveChanges แล้ว** (A-PL1) | ลงทะเบียนใน `check_all.sh` ส่วน self-test |
| A-PL5 | ✅ `23d7a6de` | `tools/write_permission_gate_check.py` deny-list ratchet ทั้งโฟลเดอร์ Controllers (นอก WATCHED · นับด่านระดับคลาสในโหมดนี้ · baseline `write_permission_gate_baseline.txt` 547 endpoint ห้ามเพิ่ม) + negative test ฉีดลง `BankController` จริง + สองคลาสในไฟล์เดียว · attachment_gate (S2-C4/R2-C11) และ contact_taxid (R2-C10) **ปิดไปแล้วรอบ 193** — ยืนยันด้วย self-test ของ checker ทั้งสอง (เคส A1–A8/C2–C5 อยู่ในตัว) | `--self-test` ลงทะเบียนใน `check_all.sh` |
| A-PL6 | ✅ `23d7a6de` | `Helpers/DocumentBrandColor` ใช้ทั้ง HTML `BuildCss/BuildLayoutCss(…, doc.Brand?.PrimaryColor)` และ QuestPDF `BuildBranding` | `PlatformRound201Tests.PL6_*` (สองทิศ) · required_call_site 3 แถว |
| A-PL7 | ✅ `23d7a6de` | `CssThemeValue.SafeCustomCss` (`<` ⇒ `\3c `) ใน `GenerateThemeCssFromEntity` + `StorefrontThemeInfo.CustomCss` | `PL7_*` (สองทิศ) · required_call_site |
| A-PL8 | ✅ `23d7a6de` | `DocumentTemplateService.GetDefaultTemplateAsync` อ่านอย่างเดียว (ไม่มี ⇒ ค่าเริ่มต้น id ว่าง) · `EnsureDefaultTemplateAsync` + `POST document-templates/default/{type}` (`CompanySettings.Edit`) · `documents.html` ปุ่ม ⚡ + `document-templates.html` เปิดตัวแก้ใช้ POST | required_call_site 2 แถว · write_permission (WATCHED) |
| A-PL9 | ✅ `23d7a6de` | `PdfGenerationService.LoadHeadingCompanyContextAsync` + overload `ResolveDocumentHeadingAsync(…, shared)` · `EmailScheduleService.HeadingForAsync` (แคชต่อรอบ: บริษัท 1 ครั้ง/หัวต่อใบ 1 ครั้ง) | required_call_site 3 แถว · owner_action_wiring แถว S-12 ย้ายไป HeadingForAsync (`545cc3ea`) · 📋 ไม่มีเทสต์ DB เทียบผล (ตัวตัดสินหัวตัวเดิม — ต่างแค่โหลดครั้งเดียว) |
| A-PL10 | ✅ `23d7a6de` | `CmsCommerceService.UpdateOrderStatusAsync` — Void ล้ม ⇒ `ChangeTracker.Clear()` + โหลดออเดอร์ใหม่ + `ApplyStatus` + หมายเหตุ `[ERP-VOID-FAILED]` | required_call_site (before Void → Clear) · TEST_PLAN PL-05 |
| A-PL11 | ✅ `23d7a6de` | เติม `dd5ceb1e` ใน `review200-round2-sec.md` 11 แถว + หัว `team-Z.md` (ยืนยัน `merge-base --is-ancestor`) | doc_commit_sha_check |
| C-3 | ✅ `545cc3ea` | `Helpers/OwnerFeatureMask` · `SubscriptionService.CheckFeatureAccessAsync` (โหมดเงา: ผ่าน + `SubscriptionGateShadowHits` เหตุ `OwnerDisabledFeature`) · สวิตช์แยก `SiteSettings.OwnerFeatureMaskEnforced` (DEFAULT false) · `PUT api/admin/subscription-enforcement/owner-mask` · การ์ด 🔒 หน้าแอดมิน · EntitlementService คอมเมนต์ตรงความจริง | `PlatformOwnerRound201Tests.C3_*` (3) · required_call_site |
| C-4 | ✅ `545cc3ea` | `UserRole.PlatformSupport = 7` · `Helpers/OwnershipTransferPolicy` · `CompanyService.CreateAsync` (แอดมินแพลตฟอร์ม ⇒ support) · `CheckOwnershipTransferAsync`/`TransferOwnershipAsync` (audit chain `PLATFORM-OWNERSHIP-TRANSFER`) · `POST api/company/{id}/transfer-ownership` · `PermissionService` (support = คีย์งานตั้งค่าเท่านั้น) · ห้ามตั้ง/เชิญ support ผ่านหน้าทีม · `team.html` กล่องส่งมอบ + ป้าย · `usage.html` ป้าย | `C4_*` (4) · required_call_site 7 แถว · write_permission marker |
| B-9 | ✅ `233ba81f` (ส่วนที่ทำได้ก่อน) | ตาราง `PlatformHolidays` + `admin/platform-holidays.html` + `api/admin/platform-holidays` (แสดงงวดที่เลื่อนจากตัวตัดสินเดียวกับผู้อ่าน) · `Helpers/BusinessDayCalendar` · `TaxFilingDeadline` overload (…, holidays) · ผู้อ่าน `TaxCalendarService` + `StatutoryRemittanceService` · ตารางว่าง = เดิมทุกวัน | `BusinessDayCalendarTests` (4) · required_call_site 6 แถว |
| (คำสั่ง main agent · ผลยกเลิกใน CMS) | ✅ 7ab6cf72 | `Helpers/VoidResultNotice` · `CmsCommerceService.UpdateOrderStatusAsync` ประทับ `[ERP-VOID-NOTICE]` บนออเดอร์ · `CmsBookingService.SettleErpDocumentOnCancelAsync` ส่งเข้า notices + หมายเหตุการจอง | `VoidResultNoticeTests` · required_call_site |
| (DV Q3 คำตัดสิน main agent) | ✅ `d2aab79e` | แถวนอก chain รุ่นเก่า **ไม่เติม hash ย้อนหลัง** — `AuditHashChain.UnchainedNote` + `AuditChainVerifyResult.UnchainedCount/UnchainedLatestAt/UnchainedNote` · endpoint `verify-hash-chain` + job รายงานแยก | `Unchained_legacy_rows_are_reported_separately_not_as_tampered` |

📋 ที่เหลือ (ตั้งใจไม่ทำรอบนี้ — ไฟล์ทีมอื่น/ต้องตัดสิน):
- **B-9 ผู้อ่านอื่น** ยังเลื่อนเฉพาะเสาร์/อาทิตย์: `ComplianceService:314` · `TaxComplianceChecker:192` · `SsoLateFee` · `DepositPolicyResolver:823` · `PayrollService` (สปส.6-09 · ทีม PR2) ·
  §87 `SettlementPosting.WeekdaysAfter` (ทีม ST — `BusinessDayCalendar.BusinessDaysAfter` internal พร้อมต่อ ต้องส่งชุดวันหยุดผ่าน facts) — API overload พร้อมใช้ ไม่เปลี่ยนลายเซ็นเดิม
- **A-PL4 baseline 29 จุด** ในไฟล์ทีมอื่น (Lodging 19 · Auth 3 · Payroll 3 (PR2) · OCR 1 · LINE 1 · FixedAsset 1 (IN) · LodgingNightAuditJob 1) — ถูกประทับแล้วโดย A-PL1 · ย้ายเป็น `AddChainedAuditLog` ทีละไฟล์แล้วลด baseline

## ความเสี่ยงคอมไพล์ / พฤติกรรม (ยังไม่ได้คอมไพล์ — ไม่มี .NET SDK · CI คือ compiler ตัวแรก)
1. **A-PL1 ล็อก audit เป็นจุดร้อน**: ในธุรกรรมของผู้เรียก ล็อกถือจน commit ⇒ งานเขียนของบริษัทเดียวกันต่อคิวตั้งแต่ SaveChanges แรกที่มีแถว audit
   (ราคาของคำตัดสินข้อ 32) · deadlock ได้เฉพาะเมื่อคำขอหนึ่งถือล็อกอื่น (เช่นล็อกเอกสาร) **ก่อน** SaveChanges แรก แล้วอีกคำขอที่ถือล็อก audit ไปขอล็อกนั้น —
   PostgreSQL ตรวจเจอแล้วยกเลิกหนึ่งคำขอ (40P01 · ล้มดัง ไม่ใช่ chain เสีย) · ถ้าเจอบ่อยใน log ต้องย้ายการประทับไปตอน commit (`DbTransactionInterceptor`) — ไม่ทำรอบนี้
2. **A-PL1 เปลี่ยนจำนวนธุรกรรม**: คำขอที่ไม่มีธุรกรรมและมีแถว audit ตอนนี้ได้ธุรกรรมสั้น (ข้อมูลหลัก + audit atomic — ดีขึ้น) · คำขอที่ไม่มีแถว audit เดินทางเดิม
3. **A-PL2 EnsureCreated ทั้งโมเดลบน postgres:16** ยังไม่เคยรันใน CI — ถ้าล้มที่ขั้นสร้างตาราง = สัญญาณจริงว่าฐานใหม่สร้างไม่ได้ (job แยก ไม่ทำให้ build/test เดิมแดง แต่ workflow รวมจะแดง)
4. `AuditChainDbTests.Verifier_sees_fork_*` ใช้ `ExecuteSqlInterpolatedAsync` กับคอลัมน์ตามชื่อ property (ตามโมเดล EF ปกติ) — ถ้าโมเดลตั้งชื่อคอลัมน์ต่าง เทสต์นี้จะล้มที่ SQL
5. C-4 `UserRole.PlatformSupport` เป็นค่า enum ใหม่ — หน้าเว็บที่ map บทบาทแบบไม่มี default จะแสดงชื่อดิบ (team/usage แก้แล้ว · sensitivity/accept-invitation ไม่มีทางเกิดบทบาทนี้)
6. `IsNpgsql()` / `ExecuteSqlRawAsync(string, IEnumerable<object>, CancellationToken)` / `IDbContextTransaction` ผ่าน `var` — เปิดนิยามแล้ว แต่ยังไม่ผ่านคอมไพเลอร์จริง

## checker ที่รัน (ใน worktree ใต้ `.claude/` — undeclared_local ตรวจ 1587 ไฟล์ ไม่ใช่ 0)
ผ่าน: required_call_site (เต็มชุด — ดูบรรทัดท้าย) · record_arg · nullable_arg · using · undeclared_local · arg_type · service_interface · write_permission_gate (+self-test) ·
audit_direct_add (ใหม่) · dto_nullable_contract · string_quote_close · comment_line_break · tuple_name_merge · accessibility · identifier_space · html_attr_escape ·
onclick_js_string · enum_number_compare · dead_helper · settings_reader · gl_code · owner_action_wiring (+self-test) · admin_menu_gate · dead_link ·
filing_deadline_single_source · advisory_lock_key · di_cycle · namespace_shadow · verbatim_string · css_var · escape_helper · deep_link_param · sims 14 ตัว ·
`node --check` (documents · document-templates · team · usage · admin/subscription-enforcement · admin/platform-holidays · admin-layout.js) · brace/U+FFFD ทุก .cs ที่แก้
ไม่ผ่าน (ตามกติกา): `test_inventory --check` — จำนวนเทสต์เปลี่ยน (main agent วางทับ §0 หลัง merge)

## ไฟล์ทีมอื่นที่ต้องแตะ (เล็กที่สุด)
`CompanyService.cs` (IN ถือเฉพาะจุดเปลี่ยนประเภทธุรกิจ — แตะ CreateAsync บรรทัดบทบาท + เมธอดใหม่ท้ายคลาส) · `SubscriptionService.CheckFeatureAccessAsync` + ctor (ไม่มีเจ้าของ) ·
`PermissionService` · `TaxCalendarService` / `StatutoryRemittanceService` (ไม่มีเจ้าของ — ทีม TX ทำ B-7 อาจชน `TaxCalendarService` บรรทัด `TaxFilingDeadline.For`) ·
`tools/owner_action_wiring_check.py` (แถว S-12) · `tools/check_all.sh` (ลิสต์ self-test) · ข้อความ `AdminSubscriptionEnforcementController` · `admin-layout.js` (เมนู)

## คำถามค้าง
1. **C-4 แถวเดิม**: บริษัทที่แอดมินแพลตฟอร์มถือ `CompanyUser.Owner` อยู่แล้ว — แยกไม่ได้ว่าเปิดให้ลูกค้าหรือของแอดมินเอง ⇒ ไม่ migrate · ควรมีรายงานให้แอดมินไล่โอนไหม
2. **C-4 หน้า `admin/customers.html`** ตั้งบทบาทสมาชิกได้ทุกค่ารวม Owner ผ่าน AdminController — ควรรู้จัก PlatformSupport/ห้ามตั้งไหม (ไม่แตะรอบนี้)
3. เจ้าของบริษัทตั้งบทบาท `SystemAdmin` (99) ให้สมาชิกผ่านหน้าทีมได้ (ของเดิม · ผ่านด่านเจ้าของเท่า Owner) — ควรปิดเหมือน PlatformSupport ไหม
4. **A-PL1 ทางเลือกถ้าล็อกเป็นคอขวด**: ย้ายการประทับไปตอน commit ผ่าน `DbTransactionInterceptor` (ล็อก audit ท้ายสุดเสมอ) — ต้องการเทสต์ DB ก่อน
5. **F3 ข้อ 11**: ยังไม่ได้ส่ง diff ให้ฝ่ายค้าน (แตะสิทธิ์/หลักฐาน/กำหนดยื่นภาษี) — ขอให้ main agent ส่งรอบรวม
