namespace Accounting.Helpers;

/// <summary>
/// คีย์สำหรับ <c>pg_advisory_xact_lock</c> — <b>ต้อง deterministic ข้าม process
/// และข้ามเครื่อง</b>
///
/// ═══ ที่มา (บั๊กจริงที่เคยแก้ไปแล้วแต่เหลือที่อื่น) ═══
/// เดิมทุกจุดคำนวณคีย์ด้วย <c>HashCode.Combine(...)</c> ซึ่ง .NET
/// <b>สุ่ม seed ใหม่ทุก process</b> (Marvin hash) ⇒ instance A กับ instance B
/// ได้คีย์คนละค่าสำหรับทรัพยากรเดียวกัน ⇒ <b>ล็อกไม่กันกันเลย</b> ทั้งที่โค้ด
/// อ่านแล้วเหมือนกันได้ป้องกัน. อาการที่ผู้ใช้เจอ: เลขเอกสาร/เลข JE ซ้ำ หรือ
/// "อนุมัติไม่สำเร็จ" แบบสุ่มที่กดใหม่แล้วหาย — เกิดเฉพาะตอนมีหลาย instance
/// จึงไม่โผล่ตอนเทสต์เครื่องเดียว
///
/// รอบก่อนแก้ไปแล้ว 1 จุด (<c>JournalEntryBuilder.JournalNumberLockKey</c>) แต่
/// เหลืออีก <b>6 จุด</b> ที่ยังใช้ <c>HashCode.Combine</c> — รวมถึงตัวออก
/// <b>เลขเอกสาร</b> (§86/4 บังคับเลขไม่ซ้ำ ไม่ขาดช่วง) · ตัวจับคู่รายการธนาคาร
/// (สองเครื่องอ้างสิทธิ์ธุรกรรมเดียวกันได้) · การปรับสต็อก (ยอดหายจริง) และ
/// <b>ตัวออกเลข JE ตัวที่ 5 ใน DocumentService</b> ที่ยังไม่ถูกยุบทิ้งรอบก่อน
/// = defect class "แก้ตัวเดียว เหลือที่เหลือ" → ยุบมาไว้ที่นี่ที่เดียว
///
/// <para><b>กติกา:</b> ทุกที่ที่เรียก <c>pg_advisory_xact_lock</c> ต้องเอาคีย์
/// จากคลาสนี้เท่านั้น · ทรัพยากรเดียวกันต้องใช้ <paramref name="scope"/>
/// เดียวกันเป๊ะ (คนละ scope = คนละล็อก = ไม่กันกัน)</para>
/// </summary>
public static class AdvisoryLockKey
{
    /// <summary>FNV-1a 64-bit — เสถียรตลอดกาล ไม่ขึ้นกับ runtime/process
    /// (ต่างจาก <c>HashCode.Combine</c> / <c>string.GetHashCode()</c>)</summary>
    public static long For(Guid companyId, string scope, string part)
    {
        unchecked
        {
            ulong h = 14695981039346656037UL;
            void Mix(string s)
            {
                foreach (var ch in s) { h ^= ch; h *= 1099511628211UL; }
            }
            Mix(companyId.ToString("N"));
            Mix($"|{scope}|");
            Mix(part);
            return (long)h;
        }
    }

    /// <summary>สำหรับทรัพยากรที่คีย์เป็น GUID ไม่ซ้ำทั้งระบบอยู่แล้ว
    /// (เช่นสินค้ารายตัว) จึงไม่ต้องผูกกับบริษัท</summary>
    public static long For(string scope, string part) => For(Guid.Empty, scope, part);

    // ── scope กลาง — ประกาศไว้ที่เดียวกันไม่ให้พิมพ์ผิดแล้วกลายเป็นคนละล็อก ──

    /// <summary>เลขสมุดรายวัน (JE) — part = prefix เต็มรวมเดือน เช่น "JV-202608-"</summary>
    public const string JournalSequence = "je-seq";
    /// <summary>เลขเอกสาร — part = prefix ของชนิดเอกสาร</summary>
    public const string DocumentSequence = "doc-seq";
    /// <summary>อ้างสิทธิ์จับคู่รายการธนาคาร — part = BankTransactionId</summary>
    public const string BankReconcile = "bank-rec";
    /// <summary>เลขอ้างอิงของงานการเงิน — part = "{prefix}-{yyyyMM}"</summary>
    public const string FinanceReference = "fin-ref";
    /// <summary>ปรับสต็อกรายสินค้า (read-modify-write) — part = ProductId</summary>
    public const string StockAdjust = "stock-adj";
    /// <summary>ซ่อมผลแกะรุ่น 6–8 (<c>OcrLumpSumRepairService</c> · ทีม OCR 11) — หนึ่งการซ่อมต่อบริษัท · part = "repair"</summary>
    public const string OcrLumpSumRepair = "ocr-lumpsum";
    /// <summary>ออกค่าเหมา add-on รายเดือน — part = งวด "yyyy-MM" (กันหลาย instance
    /// ออกบิลงวดเดียวกันพร้อมกัน · คีย์ระดับระบบไม่ผูกบริษัท)</summary>
    public const string AddOnMonthlyBilling = "addon-bill";
    /// <summary>night audit ของที่พัก — part = PropertyId</summary>
    public const string LodgingNightAudit = "lodging-audit";
    /// <summary>ล้างภาษีซื้อ undue ที่พ้น 6 เดือน (§82/3) — part = "undue-vat"
    /// **ต้องผูก companyId**: งานทำรายบริษัท เดิมใช้ค่าคงที่ 828003 ทั้งระบบ ⇒
    /// ผู้ใช้บริษัท A กดปุ่มแล้วผู้ใช้บริษัท B ต้องรอจนเสร็จ ทั้งที่คนละชุดข้อมูล</summary>
    public const string UndueVatExpiry = "undue-vat";
    /// <summary>ปิดรอบบิลค่าใช้งาน (รวม UsageEvent เป็นใบแจ้งหนี้) — part = งวดที่รัน
    /// · คีย์ระดับระบบไม่ผูกบริษัท เพราะงานเดินทีเดียวทุก tenant</summary>
    public const string UsageInvoicing = "usage-invoice";
    /// <summary>เพิ่มโควตาเอกสาร (ซื้อ top-up / แลกจากภารกิจ) — part = "reward"/"topup"
    /// เพดานต่อวัน-เดือนจะไร้ผลทันทีถ้าปล่อยให้สองแท็บกดพร้อมกันแล้วผ่านทั้งคู่</summary>
    public const string QuotaGrant = "quota-grant";

    /// <summary>การชำระเงินผ่าน gateway — ล็อกทั้งตอน "มี intent อยู่แล้วไหม" (คีย์ = source)
    /// และตอนเปลี่ยนสถานะ (คีย์ = intent id) · สองแท็บที่กดจ่ายพร้อมกันต้องได้ QR ใบเดียว
    /// ไม่ใช่สองใบซ้อน · webhook กับ job กระทบยอดต้องไม่เขียนทับกัน</summary>
    public const string PaymentIntent = "pay-intent";

    /// <summary>บันทึกเงินที่ผู้ให้บริการโอนเข้า (settlement) — ล็อก**ต่อ provider ต่อบริษัท**
    /// เพราะการเลือกรายการ "ที่ยังไม่ถูกโอน" แล้วมาร์กทีหลังเป็น read-modify-write:
    /// สองคนกดพร้อมกันจะเลือกชุดเดียวกันแล้วลง JE ซ้ำ ⇒ ธนาคารเกินสองเท่า</summary>
    public const string GatewaySettlement = "pay-settle";

    /// <summary>นำเข้า/แก้รอบโอน settlement (รอบ 198 ทีม B) — part = SettlementChannel.Id ·
    /// ตรวจ "มีบรรทัดนี้แล้วไหม" แล้วค่อยเพิ่มเป็น read-modify-write: สองคนนำเข้าไฟล์เดียวกันพร้อมกัน = ยอดขาย/ค่าธรรมเนียมซ้ำ
    /// (unique index เป็นตาข่ายชั้นสุดท้าย แต่ชนแล้วทั้งไฟล์ล้มด้วยข้อความของฐานข้อมูล)</summary>
    public const string SettlementImport = "settlement-import";

    /// <summary>ทำงานกับ batch ของศูนย์นำเข้าข้อมูล (Import Hub · รอบ 205) — part = Id ของ batch (<c>SmartImportSession.Id</c>) ผูก companyId ·
    /// worker วิเคราะห์ไฟล์ใหญ่ / commit / undo ของ batch เดียวกันต้องไม่ซ้อนกัน (สองเครื่องหยิบ batch เดียวกัน = แถวลงซ้ำ) ·
    /// ใช้ผ่าน <c>JobLock.RunExclusiveAsync(db, ImportBatch, batchId.ToString(), …, companyId)</c> (session lock — commit ผ่าน domain service
    /// ที่เปิดธุรกรรมเองหลายขั้น xact lock จะหลุดตั้งแต่ขั้นแรก)</summary>
    public const string ImportBatch = "import-batch";

    /// <summary>สร้างผู้ติดต่อกลาง "ลูกค้าเงินสด" ของบริษัท (<c>WalkInCustomerContact</c>) — part = "" · ตรวจ-แล้ว-สร้างเป็น read-modify-write:
    /// ลงบัญชีรอบโอนสองช่องทางพร้อมกัน (คนละล็อกช่องทาง) หรือ integration = ได้สองแถว แล้ว <c>FirstOrDefault(IsWalkInCustomer)</c>
    /// หยิบแถวไหนก็ได้ (review198-C C-13)</summary>
    public const string WalkInContact = "walk-in-contact";

    /// <summary>งานเทรน local model จาก feedback (กฎเหล็ก #1 ขั้น DISTILL) —
    /// part = "global" · คีย์ระดับระบบไม่ผูกบริษัท เพราะงานเดินทีเดียวทุก tenant
    ///
    /// <para>เดิม<b>ไม่มีล็อกเลย</b> ต่างจาก job อื่นทุกตัว ⇒ สอง instance ตื่นพร้อมกัน
    /// (หน่วงเริ่ม 7 นาทีเท่ากันทุกเครื่อง จึงตื่นพร้อมกัน<b>เกือบเสมอ</b>) แล้ว
    /// upsert `LocalModelHealth`/`AiLearnedMemory` ทับกัน — ตัวเลขความแม่นที่แอดมิน
    /// ใช้ตัดสินว่า "ปิด AI ได้หรือยัง" กลายเป็นของครึ่ง ๆ ของสองรอบ</para></summary>
    public const string AiFeedbackTraining = "ai-train";

    // ── number space อื่น ๆ ที่ไม่ใช่เลขเอกสาร/เลข JE (ผลตรวจ F-08) ──
    // ทุกตัวเคยออกเลขเองด้วย OrderByDescending().First()+1 โดยไม่มีล็อก
    // และเรียงแบบ **ข้อความ** (⇒ "9999" > "10000" ⇒ เลขวนกลับไปทับของเดิม)

    /// <summary>เลขใบรับ-จ่ายเงิน (Payment) — part = prefix รวมงวด "PAY-yyyyMM-"</summary>
    public const string PaymentSequence = "pay-seq";
    /// <summary>รหัสสินทรัพย์ถาวร — part = prefix ของบริษัท</summary>
    public const string AssetSequence = "asset-seq";

    /// <summary>โพสต์ค่าเสื่อมราคาของงวดหนึ่ง — ผูกกับ (บริษัท, ปี-เดือน)
    ///
    /// <para>⚠️ cron มี <c>JobLock</c> ของตัวเองอยู่แล้ว แต่ล็อกนั้นอยู่ที่
    /// <b>ตัว background service</b> ไม่ได้อยู่ในเมธอดที่ลง JE ⇒ ผู้ใช้กด
    /// <c>POST depreciate</c> ตอน cron กำลังรัน (หรือกดสองแท็บพร้อมกัน) จะผ่าน
    /// ด่าน <c>IsPosted</c> พร้อมกันทั้งคู่แล้ว <b>บวก AccumulatedDepreciation
    /// สองเท่า</b> พร้อม JE คนละใบ (ผลตรวจทีม E · E-04)</para></summary>
    public const string AssetDepreciation = "asset-dep";
    /// <summary>เลขการจองที่พัก — part = prefix รวมงวด</summary>
    public const string ReservationSequence = "resv-seq";
    /// <summary>บันทึก "คืนเงินแขกแล้ว" ของการจองที่พัก — part = id การจอง (รอบ 193 ฝ่ายค้าน: สองคำขอพร้อมกันผ่านด่านทั้งคู่
    /// แล้ว RefundPaidAmount ถูกเขียนทับ)</summary>
    public const string LodgingRefundPaid = "lodging-refund";
    /// <summary>เช็คเอาต์ / ออกใบเช็คเอาต์ใหม่ของการจองที่พัก — part = id การจอง (รอบ 201 ฝ่ายค้าน X7: สองคำขอพร้อมกันผ่านด่าน
    /// "ยังไม่มีใบ/ใบเดิมถูกยกเลิก" ทั้งคู่ ⇒ ใบกำกับสองใบ) · session lock (<c>JobLock.RunExclusiveAsync</c>) เพราะเส้นออกเอกสาร/ใช้มัดจำ
    /// เปิดธุรกรรมของตัวเองหลายขั้น (xact lock จะหลุดตั้งแต่ขั้นแรก)</summary>
    public const string LodgingCheckout = "lodging-checkout";
    /// <summary>ยืนยัน/รับมัดจำของการจองที่พัก — part = id การจอง (ฝ่ายค้านรอบ 202 P2-1: ยืนยันซ้อนสองคำขอ (เว็บสองแท็บ · webhook + poll ·
    /// พนักงาน + เงินออนไลน์) ⇒ ใบมัดจำสองใบ + DepositPaid lost update) · session lock เพราะเส้นออกใบมัดจำเปิดธุรกรรมของตัวเอง</summary>
    public const string LodgingConfirm = "lodging-confirm";
    /// <summary>รับรู้/ริบใบมัดจำ — part = id ใบมัดจำ (รอบ 194 ฝ่ายค้าน P-a: สองคำขอพร้อมกันผ่านด่านยอดคงค้างทั้งคู่
    /// ⇒ ใบกำกับของยอดที่ริบสองใบ · ต้องเป็น session lock เพราะเส้นออกใบกำกับเปิดธุรกรรมของตัวเองหลายขั้น)</summary>
    public const string DepositRealize = "deposit-realize";
    /// <summary>สร้างเอกสาร/สมุดรายวันจากรายการประจำหนึ่งรอบ — part = id รายการประจำ (ทีมตรวจรายการประจำ 2026-10-14):
    /// cron หลายเครื่อง + ปุ่ม "สร้างทันที" ต้องไม่ออกใบงวดเดียวกันสองใบ · <b>session lock</b> (<c>JobLock.RunExclusiveAsync</c>) เพราะ
    /// <c>CreateDocumentAsync</c>/<c>ApproveDocumentAsync</c> เปิดธุรกรรมของตัวเอง — เดิมห่อด้วยธุรกรรม + <c>FOR UPDATE</c> ⇒ EF โยน
    /// "connection is already in a transaction" ทุกรอบ (รายการประจำชนิดเอกสารไม่เคยออกใบได้ทั้งทาง cron และ "สร้างทันที")</summary>
    public const string RecurringRun = "recurring-run";

    /// <summary>คีย์ล็อก "ยอดคงค้างของใบมัดจำ" ตัวเดียวของทุกทางเข้าที่อ่าน-แล้ว-เขียนยอดรับรู้/ตัดชำระ (รอบ 194 R2 · P-a):
    /// ปุ่มรับรู้/ริบ · ที่พัก · CMS (<c>RealizeDepositAsync</c> — session lock ผ่าน <c>JobLock</c> เพราะเส้นริบเปิดธุรกรรมของตัวเองหลายขั้น) ·
    /// อนุมัติใบที่หักฐานมัดจำ (<c>LoadTaxedDepositsByRefAsync(lockRows)</c>) · ตัดชำระด้วยมัดจำ (<c>ApplyDepositToInvoiceAsync</c>) —
    /// สองตัวหลังอยู่ในธุรกรรม ⇒ <c>pg_advisory_xact_lock</c> คีย์เดียวกัน (session กับ xact lock ของคีย์เดียวกันรอกันเองตามปกติ ·
    /// session เดียวกันถือซ้ำได้ ⇒ เส้นริบที่ถือ session lock แล้วเรียกตัดชำระไม่ติดตัวเอง) · ต้องได้ล็อกนี้<b>ก่อน</b>ล็อกแถว (กัน deadlock)</summary>
    public static long DepositRealizeKey(Guid companyId, Guid depositId) => For(companyId, DepositRealize, depositId.ToString());
    /// <summary>เลขคำสั่งซื้อ/การจองจากหน้าเว็บ (CMS) — part = prefix รวมงวด</summary>
    public const string StorefrontSequence = "store-seq";
    /// <summary>ตัดสิน "รอบนี้ยังว่างไหม" + บันทึกการจองคิว CMS (Helpers/BookingSlotEngine) — part = บริการ หรือกลุ่มทรัพยากรร่วมของเว็บ</summary>
    public const string CmsBookingSlot = "cms-booking-slot";
    /// <summary>ออกใบกำกับของการจองนัดหมายหลังจอง (รับเงิน/ให้บริการ · BookingInvoiceTimingPolicy ข้อ 155) — part = การจอง · กันกดซ้ำพร้อมกันแปลงสองใบ</summary>
    public const string CmsBookingInvoice = "cms-booking-invoice";
    /// <summary>รหัสผังบัญชีที่ระบบสร้างให้อัตโนมัติ — part = ช่วงเลขที่ใช้</summary>
    public const string AccountCodeSequence = "coa-seq";
    /// <summary>เลขเอกสารของโมดูลย่อย (เบิกค่าใช้จ่าย · เงินกู้ · โอนคลัง ฯลฯ)
    /// — part = prefix รวมงวด</summary>
    public const string ModuleSequence = "mod-seq";

    /// <summary>งานเบื้องหลังตามตาราง — part = ชื่องาน (ผลตรวจ F-09)
    ///
    /// <para>จาก 16 job มีแค่ 5 ตัวที่ล็อก · ที่เหลือรันพร้อมกันได้ทุกเครื่อง
    /// และหลายตัว<b>เขียนข้อมูลจริง</b> ไม่ใช่แค่ทำงานซ้ำ: ค่าเสื่อมลง JE
    /// สองเท่า · ค่าปรับล่าช้าคิดซ้ำ · อีเมลทวงหนี้ส่งถึงลูกค้า N ครั้ง
    /// ตามจำนวนเครื่อง</para></summary>
    public const string BackgroundJob = "job";
    /// <summary>ตั้ง/ซ่อมเจ้าของบริษัท (assign-owner · repair · สร้างผู้ใช้เป็น Owner) — part = "owner" ผูก companyId · กัน "ตรวจว่าไม่มีเจ้าของ"
    /// กับ "เลื่อนบทบาท" สลับกันจนได้ Owner สองคน (ฝ่ายค้านรอบ 205 ทีม I)</summary>
    public const string CompanyOwnership = "company-owner";

    /// <summary>สร้างผู้ติดต่อจากสแกน OCR — part = เลขผู้เสียภาษี (ตัวเลขล้วน · <c>OcrContactCreateLock.LockPart</c>) ผูก companyId
    /// (รอบ 200 · คำตัดสินข้อ 19 · K-5): หน้าเว็บอัปโหลด 3 ไฟล์พร้อมกัน ⇒ ใบ Makro 00005 สองใบได้แถวสาขาซ้ำ เพราะไม่มี unique index
    /// (ตั้งใจไม่เพิ่มจนกว่าข้อมูลซ้ำเดิมจะถูกจัดการ) — ล็อกต่อ "นิติบุคคล" (ไม่ใช่ต่อสาขา) เพราะแถวสาขาใหม่ถูกตัดสินจากแถวทุกสาขาของเลขนั้น</summary>
    public const string OcrContactCreate = "ocr-contact-create";

    /// <summary>ต่อ audit hash chain ของบริษัท — part = "" (รอบ 201 ทีม PL · A-PL1 · คำตัดสินข้อ 32): สองคำขอพร้อมกันอ่านปลาย chain
    /// เดียวกันแล้วประทับ PrevHash เดียวกัน = แตกกิ่ง ⇒ ถือ <c>pg_advisory_xact_lock</c> ตั้งแต่อ่านปลาย chain จนธุรกรรมจบ ·
    /// ผู้ใช้คีย์นี้มีที่เดียว: <c>AccountingDbContext.SaveChanges/SaveChangesAsync</c> (ล็อกท้ายสุดของคำสั่ง — หลังล็อกเอกสาร/เลข JE)</summary>
    public const string AuditChain = "audit-chain";

    /// <summary>resync เอกสารจากระบบภายนอก — part = id เอกสาร (ฝ่ายค้าน GWO-4 รอบ 201 ทีม GW): dry-run → กลับ JE เดิม → ลง JE ใหม่ ต้องเป็นธุรกรรมเดียวและ
    /// คำขอ resync ซ้อนของเอกสารเดียวกันต้องรอกัน (ไม่งั้นสองคำขอเห็น JE เดิมชุดเดียวกัน ⇒ กลับซ้ำ/ลงซ้ำ)</summary>
    public const string IntegrationResync = "int-resync";

    /// <summary>ส่งรายงานตามกำหนดเวลา <b>หนึ่งรายการ</b> — part = id รายงาน · ผูก companyId (ฝ่ายค้านรอบ 2 P1-2): ปุ่ม "ส่งทันที" เคยถือล็อกรวมของ
    /// ตัวส่งตามรอบ (<see cref="BackgroundJob"/>) ระหว่างสร้าง/ส่ง ⇒ รอบรายชั่วโมงได้ล็อกไม่ได้แล้วข้ามรายงาน<b>ของทุกบริษัท</b>ในชั่วโมงนั้น
    /// (IsDue ต้องตรงชั่วโมง = หายทั้งงวด) · ล็อกรวมเหลือไว้กันสองเครื่องเดินรอบพร้อมกันเท่านั้น</summary>
    public const string ScheduledReportSend = "sched-report";

    /// <summary>ปิดปี · ยกยอดด้วยมือ · ซ่อมยอดยกมา — ผูก companyId · ใช้ผ่าน <c>OpeningRoll.LockKey</c> เท่านั้น (ฝ่ายค้านรอบส่งออก 2026-10-15
    /// P1-1 + รอบ 2 P2-7: เดิมล็อกแค่การซ่อม ⇒ ปิดปี/ยกยอดด้วยมือพร้อมการซ่อมเขียนงวดเดียวกันทับกัน) · xact lock เพราะทุกทางอยู่ในธุรกรรมเดียว</summary>
    public const string OpeningRoll = "opening-roll";

    /// <summary>เลขบัตรคอร์ส/เครดิต (ทีม L · <c>CourseCreditService</c>) — part = prefix "CP-yyyyMM-" · ผูก companyId</summary>
    public const string CreditPassSequence = "credit-pass-seq";

    /// <summary>กันเครดิตจากเครื่องจองภายนอก — part = externalRef · ผูก companyId (ฝ่ายค้านทีม L ข้อ 3: ref เดียวกันสองคำขอพร้อมกัน = จองซ้ำ)</summary>
    public const string CreditHold = "credit-hold";

    /// <summary>ตารางสอนของครูฝึก — part = "{employeeId}:{yyyyMMdd วันไทย}" · ผูก companyId (ฝ่ายค้านทีม L ข้อ 5: ตรวจว่าง→จอง ซ้อนกันสองคำขอ)</summary>
    public const string CreditTrainerDay = "credit-trainer-day";

    /// <summary>ขายคอร์ส 1 คำขอ (idempotency key ของหน้าจอ) — part = key · session lock ผ่าน <c>JobLock.RunExclusiveAsync</c> ครอบการออกใบ+อนุมัติ+เปิดบัตร</summary>
    public const string CreditSale = "credit-sale";
}
