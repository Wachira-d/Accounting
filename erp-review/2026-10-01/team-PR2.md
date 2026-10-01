# รอบ 201 ทีม PR2 — แก้ผลฝ่ายค้านรอบสอง (คอมมิต 2fe0e7c4/194206d1 · merge e3562380) — แก้ที่ `c5fefbc6`

รายงานหลักของทีมอยู่ที่ `erp-review/2026-09-29/team-PR2.md` · ไฟล์นี้คือรอบที่สอง · **ยังไม่ได้คอมไพล์/รันเทสต์ในเครื่องนี้ (ไม่มี .NET SDK)**

| ID | สถานะ | สิ่งที่ทำ | ที่ |
| --- | --- | --- | --- |
| **P1-a** | ✅ | ยืนยันที่ HEAD: `StatutoryRemittanceService` ประทับรอบเฉพาะ `SsoSps110` · ตัวหาหลักฐานไม่อ่านการนำส่ง ภ.ง.ด.1 ⇒ เพิ่มแหล่ง `PayrollFilingSource.StatutoryRemittance` (แถว `WhtPnd1` · `!IsDeleted` · tenant · งวดเดียวกัน) ใน `LoadRecalculateLockEvidenceAsync` ตัวเดียว ⇒ แก้ยอด/คำนวณใหม่/ยกเลิกถูกล็อกด้วยกัน · ทางปลด = ผู้ดูแลระบบยกเลิกรายการนำส่ง (ไม่มีปุ่ม — บอกตรง ๆ) · `WhtCertVoidGuard.CheckAsync` นับการนำส่ง ภ.ง.ด.1/3/53 ของงวดเป็นยื่นแล้ว (**มีผลกับทุกเส้นที่ยกเลิก 50 ทวิ** — หน้า 50 ทวิ · ยกเลิกเอกสาร · ยกเลิกการลงบัญชีรอบโอน · ออกใหม่ตอน re-post) · checker เดิมที่ห้ามคำว่า `StatutoryRemittances` ในตัวหาหลักฐานเปลี่ยนเป็นห้าม `"SsoSps110"` (เจตนาเดิม: นำส่ง สปส. ผูกกับรอบ) | `PayrollService.LoadRecalculateLockEvidenceAsync` · `PayrollRunEditPolicy.cs` · `WhtCertVoidGuard.cs` |
| **P1-b** | ✅ | ยืนยันที่ HEAD: โหลดรอบไม่ Include ⇒ แก้ `Include(r => r.Details)` · `IssueMonthlyPnd1CertsAsync` รอบมีพนักงานแต่ไม่มีแถวที่โหลด ⇒ throw (ตกไป catch เดิม: LogError + แจ้งเตือน) · เทสต์ที่ใช้ context ใหม่จริง: ทำไม่ได้ (เรพไม่มีเทสต์ที่มี DbContext) — ล็อกด้วย pure `DetailsNotLoaded` + required_call_site (Include ก่อนเรียก · ด่านก่อน WhtCertVoidGuard) | `PayrollService.GeneratePostPaymentArtifactsAsync` · `Helpers/PayrollPnd1Certs.cs` |
| **P1-c** | ✅ | `PayrollPnd1Certs.NextNumber`: ใบแรกรูปเดิม · ชนแล้ว `-2`, `-3` … · เลขที่ใช้แล้วค้นด้วย `IgnoreQueryFilters` (รวม Voided/ลบ) + tenant · ใช้ทั้งเส้นออกครั้งแรกและ re-post (เมธอดเดียว) · ออกใบในธุรกรรมของตัวเอง + `FOR UPDATE` แถวรอบ (ถ้าผู้เรียกไม่มีธุรกรรม) — ล้ม ⇒ ย้อนทั้งชุด | `PayrollService.IssueMonthlyPnd1CertsAsync` |
| **P2-d** | ✅ | `AllocatePayrollRunAsync`: ธุรกรรม + `FOR UPDATE` (CompanyId ใน SQL) ก่อนอ่านรอบ (สถานะอ่านใต้ล็อก) · commit หลัง save · webhook หลัง commit | `HrAllocationService.cs` (ไฟล์ทีมอื่น — แก้เล็กที่สุด) |
| **P2-e** | ✅ | ข้อความปันต้นทุนของ `CanVoid`: Paid ⇒ "กลับรายการจ่าย" ก่อน · อื่น ๆ ⇒ แก้ยอด | `PayrollRunEditPolicy.CanVoid` |
| **P2-f** | ✅ | ตัวอย่าง `SocialSecurityNumber` ในเทมเพลต = `1101700203450` (13 หลัก checksum ผ่าน) | `ImportExportService.cs` (ไฟล์ทีมอื่น · 1 บรรทัด) |
| **P2-g** | ✅ เทสต์ | `PayrollPnd1CertsTests.คำตัดสิน112_…` — `CanReopen(Paid)` ได้ · `CanEditAmounts(Approved, ยื่นแล้ว)` ไม่ได้ | — |

**ความเสี่ยงคอมไพล์**: `await using var certTx = cond ? await BeginTransactionAsync() : null;` (ชนิด `IDbContextTransaction?`) · `HashSet<string>` ส่งเข้า `IReadOnlySet<string>` ·
`filed.Add((form, …))` บน HashSet ของ tuple ที่ชื่อช่องต่างกัน (ชนิดตรง) · `ExecuteSqlRawAsync(sql, new object[] {…}, ct)` ใน HrAllocationService

**คำถามค้าง**: (1) ขยาย `WhtCertVoidGuard` ให้นับการนำส่ง ภ.ง.ด.3/53 ด้วย (ตามหลักเดียวกัน) — กระทบเส้นยกเลิกเอกสาร/รอบโอนของทีมอื่น ⇒ main agent ควรยืนยัน ·
(2) ยังไม่มีปุ่ม "ยกเลิกรายการนำส่ง" บนหน้าจอ — ทางปลดของแหล่งนี้ต้องผ่านผู้ดูแลระบบ · (3) ล็อกแถวรอบกันชนเลขภายในรอบเดียว — สองรอบของเดือนเดียวกันที่ออกใบพร้อมกัน
(รอบเก่าถูกยกเลิกแล้วโดยปกติ) ยังชน unique ได้ในทางทฤษฎี ⇒ ล้มดังผ่าน catch เดิม

# รอบ 201 ทีม PR2 — แก้ผลฝ่ายค้านรอบสาม (คอมมิต c5fefbc6 · merge 2c8e64d8) — แก้ที่ `9e51756e`

**ยังไม่ได้คอมไพล์/รันเทสต์ในเครื่องนี้** (ไม่มี .NET SDK) — CI บน `claude/**` คือ compiler ตัวแรก

| ID | สถานะ | สิ่งที่ทำ | ที่ |
| --- | --- | --- | --- |
| **P1-1** | ✅ | ยืนยันที่ HEAD: `WhtCertVoidGuard` นับการนำส่งทั้งงวด ขณะที่หน้านำส่งคิดยอดค้าง = ใบ − ยอดที่นำส่ง (`StatutoryRemittanceService` · ห้ามนำส่งงวดเดิมซ้ำ ⇒ ใบหลังนำส่งค้างตลอด) · แก้: `Helpers/RemittanceInclusion` (`Includes` · `CertCountedAt` · `LatestByPeriod` · `ThaiStamp`) — ใบ: `IssuedDate` (ทุกทางออกใบประทับ `UtcNow` ตอนออก) ไม่มี ⇒ `CreatedAt` · รอบ: `CreatedAt` · เวลานำส่ง = `StatutoryRemittance.CreatedAt` · ข้อความ "อยู่ในการนำส่ง ภ.ง.ด.x เดือน … ณ วันที่ dd/mm/พ.ศ. HH:mm น." แยกจากข้อความรายงานที่ประกาศว่ายื่น | `Helpers/RemittanceInclusion.cs` · `WhtCertVoidGuard.Reason` (8 อาร์กิวเมนต์) / `CheckAsync` · `PayrollService.LoadRecalculateLockEvidenceAsync` |
| **P2-1** | ✅ | อ่านเส้นหลัง rollback: ผู้เรียกตัวเดียว `GeneratePostPaymentArtifactsAsync` ใช้ `run` แบบอ่านอย่างเดียว (`AutoGenerateFilingsAsync` query ใหม่ · อีเมลส่งแค่ id) · เส้น inline ของ `ProcessPaymentAsync` หลัง dispatch อ่านค่า + เพิ่มการแจ้งเตือนใหม่เท่านั้น ⇒ `Clear()` ปลอดภัย · ทำเฉพาะเมื่อธุรกรรมเป็นของเมธอดเอง (`certTx != null`) — มีธุรกรรมของผู้เรียกอยู่ ⇒ ไม่แตะ context ของผู้เรียก | `IssueMonthlyPnd1CertsAsync` ทั้งสอง catch |
| **P2-5** | ✅ | `RemittanceForm("WhtPnd54") → WithholdingTax54` + query รวม `WhtPnd54` (กติกาเวลาเดียวกับ P1-1) | `WhtCertVoidGuard` |

**เทสต์**: `RemittanceInclusionTests` (11 เคส สองทิศ) · `PayrollPnd1CertsTests` +`WhtPnd54` · required_call_site +4 แถว + negative 12 กรณี (ถอยไปนับทั้งงวด · เวลาใบ/รอบเป็นค่าคงที่ ·
ตัด ภ.ง.ด.54 · ถอดตัวเทียบเวลาใน `Reason` · ถอดวันที่ในข้อความ · ถอด `Clear` ทีละ catch — ฟ้องครบ · ไฟล์จริงไม่ฟ้อง)

**ความเสี่ยงคอมไพล์**: `RemittanceInclusion.LatestByPeriod` อนุมาน `TKey` จาก tuple ซ้อน `((TaxType, int, int), DateTime)` / `((int, int), DateTime)` ·
`remittedAtUtc is not DateTime remittedAt` ในเงื่อนไข `||` แล้วใช้ต่อ (definite assignment) · `cond ? (DateTime?)x : null`

**คำถามค้าง**: (1) รอบเงินเดือนไม่มีเวลา "จ่าย" ของตัวเอง ⇒ ใช้เวลาสร้างรอบ: รอบที่สร้างก่อนนำส่งแต่จ่ายหลังนำส่งยังถูกล็อก (ทิศปลอดภัย · มีทางปลดผ่านผู้ดูแล) —
ถ้าต้องการแม่นกว่านี้ต้องเพิ่ม `PayrollRun.PaidAt` (+ migration · backfill จาก JE จ่าย) · (2) รายงานภาษีที่ "ประกาศว่ายื่น" ยังล็อกทั้งงวด (ไม่ได้อยู่ในขอบเขตข้อนี้) — ถ้าจะใช้กติกาเวลาเดียวกัน
ต้องตัดสินว่าเวลาไหนคือเวลายื่น (`FiledAt`/`FilingLockedAt`) · (3) ใบที่ออกหลังนำส่งยังนำส่งเพิ่มไม่ได้ทางหน้าจอ (ระบบห้ามนำส่งงวดเดิมซ้ำ) — ยอดค้างจะค้างบนหน้านำส่ง
