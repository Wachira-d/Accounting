# BOOKING_ENGINE_DESIGN — ระบบจองคิว/บริการบนเว็บ CMS (ไม่ใช่ที่พัก)

> ขอบเขต: `SiteBookingService` / `SiteBookingSlot` / `SiteBooking` (หน้า `/booking` แบบ BookingCalendar ของเว็บ CMS) ·
> โมดูลที่พัก (Lodging) มี engine ของตัวเอง (`LodgingAvailability`) — ไม่อยู่ในเอกสารนี้ · ร้านอาหารแบบ `booking.html` (`/api/public/reservations`)
> เป็นโมดูลจองโต๊ะอีกตัว — ไม่อยู่ในเอกสารนี้
>
> สถานะ ณ 2026-10-22 (ทีม B) — **ยังไม่ได้คอมไพล์/รันเทสต์ในเครื่องที่เขียน** (env ไม่มี .NET SDK) ⇒ CI คือ compiler ตัวแรก

## 1. สิ่งที่ตรวจพบก่อนแก้ (สถานะเดิม)

| # | พบ | ผล |
|---|----|----|
| 1 | `CreateBookingAsync` ไม่ตรวจอะไรเลย — เวลาไหน · วันหยุด · รอบเต็ม · นอกเวลาทำการ · ย้อนหลัง ก็จองผ่าน · เวลาจบคำนวณจาก `TimeOnly.AddMinutes` (วนข้ามเที่ยงคืน) | จองซ้อนเกินความจุได้จากหน้าเว็บสาธารณะ |
| 2 | บริการตัวอย่างที่ seed ไม่มีแถวเวลา (`SiteBookingSlots`) และไม่มีหน้าจอให้สร้างแถวเวลา | หน้าเวลาว่างตอบ "ไม่มีเวลาว่าง" ทุกวัน — ลูกค้าจองไม่ได้เลย |
| 3 | ฟอร์มแก้ไขบริการใน `cms-edit.html` ส่งแค่ 5 ฟิลด์ แต่ PUT แทนทั้งชุด · รายการบริการไม่ echo buffer/มัดจำ/ชนิดการจอง | เปิดแก้แล้วบันทึก ⇒ `BookingType` กลับเป็น Lead · มัดจำ/ยกเลิก/ยืนยันอัตโนมัติหายเงียบ (defect class "เก็บแล้วต้อง echo กลับ") |
| 4 | `BufferMinutes`, `MinAdvanceBookingHours`, `MaxAdvanceBookingDays` มีในฐานแต่ไม่มีผู้อ่าน | ค่าตั้งไม่มีผล |
| 5 | ความจุนับ "การจองที่เริ่มในช่วง" ไม่ใช่การใช้พร้อมกัน · ไม่มี buffer | บริการ 90 นาทีกินช่วงถัดไปแต่ระบบไม่รู้ |
| 6 | query ของ slot/delete/sync ไม่มี `CompanyId` (tenant isolation) | ช่องโหว่ข้ามบริษัทเมื่อรู้ id |
| 7 | เลขการจองออกนอก transaction (SequenceNumber เตือน) | ล็อกเลขไม่มีผล |
| 8 | `cms-bookings.html` ส่ง `date=`/`search=` ที่ server ไม่รู้จัก · ป้ายสถิติอ่าน `totalItems` (server ส่ง `totalCount`) | ตัวกรองวันที่/ค้นหาไม่มีผล · สถิติเป็น 0 เสมอ |
| 9 | มัดจำ % ไม่ปัดเศษ | ยอดมัดจำทศนิยมยาว |

## 2. สิ่งที่ทำแล้ว (ทีม B)

### 2.1 Engine ตัวเดียว — `Helpers/BookingSlotEngine.cs` (pure · เทสต์ `BookingSlotEngineTests`)

- **ความละเอียดรอบ** (`BookingSlotMode`): `Windows` (เดิม — แต่ละช่วงในตาราง = 1 รอบ) · `Interval` (ทุก N นาที: 15/30/60/กำหนดเอง ·
  null = เท่าระยะเวลาบริการ) · `HalfDay` (เช้า/บ่าย ตามเวลาแบ่ง) · `FullDay` (ทั้งช่วงเวลาทำการ)
- **จองหลายช่วงติดกัน** (`MaxUnitsPerBooking` · Interval เท่านั้น) — สนาม/ห้องประชุม 1–N ชั่วโมง · ยอด = ราคาต่อช่วง × ช่วง (server คำนวณ)
- **buffer ก่อน/หลัง** — ช่วงที่ทรัพยากรถูกใช้ = [เริ่ม − เตรียม, จบ + เก็บ) ของทั้งสองฝั่ง (buffer ของการจองเดิมมาจากบริการของการจองนั้น)
- **ความจุ/จองซ้อน** — `MaxCapacity` (1 = ห้ามซ้อน) นับเป็นคิว (`Bookings`) หรือคน (`Guests`) · ตัดสินด้วย **การใช้พร้อมกันสูงสุด** ในช่วงนั้น
  (ไม่ใช่ผลรวมทั้งวัน)
- **ทรัพยากรร่วม** (`CapacityGroup`) — บริการที่ใช้ชื่อกลุ่มเดียวกันในเว็บเดียวกันแชร์ความจุ (ห้องประชุมขายทั้งราย 30 นาที/ครึ่งวัน/เต็มวัน ·
  หมอนวดชุดเดียวทุกคอร์ส)
- **เวลาทำการ + วันหยุด** — แถวรายสัปดาห์ (หลายช่วงต่อวันได้ · ช่วง "พัก") · วันพิเศษ: ปิดทั้งวัน / ปิดบางช่วง / เปิดเวลาพิเศษ (แทนรายสัปดาห์)
- **จองล่วงหน้า** ขั้นต่ำ (ชั่วโมง) / สูงสุด (วัน · 0 = วันนี้เท่านั้น) เทียบเวลาไทย (UTC+7)
- **มัดจำ** คงที่ หรือ % (ปัด 2 ตำแหน่ง `AwayFromZero` · ไม่เกินยอด) · **นโยบายยกเลิก** (ยกเลิกฟรีก่อน N ชม. · ค่าธรรมเนียม %) แสดงให้ลูกค้าเห็น
- ข้อความสรุปที่หน้าเว็บแสดง (`Summary` / `PolicySummary`) ประกอบที่ server — หน้าเว็บไม่มีสำเนากติกา

### 2.1b ขอบเขตเพิ่ม (คำสั่งผู้ใช้ผ่านผู้ประสานงาน · 2026-10-22)

- **สวิตช์ "คุม Slot"** (`EnforceSlotCapacity` · ต่อบริการ): เปิด = บล็อกเมื่อทรัพยากรเต็ม (ช่าง 3 คนจองครบ 3 ในช่วงเดียวกัน ⇒ คนที่ 4 ถูกปฏิเสธ
  `CMS-BOOKING-FULL`) · ปิด = ยอมรับจองซ้อนได้ถึง `OverlapLimit` (ว่าง = ไม่จำกัด) — ยังจัดผู้ให้บริการ (ว่างก่อน แล้วงานน้อยสุด)
- **คิวสำรอง (Waiting List)** (`WaitlistEnabled`): รอบเต็ม (ไม่ว่าเพราะความจุ/ผู้ให้บริการ/เพดานจองซ้อน) ⇒ บันทึกเป็น `BookingStatus.Waitlisted`
  (ไม่กินความจุ · ไม่ออกเอกสาร ERP) · ลำดับคิว = `BookingSlotEngine.WaitlistPosition` (เวลาลงคิว → Id ภายในรอบเดียวกัน · คิดสดจากใบที่เหลือ ⇒ มีคนออก
  ลำดับขยับขึ้นเอง) · มีคนยกเลิก/ไม่มา ⇒ `CmsBookingService.PromoteWaitlistAsync` ไล่คิวของวันนั้นทั้งไซต์ตามลำดับ ให้ `Decide` ตัดสินใหม่ทีละใบ
  (ข้ามด่านล่วงหน้าขั้นต่ำ) ผ่าน ⇒ รอยืนยัน/ยืนยันแล้ว + จัดผู้ให้บริการ + ออกเอกสาร ERP (ไม่ใช่ Lead) + แจ้งผู้กดใน `ErpNotices` ·
  ตั้งสถานะคิวสำรองเองไม่ได้ (`CMS-BOOKING-WAITLIST-MANUAL`) · แอดมิน "ยืนยันนอกคิว" ได้ (รับเกิน — ตัดสินใจของคน)
- **ค่าตั้งระดับไซต์ + override รายบริการ**: ตาราง `SiteBookingSettings` (ระยะเวลา · เผื่อก่อน/หลัง · ล่วงหน้าขั้นต่ำ/สูงสุด) · บริการติ๊ก
  `UseSiteDefaults` ⇒ รับค่าไซต์ · ไม่ติ๊ก (ข้อมูลเดิมทั้งหมด) ⇒ ค่าของบริการเอง · ประกอบที่ `BookingSlotEngine.RulesOf(svc, DefaultsOf(site))` ที่เดียว
- **ผู้ให้บริการ/ห้อง/อุปกรณ์** (`SiteBookingResource` + `SiteBookingResourceService` = บริการที่รับได้ + `SiteBookingResourceSlot` = เวลาเข้า-ออกงาน
  รายสัปดาห์ · วันหยุด/วันลา) · โหมดต่อบริการ `ResourceMode`: ไม่ใช้ (เดิม) · **บังคับระบุ** (ไม่เลือก = `InvalidRequest`) · **ลูกค้าเลือกเอง**
  (ไม่เลือก = ระบบจัดให้) · **ระบบจัดสรรอัตโนมัติ** (ไม่รับค่าจาก client) · กติกาจัดสรร deterministic `BookingSlotEngine.PickResource`:
  อยู่ในเวลางาน + ว่างพอ → งานของวันนั้นน้อยสุด (นาที) → ลำดับที่ตั้ง → ชื่อ (ordinal) → Id · ผู้ให้บริการคือของที่ใช้ร่วมข้ามบริการ
  (งานบริการอื่นที่ทับ ⇒ ไม่ว่าง) · ไม่มีใครเข้างานช่วงนั้น ⇒ ไม่เปิดรอบ (`Closed` ไม่ใช่ `Full`) · ยังไม่ผูกใครเลย ⇒ นับความจุของบริการ (cold-start)
- **คลาสกลุ่ม** (สำหรับทีม L ต่อยอดคอร์ส/เครดิต): `CapacityUnit = Guests` + `MaxCapacity` = ที่นั่งต่อรอบ · ไม่ใช้ผู้ให้บริการ = รอบละ N ที่นั่ง ·
  ใช้ผู้ให้บริการ (ครู) = การจองบริการเดียวกัน + เวลาเริ่มเดียวกัน + ครูคนเดียวกัน คือ "รอบเดียวกัน" ใช้ที่นั่งร่วม · งานอื่นของครูที่ทับ ⇒ รอบนั้นเปิดไม่ได้
- ล็อก: `CreateBookingAsync` / `PromoteWaitlistAsync` ตัดสินใต้ `AdvisoryLockKey.CmsBookingSlot` **ระดับไซต์** (ผู้ให้บริการใช้ร่วมข้ามบริการ)

### 2.1c API/ชนิดที่ทีมอื่นเรียกได้ (เช่น ทีม L — คอร์ส/เครดิต)

| ชนิด/ฟังก์ชัน | ใช้ทำอะไร |
|---|---|
| `Helpers/BookingSlotEngine.RulesOf(SiteBookingService, BookingSiteDefaults?)` | ประกอบกติกาจาก entity (ห้ามประกอบเอง) · `DefaultsOf(SiteBookingSettings?)` |
| `BookingSlotEngine.Available(rules, date, rows, occupied, nowLocal, guests, units, resources, requestedResource, includeFull)` | รอบที่จองได้ของวัน (+ รอบเต็มสำหรับคิวสำรอง) |
| `BookingSlotEngine.Decide(..., resources, requestedResource, ignoreAdvance)` → `BookingSlotDecision` (`Allowed` · `Reason` · `Message` · `Slot` (`Start`/`BookedEnd`/`BookedMinutes`/`ResourceId`/`Remaining`) · `CanWaitlist(rules)`) | ด่านตัดสินตัวเดียว — ทีม L เรียกก่อนตัดเครดิต/ลงคอร์ส |
| `BookingSlotEngine.PickResource` · `Covers` · `Candidates` · `WaitlistPosition` · `TotalPrice` · `Deposit` · `Summary` · `PolicySummary` | ชิ้นส่วนย่อย (pure) |
| ชนิด: `BookingSlotRules` · `BookingScheduleRow` · `BookingOccupancy` (`ServiceId`/`ResourceId`) · `BookingResourceInfo` · `BookingSiteDefaults` · `BookingSlotOption` · enum `BookingSlotMode`/`BookingCapacityUnit`/`BookingResourceMode`/`BookingResourceKind` · `BookingStatus.Waitlisted` | |
| `ICmsBookingService.CreateBookingAsync` | ทางเข้าเดียวที่บันทึกการจอง (ล็อก + ตัดสิน + เลข + ERP) — ทีม L ควรเรียกตัวนี้ (หรือแยกเมธอดภายในที่รับ "ชำระด้วยเครดิต") ไม่เขียน `SiteBookings` เอง |
| endpoint: `GET/PUT …/booking/settings` · `GET/POST/PUT/DELETE …/booking/resources` (เขียน = `CMS.SiteSettings`) · `GET …/booking/presets` · `POST …/booking/available-slots` (`resourceId`) · `POST …/booking/bookings` (`units` · `resourceId`) | |

### 2.2 Server ตัดสินตอนสร้างการจอง

`CmsBookingService.CreateBookingAsync`: transaction → `pg_advisory_xact_lock` ต่อทรัพยากร (`AdvisoryLockKey.CmsBookingSlot` · บริการ หรือกลุ่ม) →
โหลดตาราง + การจองของวัน → `BookingSlotEngine.Decide` → ผ่านแล้วค่อยออกเลข + บันทึก → commit → ขั้น ERP (เส้นเดิม) ·
ไม่ผ่าน ⇒ `BusinessRuleException` ข้อความไทย (`CMS-BOOKING-FULL` = 409 · อื่น ๆ 400) · เวลาจบ/ระยะ/ยอด/มัดจำมาจาก server ไม่ใช่ client ·
หน้าเวลาว่าง (`GetAvailableSlotsAsync`) ใช้ `Available` ตัวเดียวกัน ⇒ สิ่งที่เห็น = สิ่งที่จองได้

### 2.3 ข้อมูลเดิมยังทำงาน

`Helpers/BookingEngineMigration` (ADD COLUMN IF NOT EXISTS · DEFAULT = พฤติกรรมเดิม): `SlotMode`=0 (Windows) · `BufferBeforeMinutes`=0 ·
`CapacityUnit`=0 (คิว) · `MaxUnitsPerBooking`=1 · `SiteBookings.Units`=1 · ไม่แต่งเวลาทำการให้บริการที่ไม่เคยตั้ง (หน้า CMS เตือน
"ยังไม่ได้ตั้งเวลาทำการ") · ผู้เรียก API เดิมที่ไม่ส่ง `Schedule` ⇒ ไม่แตะแถวเวลา · การจองเดิมไม่ถูกแตะ

> ⚠️ ความต่างจากพฤติกรรมเดิมที่ตั้งใจ (โหมด Windows): ① แถวเวลาของ "วันที่เฉพาะ" ที่ไม่ใช่ปิด **แทน** แถวรายสัปดาห์ของวันนั้น (เดิมรวมกัน) ·
> ② การจองที่เวลา**ทับ**ช่วง (ไม่ใช่แค่เริ่มในช่วง) ถูกนับ · ③ จองนอกตาราง/วันหยุด/เต็ม ถูกปฏิเสธ (เดิมผ่าน)

### 2.4 แม่แบบตามประเภทธุรกิจ — `CmsBookingServiceSeeder.Presets` (ตัวตั้งเดียว)

สปา/นวด · ทำเล็บ/เสริมสวย · คลินิก · บริการมืออาชีพ · ฟิตเนส/คลาส · สนามกีฬารายชั่วโมง · ห้องประชุม/coworking · รถเช่า/อุปกรณ์ ·
ร้านอาหาร · คาเฟ่ · โรงเรียน · อสังหา — ทุกตัวมีเวลาทำการ/ความละเอียด/buffer/ความจุ/ล่วงหน้า/มัดจำ/ยกเลิก · seed ตอนสร้างเว็บใช้ตารางเดียวกัน ·
หน้า CMS: ปุ่ม "เพิ่มจากแม่แบบ" (`GET …/booking/presets`) เติมฟอร์มให้แก้ก่อนบันทึก · เทสต์: ทุกแม่แบบผ่านการตรวจค่าตั้ง + มีรอบว่างอย่างน้อย 1 วัน/สัปดาห์

### 2.5 UI

- `cms-edit.html` แท็บ "บริการจอง": ฟอร์ม 6 หมวด (สิ่งที่ให้จอง · ความละเอียดรอบ · ความจุ · เวลาทำการรายสัปดาห์ · วันหยุด/วันพิเศษ ·
  ล่วงหน้า/มัดจำ/ยกเลิก) · hydrate ครบทุกฟิลด์ · ปุ่ม "ดูรอบว่าง" (เรียก endpoint เดียวกับหน้าเว็บ) · ป้ายเตือนบริการที่ไม่มีเวลาทำการ
- `storefront.html` ตัวเลือกเวลา: รูป · สรุปรอบ/นโยบาย · จำนวนช่วง (ถ้าจองติดกันได้) · จำนวนคน (ถ้านับเป็นคน) · ป้ายเช้า/บ่าย/ทั้งวัน ·
  ราคาของรอบ · จองไม่ผ่าน ⇒ ข้อความของ server + โหลดเวลาว่างใหม่
- `cms-bookings.html`: `?siteId=` ล็อกเฉพาะไซต์ (ซ่อนตัวเลือก — รองรับเมนูใต้แต่ละไซต์ของทีม C) · ตัวกรองวันที่/ค้นหาใช้พารามิเตอร์ที่ server รู้จัก ·
  สถิติอ่าน `totalCount`

## 3. Backlog — ฟีเจอร์ที่ควรมีต่อ (เรียงตามคุณค่า/แรง)

| ลำดับ | ฟีเจอร์ | ทำไม / ธุรกิจที่ได้ | ร่างการออกแบบ | ขึ้นกับ |
|---|---|---|---|---|
| ~~B1~~ | ✅ ทำแล้ว (§2.1b) — เหลือ: ลูกค้าเห็นรูป/ประวัติผู้ให้บริการ · ราคาต่างตามผู้ให้บริการ · ทักษะระดับ (senior/junior) | | | |
| B1x | **ตารางพนักงาน (staff resource)** — เดิม — เลือกช่าง/หมอ/เทรนเนอร์ · ใครทำบริการไหนได้ · เวลาเข้างาน/วันลาของแต่ละคน | สปา · ร้านทำผม · คลินิก · ฟิตเนส (ตอนนี้ใช้ `CapacityGroup` + ความจุแทน "จำนวนคนที่ว่าง" — ลูกค้าเลือกคนไม่ได้) | ตาราง `SiteBookingResource(Kind=Staff/Room/Asset, Name, Schedule)` + `SiteBookingServiceResource` (M:N) · engine: รอบว่างเมื่อมี resource ที่ทำบริการได้ ≥ 1 ตัวว่าง (เลือกให้อัตโนมัติ หรือให้ลูกค้าเลือก) · การจองเก็บ `ResourceId` · ใช้ `BookingSlotEngine.Candidates`/`PeakUsage` ต่อ resource | — |
| B2 | **ยกเลิก/เลื่อนนัดโดยลูกค้าเอง + คิดค่าธรรมเนียมตามนโยบาย** | ทุกธุรกิจ — `FreeCancellationHours`/`CancellationFeePercent` ตอนนี้แสดงเฉย ๆ ยังไม่คิดเงิน | ลิงก์จัดการการจองแบบโทเคน (แบบ `booking-manage.html` ของร้านอาหาร) · ตัวตัดสินค่าธรรมเนียม pure (เพดาน = มัดจำ เหมือน `LodgingBookingGuards.CappedCancellationFee`) · ต่อเส้นมัดจำเดิม (`CmsBookingCancelPolicy`) — ริบ/คืนผ่านศูนย์มัดจำ | B5 |
| B3 | **แจ้งเตือนก่อนนัด (reminder)** อีเมล/LINE/SMS 24 ชม. + 2 ชม. | ลด no-show ทุกธุรกิจ (คอลัมน์ `ReminderSent/ReminderSentAt` มีแล้วแต่ไม่มี job) | `BackgroundService` ผ่าน `Helpers/JobLock` + watermark ต่อบริษัท · เทมเพลตข้อความต่อเว็บ · ประทับ `ReminderSentAt` หลังส่งถึงจริงเท่านั้น (F2 #3) | — |
| B4 | **ค่าปรับไม่มาตามนัด (no-show fee)** | สนาม · ร้านอาหารคอร์สพิเศษ · คลินิกเฉพาะทาง | สถานะ `NoShow` มีแล้ว ⇒ เมื่อกด "ลูกค้าไม่มา" ริบมัดจำตามนโยบาย (ศูนย์มัดจำ · ใบมัดจำ §78/1) · ไม่มีมัดจำ = บันทึกยอดค้างใน CRM ไม่ออกใบเอง | B2 |
| B5 | **ชำระมัดจำออนไลน์ตอนจอง** | รถเช่า · สนาม · คอร์สราคาสูง (ตอนนี้ PrePayment ออกใบรับมัดจำทันทีแม้ยังไม่ได้รับเงินจริง — ⚠️ ควรตรวจ) | `PaymentIntent` + `IPaymentProvider` (Omise) ผ่าน `PublicPaymentController` แบบที่พัก · hold รอบไว้ N นาทีระหว่างรอจ่าย (สถานะ `Pending` + หมดอายุ) | PAYMENT_GATEWAY_DESIGN |
| ~~B6~~ | ✅ คิวสำรองพื้นฐานทำแล้ว (§2.1b) — เหลือ: แจ้งลูกค้าอัตโนมัติเมื่อได้คิว (ขึ้นกับ B3) · ถือรอบ N นาทีให้ตอบรับ · คิวสำรองข้ามรอบ ("เวลาไหนก็ได้วันนี้") | | | |
| B6x | **Waitlist** — เดิม — รอบเต็มแล้วลงชื่อรอ · มีคนยกเลิกแจ้งอัตโนมัติ | คลาสฟิตเนส · คลินิก · ร้านอาหาร | ตาราง `SiteBookingWaitlist(ServiceId, Date, Start, Units, Guests, Contact, NotifiedAt, ExpiresAt)` · ตอนยกเลิกเรียก `BookingSlotEngine.Decide` กับคิวรอตามลำดับ · ให้สิทธิ์ถือรอบ 15–30 นาที | B3 |
| B7 | **แพ็กเกจ/คอร์ส (บัตร 10 ครั้ง · คอร์สเรียน 8 ครั้ง)** | สปา · ฟิตเนส · สถาบันสอน | `SiteBookingPackage(ServiceIds, Credits, ValidDays, Price)` + `SiteCustomerPackage(Remaining, ExpiresAt)` · ขายแพ็กเกจ = ใบรับเงินล่วงหน้า (มัดจำ/รายได้รอรับรู้) · ใช้แต่ละครั้งรับรู้รายได้ตามสัดส่วน (TFRS for NPAEs บทที่ 6) | B5 |
| B8 | **จองข้ามวัน / หลายวัน** (รถเช่า 3 วัน · อุปกรณ์ทั้งสัปดาห์) | รถเช่า · อุปกรณ์ · coworking รายสัปดาห์ | การจองมี `EndDate` · engine เพิ่มโหมด `MultiDay` (ตรวจความจุทุกวันในช่วง) · ราคาต่อวัน × วัน · เวลารับ/คืน | — |
| B9 | **ราคาต่อคน / ราคาตามช่วงเวลา (peak/off-peak) / วันหยุด** | สนาม (เย็นแพงกว่า) · คลาส (ราคาต่อที่นั่ง) | ตาราง price rule ต่อบริการ (วัน·ช่วงเวลา·ราคา) → `BookingSlotEngine.TotalPrice` รับ rules · ห้ามคำนวณราคาในหน้าเว็บ | — |
| B10 | **Calendar sync (Google/Outlook · iCal feed)** | ทุกธุรกิจที่ใช้ปฏิทินอยู่แล้ว | ขั้นแรก: iCal feed อ่านอย่างเดียวต่อบริการ/พนักงาน (URL ลับต่อไซต์) · ขั้นสอง: ดึงเวลาที่ไม่ว่างจาก Google Calendar มาเป็นช่วงปิด (OAuth ต่อพนักงาน) | B1 |
| B11 | **คำถามตอนจอง (intake form) + แนบไฟล์** | คลินิก (อาการ) · อสังหา (รหัสทรัพย์) · ซ่อม | ใช้โครง `ConfigJson.extraFields` ของ ContactForm เดิม · ข้อมูลสุขภาพ = ข้อมูลอ่อนไหว PDPA ม.26 (เข้ารหัส + RoPA) | PDPA |
| B12 | **ปฏิทินมุมมองพนักงาน (day/week view) + ลากเลื่อนนัด** | ทุกธุรกิจ (ตอนนี้ `cms-bookings.html` เป็นตาราง) | หน้าใหม่อ่านจาก endpoint รายวัน · การเลื่อนผ่าน `Decide` ตัวเดียว (ไม่ข้ามด่าน) | B1 |
| B13 | **เวลาทำการระดับเว็บ (ตั้งครั้งเดียวใช้ทุกบริการ)** | ร้านที่มีหลายบริการ — ตอนนี้ตั้งต่อบริการ (มีปุ่มคัดลอกวันจันทร์) | `Site.BookingHours` เป็นค่าตั้งต้น · บริการ override ได้ · resolver กลางตัวเดียว (กฎ #4 "Resolver กลาง") | — |
| B14 | **Db test การจองพร้อมกัน** (รอบสุดท้าย N คำขอพร้อมกัน ⇒ ได้ใบเดียว) | ยืนยันล็อกจริง (แบบ `Db/LodgingDoubleBookingDbTests`) | Category=Db · job db-test | — |
| B15 | **การจองข้ามเที่ยงคืน** (บาร์/คาราโอเกะ 22:00–02:00) | ร้านกลางคืน | engine ตอนนี้จำกัดรอบในวันเดียว (ปลาย 24:00) · ต้องเก็บ EndDate หรือ offset | B8 |

## 4. กติกาที่ต้องรักษา (สำหรับผู้แก้ต่อ)

- คำนวณรอบว่าง/ตัดสินจองได้ไหม **ผ่าน `BookingSlotEngine` เท่านั้น** (หน้าเวลาว่าง · สร้างการจอง · เลื่อนนัดในอนาคต · waitlist) ·
  ประกอบกติกาจาก entity ผ่าน `BookingSlotEngine.RulesOf` / `RowOf` ตัวเดียว
- เส้นที่ "ตัดสินแล้วเขียน" ต้องอยู่ใต้ `AdvisoryLockKey.CmsBookingSlot` ของทรัพยากรเดียวกัน (บริการ หรือกลุ่ม) ภายใน transaction
- ค่าตั้งใหม่ของบริการ: ฟอร์ม → `CreateBookingServiceRequest` → `CmsBookingService.ApplySettings` → entity → migration
  (`BookingEngineMigration`) → `MapService` (echo) → hydrate (`_bsHydrate`) → ผู้อ่านใน engine — ครบทุกข้อในคอมมิตเดียว
- แม่แบบธุรกิจ: แก้ที่ `CmsBookingServiceSeeder.Presets` ที่เดียว (หน้า CMS อ่านผ่าน API)
