# ACCOUNT_STRUCTURE.md — โครงสร้างลูกค้า / กลุ่มบริษัท / สาขา / บิลลิ่ง / ผลิตภัณฑ์ API

> **Single source of truth** ของโครงสร้าง "ใครเป็นลูกค้า ใครจ่ายเงิน ข้อมูลอยู่ที่ไหน
> ตั้งค่าอะไรได้ที่ชั้นไหน" — ครอบทั้งลูกค้า NextAcc เต็มรูป (Full) และลูกค้าที่เชื่อม
> ผ่าน API จากระบบบัญชีอื่น (Connected เช่น Microsoft Dynamics)
>
> คู่กับ: `DOCUMENT_FLOW.md` (flow เอกสาร) · `CHATBOT_PLAN.md` (chatbot)

## กฎการดูแลไฟล์นี้ (hard requirement — แบบเดียวกับ DOCUMENT_FLOW.md)

- ทุก commit ที่แตะ entity/พฤติกรรมในไฟล์นี้ (BillingAccount, AccountSubscription,
  Subscription, Company, Branch, ApiClient, UsageEvent, pricing, portal `/connect`)
  **ต้องอัปเดตไฟล์นี้ในคอมมิตเดียวกัน** — ห้ามแยก, ห้าม TODO
- ทุก section มีป้ายสถานะ: ✅ มีจริงในโค้ด · 🔨 กำลังทำ · 📋 ออกแบบแล้วยังไม่ได้ทำ
  **เปลี่ยนสถานะทันทีที่โค้ดเปลี่ยน** — ไฟล์นี้ต้องบอกความจริงเสมอ ไม่ใช่ความตั้งใจ
- ถ้าโค้ดกับ doc ไม่ตรง = **doc ผิด** แก้ doc ในคอมมิตเดียวกับงานที่ทำอยู่
- อัปเดตบรรทัดท้ายไฟล์ `Last verified …` ทุกครั้ง

---

## 1. หลักการ 3 ชั้น

```
BillingAccount  ─ ใครจ่าย (สัญญา, บิล, โควตา pool, volume tier, เครดิต)
 └─ Company     ─ นิติบุคคล = tenant (สมุดบัญชี, ภ.พ.30, งบ, e-Tax cert, PDPA)
     └─ Branch  ─ จุดปฏิบัติการ (เลขเอกสาร series, รหัสสาขา §86/4, มิติรายงาน)
```

| ชั้น | เป็นหน่วยของ | **ไม่ใช่**หน่วยของ |
| --- | --- | --- |
| BillingAccount | เงิน/สัญญา/โควตารวม/สิทธิ์บริหารกลุ่ม | ข้อมูลบัญชี (ห้ามเห็นข้ามบริษัทอัตโนมัติ) |
| Company | ข้อมูลบัญชี + ภาษีทุกอย่าง (tenant isolation ต่อ `CompanyId` ทุก query) | การจ่ายเงิน (จ่ายเองได้เป็นข้อยกเว้น — ดู §6.3) |
| Branch | เลขเอกสาร, ใบกำกับ, การยื่น VAT รายสาขา, attribution การใช้งาน | เงิน/โควตา/สิทธิ์ (ไม่มีบิลระดับสาขา ไม่มีโควตาระดับสาขา) |

**เหตุผลทางกฎหมาย**: บริษัทย่อย = นิติบุคคลแยก (เลขภาษี 13 หลักของตัวเอง งบของตัวเอง)
ส่วนสาขา = นิติบุคคลเดียวกัน เลขภาษีเดียวกัน ต่างแค่รหัส 00000/00001 สมุดบัญชีชุดเดียว —
สองอย่างนี้ห้ามออกแบบเหมือนกันเด็ดขาด

---

## 2. รูปแบบการใช้งานที่ต้องรองรับ (ทุก shape → โครงเดียวกัน)

| # | รูปแบบ | mapping | หมายเหตุ |
| --- | --- | --- | --- |
| 1 | บริษัทเดี่ยว 1 เจ้าของ | 1 Account · 1 Company · 1 Branch (00000) | เคสเล็กสุด — Account ถูกสร้างห่ออัตโนมัติ ผู้ใช้ไม่รู้สึก |
| 2 | Holding + บริษัทย่อยแนวดิ่ง | 1 Account · N Company (`ParentCompanyId` ชี้ holding) | ผังบริษัทวาดจาก ParentCompanyId; งบรวมใช้ ConsolidationGroup (แยกเรื่องกัน) |
| 3 | บริษัทพี่น้องแนวขนาน (เจ้าของเดียว) | 1 Account · N Company (ไม่มี Parent) | billing เหมือน #2 เป๊ะ — ต้นไม้เป็นแค่การแสดงผล |
| 4 | เครือสาขา (1 นิติบุคคล N จุดขาย เช่นเครือรีสอร์ต/ร้านอาหาร) | 1 Account · 1 Company · N Branch | **ไม่ใช่** N Company! เลขเอกสาร/ภ.พ.30 แยกตามสาขา แต่สมุดเดียว |
| 5 | สำนักงานบัญชีดูแลลูกค้า N ราย | 1 Account (ของสำนักงาน) · N Company (ของลูกค้าแต่ละราย) | ลูกค้าย้ายสำนักงานบัญชี = detach ไป Account ใหม่ ข้อมูลติดบริษัทไป |
| 6 | Franchise | 1 Account · N Company (franchisee ที่จดนิติบุคคลแยก) หรือ Branch (สาขาของ franchisor เอง) | เลือกตามสถานะนิติบุคคลจริง |
| 7 | **Connected** — ใช้ ERP อื่น (Dynamics/SAP) ยิง API มาใช้ฟีเจอร์ | เหมือน #1–#6 ทุกประการ แต่ `Company.CompanyKind = Connected` | ไม่มี user login เข้าหน้าบัญชี, ไม่มี UI บัญชี — จัดการผ่าน portal `/connect` + API เท่านั้น |
| 8 | ผสม (บางบริษัทในเครือใช้ NextAcc เต็ม บางบริษัทใช้ Dynamics) | 1 Account · Company ปน Full/Connected | โครงรองรับโดยไม่ต้องทำอะไรเพิ่ม — CompanyKind เป็น per-company |

**Upsell path ในตัว**: Connected → Full = พลิก `CompanyKind` (ข้อมูลอยู่ครบแล้ว) —
นี่คือเหตุผลเชิงธุรกิจหลักที่ Connected ต้องเป็น tenant จริง ไม่ใช่ระบบแยก

---

## 3. Entity (สถานะปัจจุบัน + ที่ออกแบบ)

### 3.1 มีจริงแล้ว ✅

| Entity | ไฟล์ | หมายเหตุ |
| --- | --- | --- |
| `Company` | `Models/Entities/Company.cs` | tenant; มี `BranchCode` (สำนักงานใหญ่ 00000) |
| `Branch` | `Models/Entities/DimensionalAccounting.cs:37` | `TaxBranchCode`, `IsHeadOffice`, ที่อยู่ครบ; `BranchId` ใช้บน JournalEntry/Payroll แล้ว |
| `AccountSubscription` | `Models/Entities/AccountSubscription.cs` | แพลนครอบหลายบริษัท **แต่ผูก `OwnerUserId` (คน)** — จุดอ่อนที่ §8 แก้ |
| `Subscription` (ต่อบริษัท) | `Models/Entities/Subscription.cs` | ชนะ AccountSubscription เมื่อบริษัทมีของตัวเอง (resolution order §6.3) |
| `ConsolidationGroup/Member` | `DimensionalAccounting.cs:122` | งบรวม + %ถือหุ้น — **เรื่องการเงิน แยกจาก billing เด็ดขาด** |
| `ExternalIntegration` + `ApiKeyMiddleware` | `Models/Entities/`, `Middleware/` | ต้นแบบของ ApiClient (TakeTime ใช้อยู่) |
| `CompanyUser` | `Models/Entities/User.cs:75` | สิทธิ์ราย user รายบริษัท |

### 3.2 ออกแบบใหม่ 📋

```csharp
// องค์กรผู้จ่ายเงิน — ไม่ใช่คน (คนเป็นแค่ผู้ดูแล ถอด/เพิ่มได้)
public class BillingAccount : BaseEntity
{
    public string Name;                    // "เครือ ABC กรุ๊ป"
    public string? TaxId;                  // นิติบุคคลผู้รับใบกำกับ (โหมดรวมศูนย์)
    public string? BillingAddress;
    public string BillingEmail;
    public BillingMode BillingMode;        // Centralized | PerCompany   (§6.1)
    public PaymentModel PaymentModel;      // Prepaid | Postpaid         (§6.2)
    public decimal CreditBalance;          // เครดิตคงเหลือ (Prepaid)
    public int GraceDays = 7;
    public bool IsSandbox;                 // ทั้ง account เป็น sandbox (ทดสอบก่อนเซ็น)
}
public class BillingAccountAdmin { Guid BillingAccountId; Guid UserId; }  // M:N

// Company เพิ่ม:
//   Guid? BillingAccountId   — สังกัดกระเป๋าเงินไหน (null = จ่ายเอง/trial)
//   Guid? ParentCompanyId    — ผังเครือ (แสดงผล + consolidation เท่านั้น ไม่เกี่ยวเงิน)
//   CompanyKind CompanyKind  — Full | Connected
//   bool VatFilingConsolidated — ยื่น ภ.พ.30 รวมสาขา (ต้องมีอนุมัติสรรพากร)

// API key ราย client — ยกระดับจาก ExternalIntegration
public class ApiClient : TenantEntity      // CompanyId = บริษัทที่ key นี้เขียน/อ่าน
{
    public Guid? BillingAccountId;         // รวมบิล/รวม dashboard ที่ account
    public Guid? BranchId;                 // scope ระดับสาขา (POS/PMS ต่อสาขา) — null = ทั้งบริษัท
    public string KeyHash;                 // เก็บ hash ไม่เก็บ plaintext
    public string Scopes;                  // "ocr:write bank:write documents:write" (space-sep)
    public int RateLimitPerMinute = 60;
    public string? IpAllowlistCsv;
    public string? WebhookUrl; public string? WebhookSecret;  // HMAC-SHA256 sign
    // ระบบบัญชีปลายทางที่ลูกค้าเลือกตอน onboard — กำหนดว่า connector ปลั๊กไหน
    // แปลง payload (GenericRest = ยิง contract กลางของเราเองตรง ๆ ไม่ต้องมีปลั๊ก)
    public ErpConnectorType ConnectorType; // GenericRest | Dynamics365 | SapB1 | Xero | Odoo | ...
    public string? ConnectorConfigJson;    // ค่าเชื่อมต่อเฉพาะ ERP นั้น (endpoint/tenant id — ไม่เก็บ secret ตรง ๆ)
    public bool IsSandbox; public bool IsActive;
}

// การใช้งานที่คิดเงินได้ — append-only ห้าม UPDATE/DELETE
public class UsageEvent : TenantEntity     // CompanyId = ผู้ใช้งานจริง
{
    public Guid? BillingAccountId;         // denormalize ตอนเกิด (กัน detach ย้อนบิลเก่า)
    public Guid? BranchId;                 // attribution รายสาขา (breakdown ไม่ใช่บิล)
    public Guid? ApiClientId;              // มาจาก key ไหน (null = ใช้ผ่าน UI ปกติ)
    public string FeatureCode;             // "ocr.scan" | "bank.line" | "etax.doc" | ...
    public int Quantity = 1;
    public decimal UnitPriceSnapshot;      // ราคา ณ วันเกิด — เปลี่ยนราคาแล้วบิลเก่าห้ามขยับ
    public string? IdempotencyKey;         // unique ต่อ client — retry ไม่โดนเก็บซ้ำ
    public string? RefEntityType; public Guid? RefEntityId;  // ชี้กลับเอกสาร/scan ที่เกิด
}

// แคตตาล็อกฟีเจอร์ที่เปิดขายผ่าน API — admin คุมทั้งการมีอยู่และวิธีคิดเงิน
public class ApiFeature : BaseEntity
{
    public string FeatureCode;             // "ocr.scan" | "bank.recon" | "etax.generate" | ...
    public string Name; public string NameEn; public string? Description;
    public bool IsPublished;               // ปิด = หายจากหน้าเลือกของลูกค้าทันที (ที่สมัครแล้วใช้ต่อได้)
    public string RequiredScopes;          // scope ที่ key ต้องมีเมื่อเปิดฟีเจอร์นี้
}

// ฟีเจอร์ที่ "บริษัทนี้" เลือกเปิด — ลูกค้ากดเปิด/ปิดเองใน portal (self-service)
public class CompanyFeature : TenantEntity
{
    public string FeatureCode;
    public bool IsEnabled;                 // ปิด = /api/v1 ของฟีเจอร์นั้นตอบ 403 ทันที + หยุดคิดเงิน
    public DateTime EnabledAt; public string EnabledBy;   // audit ว่าใครกดเปิด (มีผลเรื่องเงิน)
}

public class ApiPricingPlan : BaseEntity
{
    public string FeatureCode;
    // วิธีคิดเงิน — admin เลือกต่อฟีเจอร์ ไม่ hard-code:
    //   PerUnit     = ต่อหน่วยงาน (ต่อเอกสาร OCR / ต่อบรรทัด statement)
    //   Tiered      = ต่อหน่วยแบบขั้นบันได (TierJson, นับรวมทั้ง account)
    //   FlatMonthly = เหมา/เดือน ไม่จำกัดจำนวน
    //   PerCall     = ต่อ request (ฟีเจอร์เบา ๆ เช่น validate เลขภาษี)
    public PricingMethod Method;
    public decimal UnitPrice;              // ความหมายตาม Method
    public int FreeQuotaPerMonth;
    public string? TierJson;               // [{fromQty, unitPrice}]
    public DateTime EffectiveFrom; public DateTime? EffectiveTo;   // ราคามีอายุ — audit ได้
}
```

หลักที่ฝังในดีไซน์:
- `UsageEvent.BillingAccountId` **stamp ตอนเกิด** ไม่ join สด — บริษัท detach ออกจากเครือแล้ว
  บิลเดือนเก่าไม่เปลี่ยน (append-only เหมือน AuditLog)
- ราคา snapshot บน event + แพลนมี `EffectiveFrom/To` — โต้แย้งบิลได้เสมอว่า ณ วันนั้นราคาเท่าไร
- นับเงินตาม **"งานที่สำเร็จ"** (ต่อเอกสาร/ต่อบรรทัด) ไม่ใช่ต่อ AI token — local model
  เก่งขึ้น → ต้นทุนเราลด → margin โตเอง (สอดคล้องกฎเหล็ก #1)

---

## 4. ตารางการตั้งค่า — อะไรตั้งได้ที่ชั้นไหน (ทุกช่องตั้งอิสระ)

| การตั้งค่า | Account | Company | Branch | ApiClient | default |
| --- | :-: | :-: | :-: | :-: | --- |
| โหมดบิล (รวมศูนย์/แยกใบ) | ✔ | override ได้ (§6.3) | — | — | Centralized |
| Prepaid/Postpaid | ✔ | — | — | — | Prepaid |
| โควตารวม (pool) | ✔ | — | — | — | ตามแพลน |
| เพดานย่อยกันกินโควตากลุ่ม | — | ✔ optional | — | — | ไม่จำกัด |
| Volume tier | ✔ (นับรวมทุกบริษัท) | — | — | — | ตามแพลน |
| Grace period | ✔ | — | — | — | 7 วัน |
| CompanyKind (Full/Connected) | — | ✔ | — | — | Full |
| ยื่น ภ.พ.30 รวม/แยกสาขา | — | ✔ (`VatFilingConsolidated`) | — | — | แยกรายสาขา (ตามกฎหมาย ยื่นรวมต้องขออนุมัติ) |
| เลขเอกสาร series | — | — | ✔ (ต่อ `CompanyId+BranchCode+TaxYear` gap-free) | — | 00000 |
| Scopes / สิทธิ์ API | — | — | — | ✔ | แคบสุด |
| Rate limit / IP allowlist | — | — | — | ✔ | 60/นาที |
| ผูก key กับสาขา | — | — | — | ✔ optional | ทั้งบริษัท |
| Webhook + secret | — | — | — | ✔ | ปิด |
| Sandbox | ✔ ทั้ง account | — | — | ✔ ราย key | ปิด |
| AI budget cap | ✔ (ต่อยอด AiBudgetGuard จาก global → ราย account) 📋 | ✔ optional | — | — | ตามแพลน |
| เปิด/ปิดฟีเจอร์ (`CompanyFeature`) | — | ✔ **ลูกค้ากดเองใน portal** | — | — | ปิดทุกตัว (opt-in) |
| วิธีคิดเงิน+ราคาต่อฟีเจอร์ (`ApiPricingPlan`) | — | — | — | — | **admin เท่านั้น** (หน้า `/admin`) |
| ระบบบัญชีที่เชื่อม (`ConnectorType`) | — | — | — | ✔ ลูกค้าเลือกตอนสร้าง key | GenericRest |
| Subdomain (`{slug}.nextacc.app`) | ✔ ตั้งเองทันที | — | — | — | auto จากชื่อ account |
| Custom domain (โดเมนตัวเอง) | ✔ + ต้อง verify DNS TXT | — | — | — | ไม่มี |
| วิธี login ที่อนุญาต (`AllowedAuthMethods`) | ✔ (เช่นบังคับ O365 อย่างเดียว) | — | — | — | ทุกวิธี |

---

## 5. โควตา — ลำดับ resolve (ต่อยอดของเดิม เปลี่ยนสมอจากคนเป็นองค์กร)

```
1) Company มี Subscription ของตัวเอง (Active)     → ใช้อันนั้น (ชนะเสมอ — ของเดิม)
2) Company.BillingAccountId → AccountSubscription  → โควตา pool ของกลุ่ม
3) ไม่มีทั้งคู่                                     → auto-trial ต่อบริษัท (ของเดิม)
```

- Pool นับ**รวมทุกบริษัทใต้ account** (ปรัชญาเดิมของ AccountSubscription — ถูกแล้ว)
- เพดานย่อยต่อบริษัท (optional): เตือนที่ 80% → block ที่ 100% → AccountAdmin ปลดได้เอง
- สาขา**ไม่มีโควตา** — คุมพฤติกรรมด้วย rate limit ของ ApiClient ที่ผูกสาขาแทน

---

## 6. Billing

### 6.1 สองโหมด (ตั้งที่ Account)

**Centralized (default)** — ใบแจ้งหนี้ + ใบกำกับภาษี **1 ใบ/เดือน** ออกให้นิติบุคคลตาม
`BillingAccount.TaxId` โดย**แยกบรรทัดต่อบริษัท** (นักบัญชีกลุ่มใช้ charge back ภายใน):

```
ใบกำกับภาษี → บจก.โฮลดิ้ง
  ค่าบริการ OCR — บจก.ย่อย B (120 เอกสาร)   1,200.-
  ค่าบริการ OCR — บจก.ย่อย C (45 เอกสาร)      450.-
  Bank Recon — บจก.ย่อย B (300 บรรทัด)        300.-
  รวม + VAT 7%                              2,086.50
```
- สัญญาต้องระบุชัดว่า holding เป็นคู่สัญญาผู้ซื้อบริการแทนกลุ่ม
- หัก ณ ที่จ่าย 3% มาจากผู้จ่ายใบเดียว → 50 ทวิใบเดียว

**PerCompany** — ออกใบกำกับ N ใบให้แต่ละนิติบุคคลตามการใช้จริง (ผู้สอบบัญชีบางเจ้าขอ
ให้ต้นทุนลงตรงนิติบุคคล) — portal ยังเห็น roll-up รวม; แลกกับตามเก็บ N ทาง + 50 ทวิ N ใบ

ทั้งสองโหมด gen ผ่าน **pipeline เอกสารของ NextAcc เอง** (บริษัทผู้ให้บริการ = tenant
ของตัวเอง) → ได้เลข gap-free + e-Tax + VAT 7% ครบโดยไม่เขียนระบบบิลใหม่

### 6.2 Prepaid (default) / Postpaid

- **Prepaid**: ซื้อแพ็กเครดิตล่วงหน้า → `CreditBalance` ตัดตาม UsageEvent → เตือน 20%/หมด
  → หมดแล้ว block งานใหม่ (งานค้างคิวทำต่อจนจบ) — เหมาะตลาดไทย ตัดปัญหาตามเก็บเงิน
- **Postpaid**: ใช้ก่อนจ่ายทีหลัง สิ้นเดือนออกใบแจ้งหนี้ — เฉพาะลูกค้าใหญ่ที่มีสัญญา + วงเงิน
- ซื้อเครดิต = ออก "ใบเสร็จรับเงิน (มัดจำ)" ตาม flow เงินมัดจำที่มีอยู่ (deferred VAT
  ตามเคสที่ระบบรองรับแล้ว — ดู DOCUMENT_FLOW.md §2.3a)

### 6.3 Edge cases (ตัดสินใจแล้ว)

| เคส | นโยบาย |
| --- | --- |
| ขายบริษัทออกจากเครือ | detach: `BillingAccountId = null` → ได้ subscription ตัวเอง; **usage เดือนเก่าอยู่บิลกลุ่มเดิม** (stamp ไว้แล้ว ไม่ย้ายย้อน) |
| เข้ากลุ่มกลางเดือน | usage นับเข้าบิลกลุ่มตั้งแต่วัน attach — ไม่ prorate ย้อน |
| กลุ่มค้างจ่าย | ระงับทั้งกลุ่มหลัง grace 7 วัน — แจ้งเตือน**ทุกบริษัทย่อย** ไม่ใช่แค่ holding (คนทำงานจริงอยู่ที่ย่อย) |
| ย่อยขอจ่ายเอง | มีอยู่แล้วใน resolution order — company subscription ชนะ account |
| บริษัทอยู่ 2 กลุ่ม | **ไม่อนุญาต** — FK เดี่ยว (หลายกลุ่ม = โควตา/บิลกำกวม) |
| retry ยิง API ซ้ำ | `IdempotencyKey` unique ต่อ client → event เดียว เก็บเงินครั้งเดียว |

---

## 7. ผลิตภัณฑ์ Connected (API) — สรุปสถาปัตยกรรม

- **ไม่แยกระบบ** — เครื่องยนต์ (OCR/matching/tax/AI) + ข้อมูล + billing อยู่ใน NextAcc;
  แยกเมื่อไรได้ที่ระดับ **deployment** (โค้ดเดิม อีก instance เปิดเฉพาะ `/api/v1`)
  ไม่ใช่ระดับ codebase. ต้นแบบที่พิสูจน์แล้ว: TakeTime integration
- **`/api/v1/*`** — versioned public API: `ocr/scan`, `bank/statements`, `documents`,
  webhooks; งานช้าเป็น async (รับงานคืน `jobId` → แจ้งผลทาง webhook) ห้ามให้ลูกค้า polling
- **Connector ราย ERP** (`IErpConnector`) เป็นปลั๊กแปลง format เท่านั้น — แกนรู้จักแค่ DTO
  ของเรา; เริ่มจาก "ลูกค้ายิงหาเราเอง" ก่อน ค่อยสร้าง connector สำเร็จรูปเมื่อมี demand จริง
- **เอกสารจริงอยู่ระบบไหน** — ต่อ 1 Company เลือกได้อย่างเดียว: NextAcc เป็นตัวจริง
  (เราออกเลข gap-free/ยื่นภาษี) หรือ ERP เขาเป็นหลัก (เราเป็นเงา ประมวลผลอย่างเดียว)
  — ปนกัน = เลขเอกสาร/ภ.พ.30 ชนแน่นอน
- **PDPA**: ลูกค้า Connected ฝากข้อมูลมาประมวลผล → เราเป็น **Data Processor** ต้องมี DPA,
  retention ของ payload ที่ฝาก (เช่นรูป OCR ลบใน 90 วัน), DSR ครอบข้อมูลชุดนี้
- **กฎเหล็ก #1 ยังบังคับเต็ม** — ทุก endpoint ที่แตะ AI ผ่าน orchestrator + kill-switch;
  ลูกค้า API ได้ local-first อัตโนมัติ = ต้นทุนต่อ transaction ลดเรื่อย ๆ ราคาขายคงที่

### 7.1 Self-service onboarding 📋 — ลูกค้าสร้างการเข้าถึงเองครบวงจร ไม่ต้องรอ admin

```
สมัคร (User เดิมของระบบ)
 → สร้าง BillingAccount (ชื่อกลุ่ม, ข้อมูลออกใบกำกับ)
 → เพิ่ม Company (Connected) + สาขาถ้ามี
 → เลือกฟีเจอร์ (ติ๊กจาก ApiFeature ที่ IsPublished — เห็นราคา/วิธีคิดเงินก่อนเปิด)
 → เลือกระบบบัญชีที่จะเชื่อม (ConnectorType + คู่มือเฉพาะ ERP นั้น)
 → ได้ API key (sandbox ก่อนเสมอ) → ทดสอบ → เติมเครดิต → สลับ production
```

- ทุกขั้นทำเองใน portal `/connect` — admin เข้ามาเกี่ยวเฉพาะ (ก) กำหนดราคา/วิธี
  คิดเงินใน `ApiPricingPlan` (ข) อนุมัติ Postpaid/วงเงิน (ค) ปิด `ApiFeature`
  ทั้งระบบเมื่อจำเป็น
- **ราคาที่โชว์ตอนลูกค้ากดเปิดฟีเจอร์ = แผนที่ active ณ วันนั้น** และการกดเปิดถูก
  audit (`CompanyFeature.EnabledBy/At`) — กันข้อพิพาท "ไม่เคยเปิด/ไม่รู้ราคา"
- ฟีเจอร์ที่ปิดอยู่ = endpoint ตอบ 403 + **ไม่เกิด UsageEvent** — เปิด/ปิดคือ
  สวิตช์เงินจริง ไม่ใช่แค่ซ่อนเมนู

### 7.2 ฟีเจอร์กลาง = ระบบเก่งขึ้นเรื่อย ๆ (สองชั้น — ห้ามสับสนกัน)

- **ชั้น per-tenant**: feedback จากทุก call (OCR แก้ field, ยืนยัน bank match)
  เทรน distillation model **ของบริษัทนั้น** — ข้อมูลลูกค้าไม่ข้าม tenant (PDPA;
  `AiResponseCache`/`AiSuggestionFeedback` tenant-isolated อยู่แล้ว)
- **ชั้นกลางที่แชร์ได้**: seed corpus, `SystemOcr*` mappings, rule resolver,
  prompt/โค้ดที่แก้จาก edge case ที่ลูกค้าเจอ — ยิ่งมีลูกค้า Connected มาก
  ชั้นนี้ยิ่งแข็งให้ทุกคนโดยไม่แตะข้อมูลใคร
- ผลเชิงธุรกิจ: local ตอบแทน AI มากขึ้น → ต้นทุน/transaction ลด → ราคาขายคงที่
  → margin โตเอง (เหตุผลที่คิดเงินตาม "งานสำเร็จ" ไม่ใช่ token)

## 8. Portal `/connect` 📋

- โฟลเดอร์ใหม่ `wwwroot/connect/` — **อยู่ใน deployment เดียวกัน** (แบบ `/admin`)
- **กฎเหล็ก: ยิงได้เฉพาะ `/api/v1` สาธารณะ ห้ามแตะ endpoint ภายใน ห้ามมีข้อมูลของตัวเอง**
  → dogfooding (เราคือลูกค้ารายแรกของ API ตัวเอง) + วันหลังยกไปโฮสต์แยกได้ทั้งโฟลเดอร์
- โครงหน้า = โครงข้อมูล:
  ```
  /connect (login User เดิม → role AccountAdmin)
   ├─ ภาพรวมกลุ่ม: usage ทุกบริษัท · บิลค้าง · เครดิตคงเหลือ · สถานะ key
   ├─ [บริษัท] usage รายสาขา/ฟีเจอร์ · API keys · webhook · log · เอกสารที่สร้างผ่าน API
   │           · **เลือกฟีเจอร์** (เปิด/ปิด CompanyFeature เห็นราคาก่อนเปิด)
   │           · **เลือก connector** (ConnectorType + config + คู่มือ ERP)
   ├─ onboarding wizard (§7.1) — สร้าง account/บริษัท/key ครบวงจรด้วยตัวเอง
   └─ บิล & ชำระเงิน (ระดับ account) · ซื้อเครดิต · ประวัติ
  ```
- ฝั่ง admin (`/admin`): หน้าใหม่ "ฟีเจอร์ & ราคา API" — CRUD `ApiFeature` +
  `ApiPricingPlan` (วิธีคิดเงิน 4 แบบ, ราคา, free quota, tier, วันมีผล) 📋

### 8.1 การเข้าถึงผ่านโดเมน 📋 — subdomain ให้ฟรี / โดเมนตัวเองก็ชี้เข้าได้

ยกแบบมาจากของที่มีจริงแล้วใน CMS (`CmsSite.Subdomain`/`CustomDomain` +
`SiteDomain` + `DomainType` — `CmsSite.cs:17,171`) — pattern พิสูจน์แล้ว ไม่ออกแบบใหม่:

```csharp
public class AccountDomain : BaseEntity          // ผูกระดับ BillingAccount
{
    public Guid BillingAccountId;
    public string Domain;                        // "abcgroup.nextacc.app" | "erp.abcgroup.co.th"
    public DomainType DomainType;                // Subdomain | CustomDomain
    // CustomDomain ต้องพิสูจน์ความเป็นเจ้าของก่อนใช้ — กัน host-header hijack:
    public string VerificationToken;             // ลูกค้าตั้ง DNS TXT ตาม token นี้
    public DateTime? VerifiedAt;                 // null = ยังใช้ไม่ได้
    public bool IsPrimary;
}
```

- **Subdomain**: ตั้งเองใน portal ได้ทันที (`{slug}.nextacc.app` — ตรวจ slug ซ้ำ/คำสงวน)
- **Custom domain**: ลูกค้าเพิ่มโดเมน → ระบบให้ TXT record → ตั้ง DNS → กด verify →
  ใช้ได้ (TLS อัตโนมัติผ่าน reverse proxy/Let's Encrypt — งาน infra ไม่ใช่งานโค้ด)
- **Middleware resolve host → BillingAccountId** → portal ขึ้นแบรนด์ของ account นั้น
  (โลโก้/ชื่อ/สี — white-label ให้ partner ได้ในตัว); host ที่ไม่รู้จัก → หน้า
  กลาง `/connect` ปกติ. Cookie/JWT audience ผูกต่อ host กัน token ข้ามโดเมน

### 8.2 Authentication — ในระบบเอง + LINE + Microsoft 365

| วิธี | สถานะ | หมายเหตุ |
| --- | --- | --- |
| Email + password (ในระบบ) | ✅ | `AuthController` login/refresh/forgot/reset ครบ |
| Google / Facebook OAuth | ✅ | `POST /auth/sso` + `User.AuthProvider/AuthProviderId` (generic รองรับ provider เพิ่มโดยไม่แก้ schema) |
| **LINE Login** (OAuth2) | 📋 | คนละอย่างกับ LINE bot binding ที่มีแล้ว (`User.LineUserId` + `LineBindCode`) — แต่ login แล้ว map เข้า `LineUserId` เดิมได้เลย บัญชีเดียวทั้ง login และ bot |
| **Microsoft Entra ID (Office 365)** | 📋 | OIDC มาตรฐาน; ตลาดเดียวกับลูกค้า Dynamics พอดี — บริษัทที่ใช้ Dynamics มี M365 อยู่แล้วเกือบ 100% |

- `AuthProvider` เพิ่มค่า `"Line"`, `"Microsoft"` — โครงเดิมรองรับอยู่แล้ว
- **ตั้งค่าที่ระดับ BillingAccount**: `AllowedAuthMethodsCsv` — องค์กรบังคับได้ว่า
  user ใต้ account ต้อง login วิธีไหน (เช่น enterprise บังคับ O365 เท่านั้น
  ปิด password login) + `EnforceSsoForAccountUsers`
- **JIT provisioning**: login ผ่าน IdP ครั้งแรก → สร้าง User อัตโนมัติ →
  เข้ากลุ่มด้วย invite เท่านั้น (**ห้าม auto-join จาก email domain** — โดเมน
  อีเมลปลอมง่าย ไม่ใช่หลักฐานความเป็นสมาชิกองค์กร)
- ทุกวิธี login จบที่ JWT เดิมของระบบ → ชั้นสิทธิ์ (`CompanyUser`/`AccountAdmin`)
  ไม่ต้องรู้ว่า login มาจากไหน
- Governance: `AccountAdmin` จัดการกลุ่ม/บิล/key ได้ แต่**เปิดสมุดบัญชีบริษัทใดต้องมี
  `CompanyUser` ของบริษัทนั้น** — งบรวมไปทาง ConsolidationGroup (opt-in) เท่านั้น

---

## 9. แผน migrate จากปัจจุบัน (ลำดับทำจริง)

1. `BillingAccount` + `BillingAccountAdmin` + `Company.BillingAccountId/ParentCompanyId/CompanyKind`
   — migration สร้าง BillingAccount ห่อ `AccountSubscription.OwnerUserId` เดิม 1:1 อัตโนมัติ
   (owner เดิมกลายเป็น AccountAdmin คนแรก) **ลูกค้าเก่าไม่รู้สึกอะไรเลย**
2. เปลี่ยนสมอ `AccountSubscription` → `BillingAccountId` + จุดเดียวใน `CheckUsageLimitAsync`
3. `UsageEvent` + `ApiFeature`/`CompanyFeature`/`ApiPricingPlan` + rollup + หน้า usage
   + หน้า admin "ฟีเจอร์ & ราคา API"
4. `ApiClient` (ยกระดับ ExternalIntegration: scopes/branch/HMAC/sandbox/ConnectorType)
   + `/api/v1` area + onboarding wizard (§7.1)
5. Billing สิ้นเดือน → ใบแจ้งหนี้/ใบกำกับอัตโนมัติผ่าน pipeline เอกสารเดิม (2 โหมด)
6. Portal `/connect` + `AccountDomain` (subdomain → custom domain + verify) +
   LINE Login / Microsoft Entra ID (§8.1–8.2)
7. เปิด 2 ฟีเจอร์แรก: OCR→DTO, statement→matching; Connector Dynamics เมื่อมีลูกค้าจริง

## 10. การบ้านที่งอกจากดีไซน์ (ยังไม่ทำ)

- [ ] ตรวจ `TaxService` ว่ารายงาน ภ.พ.30 กรอง/แยกตามสาขาได้จริงครบไหม + gate
      `VatFilingConsolidated` (ยื่นรวมต้องมีอนุมัติ)
- [ ] `AiBudgetGuard` จาก global → ราย BillingAccount/Company (ผูกตาราง §4)
- [ ] `AiUsageDaily` เพิ่ม `CompanyId` (จากการวิเคราะห์ token accounting รอบก่อน)
- [ ] `AiSuggestionFeedback` เพิ่ม `UserId`/`ApiClientId`/`RequestSource` (ตรวจสอบ "ใครทำ")
- [ ] SLA + status page ก่อนเซ็นลูกค้า Connected รายแรก

---

_Last verified against codebase: 2026-08-07 (rev 3 — เพิ่ม §8.1 โดเมน (subdomain/custom_
_domain ยกแบบจาก CmsSite ที่มีจริง) + §8.2 auth (local/Google/FB ✅ · LINE/Microsoft 365 📋);_
_rev 2 — self-service onboarding §7.1,_
_ฟีเจอร์กลาง/การเรียนรู้ 2 ชั้น §7.2, ApiFeature/CompanyFeature/PricingMethod/ConnectorType) —_
_สถานะ: §3.1 ✅ ตรวจกับโค้ดแล้ว ·
§3.2, §4 (คอลัมน์ Account/ApiClient), §6, §7 (`/api/v1`), §8, §9 = 📋 ออกแบบ ยังไม่มีโค้ด_
