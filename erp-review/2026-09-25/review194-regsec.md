# รอบ 194 — ฝ่ายค้าน (ถดถอย/ความปลอดภัย/ข้อมูล) · `git diff ce29e0f5..HEAD`

อ่านอย่างเดียว · รัน checker: record_arg · nullable_arg · using · accessibility · arg_type · tuple_name_merge · undeclared_local ·
write_permission_gate · html_attr_escape · onclick_js_string · enum_number_compare · escape_helper · css_var · blank_number_null ·
service_interface · dead_helper · required_call_site (160 กติกา) · line_vat_source · deep_link_param = **0 ปัญหาทุกตัว** ·
`node --check` สคริปต์ inline ของ 7 หน้าที่แก้ = ผ่าน · ยังไม่ได้คอมไพล์ (ไม่มี SDK)

## CONFIRMED

✅ ทีม R <pending> **C1 (สูง) ประเภท "เงินประกัน" หลุดเข้าเป็นมัดจำค่าห้องได้ 2 ทาง — ด่านมีแค่ที่หน้าบันทึกที่พัก**
- `LodgingService.cs:83-100` `DepositKindForAsync` ใช้ `channelKind` (RoomDepositKindId) และ `companyDefault` โดย**ไม่ตรวจ Nature**
  (ด่าน PartOfPrice/NonVat มีเฉพาะ `ApplyDepositKindsAsync` ตอนบันทึกที่พัก)
- `DepositKindService.cs:73` `UpdateAsync` เปลี่ยน `Nature` ของประเภทที่ที่พักผูกเป็นห้องอยู่ได้ (บรรทัด 184) · `:135` `SetDefaultAsync`
  ตั้งประเภทเงินประกันเป็นค่าเริ่มต้นบริษัทได้ ⇒ ที่พักที่เลือก "ตามบริษัท" ได้ประเภทนั้น
- ผล: ใบมัดจำค่าห้องใหม่ตรึง `DepositNature=RefundableSecurity` (ไม่เกิดภาษีตอนรับเงิน — ขัด §78/1) →
  `LodgingDepositSettlement.cs:328` `IsSecurityDeposit` ตัดออกจาก `RoomDeposits` ⇒ เช็คเอาต์ไม่หักมัดจำ (แขกถูกเก็บเต็ม) · ยกเลิกไม่ริบ/ไม่คืนตามแผน ·
  `:341` fallback `list.FirstOrDefault()` อาจหยิบมัดจำค่าห้องเป็น "เงินประกัน" · CMS PrePayment (default เป็นเงินประกัน) ได้ `SecurityNotRevenue` ไม่รับรู้เลย
- แก้: ตรวจ Nature ใน `DepositKindForAsync` (ไม่ผ่าน = ตกชั้นถัดไป + คำเตือน) และ/หรือ UpdateAsync/SetDefaultAsync ปฏิเสธเมื่อขัดกับการผูกของที่พัก

**C2 (กลาง) ริบ/รับรู้มัดจำเต็มยอดกลายเป็น "ครั้งเดียว" — ถดถอยจากเดิม**
- `DocumentService.cs:3801` บล็อกเมื่อ `DepositAppliedToDocumentId` มีค่า · การริบครั้งแรกออกใบกำกับแล้ว ApplyDeposit ⇒ ตั้งค่านี้ ⇒ รับรู้บางส่วนครั้งที่ 2
  ของใบเดียวกัน (ส่งมอบเป็นงวด) ล้ม · มัดจำที่เคยตัดชำระบางส่วนแล้วจะริบส่วนที่เหลือ ล้ม — "ทางไปต่อ" ให้ "คืน/โอนยอดมัดจำ" ซึ่งไม่ใช่เหตุการณ์จริง (F2 ข้อ 8)
- ใบเดิม `DepositNature=NULL` + เต็มยอด ⇒ ค่าเริ่มต้น PriceOrFee ⇒ ปุ่ม "รับรู้" ทุกครั้งออกใบกำกับอัตโนมัติ (ตาม spec แต่เป็นพฤติกรรมใหม่ของใบเก่า — ต้องอยู่ใน DOCUMENT_FLOW/CHANGELOG ชัด)

✅ ทีม R <pending> **C3 (กลาง-ต่ำ) เงินประกันค้างตายเมื่อใบรับถูกยกเลิก**
- `LodgingService.Lifecycle.cs:1200` ห้ามรับใหม่ถ้า `SecurityDepositDocumentId != null && SettledAt == null` · ถ้าใบถูก void ที่หน้าเอกสาร ตัวโหลด (`:455` ตัด Voided)
  ⇒ `SecurityDeposit()` = null ⇒ ปิดไม่ได้ (`:1299`) และรับใหม่ไม่ได้ · ไม่มีใครล้างลิงก์

✅ ทีม R <pending> **C4 (ต่ำ-กลาง) "พิมพ์เหตุผลเป็นหมายเหตุบนใบ" ไม่จริง**
- `DepositPolicyNote`/`DepositKindName` ไม่มีผู้อ่านใน `PdfGenerationService`/`DocumentRenderer` (grep 0) แต่ `settings.html` ป้ายช่องเหตุผล และ
  `DepositPolicyResolver.KindProblem` บอกว่า "ระบบพิมพ์เหตุผลเป็นหมายเหตุบนใบ" (F2 ข้อ 2 · ข้อความเท็จถึงผู้ใช้)

**C5 (ต่ำ) สูตร "ผังมี 21530" สองชุด**
- `DocumentService.cs:3549` ใช้ `!IsDeleted` · `LodgingService.cs:103`/`DepositKindCatalog.LoadContextAsync` ใช้ `IsActive` ⇒ 21530 ปิดใช้: ใบจริงลง 21530 (บัญชีปิด)
  ขณะหน้าตั้งค่า/ที่พักแสดง 21620

## PLAUSIBLE
- P1 `DocumentService.cs:3625` ตัดสินริบด้วยอัตราบริษัท ไม่ใช่อัตราของช่องทาง — ที่พัก `ChargeVat=false` (อัตรา 0) ยกเลิกแล้วริบ ⇒ ออกใบกำกับ 7% ·
  และ CreateDocument จัดรูปซ้ำด้วยอัตราบริษัท ⇒ `ShapeFor(Undue,7)` ติด deferred=true บนใบ VAT 0 ที่ที่พักคิดเป็น (0,false)
- P2 `DocumentService.cs:3814` SaveChanges ธง `[DEPOSIT-LATE-VAT]` ก่อน `CreateDocumentAsync` — สร้างล้ม (ไม่มีผู้ติดต่อ/โควตา) ⇒ ธงค้างบนใบทั้งที่ไม่มีใบกำกับ
- P3 ยกเลิกที่พักล้มกลางทาง: ข้อความ lodging (`Lifecycle.cs:~1020`) สั่ง "รับรู้ที่หน้าเงินมัดจำ" ขัดกับข้อความ IssueForfeit "ห้ามกดซ้ำ" · ใบกำกับร่างค้าง
- ✅ ทีม R <pending> P4 `documents.html` `_hydrateDepositKind` ก่อนรายการประเภทโหลดเสร็จ ⇒ ป้าย "(ปิดใช้แล้ว)" ผิด + ไม่มีตัวเลือกอื่น (re-render เฉพาะ `sel.value===''`)
- (backlog — ทีม R ไม่ทำรอบนี้) P5 สมัครผ่าน AuthService seed ชุด General · เปลี่ยน IndustryType ทีหลังไม่เปลี่ยนชื่อ (โรงแรม) · RENT-ADV มาตอนบูตถัดไป (cosmetic)
- ✅ ทีม R <pending> P6 DDL คอลัมน์ Documents ใหม่อยู่ในชุด `ApplyFullTextSearchIndexes` ซึ่ง `catch {}` เงียบ (ต่างจาก `ApplyMissingColumns` ที่ log) — ถ้าล้ม ทุก query Documents พังโดยไม่มี log

## NOT-A-BUG (ตรวจแล้ว)
- migration: idempotent · ไม่มี UPDATE "Documents" · index สร้างก่อน seed · `ON CONFLICT DO NOTHING` ไม่ระบุเป้า ครอบทุก unique index (รวม partial) ·
  บูตพร้อมกันปลอดภัย · ลำดับคอลัมน์ถูก · ผูก RoomDepositKindId **ไม่ทับ "ตามบริษัท"**: `ApplyDepositKindsAsync` ล้าง DVT+ธงทุกครั้ง · ผู้เขียน LodgingProperty อื่น
  (LodgingSeeder) ไม่ตั้ง DVT · UPDATE เดิมบรรทัด 6765 ต้องการธง=true ซึ่งถูกล้างแล้ว
- seed จุดสร้างบริษัท: `FindAsync` เจอ entity Added · lazy seed บน GET เฉพาะเมื่อ 0 แถว (รวมที่ลบ) + จับ 23505
- Security: DepositKinds เขียน = `RequirePermission(CompanySettingsEdit)` + `[RejectApiKey]` · lodging ใหม่ = LodgingManage (เท่าข้างเคียง) ·
  ทุก lookup ด้วย id มี CompanyId · HTML ผ่าน esc/`data-id`+`this.dataset` · toast esc ในตัว
- คอมไพล์: named args/record ข้ามทีมผ่าน checker · ไม่มี `IsDefined(null)` เหลือ · clone พา IsDeposit/ประเภท
