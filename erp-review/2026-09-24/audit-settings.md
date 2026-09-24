# ทีมตรวจ S — "การตั้งค่าถูกเรียกใช้ครบทุกส่วนงานไหม" (รอบ 193 · 2026-09-24)

> โจทย์เจ้าของ: "ส่วนงานที่เกี่ยวข้องมาเรียกใช้การตั้งค่าทั้งหมดด้วย ตรวจสอบกระบวนการทำงานให้ถูกต้องครบถ้วนทั้งหมด"
> ⇒ ค่าที่ผู้ใช้ตั้งได้ทุกตัว ต้องมีผลกับ**ทุก**โมดูล/ทางเข้าที่มันเกี่ยวข้อง · READ-ONLY (ไม่ได้แก้โค้ด)
>
> **วิธีตรวจ**: สคริปต์ (`scratchpad/auditS/inv.py`, `tpl.py`) ดึง property ทุกตัวของ entity ที่เก็บค่าตั้ง แล้วนับ
> ผู้เขียน (`X =`) / ผู้อ่าน (นอก entity/DbContext/migration) / การอ้างใน `wwwroot` · ตัวที่ผู้อ่าน ≤ ไฟล์ mapper ถูกเปิดไฟล์ตรวจมือ
> ทุกตัว (สคริปต์พลาด property-pattern `settings is { X: true }` ได้ — จึงตรวจซ้ำด้วยชื่อเปล่าทุกข้อที่รายงาน)
> **ข้อจำกัด**: ไม่มีคอมไพเลอร์ · ทีมอื่นแก้ไฟล์พร้อมกัน (เลขบรรทัด `IntegrationService.cs` เลื่อน +44 ระหว่างตรวจ) ⇒
> เลขบรรทัดคือ ณ เวลาเขียน ให้ `grep` ซ้ำก่อนแก้ · ไม่รายงานซ้ำข้อที่อยู่ใน §9/§5/§7/§6 ของรายงานก่อน (ข้อที่ยังเปิดอยู่
> แยกไว้ใน §4 "รู้แล้ว-ยังไม่ปิด")

## 1. คำตอบสั้น

1. **ไม่ครบ** — จาก ~500 ฟิลด์ค่าตั้งใน 12 entity: **ตายสนิทแต่มีช่องบนหน้าจอ 18 ตัว** (ไม่นับ D-12 ที่รู้แล้ว) (ผู้ใช้ตั้งแล้วไม่มีอะไรเกิด) ·
   **ตายแบบ API-only ~65 ตัว** (ส่วนใหญ่ `SiteCommerceConfig`/`DocumentTemplate`/`TrialConfig`) · **มีผลบางทางเข้า 6 ตระกูล**
2. ต้นเหตุร่วมมี 3 แบบ: **(ก) ค่าเดียวเก็บสองที่ ค่าเริ่มต้นตรงข้ามกัน** (สถานะ VAT) · **(ข) ค่าที่บังคับใน `ApproveDocumentAsync`
   เท่านั้น** แต่มีทางเข้าที่ประทับ `Approved` เอง (POS/Integration/Import) · **(ค) "ฟอร์มเก็บ-แสดงคืนครบ" ถูกนับว่าเสร็จ** ทั้งที่ไม่มีผู้อ่าน
   (การตรวจ round-trip ของรอบก่อนผ่านหมด เพราะวัดแค่ echo ไม่ได้วัด "มีผล")
3. P0 1 ข้อ (S-01 สถานะ VAT สองธง) · P1 7 ข้อ · P2 12 ข้อ · รู้แล้วยังเปิด 6 ข้อ
4. ข้อเสนอหลัก: **resolver ตัวเดียวต่อตระกูลค่าตั้ง** + **checker ratchet `settings_reader_check`** (ฟิลด์ค่าตั้งใหม่ต้องมีผู้อ่านนอก
   mapper) + ตัดสิน "ต่อสาย หรือ ลบ" ทีละตัวโดยเจ้าของ (§6)

## 2. Inventory — ตระกูลค่าตั้ง · ผู้เขียน · ผู้อ่าน · ทางเข้าที่ทำตาม/ไม่ทำตาม

สัญลักษณ์: ✅ ทุกทางเข้าทำตาม · ⚠️ บางทาง (drift) · ❌ ไม่มีผู้อ่าน (dead) · 🔒 เจตนาไม่ใช้ (มีคอมเมนต์อธิบาย)

| ตระกูล / ค่าตั้ง | ผู้เขียน (UI → endpoint) | ผู้อ่าน (service) | ทางเข้าที่ทำตาม / ไม่ทำตาม | สถานะ |
|---|---|---|---|---|
| **VAT** `Company.IsVatRegistered`/`VatRate` | วิซาร์ดสร้างบริษัท `layout.js:2081` → `CompanyService.CreateAsync:53` · หน้าบริษัท → `UpdateAsync:247` | POS `PosService.Orders:511,1842` · Lodging `LodgingService:73,161` · CMS `CmsBookingService:491` `CmsCommerceService:885` · TimeBilling `:315` · ปฏิทินยื่น `StatutoryRemittanceService:484` · PDF อย่างย่อ `PdfGenerationService:1795` | ❌ เว็บสร้าง/อนุมัติ · Integration · หน้า documents | ⚠️ **S-01** |
| **VAT** `CompanySettings.VatRegistered`/`DefaultVatRate` | `settings.html:2439` → `SettingsService:113-131` (sync กลับ Company) | §90/2 `DocumentService:4773` · ภาษีซื้อ `:1353,2389` · แปลงเอกสาร `:9323` · Integration `:1905` · `documents.html:1591` | ❌ POS/CMS/Lodging/TimeBilling/ปฏิทิน | ⚠️ **S-01** |
| `SiteCommerceConfig.DefaultVatRate`/`PricesIncludeVat` | API `CmsCommerceConfigController:64-65` (ไม่มี UI) | ไม่มี — `CmsCommerceService:913,1571` ใส่ `PricesIncludeVat:true` ตายตัว | — | ❌ S-11 |
| `LodgingProperty.ChargeVat` | `lodging-settings.html` → `LodgingService` | `LodgingService:71` (`? 7m : 0m` ไม่อ่าน `VatRate`) | ⚠️ อัตรา 7 ตายตัว | ⚠️ S-10 |
| `ReceiptIssueMode` | settings → `SettingsService:74-76` | `ReceiptIssuePolicy` ← DocumentService (settlement) · PDF ×2 · TaxService | ✅ (CMS/Lodging เดินผ่าน DocumentService) | ✅ |
| `UnifyTaxInvoiceNumberSeries` | settings → `:82` | `TaxInvoiceSeriesPolicy` ← DocumentService `:5226,10795` · Integration | ✅ | ✅ |
| `NumberSeries.Prefix` | settings.html:2386 → `CreateNumberSeriesAsync` | `DocumentNumberGenerator.ResolvePrefixAsync:60` (ทุกทางออกเลขใช้ตัวนี้) | ✅ | ✅ |
| `NumberSeries.Suffix/Format/ResetPeriod/CurrentNumber` | API `SettingsService:377-416` | ไม่มี (เจตนา `DocumentNumberGenerator:51-59`) | API รับแล้วทิ้งเงียบ | 🔒/⚠️ S-20 |
| `IsRetailApproved`+`PhoR06ApprovedDate` · platform `RequirePhoR06…` | หน้าบริษัท · admin | `AbbreviatedTaxInvoiceRule` ← PDF ×2 · POS `:1389` · DocumentService `:1641` | ✅ | ✅ |
| `EnforceFullTaxInvoiceFields` | **ไม่มีผู้เขียน/ไม่มี UI** | `DocumentService:4828` (`?? true` แต่ entity default `false`) | ขึ้นกับว่า "มีแถว settings หรือยัง" | ⚠️ **S-07** |
| `DocumentTitleOverridesJson` | settings.html:1250 | `ComputeDocumentTitle` ← PDF ×2 | ❌ หัวข้ออีเมล/LINE ใช้ตารางชื่อของตัวเอง | ⚠️ S-12 |
| `DocumentLanguage` (บริษัท/เทมเพลต/ผู้ติดต่อ/ใบ) | settings · template · contact | `ResolveDocumentLanguage:1216` ← PDF ×2 · `DocumentService:1247-1266` เติมจากผู้ติดต่อ | ❌ อีเมล `DocumentEmailService:330` · LINE `DocumentLineDeliveryService:58` · ตั้งเวลา `EmailScheduleService:535` คำนวณ `??` เอง ข้ามเทมเพลต | ⚠️ S-12 |
| `RequireApprovalForDocuments`+`ApprovalThresholdAmount` · `SodBlockSelfApproval` · `BudgetCommitmentMode` | settings | `ApproveDocumentAsync:4968,4990,5002` · PO `:2527` | ❌ POS/Integration/Import ที่ประทับ `Approved` เอง | ⚠️ **S-02** |
| `EtaxEnabled`/`EtaxAutoSign`/`EtaxAutoSubmit` | settings · `EtaxController:314-316` | `TryAutoGenerateEtaxAsync` (`DocumentService:5477→5603`) · `EtaxInvoiceService:55-70` · CMS `:1135` | ❌ POS ใบกำกับเต็มรูป · Integration TIV/CN/DN | ⚠️ **S-02** |
| `EtaxByEmailAutoSendOnApprove` · `EtaxByEmailEmbedXml` | `settings.html:670-671,1813-1814` → `EtaxController:322-323` | **ไม่มี** (echo `:354-355` เท่านั้น) | — | ❌ **S-06** |
| `DefaultPaymentTerms`/`DefaultPaymentDueDays` (บริษัท) | settings.html:2401-2402 | `AiSuggestionController:642` เท่านั้น | ❌ ทุกเส้นสร้างเอกสาร | ❌ รู้แล้ว D-12 |
| `DocumentTemplate.DefaultCreditDays/Terms/PaymentAccountId` | document-templates → `DocumentTemplateService:352,447` | หน้า `documents.html:5324-5336` เท่านั้น | ❌ API/recurring/LINE/Integration | ⚠️ รู้แล้ว D-12 |
| `Contact.PaymentDueDays/PaymentTerms` | contacts | `DocumentService:1247-1262` (เส้นที่ผ่าน `CreateDocumentAsync`) | ❌ Integration `AddDays(30)` `:970,2792,2885,3319` · TimeBilling `:329` · CMS `DueDate=UtcNow` | ⚠️ รู้แล้ว D-12 |
| `InvoiceNotes/ReceiptNotes/QuotationNotes/InvoiceFooter/ReceiptFooter` | `settings.html:327-343` → `SettingsService:62-66` | **ไม่มี** (PDF อ่าน `DocumentTemplate.FooterNotes` แทน) | — | ❌ **S-06** |
| `InvoiceEmailSubject/InvoiceEmailBody` | `settings.html:497` → `:136-137` | **ไม่มี** — `DocumentEmailService:343-386` ประกอบหัว/เนื้อเอง | — | ❌ **S-06** |
| `EmailFromName/ReplyTo/SMTP/Graph/Gmail` | settings/`EmailConfigController` | `DocumentEmailService:82-83` · `EmailSenderFactory` · `NotificationEngine:316` | ✅ (`EmailGmailServiceAccountJson` ❌ ไม่มีผู้เขียน-อ่าน) | ✅ |
| `LineChannelAccessToken`/`LineDefaultGroupId` | `settings.html` → `LineConfigController:50-52` | `LineNotifyService:40-46` (push) | ❌ บอท/webhook/รับรูป `LineBotService:84,456,834,961` ใช้ค่ากลาง | ⚠️ **S-03** |
| `LineChannelSecret` · `LineOaBasicId` | `LineConfigController:51,53` | secret: **ไม่มี** (`HasChannelSecret` เท่านั้น) · OA: `PayslipLineDeliveryService:66` สร้างลิงก์เพิ่มเพื่อน | — | ❌/⚠️ **S-03** |
| `EnableApiAccess`/`MaxApiKeys` | `settings.html:892-893` | `SettingsService.CreateApiKeyAsync:463-469` | ❌ `ApiKeyMiddleware:79-82` ไม่ตรวจ | ⚠️ **S-04** |
| `IsVehicleDealer` | settings | `DocumentService:17178` (เตือน) · `TaxService:273` | ❌ `ProhibitedInputVatScreener` ← OCR `OcrService:1270` · `DocumentService:16768` | ⚠️ **S-05** |
| `WhtRecognitionBasis` · `AutoAttachWhtCertPdf` · `CashSaleStockPolicy` · `PosTipPayableAccountCode` · `AllowNegativeStock` | settings | resolver กลาง (`TaxService`/`WhtCreditService` · `WithholdingTaxCertService:44` · `CashSaleStockRules` · `TipAccountResolver` · `StockLedger:33`) | ✅ (Integration JE ตาม `WhtRecognitionBasis` — **ไม่ทราบ** ต้องตรวจต่อ) | ✅ |
| `AutoCloseMonthEnd`/`MonthEndClosingDay` | `settings.html:2158-2159,2403-2404` | **ไม่มี** (ไม่มี job ปิดงวดอัตโนมัติ) | — | ❌ **S-06** |
| `PreventPostToClosedPeriod` | API | ไม่มี (ด่านงวดปิดบังคับเสมอ) | — | ❌ รู้แล้ว D-14 |
| `Company.FiscalYearStartMonth` | วิซาร์ด/หน้าบริษัท | `FiscalYear.RangeFor` ← CIT/XBRL/PND51 | ❌ §65 ตรี `DocumentService:12524,12574` · งบประมาณ `BudgetService:157` `DocumentService:13372` · ปฏิทิน | ⚠️ รู้แล้ว D1-9/B-10/C-R3/T-10 |
| `Company.SocialSecurityAccountNo` · `IsWhtRegistered` | หน้าบริษัท | echo `CompanyService:607-608` เท่านั้น | ❌ ไฟล์ สปส.1-10 `TaxFilingExportService:~459` ใช้ TaxId | ❌ S-17 |
| `EclEnabled` · `EclLossRatesJson` | settings (Enabled เท่านั้น) | `EclAllowanceJob:77-83` | อัตราสูญเสีย**ไม่มีผู้เขียน** ⇒ ค่าเริ่มต้นเสมอ | ⚠️ S-14 |
| `ShareTrainingDataAnonymously`/`OwnTrainingBonusMultiplier` | **ไม่มีผู้เขียน/UI/API** | `CrossTenantKnowledgeAggregator:71,82` · `ExpenseCategoryLearner:47` | opt-out ที่ entity สัญญาไว้ ใช้จริงไม่ได้ | ⚠️ S-15 |
| `OcrBuyerInvoiceDefaultTarget` · `DefaultPaymentAccountId` | settings (ป้าย "สำหรับ OCR") | `OcrService:908,8003` · Payroll `:4467` · Statutory `:1481` | ✅ ตามขอบเขตป้าย | ✅ |
| Payroll `SsoYearConfig` · `WorkersCompensation*` · `LeaveQuotasJson` · `EnforceManagerApproval` | หน้า payroll/settings | `SsoRateSchedule`/`PayrollService:131,1985,3954,170` · `SalaryAdvanceService:66` | ✅ | ✅ |
| `TaxRuleConfig.HealthInsuranceCap`/`MortgageInterestCap` | `PayrollController:901-903` | **ไม่มี** — ตัวคำนวณ PIT ไม่มีรายการลดหย่อนนี้เลย | — | ❌ S-16 |
| `LodgingProperty` ~50 ฟิลด์ | `lodging-settings.html` → `LodgingService:217-285` | `LodgingService.Lifecycle/Reservations/Operations` · `LodgingPricingEngine` · night audit | ✅ ยกเว้น `AutoConfirmOnDeposit` ❌ (`Lifecycle:333` ยืนยันเสมอ) · `AccountingModeAckBy` เขียนไม่อ่าน | ⚠️ **S-06** |
| `SiteCommerceConfig` ~40 ฟิลด์ | API เท่านั้น (UI แตะแค่ `quotationMessageTh` `cms-orders.html:233`) | `QuotationMessageTh` (`CmsCommerceService:1530`) · `ErpDocumentType` (`CmsCustomerService`) | ที่เหลือ ~37 ฟิลด์ไม่มีผู้อ่าน | ❌ S-11 |
| `DocumentTemplate` ~95 ฟิลด์ | `document-templates.html` → `DocumentTemplateService` | `BuildBranding:3128` + ทั้งสอง renderer | ✅ เกือบทั้งหมด · `HeaderBackgroundColor`/`LogoWidth` HTML อย่างเดียว · 12 ฟิลด์ e-Tax/QR/สำเนา ไม่มีผู้อ่าน | ⚠️ S-19 |
| Platform `SiteSettings` `RegistrationEnabled` · `MaintenanceMode` · `OcrAutoCreateThreshold` · `Ai{ReviewConfidenceThreshold,SamplingRate,VerifyAgainstThaiComplianceRules}` · `OcrFreePages*` | admin pages | Registration: `register.html:633` (หน้าเว็บ) · ที่เหลือ **ไม่มี** | ❌ server ไม่ตรวจ | ❌ **S-08** / S-06 |
| `TrialConfig` 6 ฟิลด์ (`DeleteDataAfterGracePeriod` `DataRetentionDays` `ShowTrialWatermark` `TrialMessage` `AllowDataExportAfterExpiry` `TrialMaxCompanies`) | admin → `SubscriptionService:1380-1389` | echo `:272-275` เท่านั้น | — | ❌ S-18 |

## 3. ผลตรวจเรียงความรุนแรง (ข้อใหม่ — main agent/ทีมถัดไปต้อง verify ก่อนแก้)

### P0

**S-01 สถานะ VAT เก็บสองธง ค่าเริ่มต้นตรงข้ามกัน และเส้นสร้างบริษัทไม่ sync ⇒ บริษัทที่ "ไม่จด VAT" ออกใบกำกับภาษีเก็บ VAT 7% ได้ (§90/2)**
- `Company.IsVatRegistered` default **false** (`Company.cs:48`) · `CompanySettings.VatRegistered` default **true** (`CompanySettings.cs:115`)
  และทุกผู้อ่านฝั่ง settings ใช้ `?? true` (`DocumentService.cs:1353,2389,4773,9323`)
- สร้างบริษัท: `CompanyService.CreateAsync:53` / สมัครสมาชิก `AuthService.cs:201` เขียน**เฉพาะ** `Company` · แถว `CompanySettings`
  ถูกสร้างทีหลังแบบ lazy ด้วยค่า default (`SettingsService.cs:526-535` + อีก 4 ที่ `DocumentEmailService:394` `EmailConfigController:94`
  `OwnerConfigController:96` `LineConfigController:95`) · การ sync ใน `CompanyService.UpdateAsync:294-301` ข้ามเมื่อยังไม่มีแถว (`if (cs != null)`)
- **สถานการณ์**: เจ้าของสร้างบริษัทในวิซาร์ดโดย**ไม่ติ๊ก** "จด VAT" (ค่าเริ่มต้นของวิซาร์ด `layout.js:2035`) → ระบบพาไป
  `settings.html?setup=1` ซึ่งโหลดแถวใหม่ `vatRegistered=true` แล้วโชว์ติ๊กไว้ (`settings.html:2220`) → กดบันทึกหน้าตั้งค่าครั้งแรก
  (แก้อย่างอื่นก็ตาม) ส่ง `vatRegistered:true` (`:2439`) → `SettingsService:120-131` **เขียนทับ `Company.IsVatRegistered=true`** — เจตนาจาก
  วิซาร์ดหายเงียบ. ก่อนกดบันทึก: หน้าเอกสารเสนอ "ใบกำกับภาษี" (`documents.html:1591,11767`) · ด่าน §90/2 ไม่ทำงาน · ภาษีซื้อถูกเคลม
  เข้า 11610 · Integration คิด VAT (`IntegrationService.cs:1905`) — ขณะที่ POS/CMS/ที่พัก/TimeBilling คิด 0% และปฏิทินยื่นภาษี
  **ไม่แสดง ภ.พ.30** (`StatutoryRemittanceService.cs:484`) ⇒ เก็บ VAT แต่ไม่มีใครเตือนให้นำส่ง
- **ทิศกลับ**: บริษัทจด VAT ที่ติ๊กในวิซาร์ด → ทั้งคู่เป็น true (ไม่พัง) ⇒ บั๊กอยู่ทิศเดียวซึ่งเป็นทิศที่ผิดกฎหมาย
- **แก้**: `Helpers/CompanyVatStatus` ตัวเดียว (อ่านคอลัมน์เดียว — แนะนำ `Company.IsVatRegistered/VatRate` เพราะเป็นข้อมูลทะเบียน) ·
  ทุกผู้อ่าน 20+ จุดเรียกตัวนี้ · `CompanySettings.VatRegistered/DefaultVatRate` เหลือเป็น echo (หรือถอด) · สร้างแถว settings ใน
  `CreateAsync`/สมัครสมาชิกทันที (stopgap) · **migration: ห้ามเดาว่าธงไหนถูก** — ออกรายงานบริษัทที่สองธงขัดกัน + ใบกำกับที่ออกขณะขัดกัน
  ให้เจ้าของ/นักบัญชีตัดสิน (คำถาม Q1) · เทสต์สองครึ่ง: ไม่จด → บล็อกทุกทางเข้า / จด → ไม่ถูกแตะ

### P1

**S-02 ค่าตั้งที่บังคับ "ตอนอนุมัติ" ไม่ถึงทางเข้าที่ประทับ `Approved` เอง** — `TryAutoGenerateEtaxAsync` (e-Tax อัตโนมัติ) ·
วงเงินอนุมัติ · SoD · Budget commitment · `EnforceFullTaxInvoiceFields` · §90/2 อยู่ใน `ApproveDocumentAsync` เท่านั้น แต่:
POS ใบกำกับเต็มรูป `PosService.Orders.cs:615` · Integration TIV/CN/DN/CIL `IntegrationService.cs:968,1264,1394,3705` · นำเข้า
`ImportExportService.cs:1069` สร้างเป็น `Approved` ตรง (ไม่มีการเรียก e-Tax เลย — grep "etax" ในสองไฟล์ = 0)
- **สถานการณ์**: บริษัทเปิด e-Tax (Direct) + `EtaxAutoSubmit` → ใบกำกับจากเว็บถูกออก XML/ส่งสรรพากรอัตโนมัติ แต่ใบกำกับจาก POS/TakeTime
  **ไม่เคยมี e-Tax** ⇒ ไม่ถูกนำส่งภายในวันที่ 15 โดยไม่มีคำเตือน · ตั้งวงเงินอนุมัติ 50,000 → ใบ 200,000 จาก integration ผ่านฉลุย
- ความเชื่อมโยง: ราก R1/R5 ของ DECISION_AUDIT พูดถึง "ทางเข้าอื่นไม่เดินด่าน" ทั่วไป — มุมใหม่ที่นี่คือ**ค่าตั้งของเจ้าของ**หายไปทั้งชุด
- **แก้**: แยก "ผลข้างเคียงหลังออกเอกสาร" (e-Tax · แจ้งเตือน · audit) เป็นเมธอดเดียว `IssuedDocumentHooks.RunAsync` ที่ทุกเส้นออกเอกสาร
  ต้องเรียก + checker ratchet ห้าม `Status = DocumentStatus.Approved` นอก DocumentService (ตอนนี้ 10 จุด ใส่ baseline) · ด่านนโยบาย
  (วงเงิน/SoD) ตัดสินใจเจ้าของว่าใช้กับ POS/integration ไหม (Q2)
- ข้อย่อย: CMS เรียก e-Tax ซ้ำหลัง Approve ด้วย `SignDigitally: true` ตายตัว (`CmsCommerceService.cs:1135-1146`) ไม่อ่าน `EtaxAutoSign`

**S-03 ค่าตั้ง LINE ต่อบริษัทมีผลแค่ "ส่งแจ้งเตือน" — webhook/บอท/รับรูปใช้ช่องกลางเสมอ**
- เก็บ `LineChannelSecret` เข้ารหัส (`LineConfigController.cs:51`) แต่ไม่มีผู้อ่าน · `LineBotService.VerifySignature:84` ใช้
  `_config["Line:ChannelSecret"]` · ตอบกลับ/ดึงรูปใช้ token กลาง (`:456,834,961`) · route เดียว `api/line-webhook` ไม่มีรหัสบริษัท
- **สถานการณ์**: บริษัทตั้ง OA ของตัวเอง + `LineOaBasicId` → ระบบออกลิงก์ "เพิ่มเพื่อน OA บริษัท" ให้พนักงานผูกรับสลิป
  (`PayslipLineDeliveryService.cs:66`) → ข้อความผูกบัญชีวิ่งไป webhook ของ OA บริษัท → ลายเซ็นไม่ผ่าน (secret คนละตัว) ⇒ ผูกไม่ได้/
  ส่งรูปสแกนผ่าน LINE ไม่ได้ · และ LINE userId เป็นค่าต่อ provider ⇒ userId ที่ผูกผ่าน OA กลางใช้ push ผ่าน OA บริษัทไม่ได้
- **ไม่ทราบ**: มีลูกค้ารายใดใช้ OA ของตัวเองแล้วหรือยัง · **แก้**: `LineChannelResolver(companyId)` ตัวเดียวให้ทั้ง push/reply/verify +
  webhook `api/line-webhook/{channelKey}` · หน้าตั้งค่าแสดง URL webhook ที่ต้องใส่ใน LINE Developers

**S-04 ปิด "เปิดใช้งาน API Access" แล้วคีย์เดิมยังใช้ได้** — `EnableApiAccess` ถูกตรวจแค่ตอนสร้างคีย์ (`SettingsService.cs:463`) ·
`ApiKeyMiddleware.cs:79-82` รับทุกคีย์ `Active` · **สถานการณ์**: เจ้าของสงสัยคีย์รั่ว กดปิดสวิตช์ → คู่ค้ายังอ่าน/เขียนข้อมูลได้ ·
**แก้**: middleware ตรวจธงของบริษัท (แคชสั้นเหมือนสถานะคีย์) + ข้อความ 403 ภาษาไทย · เทสต์ทิศตรงข้าม: เปิดอยู่ → คีย์ใช้ได้

**S-05 `IsVehicleDealer` ไม่ถึงตัวคัดกรอง §82/5(6) — OCR ตั้ง "ไม่เคลม" ให้บริษัทขาย/ซ่อมรถทุกใบ**
- `ProhibitedInputVatScreener.Screen` ไม่มีพารามิเตอร์นี้ → OCR (`OcrService.cs:1270`) ใส่ `[VAT-CLAIM]` = ปิดเคลมรายบรรทัด ·
  `DocumentService.cs:16768` เตือนซ้ำ · ขณะที่ด่านเตือนอีกตัว**ในเมธอดเดียวกัน** (`:17178`) และ `TaxService.cs:273` เคารพธง ⇒ ด่านรถสองชุด
  คำคีย์คนละลิสต์ (ราก R4 "สองด่านในเมธอดเดียว")
- **สถานการณ์**: อู่ซ่อมรถเปิดธง → สแกนใบค่าอะไหล่/น้ำมันรถลูกค้า → ฟอร์มเติม "ไม่เคลม" → ผู้ใช้กด "ยืนยัน" (1-click ตามกฎเหล็ก #3) ⇒ ภาษีซื้อหาย
- **แก้**: `Helpers/InputVatVehicleRule` ตัวเดียวรับ `isVehicleDealer` ใช้ทั้ง OCR/เว็บ/TaxService · ถอดลิสต์คำคู่ที่ `:17178`

**S-06 ค่าตั้งที่มีช่องบนหน้าจอแต่ไม่มีผลเลย (18 ตัว)** — ผิดกฎเหล็ก #4 A "ห้าม silent no-op" ในระดับ "ทั้งฟีเจอร์"
| ค่าตั้ง | ช่องบนหน้า | สิ่งที่ผู้ใช้คาด | ความรุนแรง |
|---|---|---|---|
| `EtaxByEmailAutoSendOnApprove` · `EtaxByEmailEmbedXml` | `settings.html:670-671` | ส่ง e-Tax by Email ให้ลูกค้า/สรรพากรเองเมื่ออนุมัติ | **P1** (หน้าที่ส่งมอบตามกฎหมาย) |
| `AutoCloseMonthEnd` · `MonthEndClosingDay` | `settings.html:2158-2159` (UI default 5 ≠ entity 15) | ปิดงวดอัตโนมัติ/ห้ามลงย้อนหลังหลังวันที่ | **P1** (คิดว่ามีการควบคุมภายใน) |
| `InvoiceNotes/ReceiptNotes/QuotationNotes/InvoiceFooter/ReceiptFooter` | `settings.html:327-343` | ข้อความขึ้นบนเอกสารทุกใบ | P2 (มีของจริงที่ `DocumentTemplate.FooterNotes`) |
| `InvoiceEmailSubject/Body` | `settings.html:497` | หัว/เนื้ออีเมลตามที่ตั้ง | P2 |
| `LodgingProperty.AutoConfirmOnDeposit` | `lodging-settings.html:115` | ปลดติ๊ก = รับมัดจำแล้วยังไม่ยืนยันเอง | P2 (`LodgingService.Lifecycle.cs:333` ยืนยันเสมอ) |
| platform `MaintenanceMode`/`MaintenanceMessage` | `admin/site-settings.html:333` | ปิดระบบชั่วคราว | P2 |
| platform `OcrAutoCreateThreshold` | `admin/ocr-config.html:179` | เกณฑ์สร้างเอกสารอัตโนมัติ | P2 (ของจริงคือ `OcrPostingReadiness`) |
| platform `AiReviewConfidenceThreshold/AiSamplingRate/AiVerifyAgainstThaiComplianceRules` | `admin/ai-config.html:279-281` | คุม AI ทั้งแพลตฟอร์ม | P2 (ของจริงอยู่ `AiFeatureRoutingConfig` ต่อฟีเจอร์) |
- **แก้**: ทีละตัว "ต่อสาย หรือ ลบช่อง" (Q3) — ระหว่างรอ: ทำช่องเป็น disabled + ป้าย "ยังไม่รองรับ" (ทางไปต่อของผู้ใช้)

**S-07 `EnforceFullTaxInvoiceFields` ไม่มีทางตั้ง และค่าเริ่มต้นสองทางขัดกัน** — `DocumentService.cs:4826-4829` คอมเมนต์ "default = บังคับ"
ด้วย `?? true` แต่ entity default `false` (`CompanySettings.cs:283`) ⇒ บริษัทที่**ยังไม่เคย**เปิดหน้าตั้งค่า/อีเมล/LINE = บังคับ §86/4 กับ
ใบลด/เพิ่มหนี้ · บริษัทที่เคยเปิด = ไม่บังคับ — พฤติกรรมภาษีขึ้นกับ "เคยคลิกหน้าไหนมา" · ไม่มี DTO/UI ให้เลือก (CHANGELOG:1093 จดไว้แค่ "ไม่มี UI")
- **แก้**: ตัดสินค่าเดียว (Q4) · ใส่ใน DTO + หน้าตั้งค่า · migration ตั้งค่าให้ตรงกับที่ตัดสิน · เทสต์ "มีแถว/ไม่มีแถว ให้ผลเดียวกัน"

**S-08 ปิดรับสมัครสมาชิก (`RegistrationEnabled`) กันแค่หน้าเว็บ** — `register.html:633` ซ่อนฟอร์ม แต่ endpoint สมัคร (`AuthService` —
grep `RegistrationEnabled` = 0) และเส้น social login ที่สร้างบริษัท (`AuthService.cs:674`) ไม่ตรวจ ⇒ ยิง API ตรงยังสร้างบัญชี+บริษัทได้ ·
**แก้**: ตรวจที่ service ทุกทางสร้างผู้ใช้ (ด่านเดียว `RegistrationPolicy`)

### P2

- **S-10 อัตรา VAT ตายตัว 7 แทน `Company.VatRate`**: `LodgingService.cs:71,74` · `CmsBookingService.cs:491` · **`CmsLeadService.cs:185`
  ใบเสนอราคาจาก lead ใส่ VAT 7% แม้บริษัทไม่จด VAT** · (แพลตฟอร์ม `PlatformBillingDocumentIssuer:124,182,236` อ่าน `PlatformIsVatRegistered` — ยอมรับได้)
  ⇒ ใช้ `OutputVatRate.ForCompany` ตัวเดียว (มีอยู่แล้ว ใช้แค่ POS/TimeBilling)
- **S-11 `SiteCommerceConfig` ~37 ฟิลด์ไม่มีผู้อ่าน** (`CheckoutMode` `AutoSyncToErp` `AutoConfirmOrders` `EnableTaxInvoice` `DefaultVatRate`
  `PricesIncludeVat` `AutoDeductStock` `EnableCod/OnlinePayment/BankTransfer` `OrderNumberPrefix` `LowStockThresholdJson`(ไม่มีผู้เขียนด้วย) …)
  ไม่มี UI · service ทำพฤติกรรมตายตัว ⇒ กอง "ต่อสาย หรือ ลบ" ของ SYSTEM_REVIEW (Q3)
- **S-12 ภาษา/หัวเรื่องในช่องทางที่ไม่ใช่ PDF คำนวณเอง**: `DocumentEmailService.cs:330` · `DocumentLineDeliveryService.cs:58` ·
  `EmailScheduleService.cs:535` ใช้ `doc ?? company` ข้าม `template.Language` · หัวอีเมลใช้ตาราง `docTypeText` ของตัวเอง (`:332-341`) ไม่ผ่าน
  `DocumentTitleOverridesJson`/`DocumentLabels` ⇒ อีเมลบอก "ใบแจ้งหนี้" แต่ PDF แนบหัว "ใบวางบิล/ใบแจ้งหนี้" (หรือคนละภาษา) ·
  แก้: เรียก `ResolveDocumentLanguage` + `ComputeDocumentTitle` (กฎ #4 A "Resolver กลาง")
- **S-13 ล้างค่าข้อความในหน้าตั้งค่าไม่ได้**: `settings.html:2401-2436` ส่ง `value || null` · `SettingsService.cs:60-66,134-137` ตีความ null =
  "ไม่แก้" ⇒ ลบ `EmailFromName`/`EmailReplyTo`/`DefaultPaymentTerms` แล้วกดบันทึก ค่าเดิมยังอยู่ (silent no-op) · แก้: `""` = ล้าง (แบบ
  `AuthorizedSignatoryName` `:72`)
- **S-14 `EclLossRatesJson` ไม่มีผู้เขียน** — `EclAllowanceJob.cs:31` เขียนว่า "ปรับได้" แต่ไม่มี DTO/UI ⇒ อัตราสูญเสียต่อบริษัท (TFRS บทที่ 9) ใช้ไม่ได้
- **S-15 `ShareTrainingDataAnonymously` (default true) ไม่มี UI/API** — entity สัญญา "opt out here" · ข้อมูลผู้ขาย→ผังบัญชีรวมข้ามบริษัท (Q5)
- **S-16 `TaxRuleConfig.HealthInsuranceCap/MortgageInterestCap`** ตั้งได้ (`PayrollController.cs:901-903`) แต่ PIT ไม่มีรายการลดหย่อนนี้
  (`Employee` มีแค่ `LifeInsurancePremium`) ⇒ ค่าตั้งไม่มีผล + พนักงานที่มีดอกเบี้ยบ้าน/ประกันสุขภาพถูกหักภาษีเกิน
- **S-17 `Company.SocialSecurityAccountNo`** เก็บแต่ไฟล์ สปส.1-10 ใช้ `TaxId|BranchCode` เป็นหัว (`TaxFilingExportService.cs:~457-459`) —
  **ไม่ทราบ**ว่าสเปก e-Service ต้องใช้เลขบัญชีนายจ้าง 10 หลักหรือไม่ ต้องเทียบสเปกจริง · `IsWhtRegistered` echo อย่างเดียว
- **S-18 `TrialConfig` 6 ฟิลด์** (รวม `DeleteDataAfterGracePeriod`/`DataRetentionDays` ที่ฟังเหมือนนโยบายลบข้อมูล PDPA) ไม่มีผู้อ่าน
- **S-19 `DocumentTemplate`**: 12 ฟิลด์ไม่มีผู้อ่าน+ไม่มี UI (`IsEtaxTemplate` `AutoGenerateEtaxXml` `ShowQrCode` `QrCodeType` `PromptPayId`
  `QrCodeCustomData` `DefaultCopies` `CopyLabels` `ShowBilingual` `HeaderTextColor` `DigitalCertificatePath/Password`) · drift เล็ก:
  `HeaderBackgroundColor` (`PdfGenerationService.cs:2874`) และ `LogoWidth` (`:1869`) มีผลเฉพาะ HTML ไม่ถึง QuestPDF
- **S-20 `NumberSeries.Suffix/Format/ResetPeriod/CurrentNumber`** API รับ-เก็บ-ตอบกลับ แต่ตัวออกเลขจงใจไม่ใช้ (`DocumentNumberGenerator.cs:51-59`)
  ⇒ ควร reject ด้วยข้อความไทย แทนการรับเงียบ
- **S-21 `PosTerminal.SettingsJson`** เก็บ/คืนอย่างเดียว · `LodgingProperty.AccountingModeAckBy` เขียนไม่อ่าน (audit พอ — ต่ำ)
- **ของแถม (นอกขอบเขต แต่เจอระหว่างตรวจ)**: `CmsLeadService.cs:180` ส่ง `Notes: lead.InternalNotes` เข้าใบเสนอราคา — `Notes` พิมพ์บนกระดาษ
  ที่ส่งลูกค้า (คอมเมนต์ `DocumentService.cs:5640,13031` ห้ามใช้ `Notes` ด้วยเหตุนี้) ⇒ บันทึกภายในหลุดถึงลูกค้า · ส่งทีม CMS

## 4. รู้แล้ว-ยังไม่ปิด (ยืนยันซ้ำวันนี้ ไม่นับเป็นข้อใหม่)

| ID เดิม | สาระ | สถานะวันนี้ |
|---|---|---|
| ERP D-12 | เครดิตเทอม: ค่าบริษัทไม่มีผู้อ่าน · เทมเพลตมีผลแค่หน้าเว็บ · Integration `+30` 4 จุด | ยังเปิด — เพิ่มหลักฐาน: `TimeBillingService.cs:329` `+30` · CMS `DueDate=UtcNow` · server ใช้แค่ผู้ติดต่อ `DocumentService.cs:1247-1262` |
| ERP D-14 | `PreventPostToClosedPeriod` ไม่มีผู้อ่าน/ไม่มี UI | ยังเปิด |
| DECISION D1-9 / 09-21 B-10 / ERP C-R3 | §65 ตรี ใช้ปีปฏิทิน | ยังเปิด — `DocumentService.cs:12524` ส่ง `CurrentFiscalYearStart: yearStart` (1 ม.ค.) ⇒ ข้อ (10) "รายจ่ายรอบปีก่อน" เตือนผิดด้วยสำหรับรอบ เม.ย.–มี.ค. · งบประมาณ `BudgetService.cs:157` + commitment `DocumentService.cs:13372` ก็ปีปฏิทิน |
| SYSTEM T-10 | ปฏิทินภาษีไม่อ่าน `FiscalYearStartMonth` | ยังเปิด (ไม่ได้ตรวจลึก) |
| CHANGELOG:1093 | `EnforceFullTaxInvoiceFields` ไม่มี UI | ยังเปิด — มุมใหม่ใน S-07 (ค่าเริ่มต้นสองทาง) |
| 09-21 E-08 | ด่านสต็อกติดลบตัวที่สองไม่อ่าน `AllowNegativeStock` | ไม่ได้ตรวจซ้ำ (ทีม E ถือ) |

## 5. ข้อเสนอการแก้ — "resolver ตัวเดียวต่อตระกูล · server คำนวณ · หน้าแสดง"

| ตระกูล | resolver (ใหม่/ที่มี) | ผู้เรียกที่ต้องย้ายมา | เทสต์สองครึ่ง |
|---|---|---|---|
| สถานะ/อัตรา VAT | **ใหม่** `Helpers/CompanyVatStatus` (+ ใช้ `OutputVatRate` ที่มี) | 20+ จุดใน §2 แถวแรก + JS อ่านจาก `/settings` ที่ server คำนวณ | ไม่จด → ทุกทางเข้า 0%/บล็อกใบกำกับ · จด → ผลเดิมทุกใบ |
| เทอมเครดิต/วันครบกำหนด | **ใหม่** `Helpers/CreditTermResolver` (ผู้ใช้ > ผู้ติดต่อ > เทมเพลตต่อชนิด > บริษัท > ไม่มี) | `DocumentService:1247` · Integration ×4 · TimeBilling · CMS · Lodging · หน้า documents (ให้ server ส่งค่าเริ่ม) | ใบเดียวกันสร้าง 5 ทางได้วันครบกำหนดเท่ากัน |
| ผลข้างเคียงหลังออกเอกสาร | **ใหม่** `IssuedDocumentHooks.RunAsync` (e-Tax auto · by-email auto-send ถ้าต่อสาย) | ApproveDocumentAsync · POS · Integration · Import · CMS (ถอดขั้น 6 ซ้ำ) | e-Tax เปิด → ใบจาก POS มี e-Tax · ปิด → ไม่มีทุกทาง |
| นโยบายอนุมัติ | ที่มีใน `ApproveDocumentAsync` แยกเป็น `ApprovalPolicy.Evaluate` | ทางเข้าที่ประทับเอง (ตามคำตัดสิน Q2) | |
| ภาษา/หัวเรื่อง | ที่มี `ResolveDocumentLanguage` · `ComputeDocumentTitle` | อีเมล · LINE · อีเมลตั้งเวลา | ภาษา/หัวอีเมล = PDF แนบเสมอ |
| ช่อง LINE | **ใหม่** `LineChannelResolver` | `LineNotifyService` · `LineBotService` · webhook | OA บริษัท: ผูก/รับรูปได้ · ไม่มี OA: ใช้ช่องกลางเหมือนเดิม |
| API access | **ใหม่** `ApiAccessPolicy` ใน `ApiKeyMiddleware` | middleware + integration key | ปิด → 403 · เปิด → ผ่าน |
| §82/5(6) รถ | **ใหม่** `Helpers/InputVatVehicleRule(isVehicleDealer)` | Screener · `DocumentService:17178` · `TaxService:273` | dealer → เคลมได้ · ไม่ใช่ dealer → ผลเดิม |
| การสมัคร | **ใหม่** `RegistrationPolicy` | register · social login · invite | ปิด → 403 ทุกทาง · เปิด → ผลเดิม |

**กลไกกันเกิดตัวที่สาม (litmus #4)**: `tools/settings_reader_check.py` — ratchet แบบ `dead_helper_check`: ทุก public property ของ
`CompanySettings/Company(ส่วนค่าตั้ง)/SiteSettings/LodgingProperty/SiteCommerceConfig/DocumentTemplate/TrialConfig/TaxRuleConfig` ต้องมีผู้อ่าน
อย่างน้อย 1 จุดนอกไฟล์ entity/DTO/mapper/controller ที่เขียนมัน (รวมรูป `x is { P: … }`) · baseline = รายการ ❌ ใน §2 (ห้ามเพิ่มแถว ตัดออกเมื่อต่อสาย/ลบ) ·
negative test: ลบผู้อ่าน `AutoAttachWhtCertPdf` แล้วต้องฟ้อง · ข้อจำกัดที่ต้องจดไว้: checker รู้ว่า "มีผู้อ่าน" ไม่รู้ว่า "อ่านครบทุกทางเข้า"
(S-01/S-02/S-05 ต้องพึ่ง resolver + เทสต์ ไม่ใช่ checker)

## 6. คำถามเจ้าของ (ผลต่างกันคนละเรื่อง — ห้ามเดาแทน)

- **Q1 (S-01)** ธง VAT ที่เชื่อเป็นต้นทาง: (ก) `Company.IsVatRegistered` (ข้อมูลทะเบียน — แนะนำ) (ข) `CompanySettings.VatRegistered` · และบริษัทที่
  สองธงขัดกันวันนี้: (ก) รายงานให้เจ้าของบริษัทยืนยันก่อนใช้งานต่อ (แนะนำ) (ข) ยึดธงบริษัทอัตโนมัติ (ค) ยึดธงตั้งค่าอัตโนมัติ ·
  ใบกำกับที่ออกขณะธงขัดกัน → รายงานให้นักบัญชี (ตามคำตัดสินข้อ 14 "ห้ามแก้หลังบ้าน")
- **Q2 (S-02)** วงเงินอนุมัติ/SoD/Budget ใช้กับใบจาก POS และ integration ไหม: (ก) ไม่ใช้ (เป็นการขายหน้าร้าน/ระบบต้นทางอนุมัติแล้ว) แต่ e-Tax
  ต้องออกทุกทาง (แนะนำ) (ข) ใช้ทุกทาง (POS จะติดรออนุมัติ)
- **Q3 (S-06/S-11/S-18/S-19)** ค่าตั้งที่ไม่มีผล: ต่อสาย หรือ ลบ ทีละตัว — ข้อเสนอเริ่มต้น: ต่อสาย `EtaxByEmailAutoSendOnApprove` · `AutoConfirmOnDeposit` ·
  `MaintenanceMode` · `Invoice/ReceiptNotes` (เป็นค่าเริ่มต้นของช่องหมายเหตุ) · ลบ/ซ่อน `AutoCloseMonthEnd` (จนกว่าจะมี job) · `InvoiceEmail*`
  (ย้ายไปเทมเพลตอีเมลจริง) · ค่าตั้ง AI/OCR ระดับแพลตฟอร์มที่ซ้ำกับของต่อฟีเจอร์ · `SiteCommerceConfig` ทั้งก้อนผูกกับคำตัดสิน E-commerce ของ SYSTEM_REVIEW
- **Q4 (S-07)** ใบลด/เพิ่มหนี้ที่ข้อมูลผู้ซื้อ §86/4 ไม่ครบ: (ก) บังคับเสมอ ไม่มีสวิตช์ (แนะนำ — สอดคล้อง TaxInvoice ที่บังคับเสมออยู่แล้ว)
  (ข) สวิตช์ในหน้าตั้งค่า ค่าเริ่มต้นบังคับ
- **Q5 (S-15)** การรวมข้อมูลเรียนรู้ข้ามบริษัท: (ก) เปิดช่อง opt-out ในหน้าตั้งค่า (ข) ปิดเป็นค่าเริ่มต้น ต้อง opt-in (ค) คงเดิม + เขียนในนโยบายความเป็นส่วนตัว
- **Q6 (S-03)** OA ของบริษัทเอง: (ก) รองรับเต็ม (webhook ต่อบริษัท) (ข) รองรับแค่ push และเอาช่อง OA/secret ออกจากหน้าตั้งค่า

## 7. เอกสารที่ต้องขยับเมื่อแก้ (main agent รวม — ทีมนี้ไม่แตะ)

- `DOCUMENT_FLOW.md`: ทางเข้า POS/Integration/Import ไม่ผ่าน e-Tax auto (§ทางเข้า + ตาราง e-Tax) · ลำดับเครดิตเทอมจริง (ผู้ติดต่อเท่านั้นฝั่ง server) ·
  ธง VAT สองตัว (ระบุว่าตัวไหนเป็นต้นทางหลังตัดสิน Q1)
- `ACCOUNT_STRUCTURE.md`: `EnableApiAccess` ไม่คุมคีย์ที่ออกแล้ว (Connected/API) · ค่าตั้ง LINE ต่อบริษัทขอบเขตจริง
- `TEST_PLAN.md`: เคสใหม่ S-01 (สร้างบริษัทไม่จด VAT → ออกใบกำกับไม่ได้ทุกทาง / บันทึกหน้าตั้งค่าแล้วธงไม่พลิก) · S-02 (ใบกำกับ POS มี e-Tax) ·
  S-04 · S-05 · S-07 (มี/ไม่มีแถว settings ผลเท่ากัน)
- `CLAUDE.md` F2 ข้อ 2 "มี ≠ ถูกเรียก": เพิ่มว่า **"เก็บ+echo ครบ ≠ มีผล"** — การตรวจ round-trip ต้องตามถึงผู้อ่านเชิงธุรกิจ (ถ้าเจ้าของเห็นว่าเป็นหลักการใหม่)
