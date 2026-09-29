# ฝ่ายค้าน F3 #11 — รอบ 198 เฟส 1 ทีม D (`4ce5673c` · SettlementController + หน้าจอ)

ผู้ตรวจ: subagent ฝ่ายค้าน (อ่านอย่างเดียว · ไม่แก้/ไม่คอมมิต) · วันที่ 2026-09-28 · branch `claude/erp-system-review-team-660mev`

วิธีตรวจ: `git show 4ce5673c` + เปิด service ทีม B/C ทุกเมธอดที่ controller เรียก + middleware/filter ที่ครอบ route นี้
(`TenantAccessMiddleware` · `RequirePermissionAttribute` · `RejectApiKeyAttribute` · `ApiKeyScopeFilter` · `SubscriptionMiddleware` ·
`ExceptionMiddleware` · `InputSanitizationMiddleware`) + `api.js` `_requestCore` + รัน checker:
`onclick_js_string_check` · `html_attr_escape_check` · `enum_number_compare_check` · `deep_link_param_check` · `blank_number_null_check` ·
`write_permission_gate_check` · `owner_action_wiring_check` · `dead_link_check` · `css_var_check` · `escape_helper_check` ·
`dto_nullable_contract_check` · `tuple_name_merge_check` = **ผ่านทั้งหมด** · `node tools/settlement_import_form_sim.js` ผ่าน ·
`node --check` สคริปต์ของทั้งสองหน้า ผ่าน (`record_arg_check`/`using_check` timeout ในเครื่องนี้ — ไม่ได้ผล)

**สรุป: ไม่พบ P0/P1 ที่ยืนยันได้** · P2 ที่ยืนยันแล้ว 3 ข้อ (D-01..D-03) · P2 ที่น่าจะจริง/ต้องให้เจ้าของตัดสิน 3 ข้อ · P3 หลายข้อ

---

## CONFIRMED

### ✅ <pending> D-01 · P2 · โมดัล "ตัดสินการจับคู่" ส่ง id ของ **PaymentIntent** เป็น `documentId` ⇒ server ตอบ 404 "ไม่พบเอกสารขาย" ทุกครั้ง
- `wwwroot/pages/settlements.html:654-659` เปิด radio ตาม `m.canReceive` / `m.isRefundTarget` โดย**ไม่ดู `m.kind`** · `:674`
  ส่ง `{ documentId: cands[pick].id }`
- ผู้สมัครชนิด `PaymentIntent` มี `CanReceive = free && received` / `IsRefundTarget = free && RefundedAmount>0`
  (`Services/Settlement/SettlementImportService.Lines.cs:90-92`) ⇒ radio เปิดได้
- `AssignLineMatchAsync` หา id นั้นใน `Documents` เท่านั้น (`SettlementImportService.Lines.cs:386-392`) และ `SettlementAssignMatchRequest`
  ไม่มีช่อง PaymentIntentId ⇒ 404 `SETTLEMENT-MATCH-DOC` "ไม่พบเอกสารขายนี้ในบริษัท" — ข้อความชี้สาเหตุผิด (F2 ข้อ 7)
- สถานการณ์: ช่องทาง gateway นำเข้า CSV · ออเดอร์หนึ่งตรงทั้งใบแจ้งหนี้และ intent (หลายผู้สมัคร ⇒ Unmatched) · ผู้ใช้เลือก intent → ล้มเสมอ
  (บรรทัดคืนเงินที่ intent คืนผ่านระบบแล้วก็เช่นกัน)
- แก้: เซิร์ฟเวอร์ส่ง `selectable`/`selectReason` ต่อผู้สมัครตามสิ่งที่ `AssignLineMatchAsync` รับจริง (Document เท่านั้นในเฟส 1) แล้วหน้าเว็บแสดงตามนั้น —
  หรือเพิ่ม `PaymentIntentId` ใน request + service รองรับ · เทสต์: ผู้สมัคร intent ต้องไม่เลือกได้ (หรือเลือกแล้วสำเร็จ)
- ของแถม (P3): ปุ่ม "ตัดสินการจับคู่" แสดงกับทุกบรรทัด `requiresSaleMatch` รวมบรรทัด intent-sourced (`pi:`) ที่ service ตีกลับเสมอ
  (`SettlementImportService.Lines.cs:366-368`) — `SettlementBatchActions`/view ไม่มีธงต่อบรรทัด

### ✅ <pending> D-02 · P2 · พรีวิวการลงบัญชีแสดง "รหัสผังมาตรฐาน" ไม่ใช่ผังที่จะลงจริง ⇒ ผู้กดลงบัญชีอนุมัติจากข้อมูลที่ไม่ตรง
- `settlements.html:741` แสดง `accountRole · defaultAccountCode` · แต่ `SettlementBatchMath.cs:456-457` ใช้
  `counterId = OverrideAccountId ?? RoleAccountId(role, channel, feeMap)` ขณะที่ `counterCode = DefaultCode(role)` ⇒
  - ค่าธรรมเนียมที่ช่องทางแมปไว้ใน `FeeAccountMapJson` (เช่น ค่าคอม → 53999) พรีวิวโชว์ `53140`
  - บรรทัด Adjustment ที่ผู้มีสิทธิ์ **Import** เลือก `OverrideAccountId` เอง — role `adjustment` ไม่มี default ⇒ พรีวิวไม่บอกผังเลย
  - ผังพัก/ธนาคาร (`clearing`/`bank`) ไม่มีรหัส — ผู้ใช้ไม่เห็นว่าเดบิตบัญชีธนาคารไหน (ดู D-03)
  - ใบค่าธรรมเนียม (`:746`) แสดงแค่ป้าย ไม่แสดงผัง
- ผล: การแยกหน้าที่ Import/Post (เหตุผลที่มี 4 คีย์) อ่อนลง — ผู้ลงบัญชีเห็น "ผังตามบทบาท" ขณะที่ JE ลงผังที่คนนำเข้าเลือก · รู้ตัวหลังเปิด JE
- แก้: `PreviewAsync` คืนผังที่ resolve แล้ว (`gate.Accounts` มีอยู่แล้ว) เป็น `accountCode/accountName` ต่อขา · หน้าเว็บแสดงค่านั้นแทน
  `defaultAccountCode` (F2 ข้อ 5 · "หน้าจอบอกอย่าง ระบบทำอีกอย่าง")

### ✅ <pending> D-03 · P2 · ฟอร์มหัวรอบโอน **เลือกบัญชีธนาคารแรกให้เอง** และหลังนำเข้าไม่มีที่ไหนแสดงบัญชีที่ผูก
- `settlements.html:333` `this.options(banks, banks[0]?.value || '', '(ไม่ระบุ)')` ⇒ บริษัทที่มี ≥2 บัญชี กดนำเข้าโดยไม่แตะ = ผูกบัญชีแรกตามชื่อธนาคาร
- server ถือว่าค่านี้คือ "บัญชีที่เงินเข้าจริง": ขา `bank` ของ JE รอบโอน (`SettlementBatchMath.cs:439/446` + `SettlementPostingService.cs:409`) และ
  ผู้สมัครจับคู่เงินเข้า (`SettlementController.cs:320-326`) · แก้หลังนำเข้าไม่ได้ (ต้องยกเลิกรอบแล้วนำเข้าใหม่ — ข้อความของ controller เอง)
- รายละเอียดรอบ (`renderDetail`) และพรีวิว (ขา `bank` ไม่มีรหัส — D-02) ไม่แสดง `bankAccountId` เลย ⇒ ผิดแล้วมองไม่เห็นจนจับคู่เงินเข้าไม่เจอ
  → ต้องยกเลิกการลงบัญชี + ยกเลิกรอบ + นำเข้าใหม่
- ขัด F2 ข้อ 3 (ค่าที่แต่งขึ้นอันตรายกว่าการไม่ตอบ) — server บล็อกเองอยู่แล้วเมื่อ `NetPayout≠0 && BankAccountId==null` (`SettlementBatchMath.cs:242`)
  ⇒ ทางที่ปลอดภัยคือค่าเริ่มต้นว่าง (หรือเลือกให้เฉพาะเมื่อมีบัญชีเดียว) + แสดงชื่อบัญชีในหัวรอบและพรีวิว

### ✅ <pending> D-04 · P3 · `SettlementBatchActions` เปิดปุ่มแพ้/ชนะ chargeback ให้บรรทัดที่ปิดผลไปแล้ว
- `Helpers/SettlementBatchActions.cs:76-78` คืน chargeback ทุกบรรทัดเมื่อ Posted/BankMatched · service มีด่าน "ปิดไว้แล้ว" (JE `Reference`
  ของบรรทัด ยังไม่ถูกกลับ — `SettlementPostingService.cs:826-832`) ⇒ ปุ่มค้างตลอดไป · กด "ชนะ" หลังเคยกด "แพ้" ได้ 200 Ok=true
  "ปิดรายการไว้แล้ว — ไม่ลงซ้ำ" (toast แบบสำเร็จ) และหน้าจอไม่เคยบอกว่าปิดด้วยผลไหน · คอมเมนต์ของ helper อ้างว่า "ด่านเดียวกับ service" — ไม่ครบ
- แก้: controller ส่งรายการ chargeback ที่ปิดแล้ว (+ เลข JE/ผล) ให้ helper ตัดออก และแสดงผลที่ปิดไว้

### ✅ <pending> D-05 · P3 · สถานะ "ยกเลิกแล้ว" (Voided) มองไม่เห็นได้เลย — ตัวกรอง/ป้าย/ปุ่มของ Voided เป็นโค้ดตาย
- `VoidBatchAsync` ตั้ง `IsDeleted = true` (`SettlementImportService.Lines.cs:482-483`) และ `SettlementBatch` มี global filter `!IsDeleted`
  (`AccountingDbContext.cs` แถว SettlementBatch) ⇒ `GET batches?status=Voided` คืนว่างเสมอ · `GET batches/{id}` ของรอบที่ยกเลิก = 404
- แต่ `settlements.html:200` ใส่ "ยกเลิกแล้ว" ในตัวกรอง · `SettlementBatchActions.cs:81` มีเหตุผลล็อกของ Voided · เทสต์
  `จับคู่ธนาคารแล้ว_…_ยกเลิกแล้วไม่มีปุ่มใดเลย` ล็อกพฤติกรรมที่ไม่มีทางเกิด (F2 ข้อ 2 "มี ≠ ถูกเรียก")
- แก้: ตัด Voided ออกจากตัวกรอง (หรือทำ endpoint ประวัติที่ `IgnoreQueryFilters`) — ตัดสินอย่างตั้งใจ

### ✅ <pending> D-06 · P3 · ปุ่มทุกปุ่มตัดสินจากสถานะอย่างเดียว ไม่ดูสิทธิ์ของผู้ใช้
- `SettlementBatchActions.For(status, lines)` ไม่รู้สิทธิ์ ⇒ ผู้มีแค่ `Settlement.View` เห็น "ลงบัญชี/ยกเลิกรอบ/ยกเลิกการลงบัญชี/จับคู่เงินเข้า/แพ้-ชนะ"
  แล้วได้ 403 หลังกด (ยกเลิก = หลังพิมพ์เหตุผลแล้ว) · ไม่ใช่ silent no-op (403 มีข้อความ) แต่เป็นทางตันที่คาดได้
- แก้: controller intersect ด้วย `IPermissionService.HasPermissionAsync` ของ Import/Post ก่อนส่ง `actions` (server computes)

### ✅ <pending> D-07 · P3 · `payload`/body ที่มี `"header": null` ⇒ NullReferenceException ⇒ 500
- `SettlementController.cs:143` `request.Header.ChannelId` (STJ ไม่บังคับ non-nullable ใน .NET 8) · `ImportFromIntents` ส่ง request ตรงให้ service
  ที่อ่าน `request.Header` ทันที (`SettlementImportService.cs:117-119`) ⇒ 500 แทน 400 · เฉพาะคำขอที่ประกอบเอง (หน้าเว็บส่ง header เสมอ)
- แก้: `request?.Header is not { } h || h.ChannelId == Guid.Empty` ทั้งสองเส้น (หรือ `[Required]` + ข้อความไทย)

### ✅ <pending> D-08 · P3 · `ChargebackResolveRequest(bool Won)` — body ไม่มี `won` = **แพ้** (ลง JE ขาดทุน)
- `SettlementController.cs:239` ค่า default ของ bool = false ⇒ `{}` ปิดรายการเป็นขาดทุนเงียบ ๆ · หน้าเว็บส่ง `!!won` เสมอ จึงเป็นความเสี่ยงของ client อื่น
  (คีย์ API ถูก `[RejectApiKey]` กันแล้ว) · แก้: `bool? Won` + 400 เมื่อ null

### ✅ <pending> D-09 · P3 · `NotFoundMessage` แปลง `KeyNotFoundException` ที่ไม่ใช่ไทยเป็น "ไม่พบรายการนี้ในบริษัท"
- `SettlementController.cs:382-384` — ถ้าบั๊กภายใน (dictionary lookup) โยน `KeyNotFoundException` ผู้ใช้จะเห็น 404 "ไม่พบรายการ" ซึ่งระบุสาเหตุผิด
  (F2 ข้อ 7) · ของ service ทีม C โยนเป็นไทยอยู่แล้ว ⇒ ตัวแปลงนี้มีผลเฉพาะกรณีบั๊ก · แก้: ให้ตกไป `ExceptionMiddleware` (มี REF code) แทน

### ✅ <pending> D-10 · P3 · สำเนาเกณฑ์ผลต่าง 0.01 ใน JS
- `settlements.html:737` `Math.abs(plan.difference) > 0.01` = สำเนาของ `SettlementBatchMath.ToleranceBaht` (`:189`) — ค่าเท่ากันวันนี้ แต่เป็นตารางกติกาชุดที่สอง
  (F2 ข้อ 4/5) · แก้: ใช้ issue ที่ blocking ของแผน (มีอยู่แล้ว) หรือส่งธง `balanced` จาก server

### ✅ <pending> D-11 · P3 · รายการรอบโอนแสดงแค่ 50 รอบ ไม่มีหน้าถัดไป
- `settlements.html:216` ไม่ส่ง skip/take · service clamp 50 (`SettlementImportService.Lines.cs:160`) ⇒ รอบเก่ากว่า 50 เปิดได้แค่ผ่าน `?batch=`

---

## PLAUSIBLE / ต้องให้เจ้าของตัดสิน

### ✅ 266acad2 D-P1 · P2 · คีย์ API (`acc_` = ตัวตนเจ้าของ) ยิง `reclassify` ได้ ⇒ คำตอบของเครื่องถูกบันทึกเป็น "ผู้ใช้เลือก (Explicit)"
- `reclassify`/`match`/`rematch`/`files/import`/`from-payment-intents` ไม่มี `[RejectApiKey]` (ตั้งใจ — ไม่ขยับ GL) แต่ `ReclassifyLineAsync` เรียก
  `RecordUserChoiceAsync(…, Explicit)` เสมอ ⇒ integration ที่จัดประเภทเป็นชุดจะเข้าคลังเรียนรู้ในชั้นสูงสุด (DECISION_DOCTRINE §3 "กันคลังเอียง")
  และ `ApplyToSameLabel=true` ขยายผลไปทั้งรอบ
- ทางเลือก: `[RejectApiKey]` ที่ reclassify · หรือ service รับ `source` จาก controller (Api ⇒ Implicit) — ต้องแก้คู่กับ `required_call_site_check` ของทีม B

### ✅ 266acad2 D-P2 · P2 · การจับคู่คอลัมน์ของช่องทาง (`ColumnMapJson`) ถูกเขียนได้สองทางด้วยด่านต่างกัน
- `PUT channels/{id}` = `Settlement.Channels` + ห้ามคีย์ API · แต่ `files/import` + `rememberColumnMap=true` (ค่าเริ่มต้น) เขียน `channel.ColumnMapJson`
  ด้วยแค่ `Settlement.Import` และคีย์ API ได้ · แผนที่นี้มี `negate`/`vatExclusive` ซึ่งเปลี่ยนเครื่องหมาย/VAT ของทุกรอบถัดไป (ข้อความคีย์ Channels
  เองบอกว่า "กำหนดภาษีของทุกรอบถัดไป") — R5 "ทางเข้าอื่นไม่เดินด่านเดียวกัน" ในขนาดเล็ก · ผลกระทบถูกจำกัดเพราะทุกรอบยังต้องผ่านพรีวิว+Post
- ทางเลือก: จำแผนเฉพาะเมื่อผู้เรียกมี Channels (ไม่มี ⇒ ใช้กับไฟล์นี้อย่างเดียว + บอกผู้ใช้) — ให้เจ้าของเลือก

### ✅ รอบ 200 ทีม S (DECISIONS 2026-09-29 ข้อ 14 · sha ใน team-S.md) D-P3 · P2 · โมดูล settlement ไม่ผูกแพ็กเกจเลย และหลบ `/bank` โดยตั้งใจ
- ไม่มี `/settlement` ใน `SubscriptionMiddleware.RouteFeatureMap` · `deposit-match` เรียก `IBankService.ReconcileAsync` (งานของ `BankReconciliation`)
  ⇒ แพ็กเกจที่ไม่มีกระทบยอดธนาคารก็ได้งานกระทบยอดผ่านทางนี้ · เมนูไม่มี `feature` · เป็นการตัดสินเชิงผลิตภัณฑ์ — **ห้ามเดาแทนเจ้าของ**
- หมายเหตุที่ตรวจเจอระหว่างทาง (ไม่ใช่ของทีม D): middleware นี้ข้ามทุกคำขอที่ไม่มี header `X-Company-Id` และ `api.js _requestCore` ไม่ส่ง header นี้เลย
  ⇒ การ gate ตามแพ็กเกจจากหน้าเว็บอาจไม่ทำงานทั้งระบบ — ควรตรวจแยก

### ✅ <pending> D-P4 · P3 · ไม่มีเพดานจำนวนแถว/ขนาดหลังคลาย zip ของไฟล์ (DoS โดยผู้มีสิทธิ์ Import)
- controller จำกัด 30 MB · reader จำกัด 25 MB ของไฟล์ดิบ แต่ไม่มีเพดานแถว (`SettlementFileReader`/`GenericColumnMapAdapter`) · xlsx 25 MB ที่คลายเป็น
  หลาย GB หรือ CSV ~500k แถว ⇒ ทุกแถวเข้า list + ธุรกรรมเดียว · ของทีม B เป็นหลัก แต่ทางเข้าอยู่ที่ D

### ✅ รอบ 200 ทีม S (DECISIONS 2026-09-29 ข้อ 14 · sha ใน team-S.md) D-P5 · P3 · `Settlement.View` เห็นผู้สมัครเอกสารขาย (เลขที่ + ยอดค้าง) โดยไม่ต้องมีสิทธิ์ดูเอกสารรายได้
- `GET batches/{id}` คืน `MatchCandidates` ของทุกบรรทัด · บทบาทที่ถูกจำกัดการดูเอกสารขายจะเห็นยอดค้างผ่านทางนี้ · ประเมินว่าควรซ่อนผู้สมัครเมื่อไม่มี Import

---

## NOT-A-BUG (ตรวจแล้ว)

- **IDOR ข้ามบริษัท**: `TenantAccessMiddleware` ตรวจสมาชิกบริษัทของ `{companyId}` (และคีย์ API ต้องตรงบริษัทของคีย์) + `RequirePermission` ตรวจสิทธิ์ต่อบริษัท ·
  ทุก id ลูก (batch/line/channel/bankTxn/document/account/contact/gatewayConfig/bankAccount) ถูกกรอง `CompanyId` ใน service หรือใน query ของ controller
  (`HeadAsync`/`LoadAsync`/`LoadEditableBatchAsync`/`ResolveCounterpartyAsync`/`GetUnreconciledAsync`/ตรวจผังในช่องทาง/ตรวจ `BankAccountId` ตอนนำเข้า)
- **ครบ 20 endpoint มี `[RequirePermission]`** · 7 endpoint ที่ขยับ GL/ภาษี มี `[RejectApiKey]` (ช่องทาง×2 · void · chargeback · post · unpost · deposit-match) ·
  `ApiKeyScopeFilter` global ยังบังคับ CanWrite ของคีย์ · checker สองตัวผ่าน
- **Unpost** ตรวจ `DocumentPermissionHelper.CanVoidAsync` ต่อชนิดเอกสารที่รอบสร้าง · ลงบัญชีตรวจสิทธิ์อนุมัติผ่าน `SettlementPostingGate` — ไม่มีการยกสิทธิ์
- **deposit-candidates ต้อง Post "เพื่อกันข้อมูลธนาคาร"** — ป้องกันได้จริงแค่ระดับ endpoint นี้: `BankController` ทั้งคลาสมีแค่ `[Authorize]`
  สมาชิกทุกคนอ่านรายการเดินบัญชีได้อยู่แล้ว (ไม่ใช่ช่องโหว่ใหม่ แต่ข้อความใน doc/commit ให้ความรู้สึกปลอดภัยเกินจริง)
- **XSS**: ทุกค่าจาก server/ไฟล์/แพลตฟอร์ม (หัวคอลัมน์ · ตัวอย่าง · issues · warnings · skippedRows · label · description · matchNote · message ·
  ชื่อช่องทาง/ผู้ติดต่อ/ผัง · ข้อความ error) ผ่าน `Layout.esc` (หนีครบ 5 ตัว) · attribute ใช้ `esc` · onclick ส่งแค่ดัชนี/boolean ·
  ลิงก์ใช้ `esc(encodeURIComponent(id))` · `confirmDanger` message เป็นข้อความคงที่ · checker 3 ตัวผ่าน
- **enum ชื่อ vs ตัวเลข**: หน้าเว็บเทียบด้วยชื่อทั้งหมด (`'User'`/`'None'`/`'Refund'`/`'Gateway'`) · ป้ายจาก `SettlementReferenceCatalog` · checker ผ่าน
- **ช่องตัวเลขว่าง**: `readHeader` ตัดคีย์ (`Layout.numOrNull`) · `OpeningWalletBalance/ClosingWalletBalance` = decimal ⇒ ว่าง = 0 (ตั้งใจ) ·
  `NetPayout`/`PayoutDate` nullable และ service บังคับพร้อมข้อความไทย
- **การตีความ payload multipart**: `FormJson` = Web defaults + `JsonStringEnumConverter` เทียบเท่า global options · `EnableLegacyTimestampBehavior` เปิด
  ⇒ วันที่ `yyyy-MM-dd` ไม่ล้มที่ Npgsql · JSON พัง ⇒ 400 ไทย (ไม่ใช่ 500)
- **error mapping**: `BusinessRuleException` ⇒ สถานะ+ข้อความไทย+`ruleCode` · `Ok=false` ⇒ 409 พร้อมผลเต็ม (`api.js` แนบ `err.body` ⇒ หน้าเว็บแสดงแผน) ·
  exception อื่นไป `ExceptionMiddleware` (ไม่มี stack trace · มี REF code) · `JobLock.RunExclusiveAsync` โยนต่อ ไม่กลืน
- **อัปโหลด**: `.csv/.txt/.xlsx` อยู่ใน `AllowedExtensions` ของ `FileAttachmentService` · `.xls` ได้ข้อความไทยจาก reader · เพดาน 25/30 MB สอดคล้อง ·
  ไฟล์ต้นฉบับอัปโหลดจากหน้าเว็บไม่ได้ (`ClientUploadAllowed=false`) · ลบ = soft (หลักฐาน) · ชนิดไฟล์ที่เก็บ = text/csv หรือ xlsx (ไม่ใช่ค่าจากผู้ใช้)
- **CSV injection**: ไม่มี export ของ settlement ในคอมมิตนี้ · `payoutRef`/ชื่อช่องทางไหลเข้าคำอธิบาย JE — ขึ้นกับ export ของสมุดรายวัน (`Helpers/CsvFieldSafety`)
- **คอมไพล์ (อ่านด้วยตา)**: ternary `r.Ok ? Ok(...) : Conflict(...)` = target-typed conditional (C# 9+, net8) มีแบบอย่างในเรพแล้ว
  (`DocumentController.cs:119` ฯลฯ) · `? other : null` ไป `Guid?` ในอาร์กิวเมนต์ constructor ได้ · EF projection เข้า record constructor ใน `Select` สุดท้ายได้ ·
  `Guarded<object>(async () => …)` ระบุชนิดไว้ · ชนิดใน `BankTransactionResponse`/entity ตรงกับ `SettlementBankTxnRow`/`SettlementBankBatchFacts` ·
  route `settlement` ไม่ชน `PaymentGatewayController` (`settlements`) · nested record ชื่อไม่ชนกับ Swagger เพราะมี `CustomSchemaIds(FullName)` ·
  DI ของ service ทั้งสามลงทะเบียนแล้ว (`Program.cs:344-355`) — **ยังไม่ได้คอมไพล์จริง** รอ CI
- **ช่องทาง: ล็อกเมื่อมีรอบ** — หน้าเว็บ (`hasBatches`) กับ service (`AnyAsync` บนตารางที่มี filter `!IsDeleted`) ใช้นิยามเดียวกัน · ค่าที่ล็อกส่ง hidden
  ค่าเดิม/`null` = คงเดิม ตรงกับ service
- **`SettlementBatchActions` เทียบด่าน service**: แก้บรรทัด/rematch/void = `IsEditable` ตรงกับ `LoadEditableBatchAsync` · unpost = Posted|BankMatched ตรง
  `UnpostCoreAsync` · จับคู่ธนาคาร = Posted ตรง `SettlementBankMatch.Check` · ลงบัญชีรอพรีวิวเสมอ — ต่างกันเฉพาะ chargeback ที่ปิดแล้ว (D-04)

---

## ผลการแก้ — ทีม D2 (รอบ 198 · คอมมิต `<pending>` · ยังไม่ได้คอมไพล์ในเครื่องนี้ — รอ CI)

| ID | ทำอะไร | ล็อกด้วย |
| --- | --- | --- |
| D-01 | **รองรับการเลือกรายการรับชำระ** (ทางที่แนะนำ): `SettlementAssignMatchRequest.PaymentIntentId` · `AssignLineMatchAsync` คำนวณผู้สมัครสดใต้ล็อกด้วย `MatchLinesAsync(apply:false)` (ข้อเท็จจริงชุดเดียวกับการจับคู่อัตโนมัติ) แล้วตัดสินด้วย `SettlementSaleMatch.AssignRefusal` — ช่องทางใช้ได้ · อยู่รอบอื่นแล้ว · ผลคืนเงินไม่แน่ชัด · **ยอดขายของออเดอร์ = ยอดรับชำระ ±0.01** · **คืนเงิน ≤ ยอดคืนที่ยังไม่ถูกนับ** · ผ่าน = `Matched`+`PaymentIntentId` ทั้งออเดอร์ · ไม่ผ่าน 409 ข้อความเดียวกับหน้าจอ · ไม่ใช่ผู้สมัคร 404 · มุมมองติดธง `Selectable/SelectReason` ต่อผู้สมัคร (ตัวเดียวกัน) + `CanAssignMatch` ต่อบรรทัด (ของแถม P3: บรรทัด `pi:` ไม่มีปุ่ม) · ยอดของออเดอร์ `OrderGroupAmounts` ตัวเดียวของสามทาง · หน้าเว็บ `matchBody` | `SettlementReview198DTests` D01×6 · `settlement_import_form_sim.js` (matchBody + negative 2) · `required_call_site_check` (AssignLineMatchAsync · ToLineView) |
| D-02 | `SettlementAccountResolver.DescribePlan/Describe` ใช้ `ResolveOne` ตัวเดียวกับการลงจริง ⇒ `SettlementPostingPreview.Accounts` (รหัส+ชื่อ+บทบาท+ปัญหา ต่อขา JE และต่อบรรทัดใบค่าธรรมเนียม + ผังพัก) · หน้าเว็บคอลัมน์ "ผังบัญชีที่จะลง" · `SettlementChartAccount.Name` | D02×2 (ผังของบัญชีธนาคาร · FeeAccountMap · ผังของบรรทัดปรับปรุง · ปัญหา = ข้อความเดียวกับแผน) · required (PreviewAsync · Describe) |
| D-03 | ฟอร์มไม่เลือกบัญชีให้ (ค่าเริ่มต้นเฉพาะมีบัญชีเดียว `SettlementBankAccountRule.DefaultChoice`) · รอบใหม่ที่มีเงินโอนต้องระบุ (`MissingForImport` · 400 พร้อมทางไปต่อ) · หัวรอบโอน/พรีวิวแสดงบัญชี · `PUT batches/{id}/bank-account` → `SetBankAccountAsync` (ล็อกก่อนโหลด · `LoadEditableBatchAsync` · tenant+เปิดใช้ · audit) · แก้ข้อความ "แก้หัวรอบโอนได้จากหน้ารอบโอน" ที่ไม่จริง | D03×2 · required (SetBankAccountAsync · PersistAsync) |
| D-04 | `ISettlementPostingService.ClosedChargebacksAsync` (นิยาม "ปิดไว้แล้ว" `OpenChargebackEntries` ตัวเดียวกับด่านกันลงซ้ำ) → `SettlementBatchActions` ตัดปุ่ม + `ClosedChargebacks` (เลข JE + คำอธิบายที่บอกแพ้/ชนะ) | D04 (สองทิศ) · required (For · ClosedChargebacksAsync · ResolveChargebackCoreAsync) |
| D-05 | ตัดสินอย่างตั้งใจ = **ตัดออก**: `BatchFilterStatuses` ไม่มี Voided · `GET batches?status=Voided` ⇒ 400 พร้อมเหตุผล · กิ่ง Voided ใน `SettlementBatchActions` คงไว้เป็นทางป้องกัน (doc-comment บอกว่าไม่ใช่เส้นที่ผู้ใช้เห็น) | D05 · required (ListBatches) |
| D-06 | `SettlementActionPermissions(Import, Post, JournalManage)` จาก `IPermissionService` คีย์เดียวกับ `[RequirePermission]` · `CanPreview` แยกจาก `CanPost` · `PermissionNote` บอกปุ่มที่ถูกซ่อน + สิทธิ์ที่ต้องขอ | D06×2 · required (For · BatchDetailAsync) |
| D-07 | `HeaderPresent` ทั้ง `files/import` และ `from-payment-intents` ⇒ 400 ไทย | required (ImportFile · ImportFromIntents) |
| D-08 | `ChargebackResolveRequest(bool? Won)` ⇒ ไม่ส่ง = 400 | required (ResolveChargeback must_re) |
| D-09 | ข้อความไทย ⇒ 404 ข้อความนั้น · ไม่ใช่ไทย ⇒ โยน `SettlementInternalLookupException` ⇒ `ExceptionMiddleware` 500 + รหัสอ้างอิง | — (เส้น controller · ไม่มีเทสต์ที่มี HttpContext) |
| D-10 | `SettlementPostingPlan.Balanced` = `SettlementBatchMath.IsBalanced` (ตัวเดียวกับปัญหา `Unbalanced`) · JS ใช้ `plan.balanced` | D10 |
| D-11 | `GET batches` = `{items, skip, take, hasMore}` (≤100/หน้า) · ปุ่ม "โหลดรอบที่เก่ากว่า" | — |
| D-P1 | `[RejectApiKey]` ที่ reclassify (ไม่เปลี่ยนสัญญา `Explicit` ของ service) | `owner_action_wiring_check` +2 แถว (self-test ถอดแล้วฟ้อง) |
| D-P2 | `SettlementPermissionScope.ColumnMapMemory` — จำเฉพาะผู้มี `Settlement.Channels` และไม่ใช่คีย์ API · ไม่ผ่าน = ใช้กับไฟล์นี้ + คำเตือนในผล · หน้าเว็บแทนช่องติ๊กด้วยเหตุผล (`reference.columnMapMemory`) | DP2 · required (ImportFile) |
| D-P3 | **ไม่แตะ** — คำตัดสินเจ้าของข้อ 5 (gate แพ็กเกจ) ทีม G รับไป · หมายเหตุ `X-Company-Id` ส่งต่อทีม G · **รอบ 200 ทีม S**: `("/settlement", BankReconciliation)` ใน `RouteFeatureMap` + เมนู 2 รายการ `feature: BankReconciliation` · `X-Company-Id` แก้แล้วรอบ 198 (`TenantCompanyId`) | `SubscriptionGatePolicyTests` (settlement ×2 + ทิศตรงข้าม ×3) · `SubscriptionTrialReadinessTests` |
| D-P4 | controller ตอบ 400 ไทยเมื่อไฟล์ > 25 MB ก่อนอ่าน · หน้าเว็บเตือนก่อนอัปโหลดด้วยตัวเลขจากเซิร์ฟเวอร์ · `SettlementFileReader.Capped` 100,000 แถว / 500 คอลัมน์ อ่านทีละแถวหยุดทันที (xlsx/CSV) | DP4×2 |
| D-P5 | backlog — "View เห็นผู้สมัครเอกสารขาย" เป็นคำถามขอบเขตสิทธิ์ (ซ่อนผู้สมัครเมื่อไม่มี Import หรือไม่) ให้เจ้าของตัดสิน · **รอบ 200 ทีม S (ข้อ 14)**: `SettlementPermissionScope.CandidatesHiddenReason/HideCandidates` — ไม่มีทั้ง Import และ Post ⇒ ซ่อน `MatchCandidates` + `MatchNote` + ตอบ `candidatesHiddenReason` (แบนเนอร์ 🔒) | `SubscriptionTrialReadinessTests` D-P5 ×2 · required (BatchDetailAsync) |

## ลำดับที่แนะนำ
1. D-01 (ผู้ใช้เจอทางตันพร้อมข้อความผิด) · D-02 + D-03 (ข้อมูลที่ผู้อนุมัติเห็นไม่ตรงสิ่งที่ลงจริง — แก้คู่กัน: ให้ server resolve ผัง+ชื่อบัญชีธนาคารลงในพรีวิว)
2. D-P1/D-P2/D-P3 ส่งเจ้าของตัดสิน (อย่าเดา)
3. P3 ที่เหลือเก็บตามสะดวก (D-04/D-05 ควรไปพร้อมกันเพราะแตะ `SettlementBatchActions` + เทสต์ตัวเดียวกัน)
