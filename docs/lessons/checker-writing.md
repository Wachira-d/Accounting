# บทเรียน — การเขียน checker / negative test / simulation

> **คลังบทเรียน (defect class) ของโปรเจกต์** — ย้ายมาจาก `CLAUDE.md` กฎเหล็ก #4 F เมื่อ 2026-09-18 (รอบ 170)
> ตามคำตัดสินเจ้าของ (REGRESSION_ROOT_CAUSE_2026-09-18 §7.4 RC-3: บทเรียน 142 ข้อจดแล้วยังเกิดซ้ำ 20/33 —
> "การจดไม่ใช่ด่าน"). **เนื้อหาไม่ถูกลบหรือแก้** — แค่ย้ายที่ เพื่อให้ CLAUDE.md เหลือ "หลักการ 10 ข้อ + checklist"
> ที่อ่านจบได้ทุก session
>
> กติกาการใช้: (1) ก่อนแก้เรื่องใด `grep -rl "<คำสำคัญ>" docs/lessons/` — ถ้ามีบทเรียน ให้อ่านก่อนลงมือ
> (2) เจอ defect class **ใหม่** → เพิ่ม bullet ท้ายไฟล์หมวดที่ตรง (append · ไม่ลบของเดิม) พร้อม "ที่มา" และ
> "กลไกที่กัน" (checker/test/helper) (3) แตะ CLAUDE.md เฉพาะเมื่อบทเรียนนั้นเปลี่ยน **หลักการ** ไม่ใช่เพิ่มตัวอย่าง
> (4) บทเรียนคลาสบิลด์ (CSxxxx) **หยุดจดเพิ่ม** — คำตอบคือ compiler (CI/SDK) ไม่ใช่บทเรียน

จำนวน 3 ข้อ · เรียงตามลำดับที่จดใน CLAUDE.md (เก่า→ใหม่)

- **checker ที่ฟ้องผิด = checker ที่พังแล้ว ต้องแก้ทันที ห้ามเลี่ยงโค้ด**
  `undeclared_local_check` ฟ้อง `x is { Matched: true, Status: { } s }` ว่า `s`
  ไม่เคยประกาศ — มันรู้จักแค่ `is { } f` (มี `is` ติดหน้า) ไม่รู้จัก designator ที่
  อยู่ใน **subpattern ซ้อน**. ทางที่ผิดคือเขียนโค้ดให้อ้อม checker (โค้ดแย่ลงเพื่อ
  ให้เครื่องมือพอใจ) ทางที่ถูกคือขยายฝั่ง "ประกาศแล้ว" ให้ครอบ ตามหลักที่เขียนไว้
  ในตัว checker เองว่า **over-approximate ฝั่งนี้ได้ไม่เสียหาย** เพราะตัวที่พลาด
  จะไม่ถูกกำหนดค่าที่ไหนเลยอยู่แล้ว · แก้แล้วต้องรัน negative test ซ้ำเสมอ
  (ไฟล์สังเคราะห์ที่มีทั้งรูปแบบถูก 2 แบบและบั๊กจริง 1 จุด — ต้องฟ้องเฉพาะจุดหลัง)

- checker ใหม่ทุกตัวต้องผ่าน **negative test** ก่อนเชื่อ: ใส่บั๊กที่ตั้งใจจับ
  กลับเข้าไปแล้วยืนยันว่า checker จับได้จริง (เคยมี checker ที่ regex ผิด
  จนไม่จับเคสหลักของตัวเอง)
  _(รอบล่าสุดพิสูจน์อีกครั้ง: `tab_hidelist_check` รุ่นแรกใช้ `[^)]{0,200}?`_
  _ระหว่าง `.forEach(` กับ `classList.add('hidden')` ⇒ **จับไม่ได้เลย** เพราะ_
  _ข้อความจริงมี `)` คั่นอยู่ (`getElementById(id)?.classList…`) ⇒ อาร์เรย์ว่าง ⇒_
  _ข้ามไฟล์ ⇒ "ผ่าน" ทั้งที่มีบั๊ก. ถ้าไม่รัน negative test จะได้ checker ที่_
  _รายงานเขียวตลอดกาล = ด่านที่ไม่มีอยู่จริง)_

- **ข้อตกลงระดับ serializer เปลี่ยนความหมายของโค้ดฝั่งหน้าเว็บทั้งระบบ — ต้องรู้ก่อนเขียน UI สักบรรทัด**
  `Program.cs` ลงทะเบียน `JsonStringEnumConverter` ไว้ (ตัวเดียว บรรทัดเดียว) ⇒ **ทุก enum ออกไปเป็น
  "ชื่อ"** (`"Pending"` · `"Hotel"`) ไม่ใช่ตัวเลข. แต่หน้าเว็บหลายหน้าเขียนตัวแมปคีย์ตัวเลข
  `{0:'รอยืนยัน',1:'ยืนยันแล้ว'}` แล้ว index ด้วย `x.status` หรือเทียบ `b.status === 0` —
  **ไม่ match อะไรเลยและเงียบสนิท** (JS ไม่ error เวลา index ไม่เจอ) อาการที่ผู้ใช้เห็นจึงกระจาย
  และดูไม่เกี่ยวกัน: การ์ดเว็บโชว์ชนิดเป็น "-" · แท็บออเดอร์/การจองโชว์ชื่ออังกฤษดิบ ·
  **ปุ่ม "ยืนยัน/เสร็จสิ้น/ยกเลิก" ของการจองไม่เคย render เลยแม้แต่ครั้งเดียว** · หน้าเงินสด
  (เช็ครับล่วงหน้า/เงินทดรอง) ปุ่มดำเนินการทั้งแถบหายทั้งหน้า — และถ้าเผลอ render ก็ยัง
  **ส่งเลขที่ความหมายเลื่อนไปแล้ว** (`BookingStatus 2` = กำลังให้บริการ ไม่ใช่ "เสร็จสิ้น")
  ⇒ กดปุ่มแล้วสถานะกระโดดผิดขั้น
  → กติกา: ฝั่ง JS **ห้ามมีตัวแมปคีย์ตัวเลขสำหรับ enum ของ API เลย** · ป้ายไทยทุกตัวอยู่ที่
  `Layout.ENUM_LABELS` + `Layout.enumLabel(kind, value)` ที่เดียว (สำเนามือ 17 ชุดในหน้าต่าง ๆ
  ถูกยุบเข้ามาแล้ว) · ค่าที่หน้าเว็บ *ต้อง* ใช้เป็นตัวเลขจริง ๆ ให้ normalize **ที่จุดรับ**
  (`this._x = this._normX(api.y)`) ไม่ใช่เทียบเลขกับค่าดิบกลางโค้ด
  _(→ `tools/enum_number_compare_check.py`. **บทเรียนซ้อน 3 ข้อตอนเขียน checker**: (1) รุ่นแรก_
  _ฟ้อง `ae?.status === 403` เพราะ regex ตัด HTTP context เขียนไว้แค่ `ae\.status` — **optional_
  _chaining ทำให้ตัวกรองพลาด** ทั้งที่โค้ดถูกทุกประการ (2) ตัวกรอง "รับทั้งสองรูป" เขียน `===\s*'`_
  _จึงไม่ครอบทิศลบ `x !== 5 && x !== 'Project'` (3) ตัวกรอง `this._x` ประกอบสตริงเป็น `this.__x`_
  _เพราะชื่อที่จับมาได้มี `_` นำหน้าอยู่แล้ว ⇒ **ฟ้องผิดแม้จุดเดียวก็คือ checker ที่พังแล้ว**_
  _ต้องไล่จนเหลือ 0 บนเรพสะอาด แล้ว negative test ใส่บั๊กจริงทั้ง 3 ทรงกลับเข้าไปต้องจับได้ครบ)_

- **เทสต์ที่เรียกแค่ helper เขียวแม้ถอดการแก้ออกจาก service — ล็อก "จุดเรียก" ด้วย checker** (รอบ 193 · ฝ่ายค้าน review193-M2 C6 ·
  review193-S2 P6 · review193-V-C3) — เรพไม่มีเทสต์ระดับ service (0 ไฟล์แตะ `DbContext`) ⇒ ทุกการแก้เงิน/ภาษี/สิทธิ์รอบนี้ถูก extract เป็น pure
  helper + เทสต์ (ถูกตามกฎ #4 G) แต่ฝ่ายค้าน**ถอดการเรียก helper ออกจาก service แล้วเทสต์ยังเขียวทุกตัว** (`SsoUnpaidLeaveWageTests.Current()`
  ประกอบสูตรเองแทนเรียกตัวที่ service เรียก · ถอด `MayOverwriteBranch` ใน v1 · ถอดด่านไฟล์แนบ) ⇒ เทสต์พิสูจน์ว่า "helper ถูก" ไม่ได้พิสูจน์ว่า
  "ระบบใช้ helper" (F2 ข้อ 2 "มี ≠ ถูกเรียก" ในทรงของเทสต์)
  → กลไก: `tools/required_call_site_check.py` — กติการายเมธอด 7 ชนิด (`must` · `must_re` · `must_lit` · `call_args` · `before` · `forbid` ·
  `ชื่อ#n` overload) บนโค้ดที่ตัดคอมเมนต์/สตริงแล้ว · **negative test ในตัวทุกครั้งที่รัน** (ลบการเรียก · เหลือแค่ในคอมเมนต์ · สลับลำดับ · ใส่สูตรต้องห้าม ·
  ส่งตัวแปรว่าง `PayrollRunLockEvidence.None` · ถอด `throw` หลัง `if (!ok)`) · รุ่นแรกจับได้ 1/8 กลายพันธุ์ของฝ่ายค้าน → รุ่นที่สอง 8/8 +
  ไม่ฟ้องผิดเมื่อขึ้นบรรทัดก่อน `.Decide(` · เทสต์เองก็ต้องเรียก**ตัวที่ service เรียก** (`SsoWageBase.ForPeriod` · `DocumentService.PreviewTotals`)
  ไม่ใช่สูตรของ helper ตรวจตัวเอง · คู่เฉพาะทาง: `attachment_gate_check` (ด่านไฟล์แนบ) · `owner_action_wiring_check` (ด่านเจ้าของ — ต้อง "ใช้ผล")
  _(ที่ checker ทำไม่ได้ — จดไว้ใน docstring: ส่งตัวแปรชื่อถูกแต่ค่าผิด · เงื่อนไขกรองผิดในตัวหา · การเรียกใน branch ที่ไม่เคยวิ่ง → compiler/เทสต์ DB)_

- **checker ที่ตัดสตริงผิดทำให้ "ไม่พบเมธอด" กลายเป็นเขียวตลอดกาล** (รอบ 193 · ทีม W · ฝ่ายค้านรอบสอง) — `required_call_site_check.mask()` ตีความ verbatim
  `@"""IsActive"" = true"` เป็น raw string `"""` ⇒ ตัดโค้ดครึ่งหลังของ `AccountingDbContext.cs` (บรรทัด 2528 ลงไป) ทิ้ง ⇒ กติกาใด ๆ ของไฟล์นั้นใช้ไม่ได้มาตลอด ·
  ญาติ: `undeclared_local_check` ฟ้อง `x is not decimal t` ผิด (CS0103 ทั้งที่คอมไพล์ผ่าน · ทีม O1) และฟ้อง `is not { } x` (ผูก x หลัง `if (...) return;`)
  → กติกา: self-test ของ checker ต้องมีเคส "ไฟล์จริงที่ยาวและมีสตริงพิเศษ" ไม่ใช่แค่โค้ดสังเคราะห์สั้น ๆ · checker ที่ฟ้องผิด = checker ที่พัง (F2 ข้อ 6)

---

<!-- ย้ายจาก CLAUDE.md 2026-10-03 (ลดขนาด context ต่อ turn 130KB→~55KB) — เนื้อหาคงเดิมทุกตัวอักษร -->

# รายการ checker ทั้งหมด (เดิม CLAUDE.md กฎเหล็ก #4 §F)

### F. เครื่องมือบังคับก่อน commit (env นี้ยังไม่มี .NET SDK จนกว่าเจ้าของจะเปิด host .NET ใน proxy — **CI บน `claude/**` คือ compiler ตัวแรกหลัง push**)

```
python3 tools/di_cycle_check.py        # วงกลม DI (dotnet build จับไม่ได้)
python3 tools/nullable_arg_check.py    # CS1503 nullable→non-nullable
python3 tools/using_check.py           # CS0246 ลืม using ของ type ในเรพ
python3 tools/record_arg_check.py      # CS1739 named arg ที่ record ไม่มี
python3 tools/accessibility_check.py   # CS0051/CS0050 ชนิด private ในลายเซ็น public
python3 tools/arg_type_check.py        # CS1503 ส่ง id เข้าพารามิเตอร์ที่รับ entity
python3 tools/gl_code_check.py         # เลขผังบัญชี hardcode ชนความหมายผังมาตรฐาน
python3 tools/verbatim_string_check.py # CS1010/CS1056 `"` เดี่ยวปิด verbatim string
python3 tools/dead_link_check.py      # ลิงก์ /pages/*.html ที่ไม่มีไฟล์ปลายทาง
python3 tools/localstorage_key_check.py # คีย์ localStorage ที่อ่านแต่ไม่มีใครเขียน
python3 tools/js_dup_method_check.py   # method ชื่อซ้ำใน object เดียวกัน (ตัวหลังทับเงียบ)
python3 tools/identifier_space_check.py # CS1001/CS1003 ช่องว่างในชื่อ method/ชนิด · CS1056 ตัวอักษรต้องห้าม (§) ในชื่อ
python3 tools/namespace_shadow_check.py # CS0234 `Helpers.X` ผูกไป namespace ผิดชั้น
python3 tools/service_interface_check.py # CS1061 controller เรียกเมธอดที่ลืมประกาศใน interface ของ service (impl+endpoint ครบ แต่ interface ขาด) → ลาก CS0006 ให้เทสต์ล้มตาม
python3 tools/dto_nullable_contract_check.py # DTO ประกาศ `string` (ไม่ nullable) ทั้งที่ service เติมค่าให้เมื่อว่าง → ASP.NET ใส่ [Required] โดยปริยาย แล้วตีกลับเป็นอังกฤษชื่อ property C# ก่อนถึงโค้ดเรา (ฟ้องเฉพาะตอนสองชั้น**ขัดกัน** — ชั้นที่ throw/BadRequest เองถือว่าตรงกัน ไม่ฟ้อง)
python3 tools/css_var_check.py       # var(--x) ที่ไม่เคยประกาศ → ปุ่มล่องหน/สีหาย
python3 tools/undeclared_local_check.py # CS0103 ส่งตัวแปรที่ไม่มีในเมธอดนั้นเป็นอาร์กิวเมนต์
python3 tools/admin_menu_gate_check.py # เมนู/endpoint ของแพลตฟอร์มที่ลูกค้ามองเห็น
python3 tools/upload_route_check.py  # โฟลเดอร์อัปโหลดที่เขียนได้แต่ static handler ตอบ 404
python3 tools/regex_line_span_check.py # \s เป็นตัวคั่นระหว่างตัวเลข → กลืนขึ้นบรรทัดใหม่
python3 tools/advisory_lock_key_check.py # คีย์ advisory lock ที่สุ่มต่อ process → ล็อกข้ามเครื่องไม่ได้
python3 tools/csp_external_ref_check.py # สคริปต์/สไตล์ภายนอกที่ CSP ของเราเองไม่อนุญาต → เบราว์เซอร์บล็อกเงียบ
python3 tools/tab_hidelist_check.py  # panel ของแท็บที่ไม่อยู่ในลิสต์ซ่อน → เปิดแล้วค้างทับแท็บอื่น
python3 tools/stock_writer_check.py  # เขียนสต็อกนอก IStockLedger → สองความจริงที่ไม่มีวันตรงกัน
python3 tools/payment_provider_boundary_check.py # โดเมน/คีย์ของ gateway หลุดนอก adapter · ข้อมูลบัตรบนเซิร์ฟเวอร์
python3 tools/write_permission_gate_check.py # endpoint ที่เขียนข้อมูลแต่ไม่มีด่านสิทธิ์ ([Authorize] ตอบแค่ "ล็อกอินไหม")
python3 tools/html_attr_escape_check.py # ข้อความอิสระเข้า attribute ไม่ผ่านตัวหนี → แตก attribute ยิงสคริปต์ได้
python3 tools/escape_helper_check.py # ตัวหนี HTML ที่เขียนเองหนีไม่ครบ 5 ตัว → ปลอดภัยแค่ครึ่งเดียวแต่ดูเหมือนปลอดภัยแล้ว
python3 tools/string_quote_close_check.py # CS1002 `"` ASCII ในสตริง**ธรรมดา** ปิดสตริงกลางคำ (verbatim_string_check ไม่ครอบ)
python3 tools/comment_line_break_check.py # CS1519/CS1010 คอมเมนต์ `//`/`///` ถูกตัดบรรทัดกลางข้อความ (ยกข้อความกระดาษที่มีขึ้นบรรทัด) ⇒ ครึ่งหลังเป็นโค้ด (รอบ 199)
python3 tools/onclick_js_string_check.py # esc() ใน JS string ของ onclick → เบราว์เซอร์ decode entity ก่อน JS อ่าน ⇒ ปิด string ได้อยู่ดี ทั้งหน้าตาย
python3 tools/sequence_lock_check.py # ออกเลขรันเองด้วยการเรียงแบบข้อความ → ไม่มีล็อก และ "9999" ชนะ "10000" เลขวนกลับทับของเดิม
python3 tools/deep_link_param_check.py # ลิงก์ส่ง query param ชื่อที่หน้าปลายทางไม่เคยอ่าน → กดแล้วตกที่ลิสต์เปล่า (dead_link_check ดูแค่ว่าไฟล์มีอยู่)
python3 tools/flag_field_overwrite_check.py # เขียนทับช่องข้อความที่เป็นที่สะสม**และ**มีด่านอ่านธงจากมัน → ธงของด่านหายเงียบ
python3 tools/ocr_helper_test_check.py # ตัวตัดสิน OCR (Helpers/Ocr*.cs) ที่ไม่มีเทสต์อ้างถึง → แก้แล้วใบที่เคยถูกกลับมาผิดโดยไม่มีอะไรฟ้อง
python3 tools/tuple_name_merge_check.py # ternary ที่สองสาขาเป็น tuple ชื่อไม่ตรงกัน → C# ทิ้งชื่อ แล้ว CS1061 ไปโผล่ไกลจากจุดที่ผิด
python3 tools/line_vat_source_check.py # เขียนอัตรา VAT ของบรรทัดตรง ๆ ไม่ผ่าน Layout.setLineVat → ตัวแนะนำทับค่าที่อ่านจากกระดาษ ยอดเพี้ยนเงียบ
node tools/vat_line_source_sim.js   # ล็อกพฤติกรรมลำดับที่มาของอัตรา VAT ด้วยโค้ดจริง (สองทิศ)
node tools/validation_field_label_sim.js # ข้อความ validation ต้องชี้ "ป้ายไทยที่ผู้ใช้เห็น" ไม่ใช่ชื่อ property C# · ช่องที่ไม่มีบนหน้าต้องบอกว่าไม่มี (รันโค้ดจริงจาก api.js)
python3 tools/blank_number_null_check.py # ช่องตัวเลขที่เว้นว่างถูกส่งเป็น `null` → System.Text.Json แปลงเข้า int/decimal ไม่ได้ ⇒ โยน body ทิ้งทั้งก้อน ⇒ ไม่มีอะไรถูกบันทึกและ error ชี้ไปที่ "dto" ที่ไม่มีบนหน้าจอ
node tools/blank_number_form_sim.js # ล็อก "ว่าง = ตัดคีย์ทิ้ง · data-blank=\"0\" = ศูนย์ · 0 ที่พิมพ์เองต้องไม่หาย" ด้วยโค้ดจริงจากหน้าเว็บ
# ↑ `tools/*_sim.js` ทุกตัวถูก check_all.sh กวาดรันเอง (แก้ 2026-09-21 — เดิมเขียนไว้ว่ารันแต่ **ไม่เคยรัน**)
python3 tools/enum_number_compare_check.py # UI ตัดสิน enum ด้วยตัวเลข ทั้งที่ API ส่งเป็น "ชื่อ" → เงื่อนไขเท็จเสมอ ปุ่มไม่ขึ้น ป้ายเป็น "-" · select option ตัวเลขที่ hydrate จาก enum (กติกา 3 · รอบ 193)
python3 tools/filing_deadline_single_source_check.py # ตารางกำหนดยื่นแบบภาษีที่เขียนซ้ำ → ภ.พ.36 เคยได้วันที่ 23 แทน 15 = เตือนช้ากว่ากฎหมาย 8 วัน
python3 tools/terminal_status_writer_check.py # สถานะปลายทาง (Filed/Matched/Approved/NoShow/StockDeducted) ประทับนอกเจ้าของกติกา — ratchet baseline (ราก R1)
python3 tools/ai_feedback_source_check.py # หน้าเว็บบันทึก "คำตอบที่ผู้ใช้เลือก" โดยไม่ส่ง `source` → นับเป็น Implicit ⇒ คลังเรียนรู้ทันทีตายเงียบ
python3 tools/required_call_site_check.py # ด่านเงิน/ภาษี/สต็อก/สิทธิ์ที่มีแต่ service ไม่เรียก — ล็อกจุดเรียกรายเมธอด 7 ชนิด (must · must_re · must_lit · call_args · before · forbid · `ชื่อ#n` overload) · negative test ในตัวรันทุกครั้ง (ลบ/คอมเมนต์/สลับลำดับ/ใส่สูตรต้องห้าม แล้วต้องฟ้อง) · "เทสต์เรียกแค่ helper" เขียวแม้ถอดการแก้ — ตัวนี้ล็อกว่า service เรียกจริง (รอบ 193)
python3 tools/settings_reader_check.py # ค่าตั้งที่เก็บ+echo ครบแต่ไม่มีผู้อ่าน ("มีช่อง ≠ มีผล") — ratchet กับ settings_reader_baseline.txt · `--self-test` ถอดผู้อ่านจริงแล้วต้องฟ้อง (รอบ 193)
python3 tools/approved_status_writer_check.py # เอกสารเกิดมา/ถูกตั้ง `Approved` นอกเส้นที่เรียก `IIssuedDocumentHooks.RunAsync` (e-Tax หลังออกเอกสาร) · hook ต้องอยู่หลัง `CommitAsync` บนเส้นเดียวกัน — ratchet baseline (รอบ 193)
python3 tools/company_settings_factory_check.py # `new CompanySettings` นอก `Helpers/CompanySettingsFactory` → แถวค่าตั้งเกิดด้วยค่า default ที่ขัดกับธงบริษัท (VAT สองธง · รอบ 193)
python3 tools/contact_taxid_only_match_check.py # query ผู้ติดต่อด้วยเลขภาษีอย่างเดียวไม่ดูสาขา (กติกา 1) · จับชื่อ/อีเมล/เบอร์หลัง `ContactTaxBranchKey.FindAsync` นอก `SoftScope` (กติกา 2 `#soft`) — ratchet baseline (รอบ 193)
python3 tools/attachment_gate_check.py # ทางเข้าที่แตะไฟล์แนบ/สแกนแต่ไม่เรียก `IAttachmentAccessGate` (หรือเรียกแล้วทิ้งผล/เรียกหลังแตะไฟล์) · ทุก action ของ OcrController ที่รับ scanId/fileAttachmentId/documentId · negative test ถอดด่านจากไฟล์จริงในตัว (รอบ 193)
python3 tools/owner_action_wiring_check.py # ด่านเจ้าของ/ปฏิเสธ API key (`[RejectApiKey]` · `[RequireOwner]` · `OwnerActionGuard` · `ApiAccessPolicy` · `RegistrationPolicy`) ต้องอยู่ที่จุดเรียกจริงและ "ใช้ผล" — `--self-test` ถอดทีละแถวจากไฟล์จริง (รอบ 193)
node tools/api_busy_indicator_sim.js # ตัวแสดง "กำลังทำงาน" กลาง (`ApiBusy` ใน api.js) — นานแสดง · สั้นไม่กระพริบ · ปุ่มกันกดซ้ำ (โค้ดจริง + negative test ในตัว)
node tools/employee_form_contract_sim.js # ฟอร์มพนักงาน ↔ API: hydrate↔payload สองทิศ · คีย์ ⊆ DTO · ทุกช่องในโมดัลถูก hydrate (ซอร์สจริง · baseline จากคอมมิตก่อนแก้ · รอบ 193)
python3 tools/doc_commit_sha_check.py # sha ที่ doc อ้างแต่ไม่อยู่บน branch (amend แล้ว sha ที่จดไว้ก่อน commit ตายทันที)
python3 tools/dead_helper_check.py    # public static ใน Helpers ที่ไม่มีผู้เรียกนอกไฟล์ (นอกคอมเมนต์ · เทสต์ไม่นับ) — ratchet กับ tools/dead_helper_baseline.txt: ล้มเฉพาะตัวใหม่ · "มี ≠ ถูกเรียก" มีตัววัดแล้ว
python3 tools/test_inventory.py --check # TEST_PLAN §0 ต้องตรงกับ [Fact]/[Theory] จริง (เคยค้าง "~150 เคส/19 ไฟล์" จนผิด 10 เท่า) — วางผล --row ทับ
node --check                           # ทุก <script> ใน .html ที่แก้
awk brace-balance                      # ทุก .cs ที่แก้
```
> **ทางลัด (รอบ 169): `bash tools/check_all.sh`** รันทุกบรรทัดข้างบนด้วยคำสั่งเดียว (checker ทุกตัว + `node --check`
> ทุก `<script>` ในไฟล์ที่แก้ + awk brace + U+FFFD + `test_inventory --check` + `dotnet build/test` ถ้ามี SDK) — exit code
> เดียว · และ **ก่อนแก้สัญลักษณ์ใด** ให้ `python3 tools/callers.py <Symbol>` (นิยาม · ผู้เรียกจริง · เทสต์ · คอมเมนต์ แยกกัน)
> แทนการประกอบ grep เอง — เหตุผลอยู่ใน `REGRESSION_ROOT_CAUSE_2026-09-18.md` §2.3 (ต้นเหตุอันดับ 2 ของการถดถอย 33 กรณี
> คือ "แก้เส้นเดียวจาก N โดยไม่ grep call site")
- แจ้งผู้ใช้เสมอว่า "ยังไม่ได้คอมไพล์ — รบกวน rebuild ฝั่งคุณ"

