# ฝ่ายค้านรอบ 193: security ทีม S (d334f1d) + U2 (f04c145)

> ตรวจแบบอ่านอย่างเดียว ไม่มีคอมไพเลอร์ ได้ข้อสรุปจากการอ่านซอร์สเท่านั้น · ลำดับ: CONFIRMED → PLAUSIBLE → ตรวจแล้วไม่มีปัญหา

## CONFIRMED

**C1: คีย์เก่า (legacy) ในช่วงผ่อนผัน หรือคีย์ใหม่ที่ผูกกับเจ้าของ ออกคีย์ `acc_` สิทธิ์เต็มแบบไม่หมดอายุได้ จึงหลุดทั้งวันเลิกใช้และขอบเขตสิทธิ์**
- `RequireOwnerAsync` (IntegrationController.cs:45) ปฏิเสธ API key ทุกดอก และคอมเมนต์ของมันเองระบุภัยนี้ไว้ แต่ปิดแค่ทาง `int_`
- `SettingsController.CreateApiKey` (:144) เรียก `EnsureOwnerAccessAsync` (CompanyService.cs:574) ซึ่งดูแค่ role และไม่เช็ก `IsApiKeyAuth`
- ขั้นการโจมตี:
  1. ส่ง `X-Acting-User: <อีเมลเจ้าของ>` คู่กับคีย์ legacy หรือคีย์ที่ผูกไว้ ระบบจะตั้ง NameIdentifier เป็นเจ้าของ
  2. เปิด `EnableApiAccess` ผ่าน `PUT settings` (:44) ซึ่ง**ไม่มีด่านเลย**
  3. `POST settings/api-keys` โดยใส่ `CanWrite/CanDelete=true` และ `ExpiresAt=null`
- ผลคือได้คีย์ที่ไม่มี `LegacyDeprecatesAt` และทำงานในนามเจ้าของตลอดไป
- คีย์ใหม่ที่มีแค่ `CanWrite` แต่ผูกกับเจ้าของ ก็ยกตัวเองขึ้นเป็น `CanDelete` ได้ด้วยวิธีเดียวกัน
- ทางเชิญสมาชิก (CompanyService.cs:336) เป็นช่องแบบเดียวกัน
- วิธีแก้: ปฏิเสธ `IsApiKeyAuth` ใน `EnsureOwnerAccessAsync` (หรือทำเป็นฟิลเตอร์กลางสำหรับ endpoint ระดับเจ้าของ) และใส่ด่าน `CompanySettings.Edit` ให้ `PUT settings`

**C2: ทางเข้าอื่นที่ไม่ผ่านด่าน (R5): `StatutoryRemittanceController.UploadReceipt` (:115)**
- endpoint นี้ไม่มี `[RequirePermission]` และไม่เช็กว่า remittance เป็นของบริษัทก่อนเขียนไฟล์
- ใช้ `UploadBytesAsync` ที่ตรวจแค่นามสกุลไฟล์ ไม่ตรวจไบต์จริง
- ตาราง U2 บอกว่า `StatutoryRemittance` เขียนได้ด้วย `Tax.File` แต่สมาชิกทุกคนแนบไฟล์ผ่านเส้นนี้ได้
- ข้อความ F3-8 ของ U2 ("ระบบแนบเองผ่าน service") จึงไม่ครอบเส้นนี้

**C3: OCR เปิดไฟล์ที่ด่านเอกสารใหม่ปิดไว้**
- `GET ocr` (:294), `GET ocr/{scanId}` และ `GET ocr/{scanId}/image` (:1353) มีแค่ `[Authorize]`
- หลังสร้างเอกสาร ไฟล์ถูกย้ายไปเป็น `EntityType="Document"` (OcrService.cs:8078) แต่ `scan.FileAttachmentId` ยังชี้ไฟล์เดิม
- ผลคือผู้ใช้ที่ `DenyDocAsync` ปฏิเสธ (ไม่เห็นฝั่งรายจ่าย หรือใบเป็นข้อมูลลับ) ยังเปิดรูปใบเดียวกันผ่าน OCR ได้
- กฎ `OcrScan` เองก็มี `ReadAnyOf` ว่าง

**C4: ข้อสังเกตของ U2 (§5 ข้อ 161/189) ว่า "static `/uploads/` ยังเปิด" ไม่จริง**
- Program.cs:1011–1044 เป็น allow-list ที่ตอบ 404 ให้ `/uploads/attachments/**`, `/uploads/{cid}/**` และ `/uploads/ocr`
- middleware นี้รันก่อน `UseStaticFiles` ทั้งสองตัว
- คำถามเจ้าของข้อ 1 จึงตกไป ควรจดลง §"ตรวจแล้วไม่ใช่บั๊ก"

## PLAUSIBLE

**P1: กฎ "เจ้าของใบเบิก" (FileAttachmentController ~:96) ไม่ดูสถานะใบ**
- ผู้ยื่นแนบหรือถอดใบเสร็จได้แม้ใบถูก Approved/Paid ไปแล้ว คือสลับหลักฐานหลังอนุมัติได้
- ไฟล์จริงยังถูกเก็บไว้ แต่หายจาก UI

**P2: แถว audit ของ legacy email-match ไม่เข้า hash chain**
- `db.AuditLogs.Add` ตรง ๆ ข้ามขั้นตอน hash เพราะ `ApplyAuditHashChain` ใส่ hash เฉพาะแถวที่ได้จาก `CaptureAuditEntries` (AccountingDbContext.cs:3503) ซึ่งข้าม `AuditLog`
- ผลคือ `RowHash=null` และแถวอยู่นอก chain ขณะที่คอมเมนต์ใน middleware บอกว่าเข้า chain
- นอกจากนี้ทุก request ของ TakeTime จะเกิด `SaveChanges` 1 ครั้งกับ `LogWarning` 1 ครั้ง (log spam)

**P3: regenerate คีย์ legacy แล้วคีย์ใหม่ยังเป็น legacy**
- secret ใหม่ได้สิทธิ์เต็มและสวมผู้ใช้ด้วยอีเมลได้ต่อ
- กรณีเจ้าของหมุนคีย์ที่พนักงานเก่าออกไว้ จะยังได้คีย์ที่มีอำนาจเต็มถึงวันเลิกใช้

**P4: ยังไม่มีทางทำความสะอาดคีย์ legacy ที่ไม่ใช่เจ้าของออก**
- คีย์ legacy รวมคีย์ที่ไม่ใช่เจ้าของออกไว้ก่อนรอบ 193 ซึ่งก็คือบั๊กต้นทาง
- คีย์เหล่านี้สวมเป็นเจ้าของได้อีก 90 วัน ตามคำตัดสินเจ้าของ
- แต่ถ้ารวมกับ C1 ผลจะกลายเป็นถาวร
- UI ไม่แสดงว่าใครออกคีย์ ทั้งที่ `CreatedBy` มีอยู่ใน BaseEntity

**P5: หน้าผาวันเลิกใช้**
- ประมาณ deploy+90 วัน การเขียนของ TakeTime จะได้ 403 และ header จะเป็น `X-Acting-User-Resolved: unmapped`
- ไม่มีงานแจ้งเตือนล่วงหน้า มีแค่ประกาศในหน้าเว็บ
- `LegacyDeprecatesAt` เป็น `timestamp` ที่ serialize ออกไปโดยไม่มี `Z` หน้าเว็บจึงแสดงเวลาเลื่อนไป 7 ชม.

**P6: เทสต์เป็นแบบ pure-helper ทั้งหมด ไม่มีตัวไหนแตะ middleware หรือ controller**
- ถอด `DenyAttachmentAsync` ออกจาก `Download`/`GetByEntity` (ด่านอ่าน) แล้วเทสต์ทั้งหมดยังผ่าน และ `write_permission_gate_check` ก็ไม่ดู GET
- ถอด `IAsyncActionFilter` ออกจากรายการ interface ของ `ExternalIntegrationController` แล้วฟิลเตอร์จะหยุดทำงานเงียบ ๆ แต่ checker ยังผ่าน
  - สาเหตุ: marker เป็นแค่การมีสตริงอยู่ในไฟล์ ("มี ≠ ถูกเรียก")
- ถอด `EffectiveScopes` ออกจาก middleware แล้วก็ไม่มีเทสต์ตัวไหนล้ม

**P7: ด่านอ่านว่างสำหรับหลายชนิด ใครก็ list/download ได้**
- ชนิดที่ด่านอ่านว่าง: `StatutoryRemittance`, `WhtCredit`, JE ที่ไม่ลับ, `FixedAsset`, `OcrScan`
- `WhtCredit` ที่แนบก่อนบันทึก (`entityId=Guid.Empty`) รวมอยู่ถังเดียว คนที่เปิด `GET attachments/WhtCredit/0000…` จึงเห็นไฟล์ 50 ทวิ ที่ยังไม่บันทึกของทั้งบริษัท

**P8: คีย์ `int_` ที่ไม่ส่ง `X-Acting-User` จะได้ 403 ที่ `GET attachments/Document`**
- เพราะ `VisibleDirections` ของ IntegrationId ว่าง ทั้งที่ก่อนหน้านี้ผ่าน
- เส้นหลักของ TakeTime (`/api/integration` + base64) น่าจะไม่กระทบ แต่ควรเช็กว่า TakeTime ไม่ได้เรียก `/attachments` ผ่าน `X-Api-Key`

## ตรวจแล้วไม่มีปัญหา

**Migration**
- migration idempotent: `ADD … DEFAULT true`, จากนั้น `SET DEFAULT false`, แล้ว `UPDATE … WHERE IsLegacyKey AND LegacyDeprecatesAt IS NULL` ⇒ วันเลิกใช้คงที่ ไม่ถูกคำนวณใหม่
- ไม่มีเส้น DDL อื่นเพิ่มคอลัมน์ `IsLegacyKey` ก่อน
  - `GenerateCreateScript` สร้างแค่ CREATE TABLE
  - Program.cs:1530 เป็น `CREATE TABLE IF NOT EXISTS` ซึ่งไม่ทำอะไรกับตารางที่มีอยู่
- เปิด `EnableLegacyTimestampBehavior` ไว้ (Program.cs:50)
- EF ส่ง `IsLegacyKey=false` ทุกครั้งที่สร้างคีย์ใหม่

**คีย์ `int_` และการสวมผู้ใช้**
- คีย์ `int_` ทั้งสองทางเข้าใช้ `EffectiveScopes` ตัวเดียว
- `ApiKeyScopeFilter` เป็น global (Program.cs:795)
- ASP.NET ต่อ `ControllerActionFilter` ให้ controller ที่ implement `IAsyncActionFilter` ได้แม้สืบจาก `ControllerBase` และมี `[NonAction]` ครบ
- หน้าเว็บส่งสิทธิ์เฉพาะเมื่อผู้ใช้แตะช่อง และ hydrate ด้วยค่า effective ⇒ แก้ชื่อคีย์ legacy แล้วไม่เสียสิทธิ์
- endpoint จัดการคีย์ทุกตัวปฏิเสธ API key
- การผูกผู้ใช้เช็กทั้งการเป็นสมาชิกและว่า integration เป็นของบริษัท และตอนสวมผู้ใช้เช็กสมาชิกซ้ำอีกรอบ
- `TenantAccessMiddleware` ยังบล็อกคีย์ที่ข้ามบริษัท

**Tax (B-03 / B-04)**
- B-03 ครบ: ทุกจุดที่อ่าน `TaxReports` ด้วย id ใน `TaxService*` มี `CompanyId`, `DeferInputVat` กรองบริษัท, และ `KeyNotFound` แปลงเป็น 404
- B-04 ไม่ทำให้ใครใช้งานไม่ได้
  - คีย์ `Tax.File`, `Tax.Export`, `Journal.Manage` มีอยู่ใน catalog จึง grant ได้ (PermissionKeys.cs:149–156, 273–280)
  - Owner ผ่านอัตโนมัติ และ Accountant ได้ครบทั้งสามคีย์เป็นค่าเริ่มต้น

**ไฟล์แนบ**
- `GetById`, `GetByEntity` (รวม fallback ของ OCR) และ `Delete` กรอง `CompanyId` ทั้งหมด
- download/delete ใช้ EntityType/EntityId ที่เก็บไว้ในแถว ⇒ ไม่มี IDOR
- ตัวเช็กว่าเจ้าของไฟล์มีอยู่จริงกรองตามบริษัท
- EntityType ทุกชนิดที่ระบบเขียนลง `FileAttachments` อยู่ในตาราง
- PayrollRun ต้องผ่านด่านความลับ Payroll ทั้งอ่านและเขียน

**ความเสี่ยงคอมไพล์ (อ่านด้วยตา): ไม่พบ**
- `CheckAsync` ที่เปลี่ยนลายเซ็นมีผู้เรียกจุดเดียว
- `GetVisibleKindsAsync` และ `AuditAction.ApiAccess` มีอยู่จริง
- spread `..` อยู่ใน collection expression `[...]` ใช้ได้ใน C# 12 / net8
- `Document` ไม่ชนชื่อกับชนิดอื่น
