# รอบ 193 — ฝ่ายค้าน security/ค่าตั้ง · ตรวจงานทีม W (`11e79b2` + `10ed065` · merge `1c37cec`)

> อ่านอย่างเดียว · ไม่มี .NET SDK ในเครื่อง ⇒ ตรวจจากการอ่านซอร์ส + รัน checker ของเรพ + ทดลอง `settings_reader_check` ในสำเนาใน scratchpad
> ผลตรวจอ้างบรรทัดของ HEAD `52b8a42` (ไฟล์ที่ทีม W แตะไม่ถูกแก้ต่อหลัง merge ยกเว้น `DocumentController.cs` ที่มีคอมมิตอื่นเพิ่ม `GetLinkedScan` — บรรทัดของ purge เลื่อนไปเป็น 915–931)

## 0. สรุปสั้น

- **C1 ปิดเส้นที่รายงานไว้ครบ** (คีย์ → เปิด `EnableApiAccess` → ออกคีย์ `acc_` สิทธิ์เต็ม): `EnsureOwnerAccessAsync` ปฏิเสธคีย์ก่อนดู role และครอบผู้เรียก 17 จุดจริง, `RolePermissionService`, template role, `OwnerConfig`, `IntegrationController.RequireOwnerAsync` (`int_`) ปิดครบ และ `EnableApiAccess`/`MaxApiKeys` มีทางเขียนทางเดียว
- **แต่ "งานระดับเจ้าของที่คีย์ทำได้" ยังไม่หมด.** คีย์ที่ถือตัวตนเจ้าของ (`acc_` ทุกดอกถือ `CreatedByUserId` = เจ้าของ/แอดมิน · `int_` รุ่นเก่าที่สวมด้วยอีเมลเจ้าของ) ยังทำสิ่งเหล่านี้ได้: **ลบเอกสารถาวรโดยข้ามช่วงเก็บรักษา 5 ปี** (`force=true`) · ลบ 50 ทวิถาวร · ปิดการอนุมัติ/SoD/เปลี่ยนนโยบายภาษีและข้อมูลรับรอง e-Tax ผ่าน `PUT settings` · ผูก/ถอดบริษัทอื่นของเจ้าของใน Account Plan. ถ้าไม่นับเรื่องคีย์ **สมาชิกทุกบทบาทยกเลิก subscription / เปลี่ยนแพ็กเกจ / ขยาย trial ได้ไม่จำกัดวัน** (ข้อนี้มีอยู่ก่อนแล้ว ทีม W ไม่ได้ทำให้เกิด)
- **S-12 หน้าเว็บไม่ได้ใช้ของที่ทีมแก้.** สองหน้าต่างส่งอีเมลใน `documents.html` เติมหัว/เนื้ออีเมลเองฝั่ง JS เป็นภาษาไทยเสมอแล้วส่งเป็นค่าที่ไม่ว่าง ⇒ `ResolveDocumentHeadingAsync` ถูกใช้เฉพาะตอนเรียก API ตรง, LINE และอีเมลตั้งเวลา
- **S-06 มีค่าที่ถูกล้าง:** `admin/site-settings.html` เลิกส่ง `maintenanceMessage` แต่ server เขียนทับค่านี้โดยไม่มีเงื่อนไข ⇒ กดบันทึกหน้านั้นครั้งไหน ค่าก็ถูกล้างเป็น null ครั้งนั้น
- เทสต์ใหม่ทั้ง 7 ไฟล์เรียกแต่ pure helper **ไม่มีไฟล์ไหนตรวจการต่อสาย** ⇒ ถ้าถอดบรรทัดที่เรียกด่านออกทุกจุด เทสต์ก็ยังเขียวทั้งหมด (ดู §4)
- ด้านคอมไพล์ (อ่านอย่างเดียว): ไม่เจอความเสี่ยง · `di_cycle` / `record_arg` / `using` / `nullable_arg` / `arg_type` / `undeclared_local` / `service_interface` / `write_permission_gate` / `dead_helper` ผ่านทั้งหมด · static helper ที่เปลี่ยนลายเซ็นมีผู้เรียกครบทุกจุด

---

## 1. CONFIRMED (มี file:line)

### W-C1 (P0) — คีย์ที่ถือตัวตนเจ้าของยัง "ลบเอกสารถาวรข้ามช่วงเก็บรักษา §87/3 · พ.ร.บ.บัญชี ม.10" ได้
- `Accounting/Controllers/DocumentController.cs:915-930` `DELETE documents/{id}/purge?force=true&reason=…` เช็กสิทธิ์แบบ inline ด้วย `role != UserRole.Owner` อย่างเดียว ไม่ได้เรียก `OwnerActionGuard`. `DocumentService.PurgeDocumentAsync` (`DocumentService.cs:8022+`) ข้าม `RetentionUntil` เมื่อได้ `force` และไม่มีด่านอื่นซ้อน
- **ใครทำได้:** คีย์ `acc_` ที่มี `CanDelete` (claim NameIdentifier = `CreatedByUserId` ซึ่งต้องเป็นเจ้าของ/แอดมินเสมอ เพราะคนออกคีย์ได้มีแค่เจ้าของ) และคีย์ `int_` รุ่นเก่าในช่วงผ่อนผัน ซึ่งได้สิทธิ์เต็มรวม `CanDelete` และสวมเจ้าของได้ด้วย `X-Acting-User: <อีเมลเจ้าของ>`. นี่คือโมเดลภัยเดียวกับ C1
- ทีม W จัดจุดนี้เป็น "override ทางธุรกิจ ไม่ใช่การให้สิทธิ์" (r193-W §5.3). **ไม่เห็นด้วย** เพราะผลคือทำลายหลักฐานบัญชีที่กฎหมายบังคับให้เก็บ และทำจากเครื่องที่ไม่มีคนนั่งอยู่
- ในไฟล์ถัดไปเป็นชนิดเดียวกัน: `WithholdingTaxCertController.cs:78-93` ลบ 50 ทวิถาวร (เจ้าของเท่านั้น · เช็ก inline)
- ทางแก้: ต่อ `OwnerActionGuard.IsApiKeyRequest(HttpContext)` เข้าสองจุดนี้ แล้วให้ `write_permission_gate_check` (หรือ checker แคบ ๆ) ฟ้องเมื่อพบ `role != UserRole.Owner` แบบ inline ใน controller

### W-C2 (P1) — `PUT settings` ยังให้คีย์ที่ถือตัวตนเจ้าของเปลี่ยน "นโยบาย" ได้ทุกช่อง ยกเว้นสองช่องของ API
- `SettingsController.cs:51-62` ด่าน `[RequirePermission(CompanySettings.Edit)]` ผ่านให้ตัวตนเจ้าของเสมอ (`PermissionService.cs:53`) และด่านที่กันคีย์เพิ่มเฉพาะ `EnableApiAccess`/`MaxApiKeys`
- ⇒ คีย์ยังเปลี่ยน `RequireApprovalForDocuments` · `ApprovalThresholdAmount` · `SodBlockSelfApproval` (ห้ามอนุมัติเอกสารตัวเอง) · `AllowNegativeStock` · `WhtRecognitionBasis` · `EtaxRdApiKey/Secret` · `EtaxCertificatePath/Password` · `DocumentTitleOverridesJson` ได้. เท่ากับคีย์ปิดด่านควบคุมภายในของบริษัทเองได้
- ขัดกับกติกาที่ `OwnerActionGuard` เขียนไว้เองว่า "งานที่ให้สิทธิ์/**เปลี่ยนนโยบาย**/ปิดงวด ต้องมาจากคนที่ล็อกอิน" (`OwnerActionGuard.cs` doc) · `EtaxController:310` (`RequireEtaxAsync(..., CompanySettingsEdit)`) มีลักษณะเดียวกัน
- ทางแก้ที่ง่ายที่สุด: ปฏิเสธ `PUT settings` ทั้งเส้นเมื่อเป็นคำขอจากคีย์ (หน้าตั้งค่าเป็นงานของคนอยู่แล้ว) · ถ้าต้องให้คีย์เขียนบางช่อง ให้ใช้ allow-list

### W-C3 (P1 · ของเดิม ไม่ได้เกิดจากทีม W) — การเงินของ subscription ไม่มีด่านเจ้าของเลย (แม้แต่ JWT)
- `SubscriptionController.cs:114-120` (`trial/extend`) · `:137` (`convert`) · `:148` (`PUT plan`) · `:159` (`cancel`) มีแค่ `[Authorize]` ส่วน `TenantAccessMiddleware` ตรวจแค่ว่าเป็นสมาชิก ⇒ **พนักงานบทบาท "ดูอย่างเดียว" หรือคีย์ใดก็ได้ ยกเลิก subscription ของบริษัทได้**
- `SubscriptionService.cs:298` `additionalDays = request.AdditionalDays > 0 ? request.AdditionalDays : tc.ExtensionDays` — ผู้เรียกกำหนดจำนวนวันเองโดยไม่มีเพดาน (`MaxExtensions` จำกัดแค่จำนวนครั้ง) ⇒ ขยาย trial ครั้งเดียว 36,500 วันได้
- `AccountSubscriptionController.cs:122` `start-trial` · `:174` `attach` · `:212` `detach` เช็ก `cu.Role == Owner` แบบ inline และรับ `CompanyId` จาก **body**. `TenantAccessMiddleware` ดูเฉพาะ route/`X-Company-Id` ⇒ คีย์ของบริษัท A (ตัวตนเจ้าของ) ผูก/ถอด**บริษัท B** ของเจ้าของคนเดียวกันใน Account Plan ได้ ซึ่งเป็นการข้ามขอบเขตบริษัทของคีย์. ทีม W จดไว้ว่า "×4 ไม่ได้แตะ" — ยืนยันว่าเป็นจริง
- ทางแก้: `EnsureOwnerAccessAsync` (ซึ่งตอนนี้ปฏิเสธคีย์อยู่แล้ว) ในทั้ง 7 action · ใส่เพดาน `AdditionalDays ≤ tc.ExtensionDays`

### W-C4 (P1) — S-12 ไม่มีผลกับเส้นที่ผู้ใช้ส่งอีเมลจริงบนเว็บ (silent no-op · กฎ #4 A · F2 ข้อ 5)
- `documents.html:6860-6884` `refreshEmailTemplate()` ประกอบหัว `${Layout.docTypeLabel(docType)} - ${customer}` และเนื้อ HTML ภาษาไทยเสมอ (ใช้ตารางชื่อชนิดเอกสารของ JS เอง ไม่รู้จักภาษาของเทมเพลต/`DocumentTitleOverridesJson`/หัวรวม/หัวอย่างย่อ) แล้ว `:9051-9052` ส่งค่านี้ไปเป็น `subject`/`body` ที่ไม่ว่าง
- `documents.html:10312-10313` หน้าต่าง "ส่งอีเมล" ของรายการเอกสารก็เติม `defSubject`/`defBody` เป็นภาษาไทยเช่นกัน แล้วส่งไปที่ `:10344` / e-Tax ที่ `:10340`
- `DocumentEmailService.cs:61-62` ใช้หัว/เนื้อที่ server สร้างเฉพาะเมื่อ `req.Subject`/`req.Body` ว่าง ⇒ **ใบภาษาอังกฤษที่ส่งจากเว็บยังได้ "เรียน …" ภาษาไทยครอบ PDF ภาษาอังกฤษ** ซึ่งเป็นบั๊กเดียวกับที่ S-12 ตั้งใจแก้ · commit message ข้อ 8 ที่เขียนว่า "4 ช่องทางใช้ resolver ตัวเดียวกันครบ" จริงเฉพาะฝั่ง server
- เพิ่มเติม: `refreshEmailTemplate` เอาชื่อลูกค้าต่อเข้า HTML ของเนื้ออีเมลโดยไม่หนี (`:6879`) ⇒ ยังเหลือช่อง HTML injection ในอีเมลที่ส่งถึงลูกค้า ซึ่งเป็นสิ่งที่ S-12 ตั้งใจปิด
- ทางแก้: เพิ่ม `GET document/{id}/email-template` ที่คืน `BuildDefaultTemplate(doc, heading, …)` แล้วให้ทั้งสองหน้าต่างเติมค่าจาก endpoint นี้ (หรือส่ง null เมื่อผู้ใช้ไม่ได้แก้ข้อความ)

### W-C5 (P2) — S-06: บันทึกหน้า site-settings แล้ว `MaintenanceMessage` ถูกล้างทุกครั้ง
- `admin/site-settings.html` ถอด `maintenanceMessage` ออกจาก payload แล้ว แต่ `AdminController.cs:1827` ยังเขียน `settings.MaintenanceMessage = request.MaintenanceMessage;` **โดยไม่มีเงื่อนไข** ⇒ ค่าเดิมกลายเป็น null ทุกครั้งที่กดบันทึก. ที่ r193-W §1.8 และคอมเมนต์ในหน้าเขียนว่า "ค่าเดิมคงอยู่" จึงไม่จริงสำหรับช่องนี้
- ผลกระทบวันนี้ต่ำ (ค่านี้ไม่มีผู้อ่าน) แต่ขัดกับเงื่อนไขที่ทีมตั้งไว้เอง ("ห้ามลบค่า — รอเจ้าของตัดสิน Q3") · `MaintenanceMode` (`:1825` มี `HasValue`) และ `OcrAutoCreateThreshold` (`:2341` มี `HasValue`) ปลอดภัย
- ทางแก้: `if (request.MaintenanceMessage != null)` · ช่องข้อความอื่นของหน้า landing ในเมธอดเดียวกัน (`:1808-1821`) ก็เขียนทับแบบไม่มีเงื่อนไขเช่นกัน แต่หน้าส่งค่าเหล่านั้นครบทุกครั้ง จึงยังไม่เกิดปัญหา

### W-C6 (P2) — ค่าตั้งที่ผู้ใช้กดได้แต่ไม่มีผล ยังไม่ถูกล็อก (checker จับได้ แต่อยู่ใน baseline)
- `TaxRuleConfig.HealthInsuranceCap` / `MortgageInterestCap`: `payroll.html` ให้แก้ได้ และ `PayrollController.cs:940-942` บันทึกค่า แต่ตัวคำนวณ ภ.ง.ด.1 ไม่อ่านสองค่านี้ (`PayrollService.cs:4251` อ่าน `TaxRuleConfigs` เฉพาะบางช่อง) ⇒ **แก้เพดานลดหย่อนแล้วภาษีหัก ณ ที่จ่ายไม่เปลี่ยน โดยไม่มีอะไรบอก** · เรื่องนี้แตะภาษี จึงควรล็อกหรือต่อสายก่อนช่องที่ S-06 ล็อกไปแล้ว
- `EtaxByEmailAutoSendOnApprove` (`settings.html:1816/1854`) ติ๊กได้แต่ไม่มีงานส่งอัตโนมัติ — ทีม W จงใจส่งต่อให้ทีม V (r193-W §5.1) · ระหว่างรอควรติดป้ายชั่วคราว ไม่ปล่อยให้ติ๊กแล้วเงียบ

### W-C7 (P3) — ข้อความ 403 หลังบังคับ `CompanySettings.Edit` ไม่บอกทางไปต่อ
- `RequirePermissionAttribute.cs:61` ตอบ `"ไม่มีสิทธิ์เข้าถึง (ต้องการ CompanySettings.Edit)"` แล้ว `settings.html` save → `Layout.toast(e.message)` (หน้าไม่ได้ตรวจสิทธิ์ก่อน ⇒ ผู้ใช้กรอกทั้งหน้าก่อนจึงรู้ว่าบันทึกไม่ได้)
- ข้อความไม่ได้บอกว่า "ขอเจ้าของบริษัทเปิดสิทธิ์ ตั้งค่าบริษัท ที่หน้าบทบาท" (F2 ข้อ 8) · ต่างจาก `IntegrationController.RequireSettingsAsync` (`:75`) ที่บอกไว้

---

## 2. PLAUSIBLE (ต้องมี DB/runtime ยืนยัน)

1. **P2 — hash chain ของ audit น่าจะ verify ไม่ผ่านเมื่ออ่านกลับจาก DB (มีมาก่อนทีม W แต่ P2 ตั้งอยู่บนสมมติฐานนี้).** `AuditHashChain.Canonical` ใช้ `{timestamp:O}` (`AuditHashChain.cs:32`) ฝั่งเขียนได้ `Kind=Utc` + 7 หลัก (`…1234567Z`) · ฝั่งอ่านภายใต้ `EnableLegacyTimestampBehavior` (`Program.cs:50`) ได้ `Kind=Unspecified/Local` และความละเอียดถึงไมโครวินาที ⇒ สตริงต่างกัน ⇒ `VerifyHashChainAsync` ล้มตั้งแต่แถวแรก. เทสต์ `AuditHashChainTests` ใช้เวลาวินาทีเต็ม + `Kind=Utc` ทั้งสองฝั่ง จึงไม่เคยเจอ ⇒ ต้องมีเทสต์ round-trip ผ่าน Npgsql จริง (กฎ #4 G "control ต้องมี round-trip test")
2. **P2 — `AddChainedAuditLog` เรียกสองครั้งใน SaveChanges เดียว = chain แตกเป็นสองกิ่ง.** `ApplyAuditHashChain` (`AccountingDbContext.cs:3543-3558`) อ่าน `lastHash` จาก **DB** ทุกครั้ง ⇒ แถวที่สองที่ยังไม่ได้ save จะได้ `PrevHash` เดียวกับแถวแรก. วันนี้มีผู้เรียกจุดเดียวที่ SaveChanges ทันที จึงยังไม่เกิด แต่ doc-comment ชวนให้ย้ายอีก 9 จุดมาใช้ ⇒ ควรเก็บ `lastHash` ที่รอ save ไว้ต่อบริษัทใน context หรือบังคับว่าหนึ่ง SaveChanges เรียกได้ครั้งเดียว. นอกจากนี้ฝั่งเขียนเรียงตาม `Id` แต่ฝั่ง verify เรียงตาม `Timestamp` แล้วค่อย `Id` (`AuditTrailService.cs:108`) ⇒ ถ้าแถวที่ประทับเวลาก่อนถูก save ทีหลังจะ verify ไม่ผ่าน (ของเดิม)
3. **S-12 — N+1 ในอีเมลตั้งเวลา:** `ResolveDocumentHeadingAsync` ยิงประมาณ 6–9 query ต่อใบ (doc+brand · contact · company · settings · template pool · ใบต้นทาง/ใบเสร็จแยก/วันชำระ · SiteSettings) และถูกเรียก**ต่อใบ**ใน `EmailScheduleService` (`:533`) ⇒ กฎ DueSoon/Overdue ที่ครอบหลายร้อยใบ = หลายพัน query ต่อรอบ. มี batch resolver `ResolveDocumentTitlesAsync` อยู่แล้ว (แต่ไม่ได้ทำขั้น ServedAsReceipt)
4. **S-08 — ทางไปต่อของแอดมินแพลตฟอร์มใช้ได้แต่อึดอัด:** ทั้งระบบสร้างบริษัทได้ 3 ที่ และเส้นที่แอดมินใช้ได้ขณะปิดรับสมัครมีแค่ `POST /api/companies` ใน session ของแอดมินเอง ⇒ **แอดมินกลายเป็น Owner ของบริษัทลูกค้า** (`CompanyService.cs:99-103`) แล้วต้องเพิ่มลูกค้าผ่าน `AdminController` create-user + `CompanyId` ⇒ แอดมินค้างเป็นสมาชิกเจ้าของของบริษัทลูกค้า (เรื่อง PDPA/หลักแบ่งหน้าที่) · ไม่มี endpoint "สร้างบริษัทให้ผู้ใช้ X"
5. **ทิศตรงข้ามของ `CompanySettings.Edit`:** ไม่มีบทบาทไหนได้คีย์นี้เป็นค่าเริ่มต้น (`AccountantDefaultKeys` ไม่มี · role แม่แบบ "นักบัญชี" ได้แค่เมนู `settings` ซึ่งเป็นคนละคีย์กับ `perm:CompanySettings.Edit` — `RolePermissionService.cs:289-294`) ⇒ หลัง deploy **ทุกบริษัทจะมีแค่ Owner/CompanyUser.Role=SystemAdmin ที่บันทึกหน้าตั้งค่า/โลโก้/ตรายาง/ลำดับเลขได้**. ไม่มีบริษัทไหนล็อกตายตราบใดที่ยังมี Owner แต่กรณีที่เจอบ่อยคือ "สำนักงานบัญชีดูแลให้ เจ้าของไม่เคยล็อกอิน" (ExternalAccountant/Accountant) ซึ่งจะติดทันที. ความไม่สอดคล้อง: `EnsureOwnerAccessAsync` ข้ามให้ `User.IsSystemAdmin` แต่ `PermissionService.HasPermissionAsync` ไม่ข้าม
6. **Legacy-key regenerate (P3):** หลังหมุนคีย์ สิทธิ์ = ค่าที่เก็บไว้ (คีย์รุ่นเก่าที่ migration ตั้งไว้ = อ่านอย่างเดียว) ⇒ คู่ค้าที่ใช้เขียนข้อมูล (TakeTime) จะล้มทันทีหลังเจ้าของกดหมุน · หน้าเว็บเตือนแล้ว แต่ response ของ regenerate ไม่ได้ส่งสิทธิ์ที่มีผลกลับมาให้ UI แสดงซ้ำ
7. **ช่องลับ e-Tax "ว่าง = คงเดิม"** ปลอดภัยในทิศ "ไม่ล้างโดยไม่ตั้งใจ" แต่ **ไม่มีทางล้าง RD API key/secret ที่รั่วจากหน้าจอเลย** (ต้องใส่ค่าหลอกทับ) · `EtaxCertificatePath` เป็น path อิสระบนเซิร์ฟเวอร์ที่ผู้มี `CompanySettings.Edit` ตั้งได้ (`SettingsService.cs:185`) ⇒ ชี้ไปไฟล์ใบรับรองของบริษัทอื่นบนดิสก์เดียวกันได้ถ้ารู้ path + รหัส (ของเดิม · ขัดกฎ D "ไฟล์ห้ามเขียน local disk ตรง")

---

## 3. ตรวจแล้วไม่มีปัญหา

- **C1 แกนหลัก:** `CompanyService.EnsureOwnerAccessAsync` (`:595-610`) เรียก `OwnerActionGuard.EnsureNotApiKey` ก่อน role · `callers.py` นับผู้เรียกจริงได้ 17 จุด (Webhook 5 · Settings api-keys 2 · Accounting ปิดงวด/เปิดงวด/ปิดปี/ยอดยกมา 4 · Company update/add-user/delete/remove/role/name 6) ตรงกับที่ทีมเขียน · `RolePermissionService.EnsureOwnerAccessAsync` (Create/Update/Delete/Assign role) · `PermissionCatalog` template · `OwnerConfig` · `SampleData`/`BulkCleanup` (เส้นที่ตั้งใจให้คีย์เข้าได้ยังผ่าน `IsCompanyScopedApiKey` ตามเดิม) · `IntegrationController.RequireOwnerAsync` (`int_` ออก/แก้/ลบ/หมุนคีย์/ผูกผู้ใช้ — ปิดไว้แล้วตั้งแต่ก่อนรอบนี้ ตอนนี้ใช้ตัวบอกตัวเดียวกัน)
- **ทางอื่นที่คีย์จะใช้เปลี่ยนสิทธิ์ตัวเอง:** `EnableApiAccess`/`MaxApiKeys` มีผู้เขียนที่เดียว (`SettingsService.cs:167-168`) · เชิญสมาชิกสร้าง `CompanyInvitation` ที่เดียว (`CompanyService.cs:417` ภายใน `AddUserAsync`) · โอนความเป็นเจ้าของ = `UpdateUserRoleAsync` · `InvitationController` มีแค่ by-token/accept ⇒ ปิดครบ
- **ตัวบอกว่าเป็นคีย์:** ดูทั้ง `Items["IsApiKeyAuth"]` และ claim `AuthMethod` · JWT ไม่ได้ mint claim `AuthMethod` (grep แล้วมีแค่ middleware) จึงไม่เกิดการตีเจ้าของตัวจริงเป็นคีย์ · ทิศตรงข้ามคือเจ้าของที่ใช้ JWT ผ่านทุกเส้นเหมือนเดิม (มีขอบเดียว: ไคลเอนต์ที่ส่ง `X-Api-Key` **พร้อม** Bearer จะถูกตีเป็นคีย์ — ถือว่าถูกทาง)
- **DI:** `CompanyService`/`RolePermissionService` รับ `IHttpContextAccessor?` แบบ optional · `AddHttpContextAccessor()` ถูก register แล้ว (`Program.cs:418`) · ไม่มีใคร `new` สองคลาสนี้เอง · `di_cycle_check` ผ่าน (193 service)
- **S-04:** เพิ่ม query 1 ตัว (`CompanySettings` ตาม `CompanyId`) ต่อคำขอของ `acc_` ไม่มี N+1 · ตอบ 403 และ `return` ก่อน `_next` ⇒ ไม่ถึง controller · มีผลทันทีทุกเครื่อง · ไม่มีแถว = ไม่ผ่าน (ตรงกับ entity default และตรงกับเงื่อนไขออกคีย์) · **คีย์ `int_` รวมคีย์รุ่นเก่าในช่วงผ่อนผันไม่ได้ขึ้นกับสวิตช์นี้** — จงใจ และบอกไว้ใต้สวิตช์แล้ว (เจ้าของที่สงสัยว่าคีย์ `int_` รั่วต้องไปปิดที่หน้าเชื่อมต่อระบบ)
- **S-08:** สร้างบริษัททั้งระบบมี 3 ที่ (`CompanyService.cs:60` · `AuthService.cs:208` · `:690`) กั้นครบทั้งสาม · สร้างผู้ใช้ 3 ที่ (`AuthService.cs:168` register · `:673` SSO บัญชีใหม่ · `AdminController.cs:1046` แอดมิน — ตั้งใจไม่กั้น) · SSO ทุก provider เดินเมธอดเดียว (`SsoLoginAsync`) และตั๋ว SSO ที่พาไปสมัครจะกลับเข้า `RegisterAsync` ซึ่งกั้นอีกชั้น · `IsInvitationUsable` เป็น predicate เดียวกับ `ConsumeInvitationAsync` และเทียบอีเมลแบบไม่สนตัวพิมพ์ + trim · คำเชิญของผู้ใช้เดิมผ่าน `InvitationController` ไม่ถูกกั้น
- **S-12 ฝั่ง server:** `ResolveDocumentHeadingAsync` เดินขั้นเดียวกับ PDF ครบ — เทมเพลตจาก pool แบบ `AsNoTracking` (ไม่ทำให้ template ที่ถูก `EnforceTaxDocTemplateInvariants` แก้หลุดไปถูก save ใน context ของผู้เรียก) · `ResolveServedAsReceiptAsync` (หัวรวมใบกำกับ/ใบเสร็จ) · `AbbreviatedTaxInvoiceRule.CanIssue` ด้วย argument ชุดเดียวกับทั้งสอง renderer (`PdfGenerationService.cs:1839` · `.DocumentRenderer.cs:65`) ⇒ ใบกำกับอย่างย่อที่ถูกลดหัวตาม ภ.พ.06 ได้หัวตรงกัน · ภาษาเทมเพลต en และ `DocumentTitleOverridesJson` ถูกใช้ · ไฟล์ PDF ที่แนบอีเมลสร้างด้วย `TemplateId=null`, `Language=null` (`DocumentEmailService.cs:99-100, 239-240`) ตรงกับ heading
- **static refactor:** helper 4 ตัวถูกเรียกเฉพาะใน `PdfGenerationService.cs` (partial อื่นไม่ได้เรียก) · เนื้อเมธอดไม่มีการอ้าง `_db`/สมาชิก instance เหลือ · `DocumentService.GetReceiptIssueModeAsync` เป็นเมธอดคนละคลาส · `DocumentHeading` record มีจริง · `HydrateContactAsync` extension ตรงลายเซ็น · `BuildDefaultTemplate` มีผู้เรียกแค่ 2 จุดในไฟล์ + interface
- **HtmlEncode:** `ComposeDefaultTemplate` หนีหัวเอกสาร/เลขเอกสาร/ชื่อลูกค้าในเนื้อ HTML · หัวเรื่องอีเมลเป็นข้อความล้วนไม่หนี (ถูก) · `EmailScheduleService.Render(..., htmlEncodeValues:true)` ใช้กับ body เท่านั้น · ค่าใน ctx ทุกคีย์เป็นข้อความล้วน ไม่มี placeholder ที่เป็น HTML สำเร็จรูป ⇒ ไม่มีการหนีซ้ำสองชั้น · อักษรไทยไม่ถูก `WebUtility.HtmlEncode` แปลง
- **S-13:** ช่องที่ล้างได้ (`DefaultPaymentTerms` · `EmailFromName` · `EmailReplyTo` · `VatRegistrationDate` · Bookkeeper ×2) ไม่มีช่องไหนที่ถ้าว่างแล้วระบบพัง — SMTP host/รหัส **ไม่ได้อยู่ใน DTO นี้** (อยู่คนละ endpoint) · `EmailFromName` null ⇒ ผู้ส่งใช้ค่า default ของ sender · ช่องลับ e-Tax ส่ง `|| null` = คงเดิม (ดู PLAUSIBLE 7 เรื่อง "ล้างไม่ได้")
- **S-20:** หน้าเว็บส่งแค่ `{documentType, prefix}` (`settings.html:2427-2429`) ⇒ ผ่านทั้งสร้างและแก้ · ของเดิม `CreateNumberSeriesRequest.Format` เป็น `string` ไม่ nullable (ASP.NET ใส่ `[Required]` ให้โดยปริยาย) — ตอนนี้เป็น optional จึงไม่มีทางถูกตีกลับ 400 · ส่งค่าเดิมกลับมาทั้งก้อนก็ผ่าน · `CurrentNumber` ไม่ขยับเพราะตัวออกเลขไม่ได้เขียนค่านี้ จึงไม่มีปัญหา race ระหว่าง GET กับ PUT
- **S-06:** ช่องใน `settings.html` 9 ช่องถูกถอดออกจาก payload และ server ตีความ null = ไม่แก้ (`SettingsService` ทุกบรรทัดมี `!= null`/`HasValue`) · `MaintenanceMode` และ `OcrAutoCreateThreshold` เป็น `bool?`/`decimal?` + `HasValue` · ai-config ส่งค่าเดิมกลับ · ข้อยกเว้นเดียวคือ W-C5 · ไม่เจอช่องที่ "มีผลจริงแต่ถูกล็อก" (ทั้ง 13 ช่องอยู่ใน baseline ของ checker และ grep ยืนยันว่าไม่มีผู้อ่าน)
- **P3/P5:** `IsLegacyKey` มีผู้เขียนแค่สามจุดและทุกจุดเขียน `false` (สร้าง · แก้ · หมุนคีย์) ⇒ ไม่มีทางย้อนกลับเป็นรุ่นเก่า · `LegacyDaysRemaining` ปัดขึ้น · หมดแล้ว = 0 · ไม่ใช่รุ่นเก่า = null · การลบ `DateTime` ไม่ดู Kind และคอลัมน์เก็บเป็น UTC จึงคำนวณถูก · `AsUtc` ใช้เฉพาะตอนส่งออก
- **Q11 merge:** `write_permission_gate_check.py` เก็บทั้งสองฝั่งถูกต้อง (รายการ WATCHED + marker) และผ่าน · `check_all.sh` ทำงานได้ถูก (ทั้งสองบล็อกรัน) แต่มีหัวข้อ `# ---------- 1b.` **ซ้ำสองครั้ง** และควรย้าย `settings_reader_check` เข้าลิสต์ของบล็อก 1a ที่ทีม C3 ทำไว้แทนการเปิดบล็อกใหม่ (เรื่องความเรียบร้อย ไม่ใช่บั๊ก)

## 3.1 `tools/settings_reader_check.py` (Q10)

- รันกับเรพจริง: ผ่าน · `--self-test` ผ่าน · มี ℹ️ ว่า `LodgingProperty.AutoConfirmOnDeposit` มีผู้อ่านแล้ว (ต่อสายโดยทีม L2/S-06 lodging) ⇒ **ต้องตัดแถวนี้ออกจาก baseline** ตามกติกา ratchet ของตัวเอง (ตอนนี้ baseline เหลือ 82 แถวที่ยังไม่มีผู้อ่านจริง)
- baseline ที่สุ่มตรวจด้วย grep (e-Tax by Email ×2 · `PreventPostToClosedPeriod` · `SiteCommerceConfig.CheckoutMode` · `AiSamplingRate` · `TaxRuleConfig` ×2 · `LodgingProperty.AccountingModeAckBy` · `SiteSettings.HeroSubtitle` ซึ่งมีใน `LandingPageResponse` แต่ `index.html` ไม่อ่าน) — **ยังไม่มีผู้อ่านจริงทุกตัว** · baseline สมเหตุสมผล · หมายเหตุ: `PreventPostToClosedPeriod` ไม่มีผู้อ่าน ⇒ สวิตช์ "ห้ามลงรายการในงวดที่ปิด" ใช้การไม่ได้ (ด่านงวดปิดทำงานตลอด ไม่ขึ้นกับสวิตช์) · ช่องนี้ไม่อยู่บนหน้าจอ จึงไม่ใช่ silent no-op ของผู้ใช้
- **ฟ้องผิด 3 แบบ** (ทดลองในสำเนาใน scratchpad · เรพจริงวันนี้ยังไม่โดน):
  1. อ่านผ่าน object initializer ของ input ที่เอาไปคำนวณ: `new PricingInput { WeekendMask = s.WeekendMask }` ⇒ ถูกตัดทิ้งด้วยกฎ "คัดลอกผ่าน `P = y.P`" **ก่อน**ขั้น `is_echo` ⇒ ฟ้องว่าไม่มีผู้อ่าน (ทีมแก้ไว้แค่แบบ target-typed `new(…)`)
  2. อ่านใน raw SQL (`"SELECT \"LockRaw\" …"`) ⇒ มองไม่เห็น
  3. ตัวแปรชนิด entity ที่ชื่อ `model`/`input`/`update`/`body` ถูกตีเป็น "ตัวแปรคำขอ" ⇒ `model.ReadViaModel` ไม่นับ
  - ทั้งสามแบบฟ้องในทิศ "ล้มดัง" (dev มองเห็น) แต่ตามหลัก F2 ข้อ 6 checker ที่ฟ้องผิดถือว่าพัง ⇒ อย่างน้อยข้อ 1 ควรให้กฎ copy-through นับเฉพาะเมื่อฝั่งซ้ายเป็น echo/entity
- **พลาดของจริง** (ทีมยอมรับไว้แล้วใน docstring): ถ้ามี property ชื่อเดียวกันใน entity อื่นที่ยังถูกอ่าน (`DocumentLanguage` มีทั้งบน `Document` และ `CompanySettings`) ต่อให้ถอดผู้อ่านตัวสุดท้ายของ `CompanySettings.DocumentLanguage` ก็จะไม่ฟ้อง · เรื่องนี้รับได้ในฐานะ ratchet

---

## 4. เทสต์ที่ยังเขียวแม้ถอดการแก้ออก

ทั้ง 7 ไฟล์เรียกแต่ pure helper **ไม่มีเทสต์ไหนตรวจการต่อสาย** ⇒ ลบบรรทัดที่เรียกด่านออกแล้วเทสต์ยังเขียวทั้งหมด:

| ถ้าถอด | เทสต์ที่ยังเขียว |
|---|---|
| `OwnerActionGuard.EnsureNotApiKey` ใน `CompanyService.EnsureOwnerAccessAsync` / `RolePermissionService` | `OwnerActionGuardTests` ทั้งไฟล์ |
| `[RequirePermission]` และด่านคีย์ใน `SettingsController.UpdateSettings` | ไม่มีเทสต์ (มีแค่ `write_permission_gate_check` ที่กันการถอด attribute) |
| บล็อก `ApiAccessPolicy` ใน `ApiKeyMiddleware` | `ApiAccessPolicyTests` ทั้งไฟล์ |
| `EvaluateRegistrationAsync` ใน `AuthService` และด่านใน `CompanyService.CreateAsync` | `RegistrationPolicyTests` ทั้งไฟล์ |
| `integration.IsLegacyKey = false` ใน `RegenerateApiKeyAsync` · `AddChainedAuditLog` → `AuditLogs.Add` | `IntegrationKeyLegacyHygieneTests` ทั้งไฟล์ |
| การเรียก `ResolveDocumentHeadingAsync` ใน `DocumentEmailService`/`LineDelivery`/`EmailSchedule` (กลับไปคำนวณ heading เอง) | `DocumentChannelHeadingTests` ทั้งไฟล์ (เทสต์ส่ง heading เข้า `ComposeDefaultTemplate` เอง) |
| `TextOrNull`/`MultilineOrNull` ใน `UpdateSettingsAsync` · `NumberSeriesFieldPolicy.ThrowIfAny` ใน service | `SettingsSilentNoOpTests` ทั้งไฟล์ |

ข้อเสนอ: อย่างน้อยเพิ่มเทสต์ใน service แบบ InMemory/SQLite หนึ่งตัวต่อเส้น (`EnsureOwnerAccessAsync` เมื่อมี `HttpContext` ที่ตั้ง `IsApiKeyAuth` ต้องโยน 403 · `CreateAsync` เมื่อ `RegistrationEnabled=false` ต้องโยน · `UpdateSettingsAsync("")` ต้องได้ null · `UpdateNumberSeriesAsync(CurrentNumber=999)` ต้องโยนและไม่แตะ entity) และเทสต์ middleware หนึ่งตัวสำหรับ S-04

---

## 5. คำตอบรายข้อ (ย่อ)

1. **C1:** ครบ 17 จุด + ทุกเส้นให้สิทธิ์ ✅ · inline ที่เหลือ: `AccountSubscriptionController` ×4 (GET รายการบริษัทที่เป็นเจ้าของ · start-trial · attach/detach ข้ามบริษัท — W-C3) · `WithholdingTaxCertController:89` (ลบ 50 ทวิถาวร — W-C1) · `DocumentController:926` (purge ข้ามช่วงเก็บรักษา — **W-C1 P0**) · `CompanyController:145` (ดูสิทธิ์สมาชิก อ่านอย่างเดียว — ไม่เป็นไร) · `PayrollService:183` (Owner อนุมัติ HR ข้าม manager — ความเสี่ยงต่ำ) · `int_` ปิด ✅ · ทางอื่นที่คีย์ใช้เปลี่ยนสิทธิ์ตัวเองไม่มี ✅ · แต่ยังเปลี่ยนนโยบายได้ (W-C2)
2. **ทิศตรงข้าม:** เจ้าของที่ใช้ JWT ทำได้ครบ ✅ · ไม่มีบทบาทไหนได้ `CompanySettings.Edit` เป็นค่าเริ่มต้น ⇒ มีแต่เจ้าของที่บันทึกหน้าตั้งค่าได้ (PLAUSIBLE 5) · ข้อความ 403 เป็นภาษาไทยแต่ไม่บอกทางไปต่อ (W-C7)
3. **S-04:** +1 query/คำขอ ไม่มี N+1 · 403 ก่อนถึง controller ✅ · คีย์รุ่นเก่า (`int_`) ไม่โดนสวิตช์นี้ — จงใจ
4. **S-08:** ครอบทุกทาง ✅ · คำเชิญยังใช้ได้ ✅ · แอดมินเปิดบริษัทได้แต่ต้องเป็นเจ้าของเอง (PLAUSIBLE 4)
5. **S-12:** ฝั่ง server ตรงกับ PDF ทุกเคสที่ถาม ✅ · static refactor ไม่ทำให้ผู้เรียกเดิมพัง ✅ · HtmlEncode ครบและไม่หนีซ้ำ ✅ · **แต่หน้าเว็บข้ามทั้งหมด (W-C4)**
6. **S-13:** ไม่มีช่องไหนที่ล้างแล้วพัง ✅ · ช่องลับ "ว่าง = คงเดิม" ปลอดภัยแต่ล้างไม่ได้ (PLAUSIBLE 7)
7. **S-20:** หน้าเว็บเดิมยังบันทึกได้ ✅
8. **S-06:** "ไม่ส่ง = คงเดิม" จริง ยกเว้น `MaintenanceMessage` (W-C5) · ไม่มีช่องที่ถูกล็อกผิด ✅ · ยังมีช่องที่ไม่มีผลแต่ไม่ถูกล็อก (W-C6)
9. **P2/P3/P5:** ใช้ canonical ตัวเดียวกับฝั่ง verify ✅ แต่เรียกหลายครั้งใน SaveChanges เดียวจะแตกกิ่ง และ round-trip ของ timestamp น่าจะทำให้ verify ไม่ผ่านทั้งระบบ (PLAUSIBLE 1–2) · P3 ✅ · `LegacyDaysRemaining` ✅
10. **checker:** ฟ้องผิด 3 แบบ (สังเคราะห์) · baseline สมเหตุสมผล · ต้องตัด `AutoConfirmOnDeposit` ออก
11. **merge:** ถูกต้องตามหน้าที่ · มีหัวข้อ 1b ซ้ำ (เรื่องความเรียบร้อย)
12. **คอมไพล์:** ไม่เจอความเสี่ยงจากการอ่าน · checker ที่เกี่ยวผ่านทั้งหมด · **ยังไม่ได้คอมไพล์ — ต้องรอ CI หรือให้ผู้ใช้ rebuild**
