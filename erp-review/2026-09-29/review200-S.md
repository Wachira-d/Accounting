# ฝ่ายค้าน รอบ 200 — ทีม S (บังคับแพ็กเกจ/สิทธิ์)

> diff `5c1fe028..team-S-r200` (`2bff9ece` + `8b239819`) · อ่านอย่างเดียว (ไฟล์จาก `git show team-S-r200:<path>`) · ไม่มี .NET SDK — ข้อคอมไพล์ตรวจด้วยการอ่าน + checker ของเรพบนสำเนา `git archive`
> บริบท: `team-S.md` · `DECISIONS.md` ข้อ 14 · `settlement/DECISIONS.md` ข้อ 5 · `ACCOUNT_STRUCTURE.md` §5.2
> ป้าย: **CONFIRMED** = เปิดไฟล์ยืนยันแล้ว มีฉากที่เกิดจริง · **PLAUSIBLE** = ขึ้นกับข้อมูล/ค่าตั้งที่มองไม่เห็นจากเรพ · **NOT-A-BUG** = ตรวจแล้วไม่เป็น

## สรุป

| # | ป้าย | P | เรื่อง |
|---|---|---|---|
| S200-1 | PLAUSIBLE | **P1** | config เดิม `Subscription:Enforcement:Mode=Enforce` (ถ้า production ตั้งไว้ตามแผน WP-A1) ⇒ หลัง deploy ด่านเขียนของคำขอ partner (บริษัทถูกระงับ/หมดอายุ) **หลวมลงเป็น LogOnly เงียบ ๆ** |
| S200-2 | CONFIRMED | **P2** | D-P3 ใน `layout.js` = บังคับทันทีหลัง deploy (เมนู settlement ล็อก 🔒 ชี้ไปหน้าอัปเกรด สำหรับ Free/Starter) ก่อนเจ้าของกด Enforce · ไม่ผ่านรายงานเงา |
| S200-3 | CONFIRMED (โค้ด) / PLAUSIBLE (มีผู้ใช้จริงไหม) | **P2** | คีย์ `("/settlement", BankReconciliation)` บังคับคำขอที่ส่ง `X-Company-Id` **ทันที** (header = Enforce เสมอ) และไม่ถูกบันทึกในรายงาน — ขัดกับเหตุผลที่ทีมเองใช้ไม่แก้ F-1 |
| S200-4 | CONFIRMED | **P2** | FreeTrial fallback ไม่ใช่ "ตัวเดียว": `GetEffectivePlanAsync`/`CheckFeatureAccessAsync` (ด่านสร้างเอกสาร · EntitlementService) ยังอ่านสำเนาว่าง ⇒ หน้าเว็บ/gate บอก "มีฟีเจอร์" แต่สร้างเอกสารไม่ได้ |
| S200-5 | CONFIRMED | P3 | D-P5 ยังรั่วยอดค้างผ่าน `GET posting-preview` (สิทธิ์ View) — ข้อความ issue "ใบ X ค้างชำระ N" |
| S200-6 | CONFIRMED | P3 | FreeTrial fallback ครอบกว้างกว่าคำตัดสิน "ระหว่างทดลอง" (แพ็กเกจ FreeTrial ทุกสถานะ · และทับค่า "ว่าง" ที่แอดมินตั้งรายบริษัทโดยตั้งใจ) |
| S200-7 | CONFIRMED | P3 | migration: `DROP INDEX` เดิมก่อน `CREATE UNIQUE INDEX …KeyV2` + rolling deploy ⇒ ช่วงหนึ่ง upsert ล้ม (fail-open + log warning ทุกคำขอ) |
| S200-8 | CONFIRMED | P3 | อ่านสวิตช์แอดมิน (query `SiteSettings`) ทุกคำขอ partner ด้วย (เดิมอ่านเฉพาะคำขอเว็บ) · config Mode=Off เดิม ⇒ ตอนนี้โหลด write facts + log ทุกคำขอเขียนของ partner |
| S200-9 | CONFIRMED | P3 | D-P5 เขียนทับ `MatchNote` ทุกบรรทัดที่มีข้อความ (รวมเหตุผลที่ไม่เกี่ยวกับผู้สมัคร) ด้วยข้อความเดียวกัน — สถานะยังอยู่แต่ "ทำไม" หาย |
| S200-10 | NOT-A-BUG | — | ค่าตั้งต้นยังเป็น Shadow · CREATE UNIQUE INDEX ใหม่ล้มเพราะแถวซ้ำไม่ได้ · migration ไม่ทำให้แอปสตาร์ตไม่ขึ้น · upsert หลายเครื่อง · PruneAsync · endpoint null · คอมไพล์ทั้ง 6 ข้อ · ผู้เรียก `ParseWriteMode`/`GetWebModeAsync` = 0 |

---

## รายละเอียด

### S200-1 · PLAUSIBLE · P1 — config เดิม `Mode=Enforce` ถูกทิ้งเงียบ ⇒ partner ของบริษัทถูกระงับ/หมดอายุเขียนได้อีก

**ก่อน** (`5c1fe028:Accounting/Middleware/SubscriptionMiddleware.cs` — `var writeMode = SubscriptionGatePolicy.ParseWriteMode(_config["Subscription:Enforcement:Mode"])`):
ใช้กับ**ทุกคำขอ** · คำขอที่ส่ง `X-Company-Id` = action Enforce เสมอ ⇒ ถ้า config = `Enforce` → POST ของบริษัท `Company.Status=Suspended` ได้ 403
`COMPANY_SUSPENDED` · หมดอายุเกินผ่อนผันได้ 402.

**หลัง** (`team-S-r200:Accounting/Helpers/SubscriptionEnforcementResolver.cs:128` `HeaderWriteGateMode = effective == Enforce ? Enforce : LogOnly` ·
`SubscriptionGatePolicy.cs:211` `_ => target.HeaderCarried ? enforcement.HeaderWriteGateMode : …`):
คีย์เดิมไม่ถูกอ่านเพื่อตัดสินเลย ⇒ สวิตช์แอดมิน = Shadow (ค่าตั้งต้น) ⇒ partner ได้ **LogOnly** ⇒ POST ของบริษัทที่ถูกระงับ**ผ่าน**.

- `appsettings.json` ในเรพ = `LogOnly` ⇒ ถ้า production ใช้ค่าเรพ พฤติกรรมไม่เปลี่ยน (ทีมพูดถูกในกรณีนี้ · ยืนยันด้วยเทสต์
  `ค่าตั้งต้น_โหมดเงา_partnerที่ส่งheader_…LogOnly…`)
- แต่ `ADMIN_REDESIGN_PLAN.md:42` เขียนแผนไว้ชัดว่า "เปลี่ยน `Subscription:Enforcement:Mode` เป็น `Enforce` หลัง monitor" และ comment เดิมใน appsettings ก็บอกเช่นนั้น ⇒
  มีโอกาสจริงที่ env `Subscription__Enforcement__Mode=Enforce` ถูกตั้งไว้ใน production (มองจากเรพไม่เห็น)
- ทีมรู้ฉากนี้ (ข้อความเตือนใน `Resolve`: "เดิมค่านี้บล็อกการเขียน … จาก partner อยู่แล้ว — ตอนนี้จะบล็อกเมื่อโหมดที่มีผลจริง = บังคับ เท่านั้น") แต่เตือน**เฉพาะบนหน้าแอดมิน**
  ไม่มี log ตอนบูต/ต่อคำขอ ⇒ ทิศอันตราย (หลวมลง) ที่เงียบจนกว่าจะมีคนเปิดหน้านั้น · ขัด DOCTRINE §1 "ทิศปลอดภัย = มองเห็นและแก้ทัน"
- **ข้อเสนอ**: (ก) ถ้าคีย์เดิม = `Enforce` ให้ `HeaderWriteGateMode` คง `Enforce` จนกว่าจะลบคีย์ (ไม่หลวมกว่าเดิม) หรืออย่างน้อย (ข) `LogWarning` ตอนบูต/ครั้งแรกที่ resolve +
  checklist ข้อ 2 ของทีมย้ายขึ้นเป็น "ก่อน deploy" ไม่ใช่ "หลัง deploy" · เทสต์ทิศตรงข้าม: `legacy="Enforce"` + admin=Shadow ⇒ partner ยังบล็อก

### S200-2 · CONFIRMED · P2 — D-P3 ฝั่งเมนูมีผลทันที ไม่รอ Enforce

`team-S-r200:Accounting/wwwroot/js/layout.js:1259,1261` เพิ่ม `feature: 'BankReconciliation'` ให้ `settlements` / `settlement-channels` ·
`_renderNavItem` (`layout.js:1112`) ⇒ `locked = item.feature && this.subscription && !this.hasFeature(item.feature)` ⇒ เมนูกลายเป็นลิงก์ 🔒 ไป `/pages/subscription.html`
**ไม่ขึ้นกับสวิตช์บังคับเลย** (ข้อมูลฟีเจอร์มาจาก `GET /api/subscription` ตลอด).

ฉาก: ลูกค้า Free Edition/Starter (`FeatureFlags.TrialFeatures`/`BasicFeatures` ไม่มี `BankReconciliation` — `Models/Enums/AllEnums.cs:964-967`) ที่ใช้รอบโอน Shopee/Lazada อยู่แล้ว
หลัง deploy เห็นเมนูล็อก + ข้อความ "Upgrade required" ทั้งที่เซิร์ฟเวอร์ยังเป็น Shadow · หน้าเองยังเปิดได้ถ้ารู้ URL (`settlements.html:146` เรียก `Layout.init()` ไม่ส่ง pageName ⇒
`_enforcePageAccess` ไม่ดีดออก) ⇒ เป็น "ล็อกครึ่งเดียว" ที่ขัดคำตัดสิน "ยังไม่พลิกเป็น Enforce — เจ้าของกดเองหลังดูรายงาน".
และทำให้รายงานเงา **นับต่ำกว่าจริง** — ผู้ใช้ที่หยุดเข้าเมนูไม่สร้างแถว `FeatureNotInPlan` ที่ `/settlement` (ตอบคำถามข้อ 5: แสดงในรายงานเงา**ได้**เฉพาะคนที่ยังเข้าหน้าได้ ·
precheck แสดง `/settlement` ใต้ BankReconciliation แต่ไม่บอกว่าบริษัทไหน "ใช้ settlement จริง")

ข้อเสนอ: เลื่อน `feature:` ในเมนูไปคอมมิตเดียวกับการกด Enforce หรือให้ `hasFeature` ของเมนูเคารพ "โหมดที่มีผลจริง" (เซิร์ฟเวอร์ส่งมากับ `/api/subscription`) ·
หรืออย่างน้อยบันทึกในรายงานทีมว่าเมนูล็อกทันที (ตอนนี้ `team-S.md` เขียนว่า D-P3 ✅ โดยไม่บอกว่ามีผลทันที)

### S200-3 · CONFIRMED (โค้ด) · PLAUSIBLE (มีผู้ใช้จริง) · P2 — คีย์ `/settlement` บังคับ partner ทันทีและมองไม่เห็นในรายงาน

`SubscriptionGatePolicy.cs:129` `("/settlement", FeatureFlags.BankReconciliation)` + `ActionFor` (`HeaderCarried ⇒ Enforce` เสมอ) ⇒ คำขอใด ๆ ที่ส่ง `X-Company-Id`
มาที่ `/api/companies/{id}/settlement/...` ของบริษัทที่ไม่มี BankReconciliation ได้ **403 FEATURE_NOT_AVAILABLE ทันทีหลัง deploy** และ `SubscriptionMiddleware.cs:148`
`if (verdict.Blocks && !target.HeaderCarried)` ⇒ ไม่ถูกนับใน "ถูกบล็อกจริง".
นี่คือเหตุผลเดียวกับที่ `team-S.md` F-1 ใช้ปฏิเสธการแก้คีย์ ("แก้คีย์ = บล็อก partner ทันที (ไม่ผ่านโหมดเงา)") แต่ D-P3 เพิ่มคีย์ใหม่ที่มีผลแบบเดียวกัน.
ไม่พบ client ตัวแรกที่ส่ง header (`git grep X-Company-Id` ใน `*.js/*.html` = 0 นอกหน้าแอดมิน) ⇒ ผลจริงขึ้นกับว่ามี integration ภายนอกเรียก settlement ด้วย header ไหม — จึง PLAUSIBLE.
ข้อเสนอ: ระบุใน team-S.md/ACCOUNT_STRUCTURE §5.2 ว่าคีย์นี้มีผลกับ partner ทันที (ให้เจ้าของเลือกทิศ) หรือนับคำขอ header ที่ถูกบล็อกด้วยในรายงาน (แยกคอลัมน์)

### S200-4 · CONFIRMED · P2 — FreeTrial fallback มีสองตัวตัดสิน (overlay ✓ · EffectivePlan ✗)

- `SubscriptionService.ResolveGateOverlayAsync` (`SubscriptionService.cs:523-531`) เติมฟีเจอร์จากแพ็กเกจเมื่อสำเนาว่าง ⇒ ใช้โดย `GetSubscriptionAsync` (หน้าเว็บ `Layout.hasFeature`) +
  `GetGateStateAsync` (middleware + precheck)
- แต่ `GetEffectivePlanAsync` → `BuildFromCompanySub` (`SubscriptionService.cs:825` `EnabledFeatures: sub.EnabledFeatures`) และสาย account (`:799` `EnabledFeatures: acct.EnabledFeatures`) **ไม่เติม** ⇒
  `CheckFeatureAccessAsync` (`SubscriptionService.cs:719-724`) ยังคืน false ⇒ `DocumentService.cs:920` `CheckFeatureAccessAsync(DocumentEngine)` → `"ไม่มีสิทธิ์ใช้ระบบเอกสาร"` ·
  `EntitlementService.cs:46` · `SubscriptionController.cs:351`
- ฉาก: subscription ทดลองที่สำเนาว่าง (กรณีที่ทีมตั้งใจแก้) — หลัง deploy หน้าเว็บแสดงเมนูเอกสารเปิด · precheck บอก "ไม่ขาดฟีเจอร์" · แต่กดสร้างเอกสารได้ error เดิม ⇒
  "หน้าจอบอกว่าได้ แต่ทำไม่ได้" (R2/ตัวตั้งสองตัว) · `team-S.md` S-3 เขียนว่า "ตัวเดียวของหน้าเว็บ `hasFeature` + gate" — ไม่ครอบด่านฝั่ง service
- ข้อเสนอ: ย้าย `ResolveFeatures` เข้า `GetEffectivePlanAsync` ด้วย (ทั้ง Company/Account) หรือให้ `CheckFeatureAccessAsync` อ่านจาก overlay · เทสต์: สำเนาว่าง + Trial ⇒ `CheckFeatureAccessAsync(DocumentEngine)=true`

### S200-5 · CONFIRMED · P3 — D-P5 ยังรั่วยอดค้างผ่าน posting-preview

`SettlementController.cs:357-360` `GET batches/{id}/posting-preview` = `[RequirePermission(View)]` · หน้าเว็บแสดงปุ่มให้ผู้มีสิทธิ์ดู (`SettlementBatchActions.cs:127` `CanPreview: status != Voided`)
· issue ใน `Helpers/SettlementPosting.cs:381` = `$"ใบ {t.DocumentNumber} ค้างชำระ {t.BalanceDue:N2} น้อยกว่ายอดที่แพลตฟอร์มโอน …"` (ข้อมูลจาก `SettlementPostingService.cs:505-510` `d.BalanceDue`)
⇒ ผู้มีแค่ `Settlement.View` ยังเห็นเลขที่ + ยอดค้างของใบที่จับคู่ไว้ (แคบกว่ารายการผู้สมัครเดิม แต่เป็นข้อมูลชนิดเดียวกับที่ D-P5 ตั้งใจซ่อน) ·
`Plan.Receipts` ยังมี DocumentId + ยอด (ยอดโอน ไม่ใช่ยอดค้าง — รับได้). ทางอื่นที่ตรวจแล้วไม่รั่ว: `rematch`/`match`/`reclassify`/`bank-account`/`void` = Import ·
`deposit-candidates`/`deposit-match` = Post · `GET batches` (list) ไม่มีผู้สมัคร · `reference` ไม่มีเอกสารขาย.
ข้อเสนอ: ใช้ `CandidatesHiddenReason` เดียวกันกรองข้อความ issue ที่อ้าง `BalanceDue` ใน `PostingPreview` เมื่อไม่มี Import/Post

### S200-6 · CONFIRMED · P3 — fallback กว้างกว่าคำตัดสิน

`SubscriptionTrialReadiness.IsTrialLike(plan, status)` = `status == Trial || plan == FreeTrial` ⇒ แพ็กเกจ FreeTrial สถานะ `Expired`/`PastDue` ก็ได้ `templateEnabled`
(คำตัดสินข้อ 14 = "FreeTrial **ระหว่างทดลอง**") · seeder Free Edition `EnabledFeatures = TrialFeatures` จึงไม่เกินวันนี้ แต่ถ้าแอดมินตั้ง EnabledFeatures ของแพ็กเกจ FreeTrial
ใหญ่กว่า TrialFeatures ลูกค้าที่ทดลองหมดอายุ (อ่านได้ในช่วงผ่อนผัน) ได้ชุดใหญ่กว่า · และ `UpdateTrialConfig` (`SubscriptionService.cs:1400-1403`) ให้แอดมินตั้ง
`TrialFeatures = None` รายบริษัทได้ ⇒ fallback ทับค่านั้นด้วยแพ็กเกจ (doc ของ helper บอกว่า "สำเนาที่ไม่ว่าง = ค่าที่ตั้งรายบริษัท ไม่แตะ" แต่ "ว่างที่ตั้งใจ" แยกไม่ออก).
ไม่พบฉาก "ลูกค้าจ่ายแพ็กเกจเล็กได้ฟีเจอร์แพ็กเกจใหญ่": แพ็กเกจเสียเงินสถานะ Active/PastDue ไม่เข้า fallback (เทสต์ `แพ็กเกจเสียเงินสำเนาว่าง_ไม่เติมเอง…`) · สถานะ Trial ของแพ็กเกจ X ได้ `TrialFeatures` ของ X
ซึ่งเป็นค่าเดียวกับที่ `StartTrialAsync`/`UpdatePlanTemplate` คัดลอกให้อยู่แล้ว · ข้อสังเกตรอง: หาแพ็กเกจด้วย `Plan == plan && IsActive` แถวเก่าสุด — ถ้ามีหลายแพ็กเกจ active บน enum เดียว
อาจไม่ใช่แพ็กเกจที่ลูกค้าสมัคร (ไม่ใหม่ — `ResyncSubscriptionsFromTemplateAsync`/planName ใช้คีย์เดียวกัน)

### S200-7 · CONFIRMED · P3 — ลำดับ migration

`DatabaseMigrationHelper.cs:6210-6211`: `DROP INDEX "IX_…_Key"` แล้วค่อย `CREATE UNIQUE INDEX "IX_…_KeyV2"`.
- แถวซ้ำตามคีย์ใหม่เป็นไปไม่ได้ (คีย์ใหม่ ⊃ คีย์เดิมที่ unique อยู่แล้ว · แถวเดิมได้ `Endpoint=''`) ⇒ CREATE ไม่ล้มเพราะข้อมูล — **NOT-A-BUG** ส่วนนั้น
- ทุก statement ถูกห่อ try/catch + log (`DatabaseMigrationHelper.cs:15-21`) ⇒ ล้มก็ไม่ทำให้แอปไม่สตาร์ต
- แต่ถ้า CREATE ล้มด้วยเหตุอื่น (lock timeout) หลัง DROP สำเร็จ ⇒ ไม่มี unique index ⇒ `ON CONFLICT (…4 คอลัมน์)` ล้มทุกคำขอ (fail-open + `LogWarning` ทุกคำขอ = log spam, รายงานว่างเงียบ) ·
  และช่วง rolling deploy เครื่องรุ่นเก่ายังยิง `ON CONFLICT ("CompanyId","Reason","Feature")` ซึ่งไม่มี index รองรับแล้ว ⇒ บันทึกไม่ได้ชั่วคราว
- ข้อเสนอ: สลับเป็น CREATE V2 ก่อน แล้วค่อย DROP เดิม (เครื่องเก่ายังทำงานได้จนเปลี่ยนเครื่อง)

### S200-8 · CONFIRMED · P3 — ต้นทุนต่อคำขอ partner

`SubscriptionMiddleware.cs:94-95` เรียก `ReadAdminSwitchAsync` ก่อน `ActionFor` ทุกคำขอที่รู้บริษัท (เดิม `if (!target.HeaderCarried)`) ⇒ query `SiteSettings` เพิ่ม 1 ครั้งต่อคำขอ partner ·
ถ้า production เคยตั้ง `Mode=Off` (ปิดด่านเขียน) ตอนนี้ partner ได้ `LogOnly` ⇒ โหลดสถานะบริษัท + `GetEffectivePlanAsync` ทุกคำขอเขียน + `LogInformation` ทุกครั้งที่ติด (ไม่หลวม แต่เพิ่มโหลด/เสียง)

### S200-9 · CONFIRMED · P3 — MatchNote ถูกแทนทั้งหมด

`SettlementPermissionScope.HideCandidates` (`Helpers/SettlementPermissionScope.cs:77-86`) `MatchNote = l.MatchNote is null ? null : reason` ⇒ บรรทัดที่ note เป็นเหตุผลสถานะ
(เช่น AutoSummary "ไม่พบร่องรอย") ก็กลายเป็นข้อความ "ผู้สมัคร…แสดงเฉพาะผู้มีสิทธิ์…" ซ้ำทุกบรรทัด + แบนเนอร์หัว (`settlements.html:554`) — UI ไม่พัง
(`matchCandidates || []` · ปุ่มตัดสินต้องมี `canEditLines`) แต่ผู้ดูสูญเสียเหตุผลของสถานะ. ปลอดภัยเกินไว้ก่อน รับได้ — บันทึกไว้

### S200-10 · NOT-A-BUG (ตรวจแล้ว)

1. **ค่าตั้งต้น Shadow จริง**: `SiteSettings.SubscriptionEnforcementMode = Shadow` · migration `DEFAULT 1` · ไม่มีแถว/อ่านไม่ได้ ⇒ Shadow (`SubscriptionGateShadowLog.cs:21-38`) ·
   `EmergencyOverride: ""` ใน appsettings · `appsettings.Production.json` ไม่มีหมวด Subscription
2. **บริษัทถูกระงับ/หมดอายุก่อนหน้า**: เว็บ Shadow = บันทึก LogOnly (`WouldBlock:false`) ไม่บล็อก · เว็บ Enforce = ด่านเขียนตาม config (LogOnly ในเรพ) ⇒ ไม่บล็อก ·
   partner = ตาม config (LogOnly ในเรพ) ⇒ ไม่บล็อก · `SubscriptionInactive` (subscription Cancelled/Suspended) บล็อกทั้งสองรุ่นเหมือนกัน. หลังแก้ ค่าตั้งต้นเหมือนเดิมทุกเส้น
   (ยกเว้น S200-1 ถ้า env ตั้ง Enforce)
3. **Shadow ไม่บล็อก**: `WriteGateModeFor(Shadow)=Enforce` แค่ทำให้ verdict บอกเหตุ แต่ `if (action == Shadow) { Record…; await _next; return; }` (`SubscriptionMiddleware.cs:139-144`)
   อยู่ก่อนทุก `Write403`
4. **unique index ใหม่ล้มเพราะแถวซ้ำ**: เป็นไปไม่ได้ (ดู S200-7) · แอปสตาร์ตได้เสมอ (try/catch ต่อ statement)
5. **upsert หลายเครื่อง**: `INSERT … ON CONFLICT DO UPDATE` อะตอมมิกใน PostgreSQL · `"HitCount"`/`"BlockedCount"` bigint ↔ `long` ตรงชนิด
6. **PruneAsync**: ตารางระดับแพลตฟอร์ม (ไม่ใช่ข้อมูล tenant — ไม่ต้องมี `CompanyId`) · parameterized `{0}` · endpoint แอดมินอยู่ใต้ `/api/admin` (SystemAdmin)
7. **endpoint null**: บริษัทจาก route ต้องมี endpoint ที่จับคู่แล้ว (`TenantCompanyId.FromHttp` อ่าน `RouteValues`) และคำขอ header ไม่ถูกบันทึก ⇒ fallback ด้วย path แทบไม่ถูกใช้ ·
   ถ้าถูกใช้ก็แทน GUID/ตัวเลขด้วย `{id}` + ตัดที่ 200 ⇒ cardinality ถูกจำกัด · `WebApplication` เพิ่ม `UseRouting` ให้เองก่อน middleware ⇒ `GetEndpoint()` มีค่า
8. **คำขอ `/pay/settlements/…` · `orphaned-settlement-receipts` · `settlement-proposal`**: ไม่ตรงคีย์ `/settlement` (ตัวจับคู่ต้องเจอ `/settlement/` หรือจบด้วย `/settlement`) ·
   `git grep` route ที่มีคำว่า settlement ทั้งเรพ = 9 จุด — มีแต่ `SettlementController` ที่ตรง
9. **คอมไพล์**:
   - `context.GetEndpoint() as RouteEndpoint` (`Microsoft.AspNetCore.Routing` — ใส่ using แล้ว · ซ้ำกับ implicit using ของ Web SDK ได้แค่ hidden/warning) · `RoutePattern.RawText` = `string?` ไม่ต้อง using เพิ่ม
   - `view with { Lines = … .ToList() }` / `l with { MatchCandidates = Array.Empty<…>(), … }` — positional `sealed record` · `List<>`/array → `IReadOnlyList<>` ได้
   - `ExecuteSqlRawAsync(string, IEnumerable<object>, CancellationToken)` มีจริง (EF Core 8) · รูปเดียวกับ `RecordAsync` เดิม
   - `Assert.Equal(new[]{…}, v.LogOnly)` ⇒ เลือก `Equal<IReadOnlyList<T>>(T,T)` (ดีกว่า `IEnumerable<T>` overload ทั้งสองอาร์กิวเมนต์) ไม่กำกวม · เปรียบเทียบเชิงลำดับได้ · รูปเดียวกับ `ApprovalAcknowledgementTests.cs:26`
   - `Assert.Equal(10, long)` ⇒ `Equal<long>` (xunit 2.9.2 ไม่มี overload 2 อาร์กิวเมนต์แบบ double/decimal) ไม่กำกวม
   - named-then-positional (`IsWrite: true, writeMode, …` · `WouldBlock: true, endpoint, …`) อยู่ตำแหน่งตรง ⇒ C# 7.2+ ผ่าน
   - นับอาร์กิวเมนต์ DTO/record ทุกตัวที่เปลี่ยน (`SubscriptionGateShadowHit` 10 · `SubscriptionGateShadowRow` 13 · `SubscriptionShadowHitDto` 16 · `SubscriptionEnforcementStatusDto` 12 ·
     `SubscriptionGatePrecheckDto` 12 · `TrialBlockSummary` 6 · เทสต์ `SettlementLineView` 23+2 optional · `SettlementBatchView` 24+1) ตรงทุกตัว · enum ที่เทสต์ใช้มีจริง
     (`SettlementClassifiedBy.AdapterRule` · `SettlementSourceKind.CsvImport` · `SettlementBatchStatus.Imported`)
   - tuple ใน `GroupBy(r => (r.Reason, Feature: …))` ⇒ ชื่อ `Reason` อนุมานได้ (C# 7.1) · `g.Key.Reason` ผ่าน
10. **ผู้เรียกที่ถูกลบ**: `git grep -E "ParseWriteMode|GetWebModeAsync"` บน `team-S-r200` (รวมเทสต์ · JS · tools) = **0** · `writeGateMode` ใน JS เหลือเฉพาะ precheck DTO ที่ยังมีฟิลด์นี้ ·
    ไม่มี implementer อื่นของ `ISubscriptionGateShadowLog` (ไม่มี fake ในเทสต์)

## checker บนสำเนา `git archive team-S-r200`

ผ่านทั้งหมด (สำเนาจาก `git archive team-S-r200` ใน scratchpad — ไม่แตะ working tree หลัก):
`required_call_site_check` 373 กติกา + negative 14 · `dead_helper_check` ไม่มีตัวใหม่ · `using_check` · `record_arg_check` · `nullable_arg_check` ·
`accessibility_check` · `undeclared_local_check` · `tuple_name_merge_check` · `service_interface_check` · `settings_reader_check` = 0 ปัญหา ·
`test_inventory --check` ตรง · sha `2bff9ece`/`8b239819` อยู่บน `team-S-r200` (`merge-base --is-ancestor`) ·
**ไม่ได้ build/test จริง** (ไม่มี SDK) — ข้อคอมไพล์ใน S200-10 ข้อ 9 มาจากการอ่าน

หมายเหตุ: checker เหล่านี้ล็อก "มีการเรียก" แต่ไม่จับ S200-1..4 (เป็นเรื่องทิศของพฤติกรรม/ผู้อ่านตัวที่สอง) — เทสต์ทิศตรงข้ามที่เสนอ:
`legacyEnforce_adminShadow_partnerยังบล็อก` (S200-1) · `trialสำเนาว่าง_CheckFeatureAccessAsync_ได้DocumentEngine` (S200-4) ·
`posting-preview_ดูอย่างเดียว_ไม่มียอดค้างในข้อความ` (S200-5)
