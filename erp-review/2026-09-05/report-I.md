# ทีม I — ความสอดคล้องของหน้าจอ/เมนู/สถานะ ข้ามหน้า + แผนที่ช่องว่างสู่ ERP

> ฐาน: HEAD `a054340` · branch `claude/erp-system-review-team-660mev` · 2026-09-05
> วิธี: ใช้ผลสแกน enum×หน้า รอบแรก (`scratchpad/team/I/labelmaps.md` · `options.md` · `enums.json`) + สคริปต์ใหม่ 2 ตัว
> (`formpayload_scan.py` ฟอร์ม↔payload · `deadref_scan.py` เมธอด/API ที่ไม่มีนิยาม) · เปิดไฟล์ยืนยันทุกข้อก่อนเขียน
> **ไม่แตะ `DocumentType`/`DocumentStatus`** (ทีม A ทำครบแล้ว — A-07…A-13) · ไม่ได้คอมไพล์ (env ไม่มี .NET SDK)

## สรุป 5 บรรทัด
_(เขียนตอนจบ — ดูท้ายไฟล์)_

## Findings (เรียง P0→P3)

### I-01 [P1][S] รายการเกิดซ้ำ "รายปี" สร้าง/แก้ไม่ได้เลย — หน้าเว็บส่ง `Yearly` แต่ enum ฝั่ง server ชื่อ `Annual`
- ไฟล์: `Accounting/wwwroot/pages/recurring.html:58-64` (select) · `:455` (payload) · `:180` และ `:499` (label map) · `:552` (hydrate) ·
  `Accounting/Models/Enums/AllEnums.cs:683-692` · `Accounting/Models/DTOs/Recurring/RecurringDtos.cs:8,25` · `Accounting/Program.cs:752`
- โค้ด:
  - recurring.html:63 `<option value="Yearly">รายปี</option>` · :455 `frequency: document.getElementById('fFrequency').value,`
  - AllEnums.cs:683 `public enum RecurringFrequency { Daily=1, Weekly=7, BiWeekly=14, Monthly=30, Quarterly=90, SemiAnnual=180, Annual=365 }`
  - RecurringDtos.cs:8 `RecurringFrequency Frequency,` (Create) · :25 `RecurringFrequency? Frequency,` (Update)
  - Program.cs:752 `Converters.Add(new JsonStringEnumConverter())` (ไม่มี converter พิเศษที่ map Yearly→Annual — `grep -rn Yearly Services Controllers Models Helpers` เจอแค่ LodgingSeason/RecurringExpenseDetector ซึ่งเป็น string คนละเรื่อง)
- ทำไมพัง: ผู้ใช้เลือก "รายปี" → payload `{"frequency":"Yearly"}` → `POST api/companies/{id}/recurring` (`RecurringController.cs:38-46` bind `[FromBody] CreateRecurringTransactionRequest`)
  → `JsonStringEnumConverter` ไม่รู้จัก `Yearly` → model binding ล้ม → **400** ทุกครั้ง ทั้งสร้างและแก้ไข
  → ทางกลับ: แถวที่ `Frequency=Annual/BiWeekly/SemiAnnual` (เช่นถูกสร้างจาก API/ทางอื่น) ตอน hydrate `fFrequency.value = 'Annual'` (:552) ไม่มี option → select ว่าง → กดบันทึกส่ง `frequency: ""` → 400 อีกทาง;
  ตาราง/รายละเอียด (:185, :520) โชว์ `freqMap[r.frequency] || r.frequency` = คำอังกฤษดิบ "Annual"
- ผลกระทบ: ค่าเช่า/ประกัน/ค่าบริการรายปี ตั้งเป็นรายการเกิดซ้ำไม่ได้เลยตั้งแต่เขียนหน้า — ผู้ใช้เห็นแค่ "บันทึกไม่สำเร็จ" ไม่รู้ว่าเพราะคำเดียวในตัวเลือก ·
  ตัวเลือก 3 ใน 7 ของ enum (BiWeekly/SemiAnnual/Annual) ไม่มีใน UI แม้ backend เดินได้ครบ (`RecurringTransactionService.cs:350-358` switch ครบ 7 ค่า)
- defect class (CLAUDE.md): "สำเนามือฝั่ง JS ที่ตามหลังอยู่ไม่กี่ธง" + "ค่าที่ถูกต้องทางไวยากรณ์ทุกประการ — ไม่มี checker/compiler ตัวไหนจับ" (ญาติ A-09 `risk.html` ส่ง `JournalEntry` เข้า enum)
- ทางแก้ที่เสนอ: เปลี่ยน option/label เป็น `Annual` + เพิ่ม `BiWeekly`/`SemiAnnual` — ดีกว่านั้น: สร้าง `<select>` และ label จาก `Layout.enumLabel('RecurringFrequency', v)` ตัวเดียว (ดู I-ERP ข้อ resolver กลาง) ·
  checker `enum_option_value_check.py`: ทุก `<option value="X">` ของ select ที่ id/ชื่อผูกกับ enum ต้องเป็นสมาชิก enum (negative test = ใส่ `Yearly` กลับ)
- ความมั่นใจ: **สูง** (สาย UI→api.js:790→Controller→DTO→enum เปิดครบทุกข้อ) — เหลือรัน/ยิงจริงเพื่อดูข้อความ 400 เท่านั้น

### I-02 [P1][S] ปุ่ม "ลบ" ตาย 5 จุดใน 4 หน้า — เรียก `API.delete(...)` ซึ่ง **ไม่มีอยู่ใน api.js** (ของจริงชื่อ `del`)
- ไฟล์: `Accounting/wwwroot/js/api.js:138` · `pages/employees.html:726` · `pages/leave-types.html:328` · `:392` · `pages/project-time.html:322` · `pages/roles.html:457`
- โค้ด:
  - api.js:138 `del(url, signal) { return this.request('DELETE', url, null, false, signal); },` — `grep -rn "API\.delete\s*=\|delete\s*:" js/ pages/*.js` = **0** (ไม่มีนิยาม `delete` ที่ไหนเลย) · เรพใช้ `API.del(` ถูกอยู่ **79 จุด**
  - employees.html:726 `await API.delete(\`/api/companies/${cid}/hr/compensation/employees/${empId}\`);`
  - leave-types.html:328 `try { await API.delete(\`/api/companies/${cid}/leaves/admin/types/${id}\`); Layout.toast('ลบแล้ว'); …` · :392 (วันหยุด)
  - project-time.html:322 `await API.delete(\`/api/companies/${cid}/hr/project-time/${id}\`);` · roles.html:457 `await API.delete(\`/api/company/${cid}/roles/${roleId}\`);`
- ทำไมพัง: กดลบ → `API.delete` = `undefined` → `TypeError: API.delete is not a function` → เข้า `catch` → toast ข้อความ error ภาษาอังกฤษของเบราว์เซอร์ (leave-types) หรือเงียบ → **ไม่มี request ออกไปเลย** · JS ไม่ฟ้องจนกว่าจะถึงบรรทัดนั้น (`node --check` ผ่าน)
- ผลกระทบ: ลบโครงสร้างค่าตอบแทนพนักงาน · ลบประเภทการลา · ลบวันหยุดบริษัท · ลบบันทึกเวลาโครงการ · **ลบ Role ที่กำหนดเอง** — ทำไม่ได้เลยทั้ง 5 ทางตั้งแต่เขียนหน้า (ผู้ใช้เห็น "ลบไม่สำเร็จ" หรือ toast ภาษาอังกฤษ)
- defect class (CLAUDE.md): "เรียกเมธอดที่ไม่มีนิยาม — JS ไม่ฟ้องจนกว่าจะถึงบรรทัดนั้น" (เคส `this.edit()`/`viewDetail` ใน documents.html รอบก่อน) — คราวนี้เป็นฝั่ง `API.*` ซึ่ง checker/harness เดิมไม่มองเลย
- ทางแก้ที่เสนอ: (ก) เปลี่ยน 5 จุดเป็น `API.del` **หรือ** (ข) เพิ่ม alias `delete(url, signal) { return this.del(url, signal); }` ใน api.js (กันตัวถัดไปด้วย — ชื่อ `delete` เป็นชื่อที่ทุกคนเดาก่อน) · เพิ่ม `tools/api_method_check.py`: ทุก `API.x(`/`api.x(`/`Layout.x(` ในหน้า ต้องมีนิยามใน api.js/layout.js (negative test = `API.delete` ทั้ง 5 จุดนี้ · false-positive guard: หน้าที่ประกาศ `API.x =` เองให้ข้าม)
- ความมั่นใจ: **สูง** (นิยามค้นทั้ง `js/` + `pages/*.js` = 0 · call site เปิดครบ 5)

### I-03 [P1][S] POS บันทึก "ประเภทออเดอร์" ผิดสมาชิก enum ทุกใบ — select ส่งเลข 0–4 ตามความหมายของหน้าจอ แต่ `PosOrderType` เริ่มที่ WalkIn=1 ⇒ ทานที่ร้าน→(0 ไม่มีใน enum) · กลับบ้าน→WalkIn · เดลิเวอรี่→DineIn · คิว→TakeAway · นัดหมาย→Delivery
- ไฟล์: `Accounting/wwwroot/pages/pos.html:171-177` (select) · `:1701,1713` และ `:2262` (payload 2 เส้น) · `:1692-1694` (UI ตัดสินจาก `'0'`/`'3'`) · `:2408` (label map) ·
  `Accounting/Models/Enums/AllEnums.cs:1015-1023` · `Models/DTOs/Pos/PosDtos.cs:80` · `Services/Implementations/PosService.Orders.cs:57` · `pages/pos-kds.html:81-85,203-205,311` · `pages/pos-reports.html:551`
- โค้ด:
  - pos.html:172-176 `<option value="0">ทานที่ร้าน</option><option value="1">สั่งกลับบ้าน</option><option value="2">เดลิเวอรี่</option><option value="3">คิว</option><option value="4">นัดหมาย</option>`
  - pos.html:1701 `const ot = parseInt(document.getElementById('orderType').value);` · :1713 `orderType: ot,` · :2262 `orderType: document.getElementById('orderType').value || 'DineIn',` (ส่ง string `"0"`–`"4"`)
  - AllEnums.cs:1015 `public enum PosOrderType { WalkIn = 1, DineIn = 2, TakeAway = 3, Delivery = 4, Appointment = 5, Online = 6 }`
  - PosDtos.cs:80 `PosOrderType OrderType,` · PosService.Orders.cs:57 `OrderType = request.OrderType,` (ไม่มี `Enum.IsDefined`)
  - pos-kds.html:84 `<option value="Takeaway">นำกลับ</option>` · :205 `if (cat) list = list.filter(o => o.orderType === cat);` · :311 `({ DineIn: '🪑 ทานที่ร้าน', Takeaway: '🥡 กลับ', Delivery: '🛵 ส่ง' })[t] || t`
  - pos-reports.html:551 `{ 0:'ทานที่ร้าน', 1:'กลับบ้าน', 2:'เดลิเวอรี่', 3:'คิว', 4:'นัดหมาย', DineIn:'ทานที่ร้าน', TakeAway:'กลับบ้าน', … }` (สำเนามือชุดที่ 3 ที่พยายามรองรับ**ทั้งสอง**ระบบเลข = หลักฐานว่าเคยเจออาการแล้วแต่แก้ที่ป้าย)
- ทำไมพัง: `JsonStringEnumConverter` รับตัวเลข/สตริงตัวเลขและ**ไม่ตรวจว่าเป็นสมาชิกที่นิยาม** → ค่า 0 ถูกเก็บลง `PosOrder.OrderType` เป็น 0 (นอก enum) · 1–4 ถูกเก็บเป็นสมาชิก**ถัดไปหนึ่งขั้น**จากที่ผู้ใช้เลือก
  → ตอนอ่านกลับ server serialize เป็นชื่อ (`"WalkIn"`, `"DineIn"`…) ยกเว้น 0 ที่ออกเป็นเลข `0`
  → KDS: ตัวกรอง "นำกลับ" (`'Takeaway'` — สะกดคนละแบบกับ enum `TakeAway` ด้วย) **ไม่มีวัน match** · ตัวกรอง "ทานที่ร้าน" (`DineIn`) ได้**ออเดอร์เดลิเวอรี่** · "เดลิเวอรี่" ได้**นัดหมาย** · ออเดอร์ทานที่ร้านจริง (0) ไม่ติดตัวกรองไหนเลย
  → pos.html:2408 และ pos-reports:551 โชว์ `"WalkIn"` ดิบ (ไม่มีคีย์) สำหรับออเดอร์กลับบ้าน
- ผลกระทบ: ข้อมูลช่องทางขายของ POS **ผิดในฐานข้อมูลทุกแถวตั้งแต่เขียนหน้า** (แก้ป้ายไม่พอ ต้อง migration) · รายงานแยกช่องทาง/KDS แยกประเภทใช้ไม่ได้ · เมื่อใดที่มีคนเพิ่มตรรกะบน `OrderType` (ค่าบริการ dine-in · ค่าส่ง · ภาษีที่ต่างกัน) จะผิดทันทีโดยไม่มีอะไรเตือน (วันนี้ `grep OrderType ==` ใน Services = 0 จึงยังไม่กระทบเงิน)
- defect class (CLAUDE.md): "สำเนามือ ≥3 ชุดที่ไม่เท่ากันเอง" + "ค่าที่ถูกต้องทางไวยากรณ์ — ไม่มี checker จับ" + "คำเตือนที่แก้ที่ป้ายแทนที่ต้นเหตุ" (pos-reports:551 รองรับสองระบบเลข)
- ทางแก้ที่เสนอ: (1) select ใช้ **ชื่อ enum** (`DineIn/TakeAway/Delivery/Appointment`) และเลิกใช้ "คิว" เป็นชนิดออเดอร์ (คิวเป็น attribute `QueueNumber` อยู่แล้ว) (2) server: `if (!Enum.IsDefined(request.OrderType)) throw BusinessRuleException` (3) migration แปลงแถวเดิม `0→DineIn(2)`, `1→TakeAway(3)`, `2→Delivery(4)`, `4→Appointment(5)` **เฉพาะแถวที่สร้างจาก pos.html** (ต้องดู `Source`/`ClientOrderId` ก่อน — แถวจาก API อื่นอาจถูกอยู่แล้ว) (4) label/option ทุกหน้า POS จาก resolver เดียว · checker: `<option value="<digit>">` บน select ที่ผูก enum แบบชื่อ → ฟ้อง
- ความมั่นใจ: **สูง** สำหรับการเก็บผิดสมาชิก (สาย select→payload→DTO→entity เปิดครบ) · **กลาง** สำหรับ "0 เก็บลงได้ไม่ throw" — STJ ไม่ validate defined-ness โดยปริยาย แต่ควรยิงจริง 1 ครั้งเพื่อยืนยันว่าไม่มี validator อื่นดัก

### I-04 [P1][S] ลงทะเบียน "ที่ดิน/งานระหว่างก่อสร้าง" จากหน้าสินทรัพย์ไม่ได้เลย — server บังคับ `DepreciationMethod.None` พร้อมข้อความ "เลือกวิธีคิดค่าเสื่อมเป็น 'ไม่คิดค่าเสื่อม'" แต่ select ไม่มีตัวเลือกนั้น
- ไฟล์: `Accounting/wwwroot/pages/fixed-assets.html:121-125` (select) · `:559` (hydrate) · `:592` (payload) · `:286` (label map) · `pages/document-scan.html:3597` (select ขึ้นทะเบียนจากใบซื้อ — ก็ไม่มี None) ·
  `Accounting/Services/Implementations/FixedAssetService.cs:48-56` · `:171` · `Services/Implementations/Tax/FixedAssetAccountClassifier.cs:19`
- โค้ด:
  - fixed-assets.html:121 `<select class="form-select" id="fMethod"><option value="StraightLine">…<option value="DecliningBalance">…<option value="DoubleDecliningBalance">…</select>` (3 จาก 4 สมาชิก — label map :286 ก็ไม่มี `None`)
  - FixedAssetService.cs:49-56 `if (assetClass is { Depreciable: false } && request.DepreciationMethod != DepreciationMethod.None) { throw new BusinessRuleException($"\"{assetClass.Category}\" (ผัง …) คิดค่าเสื่อมราคาไม่ได้ … ทางแก้: เลือกวิธีคิดค่าเสื่อมเป็น \"ไม่คิดค่าเสื่อม\" …` 
  - fixed-assets.html:559 `document.getElementById('fMethod').value = a.depreciationMethod;` (แถว None ที่มาจาก API/OCR → select ว่าง → :592 ส่ง `depreciationMethod: ""` → 400)
- ทำไมพัง: ผู้ใช้เลือกผังที่ดิน (16xx ตาม `FixedAssetAccountClassifier`) → กดบันทึก → server ปฏิเสธถูกต้องตาม พ.ร.ฎ.145/TFRS NPAEs บทที่ 10 → ข้อความบอกให้เลือกตัวเลือกที่**ไม่มีบนจอ** → ผู้ใช้วนลูป · ทางออกเดียวคือเปลี่ยนผังเป็นอาคาร (ข้อมูลผิด) หรือไม่ลงทะเบียน (ทะเบียนทรัพย์สินไม่ครบ)
- ผลกระทบ: ทะเบียนสินทรัพย์ไม่มีที่ดินได้เลย → งบแสดงฐานะการเงิน/ทะเบียนทรัพย์สินตาม พ.ร.บ.การบัญชี ม.10 ขาด · แรงจูงใจให้ผู้ใช้ลงที่ดินเป็น "อาคาร" แล้วคิดค่าเสื่อม = รายจ่ายต้องห้าม (§65ตรี · CLAUDE.md G ระบุ "ที่ดิน → DepreciationMethod = None (validation)" — validation มี แต่ UI ไม่ให้ผ่าน)
- defect class (CLAUDE.md): "ครึ่งเซิร์ฟเวอร์ของการแก้ ship ไปคนเดียว" (ด่าน + ข้อความทางแก้เสร็จฝั่ง server · ฟอร์มไม่ตาม) + "ข้อความ error ที่ชี้ทางแก้ที่ไม่มีอยู่จริง"
- ทางแก้ที่เสนอ: เพิ่ม `<option value="None">ไม่คิดค่าเสื่อม (ที่ดิน/งานระหว่างก่อสร้าง)</option>` ทั้ง 2 select + label map · auto-select None เมื่อผัง `Depreciable:false` (ใช้ classifier เดียวกันผ่าน endpoint ไม่ใช่สำเนา JS) · เทสต์: select ต้องมี option ครบทุกค่าที่ server **บังคับให้เลือก**
- ความมั่นใจ: **สูง** (ด่าน + select + hydrate เปิดครบ)

### I-05 [P2][S] ฟอร์มแก้ไขพนักงาน: ช่องธนาคาร 3 ช่องขึ้นว่างทุกครั้ง · "ชื่อบัญชี" แก้แล้วไม่ส่ง · ติ๊ก "อยู่ในระบบประกันสังคม" ถูกรีเซ็ตเป็นติ๊กเสมอและ**ไม่เคยส่ง**ตอนแก้ไข (silent no-op)
- ไฟล์: `Accounting/wwwroot/pages/employees.html:173-176` (input) · `:393` (reset list) · `:404` (`fSso.checked = true`) · `:415-433` (hydrate — ไม่มี bank/sso) · `:460-464` (create ส่งครบ) · `:485-503` (update) ·
  `Accounting/Models/DTOs/Payroll/PayrollDtos.cs:54-91` (`UpdateEmployeeRequest` มี `IsSubjectToSocialSecurity` :74 แต่**ไม่มี** `BankAccountName`) · `Services/Implementations/PayrollService.cs:368-369,412-413`
- โค้ด:
  - :404 `document.getElementById('fSso').checked = true;` (รันทั้งตอนสร้างและตอนเปิดแก้ — hydrate :415-433 ไม่ตั้งค่าจาก `e.isSubjectToSocialSecurity`)
  - update :491-492 `bankName: …fBankName…trim() || null, bankAccountNumber: …fBankAcct… || null,` — **ไม่มี** `bankAccountName` และ **ไม่มี** `isSubjectToSocialSecurity` (create :462-464 มีทั้งคู่)
  - PayrollService.cs:368 `if (request.BankName != null) employee.BankName = request.BankName;` (null = คงเดิม → ไม่เสียข้อมูล แต่ล้างค่าไม่ได้ด้วย)
- ทำไมพัง: เปิดแก้ → ช่องธนาคารว่าง (ไม่ hydrate) ทั้งที่ DB มีค่า → ผู้ใช้เข้าใจว่าหาย พิมพ์ใหม่ (ถ้าพิมพ์ต่างจากเดิม = ทับ) · แก้ "ชื่อบัญชี" → ไม่ถูกส่ง → "แก้ไขสำเร็จ" แต่ไม่เปลี่ยน · พนักงานที่ `IsSubjectToSocialSecurity=false` เปิดแก้เห็นติ๊ก ✓ · ปลดติ๊ก/ติ๊ก → ไม่ส่ง → ไม่มีผล
- ผลกระทบ: ธง ปกส. คือตัวตัดสินว่าพนักงานเข้า สปส.1-10 / หัก 5% หรือไม่ — ผู้ใช้ที่ตั้งใจถอดพนักงานออกจากระบบ ปกส. ผ่านหน้าแก้ไข **ทำไม่ได้** และหน้าจอโกหกว่าเขาอยู่ในระบบ · ไฟล์โอนเงินเดือน (I-ERP) จะใช้เลขบัญชี/ชื่อบัญชีที่แก้ไม่ได้
- defect class (CLAUDE.md): "เก็บแล้วต้อง echo กลับ" + "ห้าม silent no-op" (ญาติ D-01 products / D-10 accounts)
- ทางแก้ที่เสนอ: hydrate 4 ช่องจาก `e.*` · update ส่ง `bankAccountName` (เพิ่มใน DTO + service) และ `isSubjectToSocialSecurity` (DTO รองรับอยู่แล้ว :74) · ช่องที่ server ไม่รับตอน update ให้ล็อก + บอกเหตุผล
- ความมั่นใจ: **สูง**

### I-06 [P1][S] "ใส่ข้อมูลตัวอย่าง" ทำได้โดย**สมาชิกคนไหนก็ได้**ของบริษัท — และมันกินเลขรัน §86/4 จริง + ตั้ง `Status=Approved` ตรงโดยไม่ผ่าน `IDocumentService`
- ไฟล์: `Accounting/Controllers/SampleDataController.cs:22-23` (route + `[Authorize]` ระดับคลาสอย่างเดียว) · `:37-60` (Seed — ไม่มี `IsOwner`/`HasPermission` เลยสักบรรทัด) · `:74,80,110,115` · `:142-158` (DELETE ก็ไม่มีด่าน) · `wwwroot/pages/sme-config.html:532,546` (UI เรียก)
- โค้ด:
  - :22-23 `[Route("api/companies/{companyId:guid}/sample-data")] [Authorize]` · :37 `[HttpPost("seed")] public async Task<…> Seed(Guid companyId)` → ตรวจแค่ "มี SAMPLE_DATA แล้วไหม" แล้วเดินต่อ
  - :74 `DocumentNumber = await Accounting.Helpers.DocumentNumberGenerator.NextAsync(_db, companyId, DocumentType.Invoice…` · :80 `Status = DocumentStatus.Approved,` · :98 `_db.Documents.Add(inv);` (Expense แบบเดียวกัน :110-128)
  - เทียบ `BulkCleanupController.cs:32-48` ที่มี `IsOwnerAsync` + `Forbid()` — ของคู่กัน ("ล้าง" กับ "ใส่") แต่คนละมาตรฐาน
- ทำไมพัง: (1) `TenantAccessMiddleware` ตรวจแค่เป็นสมาชิกบริษัท (`:90-101`) — Staff/Viewer/custom-role strict ยิง `POST …/sample-data/seed` ตรงได้ (2) เลขที่ออกจาก `DocumentNumberGenerator.NextAsync` = ชุดเดียวกับใบจริง ⇒ เลข INV/EXP ของบริษัทถูกกิน (3) เอกสารถูกตั้ง Approved ตรง ⇒ **ไม่มี JE** แต่ถูกนับในรายงานภาษีขาย/ภ.พ.30 ที่กรองด้วยสถานะ (ทีม F: F-05 กรอง `!= Voided && != Draft`) (4) Cleanup (:142) soft-delete ⇒ เลขที่ใช้ไปแล้วเป็น **gap ถาวร** ใน series ที่กฎหมายบังคับ gap-free และลบ Contact ด้วย**ชื่อขึ้นต้น** ("บริษัท ผู้ขาย ก…") ⇒ ผู้ติดต่อจริงที่ชื่อบังเอิญขึ้นต้นเหมือนกันโดนลบด้วย
- ผลกระทบ: เอกสาร "ตัวอย่าง" 2 ชนิดเข้าเล่มจริงของบริษัท (เลขจริง · ยอด VAT จริง · ไม่มี GL) โดยผู้ใช้ที่ไม่ควรทำได้ · ภ.พ.30/รายงานภาษีขายรวมยอดตัวอย่าง จนกว่าจะมีคนกด Cleanup ซึ่งทิ้ง gap ไว้
- defect class (CLAUDE.md): "`[Authorize]` ระดับคลาส = ล็อกอินอยู่ไหม ไม่ใช่มีสิทธิ์ทำสิ่งนี้ไหม" + ERP_REVIEW §2 ราก "เขียน `Document.Status` นอก `IDocumentService`" (B-02/MAIN-01 — จุดนี้ยังไม่อยู่ในลิสต์) · `write_permission_gate_check` ไม่ครอบไฟล์นี้ (allow-list)
- ทางแก้ที่เสนอ: ด่าน Owner แบบเดียวกับ `BulkCleanupController.IsOwnerAsync` ทั้ง seed/DELETE · seed ผ่าน `IDocumentService.CreateAsync` + `ApproveDocumentAsync` (ได้ JE/tax point/retention ครบ) **หรือ** เก็บเป็น Draft `DRAFT-{guid}` ไม่กินเลข · Cleanup ลบด้วย `Reference == "SAMPLE_DATA"` เท่านั้น ไม่ใช่ prefix ชื่อ · เพิ่ม `SampleDataController` เข้า allow-list ของ `write_permission_gate_check` + `document_status_writer_check` (ERP_REVIEW §8 ข้อ 1)
- ความมั่นใจ: **สูง** (ไม่พบใน SYSTEM_REVIEW/ERP_REVIEW/report-A–G — `grep SampleData` = 0)

### I-07 [P1][L] "สิทธิ์ตามเมนู" (roles.html) บังคับที่**หน้าจอเท่านั้น** — server ตรวจ `IPermissionService` ใน 10 จาก ~140 controller · middleware ไม่ตรวจ permission เลย ⇒ custom-role ที่ติ๊กเมนูให้แค่ "ดูรายงาน" ยังยิง endpoint เขียนของอีก ~130 controller ได้ (ระบบ · เชื่อมกับทีม G)
- ไฟล์: `Accounting/wwwroot/js/layout.js:453-470` (`hasMenuAccess` — ตรรกะ allowedMenuIds อยู่ที่นี่ที่เดียว) · `Middleware/TenantAccessMiddleware.cs:84-101` (อ่าน `Role`/`CompanyRoleId` เพื่อยืนยันสมาชิกภาพ ไม่ตัดสินสิทธิ์) · `Services/Interfaces/IPermissionService.cs:16,21` · controller ที่ inject `IPermissionService`: `Company · Dashboard · Document · ExpenseClaim · Leave · Me · Metering · Payroll · Pdpa · SalaryAdvance` (10 ไฟล์)
- โค้ด: `grep -rn 'HasPermissionAsync\|HasMenuAccessAsync' Middleware/*.cs` = **0** · controller ที่ไม่มี `RequirePermission/HasPermission/Deny*/PermissionKeys/Roles=/IsOwner` เลย = **~110 ไฟล์** รวมที่เขียนเงิน/สต๊อก/ตั้งค่า: `BankController · FixedAssetController · RecurringController · BudgetController · WarehouseController · StockTransferController · ProductController · ProjectController · TaxController · EtaxController · SettingsController · ChequeController · LoanController · ImportExportController · WithholdingTaxCertController · WhtCreditController · IntercompanyController · ConsolidationController · CurrencyController · ProcurementController · TimeBillingController · CommissionController · SampleDataController (I-06)` (บางตัวอาจมีด่านใน service ชั้นล่างแบบ `RolePermissionService.EnsureOwnerAccessAsync` — ตรวจแล้วเฉพาะ RolePermission/BulkCleanup ว่ามี · ที่เหลือ**ยังไม่ได้เปิดทีละไฟล์**)
- ทำไมพัง: แอดมินสร้าง role "พนักงานคลัง" ติ๊กเมนู 3 อัน → หน้าจอซ่อนเมนูอื่นถูกต้อง → แต่ token ของสมาชิกคนนั้นใช้ยิง `PUT /bank/...`, `POST /fixed-assets`, `POST /warehouse/transfers`, `PUT /settings` ได้ทั้งหมด (ผ่าน `TenantAccessMiddleware` เพราะเป็นสมาชิก) — สิทธิ์ที่ตั้งบนหน้า Role เป็น**การซ่อน**ไม่ใช่**การห้าม**
- ผลกระทบ: โมเดลสิทธิ์ที่ขายให้ลูกค้า (แยกหน้าที่ · segregation of duties ซึ่งเป็นแกนของ ERP) ไม่มีจริงฝั่ง server ยกเว้นเอกสาร/เงินเดือน/ค่าใช้จ่าย/ลา/PDPA/metering · ผู้สอบบัญชีถาม "ใครแก้ยอดธนาคารได้" ตอบจาก role ไม่ได้
- defect class (CLAUDE.md): "`[Authorize]` ระดับคลาส = ล็อกอินอยู่ไหม" + "checker ที่เป็น allow-list ให้ความมั่นใจเท่ากับ 'ลิสต์ครบไหม'" (`write_permission_gate_check` เฝ้า 3 ไฟล์)
- ทางแก้ที่เสนอ (ราก — ไม่ใช่ไล่ 110 ไฟล์ด้วยมือ): ผูก **permission key เข้ากับ route ที่ชั้นเดียว** — เช่น `[RequirePermission("Bank.Write")]` attribute + action filter ที่เรียก `IPermissionService` · หรือ map `navItem.id → controller prefix` แล้วให้ `TenantAccessMiddleware` ปฏิเสธ verb เขียนเมื่อ role strict ไม่มีเมนูนั้น (ใช้ตารางเดียวกับที่ `roles.html` ใช้ — ไม่มีสำเนาที่สอง) · ขยาย `write_permission_gate_check` เป็น **deny-list** (controller สาธารณะระบุชื่อ) แทน allow-list
- ความมั่นใจ: **สูง** ว่า middleware/controller ส่วนใหญ่ไม่มีด่าน · **กลาง** สำหรับ "ไม่มีด่านใน service ชั้นล่าง" ของแต่ละไฟล์ — ทีม G ควรเป็นเจ้าของรายการต่อไฟล์

### I-08 [P2][M] label map ของ enum ที่คัดลอกมือหลายหน้า — ตารางค่าที่ขาด/เกิน/ชื่อไทยไม่ตรงกัน (จาก `labelmaps.md` + `options.md` · ยกเว้น DocumentType/DocumentStatus ที่ทีม A ทำแล้ว)
| enum (สมาชิก) | หน้า:บรรทัด | ขาด (โชว์อังกฤษดิบ/กรองไม่ได้) | เกิน/ผิดชื่อ | ชื่อไทยไม่ตรงข้ามหน้า |
| --- | --- | --- | --- | --- |
| **RecurringFrequency** (7) | recurring.html:58 select · :180, :499 map · subscription.html:208,228 · sme-config.html:115 · loans.html:60 · employees.html:102 (`fSalaryType`) | recurring: `BiWeekly/SemiAnnual/Annual` · sme-config: 3 ตัว · loans: `SemiAnnual/Annual` | recurring **`Yearly` (→ I-01, 400)** · employees `Hourly` (ตรวจแล้ว: `SalaryType` เป็นคนละ enum — ไม่ใช่บั๊ก) | Quarterly = "รายไตรมาส" (recurring) vs "ราย 3 เดือน" (subscription) |
| **PaymentMethod** (8) | payments.html:88 map · :22 · :54 select · pos.html:2760 · pos-reports.html:546 · :374 select · subscription.html:209 · admin/payments.html:360 | `Other` ทุกหน้า (payments.html:54 ขาด `EWallet/Other` ทั้งที่ backend รับ) · POS ขาด `DirectDebit/EWallet/Other` | — | "โอนเงิน" ตรงกันทุกหน้า ✓ · `EWallet` = "E-Wallet" (payments) — ไม่มีคำไทย |
| **PosOrderType** (6) | pos.html:171 select (เลข 0-4!) · :2408 map · pos-kds.html:81 select · :311 map · pos-reports.html:551 | ทุกหน้าขาด `WalkIn/Online` | **`Takeaway` ≠ `TakeAway`** (pos/kds) · `Queue` ไม่มีใน enum · select เป็นเลขเลื่อน 1 ขั้น (→ I-03) | "กลับบ้าน" (pos) vs "นำกลับ" (kds select) vs "กลับ" (kds map) |
| **DepreciationMethod** (4) | fixed-assets.html:121 select · :286 map · document-scan.html:3597 select | `None` ทุกจุด (→ I-04) | — | — |
| **UserRole** (7) | accept-invitation.html:76 · sensitivity.html:58 · usage.html:283 · team.html:78 select · admin/users.html:311 · admin/customers.html:385 | `SystemAdmin` (ตั้งใจ — ห้ามเชิญ) ✓ | — | Viewer = "ผู้ดู" (2 หน้า) vs "ผู้ดูข้อมูล" (sensitivity) · SystemAdmin = "แอดมิน" vs "แอดมินระบบ" |
| **SubscriptionPlan** (4) | admin-layout.js:213 · subscription.html:207,226,332 · usage.html:203 | subscription.html:332 ขาด `FreeTrial` (ตรงนั้นเป็นตารางราคา — ตั้งใจ) | — | FreeTrial = "ทดลองใช้" (admin) vs "ทดลองฟรี" (ลูกค้า) |
| **SubscriptionStatus** (6) | subscription.html:227 · usage.html:204 · admin-layout.js:203 | — | admin-layout รวม 6 สถานะของ enum อื่นในถังเดียว (`Pending/UnderReview/Approved/…`) | PastDue = "เกินกำหนดชำระ" vs "เกินกำหนด" |
| **TaxType** (10) | tax.html:161 map · :87 select · wht.html:227,252 · :32,:66 select | tax.html map ขาด `StampDuty` · select :87 ขาด 5 (`WithholdingTax1/SocialSecurity/PersonalIncomeTax91/WithholdingTax54/StampDuty`) — หน้า tax.html เป็นหน้ารวมทุกแบบ | — | — |
| **BookingStatus** (6) | cms-bookings.html:87 · pos-reports.html:541 · projects.html:422 | pos-reports/projects ขาด `Confirmed/NoShow` (projects: เป็น task status คนละเรื่อง — สแกนจับผิด) | — | Completed = "เสร็จสิ้น" vs "✅ เสร็จ" |
| **PosItemStatus** (5) | pos-kds.html:270,274 | `Served/Cancelled` (เป็น next-state map — ตั้งใจ) ✓ | — | — |
| **ReconciliationStatus** (4) | bank.html:60 select | `Suggested` — backend เซ็ตจริง 3 จุด (`grep ReconciliationStatus.Suggested Services` = 3) ⇒ กรอง "ที่ระบบเดาให้" ไม่ได้ | — | — |
| **WithholdingTaxCertStatus** (4) | wht.html:38 select | `Printed` — backend เซ็ต 4 จุด ⇒ กรองใบที่พิมพ์แล้วไม่ได้ | — | — |
| **TaxReportStatus** (3) | tax.html:55 select · :413 filter client-side | `Submitted` | **`Generated` ไม่มีใน enum** ⇒ เลือกแล้วตารางว่างเสมอ (:415 `r.status === statusF`) | — |
| **PaymentIntentStatus** (7) | payment-intents.html:120 select | `Created` (backend ใช้ 7 จุด) · `PartiallyRefunded` | — | — |
| **ExpenseClaimStatus** (6) | expense.html:28 select | `Voided` | — | — |
| **SiteOrderStatus** (8) | cms-orders.html:56 select | `Refunded/PartiallyRefunded` (มีใน map :161 แต่กรองไม่ได้) | — | — |
| **AccountType** (5) | 13 map ใน 7 ไฟล์ | — | — | Expense = "ค่าใช้จ่าย" (11 ที่) vs "ค่าใช้จ่าย / ต้นทุน" (documents.html:2134,2419) — ถูกทั้งคู่แต่ 13 สำเนา |
| **ContactType** (3: Individual/JuristicPerson/GovernmentAgency) | layout.js:1612 select (ใน modal สร้างผู้ติดต่อด่วน) · contacts.html · documents.html | สแกน flat-map ไม่พบ map อื่น — label ฝังใน `<option>` เป็นสำเนา 3 ชุด (ตรวจด้วยตาแล้วชื่อตรงกัน) | — | — |
| **ProductType** (5) | products.html:306 map + select | หน้าอื่นที่โชว์ `productType` (warehouse/inventory/pos) ไม่พบ map ⇒ โชว์อังกฤษดิบ (ยังไม่ได้ยืนยันทุกหน้า) | — | NonStock = "อื่นๆ" (ความหมายผิด — NonStock คือ "ไม่ติดตามสต๊อก") |
| PayrollRunStatus · LeaveType · ApprovalStatus · JournalEntryStatus · WhtIncomeType | payroll.html · leave.html · journals.html · wht.html | **ไม่พบ label map เลย** — หน้าเหล่านี้ใช้ `=== 'Calculated'`/`=== 'Posted'` ตัดสินปุ่ม แต่โชว์สถานะเป็นอังกฤษดิบหรือ badge เฉพาะกิจ (WhtIncomeType ดึงจาก endpoint ตามกฎ CLAUDE.md แล้ว ✓) | — | — |
- ทำไมพัง (ราก): label/option ของ enum ถูกพิมพ์มือ **≥ 60 ชุดใน 30+ ไฟล์** ไม่มีตัวกลาง — ทุกครั้งที่ enum โต (เช่น `Suggested`, `Printed`, `Created`) หน้าเว็บตามไม่ทันเงียบ ๆ และตัวสะกดผิด (`Yearly`, `Takeaway`, `Generated`) เป็น string ถูกไวยากรณ์ที่ไม่มี checker จับ
- ผลกระทบ: I-01/I-03/I-04 เป็นผลโดยตรง · ที่เหลือ = ตัวกรองที่กรองสถานะจริงไม่ได้ / ป้ายอังกฤษดิบ / คำไทยต่างกันข้ามหน้าทำให้ผู้ใช้เข้าใจว่าเป็นคนละสถานะ
- defect class (CLAUDE.md): "สำเนามือฝั่ง JS ที่ตามหลังอยู่ไม่กี่ธง อันตรายกว่าสำเนาที่ผิดชัด ๆ" · กลไกที่ไฟล์เดียวกันแนะไว้แล้ว = "เซิร์ฟเวอร์ส่งค่าที่คำนวณแล้ว หน้าเว็บแสดงอย่างเดียว" (`MENU_SECTIONS`/`complianceIssues`/`DocumentTitle`)
- ทางแก้ที่เสนอ (**resolver กลางต่อ enum**): endpoint เดียว `GET /api/meta/enums` (สร้างจาก `Enum.GetValues` + `[Display]`/dictionary ไทย-อังกฤษใน `Helpers/EnumLabels.cs` ตัวเดียว) → `Layout.enumLabel(enumName, value)` และ `Layout.enumOptions(enumName, selectEl, {include|exclude})` สร้าง `<option>` ตอน runtime (กลไกเดียวกับ `roles.html` ที่สร้างจาก `Layout.navItems`) · ขั้นแรกทำ 5 enum ที่พังจริง: `RecurringFrequency · PosOrderType · DepreciationMethod · PaymentMethod · ReconciliationStatus/WithholdingTaxCertStatus/TaxReportStatus/PaymentIntentStatus` (ตัวกรองสถานะ) · checker `enum_option_value_check.py` (option value ต้องเป็นสมาชิก) + `enum_label_map_check.py` (map ที่มีคีย์ ≥ 50% ตรง enum ต้องครบทุกสมาชิกหรือติดคอมเมนต์ `// partial:` อธิบาย)
- ความมั่นใจ: **สูง** ต่อแถวที่มี file:line · แถว "ไม่พบ label map" = ผลสแกน flat object เท่านั้น (switch/ternary ไม่พบใน `grep case '…'`) — ควรเปิดหน้า payroll/leave/journals ยืนยันด้วยตา

### I-09 [P3][S] `Layout.statusLabel` ไม่มีอยู่จริง → ตรรกะกัน "ป้ายเหตุผล lifecycle ซ้ำกับป้ายสถานะ" ใน documents.html เป็น dead branch (คืน `''` เสมอ)
- ไฟล์: `Accounting/wwwroot/pages/documents.html:8988-8989` · `js/layout.js` (`grep -n statusLabel js/layout.js` = 0)
- โค้ด: `const statusLabel = (Layout.statusLabel ? Layout.statusLabel(d.status) : '').trim(); if (statusLabel && lr…includes(…)) return '';`
- ทำไมพัง: guard `Layout.statusLabel ?` เป็นเท็จเสมอ → `statusLabel=''` → เงื่อนไขกันซ้ำไม่เคยจริง → ป้าย "ยกเลิก"/"ชำระแล้ว" อาจขึ้นสองอันติดกัน (คอมเมนต์บนบรรทัด :8986 บอกว่า server กันไว้แล้วชั้นหนึ่ง จึงเป็น P3)
- defect class: "`typeof this.x === 'function'`/guard รอบเมธอดที่ไม่มี = dead branch ที่ปิดบั๊กไว้" (D4 ใน SYSTEM_REVIEW เป็นญาติ) · ญาติ: `cms-edit.html:2615` `api.getCompanyUsers ? … : API.get(...)` — ไม่มี `getCompanyUsers` ใน api.js เช่นกัน แต่ fallback ทำงาน (ไม่ใช่บั๊ก แค่ guard ตาย)
- ทางแก้: ใช้ `Layout.docStatusLabel`/ค่า `statusLabel` ที่ server ส่ง (ทีม A-11 เสนอ resolver สถานะอยู่แล้ว) แล้วลบ guard
- ความมั่นใจ: สูง

### I-10 [P3][S] `typeof this.<เมธอดตัวเอง> === 'function'` 7 จุด — ทุกตัว**มีเมธอดจริง** จึงไม่ใช่บั๊กวันนี้ แต่เป็นกลิ่นที่ CLAUDE.md ระบุว่ากลบบั๊กในอนาคต
- ไฟล์: `pages/documents.html:3900` (`onPaperChange` — นิยาม :4299) · `pages/accounts.html` · `contacts.html` · `journals.html` · `products.html` (`typeof this.load` — ทุกหน้ามี `load()`) · `pages/bank.html` (`loadAccounts` :722 · `loadTransactions` :836 — ไฟล์นี้ใช้ mixin จึงพอเข้าใจได้)
- ทางแก้: ลบ guard เรียกตรง · checker `js_self_typeof_check.py` ฟ้อง `typeof this.x === 'function'` เมื่อ `x` นิยามในไฟล์เดียวกัน (มีจริง = ไม่ต้องถาม · ไม่มี = dead branch — ฟ้องทั้งสองทาง)
- ความมั่นใจ: สูง

## ตรวจแล้วไม่ใช่บั๊ก (บอกว่าตรวจอะไร เพื่อทีมอื่นไม่เสียเวลาซ้ำ)
- **ฟอร์ม↔payload 20 หน้า** (`hydrate_vs_payload.py`): ผลฟ้อง "SENT-NOT-HYDRATED" ที่เป็น**ฟอร์มสร้างอย่างเดียว** ไม่ใช่บั๊ก — bank.html (`cAccount/cApiKey/tAmount/tDesc…` สร้างบัญชี/รายการ) · leave.html (5 ช่อง — ยื่นลา) · time-billing.html (`e*` — บันทึกเวลา) · warehouse.html (`tFrom/tTo/tNotes` — โอนคลัง) · petty-cash.html (`fImprest/fName`) · fixed-assets.html `fCreditAcct/fPostAcq` (JE ตอนซื้อ ครั้งเดียว) · **contacts.html** ฟ้อง 11 ช่องแต่เปิดดูแล้ว hydrate ผ่านตัวแปร (`arSel.value = c.defaultArAccountId` :903-919) — ครบ · **journals.html** `fProject/fSrcDocNumber` hydrate :681,:689 ครบ · **recurring.html** `fContactId/fPaymentChannel/fExpenseCategory` hydrate ผ่าน `savedPaymentChannelVal`/`savedExpenseCategoryId` (:562-575) ครบ
- **dead reference** (`deadref_scan.py` ทุกหน้า): `signatures.html` 10 เมธอด และ `financial-mgmt.html` 17 เมธอด = นิยามใน `signatures-logic.js`/`financial-mgmt-logic.js` (โหลดผ่าน `<script src>`) ✓ · `document-scan.html` `registerAsset/rescan` อยู่ในคอมเมนต์ ✓ · `products.html` `Page.foo()` อยู่ในคอมเมนต์ ✓ · `closest/querySelector/select/replaceWith` = DOM API บน `this` ของ element ✓ · `Layout.*` ที่เรียกทุกหน้า 112 เมธอด — ไม่พบตัวที่ไม่มี ยกเว้น `statusLabel` (I-09, guarded)
- **RolePermissionController** มีแค่ `[Authorize]` แต่ service ทุกเมธอดเขียนเรียก `EnsureOwnerAccessAsync` (`RolePermissionService.cs:55,98,168,191`) ✓ · **BulkCleanupController** มี `IsOwnerAsync` + `Forbid` ครบ (:48,77,118) ✓
- **delivery-sign.html** ไม่ใช่ orphan — server สร้าง URL ที่ `DocumentController.cs:173` และ documents.html:9348 มีปุ่ม ✓ · `quotation-accept.html` เช่นกัน (:138) ✓ · `line-bind.html` ถึงจาก simple.html ✓ · `dimensions.html` อยู่ใน nav ด้วย href ที่มี `?tab=` (สแกนตัดผิด) ✓
- **PosOrderType `Takeaway` ตอนส่งขึ้น server** — ถ้าส่งเป็นสตริงชื่อ `JsonStringEnumConverter` อ่านแบบ case-insensitive จึงไม่ 400 · ปัญหาจริงคือฝั่งอ่านกลับ/กรอง (I-03)
- `employees.html:102` `fSalaryType` option `Hourly` — สแกนจับเป็น RecurringFrequency แต่จริงเป็น `SalaryType` คนละ enum ✓ · `leave.html:181` map ที่สแกนจับเป็น `SubscriptionPaymentStatus` จริงเป็น LeaveStatus ✓ · `warehouse.html:169` map ที่จับเป็น `SiteOrderStatus` จริงเป็นสถานะ StockTransfer ✓ · `projects.html:18` `OnHold` มีใน `ImportExportService.cs:2621` (status เป็น string) ✓ · `intercompany.html:44`/`financial-mgmt.html:214` ที่จับเป็น BankFlowCategory = คนละ enum ✓
- `SampleDataController.Cleanup` คืน quota/ลบเลข? — ไม่เกี่ยว: ปัญหาคือด่านและการกินเลข (I-06)

## ซ้ำกับ SYSTEM_REVIEW (ID เดิม + ยังเปิดอยู่จริงไหม)
- **A17** (pos-floorplan/pos-kds/pos-reservations/admin-ocr/review-queue เรียก `Layout.init(id)` ที่ไม่มีใน navItems ⇒ custom-role strict เด้งออก) — **ยังเปิด**: สแกนรอบนี้พบ 23 id ที่ `Layout.init` ใช้แต่ไม่อยู่ใน navItems (5 หน้าเดิม + 17 หน้า `admin/*` ซึ่งใช้ admin-layout จึงไม่กระทบ + `my-signature` ที่ `hasMenuAccess` ยกเว้นให้แล้ว :460) — ที่ยังพัง = 5 หน้าเดิมของ A17 เท่านั้น
- **A5** review-queue.html orphan — ยังเปิด (NO-REF ในสแกนรอบนี้) · **§9 design-system/deposits/dashboard** ตั้งใจ ✓
- **D4** (`typeof this.viewDetail` ✅ แก้แล้ว) — I-10 คือตัวที่เหลือของคลาสเดียวกัน (ไม่ใช่บั๊ก)
- **D-10/D-11** (ทีม D — accounts/contacts silent no-op) — I-05 คือหน้าที่ 3 ของคลาสเดียวกัน (employees) · ไม่ซ้ำเนื้อหา
- **G-07** (api.js 101 เมธอดไม่มีใครเรียก) — I-02 เป็นทิศกลับ: หน้าเรียกเมธอดที่ **api.js ไม่มี** · checker ที่เสนอใน I-02 ปิดได้ทั้งสองทิศ
- **A-09** (ทีม A — risk.html ส่ง `JournalEntry` เข้า enum DocumentType) — I-01 เป็นคลาสเดียวกันกับ enum อื่น
- **ERP_REVIEW §2 ราก "เขียน `Document.Status` นอก `IDocumentService`"** — I-06 เพิ่มจุดที่ 4 (`SampleDataController:80,115`) ที่ยังไม่อยู่ในลิสต์

## ยังไม่ได้อ่าน
- label map ที่เขียนเป็น `switch`/ternary หรือ badge inline ใน payroll.html · leave.html · journals.html · wht.html (แถวสุดท้ายของ I-08) — สแกน flat-object ไม่ครอบ ต้องเปิดดูด้วยตา
- ด่านสิทธิ์ใน **service ชั้นล่าง** ของ ~110 controller ใน I-07 (เปิดยืนยันแล้วเฉพาะ RolePermission/BulkCleanup/SampleData) — ทีม G
- หน้า `admin/*` 23 หน้า (ใช้ `admin-layout.js` — สแกน dead-ref ใช้ inventory ของ `layout.js` จึงไม่ครอบ `AdminLayout.*`)
- ฟอร์ม↔payload ของ `documents.html`/`products.html`/`settings.html` (ทีม B/D ทำ · ไฟล์ใหญ่เกินงบ)
- `pos-reports.html:551` map เลข 0-4 — ควรดูว่ารายงาน POS ฝั่ง server จัดกลุ่มตาม `OrderType` ที่ไหนบ้าง (grep รอบนี้ = 0 ใน Services แต่ SQL raw อาจมี)

## ข้อเสนอเชิง ERP — แผนที่ช่องว่างสู่ ERP (ส่วนที่ 2)
สถานะ: ✅ ใช้ได้จริง (มี UI + service + เดินจริงในเส้นหลัก) · 🔨 บางส่วน/มีแต่ไม่มี UI/ไม่ถูกเรียก · 📋 ไม่มี — ทุกแถวอ้างไฟล์ที่เปิด/grep จริงในรอบนี้หรือรอบ A–F (ระบุ ID)

| โมดูล ERP | สถานะ | หลักฐาน (ไฟล์) | รากที่ต้องซ่อมก่อนขยาย |
| --- | --- | --- | --- |
| **GL** (ผังบัญชี · JE · งวด · ปิดงวด) | ✅ / 🔨 ปิดงวด | `Services/Implementations/JournalEntryService*.cs` · `Helpers/JournalEntryBuilder.cs` · `PreCloseChecklistService.cs` (มี service — UI ยังต้องยืนยัน) · `PreventPostToClosedPeriod` มีผู้อ่าน 2 จุดหลัง 802f149 | C-04 (JE 50 จุดนอก Builder · 4 จุดไม่สมดุลแล้ว Posted) · D-04 (Level ตายตัว) · ต้องมี interceptor Dr=Cr + ด่านงวดปิดชั้นเดียว (ERP_REVIEW §6 ข้อ 1) |
| **AR** (ลูกหนี้ · aging · ใบวางบิล · ทวงหนี้ · หนี้สูญ) | ✅ / 🔨 ค่าเผื่อ | `AgingReportService.cs` · `AdvancedArApController.cs` · `CustomerStatementController.cs` · `PaymentReminder*` | F-08 (BillingNote นับเป็นลูกหนี้ทั้งที่ไม่มี JE) · ค่าเผื่อหนี้สงสัยจะสูญ TFRS บทที่ 9 (`ArAgingBucket`/`LossRatePercent` — SYSTEM_REVIEW ระบุยังไม่มี) · sub-ledger↔GL 11310 reconcile รายวัน |
| **AP** (เจ้าหนี้ · จ่ายรวม · เช็ค/PDC · WHT) | ✅ | `VendorConsolidatedPaymentController.cs` · `PostDatedCheckController.cs` · `WithholdingTaxCertService.cs` | C-07 (50 ทวิ ไม่คูณ ExchangeRate) · ด่านสิทธิ์ (I-07: `WithholdingTaxCertController`/`ChequeController` ไม่มี) |
| **Inventory / WMS** | 🔨 | `Services/Implementations/Inventory/StockLedger.cs` · `WarehouseService.cs` · `StockCountService.cs` · `InventoryCostingService.cs:135-185` (FIFO layer จริง) · **ไม่มี** bin/location (`grep BinLocation Models/Entities` = 0) · lot/serial เป็นแค่ 3 คอลัมน์บน `Product.cs:114-138` ไม่มีตาราง lot · `ReservedQuantity` เขียนที่เดียว = `StockLedger.cs:119` ตั้ง 0 (ไม่มีใครจอง) | E-02 (MovementTypes) · E-07 (ปรับสต๊อกไม่มี JE) · A-06 (Receipt ไม่ตัดสต๊อก) · POS_MULTI_BRANCH (สองความจริง `Product.CurrentStock` vs `WarehouseStock`) — ต้องเป็น subledger จริงก่อนทำ WMS |
| **Purchasing** (PR→PO→GRN→PI · vendor portal · 3-way match) | ✅ / 🔨 | `ProcurementController.cs` · `VendorPortalController.cs` · convert chain ใน `DocumentService` (E-05 ✅ แก้แล้ว) | สถานะ PO Open/PartiallyReceived/Closed ไม่มี (ERP_REVIEW §6) · "ยอดค้างรับต่อ PO" ไม่มีหน้า · Backorder/Drop-ship 📋 |
| **Sales / CRM** | 🔨 | Quotation→Invoice ✅ · Lead มีเฉพาะ CMS (`cms-leads.html` · `LeadStatus/LeadType`) · **ไม่มี `SalesOrder` entity** (`grep 'class SalesOrder' Models/Entities` = 0) · ไม่มี Opportunity/pipeline (`grep Opportunity|Deal` = 0) · PriceList ต่อลูกค้า/สาขา 📋 | Sales Order เป็นเอกสารกลางระหว่างใบเสนอราคากับใบกำกับ/ใบส่งของ (จอง ATP · backorder) — ขาดตัวนี้ทำให้ `ReservedQuantity` ไม่มีใครเขียน |
| **Manufacturing / BOM** | 🔨 | `Models/Entities/BankAccount.cs` มี `BomLine` + `ProductionOrder` (อยู่ผิดไฟล์ — ตั้งชื่อไฟล์ตามคลาสแรก) · `ProductionConsignmentController.cs` · `production.html` · **UnitConversion** มี CRUD แต่ถูกอ้างนอก service ตัวเองแค่ `ProductService/ProductController/OcrController` (ไม่ใช่บรรทัดเอกสาร) | WIP/ค่าแรง/โสหุ้ย 📋 · UoM หลายระดับในบรรทัดเอกสาร 📋 (ERP_REVIEW §6) · E-06 consignment `_db.Documents.Add` เอง |
| **Fixed Assets** | ✅ / 🔨 | `FixedAssetService.cs` · `FixedAssetAccountClassifier.cs` · depreciation job | I-04 (ที่ดินลงทะเบียนไม่ได้) · C-05/C-06 (จำหน่ายไม่มี VAT ขาย · แผน≠โพสต์ · `Math.Round` 0 จุด) · ค่าเสื่อม 2 ชุดบัญชี (TFRS vs พ.ร.ฎ.145) 📋 |
| **HR / Payroll** | ✅ / 🔨 | `PayrollService*.cs` · `LeaveController.cs` · `TimeBillingController.cs` · `SalaryAdvanceController.cs` · ปกส./ภ.ง.ด.1 export | I-05 (ฟอร์มแก้พนักงาน) · I-02 (ลบ compensation/leave-type/holiday ตาย) · ไฟล์โอนเงินเดือนเข้าธนาคาร 📋 (`grep BankTransferFile|SmartCredit Services` = 0) · ปฏิทินวันหยุดราชการ 📋 (SYSTEM_REVIEW) |
| **Projects / Job costing** | 🔨 | `ProjectController.cs` · `project-time.html` · `time-billing.html` · `DimensionId` บนเอกสาร | B-06/B-09/B-10 (Dimension ไม่สืบทอดตอน convert) · I-02 (ลบ project-time ตาย) · ต้นทุนโครงการรวมค่าแรงจาก payroll 📋 |
| **Budgeting** | ✅ / 🔨 | `BudgetController.cs` · `BudgetService.cs` (คำว่า actual 21 จุด = มี budget-vs-actual) · `budget.html` (ฟอร์ม 4 ช่อง hydrate/sent ครบ) | ไม่มีด่านสิทธิ์ (I-07) · budget ต่อ dimension/โครงการ — ต้องยืนยัน |
| **Multi-company / Consolidation / Intercompany** | 🔨 | `ConsolidationService.cs` · `consolidation.html` · `intercompany.html` · `CrossTenantWorkflowService.cs` (MAIN-01: PO เลขนอก series) · `AccountantWorkspaceController.cs` | MAIN-01 · elimination entries/FX translation ยังต้องตรวจ (ทีม H ยังไม่รัน) |
| **Multi-branch** | 🔨 | `Branch` entity · `IssuerBranchCode` ตรึงบนเอกสาร ✓ · `dimensions.html?tab=branches` · **POS ยังไม่ผูก Branch/Warehouse** (`POS_MULTI_BRANCH_ANALYSIS.md`) · `Warehouse.BranchId` ไม่มีใครอ่าน (D-14) · JE ไม่ติด BranchId (SYSTEM_REVIEW H-A15) | ยุบสต๊อกสองความจริงก่อน (เฟส 0 ของแผน 7 เฟส) |
| **Multi-currency** | 🔨 | `CurrencyController.cs` · `fx-reval.html` · `ExchangeRate` บนเอกสาร · แต่ `grep ClosingRate|ForeignCurrency|OriginalRate Models/Entities/Journal*.cs` = **0** (CLAUDE.md G บทที่ 19 ระบุ field เหล่านี้ต้องมี) | B-08 (JE มัดจำไม่คูณ rate) · C-07 (50 ทวิ) · recurring THB เท่านั้น (B-10) — revalue ทำงานบนอะไรถ้า JournalLine ไม่มี FX field? ต้องตรวจ `fx-reval` เส้นจริง |
| **Approval workflow** | 🔨 | `ApprovalService.cs` · `SignatureApprovalService.cs` · `sme-config.html` กฎอนุมัติ (A-08: เลือกชนิดได้ 5/16) | B-02 ✅ · B-03 (Rejected ทางตัน) · G-01 (ด่าน `Document.Approve` ขาดในเส้นลายเซ็น) · ขั้นอนุมัติแบบหลายระดับ/วงเงิน ต่อชนิดเอกสาร — เป็นกฎมือ ไม่ใช่ engine |
| **Audit** | ✅ | `Helpers/AuditHashChain.cs` · `AuditMiddleware.cs` · `AuditTrailController.cs` · เทสต์ round-trip (CLAUDE.md G) | ตัวกรอง `AuditAction` ใน `admin/audit-log.html:36` ขาด `Print/View/ApiAccess` · `pages/audit.html:34` ขาด `Logout/View/ApiAccess` (options.md) |
| **Reporting / BI** | 🔨 | `ReportBuilderController.cs` 10 endpoint (`/reports/{id}/execute|export|data-sources`) + wrapper ใน `api.js` แต่**ไม่มีหน้าใดเรียก** (grep wwwroot = api.js เท่านั้น) · `executive-reports.html` · `ScheduledReportController.cs` | F-01 ✅ · F-04/F-05 · "สองความจริง" รายงาน↔GL ต้องมี reconcile เป็นรายงานมาตรฐาน (ERP_REVIEW §6 ข้อ 6) · Report Builder = ต่อสายหรือลบ |
| **API / Integration** | 🔨 | `Middleware/ApiKeyMiddleware.cs` · `PublicApiControllerBase` (Scopes แก้ fcd79e2) · `IntegrationController.cs` · `WebhookService.cs` · `ECommerceController.cs` (460 บรรทัดไม่มี UI — SYSTEM_REVIEW) · `OpenBankingService.cs:115` "we simulate" | IntegrationService 7 ทาง `return null` แล้ว Success (แก้แล้ว) · ตัดสิน E-commerce/Open Banking ต่อสายหรือลบ |
| **e-Tax Invoice** | ✅ / 🔨 | `EtaxInvoiceService.cs` (XAdES/SignedXml มีจริง 1 ไฟล์) · `EtaxController.cs` · `etax.html` (เขียน URL เอง 12 จุด — G-07) | e-Tax by Email (PDF/A-3 + `EtdaTimestampToken`) ต้องยืนยัน · ไม่มีด่านสิทธิ์ (I-07) |
| **POS** | ✅ / 🔨 | `PosService.Orders.cs` · `pos.html` · `pos-kds.html` · `pos-reports.html` | **I-03 (OrderType เก็บผิดทุกแถว)** · POS ไม่ผูกสาขา/คลัง · สลิป "ใบกำกับภาษีอย่างย่อ" ไม่ตรวจ ภ.พ.06 · E-03 (AllowNegativeStock) |
| **Lodging / CMS / Storefront** | ✅ / 🔨 | `LodgingService.cs` · `Cms*Controller` · `storefront.html` · `LODGING_BOOKING_AUDIT.md` (ปลายทางขาด 6 ชิ้น — 3 ชิ้นแก้ใน 1e05ffe/b63dfd8) | `EarlyCheckInFee/LateCheckOutFee` ไม่มีใครอ่าน · `storefront.html:1819` ขาด `ManualSlip` label |
| **สิทธิ์/แยกหน้าที่ (SoD)** — แกนของทุกโมดูล | 🔨 | `roles.html` + `hasMenuAccess` (จอ) · `IPermissionService` 10 controller (server) | **I-07** — ต้องปิดก่อนเรียกว่าเป็น ERP หลายผู้ใช้ |

**รากที่ต้องซ่อมก่อนขยาย (สรุปจากตาราง — เพิ่มจาก ERP_REVIEW §6 สองข้อ)**
1. (ERP_REVIEW §6 ข้อ 1–6 ยังคงเป็นลำดับแรก: posting ชั้นเดียว · Registry ชนิด/สถานะ · master-data governance · header-inheritance · Inventory subledger · reconcile รายวัน)
2. **+7 สิทธิ์ที่ server** — permission ต่อ route ชั้นเดียว (I-07) ก่อนเพิ่มโมดูลใด ๆ ไม่งั้นทุกโมดูลใหม่เกิดมาโดยไม่มีด่าน
3. **+8 Enum → UI จากแหล่งเดียว** (I-08) — `GET /api/meta/enums` + `Layout.enumLabel/enumOptions` · ทุกโมดูลใหม่ห้ามพิมพ์ `<option>` มือ · checker option-value/label-map ปิด I-01/I-03/I-04 คลาสทั้งหมด
4. **+9 Sales Order** — เอกสารกลางที่ทำให้ Reservation/ATP · backorder · PO-from-SO · delivery scheduling มีที่อยู่ (ไม่มี SO = `ReservedQuantity` ไม่มีวันมีคนเขียน)

## สรุป 5 บรรทัด
1. **บั๊กใช้งานไม่ได้จริง 4 เรื่อง (P1)** จากคลาส "สำเนามือ enum ฝั่ง JS": รายการเกิดซ้ำรายปีสร้างไม่ได้ (`Yearly`≠`Annual` — I-01) · ปุ่มลบตาย 5 จุด/4 หน้า (`API.delete` ไม่มี — I-02) · POS เก็บประเภทออเดอร์ผิดสมาชิกทุกแถวและ KDS กรองผิดชนิด (เลข 0-4 vs enum 1-6 · `Takeaway`≠`TakeAway` — I-03) · ที่ดินลงทะเบียนสินทรัพย์ไม่ได้เพราะ select ไม่มี `None` ที่ server บังคับ (I-04)
2. **สิทธิ์ (P1 × 2)**: ใครก็ seed เอกสารตัวอย่างที่กินเลข §86/4 จริง + Approved ตรงไม่มี JE (I-06) · และโดยรวม "สิทธิ์ตามเมนู" บังคับที่จอเท่านั้น — server มี `IPermissionService` ใน 10/~140 controller, middleware ไม่ตรวจ permission (I-07 · ส่งต่อทีม G เป็นเจ้าของรายไฟล์)
3. **silent no-op หน้าที่ 3** (I-05 employees: ธนาคาร 3 ช่องไม่ hydrate · ชื่อบัญชี/ธง ปกส. ไม่ส่งตอนแก้) — คลาสเดียวกับ D-01/D-10/D-11 ⇒ ควรทำ helper ฟอร์ม↔payload ตัวเดียว + เทสต์ reflection แบบ `OcrScanSnapshot` ต่อหน้า
4. **ตาราง enum × หน้า** (I-08): ≥60 map/select ใน 30+ ไฟล์ · ตัวกรองสถานะ 6 หน้ากรองสถานะที่ backend เซ็ตจริงไม่ได้ (`Suggested/Printed/Created/Voided/Submitted`) + 1 ค่าผี (`Generated`) — ทางแก้เดียว: `GET /api/meta/enums` + `Layout.enumLabel/enumOptions` + checker option-value (ปิด I-01/03/04 ทั้งคลาส)
5. **ERP gap map** 21 โมดูล: ✅ แกน GL/AR/AP/Audit/e-Tax · 🔨 ส่วนใหญ่ (Inventory ไม่ใช่ subledger · ไม่มี Sales Order/ATP · BOM ไม่มี WIP · FX ไม่มี field บน JournalLine · Report Builder/E-commerce/Open Banking มีหลังบ้านไม่มีหน้า/simulate) · 📋 SO · WMS bin/lot · ค่าเผื่อหนี้ · ไฟล์โอนเงินเดือน — รากเพิ่มจาก §6: **สิทธิ์ที่ server · enum จากแหล่งเดียว · Sales Order**

---
_ทีม I · HEAD a054340 · สคริปต์: `scratchpad/team/I/{labelmap_scan,option_scan,formpayload_scan,hydrate_vs_payload,deadref_scan}.py` · ไม่ได้คอมไพล์/รัน — ยิงจริง 1 ครั้งต่อข้อ P1 ก่อนแก้_
