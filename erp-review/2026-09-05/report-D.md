# ทีม D — ข้อมูลหลัก (master data) และการตั้งค่า
(รอบ 2 · 2026-09-05 · HEAD 444d2cb · อ่านโค้ด + grep + `team/D/dto_roundtrip.py` (รันกับ Contact/Product/Account/Branch/Warehouse/BankAccount/FiscalPeriod/CompanySettings/Company) · ไม่ได้รัน runtime)

## สรุป 5 บรรทัด
1. **ไม่พบ P0** ในชั้น master data — โครง unique index/ด่านลบ/merge ส่วนใหญ่มีอยู่จริง (merge ผู้ติดต่อ repoint FK จาก information_schema ครอบทุกตาราง — ดีมาก) แต่มี **ด่านที่ครอบไม่ครบ/สองมาตรฐาน** หลายจุด
2. **P1 6 ข้อ**: ฟอร์มแก้สินค้าไม่ส่ง SKU/หน่วย/ติดตามสต็อก (silent no-op) · ลบสินค้าที่มีสต็อกได้ → GL 115x กับทะเบียนแตกถาวร · ลบผู้ติดต่อตรวจแค่ 2/20 ตาราง → 500 หรือ **cascade ลบ ConsignmentRecord/PaymentReminder/VendorPortalToken** · **นำเข้าผังบัญชี CSV ตั้ง Level=1/2 ตายตัว ⇒ 59 จุดที่กรอง `Level >= 4` มองไม่เห็นบัญชีที่นำเข้าเลย** (AutoPost/ช่องทางจ่าย/payroll) · ยอดคงเหลือธนาคารถูกคำนวณใหม่จาก `BalanceAfter` ของแถวล่าสุด ⇒ ยอดเปิดบัญชีหาย/ค้างค่าเก่า · ส่งออก/นำเข้าผู้ติดต่อ **ไม่มีคอลัมน์รหัสสาขา** §86/4 และ dedupe ด้วย TaxId เดี่ยว
3. **P2 7 ข้อ**: สร้างงวดบัญชีไม่ตรวจทับซ้อน (ฝั่ง update ตรวจ) · แก้ผู้ติดต่อไม่ตรวจเลขภาษีซ้ำ (ฝั่ง create ตรวจ) · รหัสสินค้าที่ลบแล้วสร้างใหม่ = 500 (unique index ไม่กรอง IsDeleted) · ฟอร์มผังบัญชีเปิดให้แก้ประเภท/บัญชีแม่แต่ไม่ส่ง · ล้างวันเครดิตของผู้ติดต่อไม่ได้ · เครดิตเทอมไม่มี resolver กลาง (settings.DefaultPaymentDueDays ไม่มีใครอ่านฝั่ง server · Integration hardcode 30 วัน 4 จุด) · export CSV ไม่มี BOM (template มี)
4. **defect class เด่น**: "ด่านที่ครอบแค่ทางเดียว" (create vs update · ลบ Contact vs Product · import vs UI) และ "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" (Product.SalesAccountId · Warehouse.BranchId/Phone · PreventPostToClosedPeriod · Contact.PaymentTerms)
5. ซ้ำ SYSTEM_REVIEW: C-R3 (`FiscalYear.RangeFor` ยังเปิด ≥8 จุด) · แถว Onboarding (row 177) **ระบุผิดบางส่วน** — ผังถูก seed ตอนสร้างบริษัทจริง (CompanyService.cs:90 · AuthService.cs:249) และ BranchCode 00000 default ที่ server (CompanyService.cs:48) ไม่ใช่เฉพาะ wizard

## Findings (เรียง P0→P3)

### D-01 [P1][S] products.html แก้ไขสินค้า: ฟอร์มมี SKU/หน่วย/ชนิด/ติดตามสต็อก แต่ payload ตอน update ไม่ส่ง → กดบันทึกแล้วไม่มีผลเงียบ ๆ
- ไฟล์: wwwroot/pages/products.html:595 (payload update) · :524-528 (hydrate `fSku`/`fUnit`/`fType`/`fTrackStock` ตอน openEdit — ช่องเปิดให้แก้ ไม่ disabled) · Services/Implementations/ProductService.cs:170-179 (`if (request.SKU != null)…`, `if (request.Unit != null)…`, `if (request.TrackStock.HasValue)…`)
- โค้ด: `api.updateProduct(id,{name:…,nameEn:…,category:…,sellingPrice:…,costPrice:…,vatRate:…,isVatIncluded:…,minimumStock:…,description:…,printStation:…})` — ไม่มี `sku`, `unit`, `trackStock`, `barcode`, `productType`
- ทำไมพัง: (1) openEdit เติม fSku/fUnit/fTrackStock/fType ให้แก้ได้ → (2) save() ส่งเฉพาะ 10 ช่อง → (3) service เป็น patch-semantics (`!= null`) ⇒ ค่าเดิมคงอยู่ → (4) toast "แก้ไขสำเร็จ" แล้ว reload ค่าเดิมกลับมา
- ผลกระทบ: สินค้าที่สร้างโดยไม่ติ๊ก TrackStock แก้กลับไม่ได้จาก UI ⇒ ไม่มีสต็อกการ์ด/JE สินค้าคงเหลือ · หน่วยนับผิดแก้ไม่ได้ทั้งที่พิมพ์ลงใบกำกับ §86/4
- defect class: "ห้าม silent no-op" (#4 A) + เช็กลิสต์ B "ฟอร์ม → payload"
- ทางแก้: เติม `sku/unit/trackStock/barcode` ใน payload update (UpdateProductRequest รับอยู่แล้ว) · `fType` disabled ตอน edit พร้อม title บอกเหตุผล
- ความมั่นใจ: สูง

### D-02 [P1][M] ลบสินค้าที่ยังมีสต็อกคงเหลือได้ทันที → มูลค่าหายจากรายงานสต็อก แต่ GL 115x ยังถืออยู่ — tie-out ต่างถาวร
- ไฟล์: Services/Implementations/ProductService.cs:192-200 (`DeleteAsync`) · :539-543 (`GetInventoryValuationAsync` กรอง `!p.IsDeleted && p.CurrentStock > 0`) · :572 (`GetInventoryGlTieOutAsync`) · Controllers/ProductController.cs:51-54
- โค้ด: `product.IsDeleted = true; await _db.SaveChangesAsync();` — ไม่ตรวจ `CurrentStock`, `WarehouseStocks`, `DocumentLines`, BOM/Recipe
- ทำไมพัง: (1) สินค้ามีสต็อก 100 ชิ้น × 50 = 5,000 ใน GL 115x → (2) กดลบ (soft) → (3) global filter `!IsDeleted` ซ่อนจาก valuation/stock count/ปรับสต็อก → (4) GL ยังมี 5,000 ไม่มี JE ปรับ · รายงานกระทบยอดต่าง 5,000 ตลอดไป และผู้ใช้กู้ไม่ได้ (หาไม่เจอ)
- ผลกระทบ: งบแสดงฐานะถือสินค้าคงเหลือที่ไม่มีในทะเบียน · ตรวจนับปลายปีไม่ตรง · TFRS NPAEs บทที่ 8
- defect class: สองมาตรฐานในโดเมนเดียว — `DeleteContactAsync` (DocumentService.cs:10153) นับ linked docs แล้ว deactivate แทน แต่ Product ไม่มีอะไรเลย ("ด่านที่ครอบแค่ทางเดียว")
- ทางแก้: บล็อกเมื่อ `CurrentStock != 0 || WarehouseStocks.Any(q≠0) || DocumentLines.Any()` → deactivate + ข้อความบอกจำนวน + ทางไปต่อ (ปรับสต็อกเป็น 0 ก่อน)
- ความมั่นใจ: สูง (ยังไม่ได้เช็ค PosService/BOM ว่าหา product ด้วย IgnoreQueryFilters ไหม)

### D-03 [P1][M] ลบผู้ติดต่อ: ด่านตรวจแค่ Documents(!IsDeleted)+WHT จาก ~20 ตารางที่มี FK → เอกสารที่ลบแล้ว/Payments/Employee ⇒ 500 · และ 3 ตารางไม่ตั้ง OnDelete ⇒ **cascade ลบเงียบ**
- ไฟล์: Services/Implementations/DocumentService.cs:10153-10172 (`DeleteContactAsync`) · Data/AccountingDbContext.cs:850 (Document→Contact `Restrict`) · Models/Entities/AdvancedOperations.cs:557 (`PaymentReminder.Contact = null!` required) · Models/Entities/BankAccount.cs:598 (`VendorPortalToken`), :631 (`ConsignmentRecord`) — ทั้งสามไม่มี `modelBuilder.Entity<…>` config (grep `Entity<VendorPortalToken>`/`Entity<ConsignmentRecord>` = 0; `Entity<PaymentReminder>` @1857 ไม่มีบรรทัด Contact)
- โค้ด: `var docCount = await _db.Documents.CountAsync(d => d.ContactId == contactId && … && !d.IsDeleted); var whtCount = …; if (docCount > 0 || whtCount > 0) {deactivate} … _db.Contacts.Remove(contact);`
- ทำไมพัง: (ก) เอกสาร soft-deleted ยังถือ FK (`Restrict`) → `Remove` → DbUpdateException → middleware ตอบ "เกิดข้อผิดพลาดภายในระบบ" แทนข้อความไทย · เช่นเดียวกับ Payment(Payments.cs `Guid? ContactId`), Employee(Payroll.cs:113), RecurringTransaction, Project(:1655 Restrict) ฯลฯ (ข) EF Core convention: required navigation ที่ไม่กำหนด OnDelete = **Cascade** ⇒ ผู้ติดต่อที่ไม่มี Document/WHT แต่มี `ConsignmentRecord` (ส่งของฝากขายแล้วยังไม่ขาย) / `PaymentReminder` / `VendorPortalToken` → ถูกลบตามไปทั้งแถวโดยไม่มีข้อความเตือน
- ผลกระทบ: (ก) ปุ่มลบพัง 500 ในเคสปกติมาก (ผู้ติดต่อที่เคยมีร่างเอกสารแล้วลบ) (ข) สต๊อกฝากขายหายจากทะเบียน = สินค้าหายเงียบ (เงิน)
- defect class: "ด่านที่เขียนไว้ครึ่งเดียว อันตรายกว่าไม่มีด่าน" · "ทางเข้าที่ผู้ใช้ใช้ผิดวิธีได้ ต้องตอบเป็นคำแนะนำ ไม่ใช่ 500"
- ทางแก้: ใช้ information_schema เหมือน `MergeContactsAsync` (:9905-9918) นับ FK ทุกคอลัมน์ `%ContactId` ก่อนลบ · ตั้ง `OnDelete(Restrict)` ให้ 3 entity · หรือเลิก hard delete ใช้ deactivate เสมอ
- ความมั่นใจ: สูงสำหรับ (ก) · กลางสำหรับ (ข) — ต้องยืนยันว่า migration helper สร้าง FK ของสามตารางนี้ด้วย `ON DELETE CASCADE` จริง (EF convention) ไม่ใช่สร้างมือแบบไม่มี FK

### D-04 [P1][M] นำเข้าผังบัญชี (ImportExportService) ตั้ง `Level = code.Length <= 4 ? 1 : 2` ตายตัว ⇒ ทุก consumer ที่กรอง `Level >= 4` (59 จุด/10 ไฟล์) มองไม่เห็นบัญชีที่นำเข้าเลย
- ไฟล์: Services/Implementations/ImportExportService.cs:640 (`Level = code.Length <= 4 ? 1 : 2`, ไม่มี ParentAccountId) · Services/Implementations/DocumentService.cs:11408-11413 (`FindAccountAsync` exact + prefix ทั้งคู่ `&& a.Level >= 4`) · AccountingService.cs:88-96 (`GetPaymentChannelAccountsAsync` `Level >= 4`) · PayrollService.cs:2273-2276, 2450-2453 (`"54111" && a.Level >= 4`) · grep `Level >= 4` = 59 จุด (RevenueRecognition, Pos, Wht, ExpenseClaim, StatutoryRemittance, GlCandidateBuilder …) · เทียบ ChartOfAccountTemplates.cs:34,107 — บัญชี postable มาตรฐาน Level 4
- ทำไมพัง: (1) tenant ที่ import ผังตัวเอง (CSV 5 หลัก) ได้ทุกบัญชี Level 2 → (2) `FindAccountAsync(companyId,"11310")` exact-match ต้อง Level≥4 → ไม่เจอ → prefix-search ก็กรอง Level≥4 → `null` → (3) AutoPost ตกไป fallback/ข้าม (ตามที่ C ระบุ :12693 LogWarning เดินต่อ) หรือโยน "ไม่พบบัญชี…" ทั้งที่ผู้ใช้เห็นบัญชีนั้นในหน้าผังชัด ๆ · ธนาคารที่นำเข้าไม่โผล่ในช่องทางรับ/จ่าย · จ่ายเงินเดือนหา 54111 ไม่เจอ → JE ขาดบรรทัด → CreateJournalEntryAsync ล้ม "ไม่สมดุล" (PayrollService.cs:2204 คอมเมนต์ยอมรับเอง)
- เส้นทางที่สอง: accounts.html:660-700 (import จากหน้าเว็บ) คำนวณ parent จาก `parentCode` แล้วเรียก `createAccount` → `CreateAccountAsync` ให้ `level = parent.Level + 1` — **สองอัลกอริทึม Level ในเรพเดียว** และบัญชีที่ผู้ใช้สร้างเองโดยไม่เลือกบัญชีแม่ (`fParent` ไม่บังคับ) ได้ Level 1 → ตกด่านเดียวกัน
- ผลกระทบ: ระบบ "ใช้ไม่ได้" สำหรับลูกค้าที่ย้ายจากโปรแกรมอื่นแล้วนำเข้าผัง — ทุกอย่างลงบัญชีผิดที่/ไม่ลง โดยไม่มีอะไรบอกว่าเป็นเพราะ Level
- defect class: "ค่า default ที่แต่งขึ้นเพื่อให้โค้ดเดินต่อได้" + สำเนาอัลกอริทึม 2 ชุด
- ทางแก้: ยุบตัวคำนวณ Level/parent เป็น helper เดียว (`ChartOfAccountLevel.Resolve(code, parent)`) ให้ Create/Import/Seed ใช้ร่วม · หรือเลิกใช้ Level เป็น "postable flag" → เพิ่ม `IsPostable`/`!HasChildren` ที่คำนวณจากโครงสร้างจริง · migration ซ่อม Level ของแถวเดิม
- ความมั่นใจ: สูง (ทั้งฝั่งเขียนและฝั่งอ่านอ่านครบ) — ควรเขียน simulation: import 3 บัญชี 5 หลัก แล้วเรียก FindAccountAsync

### D-05 [P1][S] BankService.RefreshAccountBalance ตั้ง `CurrentBalance = BalanceAfter ของแถวล่าสุด ?? 0` — ยอดเปิดบัญชีหาย และ BalanceAfter ของแถวหลังไม่ถูกคำนวณใหม่เมื่อลบแถวกลาง
- ไฟล์: Services/Implementations/BankService.Delete.cs:93-106 (`account.CurrentBalance = latest?.BalanceAfter ?? 0m;`) · BankService.cs:89 (`CurrentBalance = request.OpeningBalance` — ไม่มี JE/แถวเปิดบัญชี) · :341,350 (`account.CurrentBalance += amount; BalanceAfter = account.CurrentBalance`) · :894 (`BalanceAfter = r.Balance ?? 0` เมื่อ CSV ไม่มีคอลัมน์ยอด) · bank.html:2603-2606 (ยอดเปิดถูกล็อกแก้ไม่ได้ "รวมเข้า CurrentBalance แล้ว")
- ทำไมพัง: (1) เปิดบัญชี 50,000 (ไม่มีแถว transaction) → (2) เพิ่มรายการ 1,000 → BalanceAfter 51,000 → (3) ลบรายการนั้น → Refresh: ไม่มีแถว → `?? 0` ⇒ **ยอด 50,000 หายเป็น 0** · (4) กรณีลบแถวกลางลำดับ: แถวหลังยัง BalanceAfter เดิม (รวมยอดที่ลบ) ⇒ CurrentBalance ค้างค่าผิด · (5) statement ที่นำเข้าโดยไม่มีคอลัมน์ Balance → BalanceAfter=0 ทุกแถว → ลบเมื่อไร CurrentBalance=0
- ผลกระทบ: ยอดธนาคารบนแดชบอร์ด/กระทบยอดผิด · ผู้ใช้แก้ยอดเปิดคืนไม่ได้ (ช่องล็อก) = ตัน
- defect class: "ตัวเลขคู่ที่ต้องสอดคล้องกัน ห้ามมาจากคนละแหล่ง — ต้องมีตัวตั้งตัวเดียว" (BalanceAfter ต่อแถว vs CurrentBalance)
- ทางแก้: เก็บ `OpeningBalance` เป็นฟิลด์แยก แล้ว `CurrentBalance = Opening + Σ(signed amount ของแถวที่ !IsDeleted)` คำนวณจากผลรวม ไม่ใช่จาก BalanceAfter ของแถวใด · ลบแถว = recompute ทั้งสาย
- ความมั่นใจ: สูง (ทีม B ไม่ได้รายงานเรื่องนี้ — ตรวจ report-B แล้ว)

### D-06 [P1][S] นำเข้า/ส่งออกผู้ติดต่อไม่มีคอลัมน์ `BranchCode` (§86/4) ทั้งสองทาง · ตรวจซ้ำด้วย TaxId เดี่ยว ⇒ สาขาของบริษัทเดียวกันถูกตีว่าเป็นรายเดิม
- ไฟล์: Services/Implementations/ImportExportService.cs:269-279 (template: Name, TaxId, IsCustomer, IsSupplier, Email, Phone, Address, ContactPerson) · :483-545 (import row อ่านแค่ชุดนั้น) · :800-806 (export: 8 คอลัมน์เดียวกัน ไม่มี BranchCode/ที่อยู่แยก/PaymentDueDays/NameEn) · :137-145 (`existing = … ToDictionaryAsync(c => c.TaxId!)` — key TaxId เดี่ยว; ถ้ามี 2 แถว TaxId เดียวกัน `ToDictionary` **โยน ArgumentException** ด้วยซ้ำ)
- ทำไมพัง: (1) ลูกค้าย้ายระบบ export ผู้ติดต่อ 500 ราย → BranchCode หายทั้งหมด → import กลับได้ `BranchCode = null` → เอกสารพิมพ์ "สำนักงานใหญ่" ให้ลูกค้าที่เป็นสาขา 00001 (ผิดประกาศอธิบดีฯ 199) (2) ผู้ติดต่อ "บ.ก สนญ." และ "บ.ก สาขา 3" (TaxId เดียวกัน) → preview-conflicts มองเป็นแถวซ้ำ → Merge/Overwrite ทับกัน หรือ ToDictionary พัง 500 เมื่อ DB มีทั้งสองอยู่แล้ว
- ผลกระทบ: ภาษี (ใบกำกับระบุสาขาผู้ซื้อผิด) + ข้อมูลหาย
- defect class: กฎเหล็ก #2 A (BranchCode) · "รายการที่คัดลอกมาด้วยมือ = drift" (คอลัมน์ import/export/DTO สามชุดไม่ตรงกัน: ContactResponse 41 ช่อง · export 8)
- ทางแก้: สร้างคอลัมน์ import/export จาก definition เดียว (ตาราง field → getter/setter) ครอบ BranchCode + ที่อยู่แยก + เครดิต + NameEn · key ซ้ำ = (TaxId, BranchCode) ผ่าน `FindDuplicateContactAsync` ตัวเดียวกับ Create
- ความมั่นใจ: สูง

### D-07 [P2][S] รหัสสินค้าที่ลบแล้ว (soft) สร้างใหม่ซ้ำรหัสไม่ได้ — ด่าน `AnyAsync` ผ่าน (global filter) แล้วไปชน unique index ที่ไม่กรอง IsDeleted ⇒ 500
- ไฟล์: Services/Implementations/ProductService.cs:94-95 (`_db.Products.AnyAsync(p => … p.Code == request.Code)` — ผ่าน filter `!IsDeleted`) · Data/AccountingDbContext.cs:957 (`e.HasIndex(p => new { p.CompanyId, p.Code }).IsUnique();` ไม่มี `HasFilter`) · ProductService.cs:198 (`IsDeleted = true`)
- ทำไมพัง: ลบ P001 → สร้าง P001 ใหม่ → ด่านไม่เห็นแถวที่ลบ → INSERT ชน index → DbUpdateException → "เกิดข้อผิดพลาดภายในระบบ" ผู้ใช้ไม่รู้ว่ารหัสถูกจอง
- ผลกระทบ: งานประจำ (ลบผิดแล้วสร้างใหม่) ตันโดยไม่มีคำอธิบาย
- defect class: "ทางเข้าที่ผู้ใช้ใช้ผิดวิธีได้ ต้องตอบเป็นคำแนะนำ ไม่ใช่ 500"
- ทางแก้: `HasFilter("\"IsDeleted\" = false")` (แบบ IX_WarehouseStocks_Wh_Product ที่ทำแล้ว DatabaseMigrationHelper.cs:5775) หรือให้ด่านใช้ `IgnoreQueryFilters()` แล้วบอกว่า "รหัสนี้เคยใช้แล้ว (ลบไปแล้ว) — กู้คืนไหม"
- ความมั่นใจ: สูง

### D-08 [P2][S] สร้างงวดบัญชีไม่ตรวจ Start≤End/ทับซ้อน/ตรงกับปี-เดือน (ฝั่ง update ตรวจ) · `ResolveFiscalPeriodAsync` ใช้ `FirstOrDefault` ⇒ ทับซ้อนแล้วเลือกงวดตามลำดับ DB
- ไฟล์: Services/Implementations/AccountingService.cs:2186-2200 (`CreateFiscalPeriodAsync` ตรวจแค่ `Year/Month` ซ้ำ) เทียบ :2289-2311 (update: `StartDate > EndDate` + overlap) · fiscal.html:224-230 (ผู้ใช้กรอก startDate/endDate อิสระจาก year/month) · DocumentService.cs (ResolveFiscalPeriodAsync) `FirstOrDefaultAsync(p => p.StartDate <= date && p.EndDate >= date)`
- ทำไมพัง: สร้าง 2026/03 ด้วยช่วง 1 ก.พ.–31 มี.ค. (พิมพ์ผิด) → ผ่าน → วันที่ 15 ก.พ. อยู่ใน 2 งวด → JE บางใบผูก 2026/02 บางใบ 2026/03 แล้วแต่ลำดับแถว → ปิดงวด 02 แล้ว JE ที่หลุดไป 03 ยังแก้ได้ (ด่าน C-T04 ครอบไม่ถึง)
- defect class: "กติกาเดียวกันสองมาตรฐาน" (create vs update)
- ทางแก้: ย้าย validation ของ update เป็น helper เดียวเรียกจากทั้งสอง · ResolveFiscalPeriod ถ้าเจอ >1 ให้ throw
- ความมั่นใจ: สูง

### D-09 [P2][S] แก้ผู้ติดต่อเปลี่ยน TaxId/BranchCode ไปชนรายอื่นได้ — Create มี `FindDuplicateContactAsync` แต่ Update ไม่มี และไม่มี unique index
- ไฟล์: Services/Implementations/DocumentService.cs:10196-10203 (เปลี่ยน identity → audit อย่างเดียว ไม่เช็คซ้ำ) เทียบ :9986 (Create dedup) · Data/AccountingDbContext.cs:888-910 (Contact ไม่มี index unique ใด)
- ทำไมพัง: มี "บ.ก" (0105…001/00000) แล้ว → แก้ "บ.ข" ให้ TaxId เดียวกัน → สองรายซ้ำ → OCR/quick-sale ที่ค้นด้วย TaxId เจอรายแรกแบบสุ่ม → เอกสารกระจาย 2 ราย → รายงานภาษีซื้อ §87/ลูกหนี้แยกสอง
- defect class: "ด่านที่ครอบแค่ทางเดียว"
- ทางแก้: เรียก `FindDuplicateContactAsync(…, excludeId)` ใน Update + partial unique index `(CompanyId, TaxId, BranchCode) WHERE TaxId IS NOT NULL AND IsDeleted=false`
- ความมั่นใจ: สูง

### D-10 [P2][S] accounts.html แก้ไขบัญชี: `fType`/`fParent` เปิดให้แก้แต่ payload update ไม่ส่ง (และ DTO ไม่รับ) → silent no-op
- ไฟล์: wwwroot/pages/accounts.html:355-356 (hydrate `fType`, `fParent` ไม่ disabled) · :366-371 (update ส่งแค่ accountName/accountNameEn/description/inputVatClaimable) · Models/DTOs/Accounting/AccountingDtos.cs:17-25 (UpdateAccountRequest ไม่มี AccountType/ParentAccountId — **ถูกต้อง** ห้ามเปลี่ยน type ของบัญชีที่มี JE)
- ทำไมพัง: ผู้ใช้ย้ายบัญชีไปอยู่ใต้บัญชีแม่อื่น → กดบันทึก → "แก้ไขบัญชีสำเร็จ" → ต้นไม้เหมือนเดิม
- defect class: "ห้าม silent no-op — server ไม่รับ field ตอน update → UI ต้องล็อกช่อง + บอกเหตุผล"
- ทางแก้: disabled + title "เปลี่ยนประเภท/บัญชีแม่ไม่ได้หลังสร้าง — สร้างบัญชีใหม่แล้วปิดบัญชีเดิม" · (ถ้าจะให้ย้าย parent ได้เมื่อไม่มี JE ต้องคำนวณ Level ใหม่ทั้งกิ่ง — ดู D-04)
- ความมั่นใจ: สูง

### D-11 [P2][S] contacts.html ล้าง "วันเครดิต" ของผู้ติดต่อไม่ได้ — ฟอร์มส่ง `null` เมื่อว่าง แต่ service ใช้ `null = ไม่เปลี่ยน` และ `< 0 = ล้าง`
- ไฟล์: wwwroot/pages/contacts.html:1050 (`paymentDueDays: value !== '' ? parseInt(...) : null`) · Services/Implementations/DocumentService.cs:10237-10238 (`if (request.PaymentDueDays.HasValue) contact.PaymentDueDays = request.PaymentDueDays.Value < 0 ? null : …`)
- ทำไมพัง: ตั้ง 30 ไว้ → ลบช่องให้ว่าง → payload null → service ข้าม → ยัง 30 → เอกสารใหม่ของรายนี้ยังได้เครดิต 30 ทั้งที่ผู้ใช้ตั้งใจให้ "ตามค่าบริษัท"
- defect class: silent no-op · sentinel ไม่ตรงกันสองฝั่ง (DocumentLanguage ใช้ `""` = ล้าง แต่ช่องนี้ใช้ `-1`)
- ทางแก้: ฟอร์มส่ง `-1` เมื่อว่างในโหมดแก้ไข (หรือเปลี่ยน service ให้รับ `0`/`""` เป็นล้าง ให้ตรงกับ DocumentLanguage)
- ความมั่นใจ: สูง

### D-12 [P2][M] เครดิตเทอมไม่มี resolver กลาง: `CompanySettings.DefaultPaymentDueDays` ตั้งได้ในหน้า settings แต่ **ไม่มีเส้นสร้างเอกสารฝั่ง server อ่านเลย** · IntegrationService hardcode `AddDays(30)` 4 จุด ข้าม `Contact.PaymentDueDays`
- ไฟล์: wwwroot/pages/settings.html:2110,2336 (UI ตั้ง `defaultPaymentDueDays`) · grep `DefaultPaymentDueDays` ฝั่ง server = Controllers/AiSuggestionController.cs:579-617 (tier 3 ของ endpoint แนะนำ) เท่านั้น — DocumentService.cs ไม่มี · DocumentService.cs:1187-1222 (ใช้ contact.PaymentDueDays → DueDate; ไม่มี tier บริษัท/เทมเพลต) · IntegrationService.cs:891,2612,2697,3123 (`request.DueDate ?? request.DocumentDate.AddDays(30)`) · documents.html:5031,5160-5165 (ฝั่ง UI มี tier เทมเพลต `defaultCreditDays` เอง)
- ทำไมพัง: ลำดับชั้นจริง = ใบ > ผู้ติดต่อ > เทมเพลต > บริษัท แต่ (ก) เส้นฟอร์มได้ทั้ง 4 ชั้น (JS + AI suggest) (ข) เส้น API/recurring/OCR (CreateDocumentAsync) ได้แค่ ใบ > ผู้ติดต่อ (ค) เส้น Integration ได้ 30 ตายตัว ⇒ ลูกค้ารายเดียวกัน วันครบกำหนดต่างกันตามทางเข้า · ค่าที่ตั้งใน settings "วันเครดิตเริ่มต้น" ไม่มีผลกับเอกสารที่สร้างผ่าน API เลย
- ผลกระทบ: DSO/DPO · การทวงหนี้ · หน้าจอ "เกินกำหนด" ผิดตามทางเข้า
- defect class: "Resolver กลาง ห้ามคำนวณเอง" (#4 A) + "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" (DefaultPaymentDueDays)
- ทางแก้: `Helpers/CreditTermsResolver.Resolve(doc, contact, template, settings)` ตัวเดียว เรียกจาก CreateDocumentAsync (ครอบทุกทางเข้า) · Integration ส่ง DueDate=null แล้วให้ resolver ตัดสิน · UI ใช้ endpoint suggest เท่านั้น
- ความมั่นใจ: สูง

### D-13 [P2][S] ExportAsync เขียน CSV เป็น UTF-8 **ไม่มี BOM** ขณะที่ template นำเข้ามี BOM ⇒ เปิดด้วย Excel ภาษาไทยเป็นตัวยึกยือ
- ไฟล์: Services/Implementations/ImportExportService.cs:465 (`Encoding.UTF8.GetBytes(csv)`) เทียบ :2236 (`Encoding.UTF8.GetPreamble().Concat(…)`)
- ทำไมพัง: Excel (Windows) เดา code page จากไฟล์ที่ไม่มี BOM เป็น ANSI/TIS-620 ผิด → ชื่อผู้ติดต่อ/สินค้า/ผังไทยอ่านไม่ออก → ผู้ใช้ "แก้ไขแล้วนำเข้ากลับ" ได้ชื่อพัง
- defect class: สำเนามือสองที่ (encoding เขียนคนละแบบ)
- ทางแก้: helper `CsvBytes(string)` ตัวเดียวใส่ BOM · หรือ export เป็น xlsx ผ่าน MiniExcel ที่มีอยู่แล้ว
- ความมั่นใจ: สูง

### D-14 [P3][S] กลุ่ม "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้/ไม่มีทางกรอก" ในชั้น master
- `Product.SalesAccountId`/`PurchaseAccountId`/`InventoryAccountId`/`SuppliesAccountId`/`SuppliesExpenseAccountId`: อยู่ใน Create/Update DTO (ProductDtos.cs:19-23,42-46) แต่ **ไม่มีหน้าใดส่ง** (grep `salesAccountId` ใน wwwroot = 0) · ฝั่งอ่าน: DocumentService.cs:12805 อ่านเฉพาะ `InventoryAccountId`, OcrService.cs:2577 อ่าน `PurchaseAccountId`; **`SalesAccountId` ไม่มีใครอ่านเลย** ⇒ ตั้งบัญชีรายได้ต่อสินค้าไม่ได้ (ERP ต้องมี) · ProductResponse ไม่ echo Supplies*/CostingMethod
- `Warehouse.BranchId`: รับได้เฉพาะ Create (WarehouseDtos.cs:5), ไม่อยู่ใน Update/Response, ไม่มี UI — และ POS ผูกสาขากับ terminal โดยตรง (PosService.Orders.cs:22) ⇒ dead field · `Warehouse.Phone` รับใน Create/Update แต่ไม่ echo ไม่มีฟอร์ม · `IsDefault` ตั้งได้ผ่าน `SetDefaultAsync` แต่ warehouse.html ไม่มีปุ่ม (grep `setDefault` = 0)
- `CompanySettings.PreventPostToClosedPeriod`: อยู่ใน Update/Response DTO + entity แต่ grep ฝั่ง Services/Controllers = 0 reader และไม่มี UI — ด่านงวดปิด (C-T04) บังคับเสมอ ⇒ ธงนี้โกหกคนอ่าน DTO
- `Contact.PaymentTerms` (ป้ายเทอม): DTO ครบ 3 ตัว แต่ contacts.html ไม่มีช่อง (มีแค่ `fPaymentDueDays`) — DocumentService.cs:1195-1197 อ่านใช้จริง ⇒ ตั้งค่าได้ผ่าน API เท่านั้น
- defect class: "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" — ต้องเลือก **ต่อสาย หรือ ลบ**
- ความมั่นใจ: สูง (grep ครบทั้งเรพ)

### D-15 [P3][S] ภาษาเอกสาร: 3 บริการคำนวณ `doc.DocumentLanguage ?? settings.DocumentLanguage` เอง ข้ามชั้น `template.Language` ที่ `ResolveDocumentLanguage` ใช้
- ไฟล์: Services/Implementations/PdfGenerationService.cs:1119-1131 (resolver กลาง: request > doc > template > settings > th) · DocumentEmailService.cs:330 · DocumentLineDeliveryService.cs:58 · EmailScheduleService.cs:535 (inline `??` ไม่มี template)
- ทำไมพัง: ใบที่ `DocumentLanguage=null` แต่เทมเพลตตั้ง en → PDF อังกฤษ แต่ข้อความอีเมล/LINE ไทย (คอมเมนต์ :328 บอกจงใจ แต่ผลคือคนละภาษาในการส่งครั้งเดียว)
- defect class: "Resolver กลาง ห้ามคำนวณเอง" (บทเรียนต้นทางเรื่องภาษาเอกสารพอดี)
- ทางแก้: ให้ 3 จุดเรียก `ResolveDocumentLanguage(null, doc, template, settings)` (โหลด template จาก `doc.DocumentTemplateId`)
- ความมั่นใจ: กลาง — ผลจริงขึ้นกับว่า CreateDocumentAsync ประทับ `DocumentLanguage` ลงใบทุกครั้งหรือไม่ (:1204 เติมจากผู้ติดต่อเมื่อ null; ถ้าผู้ติดต่อไม่ตั้ง ใบยัง null จริง)

### D-16 [P3][S] สร้างผู้ติดต่อซ้ำ TaxId+สาขา → service คืนรายเดิม (เติมเฉพาะช่องว่าง) แต่ UI toast "เพิ่มสำเร็จ" — ชื่อ/ที่อยู่ที่พิมพ์ใหม่ถูกทิ้งเงียบ
- ไฟล์: Services/Implementations/DocumentService.cs:9986-10004 (`Fill` เฉพาะช่องว่าง, `return MapContactToResponse(dup)`) · contacts.html save() ไม่เทียบ `res.data.id`/name กับที่กรอก
- ทำไมพัง: ผู้ใช้พิมพ์ชื่อใหม่ (เช่นเปลี่ยนชื่อบริษัท) → ได้รายเดิมชื่อเดิม ไม่มีคำอธิบาย
- ทางแก้: response ติดธง `ReusedExisting=true` + UI แจ้ง "ใช้รายที่มีอยู่แล้ว: … (แก้ไขที่รายนั้น)"
- ความมั่นใจ: สูงในพฤติกรรม · กลางในความรุนแรง

### D-17 [P3][S] จ่ายเงินเดือนเมื่อผังไม่มี 541xx → JE ขาดบรรทัด Dr → ล้มด้วย "ไม่สมดุล" แทน "ไม่พบผังบัญชีเงินเดือน"
- ไฟล์: Services/Implementations/PayrollService.cs:2266-2276 (fallback `54111` → prefix `541` → null) · :2275 `if (salaryAccount != null)` ไม่มี else · :2204 คอมเมนต์ยอมรับว่าจะไปตายที่ CreateJournalEntryAsync
- ทางแก้: throw `BusinessRuleException("ไม่พบผังบัญชีเงินเดือน (541xx) — สร้างในผังบัญชีก่อน")` ตรงจุด (แบบ FixedAssetService.cs:425,444,459 ที่ทำถูกแล้ว)
- ความมั่นใจ: สูง

## ตรวจแล้วไม่ใช่บั๊ก
- `MergeContactsAsync` (DocumentService.cs:9858-9980) repoint FK **ทุกคอลัมน์** `%ContactId` uuid จาก information_schema ในธุรกรรมเดียว + AuditLog — ครอบทั้ง 25 คอลัมน์ที่ grep เจอใน Models/Entities (รวม PayeeContactId/LinkedContactId/MatchedContactId) ✅
- unique index มีจริง: Products(CompanyId,Code) · ChartOfAccounts(CompanyId,AccountCode) · Branches(CompanyId,Code) · Warehouses(CompanyId,Code) · FiscalPeriods(CompanyId,Year,Month) (AccountingDbContext.cs:957,732,1584,1714,772) · ที่**ไม่มี**: Contacts (D-09) · BankAccounts.AccountNumber (ไม่มีด่านฝั่ง service ด้วย — P3 ไม่แยกเป็นข้อ)
- ChartOfAccount: ไม่มี endpoint ลบ · Update ไม่รับ AccountType/AccountCode/Parent (ป้องกันเปลี่ยนความหมายบัญชีที่มี JE) · IsSystemAccount แก้ไม่ได้ · seed idempotent (`existingCodeSet` ใน `SeedDefaultAccountsAsync(companyId, businessType, industryType)` AccountingService.cs)
- Branch: Create/Update ตรวจ Code ซ้ำ + TaxBranchCode ซ้ำ + normalize 00000 (DimensionalAccountingService.cs: Create :294-356 · Update :357-434) · Delete :435-445 บล็อกเมื่อมี JE/JE line อ้าง (`JournalEntries.AnyAsync(j => j.BranchId == branchId)`) · ปิดสาขาสุดท้าย/สนญ. ไม่ได้ (ใน Update ช่วง `request.IsActive.HasValue`) · DTO 3 ตัว round-trip ครบ (สคริปต์: 0 flag)
- FiscalPeriod: Delete เฉพาะ Open + ไม่มี JE (AccountingService.cs:2415-2437) · `EnsureFiscalYearPeriodsAsync` สร้างรายเดือนครบ · Update มี overlap check
- Company/CompanySettings round-trip: สคริปต์พบเฉพาะฟิลด์ที่ตั้งจากหน้าอื่น (Landing*, LeaveQuotasJson) · `MyRole` read-only — ไม่มี field หายเงียบ
- PdpaRetentionPurgeJob soft-delete contact เฉพาะเมื่อไม่มีเอกสาร 5 ปี (:75-80) ✅
- ผู้ติดต่อที่ถูก deactivate (IsActive=false) ยังแสดงชื่อในเอกสารเก่าได้ (global filter = IsDeleted เท่านั้น)
- Onboarding: CompanyService.CreateAsync:48 `BranchCode = request.BranchCode ?? "00000"` และ :90 seed ผัง — ทำที่ server ทุกทางเข้า (register AuthService.cs:249 · SSO :708 · wizard) · settings.html completeSetup seed ซ้ำแต่ idempotent · ไม่สร้างแถว Branch ให้ (dimensions.html:222 บอกชัด "กิจการที่มีที่เดียวไม่ต้อง…" — จงใจ)

## ซ้ำกับ SYSTEM_REVIEW (ID เดิม + ยังเปิดอยู่จริงไหม)
- **C-R3 `FiscalYear.RangeFor` ตัวเดียว** — ยังเปิด: ใช้ RangeFor 5 จุด แต่คำนวณ ม.ค.–ธ.ค. เอง ≥8 จุด: ExecutiveReportService.Trends.cs:59-60, .Budget.cs:21-22 · FinancialManagementService.Part2.cs:19 · TaxFilingExportService.cs:165-166 · DocumentService.cs:12685-12686 · FpaService.cs:536-537,587-588 · AiSuggestionController.cs:1923-1924 (WhtCreditService.cs:194 และ TaxService.cs:2353 เป็นปีภาษี/ปีปฏิทินโดยชอบ)
- **แถว Onboarding (§4 row 177)** — ข้อความ "จริง seed ตอนกด ตั้งค่าเสร็จสิ้น" และ "settings ไม่ทำ 00000" **ไม่ตรงโค้ดปัจจุบัน** (ดู "ตรวจแล้วไม่ใช่บั๊ก") · ส่วน "ไม่มี fiscal period ปีปัจจุบัน/prefix ใน checklist" ยังจริง — บริษัทใหม่ไม่มีงวดจนกว่าจะกด "สร้างทั้งปี" ใน fiscal.html:18 ⇒ JE ทุกใบก่อนหน้านั้น `FiscalPeriodId=null` และด่านงวดปิด (C-T04) ไม่มีอะไรให้ล็อก (report-C §ERP ข้อ 6 ชี้เรื่องเดียวกัน)
- C-T04/report-C C-01: ResolveFiscalPeriodAsync — D-08 เป็นมุมใหม่ (ทับซ้อน) ไม่ซ้ำ
- H-R3/H-A8: ชั้นสต็อกทางเข้าเดียว — D-02 เกี่ยวเนื่อง (ลบสินค้า = ทางออกจากสต็อกอีกทางที่ไม่ผ่าน ledger)
- Performance §7 "ChartOfAccounts ต่อบรรทัด" — ไม่ซ้ำกับของทีมนี้

## ยังไม่ได้อ่าน
- prefix/ลำดับเลขที่ (settings.html แท็บลำดับเลขที่ ↔ `UpdateCompanySettingsRequest` — สคริปต์บอก round-trip ครบ แต่ไม่ได้ไล่ semantics ของ NumberSeries ที่ CLAUDE.md บอกว่าต่อสายเฉพาะ Prefix)
- ProductCategory/UnitConversion delete guards (ProductService.cs:325,402) · ProductAlias · product-aliases.html
- BankService.AutoCreateLinkedAccountAsync/SyncLinkedAccountAsync ปลายทาง (เลขบัญชีย่อยซ้ำ/`-` suffix) · BankAccount ปิดใช้งานที่ยังมี JE
- ImportExportService smart import (:1718-2215) · DuplicateDetector.cs · export entity อื่น (employees/fixed-assets/budgets)
- DocumentTemplateService (เทมเพลตเป็น master อีกตัว: ลบเทมเพลตที่เอกสารอ้าง — PdfGenerationService บอกว่า fallback เงียบ ยังไม่ verify)
- CompanySettings ลำดับชั้นที่อยู่/หัวเอกสาร (`DocumentIssuerBranch.ResolveCode` DocumentService.cs:5096) — อ่านแค่จุดเรียก
- ไม่ได้ตรวจ dimensions.html ฟอร์มสาขาเชิง UX (สคริปต์เทียบชื่อ property เท่านั้น)

## ข้อเสนอเชิง ERP (แยกจากบั๊ก)
1. **Master Data Governance ชั้นเดียว**: ทุก master (Contact/Product/CoA/Branch/Warehouse/BankAccount) ควรมี lifecycle เดียวกัน — `IsActive` (ห้ามใช้ต่อ) แยกจาก `IsDeleted` (ซ่อน) · ลบได้เฉพาะเมื่อ "ไม่มีใครอ้าง" ตรวจด้วย helper กลางที่อ่าน FK จาก information_schema (มีอยู่แล้วใน MergeContacts) · unique index แบบ partial `WHERE IsDeleted=false` ทุกตัว
2. **Chart of Accounts เป็นโครงสร้าง ไม่ใช่ Level ตัวเลข**: `IsPostable` คำนวณจาก children · ห้ามให้ 59 จุดในโค้ดพึ่ง `Level >= 4` · ตัวคำนวณ Level/parent ตัวเดียวสำหรับ Create/Import/Seed/ย้ายกิ่ง · ตั้ง "บัญชีที่โค้ดต้องใช้" (54111, 11310, 21210, 115x, 116xx…) เป็น **account role mapping** ต่อ tenant (`SystemAccountRole → AccountId`) แทน hardcode รหัส — tenant ที่ import ผังตัวเองจึงแมปได้โดยไม่ต้องตั้งรหัสให้ตรง (จุดที่ทำแล้วบางส่วน: Contact.DefaultAr/Ap · Product.InventoryAccountId)
3. **Import/Export จาก field definition เดียว**: ตาราง `EntityField(name, label, getter, setter, required, validator)` ต่อ master → สร้าง template/import/export/DTO doc จากที่เดียว ปิด drift 3 ชุด (D-06) · รองรับ xlsx (MiniExcel มีแล้ว) + preview ต่างแถว
4. **เครดิตเทอม/ภาษา/ที่อยู่/สาขาผู้ออก** เป็น `DocumentDefaultsResolver` ตัวเดียว (ใบ > ผู้ติดต่อ > เทมเพลต > แบรนด์ > บริษัท) เรียกจาก CreateDocumentAsync ทุกทางเข้า — UI ใช้ endpoint preview ของ resolver เดียวกัน
5. **Onboarding ให้ครบชุด ERP**: สร้างบริษัท = ผัง + สาขา 00000 (แถว Branch จริงเพื่อให้ POS/JE BranchId ใช้ได้) + งวดบัญชีปีปัจจุบันตาม FiscalYearStartMonth + คลังหลัก + ชุด prefix + เทอมเริ่มต้น — ทำใน `CompanyService.CreateAsync` ที่เดียวไม่ใช่กระจายในหน้า settings/wizard
6. **Product master ระดับ ERP**: UoM หลายหน่วย (มี UnitConversion แล้วแต่ไม่ echo ใน list) · variant/attribute · บัญชีรายได้/ต้นทุน/สินค้าคงเหลือต่อสินค้า **มี UI และถูกอ่านจริง** (D-14) · CostingMethod ต่อสินค้าแก้ได้ · effective-dated price list
7. **Audit ของ master**: การเปลี่ยน identity ผู้ติดต่อมี audit แล้ว (DocumentService.cs:10230-10245) — ควรใช้กติกาเดียวกับ Product/CoA/BankAccount (เปลี่ยนรหัสภาษี/ประเภทบัญชี/เลขบัญชีธนาคาร = audit + hash chain)
