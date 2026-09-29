# รอบ 200 ทีม S2 — แพ็กเกจ/สิทธิ์: คำตัดสิน 21–24 + แก้ผลฝ่ายค้านทีม S (S200-1..9)

> ขอบเขต: `DECISIONS.md` ข้อ 21–24 (+ ข้อ 5 ของ `2026-09-25/settlement/DECISIONS.md` · ข้อ 14) · `review200-S.md` S200-1..9 · `team-S.md` · ACCOUNT_STRUCTURE §5.2
> ฐาน: `df190aae` (branch `claude/erp-system-review-team-660mev`) · **ยังไม่ได้คอมไพล์** (ไม่มี .NET SDK) · **ค่าตั้งต้นยังเป็น Shadow — ไม่ได้พลิกเป็น Enforce**

## หลักใหญ่ที่ใช้ตัดสินทุกข้อ

"ก่อนเจ้าของกด Enforce ต้องไม่มีใครถูกบล็อกหรือถูกล็อกเมนูเพิ่มจากวันนี้ (ยกเว้นสิ่งที่เคยบล็อกอยู่แล้ว)" + ทุกการเปลี่ยนผ่าน
`SubscriptionEnforcementResolver` ตัวเดียว ⇒ ตรวจทีละทางเข้า: หน้าเว็บ (route) · partner (header) · `/api/v1` (คีย์ API) · เมนู · `api.js`

| ทางเข้า | ก่อนรอบนี้ (หลังทีม S) | หลังรอบนี้ ระหว่างโหมดเงา | หลังกดบังคับ |
|---|---|---|---|
| หน้าเว็บ (route) | เงา | เงา (ไม่เปลี่ยน) | บล็อกตามตาราง (คีย์ข้อ 22 ด้วย · ยกเว้น GET รายการบัญชีธนาคาร/คลัง ข้อ 21) |
| partner คีย์เดิม | บังคับเสมอ | บังคับเสมอ (ไม่หลวม) | บังคับ |
| partner คีย์ใหม่รอบ 200 (`/settlement` + ข้อ 22) | `/settlement` **บังคับทันที** (S200-3) | **เงา** + นับคอลัมน์ partner · คีย์เดิมของคำขอเดียวกันยังบังคับ | บังคับ |
| partner ด่านเขียน (ระงับ/หมดอายุ) | LogOnly (หลวมลงถ้า env เดิม = Enforce — S200-1) | env เดิม = Enforce ⇒ **บังคับต่อ** · ไม่งั้น LogOnly + นับคอลัมน์ partner | บังคับ |
| `/api/v1` (คีย์ API) | ไม่ตรวจสถานะเลย | **เงา** (บันทึกคอลัมน์ partner/API) | ระงับ/หมดอายุ/ยกเลิก = 403/402 (ข้อ 23) |
| เมนู settlements/settlement-channels | 🔒 ทันที (S200-2) | ป้ายเล็ก "แพ็กเกจไม่รวม" กดเข้าได้ | 🔒 |
| `api.js` 403 ฟีเจอร์ | ดีดทั้งหน้าทุก 403 | (เซิร์ฟเวอร์ไม่บล็อกเว็บในโหมดเงา) | เบื้องหลัง = เตือนครั้งเดียว · ดีดเฉพาะฟีเจอร์ของหน้าเอง (ข้อ 24) |

## ตารางรายการ

| # | เรื่อง | สถานะ | file | เทสต์ / ด่าน |
|---|---|---|---|---|
| S200-1 | config เดิม `Mode=Enforce` หลวมลงเงียบ | ✅ | `Helpers/SubscriptionEnforcementResolver.cs` (`LegacyHeaderEnforce` · `LegacyBootWarning`) · `Program.cs` (log ตอนบูต) · หน้าแอดมิน `legacyBox` | `SubscriptionEnforcementResolverTests.S200_1_*` ×3 + แก้เทสต์เดิม (LogOnly/Off ยังไม่มีผล) · SUB-G21 |
| S200-2 | เมนูล็อกก่อนกดบังคับ | ✅ | `wwwroot/js/layout.js` `_navLockState` + `lockOnEnforce` · `SubscriptionResponse.FeatureGateMode` · `SubscriptionController.GetSubscription` | `tools/nav_lock_mode_sim.js` (โค้ดจริง · negative ในตัว) · required · SUB-G28 |
| S200-3 + ข้อ 22 | คีย์ใหม่บังคับ partner ทันที + คีย์ไม่ตรง route | ✅ | `Helpers/SubscriptionGatePolicy.cs` (`RouteFeatureMap` แก้คีย์ · `NewlyGatedRouteKeys` · wildcard `*` · `RequiredFeatureFor(path, method)` คืน `SubscriptionRouteRequirement` (+คีย์เดิม) · `FeaturePlanFor`) · `Middleware/SubscriptionMiddleware.cs` · `SubscriptionGateShadowLog` + migration คอลัมน์ `PartnerHitCount`/`PartnerBlockedCount` · หน้าแอดมิน 2 คอลัมน์ + การ์ดสรุป + ป้าย "คีย์ใหม่รอบ 200" | `SubscriptionGateRound200S2Tests` (ข้อ22_* · S200_3_*) · required (InvokeAsync) · SUB-G22/G23 |
| ข้อ 21 | `GET bank/accounts` ผูกฟีเจอร์กระทบยอด | ✅ | `SubscriptionGatePolicy.FeatureExemptEndpoints` (GET `…/bank/accounts` · GET `…/warehouses`) | `ข้อ21_*` สองทิศ · SUB-G24 |
| ข้อ 23 | `/api/v1` ไม่ตรวจสถานะ | ✅ | `SubscriptionGatePolicy.WithPublicApiCompany` · `TenantCompanySource.ApiKey` · middleware | `ข้อ23_*` ×4 · SUB-G25 |
| ข้อ 24 | `api.js` ดีดทั้งหน้า | ✅ | `wwwroot/js/api.js` `featureDenialAction` + `_featureDenialNotified` · `layout.js` `currentPageFeature()` | `tools/api_feature_denial_sim.js` (โค้ดจริง · negative ในตัว) · SUB-G26/G27 |
| S200-4 | ตัวตัดสินฟีเจอร์บริษัทสองตัว | ✅ | `SubscriptionService.ResolvePlanFeaturesAsync` (overlay + `GetEffectivePlanAsync` ทั้งสองสาย · `WithPlanFeaturesAsync`) | `S200_4_*` · required 4 แถว · SUB-G29 |
| S200-5 | posting-preview รั่วยอดค้าง | ✅ | `SettlementPermissionScope.HideReceivableDetails` · `SettlementController.PostingPreview` | `S200_5_*` · required · SUB-G30 |
| S200-6 | fallback กว้างเกิน | ✅ | `SubscriptionTrialReadiness.ResolveFeatures(TrialFeatureInputs, now)` · `MayFillFromTemplate` | `S200_6_*` ×5 |
| S200-7 | ลำดับ migration | ✅ | `DatabaseMigrationHelper.cs` (CREATE V2 → DROP เดิม) | อ่านโค้ด · checklist A2 |
| S200-8 | query สวิตช์ทุกคำขอ | ✅ | `Helpers/SubscriptionAdminSwitchCache.cs` (singleton · TTL 5 วิ) · `ReadAdminSwitchFreshAsync` · `InvalidateAdminSwitchCache` | `S200_8_*` · required |
| S200-9 | MatchNote ถูกทับทุกบรรทัด | ✅ | `SettlementPermissionScope.HideCandidates` (เฉพาะบรรทัดที่มีผู้สมัคร) | `S200_9_*` |

## ข้อ 22 — ตรวจ route จริงทุกคีย์ (สคริปต์อ่าน `[Route]`+`[HttpX]` ของ controller ทั้ง 1,753 endpoint แล้วเทียบตัวจับคู่เดิม/ใหม่)

| คีย์เดิม | route จริง | คีย์ใหม่ | endpoint ที่เปลี่ยนฟีเจอร์ |
|---|---|---|---|
| `/fixed-assets` | `FixedAssetController` `[controller]` | `/fixedasset` | 19 (None → FixedAssets) |
| `/multi-currency` | `CurrencyController` | `/currency` | 10 |
| `/warehouse` | `WarehouseController` | `/warehouses` (GET รายการยกเว้น) | 13 · `warehouses/products/{id}/stock` Inventory → WarehouseManagement (partner ยังบังคับ Inventory ระหว่างเงา) |
| `/commission` | `CommissionController` | `/commissions` | 11 |
| (`/approval` ตรง `ApprovalController` อยู่แล้ว) | `SignatureApprovalController` | + `/approvals` | 5 |
| `/ai/` (ต้องเจอ `/ai//`) | `AiController` + `AiSuggestionController` | `/ai` | 48 · `/ai/bank/*` ยังเป็น `/bank` · `/ai/ocr/*` ยังเป็น `/ocr` (ยาวกว่าชนะ) · `/api/admin/ai/*` อยู่ใต้ `/api/admin` (middleware ข้าม) |
| `/commerce` · `/booking` | ใต้ `/cms/sites/{siteId}/…` | `/cms/sites/*/commerce` · `/cms/sites/*/booking` | commerce/booking (CmsWebsiteBuilder → CmsEcommerce/CmsBooking · partner ยังบังคับ CmsWebsiteBuilder ระหว่างเงา) |

คีย์เดิมที่ถอด (ไม่เคยตรง endpoint ใด — ยืนยันด้วยสคริปต์ว่าถอดแล้วไม่มี endpoint เปลี่ยน): `/fixed-assets` `/multi-currency` `/warehouse` `/commission` `/ai/` `/commerce` `/booking`

## ข้อ 21 — ผู้เรียก `GET …/bank/accounts` ใน wwwroot (ทั้งหมด = GET รายการ)

`documents.html` ×2 · `payments.html` · `cheques.html` · `petty-cash.html` · `payment-settlements.html` · `tax-remittance.html` · `recurring.html` ·
`payroll.html` ×2 · `lodging.html` ×3 · `pos.html` · `getting-started.html` · `bank.html` — สร้าง/แก้บัญชี (`POST/PUT bank/accounts`) และทุกเส้นกระทบยอด
(`transactions` · `reconcile` · `import-statement` · `auto-match` · `reconciliation-*`) มีผู้เรียกเฉพาะ `bank.html` ⇒ ยังผูก BankReconciliation ·
**ขยายหลักเดียวกันไป `GET …/warehouses`** (รายการคลัง — `pos.html` ตั้งเครื่อง POS + `sme-config.html` · คีย์นี้เพิ่งเริ่มมีผลตามข้อ 22) — ทางแยกนอก DECISIONS
เลือกทิศ "ไม่ตัดงานหลัก" ตามเหตุผลข้อ 21 · ย้อนได้ด้วยการถอดแถวเดียวใน `FeatureExemptEndpoints`

## ทางแยกที่เลือกเอง (ทิศมองเห็น/ย้อนได้ — เจ้าของทบทวนได้)

1. **S200-2 เมนูที่ล็อกมาก่อนรอบ 200 คงล็อก** (เช่น เงินเดือนของแพ็กเกจฟรี) แม้โหมดเงา — ตามวงเล็บ "ยกเว้นสิ่งที่เคยบล็อกอยู่แล้ว" · ถ้าต้องการ "โหมดเงา = ไม่ล็อกเมนูเลย"
   ให้ใส่ `lockOnEnforce: true` ให้เมนูเหล่านั้น (ผลคือลูกค้าแพ็กเกจเล็กเข้าใช้ฟีเจอร์ที่ไม่ได้ซื้อได้จนกว่ากดบังคับ — ทางเลือกเชิงรายได้)
2. **`/api/v1` ตรวจเฉพาะสถานะ** ไม่ใช้ตารางฟีเจอร์ของเว็บ (Connected มีด่าน scope/feature ใน `PublicApiControllerBase` อยู่แล้ว) · คำขอ `/api/v1` ที่ส่ง
   `X-Company-Id` ด้วย = พฤติกรรม partner เดิม (ไม่แตะ)
3. **S200-6 "ทดลองที่เลยวันหมดอายุ" = ไม่ใช่ระหว่างทดลอง** แม้งานเปลี่ยนสถานะยังไม่รัน · `TrialConfig.TrialFeatures = None` = ตั้งใจ (เคารพ) — แยกไม่ได้จาก
   "สร้างจากแพ็กเกจที่ว่าง" แต่กรณีหลังแพ็กเกจก็ว่าง ผลเท่ากัน (precheck แสดงอยู่แล้ว)
4. **S200-8 แคชต่อเครื่อง 5 วินาที** (ไม่ใช่ state ข้ามเครื่อง — ไม่มีการนับ ไม่มีค่าที่ต้องตรงกัน) · กดบังคับแล้วเครื่องอื่นตามทันใน ≤5 วิ

## สิ่งที่พบระหว่างทาง (ไม่ได้แก้ — เหตุผลท้ายข้อ)

- **F-S2-1 `EntitlementService` doc บอกว่า `CheckFeatureAccessAsync` "ครอบ OwnerDisabledFeatures" — ไม่จริง** (`SubscriptionService.CheckFeatureAccessAsync` อ่าน
  `eff.EnabledFeatures` ตรง ๆ · mask ของเจ้าของอยู่แค่ overlay หน้าเว็บ/gate) ⇒ เจ้าของปิดฟีเจอร์แล้วด่าน service ยังให้ผ่าน · ไม่แก้เพราะแก้ = เข้มขึ้นทันที (ขัดหลักใหญ่) ·
  บันทึกใน ACCOUNT_STRUCTURE §5.2 · ควรตัดสินรอบถัดไป
- **F-S2-2 `/ai` = ตัวแนะนำเบื้องหลังหลายสิบตัว** (หน้าเอกสาร/สมุดรายวัน/เงินสดย่อย/ผู้ติดต่อ/สินค้า) — กดบังคับแล้วลูกค้าที่ไม่มี AI_Features เสียตัวแนะนำ (ไม่ถูกดีด) ·
  เป็นผลตรงตามข้อ 22 · ใส่เป็น checklist ข้อ 3 ใน `team-S.md` ให้เจ้าของตัดสินก่อนกด
- **F-S2-3 TEST_PLAN §0 ค้างเลขเก่า** (370 ไฟล์ ขณะที่จริง 387 ก่อนรอบนี้ — ทีมอื่นเพิ่มเทสต์ไม่ได้อัปเดต) · แก้เป็นเลขจริงแล้ว (388 รวมไฟล์ใหม่)
- **F-S2-4 `tools/undeclared_local_check.py` ข้ามไฟล์ทั้งหมดเมื่อรันใน git worktree** (`.claude` อยู่ใน path ของ worktree ⇒ "ตรวจ 0 ไฟล์" แต่ exit 0) — ไม่ได้แก้ checker
  (นอกขอบเขต) · รันสำเนาที่กรองด้วย path สัมพัทธ์แทน: 1,554 ไฟล์ 0 ปัญหา

## Checker ที่รัน (เครื่องโหลดหนัก — รันเฉพาะที่เกี่ยว)

ผ่านทั้งหมด (exit 0): `required_call_site_check` (กติกาใหม่/แก้ 10 แถว + negative ในตัว — ผลดูท้ายไฟล์) · `admin_menu_gate_check` · `write_permission_gate_check` ·
`dead_link_check` · `localstorage_key_check` · `js_dup_method_check` · `record_arg_check` · `using_check` · `nullable_arg_check` · `accessibility_check` ·
`undeclared_local_check` (สำเนากรอง path สัมพัทธ์ — F-S2-4) · `tuple_name_merge_check` · `service_interface_check` · `di_cycle_check` · `dead_helper_check`
(หลังทำ `IsPublicApiPath` เป็น private) · `html_attr_escape_check` · `onclick_js_string_check` · `string_quote_close_check` · `verbatim_string_check` ·
`comment_line_break_check` · `identifier_space_check` · `namespace_shadow_check` · `owner_action_wiring_check` · `settings_reader_check` · `escape_helper_check` ·
`css_var_check` · `test_inventory --check` · `node --check` api.js/layout.js · sim: `api_feature_denial_sim` (ใหม่ · negative ล้ม 8) · `nav_lock_mode_sim`
(ใหม่ · negative ล้ม 3) · `api_busy_indicator_sim` · `validation_field_label_sim` · `settlement_import_form_sim` ·
**ไม่ได้รัน** `check_all.sh` ทั้งชุด (เครื่องโหลดหนัก — หลาย agent รัน checker พร้อมกัน) · **ไม่ได้ build/test** (ไม่มี SDK)

## ความเสี่ยงคอมไพล์ (ไม่มี .NET SDK — CI บน `claude/**` เป็นตัวแรก)

1. `IReadOnlySet<string> NewlyGatedRouteKeys = new HashSet<string>(StringComparer.Ordinal) { … }` · static init ลำดับ `RouteFeatureMap` → `NewlyGatedRouteKeys` →
   `RouteMatchers` (ตามลำดับในไฟล์) · `Regex.Escape(...).Replace(@"\*", "[^/]+")`
2. `(string Path, FeatureFlags Feature)? current = null, legacy = null;` + `current ??= (m.Path, m.Feature)` · ternary `FeatureFlags?`/`FeatureFlags`
3. `SubscriptionRouteRequirement` เป็น `readonly record struct` ⇒ `RequiredFeatureFor(...)!.Value.Feature` ในเทสต์เดิมยังคอมไพล์ · `required?.Feature` ที่ใช้เดิมถูกแทนด้วย `featurePlan`
4. `eff with { EnabledFeatures = await ResolvePlanFeaturesAsync(...) }` (await ใน with-initializer · async method) · named-then-positional args
   (`subscriptionIdForTrialConfig: null, acct.PlanTemplate` · `enforced: false, partner`)
5. `Volatile.Read(ref _entry)` กับ `Entry?` (record class) · `_cache.TryGet(...) is SubscriptionAdminSwitchRead cached` (nullable struct pattern)
6. `SubscriptionController` ctor เพิ่ม `ISubscriptionGateShadowLog` + `IConfiguration` (DI มีครบ · di_cycle_check ผ่าน) · `SubscriptionGateShadowLog` ctor เพิ่ม
   `SubscriptionAdminSwitchCache` (register singleton ใน `Program.cs`)
7. เทสต์: `Assert.Equal(read, cache.TryGet(...))` (struct vs nullable struct — อนุมาน `T = S?`) · `Assert.Contains(k, HashSet<string>)` (xunit 2.9 มี overload set)

## commit

- โค้ด + เอกสาร: `<pending>` (เติมในคอมมิตตามหลัง — ไม่ amend)
