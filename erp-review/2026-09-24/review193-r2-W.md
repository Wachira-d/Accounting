# รอบ 193: ฝ่ายค้านรอบสอง (security/compliance) ตรวจงานแก้ของทีม W (`c5df11c` · merge `0c6c050`)

> อ่านอย่างเดียว · เครื่องนี้ไม่มี .NET SDK จึงตรวจจากการอ่านซอร์สที่ HEAD `f9f1556` + รัน checker ของเรพทีละตัว + ทดลองกลายพันธุ์ `owner_action_wiring_check` ในสำเนาที่ scratchpad (ไม่ได้แตะเรพ)
> ไม่ได้รัน `check_all.sh` ตามที่สั่งไว้ · **ยังไม่ได้คอมไพล์ ต้องรอ CI หรือให้ผู้ใช้ rebuild**

## 0. สรุปสั้น

- **W-C1 ถึง W-C7 ปิดตามที่รายงานไว้ทุกข้อ** (W-C4 ปิดแบบมีข้อสังเกต P3 เรื่องสิ่งที่หน้าจอแสดง) · `[RejectApiKey]` 18 action อยู่ครบตามรายการ · เจ้าของที่ใช้ JWT ยังทำได้เหมือนเดิม · ไม่เจอหลักฐานว่าคีย์ของ TakeTime เรียก endpoint ทั้ง 18 นี้
- **hash chain v2 แก้ต้นเหตุเรื่องเวลาได้ถูก** (ตัวเขียนและตัวตรวจเรียกฟังก์ชันชุดเดียวกัน · เวลาที่ hash คือเวลาที่เก็บ · ไม่พึ่งการปัดของฐานข้อมูล) **แต่ control ตัวนี้ยังไม่พร้อมใช้งานจริง.** chain ยังแตกกิ่งได้เมื่อคำขอพร้อมกัน **แม้มีเครื่องเดียว** ซึ่งไม่ได้เกิดเฉพาะกรณีหลายเครื่องอย่างที่ทีมบันทึกไว้. เมื่อแตกแล้ว งานตรวจรายสัปดาห์จะส่งแจ้งเตือนถึงลูกค้าว่า "มีคนแก้/แทรก/ลบจาก raw SQL" ทุกสัปดาห์ไปตลอด และแถวที่ถูกแก้จริงทีหลังจะถูกบังด้วยแถวแรกที่แตก (W2-C1). นอกจากนี้ **เทสต์ "round-trip" เป็นการจำลอง ไม่ได้ผ่านฐานข้อมูลจริง.** มันไม่ได้แตะ `ApplyAuditHashChain` ลำดับ `Id` หรือการทำงานพร้อมกันเลย และ checker ก็ไม่ได้ล็อกการต่อสายเส้นนี้ไว้ (W2-C2)
- **ของเดิมที่ไม่ใช่ความผิดของทีม W แต่อยู่ในคลาสเดียวกับที่ทีม W เพิ่งปิด (โจทย์ข้อ 5):** `PaymentSettingsController` (คีย์ลับของ gateway และสลับเป็นโหมด live) · `SensitivityController.SetRule` (doc เขียนว่า "Owner only" แต่ไม่มีด่านอะไรเลย) · `ApprovalController` rules ทั้งสามตัว **ไม่มีด่านสิทธิ์เลยแม้แต่กับ JWT** ⇒ สมาชิกบทบาท "ดูอย่างเดียว" ทำได้ (W2-C3, W2-C4, W2-C5)
- `owner_action_wiring_check` จับการถอด/ย้าย/ใส่คอมเมนต์/เปลี่ยนชื่อได้ครบ **แต่ไม่จับกรณี "ยังเรียกอยู่แต่ไม่ได้ใช้ผล"** (เช่น `_ = await RequireOwnerAsync(...)` หรือ `if (false && …)` หรือ attribute ที่เรียก `DenyResult` แต่ไม่ได้ตั้ง `context.Result`). นอกจากนี้ **ฟ้องผิด 3 รูปแบบ** กับด่านที่ถูกต้อง (§4)
- ด้านคอมไพล์ (จากการอ่าน): ไม่เจอความเสี่ยง · checker ที่เกี่ยวข้อง 15 ตัวผ่านทั้งหมด

---

## 1. ตารางปิด/ไม่ปิด (รอบแรก W-C1..W-C7 + PLAUSIBLE)

| ID | ผล | หลักฐาน (file:line ที่ HEAD) | หมายเหตุ |
|---|---|---|---|
| **W-C1** purge ข้ามช่วงเก็บ · ลบ 50 ทวิ | ✅ ปิด | `DocumentController.cs:932` · `WithholdingTaxCertController.cs:80` · `PdpaController.cs:206` (DSR erase ยังต้องผ่าน `RequireDpoAsync` เหมือนเดิม) | attribute ปฏิเสธคีย์ทุกกรณีของ purge ด้วย รวม `force=false` ซึ่งกว้างกว่า W-C1 แต่ไม่มีหลักฐานว่า integration ใช้เส้นนี้ (TakeTime ใช้ `/api/integration/*` และใช้ `cleanup/orphaned-settlement-receipts/purge` ที่ยังอนุญาตคีย์แบบเจาะจงอยู่) |
| **W-C2** PUT settings และค่าตั้งเชิงนโยบาย | ✅ ปิด (เฉพาะ endpoint ที่ระบุ) | `SettingsController.cs:68,81,95,121,140,158,167` · `EmailConfigController.cs:39-40,81` · `LineConfigController.cs:44-45,65` · `EtaxController.cs:307` · `PayrollController.cs:916,956` | ยังมีเส้นนโยบายอื่นที่คีย์ถือตัวตนเจ้าของทำได้ ดู §2 W2-C3..C5 และ §3 ข้อ 5 |
| **W-C3** subscription | ✅ ปิด | `SubscriptionController.cs:68-80` (`RequireOwnerAsync`: คีย์ได้ 403 ก่อน · SystemAdmin ผ่าน · สมาชิกที่ไม่ใช่ Owner ได้ 403 พร้อมข้อความไทย) · `:134,158,170,182` · `TrialExtensionPolicy.cs` · `SubscriptionService.cs:301-302` · `AdminController.cs:1293` (`allowCustomDays:true`) · `AccountSubscriptionController.cs:123,178,219` | ผู้เรียก `ExtendTrialAsync` มี 2 จุดพอดี (callers.py) · ไม่มี fake/mock ของ `ISubscriptionService` ในเทสต์ ⇒ ไม่มีความเสี่ยง CS0535 · ยังเหลือ `IncrementUsage` ที่สมาชิกทุกบทบาทเรียกได้ (§3 ข้อ 4) |
| **W-C4** หน้าเว็บประกอบหัวอีเมลเอง | ✅ ปิด (มีข้อสังเกต P3) | `DocumentController.cs:104-117` (เช็ก tenant ผ่าน `GetDocumentTypeAsync` + `DenyDocAsync(Create)` แบบเดียวกับ send-email) · `DocumentEmailService.cs:331-339` (ค่าที่ผู้ใช้คุมได้ถูกหนีใน `ComposeDefaultTemplate:353-360`) · `documents.html:6870-6881, 10320-10366` | ค่าที่ผู้ใช้แก้ถูกส่งจริง · ค่าที่ยังตรงกับที่ server เติมจะส่งเป็น null ⇒ server ประกอบให้ตรงกับ PDF · ดู W2-P5 (สิ่งที่แสดงไม่ตรงกับสิ่งที่ส่ง) และ W2-P6 (ไม่ตรวจชั้นความลับ) |
| **W-C5** MaintenanceMessage | ✅ ปิด | `AdminController.cs:1828` | |
| **W-C6** ช่องที่ไม่มีผล | ✅ ปิด | `PayrollController.cs:863-866,944-946` · `payroll.html:901-917,943` · `settings.html:691,1857` · `EtaxController.cs:324` (`HasValue`) | entity มีค่าเริ่มต้นตามกฎหมาย (`Payroll.cs:570-579`) ⇒ แถวใหม่ที่ไม่ส่งค่าจะไม่เป็น 0 · ผู้อ่าน `Section42TwiCap` (`PayrollService.cs:2184`) อ่าน `decimal` ที่ไม่ nullable จึงไม่มีความเสี่ยง NullReference (โจทย์ข้อ 6 ✅) |
| **W-C7** ข้อความ 403 | ✅ ปิด | `PermissionKeys.cs:297-304` · `RequirePermissionAttribute.cs:60-64` · `SettingsController.cs:45-57` · `settings.html:2179-2197` | ข้อสังเกต P3: ข้อความ "ขอให้เจ้าของเปิดสิทธิ์ให้บทบาทของคุณ" ขึ้นกับคำขอจากคีย์ `int_` ที่ไม่มีผู้ใช้ผูกไว้ด้วย ซึ่งทำตามไม่ได้ |
| **P-1** hash chain round-trip | 🟡 บางส่วน | `AuditHashChain.cs:69-125` · `AccountingDbContext.cs:3534-3566` · `AuditTrailService.cs:104-116` · `AuditTrailController.cs:57-95` | เรื่องเวลาปิดแล้ว (§5.1) · **แต่เทสต์เป็นการจำลอง และยังแตกกิ่งเมื่อคำขอพร้อมกัน** ⇒ W2-C1, W2-C2 |
| **P-2** สองแถวก่อน SaveChanges | ✅ ปิดในกรณี context เดียว · ❌ **ไม่ปิดเมื่อคำขอพร้อมกัน** | `AuditHashChain.cs:146-152` · `AccountingDbContext.cs:3551-3556` | ตรวจตามโค้ดแล้ว: `AddChainedAuditLog` สองครั้ง ⇒ ครั้งที่สองได้แถวแรกเป็นปลาย chain ✅ · แต่ถ้ามีสอง `DbContext` (สองคำขอ) ⇒ แตกกิ่ง (W2-C1) · ลำดับ `ChangeTracker.Entries` ขึ้นกับรายละเอียดภายในของ EF (W2-P2) |
| P-3 N+1 อีเมลตั้งเวลา | ❌ ไม่ได้แตะ | `EmailScheduleService.cs:533` | ทีมไม่ได้นับข้อนี้ใน "PLAUSIBLE 1–5" ของตัวเอง (ทีมใช้เลขลำดับคนละชุด) |
| P-4 แอดมินกลายเป็น Owner ของบริษัทลูกค้า | ❌ ไม่ได้แตะ | `CompanyService.cs:99-103` | รอเจ้าของตัดสิน |
| P-5 ไม่มีบทบาทไหนได้ `CompanySettings.Edit` โดยปริยาย | 🟡 ส่งให้เจ้าของตัดสินแล้ว (§8.4 ข้อ 1) | | ระหว่างรอ หน้าจอบอกทางไปต่อแล้ว (W-C7) · **เพิ่มเติม:** ตอนนี้ `email-config` / `line-config` ต้องใช้คีย์สิทธิ์นี้ด้วย ⇒ กรณี "สำนักงานบัญชีดูแลให้" ติดเพิ่มอีกสองหน้า |
| (§3.1) `settings_reader_check` ฟ้องผิด 3 แบบ | ✅ ปิด | รันแล้ว: ผ่าน · `--self-test` ผ่าน · baseline 82 แถว | |
| (§3) หัวข้อ 1b ซ้ำใน `check_all.sh` | ✅ ปิด | `check_all.sh:50-52` | |

---

## 2. CONFIRMED ใหม่

### W2-C1 (P1 · control compliance) chain แตกกิ่งเมื่อคำขอพร้อมกันแม้มีเครื่องเดียว ⇒ แจ้งลูกค้าว่า "ถูก tamper" ถาวร และบังการ tamper จริงที่เกิดทีหลัง
- `AccountingDbContext.cs:3544-3556`: อ่านปลาย chain ด้วย `SELECT … ORDER BY Id DESC LIMIT 1` **นอก transaction และไม่มีล็อก** ใน `SaveChangesAsync` แถว audit ถูกเขียนใน `base.SaveChangesAsync` รอบที่สอง (`:3510-3512`)
- ผลคือสองคำขอของบริษัทเดียวกันที่ SaveChanges ใกล้กัน (หรือคำขอหนึ่งอยู่ใน transaction ของ service ซึ่งมีการใช้ `BeginTransaction` 98 จุด ทำให้แถว audit มองไม่เห็นจนกว่าจะ commit ⇒ ช่วงที่ชนกันยาวเท่ากับทั้ง transaction) จะได้ `PrevHash` เดียวกันทั้งคู่. **ไม่ต้องมีหลายเครื่องก็เกิดได้** เพราะงานกลางคืน · POS หลายเครื่อง · integration · ผู้ใช้ ทำงานพร้อมกันได้อยู่แล้ว. ทีมบันทึกเรื่องนี้ไว้ใน §8.4 ข้อ 3 แต่บอกว่าเป็นกรณี "หลายเครื่อง" ซึ่งประเมินต่ำไป
- เมื่อแตกแล้ว `FirstBrokenIndex` (`AuditHashChain.cs:136`) คืนแถวนั้นทุกครั้ง ⇒ `AuditChainVerifyJob.cs:92-96` ส่งแจ้งเตือนถึงบริษัททุก 7 วันว่า **"มีคนแก้/แทรก/ลบจาก raw SQL หลัง insert"**. ข้อความนี้ระบุสาเหตุโดยไม่ได้ตรวจสาเหตุนั้น (F2 ข้อ 7) และ**งานตรวจรายงานเฉพาะแถวแรกที่พัง** ⇒ การแก้จริงที่เกิดทีหลังถูกบังไว้. append-only จึงซ่อมไม่ได้
- ตัวตรวจแยก "แตกกิ่ง" ออกจาก "ถูกแก้" ได้โดยไม่ลดความเข้ม: ถ้า `PrevHash` ของแถวตรงกับ `RowHash` ของ**แถวก่อนหน้าแถวใดก็ได้** และ `VerifyRow` ของแถวนั้นผ่าน ให้นับเป็น fork. ถ้าแถวก่อนหน้าถูกลบ `PrevHash` จะไม่ตรงกับแถวไหนเลย จึงยังจับได้
- เรื่อง advisory lock ที่ทีมถามมา: `pg_advisory_xact_lock` ต่อบริษัทใช้ได้ก็ต่อเมื่อถือล็อกตั้งแต่อ่านปลาย chain จนถึง **commit ของ transaction ชั้นนอก**. ⇒ การเขียนทุกครั้งของบริษัทจะเรียงคิวกันทั้ง transaction และเกิด deadlock ได้ (A ถือ row lock แล้วรอล็อก audit ขณะที่ B ถือล็อก audit แล้วรอ row lock เดียวกัน). ทางที่เสี่ยงน้อยกว่าคือ **แยกขั้นประทับ hash ออกไป**: เขียนแถวโดยยังไม่มี hash ⇒ ให้งาน sealer ตัวเดียว (`JobLock` + watermark ต่อบริษัท ตามกฎ #4 D) ประทับตามลำดับ `ChainSeq` ของตัวเองหลัง commit. ฝั่งตรวจเรียงตาม `ChainSeq` ไม่ใช่ `Id` (ตอนมีคำขอพร้อมกัน ลำดับ `Id` ไม่เท่ากับลำดับ commit)

### W2-C2 (P1 · กฎ #4 G) "round-trip test" เป็นการจำลอง และเส้นเขียนจริงไม่มีอะไรล็อกไว้
- `AuditHashChainTests.cs` `PgRoundTrip` จำลองแค่สองอย่าง คือ (ก) ตัดเหลือ 10 ticks และ (ข) `Kind=Unspecified`. เทสต์ทุกตัวเรียก `Seal`/`FirstBrokenIndex` ตรง ๆ **ไม่มีตัวไหนผ่าน `AccountingDbContext.ApplyAuditHashChain`, Npgsql หรือลำดับ `Id` ที่ฐานข้อมูลออกให้**
- ถ้าพรุ่งนี้มีคนแก้ `ApplyAuditHashChain` กลับไปใช้ `ComputeRowHash` (v1 ยังเป็น `internal` จึงเรียกได้ทั้ง assembly) หรือถอด `ResolveTip` ออก **เทสต์ทั้ง 13 ตัวยังเขียว** และ `owner_action_wiring_check` ก็ไม่มีแถวของเส้นนี้ (RULES มี 40 แถว ไม่มี `AccountingDbContext` / `AuditTrailService` / `AuditTrailController`)
- ขั้นต่ำที่ควรมี: เพิ่มแถวใน checker ⇒ `AccountingDbContext.cs` `ApplyAuditHashChain` ต้องมี `AuditHashChain.Seal(` และ `ResolveTip(` · `AuditTrailService.VerifyHashChainAsync` ต้องมี `FirstBrokenIndex(` · `AuditTrailController.VerifyHashChain` ต้องมี `VerifyRow(`. ส่วนเทสต์ที่ถือว่าเป็น "control ที่มีจริง" ต้องเป็นเทสต์ที่ write→SaveChanges→อ่านกลับ→verify ผ่าน Npgsql จริง (Testcontainers หรือ job CI ที่มี PostgreSQL) อย่างน้อย 3 กรณี: แถวเดียว · หลายแถวใน SaveChanges เดียว (ตรวจว่าลำดับ `Id` เท่ากับลำดับที่ประทับ) · `AddChainedAuditLog` สองครั้ง + ChangeTracker ใน SaveChanges เดียว

### W2-C3 (P1 · ของเดิม · คลาสเดียวกับ email/line-config) `PaymentSettingsController` ไม่มีด่านสิทธิ์และไม่ปฏิเสธคีย์
- `PaymentSettingsController.cs:120-165` (`PUT` บันทึก `Test/LiveSecretKey`, `ClearingAccountId`, `FeeExpenseAccountId`, `IsActive`) · `:167-187` (test) · `:194-240` (สลับเป็น **live**) มีแค่ `[Authorize]` ระดับคลาส ⇒ **สมาชิกทุกบทบาท รวมถึงคีย์ `acc_` ที่มีสิทธิ์เขียน แทนคีย์ลับของ gateway ด้วยบัญชีร้านค้าของตัวเองได้** ⇒ เงินที่ลูกค้าปลายทางจ่ายจะไปเข้าบัญชีอื่น (ด่าน "ต้องทดสอบผ่านก่อนเปิด live" ไม่ได้กันอะไร เพราะผู้โจมตีตั้ง webhook ของบัญชีตัวเองได้)
- บันทึกการเปิด live ใช้ `_db.AuditLogs.Add` ตรง (`:216`) ⇒ `RowHash=null` คือแถวอยู่นอก chain
- `write_permission_gate_check.py` ไม่ได้เฝ้าไฟล์นี้ (ลองรันด้วย path ของไฟล์นี้แล้วฟ้อง 3 จุด)

### W2-C4 (P1 · ของเดิม) `SensitivityController.SetRule` ใครก็เปิดสิทธิ์ดูเอกสารลับ/เงินเดือนให้บทบาทตัวเองได้
- `SensitivityController.cs:24-33` doc เขียนว่า "Owner only" แต่ไม่มีด่านอะไรเลย · `SensitivityService.cs:64-87` กันไว้อย่างเดียวคือ "ปิดสิทธิ์ของ Owner ไม่ได้" ⇒ บทบาท Viewer/Staff ส่ง `{Kind: Payroll, Role: Viewer, CanView: true}` แล้วจะเห็นเอกสาร `SensitivityKind.Payroll/ExecutivePay/HrPersonal/Confidential` (`DocumentService.cs:1865-1870`) · ถือเป็น privilege escalation และขัด PDPA ม.37 (RBAC)

### W2-C5 (P1 · ของเดิม) กฎการอนุมัติ `ApprovalController` rules ไม่มีด่านเลย
- `ApprovalController.cs:31-52` POST/PUT/DELETE rules ไม่มีด่านใดเลย ⇒ สมาชิกทุกบทบาท (และคีย์) ลบหรือแก้ขั้นอนุมัติ (`ApproverUserId`, `MinAmount`) ได้. เรื่องนี้เป็นด่านควบคุมภายในแบบเดียวกับ `RequireApprovalForDocuments`/`SodBlockSelfApproval` ที่ W-C2 ย้ายไปอยู่หลัง `CompanySettings.Edit` + `[RejectApiKey]` แล้ว ⇒ นโยบายเดียวกันแต่มีสองเส้น และมีเส้นหนึ่งที่ไม่มีประตูเลย (R5)

### W2-C6 (P2) `sso-config` ขาด `[RejectApiKey]` ขณะที่ `tax-rule-config` ซึ่งเป็นตารางกฎหมายประเภทเดียวกันมีแล้ว
- `PayrollController.cs:762` (PUT) · `:822` (DELETE) มีแค่ `RequirePayrollWriteAsync(PayrollApprove)` ⇒ คีย์ที่ถือตัวตนเจ้าของเปลี่ยนเพดาน/อัตราประกันสังคมได้ ขณะที่ `:916/:956` (อัตราภาษี) ปฏิเสธคีย์แล้ว

### W2-C7 (P3) หน้าต่างส่งอีเมลแสดงหัว/เนื้อที่ไม่ตรงกับสิ่งที่จะถูกส่ง
- `documents.html:10325` เรียก `_loadSendEmailTemplate(id)` ซึ่งอ่าน `#seEtax.checked` ทันทีที่เริ่ม (`:10340`) **ก่อน**บรรทัด `:10329` จะตั้งค่า checkbox ⇒ ใบที่เปิดหน้าต่างมาเป็น e-Tax จะแสดงหัวที่ไม่มี "[e-Tax]". การส่งจริงไม่ผิด เพราะค่าที่ยังตรงกับค่าอัตโนมัติจะถูกส่งเป็น null แล้ว server ประกอบให้ถูก
- ถ้าสลับ checkbox เร็ว ๆ response ที่มาถึงทีหลังจะชนะ ("ใครมาก่อนชนะ" กฎ #4 A) ⇒ สิ่งที่แสดงไม่ตรงกับ checkbox · อีกข้อคือ `#seBody` เป็น textarea ที่แสดง **HTML ดิบ** (`tpl.htmlBody`) ซึ่งเดิมเป็นข้อความธรรมดา ⇒ ผู้ใช้ที่แก้คำสองคำต้องระวังไม่ให้ tag พัง

---

## 3. PLAUSIBLE (ต้องมี DB/runtime หรือการตัดสินใจยืนยัน)

1. **W2-P1 แถว v1 จะตรวจผ่านแบบ legacy ได้ก็ต่อเมื่อ Npgsql 8.0.11 "ตัด" ไม่ใช่ "ปัด" ticks** ตอนเขียน `timestamp`. เท่าที่รู้ `PgTimestamp.Encode` หารจำนวนเต็มด้วย 10 จึงเป็นการตัด ⇒ `VerifyLegacyRow` ที่ลอง base+0..9 (`AuditHashChain.cs:121-123`) น่าจะครอบได้. **แต่ยังไม่มีการยืนยันกับฐานข้อมูลจริง** ถ้าเป็นการปัด แถว v1 ประมาณครึ่งหนึ่งจะตรวจไม่ผ่าน. ส่วนแถว v1 ที่เคยแตกกิ่งจากคำขอพร้อมกันในอดีตจะยังตรวจไม่ผ่านต่อไป ⇒ หลัง deploy งานตรวจจะยังแจ้งเตือน "raw SQL" ถึงบริษัทเหล่านั้นต่อ และไม่มีช่องทางบอกผู้ใช้ว่า "ประวัติก่อนรอบ 193 ตรวจได้แค่ระดับนี้" หรือว่าการแจ้งเตือนรายสัปดาห์ที่ผ่านมาทั้งหมด (v1 ไม่ผ่านที่แถวแรกของทุกบริษัท) เป็นการแจ้งเตือนหลอก. โค้ดไม่ได้ประทับว่า "ผ่าน" เองซึ่งถูกแล้ว แต่ข้อความที่ส่งออกไปอ้างสาเหตุผิด. ข้อเสนอ: บันทึก checkpoint (Id ที่ตรวจผ่านถึง) ตอน deploy + ข้อความแยก "ประวัติ v1 ตรวจไม่ได้" ออกจาก "ถูกแก้"
2. **W2-P2 ลำดับ `Id` ในหนึ่ง SaveChanges กับลำดับที่ประทับ** · `ApplyAuditHashChain` ประทับตามลำดับใน list แล้ว `AddRange` ส่วนฝั่งตรวจเรียงตาม `Id`. ถ้า EF Core 8 ไม่ได้ INSERT แถว `Added` ของตารางเดียวกันตามลำดับที่ Add chain จะพังตั้งแต่ SaveChanges แรกที่มีหลายแถว (ซึ่งเกิดบ่อย: บันทึกเอกสาร 1 ครั้งได้หลายแถว). ตอน v1 ไม่มีใครเห็นเรื่องนี้เพราะ v1 ไม่ผ่านที่แถวแรกอยู่แล้ว. `ResolveTip` ก็พึ่งลำดับของ `ChangeTracker.Entries` เช่นกัน ⇒ ต้องมีเทสต์ผ่านฐานข้อมูลจริง (W2-C2). ทางที่แข็งกว่า: ให้ `ResolveTip` เลือกแถวที่รอบันทึกซึ่ง**ไม่มีแถวรอบันทึกอื่นอ้าง `PrevHash` ถึง** แทนการเลือก "ตัวท้าย"
3. **W2-P3 chain ไม่มี anchor ภายนอก + ไม่กัน UPDATE** · trigger กันแค่ DELETE (`DatabaseMigrationHelper.cs:5054` "UPDATE เผื่อ backfill") และตอนนี้ v2 ประกาศเองว่า "ไม่เขียนทับ" ⇒ ไม่มีเหตุผลต้องเปิด UPDATE ไว้แล้ว. ใครก็ตามที่เขียน SQL ได้ แก้แถวแล้ว seal ใหม่ทั้งช่วงได้ (SHA-256 ไม่มีกุญแจ). ควรเพิ่ม trigger กัน UPDATE และพิจารณา HMAC/checkpoint นอกฐานข้อมูล. ข้อความในงานตรวจที่อ้างว่า "จับการแก้จาก raw SQL ได้" จึงเกินจริง
4. **W2-P4 `SubscriptionController` ยังเหลือเส้นเขียนที่ไม่มีด่านบทบาท** · `IncrementUsage` (`:372-378`) ที่สมาชิกทุกบทบาทเพิ่มตัวนับการใช้งานได้ ⇒ ถ้าตัวนับนี้ป้อน overage billing ก็คือการเพิ่มยอดบิลของบริษัท · `UpdateNotificationSettings` (`:222`) · `SubmitPayment` (`:237`) · ความเสี่ยงต่ำกว่า W-C3 แต่เป็นคลาสเดียวกัน
5. **W2-P5 เส้นนโยบายอื่นที่คีย์ถือตัวตนเจ้าของยังทำได้** (ครบแล้วสำหรับเส้นทำลายหลักฐาน แต่ยังไม่ครบสำหรับเส้น "เปลี่ยนนโยบาย"): `TaxController.cs:207` `unlock-filing` (ปลดล็อกงวดที่ยื่นแล้ว เป็นญาติของการเปิดงวดซึ่งปฏิเสธคีย์แล้ว) · `PayrollController.cs:629` `runs/{id}/reopen` · `NotificationConfigController.cs:34` · `DocumentTemplateController.cs:26-78` (ไม่มีด่านเลยแม้กับ JWT) · `EmailScheduleController.cs:28,52` (กฎส่งอีเมลหาลูกค้าอัตโนมัติ ไม่มีด่าน) · `AiController.cs:47-63` (กฎจับคู่ผังบัญชี ไม่มีด่าน) · `CmsCommerceController.cs:315-343` (credential ของ gateway ใน CMS · `RequireSiteRole(Admin)` ซึ่งเจ้าของน่าจะผ่าน). ข้อเสนอ: เพิ่มไฟล์เหล่านี้ใน `WATCHED` ของ `write_permission_gate_check.py` (ตอนนี้เฝ้า 15 ไฟล์ ยังไม่มี Approval/Sensitivity/PaymentSettings/EmailConfig/LineConfig/Subscription/Pdpa)
6. **W2-P6 `GET email-template` ไม่ตรวจชั้นความลับ** · ส่งชื่อลูกค้า + `TotalAmount` ของใบ `Sensitivity≠None` ให้ผู้ที่มีแค่ `Document.Create` (`DocumentController.cs:104-117` เทียบกับ `DocumentService.cs:1865-1870` ที่ส่งแค่ stub). เป็นคลาสเดียวกับ `send-email` เดิม (`:88-101`) ที่ส่ง PDF ทั้งใบของใบลับออกไปยังอีเมลไหนก็ได้ และ `DocumentTemplateController` generate-pdf ที่ไม่มีด่าน ⇒ ชั้นความลับบังคับใช้แค่ที่ JSON ของ GET document. เป็นของเดิม แต่ endpoint ใหม่เพิ่มทางอ่านอีกหนึ่งทาง
7. **W2-P7 `AuditTrailController` ทั้งคลาสมีแค่ `[Authorize]`** (`:12-13`) ⇒ สมาชิกทุกบทบาทและคีย์อ่าน `OldValues/NewValues` ของทุก entity ได้ (`CaptureAuditEntries` ไม่ได้ปิดบังค่าใดเลย `AuditTrailService.cs:163-175` ⇒ ค่าเงินเดือนที่ถูกแก้ก็อยู่ในนั้น) และเรียก `verify-hash-chain` ได้ถึง 1,000,000 แถว × SHA-256 สูงสุด 11 รอบต่อแถวสำหรับแถว v1 (doc เขียนว่า "Admin role only"). เป็นของเดิม ไม่ใช่ของทีม W
8. **W2-P8 purge ที่ไม่ใช้ `force` ก็ปฏิเสธคีย์ด้วย** · ถ้ามีคู่ค้าที่ใช้คีย์ `acc_` ลบ "ร่าง" ถาวรเพื่อ resync คู่ค้านั้นจะเริ่มได้ 403. ไม่พบหลักฐานในเรพ/เอกสาร (TakeTime ใช้ `/api/integration/*` และ cleanup แบบเจาะจง) ⇒ ถือว่ายอมรับได้ แต่ควรเขียนไว้ใน ACCOUNT_STRUCTURE ว่า "คีย์ลบถาวรไม่ได้ทุกกรณี"

---

## 4. `tools/owner_action_wiring_check.py`: ทดลองกลายพันธุ์ (สำเนาใน scratchpad · ไม่ได้แตะเรพ)

| # | การกลายพันธุ์ | ผล | ถูกไหม |
|---|---|---|---|
| M1/M1b | ถอด `[RejectApiKey]` (เหลือบรรทัดว่าง/ลบบรรทัด) | ฟ้อง | ✅ |
| M2 | ย้าย attribute จาก `PurgeDocument` ไป `GetEmailTemplate` | ฟ้อง | ✅ |
| M3/M3b | ใส่ attribute ไว้ใน `//` และ `/* */` | ฟ้อง | ✅ |
| M4 | เปลี่ยนชื่อ `PurgeDocument` เป็น `PurgeDocumentV2` | ฟ้อง ("ไม่พบเมธอด") | ✅ |
| M10 | ต่อ attribute บรรทัดเดียวกับ `[HttpDelete(...), RejectApiKey(...)]` | ไม่ฟ้อง | ✅ |
| **M7** | `_ = await RequireOwnerAsync(...)` (เรียกแต่ทิ้งผล) | **ไม่ฟ้อง** | ❌ พลาดของจริง |
| **M8** | `RejectApiKeyAttribute.OnAuthorization` เรียก `DenyResult` แต่ไม่ตั้ง `context.Result` | **ไม่ฟ้อง** | ❌ พลาดของจริง (attribute ทั้ง 18 จุดกลายเป็นด่านที่ไม่ทำอะไรในบรรทัดเดียว) |
| **M11** | `if (false && await RequireOwnerAsync(...) is { } deny)` | **ไม่ฟ้อง** | ❌ พลาด |
| **M6** | แทน attribute ด้วย `[System.Obsolete("RejectApiKey(ลบ)")]` | **ไม่ฟ้อง** (สตริงไม่ถูกตัดก่อน regex) | ❌ พลาด (เกิดยาก) |
| **M5** | `[Accounting.Filters.RejectApiKey]` ไม่มีวงเล็บ (C# ถูกต้อง · ctor มีค่า default) | **ฟ้อง** | ❌ ฟ้องผิด |
| **M9** | คอมเมนต์หนึ่งบรรทัดคั่นระหว่าง attribute กับเมธอด | **ฟ้อง** | ❌ ฟ้องผิด (`find_method` ยอมรับบรรทัดว่างเฉพาะหลังเจอ attribute แล้ว) |
| **M12** | ย้าย `[RejectApiKey]` ไประดับคลาส (ด่านกว้างกว่าเดิม) | **ฟ้อง** | ❌ ฟ้องผิด (จะมองเป็นความตั้งใจก็ได้ แต่ข้อความควรบอกว่าเจอที่ระดับคลาส) |

ข้อเสนอ: regex ของแถว `attr` เปลี่ยนเป็น `\bRejectApiKey\b(\s*\(|\s*\])` แล้วตัดสตริงออกก่อนเช็ก attribute · แถว `body` ของด่านที่คืนค่า ให้เช็กรูป `is { } \w+\) return` หรือ `context.Result =` แทนการเช็กแค่ชื่อที่ถูกเรียก · เพิ่ม RULES ของเส้น hash chain (W2-C2) · self-test ควรมีกรณี M7/M8

---

## 5. ตรวจแล้วไม่มีปัญหา

1. **hash chain: สูตรหลักตัวเดียว** · grep `PrevHash|RowHash` ทั้งเรพเจอแค่ `AuditHashChain.cs` (สูตร) · `AccountingDbContext.cs` (`Seal`) · `AuditTrailService.cs:113` (`FirstBrokenIndex`) · `AuditTrailController.cs:80-86` (`VerifyRow`) · `AuditChainVerifyJob` (เรียก service) · `PosService.Orders.cs:257` + `ApiKeyMiddleware.cs:335` (`AddChainedAuditLog`). **ไม่มีการประกอบ canonical string หรือ SHA256 ของ audit ที่อื่นแล้ว** ✅ · ข้อสังเกตย่อย: controller เดิน chain ด้วยลูปของตัวเอง (`:78-87`) แทนที่จะใช้ `FirstBrokenIndex` ⇒ ตรรกะเช็กลิงก์มีสองชุด (ไม่ใช่ปัญหา format แต่ต่างกันตรงที่ controller เดินต่อหลังเจอจุดแรก)
2. **normalize เวลา** · คอลัมน์ `Timestamp` เป็น `timestamp without time zone` (EF + legacy switch `Program.cs:50`, ตัวแปลง timestamptz ทิ้ง `Program.cs:1911-1929` ใช้ `AT TIME ZONE 'UTC'`) · `Seal` ตั้ง `row.Timestamp` เป็นค่าที่ตัดเหลือ µs แล้ว (`:97`) ⇒ เวลาที่เก็บคือเวลาที่ hash ไม่ว่า Npgsql จะตัดหรือปัด · อ่านกลับเป็น Unspecified ⇒ ตีเป็น UTC ถูก · ถ้าคอลัมน์ยังเป็น timestamptz (อ่านกลับเป็น Local) ⇒ `ToUniversalTime` ถูก · `RowHash` เป็น `text` (`DatabaseMigrationHelper.cs:5365`) จึงเก็บป้าย `v2:` + 64 hex ได้ · `NewValues/OldValues` เป็น text ไม่ใช่ jsonb (ถ้าเป็น jsonb PostgreSQL จะจัด JSON ใหม่จน hash เพี้ยน)
3. **ปลอมป้ายรุ่น** · `v2:` + hash v1 ไม่ผ่าน · canonical v2 ขึ้นต้น `v2|` จึงชนกับ v1 ไม่ได้ ✅
4. **`ResolveTip` ใน context เดียว** · ข้ามแถวที่ `RowHash` ว่าง (แถวที่ `AuditLogs.Add` ตรง) · กรองบริษัทเดียวกัน · กรณี `AddChainedAuditLog` แล้วตามด้วย SaveChanges ที่มีแถวจาก ChangeTracker: แถวแรกถูก save ในรอบแรก แล้วรอบที่สองอ่านปลาย chain เจอแถวนั้น (connection/transaction เดียวกันมองเห็น) ✅
5. **`[RejectApiKey]` เป็น `IAuthorizationFilter`** ⇒ ปฏิเสธก่อน model binding · ตัวบอกว่าเป็นคีย์ดูทั้ง `Items["IsApiKeyAuth"]` และ claim `AuthMethod` · JWT ไม่ได้สร้าง claim นี้ (`JwtHelper` ใส่แค่ Role) · ในเรพไม่มี auth scheme อื่นนอกจาก JWT + ApiKeyMiddleware ⇒ **เจ้าของที่ใช้ JWT ผ่านทุกเส้น** (ทิศตรงข้าม ✅) · `AdminController` เป็น `Roles="SystemAdmin"` ซึ่งคีย์ไม่มี Role claim ⇒ คีย์เข้าไม่ได้ ✅
6. **เส้นทำลายหลักฐานอื่น** · `ExecuteDelete` ใน controller มีแค่ `AdminController` (แพลตฟอร์ม) · `BulkCleanup` transactions/master-data ปฏิเสธคีย์ด้วย `IsOwnerAsync:36` · ลบไฟล์แนบของใบที่ไม่ใช่ร่างเป็น soft-delete (`FileAttachmentController.cs:163-173`) · ลบบริษัทผ่าน `EnsureOwnerAccessAsync` ✅
7. **`email-config`/`line-config`** · PUT มีทั้ง `RequirePermission` + `RejectApiKey` · test มี `RequirePermission` · `write_permission_gate_check` เมื่อรันด้วย path ของสองไฟล์นี้ = 0 จุด ✅ (แต่ไฟล์ยังไม่อยู่ใน `WATCHED`)
8. **`TrialExtensionPolicy`** · โยนก่อนแก้ entity (`ExtensionsUsed` ไม่ขยับเมื่อถูกปฏิเสธ) · `BusinessRuleException(string,string)` ไม่กำกวมกับ overload ที่รับ `Exception` · แอดมินผ่าน `allowCustomDays:true` ✅
9. **W-C4 ฝั่ง server** · tenant (`d.CompanyId == companyId` ทั้งใน controller และ service) · `AsNoTracking` · `KeyNotFoundException` ⇒ 404 (`ExceptionMiddleware.cs:44`) · หัวเรื่องเป็นข้อความล้วน ส่วนเนื้อ HTML หนีค่าที่ผู้ใช้คุมได้ครบ · หน้าเว็บตั้งค่าผ่าน `.value` เท่านั้น ✅
10. **คอมไพล์ (โจทย์ข้อ 8)** · `using_check` · `di_cycle_check` (193 service ไม่มีวงกลม) · `nullable_arg` · `record_arg` · `arg_type` · `undeclared_local` · `service_interface` · `accessibility` · `namespace_shadow` · `dto_nullable_contract` · `dead_helper` · `tuple_name_merge` · `string_quote_close` · `verbatim_string` · `identifier_space` ผ่านหมด · `SettingsController` มี `using Accounting.Filters/Helpers/Models.Constants` · `IPermissionService?` แบบ optional มีตัวอย่างเดิมในเรพ · `ComputeRowHash` ที่เปลี่ยนเป็น `internal` ถูกเทสต์เรียกผ่าน `InternalsVisibleTo` (`Accounting.csproj:82`) · `EmailTemplate` / `HydrateContactAsync` / `PermissionMeta.LabelTh` / `BusinessRuleException.RuleCode` มีจริง · `TaxRuleConfigRequest` / `CompanySettingsResponse` ไม่มีผู้เรียกแบบ positional ที่ต้องแก้ตาม
11. `settings_reader_check` + `--self-test` ผ่าน · `owner_action_wiring_check` + `--self-test` ผ่าน (40/40) · `write_permission_gate_check` (WATCHED เดิม) ผ่าน

---

## 6. ลำดับที่แนะนำ

1. W2-C1 + W2-C2: ก่อนจะให้ใครเชื่อ "verify ผ่าน" ต้องมีเทสต์ผ่าน PostgreSQL จริง และแยก fork ออกจาก tamper ในตัวตรวจ + ข้อความแจ้งเตือน · ตัดสินใจเรื่อง sealer กับ advisory lock (§2 W2-C1 ย่อหน้าสุดท้าย)
2. W2-C3 / W2-C4 / W2-C5: ใส่ด่านสิทธิ์ (+ `[RejectApiKey]` ที่ PaymentSettings) และเพิ่มสามไฟล์ใน `WATCHED`
3. W2-C6 + รายการใน W2-P5: ตัดสินทีละเส้นว่า "คีย์ทำได้ไหม" แล้วเพิ่มแถวใน `owner_action_wiring_check`
4. checker: ปิดพลาด M7/M8 และแก้ฟ้องผิด M5/M9

**ยังไม่ได้คอมไพล์ในเครื่องนี้ รบกวน rebuild หรือดูผล CI**
