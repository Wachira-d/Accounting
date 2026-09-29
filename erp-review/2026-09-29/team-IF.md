# รอบ 200 ทีม IF — แก้ผลฝ่ายค้านทีม I (ตัวอ่านไฟล์ settlement)

แหล่ง: `review200-I.md` (CONFIRMED I-1…I-6 · PLAUSIBLE I-7…I-11) · `DECISIONS.md` ข้อ 12 (ห้าม adapter เฉพาะเจ้า) · ข้อ 39 (แถวไม่มีเลขในไฟล์แบบกว้าง) ·
**ยังไม่ได้คอมไพล์** (env ไม่มี .NET SDK) · commit `<pending>` (ไม่ push) · ไม่แตะเส้นลงบัญชี/ModeMismatch (ทีม SF) และ `ConfirmedRangeUtc` (ทีม GF)

ทุกข้อ verify ที่ HEAD `c26138d8` ก่อนแก้ · ตัวตัดสินใหม่เป็น pure helper · เทสต์สองทิศใน `Accounting.Tests/SettlementReview200IfTests.cs`

## ตารางรายการ

| ID | verify | สถานะ | แก้ที่ | เทสต์ |
|---|---|---|---|---|
| **I-1** (P2) คีย์กันซ้ำ drift เมื่อการอ่านเขตเวลาเปลี่ยน | จริง — `Assign`/`LegacyKeys` ใส่วันที่ที่อ่าน**วันนี้** ⇒ หัว "(UTC)"/ค่า "… UTC" ให้วันที่ใหม่ ⇒ ไม่มีคีย์ใดตรงบรรทัดก่อน deploy | ✅ | `SettlementValueParser.TryParseLiteralDate` (ISO+offset แปลงเป็นวันไทย · รูปอื่นทิ้งส่วนท้าย = ตัวอ่านเดิมทุกตัวอักษร) · `SettlementFileDecisions.LegacyReadOrders` (ลำดับที่ตัดสินวันนี้ + วัน/เดือน เมื่อไฟล์ไม่มีหลักฐาน = สิ่งที่ตัวอ่านเดิมเดา) · `SettlementParsedRow.LiteralDates` · `SettlementTxnKey.LegacyKeys(rows, typed, literalDateSets)` คิด v1 · v2-เลขพิมพ์ · **กติกาปัจจุบัน** ซ้ำด้วยวันที่ตามตัวอักษร (ลายนิ้วมือไฟล์ของ `v2:rowc:` คิดใหม่ทั้งชุดด้วย) · `PersistAsync` ส่ง `LiteralDateSets(rows)` · ข้อความ `SETTLEMENT-BATCH-POSTED` บอกเหตุจริง (`PostedBatchNewRowsMessage` + `SettlementTxnKey.SharesRawIdWith`: เลขรายการเดิมแต่เนื้อหาต่าง ⇒ ชี้ "รูปแบบวันที่/เขตเวลาในไฟล์" · เลขใหม่จริง ⇒ "ไม่เคยอยู่ในรอบนี้") · รอบที่ยังแก้ได้ ⇒ เพิ่ม + คำเตือนรายแถว (ไม่กลืน — คืนเงินครั้งที่สองของ id เดิมเป็นรายการจริงได้ R-B5) | `I1_วันที่ตามตัวอักษร…` · `I1_ทิศตรงข้าม_ค่าที่ตัวอ่านเดิมอ่านเหมือนวันนี้…` · `I1_ไฟล์หัวUTCที่นำเข้าก่อนdeploy…` (แถวมี id + ไม่มี id) · `I1_ทิศตรงข้าม_รายการจริงคนละวัน…` · `I1_ไฟล์ไม่มีหลักฐานลำดับวันเดือน…` · `I1_เลขรายการเดียวกับบรรทัดเดิม…` |
| **I-2** (P2) "ระบบจำไว้ให้" ทั้งที่จำไม่ได้ · ถามทีละเรื่อง | จริง — `SettlementFileDecisions.cs:58,110` · `DecideDateOrder` โยนก่อนถึง `DecideTimeZone` | ✅ | `SettlementFileDecisions.DecideDates` ถามทั้งสองเรื่องในรอบเดียว (`date-order-timezone-ambiguous` · เรื่องเดียวคงรหัสเดิม) · `MemoryClause` บอกตามจริง ← `SettlementParseContext(…, WillRemember, NotRememberedReason)` ← `ImportFileAsync(..., memoryBlockedReason, ct)` ← controller `SettlementPermissionScope.ColumnMapMemoryBlocker(isApiKey, canChannels)` (ไม่ขึ้นกับการติ๊ก — หน้าเว็บซ่อนช่องติ๊กของผู้ไม่มีสิทธิ์ · `ColumnMapMemory` ตัดสินด้วยตัวเดียวกัน) · ผลบนหน้าจอเดิม (select 2 ช่องมีอยู่แล้ว) | `I2_ข้อความจำ_ตามสิทธิ์จริง` · `I2_ไฟล์กำกวมทั้งลำดับวันเดือนและเขตเวลา…` |
| **I-3** (P3) "จำแล้ว" บนเส้น rollback | จริง — `warnings.AddRange(toSave.Learn(...))` ก่อน `PersistAsync` | ✅ | `PersistInput.LearnNotes` · เติมเฉพาะในบล็อก `if (input.RememberMapJson != null)` ที่เขียน `ColumnMapJson` (หลังเส้น rollback) · `required_call_site_check`: `forbid warnings.AddRange(toSave.Learn(` + `before RollbackAsync → input.LearnNotes` | call-site (service ไม่มีเทสต์ DbContext) · SPIF-01 |
| **I-4** (P3) ยอดแถวสรุปไม่ดู Negate/VAT/ยอดออก | จริง — ข้อมูลเทสต์ทีม I เอง 1605/−53.5/30 แสดง 1,581.50 | ✅ | `LongAmount`/`WideAmounts` = ตัวเดียวของแถวจริงและ `SummaryNotice` (อ่านแบบไม่ล้มสำหรับแถวที่ไม่นำเข้า) · ตัวเลขใช้ `InvariantCulture` | `I4_แถวสรุปแบบยาวยอดเข้าออก…` (950.00 ไม่ใช่ 1,000.00) · `RB11_xlsx…` (1,521.50) |
| **I-5** (P3) CSV `;` ยอดบาทเต็ม | จริง — ทางไปต่อแรก "ส่งออกให้ใช้จุดทศนิยม" ทำไม่ได้ | ✅ | `SettlementColumnMap.CommaIsThousands` (ผู้ใช้ติ๊กเท่านั้น · ระบบไม่เดา/ไม่จำเอง) + ช่องติ๊ก "จุลภาคในยอด = คั่นหลักพัน" ใน `settlements.html` (hydrate + `readMap`) · ข้อความ `decimal-comma` ทางหลัก = ติ๊ก / .xlsx + `MemoryClause` | `I5_CSVอัฒภาคยอดบาทเต็ม…` (ทั้งสองทิศ + JSON เดิม = ไม่ยืนยัน) |
| **I-6** (P3) คอลัมน์เลขออเดอร์ว่างทั้งไฟล์ | จริง | ✅ | `SettlementFileDecisions.WideIdColumnsEmpty` ⇒ `id-column-empty` ชี้คอลัมน์เลขออเดอร์/เลขรายการ (ตรวจก่อนตัดแถวสรุป) · ข้อความ `no-rows` แนบเหตุของแถวสรุปที่ข้าม | `I6_ไฟล์แบบกว้างคอลัมน์เลขออเดอร์ว่างทุกแถว…` |
| **I-7** (P2) → **DECISIONS ข้อ 39** | PLAUSIBLE → เจ้าของตัดสินแล้ว | ✅ | `IsSummaryRow(layout, txn, order, label, date, first, totalsIdRows)`: มีเลข ⇒ ไม่ใช่ · คำสรุป ⇒ ใช่ · แบบกว้าง: ไม่มีวันที่ หรือ `TotalsIdRows` (ยอดทุกคอลัมน์ = ผลรวมของแถวที่มีเลข · ต้องมีแถวที่มีเลข ≥ 2 กันแถวสินค้าแถวที่สองที่ยอดเท่ากัน) ⇒ ใช่ · แถวไม่มีเลขที่เหลือ = รายการจริง นำเข้า + คำเตือนรายแถว (เลขแถว + ยอดสุทธิรวม + ทางไปต่อ) · แถวสรุปที่ข้าม ⇒ `SettlementParseResult.Warnings` (แถบเตือน · ไม่ใช่บรรทัดเทา) | `D39_แถวไม่มีเลขที่มีวันที่…` · `D39_แถวรวมที่ยอดเท่าผลรวม…` · ปรับ `RB11_*` + `SettlementImportTests.CSVแบบยาว…` |
| **I-8** (P3) กลุ่มเล็กถูกครอบโดยบังเอิญ | จริงตามฉากในรายงาน — และ**หักจำนวนอย่างเดียวไม่ปิดฉากนั้น** (กลุ่ม S1 ไม่ถูกครอบจึงไม่กินจำนวน · กลุ่ม {−10} ยังถูกครอบ) | ✅ | `SplitRevisedFilePool`: หักจำนวนระหว่างกลุ่ม (ใหญ่ก่อน · เท่ากันเรียงตามลายนิ้วมือ ⇒ ไม่ขึ้นกับลำดับ) **+** `SettlementContentPool.RevisedScope` ⇒ `PersistAsync` เก็บ `ImportScope` ของบรรทัดที่เติมจากไฟล์ฉบับแก้เป็นลายนิ้วมือของไฟล์รุ่นก่อน (กลุ่มโตตามไฟล์ล่าสุด · ไม่เกิดกลุ่มเล็ก) — ข้อเสนอทางที่สองของฝ่ายค้าน | `I8_บรรทัดที่เติมจากไฟล์ฉบับแก้สืบลายนิ้วมือ…` (มีทิศ "บั๊กเดิม" ให้เห็นว่าทำไมต้องสืบ) · `I8_หักจำนวนระหว่างกลุ่ม…` |
| **I-9** (P3) ส่วนท้ายเวลา | จริง | ✅ (บางส่วนตามทิศปลอดภัย) | `TimeTail`: `ICT` (+7) · วงเล็บ `(GMT+07:00)` / `(UTC)` · "น" ไม่มีจุด · `13:05 PM` = 24 ชม. ตามตัวเลข · `24:00(:00)` = สิ้นวันที่เขียน (ไทยไม่มี offset ⇒ วันที่เขียน · UTC ⇒ +24 ชม.) · **ไม่ทำ** "UnreadTime กำกวมเฉพาะเมื่อเลือก Utc" — เท่ากับเดาว่าเป็นเวลาไทยเงียบ ๆ (DOCTRINE §1) · เวลาที่ยังอ่านไม่ออกจึงยังถาม | `I9_ส่วนท้ายเวลาที่ตีความได้ชัด_อ่านได้` (8) · `I9_ทิศตรงข้าม…` (4) · `I9_ค่าที่บอกเขตเวลาแล้ว_ไม่ถูกถามเขตเวลา` |
| **I-10** (P3) ค่าที่จำขัดกับไฟล์ | จริง | ✅ | `DecideDates` ⇒ `date-order-conflict` "ตั้ง/จำ X ไว้ แต่ไฟล์เป็น Y · แถว n" + ทางล้าง "อัตโนมัติ" · **เขตเวลาไม่ทำ conflict**: หัวคอลัมน์เป็นแค่คำประกาศ — บล็อกแล้วผู้ใช้ที่รู้ว่าหัวผิดไม่มีทางไปต่อ (ทางตัน) ⇒ ค่าที่ตั้งยังชนะ (พฤติกรรมเดิม · เทสต์เดิม `RB8_ตัดสินเขตเวลา_ค่าตั้งชนะ…` คงอยู่) | `I10_…` |
| **I-11** (P3) โหลดทั้งวัน + เตือนสมมาตร | จริง | ✅ | `LoadOtherBatchesAsync` กรอง `ExternalTxnId.StartsWith(RowKeyPrefix/LegacyRowKeyPrefix)` ใน SQL (ค่าคงที่ ⇒ `LIKE 'v2:row%'` ใช้ prefix ของดัชนี `(CompanyId, ChannelId, ExternalTxnId)` ได้) + ดึง `l.Batch.Status` · `SettlementContentHit.PostedPayoutRefs` · `RefsWithStatus` "(ลงบัญชีแล้ว)" · `WhatToDo` ตัวเดียวของพรีวิว (`Annotate`) และตอนนำเข้า (`ContentOverlapElsewhereAsync`) — มีรอบที่ลงแล้ว ⇒ "ห้ามยกเลิก/กลับรายการรอบที่ลงบัญชีแล้ว · ยกเลิกรอบนี้ (ที่ยังไม่ลง)" · ไม่มี ⇒ "ยกเลิกรอบที่นำเข้าทีหลัง" | `I11_คำเตือนบอกรอบที่ลงบัญชีแล้ว…` · `I11_คำนำหน้าคีย์ในSQL_ชุดเดียวกับIsRowKey` (6) |

## ลายนิ้วมือของการแก้ (F3 ข้อ 7–10)

- **grep รูปแบบเดิม**: `SettlementFileDecisions.DecideDateOrder(`/`DecideTimeZone(` ใน adapter = **0** (ห้ามด้วย `forbid` — ผ่าน `DecideDates` ตัวเดียว · สองตัวเดิมคงไว้เป็น wrapper ที่เทสต์เดิมใช้) ·
  `warnings.AddRange(toSave.Learn(` = 0 · ผู้เรียก `SettlementTxnKey.LegacyKeys(` ในโค้ดจริง = 1 (`PersistAsync` — ส่งชุดวันที่แล้ว) · `IsSummaryRow(` = 1 (adapter) · `SummaryNotice` คิดยอดเองแบบค่าดิบ = 0 ·
  `ImportFileAsync(` ผู้เรียก = 1 (controller · อัปเดตลายเซ็นแล้ว) · `new SettlementParseContext(` โค้ดจริง = 1
- **ทางเข้าอื่นที่แตะข้อมูลชุดเดียวกัน**: บรรทัด settlement เกิดจาก 2 ทาง — ไฟล์ (ทุกข้อข้างบน) และ PaymentIntent (`pi:` · ไม่มี `LiteralDates` ⇒ ไม่มีชุดวันที่ ⇒ `LegacyKeys` เท่าเดิม · `SharesRawIdWith` ข้ามคีย์ `pi:`) ·
  หน้าตรวจไฟล์ (`InspectFileAsync`/`Peek`) ไม่ตัดสินวันที่/แถวสรุป — ไม่เปลี่ยน · คำเตือนเนื้อหาตรงรอบอื่นมีสองผู้เรียก (นำเข้า · พรีวิว/ลงบัญชี) ใช้ `WhatToDo`/`RefsWithStatus` ตัวเดียวกันแล้ว
- **เข้มขึ้น — ผู้ใช้ทำอะไรแทน**: `id-column-empty` ⇒ จับคู่คอลัมน์เลขออเดอร์ใหม่ · `date-order-conflict` ⇒ เปลี่ยน "รูปแบบวันที่" หรือ "อัตโนมัติ" · `date-order-timezone-ambiguous` ⇒ เลือกสองช่องแล้วนำเข้าครั้งเดียว ·
  แถวไม่มีเลขที่เคย**ข้าม**ตอนนี้**นำเข้า** (ข้อ 39) ⇒ ถ้าเป็นแถวสรุปที่พิสูจน์ไม่ได้ ผู้ใช้เห็นแถบเตือนรายแถว + ยกเลิกรอบแล้วลบแถวออกจากไฟล์ · เทสต์ทิศตรงข้าม: `I1_ทิศตรงข้าม_*` · `I9_ทิศตรงข้าม_*` ·
  `D39_แถวรวมที่ยอดเท่าผลรวม…` · `I8_…ทิศ "บั๊กเดิม"` · `I10_…เขตเวลาที่ตั้งยังชนะหัวคอลัมน์` · `I6_…มีเลขบางแถวไม่ล้ม`
- **ค่าที่ persist ไว้ก่อนแก้**: คีย์ `ExternalTxnId` ที่เก็บแล้ว**ไม่ต้อง migrate** — การแก้คือ "เทียบด้วยคีย์รุ่นก่อนเพิ่ม" ไม่ใช่เปลี่ยนคีย์ที่เก็บ · `SettlementLine.ImportScope` เป็นคอลัมน์ใหม่ของรอบ 200 เอง (ยังไม่ deploy) ⇒
  ไม่มีข้อมูลกลุ่มเล็กที่ค้าง · `ColumnMapJson` เดิมไม่มี `commaIsThousands` ⇒ false (เทสต์ล็อก) · ไม่มี migration ใหม่

## ขอบเขตไฟล์ของทีมอื่นที่แตะ (เล็กที่สุด)

- `Controllers/SettlementController.cs` `ImportFile` — ส่ง `ColumnMapMemoryBlocker` เข้า service (4 บรรทัด)
- `Helpers/SettlementPermissionScope.cs` — เพิ่ม `ColumnMapMemoryBlocker` + ให้ `ColumnMapMemory` ตัดสินด้วยตัวนั้น (ข้อความเดิมไม่เปลี่ยน · เทสต์เดิม `SettlementReview198DTests` ใช้ได้)
- `Helpers/SettlementContentOverlap.cs` (ของทีม I) — record 2 ตัวได้พารามิเตอร์ท้ายแบบมีค่าเริ่มต้น (`BatchStatus` · `PostedPayoutRefs`) — projection ใน EF ส่งครบทุกตัว (CS0854)
- **ไม่แตะ** `SettlementPostingService` (เส้นลงบัญชี/ModeMismatch · ทีม SF) · `GatewaySettlementService`/`ConfirmedRangeUtc` (ทีม GF)

## ตัวตรวจ (เครื่องโหลดหนัก — รันเฉพาะตัวที่เกี่ยว)

- `required_call_site_check` — ดูผลในคำตอบสุดท้าย (แถวที่ล้ม `SettlementPostingService.BuildGateAsync` `c.CountryCode` เป็นของทีม W ที่ HEAD `c26138d8` — ไฟล์นั้นไม่ถูกแตะในคอมมิตนี้)
- ✅ `record_arg_check` · `nullable_arg_check` · `using_check` · `tuple_name_merge_check` · `regex_line_span_check` · `html_attr_escape_check` · `dead_helper_check` (ไม่มีตัวใหม่) ·
  `string_quote_close_check` · `comment_line_break_check` · `verbatim_string_check` · `identifier_space_check` · `namespace_shadow_check` · `service_interface_check` · `accessibility_check` ·
  `test_inventory --check` (วางแถวใหม่ — HEAD ค้างอยู่ 370 ไฟล์จากงานทีมอื่น)
- `undeclared_local_check` ตัวจริง **ข้ามทุกไฟล์ใน worktree** (กรอง path ที่มี `.claude`) ⇒ รันสำเนาที่กรองด้วย path สัมพัทธ์: 1,551 ไฟล์ · 0 จุด (**ข้อสังเกตถึงเจ้าของเครื่องมือ**: agent ที่ทำงานใน worktree ได้ "ตรวจ 0 ไฟล์" แล้วเขียวเสมอ)
- `node --check` script ใน `settlements.html` ✅ · awk brace-balance ทุก .cs ที่แก้ ✅ · ไม่มี U+FFFD

## ความเสี่ยงคอมไพล์ที่เหลือ (ไม่มี SDK)

1. `DecideDates` แปลงผลลำดับวัน/เดือน (`SettlementFileDecision<SettlementDateOrder>?`) เป็น `SettlementDateOrder?` ด้วย pattern `is … od ? od.Value : null` (ไม่ใช้ `?.Value` ที่อ่านสับสนกับ `Nullable<T>.Value`)
2. `private sealed record SourceRow(...)` ซ้อนใน adapter + local function `Strict` ส่งเป็น `Func<string?, string?, decimal?>` · `foreach (var (col, amt, vat) in amounts)` บน `List<(SettlementAmountColumn Col, decimal Amount, decimal? Vat)>`
3. `r with { Date = set[i] }` บน `readonly record struct SettlementTxnKeyInput` ใน lambda · `ToDictionary(..., g => (Refs: ..., Posted: ...))` + `TryGetValue(key, out var found)` แล้วใช้ `found.Refs`
4. EF: `l.ExternalTxnId.StartsWith(RowKeyPrefix)` (const `SettlementTxnKey.Version + "row"`) ภายใน expression tree + `l.Batch.Status` ใน projection (navigation — รูปเดียวกับ `l.Batch.PayoutRef` เดิม)
5. `IsSummaryRow` เปลี่ยนเป็น 7 อาร์กิวเมนต์ (ไม่มี optional) — ผู้เรียกโค้ดจริง 1 + เทสต์ปรับแล้ว · `ImportFileAsync` เพิ่มพารามิเตอร์ `memoryBlockedReason` ก่อน `ct` — ผู้เรียก 1 (controller)
6. ข้อความในเทสต์ที่พึ่งรูปตัวเลข ("1,521.50" · "950.00") ใช้ `InvariantCulture` แล้ว (เดิม `N2` ขึ้นกับ culture ของเครื่อง)

## ความเสี่ยงเชิงพฤติกรรมที่เหลือ / คำถามค้าง

1. **ค่าตั้งต้นของช่องทางที่มีประวัตินำเข้าแล้ว** (I-2 ข้อเสนอ "ตั้งต้นเป็น Bangkok") — ไม่ทำ: เป็นการเดาเขตเวลาแทนผู้ใช้ (DOCTRINE §1) · ตอนนี้ถูกถามครั้งเดียวต่อช่องทาง (ผู้มีสิทธิ์) และข้อความบอกตามจริง — **รอเจ้าของตัดสิน** ถ้าต้องการ migration ตั้ง Bangkok ให้ช่องทางเดิม
2. **แถวสรุปที่เคยนำเข้าก่อน deploy** (ไฟล์แบบยาวที่ขึ้นต้น "ยอดสุทธิ/สรุป/Net total" — กติกาทีม I ขยายคำสรุป) — แถวนั้นเคยอยู่ในลายนิ้วมือไฟล์ ⇒ นำเข้าไฟล์เดิมซ้ำ แถวไม่มี id ได้คีย์ `v2:rowc:` ใหม่ ⇒ เข้าเป็นบรรทัดใหม่ **พร้อมคำเตือน** "เนื้อหาตรง…จากอีกไฟล์" (ไม่เงียบ) · ไม่ได้คิดลายนิ้วมือรุ่นก่อนที่รวมแถวสรุป (ต้องรู้ว่าตัวอ่านเดิมนับแถวไหน = ซ้อนอีกชั้น) — ขอบเขตแคบ ทิศมองเห็นได้
3. ไฟล์แบบกว้าง**ที่ไม่มีคอลัมน์วันที่**: แถวไม่มีเลขทุกแถวถูกข้ามเป็นแถวสรุป (ข้อ 39 "ไม่มีวันที่") — แสดงเป็นแถบเตือนพร้อมยอด · ถ้าเป็นแถวจริงที่ต้องนำเข้า ผู้ใช้เติมเลขออเดอร์/วันที่ในไฟล์
4. `ImportScope` ของบรรทัดที่สืบจากไฟล์รุ่นก่อน**ไม่เท่ากับ**ลายนิ้วมือในคีย์ `v2:rowc:` ของบรรทัดนั้นอีกต่อไป (doc-comment ปรับแล้ว) — ไม่มีผู้อ่านที่อาศัยความเท่ากันนี้ (grep `ImportScope` = `StoredRowContentAsync` จุดเดียว)
