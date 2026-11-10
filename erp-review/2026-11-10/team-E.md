# ทีม E — หน้าตั้งค่า + cross-cutting (รอบ 250–256 · HEAD a41041f3)
BLOCKER: ไม่มี
- H1 การ์ด go-live แอดมิน (`Helpers/PlatformGoLiveChecklist.cs:58-106`) ไม่มีข้อ LINE: PlatformCompanyId · token/secret/Basic ID OA กลาง · webhook /api/line-webhook · Allow bot to join groups · ชื่อการ์ด "รับเงินจริงผ่าน Omise" ซ่อนว่าเป็นรายการ go-live เดียว
- M1 recurring.html:469-485,520-521 — ขอบเขต "ค่าเริ่มต้น" แสดง dropdown ตามนโยบายผู้ติดต่อ (ขัดป้าย) + แก้ได้แต่บันทึกส่ง clear:true = silent no-op
- M2 pay-document.html:158 `alert()` บนหน้าชำระสาธารณะ (LINE in-app บล็อก)
- M3 ค่าตั้งต้นรายการประจำเปลี่ยน (1dd21fd7) โดยเจ้าของไม่ถูกแจ้ง — banner/บรรทัดใน digest
- L1 partners.html:91-92,136-138 โหลดค่าล้ม ⇒ ช่องว่าง/ปุ่มปิดไม่บอกเหตุ
- L2 confirm() ใหม่: contacts.html:1351 · email-schedule.html:483 (+ ของเก่า admin/plans, site-settings, tax-remittance prompt)
- L3 settings.html Webhook URL จาก location.origin — ควรมาจาก AppBaseUrl ฝั่งเซิร์ฟเวอร์
- L4 ShowAmounts ตั้งต้น true — ป้ายตอนผูกกลุ่มให้ชัด
- L5 admin/plans.html ช่องโควตาไม่มี label for · documents.html:1412 ตารางใน modal ไม่มี wrapper
ค่าตั้งครบ round-trip ทุกตัวที่ไล่ · settings_reader_check ไม่ครอบ PartnerDiscoverable, PlanTemplate quota 4 ช่อง, Company notify quota, ContactNotifyEndpoint อื่นนอก TopicsJson
