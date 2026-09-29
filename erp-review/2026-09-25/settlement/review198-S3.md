# ฝ่ายค้าน (F3 #11) — ทีม S3 settlement · `60db75ee` + `85734477` + `1f696501` (merge `92ad5172`)

ขอบเขต: `git diff 77c17aac 92ad5172 -- Accounting Accounting.Tests` · อ่านอย่างเดียว ไม่ได้แก้/คอมมิตโค้ด ·
**ยังไม่ได้คอมไพล์** (env ไม่มี .NET SDK) · MCP `actions_list` ของ branch `claude/erp-system-review-team-660mev` คืนมา 0 run (ไม่มีผล CI ให้อ้าง)

ระดับ: CONFIRMED = เปิดโค้ดแล้วเห็นเส้นทางครบ · PLAUSIBLE = เส้นทางมีจริงแต่ขึ้นกับข้อมูล/การตั้งค่า · NOT-A-BUG = ตรวจแล้วข้อกล่าวอ้างถูก

---

## สรุป

| ID | ระดับ | P | เรื่อง |
|---|---|---|---|
| ✅ <pending> S3-1 | PLAUSIBLE (ตรรกะยืนยันแล้ว) | **P1** | รอบโอนที่ลงค้างครึ่งทางซึ่งมีของที่ยกเลิกไม่ได้ (e-Tax Accepted / อยู่ในรายงานที่ล็อก / 50 ทวิ อยู่ในแบบที่ยื่น) = **ไปต่อไม่ได้เลย**: แก้บรรทัดไม่ได้ ยกเลิกรอบไม่ได้ ลงต่อไม่ได้ถ้าด่านต้องให้แก้บรรทัด |
| ✅ <pending> S3-2 | CONFIRMED | P2 | ด่านยกเลิกการลงบัญชี (C-2) ไม่ตรวจภาษีซื้อของใบค่าธรรมเนียมในเดือน ภ.พ.30 ที่ประกาศว่ายื่นแล้ว — อสมมาตรกับฝั่งขาย |
| ✅ <pending> S3-3 | CONFIRMED | P2 | ด่าน C-2 ไม่ครอบเหตุที่ `VoidDocumentAsync` ปฏิเสธเอง (เอกสารลูก active / ใบลดหนี้อ้างเลขที่) ⇒ ยกเลิกครึ่งทางแบบ C-2 เดิมยังเกิดได้ |
| ✅ <pending> S3-4 | PLAUSIBLE | P2 | คีย์กันซ้ำ v2 ใส่ `payoutRef` ในแถวไม่มี id — เลขรอบโอนที่ผู้ใช้พิมพ์ต่างกันครั้งที่สอง ⇒ แถวเดิมเข้าอีกรอบ (R-A9 ถอย) · และไม่มีการเทียบคีย์ v1 ที่เก็บไว้แล้ว |
| ✅ V1 <pending> S3-5 (คำตัดสินรอบ 200 ข้อ 9) | CONFIRMED (ผลข้างเคียง) | P2 | `CheckDocumentPaymentsAsync` ใน `VoidDocumentAsync` ทำให้ใบขายของผู้ใช้ที่รอบโอนรับชำระ **ยกเลิก-ออกใหม่ไม่ได้ถาวร** เมื่อรอบโอนนั้น unpost ไม่ได้ (ภ.พ.30 ของใบสรุปถูกประกาศแล้ว) |
| ✅ <pending> S3-6 | PLAUSIBLE | P2 | ของกำพร้า (C-1(d)) ที่ยกเลิกไม่ได้ ⇒ `OrphanPostingArtifacts` บล็อก **ทุกรอบโอนของช่องทางนั้นตลอดไป** |
| ✅ c3116a4d S3-7 (ยืนยันรอบ 200 ทีม V1) | PLAUSIBLE | P2 | ด่าน C-2 ไม่ดูการรับชำระ (tax point บริการ §78/1 / undue VAT) ในเดือนที่ประกาศว่ายื่นแล้ว |
| ✅ <pending> S3-8 | PLAUSIBLE | P3 | race ช่วงสั้น: ยกเลิกเอกสารผ่านหน้าปกติระหว่าง completeness check กับ commit ของ `CommitPostedAsync` |
| ✅ <pending> S3-9 | PLAUSIBLE | P3 | ล็อกฝั่งนำเข้า `pg_advisory_xact_lock` แบบ**รอ** ขณะลงบัญชีถือ session lock นาน ⇒ คำขอเว็บค้างจน command timeout (500) |
| ✅ <pending> S3-10 | CONFIRMED | P3 | `WhtCertVoidGuard` ไม่ใช่ "ตัวตัดสินตัวเดียว" จริง — `PayrollService.cs:3328` ประทับ Voided ตรง |
| 📋 S3-11 (backlog) | CONFIRMED | P3 | ของเล็ก: `OrphanArtifactsAsync` `Take(200)` · `UnpostBlockersAsync` ใน GET ทุกครั้ง (LoadAsync ทั้งรอบ · ช่องทางถูกลบ ⇒ GET พัง) · SoD เทียบแค่ผู้สร้างรอบ · Unpost/Post ไม่ตรวจ `JournalManage` สำหรับ JE รอบโอน |
| — | NOT-A-BUG | — | ล็อกร่วม (คีย์/รูปแบบเดียวกัน) · deadlock · AsyncLocal · `autoApproveBy` · EF translation · fingerprint false positive · tenant · R1 |

**ไม่พบ P0 ที่ยืนยันได้** · P1 ที่ต้องแก้ก่อน merge เข้า main: S3-1

---

## รายละเอียด

### S3-1 · P1 · PLAUSIBLE — ลงค้างครึ่งทาง + ของที่ยกเลิกไม่ได้ = ทางตัน

**ที่:** `SettlementImportService.Lines.cs:546-559` (`LoadEditableBatchAsync` — มี artifact ⇒ 409 `SETTLEMENT-BATCH-PARTIAL`) ·
`SettlementSaleMatch.IsEditable(status, artifacts)` · `SettlementBatchActions` (halfPosted ⇒ CanEditLines/CanVoid = false) ·
ทางไปต่อที่ข้อความเสนอ = "ลงต่อให้ครบ" หรือ "ยกเลิกเอกสาร/การรับชำระที่หน้าเอกสาร"

**ฉาก:** ลงบัญชีรอบ 1 → ใบค่าธรรมเนียม + ใบขายสรุป (TaxInvoice) ออกและอนุมัติแล้ว → `IIssuedDocumentHooks` ส่ง e-Tax อัตโนมัติ → Accepted ·
ขั้นรับชำระล้ม (ใบขายที่จับคู่ถูกผู้ใช้รับชำระ/ยกเลิกระหว่างนั้น ⇒ `CreatePaymentAsync` ล้ม หรือพรีวิวรอบถัดไปฟ้อง receipt target ใช้ไม่ได้ —
`SettlementPosting.cs` ส่วน "ใบขายที่จะรับชำระ" ซึ่งทางไปต่อคือ **เลือกใบใหม่ = แก้บรรทัด**) ·
- แก้/จับคู่บรรทัด ⇒ `LoadEditableBatchAsync` 409 PARTIAL
- ยกเลิกใบขายสรุปทีละใบ ⇒ `VoidDocumentAsync` ปฏิเสธ (e-Tax Accepted `DocumentService.cs` ~8058)
- ยกเลิกรอบโอน ⇒ 409 PARTIAL
- ลงต่อ ⇒ ด่าน receipt target บล็อก
- Unpost ⇒ ไม่ใช่ Posted (ปุ่มไม่มี · service ตอบ "ยังไม่ลงบัญชี")

ทางเดียวกันกับ 50 ทวิ ของใบค่าธรรมเนียมที่ ภ.ง.ด.53 เดือนนั้นประกาศแล้ว (`WhtCertVoidGuard` ใหม่ใน `DocumentService.cs:8040`) และเอกสารที่อยู่ในรายงาน `FilingLockedAt`.
ก่อน S3 ผู้ใช้ยังแก้บรรทัดได้ (ด่านเก่าดูแค่สถานะ) — ด่านใหม่ทำให้เคสนี้ไม่มีทางไปต่อ (F2 #8: "เข้มขึ้นต้องมีทางไปต่อ").

**แก้:** ให้แก้บรรทัดที่ไม่กระทบชิ้นที่ออกแล้วได้ (เช่น เฉพาะการจับคู่ของ receipts ที่ยังไม่มีการรับชำระ) หรือให้ `IsEditable` นับเฉพาะ artifact ที่ยกเลิกได้
และบอกทางไปต่อที่มีจริงเมื่อ artifact ยกเลิกไม่ได้ (เช่น "ลงต่อด้วยการเลือกใบใหม่" ผ่านเส้นแก้การจับคู่ที่อนุญาตเฉพาะชิ้นที่ยังไม่ทำ) · เทสต์ทิศตรงข้าม: half-posted + e-Tax Accepted + receipt target เสีย ⇒ ต้องมีเส้นที่ทำต่อได้

### S3-2 · P2 · CONFIRMED — Unpost ไม่ตรวจภาษีซื้อใบค่าธรรมเนียมใน ภ.พ.30 ที่ประกาศแล้ว

**ที่:** `SettlementPostingGuards.cs:199` — `if (IsSaleSide(d.Component) && d.VatAmount != 0m && declaredOrFiled.Contains((VAT,…)))` ตรวจเฉพาะ `sum-*`.
ใบค่าธรรมเนียม (`fee-Standard7` ภาษีซื้อ 7% ของแพลตฟอร์มในประเทศ) ที่ ภ.พ.30 เดือน payout ถูก **ประกาศว่ายื่น** (Submitted — ไม่มี `FilingLockedAt`)
ถูกยกเลิกผ่านด่านได้ ⇒ ภาษีซื้อที่เคลมในแบบที่ยื่นแล้วถูกกลับในสมุดเงียบ ๆ — ตรงกับเหตุผลที่ C-2 เองเขียนไว้สำหรับฝั่งขาย (`DocumentService.VoidDocumentAsync`
กันเฉพาะ `FilingLockedAt`). **แก้:** ตรวจ `d.VatAmount != 0` ทุกชิ้น (หรือใส่ input VAT แยก) กับ `(VAT, year, month)` · เพิ่มเทสต์ใน `C2_…`

### S3-3 · P2 · CONFIRMED — ด่าน C-2 ไม่ครอบเหตุปฏิเสธทั้งหมดของ `VoidDocumentAsync`

**ที่:** `SettlementUnpostGate.Evaluate` ตรวจ e-Tax Accepted · locked report · VAT/PP36/50 ทวิ แต่ `VoidDocumentAsync` (`DocumentService.cs` ~8066-8095) ยังปฏิเสธ
"มีเอกสารลูก active อ้าง `RelatedDocumentId`" และ "ใบลดหนี้/ใบเพิ่มหนี้อ้างเลขที่ใน `Reference`". ใบขายสรุปที่ผู้ใช้ออกใบลดหนี้อ้างไว้ (ลูกค้าเงินสดขอคืนบางส่วน)
⇒ รอบที่มีใบสรุปหลายวัน: ใบแรกถูกยกเลิก ใบที่สองล้ม ⇒ `SETTLEMENT-UNPOST-PARTIAL` = ครึ่งกลับครึ่งค้าง (ปัญหาเดิมของ C-2).
**แก้:** โหลด "เอกสารลูก active / CN อ้างเลขที่" เข้า `SettlementUnpostDocument` แล้วปฏิเสธใน `Evaluate` (ตัวโหลดเดียว `LoadUnpostFactsAsync`) — หรือดีกว่า: ยก guard ของ `VoidDocumentAsync` เป็น pure `DocumentVoidPreconditions` ให้ทั้งสองเส้นเรียกตัวเดียว

### S3-4 · P2 · PLAUSIBLE — คีย์กันซ้ำ v2

`SettlementTxnKey.cs` (Assign สาขาไม่มี id) + `SettlementImportService.cs:243-244` ส่ง `payoutRef` ที่มาจาก `SelectPayout(h.PayoutRef, rows)` — ผู้ใช้พิมพ์ได้เมื่อไฟล์ไม่มีคอลัมน์รอบโอน
1. **ถอยจาก R-A9:** นำเข้าไฟล์เดิม (หรือไฟล์ช่วงวันทับกัน) ด้วยเลขรอบโอนที่พิมพ์ต่าง (แก้คำผิด / ใส่รูปแบบอื่น) ⇒ แถวไม่มี id ทุกแถวได้คีย์ใหม่ ⇒ รอบโอนที่สองซ้ำทั้งก้อน · ยอดขายซ้ำถูก `DuplicateSalesAsync` จับเฉพาะใบสรุปรายวันตอนลงบัญชี — **ค่าธรรมเนียม/ปรับปรุงซ้ำไม่มีใครจับ**. เทสต์ `RB5_…` ล็อกทิศ "สองรอบโอนแยกกัน" แต่ไม่มีเทสต์ทิศ "ไฟล์เดิมเลขรอบโอนต่าง"
2. **ไม่มีทางเชื่อม v1:** `ExistingKeysAsync` เทียบเฉพาะคีย์ v2 · แถวที่นำเข้าไว้ด้วยคีย์ v1 (ถ้าเฟส 1 เคยรันบน staging/ฐานใด) + นำเข้าไฟล์เดิมซ้ำ ⇒ รอบที่ยังไม่ลงบัญชีได้บรรทัดซ้ำทั้งไฟล์ · รอบที่ลงแล้วได้ข้อความเท็จ "แพลตฟอร์มแก้รายงานย้อนหลัง". branch ยังไม่เข้า main จึงเป็น PLAUSIBLE — ถ้ามีฐานที่รันเฟส 1 แล้วต้องมี migration (คำนวณคีย์ v1 ด้วยสำหรับเทียบ หรือ rekey)
**แก้:** ใช้ payoutRef เฉพาะเมื่อมาจากคอลัมน์ในไฟล์ (ไม่ใช่ที่ผู้ใช้พิมพ์) หรือเตือนเมื่อแถวไม่มี id ตรงทุกช่องกับแถวของรอบอื่นในช่องทาง · เทียบคีย์ v1 ด้วยจนกว่าจะ migrate

### S3-5 · P2 · CONFIRMED (ผลข้างเคียงที่ต้องให้เจ้าของเลือก) — ใบขายของผู้ใช้ยกเลิก-ออกใหม่ไม่ได้

`DocumentService.cs:8037` `CheckDocumentPaymentsAsync` บล็อกการยกเลิก**ใบขายของผู้ใช้**ที่รอบโอน Posted รับชำระไว้ ⇒ ต้อง unpost ทั้งรอบ ·
แต่ unpost ถูก `SettlementUnpostGate` ปฏิเสธถาวรเมื่อใบสรุปใดของรอบอยู่เดือน ภ.พ.30 ที่ประกาศแล้ว/e-Tax Accepted ⇒ ใบกำกับของผู้ใช้เดือนถัดไป (ยังไม่ยื่น)
ที่ต้อง "ยกเลิกแล้วออกใหม่" เพราะชื่อ/ที่อยู่ผู้ซื้อผิด (§86/4 ไม่มีใบลดหนี้ให้ใช้กับกรณีนี้) **ทำไม่ได้เลย**. ทางเข้าอื่นที่ได้ 409 ใหม่: `IntegrationService.cs:4386` (inbound void ⇒ sync ล้ม),
`CmsCommerceService.cs:683` / `CmsBookingService.cs:410` (catch แล้วเป็น notice — ยอมรับได้). ทิศนี้ถูกทางบัญชี (กันผังพักคลาด) แต่ต้องมีทางไปต่อ: เส้น "ย้ายการรับชำระของรอบโอนไปใบใหม่" หรือ
อนุญาตยกเลิกใบ + ยกเลิกการรับชำระนั้น + บรรทัดปรับปรุงผังพักในรอบถัดไป — **ให้เจ้าของตัดสิน**

### S3-6 · P2 · PLAUSIBLE — ของกำพร้าที่ยกเลิกไม่ได้บล็อกทั้งช่องทางถาวร

`SettlementPostingService.cs:681-710` + `SettlementPostingGate` (`OrphanPostingArtifacts` blocking ไม่มี override). ของกำพร้าจากก่อน S3 (ลงค้าง → ยกเลิกรอบ) ที่เป็นใบสรุป
e-Tax Accepted / อยู่ในรายงานล็อก / ใบค่าธรรมเนียมที่ 50 ทวิ ยื่นแล้ว ⇒ ยกเลิกไม่ได้ ⇒ **ทุกรอบโอนใหม่ของช่องทางนั้นลงบัญชีไม่ได้ตลอดไป** · ใบลดหนี้ไม่ช่วย (ตรวจ `Status != Voided`).
ของกำพร้าเกิดได้เฉพาะข้อมูลก่อน S3 (branch ยังไม่เข้า main ⇒ PLAUSIBLE) **แก้:** ให้ "รับรู้/ผูก" ของกำพร้าแบบมีเหตุผล+ผู้อนุมัติ (ธงบนเอกสาร) แทนการบล็อกไม่มีทางออก

### S3-7 · P2 · PLAUSIBLE — ด่าน C-2 ไม่ดูการรับชำระ

`UnpostCoreAsync` ยกเลิกการรับชำระ (`:802`) หลังผ่าน `Evaluate` ซึ่งไม่ได้รับข้อเท็จจริงของการรับชำระเลย. การรับชำระใบบริการ = จุดความรับผิด §78/1 (ย้าย undue VAT 11640→11610 ตอนรับเงิน) ·
ถ้าเดือน payout ประกาศ ภ.พ.30 แล้ว การยกเลิกการรับชำระกลับภาษีขายของเดือนที่ยื่นแล้วเงียบ ๆ (หรือ `VoidPaymentAsync` ปฏิเสธกลางทาง ⇒ ครึ่งทาง). ตรวจว่าเส้นรับชำระ
ประทับ VAT period จริงแล้ว เพิ่มเดือนของการรับชำระ (ใบบริการ) เข้า `Evaluate`

### S3-8 · P3 · PLAUSIBLE — race ช่วงสั้นตอน commit

`CommitPostedAsync` (`:314-335`) ล็อกแถวรอบโอน `FOR UPDATE` แล้วตรวจความครบ แต่ `SettlementArtifactGuard.CheckAsync` ในเส้นยกเลิกปกติอ่านสถานะรอบโอนแบบไม่ล็อก ·
ยกเลิกใบค่าธรรมเนียมระหว่าง completeness query กับ commit ⇒ guard เห็นยังไม่ Posted ⇒ ผ่าน ⇒ รอบโอน Posted พร้อมชิ้นที่หาย (R1). **แก้:** guard อ่านแถวรอบโอน `FOR SHARE` ในธุรกรรมของเส้นยกเลิก (หรือถือล็อกช่องทางแบบ try)

### S3-9 · P3 · PLAUSIBLE — คำขอเว็บรอล็อก

`LockChannelAsync` (`Lines.cs:578-582`) ใช้ `pg_advisory_xact_lock` (รอไม่จำกัด) ขณะผู้ลงบัญชีถือ session lock ตลอดการสร้าง/อนุมัติเอกสารทุกชิ้น (หลายสิบวินาทีได้) ⇒ นำเข้า/จัดประเภท/จับคู่
ค้างจน Npgsql command timeout ⇒ 500 ข้อความระบบ. ไม่ใช่ deadlock (ฝั่งนำเข้าไม่ถืออะไรก่อนรอ · ฝั่งลงบัญชีใช้ try) — แต่ JobLock เองเขียนไว้ว่า "อย่ารอ ล้มเร็ว".
**แก้:** ใช้ `JobLock.TryXactLockAsync` แล้วตอบ BusyMessage เดียวกับฝั่งลงบัญชี

### S3-10 · P3 · CONFIRMED — ตัวตัดสิน 50 ทวิ ไม่ใช่ตัวเดียว

`PayrollService.cs:3328` (`existingFromThisRun … Status = Voided`) ยกเลิกใบ ภ.ง.ด.1 ของ run ที่ re-post โดยไม่ผ่าน `WhtCertVoidGuard` ·
`DocumentService.cs:9804/9945` แตะเฉพาะ Draft (ไม่ขัด). นอกขอบเขต S3 แต่ doc-comment ของ `WhtCertVoidGuard` อ้างว่าเป็นตัวตัดสินตัวเดียว — ต้องแก้คำหรือต่อสาย payroll

### S3-11 · P3 · CONFIRMED — ของเล็ก
- `OrphanArtifactsAsync` `Take(200)` (`:687`) — ช่องทางที่ยกเลิกรอบเกิน 200 ⇒ ของกำพร้าเก่าหลุดด่าน
- `SettlementController.BatchDetailAsync` เรียก `UnpostBlockersAsync` ทุก GET → `LoadAsync` โหลดบรรทัดทั้งรอบซ้ำ และโยน `KeyNotFound` ถ้าช่องทางถูก soft-delete (ก่อนหน้าหน้า GET ใช้ `LoadChannelAsync` ของผู้นำเข้า)
- SoD (`SettlementPostingGate.SodSelfApproval`) เทียบแค่ `batch.CreatedBy` — ผู้ที่เติมไฟล์ต่อ/ตัดสินการจับคู่ (ผู้ทำจริง) ไม่ถูกนับ
- Post/Unpost สร้าง/กลับ JE รอบโอนโดยไม่ตรวจ `PermissionKeys.JournalManage` ใน service (C-8 ตรวจเฉพาะ BankReconcile/Void/ปิด chargeback)
- ป้ายรอบโอนของการรับชำระอยู่ใน `Payment.Notes` ซึ่งผู้ใช้พิมพ์ตอนสร้างได้ — ใส่ `[SETTLEMENT:<id>]` เองทำให้รอบโอนนั้น "ค้างครึ่งทาง"/unpost ไปยกเลิกการรับชำระของคนอื่น (ต้องรู้ id รอบ · insider เท่านั้น)

---

## ตรวจแล้วข้อกล่าวอ้างถูก (NOT-A-BUG)

1. **ล็อกร่วม C-1** — ฝั่งนำเข้า `pg_advisory_xact_lock(bigint)` ด้วย `SettlementChannelLock.Key` = `AdvisoryLockKey.For(companyId, "settlement-import", channelId:N)`;
   ฝั่ง Post/Unpost/MatchBank/ResolveChargeback `JobLock.RunExclusiveAsync(_db, Scope, Part, …, companyId)` ⇒ `pg_try_advisory_lock(bigint)` ด้วยสูตรเดียวกัน (FNV-1a deterministic) ·
   **รูป 1 อาร์กิวเมนต์ทั้งคู่** ⇒ session/xact lock คีย์เดียวกันกันกันจริง · ไม่เหลือผู้ใช้ `SettlementPostingKeys.LockScope` ต่อช่องทาง (เหลือเฉพาะคีย์ต่อ reference ของ chargeback) · เรียงล็อก:
   ฝั่งนำเข้าช่องทาง → pay-settle, `GatewaySettlementService` ถือแต่ pay-settle ⇒ ไม่มีวงรอ · ฝั่งลงบัญชีไม่เคย "รอ" คีย์ช่องทาง ⇒ ไม่มี deadlock กับเลขเอกสาร/มัดจำ
2. **R-B3** ล็อกก่อนโหลด — ทุกเส้นแก้บรรทัด/ยกเลิก/นำเข้า อ่านแค่ ChannelId ก่อน แล้ว `LockChannelAsync` ก่อน `LoadEditableBatchAsync`
3. **AsyncLocal `SettlementUnpostScope`** (`Guards.cs:229`, ใช้ `:791`) — ตั้งใน async method ⇒ EC คืนค่าเมื่อเมธอดจบ ไม่รั่วไปคำขออื่น · ค่าเทียบกับ batchId ⇒ แม้ task ที่แตกออกไปรับค่าไปก็ยกเลิกได้แค่ชิ้นของรอบนั้น · `Enter` ไม่มีทางเข้าจาก input ผู้ใช้
4. **C-7 `autoApproveBy`** — ผู้เรียกมีตัวเดียว (`SettlementPostingService.cs:263`) · เป็นพารามิเตอร์เมธอด ไม่อยู่ใน request · สิทธิ์อนุมัติ PV/ชนิดใบขายตรวจที่ด่าน (`CanApproveAsync`) · SoD ของ DocumentService เทียบกับป้ายระบบจึงต้องมีด่าน SoD ของ settlement (มีแล้ว — ข้อจำกัดอยู่ใน S3-11)
5. **WhtCertVoidGuard ใน `VoidDocumentAsync`** — ตรวจก่อนธุรกรรม (ล็อกด้วย required_call_site) · cascade เดิมที่กลืน error ไม่เจอเคสยื่นแล้วอีก · ร่าง/ยังไม่ยื่นยกเลิกได้ (เทสต์ `C2_50ทวิ…ร่างหรือยังไม่ยื่น_ยกเลิกได้`)
6. **Fingerprint false positive** — `SettlementPostingGate.Evaluate` คืน `plan with { Issues, CanPost }` เท่านั้น · fingerprint ไม่รวม issues · อินพุตเดียวกัน (Seq order, `!IsDeleted`, `gate.VatRegistered`) ⇒ ไม่ฟ้องเท็จ · ถ้าช่องทางถูกแก้จริง ทางไปต่อ = กดใหม่
7. **Completeness** — receipts 1 แถว/ใบ (`BatchMath.cs:352-364`) · ยอดการรับชำระ = `r.Amount` (`ReceiptPayment`) · การรับชำระที่ยกเลิก = `IsDeleted` ⇒ ไม่ค้างเป็น artifact · ชิ้นขาด/เกินมีทางไปต่อ (ยกเลิกทีละชิ้นได้เพราะยังไม่ Posted — ยกเว้นเคส S3-1)
8. **EF translation** — `upper.Contains(x.Trim().ToUpper())` ⇒ `upper(btrim(..)) = ANY(@p)` · `Substring(18,32)` ⇒ `substring` (ความยาว "system:settlement:" = 18 ถูก) · record ctor อยู่ใน projection สุดท้าย ·
   tuple สร้างหลัง `ToListAsync` · `WhtCertFilingScope.Filed` เป็น array (ใช้ใน query ที่อื่นอยู่แล้ว)
9. **Tenant** — query ใหม่ทุกตัวมี `CompanyId` (Documents/Payments/Certs/EtaxInvoices/TaxReports/Batches `IgnoreQueryFilters` + CompanyId) · `SiteSettings` เป็นตารางระดับแพลตฟอร์ม · `refundInLines` ข้ามช่องทางโดยตั้งใจ (R-B17)
10. **R1** — `Posted` ประทับหลัง fingerprint + completeness ในธุรกรรมเดียว · `intent.SettlementBatchId ??=` ไม่ย้ายเจ้าของ · E2-10 บล็อกทั้งผู้สมัครจับคู่และ clearing source

---

## ผลการแก้ — ทีม S4 (รอบ 198 · คอมมิต <pending>)

| ID | สถานะ | ที่แก้ / เหตุผล |
|---|---|---|
| S3-1 | ✅ | เลือกทาง "แก้บรรทัดที่ยังไม่มีชิ้นที่ออกแล้วได้" (ขั้นต่ำที่ถูก — ไม่ต้องมีเส้น "ลงบัญชีโดยข้ามใบนี้"): `LoadRedecidableBatchAsync` + `SettlementPartialEdit.Refusal` เทียบลายนิ้วมือเฉพาะชิ้นที่ออกแล้ว (`SettlementPlanFingerprint.Piece/ReceiptPiece`) ใน `AssignLineMatchAsync`/`ReclassifyLineAsync` · เปลี่ยนชิ้นที่ออกแล้ว = 409 `SETTLEMENT-BATCH-PARTIAL-FROZEN` ทั้งธุรกรรม · ปุ่ม `CanRedecideLines` · ยกเลิกรอบ/จับคู่ใหม่ทั้งรอบ/บัญชีธนาคารยังล็อก (C-1(b) คงเดิม) · เทสต์ฉากผู้ตรวจตรงตัวสองทิศ `SettlementReview198S4Tests.S31_*` |
| S3-2 | ✅ | `SettlementUnpostGate.Evaluate` ตรวจภาษีซื้อฝั่งซื้อ: ไม่พัก = เดือนเอกสาร · พัก = เดือน `InputVatBecameClaimableAt` · ยังพัก = ไม่อยู่ในแบบใด (ไม่ปฏิเสธเกิน) |
| S3-3 | ✅ | `Helpers/DocumentVoidPreconditions` ตัวตัดสินเดียว (ย้ายจาก `VoidDocumentAsync` ข้อความเดิมทุกตัวอักษร) · `LoadUnpostFactsAsync` เรียกตัวเดียวกัน → `SettlementUnpostDocument.VoidBlock` |
| S3-4 | ✅ | คีย์ใช้เลขรอบโอนจากคอลัมน์ในไฟล์เท่านั้น · ไม่มีคอลัมน์ = ลายนิ้วมือเนื้อหาไฟล์ (`v2:rowc:`) · เทียบคีย์รุ่นก่อน (v1 + v2 เลขพิมพ์) · ไฟล์ฉบับแก้ของรอบเดิมเทียบเนื้อหาแบบนับจำนวน · เนื้อหาตรงรอบอื่น = เตือน · ข้อจำกัด: v1 ใช้ตัว normalize ของวันนี้ (ไม่มี migration rekey — ไม่มีฐาน production ที่รันเฟส 1) |
| S3-5 | ⏸ | ต้องให้เจ้าของเลือกทาง (ย้ายการรับชำระไปใบใหม่ vs ยกเลิก+ปรับปรุงรอบถัดไป) — จดใน DOCUMENT_FLOW §2.10 |
| S3-5 (รอบ 200) | ✅ | คำตัดสินข้อ 9: "ยกเลิกและออกใบแทน" ย้ายการรับชำระ + JE + คู่จับของรอบโอนไปใบใหม่ในธุรกรรมเดียว — `DocumentService.Reissue.cs` · `Helpers/SettlementPaidReissue` · DOCUMENT_FLOW §2.4c · รายงาน `erp-review/2026-09-29/team-V1.md` |
| S3-6 | ✅ | เลือกทาง "ของกำพร้าที่ยกเลิกไม่ได้ = เตือนไม่บล็อก" (ด่าน Unpost ตัวเดียวผ่าน `ArtifactId`) · ยกเลิกได้ = บล็อกเหมือนเดิม · ไม่เพิ่ม schema (owner-ack ไม่จำเป็นเมื่อระบบรู้เองว่ายกเลิกไม่ได้) |
| S3-7 | ✅ | `SettlementUnpostPayment` (OutputVatDueAt + ยอดรับสะสม) → ปฏิเสธเมื่อยกเลิกแล้วภาษีขายของเดือนที่ยื่นแล้วถูกกลับ |
| S3-8 | ✅ | `SettlementArtifactGuard.CheckLockedAsync` (แถวรอบโอน `FOR SHARE`) ใต้ธุรกรรมของ `VoidDocumentAsync`/`VoidPaymentAsync` |
| S3-9 | ✅ | `LockChannelAsync` = `JobLock.TryXactLockAsync` + `SettlementChannelLock.BusyMessage` (ข้อความเดียวกับฝั่งลงบัญชี) |
| S3-10 | ✅ | `PayrollService.IssueMonthlyPnd1CertsAsync` เรียก `WhtCertVoidGuard.CheckAsync` ก่อนประทับ Voided (ปฏิเสธ = ล้มดังผ่าน catch เดิม: LogError + แจ้งเตือน) |
| S3-11 | 📋 | ของเล็ก 5 ข้อ — backlog (ไม่มีข้อใดทำให้ข้อมูลผิดเงียบ · `Take(200)` / GET โหลดซ้ำ / SoD ผู้ทำจริง / JournalManage ใน Post / ป้ายใน Notes) |
