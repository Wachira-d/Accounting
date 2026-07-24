// ===== Onboarding — ระบบแนะนำการใช้งานต่อหน้า =====
// เป้าหมาย: "สอนจริง" ไม่ใช่แค่ชี้ว่ามีปุ่ม — ทุกหน้าเริ่มด้วยการ์ดอธิบายว่า
// หน้านี้มีไว้ทำอะไร + flow การทำงาน แล้วค่อยไล่ชี้ทีละจุดพร้อมคำอธิบาย "ทำไม/อย่างไร"
//
// การปิด: ผู้ใช้กด "ปิดการสอนนี้" → บันทึกฝั่ง server (ต่อ user) → ไม่ขึ้นอีกเลย
// ข้ามเครื่อง/ล้าง cache. localStorage เป็นแค่ cache กันกระพริบตอนโหลด.
// ปุ่มลอย "📘" ยังอยู่ให้เปิดดูเองได้เสมอ (user เรียกเอง ≠ ระบบเด้งเอง)
//
// โหลดโดย layout.js อัตโนมัติ — หน้าไม่ต้องแก้อะไร. เนื้อหาอยู่ใน TOURS ข้างล่าง
// (หน้าที่ไม่มีใน TOURS แต่มี data-tour-step ในหน้า จะ fallback ใช้ของเดิม)

const Onboarding = {
  _prefs: null,          // { dismissed: [...], all: bool } — จาก server
  _active: false,

  // ---------- เนื้อหาการสอน (หัวใจของระบบ) ----------
  // โครงสร้าง: intro = การ์ดกลางจออธิบายภาพรวม, steps = ไล่ชี้ทีละจุด
  // sel รับได้หลาย selector (ลองตามลำดับ, ไม่เจอ = โชว์เป็นการ์ดกลางจอแทน
  // เพื่อให้เนื้อหายังสอนได้แม้ UI เปลี่ยน)
  TOURS: {
    dashboard: {
      title: 'แดชบอร์ด — ภาพรวมกิจการ',
      intro: 'หน้านี้สรุปสุขภาพการเงินของกิจการในจอเดียว: รายรับ-รายจ่ายเดือนนี้ เงินสดคงเหลือ ลูกหนี้/เจ้าหนี้ค้าง และภาษีที่ต้องยื่น\n\nแนะนำให้เปิดดูทุกเช้า — ตัวเลขทุกใบมาจากเอกสารที่อนุมัติแล้วเท่านั้น (ฉบับร่างยังไม่นับ)',
      steps: [
        { sel: ['#companySelect'], title: 'สลับบริษัท', body: 'ระบบรองรับหลายบริษัทใน login เดียว — เลือกบริษัทที่จะทำงานตรงนี้ ทุกหน้าจะแสดงข้อมูลของบริษัทที่เลือกเท่านั้น (ข้อมูลแยกขาดกันโดยสมบูรณ์)' },
        { sel: ['.kpi-grid', '#kpiRow', '.admin-kpi-grid'], title: 'ตัวเลขสำคัญ (KPI)', body: 'รายได้/ค่าใช้จ่าย/กำไรของเดือนนี้ คลิกที่การ์ดเพื่อเจาะดูรายการที่ประกอบเป็นตัวเลขนั้นได้' },
        { sel: ['#quickActions', '.quick-actions'], title: 'ทางลัดงานประจำ', body: 'สร้างเอกสารที่ใช้บ่อย (ใบกำกับภาษี/ใบเสร็จ/บันทึกซื้อ) ได้จากตรงนี้เลย ไม่ต้องเข้าเมนู' },
      ],
    },

    documents: {
      title: 'เอกสาร — หัวใจของระบบบัญชี',
      intro: 'ทุกอย่างเริ่มที่นี่: ใบเสนอราคา ใบแจ้งหนี้ ใบกำกับภาษี ใบเสร็จรับเงิน ใบสำคัญจ่าย ฯลฯ\n\nแนวคิดสำคัญ:\n• เอกสารเริ่มเป็น "ฉบับร่าง" — แก้ได้อิสระ ยังไม่มีผลทางบัญชี\n• กด "อนุมัติ" เมื่อไร ระบบจะออกเลขที่จริง + ลงบัญชี (JE) + ตัดสต๊อกให้อัตโนมัติ\n• เอกสารที่อนุมัติแล้วห้ามแก้/ลบตามกฎสรรพากร — ต้องออกใบยกเลิกหรือใบลดหนี้แทน',
      steps: [
        { sel: ['[data-tour-step="1"]', '#btnCreate', '.btn-primary'], title: 'สร้างเอกสารใหม่', body: 'เลือกประเภทเอกสารให้ตรงงาน:\n• ขายเชื่อ → ใบแจ้งหนี้/ใบกำกับภาษี (ตั้งลูกหนี้)\n• ขายรับเงินสดทันที → ใบกำกับภาษี/ใบเสร็จรับเงินใบเดียว\n• ซื้อ/จ่าย → บันทึกซื้อ, ใบสำคัญจ่าย\nระบบคำนวณ VAT และหัก ณ ที่จ่ายให้อัตโนมัติ' },
        { sel: ['[data-tour-step="2"]', '#searchInput', '.admin-search'], title: 'ค้นหาเอกสาร', body: 'ค้นได้จากเลขที่ ชื่อลูกค้า คำอธิบาย และอ้างอิง — พิมพ์บางส่วนก็เจอ ใช้ตัวกรองสถานะช่วยดูเฉพาะ "รออนุมัติ" หรือ "ค้างชำระ" ได้' },
        { sel: ['[data-tour-step="3"]', 'table', '.admin-table'], title: 'สถานะเอกสาร', body: 'ไล่ตาม flow: ฉบับร่าง → อนุมัติ (ออกเลข+ลงบัญชี) → ส่งแล้ว → ชำระแล้ว\n\nถ้าเก็บเงินมัดจำไว้ก่อน ตอนออกใบจริงระบบจะให้เลือก "หักมัดจำ" และแสดงยอดสุทธิให้เอง' },
      ],
    },

    journals: {
      title: 'สมุดรายวัน (Journal Entries)',
      intro: 'ทุกเอกสารที่อนุมัติจะลงบัญชีเป็น JE ที่นี่อัตโนมัติ — ปกติไม่ต้องคีย์เอง\n\nใช้หน้านี้เมื่อ:\n• ตรวจว่าเอกสารลงบัญชีถูกช่องไหม (เดบิต/เครดิตบัญชีอะไร)\n• บันทึกรายการปรับปรุง (ค่าเสื่อม ค้างรับ-ค้างจ่าย) ที่ไม่มีเอกสารต้นทาง\n\nJE ที่มาจากเอกสาร แก้ที่ JE ตรง ๆ ไม่ได้ — ต้องไปแก้ที่ตัวเอกสารต้นทาง เพื่อให้เอกสารกับบัญชีตรงกันเสมอ',
      steps: [
        { sel: ['[data-tour-step="1"]', '.btn-primary'], title: 'บันทึกรายการเอง (JV)', body: 'สำหรับรายการปรับปรุงที่ไม่มีเอกสาร เช่น ปรับปรุงสิ้นเดือน — ระบบบังคับเดบิต = เครดิตก่อนบันทึกเสมอ ไม่ต้องกลัวงบไม่ลงตัว' },
        { sel: ['[data-tour-step="2"]', 'table'], title: 'อ่านรายการ', body: 'คอลัมน์ "อ้างอิง" บอกว่า JE มาจากเอกสารใบไหน — คลิกเข้าไปดูต้นทางได้เลย ถ้ายอดในงบดูแปลก เริ่มไล่จากตรงนี้' },
      ],
    },

    contacts: {
      title: 'ผู้ติดต่อ — ลูกค้าและผู้ขาย',
      intro: 'เก็บข้อมูลลูกค้า/ผู้ขายไว้ที่เดียว แล้วทุกเอกสารดึงไปใช้อัตโนมัติ\n\nจุดที่สำคัญมาก: เลขประจำตัวผู้เสียภาษี 13 หลัก + รหัสสาขา — ใบกำกับภาษีเต็มรูปต้องมีข้อมูลนี้ของผู้ซื้อ ไม่งั้นลูกค้าเอาไปเคลม VAT ไม่ได้ กรอกให้ครบตั้งแต่ตอนสร้างจะได้ไม่ต้องตามแก้ทีหลัง',
      steps: [
        { sel: ['[data-tour-step="1"]', '.btn-primary'], title: 'เพิ่มผู้ติดต่อ', body: 'บุคคลธรรมดา: กรอกแค่ชื่อ-เบอร์ก็พอ\nนิติบุคคล: กรอกเลขภาษี 13 หลักแล้วระบบตรวจ checksum ให้ + สาขา 00000 = สำนักงานใหญ่\n\nตั้ง "บัญชีเริ่มต้น" ให้ผู้ขายประจำได้ — OCR จะลงบัญชีถูกช่องเองตั้งแต่ครั้งแรก' },
        { sel: ['[data-tour-step="2"]', '#searchInput'], title: 'ค้นหา', body: 'พิมพ์ชื่อหรือเลขภาษีบางส่วน — ระบบค้นทั้งสองอย่างพร้อมกัน' },
      ],
    },

    products: {
      title: 'สินค้าและบริการ',
      intro: 'สร้างรายการสินค้า/บริการที่ขายบ่อยไว้ล่วงหน้า → ตอนออกเอกสารพิมพ์ชื่อแล้วเลือก ราคาและ VAT จะเติมให้เอง\n\nประเภทมีผลกับบัญชี:\n• สินค้า (นับสต๊อก) — ตัดสต๊อก + คำนวณต้นทุนขายอัตโนมัติเมื่อขาย\n• บริการ — ไม่ยุ่งกับสต๊อก\n• วัสดุสิ้นเปลือง — ซื้อเข้าเป็นค่าใช้จ่าย ไม่ขาย',
      steps: [
        { sel: ['[data-tour-step="1"]', '.btn-primary'], title: 'เพิ่มสินค้า', body: 'ระบุราคาขาย-ต้นทุน หน่วยนับ และอัตรา VAT ต่อรายการ (7% / 0% / ยกเว้น) — สินค้านับสต๊อกจะให้เลือกวิธีคิดต้นทุน (FIFO/ถัวเฉลี่ย)' },
        { sel: ['[data-tour-step="2"]', 'table'], title: 'ดูสต๊อกคงเหลือ', body: 'ยอดคงเหลืออัปเดตเรียลไทม์จากเอกสารซื้อ-ขายที่อนุมัติ ถ้าตัวเลขไม่ตรงของจริง ใช้เมนูปรับปรุงสต๊อก (Stock Adjustment) ไม่ต้องแก้มือ' },
      ],
    },

    'document-scan': {
      title: 'สแกนเอกสาร (OCR) — ให้ระบบคีย์แทนคุณ',
      intro: 'ถ่ายรูป/อัปโหลดใบกำกับภาษีหรือใบเสร็จ แล้วระบบอ่านและกรอกให้ทุกช่อง: ผู้ขาย เลขภาษี วันที่ รายการสินค้า ยอดเงิน VAT รวมถึงเดาบัญชีที่จะลงให้\n\nหน้าที่ของคุณเหลืออย่างเดียว: ตรวจแล้วกด "ยืนยัน" — ช่องไหนระบบไม่มั่นใจจะไฮไลต์สีเหลืองให้ดูเป็นพิเศษ\n\nยิ่งใช้บ่อยยิ่งแม่น — ทุกครั้งที่คุณแก้ค่า ระบบจะจำไว้สอนตัวเอง',
      steps: [
        { sel: ['[data-tour-step="1"]'], title: 'อัปโหลดเอกสาร', body: 'รองรับ JPG/PNG/PDF (หลายหน้าได้) ลากไฟล์มาวางได้เลย — สแกนใบเดียวหรือทีละหลายใบก็ได้' },
        { sel: ['[data-tour-step="2"]'], title: 'ตรวจผลการอ่าน', body: 'ระบบเทียบยอดรวม = ผลบวกของรายการให้อัตโนมัติ ถ้าไม่ตรงจะเตือน — เลขภาษีผู้ขายก็ตรวจ checksum ให้ว่าเป็นเลขจริง' },
        { sel: ['[data-tour-step="3"]'], title: 'สร้างเอกสารจากผลสแกน', body: 'กดปุ่มเดียวได้เป็นบันทึกซื้อ/ค่าใช้จ่ายพร้อมลงบัญชี — ผู้ขายเจ้าประจำระบบจะจับคู่กับผู้ติดต่อเดิมให้ ไม่สร้างซ้ำ' },
      ],
    },

    bank: {
      title: 'ธนาคาร — กระทบยอดอัตโนมัติ',
      intro: 'นำเข้ารายการเดินบัญชี (statement) แล้วระบบจับคู่กับเอกสารรับ-จ่ายในระบบให้อัตโนมัติ\n\nเป้าหมาย: ยอดเงินในระบบ = ยอดในธนาคารเสมอ ต่างกันเมื่อไรรู้ทันทีว่ารายการไหนหาย/ซ้ำ\n\nทำสม่ำเสมอ (แนะนำทุกสัปดาห์) จะใช้เวลาไม่กี่นาที เพราะระบบจับคู่ให้เกือบหมด เหลือเฉพาะรายการกำกวมให้คุณชี้',
      steps: [
        { sel: ['[data-tour-step="1"]'], title: 'นำเข้า statement', body: 'รองรับไฟล์ Excel/CSV จากธนาคารหลัก ๆ — ระบบจำ format ของแต่ละธนาคารได้ นำเข้าซ้ำไฟล์เดิมไม่ทำให้รายการซ้ำ' },
        { sel: ['[data-tour-step="2"]'], title: 'จับคู่รายการ', body: 'เขียว = จับคู่แล้ว, เหลือง = ระบบเดาให้ตรวจยืนยัน, แดง = ไม่พบคู่ — คลิกรายการแดงเพื่อสร้างเอกสารรับ/จ่ายจากรายการนั้นได้เลย' },
      ],
    },

    reports: {
      title: 'รายงาน — งบการเงินและวิเคราะห์',
      intro: 'งบทุกตัวสร้างสดจากรายการจริง ณ วินาทีที่เปิด ไม่ต้องกด "ประมวลผล"\n\nรายงานหลักที่ควรดูประจำ:\n• งบกำไรขาดทุน — เดือนนี้กำไรเท่าไร มาจากไหน\n• งบแสดงฐานะการเงิน — สินทรัพย์/หนี้สิน ณ วันนี้\n• งบทดลอง — สำหรับตรวจว่าเดบิต=เครดิตทุกบัญชี\n• อายุลูกหนี้ — ใครค้างนานเกิน 90 วันต้องตาม',
      steps: [
        { sel: ['[data-tour-step="1"]'], title: 'เลือกช่วงเวลา', body: 'เทียบช่วงเวลาได้ (เดือนนี้ vs เดือนก่อน / ปีนี้ vs ปีก่อน) — ตัวเลขติดลบหรือแกว่งผิดปกติจะไฮไลต์ให้เห็นชัด' },
        { sel: ['[data-tour-step="2"]'], title: 'เจาะลึกทุกตัวเลข', body: 'คลิกตัวเลขใดก็ได้ในงบ → เห็นรายการที่ประกอบเป็นยอดนั้น → คลิกต่อถึงเอกสารต้นทาง ใช้ตอบคำถาม "ยอดนี้มาจากไหน" ได้ใน 3 คลิก' },
        { sel: ['[data-tour-step="3"]'], title: 'ส่งออก', body: 'ทุกรายงานส่งออกเป็น Excel/PDF ได้ — ไฟล์ภาษีจัด layout ตามแบบสรรพากรพร้อมยื่น' },
      ],
    },

    accounts: {
      title: 'ผังบัญชี (Chart of Accounts)',
      intro: 'โครงกระดูกของระบบบัญชี — ระบบ seed ผังมาตรฐานไทยให้แล้ว (สินทรัพย์ 1xxxx, หนี้สิน 2xxxx, ทุน 3xxxx, รายได้ 4xxxx, ค่าใช้จ่าย 5xxxx)\n\nคนส่วนใหญ่ไม่ต้องแตะหน้านี้เลย — เอกสารลงบัญชีให้อัตโนมัติ จะเข้ามาเมื่ออยากเพิ่มบัญชีย่อยเพื่อแยกดูละเอียดขึ้น เช่น แยก "ค่าโฆษณา Facebook" ออกจาก "ค่าโฆษณา Google"',
      steps: [
        { sel: ['[data-tour-step="1"]', '.btn-primary'], title: 'เพิ่มบัญชีย่อย', body: 'เลือกหมวดแม่แล้วตั้งรหัสในช่วงเดียวกัน — บัญชีที่มีรายการลงแล้วจะลบไม่ได้ (ปิดใช้งานแทน) เพื่อรักษาประวัติ' },
        { sel: ['[data-tour-step="2"]', 'table'], title: 'ยอดคงเหลือ', body: 'ยอดแต่ละบัญชีอัปเดตเรียลไทม์ คลิกดูรายการเคลื่อนไหว (ledger) ของบัญชีนั้นได้' },
      ],
    },

    'quick-sale': {
      title: 'ขายด่วน (POS)',
      intro: 'หน้าขายหน้าร้าน: จิ้มสินค้า → รับเงิน → พิมพ์ใบเสร็จ จบใน 30 วินาที\n\nทุกบิลลงบัญชี + ตัดสต๊อกอัตโนมัติเหมือนออกเอกสารเต็มรูปแบบ แต่เร็วกว่ามาก — เหมาะกับขายหน้าร้านที่ลูกค้าไม่ต้องการใบกำกับภาษีเต็มรูป (ถ้าต้องการ กดแปลงเป็นใบกำกับเต็มรูปได้จากบิลนั้นทีหลัง)',
      steps: [
        { sel: ['[data-tour-step="1"]'], title: 'เลือกสินค้า', body: 'จิ้มการ์ดสินค้าหรือยิงบาร์โค้ด — แก้จำนวน/ราคา/ส่วนลดได้ที่ตะกร้าด้านขวา' },
        { sel: ['[data-tour-step="2"]'], title: 'รับเงิน', body: 'เงินสด (คำนวณเงินทอนให้) / โอน / บัตร — ปิดบิลแล้วพิมพ์ใบเสร็จหรือส่งลิงก์ให้ลูกค้าได้ทันที' },
      ],
    },

    payments: {
      title: 'รับ/จ่ายชำระ',
      intro: 'บันทึกการรับเงินจากลูกหนี้และจ่ายเงินให้เจ้าหนี้ — ระบบตัดยอดค้างของเอกสารให้อัตโนมัติ\n\nรับชำระบางส่วนได้ (ลูกค้าผ่อนจ่าย) ยอดค้างที่เหลือจะติดตามต่อในรายงานอายุลูกหนี้เอง และถ้ามีหัก ณ ที่จ่าย ระบบแยกยอดและออกหนังสือรับรอง 50 ทวิ ให้ด้วย',
      steps: [
        { sel: ['[data-tour-step="1"]', '.btn-primary'], title: 'บันทึกรับ/จ่าย', body: 'เลือกผู้ติดต่อ → ระบบลิสต์เอกสารค้างของรายนั้นให้เลือกตัด — จ่ายทีเดียวหลายใบก็ได้ ระบบเฉลี่ยยอดให้' },
        { sel: ['[data-tour-step="2"]', 'table'], title: 'ประวัติการชำระ', body: 'ทุกรายการโยงกลับถึงเอกสารต้นทาง — พิมพ์ใบเสร็จรับเงินซ้ำจากตรงนี้ได้' },
      ],
    },

    payroll: {
      title: 'เงินเดือน (Payroll)',
      intro: 'คำนวณเงินเดือนครบวงจร: ภาษีหัก ณ ที่จ่าย (ขั้นบันได) + ประกันสังคม 5% (เพดาน 750 บาท) อัตโนมัติตามกฎหมาย\n\nปิดงวดแล้วได้ครบ: สลิปเงินเดือนพนักงานทุกคน, ไฟล์ ภงด.1, สปส.1-10 พร้อมยื่น และลงบัญชีเงินเดือนค้างจ่ายให้เอง',
      steps: [
        { sel: ['[data-tour-step="1"]'], title: 'รอบเงินเดือน', body: 'สร้างรอบ → ระบบดึงพนักงานทุกคนพร้อมเงินเดือน + รายการหักประจำมาให้ แก้เฉพาะรายการพิเศษของเดือนนั้น (OT, โบนัส, ขาดงาน)' },
        { sel: ['[data-tour-step="2"]'], title: 'อนุมัติ + จ่าย', body: 'ตรวจสรุปแล้วอนุมัติ → พิมพ์/ส่งอีเมลสลิปให้พนักงาน + ไฟล์นำส่งธนาคาร — ยอดภาษีและประกันสังคมไปรอที่หน้า "นำส่งภาษี" ให้กดจ่ายตามกำหนด' },
      ],
    },

    etax: {
      title: 'e-Tax Invoice — ใบกำกับภาษีอิเล็กทรอนิกส์',
      intro: 'ส่งใบกำกับภาษีเข้ากรมสรรพากรแบบอิเล็กทรอนิกส์ ตามมาตรฐาน ETDA\n\n2 ช่องทาง:\n• e-Tax by Email — สำหรับกิจการรายได้ ≤ 30 ล้าน/ปี (ง่ายสุด: ระบบส่ง PDF ให้ลูกค้า + cc สรรพากรอัตโนมัติ)\n• ส่งตรง RD — ต้องมีใบรับรองอิเล็กทรอนิกส์ (มีผู้ให้บริการ CA แนะนำในหน้าตั้งค่า)\n\nกำหนดส่ง: ภายในวันที่ 15 ของเดือนถัดไป — ระบบมี cron ส่งให้อัตโนมัติ',
      steps: [
        { sel: ['[data-tour-step="1"]'], title: 'สถานะการส่ง', body: 'เขียว = ส่งสำเร็จ (มี timestamp จาก ETDA เป็นหลักฐาน), แดง = ล้มเหลว มีคิว retry ให้ — เอกสารไหนยังไม่ส่งจะเห็นชัดก่อนถึงกำหนด' },
      ],
    },

    'account-subscription': {
      title: 'แพ็กเกจการใช้งาน (License)',
      intro: 'ดูแพ็กเกจปัจจุบัน วันหมดอายุ และปริมาณการใช้งาน (เอกสาร/ผู้ใช้/พื้นที่/OCR) เทียบกับเพดานของแพ็กเกจ\n\nต่ออายุ: กดชำระเงิน → โอน → อัปโหลดสลิป → ทีมงานตรวจแล้วต่ออายุให้ พร้อมส่งใบเสร็จ/ใบกำกับภาษีให้ทางอีเมล และดาวน์โหลดย้อนหลังได้จากหน้านี้',
      steps: [
        { sel: ['[data-tour-step="1"]'], title: 'การใช้งานปัจจุบัน', body: 'แถบไหนใกล้เต็ม (>85%) จะเปลี่ยนสีเตือน — เต็มแล้วระบบจะไม่ให้สร้างรายการใหม่จนกว่าจะอัปเกรดหรือขึ้นเดือนใหม่' },
      ],
    },
  },

  // ---------- prefs (server-synced) ----------
  async loadPrefs() {
    if (this._prefs) return this._prefs;
    // cache ก่อน (กันกระพริบ) แล้ว sync จาก server
    try { this._prefs = JSON.parse(localStorage.getItem('tourPrefs') || 'null'); } catch {}
    try {
      const res = await API.get('/api/auth/tour-prefs');
      if (res && res.success && res.data) {
        this._prefs = { dismissed: res.data.dismissed || [], all: !!res.data.all };
        localStorage.setItem('tourPrefs', JSON.stringify(this._prefs));
      }
    } catch { /* offline/ยังไม่ login — ใช้ cache */ }
    if (!this._prefs) this._prefs = { dismissed: [], all: false };
    return this._prefs;
  },

  isDismissed(pageKey) {
    const p = this._prefs || { dismissed: [], all: false };
    return p.all || (p.dismissed || []).includes(pageKey);
  },

  async dismiss(pageKey, all = false) {
    // optimistic: กันขึ้นซ้ำทันที แม้ network ช้า/ล่ม
    this._prefs = this._prefs || { dismissed: [], all: false };
    if (all) this._prefs.all = true;
    else if (!this._prefs.dismissed.includes(pageKey)) this._prefs.dismissed.push(pageKey);
    localStorage.setItem('tourPrefs', JSON.stringify(this._prefs));
    try {
      await API.post('/api/auth/tour-prefs/dismiss', all ? { all: true } : { pageKey });
    } catch { /* จะ sync สำเร็จรอบหน้า — local กันไว้แล้ว */ }
  },

  // ---------- auto-run ----------
  // เรียกจาก layout.js หลัง init — หน้าไม่ต้องแก้อะไร
  async auto(pageKey) {
    if (!pageKey) return;
    const hasContent = this.TOURS[pageKey] || document.querySelector('[data-tour-step]');
    if (!hasContent) return;

    await this.loadPrefs();
    this._injectReplayButton(pageKey);

    if (this.isDismissed(pageKey)) return;                       // ปิดถาวรแล้ว — ไม่เด้งอีกเลย
    if (localStorage.getItem('tourSeen:' + pageKey)) return;     // เคยดู/ข้ามในเครื่องนี้แล้ว
    if (localStorage.getItem('tour:' + pageKey)) return;         // legacy key ของระบบเดิม

    setTimeout(() => { if (!this._active) this.start(pageKey); }, 800);
  },

  _injectReplayButton(pageKey) {
    if (document.getElementById('tourReplayBtn')) return;
    const btn = document.createElement('button');
    btn.id = 'tourReplayBtn';
    btn.type = 'button';
    btn.className = 'tour-replay-btn';
    btn.title = 'ดูวิธีใช้หน้านี้';
    btn.innerHTML = '📘';
    btn.onclick = () => this.start(pageKey);
    document.body.appendChild(btn);
  },

  // ---------- runner ----------
  start(pageKey) {
    if (this._active) return;
    const tour = this.TOURS[pageKey];
    const steps = this._resolveSteps(tour);
    if (!tour && steps.length === 0) return;
    this._active = true;

    const overlay = document.createElement('div');
    overlay.id = 'obOverlay';
    document.body.appendChild(overlay);

    const close = (markSeen) => {
      overlay.remove();
      document.removeEventListener('keydown', keyNav);
      this._active = false;
      if (markSeen) localStorage.setItem('tourSeen:' + pageKey, '1');
    };
    const dismissForever = (all) => {
      this.dismiss(pageKey, all);
      close(true);
      if (typeof Layout !== 'undefined' && Layout.toast)
        Layout.toast(all ? 'ปิดการสอนทุกหน้าแล้ว — เปิดดูเองได้จากปุ่ม 📘' : 'ปิดการสอนหน้านี้แล้ว — เปิดดูเองได้จากปุ่ม 📘');
    };

    let i = -1; // -1 = การ์ดแนะนำภาพรวม (intro)

    const esc = (s) => (s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    const nl2br = (s) => esc(s).replace(/\n/g, '<br>');

    const footerHtml = `
      <div class="ob-footer">
        <label class="ob-dismiss"><input type="checkbox" id="obDismissAll"> ปิดการสอนทุกหน้า</label>
        <button type="button" class="ob-link" id="obDismissBtn">🚫 ปิดการสอนนี้ ไม่ต้องแสดงอีก</button>
      </div>`;

    const renderIntro = () => {
      const t = tour || { title: 'แนะนำการใช้งานหน้านี้', intro: 'กด "เริ่ม" เพื่อดูคำแนะนำทีละจุด' };
      overlay.innerHTML = `
        <div class="ob-backdrop"></div>
        <div class="ob-card ob-center">
          <div class="ob-emoji">👋</div>
          <div class="ob-title">${esc(t.title)}</div>
          <div class="ob-body">${nl2br(t.intro)}</div>
          <div class="ob-actions">
            <button type="button" class="ob-btn-secondary" id="obSkip">ข้ามครั้งนี้</button>
            ${steps.length > 0 ? '<button type="button" class="ob-btn-primary" id="obStart">เริ่มดูทีละจุด →</button>'
                               : '<button type="button" class="ob-btn-primary" id="obStart">เข้าใจแล้ว ✓</button>'}
          </div>
          ${footerHtml}
        </div>`;
      overlay.querySelector('#obSkip').onclick = () => close(true);
      overlay.querySelector('#obStart').onclick = () => {
        if (steps.length === 0) { close(true); return; }
        i = 0; renderStep();
      };
      bindDismiss();
    };

    const bindDismiss = () => {
      const btn = overlay.querySelector('#obDismissBtn');
      if (btn) btn.onclick = () => dismissForever(overlay.querySelector('#obDismissAll')?.checked);
    };

    const renderStep = () => {
      const s = steps[i];
      const el = s.el || this._find(s.sel);
      const progress = steps.map((_, k) =>
        `<span class="ob-dot${k === i ? ' on' : ''}"></span>`).join('');
      const nav = `
        <div class="ob-actions">
          ${i > 0 ? '<button type="button" class="ob-btn-secondary" id="obPrev">← ก่อนหน้า</button>' : '<span></span>'}
          <div class="ob-dots">${progress}</div>
          ${i < steps.length - 1
            ? '<button type="button" class="ob-btn-primary" id="obNext">ถัดไป →</button>'
            : '<button type="button" class="ob-btn-primary" id="obDone">เสร็จสิ้น ✓</button>'}
        </div>`;

      if (el) {
        el.scrollIntoView({ block: 'center', behavior: 'smooth' });
        setTimeout(() => {
          const r = el.getBoundingClientRect();
          // popover วางล่าง element; ถ้าล้นจอ → วางบน
          const below = r.bottom + 190 < window.innerHeight;
          const top = below ? r.bottom + 12 : Math.max(8, r.top - 200);
          const left = Math.min(Math.max(8, r.left), Math.max(8, window.innerWidth - 400));
          overlay.innerHTML = `
            <div class="ob-backdrop ob-see-through"></div>
            <div class="ob-ring" style="top:${r.top - 6}px;left:${r.left - 6}px;width:${r.width + 12}px;height:${r.height + 12}px;"></div>
            <div class="ob-card ob-pop" style="top:${top}px;left:${left}px;">
              <div class="ob-title">${esc(s.title || '')}</div>
              <div class="ob-body">${nl2br(s.body || s.text || '')}</div>
              ${nav}${footerHtml}
            </div>`;
          wire();
        }, 260);
      } else {
        // ไม่เจอ element (UI เปลี่ยน/ยังไม่ render) — สอนต่อด้วยการ์ดกลางจอ ไม่พังกลางคัน
        overlay.innerHTML = `
          <div class="ob-backdrop"></div>
          <div class="ob-card ob-center">
            <div class="ob-title">${esc(s.title || '')}</div>
            <div class="ob-body">${nl2br(s.body || s.text || '')}</div>
            ${nav}${footerHtml}
          </div>`;
        wire();
      }
    };

    const wire = () => {
      const n = overlay.querySelector('#obNext'); if (n) n.onclick = () => { i++; renderStep(); };
      const p = overlay.querySelector('#obPrev'); if (p) p.onclick = () => { i--; renderStep(); };
      const d = overlay.querySelector('#obDone'); if (d) d.onclick = () => close(true);
      bindDismiss();
    };

    const keyNav = (e) => {
      if (e.key === 'Escape') close(true);
      else if (e.key === 'ArrowRight' && i >= 0 && i < steps.length - 1) { i++; renderStep(); }
      else if (e.key === 'ArrowLeft' && i > 0) { i--; renderStep(); }
    };
    document.addEventListener('keydown', keyNav);

    renderIntro();
  },

  _resolveSteps(tour) {
    if (tour && tour.steps) return tour.steps.slice();
    // legacy: อ่านจาก data-tour-step attributes ของหน้า (ระบบเดิม 17 หน้า)
    return Array.from(document.querySelectorAll('[data-tour-step]'))
      .map(el => ({ el, n: parseInt(el.getAttribute('data-tour-step') || '0', 10),
                    body: el.getAttribute('data-tour-text') || '' }))
      .filter(s => s.n > 0 && s.body)
      .sort((a, b) => a.n - b.n);
  },

  _find(sels) {
    for (const sel of (Array.isArray(sels) ? sels : [sels])) {
      try {
        const el = document.querySelector(sel);
        if (el && el.offsetParent !== null) return el;   // ต้องมองเห็นจริง
      } catch {}
    }
    return null;
  },
};

// documents.html เปิดได้ 2 ฝั่ง (ขาย=documents / ซื้อ=expense-docs) — ใช้เนื้อหาเดียวกัน
Onboarding.TOURS['expense-docs'] = Onboarding.TOURS.documents;

window.Onboarding = Onboarding;
