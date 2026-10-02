# รอบ 202 — ตรวจฟีเจอร์ที่พักอย่างละเอียด (3 ทีม · main agent เปิดไฟล์ยืนยัน P0/P1 หลักแล้ว)

โจทย์ผู้ใช้ (2026-10-02): (1) เวลาเช็คอิน/เช็คเอาต์เป็นช่องบังคับแต่ไม่มี `*` · (2) หน้าเว็บ /site/b4 ไม่อ้างอิงข้อมูลห้องที่ตั้งค่าเลย ·
(3) หน้าจองต้องให้ลูกค้าเลือกวัน ตรวจห้องว่าง และจองเองได้

## สรุปสั้น
- เครื่องจอง (ค้นห้องว่าง · ราคา · จอง · ชำระผ่าน `PublicPaymentController` · voucher · อีเมล) **มีครบแล้ว** แต่ทำงานเฉพาะเมื่อที่พักถูก **ผูกเว็บ + Active**
  — ที่พักในภาพ "เว็บไซต์ที่ผูก = ไม่ผูก" ⇒ /booking ตกไปเป็นการ์ดนัดหมาย ฿0 (บริการนัดหมายที่ถูกสร้างจาก GET สาธารณะ)
- ราคาห้องบนหน้าแรกเป็น **ข้อความ seed ตายตัว** (`CmsSiteTemplateSeeder.HotelPlan` + `LodgingSeedDefaults`) — ผูกเว็บแล้วก็ไม่เปลี่ยน
- ช่องเวลาเข้า-ออก: DTO `string` ไม่ nullable ⇒ ASP.NET ใส่ [Required] เอง · หน้าเว็บไม่บังคับ/ไม่มี `*` · service มี default อยู่แล้ว ⇒ สองชั้นขัดกัน

## ข้อที่ยืนยันแล้ว (ทีม · ระดับ · หลักฐาน)
| ID | ระดับ | เรื่อง | หลักฐาน |
|---|---|---|---|
| O-P0-1 | P0 | ตรวจห้องว่าง/ราคานอกล็อก ⇒ จองห้องสุดท้ายซ้อน (ล็อกคุมแค่เลขจอง) · Confirm/Reschedule/Assign ไม่ล็อก | `LodgingService.Reservations.cs:329-337` vs `:386-389` ✅ |
| O-P0-2 | P0 | night audit ประทับ CheckedOut/NoShow เอง (R1) ⇒ ออกใบเช็คเอาต์/ริบมัดจำผ่านโมดูลไม่ได้อีก | `LodgingNightAuditJob.cs:105-128` ✅ |
| W-01 | P0 | ที่พักไม่ผูกเว็บไม่มีป้ายเตือน/ไม่มีทางผูกจากหน้าเว็บ | `lodging-settings.html:69` · `Reservations.cs:142` |
| W-02 | P0 | ราคา/ห้องบนหน้าแรกเป็น seed ตายตัว ไม่มีบล็อกข้อมูลสด | `CmsSiteTemplateSeeder.cs:2556-2643` |
| S-P1-1 | P1 | เวลาเข้า-ออก [Required] โดยปริยาย ไม่มี `*` ⇒ ขอบแดง | `LodgingDtos.cs:42-43` · `lodging-settings.html:89-90` ✅ |
| S-P1-2 | P1 | เวลาผิดรูปถูกแทน 14:00/12:00 เงียบ | `LodgingService.cs:58-60` ✅ |
| S-P1-3 | P1 | สร้างที่พักแรกจากฟอร์มว่าง ⇒ Inactive + ปิดจองออนไลน์ + มัดจำ 0% | `lodging-settings.html:265` |
| S-P1-4/W-06 | P1 | dropdown เว็บโหลด 20 รายการ ⇒ บันทึกแล้วปลดผูกเงียบ | `lodging-settings.html:238` · `CmsSiteController.cs:38` |
| W-03 | P1 | GET สาธารณะสร้างบริการนัดหมาย ฿0 ลง DB + `catch {}` | `CmsBookingService.cs:100-161` ✅ |
| W-04 | P1 | `/lodging/info` 5xx ตกเป็นการ์ดนัดหมายเงียบ | `storefront.html:1511` ✅ |
| W-05 | P1 | "เติมเทมเพลต" สร้างที่พักแห่งที่สองยึดเว็บ · ไม่มีทาง "ผูกที่พักที่มีอยู่" | `LodgingSeeder.cs:19` |
| O-P1-3 | P1 | แผนราคาที่แขกส่ง id มาไม่ผ่าน `PlanApplies` | `Reservations.cs:110-111` |
| O-P1-4 | P1 | hold หมดระหว่างจ่าย ⇒ เงินเข้าแต่ยืนยันไม่ได้ เห็นแค่ใน event | `PublicPaymentController.cs:144+` · `Reservations.cs:503-517` |
| O-P1-5 | P1 | ส่งสลิปแล้ว hold +24 ชม. แล้วเลิกกันห้องเงียบ | `Lifecycle.cs:127` · `LodgingPricingEngine.cs:321` |
| O-P1-6 | P1 | POST จองสาธารณะไม่มีเพดานต่อผู้จอง ⇒ กันห้องทั้งที่พักได้ | `LodgingPublicController.cs:58-69` |
| P2 | P2 | early/late hours + อายุทารกไม่มีผู้อ่าน · overbooking บวกทุกประเภท · quote พนักงาน isStaff ไม่ตรง · เช็คอินล่วงหน้า 1 วัน · ลด capacity ไม่ตรวจการจอง · เลื่อนวันจับคู่บริการเสริมด้วย index · PromoCode = 0 เสมอ · ขอบแดงค้าง · แท็บห้องก่อนมีที่พัก · ID นโยบาย/เว็บไม่ตรวจเจ้าของ · วันที่ไม่ส่งต่อไป /booking · seed ล้มเงียบ | รายงานทีม S/W/O |

## doc ล้าสมัยที่พบ
- CLAUDE.md "Payment gateway — วันนี้ไม่มีการเชื่อม gateway ใดเลย" และ bullet ที่พัก "`PaymentGatewayController` เป็น `[Authorize]` ⇒ จ่ายออนไลน์ไม่ได้" — **ไม่จริงแล้ว** (`PublicPaymentController` `[AllowAnonymous]` + `OmisePaymentProvider`)
- `LODGING_BOOKING_AUDIT.md:133` · `LODGING_TAKETIME_ANALYSIS.md` §2 ข้อ 3 — ค่าธรรมเนียม early/late ต่อสายแล้ว

## คำตัดสิน main agent (มอบอำนาจเดิม · บันทึกใน DECISIONS ข้อ 116–122)
ดู `erp-review/2026-09-29/DECISIONS.md`

## เรื่องที่ต้องให้เจ้าของตัดสิน (ไม่เดาแทน)
OTA merchant/agency + ฐาน VAT · ราคาเด็ก/ทารกนับความจุ · เช็คอินก่อนวันจอง · ค่าปรับยกเลิกที่เกินมัดจำออกใบแจ้งหนี้ไหม · ระยะกันห้องของการจองที่ส่งสลิป · คืนเงินผ่าน gateway อัตโนมัติเมื่อห้องเต็ม
