# แผนแก้ "คีย์ไม่ซ้ำ ปะทะ soft-delete" ทั้งระบบ — รอบ 172

> ที่มา: ผู้ใช้สร้างเว็บชื่อ `b1` แล้วได้ 500 (`REF:F37BE341` · `23505 IX_Sites_CompanyId_Slug`)
> แก้เฉพาะ CMS ไปแล้วในรอบ 171 (`2a5af4a`) เอกสารนี้คือการไล่ทั้งระบบว่าเหลืออะไร และควรแก้อย่างไร
>
> **ทุกบรรทัดในเอกสารนี้ผมเปิดไฟล์ยืนยันเอง** ผลของทีมสำรวจที่ยืนยันแล้วว่าผิด อยู่ใน §7

---

## 1. รากของปัญหา — ประโยคเดียว

**ฐานข้อมูลกับแอปมองเห็นแถวคนละชุด** — unique index มองเห็นทุกแถวรวมที่ลบแล้ว
แต่ทุก query ของแอปถูก `HasQueryFilter(!IsDeleted)` ตัดแถวที่ลบทิ้ง
⇒ ด่าน "ตรวจแล้วว่าง" **ไม่เท่ากับ** "insert ได้จริง" ⇒ 23505 โผล่เป็น 500 ที่ผู้ใช้อ่านไม่ออก

กติกาเดียวที่ทำให้หายถาวร: **index ต้องมองเห็นแถวชุดเดียวกับที่ด่านตรวจซ้ำมองเห็น**

---

## 2. ขนาดจริง (ตัวเลขที่วัดแล้ว ไม่ใช่ประมาณ)

| ตัวชี้วัด | จำนวน |
| --- | --- |
| entity ที่ soft-delete ได้ (มี query filter `!IsDeleted`) | 127 |
| unique index ที่ **โมเดล** ประกาศโดยไม่กรอง `IsDeleted` | 64 |
| unique index ที่ **โมเดล** ประกาศพร้อมตัวกรองแล้ว | 1 (`Employee`) |
| `CREATE UNIQUE INDEX` ใน raw SQL ทั้งหมด | 76 |
| ในนั้น กรอง `IsDeleted = false` แล้ว | 43 |

**ข้อสังเกตที่สำคัญกว่าตัวเลข:** เรพนี้มีทางแก้ปัญหาเดียวกัน **5 แบบที่ไม่คุยกัน**

| # | วิธี | ที่ใช้อยู่ |
| --- | --- | --- |
| 1 | partial unique index (ถูกที่สุด) | `Employee` — `AccountingDbContext.cs:1473` + `DatabaseMigrationHelper.cs:5156` |
| 2 | ปลดคีย์ด้วย suffix ตอนลบ | CMS — `Helpers/CmsRetiredSlug` (รอบ 158 + 171) |
| 3 | raw SQL ลบแถว tombstone ทิ้ง | ผังบัญชี — `AccountingService.cs:217` |
| 4 | ปลุกแถวเดิมแทน insert (upsert) | `SubscriptionService.cs:67-77` · `PayrollService.RestoreEmployeeAsync:601` |
| 5 | ไม่ทำอะไร → 500 | ที่เหลือทั้งหมด |

นี่คือ "logic ทำงานสิ่งเดียวกันซ้อนกัน" ตรงตามที่เจ้าของถามไว้ในรอบ 169

---

## 3. "ติดจริง" ไม่ใช่ 64 — คัดด้วย 2 เงื่อนไข

ต้องเป็นจริงทั้งคู่: (ก) **มีเส้น soft-delete ที่กดได้จริง** และ (ข) **คีย์เป็นค่าที่ผู้ใช้ใส่ซ้ำได้**

### 3.1 ติดจริง 8 ตัว (ยืนยันทุกตัว)

| # | Entity · คีย์ | เส้นลบ | ด่านกันลบ | หมายเหตุ |
| --- | --- | --- | --- | --- |
| 1 | **Product.Code** | soft `ProductService.cs:198` | **ไม่มีเลย** | อันตรายสุด — ดู §4 |
| 2 | **Project.Code** | soft ทั้ง 2 สาขา `ProjectAccountingService.cs:267,273` | ไม่มี (เช็กเพื่อเลือกโหมดเท่านั้น) | มีเส้นสร้าง **อัตโนมัติจาก OCR** `OcrMetadataProjectMatcher.cs:363` ⇒ ล้มโดยไม่มีคนเห็น |
| 3 | **SiteCustomer.Email** | soft 2 ทาง `CmsCustomerService.cs:134` + merge `:326` | ไม่มี | ลูกค้าหน้าร้านที่ถูกลบ สมัครด้วยอีเมลเดิมไม่ได้อีก |
| 4 | **CompanyRole.Name** | soft `RolePermissionService.cs:184` | — | ลบบทบาท "ผู้จัดการ" แล้วสร้างชื่อเดิมไม่ได้ |
| 5 | **AccountingDimension.Code** | soft `DimensionalAccountingService.cs:115` | มี (มิติย่อย) | |
| 6 | **Department.Code** | soft `OrganizationService.cs:119` | แคบ (เฉพาะพนักงาน) | รายงานโชว์ Name ไม่ใช่ Code ⇒ ใช้ซ้ำปลอดภัย |
| 7 | **Position.Code** | soft `OrganizationService.cs:205` | แคบ (เฉพาะพนักงาน) | เหมือนข้อ 6 |
| 8 | **UserExternalLogin** (Provider, ProviderUserId) | soft `AuthService.cs:1090` | มี | ถอดบัญชี Google แล้วผูกกลับไม่ได้ · ซ้ำร้ายมี no-op ตาม §5 |

### 3.2 ระเบิดเวลา — index ไม่กรอง แต่ยังไม่มีเส้นลบ (เพิ่มเส้นลบเมื่อไรคือติดทันที)

`PortalAccess.Email` · `RevenueContract.ContractNumber` · `PayrollItem.Code` ·
`FinancialKpi.Code` · `ApiFeature.FeatureCode` · `CompanyFeature.FeatureCode` · `WarehouseStock.LotNumber`

### 3.3 กลุ่มที่ **ห้าม** ใช้คีย์ซ้ำ (เลขตามกฎหมาย/audit) — ต้องคงไว้แบบเดิม

เลขเอกสาร §86/4 · เลข JE · เลขใบสำคัญจ่าย · 50 ทวิ · ภ.พ. ฯลฯ — ตรวจแล้วว่า
**ตัวออกเลขทุกตัวใส่ `IgnoreQueryFilters()` ครบ** (`SequenceNumber` 6 ผู้เรียก + `DocumentNumberGenerator.cs:88`)
⇒ ไม่ใช่บั๊ก ห้ามเปลี่ยนเป็น partial index

---

## 4. ข้อค้นพบที่เปลี่ยนคำตอบ — สินค้าใช้รหัสซ้ำไม่ได้

`DocumentLine` **ไม่มี FK ไปสินค้าเลย** มีแต่ `ProductCode` เป็นสตริง (`Document.cs:671`)
และตอนตัดสต็อก/คิดต้นทุน ระบบ **resolve สินค้ากลับจากรหัส** (`DocumentService.cs:12998-13000`, `:13019-13021`)

ผลสองชั้น:

1. **ถ้าเปิดให้ใช้รหัสซ้ำ** → เอกสารเก่าที่ถือ `ProductCode="P001"` จะไปตัดสต็อกของ**สินค้าตัวใหม่** เงียบ ๆ
2. **บั๊กที่มีอยู่แล้ววันนี้** → ลบสินค้าที่ยังอยู่บนเอกสารที่ยังไม่อนุมัติ แล้วอนุมัติทีหลัง
   `TryGetValue` ไม่เจอ (เพราะ `!p.IsDeleted`) → `continue` → **เอกสารโพสต์โดยไม่ตัดสต็อกเลย ไม่มีคำเตือน**

⇒ สินค้าต้องใช้ทางแก้คนละแบบกับตัวอื่น (ดู §6 เฟส 2)

---

## 5. จุดอ่อนของกลไก migration เอง (ต้องแก้ก่อนทุกอย่าง)

1. **`catch { }` กลืน error ทุกคำสั่ง ไม่ log อะไรเลย** (`DatabaseMigrationHelper.cs` ท้ายไฟล์)
   ⇒ ถ้าคำสั่งแก้ index ล้ม จะไม่มีใครรู้ และเราจะเข้าใจผิดว่าแก้แล้ว
   (ขัดกฎเหล็ก #4 E "ห้าม catch {} กลืน error" และหลักการ F2 ข้อ 7 "ล้มดัง 3 ที่")
2. **ไม่มีการตรวจย้อนว่า index ถูกสร้างจริง** (`pg_indexes` ปรากฏ 0 ครั้ง)
3. **`IF NOT EXISTS` + ชื่อชนกับ index ของโมเดล = การแก้กลายเป็น no-op เงียบ** — พบ **7 จุด**:
   `IX_UserExternalLogins_Provider_ProviderUserId` · `IX_AiResponseCaches_Hash_Company` ·
   `IX_AiSuggestionMemories_Company_Feature_Input` · `IX_AiUsageDailies_Day_Provider_Feature` ·
   `IX_AiUsageDailyTenants_Day_Co_Provider_Feature_Channel` · `IX_AiFeatureRoutingConfigs_FeatureKey` ·
   `IX_LocalModelHealths_FeatureKey`
   (โมเดลสร้างตัวไม่กรองด้วยชื่อนี้ไปก่อน → raw SQL ที่ตั้งใจใส่ตัวกรองจึงไม่เคยถูกสร้าง)

---

## 6. แผน 5 เฟส

### เฟส 0 — ทำให้ "มองเห็น" ก่อนแก้อะไร  *(เล็ก · ไม่กระทบผู้ใช้)*
- migration loop เลิกกลืน error → log ชื่อคำสั่งที่ล้ม
- เพิ่มขั้นตรวจย้อน: query `pg_indexes` ยืนยันว่า index ที่ตั้งใจสร้าง/ลบ เป็นไปตามนั้นจริง ไม่ตรง = log ดัง
- **checker ใหม่** `tools/soft_delete_unique_index_check.py` — parse `AccountingDbContext.cs` ล้วน
  (ไม่ต้อง type resolution จึงไม่ขัดข้อห้าม F4 ข้อ 1) ฟ้องเมื่อ entity มี query filter `!IsDeleted`
  แต่ unique index ไม่กรอง · ratchet กับ baseline · negative test = ถอดตัวกรองของ `Employee` ออกต้องฟ้อง
- แก้ 7 จุด no-op ใน §5.3 (เปลี่ยนชื่อ index ใหม่ + DROP ตัวเดิม)

### เฟส 1 — 6 ตัวที่ "ใช้ซ้ำได้ปลอดภัย" → partial index  *(ตามแบบ `Employee` เป๊ะ)*
`Department` · `Position` · `AccountingDimension` · `CompanyRole` · `SiteCustomer` · `UserExternalLogin`

สูตรต่อตัว (4 บรรทัด ไม่แตะข้อมูลสักแถว):
1. `AccountingDbContext`: `.IsUnique().HasFilter("\"IsDeleted\" = false")`
2. migration: `CREATE UNIQUE INDEX … "UX_<Table>_<Cols>_Active" … WHERE "IsDeleted" = false` **ก่อน** แล้วค่อย
   `DROP INDEX IF EXISTS "IX_<Table>_<Cols>"` (กลับลำดับจาก `Employee` เพื่อไม่ให้มีช่วงที่ไม่มีใครบังคับ uniqueness)
3. ด่านตรวจซ้ำในเส้นสร้าง **ไม่ต้องแก้** — ของเดิมใช้ query filter อยู่แล้ว ซึ่งตรงกับ index ใหม่พอดี
4. ไม่มี data migration เพราะไม่ต้องแตะข้อมูล

> สร้าง partial index ไม่มีทางล้มเพราะข้อมูลซ้ำ — index เดิมบังคับ uniqueness ทั้งตารางอยู่แล้ว เซตย่อยจึงไม่ซ้ำแน่นอน

### เฟส 2 — `Product` (ต้องเลือกทาง ดู §8)
ไม่ว่าเลือกทางไหน ต้องทำร่วมกัน 2 อย่าง:
- **ด่านกันลบ** — บล็อกลบสินค้าที่ปรากฏบน `DocumentLine` หรือมี `StockMovement` แล้ว
  (ลอกรูปแบบจาก `Branch` ที่บล็อกพร้อมอ้าง พ.ร.บ.การบัญชี ม.10 — `DimensionalAccountingService.cs:441-448`)
- **แก้บั๊กสต็อกเงียบ §4.2** — resolve ไม่เจอต้อง **ดัง** ไม่ใช่ `continue`

### เฟส 3 — `Project` (เส้นอัตโนมัติสำคัญกว่าเส้นมือ)
partial index + ให้ตัวจับคู่ OCR มองเห็นแถวที่ลบแล้ว (`OcrMetadataProjectMatcher.cs:277-282` กรอง `!IsDeleted` ทิ้ง)
ระหว่างนี้ฝั่งอัตโนมัติควร fallback เป็นรหัสสุ่ม `EXT-…` แทนการล้มทั้ง batch

### เฟส 4 — ยุบ 5 วิธีให้เหลือ 1
- CMS กลับมาใช้ partial index เหมือนทุกตัว (ถ้าเจ้าของเลือกตาม §8.2) แล้วถอน `CmsRetiredSlug` ออกจากเส้น `Sites`
- ถอน raw SQL ลบ tombstone ของผังบัญชีเมื่อ index เป็น partial แล้ว
- เขียนกติกาข้อเดียวลง `docs/lessons/` + CLAUDE.md F2 (ถ้าเปลี่ยนหลักการ)

---

## 7. ตรวจแล้วไม่ใช่บั๊ก — ห้ามรายงานซ้ำ

| สิ่งที่เคยรายงาน | ความจริงที่ยืนยันแล้ว |
| --- | --- |
| `Branch.Code` ติดปัญหา | **ไม่ติด** — hard-delete `DimensionalAccountingService.cs:450` + ด่านกัน JE `:441-448` |
| `ProductCategory.Code` ติดปัญหา | **ไม่ติด** — hard-delete `ProductService.cs:410` |
| `Warehouse.Code` ติดปัญหา | **ไม่ติด** — ไม่มี endpoint ลบเลยสักตัว |
| `ChartOfAccount.AccountCode` ติดปัญหา | **ไม่ติด** — ไม่มีเส้นลบทั้ง soft และ hard (เหลือแต่ข้อมูลเก่า ซึ่ง seed กวาดให้แล้ว) |
| `User.Email` มีเส้น soft-delete | **ไม่จริง** — บรรทัดที่ทีมอ้างเป็น `LodgingUnit` ไม่ใช่ `User` (`LodgingService.cs:420`) |
| ตัวออกเลขเอกสารมองไม่เห็นแถวที่ลบ | **ไม่จริง** — ใส่ `IgnoreQueryFilters()` ครบทั้ง 7 จุด |
| partial index 39/40 ลืม DROP ตัวเดิม | **เกินจริง** — ส่วนใหญ่โมเดลไม่ได้ประกาศ index คู่แข่ง ของจริงคือ **7 จุด** ใน §5.3 |

*(บรรทัดแรก 4 ข้อคือรายงานของ **ผมเอง** ในรอบ 171 ที่ผิด — `CHANGELOG.md` รอบ 171 และ
`docs/lessons/general-design.md` ระบุ 9 ตัว ของจริงคือ 8 ตัวคนละชุด ต้องแก้ทั้งสองไฟล์ในเฟส 0)*

---

## 8. สิ่งที่ต้องให้เจ้าของตัดสิน (ไม่เดาแทน)

**8.1 `Product` เลือกทางไหน**

| ทาง | ทำอะไร | ได้ | เสีย |
| --- | --- | --- | --- |
| **A. ด่านกันลบ + คงคีย์เข้ม** | ห้ามลบสินค้าที่มีประวัติ · รหัสไม่มีวันถูกใช้ซ้ำ | เล็ก เร็ว ไม่แตะโครงสร้าง | สินค้าที่สร้างผิดและเคยออกเอกสาร จะลบไม่ได้ตลอดไป (ปิดใช้งานได้อย่างเดียว) |
| **B. เพิ่ม `DocumentLine.ProductId`** | ผูกด้วย Id เหมือนโมดูลอื่น แล้วค่อยเปิดให้ใช้รหัสซ้ำ | ถูกต้องระยะยาว · ปิดบั๊กสต็อกเงียบไปในตัว | ใหญ่ — ต้อง backfill Id จากรหัสของเอกสารเก่า และแตะเส้นตัดสต็อก/ต้นทุน |

ผมแนะนำ **A ก่อน แล้ว B เป็นงานแยก** — A ปิดอาการได้ทันทีโดยไม่เสี่ยง และไม่ขวาง B ทีหลัง

**8.2 `Sites` ที่แก้ไปแล้วรอบ 171 จะย้อนมาใช้ partial index ไหม**
ตอนนี้ CMS ใช้วิธี "ปลดคีย์ด้วย suffix" ซึ่งต่างจาก `Employee` ถ้าอยากเหลือวิธีเดียวทั้งระบบควรย้อน
**ยังย้อนได้สะอาดถ้ายังไม่ deploy** (migration ปลดคีย์ยังไม่เคยรัน) ถ้า deploy ไปแล้วต้องมี migration ย้อนกลับเพิ่ม

---

## 9. ลำดับที่แนะนำ

```
เฟส 0 (เล็ก · ปลอดภัย · ทำให้เห็นของจริง)  →  เฟส 1 (6 ตัว สูตรเดียวกัน)
   →  เฟส 3 (Project + เส้น OCR)  →  เฟส 2 (Product ตามคำตัดสิน 8.1)  →  เฟส 4 (ยุบให้เหลือวิธีเดียว)
```

เฟส 0+1 ไม่ต้องรอคำตัดสินใด ๆ และไม่แตะข้อมูลผู้ใช้สักแถว
