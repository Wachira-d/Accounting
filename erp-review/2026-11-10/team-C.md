# ทีม C — LINE · แจ้งเตือน · โควตา · HEAD a41041f3
BLOCKER: ไม่มี
- H1 settings.html:1086-1137 + LineConfigController.cs:53-96 — บริษัทผู้ให้บริการเห็น "ใช้ OA ของร้านเอง" + Webhook URL ⇒ ใส่ token/secret ของ @810pamnc แล้ววาง URL บริษัทใน LINE Console = ทับ /api/line-webhook ⇒ บอท ERP/ลิงก์ลูกค้า/คำสั่งกลุ่มรอบ 256 พังทุกผู้เช่า · แก้: Update ปฏิเสธ token ที่ bot id = บอทกลาง · ซ่อนส่วนขั้นสูงสำหรับ PlatformCompanyId พร้อมคำอธิบาย
- M1 ล้างช่อง Group ID ทีม/Basic ID = no-op เงียบ (settings.html:2223-2231 ส่ง null · controller :73,75 ถือว่าคงเดิม) · token ล้างไม่ได้เช่นกัน (LineConfigDtos.cs:47)
- M2 แพ็กที่ใช้ไม่ได้ยังขาย: ChannelPackSellable คืน true เมื่อ -1 (NotifyQuotaPolicy.cs:211) · ราคาแพ็กรวมคืนเมื่อไม่จำกัด (NotifyQuotaService.cs:530,554)
- M3 e-Tax by Email + กฎ LINE อย่างเดียว + LINE ล้ม ⇒ ลูกค้าได้อีเมลสองฉบับ + โควตาอีเมลถูกนับสองครั้ง (EmailScheduleService.cs:1118 ไม่ดู etax ที่บังคับอีเมล :1011)
- M4 เมื่อมี PlatformCompanyId แล้ว บริษัทอื่นพิมพ์ #HeyNextAcc กับ @810pamnc ได้รหัส GB ที่ใช้ไม่ได้ — รู้ตอนยืนยันเท่านั้น (ContactLineGroupService.cs:128-150)
- M5 ข้อความรอบ 254 ขัดรอบ 256 สำหรับบริษัทผู้ให้บริการ: ContactRecipientResolver.cs:174-180 · email-schedule.html:174 · DOCUMENT_FLOW §2.12h "NOTIFY-CENTRAL-OA-GROUP-OFF"
- M6 settings.html:1089 "ส่งแจ้งเตือนลูกค้าทาง LINE ได้แล้ว ไม่ต้องตั้งค่า" — จริงเฉพาะจอง/คำสั่งซื้อ ไม่ใช่เอกสารถึงผู้ติดต่อ
- LOW: Webhook URL สองแหล่ง (location.origin vs companyWebhookUrl จาก App:BaseUrl) · ไม่บอกว่า LINE เรียก webhook แล้วหรือยัง · ทดสอบส่ง/กลุ่มทีมต้องพิมพ์ U…/C… ที่หาไม่ได้ในแอป · ยกเลิกปลายทางจากบริษัทไม่ประทับ WithdrawnAt ความยินยอม · group-push นับ 1/ครั้ง (LINE คิดต่อสมาชิก) · OA บริษัทโควตาฟรีหมด (429) ไม่แจ้งเจ้าของ · ป้าย /admin/plans "ไม่จำกัด (ว่าง)" = ไม่แยก
ผล: คิว/ผูก LINE/กลุ่ม OA บริษัท = มีข้อควรระวัง (M3) · กลุ่ม OA กลาง (บริษัทผู้ให้บริการ) = มีข้อควรระวัง (H1,M4) · โควตา/แพ็ก = มีข้อควรระวัง (M2) · โควตาอีเมลรายเดือน = พร้อม · เฝ้า OA กลาง = มีข้อควรระวัง · ตารางส่ง/แท็บผู้ติดต่อ = มีข้อควรระวัง · หน้าตั้งค่า LINE = **ยังไม่พร้อม** (H1, M1) · /admin plans/addons = มีข้อควรระวัง
