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
