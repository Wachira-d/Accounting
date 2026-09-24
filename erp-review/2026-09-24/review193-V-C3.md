# รอบ 193 · ฝ่ายค้านด้านภาษี — ทีม V (8bcc733) · ทีม C3 (4fcd06b) · ที่พัก SoftMatchScope (de97a95)

> เลขบรรทัดอ้าง HEAD `d2f8b2e` (หลัง merge) · อ่านอย่างเดียวจากซอร์ส (ไม่มี .NET SDK) · ทุกข้อ CONFIRMED เปิดไฟล์ตรงจุดแล้ว · checker ใหม่ 3 ตัวรันจริง + ป้อนโค้ดสังเคราะห์ทดสอบ
> · checker เดิม 12 ตัว (namespace/using/record/nullable/service_interface/undeclared/arg_type/accessibility/tuple/flag_field/write_gate/admin_menu) + `di_cycle_check` = ผ่านทั้งหมด

---

## CONFIRMED (มี file:line)

### C-1 (P1 · S-02) e-Tax hook ประทับ `[ETAX-AUTO-FAILED]` ให้ใบขายปลีกจาก Integration **ทุกใบ** — "ข้ามโดยเจตนา" ขาดกรณีที่พบบ่อยที่สุด
- `IntegrationService.cs:761-766` payload ไม่มีข้อมูลผู้ซื้อ / `BuyerDeclinedTaxInvoice` ⇒ ผูกผู้ติดต่อ walk-in (`:1580-1590` ไม่มี TaxId · `IsWalkInCustomer=true`) แล้วสร้าง `DocumentType.TaxInvoice` Approved (`:988`) → hook `:1066`
- `EtaxAutoIssueScope.Judge` (`Helpers/EtaxAutoIssueScope.cs:66-84`) ไม่ดู walk-in / `BuyerDeclinedTaxInvoice` / `TaxService.NotFullTaxInvoice` / `IsTaxInvoiceByLaw` ⇒ `None`
- `EtaxInvoiceService.cs:223-224` โยน "กรุณาระบุเลขประจำตัวผู้เสียภาษีของผู้ซื้อ" ⇒ `IssuedDocumentHooks.StampFailureAsync` ประทับป้าย + `SaveChanges` ทุกใบ
- ผล: บริษัทที่เปิด e-Tax และรับยอดขายหน้าร้าน/PMS ผ่าน API ได้ป้าย "ล้ม · ยังไม่ถูกนำส่งกรมสรรพากร" บนใบที่**ไม่ใช่ใบกำกับเต็มรูปโดยกฎหมาย** — F2 ข้อ 8 "คำเตือนที่ฟ้องใบถูกทุกใบ" (ทีม V ตั้ง `EtaxAutoIssueScope` เพื่อแก้คลาสนี้เอง แต่ขาดกรณีนี้) · เส้นเว็บมีพฤติกรรมเดียวกันมาก่อน แต่ปริมาณจาก API สูงกว่ามาก
- ทางแก้ที่เสนอ: เพิ่ม `EtaxAutoSkip.NotFullTaxInvoice` ใน `Judge` (walk-in · `BuyerDeclinedTaxInvoice` · `IsTaxInvoiceByLaw == false`) โดยใช้ predicate ตัวเดียวกับ `TaxService.NotFullTaxInvoice` + เทสต์สองทิศ

### C-2 (P1 · S-02) ใบที่ `IsTaxInvoiceByLaw == false` (VAT 0 / ยกเว้น §81) ถูกส่งออก e-Tax ได้
- `IntegrationService.cs:962-986` ใบ TaxInvoice ที่ VAT = 0 ได้ `IsTaxInvoiceByLaw=false` (หัวพิมพ์ "ใบเสร็จ"/เลขชุด REC) แต่ `Judge` ไม่ดูธงนี้ และ `EtaxInvoiceService.GenerateAsync` ไม่มีการตรวจ `IsTaxInvoiceByLaw` เลย (grep = 0) — กันแค่ `VatAmount > 0 && NotFullTaxInvoice` ใน Receipt branch (`:160`)
- ⇒ ใบขายที่ยกเว้น VAT ทั้งใบ (`VatRate = -1`) ที่ผู้ซื้อมีเลขภาษีครบ จะได้ XML T01/T02 ประกาศต่อ RD ว่าเป็นใบกำกับ ขัดกับกระดาษ — คลาสเดียวกับที่คอมเมนต์ใน `EtaxInvoiceService.cs:120-128` เตือนไว้เอง · §81 ห้ามออกใบกำกับ (กฎเหล็ก #2 D)

### C-3 (P1 · S-02 ยังไม่ครบ "ทุกทางเข้า") ใบเสร็จรับชำระที่เป็นใบกำกับ ณ วันรับเงิน (§78/1) ไม่ได้ e-Tax อัตโนมัติ
- `DocumentService.cs:10758 CreateSettlementReceiptAsync` สร้าง Receipt `Status = Paid` (`:10817`) + `IsTaxInvoiceByLaw = carryVatFromSource` (ใบแจ้งหนี้ → ใบเสร็จถือ VAT = ใบกำกับ T03) โดยไม่เรียก `_issuedHooks` (ผู้เรียก hook ในไฟล์มีที่เดียว `:5528`)
- `tools/approved_status_writer_check.py` มองไม่เห็นเพราะ (ก) ข้ามไฟล์ `DocumentService*.cs` ทั้งไฟล์ (ข) จับเฉพาะ `Approved` ไม่จับใบที่เกิดเป็น `Paid` · รายงาน r193-V ระบุแค่ CN คืนมัดจำ (`:3934`) — ใบนี้ไม่อยู่ในรายการค้าง
- ไม่ใช่การถดถอย (เดิมก็ไม่ได้) แต่ข้อความ "e-Tax: เว็บ/POS/Integration/CMS ครบ" ไม่จริงสำหรับธุรกิจบริการ (ใบกำกับออก ณ วันรับเงิน) ซึ่งเป็นเส้นหลักของ tax point บริการ

### C-4 (P1 · S-05) ลิสต์คำรถยังมีสำเนาใน JS 2 ชุด — ชุดหนึ่ง **ปิดเคลมให้อัตโนมัติ** โดยไม่รู้จัก `IsVehicleDealer`
- `wwwroot/pages/documents.html:7803-7822 suggestVatClaim` (ผูกกับ `input` ของทุกช่องรายละเอียด `:7425`) regex `/น้ำมัน(?:รถ|เชื้อเพลิง)?|…|ปตท|บางจาก|เชลล์|ค่าน้ำมัน/` และ `/ซ่อมรถ|อะไหล่รถ|…/` ⇒ ตั้ง `claim='false'` 🚫 ทันที — อู่/ผู้ขายรถที่คีย์มือ "อะไหล่รถ"/"ซ่อมรถ" ถูกปิดเคลมเหมือนก่อนแก้ (ช่องที่ S-05 ตั้งใจปิด แค่ย้ายจาก OCR ไปเส้นคีย์มือ) และ `น้ำมัน` เดี่ยว ๆ จับ "น้ำมันพืช" ด้วย
- `documents.html:10436-10442 toggleVatClaim` = ลิสต์ชุดที่สองที่ทีม V เพิ่งถอดจากเซิร์ฟเวอร์ (`'น้ำมัน','ค่าซ่อม','อะไหล่','fuel'…`) ครบทุกคำ — ไม่อ่านธง dealer
- ข้อความ F3 ข้อ 7 "ลิสต์คำรถ 2 → 1" ไม่จริง (เซิร์ฟเวอร์ 2→1 · ทั้งระบบ 4→3) · ขัด F2 ข้อ 5 (JS ห้ามมีสำเนากติกา compliance)

### C-5 (P2 · S-05) คำเตือน §82/5(6) ตอนอนุมัติที่หายไปของบริษัทที่**ไม่ใช่** dealer (จำลองด้วยลิสต์จริงทั้งสองชุด)
ด่านชุดที่สองเดิม (`git show 8bcc733^:…/DocumentService.cs` ~17178) เทียบกับ `InputVatVehicleRule.Judge` (hay = ชื่อผู้ขาย + คำอธิบายบรรทัด — ด่านอนุมัติไม่มี raw text):

| รายการ | ผู้ขาย | เดิม | ตอนนี้ |
|---|---|---|---|
| อะไหล่ Toyota Altis | บจ. โตโยต้า บางนา | เตือน | **เงียบ** |
| น้ำมัน | หจก. สมชายปิโตรเลียม (ปั๊มอิสระ) | เตือน | **เงียบ** |
| เปลี่ยนน้ำมันเครื่อง Honda Civic | บี-ควิก | เตือน | **เงียบ** |
| ค่าซ่อมบำรุงรถยนต์นั่ง | อู่ | เตือน | **เงียบ** (ลิสต์มีแค่ "ค่าซ่อมรถ"/"ซ่อมรถยนต์") |
| Fuel | Fleet Card Co | เตือน | **เงียบ** |
| ค่าซ่อมรถเก๋ง Camry · น้ำมัน@ปตท · Fuel@Esso · ค่าเช่ารถ | — | เตือน | เตือน (คงอยู่) |
| ค่าซ่อมแอร์ · น้ำมันพืช · fuel surcharge | — | เตือน (ผิด) | เงียบ (ถูก — ตามที่ตั้งใจ) |
| ยางรถยนต์ · Gasohol 95 | — | เงียบ | เตือน (ได้เพิ่ม) |

รายงาน r193-V บอกผลข้างเคียงไว้แค่ "อะไหล่ Toyota" — ตกหล่นอีก 4 แบบ (ปั๊มอิสระ · น้ำมันเครื่อง · ซ่อมบำรุง · Fuel ผู้ขายไม่ใช่แบรนด์) · ไม่มีเทสต์กระดาษจริงล็อกแถว "เงียบ" ข้างบน

### C-6 (P1 · C3/ที่พัก) `SoftMatchScope` ใช้ที่ที่พักที่เดียว — อีก 5 ทางเข้ายังถอยไปจับชื่อ/อีเมลได้กับแถวที่ถือ**เลขภาษีอื่น** เมื่อเลขใน payload เป็นเลขใหม่
เงื่อนไข `!taxKey.TaxIdExists` กันแค่ "เลขนี้มีแล้วคนละสาขา" แต่ไม่กัน "เลขใหม่ + ชื่อ/อีเมลตรงกับนิติบุคคลอื่น" (กติกาที่ `SoftMatchScope` → `RowsWithoutTaxId` ตั้งใจปิด):
- `IntegrationService.cs:553-554 ProcessCustomerAsync` จับชื่อ → แล้ว `:642` `contact.TaxId = request.TaxId` **เขียนทับเลขภาษีของผู้ติดต่อรายอื่น** + `:662` `MayOverwriteBranch` = true (Basis null) เขียนสาขาทับด้วย ⇒ ประวัติใบกำกับของนิติบุคคลเดิมถูกโยงเป็นอีกเลข
- `IntegrationService.cs:1612-1613 ResolveContactAsync` (ใบขาย) — ชื่อตรง = ออกใบกำกับให้นิติบุคคลอื่น (§86/4)
- `CmsCustomerService.cs:350-352 AutoLinkToErpContactAsync` — อีเมล
- `ImportExportService.cs:521-523` — อีเมล (โหมด Overwrite ทับข้อมูลคนละราย)
- `Controllers/V1/DocumentsV1Controller.cs:341-349` — ชื่อข้ามภาษา (`CounterpartyNameMatcher`) บนทุกผู้ติดต่อ `IsActive` ไม่ดู TaxId
- ข้อความคอมมิต de97a95 ข้อ 7 "Integration ใช้ !TaxIdExists อยู่แล้ว · 0 จุดค้าง" จึงไม่จริง · ส่วน `CmsLeadService.cs:238-250` ยังเป็นเลขอย่างเดียว + อีเมล (อยู่ใน baseline ว่า "ทีม V ถือไฟล์" แต่ทีม V ไม่ได้แก้)

### C-7 (P1 · ที่พัก) `RowsWithoutTaxId` คืนแถวบุคคลธรรมดาให้แขกนิติบุคคลโดยไม่เติมเลข/ชื่อ
- `Lodging/LodgingService.Reservations.cs:433-439` เลขใหม่ + อีเมล/เบอร์ตรงแถวที่ไม่มีเลข ⇒ `return c.Id` ทันที ไม่เติม `TaxId`/ชื่อบริษัท/`ContactType`
- เคสจริง: ผู้จองเคยพักแบบส่วนตัว (แถวบุคคล ไม่มีเลข) แล้วจองในนาม "บริษัท เอ" + เลขภาษี ⇒ ใบเสร็จ/ใบกำกับออกในชื่อบุคคลนั้น ไม่มีเลขผู้ซื้อ (§86/4) · `GuestCompanyName`/`GuestTaxId` ที่แขกกรอกหายเงียบ
- เทสต์ `SoftMatch_NewTaxId_OnlyRowsWithoutTaxId` ล็อกแค่ enum ไม่ล็อกผลปลายทาง
- ส่วนที่ถาม: แขก**ไม่มีเลข**ยังจับอีเมล/เบอร์ทุกแถวเหมือนเดิม (`AnyRow`) ✔ · แขกนิติบุคคลเลขใหม่ที่อีเมลตรงแถว**ที่มีเลขอื่น** ⇒ ไม่หยิบ → สร้างแถวใหม่ ✔

### C-8 (P1 · C3) ใบแจ้งหนี้ค่าบริการแพลตฟอร์ม/ข้ามบริษัท: บริษัทที่สมัครใหม่มี `TaxId = "-"` ⇒ ผูกผู้ติดต่อ "-" รายแรกของใครก็ได้
- `AuthService.cs:204` และ `:676` สร้างบริษัทด้วย `TaxId = "-"` · `PlatformBillingDocumentIssuer.cs:289` ถือ "-" เป็นเลข (ไม่ใช่ช่องว่าง) → `ContactTaxBranchKey.FindAsync` (`Helpers/ContactTaxBranchKey.cs:83-89` เลขไม่ใช่ 13 หลัก ⇒ `string.Equals(c.TaxId, "-")`) ⇒ ลูกค้ารายที่สองที่ยังไม่กรอกเลขได้ผู้ติดต่อ (ชื่อ/ที่อยู่) ของลูกค้ารายแรก → ใบแจ้งหนี้/ใบเสร็จค่าบริการออกผิดคน
- แถวใหม่ที่สร้าง (`:317-338`) เก็บ `TaxId = "-"` ⇒ ปัญหาสะสมทุกรายถัดไป · มี `ExternalId = buyer.Id` ที่ผูกตัวตนได้แน่นอนแต่ไม่ถูกใช้เป็นกุญแจแรก
- `CrossTenantWorkflowService.FindPartnerContactAsync` (C3 ใหม่) คลาสเดียวกัน — คอมเมนต์บอกว่า "ไม่มีเลข ⇒ จับด้วยชื่อ" แต่ "-" ไม่ถือเป็นไม่มีเลข
- เดิมก็เป็น (`c.TaxId == taxId`) — ไม่ใช่การถดถอย แต่ C3 แตะเมธอดนี้และประกาศว่าปิดแล้ว

### C-9 (P2 · S-01 ทิศตรงข้าม) บริษัทที่สมัครผ่าน register/SSO เริ่มเป็น "ไม่จด VAT" ในเส้นเอกสารแล้ว (เดิม "จด")
- `AuthService.cs:201-208` / `:676-679` ไม่ถามสถานะ VAT ⇒ `Company.IsVatRegistered=false` (ค่า default = "ไม่รู้" ไม่ใช่คำตอบ) → factory seed แถวค่าตั้ง `false` ⇒ ใบกำกับแรกติด §90/2 · Integration ของบริษัทใหม่ตอบ "Company not VAT-registered (§90/2)" (`IntegrationService.cs:748`)
- ทิศนี้ปลอดภัยตาม DOCTRINE (มองเห็น/แก้ได้ทันที) แต่ r193-V F3 ข้อ 9 เขียนว่า "เข้มขึ้นเฉพาะบริษัทที่ไม่มีแถวค่าตั้ง" — ไม่ได้บอกว่า**ทุกบริษัทที่สมัครใหม่**เปลี่ยนค่าเริ่มต้น · หน้าจอสมัคร/ตัวเตือนตั้งค่า (`layout.js:1752`) ไม่บังคับถาม VAT ⇒ ควรถามตอนสมัครหรือขึ้น banner ชัดเจน (F2 ข้อ 3: "ไม่รู้" ควรเป็นค่าที่แยกได้)
- ส่วนที่ถาม: บริษัทสร้างผ่านวิซาร์ดแบบไม่จด → แถวค่าตั้ง `false` → `settings.html:2250` ไม่ติ๊ก → บันทึกส่ง false → `SettingsService.cs:148-155` ไม่พลิก ✔ · บริษัทที่จดและมีแถวแล้ว = ลำดับเดิม ✔

---

## PLAUSIBLE

- **P-1 e-Tax ซ้ำจากระบบต้นทาง** — `ExternalIntegration`/`InboundInvoiceRequest` ไม่มีธง "ต้นทางออก e-Tax เองแล้ว" (grep = 0) ⇒ PMS/POS ภายนอกที่ออก e-Tax/e-Receipt เองแล้ว sync เข้ามา + บริษัทเปิด `EtaxEnabled`(+`EtaxAutoSubmit`) = ใบกำกับสองฉบับต่อการขายเดียวถึง RD · ต้องให้เจ้าของตัดสินก่อนเปิดใช้กับพาร์ตเนอร์จริง
- **P-2 e-Tax ซ้ำจากการเรียกพร้อมกัน** — `IssuedDocumentHooks.cs:80-83` เช็ค `AnyAsync` แล้วค่อยสร้าง ไม่มี unique index `(CompanyId, DocumentId)` (`AccountingDbContext.cs:1306` ไม่ unique) ⇒ CMS ยืนยันชำระซ้ำพร้อมกัน/webhook ซ้ำ → สองแถว (+ส่ง RD สองครั้งถ้า AutoSubmit) · และแถวสถานะ Error ไม่กันการสร้างใหม่
- **P-3 `StampFailureAsync` เรียก `SaveChanges` ทั้ง context** (`IssuedDocumentHooks.cs:123`) — ถอดเฉพาะ `EtaxInvoice` ที่ค้าง แต่ entity อื่นที่ขั้นก่อนหน้า Add ไว้แล้วล้ม (เช่น `OnDocumentSentAsync` ข้ามบริษัทที่ถูก catch ใน `DocumentService.cs:~5515`) จะถูกบันทึกไปด้วย
- **P-4 ป้าย `[ETAX-AUTO-FAILED]` ไม่มีผู้อ่านและไม่ถูกล้าง** — ไม่มีรายงาน/ตัวกรอง/หน้าไหนค้นป้ายนี้ (grep ผู้อ่าน = 0) · สร้าง e-Tax มือสำเร็จแล้วป้ายยังอยู่ · "ล้มดัง" ได้แค่ตอนผู้ใช้เปิดหมายเหตุภายในใบนั้น (F2 ข้อ 7)
- **P-5 เส้นสร้างผู้ติดต่อสาขาใหม่ของ API v1 ไม่มีที่อยู่** (`DocumentsV1Controller.cs:~368-384`) — ไม่ copy ที่อยู่ สนญ. (ถูก) และไม่บอกผู้เรียกว่าต้อง sync ที่อยู่สาขาก่อนอนุมัติ ⇒ ใบกำกับของสาขาใหม่จะตกด่าน §86/4 ที่อยู่ผู้ซื้อตอนอนุมัติ (ดังถูกที่ แต่ response ของ `/documents` ไม่เตือน) · บทบาทลูกค้า/ผู้จำหน่ายตั้งถูกแล้ว (`isSalesDoc || siblingIsCustomer`)
- **P-6 `LoadByTaxIdsAsync` ไม่ดึงแถวที่เก็บเลขมีขีด/ช่องว่าง** (`ContactTaxBranchKey.cs:164` ใช้ `keys.Contains(c.TaxId)`) ต่างจาก `FindAsync` (`:134` ดึง `Contains("-")`) ⇒ นำเข้า/CompetitorImport สร้างแถวซ้ำของเลขที่เก็บแบบมีขีด (ถ้า migration normalize ครบแล้วจะไม่เกิด)
- **P-7 CMS lead/booking อ่าน `Company.IsVatRegistered` ขณะที่ §90/2 อ่าน `CompanyVatStatus`** (`CmsLeadService.cs:177-182` · `CmsBookingService.cs:491-496`) — บริษัทที่ธงขัดกัน (มีจริงตามรายงาน vat-flag-consistency) ได้ใบเสนอราคา/จองที่คิด VAT แล้วแปลงเป็นใบกำกับไม่ได้ หรือกลับกัน · ทีม V บอกว่าเป็นเรื่อง Q1 — แต่ควรใช้ helper ตัวเดียวกับด่านที่จะตัดสินปลายทาง
- **P-8 `settings.html:512` ติ๊ก "จด VAT" เป็นค่าเริ่มต้นใน markup** + hydrate `s.vatRegistered !== false` (`:2250`) — ถ้าโหลดค่าตั้งล้ม/ช่องไม่มาแล้วผู้ใช้กดบันทึก จะส่ง `true` และ sync ทับ `Company` (เส้นเดิมของ S-01 ในรูปใหม่)

## ตรวจแล้วไม่มีปัญหา

- `CompanySettingsFactory` ครอบทุกจุดสร้างบริษัท (3 ทาง: `CompanyService.cs:79` · `AuthService.cs:208,679`) และ lazy 6 จุด · ไม่มีทางสร้างบริษัทอื่น (seed/SampleData/admin/connect — grep `new Company`/`Companies.Add` = 3) · `Company.Id` เกิดใน ctor (`BaseEntity.cs:5`) จึงใส่ `CompanyId` ก่อน save ได้ถูก
- `CompanyVatStatus`: มีแถว = ลำดับเดิมทุกกรณี · `?? true` บนธง VAT = 0 จุด · รายงาน vat-flag-consistency: `[Authorize]` + `[RequirePermission(CompanySettingsEdit)]` (filter อ่าน `companyId` จาก route + ตรวจสิทธิ์รายบริษัท) · ทุก query กรอง `CompanyId` · แอดมินใช้ `Roles="SystemAdmin"` · อ่านอย่างเดียว
- hook ถูกเรียก**หลัง commit** ทุกทาง: `DocumentService.cs:5528` (หลัง `CommitAsync` ใน strategy) · `PosService.Orders.cs:758` (หลัง `txn.CommitAsync`) · Integration 6 จุดไม่มี transaction ห่อ (ทั้ง service และ `ProcessBatchAsync`) · CMS เรียกเฉพาะ `!approvedNow` ⇒ ไม่มีกรณี "e-Tax ของใบที่ rollback"
- Judge ข้ามใบรับมัดจำ VAT พักรอ · ใบเสร็จรับชำระใบกำกับ · CN/DN ฝั่งซื้อ · ร่าง/รออนุมัติ/ปฏิเสธ · Voided ถูกต้อง · idempotent กับการเรียกซ้ำแบบลำดับ (retry integration ถูก Skipped ด้วย ExternalRef ก่อนถึง hook)
- ป้าย `[ETAX-AUTO-FAILED]` **ต่อท้าย** `InternalNotes` ไม่ทับ — ผู้อ่านธงใน `InternalNotes` (`RecurringLateFeeAccrualJob.cs:107`) ไม่ได้รับผลกระทบ · `flag_field_overwrite_check` ผ่าน (เฝ้าแค่ `ProcessingNotes`)
- `InputVatVehicleRule`: ลิสต์ 4 ชุดย้ายมา**ตรงตัวอักษร** (เทียบ `git show 8bcc733^` ด้วยสคริปต์) · ตรรกะเท่าเดิมสำหรับ non-dealer · dealer ⇒ `Claimable=null` + `[VAT-NOTE]` ทั้ง OCR (`OcrService.cs:1318-1335`) และด่านอนุมัติ (`DocumentService.cs:16880-16892`) · รายงานภาษีซื้อ/ภ.พ.30 ไม่มีตัวตัด keyword จึงเคารพธงรายบรรทัดอยู่แล้ว · §82/5(4) ค่ารับรองไม่สนธง dealer ✔ · ขอบเขตชนิดเอกสารของด่านอนุมัติ (`WhtGateScope` = PI/Expense/PV/CIL) ครอบชุดเดิม
- S-10 CMS: booking `PricesIncludeVat: true` + อัตรา 0 เมื่อไม่จด ⇒ ยอดรวม = ราคาหน้าเว็บ ✔ · จด VAT อัตรา 7 = เท่าเดิม · lead (ราคาไม่รวม VAT) บริษัทไม่จด = ยอดเท่าราคา · ของแถม `InternalNotes` ของ lead ถูกบันทึกผ่าน `SaveChangesAsync` ที่ `CmsLeadService.cs:221`
- `ContactTaxBranchKey.Pick`: payload สนญ. → แถว 00000/ไม่ระบุ · สาขา 8 → แถว 00008 หรือ "ไม่พบ + TaxIdExists" (ไม่อ้างแถวว่างอีก) · ไม่ส่งสาขา → สนญ. → แถวเดียว → รหัสต่ำสุด (เรียงแน่นอน) · รหัสผิดรูป = ไม่ระบุ + `MayOverwriteBranch=false` · เลขไม่ใช่ 13 หลัก = เทียบตรงตัวเหมือนเดิม + เขียนสาขาได้ · API v1 รหัสผิดรูป = 400 ทั้ง `/documents` และ `/contacts/resolve` (ไม่ส่ง = ผ่าน — `TaxBranchCode.cs:76`)
- API v1 แถวสาขาใหม่: บทบาทตามฝั่งเอกสาร (`DocumentSide.IsSales`) + แถวเลขเดียวกัน · ไม่ copy ที่อยู่ สนญ. · ไม่ถอยไปเทียบชื่อเมื่อเลขมีอยู่แล้ว
- คอมไพล์ (จากการอ่าน): `IIssuedDocumentHooks` register แล้ว (`Program.cs:452`) · `di_cycle_check` ไม่มีวงกลม · ไม่มี `new PosService/IntegrationService/CmsCommerceService` นอก DI · สมาชิกที่อ้างมีจริง (`PartnerVatRate.StatutoryRate` · `PermissionKeys.CompanySettingsEdit` · `AdjustmentNoteAccount.SourceIsPurchaseSide` · `SiteCustomer.BranchCode` · `DocumentSide.IsSales`) · ไม่พบชื่อตัวแปรซ้ำในเมธอดที่แตะ (`isVehicleDealer` เหลือจุดเดียวต่อเมธอด · `approvedNow` · `quote`) · `docType` ประกาศก่อนส่งเข้า `ResolveContactAsync`

## เทสต์ที่ยังเขียวแม้ถอดการแก้

ทุกไฟล์เทสต์ใหม่ทดสอบ **helper ล้วน** (ไม่มีเทสต์ระดับ service/controller):

| ถอดการแก้ที่ | เทสต์ที่ยังเขียว | มี checker กันไหม |
|---|---|---|
| คืน `?? true` ใน DocumentService/IntegrationService | `CompanyVatStatusTests` ทั้งหมด | ❌ |
| ลบการสร้างแถวค่าตั้งใน `CompanyService`/`AuthService` | `CompanyVatStatusTests` | ❌ (checker จับแค่ `new CompanySettings`) |
| ลบ hook ใน POS/Integration | `EtaxAutoIssueScopeTests` | ✅ `approved_status_writer_check` |
| ลบ hook ใน `ApproveDocumentAsync` / CMS | `EtaxAutoIssueScopeTests` | ❌ (ไฟล์เจ้าของถูกข้าม · CMS ไม่ประทับเอง) |
| OCR/ด่านอนุมัติส่ง `isVehicleDealer: false` | `InputVatVehicleRuleTests` · screener tests | ❌ |
| Integration/Import ไม่เช็ค `MayOverwriteBranch` | `ContactTaxBranchKeyTests` | ❌ |
| API v1 ส่ง `branchCode: null` กลับไป | `ContactTaxBranchKeyTests` | ❌ (checker ดูแค่ `TaxId ==`) |
| ที่พักถอด `SoftMatchScope` | `SoftMatch_*` ทั้ง 4 | ❌ |
| `SyncOwnerCheck_*` | จำลอง `Pick` ในเทสต์เอง — ถอดจาก `ContactsV1Controller` ก็ยังเขียว | ❌ |

## checker ใหม่ — ผลรัน + probe

| checker | ผลรัน | self-test | พลาดของจริง (probe สังเคราะห์) | ฟ้องผิด |
|---|---|---|---|---|
| `approved_status_writer_check` | ✅ 4 = baseline 4 | ✅ | ternary ข้ามบรรทัด (`Status = auto\n ? …Approved`) · ใบที่เกิดเป็น `Paid` (C-3) · `Status = st` ตัวแปร · ทั้งไฟล์ `DocumentService*.cs` | — |
| `company_settings_factory_check` | ✅ 0 | ✅ | `_db.CompanySettings.Add(new() {…})` (target-typed ใน argument) · `CompanySettings? s = new()` | — |
| `contact_taxid_only_match_check` | ✅ 7 = baseline 7 | ✅ | จับใน list ที่ `await …ToListAsync()` แล้ว (`all.FirstOrDefault(c => c.TaxId == t)`) · `string.Equals(c.TaxId, t)` · ประโยคที่มีคำ `BranchCode` ที่ไหนก็ได้ถูกยกเว้น (`.Where(c => c.TaxId == t).Select(c => new { c.BranchCode })`) · การถอยไปชื่อ/อีเมล (C-6) อยู่นอกขอบเขต | `doc.Contact.TaxId == company.TaxId` (ตรวจ "ออกใบให้ตัวเอง" — ไม่เกี่ยวกับสาขา) |

วันนี้ในเรพยังไม่พบรูปที่ probe พลาด (grep ternary ข้ามบรรทัด/`Status = <ตัวแปร>` บน Document = 0) ⇒ ความเสี่ยงเป็นของ "ทางเข้าที่สาม" ในอนาคต ยกเว้น C-3 ที่มีอยู่แล้ววันนี้
