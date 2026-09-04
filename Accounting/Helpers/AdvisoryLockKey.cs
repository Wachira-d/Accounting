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
    /// <summary>เลขการจองที่พัก — part = prefix รวมงวด</summary>
    public const string ReservationSequence = "resv-seq";
    /// <summary>เลขคำสั่งซื้อ/การจองจากหน้าเว็บ (CMS) — part = prefix รวมงวด</summary>
    public const string StorefrontSequence = "store-seq";
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
}
