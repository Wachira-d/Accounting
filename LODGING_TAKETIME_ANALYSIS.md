# LODGING_TAKETIME_ANALYSIS.md — วิเคราะห์ TakeTime → โมดูลที่พักของ NextAcc

> ที่มา: `https://github.com/Ipsos-Dev-TH/TakeTime` (ASP.NET WebForms PMS ของรีสอร์ท Take Time BangPhra)
> อ่านอย่างเดียว · สกัดโดย subagent (Sonnet) แล้ว main agent ตัดสินใจว่าอะไรลอก/ไม่ลอก ·
> ผลลัพธ์ = โมดูล `Lodging*` (รอบ 124) ที่ **สร้างเว็บไซต์ประเภท "โรงแรม/ที่พัก" แล้วจองได้ทันที**

## 0. สรุปการตัดสินใจ (mapping TakeTime → NextAcc)

| ของใน TakeTime | ทำอย่างไรใน NextAcc | เหตุผล |
| --- | --- | --- |
| `Accommodation` (ห้อง/ที่พัก + `LimitWithPeople`) | `LodgingRoomType` (ประเภทห้อง · `PricingMode` PerUnit/PerPerson) + `LodgingUnit` (หมายเลขห้องจริง) | TakeTime ผูก 1 ห้อง = 1 แถว ไม่มี "ประเภทห้อง" ⇒ ตั้งราคาซ้ำทุกห้อง; แยกชั้นแล้วราคาตั้งครั้งเดียว ห้องจริง assign ทีหลัง |
| `Accommodation_RatePlan` (ช่วงเดือน × ประเภทวัน) + `Accommodation_Holiday` + `Accommodation_Price` (รายวัน) | `LodgingSeason` (ช่วงวัน · recurring · "แคบกว่าชนะ") + `WeekendMultiplier/WeekendDaysMask` + `LodgingRateOverride` (รายวัน: ราคา/ปิดขาย/allotment/ขั้นต่ำ) | ลำดับการคิดราคากำหนดชัดใน `LodgingPricingEngine` (ฐาน → แผนราคา → ฤดูกาล → สุดสัปดาห์ → override แทนที่ทั้งหมด) — TakeTime คืน `""` เมื่อไม่มี rate plan ตรงวัน (FormatException) |
| `DynamicPricingService` (7 multiplier — **dead code**) · `PricingSeasons`/`PricingRules` seed | เอามาเฉพาะ **ค่า seed** (สงกรานต์ ×1.50 · ปีใหม่ ×1.60 · high ×1.25 · low ×0.85 · weekend) เป็นฤดูกาล/ตัวคูณที่ตั้งค่าได้ · early-bird/last-minute/long-stay = เงื่อนไขของ **แผนราคา** (`Min/MaxAdvanceDays`, `MinNights`) | occupancy-based multiplier ไม่ทำ — ไม่เคยถูกเรียกใน TakeTime และอ้างคอลัมน์ที่ไม่มีจริง |
| Voucher (fixed price ต่อ rate plan group) / Affiliate discount | `LodgingRatePlan` (Base/Absolute/Multiplier/Delta · `IsRefundable` · `DepositPercent` · นโยบายยกเลิกต่อแผน) | โครง "แผนราคา" มาตรฐาน PMS ครอบทั้ง Non-refundable/รวมอาหารเช้า/long-stay; Promotion/Coupon engine ของ TakeTime ไม่มี table จริง จึงไม่ลอก (`PromoCode` เก็บไว้เป็นช่องแต่ยังไม่มี engine — ดู §2) |
| มัดจำคงที่ 500/1,000 (hardcode ID ห้อง VIP) + 50/หัว | `DepositPercent` + `DepositFixedAmount` + `DepositMin/Max` + override ต่อแผนราคา + `ConfirmWithoutDeposit` | ค่า default ที่ hardcode ในโค้ด = ต้องแก้โค้ดเมื่อเพิ่มห้อง (ข้อห้ามใน CLAUDE.md) |
| `Deposit_Vat_Recognition` (CHECKOUT/RECEIPT) | `DepositOutputVatDeferred` (default **false** = เกิด tax point ตอนรับเงิน §78/1) | TakeTime default CHECKOUT ซึ่งขัด §78/1 สำหรับบริการ — เราให้ default ถูกกฎหมายและตั้งค่าได้ |
| ใบเสร็จมัดจำ `IsDeposit` + `Deposit_Applied_Amount` guard + clearing ตอน checkout | `Document(Receipt, IsDeposit)` → ตอนเช็คเอาต์ `DepositAppliedAmount/Ref/DrivesJournal` บน TaxInvoice/Invoice (DocumentService ตัด 217xx + guard over-apply ให้แล้ว) | ใช้แกนมัดจำที่มีอยู่ — **โมดูลนี้ไม่ออกเลขเอกสารเอง** (TakeTime `SELECT TOP 1 … +1` ไม่มี lock ⇒ เลขซ้ำได้) |
| ยกเลิกคืนเงิน/ไม่คืนเงิน (พนักงานตัดสินเอง · `FORFEIT_INCOME 41220`) | `LodgingCancellationPolicy` ขั้นบันได (ตรึง snapshot ณ วันจอง) → ค่าปรับอัตโนมัติ: ส่วนคืน = `RefundDepositAsync` · ส่วนริบ = `RealizeDepositAsync(RevenueAccountCode = CancellationFeeAccountCode)` · no-show = `NoShowChargePercent` | TakeTime ไม่มีนโยบายอัตโนมัติ ⇒ ไม่สม่ำเสมอ/ข้อพิพาท; ของเราคำนวณจากกฎที่แขกเห็นตอนจอง |
| `Reservation_Product_Charges` (ROOM_CHARGE/IMMEDIATE · PENDING/PAID/CANCELLED · stock) | `LodgingFolioCharge` (Source Manual/Pos/GuestPortal/System · Status Pending/Paid/Cancelled) รวมเข้าใบเช็คเอาต์ | สต๊อกไม่ตัดที่ folio — ตัดตอนออกเอกสาร (ProductCode บนบรรทัด) ตามกลไกเดิมของ DocumentService |
| `Checkout_History` (DamageCharge/MissingItems/…) | `DamageCharge/DamageDescription` ใน `LodgingCheckOutRequest` → folio charge Source=System ก่อนออกบิล | ไม่แยกตาราง — ค่าเสียหายเป็นบรรทัดในใบกำกับ (ตรวจสอบได้จากเอกสาร) |
| `HousekeepingStatus` บน Accommodation (ใช้จริง) + `HousekeepingTasks` (schema ครบ **ไม่มี UI เรียก**) | `LodgingUnit.HousekeepingStatus` + `LodgingHousekeepingTask` (สร้างอัตโนมัติตอนเช็คเอาต์ · Pending→Assigned→InProgress→Completed→Verified) + แท็บแม่บ้านใน `lodging.html` | ต่อสายทั้งสองฝั่ง (ฝั่งเขียน+ฝั่งอ่าน) ตั้งแต่แรก — บทเรียน "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" |
| Guest Portal ผ่าน QR (12 หน้า) | หน้าการจองสาธารณะ `/reservation/{token}` (ดู · อัปโหลดสลิป · ยกเลิก · ส่งคำขอ housekeeping/ซ่อม/รูมเซอร์วิส/คอนเซียร์จ) — token 128-bit ต่อการจอง | ทำเฉพาะแกน (balance/requests); QR ต่อห้อง · แชท · loyalty · รีวิว = เฟสถัดไป (§2) |
| Payment slip + OCR verify (ปิดใช้จริง) | อัปโหลดสลิปผ่าน token → `PaymentSlipUrl` + ต่อเวลาถือห้อง 24 ชม. → พนักงานกด "ยืนยัน+รับมัดจำ" | OCR สลิปยังไม่ทำ (ใน TakeTime ก็ปิดอยู่) |
| Reschedule/Postpone + history | `RescheduleAsync` (คิดราคาใหม่ · มัดจำคงเดิม · ปลด unit ให้จัดใหม่ · บันทึก InternalNotes + AuditLog) | "postpone ไม่กำหนดวัน" ไม่ทำ — เปลี่ยนวันตรง ๆ แทน |
| Channel Manager/OTA (schema+service **ไม่เคยถูกเรียก** เขียนไปคอลัมน์ที่ไม่มี) | `LodgingReservationSource.Ota` + `SourceReference` (บันทึกมือ) | ไม่ทำ sync — ใน TakeTime ก็ไม่มีของจริง (STAAH mapping ด้วยมือ) |
| CRM/Loyalty 5 tier · dual-pool points | ไม่ทำในรอบนี้ | คนละโดเมน — ใช้ Contact + SiteCustomer ที่มีอยู่; loyalty เป็น backlog |
| ไม่มี Service charge | `ServiceChargePercent` + ผังบัญชีแยก | โรงแรมไทยนิยม 10% |
| `CheckInTime/CheckOutTime` ตั้งไว้แต่ไม่ enforce | `EarlyCheckInHours/Fee`, `LateCheckOutHours/Fee` (ตั้งค่า) + `MinAdvanceHours` (enforce ตอนจอง) | ค่าธรรมเนียม early/late ยัง**ไม่คิดอัตโนมัติ** — พนักงานเพิ่มเป็น folio charge |
| SQL injection (`CountReserved`) · race เลขเอกสาร · GET เปลี่ยนสถานะ | ไม่มี: EF parameterized · เลขจอง `pg_advisory_xact_lock` + FNV key · ทุกการเปลี่ยนสถานะเป็น POST | defect class ที่ CLAUDE.md ห้ามอยู่แล้ว |

## 1. สิ่งที่ NextAcc ทำ "เกิน" TakeTime (เพราะต่อกับบัญชีจริง)

- เลขจอง/เลขเอกสาร gap-free ข้าม instance · tax point มัดจำ §78/1 · ใบกำกับภาษีเต็มรูป §86/4 ในนามบริษัทแขก (ตรวจ mod-11)
- ราคาทุกตัวเลขคำนวณที่เซิร์ฟเวอร์ตัวเดียว (`LodgingPricingEngine` pure + เทสต์ 19 เคส) — หน้าเว็บ/หน้า front desk แสดงอย่างเดียว
- นโยบายยกเลิก **ตรึงเป็น snapshot บนการจอง** — แก้นโยบายทีหลังไม่กระทบใบที่จองไปแล้ว
- PII: เลขบัตร/พาสปอร์ตออกจากเซิร์ฟเวอร์แบบ mask เสมอ (`1-XXXX-XXXXX-XX-3`)
- ปล่อยห้องอัตโนมัติเมื่อหมดเวลาถือมัดจำ (ไม่ต้องมี background job — engine ไม่นับ hold ที่หมดอายุ + `ExpireHoldsAsync` ตอนค้นหา/แสดงรายการ)

## 2. ยังไม่ได้ทำ (backlog — เรียงตามคุณค่า)

1. **Promo code engine** — ช่อง `PromoCode` มีบนการจอง แต่ `DiscountAmount` = 0 เสมอ (ยังไม่มีตาราง/กฎ) — ต่อกับ `CmsCoupon` ที่มีอยู่ได้
2. **QR ต่อห้อง + พอร์ทัลแขกเต็ม** (แชท · loyalty · รีวิว · nearby) — ตอนนี้แขกเข้าถึงผ่านลิงก์ token ในอีเมล/หน้ายืนยันเท่านั้น
3. **ค่า early check-in / late check-out อัตโนมัติ** จาก `EarlyCheckInFee/LateCheckOutFee` (ตอนนี้เป็นค่าตั้งค่าที่ยังไม่มีใครอ่าน — ห้ามปล่อยไว้นาน: defect class "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้")
4. **POS → folio** (`LodgingChargeSource.Pos` มี enum แล้ว — ยังไม่มีปุ่ม "ชาร์จเข้าห้อง" ใน POS)
5. **OTA sync / channel manager** — เก็บ `SourceReference` ไว้แล้ว รอ connector จริง
6. **แจ้งเตือน LINE/SMS** ก่อนเช็คอิน + ขอรีวิวหลังเช็คเอาต์ (TakeTime `ReminderSent/FeedbackRequestSent`)
7. **Loyalty** — ทำเป็นโมดูลกลาง (POS/CMS/Lodging ใช้ร่วม) ไม่ผูกกับที่พักอย่างเดียว
8. **รายงาน**: occupancy/ADR/RevPAR รายเดือน · forecast — ตอนนี้มีแค่แดชบอร์ดวัน + รายได้ MTD
9. **Integration tests** กับ Postgres จริง (จอง → มัดจำ → เช็คเอาต์ → JE) — ตอนนี้มีแค่ pure tests ของ engine

---

# ภาคผนวก — รายงานสกัดจาก TakeTime (subagent, ยังไม่แก้ไข)

# TakeTime BangPhra — สกัด Feature การจองที่พัก + การจัดการที่พัก (สำหรับออกแบบ NextAcc)

> อ่านจาก `/home/user/ipsos-dev-th/taketime` (อ่านอย่างเดียว) ครอบคลุม: `Database/*.sql` (70+ ไฟล์), `SQL Scripts/*`, `Take Time BangPhra/Database/Migrations/*`, `Take Time BangPhra/SQL/*`, ทุกไฟล์ business-logic หลักที่ prompt ระบุ (`ReservationService`, `ReservationPriceCalculationService`, `AccommodationAvailabilityService`, `CheckoutService`, `RoomChargeService`/`RoomChargeDataAccess`, `HousekeepingService`, `GuestPortalService`, `ReservationDataAccess`), หน้าจอ `.aspx`/`.aspx.cs` ฝั่งลูกค้าและพนักงานทั้งหมดที่ระบุ, โฟลเดอร์ `Admin/Settings|Pricing|ChannelManager|Housekeeping|RoomService|GuestExperience|CRM|Maintenance|Notifications`, `Guest/*` ทั้งโฟลเดอร์ (14 หน้า), และ `Docs/*.md` ที่มีอยู่จริงทุกไฟล์ (5 ไฟล์) รวมถึง `Class/Integration/*` (AccountingSyncService/ApiClient/DataMapper — 11,156 บรรทัด กรีปแบบ targeted) เพื่อสกัดภาพการเชื่อมต่อบัญชี

**ส่วนที่ไม่ได้อ่านลึก** (นอกขอบเขต "การจอง+การจัดการที่พัก" หรือเป็นโมดูลรอง): `Admin/HR/*`, `Admin/Payroll/*`, `Admin/Leave/*`, `PayrollService.cs`/`LeaveService.cs`/`OTService.cs`/`EmployeeService.cs` (62KB/91KB/39KB/30KB), `AssetService.cs`, `WebAnalyticsService.cs`, `AIReviewAnalysisService.cs`/`AIKnowledgeService.cs`/`AIReportService.cs` (อ่านแค่ signature), `SignatureService.cs`, `ExcelCreator.cs`, `PDFA3U/*`, `Affiliate/BookBank`, `Affiliate/IDCard`, มาร์กอัป `.aspx` แบบเต็มบรรทัดของ `ReserveTable.aspx`/`DisplayReserve.aspx`/`Reservation_Confirmed.aspx`/`ReservationList.aspx`/`Product/Default.aspx` (อ่านเฉพาะ code-behind ส่วนต้น + คีย์เวิร์ด) — ทั้งหมดนี้ไม่กระทบความถูกต้องของ 10 หัวข้อที่ขอ

---

## 1. ภาพรวมโดเมน (entity หลักและความสัมพันธ์)

```
Customer (MobilePhone = PK ธรรมชาติ) ──┬── Address (ที่อยู่แยกตาราง, FK Address_ID)
                                        └── Customer_Type (บุคคล/นิติบุคคล)
        │ 1
        │
        ▼ N
   Reservation (ID) ──── Reservation_Accommodation (N:M) ──── Accommodation (ห้อง/ที่พัก)
        │                                                           │
        │                                                     Accommodation_RatePlan (ราคาตามเดือน+ประเภทวัน)
        │                                                           │
        │                                                     Accommodation_DayType (นิยามกลุ่มวัน)
        │                                                           │
        │                                                     Accommodation_Holiday (วันหยุดพิเศษ)
        │
        ├── Reservation_Items (N:M กับ Items = อุปกรณ์เช่า)
        ├── Reservation_Product_Charges (N:M กับ Product = POS/ของกิน — room charge)
        ├── Payment_History (ประวัติจ่ายเงินทุกงวด)
        ├── Payment_Slips (ไฟล์สลิปที่แนบ, แยกจาก Payment_History)
        ├── Account_Receipt (ใบเสร็จ/ใบกำกับภาษี) ── Account_Receipt_Detail (รายบรรทัด)
        ├── Reservation_Reschedule_History (ประวัติเลื่อน/เปลี่ยนวัน)
        ├── Checkout_History (ประวัติเช็คเอาท์ 1:N)
        ├── Guest_Room_Service_Orders / _Items (สั่งอาหาร/ของจาก portal)
        ├── Guest_Housekeeping_Requests, Guest_Concierge_Requests, Guest_Chat_Messages
        ├── Guest_Portal_Sessions (session หลัง scan QR)
        └── Loyalty_Transactions / Guest_Reviews (CRM)

Accommodation ── Room_QR_Codes (1:1) ── Guest_Portal_Sessions (ใช้ QR เข้า portal)
Accommodation ── RoomStatusHistory / Room_Status (housekeeping — คนละ table กับ column HousekeepingStatus บน Accommodation เอง)

Accounting_Sync_Queue / Accounting_Sync_Log / Accounting_Account_Mapping / Accounting_Integration_Config
  → เป็น "สะพาน" ไปยัง NextAcc ผ่าน AccountingApiClient (REST) — ครอบทุก entity ด้านบน
```

**สังเกตสำคัญ**: schema พื้นฐาน (`Reservation`, `Accommodation`, `Customer`, `Reservation_Accommodation`, `Reservation_Items`, `Account_Receipt`, `Account_Receipt_Detail`, `Items`, `Admin`) **ไม่มี `CREATE TABLE` อยู่ในเรพเลยสักที่** — grep `CREATE TABLE` ทั้งเรพ (113 จุด/33 ไฟล์) ไม่เจอเลย แปลว่าตารางเหล่านี้ถูกสร้างไว้ก่อนแล้ว (นอกเรพ, อาจผ่าน SSMS designer) แล้วโค้ด/migration ทุกไฟล์ใช้ `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` เพิ่มทีหลังเท่านั้น — schema ของตารางเหล่านี้ในเอกสารนี้จึงมาจากการไล่ column ที่ใช้จริงในโค้ด (query/insert) ไม่ใช่จาก DDL ต้นฉบับ (ระบุชัดต่อคอลัมน์ว่า "ไม่พบ DDL — สกัดจาก usage")

---

## 2. ตาราง Schema ที่พัก/การจอง

### 2.1 `Reservation` (ไม่พบ DDL ต้นฉบับ — สกัดจาก usage ทั้งหมด)

| คอลัมน์ | ชนิด (สังเกตจาก parameter binding) | Default/Nullable | ความหมาย | ที่มา |
|---|---|---|---|---|
| `ID` | bigint/int identity, PK | not null | เลขที่การจอง | `ReservationDataAccess.cs:517-525` |
| `Customer_MobilePhone` | nvarchar(20-30) | not null | FK → `Customer.MobilePhone` (key ธรรมชาติ ไม่ใช่ GUID) | `ReservationDataAccess.cs:43` |
| `CheckinDate` | date/datetime | not null | วันเช็คอิน | ทุกไฟล์ |
| `CheckoutDate` | date/datetime | not null | วันเช็คเอาท์**ตามแผน** (คนละความหมายกับ `CheckoutDate` ที่ migration 10 เพิ่มสำหรับ "เช็คเอาท์จริง" — ชื่อชนกัน ดู 2.5) | ทุกไฟล์ |
| `StayDays` | int | not null | จำนวนคืน | `ReservationDataAccess.cs:518` |
| `Status` | nvarchar(50) | not null | ค่าที่พบจริง (Thai string, ไม่ใช่ enum): `จองแล้ว` / `มัดจำแล้ว` / `เช็คอินแล้ว` / `เสร็จสิ้น` / `ยกเลิกคืนเงิน` / `ยกเลิกไม่คืนเงิน` / `ไม่มาเช็คอิน` / `ยกเลิกการเลื่อนวันเข้าพัก` — **มี `เช็คเอาท์แล้ว`/`เช็คเอ้าท์แล้ว` สองตัวสะกดปนกัน** (ดู §9) | `Class/ReservationService.cs:25`, `Class/RescheduleService.cs`, python-grep สแกนทั้งเรพ |
| `TotalPrice` | decimal(10,2) | not null default 0 | ยอดรวมทั้งบิล (รวม VAT ถ้า `Use_Vat=True`) | `ReservationDataAccess.cs:459` |
| `Deposit` | decimal(10,2) | not null default 0 | ยอดที่ชำระสะสม (ไม่ใช่แค่มัดจำ — ถูก reuse เป็น "total paid so far" ด้วย, ดู §9) | `ReservationDataAccess.cs:460`, `CheckoutService.cs:195-208` |
| `Remark` | nvarchar(500)? | null | หมายเหตุ | `ReservationDataAccess.cs:461` |
| `Reserve_By` | nvarchar | null | ผู้สร้างการจอง | `ReservationDataAccess.cs:519,522` |
| `Created_Date` | datetime | not null | วันที่สร้าง | `ReservationDataAccess.cs:511,519` |
| `NoCreateReceipt` | nvarchar('True'/'False' เป็น string ไม่ใช่ bit) | not null | ไม่ต้องออกใบเสร็จ | `ReservationDataAccess.cs:512,520` |
| `NoNameinReceipt` | nvarchar('True'/'False') | not null | ไม่ระบุชื่อในใบเสร็จ | `ReservationDataAccess.cs:513,520` |
| `IsPostponed` | bit | default 0 | สถานะ "เลื่อนวัน ยังไม่กำหนดวันใหม่" | `Database/PHASE8_Migration_01…:65` |
| `PostponedDate` | datetime | null | วันที่ถูกเลื่อน | `PHASE8_Migration_01:70+` |
| `RescheduleCount` | int | default 0 | จำนวนครั้งที่ถูกเลื่อน/เปลี่ยนวัน | `ReservationDataAccess.cs:564` |
| `CheckoutDate` (ตัวที่สอง) | datetime | null | วันเวลาที่เช็คเอาท์**จริง** | `PHASE1_Migration_10_Checkout_Status.sql:19-27` |
| `CheckoutBy_AdminID` | smallint | null, FK→Admin | ใครกดเช็คเอาท์ | `PHASE1_Migration_10:29-37` |
| `CheckoutNotes` | nvarchar(500) | null | หมายเหตุตอนเช็คเอาท์ | `PHASE1_Migration_10:39-47` |
| `FinalSettlementAmount` | decimal(10,2) | null | ยอดสุดท้ายรวมค่าเสียหาย | `PHASE1_Migration_10:49-57` |
| `BookingSource` | nvarchar(50) | null | ที่มาการจอง (channel) | `Migrations/001…:574-577` |
| `OTABookingCode` | nvarchar(100) | null | เลขจองจาก OTA | `Migrations/001…:580-583` |
| `ReminderSent` | bit | default 0 | ส่งแจ้งเตือนแล้วหรือยัง | `Migrations/001…:586-589` |
| `FeedbackRequestSent` | bit | default 0 | ส่งขอรีวิวแล้วหรือยัง | `Migrations/001…:592-595` |
| `DynamicPriceApplied` | bit | default 0 | เคยใช้ dynamic price หรือยัง (**คอลัมน์นี้มีแต่ engine ที่คำนวณเป็น dead code** ดู §9) | `Migrations/001…:598-601` |
| `PriceMultiplier` | decimal(5,2) | default 1.00 | ตัวคูณราคาที่ถูกใช้ | `Migrations/001…:604-607` |

### 2.2 `Accommodation` (ไม่พบ DDL — สกัดจาก usage)

| คอลัมน์ | ชนิด | Default | ความหมาย | ที่มา |
|---|---|---|---|---|
| `ID` | tinyint/int, PK | — | รหัสห้อง | ใช้ทั่วเรพ |
| `AccomName` | nvarchar(100) | — | ชื่อห้อง/ที่พัก | `Reserve.aspx.cs:150,199` |
| `Price` | decimal | — | ราคาฐานต่อคืน (หรือต่อคน ถ้า `LimitWithPeople=True`) | `ReservationService.cs:140` |
| `People` | int | — | จำนวนคนสูงสุด | `ReservationService.cs:141` |
| `Unit` | nvarchar | — | หน่วยนับ | `ReservationService.cs:142` |
| `LimitWithPeople` | nvarchar('True'/'False' string) | — | true = คิดราคาต่อคนต่อคืน (เช่น เต็นท์แคมป์ปิ้ง), false = คิดราคาต่อห้องต่อคืน | `AccommodationAvailabilityService.cs:81-83` |
| `Status` | bit/int(1/0) | — | เปิดใช้งานห้องหรือไม่ | `ReservationService.cs:70` |
| `Remark` | nvarchar | — | หมายเหตุ | `ReservationService.cs:145` |
| `OrderID`/`orderID` | int | — | ลำดับการแสดงผล | `ReservationDataAccess.cs` (ORDER BY), `001…:664` |
| `HousekeepingStatus` | nvarchar(50) | `'VACANT_CLEAN'` | สถานะแม่บ้าน (ใช้จริงในการผลิต — ดู §6) | `Migrations/001…:222-226` |
| `MinOccupancyRate` | decimal(5,2) | 0.85 | (สำหรับ dynamic pricing — **ไม่มีจุดอ่านค่านี้จริงในโค้ด**) | `Migrations/001…:611-615` |
| `MaxOccupancyRate` | decimal(5,2) | 1.50 | เช่นเดียวกัน — dead | `Migrations/001…:617-621` |
| `LastCleanedAt` | datetime | null | — | `Migrations/001…:623-627` |
| `LastInspectedAt` | datetime | null | — | `Migrations/001…:629-633` |

> **ยืนยันว่าไม่มีจริง**: คอลัมน์ `StandardOccupancy`, `MaxOccupancy`, `ExtraGuestPrice`, `TotalRooms`, `Min_Dynamic_Price`, `Max_Dynamic_Price` ที่ `Reserve.aspx.cs:6246-6258` (`CalculateAccomPriceWithExtraGuests`) และ `DynamicPricingService.cs:117-141,161` อ้างถึง — grep `CREATE TABLE`/`ALTER TABLE` ทั้งเรพหาไม่เจอสักคอลัมน์ (ดูหลักฐานใน §9)

### 2.3 `Reservation_Accommodation` (junction table)

| คอลัมน์ | ความหมาย | ที่มา |
|---|---|---|
| `Reservation_ID` | FK → Reservation | `ReservationDataAccess.cs:339` |
| `Accommodation_ID` | FK → Accommodation | `ReservationDataAccess.cs:339` |
| `Amount` | จำนวนคน (ถ้า LimitWithPeople) หรือจำนวนหน่วย | `ReservationDataAccess.cs:339` |
| `Price` | ราคาที่ snapshot ไว้ตอนจอง (ต่อคน/ต่อห้อง ต่อคืน — ไม่ใช่ยอดรวม) | `ReservationDataAccess.cs:339`, คอมเมนต์ `Reserve.aspx.cs:451-454` |
| `Use_Coupon` | ระบุว่าบรรทัดนี้ใช้คูปอง/affiliate หรือไม่ | `ReservationDataAccess.cs:339` |

### 2.4 `Reservation_Items` (อุปกรณ์เช่า, junction)

`Reservation_ID`, `Items_ID`, `Amount`, `Price` — `ReservationDataAccess.cs:358-360`. ตาราง `Items` เอง: `ID`, `ItemName`, `Price`, `Amount` (คงเหลือ), `LimitWithAmount`, `OrderID`, `Status` (`ReservationService.cs:102-114`)

### 2.5 `Checkout_History` (DDL เต็มมี — `PHASE1_Migration_10_Checkout_Status.sql:76-99`)

`ID` bigint PK · `Reservation_ID` bigint FK · `CheckoutDate` datetime default GETDATE() · `CheckedOutBy_AdminID` smallint FK→Admin · `FinalAmount` decimal(10,2) · `PaymentStatus` nvarchar(20) (`PAID`/`PARTIAL`/`UNPAID`) · `RoomDamage` bit default 0 · `DamageDescription` nvarchar(500) · `DamageCharge` decimal(10,2) default 0 · `MissingItems` bit default 0 · `MissingItemsDescription` nvarchar(500) · `MissingItemsCharge` decimal(10,2) default 0 · `KeyReturned` bit default 0 · `CleaningStatus` nvarchar(20) (`GOOD`/`DIRTY`/`VERY_DIRTY`) · `GuestSatisfaction` tinyint 1-5 (CHECK constraint) · `Notes` nvarchar(1000) · `CreatedDate` datetime

### 2.6 `Reservation_Product_Charges` (DDL เต็มมี — `PHASE5_Migration_01_Room_Charge_System.sql:23-95`)

`ID` bigint PK · `Reservation_ID` int FK · `Product_ID` int FK · `Product_Name`/`Product_Barcode`/`Category_ID` (snapshot) · `Quantity` decimal(10,2) CHECK>0 · `UnitPrice` decimal(10,2) · `TotalAmount` decimal(10,2) CHECK≥0 · `Status` nvarchar(20) default `PENDING` CHECK IN (`PENDING`,`PAID`,`CANCELLED`) · `ChargeType` nvarchar(20) default `ROOM_CHARGE` CHECK IN (`ROOM_CHARGE`,`IMMEDIATE`,`PRE_BOOKING`) · `IsPaid` bit default 0 · `Receipt_ID` nvarchar(50) null · `PaymentDate` datetime null · `StockDeducted` bit default 1 · `StockReturned` bit default 0 · `OriginalStock` int null (audit) · `ChargedDate` datetime default GETDATE() · `ChargedBy_AdminID`/`CancelledBy_AdminID` smallint FK→Admin · `CancelledDate`/`CancelReason`/`Notes`

### 2.7 `Payment_History` (DDL เต็มมี — `PHASE1_Migration_09_Payment_Tracking.sql:16-39`)

`ID` bigint PK · `Reservation_ID` bigint FK ON DELETE CASCADE · `PaymentDate` datetime default GETDATE() · `PaymentAmount` decimal(10,2) CHECK>0 (ยกเว้น type=REFUND) · `PaymentType` nvarchar(50) (`DEPOSIT`/`ADDITIONAL`/`FINAL`/`REFUND`) · `PaymentMethod` nvarchar(50) (`CASH`/`TRANSFER`/`CARD`/`OTHER`) · `Receipt_ID` nvarchar(50) FK→Account_Receipt · `PaymentSlip_ID` bigint FK→Payment_Slips · `RemainingBalance` decimal(10,2) · `PaidBy_CustomerPhone` nvarchar(20) · `ProcessedBy_AdminID` smallint FK→Admin · `Notes` nvarchar(500) · `Status` nvarchar(20) default `COMPLETED` (`COMPLETED`/`PENDING`/`CANCELLED`/`REFUNDED`) · `CreatedDate`/`UpdatedDate`

### 2.8 `Payment_Slips` (DDL เต็มมี — `Database/PHASE1_Migration_05b_Payment_Slips_SIMPLE.sql:43-64`)

`ID` bigint PK · `Account_Receipt_ID` bigint null · `Reservation_ID` bigint NOT NULL FK · `SlipFileURL` nvarchar(500) NOT NULL · `FileName` nvarchar(255) NOT NULL · `FileType` nvarchar(50) · `FileSize` int · `UploadedDate` datetime default GETDATE() · `UploadedBy_ID` smallint · `UploadedBy_CustomerPhone` nvarchar(20) · `IsVerified` bit default 0 · `VerifiedBy_ID` smallint · `VerifiedDate` datetime · `VerificationStatus` nvarchar(20) default `PENDING` · `RejectionReason` nvarchar(500) · `Notes` nvarchar(1000) · `IsActive` bit default 1 · `Status` tinyint default 1 — ขยายด้วย OCR columns ใน `PHASE2_Migration_03_OCR_Slip_Verification.sql`: `OCR_Amount`, `OCR_Status`, `OCR_Confidence`, `OCR_RawText`, `OCR_ProcessedDate`, `OCR_ErrorMessage` (`Docs/OCR_SLIP_VERIFICATION_README.md:54-60`)

### 2.9 ระบบราคา (Rate Plan) — ไม่พบ DDL, สกัดจาก `Reserve.aspx.cs:6282-6461` (`AccomPrice()`)

- **`Accommodation_RatePlan`**: `ID`, `Accom_ID` (FK), `Start_Month`, `End_Month` (รองรับช่วงข้ามปี เช่น เดือน 11→2), `Price`, `DayType_Name_ID` (FK→`Accommodation_DayType`)
- **`Accommodation_DayType`**: `ID`, `Day` (comma-separated เช่น `"Mon,Tue,Wed,Fri,Sat,Sun,Holiday"`)
- **`Accommodation_Holiday`**: `Holiday_Date`, `Status` — วันหยุดพิเศษที่ทำให้ day-type กลายเป็น `Holiday`
- **`Accommodation_Price`**: `Accommodation_ID`, `Date`, `Price` — ราคาพิเศษรายวัน override rate plan (ใช้ใน `ReservationPriceCalculationService.cs:167-173` และ `DynamicPricingService.cs:99-104`)
- **`Accommodation_RatePlan_Group`** / **`Voucher_RatePlan_Group`**: จัดกลุ่ม rate plan สำหรับผูก voucher

### 2.10 ส่วนลด/คูปอง/Affiliate — **สองระบบคนละยุคที่ไม่เชื่อมกัน** (ดูรายละเอียด §9)

- **ระบบที่ใช้งานจริง** (`Reserve.aspx.cs:6364-6439`): `Session["UseCoupon"]` = `"Affiliate"` หรือ `"Voucher"` → query `Affiliate_Member` + `Affiliate_Discount` + `Affiliate_Discount_RatePlan` (ส่วนลดตายตัวต่อ rate plan) หรือ `Voucher` + `Voucher_RatePlan_Group` + `Accommodation_RatePlan_Group` (voucher เป็น fixed-price ต่อ rate plan, มี `Expired_Date`, `Used_Status`)
- **ระบบที่เขียนไว้แต่ไม่มี table รองรับ** (`ReservationPriceCalculationService.cs:280-471`): อ้างตาราง `Promotions` (คอลัมน์ `Promotion_Name`,`Discount_Type`,`Discount_Value`,`Min_Stay_Days`,`Max_Discount_Amount`,`Priority`,`Is_Active`) + `Promotion_Accommodations` + `Coupons` (คอลัมน์ `Coupon_Code`,`Usage_Limit`,`Used_Count`,`Min_Order_Amount`) — **ไม่มี `CREATE TABLE` เหล่านี้ที่ไหนในเรพเลย** และ table ชื่อ `Promotions` ที่มีจริง (`Migrations/004_CreatePromotionsTable.sql:11-24`) เป็นคนละเรื่อง (ป้ายโฆษณาหน้าแรก: `Title`,`Image_Url`,`Show_On_Homepage`) — ชนชื่อกันโดยไม่มีความสัมพันธ์ใด ๆ

### 2.11 CRM / Loyalty (DDL เต็มมี — `Database/07_CRM_System_Schema.sql`, ปรับปรุงโดย `PHASE13_Migration_02_Loyalty_Restructure.sql`)

- **`Guest_Preferences`**: `Customer_MobilePhone`, `PreferenceType`(ROOM/PILLOW/BED/FOOD/ALLERGY/ACTIVITY/SPECIAL_REQUEST), `PreferenceKey`, `PreferenceValue`, `Priority`(1-4) — `07_CRM…:19-44`
- **`Guest_Notes`**: `NoteType`(GENERAL/VIP/COMPLAINT/COMPLIMENT/BEHAVIOR/HEALTH/WARNING), `IsImportant`, `IsPrivate`, `Reservation_ID` — `07_CRM…:50-78`
- **`Loyalty_Tiers`**: `TierName`,`MinPoints`,`PointsMultiplier`(1.0-3.0x),`DiscountPercent`,`TierColor` — seed 5 tier (ทั่วไป 0pt / เงิน 1,000pt +5% / ทอง 5,000pt +10% / แพลทินัม 15,000pt +15% / VIP 50,000pt +20%) — `07_CRM…:88-108`
- **`Customer_Loyalty`**: `CurrentTier_ID`,`TotalPoints`,`AvailablePoints`,`LifetimePoints`,`TotalSpend`,`MemberSince` — ขยายด้วย `YearlyPoints`,`PointsYear`,`LastReviewDate` (dual-pool model: yearly pool ใช้ตัดสิน tier, available pool ใช้แลกของรางวัลได้โดยไม่กระทบ tier) — `PHASE13_Migration_02…:20-38`
- **`Loyalty_Transactions`**: `TransactionType`(EARN/REDEEM/EXPIRE/ADJUSTMENT/BONUS), `Points`, `BalanceAfter`, `Reservation_ID`, `ExpiryDate` — `07_CRM…:146-175`
- **`Loyalty_Rewards`**: `Category`(DISCOUNT/FREE_NIGHT/UPGRADE/GIFT/VOUCHER/SERVICE), `PointsCost`, `MonetaryValue`, `MinTierRequired`, `ValidityDays` — `07_CRM…:181-217`
- **`Loyalty_Redemptions`**: `RedemptionCode`(unique), `Status`(PENDING/FULFILLED/CANCELLED/EXPIRED) — `PHASE13_Migration_02…:61-88`
- **`Guest_Reviews`**: (schema ต่อจากบรรทัด 227) ผูกกับ `Reservation_ID`

---

## 3. กติกาธุรกิจที่โค้ดทำจริง

### 3.1 การคำนวณราคาห้องพัก

**สูตรหลัก** (`Reserve.aspx.cs` ในเมธอด `GridView1_SelectedIndexChanged`-family, บรรทัด ~400-546):

```
ถ้า LimitWithPeople == "True":   # ห้องคิดตามหัวคน (เช่น เต็นท์แคมป์)
    PriceAccom += basePricePerPerson × numberOfGuests × numberOfNights    # line 448
    DepositAmount += 50 × numberOfGuests                                  # line 407 (มัดจำ 50฿/หัว)

else:                              # ห้องคิดตามห้อง
    PriceAccom += AccomPrice(id, date_i) สำหรับทุกคืน i (รองรับ rate ต่างกันรายวัน)   # line 508,527,538
    DepositAmount += 1000  ถ้า Accommodation.ID ∈ {21,22,23,18}                        # line 465-467 (ห้อง VIP)
    DepositAmount += 500   สำหรับห้องอื่น                                             # line 471 (ค่า default)
```

> **มัดจำเป็น "จำนวนเงินคงที่ต่อห้อง" ไม่ใช่เปอร์เซ็นต์ของยอดจอง** — ผิดกับสมมติฐานทั่วไปของระบบโรงแรม และ ID ห้อง VIP (21,22,23,18) ถูก **hardcode** ไว้ในโค้ด ไม่ใช่ config (`Reserve.aspx.cs:465`)

**`AccomPrice(AccomID, date)`** (`Reserve.aspx.cs:6282-6461`) — สูตร rate-plan:
1. loop `Accommodation_RatePlan` ของห้องนั้น หา record ที่ `date.Month` อยู่ในช่วง `[Start_Month, End_Month]` (รองรับช่วงข้ามปี เช่น 11→2)
2. join `Accommodation_DayType` เพื่อดูว่า `date.DayOfWeek` (เช่น `mon`,`tue`) ตรงกับ `Day` ที่ตั้งไว้หรือไม่ **หรือ** ถ้ามีคำว่า `holiday` ใน `Day` และวันนั้นอยู่ใน `Accommodation_Holiday` → ใช้ price ของ record นั้น
3. ถ้า `Session["UseCoupon"] == "Affiliate"` → ราคา = `RatePlan.Price − Affiliate_Discount.Discount_Amount`
4. ถ้า `Session["UseCoupon"] == "Voucher"` → ราคา = `Voucher.PriceTo` (fixed price, ไม่ใช่ discount) ถ้า voucher ยังไม่ใช้ (`Used_Status`) และยัง `Expired_Date >= checkout`
5. ถ้าไม่เจอ rate plan ที่ match วันนั้นเลย → **`Price = ""`** (ไม่มี fallback ราคา default — เสี่ยง `FormatException`เมื่อแปลงเป็นตัวเลขที่จุดเรียกใช้)

**Extra-bed/extra-guest pricing แบบขั้นบันได** — มีเขียนไว้ (`CalculateAccomPriceWithExtraGuests`, `Reserve.aspx.cs:6239-6280`) แต่ **ไม่เคยถูกเรียกที่ไหนเลย** และอ้างคอลัมน์ (`StandardOccupancy`,`MaxOccupancy`,`ExtraGuestPrice`) ที่ไม่มีจริงในฐานข้อมูล — 100% dead code

**Dynamic pricing แบบ multiplier** (`DynamicPricingService.cs:28-85`) — สูตรตั้งใจไว้:
```
FinalPrice = clamp(BasePrice × Occupancy × Season × Demand × DayOfWeek × LeadTime × (1 − LengthOfStayDiscount),
                    Min_Dynamic_Price, Max_Dynamic_Price)
```
โดย occupancy multiplier 0.8-1.5x ตามช่วง occupancy 6 ระดับ, weekend +20%, last-minute (≤1 วัน) +30%, early-bird (60+ วัน) -15%, stay 14+ คืน -15% — **สูตรทั้งหมดนี้ไม่เคยถูกเรียกจริง** (`new DynamicPricingService(` พบเฉพาะในไฟล์ตัวเอง) หน้า `Admin/Pricing/DynamicPricing.aspx.cs:56-72` มีสูตรคนละอันที่ง่ายกว่ามาก (occupancy>80%→+20%, <40%→-15%) และเป็นแค่ "คำแนะนำ" แสดงผล ไม่ได้เขียนราคาจริง

**VAT** — VAT-inclusive pricing (ราคาที่ตั้งรวม VAT แล้ว): `priceExcludeVat = totalAmount × 100/107`, `vat = totalAmount − priceExcludeVat` (`Class/ReceiptService.cs:302-307`) เปิด/ปิดผ่าน `Business_Info.Use_Vat` (`ReceiptService.cs:297`) — ไม่มี service charge (10%) ใด ๆ ในระบบทั้งหมด (grep ทั้งเรพไม่พบ)

### 3.2 การตรวจความว่าง (Availability)

`AccommodationAvailabilityService.CheckAvailability()` (`Class/AccommodationAvailabilityService.cs:48-157`):
```
ถ้า LimitWithPeople == false:   # ห้องปกติ
    conflict ถ้ามี reservation ใดที่ Accommodation_ID เดียวกัน, Status ∉ {ยกเลิก,เสร็จสิ้น,ไม่มาเช็คอิน},
              และช่วงวันที่ทับกัน (newCheckin < R.Checkout AND newCheckout > R.Checkin)   # line 111-116
    → ถ้าเจอ conflict = จองไม่ได้เลย (ห้ามซ้ำ 100%)

ถ้า LimitWithPeople == true:    # ห้องนับหัว
    currentOccupancy = SUM(Amount) ของทุก reservation ที่ทับช่วงวันเดียวกัน   # line 132-136
    ถ้า currentOccupancy + requestedPeople > maxCapacity (Accommodation.People) → จองไม่ได้
```
มี `CheckAvailabilityByDate()` (line 189-217) ตรวจทีละวันแยก (สำหรับกรณีจองข้ามคืนที่ห้องว่างไม่เท่ากันทุกวัน) — **ไม่มี overbooking buffer ใด ๆ** (ไม่มีแนวคิด "เผื่อ X% overbook" เหมือนโรงแรมทั่วไป) — เป็นการเช็ค hard-block แบบ real-time query เท่านั้น ไม่มี lock กันแข่งกันจอง (race condition) ระดับ DB (ไม่พบ `pg_advisory_lock`-equivalent หรือ `WITH (UPDLOCK)` ใน query เหล่านี้)

`ReservationDataAccess.CheckAccommodationAvailability()` (`ReservationDataAccess.cs:176-214`) เป็นเวอร์ชันคู่ขนานที่ทำตรรกะเดียวกันแต่คืนแค่ DataTable ของ conflict ดิบ (ไม่มี message-building) — **สองเมธอดทำ overlap-check เหมือนกันคนละที่** (เสี่ยง drift ถ้าแก้อันหนึ่งแล้วลืมอีกอัน)

`ReservationService.GetAvailableAccommodations()` (`Class/ReservationService.cs:63-94`) ใช้ query แบบ `NOT IN (subquery)` ที่กรองด้วย `AND a.LimitWithPeople = 'False'` ผสมอยู่ใน subquery — ทำให้ห้อง `LimitWithPeople=True` **ไม่เคยถูกกรองออกเลย** (ปรากฏว่างเสมอไม่ว่าจะเต็มหรือไม่) ต้องใช้ `GetAvailableAccommodationsForDate()` (line 132-175) ที่แก้ตรรกะแล้วแทน — เป็นตัวอย่าง "สองเมธอดคู่ขนานที่คนละคุณภาพ"

### 3.3 มัดจำ (Deposit)

- ไม่มี "% มัดจำมาตรฐาน" — เป็นค่าคงที่ต่อห้อง (500/1,000 บาท) + 50 บาท/หัว สำหรับห้องนับคน (§3.1)
- **นโยบายตรวจยอดตามโหมด** (สรุปจาก `Docs/PAYMENT_VALIDATION_POLICY.md`, ยืนยัน code จริงตาม):

| โหมด | เช็คยอดอย่างไร | `IsDeposit` | ไฟล์:บรรทัด |
|---|---|---|---|
| Reserve (จองใหม่) | ยอดโอน ≥ 80% ของยอดมัดจำขั้นต่ำ (bypass ได้ถ้า `Session["permission"]=="True"` คือ staff/owner) | true | `Reserve.aspx.cs:5378-5399` |
| Edit + มัดจำเพิ่ม | ไม่เช็คยอดรวมเลย (จ่ายเท่าไหร่ก็ได้) | true | `Reserve.aspx.cs:1416-1433` |
| CheckIn | ต้อง = ยอดคงเหลือเป๊ะ (ล็อกช่องแก้ไข) | false | `Reserve.aspx.cs:1884-1890` |
| CheckOut | ต้อง = 100% (remaining ≤ 0) — ปิดปุ่มถ้าไม่ครบ | — | `Checkout.aspx.cs:123-144` |
| RentMore (เช่าของเพิ่ม) | ต้อง = ยอดของเช่าเพิ่มเท่านั้น | false | `Reserve.aspx.cs:1768-1790` |

- **`Reservation.Deposit` ถูก reuse เก็บ "ยอดที่จ่ายสะสมทั้งหมด" ไม่ใช่แค่มัดจำ** — ตอน checkin `Deposit = TotalPrice` (`ReservationDataAccess.cs:478`) — ชื่อ field สื่อความหมายผิด (ญาติของ defect class ใน CLAUDE.md เรื่อง "ห้าม reuse field ผิดความหมาย")
- **Vat recognition point ของมัดจำ**: ตั้งค่าได้ผ่าน config `Deposit_Vat_Recognition` (`CHECKOUT` default หรือ `RECEIPT`) — อ้างอิงประมวลรัษฎากร ม.78/1 (ธุรกิจบริการ ต้องรับรู้ VAT ตอนรับเงิน ไม่ใช่ตอนส่งมอบ) — `Database/PHASE12_Migration_13_Deposit_Vat_Recognition.sql:1-46`

### 3.4 ยกเลิกและคืนเงิน

- **ไม่มี policy อัตโนมัติแบบวันก่อนเข้าพัก** (เช่น "ยกเลิก ≥7 วันคืนเต็ม, <3 วันริบ") — grep ทั้งเรพหาคำว่า "วันก่อนเข้าพัก"/cancellation-by-days ไม่พบเลยสักจุด เป็น**การตัดสินใจโดยพนักงานล้วน ๆ** ผ่าน 2 ปุ่ม:
  - `CancelReservationWithRefund()` (`Class/ReservationService.cs:214-274`): `TotalPrice=0, Deposit=0, Status='ยกเลิกคืนเงิน'`, ลบ `Reservation_Accommodation`/`Reservation_Items`, void ใบเสร็จมัดจำ (`Account_Receipt.Status='Cancel'`), ยิง accounting event `EnqueueDepositRefund` (DR ADVANCE_DEPOSIT / CR Cash-Bank)
  - `CancelReservationWithoutRefund()` (`Class/ReservationService.cs:276-334`): เหมือนกันแต่ `Status='ยกเลิกไม่คืนเงิน'`, ใบเสร็จมัดจำตั้ง `Status='Forfeit'`, ยิง `EnqueueDepositForfeit` (DR ADVANCE_DEPOSIT / CR Forfeit Income 41220)
- **การเลื่อนวัน (Reschedule/Postpone)** แยกจากยกเลิก — `RescheduleService.cs` มี 3 ประเภท: `POSTPONE` (ลบวันเช็คอิน/เอาท์ทิ้ง ตั้ง `IsPostponed=1` — เก็บการจองไว้แต่ยังไม่มีวัน), `DATE_CHANGE` (เปลี่ยนวันปกติ), `CANCEL_POSTPONE` (ยกเลิกการเลื่อน → status = `ยกเลิกการเลื่อนวันเข้าพัก`) — บันทึกทุกครั้งลง `Reservation_Reschedule_History` พร้อม old/new date คู่กัน (`RescheduleService.cs:33-88`)
- `LogDateChangeWithPriceDiff()` (`RescheduleService.cs:360-397`) **ตั้งใจจะบันทึกส่วนต่างราคาเข้าบัญชีตอนเปลี่ยนวัน แต่ comment บอกตรง ๆ ว่า "Accounting sync disabled — ใช้ manual sync จากหน้าจัดการเอกสารแทน"** (line 393) — ฟังก์ชันไม่ enqueue อะไรเข้าคิวเลย ทั้งที่ชื่อเมธอดสัญญาไว้

### 3.5 เช็คอิน/เช็คเอาท์

**เช็คอิน** (`ReservationDataAccess.CheckInReservation()`, line 469-481): `UPDATE Reservation SET Status='เช็คอินแล้ว', Deposit=TotalPrice` — เงื่อนไขก่อนหน้าคือยอดชำระต้องเท่ายอดคงเหลือเป๊ะ (§3.3)

**เช็คเอาท์** ผ่าน stored procedure `sp_ProcessCheckout` (`PHASE1_Migration_10_Checkout_Status.sql:177-333`):
```
1. ตรวจ Status ต้องเป็น 'เช็คอินแล้ว'/'เข้าพักแล้ว' เท่านั้น ไม่งั้น error -2
2. FinalAmount = TotalPrice + DamageCharge + MissingItemsCharge
3. PaymentStatus = PAID (remaining≤0.01) / PARTIAL / UNPAID  (แต่ "ห้าม checkout ถ้าไม่จ่ายครบ" ถูก comment ปิดไว้ใน SP — บังคับจริงที่ชั้น UI (Checkout.aspx.cs:123-144) ไม่ใช่ที่ DB)
4. UPDATE Reservation: CheckoutDate=GETDATE(), CheckoutBy_AdminID, FinalSettlementAmount, Status='เสร็จสิ้น'
5. INSERT Checkout_History (เก็บ snapshot ค่าเสียหาย/ของหาย/ความสะอาด/ความพึงพอใจ 1-5)
6. UPDATE Room_Status SET RoomStatus='DIRTY' (ถ้าตาราง Room_Status มีอยู่ — เป็น legacy table คนละตัวกับ HousekeepingStatus บน Accommodation)
```
ฝั่ง C# (`CheckoutService.cs:44-136`) ก่อนเรียก SP จะตรวจ **under-paid checkout** (จ่ายไม่ครบแต่ยัง checkout อยู่ดีถ้า SP ไม่บล็อก) แล้ว log warning ผ่าน `AccountingArithmeticValidator` (ไม่ block, blocking:false — line 58-64) จากนั้นยิง `TryEnqueueDepositClearing()` (line 237-271) ตัดมัดจำจากเจ้าหนี้ ADVANCE_DEPOSIT เข้า ROOM_REVENUE โดยกันการตัดซ้ำด้วยการเช็ค `Account_Receipt.Deposit_Applied_Amount` ก่อน (line 246-256)

**ค่าปรับ**: `RoomDamage`+`DamageCharge`, `MissingItems`+`MissingItemsCharge` — เป็นตัวเลขที่พนักงานกรอกเอง (ไม่มีสูตรคำนวณอัตโนมัติ, ไม่มีตารางเรทค่าปรับมาตรฐาน) → บวกเข้า `FinalAmount` ตรง ๆ (`sp_ProcessCheckout` line 241-242)

### 3.6 ภาษี/เอกสารบัญชี — ดู §8

---

## 4. ทุกช่องตั้งค่า (Settings)

| กลุ่ม | ชื่อช่อง | ชนิด | Default | ผล | file:line |
|---|---|---|---|---|---|
| **โรงแรม** | `HotelName` | string (SystemSettings) | `TakeTime BangPhra` | ใช้ในอีเมล/แบบฟอร์ม (ผ่าน NotificationService ที่**ไม่ถูกเรียกจริง** — ดู §9) | `Migrations/001…:561` |
| | `HotelAddress` | string | `บางพระ ศรีราชา ชลบุรี` | เช่นเดียวกัน | `Migrations/001…:562` |
| | `CheckInTime` | string | `14:00` | **ไม่ถูกบังคับจริงที่ไหนเลย** ใช้แค่ตัวแปรแทนที่ในเทมเพลตอีเมลที่ไม่ทำงาน | `Migrations/001…:564`, `NotificationService.cs:294` |
| | `CheckOutTime` | string | `12:00` | เช่นเดียวกัน | `Migrations/001…:565` |
| **ภาษี** | `Business_Info.Use_Vat` | bit/string | — | เปิด/ปิด VAT 7% ทั้งระบบ (ตัดสินว่าราคาที่ตั้งรวม VAT หรือไม่) | `ReceiptService.cs:297-306` |
| | `Deposit_Vat_Recognition` | string enum | `CHECKOUT` | `CHECKOUT`=รับรู้ VAT ตอนตัดมัดจำเป็นรายได้, `RECEIPT`=รับรู้ทันทีตอนรับมัดจำ (ม.78/1) | `PHASE12_Migration_13…:29-36` |
| | `Etax_AutoGenerate` | '0'/'1' | `0` | สร้าง e-Tax Invoice อัตโนมัติเมื่อออกใบเสร็จหรือไม่ | `PHASE12_Migration_02…:59-63` |
| | `WHT_AutoGenerateCert` | '0'/'1' | `1` | สร้างหนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ) อัตโนมัติเมื่อบันทึกใบสำคัญจ่ายที่มี WHT | `PHASE12_Migration_02…:65-69` |
| **มัดจำ/ราคา** | ห้อง VIP hardcode (ID 21,22,23,18) | int[] hardcoded ในโค้ด | — | มัดจำ 1,000฿ แทนที่จะเป็น 500฿ — **ไม่ใช่ config, แก้ต้องแก้โค้ด** | `Reserve.aspx.cs:465` |
| **Room Service** | `Guest_RoomService_Settings.Is_Enabled` | bit | 1 | เปิด/ปิดระบบสั่งของทั้งระบบ | `PHASE13_Migration_01_RoomService…:17` |
| | `Manual_Mode` | nvarchar(10) enum | `AUTO` | `AUTO`=ตามเวลา, `OPEN`=เปิดฝืนเวลา, `CLOSED`=ปิดฝืนเวลา | เดียวกัน:18 |
| | `Open_Time`/`Close_Time` | time(0) | `08:00:00`/`20:00:00` | ช่วงเวลาที่ให้สั่งของได้ | เดียวกัน:19-20 |
| | `Closed_Message` | nvarchar(500) | ข้อความ default ภาษาไทย | ข้อความแจ้งตอนนอกเวลา | เดียวกัน:21,39 |
| **Channel Manager** (ตาราง `ChannelConfigurations`, ไม่มี UI จัดการจริง — ดู §9) | `ChannelCode`/`ChannelName` | seed data | `AGODA`(15%),`BOOKING`(15%),`EXPEDIA`(18%),`DIRECT`(0%) | commission rate ต่อช่องทาง | `Migrations/001…:40-45` |
| | `SyncFrequencyMinutes` | int | 15 | ความถี่ sync (ไม่มีตัว scheduler ที่เรียกจริง) | เดียวกัน:32 |
| **Dynamic Pricing rules** (ตาราง `PricingRules`, dead — ดู §9) | `RuleType` seed | 7 แถว | occupancy 4 tier (0.85/1.00/1.15/1.30), weekend +20%, last-minute -10%, early-bird -5% | `Migrations/001…:144-152` |
| **Pricing Seasons** (ตาราง `PricingSeasons`, dead) | seed | 4 ฤดู | สงกรานต์ ×1.50, ปีใหม่ ×1.60, high season ×1.25, low season ×0.85 | `Migrations/001…:173-178` |
| **Accounting Integration** | `Nexaacc_BaseUrl` | string | ว่าง | URL ของ NextAcc API | `PHASE9_Migration_01…:32` |
| | `Nexaacc_ApiKey_Encrypted` | string | ว่าง | API key (เข้ารหัส) | เดียวกัน:33 |
| | `Nexaacc_CompanyId` | string(GUID) | ว่าง | บริษัทปลายทางใน NextAcc | เดียวกัน:34 |
| | `Nexaacc_Enabled` | 'true'/'false' | `false` | เปิด/ปิด sync ทั้งระบบ (kill switch) | เดียวกัน:35 |
| | `Nexaacc_SyncInterval_Sec` | int | 30 | ความถี่ประมวลผลคิว | เดียวกัน:36 |
| | `Nexaacc_MaxRetries` | int | 5 | จำนวน retry สูงสุดต่อรายการ | เดียวกัน:37 |
| | `Nexaacc_TimeoutSec` | int | 30 | timeout ต่อ HTTP call | เดียวกัน:38 |
| **Account Mapping** | `Accounting_Account_Mapping` (30+ แถว) | key-value → NextAcc account code | ดู §8.1 | ผูก TakeTime concept (CASH/ADVANCE_DEPOSIT/ROOM_REVENUE ฯลฯ) → เลขบัญชี NextAcc จริง | `PHASE9_Migration_01…:66-138` |
| **Housekeeping** | `HousekeepingSupplies` (seed 10 รายการ) | int (MinimumStock/ReorderLevel) | ผ้าเช็ดตัว 50/100, ผ้าปูเตียง 20/40, แชมพู 100/200 ฯลฯ | เตือน reorder อุปกรณ์แม่บ้าน | `Migrations/001…:312-323` |
| **OCR** | `OCR_Enabled` (Web.config) | bool | `false` | เปิด/ปิดระบบอ่านสลิปอัตโนมัติ (**ปิดอยู่จริงในโค้ด ปัจจุบัน — Tesseract ถูกถอดออกจาก packages.config**) | `NEXT_STEPS.md:133-134`, `Web.config:33` |
| **Loyalty** | Welcome bonus | int hardcode | 100 pt | แต้มต้อนรับสมาชิกใหม่ | `LoyaltyService.cs:793` |
| | Review reward | int hardcode | 100 pt, 1 ครั้ง/ปีปฏิทิน | แต้มจากอัปโหลดภาพหน้าจอรีวิว Google | `Guest/Review.aspx.cs:184-211` |

---

## 5. Flow การจองฝั่งลูกค้า

1. **หน้าแรก** `Default.aspx` — ปฏิทินรายเดือน คลิกวันที่ → `selecteddate` querystring ไป `Reserve.aspx` (`Default.aspx.cs:23-32`); คลิกวันก่อนวันนี้ไม่ได้สำหรับ public user (`Session["permission"]=="No"` เช็คที่ `Default.aspx.cs:293,569`)
2. **`Reserve.aspx`** (ฟอร์มจองหลัก, มาร์กอัปเต็มที่ `Reserve.aspx:349-858`):
   - **Reservation Details**: วันที่เช็คอิน (`TextBox12`, date picker + AutoPostBack), จำนวนคืน (`DropDownList1` 1-7 คืน), ตาราง `GridView1` เลือกห้อง+ระบุจำนวนคนต่อห้อง (`txtPeopleStay`) — แสดงราคา/คนสูงสุดที่ query สดจาก `AccomPrice()`
   - **Guest Information**: เบอร์โทร (`TextBox1`, บังคับ, auto-parse ชื่อ+เบอร์จากข้อความวางรวมกัน `TextBox1_TextChanged`), ชื่อเต็ม/ชื่อบริษัท (`DropDownList8` คำนำหน้า + `TextBox2`), Facebook/Line ID (`TextBox3`), checkbox "ไม่รับใบกำกับภาษี" (default checked, ติ๊กออกจะเปิด panel ที่อยู่/เลขบัตร ปชช./อีเมล + checkbox "ต้องการรับ e-tax invoice")
   - **Additional Services**: checkbox "เช่าของ" เปิด `Panel2` แสดงตาราง `GridView2` (อุปกรณ์เช่า จาก `Items`) + รูปภาพอุปกรณ์
   - **Payment Information**: รหัสคูปอง (`TextBox19`+ปุ่ม Submit → ตั้ง `Session["UseCoupon"]`), panel ส่วนลดสมาชิก loyalty (ถ้ามี tier), ราคารวม (`TextBox4`, readonly, คำนวณสด), ยอดมัดจำขั้นต่ำ (`Label2`), ช่องกรอกยอดที่โอนจริง (`TextBox5`, ตรวจ ≥80% ของขั้นต่ำ), dropdown วิธีชำระ (`DropDownList2` จาก `Account_Paid_How`), อัปโหลดสลิป (`FileUpload1`+ปุ่ม Upload), แสดงประวัติการชำระ (`gvPaymentHistory`, โผล่เฉพาะโหมด checkin/edit/checkout), หมายเหตุ (`TextBox6`)
   - **กติกาที่พัก**: รูปภาพกฎระเบียบ (`./Images/กฏระเบียบ.png`) + checkbox ยอมรับกติกา (`CheckBox1`, บังคับติ๊กก่อนปุ่ม submit จะ enable — client-side gating)
   - ปุ่ม **"ยืนยันการจอง(Submit)"** (`Button1`, ปิดใช้งานจนกว่าจะติ๊กยอมรับกติกา, มี `preventDoubleSubmit()` client-side กันกดซ้ำ) หรือปุ่ม **"เลื่อนเข้าพัก"** (`btnPostpone`, มีเฉพาะบางโหมด, confirm dialog ก่อนลบวันที่)
3. **บันทึกการจอง** (`Button1_Click`, command="reserve"): validate availability (ทีละห้อง) → insert `Reservation` + `Reservation_Accommodation`(N ห้อง) + `Reservation_Items`(N รายการเช่า) → ถ้ามัดจำ > 0 และมีสลิป → เรียก `createReceipt()` สร้างใบเสร็จมัดจำ (`IsDeposit=true`) → บันทึก `Payment_History`+`Payment_Slips` → generate PDF/e-Tax (ถ้าเลือก) → ส่งอีเมลแนบใบเสร็จ (ถ้ามีอีเมล)
4. **`Reservation_Confirmed.aspx`** — หน้ายืนยัน (แสดงเลขที่จอง/สรุปยอด — ไม่ได้อ่านโค้ดเต็ม แต่ยืนยันมีจริงในเรพ 584+384 บรรทัด)
5. **แก้ไขการจองภายหลัง**: กลับมาที่ `Reserve.aspx?command=edit&id=...` ด้วยเบอร์โทร+รหัสจอง (`GetReservationByIdAndPhone`, `ReservationDataAccess.cs:34-45`) — โหมด edit ให้เพิ่มมัดจำ/เปลี่ยนวัน/เพิ่ม-ลดห้อง/ลบรายการเช่าได้ (ผ่าน `CheckBox2`+`TextBox10`)
6. **`CountReserved.aspx?telnum=...`** — เช็คประวัติเข้าพักของลูกค้ารายนี้ (แสดงเฉพาะสถานะ `เช็คอินแล้ว`/`เช็คเอาท์แล้ว`/`เสร็จสิ้น`) — ใช้ตัดสินสถานะ "ลูกค้าเก่า/ใหม่" (⚠️ ใช้ `string.Format` ต่อ SQL ตรง ๆ ไม่ parameterized — SQL injection risk, `CountReserved.aspx.cs:24-34`)

### 5.1 Guest Portal (หลังเช็คอินแล้ว, ผ่าน QR code ในห้อง)

1. แม่บ้าน/แอดมินสร้าง QR ต่อห้องผ่าน `sp_Generate_Room_QR_Code` (`SQL Scripts/11_Guest_Portal_QR_System.sql:294-345`) — ได้ URL รูปแบบ `https://taketimebangphra.com/Guest/Portal?qr={token}`
2. แขก scan QR → `Guest/Portal.aspx` → เรียก `sp_Verify_Guest_Portal_Access(QR_Token, MobilePhone)` (`เดียวกัน:354-420`) ตรวจว่ามี reservation ที่ active คาบเกี่ยววันนี้จริง (`CheckinDate ≤ วันนี้ ≤ CheckoutDate`, status ไม่ใช่ cancel/checkout) → สร้าง `Guest_Portal_Sessions` (`GuestPortalService.CreateGuestSession`, `GuestPortalService.cs:119-161`)
3. หลัง login แล้วเข้าถึง 12 หน้า guest-facing ทั้งหมด (`Guest/*.aspx`, เชื่อม `GuestPortalService` — call site ยืนยันจริง 16 จุด): `Dashboard`, `RoomService` (สั่งอาหาร/ของ, เช็ค `IsRoomServiceOpen()` ก่อนอนุญาต), `Housekeeping` (ขอทำความสะอาด/ผ้าเช็ดตัว/แม่บ้าน), `Concierge` (ทัวร์/สปา/ร้านอาหาร/รถรับส่ง), `Activities`, `Balance` (ยอดค้างชำระ), `MyPoints` (loyalty), `Review` (รีวิว Google แลกแต้ม), `Chat` (คุยกับ front desk แบบ real-time polling), `Emergency`, `NearbyPlaces`, `Facilities`, `AboutUs`

---

## 6. Flow ฝั่งพนักงาน

### 6.1 Front Desk (Admin ทั่วไป)
- **`ReserveTable.aspx`** — ปฏิทิน (`Calendar1`) เลือกวันที่ → โหลดรายการจองทั้งหมดของวันนั้น (join `Reservation`+`Customer`+`Reservation_Accommodation`+`Reservation_Items`) เรียงตาม `Accommodation.orderID` — เป็น "tape chart"/board มุมมองประจำวันสำหรับพนักงาน (`ReserveTable.aspx.cs:54-80`)
- **`DisplayReserve.aspx`** — ปฏิทินรายเดือน/รายปี แสดง occupancy grid ทั้งเดือน (dropdown ปี/เดือน) (`DisplayReserve.aspx.cs:20-52`)
- **`ReservationList.aspx`** — รายการจองแบบตาราง (มีอยู่จริง 838+716 บรรทัด — ไม่ได้อ่านลึก)
- **`Checkout.aspx`** — แสดง remaining balance, บล็อกปุ่ม checkout ถ้าไม่ครบ 100% (`Checkout.aspx.cs:123-144`), ฟอร์มกรอกค่าเสียหาย/ของหาย/ความสะอาด/ความพึงพอใจ → เรียก `CheckoutService.ProcessCheckout()` → `sp_ProcessCheckout`
- **`Admin/RoomQRGenerator.aspx`** — สร้าง/พิมพ์ QR ต่อห้อง (เรียก `GuestPortalService.GenerateRoomQRCode`)
- **`Product/Default.aspx`** (POS) — ขายของ/สแกนบาร์โค้ด, เลือกโหมด "ชาร์จเข้าห้อง" vs "จ่ายทันที" ผูกกับ `Reservation_Product_Charges` (ตาม design doc — ไม่ได้อ่าน code-behind เต็ม)

### 6.2 Housekeeping
`Admin/Housekeeping/Dashboard.aspx.cs` (**ใช้งานจริง เชื่อมกับข้อมูลจริง**): นับห้องตามสถานะ (`HousekeepingStatus` บน `Accommodation` โดยตรง — GROUP BY แล้ว map เป็น 4 กลุ่ม clean/dirty/inspecting/occupied), แสดงรายการห้องสกปรก, ปุ่ม "ทำความสะอาดแล้ว"/"ตั้งเป็นสกปรก" (`SetClean`/`SetDirty`) — `UPDATE Accommodation SET HousekeepingStatus=...` ตรง ๆ ไม่มี workflow assign/checklist ใด ๆ ในหน้าที่ใช้งานจริง (`Dashboard.aspx.cs:28-170`)

⚠️ **`HousekeepingService.cs` (35,267 bytes, ครบเครื่อง: room status history, task assignment, checklist, staff schedule) ถูก instantiate ในหน้านี้ (`_housekeepingService`) แต่ไม่มี method ไหนของมันถูกเรียกเลยแม้แต่ครั้งเดียว** — ตาราง `Room_Status`/`HousekeepingTasks`/`HousekeepingChecklistItems`/`HousekeepingSchedule` (จาก `Migrations/001…`) จึงไม่เคยถูกเขียนข้อมูลจริงในการทำงานจริง (ดู §9)

### 6.3 Room Service (ใช้งานจริง 100%)
`Admin/RoomService/OrderManagement.aspx.cs` (`412 บรรทัด, ครบ flow`): โหลดออร์เดอร์ 7 วันล่าสุดเรียงตาม priority สถานะ (PENDING→CONFIRMED→PREPARING→อื่นๆ) → รายละเอียดออร์เดอร์ (ชื่อแขก/ห้อง/รายการ/วิธีชำระ) → ปุ่ม "รับออเดอร์"(`btnClaim`→CONFIRMED)/"จัดส่งแล้ว"(`btnDelivered`→DELIVERED)/"ยกเลิก"(`btnCancel`→CANCELLED) → มี `[WebMethod]` แบบ polling (`GetPendingOrders`, `ClaimOrder`) สำหรับแจ้งเตือนแบบ real-time โดยไม่ reload หน้า (`OrderManagement.aspx.cs:305-410`)

### 6.4 CRM / Loyalty
`Admin/CRM/*` 5 หน้า ล้วนเชื่อม `LoyaltyService`/`ReviewService` จริง: `MembershipManagement` (จัดการสมาชิก), `LoyaltyDashboard` (สถิติโปรแกรม), `RedemptionTerminal` (แลกของรางวัลหน้าเคาน์เตอร์ ผ่าน token ชั่วคราว — `GenerateRedemptionToken`/`RedeemRewardByToken`, `LoyaltyService.cs:653-767`), `GuestProfile` (ดูประวัติ/preference/note รายลูกค้า), `ReviewManagement`, `AIReviewDashboard` (วิเคราะห์รีวิวด้วย AI)

### 6.5 Maintenance — **ใช้งานไม่ได้จริง (broken end-to-end)**
`Admin/Maintenance/Dashboard.aspx.cs:33-56` query `SELECT RequestId, Title, Location, Priority, RequestDate FROM MaintenanceRequests` — คอลัมน์ `Location` **ไม่มีอยู่จริง** (schema จริงคือ `LocationType`+`LocationId`+`LocationName`, `Migrations/001…:337-339`) → query throw exception ทุกครั้ง ถูก catch เงียบ → `lblNoRequests.Visible=true` เสมอ (แสดง "ไม่มีรายการ" ตลอดกาล) และ**ไม่มีทางสร้าง Maintenance Request ใหม่จากหน้าจอไหนเลยในทั้งเรพ** (`MaintenanceRequestService.cs` ที่มี `INSERT` ไม่ถูกเรียกจากที่ไหนเลย) — ฟีเจอร์นี้ตายทั้งสายทั้งฝั่งอ่านและฝั่งเขียน

---

## 7. ช่องทางขาย/OTA/Channel Manager

**สรุปตรงไปตรงมา: Channel Manager ใน TakeTime เป็นเพียง schema + service ที่เขียนไว้ล่วงหน้า ไม่มีการเชื่อมต่อ OTA จริงที่ทำงานได้ในปัจจุบัน**

- `ChannelConfigurations` seed 4 ช่องทาง (Agoda 15%, Booking.com 15%, Expedia 18%, Direct 0%) — เก็บ `ApiEndpoint`/`ApiKey`/`ApiSecret`/`HotelCode` (`Migrations/001…:19-46`)
- `ChannelManagerService.cs` (31KB) เขียน push/pull เต็มรูปแบบ: `SyncAvailabilityToAllChannels`, `SyncRatesToAllChannels`, `ImportReservationsFromChannel` (parse JSON ตาม channel, สร้าง `Reservation` local พร้อม guest จาก OTA) — **ยืนยันว่าไม่ถูก instantiate จากที่ไหนเลยในทั้งเรพ** (`grep new ChannelManagerService(` เจอเฉพาะไฟล์ตัวเอง) และ `CreateLocalReservation()`/`UpdateOTAReservation()` เขียนไปที่คอลัมน์ `OTA_Channel`/`OTA_Booking_ID`/`OTA_Guest_Name`/`OTA_Guest_Email` **ที่ไม่มีอยู่จริงในตาราง `Reservation`** (มีแค่ `BookingSource`/`OTABookingCode` จาก migration 001 คนละชื่อ) — ถ้าถูกเรียกจริงจะ SQL error ทันที
- `Admin/ChannelManager/Dashboard.aspx.cs` (หน้าเดียวที่มี UI) เป็นเพียงแดชบอร์ดแสดงสถิติ **การจองตรง (direct booking)** เท่านั้น (`SUM(TotalPrice) FROM Reservation ... MONTH(CheckinDate)=MONTH(GETDATE())`) ไม่มีปุ่ม sync/import ใด ๆ กับ OTA จริง (`Dashboard.aspx.cs:28-82`)
- **ช่องทางที่เชื่อมจริง**: `MapDataWithSTAAH` (ตารางแมปข้อมูลกับ **STAAH** — ผู้ให้บริการ Channel Manager ภายนอกจริง) จัดการผ่าน generic CRUD (`Edit_Data.aspx?data=MapDataWithSTAAH`) — ไม่มี sync engine ในเรพเอง (STAAH เป็นระบบภายนอกที่ผูก mapping ด้วยมือ) — จุดเชื่อมจริงที่ยืนยันได้คือ **`API/TaxInvoiceAPI.ashx`** ซึ่งมี comment ระบุชัดว่า "สำหรับสร้างใบกำกับภาษีจากระบบภายนอก (Channel Manager - STAAH)" (`TaxInvoiceAPI.ashx.cs:12-14`) — เป็น REST endpoint (`X-API-Key` auth, action=`createfromreservation`) ให้ STAAH หรือระบบภายนอกยิงเข้ามาสร้างใบกำกับภาษีจาก reservation ที่มีอยู่แล้ว **แต่ไม่มี endpoint ฝั่งรับการจองเข้ามาจาก OTA เลย** (มีแต่ทางออกเอกสาร ไม่มีทางเข้าการจอง)
- **OmniChannelService** (คนละเรื่องกับ Channel Manager — เป็นแชทข้ามช่องทาง LINE OA/Facebook Messenger) **ใช้งานจริง**: `Admin/Chat/OmniChannelInbox.aspx.cs`, `ChannelSettings.aspx.cs`, `API/OmniChannelWebhook.ashx.cs` เชื่อมกันจริงพร้อม `AIKnowledgeService`/`DeepSeekService` สำหรับตอบแชทอัตโนมัติ

---

## 8. เอกสารบัญชี/ภาษีที่ระบบออก และจังหวะที่ออก

### 8.1 บัญชีที่ TakeTime map ไว้เพื่อ sync เข้า NextAcc (`Accounting_Account_Mapping`, `PHASE9_Migration_01…:66-138`)

| TakeTime_Code | ความหมาย | ผังบัญชี NextAcc (ตัวอย่างที่ seed ไว้) | ประเภท |
|---|---|---|---|
| `CASH` | เงินสด | `11111` | ASSET |
| `ROOM_AR` | ลูกหนี้ค่าห้องพัก | `11310` | ASSET |
| `DIRECTOR_ADVANCE` | เงินทดรองกรรมการ | `11330` | ASSET |
| `INVENTORY` | สินค้าคงเหลือ | `11500` | ASSET |
| `INPUT_VAT` | ภาษีซื้อ | `11610` | ASSET |
| `ADVANCE_DEPOSIT` | เงินรับล่วงหน้า-มัดจำ | (sync จาก NextAcc) | LIABILITY |
| `OUTPUT_VAT` | ภาษีขาย | (sync) | LIABILITY |
| `WHT_PAYABLE` | ภาษีหัก ณ ที่จ่าย ค้างจ่าย | (sync) | LIABILITY |
| `ROOM_REVENUE` | รายได้ค่าห้องพัก | (sync) | REVENUE |
| `PRODUCT_REVENUE`/`FB_REVENUE`/`SERVICE_REVENUE` | รายได้ขายของ/อาหาร/บริการอื่น | (sync) | REVENUE |
| `DAMAGE_INCOME` | รายได้ค่าเสียหาย | `41210` | REVENUE (`PHASE12_Migration_06…:14-19`) |
| `FORFEIT_INCOME` | รายได้ริบมัดจำ | `41220` | REVENUE (เดียวกัน:22-27) |
| `COGS` | ต้นทุนขาย | (sync) | EXPENSE |
| `EXPENSE_OTA` | ค่าคอมมิชชั่น OTA | (sync) | EXPENSE |

### 8.2 เอกสารที่ระบบออกจริง + จังหวะ

| เอกสาร | ออกเมื่อ | ผูก entity | สคีมา/ที่มา |
|---|---|---|---|
| **ใบเสร็จมัดจำ** (`IsDeposit=true`) | ตอนจอง ถ้ามีการโอนเงินมัดจำ (≥80% ขั้นต่ำ) หรือตอน edit เพิ่มมัดจำ | `Account_Receipt`+`Account_Receipt_Detail` (บรรทัดเดียว "ค่ามัดจำที่พักของหมายเลขการจอง...") | `ReceiptService.cs:285-366`, `ReceiptService.CreateDepositReceipt():501+` |
| **ใบเสร็จ/ใบกำกับภาษีเต็มจำนวน** (`IsDeposit=false`) | ตอนเช็คอิน (ชำระส่วนที่เหลือ) หรือตอน rentmore | เดียวกัน (หลายบรรทัด ปรับสัดส่วนให้ตรงยอดจริงด้วย `AdjustReserveDataToMatch`) | `ReceiptService.cs:344-345` |
| **เลขที่เอกสาร** | ออกตอนสร้างใบเสร็จ (ไม่รอ approve แยก) | รูปแบบ `{Type}{YY}{MM}{DD}{running 3-digit}` เช่น `REC260902001` — หา running number จาก `SELECT TOP 1 ... ORDER BY ID DESC` แล้ว +1 (**ไม่มี advisory lock — เสี่ยง race condition เลขซ้ำ**) | `DatabaseHelper.cs:247-293` |
| **PDF ใบเสร็จ** | ทุกครั้งหลังสร้างใบเสร็จ | `GenerateReceiptPdf()` (`ReceiptService.cs:349`) | — |
| **e-Tax Invoice (ETDA)** | ถ้า checkbox "ต้องการรับ e-tax invoice" ถูกติ๊ก หรือ `Etax_AutoGenerate=1` | สร้าง XML (ETDA format) + PDF/A-3 คู่กัน ผ่าน `PDFA3Invoice.CreatePDFA3Invoice()` (`Reserve.aspx.cs:5340-5353`) — sync ผ่าน `Accounting_ETax_Log` (`Nexaacc_Etax_Id`,`Etax_Ref_Number`,`Xml_Url`,`Pdf_Url`,`Signed_Date`,`Submitted_Date`,`Email_Sent`) (`PHASE12_Migration_02…:33-56`) |
| **หนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ)** | เมื่อบันทึกใบสำคัญจ่ายที่มี WHT (ถ้า `WHT_AutoGenerateCert=1`) | `Accounting_WHT_Cert_Log` (`Certificate_No`,`Payee_TaxId`,`WHT_Rate`,`WHT_Amount`,`Income_Type`,`Form_Type`) sync ผ่าน `AccountingApiClient.AutoGenerateWhtCertAsync/GetWhtCertPdfAsync` | `PHASE12_Migration_02…:9-26`, `AccountingApiClient.cs:952-974` |
| **การตัดมัดจำเป็นรายได้ (Journal)** | ตอนเช็คเอาท์ (DR ADVANCE_DEPOSIT / CR ROOM_REVENUE, แบ่ง CR ไป DAMAGE_INCOME ถ้ามีค่าเสียหาย) | `Accounting_Sync_Queue` action `CLEAR_DEPOSIT_AT_CHECKOUT`, ref `RES-{id}-CHK` | `CheckoutService.cs:237-271`, `AccountingSyncService.cs:319-347` |
| **คืนเงินมัดจำ (Journal)** | ตอนยกเลิกแบบคืนเงิน | action `REFUND_DEPOSIT` (DR ADVANCE_DEPOSIT / CR Cash-Bank), ref `RES-{id}-REF` | `AccountingSyncService.cs:350-373` |
| **ริบมัดจำ (Journal)** | ตอนยกเลิกแบบไม่คืนเงิน | action `FORFEIT_DEPOSIT` (DR ADVANCE_DEPOSIT / CR FORFEIT_INCOME), ref `RES-{id}-FORFEIT` | `AccountingSyncService.cs:376-399` |
| **การ sync ทั้งหมด** | asynchronous ผ่านคิว, ประมวลผลทุก `Nexaacc_SyncInterval_Sec` วินาที (default 30) โดย `ProcessQueueAsync()` | `Accounting_Sync_Queue` (PENDING→PROCESSING→COMPLETED/FAILED, retry สูงสุด `Max_Retries`) + `Accounting_Sync_Log` (audit HTTP call ทุกครั้ง) | `AccountingSyncService.cs:938+`, `PHASE9_Migration_01…:146-208` |

**API contract ที่ TakeTime คาดหวังจากฝั่ง NextAcc** (จาก `AccountingApiClient.cs`, endpoint จริงที่ถูกเรียก): `POST /api/integration/invoices`, `/expenses` (+ multipart), `/payments`, `/journals` (+ `/reverse`), `/credit-notes`, `/debit-notes`, `/customers`, `/products`, `/payment-vouchers`, `/documents/void`, `/certificates-in-lieu`, `/daily-summary`, `/batch`, `GET /account-balances`, `/documents`, `/contacts`, `/payments-list`, WHT (`AutoGenerateWhtCertAsync`), e-Tax (`GenerateEtaxAsync`/`SignAndSubmitEtaxAsync`/`SendEtaxByEmailAsync`) — **นี่คือสัญญา API ที่ NextAcc ต้องรองรับให้ระบบ PMS ภายนอกแบบนี้เชื่อมได้จริง** (§`AccountingApiClient.cs:452-1148`)

---

## 9. สิ่งที่ควรลอก / ไม่ควรลอก / ยังไม่เสร็จ

### ✅ ควรลอก (ออกแบบดี ใช้งานได้จริง)

1. **Deposit lifecycle เป็น state machine ชัดเจน** พร้อม idempotency guard กันตัดซ้ำ (`Deposit_Applied_Amount` check ก่อน enqueue clearing) และ view สรุปสถานะเจ้าหนี้มัดจำ (`vw_Reservation_Deposit_Status` — OPEN/CLEARED/PARTIAL/OVER_CLEARED) — ตรงกับหลักการ "ห้าม silent no-op" ของเรา
2. **Async accounting sync ผ่านคิว** (`Accounting_Sync_Queue`) แยก retry/audit log ออกจาก transaction หลัก ไม่ block งานหน้าบ้าน (checkout ผ่านได้แม้ sync ล้มเหลว — log แต่ไม่ throw) — ตรงกับหลักการ graceful degradation
3. **Payment validation policy ตามโหมด** ที่เขียนเป็นเอกสารคู่กับโค้ดชัดเจน (ยืดหยุ่นตอนมัดจำ เข้มงวดตอน checkin/checkout) — เป็น pattern ที่ดีสำหรับ PMS
4. **Guest Portal ผ่าน QR ต่อห้อง** ครบวงจร (room service + housekeeping request + concierge + chat) พร้อม open/close hour ที่ตั้งค่าได้ (AUTO/OPEN/CLOSED)
5. **Rate plan แบบ month-range × day-type** รองรับราคาต่างกันตามวันในสัปดาห์/วันหยุดพิเศษ ยืดหยุ่นกว่าราคาคงที่แบบง่าย และรองรับ voucher เป็น fixed-price (ไม่ใช่แค่ discount %)
6. **Deposit VAT recognition point ที่ตั้งค่าได้** (CHECKOUT vs RECEIPT) — สะท้อนความเข้าใจ ม.78/1 ที่ถูกต้อง (แม้ default จะยังเป็น CHECKOUT ซึ่งอาจไม่ถูกต้องตามกฎหมายสำหรับธุรกิจบริการ)
7. **Room Charge system** (`Reservation_Product_Charges`) แยก PENDING/PAID/CANCELLED กับ stock deduct/return ที่ชัดเจน คืนสต๊อกอัตโนมัติเมื่อลบ

### ❌ ไม่ควรลอก (จุดอ่อน/บั๊กที่ยืนยันแล้ว)

1. **ฟีเจอร์ "เขียนไว้แต่ไม่มีใครเรียก" จำนวนมาก** — ยืนยันด้วย grep `new <Service>(` ทั้งเรพ:
   - `DynamicPricingService.cs` (25KB) — ไม่ถูกเรียกเลย, `Admin/Pricing/DynamicPricing.aspx` เป็นแค่ตัวเลขแนะนำ อ่านอย่างเดียว ไม่เขียนราคาจริง
   - `ChannelManagerService.cs` (31KB) — ไม่ถูกเรียกเลย และเขียนไปยังคอลัมน์ที่ไม่มีอยู่จริง (`OTA_Channel` ฯลฯ) ถ้าถูกเรียกจะ error ทันที
   - `MaintenanceRequestService.cs` (47KB) — ไม่มีทาง insert จากที่ไหนเลย และหน้าอ่าน (`Admin/Maintenance/Dashboard.aspx.cs:33-38`) query คอลัมน์ผิดชื่อ (`Location` แทน `LocationName`) ทำให้ตายทั้งฝั่งอ่านและเขียน
   - `NotificationService.cs` (40KB) — ไม่ถูกเรียกเลย, หน้า `Admin/Notifications/Settings.aspx.cs` เป็น stub เปล่า ไม่มี server control แม้แต่ตัวเดียว
   - `AdvancedAnalyticsService.cs` (44KB) — ไม่ถูกเรียกเลย
   - `HousekeepingService.cs` (35KB) — `_housekeepingService` ถูก instantiate ใน `Admin/Housekeeping/Dashboard.aspx.cs:12,24` **แต่ไม่มี method ไหนถูกเรียก** หน้าจริงเขียน SQL ตรง ๆ กับ `Accommodation.HousekeepingStatus` เท่านั้น — ตาราง `Room_Status`/`HousekeepingTasks` จึงว่างเปล่าตลอด
   - `ReservationPriceCalculationService.cs` (22KB, "Centralized service...ensures consistent price calculation across the system" ตาม doc-comment ของตัวเอง) — **ไม่ถูกเรียกจากที่ไหนเลยในทั้งเรพ** ทั้งที่ตั้งใจให้เป็นจุดศูนย์กลาง — ราคาจริงคำนวณ inline กระจายอยู่ใน `Reserve.aspx.cs` (7,927 บรรทัด) แทน — และภายในมันอ้างตาราง `Coupons`/`Promotion_Accommodations` ที่ไม่มี `CREATE TABLE` อยู่จริงเลย
   - `CalculateAccomPriceWithExtraGuests()` (extra-bed pricing แบบขั้นบันได) — ไม่ถูกเรียก และอ้างคอลัมน์ที่ไม่มีจริง

2. **Dashboard หลายหน้า query ผิด schema แบบเงียบ** (catch แล้วไม่แจ้ง) — `Admin/GuestExperience/Dashboard.aspx.cs:33-37` query `Customer.Loyalty_Points` (ไม่มีจริง — ของจริงคือ `Customer_Loyalty.AvailablePoints`) และ `Guest_Chat` (คนละตารางกับ `Guest_Chat_Messages` ที่ระบบแชทจริงเขียนอยู่) → ตัวเลขที่แสดงผิดตลอดไป โดยไม่มี error ให้เห็น

3. **ไม่มี cancellation policy อัตโนมัติแบบวันก่อนเข้าพัก** — ต่างจาก PMS มาตรฐานที่มักมี tier (เช่น ฟรีถ้ายกเลิก ≥7 วัน, ริบ 50% ถ้า <3 วัน) ระบบนี้ให้พนักงานตัดสินใจเองทุกครั้งผ่านปุ่ม "คืนเงิน"/"ไม่คืนเงิน" — เสี่ยงความไม่สม่ำเสมอและข้อพิพาทกับลูกค้า

4. **ไม่มี Service Charge** เลยทั้งระบบ (มีแค่ VAT 7%) — ถ้าลูกค้าเป้าหมายของ NextAcc ต้องการรองรับโรงแรม/รีสอร์ตที่เก็บ service charge 10% ต้องออกแบบเพิ่มเอง

5. **มัดจำเป็นค่าคงที่ hardcode ไม่ใช่ % ที่ config ได้** และ ID ห้อง VIP hardcode ในโค้ด — ถ้าเพิ่มห้องใหม่ต้องแก้โค้ด ไม่ใช่แค่ตั้งค่า

6. **สอง engine ส่วนลด/คูปองที่ไม่เชื่อมกัน** — Affiliate/Voucher (ใช้งานจริงใน `AccomPrice()`) vs Promotions/Coupons (เขียนไว้ใน service ที่ไม่ถูกเรียก, table ไม่มีอยู่จริง) — สร้างความสับสนเวลาอ่านโค้ดว่าอันไหนคือของจริง

7. **สถานะการจองสะกดไม่สม่ำเสมอ** — `เช็คเอาท์แล้ว` (ใน `Admin/Dashboard.aspx.cs`, `CountReserved.aspx.cs`) vs `เช็คเอ้าท์แล้ว` (ใน `ReservationService.cs`, `ReserveTable.aspx.cs`) — สองตัวสะกดของคำเดียวกันถูกใช้เช็คสถานะคนละที่ (แม้ในทางปฏิบัติ `sp_ProcessCheckout` จะเซ็ตเป็น `เสร็จสิ้น` ไม่ใช่สองคำนี้ ทำให้ผลกระทบจริงจำกัด แต่เป็นสัญญาณ code drift)

8. **Race condition ในเลขที่เอกสาร** — `GenerateDocumentNumber()` ใช้ `SELECT TOP 1 ... ORDER BY ID DESC` แล้ว +1 โดยไม่มี lock ใด ๆ เสี่ยงเลขซ้ำเมื่อมี concurrent request (ขัดกับหลัก gap-free ของ §86/4 ที่ NextAcc ต้องรักษา)

9. **SQL Injection ที่หลงเหลือ** — `CountReserved.aspx.cs:24-34` ยังใช้ `string.Format` ต่อ query string เข้า SQL ตรง ๆ (แม้ไฟล์ส่วนใหญ่ของระบบจะ parameterized แล้ว)

10. **OCR สลิปโอนเงิน สร้างไว้ครบแต่ปิดใช้งานจริง** — `Payment_Slips` มีคอลัมน์ OCR ครบ, มี `SlipOCRService.cs`, มีหน้า `Account/SlipVerification.aspx` แต่ `Web.config: OCR_Enabled=false` และ Tesseract package ถูกถอดออกแล้ว (`packages.config`) — เป็นฟีเจอร์ "รอวันเปิดใช้" ที่ยังไม่ผ่านการทดสอบจริง

### 🔨 ฟีเจอร์ที่เขียนไว้แต่ยังไม่เสร็จ (มีทั้งของและช่องว่างชัดเจน)

- Dynamic Pricing engine เต็มรูปแบบ (7 multiplier) — รอแค่จุดต่อสายเข้า `AccomPrice()`
- Channel Manager push/pull จริง (มี HTTP client เขียนไว้แล้ว รอ endpoint จริงของแต่ละ OTA + ตาราง Reservation ต้องเพิ่ม `OTA_Channel`/`OTA_Booking_ID` ให้ตรงกับที่ service คาดหวัง)
- Maintenance Request ทั้งระบบ (แค่แก้ชื่อคอลัมน์ในหน้า Dashboard และเพิ่มปุ่ม "แจ้งซ่อม" ที่ไหนสักที่)
- Housekeeping Task/Checklist/Schedule (มี schema+service ครบ รอ UI ที่เรียกใช้จริงแทนการ toggle สถานะแบบง่ายปัจจุบัน)
- Notification system (template email/SMS พร้อม placeholder ครบ รอเชื่อมกับ event จริง เช่น reminder ก่อนเช็คอิน)

---

## 10. Checklist ฟีเจอร์ทั้งหมด (สำหรับ gap analysis)

**การจอง (Booking core)**
- [ ] เลือกห้อง/ที่พักหลายห้องในการจองเดียว (multi-room booking)
- [ ] จองแบบคิดราคาต่อห้อง (fixed) vs คิดราคาต่อหัว (`LimitWithPeople`)
- [ ] Rate plan แยกตามช่วงเดือน × ประเภทวัน (weekday/weekend/holiday)
- [ ] ราคาพิเศษรายวัน override rate plan (`Accommodation_Price`)
- [ ] ตรวจความว่างแบบ overlap-check ต่อห้อง (ไม่มี overbooking buffer)
- [ ] ตรวจความว่างแบบนับหัวสะสมสำหรับห้องรวม (capacity pooling)
- [ ] ตรวจความว่างแยกรายวันในช่วงที่พัก (`CheckAvailabilityByDate`)
- [ ] เช่าอุปกรณ์เพิ่ม (`Items`) ผูกกับการจอง
- [ ] คำนวณราคารวม real-time ขณะกรอกฟอร์ม (postback-based)
- [ ] มัดจำคงที่ต่อห้อง (ไม่ใช่ %) + เพิ่ม 50฿/หัวสำหรับห้องนับคน
- [ ] ตรวจยอดโอนมัดจำ ≥ 80% ของขั้นต่ำ (bypass ได้ถ้า staff)
- [ ] แก้ไขการจอง (เพิ่ม/ลดห้อง, เปลี่ยนวัน, เพิ่มมัดจำ) แบบไม่เช็คยอดรวม
- [ ] เลื่อนวันเข้าพักแบบไม่กำหนดวันใหม่ (postpone) พร้อมประวัติ
- [ ] เปลี่ยนวันเข้าพัก (date change) พร้อมประวัติ old/new
- [ ] ยกเลิกการเลื่อน (cancel postpone)
- [ ] ยกเลิกการจองแบบคืนเงิน (auto void ใบเสร็จมัดจำ + journal คืนเงิน)
- [ ] ยกเลิกการจองแบบไม่คืนเงิน/ริบมัดจำ (auto journal ริบเป็นรายได้)
- [ ] ~~Cancellation policy อัตโนมัติตามจำนวนวันก่อนเข้าพัก~~ (ไม่มี — manual เท่านั้น)
- [ ] คูปอง/ส่วนลด affiliate ผูกกับ rate plan
- [ ] Voucher แบบ fixed-price ผูกกับกลุ่ม rate plan + วันหมดอายุ + used-status
- [ ] ~~Promotion/Coupon engine แบบ %/fixed + min-stay + usage-limit~~ (เขียนไว้แต่ table ไม่มีจริง — dead code)
- [ ] ส่วนลดสมาชิก loyalty อัตโนมัติตาม tier แสดงในฟอร์มจอง
- [ ] Cross-sell สินค้า pre-book ตอนจอง (`Product.CanPreBook`)

**เช็คอิน/เช็คเอาท์**
- [ ] เช็คอิน: บังคับยอดชำระ = ยอดคงเหลือเป๊ะ, ล็อกช่องแก้ไข
- [ ] เช็คเอาท์: บังคับชำระครบ 100% ก่อน (ที่ชั้น UI, ไม่ใช่ DB level)
- [ ] เก็บประวัติเช็คเอาท์แยกตาราง (`Checkout_History`) พร้อม snapshot ค่าเสียหาย/ความสะอาด/ความพึงพอใจ
- [ ] คำนวณ payment status อัตโนมัติ (PAID/PARTIAL/UNPAID) ใน stored procedure
- [ ] เตือน under-paid checkout (log แต่ไม่บล็อก)
- [ ] ค่าปรับห้องเสียหาย/ของหาย (กรอกเอง ไม่มีสูตร/ตารางเรทมาตรฐาน)
- [ ] อัปเดตสถานะห้องเป็น "รอทำความสะอาด" อัตโนมัติหลังเช็คเอาท์
- [ ] ~~บังคับเวลาเช็คอิน/เช็คเอาท์มาตรฐาน (14:00/12:00)~~ (ตั้งค่าไว้แต่ไม่ enforce จริง)

**การจัดการที่พัก/Housekeeping**
- [ ] สถานะห้องแบบง่าย (`HousekeepingStatus` บน Accommodation) — ใช้งานจริง
- [ ] ~~Task assignment + checklist + staff schedule + supplies inventory~~ (schema ครบ, service ครบ, ไม่มี UI เรียกใช้จริง)
- [ ] เปลี่ยนสถานะห้องด้วยมือ (Clean/Dirty) จาก dashboard

**Room Service / POS**
- [ ] สั่งอาหาร/ของจาก Guest Portal พร้อมช่วงเวลาเปิด-ปิด (auto/manual)
- [ ] ชาร์จเข้าห้อง (deferred payment) vs จ่ายทันที
- [ ] คิวออร์เดอร์เรียงตาม priority สถานะ + real-time badge แจ้งเตือน
- [ ] ตัดสต๊อกอัตโนมัติเมื่อสั่ง, คืนสต๊อกอัตโนมัติเมื่อยกเลิก
- [ ] รวมยอด room charge เข้าบิลห้องพัก / mark paid พร้อมกันตอนออกใบเสร็จ

**Guest Portal (QR-based)**
- [ ] Generate QR ต่อห้อง (regenerate token ได้)
- [ ] ยืนยันสิทธิ์เข้า portal จาก QR + เบอร์โทร + reservation ที่ active จริง
- [ ] Session tracking (IP, user agent, login/logout)
- [ ] แจ้งขอทำความสะอาด/ของใช้ (housekeeping request) พร้อม priority + rating หลังเสร็จ
- [ ] ขอบริการ concierge (ทัวร์/สปา/ร้านอาหาร/รถรับส่ง) พร้อมประเมินราคา
- [ ] แชทกับ front desk แบบ 2 ทาง พร้อมแนบไฟล์
- [ ] ดูยอดค้างชำระ (Balance)
- [ ] ดูแต้มสะสม + แลกของรางวัล (MyPoints)
- [ ] รีวิว Google แลกแต้ม (จำกัด 1 ครั้ง/ปีปฏิทิน)
- [ ] Emergency, Nearby Places, Facilities (ข้อมูลสถิต)

**CRM/Loyalty**
- [ ] Guest preferences (ห้อง/หมอน/อาหาร/แพ้อาหาร) แยกตาม type + priority
- [ ] Guest notes พร้อม flag private/important
- [ ] Loyalty tier 5 ระดับ พร้อม multiplier แต้ม + discount % อัตโนมัติ
- [ ] Dual-pool points (yearly สำหรับ tier, available สำหรับแลกของ — แลกไม่ลด tier)
- [ ] Point earning: welcome bonus 100pt + review-for-points 100pt/ปี — **ไม่มี point earning จากยอดใช้จ่ายจริง**
- [ ] แลกของรางวัลผ่าน terminal ด้วย token ชั่วคราว (กันแลกซ้ำ/ปลอมแปลง)
- [ ] Guest reviews + AI sentiment analysis

**ช่องทางขาย**
- [ ] Direct booking ผ่านเว็บ (ใช้งานจริง 100%)
- [ ] ~~OTA sync อัตโนมัติ (Agoda/Booking.com/Expedia)~~ — schema+service พร้อม แต่ **ไม่เคยถูกเรียกจริง และเขียนไปคอลัมน์ที่ไม่มีอยู่จริง**
- [ ] STAAH mapping table (ตั้งค่าด้วยมือ ไม่มี sync engine ในเรพ)
- [ ] REST API ให้ระบบภายนอกสร้างใบกำกับภาษีจาก reservation (`TaxInvoiceAPI.ashx`) — ใช้งานได้จริง
- [ ] Omni-channel chat (LINE OA/Facebook) พร้อม AI ตอบอัตโนมัติ — ใช้งานจริง คนละระบบกับ Channel Manager

**เอกสารบัญชี/ภาษี**
- [ ] ใบเสร็จมัดจำ + ใบเสร็จเต็มจำนวน (VAT-inclusive, 7/107)
- [ ] เลขที่เอกสารอัตโนมัติ `{Type}{YY}{MM}{DD}{running}` (ไม่มี advisory lock — เสี่ยงซ้ำ)
- [ ] PDF ใบเสร็จอัตโนมัติทุกครั้ง
- [ ] e-Tax Invoice (ETDA, PDF/A-3 + XML) แบบ manual/auto toggle
- [ ] หนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ) auto-generate ผ่าน NextAcc API
- [ ] Deposit VAT recognition point ตั้งค่าได้ (CHECKOUT/RECEIPT ตาม ม.78/1)
- [ ] Journal อัตโนมัติ: ตัดมัดจำตอนเช็คเอาท์, คืนมัดจำตอนยกเลิก, ริบมัดจำเป็นรายได้
- [ ] Async sync queue ไป NextAcc พร้อม retry + audit log
- [ ] Account mapping ปรับตั้งได้ต่อ payment method / expense type
- [ ] ~~Journal ส่วนต่างราคาตอนเปลี่ยนวันเข้าพัก~~ (ตั้งใจแต่ comment บอกว่าปิดไว้ ไม่ enqueue จริง)

---

**สรุปสถานะการอ่าน**: ครบตามที่ prompt ระบุทุกไฟล์หลักด้าน business logic (`ReservationService`, `ReservationPriceCalculationService`, `AccommodationAvailabilityService`, `CheckoutService`, `RoomChargeService`-family ผ่าน migration+design doc, `HousekeepingService`, `GuestPortalService`, `ReservationDataAccess`), ครบทุกโฟลเดอร์ Admin ที่ระบุ (settings/pricing/channel-manager/housekeeping/room-service/guest-experience/CRM/maintenance/notifications), ครบ `Guest/*` ทั้งหมด (ผ่าน service call-site verification), ครบ `Docs/*.md` ที่มีอยู่จริงทั้ง 5 ไฟล์ (ไม่มี `DOCUMENT_FLOW.md`/`ACCOUNT_STRUCTURE.md`-เทียบเท่าของ TakeTime เอง), และ SQL migration ที่เกี่ยวกับที่พัก/การจอง/บัญชี/CRM ครบ (~35 ไฟล์จาก 70+ ไฟล์ทั้งหมด — ที่ข้ามคือ Payroll/HR/Leave/Asset ซึ่งอยู่นอกขอบเขต booking+accommodation ตามที่ prompt กำหนด)