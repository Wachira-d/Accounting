# ทีม C — ความถูกต้องของบัญชี/ภาษี (GL · JE · VAT · WHT · สินทรัพย์)
_รอบ 2 (2026-09-05) · เขียน append ตั้งแต่ finding แรก · HEAD 444d2cb · ไม่ได้คอมไพล์_

## สรุป 5 บรรทัด
1. **P0 1 ข้อ (C-01)**: ใบเพิ่มหนี้/ลดหนี้ที่อ้างใบแจ้งหนี้บริการ (VAT พัก 21913) — GL reclass ตอนรับเงินรวมเฉพาะ JE ของ INV และ ภ.พ.30 ดึงตาม `OutputVatDueAt` ของเอกสารตัวเอง ⇒ VAT ของ DN **ไม่เคยเข้า ภ.พ.30 และค้าง 21913 ถาวร**เมื่อรับเงินคนละเดือน (คอมเมนต์ 2 ที่บรรยายกลไกที่ไม่มีจริง)
2. **P1 6 ข้อ**: `TryReclassifyUndueOutputVat` ไม่มีด่านงวดปิด + กลืน error + reclass เต็มก้อนตอนรับบางส่วน (C-02) · JE 50 จุดสร้างตรงไม่ผ่าน Builder — 4 จุดพิสูจน์ได้ว่าไม่สมดุลแล้ว Posted (C-04) · จำหน่ายสินทรัพย์ไม่มี VAT ขาย/ไม่มีใบกำกับ (C-05) · ค่าเสื่อม: ไม่มี pro-rata · แผน≠โพสต์ (DB ไม่ switch-to-SL) · ทบทวนอายุ/ตีราคาไม่ prospective · ไม่ปัดเศษ (C-06, sim reproduce ครบ) · 50 ทวิ/ไฟล์ ภ.ง.ด.3/53 เป็นสกุลเงินเอกสาร (C-07)
3. **P2 5 ข้อ**: CN/DN ฝั่งซื้อตัดเจ้าหนี้ที่ "212" เสมอ (C-03) · เลข 50 ทวิ max+1 ไม่มีล็อก + auto-gen ล้มแล้วเงียบ (C-08) · job ค่าเสื่อมไม่ไล่เดือนที่หลุด (C-09) · FMS 13 JE คืน FiscalPeriodId=null เมื่องวดปิด (C-10) · สงสัยใบเสร็จหลายใบนับ VAT ซ้ำ (C-12) · P3 1 ข้อ (C-11 CIL cross-rate)
4. **ตรวจแล้วดี**: 5 branch ของ AutoPost สมดุลเชิงพีชคณิตทุกเคส (sim 20,000 × 7) · `Math.Round` มี AwayFromZero ครบ · Cash/Accrual basis + cross-rate ของ PV/Receipt ถูก · dedup ภ.พ.30 Receipt↔TaxInvoice มี · JobLock + idempotent ของงานค่าเสื่อมมี
5. **ต้นเหตุร่วม**: (ก) ด่าน/กติกาเดียวกันเขียนซ้ำหลายที่ไม่ครบ (งวดปิด 3 ไฟล์ · Dr=Cr 39 จุด · rounding ไม่มีใน FixedAsset) → ต้องยกเป็นชั้น posting เดียว (Builder + interceptor) (ข) ปลายทางภาษี (50 ทวิ · ภ.พ.30 DN · จำหน่ายสินทรัพย์) ไม่ได้ถูกไล่หลังแก้ GL

## Findings (เรียง P0→P3)

### C-01 [P0][M] ใบเพิ่มหนี้/ใบลดหนี้ที่อ้างใบแจ้งหนี้บริการ (VAT พัก 21913) — VAT ไม่เคยถึง 21911 และ**หลุดจาก ภ.พ.30 ถาวร**เมื่อรับเงินคนละเดือน
- ไฟล์: `Accounting/Services/Implementations/DocumentService.cs:13375-13395` (DN/CN เลือก vatCode = "21913") · `:12325-12331` (`TryReclassifyUndueOutputVatAsync` กรอง `DocumentType == Invoice` และ `SourceDocumentId == invoiceId`) · `:14557-14575` (`SourceDocumentId = doc.Id` = ตัว DN เอง) · `Accounting/Services/Implementations/TaxService.cs:194-203` (query งวด: DocumentDate ในงวด **หรือ** `d.OutputVatDueAt` ของ**เอกสารตัวเอง**ในงวด) · `:736-759` (DN ที่ใบเดิม `OutputVatDueAt == null` → `IsExcluded=true; continue`)
- โค้ด: `if (src21913Net > 0.005m) vatCode = "21913";` (13390) · `var inv = await _db.Documents.FirstOrDefaultAsync(d => d.Id == invoiceId && … && d.DocumentType == DocumentType.Invoice …)` + `l.JournalEntry.SourceDocumentId == invoiceId … a.AccountCode == "21913"` (12327-12338) · TaxService คอมเมนต์ 655-658 อ้างว่า "ยอดสุทธิจะถูกนับตอนใบเดิมถึง tax point (reclass สุทธิหลัง CN ผ่าน GL)"
- ทำไมพัง: (1) ใบแจ้งหนี้บริการ INV เดือน ม.ค. Cr 21913 · DN เดือน ม.ค. อ้าง INV → AutoPost Cr 21913 ด้วย, JE ของ DN มี `SourceDocumentId = DN.Id` (2) ภ.พ.30 ม.ค.: DN เข้า branch "ใบเดิมยังไม่ถึง tax point" → บรรทัด IsExcluded (ไม่นับ) — ถูกต้อง (3) ลูกค้าจ่าย ก.พ. → `TryReclassifyUndueOutputVatAsync(INV.Id)` รวม 21913 **เฉพาะ JE ที่ SourceDocumentId == INV.Id** ⇒ reclass เฉพาะ VAT ของ INV; 21913 ของ DN ค้างถาวรใน GL (4) ภ.พ.30 ก.พ.: query เอา DocumentDate ก.พ. หรือ `OutputVatDueAt` **ของ DN** ในงวด — DN มี DocumentDate ม.ค. และ `OutputVatDueAt` ไม่มีใครเซ็ตให้ DN เลย (grep OutputVatDueAt ทั้งเรพ เซ็ตเฉพาะ inv) ⇒ DN **ไม่อยู่ในผลลัพธ์** (5) ภ.พ.30 ม.ค. ถ้า regenerate ตอนนี้ INV.OutputVatDueAt != null → DN หลุด branch excluded → นับ — แต่แบบ ม.ค. ยื่นไปแล้ว ⇒ ต้องยื่นเพิ่มเติม ซึ่งไม่มีอะไรบอกผู้ใช้
- ผลกระทบ: ภาษีขายของ DN (เพิ่มหนี้ = **ภาษีขายเพิ่ม**) นำส่งขาด → เบี้ยปรับ/เงินเพิ่ม §89-89/1 · ฝั่ง CN ตรงข้าม (ขอคืนน้อยกว่าสิทธิ) · GL: 21913 มียอดผี, 21911 ต่ำกว่าแบบที่ยื่น ตลอดไป — ตรวจ TB เห็นแต่ไม่รู้ที่มา · คอมเมนต์ในโค้ด 2 ที่ (TaxService 655-658 · DocumentService 13380-13383) บรรยายกลไกที่ไม่มีอยู่จริง
- defect class: "ด่านที่ doc-comment บอกว่ามี แต่ไม่มีใครเรียก" + "กฎสองข้อที่มองข้อมูลคนละชุด" (GL reclass ดูแค่ INV · รายงานดู INV+DN)
- ทางแก้ที่เสนอ: ใน `TryReclassifyUndueOutputVatAsync` รวม 21913 ของ JE ที่ `SourceDocumentId ∈ {inv.Id} ∪ {CN/DN ที่ RelatedDocumentId == inv.Id && Status ∉ Voided}` และ stamp `OutputVatDueAt` ลง CN/DN เหล่านั้นด้วย (TaxService 200-201 จะดึงเข้างวดรับเงินเอง) · เขียนเทสต์ INV ม.ค. + DN ม.ค. + Receipt ก.พ. → 21913 = 0 และ DN อยู่ในรายงาน ก.พ.
- ความมั่นใจ: สูง (อ่านครบทั้ง 3 จุด) — ต้องเช็คต่อว่า `relatedInv21913Net` ใน TaxService (~:415) คำนวณจาก INV เท่านั้นหรือรวม DN (ไม่เปลี่ยนข้อสรุปเพราะ query งวดตัดออกก่อน)

### C-02 [P1][S] `TryReclassifyUndueOutputVatAsync` — JE ที่โพสต์เข้างวดปิดได้ + `catch (Exception)` กลืนใน JE path + reclass เต็มก้อนทั้งที่รับเงินบางส่วน
- ไฟล์: `DocumentService.cs:12345` (`ResolveFiscalPeriodAsync` ไม่ใช่ `RequireOpenFiscalPeriodAsync`) · `:12388-12394` (catch ทั้งก้อน LogError แล้วเดินต่อ) · `:12326-12340` (reclass `net21913` ทั้งหมด ไม่ดูยอดใบเสร็จ) · จุดเรียก `:5197-5200`
- โค้ด: `var period = await ResolveFiscalPeriodAsync(companyId, when);` · `catch (Exception ex) { _logger.LogError(ex, "TryReclassifyUndueOutputVatAsync ล้มเหลว …"); }` · `inv.OutputVatDueAt = when;`
- ทำไมพัง: (ก) C-T04 (✅ 802f149) เปลี่ยน 9 เส้นเป็น `RequireOpenFiscalPeriodAsync` แต่เมธอดนี้ (และ `:11659` งาน §82/3 หมดสิทธิ์เคลม) ยังใช้ตัวเดิม ⇒ ใบเสร็จลงวันที่ย้อนเข้างวดปิด (แก้วันที่เอกสารได้) → JV reclass ผูก FiscalPeriodId ของงวดปิด (ข) ถ้า reclass ล้ม (เช่นเลข JV ชน) → ใบเสร็จอนุมัติสำเร็จ, VAT ค้าง 21913, INV ไม่เข้า ภ.พ.30 — ร่องรอยอยู่ใน log เท่านั้น (CLAUDE.md: "`LogWarning` แล้วเดินต่อ = กลืน error") (ค) §78/1 tax point บริการ = "ได้รับชำระ" ต่องวดชำระ — รับ 30% แล้ว reclass VAT 100% เข้า ภ.พ.30 เดือนนั้น = นำส่งก่อนกำหนด (ไม่โดนปรับ แต่รายงานภาษีขายเดือนนั้น ≠ ใบกำกับที่ออกจริงตอนรับเงิน และถ้าลูกค้าไม่จ่ายส่วนที่เหลือต้องขอคืน)
- ผลกระทบ: (ก) งบที่ยื่นกับ GL ไม่ตรง (ข) VAT หลุดจากแบบเงียบ (ค) กระแสเงินสด/รายงานภาษีขายผิดงวดในเคสแบ่งชำระ
- defect class: "แก้ตัวเดียว เหลือที่เหลือ" (C-T04) · "ห้าม catch{} กลืน error ใน JE path" · "ด่านที่เขียนไว้ครึ่งเดียว"
- ทางแก้ที่เสนอ: ใช้ `RequireOpenFiscalPeriodAsync` · ถ้า reclass ล้มให้ `AppendInternalNote` บนใบเสร็จ+ใบแจ้งหนี้ และตอบกลับผู้ใช้ (3 ที่ตามกติกา) · reclass ตามสัดส่วน `receipt.TotalAmount / inv.TotalAmount` (หรือตาม VAT ที่กรอกบนใบเสร็จ) แล้ว stamp เมื่อครบ
- ความมั่นใจ: สูง สำหรับ (ก)(ข) · กลาง สำหรับ (ค) — ต้องถามนักบัญชีว่านโยบายบริษัทเลือก "ออกใบกำกับเต็มตอนรับงวดแรก" หรือไม่

### C-03 [P2][S] ใบลดหนี้/เพิ่มหนี้ฝั่งซื้อตัดเจ้าหนี้ที่ "212" (prefix ต่ำสุด = 21210) เสมอ — ไม่ตรงบัญชีที่ใบเดิม Cr ไว้ (Expense → 21220 · contact pinned) และเงินสดใช้ "111" ไม่ใช่ `moneyAccount`
- ไฟล์: `DocumentService.cs:13286-13292` · เทียบ `ResolvePayableAccountAsync` `:11356-11373` และ `moneyAccount` `:12915-12930`
- โค้ด: `counterAccCode = isCashSettlement ? "111" : "212"; … var counterAcc = await FindAccountAsync(companyId, counterAccCode);` (ไม่ส่ง `doc.Contact` ⇒ `DefaultApAccountId` ของคู่ค้าไม่ถูกใช้ด้วย) ขณะที่ PI/Expense/PV ใช้ `ResolvePayableAccountAsync(companyId, sourceType, doc.Contact)` และเงินสดใช้ `moneyAccount` (PaymentAccountId > BankAccountId > 111)
- ทำไมพัง: Expense ตั้งหนี้ Cr 21220 (เจ้าหนี้อื่น) → CN ซื้ออ้าง Expense Dr 21210 (เจ้าหนี้การค้า) ⇒ 21220 ค้างเกิน, 21210 ติดลบ เท่ายอด CN · CN แบบเงินสดที่คืนเข้าธนาคาร → GL ลง 111 เงินสด ไม่ใช่บัญชีธนาคารที่เลือก ⇒ กระทบยอดธนาคารไม่ตรง
- ผลกระทบ: รายงานอายุเจ้าหนี้/กระทบยอดธนาคารผิด (ไม่ใช่ภาษี) — ยอดรวมหนี้สินถูก แต่แยกบัญชีผิด
- defect class: "Resolver กลาง ห้ามคำนวณเอง" (มี resolver อยู่แล้วแต่ branch นี้ inline เอง)
- ทางแก้ที่เสนอ: ฝั่งซื้อ → `ResolvePayableAccountAsync(companyId, source?.DocumentType ?? PurchaseInvoice, doc.Contact)` · เงินสด → `moneyAccount` · ฝั่งขายส่ง `doc.Contact` เข้า `FindAccountAsync("113", doc.Contact)` ให้ตรงใบแจ้งหนี้
- ความมั่นใจ: สูง

### C-04 [P1][M] JE ที่สร้างตรงด้วย `new JournalEntry` โดยไม่ผ่าน `JournalEntryBuilder` — 50 จุด, มี **ด่าน Dr=Cr ก่อน save จริงแค่ 11 จุด**; อย่างน้อย 4 จุดสร้าง JE ไม่สมดุลได้จริงแล้ว `Status=Posted`
- ไฟล์ (จุดที่พิสูจน์ได้ว่าไม่สมดุลได้): `Accounting/Services/Implementations/FinancialManagementService.Part2.cs:318-345` (เพิ่มทุน) · `:442-475` (ขายเงินลงทุน) · `Accounting/Services/Implementations/IntegrationService.cs:2440-2495` (CN ผ่าน integration) · `:2525-2580` (DN ผ่าน integration)
- โค้ด: เพิ่มทุน — `lines.Add(new() { AccountId = cashAcc.Id, DebitAmount = entity.PaidAmount …}); lines.Add(new() { AccountId = capitalAcc.Id, CreditAmount = capitalAmount …}); if (entity.SharePremium > 0 && premiumAcc != null) lines.Add(… CreditAmount = entity.SharePremium …)` แล้ว `TotalDebit = totalDr, TotalCredit = totalCr` **ไม่มี if เทียบ** · ขายเงินลงทุน — `if (gainAcc != null) lines.Add(… CreditAmount = entity.GainLoss …)` ไม่มี else · Integration CN — `DebitAmount = document.SubTotal` + `if (vatAccount != null …) DebitAmount = document.VatAmount` / `CreditAmount = document.TotalAmount`
- ทำไมพัง: (1) เพิ่มทุน: ผังไม่มี 3103 (ส่วนเกินมูลค่าหุ้น) → บรรทัด premium หาย → Dr เงินสด 1,500,000 / Cr ทุน 1,000,000 → บันทึกเป็น Posted ทั้งที่ Dr≠Cr; หรือ `PaidAmount ≠ Qty×Par + Premium` (กรอกมือ) ก็ผ่านเหมือนกัน (2) ขายเงินลงทุน: ไม่มี 4303/5711 → กำไร/ขาดทุนหายจาก JE (3) Integration CN/DN: `TotalAmount = SubTotal + VAT − WHT` (ตามสูตรเอกสาร) ⇒ ใบที่มี WHT Dr = Sub+VAT แต่ Cr = Sub+VAT−WHT → ต่างเท่า WHT; และเมื่อ `ResolveVatAccountAsync` คืน null ทั้งที่ VAT>0 ก็ต่างเท่า VAT — เทียบ `IntegrationService.cs:1380-1383` และ `:3639-3651` ที่ **มี** ด่านตรวจในไฟล์เดียวกัน = สองมาตรฐานในไฟล์เดียว
- ผลกระทบ: งบทดลองไม่ลงตัว (TB out of balance) โดยไม่มี error ตอนบันทึก — ตรวจพบทีหลังจาก `AccountingService.cs:2470` (health check นับ "ใบสำคัญไม่สมดุล") ซึ่งบอกจำนวนแต่ไม่บอกต้นทาง · `JournalEntryBuilder` (`Journal/JournalEntryBuilder.cs:92-99`) มีด่านครบ (Dr=Cr ภายใน 0.01 + ≥2 บรรทัด) แต่ถูกใช้จริงน้อยมาก — grep พบ 16 ไฟล์อ้างชื่อ แต่ `new JournalEntry` ตรง ๆ ยังอยู่ 50 จุดใน 20 ไฟล์
- defect class: "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" (Builder) · "ด่านที่ครอบแค่ทางเดียว คือด่านที่ไม่มี" · "แก้ตัวเดียว เหลือที่เหลือ"
- ทางแก้ที่เสนอ: (ก) ด่านเชิงโครงสร้าง — `SaveChanges` interceptor/`DbContext.SaveChangesAsync` override ตรวจทุก `JournalEntry` ที่ Added: `|ΣDr−ΣCr| ≤ 0.01 && Lines ≥ 2` มิฉะนั้น throw (ครอบทั้ง 50 จุดพร้อมกัน ไม่ต้องไล่แก้ทีละที่) (ข) checker `tools/journal_entry_builder_check.py` ฟ้อง `new JournalEntry` นอก Builder (allow-list จุดที่ตรวจแล้ว) (ค) แก้ 4 จุดข้างบนให้ throw เมื่อผังขาด
- ความมั่นใจ: สูง (อ่านโค้ด 4 จุดครบ) — จุดที่ "ไม่มี guard" อื่น ๆ ส่วนใหญ่เป็น JE 2 บรรทัดยอดเดียวกัน (PettyCash, Loan, RevenueRecognition, FMS 139-667) = สมดุลโดยโครงสร้าง ไม่ใช่บั๊ก; `GatewaySettlementService.cs:114-127` รวม `plan.Lines` โดยไม่ตรวจ — ต้องเช็คว่า plan builder ตรวจไว้แล้วหรือไม่ (ยังไม่ได้เปิด)

### C-05 [P1][M] จำหน่ายสินทรัพย์ (`DisposeAsync`) ไม่มี VAT ขาย — ขายทรัพย์สินโดยผู้จด VAT ต้องออกใบกำกับ + ภาษีขาย 7% (§77/1(8), §77/2) แต่ระบบรับ `DisposalAmount` เป็นกำไรทั้งก้อน และไม่สร้างเอกสารใด
- ไฟล์: `Accounting/Services/Implementations/FixedAssetService.cs:368-495` (grep `Vat|21911|ภาษีขาย` ในช่วงนี้ = 0 จุด)
- โค้ด: `var gainLoss = request.DisposalAmount - asset.NetBookValue;` · Dr เงินสด `request.DisposalAmount` / Cr 43030 `gainLoss` — ไม่มีบรรทัด 21911, ไม่มี `Document` (TaxInvoice) ให้ `TaxService` เห็น
- ทำไมพัง: ขายรถยนต์บริษัท 214,000 (รวม VAT) → ระบบลงกำไร 214,000−NBV ทั้งก้อน · ภาษีขาย 14,000 ไม่เข้า 21911 และไม่เข้า ภ.พ.30 · ไม่มีใบกำกับภาษีให้ผู้ซื้อ ⇒ นำส่ง VAT ขาด + กำไรทางบัญชี/ภาษีสูงเกิน 14,000 · ยกเว้นเฉพาะทรัพย์สินที่ซื้อมาโดยไม่มีสิทธิเคลม (รถยนต์นั่ง §82/5(6)) ซึ่งก็ต้องคิด VAT ตอนขายอยู่ดี (เว้นแต่เข้ากรณีคำสั่ง ป.86 ไม่เข้าเลย) — ระบบไม่มีช่องให้ตัดสินเลย
- ผลกระทบ: เบี้ยปรับ §89(4) 1 เท่า + เงินเพิ่ม 1.5%/เดือน บนภาษีขายที่ขาด · ของแถม: `if (asset.AssetAccountId.HasValue && …AccumulatedDepreciationAccountId.HasValue)` ไม่มี `else` (`:386`) → สินทรัพย์ที่ไม่ผูกผังถูกตั้ง `Disposed`/NBV=0 (`:481-482`) โดย**ไม่มี JE เลย** = silent no-op ทะเบียนวิ่งหนี GL
- defect class: "ค่า default ที่แต่งขึ้น" (DisposalAmount = กำไรล้วน) · "ห้าม silent no-op"
- ทางแก้ที่เสนอ: ให้จำหน่ายแบบขายเดินผ่าน `IDocumentService` (TaxInvoice บรรทัด "ขายทรัพย์สิน" ผูก `FixedAssetId`) แล้ว JE ตัดทรัพย์สินอ้าง `DisposalDocumentId` — ตามกฎ "โมดูลที่มีเงินห้ามออกเอกสาร/เลขเอง" · ผังไม่ครบ → throw
- ความมั่นใจ: สูง ว่าไม่มี VAT ในโค้ด · กลาง ว่าลูกค้าทุกรายเข้าข่าย (ต้องให้นักบัญชียืนยันเคส ป.86)

### C-06 [P1][M] ค่าเสื่อม: ไม่มี pro-rata เดือน/วันแรก · แผน (`BuildScheduleRows`) กับที่โพสต์จริง (`CalculateDepreciationAsync`) เป็น**สองอัลกอริทึม**ที่ให้ผลต่างกัน · ทบทวนอายุ/ตีราคาใหม่ไม่ prospective ตาม TFRS NPAEs บทที่ 10
- ไฟล์: `FixedAssetService.cs:640-661` (posting) · `:167-216` (plan) · `:586-608` (`AdjustUsefulLifeAsync` เซ็ต `UsefulLifeMonths` ใหม่ทั้งก้อน) · `:807-811` (`RevalueAsync` `PurchaseCost += surplus`) · grep `Math.Round` ทั้งไฟล์ = **0 จุด**
- โค้ด: posting `if (asset.PurchaseDate > periodStart) continue;` + `StraightLine => (asset.PurchaseCost - asset.SalvageValue) / asset.UsefulLifeMonths` (ไม่มี switch-to-SL) · plan `var periodDate = asset.PurchaseDate.AddMonths(i + 1);` + block switch-to-SL (`:194-203`)
- ทำไมพัง (simulation `team/C/sim_depreciation.py` รันแล้ว): (1) **เดือนแรก**: ซื้อวันที่ 1 → posting คิดเดือน M เต็ม, plan เริ่ม M+1 · ซื้อวันที่ 2–31 → posting ข้ามเดือน M (`PurchaseDate > periodStart`) ทั้งที่ พ.ร.ฎ.145 ม.4 ให้เฉลี่ยตามวัน (ทั้งสองเส้นไม่มี pro-rata) ⇒ ค่าเสื่อมปีแรกคลาดได้ถึง 1/12 ของปี ⇒ กำไรสุทธิทางภาษี ภ.ง.ด.50 คลาด (2) **DB/DDB**: plan ถึง 0 ที่งวด 60 (switch-to-SL) แต่ posting งวด 60 = 741.95 (plan 2,000) และ NBV ค้าง 43,775 หลังสิ้นอายุ ยังโพสต์ต่องวด 61+ (`asset.Status==Active` ไม่ดูอายุ) (3) **ทบทวนอายุ 60→36 หลังใช้ 24 เดือน**: ระบบคิด 120,000/36 = 3,333/เดือน ควร NBV 72,000/12 = 6,000 (TAS16 prospective) ⇒ ถึง 0 ที่เดือน 46 ไม่ใช่ 36 (4) **ตีราคาใหม่**: `(PurchaseCost+surplus)/60` ไม่หารเดือนที่เหลือ ⇒ ถึง 0 ที่เดือน 65; ส่วนเกินค่าเสื่อมจากส่วนเกินทุนตีราคาต้องบวกกลับ §65 ทวิ(3) แต่ไม่มีช่องเก็บ (5) **ปัดเศษ**: 100,000/36 = 2777.777… ส่งเข้า `JournalEntryLine` ไม่ปัด → DB เก็บ 2 ตำแหน่ง แต่ `asset.AccumulatedDepreciation` ในหน่วยความจำสะสมค่าไม่ปัด ⇒ ทะเบียน ≠ GL หลักสตางค์ทุกเดือน
- ผลกระทบ: (1)(3) กระทบภาษีเงินได้นิติบุคคลโดยตรง · (2)(4) งบการเงินผิดมาตรฐาน · (5) กระทบยอดทะเบียน-GL ไม่ตรง
- defect class: "สอง renderer ห้าม drift" (plan vs posting) · "ลืม `AwayFromZero`" (ที่นี่ไม่ปัดเลย) · "ผลตรวจ §10 ข้อ 9 ยังไม่เคยอ่าน"
- ทางแก้ที่เสนอ: pure class `Helpers/DepreciationCalculator` ตัวเดียว (input: cost, salvage, life, method, purchaseDate, accumulated, remainingMonths, month) ให้ทั้ง plan/posting/report เรียก + เทสต์ตัวเลขจาก sim · เก็บ `DepreciationStartDate` + `RemainingMonths` ตอน adjust/revalue · `Math.Round(…, 2, AwayFromZero)` ที่ตัวเดียว
- ความมั่นใจ: สูง (sim reproduce ครบ 4 เคส)

### C-07 [P1][S] หนังสือรับรอง 50 ทวิ / ไฟล์ ภ.ง.ด.3/53 ใช้ยอด**สกุลเงินเอกสาร** — เอกสารต่างสกุลได้ 50 ทวิ และไฟล์ยื่นเป็น USD
- ไฟล์: `Accounting/Services/Implementations/WithholdingTaxCertService.cs:404-592` (grep `ExchangeRate|ToGlAmount|Currency` = 0 จุด) · จุดเรียก `DocumentService.cs:10786-10790` ส่ง `payment.WithholdingTaxAmount` ซึ่งเป็นสกุลเอกสาร (Payment มี `ExchangeRate` ของตัวเอง `:8321,:10761,:14755` แสดงว่ายอดไม่ใช่บาท) · `TaxFilingExportService.cs:632-679` ใช้ `line.IncomeAmount/TaxAmount` ตรง ๆ
- โค้ด: `IncomeAmount = Prorate(line.Amount), TaxAmount = Prorate(line.WithholdingTaxAmount)` — `line.Amount` เป็นสกุลเอกสาร ขณะที่ `AutoPostToJournalAsync` Cr 21918 ด้วย `Conv(thisWht)` เป็นบาท
- ทำไมพัง (sim `team/C/sim_fx_tax.py`): ใบซื้อบริการ USD 1,000 หัก 15% (ม.70) rate 36.50 → GL Cr WHT ค้างจ่าย 5,475 บาท แต่ 50 ทวิ/ภ.ง.ด.54 ระบุ 150 (หน่วย USD ที่ไม่มีบอก) ⇒ นำส่งขาด 5,325 บาท/ใบ หรือถ้าผู้ใช้จ่ายตาม GL ไฟล์ยื่นก็ไม่ตรงยอดที่นำส่ง
- ผลกระทบ: ภ.ง.ด.54 (จ่าย ตปท. — เคสที่ต่างสกุลเกือบทั้งหมด) และ 50 ทวิ ผิดทุกใบต่างสกุล · เงินเพิ่ม 1.5%/เดือน
- defect class: "ตัวเลขคู่ที่ต้องสอดคล้องกัน ห้ามมาจากคนละแหล่ง" (GL แปลงบาท · cert ไม่แปลง) · ญาติ 327e879 ที่แก้ GL แต่ไม่ได้ไล่ปลายทางภาษี
- ทางแก้ที่เสนอ: cert เก็บ `IncomeAmount/TaxAmount` เป็นบาทผ่าน resolver เดียวกับ `Conv` (ใช้ rate ของ **วันจ่าย** = `payment.ExchangeRate ?? doc.ExchangeRate` ตาม ม.65 ทวิ(5)) + เก็บ `OriginalCurrency/OriginalAmount` ไว้พิมพ์ · migration ตรวจ cert ที่ผูกเอกสาร `Currency != THB`
- ความมั่นใจ: สูง (grep ยืนยันไม่มีการแปลงเลย) — ต้องเช็ค `TaxService.GenerateWhtReport` ด้วยว่าอ่านจาก cert หรือจาก doc (น่าจะเหมือนกัน)

### C-08 [P2][S] 50 ทวิ: เลขที่ใบ `WHT-YYYYMM-####` ออกด้วย read-max+1 ไม่มี lock (ทั้ง `CreateAsync` และ `AutoGenerateFromDocumentAsync`) · auto-gen ล้มถูก `catch` แล้ว log อย่างเดียว
- ไฟล์: `WithholdingTaxCertService.cs:479-491` (`autoExisting … Max() + 1`) · `DocumentService.cs:10791-10795` (`catch (Exception ex) { _logger.LogWarning(… "ไม่ block การชำระ") }`) · unique index `AccountingDbContext.cs:1438`
- ทำไมพัง: (1) การชำระพร้อมกัน 2 รายการเดือนเดียวกัน → เลขซ้ำ → unique index โยน → เข้า catch → **50 ทวิ ไม่ถูกออก** โดยผู้ใช้เห็นว่า "ชำระสำเร็จ" (2) `Helpers/SequenceNumber` (f474535) มีอยู่แล้วแต่จุดนี้ไม่ได้ใช้ — `sequence_lock_check.py` ไม่จับเพราะไม่ใช่ `OrderByDescending` (ใช้ `int.TryParse…Max()`) · ผู้ขายไม่ได้ 50 ทวิ = ผู้จ่ายผิด §50 ทวิ ปรับ 2,000/ฉบับ
- defect class: "เรียงเลขรันเอง ไม่มีล็อก" (ทรง Max+1) · "`LogWarning` แล้วเดินต่อ = กลืน error"
- ทางแก้ที่เสนอ: `SequenceNumber.NextAsync` + เมื่อ auto-gen ล้มให้ `AppendInternalNote` บนเอกสาร + ธง `WhtCertPending` ให้หน้า "50 ทวิ ค้างออก" (`GetPendingDocumentsAsync` มีอยู่แล้ว) เห็น
- ความมั่นใจ: สูง · ขยาย checker `sequence_lock_check.py` ให้จับ `Select(int.TryParse…).Max() + 1` ด้วย (negative test: จุดนี้)

### C-09 [P2][S] งานค่าเสื่อมอัตโนมัติคิดเฉพาะ "เดือนก่อน" — เดือนที่หลุด (server ดับ/บริษัทเพิ่งผูกผัง/นำเข้าทะเบียนย้อนหลัง) ไม่มีวันถูกโพสต์ และ DB/DDB ที่โพสต์ข้ามลำดับให้ค่าคนละตัว
- ไฟล์: `Accounting/Services/Background/DepreciationBackgroundService.cs:71-74` (`target = firstOfThisMonth.AddMonths(-1)` ค่าเดียว) · idempotent ต่อ (asset, year, month) ผ่าน `IsPosted` (`FixedAssetService.cs:650-653`) ✅ · `JobLock.RunExclusiveAsync` ✅ (`:63`)
- ทำไมพัง: ทะเบียนนำเข้าเดือน มี.ค. ซื้อ ม.ค. → job เม.ย. คิดเฉพาะ มี.ค.; ม.ค./ก.พ. ไม่มีใครคิด และ `asset.NetBookValue` (ฐานของ DB) ถูกหักเฉพาะเดือนที่โพสต์ ⇒ ค่าเสื่อมปีขาด 2 เดือน · `GetDepreciationScheduleAsync` (plan) โชว์ครบทำให้ผู้ใช้เข้าใจว่าลงแล้ว
- defect class: "watermark ต่อบริษัท" (CLAUDE.md D — งานสแกนทั้งฐานต้องมี watermark) · "ของที่มีอยู่แต่ผู้ใช้ไม่รู้ว่าต้องกดเอง"
- ทางแก้ที่เสนอ: job ไล่จากเดือนแรกที่ยังไม่ `IsPosted` ของแต่ละบริษัทถึงเดือนก่อน (watermark) + หน้าทะเบียนโชว์ "งวดที่ยังไม่โพสต์"
- ความมั่นใจ: สูง

### C-10 [P2][S] `FinancialManagementService.GetFiscalPeriodIdAsync` กรอง `Status == Open` แล้วคืน null — JE 13 จุด (เพิ่ม/ลดทุน · ปันผล · สำรองตามกฎหมาย · CIT · เงินลงทุน) โพสต์เข้างวดปิดได้โดย `FiscalPeriodId = null` (ดูเหมือนมีด่านแต่เป็นการซ่อน)
- ไฟล์: `Accounting/Services/Implementations/FinancialManagementService.cs:27-32` · ผู้เรียก `FinancialManagementService.cs:139,215,285,424,565,612,667` + `.Part2.cs:110,205,235,337,387,466` (ทุกจุด `EntryDate = DateTime.UtcNow`) · ญาติ: `TaxService.RdCompliance.cs:150` (`EntryNumber = "VR-…-{Guid[..6]}"` เลขสุ่ม ไม่ผ่าน `NextJournalNumberAsync` + ไม่มีด่านงวด) · `DocumentService.cs:11659` (งาน §82/3 หมดสิทธิ์เคลม ใช้ `ResolveFiscalPeriodAsync` ไม่ใช่ `RequireOpen…`)
- โค้ด: `.FirstOrDefaultAsync(f => … && f.Status == FiscalPeriodStatus.Open); return fp?.Id;`
- ทำไมพัง: งวดปัจจุบันถูกปิด (เช่นปิดเดือนก่อนวันสิ้นเดือน หรือรันงานปิดปีแล้ว) → ผู้ใช้บันทึกปันผล/CIT วันนี้ → ไม่ throw, JE Posted โดย `FiscalPeriodId=null` ⇒ query ตามงวด/ปิดปี/XBRL มองไม่เห็น JE นี้ แต่ TB เห็น = งบสองชุดไม่ตรงกัน (เคสเดียวกับที่ `FixedAssetService.cs:741-748` เขียนคอมเมนต์ไว้ว่าเคยพัง)
- defect class: "ด่านที่เขียนไว้ครึ่งเดียว อันตรายกว่าไม่มีด่าน" · "แก้ตัวเดียว เหลือที่เหลือ" (C-T04 ✅ แต่ยังเหลือ 3 ไฟล์)
- ทางแก้ที่เสนอ: ย้ายทั้งหมดไป `JournalEntryBuilder.PostAsync` (มีด่านงวดปิด + เลข JV ล็อกแล้ว `JournalEntryBuilder.cs:104-112`) — ตัวเดียวปิด C-04 กับข้อนี้พร้อมกัน
- ความมั่นใจ: สูง (โค้ดชัด) · ความน่าจะเกิดกลาง (ต้องปิดงวดปัจจุบัน)

### C-11 [P3][S] CIL ที่แปลงจาก Expense/PI (settlement) ไม่ทำ cross-rate เหมือน PV — Dr AP ที่ rate ของ CIL ทั้งที่ AP ตั้งไว้ที่ rate ต้นทาง · `lineProjects.Add` นอก `if` ทำ index เหลื่อม
- ไฟล์: `DocumentService.cs:13466-13494` (CIL settlement ใช้ `AddLine` → `Conv` rate ของ CIL ทั้ง Dr AP และ Cr เงินสด; ไม่มี `PvSrcThb`/FX G/L เหมือน `:14219-14283`) · `:13735-13737`, `:13760-13762` (`if (moneyAccount != null) pendingLines.Add(…);` แต่ `lineProjects.Add(doc.ProjectId);` อยู่นอก if)
- ทำไมพัง: PI USD 1,000 @35 → AP 35,000; CIL จ่าย @36 → Dr AP 36,000 ⇒ AP เหลือ −1,000 ถาวร แทนที่จะเป็น Dr AP 35,000 + Dr ขาดทุน FX 1,000 · ส่วน index เหลื่อมเกิดเฉพาะเมื่อไม่มีบัญชี 111 (JE จะไม่สมดุลและ throw อยู่แล้ว) จึงไม่มีผลจริง แต่เป็นกับดักถ้าใครแก้ให้ผ่าน
- defect class: "แก้ตัวเดียว เหลือที่เหลือ" (327e879 ทำ cross-rate ให้ PV/Receipt แต่ไม่ CIL)
- ทางแก้ที่เสนอ: ให้ CIL settlement เรียก helper เดียวกับ PV settlement (แยกเป็นเมธอด `PostSettlementLinesAsync`) · ย้าย `lineProjects.Add` เข้าใน if
- ความมั่นใจ: สูง · ความสำคัญต่ำ (CIL ต่างสกุลหายาก)

### C-12 [P2][สงสัย] ภ.พ.30: ใบเสร็จ "ถือ VAT" หลายใบต่อใบแจ้งหนี้ undue เดียว — แต่ละใบถูกรายงานด้วย `VatAmount` ของตัวเอง; ถ้าใบเสร็จงวดที่ 2 คัดลอก VAT เต็มใบมา (convert) จะนับซ้ำ
- ไฟล์: `TaxService.cs:423-437` (`invoiceIdsOwnedByVatReceipt` = ทุกใบเสร็จ VAT>0 ที่อ้าง INV) · `:546-564` (ใบเสร็จแต่ละใบ → 1 แถวเต็ม `doc.VatAmount`) · คู่กับ C-02(ค) ที่ GL reclass เต็มก้อนตั้งแต่ใบแรก
- ต้องเช็คต่อ: `ConvertDocumentAsync` Invoice→Receipt แบบรับบางส่วน คำนวณ `VatAmount` ของใบเสร็จตามสัดส่วนหรือคัดลอกเต็ม (ยังไม่ได้เปิด) — ถ้าตามสัดส่วนข้อนี้ไม่ใช่บั๊ก แต่ผลรวมแถวจะ ≠ 21911 ที่ reclass เต็มตั้งแต่งวดแรก (ต่างงวด)
- ความมั่นใจ: สงสัย

## ตรวจแล้วไม่ใช่บั๊ก
- **JE ของ 5 branch สมดุลเชิงพีชคณิต** — sim `team/C/sim_je_branches.py` (20,000 เคส × 7 รูปแบบ): THB (fx=1) Dr=Cr เป๊ะทุกเคสเพราะ `Conv` ไม่ปัด; FX ต่างสูงสุด 0.03 < `roundingTol` ⇒ squeeze ดูดเข้าบรรทัดยอดสูงสุดถูกต้อง · `Math.Round` ใน `AutoPostToJournalAsync` **ทุกจุด** (12954 · 13518 · 13728 · 14000 · 14224 · 14435 · 14469 · 14492) มี `AwayFromZero` และไม่มีสูตร `×0.07` ในเมธอดนี้ (VAT มาจากบรรทัดเอกสาร)
- **PV settlement / Receipt settlement**: Cash vs Accrual basis กระจก PI/Invoice ถูกต้อง (AP gross+WHT ↔ WHT ค้างจ่ายรับรู้ตอนจ่าย) · cross-rate + realized FX ถูกฝั่ง (จ่ายบาทมากกว่า AP = ขาดทุน Dr) · WHT payable แยก ภ.ง.ด.3/53/54 ผ่าน `ResolveWhtPayableAccountAsync`
- **PV standalone §83/6**: Cr เงินสด = Total − VAT ประเมินเอง (ผู้ขาย ตปท. ไม่เก็บ VAT ไทย) + Cr 21912 + Dr 11640 — ถูก
- **GRN**: ไม่ลง VAT/WHT (รอ PI) · Dr line / Cr 21240 GR-NI · ผัง GR-NI ไม่มี → throw ก่อน stock move (ถูกตามกฎ fail loud)
- **Receipt/RV standalone**: มัดจำ deferred → 21913 · ทันที → 21911 · WHT Dr 11910 — ถูก
- **CIL standalone**: VAT รวมเป็นต้นทุนรายบรรทัด (§82/4) + residual ที่หัวใบ — ถูก
- **ด่านโครงสร้าง JE** (`JournalPostingGuard`) + ด่านงวดปิดใน AutoPost (`:14528-14538`) มีจริงและถูกเรียก
- **50 ทวิ ต่องวดจ่าย**: idempotency ระดับ (payment, document) + กันใบเต็มจำนวนซ้อนใบรายงวด (B2) + void payment → void cert ผูก `SourcePaymentId` (`DocumentService.cs:8385-8400, 8532-8545`) — ถูก · 2 ฉบับ (`PdfGenerationService.cs:2317-2318`) มี · threshold ฿1,000 สะสม = C-T05 ✅
- **`PndTextFileFormat`**: ตัวเดียวถูกเรียกจาก 2 ทางออก (Export + e-Filing) ตามกฎ "สอง renderer" · ปัด 2 ตำแหน่ง AwayFromZero · UTF-8 ไม่มี BOM (ต้อง verify กับ RD ว่ารับ UTF-8 — ยังไม่ยืนยันกฎหมาย)
- **ภ.พ.30 Receipt vs TaxInvoice นับซ้ำ**: มี dedup — ใบเสร็จอ้าง TaxInvoice ไม่นับ · INV ที่ใบเสร็จถือ VAT ถูก `continue` (`TaxService.cs:505`) · ใบเสร็จอ้าง QT/BN นับเป็นขายสด — ถูก (ยกเว้นข้อสงสัย C-12)
- **งานค่าเสื่อม**: `JobLock.RunExclusiveAsync` + `IsPosted` ต่อ (asset, ปี, เดือน) idempotent · สินทรัพย์ไม่ผูกผังไม่ถูก mutate · ด่านงวดปิดมี
- **`GatewaySettlementMath`**: plan สมดุลเชิงพีชคณิต (Bank expectedNet + Fee (net+wht) = Clearing gross + WHT) · ยอดโอนจริงต่างเกิน tolerance → Blocked ไม่เดา — ถูก (สูตร gross-up WHT `fee×0.03/0.97` = "ออกให้ตลอดไป" เป็นนโยบายที่ต้องให้นักบัญชียืนยัน ไม่ใช่บั๊ก)
- `WhtCertAmountResolver` — ด่าน "ยอดภาษี < ยอดจ่าย" + Unknown → Pending ถูกตามกฎ "ไม่รู้ = บอกว่าไม่รู้"

## ซ้ำกับ SYSTEM_REVIEW
- C-T04 (ด่านงวดปิด) ✅ 802f149 แต่**ยังเปิดอยู่จริงบางส่วน** — เหลือ `TryReclassifyUndueOutputVatAsync` (C-02), งาน §82/3 (`:11659`), FMS 13 จุด + RdCompliance (C-10)
- C-T14/T16 (ปัดเศษ) — รอบนี้ยืนยันว่า AutoPost ครบ; ที่ขาดคือ `FixedAssetService` ไม่ปัดเลย (C-06 ข้อ 5) = จุดใหม่
- H-A15 (JE ไม่ติด BranchId) — ยังเปิด (AutoPost `:14557-14575` ไม่มี `BranchId`)
- §10 ข้อ 6/7/9 — ครอบแล้วในรายงานนี้ (PV/GRN/CIL/RV/DN branch · WHT cert/PND · FixedAsset)

## ยังไม่ได้อ่าน
- `ApproveDocumentAsync` เต็ม (นอกจากช่วง 5176-5240) · `VoidDocumentAsync` (กลับ JE + 21913 + cert) · Convert core (VAT ตามสัดส่วนของใบเสร็จบางส่วน — ต้องเปิดเพื่อตัดสิน C-12)
- `PurchaseInvoice/Expense` branch (13535-13678) และ Invoice deposit-apply (13790-14130) — อ่านผ่านเฉพาะจุดอ้าง
- `TaxService.GenerateWhtReport` (ภ.ง.ด.3/53/54 บนจอ) ว่าอ่านจาก cert หรือ doc · `WhtCreditService` ฝั่งเครดิต · `TaxService.EFiling.BuildPndAsync` 142-160 (filter Issued/Filed?)
- `RevalueAsync` JE เต็ม · `WriteOffAsync` · `ImportAsync` ของสินทรัพย์ · หน้าเว็บ fixed-assets.html (ป้าย/option)
- ภ.พ.30 ฝั่งซื้อ (11610/11640 → รายงานภาษีซื้อ) · §65 ตรี validator call sites · งวดปิดของ Payroll ผ่าน `AccountingService.CreateJournalEntryAsync` (มีด่าน `:434` ✅ แต่ไม่ได้ไล่ทุกทาง)

## ข้อเสนอเชิง ERP
1. **ชั้น posting เดียว** — ทุก JE ต้องผ่าน `JournalEntryBuilder.PostAsync` (มี Dr=Cr · ≥2 บรรทัด · เลขล็อก · ด่านงวด) + `SaveChanges` interceptor เป็นตาข่ายสุดท้าย + checker allow-list; วันนี้ 50 จุดเขียนเองคนละมาตรฐาน
2. **Sub-ledger ↔ GL reconciliation report** อัตโนมัติรายวัน: AR/AP aging vs 113/212 · ทะเบียนสินทรัพย์ vs 12xxx/ค่าเสื่อมสะสม · 21913/11640 vs เอกสารที่ยังไม่ถึง tax point · 21911 vs ภ.พ.30 — บั๊ก C-01/C-03/C-06 ทั้งหมดจะโผล่ในรายงานนี้ก่อนถึงสรรพากร
3. **Depreciation engine เป็น pure class** + สองชุดบัญชี (บัญชี TFRS / ภาษี พ.ร.ฎ.145) พร้อม worksheet บวกกลับ §65 ทวิ(3) อัตโนมัติ · pro-rata รายวัน · prospective หลัง adjust/revalue · component accounting
4. **Multi-currency ครบวงจร**: ทุก tax artefact (50 ทวิ · ภ.ง.ด.54 · ภ.พ.36 · ภ.พ.30 ช่อง 7 ส่งออก) ต้องมี `AmountThb` แยกจาก `Amount` สกุลเอกสาร (sim_fx_tax.py) · rate ตามวันจ่ายจริง ม.65 ทวิ(5)
5. **Fixed asset disposal เป็นเอกสารขาย** (TaxInvoice ผูก asset) — ปิด C-05 และทำให้ e-Tax/ภ.พ.30 ครบเอง
6. **Period close checklist** ที่ตรวจ "JE ที่ FiscalPeriodId = null ในช่วงงวด" + "21913/11640 ค้างเกิน N เดือน" + "50 ทวิ ค้างออก" ก่อนอนุญาตปิดงวด
