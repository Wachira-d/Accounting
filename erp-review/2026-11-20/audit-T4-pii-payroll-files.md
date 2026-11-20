# Audit T4 — PII / คีย์เข้ารหัส / ไฟล์เงินเดือน (รอบ 274 และทุกอย่างที่แตะ)

> อ่านโค้ดอย่างเดียว (ไม่ build · ไม่รันเทสต์ · ไม่แก้โค้ด) · branch `claude/booking-service-features-sdemob` @ `cf903d23` · 2026-11-20
> ไม่รายงานหัวข้อ "nested cipher auto-unwrap" (มีผู้พัฒนาทำอยู่) · path ทุกตัวอยู่ใต้ `Accounting/` · "ต้องยืนยัน" = ยังไม่ได้เปิดโค้ดถึงปลายทาง

## 1. ทะเบียนคอลัมน์เข้ารหัส / ความลับ

| คอลัมน์ / ความลับ | กลไก | ผู้อ่านหลัก | รับมือ "ถอดไม่ได้"? |
|---|---|---|---|
| `Employee.CitizenId` · `TaxId` | `EncryptedColumnConverter` (`Data/AccountingDbContext.cs:1820-1823`) คีย์ปัจจุบัน + `ENCRYPTION_KEY_PREVIOUS` + config เดิม + dev key | ภ.ง.ด.1/1ก (`Helpers/Pnd1FilingData.cs:203,354`) · 50 ทวิ เดือน/ปี (`PayrollService.cs:4029,3455`) · ภ.ง.ด.91 (`TaxFilingExportService.cs:926`) · สปส. (`:503,595,682,727`) · export (`PayrollExportSheets.cs:28-29`, `ImportExportService.cs:1819`) | ✅ ผ่าน `EmployeeTaxIdentity.Inspect` / `IsUnreadable` / `ForExport` |
| เดียวกัน — DTO พนักงานบนจอ | เดียวกัน | `PayrollService.cs:5065,5069` | ❌ ผู้มี `Pii.View` ได้ `enc1:…` ดิบลงช่องฟอร์ม · ผู้ไม่มีสิทธิ์ได้ `XXXXXXXXXXXXX` (ไม่มีป้ายว่าเสีย) |
| เดียวกัน — จับซ้ำตอนนำเข้า | เดียวกัน | `Import/DuplicateDetector.cs:328` (`EncryptedIdMatch.Same`) | ❌ เทียบตัวเลขที่ตัดจาก ciphertext ⇒ ไม่เจอเงียบ ๆ (เส้น `PayrollService.cs:1194` กันแล้ว) |
| `Employee.BankAccountNumber` | converter (`:1830`) | ไฟล์โอน (`Helpers/PayrollBankTransfer.cs:55`) · export (`PayrollExportSheets.cs:42`) | ✅ แถว "เลขบัญชีถอดรหัสไม่ได้" · DTO จอ `PayrollService.cs:5070` ❌ |
| `Employee.SocialSecurityNumber` | converter (`:1832`) | `Helpers/SsoInsuredNumber.cs:Resolve` | ✅ (ถอยไปใช้เลขบัตร/ว่าง) · DTO จอ `:5102` ❌ |
| `Employee.PassportNumber` | converter (`:1824`) | ไม่พบผู้อ่านเชิงธุรกิจใน Services/Helpers/Controllers (grep) | — (เก็บแล้วไม่มีผู้อ่าน · F2 ข้อ 2) |
| `ImportStagedRows.RawJson/MappedJson/OriginalMappedJson/BeforeJson` | converter (`:2575-2578`) | `Services/Import/ImportJson.cs:26` → `ImportPipelineService.Engine.cs:430,622,708,1361,1476` · undo `MasterDataImportTargets.cs:181` | ❌ `JsonSerializer.Deserialize("enc1:…")` ⇒ `JsonException` ดิบ |
| `CustomerSignerProfiles.FirstName/LastName/Title/Email` | converter (`:4085-4088`) + `SubjectKey` = HMAC จาก `ENCRYPTION_KEY` (`Helpers/CustomerSignerProfileRule.cs:59`) | `CustomerSignerProfileService.cs:112` (prefill หน้าลงนามสาธารณะ) · `:132` · `:378` (ค้น DSR ด้วยอีเมล) | ❌ ไม่ตรวจ `IsUnreadable` — แต่หมุนคีย์แล้ว `SubjectKey` เปลี่ยน ⇒ หาโปรไฟล์เดิมไม่เจออยู่แล้ว (ตั้งใจ · LOW-3 รอบ 267) |
| SMTP/MS/Gmail · LINE token/secret · e-Tax cert password + RD secret · `SignCallbackSecret` · Omise test/live · BankConnection · Webhook secret · ExternalIntegration · SitePaymentGateway · PayLink token · LINE user/group id | `SecretProtector` (`Helpers/SecretProtector.cs`) — คีย์ปัจจุบัน + `LegacyCandidates` (config เดิม · env trim · dev key) + `SecretReprotectJob` | ดูตาราง §2 กรณี S1–S4 | ⚠️ **ไม่อ่าน `ENCRYPTION_KEY_PREVIOUS`** (`SecretProtector.cs:84-86`) · `Unprotect` คืน `""` (`:171`) — ผู้อ่านบางตัวตรวจ `IsUsable` บางตัวไม่ |
| `SitePaymentGateway.ApiKeyEncrypted/SecretKeyEncrypted` | `EncryptionHelper.Encrypt` ตรง ด้วย `CurrentOrDev` (`CmsCommerceService.cs:54,1280`) — อยู่ในทะเบียน `SecretReprotectPass` | ไม่พบจุดถอดใน grep (ต้องยืนยัน) | — |
| `TaxSlipRefHash` (Control Code/เลขรายการธนาคาร) · `CustomerSignRule` OTP | HMAC ที่กุญแจ derive จาก `ENCRYPTION_KEY` (`Program.cs:164-174`) — **ไม่มีคีย์เดิม** | `TaxPaymentRequestService.cs:96` (เขียน) ↔ `TaxSlipIntakeService.cs:195` (เทียบ) | ❌ หมุนคีย์ ⇒ แฮชที่เก็บไว้เทียบไม่ตรง (ต้องยืนยันผลปลายทาง) |
| `PiiProtector` (DataProtection `enc:v1:`) | key ring ใน DB | register อย่างเดียว (`Program.cs:340`) ไม่มีผู้เรียก | — (dead · ไม่ผูกกับ ENCRYPTION_KEY) |
| `Contact.TaxId` | **ไม่เข้ารหัส** (ใช้ equality — คอมเมนต์ `:1827`) | 50 ทวิ/ภ.ง.ด.3/53 | ไม่ขึ้นกับคีย์ · แต่รับ "ขยะ" จากรอบก่อน 274 (ดู H-2) |

## 2. ตารางกรณีจำลอง

| # | กรณี | คาดหวัง | ที่เกิดจริง (จากการอ่าน) | ผล | หลักฐาน |
|---|---|---|---|---|---|
| K1 | ไม่มีคีย์ (dev key) → ตั้งคีย์จริง → บูต | PII อ่านได้ทันที แล้วถูกย้ายคีย์ | dev key อยู่ใน `PiiPreviousKeys` เสมอ ⇒ อ่านได้ · งานย้ายหลังบูต 3 นาที แล้วทุก 6 ชม. · เตือน dev-key ชม.ละครั้ง | OK | `EncryptionKeyResolver.cs:52-62,96` · `PiiReprotectJob.cs:172-180` |
| K1s | เดียวกัน ฝั่งความลับ (SMTP/LINE/Omise/e-Tax) | ใช้ได้ต่อ | dev key อยู่ใน `LegacyCandidates` ⇒ ถอดได้ + `SecretReprotectJob` | OK | `EncryptionKeyResolver.cs:86-98` |
| K2 | หมุน env→env ครั้งเดียว ตั้ง `ENCRYPTION_KEY_PREVIOUS` ถูก | ทุกอย่างอ่านได้ | PII อ่านได้ · **ความลับทุกตัวเป็น `WrongKey`** เพราะ `SecretProtector` ไม่รับคีย์ PREVIOUS | **BUG** | `SecretProtector.cs:82-87` (ไม่มี `PreviousKeyEnv`) เทียบ `Program.cs:159-162` |
| K3 | หมุนสองครั้ง K1→K2→K3 (งานย้ายยังไม่จบ/ล้มระหว่าง) | แถวบน K1 ยังอ่านได้ | `ENCRYPTION_KEY_PREVIOUS` รับคีย์เดียว (ไม่มีตัวคั่น/รายการ) ⇒ แถวที่ค้างบน K1 = Undecryptable · ไม่มีด่าน "ห้ามหมุนซ้ำก่อนงานย้ายสำเร็จ" นอกจากข้อความในรายงาน | GAP | `Program.cs:161` · `EmployeeIdentityHealth.cs:171` |
| K4 | `ENCRYPTION_KEY_PREVIOUS` ผิด | ไม่ทำลายข้อมูล · บอกดัง | ไม่แตะแถว (`Undecryptable` นับ + ข้าม) · ไฟล์ยื่นปฏิเสธพร้อมเหตุ · JobRunLog บอกจำนวน · **ไม่มีอะไรบอกว่า "คีย์ PREVIOUS ที่ตั้งไม่ได้ถอดอะไรได้เลย"** | OK/GAP | `PiiReprotectJob.cs:113,195` |
| K5 | คีย์มีช่องว่างท้าย | ใช้ค่าดิบสม่ำเสมอ | PII และ SecretProtector ใช้ค่าดิบเหมือนกัน · เดิม `"abc"`→ใหม่ `"abc "` ถอดได้อัตโนมัติ (ลอง trim) · กลับทาง `"abc\n"`→`"abc"` ต้องใส่ PREVIOUS ที่มีช่องว่างท้ายเป๊ะ (เครื่องมือ env ส่วนใหญ่ตัดทิ้ง) | GAP (ต้องยืนยัน) | `EncryptionKeyResolver.cs:59-60,93-95` |
| K5b | env `ENCRYPTION_KEY=""` (สตริงว่าง) + config มีคีย์ | ใช้คีย์ config | PII: `env ?? config` = `""` ⇒ production throw (fail-fast) · dev: PII ใช้ dev key แต่ SecretProtector ใช้ config ⇒ สองคีย์ | GAP ต่ำ | `Program.cs:134-135,144` เทียบ `:138` |
| K6 | สอง instance คนละคีย์ | บูตไม่ขึ้น/ฟ้องดัง | ไม่มีลายนิ้วมือคีย์ในฐาน ⇒ ไม่มีใครรู้ · instance ที่มีคีย์อีกฝั่งเป็น PREVIOUS จะย้ายทุกแถวไปคีย์ตัวเอง ⇒ อีกเครื่องเห็น "ถอดรหัสไม่ได้" (ไฟล์ปฏิเสธ ไม่เขียนเลขผิด) · ถ้าต่างฝ่ายต่างมี PREVIOUS ของกัน = ย้ายกลับไปมาทุก 6 ชม. + audit พอก | GAP | `PiiReprotectJob.cs:60-62` (ไม่มีด่านตรวจคีย์ร่วม) |
| K7 | งานย้ายล้มกลางทาง | ไม่มีแถวครึ่ง ๆ · ลองใหม่ | UPDATE+audit ต่อชุด 200 อยู่ธุรกรรมเดียว · idempotent · รอบถัดไปอีก 6 ชม. (ไม่ลองเร็วกว่า) · รายงานแสดง "งานย้ายคีย์ล้ม" — พร้อม `ex.Message` ดิบ | OK (LOW L-4) | `PiiReprotectJob.cs:104-142,205-208` |
| K8 | ผู้ใช้แก้แถวระหว่างงานวิ่ง | ของผู้ใช้ชนะ | `UPDATE … WHERE Id AND col = @old` ⇒ 0 แถว = ข้าม | OK | `PiiReprotectJob.cs:121` |
| K9 | restore ฐานสำรองเก่า (คีย์เก่ากว่า PREVIOUS) | บอกชัดว่าต้องใช้คีย์ไหน | เหมือน K3/K4 — Undecryptable + ทางไปต่อ "ตั้งคีย์เดิม/กรอกใหม่" · ความลับ = WrongKey ทั้งชุด | GAP | — |
| K10 | tenant ใหม่ | ไม่มีอะไร | รายงานว่าง · ไฟล์ปกติ | OK | — |
| F1 | ต่างชาติ มีแต่ passport (เลขบัตร/TaxId ว่าง) — ภ.ง.ด.1/1ก | ออกไฟล์ได้ | `Missing` ไม่ถูกกัน ⇒ ลง 0×13 + คำเตือน · **ไม่กันทั้งไฟล์** | OK | `Pnd1FilingData.cs:200` · `:314-317` |
| F2 | คนเดียวกัน — 50 ทวิ รายเดือน + นำส่ง ภ.ง.ด.1 | ออกใบได้ หรือมีทางข้ามที่รับทราบ | ถูก skip ("ยังไม่ได้กรอกเลข…") ⇒ ถ้าเดือนนั้นมีภาษีหัก > 0 ช่องโหว่ใบทำให้ **นำส่ง ภ.ง.ด.1 ของทั้งบริษัทถูกบล็อก** `WHT-CERT-UNISSUED` — ไฟล์ยื่นยอมแต่ด่านนำส่งไม่ยอม | **GAP** | `PayrollService.cs:4031-4038` · `StatutoryRemittanceService.cs:163-197` |
| F3 | คนเดียวกัน — สปส.1-10 | ออกได้ถ้ามีเลข ปกส. | เลขบัตรว่าง = ผ่าน (ช่องว่าง + นับเตือน) · แต่ถ้าช่องเลขบัตรมีค่าเก่าที่ไม่ผ่าน (เช่น passport ที่เข้ามาทางนำเข้า/HRIS) **ทั้งไฟล์ถูกปฏิเสธแม้เลข ปกส. ถูกต้อง** เพราะตรวจ `CitizenId` ของทุกคนไม่ดูเลข ปกส. | GAP | `TaxFilingExportService.cs:503` · `SsoInsuredNumber.cs:31-36` |
| F4 | เลขบัตรว่าง (คนไทย) | เตือน ไม่แต่งเลข | เหมือน F1/F2 | OK/GAP (F2) | — |
| F5 | เลขไม่ผ่าน mod-11 "ตามที่บัตรเขียน" | มีทางไปต่อ | บัตรไทยจริงผ่าน mod-11 เสมอ ⇒ เป็นพิมพ์ผิด · แต่**ไม่มี override** สำหรับเลข 10 หลักเดิม/เลขต่างด้าวรูปอื่น: ไฟล์ทั้งบริษัทถูกปฏิเสธจนกว่าจะ "ล้างช่อง" (แล้วกลายเป็น 0×13) | GAP | `EmployeeTaxIdentity.cs:70-77` · `TaxFilingExportService.cs:110-114` |
| F6 | พนักงานลาออกที่เลขถอดไม่ได้ — ภ.ง.ด.1ก | แก้ได้ | รายปีรวมทุกคนที่มีแถวจ่าย ⇒ กันทั้งไฟล์จนกว่าจะกรอกเลขใหม่ที่ทะเบียนคนลาออก · รายงานรวมคนลาออก (กรองแค่ `IsDeleted`) | OK | `EmployeeIdentityHealth.cs:93` |
| F7 | ผู้รับจ้าง ภ.ง.ด.3 | ไม่กระทบ | ใช้ `Contact.TaxId` (ไม่เข้ารหัส) — ไม่ผ่าน `Inspect` (grep `PndTextFileFormat` ไม่มีด่าน checksum) | OK (ต้องยืนยันด่านเลขของ ภ.ง.ด.3) | — |
| F8 | ภ.ง.ด.1ก หลังแก้เลขกลางปี | ทั้งปีใช้เลขใหม่ | แถวเงินเดือนอ่านจาก `Employee` ปัจจุบัน ⇒ ถูกทั้งปี · 50 ทวิ ภ.ง.ด.1ก รายปีก็สร้างจาก `Employee` | OK | `Pnd1FilingData.cs:219-231` · `PayrollService.cs:3565` |
| F9 | 50 ทวิ รายเดือนที่ออกไปแล้วด้วยเลขขยะ | มีทางออกใบใหม่ | รายงานบอก "กดสร้างเอกสารใหม่" — แต่**งวดที่ยื่น ภ.ง.ด.1 แล้ว ระบบคงใบเดิมทั้งชุด** (S4-6) ⇒ คำแนะนำพาไปทางตัน | **BUG** | `EmployeeIdentityHealth.cs:128-130` เทียบ `PayrollService.cs:4143-4153` |
| F10 | Contact ขยะที่รอบก่อนสร้างไว้ | ถูกเก็บกวาด/ติดป้าย | ออกใบใหม่ค้นด้วย `TaxId == เลขถูก` ⇒ สร้าง Contact ใหม่ชื่อซ้ำ · ตัวขยะ (`IsSupplier=true` เลข 5–14 หลัก) ค้างในรายชื่อผู้ขาย · พอใบเดิมถูกยกเลิกก็**หลุดจากรายงาน**ด้วย (รายงานดูเฉพาะ Contact ที่มีใบไม่ยกเลิก) · ไม่มี migration · ไม่ถูก reuse ด้วยชื่อในเส้นเงินเดือน (ค้นด้วยเลขเท่านั้น) แต่ผู้ใช้เลือกผิดตัวจาก dropdown ตอนออก 50 ทวิ ภ.ง.ด.3 ได้ | **GAP** | `PayrollService.cs:4049-4062` · `EmployeeIdentityHealth.cs:115-124` |
| F11 | ไฟล์โอนเมื่อบัญชีหนึ่งถอดไม่ได้ | ไม่โอนผิดบัญชี · ยอดรวมไม่เพี้ยน | ไม่กันทั้งไฟล์ · แถวนั้น `Ready=false` เลขบัญชีว่าง สถานะ "เลขบัญชีถอดรหัสไม่ได้" ยอดยังอยู่ ⇒ ผลรวมคอลัมน์ = `TotalNetPay` · ไม่มีบรรทัดสรุป "พร้อมโอน X จาก Y ราย / ยอดพร้อมโอน" ในไฟล์ | OK (LOW L-6) | `PayrollBankTransfer.cs:55-71` · `PayrollExportSheets.cs:141-166` |
| F12 | ใบมือ ภ.ง.ด.1 ของผู้รับไม่อยู่ในไทย (ประเภท 5) ที่ `Contact.TaxId` เป็นเลขต่างประเทศ | ลงไฟล์/เตือนเฉพาะแถว | `InspectValue` = Invalid ⇒ **กันทั้งไฟล์ ภ.ง.ด.1 ของบริษัท** | GAP | `Pnd1FilingData.cs:243-248` |
| S1 | หมุนคีย์ (K2) — LINE OA | ฟ้องว่าคีย์เปลี่ยน | `IsUsable` ⇒ ถือว่า "ไม่มี OA" เงียบ (push หยุด) | GAP | `LineNotifyService.cs:58-60` |
| S2 | เดียวกัน — Omise | ข้อความเหตุจริง | throw "ยังไม่ได้ตั้งค่า secret key" (เหตุไม่ตรง — F2 ข้อ 7) | GAP | `OmisePaymentProvider.cs:118-125` |
| S3 | เดียวกัน — e-Tax (รหัสใบรับรอง/RD secret) | ไม่ลองเซ็นด้วยรหัสว่าง | ส่ง `""` เข้าไปเฉย ๆ ไม่ตรวจ `IsUsable` ⇒ ล้มที่ปลายทางด้วยเหตุ "รหัสผิด" | GAP | `EtaxInvoiceService.cs:71-73` |
| S4 | เดียวกัน — webhook ขาออก / webhook CMS ขาเข้า | ไม่ส่งแบบไม่เซ็น · ไม่ตรวจด้วยกุญแจว่าง | ขาออก: secret ว่าง ⇒ **ส่งโดยไม่มีลายเซ็น** เงียบ · ขาเข้า: `Unprotect` คืน `""` (ไม่ null) ⇒ `VerifyHmac(body, sig, "")` = ตรวจด้วยกุญแจว่าง | BUG (M-5) | `WebhookService.cs:395-396` · `CmsWebhookController.cs:112,204-208` |
| P1 | PDPA — เลขเต็มใน log/audit/422 | ไม่มี | ข้อความปฏิเสธ/คำเตือน/แจ้งเตือน/LogError ใช้รหัส+ชื่อ+เลขปิดบัง/จำนวนหลัก · audit งานย้ายมีแต่ชื่อช่อง | OK | `Pnd1FilingData.cs:69-74` · `PiiReprotectJob.cs:131-134` |
| P2 | endpoint `tax-identity-health` | สิทธิ์เงินเดือน + ปิดบัง + บันทึกการเข้าถึง | ด่าน `CheckPayrollAccessAsync` · ปิดบังเสมอ · **ไม่มี `PiiAccessLog`** (ชื่อ-สกุล+ตัวเลขหัว/ท้าย ⇒ รับได้ แต่ควรจดว่าใครดู) · ส่ง `JobRunLog.Message` ของงาน**ทั้งแพลตฟอร์ม**ให้ tenant | GAP ต่ำ | `PayrollController.cs:751-764` · `EmployeeIdentityHealth.cs:68-72` |
| P3 | ไฟล์โอน/ทะเบียนส่งออก | `pii:view` + log | มีครบ | OK | `PayrollController.cs:1128-1141` |

## 3. ข้อค้นพบ

### HIGH

**H-1 · `SecretProtector` ไม่รู้จัก `ENCRYPTION_KEY_PREVIOUS` — หมุนคีย์ตามคู่มือรอบ 274 แล้ว integration ทั้งชุดดับ** (K2)
- สถานการณ์: เจ้าของหมุน `ENCRYPTION_KEY` K1→K2 และตั้ง `ENCRYPTION_KEY_PREVIOUS=K1` ตามข้อความที่ระบบแนะนำเอง ⇒ ไฟล์ ภ.ง.ด.1 กลับมาถูก แต่ SMTP · LINE OA · Omise · e-Tax · webhook · bank feed · pay-link เป็น `WrongKey` ทั้งหมด (คีย์ K1 ไม่อยู่ใน `LegacyCandidates` เว้นแต่ K1 เคยอยู่ใน config) และ `SecretReprotectJob` ย้ายไม่ได้ ⇒ อีเมล/แจ้งเตือน LINE เงียบ (S1) · จ่ายออนไลน์ขึ้น "ยังไม่ได้ตั้งค่า" (S2)
- หลักฐาน: `Helpers/SecretProtector.cs:82-87` · `Helpers/EncryptionKeyResolver.cs:52-62` (PII เท่านั้นที่ `Add(previousEnv)`)
- แก้ (S): ให้ `SecretProtector` ใช้รายการคีย์เดิมตัวเดียวกับ PII (`PiiPreviousKeys`) + เทสต์ round-trip "หมุน env→env แล้วความลับทุกชนิดยังถอดได้ และ SecretReprotectJob ย้าย" + เพิ่มใน `required_call_site_check`

**H-2 · ของเสียที่ persist ก่อนรอบ 274 ไม่มีทางเก็บกวาดครบ (F9 + F10)**
- สถานการณ์ ก: งวดที่ยื่น ภ.ง.ด.1 แล้ว — 50 ทวิ รายเดือนยังผูก Contact เลขขยะ · รายงานสั่ง "สร้างเอกสารใหม่" ซึ่งระบบปฏิเสธ (คงใบเดิม) ⇒ แถว `BlocksFiling=true` ค้างตลอดกาล ผู้ใช้ไม่มีปุ่มไหนทำให้หาย · พนักงานถือ 50 ทวิ เลขผิดโดยไม่มีทางออกใบแทน
- สถานการณ์ ข: งวดที่ยังไม่ยื่น — ออกใหม่แล้วได้ Contact ใหม่ ตัวขยะหลุดจากรายงานแต่ค้างในรายชื่อผู้ขาย (ชื่อพนักงานซ้ำสองแถว) ⇒ เลือกผิดตอนออก 50 ทวิ ภ.ง.ด.3/ใบสำคัญจ่าย = เลขขยะลงแบบอีกทาง (ราว 1/11 ของขยะ 13 หลักผ่าน mod-11 โดยบังเอิญ จึงไม่ถูกด่านใดจับ)
- หลักฐาน: `EmployeeIdentityHealth.cs:115-131` · `PayrollService.cs:4049-4062,4143-4153`
- แก้ (M): (1) รายงานแยก "งวดยื่นแล้ว" ⇒ ทางไปต่อจริง = แก้ `TaxId` ของ Contact ตัวนั้นให้เป็นเลขพนักงาน (ใบเดิมพิมพ์ใหม่ได้เลขถูก · ยอดไม่เปลี่ยน) หรือ merge เข้าตัวที่ถูก (2) migration/ปุ่ม "เก็บกวาด Contact จากเงินเดือน" — หา Contact ที่ถูกอ้างโดยใบ `SourcePayrollRunId != null` เท่านั้น + เลขไม่ผ่าน `InspectValue` ⇒ จับคู่กลับพนักงานผ่าน `EmployeeCode` ท้ายเลขใบ `PND1-yyyymm-รหัส` แล้วแก้เลข/ซ่อน (audit) (3) เทสต์ทิศตรงข้าม: Contact ผู้ขายจริงที่เลขไม่ครบไม่ถูกแตะ

**H-3 · ไฟล์ยื่น "ยอมให้เลขว่าง" แต่ด่านนำส่ง "ไม่ยอม" + ไม่มีทางรับทราบรายคน — หนึ่งคนล็อกทั้งบริษัท (F2 · F3 · F5 · F12)**
- สถานการณ์: พนักงานต่างชาติ 1 คนยังไม่มีเลข 13 หลัก มีภาษีหัก ⇒ 50 ทวิ รายเดือนไม่ออก ⇒ นำส่ง/ยื่น ภ.ง.ด.1 ของทั้งบริษัทถูกบล็อกทุกเดือน · ถ้าช่องเลขมีค่าเก่าที่ไม่ผ่าน (เข้าทางนำเข้า/HRIS — ต้องยืนยันว่าทางนั้นตรวจ checksum) ⇒ ไฟล์ ภ.ง.ด.1/1ก และ สปส. ทุกรูปแบบถูกปฏิเสธทั้งไฟล์ · ทางเดียวคือ "ล้างช่อง" ซึ่งทำให้กลายเป็นกรณีเลขว่าง (0×13) — คือผลลัพธ์ที่แย่กว่าการมีทางรับทราบตรง ๆ
- หลักฐาน: `Pnd1FilingData.cs:200,243` · `SsoInsuredNumber.cs:31` · `StatutoryRemittanceService.cs:186-197` · `EmployeeRecordEdit.cs:59-62` (ฟอร์มรับเฉพาะ 13 หลัก+checksum)
- แก้ (M): ธงรายพนักงาน/รายผู้รับ "ไม่มีเลข 13 หลัก (ต่างชาติ) — รับทราบแล้ว" (เหตุผล + ผู้อนุมัติ + audit `RuleCode`) ⇒ ไฟล์ลง 0×13 พร้อมหมายเหตุ · 50 ทวิ ออกได้โดยช่องเลขว่าง · ช่องโหว่นำส่งไม่นับ · สปส. ตรวจเลขบัตรเฉพาะแถวที่**ไม่มี**เลข ปกส. ใช้ได้ · คงการปฏิเสธทั้งไฟล์สำหรับ `Undecryptable` (ต้องคำตัดสินเจ้าของ — เพิ่มใน DECISIONS)

### MED

**M-1 · รอบ 274 ไม่มีจอ — ข้อความถึงผู้ใช้ชี้ไปที่ `GET payroll/tax-identity-health` และ `ENCRYPTION_KEY_PREVIOUS`** — ผู้ใช้เงินเดือนของ tenant เรียก API ดิบไม่ได้และตั้ง env ของแพลตฟอร์มไม่ได้ (`Pnd1FilingData.cs:74` · `EmployeeTaxIdentity.cs:45-46` · `PayrollBankTransfer.cs:68` · grep `wwwroot` = 0 จุด) · แก้ (M): ดู §4.1 + แยกข้อความสองผู้ฟัง (tenant: "แจ้งผู้ดูแลระบบ หรือกรอกเลขใหม่" · ผู้ดูแล: ชื่อ env)

**M-2 · DTO พนักงานส่ง ciphertext ดิบขึ้นจอ** (`PayrollService.cs:5065-5070,5102`) — ผู้มี `Pii.View` เห็น `enc1:…` ในช่องเลขบัตร/บัญชี · ถ้าฟอร์มส่งค่ากลับทั้งชุด `ThaiIdNumber` ปฏิเสธด้วย "ค่านี้เป็นเลขที่ถูกปิดบัง (PDPA)" (base64 มักมี X/x — `EmployeeRecordEdit.cs:48`) ⇒ แก้ช่องอื่นของคนนั้นไม่ได้และเหตุที่บอกไม่ตรง (ต้องยืนยันว่าฟอร์มส่งทุกช่อง) · ผู้ไม่มีสิทธิ์เห็น `XXXXXXXXXXXXX` เหมือนเลขปกติ · แก้ (S): DTO ใช้ `EmployeeTaxIdentity.Display` + `ForExport` และเพิ่ม `IdentityProblem`/`BankAccountProblem` (ชื่อ enum) ให้จอติดป้าย

**M-3 · รายงานสุขภาพฟ้องผู้รับ 50 ทวิ ทุกแบบ** (`EmployeeIdentityHealth.cs:115-131` ไม่กรอง `TaxFormType`/ประเทศ) — ผู้รับต่างประเทศ (ภ.ง.ด.54) หรือ Contact ที่เลขมีตัวเลขไม่ครบ 13 ถูกขึ้น `BlocksFiling=true` + "แก้เลขที่ผู้ติดต่อ แล้วพิมพ์ใบใหม่" ทั้งที่ไม่เกี่ยวกับเหตุคีย์ ⇒ `Blocking` บวม (คำเตือนที่ฟ้องใบถูก — F2 ข้อ 8) · แก้ (S): จำกัดที่ `WithholdingTax1` (+ 3/53 เฉพาะ `CountryCode` TH/ว่าง) และแยก Issue ใหม่ `PayeeContactInvalid` ไม่ปนกับ `Invalid` ของพนักงาน

**M-4 · หมุนคีย์ซ้ำ/หลายเครื่องไม่มีเครื่องกัน** (K3 · K6 · K9) — แก้ (M): เก็บ "ลายนิ้วมือคีย์" (HMAC ค่าคงที่ด้วยคีย์ปัจจุบัน) ใน `SiteSettings` · บูตแล้วไม่ตรงและคีย์เดิมไม่อยู่ในรายการ ⇒ LogError + แบนเนอร์ผู้ดูแล · `ENCRYPTION_KEY_PREVIOUS` รับหลายคีย์ (ตัวคั่นที่ไม่ชน base64 เช่นขึ้นบรรทัดใหม่/`ENCRYPTION_KEY_PREVIOUS_2`) · งานย้ายเขียน watermark "ไม่มีแถวบนคีย์เดิมเหลือ" ให้ผู้ดูแลรู้ว่าถอดคีย์เดิมออกได้

**M-5 · ผู้อ่านความลับที่ใช้ `Unprotect` โดยไม่ถาม `IsUsable`** (S3 · S4) — `EtaxInvoiceService.cs:71-73` · `WebhookService.cs:395` (ส่งไม่เซ็น) · `CmsWebhookController.cs:112` (ตรวจลายเซ็นด้วยกุญแจว่าง — endpoint `[AllowAnonymous]`; grep ไม่พบจุดเขียน `WebhookSecret` ในแอป ⇒ ปัจจุบันน่าจะไม่มีแถวให้โดน แต่เป็นรูปแบบ fail-open · ต้องยืนยัน) · แก้ (S): helper กลาง `RequireUsable(value, ชื่อความลับ)` คืนเหตุจาก `Inspect` (WrongKey/Malformed/Empty) และห้าม HMAC ด้วยกุญแจว่าง

**M-6 · แถวนำเข้าที่ถอดไม่ได้ล้มด้วย exception ดิบ** — `ImportJson.Deserialize` (`Services/Import/ImportJson.cs:26`) รับ `enc1:…` ⇒ `JsonException` ในหน้าตรวจ/commit/ย้อนการนำเข้า (undo ใช้ `BeforeJson`) · แก้ (S): ตรวจ `IsUnreadable` ⇒ `BusinessRuleException` "รอบนำเข้านี้อ่านไม่ได้หลังเปลี่ยนคีย์ — นำเข้าไฟล์ใหม่" และให้ undo ปฏิเสธแถวนั้นแบบมีเหตุ

### LOW

- **L-1** ตัวจับซ้ำตอนนำเข้าพนักงานเงียบเมื่อเลขเดิมถอดไม่ได้ ⇒ สร้างพนักงานซ้ำ (`DuplicateDetector.cs:328`) — แก้ (S): ข้ามค่า `IsUnreadable` แล้วขึ้นคำเตือน "ตรวจซ้ำด้วยเลขบัตรไม่ได้ N คน"
- **L-2** รายงานตัดที่ `LIMIT 2000` ต่อคอลัมน์โดยไม่บอก (`EmployeeIdentityHealth.cs:93`) และสแกน `ImportStagedRows` 4 คอลัมน์ทุกครั้งที่เรียก — แก้ (S): ธง `Truncated` + จำกัดคอลัมน์นอกทะเบียนพนักงานเป็น "จำนวน" อย่างเดียว
- **L-3** `tax-identity-health` ไม่จด `PiiAccessLog` (P2) — แก้ (S): `LogPiiAccessAsync(… "IdentityHealth", operation "Read")`
- **L-4** `RekeyJob.Message` ของทั้งแพลตฟอร์ม (จำนวนแถวทุก tenant · `ex.Message` ดิบ) ถูกส่งให้ tenant (`PayrollController.cs:760-761`) — แก้ (S): ส่งเฉพาะเวลา+สำเร็จ/ล้ม · รายละเอียดไว้หน้า admin
- **L-5** `PiiMask.CitizenId` กับค่า ciphertext: ถ้าตัวเลขใน base64 มี 13 ตัวพอดี จะได้หน้ากากรูปเลขบัตรปลอม (`Helpers/PiiMask.cs:19-25` · ใช้ที่ `PayrollService.cs:3523,5065`) — แก้ (S): ตรวจ `IsUnreadable` ก่อน
- **L-6** ไฟล์โอนไม่มีแถวสรุป "พร้อมโอน X/Y ราย ยอด …" — ผู้ใช้ที่กรองแถวไม่พร้อมออกเองจะได้ยอดไม่เท่ารอบโดยไม่มีตัวเลขเทียบ
- **L-7** Contact ที่เส้นเงินเดือนสร้างให้พนักงานต่างชาติไม่มีชื่อไทย = ชื่อว่าง (`PayrollService.cs:4055` ใช้แต่ `FirstNameTh`) ขณะที่ไฟล์ ภ.ง.ด.1 ถอยไปชื่ออังกฤษแล้ว (`Pnd1FilingData.cs:357-362`) — คู่สมมาตรที่ลืม
- **L-8** `Employee.PassportNumber` เข้ารหัส/ย้ายคีย์/รายงาน แต่ไม่มีผู้อ่านเชิงธุรกิจ — ถ้า H-3 ทำ ให้ใช้เป็นเลขอ้างอิงของผู้ไม่มีเลข 13 หลัก มิฉะนั้นจดไว้ว่าเป็นช่องข้อมูลอย่างเดียว
- **L-9** K5/K5b (ช่องว่างท้ายคีย์ · env สตริงว่าง) — เอกสาร deploy ควรระบุ และ `Resolve` ควรใช้ทั้งสองฝั่งให้เหมือนกัน (`Program.cs:134` ยังเป็น `??`)

## 4. ฟีเจอร์/UX ที่ควรสร้าง

1. **จอ "เลขประจำตัวที่ต้องแก้"** (ปิด M-1)
   - **แบนเนอร์** บน `wwwroot/pages/payroll.html` (หัวหน้ารอบเงินเดือน) และ `pages/employees.html` เมื่อ `Blocking > 0`: "⛔ มี N รายการที่เลขประจำตัวใช้ไม่ได้ — ไฟล์ ภ.ง.ด.1/สปส. จะไม่ออกจนกว่าจะแก้ [ดูรายการ]" (เซิร์ฟเวอร์ส่งข้อความ จอแค่แสดง)
   - **แท็บ/modal ใน employees.html**: ตาราง ที่มา · รหัส · ชื่อ · ช่อง · ปัญหา (ป้ายไทยจากเซิร์ฟเวอร์) · เลขปิดบัง · ทางไปต่อ · ปุ่ม "เปิดพนักงานคนนี้" (โฟกัสช่องที่เสีย) · กรอง "กันการยื่นแบบ" · บรรทัดสถานะงานย้ายคีย์
   - **ป้ายในแถวรายชื่อพนักงาน + ในฟอร์มแก้ไข** ("⛔ ถอดรหัสไม่ได้ — กรอกใหม่") จาก `IdentityProblem` ใน DTO (M-2)
   - **ลิงก์จาก 422** `RD-PND1-PAYEE-TAXID` / `SSO-CITIZENID-UNUSABLE` / `RD-50TWI-PAYEE-TAXID` และจากแถว "เลขบัญชีถอดรหัสไม่ได้" ของไฟล์โอน ไปจอนี้แทนข้อความ `GET …`
2. **หน้าผู้ดูแลแพลตฟอร์ม "สุขภาพคีย์เข้ารหัส"** ใน `wwwroot/admin/background-jobs.html` (หรือหน้าใหม่): ลายนิ้วมือคีย์ · จำนวนแถวต่อสถานะ (Current/Previous/Undecryptable) ต่อคอลัมน์**ทุก tenant** · สถานะความลับ (`SecretState`) ต่อชนิด · ปุ่ม "รันงานย้ายคีย์ตอนนี้" · ธง "ถอด `ENCRYPTION_KEY_PREVIOUS` ออกได้แล้ว"
3. **ทางรับทราบผู้ไม่มีเลข 13 หลัก** (H-3) + ใช้ `PassportNumber` เป็นข้อมูลประกอบ
4. **เครื่องมือเก็บกวาด Contact จากเงินเดือน** (H-2) — รายงานก่อน/ซ่อมเฉพาะแถวที่เลือก (รูปแบบเดียวกับ `FindProductTotalMismatchesAsync`/`Repair…`)
5. **Runbook หมุนคีย์** ใน docs: ลำดับ (ตั้ง PREVIOUS → บูต → รอทั้งสองงานสำเร็จ → ตรวจหน้า admin → ถอด PREVIOUS) และข้อห้ามหมุนซ้ำ
6. **พรีวิว/รายงานตรวจทานที่ดาวน์โหลดได้แม้ถูกกัน** — ปัจจุบันกันทุกรูปแบบ (`TaxFilingExportService.cs:110`) ผู้ใช้ที่ต้องยื่นกระดาษวันสุดท้ายไม่มีเอกสารอ้างอิงนอกจอ: ให้ออก "รายงานตรวจทาน (ไม่ใช่ไฟล์ยื่น)" ที่แถวถูกกันแสดงเป็น ⛔ + ลายน้ำ

## 5. ตรวจด้วยการอ่านไม่ได้

- พฤติกรรมจริงของ `PiiReprotectPass` บน PostgreSQL (regex `CandidateSql` · ชนิดคอลัมน์ · เวลารันกับ `ImportStagedRows` ขนาดใหญ่ · audit chain ใต้ advisory lock หลายพันแถว) — มี `Db/PiiKeyRotationDbTests` แต่ไม่ได้รัน
- ฟอร์ม `employees.html` ส่งทุกช่องหรือเฉพาะช่องที่แตะ (ผลของ M-2) · จอแสดงข้อความ 422 หลายบรรทัดอย่างไร
- ทางนำเข้า/HRIS sync ตรวจ checksum เลขบัตรหรือไม่ (ที่มาของค่า `Invalid` ใน H-3) · `SitePaymentGateway.WebhookSecret`/`ApiKeyEncrypted` มีผู้เขียน-ผู้อ่านจริงหรือไม่ (M-5)
- ผลปลายทางของ `TaxSlipRefHash` หลังหมุนคีย์ (สลิปภาษีจับคู่คำขอเดิมไม่ได้?) และ OTP ลงนามที่ค้างอยู่
- เลขผู้เสียภาษีที่สรรพากรออกให้คนต่างด้าว (ขึ้นต้น 0) ผ่าน mod-11 ทุกเลขหรือไม่ — ถ้าไม่ `EmployeeTaxIdentity` จะกันเลขที่ถูกต้อง (ต้องถามนักบัญชี/ตัวอย่างจริง)
- สเปก e-Filing รับ 0×13 ในช่องเลขผู้มีเงินได้ได้จริงหรือปฏิเสธทั้งไฟล์
- ค่าใน production ตอนนี้: เหลือแถว `Previous`/`Undecryptable` เท่าไร และมี Contact ขยะกี่ตัว (ต้อง query ฐานจริง)
