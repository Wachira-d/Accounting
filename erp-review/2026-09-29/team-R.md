# รอบ 200 · ทีม R — กวาดงานค้างรอบ 189 / 193 / 194

> โจทย์: `erp-review/2026-09-29/BRIEF.md` แถว R · ทำทะเบียนทุกแถว P0/P1 (+ P2 เงิน/ภาษี) ที่ยังไม่ติ๊ก · เปิดไฟล์จริงที่ HEAD (`5c1fe028`) ·
> จัดเป็น **แก้แล้วก่อนหน้า** (หา sha + ติ๊กในไฟล์ต้นทาง) · **NOT-A-BUG** · **ยังจริง** ⇒ แก้ (ขนาด ≤ 1 วัน/ข้อ) หรือ 📋 พร้อมแผน ·
> ไฟล์ที่ทีมอื่นรอบนี้ถือ (`Services/Settlement/*` · `Services/Payments/*` · `PaymentGatewayController` · `SubscriptionMiddleware` · `OcrService*`/`Helpers/Ocr*` ·
> `DocumentService` ส่วน Void*/`ChequeService`) ไม่แตะ — จดส่งต่อ
>
> **ยังไม่ได้คอมไพล์/รันเทสต์ในเครื่องนี้ (ไม่มี .NET SDK)** — CI บน `claude/**` เป็นตัวแรก · รายการเสี่ยงคอมไพล์อยู่ท้ายไฟล์

## สรุปตัวเลข

| กลุ่ม | จำนวน |
|---|---|
| ทะเบียนทั้งหมด (P0/P1 + P2 เงิน/ภาษี/ความปลอดภัย ที่ยังไม่ติ๊ก ณ HEAD `5c1fe028`) | **194 รายการ** (B-05 · D-06 · E-02 แยกส่วนที่แก้ / ส่วน backlog จึงนับสองที่) |
| แก้แล้วก่อนหน้า (หา sha แล้ว — ติ๊กในไฟล์ต้นทาง) | **116** (รอบ 189: 12 · รอบ 193: 70 · รอบ 194: 34) — ไม่นับ review193-r3/r4 และ DECISIONS 1–37 ที่ติ๊กไว้ครบแล้วก่อนรอบนี้ |
| NOT-A-BUG / ไม่ใช่ข้อบกพร่องที่ต้องแก้ (มีหลักฐาน) | **6** (§3 · legal-L2 C1–C3 นับ 3) |
| **แก้รอบนี้** (`65efdd86`) | **37** (36 ID — E-05 สองส่วน: สินค้า/สินทรัพย์) |
| backlog 📋 (พร้อมเหตุผล/แผน) | **26** |
| ส่งต่อทีม K (ไฟล์ OCR ที่ทีมอื่นถือ) | **9** |
| รอเจ้าของ (ไม่อยู่ใน DECISIONS — ห้ามเดา) | **9** (ซ้อนกับ backlog/ส่วนที่เหลือของข้อที่ปิดแล้ว — ไม่นับรวมซ้ำ) |

P0 ที่ยังจริงเมื่อเริ่ม: **G2-02** (stored XSS ใน HTML renderer → ขโมย JWT) — แก้รอบนี้ · P0 อื่นทุกข้อปิดแล้วก่อนหน้า (B-01 · F-02 ปิดใน `fb42474e` แต่ไม่เคยถูกติ๊ก)

---

## 1. แก้รอบนี้ (37) — คอมมิต `65efdd86` (ติ๊กในไฟล์ต้นทางแล้ว)

| ID | แหล่ง | P | สิ่งที่แก้ (ตัวตัดสิน) | ไฟล์ | เทสต์ / ด่าน |
|---|---|---|---|---|---|
| **G2-02** | 2026-09-21 report-G | **P0** | หนีทุกช่องที่ผู้ใช้/คู่ค้า/OCR คุมได้ใน HTML renderer: อ้างอิง · เลขภาษีคู่ค้า/บริษัท · เลขเอกสาร · หัวกล่องคู่ค้า · อ้างอิง/วิธีชำระของใบเสร็จ · `src` โลโก้/ตรา/ลายเซ็น (รวมลายเซ็นจากคู่ค้า `PreparerSignatureBase64`) ผ่าน `Helpers/HtmlImageSource.Attribute` (data:image base64 · https/http · path ภายในเท่านั้น) | `PdfGenerationService.cs` · `Helpers/HtmlImageSource.cs` | `HtmlImageSourceTests` · required_call_site (must_lit) |
| **G2-11** | report-G | P2 | ลายน้ำ + ข้อมูลการชำระเงินของเทมเพลต หนีแล้วคงขึ้นบรรทัด `<br/>` (ตัดสิน "เป็นข้อความ ไม่ใช่ HTML") | `PdfGenerationService.cs` | required_call_site |
| **B-06** | report-B | P1 | ด่านลบถาวร §87/3/ม.10 คำนวณระยะเก็บจากวันที่เอกสารเมื่อ `RetentionUntil` = null (ใบ POS/API/นำเข้า) · สูตรเดียวกับ Approve | `Helpers/DocumentRetention.cs` · `DocumentService.PurgeDocumentAsync#2` · `ApproveDocumentAsync#2` | `DocumentRetentionTests` · required_call_site |
| **B-07** | report-B | P2 | ด่าน §90/2 ใช้ `AdjustmentNoteAccount.SourceIsPurchaseSide` (PV/CIL) + GRN — บริษัทไม่จด VAT บันทึกใบลดหนี้จากผู้ขายที่อ้าง PV/CIL ได้ | `DocumentService.ApproveDocumentAsync#2` | required_call_site (forbid ลิสต์มือ) |
| **B-09** | report-B | P2 | คำเตือน "เดือนภาษียื่น ภ.พ.30 แล้ว" ตอนอนุมัติใบขายที่มี VAT (warn-gate · ตัวตัดสิน `VatPeriodDeclaredOrFiledAsync` เดียวกับด่านมัดจำ) | `DocumentService.CollectApprovalWarningsAsync` | required_call_site · TEST_PLAN R200-19 |
| **B-10** | report-B | P2 | เพดานค่ารับรอง §65 ตรี(4) + (10) รายจ่ายรอบก่อน ใช้ `FiscalYear.RangeFor` ตามรอบบัญชี (เดิมปีปฏิทิน) | `DocumentService.ApplySection65TerAsync` | required_call_site (forbid ปีปฏิทิน) |
| **B-05** (a) | report-B | P1 | หน้าเอกสารแสดงผลตรวจ §65 ตรี ที่บวกกลับ 0 บาท (กล่องเหลือง) — **(b) ย้ายการประเมินขึ้นเป็นคำเตือนก่อนอนุมัติ = 📋** | `documents.html` | TEST_PLAN R200-21 |
| **F-05** | report-F | P1 | `lodging/info` สาธารณะไม่ส่งรายการห้อง/หมายเหตุภายใน/สถานะแม่บ้าน/ประเภทที่ปิดขาย/สินค้าภายใน | `Helpers/LodgingPublicProjection.cs` · `LodgingService.Reservations.GetPublicInfoAsync` | `LodgingPublicProjectionTests` · required_call_site |
| **F-06** | report-F | P1 | หน้าจองแบ่งผู้ใหญ่/เด็กลงห้องให้ผลรวม = จำนวนจริง (เดิมปัดขึ้น ⇒ คิดเงินเกิน) | `storefront.html` `_splitGuests` | `tools/lodging_guest_split_sim.js` (โค้ดจริง · negative test สูตรเดิม · รุ่นก่อนแก้ล้ม) |
| **G2-05** | report-G | P1 | รายงาน ภ.ง.ด.1/ปกส. ปิดบังเลขบัตร/เลข ปกส. ถ้าไม่มี `Pii.View` (`CanViewPiiAsync` + PiiAccessLog) | `PayrollService.GeneratePnd1Async/GenerateSsoReportAsync` (+`includePii`) · `PayrollController` · `IPayrollService` | required_call_site (call_args includePii) |
| **G2-06** | report-G | P1 | ตัวจับพนักงานซ้ำเทียบเลขบัตรหลังถอดรหัส (คอลัมน์ nonce สุ่ม) · โหลดครั้งเดียวต่อคำขอ | `Helpers/EncryptedIdMatch.cs` · `Import/DuplicateDetector.DetectEmployeeAsync` | `EncryptedIdMatchTests` · required_call_site |
| **G2-08** | report-G | P2 | `roles/seed-defaults` ต้องเป็นเจ้าของ + ไม่สร้าง Role ซ้ำชื่อ | `RolePermissionService.SeedDefaultRolesAsync` · `IRolePermissionService` · controller | required_call_site |
| **G2-09** | report-G | P2 | ผู้อนุมัติของกฎต้องเป็นสมาชิกบริษัท · ชื่อ/ขั้น/ช่วงเงินถูกต้อง (สร้าง/แก้ตัวเดียว) | `Helpers/ApprovalRuleValidation.cs` · `ApprovalService.ValidateRuleAsync` | `ApprovalRuleValidationTests` · required_call_site |
| **A07** | report-A | P1 | ฟอร์มกฎการอนุมัติเขียนใหม่ตรงสัญญา: `name · documentType · min/max · steps[{stepOrder, approverUserId}]` เลือกผู้อนุมัติจากรายชื่อสมาชิก · ตาราง/แก้ไข hydrate จาก `ApprovalRuleResponse` · ตัวเลือกชนิดเอกสารจาก `Layout.docTypeOptions` (ตารางป้ายเดียวกับ `docTypeLabel`) | `approval.html` · `layout.js` | TEST_PLAN R200-15 · `node --check` |
| **A11 / E-05 / A19 / E-12** | report-A/E | P1/P2/P3 | สินค้า: ช่องตัวเลขว่าง = 0 ที่มองเห็น (ราคา/ต้นทุน/สต็อกขั้นต่ำ · VAT ว่าง = 7) ผ่าน `Layout.numOrNull` · ปรับสต็อก/แปลงหน่วยว่าง = toast ไทย · สต็อกขั้นต่ำไม่ตัดเศษ | `products.html` | `blank_number_null_check` · TEST_PLAN R200-14 |
| **A12 / E-04** | report-A/E | P1 | แก้ไข/ยืนยันสินทรัพย์: อายุ/ซาก/วิธีคิดมีผลจริง (ก่อนมีค่าเสื่อมลงบัญชี · สร้างตารางใหม่) · ราคาทุน/วันที่/ประเภท/สัญญาเช่าล็อก + ป้ายเหตุผล (เดิม "แก้ไขสำเร็จ" ไม่มีผล) | `Helpers/FixedAssetValuationEdit.cs` · `FixedAssetService.UpdateAsync` · `UpdateFixedAssetRequest` · `fixed-assets.html` | `FixedAssetValuationEditTests` · required_call_site (before RemoveRange) |
| **E-05** (สินทรัพย์) | report-E | P1 | สร้างสินทรัพย์เว้นราคาทุน/อายุ ⇒ toast ไทย (เดิม NaN → 400 "dto") | `fixed-assets.html` | TEST_PLAN R200-14 |
| **E-03** | report-E | P1 | นำเข้าทะเบียนสินทรัพย์เดินด่านที่ดิน/CIP · ข้อความวิธีคิดที่ไม่รู้จัก = ปฏิเสธแถว | `Helpers/FixedAssetImportMethod.cs` · `FixedAssetService.ImportAsync` | `FixedAssetImportMethodTests` · required_call_site |
| **E-08** | report-E | P2 | ถอดด่านสต็อกติดลบตัวที่สองใน `AdjustStockAsync` (ไม่อ่านค่าตั้ง) — ledger ตัดสินตัวเดียว | `ProductService.AdjustStockAsync` | required_call_site (forbid) |
| **E-02** (ส่วนข้อความ) | report-E | P1 | ศูนย์ช่วยเหลือบอกความจริง: ถัวเฉลี่ยถ่วงน้ำหนักทุกสินค้า (ยังไม่มีที่ตั้งรายสินค้า) — **การต่อสาย/ถอด `CostingMethod` = คำถามเจ้าของ (ค้าง)** | `HelpContentSeeder.cs` | — |
| **D-03** | report-D | P1 | API `tax/sso-rate` อ่าน `SsoRateSchedule` + `SsoWageBase.MinBase` (เดิม 15,000 ตายตัว) | `ThaiGovIntegrationService.GetCurrentSsoRateAsync` | required_call_site (forbid 15000m) |
| **D-04** | report-D | P1 | กำหนดนำส่ง สปส. + เงินเพิ่ม §49 = `Helpers/SsoLateFee` ตัวเดียว · `PayrollRunResponse.SsoDueDate` · `GET payroll/sso-late-fee` preview · หน้าเว็บเลิกคิดเอง | `Helpers/SsoLateFee.cs` · `PayrollService` · `PayrollController` · `api.js` · `payroll.html` | `SsoLateFeePreviewTests` (ตัวห่อเดิม = ตัวใหม่ทุกวันทั้งปี) · required_call_site |
| **D-05** | report-D | P1 | ทิป: ส่วนแบ่งเป็นสตางค์ Σ = กองทิป (JE สมดุลแม้ 100.01%) · อัตรา/เกณฑ์ WHT จาก `ThaiWhtRateTable` · ปัด AwayFromZero · ไม่มีผัง 21915 = ล้มดังก่อนลงบัญชี | `Helpers/TipShareAllocation.cs` · `TipPayoutService` | `TipShareAllocationTests` · required_call_site |
| **D-06** (ไฟล์) | report-D | P1 | ไฟล์ สปส.1-10/6-09 (.txt) เลข ปกส. ว่าง ⇒ เลขบัตรที่ checksum ผ่าน · ไม่มีทั้งคู่ = ว่าง + นับเตือนในสรุป — **ช่องเลข ปกส. บนหน้าพนักงาน (แรงงานต่างด้าว) = 📋** | `Helpers/SsoInsuredNumber.cs` · `TaxFilingExportService` | `SsoInsuredNumberTests` · required_call_site |
| **D-08** | report-D | P2 | เพดานสมทบปัด AwayFromZero (⚠️ แก้ข้อความ — ทีม RF · R200-X7: **ไม่ใช่**จุดสุดท้าย · ยังเหลือ 11 จุดในโมดูลเงินเดือน ปิดครบใน RF + `tools/payroll_rounding_check.py`) | `PayrollService.GetSsoParamsAsync` | — |
| **D-09** | report-D | P2 | ฐานภาษีสะสมของงวดก่อน = `TaxableGross` (แถวเก่า 0 ⇒ gross เหมือนเดิม) | `Helpers/PayrollIncomeBase.cs` · `PayrollService.CalculatePayrollAsync` | `PayrollIncomeBaseTests` · required_call_site (must_re ytdIncome) |
| **D-10** | report-D | P2 | PVD จากเงินเดือนที่จ่ายจริงของงวด + ปัด 2 ตำแหน่ง | `PayrollIncomeBase.PvdContribution` | `PayrollIncomeBaseTests` · required_call_site (forbid สูตรเดิม) |
| **D-11** | report-D | P2 | audit 50 ทวิรายปีเก็บเลขบัตรแบบปิดบัง + ผ่าน `AddChainedAuditLog` (เดิม `AuditLogs.Add` ตรง = นอก hash chain) | `PayrollService.GenerateAnnualEmployeeWhtCertsAsync` | required_call_site |
| **C-04** | report-C | P1 | `/api/v1/ocr/confirm` ตัดสิน acceptedAi ด้วย `OcrAiLabelScope.AcceptedAi(AiPrimaryAnswer, final)` (เดิม false ทุกครั้ง) | `Controllers/V1/OcrV1Controller.Confirm` (ไม่แตะ `OcrService*`/`Helpers/Ocr*`) | required_call_site |
| **H-3** | report-H | P1 | `ExplainAnomaly` บันทึกคำตอบเมื่อครู**หรือนักเรียน**ตอบ + candidate set · ป้าย usedAi ของคำตอบแคชตามแถว feedback | `Helpers/AnomalyExplainVerdict.cs` · `AiSuggestionController.ExplainAnomaly` | `AnomalyExplainVerdictTests` · required_call_site |
| **P4-7** | review193-r4 | P2 | `generate-pdf`/`generate-html` เดินด่านชั้นความลับ (ด่านเดียวกับหน้าเอกสาร/อีเมล) | `DocumentTemplateController` | required_call_site (before) |
| **P4-1** | review194-r4 | P2 | ใบร่างใบกำกับของการริบที่ค้างจากรุ่นก่อน R3-1 ถูกล้าง `PaymentDate` ก่อนอนุมัติ (tax point = วันที่ใบ) | `DocumentService.IssueForfeitTaxInvoiceAsync` (ส่วนมัดจำ ไม่ใช่ Void*) | required_call_site (must_re + before) |
| **P4-2** | review194-r4 | P2 | CMS booking: ข้อความบนการจองตรวจสถานะจริงหลังอนุมัติล้ม (อนุมัติแล้ว ≠ "ยังเป็นร่าง") | `CmsBookingService` | TEST_PLAN R200-26 |

(รวมเป็น 37 ID เมื่อนับแยก A11·E-05·A19·E-12·A12·E-04·E-05(สินทรัพย์) ทีละตัว)

## 2. แก้แล้วก่อนหน้า — หา sha แล้วติ๊กในไฟล์ต้นทาง (63)

### 2.1 รอบ 189 (`erp-review/2026-09-21/report-*.md`)
| ID | P | sha | หลักฐานที่ HEAD |
|---|---|---|---|
| B-01 | P0 | `fb42474e` | `CreatePaymentJournalAsync` ถาม `AdjustmentNoteAccount` (คอมมิตรอบ 189 ชุดที่ 1 ข้อ 1) |
| B-02 | P1 | `a7444d45` | `documents.html` ทุกทางอนุมัติผ่าน `_approveConfirmingWarnings` (รอบ 199 ทีม W) |
| F-02 · F-04 | P0 · P1 | `fb42474e` | `lodging-settings.html` `Layout.enumOptions` + `<select name="propertyType">` เป็นชื่อ enum |
| F-07 | P1 | `ef3d97b5` (+`b371d4c8` · `cbd50b37` · `e2631285`) | `PaymentGatewayController` `[RequirePermission(PaymentGatewayPermissionScope.*)]` ทุก endpoint เขียน |
| A13 · E-06 | P1 | `fb42474e` | `api.js _findFieldEl` ลอง name → data-field → id + ข้อความไม่วินิจฉัยสาเหตุเอง |
| A14 | P1 | `fb42474e` | `dto_nullable_contract_check.py` ไล่วงเล็บข้ามบรรทัด |
| A15 (บางส่วน) | P2 | `34e62a38` | `EmployeeResponse` echo TaxId/ธนาคาร (เลข ปกส./ที่อยู่/วันเกิด ยังไม่ echo — P2 ไม่ใช่เงิน) |
| G2-03 | P1 | `b371d4c8` | `ApprovalController` rules `[RequirePermission(CompanySettingsEdit)]` + `[RejectApiKey]` |
| G2-04 | P1 | `34e62a38` | `PayrollService.UpdateEmployeeAsync` `EmployeeRecordEdit.IsMaskedEcho` ไม่เขียนค่าปิดบังทับของจริง |
| G2-07 | P1 | `b371d4c8` | `AuditTrailController.verify-hash-chain` เรียก `AuditHashChain.Analyze` ตัวเดียว |

### 2.2 รอบ 193 (`erp-review/2026-09-24/review193-*.md`) — ยืนยันด้วยตารางปิด/ไม่ปิดของฝ่ายค้านรอบสอง/สาม/สี่ + เปิดไฟล์ตรงจุดที่ยังเปิด
| ไฟล์ | ID | sha |
|---|---|---|
| review193-L2 | C1 · C2 · C3 · C4 · C5 · C6 · C7 · C8 · C9 · C10 | `198fb5c` (+`979eefe` N1/N2/N4 · `04ce362` R3-1) — C4(ก) โมดูลที่พักออกใบเช็คเอาต์ใหม่หลัง void = คำถามเจ้าของ Q11 |
| review193-O1 | C1 · C2 · C3 · C4 · C5 · C6 · C8 · C9 · C10 | `f7bad8d` (+`0eb8492` N5/N6) · C6 ส่วนที่เหลือ (คำเตือนอื่นใน API v1 ⇒ 500) ปิดแล้ว — `DocumentsV1Controller` catch `DocumentApprovalWarningsException` ⇒ 422 · C7 → `f4aa7d4` (S2) |
| review193-M2 | C1 · C2 · C3 · C4 · C6 | `4cbb715` · C5 ข้อมูลเก่าคิดกลับด้าน = คำถามเจ้าของ (รายงาน `pos/packages/commission-review`) |
| review193-S2 | S2-C1 · S2-C2 · S2-C3 | `f4aa7d4` · S2-C4 (checker) = 📋 |
| review193-V-C3 | C-1 · C-2 · C-3 · C-4 · C-5 · C-7 · C-8 | `7601891` / `6b2e7fb` (+`c641f6c` · `69ccf37`) · C-6 ฝั่งเติม → `69ccf37` · C-9 → `c641f6c` (ต้นทางธง VAT = คำถามเจ้าของ Q1) |
| review193-W | W-C1 … W-C7 | `c5df11c` (ตารางปิดใน review193-r2-W §1) |
| review193-money | 1 · 2 · 5 | `fc503b5` (2: migration ข้อมูลเก่า = คำถามเจ้าของ) |
| review193-money | 3 · 4 | `4fcd06b` |
| review193-security | C1 | `11e79b2` (`OwnerActionGuard` ใน `EnsureOwnerAccessAsync` + ด่าน `PUT settings`) |
| review193-security | C2 · C3 | `fd880c2` (`IAttachmentAccessGate` ใบเสร็จนำส่ง · สแกน) |
| review193-r2-money | N1 · N2 · N3 · N4 | `979eefe` |
| review193-r2-money | N5 · N6 | `0eb8492` |
| review193-r2-sec-tax | R2-C1 · R2-C2 · R2-C3 · R2-C4 | `0a829110` (ใบเบิก `EnsureClaimActionAsync` + SoD · ลบร่างแล้วสแกนใช้ต่อ · คีย์โมดูล · ระยะเก็บไฟล์สแกน) |
| review193-r2-sec-tax | R2-C5 · R2-C9 | `69ccf37` (`AdoptTaxId` · กุญแจ tenant ก่อน `PlatformBillingDocumentIssuer`) |
| review193-r2-sec-tax | R2-C6 · R2-C7 · R2-C8 | `c641f6c` |
| review193-r2-W | W2-C3 · W2-C4 · W2-C5 · W2-C6 | `b371d4c8` (`[RequireOwner]` gateway/เอกสารลับ · กฎอนุมัติ · `[RejectApiKey]` sso-config) |
| review193-r3 · r4 | ทุกแถว CONFIRMED | ติ๊กไว้แล้วในไฟล์ (`04ce362` · `85dfda8` · `132c2b5` · `41bb2c8` · `3da2760` · `de5dc4cd` · `c3820dce` · `cb552889` · `960e98cd`) |
| 2026-09-24/DECISIONS | 1–37 | ทุกข้อที่เป็น "งาน" มี ✅ แล้ว · ⚠️ ท้ายข้อ 34 (R3-1) ปิดใน `04ce362` |

### 2.3 รอบ 194 (`erp-review/2026-09-25/`)
| ไฟล์ | ID | sha |
|---|---|---|
| review194-money | M1–M6 | `4df544f8` (ทีม M) |
| review194-regsec | C1 · C3 · C4 · P4 · P6 | `219223e2` (ทีม R รอบ 194) · C2 · C5 · P1 → `4df544f8` |
| review194-r2 | R2-1 … R2-6 · P-a | `f6a09e23` |
| review194-r3 | R3-1 · R3-2 · P-1 · P-2 · P-4 | `6ec10976` |
| review194-r4 | R4-1 · R4-2 | `8663946f` |
| legal-L1-forfeit | C-1 (CMS ยกเลิก = คงมัดจำเป็นหนี้สิน) | `d419d456` (`CmsBookingCancelPolicy.DecideOnCancel` → `KeepDepositAsLiability`) |
| legal-L1-forfeit | C-2 · C-3 · C-6 (ลักษณะเงินกำหนด VAT · เงินประกัน) | `326e83bc` (+`3a02dbef` · `d419d456` · `9708d501` · `a206db09`) |
| legal-L1-forfeit | C-4 (VAT ของการริบเข้าเดือนที่ถูก + ธง LATE-VAT) | `f6a09e23` / `6ec10976` |
| plan-deposit-kind | ชุดงาน A–D | `326e83bc` · `3a02dbef` · `d419d456` · `9708d501` |

## 3. NOT-A-BUG (6)
| ID | แหล่ง | หลักฐาน |
|---|---|---|
| review193-security C4 | "static `/uploads/` ยังเปิด" | ฝ่ายค้านยืนยันเอง: `Program.cs` allow-list ตอบ 404 ก่อน `UseStaticFiles` |
| review193-r4 P4-2 | ใบช่วง d788c2a→198fb5c ที่ไม่ถูกย้าย | อยู่บน branch ที่ไม่เคย deploy (ข้อความในไฟล์เอง) — ไม่มีข้อมูลจริงให้ย้าย |
| legal-L1 C-7 | ใบลดหนี้คืนมัดจำค่าสินค้า | ไฟล์เองระบุ "ไม่ใช่ข้อขัดที่ยืนยันแล้ว — ให้นักบัญชียืนยันก่อนแตะ" |
| legal-L2 §0.1 C1–C3 | ข้อความในโจทย์/เอกสาร | เป็นการแก้ข้อความอ้างอิงกฎหมายของรายงาน ไม่ใช่โค้ด (สเปกรอบ 194 `ce29e0f5` ใช้แล้ว) |

## 4. ส่งต่อทีมอื่น (อยู่ในไฟล์ที่ทีมอื่นถือ — ไม่แตะ)
| ID | P | ส่งทีม | เหตุ |
|---|---|---|---|
| C-01 · C-02 · C-03 | P1 | **K** (OCR) | อยู่ใน `OcrService.cs` / `document-scan.html` เส้นสแกน — ✅ fb459244 ทีม K2 (`team-K2.md`) |
| C-05 · C-06 · C-07 · C-08 · C-09 · C-10 | P2/P3 | **K** | `OcrService.cs` / `Helpers/Ocr*` / `document-scan.html` — ✅ fb459244 ทีม K2 (C-07/C-09 บางส่วนเป็น 📋 ดู `team-K2.md`) |

## 5. backlog 📋 (ยังจริง · ใหญ่เกินรอบนี้ หรือไม่ใช่เงิน/ภาษีหลัก)
| ID | P | ทำไมไม่แก้รอบนี้ · แผน |
|---|---|---|
| B-05 (b) | P1 | ย้ายการ**ประเมิน** §65 ตรี ขึ้นก่อนด่านคำเตือน (ตอนนี้รันในธุรกรรมหลังคำเตือน) — ต้องแยก `ApplySection65TerAsync` เป็น Evaluate (pure, ก่อน) + Persist (ในธุรกรรม) · ส่ง finding `NeedsConfirmation` เข้า `CollectApprovalWarningsAsync` · ผลข้างเคียง: ใบซื้อทุกใบที่มี (9)/(10) จะมีคำเตือนใหม่ (ต้องวัดความถี่ก่อน — F2 #8 คำเตือนที่ฟ้องทุกใบ = ปิดด่าน) |
| B-08 | P2 | tax point ภ.พ.36 §83/6 = วันจ่ายเงิน: ต้องเพิ่ม `SupplyKind.ReverseCharge` ใน `TaxPointResolver` + ตั้ง tax point ตอนจ่าย (ไม่ใช่ตอนอนุมัติใบซื้อ) + ย้ายงวด ภ.พ.36 — กระทบรายงาน/ปฏิทิน ต้องมีเทสต์ golden ก่อน |
| B-11 | P2 | เล่มเลขใบกำกับแยกสาขา — เปลี่ยนคีย์ของ `DocumentNumberGenerator` = เปลี่ยนลำดับเลขของบริษัทที่ใช้อยู่ (gap-free §86/4) · ต้องให้เจ้าของเลือก "แยกเล่มต่อสาขา" เป็นค่าตั้ง + ย้ายเฉพาะบริษัทที่เลือก |
| D-06 (UI) | P1 | ช่องเลขประกันสังคม (แรงงานต่างด้าว) บนหน้าพนักงาน — แตะสัญญา `employee_form_contract_sim` (baseline) + `UpdateEmployeeRequest` + PII mask · ไฟล์ .txt แก้แล้วด้วยเลขบัตร |
| E-02 (ต่อสาย) | P1 | `Product.CostingMethod` — ต่อสาย FIFO/Specific หรือถอด = **คำถามเจ้าของ (ค้างจากรอบ 193)** · รอบนี้แก้ข้อความศูนย์ช่วยเหลือให้ตรงความจริงแล้ว |
| E-07 | P2 | FIFO/WAC rebuild มองเฉพาะ IN/OUT — ผูกกับ E-02 (มีผลเมื่อเปิด FIFO) |
| E-10 · E-11 | P2 | `ReconcileProductTotalsAsync` ไม่มีผู้เรียก · `UsefulLifeReviewedAt` ไม่มีผู้อ่าน — "ต่อสาย หรือ ลบ" ต้องให้เจ้าของเลือก (F2 #2) |
| H-1 | P1 | คลังจับคู่ธนาคารสอนตัวเอง — ต้องเพิ่ม `UserChoiceSource` + `ExplicitAcceptCount` ให้ `BankReconciliationPatterns` (migration) + หน้า bank.html ส่ง source + สูตรความมั่นใจนับเฉพาะ Explicit — งานหลายไฟล์ |
| H-2 | P1 | `AuditChainVerifyJob` ไม่มี watermark — ต้องมีคอลัมน์/ตาราง checkpoint ต่อบริษัท (Id/Timestamp สุดท้ายที่ตรวจผ่าน) + ตรวจต่อจาก tip · ผูกกับคำถามเจ้าของ "serialize การประทับ audit" (W2-C1) |
| H-4 · H-5 · H-6 · H-7 · H-8 · H-9 | P2 | สถาปัตยกรรม AI/ธนาคาร (H-9 ยอดปลอมจาก candidateId ที่ AI แต่ง = ควรแก้ก่อนใน `BulkBankStatementMatch` guard) |
| G2-10 · S2-C4 · R2-C10 | P2 | checker ครอบไม่ครบ (write_permission 8/143 · attachment_gate false-negative · contact_taxid ยกเว้นกว้าง) |
| review193-r2-W P-3 · P-4 | P2 | N+1 อีเมลตั้งเวลา · แอดมินกลายเป็น Owner ของบริษัทลูกค้า (ต้องแยก role แพลตฟอร์ม) |
| review194-r4 P4-3 · P4-4 · P4-5 | P3/P2 | ล็อกมัดจำ Modified ข้ามเงียบ (ไม่พบจุดเกิดจริง) · เลขอ้างอิงสองใบ resolve ได้ใบเดียว · ขอบเดือน UTC ของธงริบ (`RealizeDate ?? UtcNow`) |
| review194-regsec P5 · review194-r3 P-5 | P2 | seed ประเภทมัดจำเมื่อเปลี่ยนประเภทธุรกิจภายหลัง · หมายเหตุ §86 ธ.ค.→ม.ค. |

## 6. รอเจ้าของ (ไม่อยู่ใน DECISIONS ทั้งสองไฟล์ — ห้ามเดา)
E-02 (`CostingMethod`) · L2 C4(ก)/Q11 (ออกใบเช็คเอาต์ใหม่หลัง void) · M2 C5 / money-2 (ข้อมูลคอมมิชชัน 0/1 เก่า) · money-6 (ฐาน ปกส. นับเบี้ยเลี้ยง) ·
V-C3 C-9 (ต้นทางธง VAT) · W2-C1 (serialize audit ข้ามคำขอ) · W2-C2 (เทสต์ hash chain บน PostgreSQL จริง) · r2-W P-5 (บทบาทไหนได้ `CompanySettings.Edit`) · B-11 (เล่มเลขต่อสาขา)

## 7. คำถามค้างใหม่จากรอบนี้ (เลือกทิศที่มองเห็นและย้อนได้แล้ว — เจ้าของทบทวนได้)
1. **สินค้าเว้นช่องราคาขาย = 0** (A11/A19) — เลือก 0 ที่มองเห็นได้บนรายการสินค้า แทน "บังคับกรอก" · ถ้าต้องการบังคับ แก้บรรทัดเดียวใน `products.html save()`
2. **แก้อายุ/วิธีคิดค่าเสื่อมหลังเริ่มคิดแล้ว** (A12) — ปฏิเสธพร้อมทางไปต่อ (ยังไม่มีเส้น "เปลี่ยนประมาณการไปข้างหน้า" TFRS บทที่ 10) · ถ้าต้องการ ต้องสร้างเส้นคำนวณตารางใหม่จากมูลค่าตามบัญชีคงเหลือ
3. **โลโก้/ตราที่เป็น URL รูปแบบอื่น** (G2-02) — ไม่พิมพ์รูป (มองเห็นได้ แก้ค่าที่ตั้งได้) แทนการพยายามแปลง
4. **ข้อมูลการชำระเงินในเทมเพลตเป็นข้อความ ไม่ใช่ HTML** (G2-11) — ถ้ามีลูกค้าตั้งใจใส่ HTML (ลิงก์/ตัวหนา) จะเห็นเป็นตัวอักษร · ทางเลือกอื่น = ผ่าน `CmsHtmlSanitizer`
5. **เลข ปกส. ว่าง ⇒ ใช้เลขบัตรประชาชน** (D-06) — ถูกสำหรับผู้ประกันตนไทย · แรงงานต่างด้าวต้องกรอกเอง (ไฟล์บอกจำนวนคนที่ขาด)
6. **ทิปยังเป็น ม.40(2) 3%** (D-05) — รอบนี้แค่ย้ายไปอ่านตาราง · การจัดประเภททิปพนักงาน (40(1) vs 40(2)) + เกณฑ์ 1,000 สะสมต่อผู้รับทั้งปี ยังไม่ได้ตัดสิน (`TipShareAllocation.Withholding(gross, alreadyPaidThisYear)` รองรับแล้ว แต่ผู้เรียกยังส่ง 0)

## 8. ความเสี่ยงคอมไพล์ที่เหลือ (ไม่มี .NET SDK)
- `Controllers/DocumentTemplateController.cs` เพิ่ม `using Microsoft.EntityFrameworkCore;` + ใช้ `db.Documents.AsNoTracking().Where(...).Select(d => d.Sensitivity).FirstOrDefaultAsync()`
- `PayrollController.SsoLateFeePreview` คืน `ActionResult<ApiResponse<SsoLateFee.Result>>` และ `return block;` (ActionResult → implicit)
- `UpdateFixedAssetRequest(... DepreciationMethod? DepreciationMethod = null)` — ชื่อพารามิเตอร์ = ชื่อชนิด (Color Color · มีแบบเดียวกันใน Create)
- `IPayrollService.GeneratePnd1Async/GenerateSsoReportAsync(+bool includePii)` และ `IRolePermissionService.SeedDefaultRolesAsync(+Guid actorUserId)` — ผู้เรียกทุกจุดแก้แล้ว (callers.py = 1 จุดต่อเมธอด)
- `DocumentService.CollectApprovalWarningsAsync` ใช้ `TaxPointResolver.Resolve(doc)` (คืน `DateTime`) กับ `doc.TaxPointDate ?? …`
- `Helpers/TipShareAllocation.Split` รับ `IReadOnlyList<KeyValuePair<Guid, decimal>>` (ผู้เรียกส่ง `.ToList()` ของ dictionary)
- `xUnit InlineData` int → decimal ใน `FixedAssetValuationEditTests` (xUnit แปลงให้)

## 9. เครื่องมือ
- `bash tools/check_all.sh` — ผลอยู่ในข้อความคอมมิต
- `required_call_site_check` +33 กติกา (บล็อก "รอบ 200 ทีม R") · `tools/lodging_guest_split_sim.js` ใหม่ · `dead_helper_baseline.txt` ตัด 2 แถวที่ต่อสายแล้ว (`FiscalYear.NormalizeStartMonth` · `PiiMask.BankAccountNo`)
