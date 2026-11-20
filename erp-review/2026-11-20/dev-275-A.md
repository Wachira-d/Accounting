# รอบ 275 แพ็ก A — คีย์และความลับ (ผลงานทีมพัฒนา)

> ขอบเขต: `WEEKLY_AUDIT_SUMMARY.md` §3 แพ็ก A — T4 H-1 · M-1 · M-2 · M-4 · M-5 (+ ฝ่ายค้าน 274-10 MED-2 ตามคำสั่งผู้ประสานงาน) ·
> คำตัดสิน 275-A-1…7 (`erp-review/2026-09-29/DECISIONS.md`) · runbook `docs/runbooks/encryption-key-rotation.md`

## ตารางสถานะ

| ข้อ | ยืนยันกับโค้ด | สถานะ | สิ่งที่ทำ |
|---|---|---|---|
| H-1 `SecretProtector` ไม่อ่าน `ENCRYPTION_KEY_PREVIOUS` | จริง (`SecretProtector` ctor ใช้ `LegacyCandidates` · PII ใช้ `PiiPreviousKeys`) | แก้แล้ว 2e7199e0 | ห่วงโซ่ตัวเดียว `EncryptionKeyResolver.DecryptKeyChain` ใช้ทั้ง `Program.cs` (PII) และ `SecretProtector` · reproduce: เทสต์ "ห่วงโซ่เดิม = WrongKey" + Db ย้ายจริง |
| — งานย้ายคีย์ความลับ | เดิมรันครั้งเดียวต่อบูต · ล้มแล้วไม่ลง JobRunLog | แก้แล้ว | ทุก 6 ชม. (เหมือน PII) · JobLock · audit ต่อแถว (เดิม) · ล้ม = JobRunLog ล้ม · นับค่าใช้ไม่ได้/ค้างคีย์เดิม · watermark |
| M-1 ข้อความชี้ API ดิบ/ชื่อ env · ไม่มีจอ | จริง (`Pnd1FilingData` · `EmployeeTaxIdentity` · `PayrollBankTransfer` · `SsoInsuredNumber` · `EmployeeIdentityHealth` · `LineFailureAlert` · `SecretWarnings`) | แก้แล้ว 2e7199e0 | `EncryptionHealthText` (ผู้ใช้ = ชื่อหน้า · ผู้ดูแล = ชื่อ env) · หน้า `/pages/encryption-health.html` (ความลับ + PII จาก endpoint เดิม) · ลิงก์จาก ตั้งค่า/พนักงาน · มุมผู้ดูแลใน admin › งานเบื้องหลัง · runbook |
| M-2 DTO พนักงานส่ง ciphertext ดิบ (+274-10 MED-2) | จริง (`MapToEmployeeResponse`) | แก้แล้ว 2e7199e0 | ป้าย `⛔ ถอดรหัสไม่ได้` ทุกสิทธิ์ + `IdentityProblem`/`BankAccountProblem`/`PiiProblemText` · ป้ายที่ส่งกลับ = คงค่าเดิม (echo guard เดิมคงอยู่) · ฟอร์มกล่องเหตุ + ขอบแดง + ป้ายในแถวรายชื่อ |
| M-4 หลายเครื่อง/หมุนซ้ำ | จริง (ไม่มีลายนิ้วมือ · PREVIOUS รับคีย์เดียว) | แก้แล้ว 2e7199e0 | `SiteSettings.EncryptionKeyFingerprint*` + `EncryptionKeyFingerprintRule` · งานย้ายคีย์**ทั้งสอง**หยุดเมื่อ Mismatch/RotatedBack · ยืนยันคีย์โดยผู้ดูแล (audit) · PREVIOUS หลายบรรทัด + `_2`…`_9` · ธง "ถอด PREVIOUS ได้" |
| M-5 ผู้อ่านความลับไม่ถาม `IsUsable` | จริง + พบเพิ่ม (Facebook login ข้ามตรวจ app id เมื่อ secret ถอดไม่ได้ = fail-open) | แก้แล้ว 2e7199e0 | `Helpers/SecretUse` (Open/Require/Optional) ที่ 22 จุด (ดูด้านล่าง) |

## ผู้เรียก `SecretProtector.Unprotect` ทั้ง 44 จุด (callers.py)

- **เปลี่ยนเป็น `SecretUse` (ล้มดังพร้อมเหตุ) 22 จุด:** WebhookService (ไม่ส่งไม่เซ็น) · CmsWebhookController (ไม่ตรวจด้วยกุญแจว่าง) · EtaxInvoiceService ×2 (รหัสใบรับรอง · RD secret — เหตุบนแถว e-Tax) ·
  EmailSenderFactory ×8 (ผู้ส่งที่ตอบล้มพร้อมเหตุ) · EmailService · NotificationEngine (SMTP) · AuthService ×3 (Google/LINE/Facebook login) · OmisePaymentProvider · BankFeedService ×2 · SignEventDispatcher · LineNotifyService (OA บริษัท — เหตุจริงในผลส่ง/หน้าทดสอบ)
- **ปรับข้อความ 1 จุด:** TaxSlipChannelService:145 ("กลุ่มไม่ได้ผูกแล้ว" ⇒ แยกเหตุถอดไม่ได้)
- **ล้มดังอยู่แล้ว/fail-closed — ไม่แก้ 17 จุด:** ContactLineQueueService ×2 · LineTestTargets · LineChatSlipService:751 · TaxSlipChannelService:84 (ตรวจค่าว่างแล้วประทับเหตุ "ถอดรหัสไม่ได้") ·
  ContactLineGroupService ×4 (`GroupProblem` ปฏิเสธค่าว่าง · ออกจากกลุ่มแบบ best-effort) · ContactRecipientResolver (ตัวตัดสินถือว่าอ่านไม่ได้ = ใช้ไม่ได้) · PlatformBillingNotifier (กรองค่าว่าง) ·
  DocumentPayLinkService (ถอดไม่ได้ ⇒ ออก token ใหม่) · NextAccLineConfig + AdminLineMessagingController (มีเหตุจาก Inspect แล้ว) · CustomerLineWebhookController + LineChatSlipService:97 (ขาเข้า 401 / ปิดรับสลิป — fail-closed · หน้าสุขภาพแสดงช่องที่เสีย) · CustomerLineLinkService (ตอบกลับไม่ได้ = false)
- **`PiiProtector` 1 จุด:** ตัวห่อ DataProtection ที่ไม่มีผู้เรียก (ไม่ใช่ SecretProtector)
- **แสดงผลอย่างเดียว 3 จุด:** PaymentSettingsController/AdminPlatformGatewayController/SignRequestService `Hint` (เลข 4 ตัวท้าย) — ค่าที่ใช้ไม่ได้ขึ้นในหน้า "สุขภาพข้อมูลเข้ารหัส"
- รูปแบบเดิม (`Unprotect` ตรงในเส้นที่เอาความลับไปยืนยันตัวตน/เซ็น) เหลือ **0 จุด** ในไฟล์ที่ล็อกด้วย `_R275A_`

## ทางเข้าอื่นที่แตะข้อมูลชุดเดียวกัน

- คอลัมน์ PII ทุกทางเข้า (แก้ไข · สร้าง · HRIS sync · นำเข้า) ใช้ `EmployeeRecordEdit` เดิม — ป้ายใหม่ผ่าน `IsEchoOfStoredUnreadable` ตัวเดียว (แก้ไข) และถูกปฏิเสธในสร้าง/sync/นำเข้า (ไม่มีค่าเดิม = ไม่ใช่เลข)
- งานย้ายคีย์ PII (`PiiReprotectJob`) ใช้ลายนิ้วมือชุดเดียวกัน (แตะ 1 บล็อกก่อน JobLock — ไม่แตะส่วนแกะ nested cipher ของ session 274-10)

## ไม่ใช่บั๊ก / เลื่อน

- `SitePaymentGateway.WebhookSecret` ไม่มีผู้เขียนในแอป (ยืนยันจาก `tools/secret_registry_check.py` READ_ONLY_REGISTERED) ⇒ ทางเข้าขาเข้าไม่มีแถวโดน แต่ปิด fail-open แล้วตามข้อ M-5
- HMAC ที่ derive จากคีย์ (OTP · SubjectKey · `TaxSlipRefHash`) ไม่มีคีย์เดิม — **เลื่อน** (ไม่อยู่ในขอบเขตแพ็ก A · บันทึกผลกระทบใน runbook: ใบขอชำระภาษีที่ออกก่อนหมุนต้องออกใหม่)
- ไม่บล็อกการบูตเมื่อลายนิ้วมือไม่ตรง (คำตัดสิน 275-A-3) — ข้อมูลยังอ่านได้ผ่านคีย์เดิม · ด่านอยู่ที่งานเขียน (ย้ายคีย์)

## สิ่งที่เจ้าของต้องทำหลัง deploy

1. ไม่ต้องตั้งอะไรเพิ่ม — เครื่องแรกที่รันงานย้ายคีย์บันทึกลายนิ้วมือเอง (`FirstSeen`)
2. ถ้าเคยหมุนคีย์ตามข้อความรอบ 274 (ตั้ง `ENCRYPTION_KEY_PREVIOUS` แล้ว) — ความลับจะกลับมาใช้ได้ทันทีหลัง deploy และถูกย้ายภายใน ~3 นาที · ตรวจ admin › งานเบื้องหลัง › 🔐 สุขภาพคีย์เข้ารหัส
3. ถ้าขึ้น `Mismatch`/`RotatedBack`: ตรวจ `ENCRYPTION_KEY` ทุกเครื่อง แล้วกด "ยืนยันคีย์ปัจจุบัน"
4. ถอด `ENCRYPTION_KEY_PREVIOUS` เมื่อหน้า admin ขึ้น "✓ ไม่มีข้อมูลบนคีย์เดิมเหลือ" (ตาม runbook)
