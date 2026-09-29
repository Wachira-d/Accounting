# รอบ 200 — ปิดงานคงค้าง 10 กอง (ทีมผู้เชี่ยวชาญ 10 ทีม)

ผู้ใช้ (เจ้าของโปรเจกต์ · 2026-09-29): "ตั้งทีมที่เก่งที่สุด ทุกด้าน ทุกมุมมอง ไล่ตรวจสิบงานคงค้าง **ตัดสินใจ**ดำเนินการให้ดี ถูกต้อง
ครบถ้วนที่สุด" ⇒ เจ้าของ**มอบอำนาจตัดสิน**คำถามที่ค้างให้ main agent — คำตัดสินทั้งหมดอยู่ใน `DECISIONS.md` (ไฟล์ข้างกัน)
พร้อมเหตุผล · ทุกข้อต้อง "ย้อนได้/ตั้งค่าได้" และบันทึกไว้ให้เจ้าของทบทวน

## กติกาทุกทีม (ห้ามข้าม)
1. อ่าน `CLAUDE.md` (กฎเหล็ก #1–#4 · F2 หลักการ 10 ข้อ · F3 checklist) + `DECISION_DOCTRINE.md` §1–§3 ก่อนลงมือ
2. **verify ก่อนเชื่อ** — รายงานเก่าอาจล้าสมัย (หลายข้อแก้ไปแล้ว) · เปิดไฟล์จริงที่ HEAD · ถ้าแก้แล้ว = ติ๊ก `✅ <sha>` ในไฟล์รายงานต้นทาง ไม่ลบแถว
3. โค้ดเป็น ground truth · ห้ามเดาคำตัดสินนอก `DECISIONS.md` — ถ้าเจอทางแยกใหม่ที่ไม่มีในนั้น ให้เลือก**ทิศที่มองเห็นและย้อนได้**
   (บล็อกพร้อมทางไปต่อ/เตือน) แล้วเขียนเป็น "คำถามค้าง" ในรายงานทีม
4. ตรรกะเงิน/ภาษี/สิทธิ์ใหม่ = pure helper ใน `Helpers/` + เทสต์ xUnit **สองทิศ** (ใบพังกลับมาถูก + ใบถูกไม่ถูกแตะ) ในคอมมิตเดียว ·
   ด่านใหม่ที่ service ต้องเรียก → เพิ่มแถวใน `tools/required_call_site_check.py` (negative test ในตัว)
5. tenant `CompanyId == companyId` ทุก query · `Math.Round(..., MidpointRounding.AwayFromZero)` · ห้าม `catch {}` ในเส้นเงิน/สต็อก/JE ·
   audit ผ่าน `AddChainedAuditLog` · schema ผ่าน `DatabaseMigrationHelper` (ADD COLUMN IF NOT EXISTS) ห้าม EF Migrations ·
   HtmlEncode ทุกช่องที่ผู้ใช้/OCR คุมได้ · enum ออก API เป็นชื่อ · ห้ามใช้ optional argument ของ record/method ภายใน EF expression (CS0854)
6. ไม่มี .NET SDK ในเครื่อง ⇒ ระวังคอมไพล์เป็นพิเศษ: เปิดนิยามจริงของทุกสัญลักษณ์ที่เรียก (`python3 tools/callers.py <Symbol>`) ·
   ห้ามตัดบรรทัดกลางคอมเมนต์ `//` · ห้าม `"` ASCII ในสตริงไทย
7. `bash tools/check_all.sh` ต้องเขียวก่อน commit (ถ้าล้มเพราะงานทีมอื่น/ของเดิม ให้ระบุในรายงาน) · อัปเดต DOCUMENT_FLOW / TEST_PLAN
   (`python3 tools/test_inventory.py --row` วางทับ §0) / ACCOUNT_STRUCTURE / CHANGELOG ตามที่แตะ **ในคอมมิตเดียวกับโค้ด**
8. commit ใน worktree ของทีมเอง (ห้าม push · ห้ามแตะ branch อื่น) · ข้อความคอมมิตภาษาไทยตอบ F3 ข้อ 7–12 · ลงท้าย
   ```
   Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
   Claude-Session: https://claude.ai/code/session_01LNdHNkcNxcTEjiEWukA3FS
   ```
9. เขียนรายงานทีม `erp-review/2026-09-29/team-<ID>.md`: ตารางรายการ · สถานะ (✅ แก้ / NOT-A-BUG พร้อมหลักฐาน / 📋 backlog พร้อมเหตุผล) ·
   file:line · ชื่อเทสต์ · ความเสี่ยงคอมไพล์ที่เหลือ · คำถามค้าง

## 10 ทีม
| ทีม | ขอบเขต | แหล่ง |
|---|---|---|
| **V1** ยกเลิก-ออกแทน | S3-5 (ใบขายที่รอบโอน Posted รับชำระ ยกเลิก-ออกใหม่ไม่ได้) + ยกเลิกรับชำระ/เช็คเด้งที่ไม่ดู e-Tax (S4-8 ค้าง) + S3-7 | `settlement/review198-S3.md` · `review198-S4.md` · DECISIONS ข้อ 9, 11, 17 |
| **V2** ของกำพร้า | ของกำพร้า + ใบลดหนี้ที่ยกเลิกไม่ได้ ⇒ ช่องทางบล็อกถาวร (S3-6 ค้าง) · S3-8 · S3-11 · S4-5 | DECISIONS ข้อ 10 |
| **P2** settlement เฟส 2 | รายการ gateway (PaymentIntent) เข้ารอบโอน settlement ตัวเดียวกับ marketplace | `settlement/report-S1/S2.md` · DECISIONS ข้อ 12 |
| **W** ภ.ง.ด.54/DTA | ตาราง DTA · ภ.ง.ด.54 · WHT ค่าธรรมเนียมแพลตฟอร์มต่างประเทศ · R-A4 | `review198-A.md` R-A4 · DECISIONS ข้อ 13 |
| **I** ตัวอ่านไฟล์ | R-B7–R-B11 · R-A9 · S4-3 · S4-4 | `review198-B.md` · `review198-A.md` · `review198-S4.md` |
| **T** เวลา/ภาษีรอบโอน | R-A6 · R-A7 · R-A8 · R-A11 · R-A12 · R-B13 · C-11 · C-12 · C-13 · C-15–C-20 · O-2 (C-9) · O-3 (C-6) · E2-9 · E2-11 | `review198-A/B/C/E2.md` · DECISIONS ข้อ 15, 16, 20 |
| **G** gateway ที่ยังไม่ได้ตรวจ | `GatewaySettlementService` G-2/G-3/G-4 · `PaymentGatewayController` G-8 ทุก endpoint · หน้าเว็บ gateway 4 หน้า · E-3 · E-4 · E2-12 · คำถาม E2 | `review198-E.md` · `review198-E2.md` · DECISIONS ข้อ 18 |
| **S** แพ็กเกจ/สิทธิ์ | ความพร้อมก่อนเปิดบังคับแพ็กเกจ (FreeTrial · `Subscription:Enforcement:Mode` LogOnly ขัดกับสวิตช์แอดมิน · รายงานเงา) · D-P3 · D-P5 · D-P2 | `review198-D.md` · DECISIONS ข้อ 5, 14 |
| **K** OCR Makro | K-3b · K-4 · K-5 · K-8 · K-9 · K-10 · K-11 + ค้างใน `review-r199-ocr.md` | `makro-branch/review197.md` · DECISIONS ข้อ 19 |
| **R** กวาดค้างรอบ 189/193/194 | ทุกแถว P0/P1 ที่ยังไม่ติ๊กใน `erp-review/2026-09-21/report-*.md` · `2026-09-24/DECISIONS.md` (37 ข้อ) · `2026-09-24/review193-*.md` · `2026-09-25/review194-*.md` · `legal-L1/L2` · `plan-deposit-kind.md` | ไฟล์ตามคอลัมน์ซ้าย |

## ขอบเขตไฟล์ (ลด merge conflict)
- V1: `DocumentService` (Void*) · `ChequeService` · `DocumentVoidPreconditions` · `SettlementUnpostGate` (เฉพาะ S3-7)
- V2: `SettlementPostingService` (orphan/unpost) · `SettlementOrphanTriage` · `SettlementPostingGate`
- P2: ไฟล์ใหม่ใน `Services/Settlement/` + จุดต่อใน `SettlementImportService` · ห้ามเขียนตรรกะ orphan/unpost
- T: `SettlementBatchMath` · `SettlementImportService.Lines` · เส้นสร้างใบสรุป
- I: ตัวอ่านไฟล์ (`GenericColumnMapAdapter` ฯลฯ) · `SettlementTxnKey`
- ถ้าต้องแตะไฟล์ของทีมอื่น: แก้ให้เล็กที่สุด และระบุในรายงาน
