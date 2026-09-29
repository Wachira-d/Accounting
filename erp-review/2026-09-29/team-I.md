# รอบ 200 ทีม I — ตัวอ่านไฟล์ settlement + คีย์กันซ้ำ

ขอบเขต (BRIEF): R-B7–R-B11 (`review198-B.md`) · R-A9 (`review198-A.md`) · S4-3 · S4-4 (`review198-S4.md`) · ไฟล์ = ตัวอ่าน (`Services/Settlement/Adapters/**`)
+ `Helpers/SettlementTxnKey` + การนำเข้า (`SettlementImportService`) · **ยังไม่ได้คอมไพล์** (env ไม่มี .NET SDK) · commit `b7cd77eb` (ไม่ push)

ทุกข้อ verify ที่ HEAD `5c1fe028` ก่อนแก้ · ทุกตัวตัดสินเป็น pure helper + เทสต์สองทิศ (`Accounting.Tests/SettlementReview200ReaderTests.cs`)

## ตารางรายการ

| ID | verify ที่ HEAD | สถานะ | แก้ที่ (file:line) | เทสต์ |
|---|---|---|---|---|
| **R-B7** xlsx เลขยาว | จริง — `SettlementFileReader.CellText` พิมพ์ double `"R"` ⇒ `1.2345678901234568E+17` เข้าคีย์/ExternalOrderId ตรง ๆ | ✅ แก้ | `SettlementValueParser.IdLostPrecision` (`Adapters/SettlementValueParser.cs:117`) · `CellText` decimal/long ≥ 1e15 คง scientific (`SettlementFileReader.cs:207`) · ด่าน `EnsureIdIntact` ทุกช่องเลขอ้างอิง (txn · order · payout) ⇒ `SettlementFormatException("id-precision")` ข้อความไทย + ทางไปต่อ (`GenericColumnMapAdapter.cs:174`) | `RB7_*` 4 ชุด (xlsx ตัวเลข ↔ ข้อความ · CSV `1.23457E+17` ↔ 18 หลัก · "5E10" ไม่ถูกจับ) |
| **R-B8** เขตเวลา | จริง — เวลาไม่มี offset ถือเป็นไทยเสมอ · NumericDate ทิ้งเวลา | ✅ แก้ | `SettlementFileTimeZone {Auto, Bangkok, Utc}` + `SettlementColumnMap.TimeZone` (`SettlementColumnMap.cs:67`) · `TryParseDate(raw, order, zone)` อ่านเวลา 24 ชม./AM-PM/"น."/Excel serial เศษวัน + offset ในค่า (Z · UTC · GMT · +07:00 · +0700 · GMT+7) · `DependsOnZone` (`:218`) · ตัวตัดสิน `SettlementFileDecisions.DecideTimeZone` (`SettlementFileDecisions.cs:100`): ค่าตั้ง → หัวคอลัมน์ (`TimeZoneFromHeader :75` · เขตอื่น ⇒ `timezone-unsupported`) → ไม่มีแถวที่ข้ามวัน = ไทย (ไม่มีผล · ไม่จำ) → **ล้มดัง `timezone-ambiguous`** · หน้าจับคู่คอลัมน์มีช่อง "เขตเวลาในไฟล์" จาก `reference.timeZones` | `RB8_*` 8 ชุด (UTC ย้ายวัน · offset ชนะค่าตั้ง · หัวคอลัมน์ 8+5 แบบสองทิศ · GMT+8 ล้ม · ไฟล์จริง 4 ฉาก) |
| **R-B9** วัน/เดือนกำกวม | จริง — `DetectDateOrder` ไม่มีหลักฐานคืน วัน/เดือน เงียบ · ไม่เคยจำ | ✅ แก้ | `DetectDateOrder` ไม่มีหลักฐาน = `Auto` (`SettlementValueParser.cs:168`) · `IsOrderSensitive` · `DecideDateOrder` (`SettlementFileDecisions.cs:32`): ค่าที่จำ/เลือก → วันที่ > 12 → ไม่มีวันที่ใดขึ้นกับลำดับ (วัน/เดือน · ไม่มีผล) → **ช่วงวันที่ของรอบโอนในหัวรอบโอน** รับได้แบบเดียว (`SettlementParseContext` · `SettlementImportService.cs:109`) → **ล้มดัง `date-order-ambiguous`** แสดงสองความหมาย · สิ่งที่ไฟล์พิสูจน์ได้คืนใน `SettlementParseResult.LearnedDateOrder/LearnedTimeZone` → `SettlementColumnMap.Learn` (`:135` · เฉพาะช่องที่ยัง Auto · ด่านสิทธิ์จำเดิมของ controller · แจ้งผู้ใช้ทุกครั้ง) | `RB9_*` 4 ชุด + ปรับ `SettlementImportTests` 2 จุดที่สัญญาเปลี่ยนโดยตั้งใจ |
| **R-B10** วงเล็บ+ลบ | จริง — `"(-100)"` = +100 (ลบสองชั้นหักล้าง) · `"1,500"` ในไฟล์ `;` = 1500 เงียบ | ✅ แก้ | `TryParseAmount` (`:47`): เครื่องหมายสองชั้น `(-100)` · `-100-` · `(+100)` = อ่านไม่ได้ · ขีดลบยูนิโค้ด ‐ ‑ ‒ – − ﹣ － · หน่วยเงินนอกวงเล็บ · `SettlementFileReader.Open(...).CsvDelimiter` + `DecimalCommaAmbiguity` (`SettlementFileDecisions.cs:140`) ⇒ `;` + "1,500" ไม่มีทศนิยมและไม่มียอดพิสูจน์รูปแบบ ⇒ **ล้มดัง `decimal-comma`** | `RB10_*` 3 ชุด (6 รูปที่ต้องปฏิเสธ · 11 รูปที่ต้องอ่านถูก · `;` ↔ `,`) |
| **R-B11** แถวสรุปแบบกว้าง | จริง — จับเฉพาะช่องแรกขึ้นต้น "รวม/Total" | ✅ แก้ | `SettlementFileDecisions.IsSummaryRow` (`:126`): แบบกว้าง = ไม่มีทั้งเลขออเดอร์และเลขรายการ ⇒ ข้าม (ช่องแรกว่าง/"ยอดสุทธิ" ก็ข้าม) · แบบยาว = ไม่มีเลข + (คำสรุปที่ขยาย: ยอดสุทธิ/สรุป/Net total/Summary หรือไม่มีทั้งป้ายและวันที่ — เดิมล้มทั้งไฟล์ `type-missing`) · ข้อความแจ้งบอกยอดของแถวที่ข้าม (`SummaryNotice`) · ตัดแถวสรุป**ก่อน**ตัดสินวันที่ | `RB11_*` 3 ชุด (xlsx แถวรวมท้ายไฟล์ได้ผลเท่าไฟล์ไม่มีแถวรวม · แถวค่าธรรมเนียมไม่มีเลขยังนำเข้า) |
| R-B11 (P3) เพดานแถว | แก้แล้วก่อนรอบนี้ | ✅ `266acad2` | `SettlementFileReader.MaxRows = 100_000` · `MaxColumns = 500` · `MaxFileBytes 25 MB` + ข้อความไทย (review198-D D-P4) | `SettlementReview198DTests` (เดิม) |
| **R-A9** unique index vs รูปข้อมูล | แก้แล้วก่อนรอบนี้ — คีย์ v2 (`60db75ee`) + ยกเลิก = soft-delete (`71c8fdeb` · `VoidBatchAsync`) · 23505 ⇒ ข้อความไทย | ✅ ยืนยัน + เทสต์รูปข้อมูล | ไม่แก้โค้ด | `RA9_ออเดอร์เดียวหลายคอลัมน์ค่าธรรมเนียม_…` (ค่าคอม −53.50 กับค่าธรรมเนียม −53.50 ของออเดอร์เดียว ไม่ชน · คืนเงินพก id เดิม) |
| **S4-3** multiset กลืนแถวจริงในรอบเดียว | จริง — `StoredRowContentAsync` คืนบรรทัดทุกไฟล์ของรอบ | ✅ แก้ + migration | `SettlementLine.ImportScope` (`Models/Entities/Settlement.cs:115` · `AccountingDbContext` MaxLength 64 · `DatabaseMigrationHelper.cs:214` `ADD COLUMN IF NOT EXISTS`) · `SettlementTxnKey.ImportScopeOf` (`:160` — ค่าเดียวกับในคีย์ `v2:rowc:`) · `SplitRevisedFilePool` (`:172`) — เทียบเนื้อหาเฉพาะ "ไฟล์รุ่นก่อนของไฟล์นี้" (ทุกบรรทัดของไฟล์นั้นมีแถวเดียวกันในไฟล์ใหม่ · นับจำนวน) · อีกไฟล์ ⇒ เพิ่ม + เตือนรายแถว (`SettlementImportService.cs:320`) · บรรทัดเดิม (NULL) = พฤติกรรม S3-4 | `S43_*` 2 ชุด (ไฟล์ส่วนที่เหลือไม่ถูกกลืน ↔ ไฟล์ฉบับแก้ยังข้ามแถวเดิม · นับจำนวน · NULL = เดิม · scope ไม่ขึ้นกับลำดับแถว) |
| **S4-4** เตือนครั้งเดียวตอนนำเข้า | จริง — `ContentOverlapElsewhereAsync` มีแค่ใน `PersistAsync` | ✅ แก้ (เตือน · ไม่บล็อก) | `Helpers/SettlementContentOverlap` (`Find :32` · `LoadOtherBatchesAsync :54` · `ForBatchAsync :68` · `Annotate :82`) = ข้อเท็จจริงตัวเดียวของผู้นำเข้า + ผู้ลงบัญชี · `SettlementPostingService.BuildGateAsync` เติมคำเตือน `SettlementPlanIssueCode.ContentOverlapElsewhere = 59` หลัง `Evaluate` (`SettlementPostingService.cs:549`) ⇒ พรีวิว/ลงบัญชีทุกครั้ง คิดจากข้อมูลปัจจุบัน · `CanPost` ไม่เปลี่ยน | `S44_*` 2 ชุด |

## ลายนิ้วมือของการแก้ (F3 ข้อ 7–10)

- **grep รูปแบบเดิม**: `SettlementValueParser.DetectDateOrder(` ถูกเรียกจาก service/adapter **0 จุด** (เหลือใน `SettlementFileDecisions` + เทสต์ · `required_call_site_check` ห้ามใน `Parse`) ·
  `TotalRow` เดิมใน adapter ถูกถอด (0 จุด) · `TryParseDate(` ผู้เรียกนอก parser = adapter 1 + decisions 1 · `ISettlementReportAdapter.Parse` มี implementation เดียว
  (`GenericColumnMapAdapter` — `PaymentIntentAdapter` ไม่ใช่ adapter ไฟล์) · `new SettlementParseResult(` 1 จุด · `new SettlementReferenceData` 1 จุด
- **ทางเข้าอื่นที่แตะข้อมูลชุดเดียวกัน**: บรรทัด settlement เกิดจาก 2 ทางเข้า — ไฟล์ (ทุกด่านข้างบน) และ PaymentIntent (ไม่มีไฟล์ ⇒ `ImportScope` NULL · คีย์ `pi:` ไม่ถูกเทียบเนื้อหา) ·
  หน้าตรวจไฟล์ (`InspectFileAsync`/`Peek`) อ่านแค่หัว+5 แถว ไม่ตัดสินวันที่ — ความกำกวมโผล่ตอนนำเข้า (ล้มทั้งไฟล์ ไม่มีอะไรถูกบันทึก)
- **เข้มขึ้น — ผู้ใช้ทำอะไรแทน**: `id-precision` ⇒ ส่งออก CSV ตรง/จัดคอลัมน์เป็นข้อความ · `date-order-ambiguous` ⇒ เลือกรูปแบบวันที่ (จำให้ช่องทาง) **หรือ** กรอกช่วงวันที่ของรอบโอน ·
  `timezone-ambiguous` ⇒ เลือกเขตเวลา (จำให้ช่องทาง) · `timezone-unsupported` ⇒ ส่งออกเป็นเวลาไทย · `decimal-comma` ⇒ ส่งออกใหม่ใช้จุดทศนิยม/บันทึกเป็น .xlsx ·
  เทสต์ทิศตรงข้าม: `RB7_ทิศตรงข้าม_*` · `RB8_ทิศตรงข้าม_*` + ไฟล์เช้า · `RB9_…ช่องทางที่จำแล้ว_ผลเดิม` · `RB10_ทิศตรงข้าม_*` · `RB11_…แถวออเดอร์ได้ผลเดิม` · `S43_…ไฟล์ฉบับแก้ยังข้ามแถวเดิม`
- **ค่าที่ persist ไว้ก่อนแก้**: บรรทัดเดิมที่นำเข้าวันที่ด้วยการเดา วัน/เดือน หรือเวลา UTC ที่ถูกอ่านเป็นไทย **คำนวณย้อนไม่ได้** (ไม่ได้เก็บค่าดิบ — ไฟล์ต้นฉบับอยู่ในไฟล์แนบของรอบ) ⇒
  ไม่มี migration แก้ค่า · ทางไปต่อ = ยกเลิกรอบที่ยังไม่ลงบัญชีแล้วนำเข้าใหม่ · `ColumnMapJson` เดิมไม่มี `timeZone` ⇒ อ่านเป็น Auto (เทสต์ล็อก) · `ImportScope` เดิม NULL ⇒ พฤติกรรมเดิม
- **ความเปลี่ยนแปลงที่ผู้ใช้เห็น (ตั้งใจ)**: ช่องทางที่จำการจับคู่ไว้แบบ `dateOrder: Auto` แล้วนำเข้าไฟล์ที่วันที่ทุกแถว ≤ 12 **โดยไม่กรอกช่วงวันที่** จะถูกถามครั้งเดียว (เดิมเดา วัน/เดือน) ·
  ไฟล์ที่มีเวลาหลัง 17:00 และหัวคอลัมน์ไม่บอกเขต จะถูกถามครั้งเดียว (เดิมถือเป็นไทยเงียบ)

## ขอบเขตไฟล์ของทีมอื่นที่แตะ (เล็กที่สุด)

- `Helpers/SettlementBatchMath.cs` (ทีม T) — **เพิ่ม enum 1 ค่า** `ContentOverlapElsewhere = 59` (เว้น 55–58 ให้ทีมอื่นกันชนเลข) ต่อท้ายหมวด "แจ้งให้ทราบ"
- `Services/Settlement/SettlementPostingService.cs` (ทีม V2) — **3 บรรทัด** หลัง `SettlementPostingGate.Evaluate` ใน `BuildGateAsync` · ไม่แตะ `SettlementPostingFacts`/`Evaluate`/orphan/unpost
- `Helpers/SettlementReferenceCatalog.cs` — พารามิเตอร์ `TimeZones` ต่อท้าย record + ตัวเลือก 3 ค่า · `wwwroot/pages/settlements.html` — select "เขตเวลาในไฟล์" 1 ช่อง + `readMap`

## ตัวตรวจ

`bash tools/check_all.sh` ✅ ผ่านทั้งหมด (checker 53 ตัว · simulation 8 · brace/node/U+FFFD 26 ไฟล์ · TEST_PLAN §0 · ไม่มี dotnet SDK = ยังไม่ได้คอมไพล์) · `required_call_site_check` +7 แถว (adapter `Parse`/`EnsureIdIntact` ·
`ImportFileAsync` · `PersistAsync` S4-3 · `ContentOverlapElsewhereAsync` · `BuildGateAsync` S4-4) + ย้ายแถวเดิมของ `ContentOverlapElsewhereAsync` (IsRowKey/claimed/tenant) ไปที่
`SettlementContentOverlap.Find/LoadOtherBatchesAsync` (ตรรกะย้ายไปที่นั่น) · `test_inventory --check` ✅ (364 ไฟล์ · 3,480 Fact + 530 Theory)

## ความเสี่ยงคอมไพล์ที่เหลือ (ไม่มี SDK)

1. `SettlementContentOverlap.Find<T>` / `DecimalCommaAmbiguity` รับ `IReadOnlyList<(T Id, string? ContentKey)>` / `IReadOnlyList<(int RowNo, string Header, string? Raw)>` แต่ผู้เรียกส่ง `List<(int SourceRow, string?)>` /
   `List<(int RowNo, string h, string?)>` — ชื่อ tuple ต่างกันเป็น identity conversion (อนุมาน `T` ได้) · ถ้า compiler ไม่ยอม: ตั้งชื่อ element ให้ตรง
2. `private readonly record struct DateParts` + `with` บน `out` parameter ใน `WithTail` · pattern `day is not DateTime d0` ใน `||` ของ `Resolve` (definite assignment)
3. `CellText` switch arm `decimal m when …` ก่อน `decimal m => …` · `long l when …` (ชื่อ pattern variable ซ้ำคนละ arm)
4. `SettlementContentOverlap.LoadOtherBatchesAsync` projection `new SettlementContentLine(... l.Batch.PayoutRef)` (record constructor ใน Select สุดท้าย — client projection) ·
   `(excludeBatchId == null || l.BatchId != excludeBatchId)` Guid กับ Guid? (รูปเดียวกับโค้ดเดิม)
5. `char.IsAsciiDigit` (.NET 7+) ใน `TryParseOffset` · MiniExcel อ่านสตริงตัวเลข 18 หลักกลับเป็น string (เทสต์ `RB7_xlsx…เป็นข้อความ` พึ่งพฤติกรรมนี้ — ถ้า MiniExcel แปลงเป็นตัวเลขเอง เทสต์จะล้มที่ `id-precision` ⇒ ต้องเขียน xlsx ด้วยค่าที่มีตัวอักษรนำ)
6. ข้อความ `{total:N2}` ในข้อความแจ้งแถวสรุปขึ้นกับ culture ของเครื่องเทสต์ (เทสต์คาด "1,521.50" — เหมือนข้อความ N2 เดิมทั้งไฟล์)

## คำถามค้าง (ถึงเจ้าของ)

1. **S4-4 ควรเป็นด่านที่ต้อง "รับทราบรายแถว" ไหม** — วันนี้เป็นคำเตือนไม่บล็อกที่พรีวิว/ลงบัญชีทุกครั้ง (ทิศที่มองเห็น/ย้อนได้ตาม BRIEF ข้อ 3) · บล็อกโดยไม่มีปุ่มรับทราบ =
   ทางตันสำหรับรายการจริงที่หน้าตาเหมือนกัน (ค่าธรรมเนียมถอนเงินสองรอบในวันเดียว · R-B5) · ถ้าต้องการ: endpoint รับทราบ + ธงต่อบรรทัด + UI (งานฟีเจอร์)
2. **S4-3 ไฟล์ฉบับแก้ที่ "ลบ" แถวเดิมออก** (แพลตฟอร์มแก้รายงานย้อนหลัง) ไม่ถือเป็นฉบับแก้ ⇒ แถวที่เหลือเข้าซ้ำ + เตือน (เดิมกลืนเงียบ) — ทิศนี้มองเห็นได้ (บรรทัดซ้ำในรอบ + สมการไม่ลงตัว)
   แต่ต้องยกเลิกรอบแล้วนำเข้าใหม่ · ถ้าอยากให้ "ทับไส้ในด้วยไฟล์ล่าสุด" = ฟีเจอร์แทนที่รอบ (ยังไม่มี)
3. ตัวอักษร **"ลบ" นำหน้ายอด** (เช่น "ลบ 100") ยังอ่านไม่ได้ (ล้มดังทั้งไฟล์) — ไม่พบในไฟล์จริงใด จึงไม่เดาความหมาย · ถ้ามีไฟล์จริงใช้รูปนี้ ส่งตัวอย่างมาแล้วค่อยเพิ่ม
4. CSV ที่ Excel บันทึกเลขยาวเป็น "123456789012345000" (เติม 0 ท้าย · จัดรูปแบบเป็นตัวเลข 0 ทศนิยม) ตรวจไม่ได้จากค่าเดียว — เลขที่ลงท้าย 000 อาจถูกจริง · ต้องมีไฟล์จริงของเจ้านั้นก่อน
   (DECISIONS ข้อ 12)
