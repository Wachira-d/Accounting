# รอบ 200 — ทีม Z (แก้ผลฝ่ายค้านรอบสอง ด้านสิทธิ์/แพ็กเกจ/security/OCR)

คอมมิตงาน: `<pending>` (sha เติมในคอมมิตตามหลัง — ห้าม amend)

แหล่ง: `erp-review/2026-09-29/review200-round2-sec.md` (S2-3 · S2-6 · S2-7 · RF-2 · RF-3 · RF-6 · K2-1 · K2-2 · K2-4 · K2-5a · K2-5b) · DECISIONS ข้อ 14, 21–24, 34 ·
`team-S2.md` `team-RF.md` `team-K2.md` · เริ่มจาก HEAD `36a0aad5` (หัว `claude/erp-system-review-team-660mev` — worktree reset แล้ว)

**ยังไม่ได้คอมไพล์** (เครื่องนี้ไม่มี .NET SDK) — เทสต์ใหม่ยังไม่เคยถูกรัน · ต้องรอ CI/rebuild ฝั่งผู้ใช้

## ตารางรายการ

| ID | P | สถานะ | ที่แก้ (file:line) | เทสต์ / ด่าน |
|---|---|---|---|---|
| **K2-5b** | P2 | ✅ | `Helpers/OcrCurrencyEvidence.cs:112-150` (`ForeignCode` แยก Strong/Weak/Symbol · `WeakWordHasEvidence`) | `Review200ZTests.K2_5b_*` |
| **K2-5a** | P3 | ✅ | ผลของ K2-5b | `K2_5a_EuroCakeOnABahtReceipt_IsNotFlaggedUnsure` |
| **K2-1** | P2 | ✅ | `Helpers/OcrCounterpartyMatch.cs:57-178` (`PickBuyerByName(…, paperBranchCode)` · `SameEntityRow` · `PrefilterToken` · โน้ต 3 ชนิด) · `OcrService.cs:3538-3580` (`ResolveSalesCounterpartyAsync`) | `K2_1a/1b/1c/…` · required_call_site |
| **RF-3** | P3 | ✅ | `DECISIONS.md` ข้อ 34 (หมายเหตุแก้ถ้อยคำ) · `team-RF.md` · `DocumentTemplateController.cs:33-51` (`GET access`) · `document-templates.html` (`_loadAccess`/`_applyAccess`/`_requireEdit` · `[data-tpl-write]` · หยุดที่ 403 แรก) · `api.js:432-460` | `api_feature_denial_sim.js` ข้อ 7–8 + negative 2 · required_call_site `GetAccess` |
| **S2-6** | P3 | ✅ | `SubscriptionGatePolicy.cs:386-402` · `SubscriptionMiddleware.cs:89-96` · `:118-128` | `S2_6_*` · required_call_site (before) |
| **S2-3** | P3 | ✅ | `SubscriptionGatePolicy.cs:362-377` (`PageMainRoutes`/`PageMainFeatures`) · `SubscriptionDtos.cs` `PageFeatures` · `SubscriptionController.GetSubscription` · `layout.js:1090` | `S2_3_*` · **sim ใหม่** `tools/page_feature_sim.js` |
| **S2-7** | P3 | ✅ (doc) | `ACCOUNT_STRUCTURE.md` §5.2 · `team-S.md` checklist ข้อ 3a | — |
| **RF-6** | P3 | ✅ | `Helpers/AnomalyExplainVerdict.cs:48-72` (`View` · `Coerce` → internal) · `AiSuggestionController.cs` ทั้งสองทางเข้า | `RF6_*` · required_call_site `ExplainAnomaly` / `ExplainAnomaly#1` |
| **RF-2** | P3 | ✅ | `Helpers/CssThemeValue.cs` (ใหม่) · `CmsRenderingService.cs:274-300` + `StorefrontThemeInfo` · `CmsSiteService.cs:623-634` (`RejectUnsafeTheme`) + `SanitizeCss` · `DocumentBrandController.cs:314-325` + `Map` | `RF2_*` · required_call_site (`must_lit`/`forbid_lit`) |
| **K2-2** | P3 | ✅ | `OcrService.cs:2784-2800` (บันทึกของค้างก่อนเปิดธุรกรรม · จำ entity ทุกชนิด) · `:3614` | required_call_site (ไม่มีเทสต์ DB ในเรพ) |
| **K2-4** | P3 | ✅ | `DocumentService.cs` `SyncScanToPostedDocumentAsync` catch → `DiscardUnsavedEntry(scan)` (`:13661`) | required_call_site |

## กันถดถอย (CLAUDE.md §H) — รันกระดาษจริงก่อน/หลัง

ไม่มี SDK ⇒ ตัวจำลอง Python ของ `OcrCurrencyEvidence.Read` **ก่อน** (HEAD `36a0aad5`) และ **หลัง** บน**สตริงข้อความทุกค่าคงที่ใน `Accounting.Tests/*.cs`**
(ต่อสตริงที่ `+` กัน · ≥ 20 ตัว · มีขึ้นบรรทัด · ไม่นับไฟล์เทสต์ของทีมนี้) = **363 ชิ้น**:

| ตัวตัดสิน | ใบที่คำตอบเปลี่ยน | อธิบาย |
|---|---|---|
| สกุลเงิน (K2-5) | **0/363** (ค่า · ที่มา · ธง) · ต่างประเทศ 7 = 7 · ไม่รู้ 2 = 2 | ไม่มีกระดาษในเทสต์ที่ใช้คำเดี่ยว EURO/YEN/YUAN/RMB เป็นหลักฐานสกุลเงินโดยไม่ติดตัวเลข/ป้าย — ใบที่เปลี่ยนมีแต่ตัวอย่างใหม่ในผลตรวจ (`YEN TA FO` JPY→บาท · `EURO … บาทถ้วน` Ambiguous→บาท · `€ 1,070.00` บาท→EUR) |

ตัวตัดสินอื่นของรอบนี้ไม่อ่านข้อความกระดาษ (จับคู่ชื่อผู้ซื้อกับรายชื่อผู้ติดต่อ) — ทิศที่ถูกอยู่แล้วล็อกด้วยครึ่งหลังของเทสต์ (`K2_1_RightAnswers_AreUntouched` · RG-03 "แอม แฮปปี้")

## F3 ข้อ 7–12

7. รูปแบบเดิมทั้งเรพ: `reasoning = resp.Reasoning` ในทางเข้าอธิบายรายการผิดปกติ 1 → 0 (ตัวประกอบเดียว `View`) · `AnomalyExplainStudent.ReadStructured(` ใน controller 1 → 0 ·
   ค่าธีม CMS ต่อ CSS ดิบ 16 → 0 (theme.css) + 16 → 0 (storefront DTO) · `Blank(r.PrimaryColor/SecondaryColor)` 2 → 0 · `Entries<Contact>()` ใน Undo 1 → 0 ·
   `throw new Error(json.message …)` ภายใน try ที่กลืน (api.js 403) 1 → 0 · คำเดี่ยวสกุลเงินที่นับทุกที่ 4 → 0
8. ทางเข้าอื่น: ลูกค้าจากชื่อ — LINE/มือถือ/API v1 autoCreate/พรีวิว "แก้ในฟอร์มก่อน" ผ่าน `ResolveSalesCounterpartyAsync` ตัวเดียว ⇒ ได้ K2-1 ครบ · สกุลเงิน — ผู้เรียก 3 จุด (สแกน · สร้างเอกสาร ·
   DTO) ผ่าน `OcrCurrencyEvidence` ตัวเดียว · อธิบายรายการผิดปกติ — 2 ทางเข้าผ่าน `View` · สีแบรนด์ — เส้นเขียนเดียว (`Apply`) · ธีม CMS — สร้าง/แก้ทั้งคู่ · 📋 `SettingsService`
   (`CompanySettings.PrimaryColor/SecondaryColor`) ยังรับสีไม่ตรวจรูป (ไม่อยู่ในรายงาน · QuestPDF/HTML ใช้ผ่าน `SanitizeHex`/`DocumentTemplateStyle.Color` อยู่แล้ว)
9. เข้มขึ้น + ทางไปต่อ: (ก) ธีม CMS/สีแบรนด์ที่ไม่ถูกรูป ⇒ 400 ข้อความไทยบอกรูปที่ถูก (`RF2_LegitThemeValues_AreUntouched` ทิศตรงข้าม) (ข) ผู้ไม่มี `CompanySettings.Edit` ⇒ ปุ่มปิด + ข้อความ
   "ขอให้เจ้าของเปิดสิทธิ์ที่หน้าบทบาทและสิทธิ์" (ค) คำเดี่ยวสกุลเงินที่ไม่มีหลักฐาน ⇒ บาท — ใบต่างประเทศจริงที่ไม่มีรหัส/ตัวเลขติด (น้อยมาก) ผู้ใช้เลือกสกุลในฟอร์มเอกสาร
   (`K2_5b_RealCurrencyEvidence_StillReadsForeign` ทิศตรงข้าม 8 แบบ) · หลวมลง: `/api/v1` อ่านได้แม้ระงับ (ตามถ้อยคำข้อ 23 · `S2_6_PublicApiWrite_*` ล็อกว่าเขียนยังบล็อก)
10. ค่าที่ persist: สีแบรนด์/ธีมเก่าที่ไม่ถูกรูป ⇒ render/echo เป็นค่าตั้งต้น (ไม่ต้อง migration · ค่าในฐานคงเดิมจนกว่าจะบันทึกใหม่) · สกุลเงินของสแกนเก่าไม่คำนวณใหม่ (อ่านตอนสแกน/สร้าง) ·
    ลูกค้าซ้ำที่เกิดไปแล้ว (แถวที่สาม) แยกไม่ได้อัตโนมัติ ⇒ ไม่ migrate (หน้า contact-hygiene `DuplicateKeyGroups` เห็นแถวเลขเดียวกัน)
11. แตะสิทธิ์/แพ็กเกจ/security — ทีมนี้เป็น subagent ⇒ **ยังไม่ได้ส่ง diff ให้ฝ่ายค้าน** · main agent ควรส่ง 1 รอบ (3 คำถาม: ทางเข้าอื่น · ทิศตรงข้าม · สถานะปลายทาง)
12. DOCUMENT_FLOW §1 OCR (บล็อกทีม Z) + ตาราง AI (AnomalyExplanation) + Last verified · ACCOUNT_STRUCTURE §5.2 (S2-3/S2-6/S2-7) rev 40 · TEST_PLAN §0 (396 ไฟล์) + OCR-U-30/31 ·
    AI-Z-1 · SEC-Z-1 · SUB-Z-1 · PERM-Z-1 · CHANGELOG · `docs/lessons/ocr-pipeline.md` (4 ข้อ) · DECISIONS ข้อ 34 · `team-RF.md` · `team-S.md`

## เครื่องมือที่รันแล้ว (ไม่มี .NET SDK)

- `required_call_site_check` (รวม negative test ในตัว): แถวใหม่ของทีมนี้ 15 + ปรับ 1 (`ExplainAnomaly`) **ผ่านทั้งหมด** · ⚠️ ล้ม 1 แถว**ที่ไม่ใช่ของทีมนี้**:
  `Helpers/SettlementFeeTax.cs Compute ไม่เรียก borne = wht` (แถวของทีม WF · ไฟล์ไม่ถูกแตะ — โค้ดใช้ `(wht, certIncome, borne) = WhtOnBase(…)` · ล้มตั้งแต่ HEAD) ⇒ main agent ปรับแถวทีม WF
- `ocr_helper_test_check` (65 คลาส · ไม่มีเทสต์ 0) · `write_permission_gate_check` · `admin_menu_gate_check` · `html_attr_escape_check` · `escape_helper_check` · `css_var_check` ·
  `record_arg_check` · `using_check` · `regex_line_span_check` · `comment_line_break_check` · `string_quote_close_check` · `dead_helper_check` (หลังทำ `Coerce`/`SafeFontName`/`SafeLength`
  เป็น internal) · `identifier_space_check` · `nullable_arg_check` · `tuple_name_merge_check` · `accessibility_check` · `arg_type_check` · `namespace_shadow_check` · `verbatim_string_check` ·
  `onclick_js_string_check` · `service_interface_check` · `test_inventory --check` · `doc_commit_sha_check` = ผ่าน
- ⚠️ `undeclared_local_check` · `contact_taxid_only_match_check` · `attachment_gate_check` · `upload_route_check` ข้าม path ที่มี `.claude` ⇒ รันบนสำเนานอก `.claude`: 0 จุด · 3 = baseline 3 · ผ่าน · 0 จุด
- sim: `node tools/page_feature_sim.js` (ใหม่ · negative test ล้ม 4 ข้อ) · `node tools/api_feature_denial_sim.js` (ข้อ 7–8 ใหม่ · negative test 2 ล้ม 1 ข้อ) · `node tools/nav_lock_mode_sim.js` = ผ่าน
- `node --check` api.js · layout.js · ทุก `<script>` ใน `document-templates.html` · brace (ตัดปีกกาในคอมเมนต์ที่เพิ่ม) · U+FFFD = ผ่าน
- `bash tools/check_all.sh` — ดูหัวข้อท้ายไฟล์

## ความเสี่ยงคอมไพล์ที่เหลือ

- `readonly record struct OcrCounterpartyCandidate(…, string? TaxId = null, string? BranchCode = null)` — ผู้สร้างเดิม (เทสต์ K2 ใช้ named `IsCustomer:`) ไม่กระทบ · ใช้นอก EF expression (หลัง `ToListAsync`)
- tuple literal ใน array `(string, string[], string[], char?)` ของ `OcrCurrencyEvidence.Foreign` (`null` → `char?` target-typed)
- `ToHashSet(ReferenceEqualityComparer.Instance)` บน `IEnumerable<object>` (nullability warning เท่านั้น)
- `student?.SuggestedActions ?? suggestedActions ?? Array.Empty<string>()` (IReadOnlyList<string>? ?? string[]) · `AnomalyExplainView` record ใหม่ · `Coerce` เปลี่ยนเป็น internal (เทสต์เดิมเข้าถึงผ่าน `InternalsVisibleTo`)
- `SubscriptionResponse(…, IReadOnlyDictionary<string, string?>? PageFeatures = null)` + `with { … }` · `ToDictionary(…, StringComparer.Ordinal)`
- `[FromServices] IPermissionService` ใน action ใหม่ · `EntityState` (มี `using Microsoft.EntityFrameworkCore`) ใน `DiscardUnsavedEntry`
- EF: `.OrderBy(c => c.Name.Contains(name) ? 0 : 1)` แปลเป็น CASE WHEN ใน Npgsql

## คำถามค้าง (ทิศที่เลือก = มองเห็นและย้อนได้)

- **Q1 (K2-1 ค)** ชื่อถูกตัดที่ครอบผู้ติดต่อ**ไม่ใช่ลูกค้า**รายเดียว: เลือก "ไม่ผูก + สร้างลูกค้าใหม่ + โน้ตบอกชื่อแถวเดิม" (ผู้ขายล้วนอาจเป็นคนละรายที่ชื่อคล้าย) —
  ถ้าเจ้าของต้องการให้ผูกแถวเดิมแล้วติ๊ก `IsCustomer` อัตโนมัติ ต้องตัดสิน · ชื่อ**ตรง** (Exact) ผูกแม้ไม่ติ๊กลูกค้า (ไม่ติ๊กธงให้อัตโนมัติ — ไม่เปลี่ยนพฤติกรรมเดิม)
- **Q2 (K2-5)** สัญลักษณ์ `¥` (JPY/CNY) และ `$` (บางใบพิมพ์ THB ด้วย $ · USD/SGD) ไม่นับ — ไม่เดา · ใบต่างประเทศที่มีแต่สัญลักษณ์เหล่านี้ = บาท (เดิม)
- **Q3 (S2-6)** คำขออ่าน `/api/v1` ของบริษัทที่ subscription ถูก**ยกเลิก**ก็อ่านได้ (ถ้อยคำข้อ 23 พูดถึงการเขียน) — ถ้าต้องการให้ยกเลิก = ปิดทั้งหมดเหมือนหน้าเว็บ ต้องตัดสิน
- **Q4 (RF-3)** `GET document-templates/default/{type}` **สร้าง**เทมเพลตให้เมื่อยังไม่มี (เขียนผ่าน GET · ไม่ผ่านด่าน `CompanySettings.Edit`) — พฤติกรรมเดิม ไม่อยู่ในรายงาน · ไม่แตะ
- **Q5 (K2-4)** `RecordLineAccountFeedbackAsync` → `AiFeedbackRecorder` ใช้ context เดียวกัน · ถ้า SaveChanges ของตัวบันทึกล้ม แถว feedback ค้าง Modified แบบเดียวกัน — ไม่แตะ (นอกรายงาน · ตัวบันทึกใช้ร่วมหลายเส้น)

## ฝ่ายค้านรอบสาม (main agent ส่ง · 2026-10-01) — ผลและการแก้

| ID | P | สถานะ | ที่แก้ / เหตุผล |
|---|---|---|---|
| Z-1 | P2 | ✅ | `OcrCounterpartyMatch.LiteralTieBreak` — ชื่อตรงหลัง normalize หลายแถว (ซ้ำจากบั๊กเดิม สะกดรูปนิติบุคคลต่าง) ไม่มีเลขภาษีขัดกัน ⇒ แถวเดียวที่สะกดตรงตัวกับกระดาษ (พฤติกรรมเดิมก่อนคำค้นเสริม) · เทสต์ `K2_1_LegacyDuplicatesSpelledDifferently_…` (สองทิศ: เลขภาษีคนละเลข/สะกดแบบที่สาม ⇒ ยังกำกวม) |
| Z-2 | P2 | ✅ | `cms-edit.html` ฟอร์มธีม — สี/ความกว้าง/มุมโค้งผ่าน `Layout.esc`/`Number` (แถวเก่าก่อนด่าน RF-2 ยิงสคริปต์ใส่เจ้าของได้) · ข้อความในคอมมิตทีมว่า "ธีมเก่าถูกกรองตอน render" **ไม่ครอบหน้าแก้ธีม** — แก้แล้วที่ฝั่งหน้า |
| Z-3 | P3 | 📋 | `CmsRenderingService` `CustomCss` แถวเก่าที่มี `</style` ต่อดิบเข้า `ThemeCss` — วันนี้ไม่มีผู้วางลง `<style>` (storefront ใช้ textContent · theme.css เป็น text/css) ⇒ ยังไม่ใช่ช่องโหว่ |
| Z-4 | P3 | 📋 | `SyncScanToPostedDocumentAsync` ถอยแค่แถวสแกน ทั้งที่ `SaveChangesAsync` บันทึกทั้ง context — ต้องแยกการบันทึกแถวสแกน (`ExecuteUpdate`) |
