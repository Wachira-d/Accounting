# รอบ 209 ทีม C — การจองคลาสเรียน (พิลาทิส / โยคะ / ฟิตเนส) รองรับกระบวนการทั้งหมดไหม

> อ่านอย่างเดียว · หลักฐาน `file:line` (path ใต้ `Accounting/` เว้นแต่ระบุ) · ✅ ทำได้ / ⚠️ ทำได้แต่ขาด / ❌ ไม่มี (รวม "มีแต่ไม่ถูกเรียก" F2 #2) · ขนาด S/M/L ·
> ยังไม่ได้คอมไพล์/รันในเครื่องนี้ (ไม่มี .NET SDK) — ทุกข้ออ่านจากโค้ด ไม่ใช่จากการกดจริง
> เอกสารตั้งต้น: `docs/BOOKING_ENGINE_DESIGN.md` · `docs/COURSE_CREDIT_DESIGN.md` §1–§5 · `DOCUMENT_FLOW.md` §6.5c/§6.6

## §0 สรุป

สตูดิโอพิลาทิสวันนี้ **เปิดร้านได้แต่ต้องใช้สองระบบที่ไม่คุยกันครบ** และสมาชิกยัง "จองด้วยบัตรเองไม่ได้":

1. **มีสองเครื่องจองคลาสคู่ขนาน** — (ก) เครื่องจอง CMS `SiteBookingService` (`SlotMode=Windows` + `CapacityUnit=Guests` + `WaitlistEnabled` = คลาสกลุ่มรอบตายตัว · แม่แบบ "ฟิตเนส" seed ให้แล้ว `Services/Implementations/Cms/CmsBookingServiceSeeder.cs:88-90`) มีความจุ · คิวสำรอง · เตือน · ครูเป็น `SiteBookingResource` · POS · เอกสาร ERP ครบ และ (ข) รอบเรียนของโมดูลคอร์ส `CreditClassSession` (`Models/Entities/CourseCredit.cs:120-133`) ที่ตั้งทีละรอบ ไม่มีคิวสำรอง ไม่มีแจ้งเตือน ไม่มีออนไลน์ ครูเป็น `Employee` — ตารางเดียวกัน ("คลาสจันทร์ 18:00") จึงมีได้ 2 ความจริง · สมุดเครดิตเชื่อม (ก) ด้วย `ExternalRef` แล้ว (`Helpers/CreditBookingLink.cs`) แต่หน้าร้านยังไม่มีทางให้สมาชิกเลือก "ใช้บัตร"
2. **ตารางคลาสประจำ "จ/พ 18:00 × 12 สัปดาห์" ทำได้ครึ่งเดียว** — แถวรายสัปดาห์ของ Windows ได้ (`Models/Entities/CmsBooking.cs:96-114`) แต่ไม่มีวันเริ่ม/วันสิ้นสุดของเทอม ไม่มี entity "รอบเรียน" ให้แปะครู/ยกเลิก/หมายเหตุต่อรอบ และไม่มีมุมมองปฏิทิน (backlog B12 `docs/BOOKING_ENGINE_DESIGN.md:120`) — ครูต่อรอบ/ครูแทน ทำได้เฉพาะ "ต่อการจอง" (`AssignResourceAsync`) ไม่ใช่ต่อคลาส
3. **สมาชิกไม่มีหน้าของตัวเอง** — หน้าร้านไม่มี login สมาชิก (ไม่มีการเรียก `portal/login` ใน `wwwroot/storefront.html` · endpoint มี `Controllers/CmsCustomerController.cs:70-88`) ⇒ ดูเครดิตคงเหลือ/วันหมดอายุ/จองด้วยบัตรออนไลน์ ❌ (COURSE_CREDIT_DESIGN B8 ยังค้าง) · `PayWithCredit` รับ `CustomerId` จาก body ของ endpoint `[AllowAnonymous]` (`Controllers/CmsBookingController.cs:171-180` · `Services/Implementations/CmsBookingService.Credits.cs:17-32`) — ไม่มีการพิสูจน์ว่าผู้เรียกคือลูกค้าคนนั้น
4. **บั๊กเงิน 1 จุด**: จองหลายที่นั่ง (`GuestCount`>1) ด้วยบัตร กันเครดิตแค่ 1 (`CmsBookingService.Credits.cs:38-43` ไม่ส่ง `Credits` ⇒ `CourseCreditService.cs:793-795` = `CreditsForSession(unit, นาที, 1m)`) ⇒ สมาชิกพาเพื่อน 3 คนจ่าย 1 เครดิต
5. **ขาย**: แพ็กเกจครั้ง/ชั่วโมง + หมดอายุ + ยกเลิกฟรี + 21713→42000 ตามการใช้ **ครบและถูกต้อง** (`DOCUMENT_FLOW.md` §6.6) · drop-in/ทดลองเรียน = บริการ CMS ปกติ ✅ · **ไม่มี**: สมาชิกรายเดือนไม่จำกัด · แช่บัตร (freeze) · ขยายอายุ · โอน/ของขวัญ · ราคาต่อที่นั่ง
6. **หลังเรียน**: รายงานผู้ให้บริการ (ยอด/นาที/งาน · CSV) มี `Helpers/BookingProviderReport.cs` แต่ **ไม่ต่อค่าคอม/เงินเดือนครู** (ไม่มีผู้อ่าน `SiteBookingAssignment` ใน `CommissionService`/`Payroll`) · ไม่มี fill-rate · retention · เตือนเครดิตใกล้หมดถึงสมาชิก (ตัวเลข "หมดอายุใน 7 วัน" อยู่บนจอแอดมินเท่านั้น `CourseCreditService.cs:489-501`)
7. **แจ้งเตือน**: มีจริง 2 เรื่อง (เตือนก่อนนัด · ได้คิวสำรอง) ทางอีเมล/LINE (`Services/Implementations/Cms/BookingNotifier.cs`) — ไม่มี ยืนยันการจอง · ร้านยกเลิกคลาส · เครดิตใกล้หมด · SMS

ลำดับคุ้มค่า: **C1 ปิดบั๊กเครดิต×ที่นั่ง + ด่านตัวตนลูกค้า (S)** → **C2 รอบเรียน `SiteClassSession` + เทอม + ปฏิทิน (L)** → **C3 portal สมาชิก (จอง/ดูเครดิต/ยกเลิกเอง) (M)** → **C4 แจ้งเตือนครบชุด (S/M)** → **C5 สมาชิกไม่จำกัด + freeze/ขยาย/โอน (M)** → **C6 ค่าตอบแทนครูต่อคลาส/ต่อหัว (M)** → **C7 รายงาน fill-rate/retention (S)**

## §1 กระบวนการทีละขั้น

### 1.1 ตั้งค่า (เจ้าของสตูดิโอ)

| บทบาท | ขั้น | สถานะ | หลักฐาน | ช่องว่าง | ขนาด |
|---|---|---|---|---|---|
| เจ้าของ | สร้างชนิดคลาส (Reformer / Mat) เป็นบริการ | ✅ | `CmsBookingService.cs:38-122` · แม่แบบ "ฟิตเนส" (`CmsBookingServiceSeeder.cs:88-92,124`) · ฟอร์ม `wwwroot/pages/cms-edit.html:843-861` | — | — |
| เจ้าของ | ตารางประจำ จ/พ 18:00 (รายสัปดาห์) | ⚠️ | Windows + แถว `DayOfWeek` (`CmsBooking.cs:96-114` · engine `Helpers/BookingSlotEngine.cs:214-262`) | ไม่มี **วันเริ่ม/วันสิ้นสุดเทอม** (12 สัปดาห์) · ปิดบางวันต้องเพิ่ม `SpecificDate+IsBlocked` ทีละวัน | M |
| เจ้าของ | เห็นตารางคลาสเป็นปฏิทิน | ❌ | `cms-bookings.html:55-80` เป็นตารางรายการ + ตัวกรอง · หน้าร้านเลือกวันทีละวัน (`storefront.html:1719-1733`) · backlog B12 `BOOKING_ENGINE_DESIGN.md:120` | ไม่มี week view / ไม่มี endpoint "รอบของสัปดาห์" | M |
| เจ้าของ | ความจุต่อคลาส (8 reformer) | ✅ | `MaxCapacity` + `CapacityUnit=Guests` (`CmsBooking.cs:21,33`) · ต่อแถวเวลา `BookingScheduleRow.Capacity` (`BookingSlotEngine.cs:54,260`) | ห้อง/อุปกรณ์เป็น `Resource` ได้ แต่คลาสกลุ่มนับที่นั่งจากบริการเท่านั้น (`CmsBookingResource.cs:54`) — ตั้งซ้ำสองที่ได้ | S |
| เจ้าของ | ครูประจำต่อคลาส | ⚠️ | ครู = `SiteBookingResource(Kind=Staff)` + บริการที่รับ (`CmsBookingResource.cs:47-75`) · โหมด Auto/CustomerChoice (`BookingEngineEnums.cs`) · "รอบเดียวกัน = บริการ+เวลา+ครู" (`BookingSlotEngine.cs:304-317`) | ครูถูก "จัด" ตอนลูกค้าจอง ไม่ใช่ "ประกาศ" บนตาราง ⇒ ลูกค้าไม่เห็นว่าคลาสนี้ครูใครก่อนจอง · ไม่มี entity รอบเรียนให้ตั้งครูต่อรอบ | M (C2) |
| เจ้าของ | ครูแทน (substitute) | ⚠️ | ต่อการจองทีละใบ `AssignResourceAsync` (`CmsBookingService.Notify.cs:98-141`) · ตอนเริ่มบริการ `StartServiceAsync` (`CmsBookingService.Workflow.cs:161`) | เปลี่ยนครู 1 คลาส 8 คน = กด 8 ครั้ง · ไม่แจ้งสมาชิก | S หลัง C2 |
| เจ้าของ | ยกเลิกคลาสทั้งรอบ + แจ้ง + คืนเครดิต | ⚠️ | CMS: ยกเลิกทีละการจอง `UpdateBookingStatusAsync` ⇒ คืนเครดิต `SettleCreditOnStatusAsync` (`Credits.cs:71-103`) · โมดูลคอร์ส: `CancelSessionAsync` คืนทุกคน (`CourseCreditService.cs:548-577`) | ไม่มี "ยกเลิกรอบ" ฝั่ง CMS · ทั้งสองทางไม่แจ้งลูกค้า (`_notifier` ถูกเรียกเฉพาะ `Notify.cs:75` + `BookingReminderJob.cs:126`) | M |
| เจ้าของ | ตารางงาน/วันลาครู | ✅ | `SiteBookingResourceSlot` (`CmsBookingResource.cs:90-100`) · ไม่มีใครเข้างาน ⇒ ไม่เปิดรอบ (`BookingSlotEngine.cs:401`) | — | — |
| เจ้าของ | ครู (CMS Resource) ↔ พนักงาน (Employee) | ⚠️ | `SiteBookingResource.StaffUserId` = **User** (`CmsBookingResource.cs:59`) · โมดูลคอร์สใช้ `Employee` (`CourseCredit.cs:128`) | ตัวตนครูสองแบบ ⇒ เชื่อมเงินเดือน/ค่าคอมไม่ได้ (ดู 1.5) | S |

### 1.2 ขาย (หน้าร้าน / ออนไลน์)

| บทบาท | ขั้น | สถานะ | หลักฐาน | ช่องว่าง | ขนาด |
|---|---|---|---|---|---|
| หน้าร้าน | แพ็กเกจ 10 ครั้ง/30 วัน · Hour pass | ✅ | `CreditPackage` (`CourseCredit.cs:36-62`) · UI `course-credits.html:105-122` | — | — |
| หน้าร้าน | ขาย: ใบกำกับ/ใบเสร็จ · Cr 21713 · VAT ครบตอนรับเงิน | ✅ | `SellAsync` (`CourseCreditService.cs:145-268`) · `DOCUMENT_FLOW.md` §6.6 · `CourseCreditMath.SaleDocumentShape` (`CourseCreditMath.cs:97-104`) | — | — |
| หน้าร้าน | รับรู้รายได้ตามการเข้าเรียน Dr 21713 / Cr 42000 | ✅ | `ConsumeAsync` + `PostRecognitionAsync` (`CourseCreditService.cs:872-939`) · ปัด AwayFromZero ครั้งสุดท้ายรับส่วนที่เหลือ (`CourseCreditMath.RecognizeOnUse`) · breakage ตอนหมดอายุ | — | — |
| ออนไลน์ | ซื้อแพ็กเกจจากร้านออนไลน์ | ✅ | `SiteProduct.CreditPackageId` → `IssuedDocumentHooks.TryActivateCoursePassAsync` (`COURSE_CREDIT_DESIGN.md` §5 B2) | ต้องมีผู้ติดต่อ ERP ผูกบัญชีลูกค้า | — |
| หน้าร้าน | Drop-in คลาสเดียว (จ่ายเงิน) | ✅ | บริการ CMS `BookingType=Appointment/PrePayment` · ราคา/มัดจำ (`CmsBooking.cs:53-59`) · เอกสาร `SyncBookingToErpAsync` | ราคาคิด **ต่อการจอง × ช่วง** ไม่ใช่ต่อที่นั่ง (`BookingSlotEngine.TotalPrice` `:485` ไม่รับ guests) — จอง 3 ที่นั่งจ่าย 1 (B9 `BOOKING_ENGINE_DESIGN.md:117`) | S |
| หน้าร้าน | ทดลองเรียนฟรี | ⚠️ | แม่แบบ `TrialClass` Lead ฿0 (`CmsBookingServiceSeeder.cs:85-86`) | ไม่มีด่าน "ทดลองได้คนละ 1 ครั้ง" | S |
| หน้าร้าน | สมาชิกรายเดือนไม่จำกัด | ❌ | `CreditUnit = {Session, Hour}` เท่านั้น (`CourseCredit.cs:6`) | ไม่มีชนิด "ช่วงเวลา" · รับรู้รายได้ตามเวลา (ไม่ใช่ตามการใช้) ต้องผูกกับ `RevenueRecognition` หรือสูตรใหม่ · ต้องมีเพดานจอง/วัน | M (C5) |
| หน้าร้าน | ขยายอายุ / แช่บัตร (freeze) | ❌ | ไม่มี method/ช่อง (grep `freeze|extend|ขยายอายุ` ใน `Services/Implementations/CourseCredit/` = 0) · B5 `COURSE_CREDIT_DESIGN.md:213` | `ExpiresOn` ตรึงตอนขาย แก้ไม่ได้จากจอ | S |
| หน้าร้าน | โอนเครดิต / ของขวัญ | ❌ | B5 เดียวกัน · บัตรผูก `ContactId` เดียว (`CourseCredit.cs:71`) | สมุด `Adjust` รองรับโครง แต่ไม่มีเส้นโอนข้ามบัตร | M |
| หน้าร้าน | คืนเงิน/ยกเลิกบัตร | ✅ | `CreditPassDocumentRule` + `DraftRefundCreditNoteAsync` (`CourseCreditService.cs:371-441`) | — | — |

### 1.3 สมาชิกจอง

| บทบาท | ขั้น | สถานะ | หลักฐาน | ช่องว่าง | ขนาด |
|---|---|---|---|---|---|
| สมาชิก | จองออนไลน์ด้วยเครดิต | ❌ | API มี (`CreateBookingRequest.PayWithCredit/CreditPassId` `Models/DTOs/Cms/BookingDtos.cs:303-306`) แต่หน้าร้านไม่ส่ง (`storefront.html:1764-1774` body ไม่มี customerId/payWithCredit) · ไม่มี login สมาชิกในหน้าร้าน (grep `portal` = 0) · `COURSE_CREDIT_DESIGN.md:212` "ยังไม่มี UI ให้แขกเลือก" | **C3** portal สมาชิก | M |
| สมาชิก | ใช้บัตรจองโดยพนักงานหลังบ้าน | ✅ | `course-credits.html:221-260,559` (รอบของโมดูลคอร์ส) · CMS: API เท่านั้น | หลังบ้าน CMS (`cms-bookings.html`) ไม่มีช่อง "ใช้บัตร" | S |
| ระบบ | ด่านตัวตน "ใช้บัตรของใคร" | ❌ | `POST …/booking/bookings` `[AllowAnonymous]` (`CmsBookingController.cs:171-180`) · `PrepareCreditBookingAsync` ตรวจแค่ `SiteCustomer` อยู่ในบริษัท (`Credits.cs:22-28`) ไม่ตรวจว่าผู้เรียกคือลูกค้าคนนั้น | รู้ `CustomerId` = กันเครดิตบัตรคนอื่นได้ (ไม่ใช่ขโมยเงิน แต่เครดิตถูกยึด/ริบถ้ายกเลิกช้า) | S (C1) |
| ระบบ | เครดิต × จำนวนที่นั่ง | ❌ | `HoldCreditForBookingAsync` ไม่ส่ง `Credits` (`Credits.cs:38-43`) ⇒ `HoldAsync` ใช้ `CreditsForSession(unit, นาที, 1m)` (`CourseCreditService.cs:793-795` · `CourseCreditMath.cs:74-77`) ขณะที่ `GuestCount` กินที่นั่ง N (`BookingSlotEngine.Demand` `:267`) | จอง 3 ที่นั่ง = 1 เครดิต | S (C1) |
| ระบบ | หน้าต่างจอง (เปิด N วันล่วงหน้า · ปิด N ชม.ก่อน) | ✅ | `MaxAdvanceBookingDays/MinAdvanceBookingHours` + ค่าไซต์ (`CmsBookingResource.cs:14-15`) · `BookingSlotEngine.DateRejection/Decide` (`:445-449,462-465`) | — | — |
| ระบบ | ความจุต่อคลาส + คิวสำรอง + เลื่อนอัตโนมัติ | ✅ | `Decide` Full⇒Waitlisted (`BookingSlotEngine.cs:466-470` · `CmsBookingService.cs:820-824`) · `PromoteWaitlistAsync` (`:936-978`) · ถือรอบ `WaitlistHoldMinutes` + job ปล่อย (`BookingReminderJob.cs:175`) | คิวสำรองของ **โมดูลคอร์ส** (`BookAsync` `CourseCreditService.cs:617-620`) ไม่มี — เต็ม = throw | M (C2 ยุบรวม) |
| ระบบ | หน้าต่างยกเลิก (late cancel = ริบเครดิต) | ✅ | `CourseCreditMath.CancelOutcome` + `FreeCancelHours` ตรึงบนบัตร (`CourseCreditService.cs:667-689`) · ลูกค้ายกเลิกผ่านลิงก์ ⇒ นโยบายคอร์ส (`CreditBookingLink.cs:26-29` · `Credits.cs:83-85`) · drop-in ⇒ `BookingCancellationFee` (`DOCUMENT_FLOW.md` §6.5c R-10) | นโยบายอยู่ **2 ที่** (บริการ `FreeCancellationHours` vs แพ็กเกจ `FreeCancelHours`) — การจองด้วยบัตรใช้ของแพ็กเกจ ลูกค้าเห็นของบริการบนหน้าเว็บ (`PolicySummary`) | S (คำตัดสิน §4-1) |
| ระบบ | No-show | ✅ | CMS `NoShow` ⇒ `SettleNoShow` ตัดเครดิต (`CreditBookingLink.cs:32-42`) · โมดูลคอร์ส no-show ก่อนเวลาเรียนไม่ได้ (`CourseCreditService.cs:655-657`) · drop-in ริบมัดจำ (R-11) | ไม่มี no-show **อัตโนมัติ** หลังคลาสจบ (ต้องกดทุกใบ) | S |
| สมาชิก | จองหลายที่นั่ง / พาเพื่อน | ⚠️ | `guestCount` หน้าร้าน (`storefront.html:1771`) · engine นับคน | ราคา/เครดิตไม่คูณที่นั่ง (ข้างบน) | S |
| แขก (ไม่สมัคร) | จอง drop-in | ✅ | ฟอร์มแขก + `BookingFormFields` (`CmsBookingService.cs:794-808`) | — | — |
| สมาชิก | ดูเครดิตคงเหลือ/วันหมดอายุ/ประวัติ | ❌ | มีแต่จอแอดมิน (`course-credits.html:300-360`) · ลิงก์ `my-booking/{token}` ดูได้ทีละการจอง (`storefront.html:1009-1010,1906`) · B8 `COURSE_CREDIT_DESIGN.md:216` | **C3** | M |
| สมาชิก | เลื่อนคลาสที่จองด้วยบัตร | ⚠️ | ปฏิเสธโดยตั้งใจ — ต้องยกเลิกแล้วจองใหม่ (`CmsBookingService.Reschedule.cs:49-54`) | ยกเลิกใหม่เสี่ยงโดนริบถ้าใกล้เวลา — ควรเป็น Release+Hold ในธุรกรรมเดียว | S |

### 1.4 วันเรียน (หน้าร้าน / ครู)

| บทบาท | ขั้น | สถานะ | หลักฐาน | ช่องว่าง | ขนาด |
|---|---|---|---|---|---|
| หน้าร้าน | รายชื่อผู้เรียนต่อคลาส (roster) | ⚠️ | CMS: กรอง วัน+บริการ+ครู (`GetBookingsAsync` `CmsBookingService.cs:1611-1625` · `cms-bookings.html:66-78`) · โมดูลคอร์ส: โมดัลผู้จองต่อรอบ (`course-credits.html:221,535-540`) | CMS ไม่มี "กลุ่มตามรอบ" (จำนวนจอง/ความจุ/คิวสำรองต่อรอบ) · สองหน้าสองรายชื่อ | M (C2) |
| หน้าร้าน | เช็คอิน / มาเรียน / ไม่มา ⇒ ตัดเครดิต + รับรู้รายได้ | ✅ | CMS: สถานะ `Completed/NoShow` ⇒ `SettleCreditOnStatusAsync` (`Credits.cs:71-103`) · ด่าน `BookingStatusTransition` (`Helpers/BookingStatusTransition.cs`) · โมดูลคอร์ส: ปุ่ม มาเรียน/ไม่มา (`course-credits.html:387-398`) | "เสร็จสิ้น" ใน CMS ต้องผ่าน `StartService`→`Complete` (ปุ่ม `cms-bookings.html:151-158`) = 2 คลิกต่อคน · ไม่มี "เช็คอินทั้งคลาส" | S |
| หน้าร้าน | Walk-in จ่ายที่ POS | ✅ | คิวของวัน → เปิดบิล `OpenOrderFromBookingAsync` (`Services/Implementations/PosService.Bookings.cs:184-266`) · ปิดบิลการจองด้วยบัตร ⇒ `SettleAsync` ในธุรกรรมเดียว (`:319-384` · `CreditBookingLink.ForPosClose`) | ขายคอร์สที่ POS ไม่ทำ (คำตัดสิน 161) ✅ ตามนโยบาย | — |
| ครู | หน้าของครู (คลาสของฉัน · ผู้เรียน · เช็คชื่อ) | ❌ | ไม่มีสิทธิ์/หน้าสำหรับครู — ทุกปุ่ม `CmsOrderManage` (`CmsBookingController.cs:271-329`) · `cms-booking-providers.html` เป็นรายงาน | ครูต้องได้สิทธิ์จัดการออเดอร์ทั้งเว็บจึงเช็คชื่อได้ | M |

### 1.5 หลังเรียน

| บทบาท | ขั้น | สถานะ | หลักฐาน | ช่องว่าง | ขนาด |
|---|---|---|---|---|---|
| เจ้าของ | ค่าตอบแทนครูต่อคลาส / ต่อหัว | ❌ | รายงานยอด/นาที/งานต่อผู้ให้บริการ (`Helpers/BookingProviderReport.cs:24-30` · `cms-booking-providers.html:133-195` CSV) · **ไม่มีผู้อ่าน** `SiteBookingAssignment`/`StaffUserId` ใน `CommissionService.cs` · `Payroll/*` (grep = 0) · ค่าคอม POS คิดจากบรรทัดบิล (`PosStaffAttribution`) เท่านั้น | การจองด้วยบัตร (ยอด 0) ⇒ รายงานผู้ให้บริการแสดงรายได้ 0 (`BookingProviderReport.cs:69` ใช้ `TotalAmount`) — ครูสอนคลาสเครดิตล้วนได้ "0 บาท" | M (C6) |
| เจ้าของ | Fill-rate ต่อคลาส/ครู/ช่วงเวลา | ❌ | grep `fill|occupancy` ใน booking/credit = 0 | ข้อมูลครบ (จอง/ความจุ/คิวสำรอง) แต่ไม่มีตัวประกอบ | S (C7) |
| เจ้าของ | Retention / สมาชิกหาย | ❌ | ไม่มี | ใช้ `CreditLedgerEntry.OccurredAt` ต่อ `ContactId` ได้ | S (C7) |
| เจ้าของ | รายงานรายได้รอรับรู้ของคอร์ส | ✅ | `GetSummaryAsync` deferred (`CourseCreditService.cs:489-501`) · JE ผูกสมุด · `revenue-recognition` deferred-account-audit | — | — |
| ระบบ | เตือนเครดิตใกล้หมดอายุ **ถึงสมาชิก** | ❌ | `CourseCreditExpiryJob` ปิดบัตรอย่างเดียว (`Services/Background/CourseCreditExpiryJob.cs:46-69`) · ตัวเลข 7 วันบนจอแอดมิน | ไม่มีช่องทางติดต่อบน `CreditPass` (มีแค่ `ContactId`) | S (C4) |

### 1.6 แจ้งเตือน (ช่องทางที่มี: อีเมล → LINE · SMS ไม่มี `BookingNotifier.cs:7-9`)

| เหตุการณ์ | สถานะ | หลักฐาน |
|---|---|---|
| ยืนยันการจอง (ทันทีหลังจอง) | ❌ | `CreateBookingAsync` ไม่เรียก `_notifier` · มีแค่หน้าจอ + ใบยืนยันดาวน์โหลด (`storefront.html:1796-1805`) |
| เตือนก่อนคลาส (24,2 ชม.) | ✅ | `BookingReminderPlan.DueStage` (`Helpers/BookingReminderPlan.cs:78-90`) · job (`BookingReminderJob.cs:113-131`) · ประทับหลังส่งถึงจริง |
| ได้คิวสำรอง + ถือรอบ | ✅ | `NotifyWaitlistPromotedAsync` (`CmsBookingService.Notify.cs:43-91`) |
| ร้านยกเลิกคลาส / เปลี่ยนครู | ❌ | `UpdateBookingStatusLockedAsync` / `AssignResourceAsync` / `CancelSessionAsync` ไม่ส่งข้อความ |
| เครดิตใกล้หมดอายุ / ใช้ครบ | ❌ | ข้างบน |
| SMS | ❌ | ไม่มีผู้ให้บริการ (B3 `BOOKING_ENGINE_DESIGN.md:110`) |

### 1.7 F2 #2 — ของที่มีแต่ไม่มีผล (ในขอบเขตนี้)

| รายการ | หลักฐาน | ผล | ข้อเสนอ |
|---|---|---|---|
| `SiteBookingSlot.CurrentBookings` + `AvailableSlots` | `CmsBooking.cs:111` · ผู้เขียน 0 · echo `CmsBookingService.cs:1973-1974` (`tools/callers.py CurrentBookings`) | API `GET services/{id}/slots` ตอบ "ว่าง = ความจุเต็ม" เสมอ — ค่าโกหก | ลบ field + คำนวณจาก engine หรือตัด endpoint slots เดิม (ตารางตั้งผ่าน `Schedule` DTO แล้ว) |
| `CreditClassSession.BookingServiceId` | ผู้อ่าน = `CannotBookReason` เฉพาะ `AllowedServiceIds` (`CourseCreditMath.cs:127`) | ไม่ได้ใช้ตาราง/ความจุ/ครูของบริการ CMS เลย (`CourseCredit.cs:117-119` บอกเอง) | ยุบใน C2 |
| `SiteBookingService.Tags` | เขียน/echo เท่านั้น (ไม่พบผู้อ่านเชิงธุรกิจใน `CmsBookingService*.cs`) | ป้ายไม่มีผล | ใช้กรอง/จัดกลุ่มคลาสบนหน้าร้าน หรือลบ |
| `CreditSaleChannel.Pos` | enum (`CourseCredit.cs:17`) · คำตัดสิน 161 ไม่ขายที่ POS | ค่าไม่มีผู้ตั้ง | ปล่อยไว้ได้ (ร่องรอย) — จดใน doc |
| รายงานผู้ให้บริการ "รายได้" ของการจองด้วยบัตร | `BookingProviderReport.cs:69` ใช้ `TotalAmount` = 0 | ตัวเลขผิดความหมายสำหรับสตูดิโอเครดิต | C6: ส่ง `RecognizedAmount` จากสมุดเครดิตเข้าแถว |

## §2 แบบจำลองข้อมูลที่ขาด — ควรต่อยอดเครื่องจอง CMS ไม่ใช่ `CreditClassSession`

**เหตุผลเลือก CMS เป็นแกน**: ความจุ · คิวสำรอง · ถือรอบ · เตือน · ครู/ห้อง · ตารางงานครู · เอกสาร ERP · POS intake · ลิงก์จัดการของลูกค้า — อยู่ฝั่ง CMS ครบแล้ว และสมุดเครดิตเชื่อมผ่าน `ExternalRef` ได้โดยไม่แก้ schema (`CreditBookingLink`) · ฝั่ง `CreditClassSession` มีแค่ capacity นับแถว (`CourseCreditService.cs:617-620`) และครูแบบ `Employee` — ถ้าเลือกขยายฝั่งนี้ต้องสร้างคิวสำรอง/เตือน/ออนไลน์ซ้ำ (ผิด F2 #4 "ตัวตั้งตัวเดียว") · กติกา `BOOKING_ENGINE_DESIGN.md` §4 ก็ระบุให้ทุกเส้นผ่าน `BookingSlotEngine`

**หลักการ**: ตารางประจำ (rule) → **รอบเรียนที่เกิดจริง** (occurrence) → การจอง · วันนี้ระบบมี rule (`SiteBookingSlot` รายสัปดาห์) และการจอง แต่ **ไม่มี occurrence** จึงไม่มีที่แปะ "ครูของรอบนี้ / ยกเลิกรอบนี้ / หมายเหตุ / นับที่นั่ง"

| สิ่งที่เสนอ | ฟิลด์ | ทำไม | ต่อกับของเดิม |
|---|---|---|---|
| **`SiteClassSession`** (ใหม่ · occurrence) | `SiteId, BookingServiceId, Date, StartTime, EndTime, InstructorResourceId?, SubstituteResourceId?, CapacityOverride?, Status{Scheduled,Cancelled}, CancelReason, Notes, GeneratedFromTermId?` · unique (`CompanyId, BookingServiceId, Date, StartTime`) | ที่เดียวของ "คลาสรอบนี้" · roster/เช็คอินทั้งคลาส/ยกเลิกรอบ/ครูแทน/ปฏิทิน อ่านจากตารางนี้ | key เดียวกับที่ engine ใช้นิยาม "รอบเดียวกัน" อยู่แล้ว (`BookingSlotEngine.cs:308` `ServiceId+StartMinute`) ⇒ `SiteBooking.ClassSessionId?` (nullable · ใบเก่า null = ค้นด้วย key) · `Decide` รับ `CapacityOverride`/`Status` เป็นแถว `BookingScheduleRow`(SpecificDate) ที่ประกอบจาก session — **ไม่เพิ่ม engine ที่สอง** |
| **`SiteClassTerm`** (ใหม่ · rule มีขอบ) | `BookingServiceId, ValidFrom, ValidTo, Weekday, Start, End, Capacity, DefaultInstructorResourceId?` | "จ/พ 18:00 × 12 สัปดาห์" + สร้าง occurrence ล่วงหน้า (job/ปุ่ม) · ปิดเทอม = ไม่ generate | ทางเลือกเล็กกว่า: เพิ่ม `ValidFrom/ValidTo` บน `SiteBookingSlot` (`CmsBooking.cs:96-114`) แล้ว `ResolveDay` กรองช่วง — พอสำหรับ "เทอม" แต่ไม่ให้ occurrence |
| `SiteBookingResource.EmployeeId?` | FK `Employee` | ครูคนเดียวทั้ง CMS (`StaffUserId`=User) และคอร์ส/เงินเดือน (`Employee`) · ฐานค่าตอบแทน | `CreditClassSession.TrainerEmployeeId` คงไว้สำหรับ 1-on-1 ที่ไม่ผ่าน CMS |
| `CreditPackage.Kind {Credits, UnlimitedPeriod}` + `PeriodDays, MaxBookingsPerDay, MaxActiveBookings` | สมาชิกรายเดือน | `CreditsTotal` ไม่มีความหมาย ⇒ Hold ไม่ตัดเครดิตแต่ตรวจเพดาน · รับรู้รายได้ **ตามเวลา** (straight-line รายวัน/รายเดือน Dr 21713 Cr 42000) — ต้องเลือกสูตร (§4-2) | สมุด `CreditLedgerEntry` เพิ่ม `Kind=PeriodRecognize` · job รายคืนเดียวกับ expiry |
| `CreditPassFreeze` (ใหม่) | `PassId, FromDate, ToDate, Reason` → `ExpiresOn += วัน` · แถว `Adjust` | แช่บัตร (ป่วย/เดินทาง) · ขยายอายุ = freeze ที่ไม่มีช่วง | ไม่แตะเงิน (21713 คงเดิม) · `CourseCreditMath.IsExpired` อ่าน `ExpiresOn` ตัวเดิม |
| โอน/ของขวัญ | `CreditPass.TransferredFromPassId?` + สองแถว `Adjust`(ออก)/`Purchase`(เข้า) ยอด `ContractAmount` สัดส่วนที่เหลือ | สมุดเดิมรองรับ · ไม่มี JE (หนี้สินย้ายเจ้าของ ไม่ย้ายผัง) | ด่าน: ห้ามโอนบัตรที่มีจองค้าง · ใบขายเดิมยังชี้บัตรต้นทาง (`SaleDocumentId`) |
| `InstructorPayRule` (ใหม่ · ต่อครู/ต่อบริการ) | `PerClassAmount, PerHeadAmount, MinHeads, Mode{PerClass,PerHead,Max}` | ค่าตอบแทนครูต่อคลาส/ต่อหัว | ผู้อ่าน = `BookingProviderReport` ขยายคอลัมน์ "ค่าตอบแทน" + ส่งเข้า `Payroll` เป็นรายการแปรผัน (ต้องยืนยันโครง payroll กับทีม D) |

สิ่งที่ **ไม่ควรสร้างใหม่**: `ClassEnrollment` แยก — `SiteBooking` (ที่นั่ง · สถานะ · เครดิตผ่าน `ExternalRef`) เป็น enrollment อยู่แล้ว · engine ที่สอง · สำเนานโยบายยกเลิกบน session

## §3 ข้อเสนอพัฒนา (เรียงตามคุณค่า)

| # | งาน | ขนาด | ไฟล์ | เทสต์ที่ต้องมี |
|---|---|---|---|---|
| **R-209-C1** | **ปิดช่องเงิน/ตัวตน (blocking)**: (ก) `HoldCreditForBookingAsync` ส่ง `Credits = CreditsForSession(...) × GuestCount` (หรือปฏิเสธ `GuestCount>1` กับบัตร — §4-3) · (ข) `PayWithCredit` ต้องมีตัวตนลูกค้า: token ของ `portal/login` หรือ `PublicToken`/OTP — อย่างน้อยปฏิเสธเมื่อผู้เรียกนิรนามส่ง `CustomerId` (ด่านใน `PrepareCreditBookingAsync`) · (ค) `cms-bookings.html` เพิ่มช่อง "ชำระด้วยบัตร" ให้พนักงาน | S | `CmsBookingService.Credits.cs` · `CmsBookingController.cs` · `Helpers/CreditBookingLink.cs` (เพิ่ม `CreditsFor(guests, unit, minutes)` pure) | `CreditBookingLinkTests`: 3 ที่นั่ง ⇒ 3 เครดิต · 1 ที่นั่ง ไม่เปลี่ยน (ทิศตรงข้าม) · นิรนาม+CustomerId ⇒ 401/400 |
| **R-209-C2** | **รอบเรียน + เทอม + ปฏิทิน**: `SiteClassSession` + `SiteClassTerm` (§2) · generate occurrence · `Decide` ประกอบแถวจาก session (ยกเลิกรอบ = blocked · ครูของรอบ = requestedResource ตั้งต้น) · endpoint `GET …/booking/classes?from&to` (สัปดาห์) + `POST classes/{id}/cancel` (คืนเครดิตทุกใบผ่าน `UpdateBookingStatus` เส้นเดิม + แจ้ง) + `PUT classes/{id}/instructor` (ครูแทนทั้งรอบ ผ่าน `Decide` ทีละใบ) · หน้า week view ใน `cms-bookings.html` + หน้าร้านแสดงตารางสัปดาห์พร้อมชื่อครู/ที่ว่าง · ย้ายรอบกลุ่มของ `course-credits.html` ให้สร้างเป็น `SiteClassSession` (คง 1-on-1 ไว้) | L | `Models/Entities/CmsBooking.cs` · `Helpers/BookingSlotEngine.cs` (เฉพาะ `RowOf(SiteClassSession)`) · `Helpers/ClassTermGenerator.cs` (ใหม่ pure) · `CmsBookingService.Classes.cs` (ใหม่) · `BookingEngineMigration` · `storefront.html` | `ClassTermGeneratorTests` (12 สัปดาห์ จ/พ = 24 รอบ · วันหยุด SpecificDate ตัด) · `BookingSlotEngineTests` รอบยกเลิก ⇒ `Closed` ไม่ใช่ `Full` · Db: ยกเลิกรอบ 8 คน ⇒ 8 Release + 0 เครดิตค้าง |
| **R-209-C3** | **portal สมาชิกบนหน้าร้าน**: login/register (`portal/login` มีแล้ว) → "บัตรของฉัน" (คงเหลือ · หมดอายุ · ประวัติ) · "คลาสของฉัน" (ยกเลิก/เลื่อนตามนโยบาย) · ปุ่มจอง "ใช้บัตร" ส่ง `PayWithCredit` พร้อม token · PDPA: endpoint อ่านเฉพาะ `ContactId` ของตัวเอง | M | `Controllers/CourseCreditController.cs` (เส้น `portal/…` ใหม่ หรือ controller แยก) · `storefront.html` · `CmsCustomerController.cs` | เทสต์สิทธิ์: token ลูกค้า A อ่านบัตร B ⇒ 403 · ทิศตรงข้าม: A อ่านของ A ได้ |
| **R-209-C4** | **แจ้งเตือนครบชุด** ผ่าน `IBookingNotifier` ตัวเดิม: ยืนยันการจอง (หลัง commit ใน `CreateBookingAsync`) · ร้านยกเลิก/เปลี่ยนครู (จาก C2) · เครดิตใกล้หมดอายุ N วัน (job เดิม `CourseCreditExpiryJob` + `CreditPass.ExpiryNotifiedAt` ประทับหลังส่งถึงจริง · ช่องทางจาก `Contact`/`SiteCustomer`) · ค่าตั้ง `SiteBookingSettings.NotifyOnBooking/OnCancel` + `CreditExpiryReminderDays` | S/M | `Helpers/BookingReminderPlan.cs` (เทมเพลตใหม่ + `Placeholders`) · `CmsBookingService.Notify.cs` · `CourseCreditExpiryJob.cs` | `BookingReminderPlanTests` เทมเพลตใหม่ · job: ส่งล้ม ⇒ ไม่ประทับ (F2 #3) |
| **R-209-C5** | **สมาชิกไม่จำกัด + freeze/ขยาย/โอน** (§2) · สูตรรับรู้ตามเวลาใน `CourseCreditMath` (pure) · `Hold` สำหรับ Unlimited = ตรวจเพดาน ไม่ตัดเครดิต · `Settle` ไม่มี JE (รับรู้โดย job รายคืน) | M | `CourseCredit.cs` · `CourseCreditMath.cs` · `CourseCreditService.cs` · `course-credits.html` · `DatabaseMigrationHelper.CourseCreditStatements` | `CourseCreditMathTests`: 30 วัน 3,000 ⇒ รายวัน 100 · เดือนปิดงวด ⇒ ลงวันนี้+หมายเหตุ · freeze 7 วัน ⇒ `ExpiresOn+7` · โอนบัตรมีจองค้าง ⇒ throw |
| **R-209-C6** | **ค่าตอบแทนครู**: `SiteBookingResource.EmployeeId` + `InstructorPayRule` · `BookingProviderReport` เพิ่ม "ผู้เรียนที่มา/ค่าตอบแทน" และใช้ `RecognizedAmount` จากสมุดเครดิตแทน `TotalAmount` 0 · ส่งออกเป็นรายการแปรผันเข้า payroll (ยืนยันโครงกับทีม D) | M | `Helpers/BookingProviderReport.cs` · `Helpers/InstructorPayRule.cs` (ใหม่ pure) · `CmsBookingService.Workflow.cs:221` · `cms-booking-providers.html` | pure: ต่อหัว 8 คน × 50 = 400 · ต่อคลาส min(…) · มา 0 คน ⇒ ตามกฎ |
| **R-209-C7** | **รายงาน fill-rate / retention / no-show rate**: `Helpers/ClassFillRateReport.cs` (pure จาก `SiteClassSession` + `SiteBooking`) · retention จาก `CreditLedgerEntry`/`SiteBooking` ต่อ `ContactId` (ครั้งล่าสุด > N วัน = เสี่ยงหาย) | S | ใหม่ + `cms-booking-providers.html` แท็บ | pure เทสต์ตัวเลข |
| R-209-C8 | ราคา drop-in ต่อที่นั่ง (`TotalPrice` รับ guests เมื่อ `CapacityUnit=Guests`) · ด่านทดลองเรียน 1 ครั้ง/เบอร์ · no-show อัตโนมัติหลังคลาสจบ N ชม. (job + ค่าตั้ง เปิดเอง) · เลื่อนคลาสด้วยบัตร = Release+Hold ธุรกรรมเดียว | S | `BookingSlotEngine.cs:485` · `CmsBookingService.Reschedule.cs:49-54` · `BookingReminderJob` | ทิศตรงข้าม: `CapacityUnit=Bookings` ราคาไม่เปลี่ยน |
| R-209-C9 | ล้าง F2 #2: ลบ `SiteBookingSlot.CurrentBookings`/`AvailableSlots` หรือคำนวณจริง · ตัดสิน `Tags` · จดใน `docs/BOOKING_ENGINE_DESIGN.md` §2.1b ว่า `CreditClassSession` กลุ่มถูกแทนด้วย C2 | S | `CmsBooking.cs:111` · `CmsBookingService.cs:1966-1975` | `tools/settings_reader_check.py` ratchet |

## §4 คำตัดสินที่ต้องถามเจ้าของ

| # | คำถาม | ตัวเลือก | ข้อเสนอของทีม |
|---|---|---|---|
| 1 | **นโยบายยกเลิกช้าของคลาสที่จองด้วยบัตร** ใช้ของใคร — บริการ (`FreeCancellationHours` ลูกค้าเห็นบนเว็บ) หรือแพ็กเกจ (`FreeCancelHours` ตรึงบนบัตร · โค้ดใช้ตัวนี้ `CourseCreditService.cs:675`) | ก. แพ็กเกจเสมอ (สัญญาตอนซื้อ) · ข. บริการ (คลาสกำหนด) · ค. เข้มกว่าชนะ | **ก** + หน้าเว็บต้องแสดงนโยบายของ**บัตร**เมื่อเลือกใช้บัตร (resolver เดียวใน `CreditBookingLink`) · ค่าเริ่มต้น 12 ชม. สำหรับคลาสกลุ่ม / 24 ชม. 1-on-1 |
| 2 | **สมาชิกรายเดือนไม่จำกัด — รับรู้รายได้อย่างไร** | ก. straight-line รายวันตามอายุ (TFRS NPAEs บท 6 · ไม่ขึ้นกับการมา) · ข. ตามการมาเรียนโดยประเมิน "ครั้งคาดหวัง" · ค. รับรู้ทั้งก้อนตอนขาย (ไม่แนะนำ — รายได้รอรับรู้ผิด) | **ก** · breakage ไม่มี (หมดอายุ = รับรู้ครบพอดี) · freeze ⇒ หยุดนับวัน |
| 3 | จองหลายที่นั่งด้วยบัตร 1 ใบ | ก. อนุญาต ตัดเครดิต × ที่นั่ง · ข. ห้าม (บัตรต่อคน) — เพื่อนจ่าย drop-in | **ข เป็นค่าเริ่มต้น** (บัตรมี `ContactId` คนเดียว · no-show ของเพื่อนริบเครดิตเจ้าของบัตร) + สวิตช์ต่อแพ็กเกจ `AllowGuestSeats` เปิดเป็น ก |
| 4 | ร้านยกเลิกคลาส ⇒ คืนเครดิตเสมอ (ปัจจุบัน) · ขยายอายุบัตรให้อัตโนมัติไหมเมื่อคลาสที่จองไว้ถูกยกเลิกหลังวันหมดอายุ | ก. คืนอย่างเดียว · ข. คืน + ต่ออายุเท่าวันที่เสีย | ข (ต้องมี freeze/ขยายจาก C5 ก่อน) |
| 5 | No-show อัตโนมัติ (ไม่กดเช็คอินภายใน N ชม.หลังจบคลาส ⇒ ตัดเครดิต) | ก. ไม่ทำ (คนตัดสิน · หลัก F2 #3) · ข. เปิดได้ต่อไซต์ ค่าเริ่มต้นปิด | **ข** · ประทับ `AutoNoShowAt` + แจ้งสมาชิก + ย้อนได้โดยพนักงาน (คืนเครดิต+กลับรายการ JE ผ่านเส้นปกติ) |
| 6 | ค่าตอบแทนครูต่อหัว นับ "มาเรียน" อย่างเดียว หรือรวม no-show/ยกเลิกช้า (ที่ตัดเครดิตแล้ว) | ก. มาเรียนเท่านั้น · ข. ทุกแถวที่รับรู้รายได้ | ให้เจ้าของเลือกต่อกฎ (`InstructorPayRule.CountMode`) ค่าเริ่มต้น ก |
| 7 | ตัวตนสมาชิกตอนใช้บัตรออนไลน์ | ก. login portal (อีเมล/LINE) · ข. OTP เบอร์โทร · ค. ลิงก์ลับต่อบัตร | ก (มี `portal/login` แล้ว · LINE login ให้ช่องทางแจ้งเตือนไปด้วย `CmsBookingService.Notify.cs:32-37`) |
| 8 | ยุบรอบกลุ่มของหน้า "คอร์ส & เครดิต" เข้า `SiteClassSession` (C2) — ร้านที่ไม่มีเว็บ CMS ต้องสร้างไซต์ก่อนไหม | ก. ยุบ (ไซต์ว่างอัตโนมัติ) · ข. คงสองทางไว้ | **ก** — สองตารางคลาสคือต้นเหตุ "สองความจริง" (บทเรียน `Product.CurrentStock` vs `WarehouseStock`) |

_อ่านจากโค้ด 2026-10-29 · ยังไม่ผ่านฝ่ายค้าน · ไม่ได้คอมไพล์ในเครื่องนี้_
