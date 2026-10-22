using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>เอกสารใบนี้ควรถูกปฏิบัติอย่างไรเมื่อโควตาเดือนเต็ม</summary>
public enum QuotaEnforcement
{
    /// <summary>ไม่นับ ไม่กระทบโควตา (ใบเสนอราคา · ใบส่งของ · เอกสารภายใน)</summary>
    NotCounted = 0,
    /// <summary>นับ · เกินโควตาแล้ว **ปฏิเสธได้** — ยังไม่มีพันธะทางกฎหมาย/เงิน
    /// (ผู้ใช้เลื่อนไปทำเดือนหน้าหรืออัปเกรดได้โดยไม่มีใครเสียหาย)</summary>
    CountedBlockable = 1,
    /// <summary>นับ · เกินโควตาแล้ว **ห้ามปฏิเสธเด็ดขาด** — ต้องออกเอกสารให้ได้
    /// แล้วคิดเป็น overage แทน. เหตุผลตาม LODGING_LICENSING_PLAN §5 + ทีม CPA:
    /// ใบกำกับภาษี (§86/4) ต้องออกทันทีที่ tax point เกิด · ใบเสร็จของเงินที่รับมาแล้ว ·
    /// ใบลดหนี้/เพิ่มหนี้ (§86/9-10) ที่ถ้าค้างจะทำให้ ภ.พ.30 เกินความจริง —
    /// **การบล็อกทำให้ลูกค้าผิดกฎหมาย และเราเป็น "สาเหตุ"**</summary>
    CountedMustAllow = 2,
}

/// <summary>
/// **กติกาโควตาเอกสาร — pure ตัวเดียวของระบบ** (LODGING_LICENSING_PLAN.md §4, §5, §13)
///
/// เดิม `DocumentService.CreateDocumentAsync` throw ทุกชนิดเอกสารเมื่อเกินโควตา
/// ⇒ พนักงาน front desk เจอ exception กลางการเช็คเอาต์ทั้งที่แขกจ่ายเงินแล้ว
/// (ทั้งผิดกฎหมายและ defect class "สถานะปลายทางที่ผู้ใช้ไปต่อไม่ได้")
///
/// สองคำถามที่แยกกันเด็ดขาด:
///   1. **นับไหม** — นับเฉพาะใบที่ "มีรายได้" คือใบที่เป็นตัวแทนของการขาย 1 ครั้ง
///      ใบเสร็จรับชำระของใบที่นับไปแล้ว / ใบลดหนี้ / เอกสารฝั่งซื้อ **ไม่นับซ้ำ**
///      (ทีม CPA: 1 การเข้าพักออกเอกสาร 3-4 ใบ ถ้านับดิบลูกค้าจะรู้สึกโดนคิดซ้ำซ้อน)
///   2. **บล็อกได้ไหม** — ต่างจากข้อ 1 คนละแกน: ใบลดหนี้ไม่นับโควตา แต่ก็ห้ามบล็อก
/// </summary>
public static class DocumentQuotaPolicy
{
    /// <summary>ชนิดเอกสารนี้ปฏิบัติอย่างไร
    ///
    /// <paramref name="fromLodging"/> = เอกสารที่โมดูลที่พักออกให้อัตโนมัติ — <b>นับโควตาเอกสาร (ใบที่กฎหมายบังคับ) + ห้ามบล็อกทุกใบ</b>
    /// จนกว่าการแปลง "1 การเข้าพัก = 2 หน่วยของ MaxDocumentsPerMonth" (LODGING_LICENSING_PLAN §13.2) จะมีโค้ด —
    /// ดูคอมเมนต์ในตัวเมธอด (ฝ่ายค้าน ภ.ง.ด.1/Tb P1-1)</summary>
    /// <param name="isReplacement">ใบกำกับภาษีเต็มรูปที่ออก "แทน" ใบเสร็จ/ใบกำกับ
    /// อย่างย่อ (§86/6 → §86/4) — <b>การขายครั้งเดียวกัน</b>กับใบที่นับไปแล้ว
    /// จึงไม่นับซ้ำ (หลักการข้อ 1 ของไฟล์นี้: นับ "การขาย 1 ครั้ง" ไม่ใช่นับใบ)
    /// และห้ามบล็อกเพราะ §86 บังคับให้ออกใบกำกับให้ผู้ซื้อ</param>
    public static QuotaEnforcement Classify(DocumentType type, bool isDeposit, bool fromLodging,
        bool isReplacement = false)
    {
        if (isReplacement) return QuotaEnforcement.NotCounted;
        // ⚠️ ที่พัก — คืนพฤติกรรมเดิม (ก่อน 058233b5): ใบที่กฎหมายบังคับ (ใบกำกับ/ใบเสร็จมัดจำ/…) นับโควตาเอกสาร + ห้ามบล็อก ·
        // ใบอื่นไม่นับ (และจึงไม่มีวันถูกบล็อก)
        //
        // ทำไมไม่ "ไม่นับทุกใบ" ตาม LODGING_LICENSING_PLAN §13.2 ("เอกสารที่ Lodging ออกเอง ไม่นับซ้ำ"): §13.2 แทนที่ด้วย
        // "1 stay-unit = 2 หน่วยของ MaxDocumentsPerMonth" แต่การแปลงนั้น**ยังไม่มีโค้ด** — มิเตอร์ lodging.stay ราคา 0.0
        // ("มิเตอร์นับอย่างเดียว — โควตาอยู่ที่แพ็กเกจบัญชี" · DatabaseMigrationHelper) และ CurrentMonthDocuments++ มีที่เดียว
        // (SubscriptionService.IncrementUsageAsync) ⇒ ไม่นับเอกสาร = ปริมาณงานของที่พักไม่ถูกคิดเงินที่ไหนเลย (โรงแรมออกใบกำกับ 300 ใบ/เดือน
        // ⇒ 0 หน่วย 0 บาท) · "คิดสองเด้ง" ที่ 058233b5 อ้าง ไม่เกิดจริงในเงิน (stay = 0 บาท) · หลัก "ห้ามแย่ลง": รายได้ห้ามหายเงียบ
        //
        // TODO(LODGING_LICENSING_PLAN §13.2 · คำถามเจ้าของใน UNIT_TEST_TRIAGE): เมื่อทำการแปลง stay → 2 หน่วยโควตา (ตอนปิดการเข้าพัก ·
        // MeterStayAsync + LodgingNightAuditJob · idempotent ต่อการจอง · overage ตาม §4) แล้ว ค่อยเปลี่ยนบรรทัดนี้เป็น NotCounted ทุกชนิด
        // ในคอมมิตเดียวกัน — ห้ามเปลี่ยนก่อน
        if (fromLodging) return MustAlwaysIssue(type) ? QuotaEnforcement.CountedMustAllow : QuotaEnforcement.NotCounted;
        return type switch
        {
            // ── ใบที่แทน "การขาย 1 ครั้ง" → นับ + ห้ามบล็อก ──
            DocumentType.TaxInvoice => QuotaEnforcement.CountedMustAllow,
            DocumentType.Invoice => QuotaEnforcement.CountedMustAllow,
            // ใบเสร็จ: ถ้าเป็นมัดจำ = รับเงินจริงแล้ว (tax point §78/1) นับ 1 ครั้ง ·
            // ใบเสร็จขายสดก็เป็นการขาย 1 ครั้งเช่นกัน — ทั้งคู่ห้ามบล็อก
            DocumentType.Receipt => QuotaEnforcement.CountedMustAllow,

            // ── ใบที่กฎหมายบังคับให้ออกแต่ **ไม่ใช่การขายใหม่** → ห้ามบล็อก แต่ไม่นับ ──
            DocumentType.CreditNote => QuotaEnforcement.NotCounted,
            DocumentType.DebitNote => QuotaEnforcement.NotCounted,
            DocumentType.ReceiptVoucher => QuotaEnforcement.NotCounted,
            DocumentType.CertificateInLieu => QuotaEnforcement.NotCounted,

            // ── เอกสารฝั่งซื้อ: เป็นของคู่ค้า เราแค่บันทึก (มีโควตา OCR แยกอยู่แล้ว) ──
            DocumentType.PurchaseInvoice => QuotaEnforcement.NotCounted,
            DocumentType.Expense => QuotaEnforcement.NotCounted,
            DocumentType.PaymentVoucher => QuotaEnforcement.NotCounted,
            DocumentType.GoodsReceiptNote => QuotaEnforcement.NotCounted,

            // ── เอกสารที่รอได้จริง ๆ → บล็อกได้เมื่อเต็ม ──
            DocumentType.Quotation => QuotaEnforcement.CountedBlockable,
            DocumentType.PurchaseOrder => QuotaEnforcement.CountedBlockable,
            DocumentType.PurchaseRequisition => QuotaEnforcement.CountedBlockable,

            // ── เอกสารประกอบ ไม่มีผลทางภาษี ──
            DocumentType.DeliveryNote => QuotaEnforcement.NotCounted,
            DocumentType.BillingNote => QuotaEnforcement.NotCounted,
            _ => QuotaEnforcement.NotCounted,
        };
    }

    /// <summary>ใบที่ "ห้ามค้าง" ตามกฎหมายไทย — ใช้ตัดสินเฉพาะเรื่องบล็อก/ไม่บล็อก
    /// (คนละคำถามกับการนับโควตา)</summary>
    public static bool MustAlwaysIssue(DocumentType type) => type is
        DocumentType.TaxInvoice or DocumentType.Invoice or DocumentType.Receipt
        or DocumentType.CreditNote or DocumentType.DebitNote
        or DocumentType.ReceiptVoucher or DocumentType.CertificateInLieu;

    /// <summary>นับเข้าโควตาไหม</summary>
    public static bool Counts(QuotaEnforcement e) => e != QuotaEnforcement.NotCounted;

    /// <summary>เกินโควตาแล้วปฏิเสธได้ไหม</summary>
    public static bool CanRefuseWhenOverQuota(QuotaEnforcement e) => e == QuotaEnforcement.CountedBlockable;

    /// <summary>ข้อความบอกผู้ใช้เมื่อถูกปฏิเสธ — ต้องบอก "ทำอะไรต่อได้" เสมอ</summary>
    public static string BlockedMessage(int used, int limit) =>
        $"เดือนนี้ออกเอกสารครบโควตาแพ็กเกจแล้ว ({used}/{limit} ฉบับ) — "
        + "เอกสารที่กฎหมายบังคับ (ใบกำกับภาษี · ใบเสร็จ · ใบลดหนี้) ยังออกได้ตามปกติ "
        + "ส่วนใบเสนอราคา/ใบสั่งซื้อ ให้ซื้อโควตาเพิ่มหรืออัปเกรดแพ็กเกจที่หน้า \"ส่วนเสริมของฉัน\"";

    /// <summary>โควตาที่ใช้ได้จริงในเดือนนี้ = โควตาแพ็กเกจ + โบนัสที่ยังไม่หมดอายุ
    /// (โบนัสมาจาก top-up ที่ซื้อ · แอดมินให้ · ภารกิจแลกโควตา §12)
    ///
    /// <paramref name="planLimit"/> ≤ 0 แปลว่า "ยังไม่ได้ตั้งค่า/ไม่จำกัด" ไม่ใช่
    /// "ศูนย์ใบ" — คืนค่าเดิมไปตรง ๆ ห้ามเอาโบนัสไปกลบ ไม่งั้นแพ็กเกจไม่จำกัด
    /// จะกลายเป็นจำกัดเท่าโบนัสทันทีที่มีใครได้โบนัส
    ///
    /// <para><b>รอบ 205 ทีม E (BOOKING_REVENUE_AUDIT Q-1) — โบนัสถูกนับสองครั้ง</b>: ใบที่เกินโควตาแพ็กเกจ<b>หักโบนัส</b>ทีละใบ
    /// (<see cref="ConsumesBonus"/>) แต่สูตรเดิม <c>plan + โบนัสคงเหลือ</c> เทียบกับตัวนับที่<b>รวม</b>ใบเหล่านั้นแล้ว ⇒ ใบเกินแพ็กเกจ 1 ใบ
    /// กินเพดานไป 2 ⇒ ซื้อ +100 ใบได้ใช้จริง 50 ใบ (ใบที่ 51 ถูกคิด overage/บล็อกใบเสนอราคา) · ตอนนี้ <paramref name="used"/> ที่เกินแพ็กเกจ
    /// ถูกนับเป็นโบนัสที่ใช้ไปแล้ว: เพดาน = max(ใช้ไป, แพ็กเกจ) + โบนัสคงเหลือ (ใช้ไม่เกินแพ็กเกจ = สูตรเดิมทุกตัวเลข)</para></summary>
    /// <param name="used">ใบที่นับเดือนนี้แล้ว (<c>CurrentMonthDocuments</c>) — 0 = ไม่ทราบ (สูตรเดิม)</param>
    public static int EffectiveLimit(int planLimit, int bonusQuota, DateTime? bonusExpiresAt, DateTime nowUtc, int used = 0)
    {
        if (planLimit <= 0 || bonusQuota <= 0) return planLimit;
        if (bonusExpiresAt.HasValue && bonusExpiresAt.Value <= nowUtc) return planLimit;
        return Math.Max(used, planLimit) + bonusQuota;
    }

    /// <summary>ใบที่เพิ่งนับ (ตัวนับหลังเพิ่ม = <paramref name="usedAfter"/>) กินโบนัส 1 ใบไหม — เฉพาะใบที่เกินโควตาแพ็กเกจล้วน ๆ
    /// และโบนัสยังไม่หมดอายุ (ตัวตัดสินเดียวของ <c>SubscriptionService.IncrementUsageAsync</c> — คู่กับ <see cref="EffectiveLimit"/>)</summary>
    public static bool ConsumesBonus(int planLimit, int usedAfter, int bonusQuota, DateTime? bonusExpiresAt, DateTime nowUtc)
        => planLimit > 0 && usedAfter > planLimit && bonusQuota > 0
           && (bonusExpiresAt == null || bonusExpiresAt.Value > nowUtc);

    /// <summary>ระดับเตือนที่<b>เพิ่งข้าม</b>จากใบนี้ (1 = ใกล้เต็ม · 2 = เต็ม) · 0 = ไม่ข้ามระดับใหม่ — ใช้แจ้งเจ้าของครั้งเดียวต่อระดับต่อเดือน
    /// (ตัวนับเพิ่มทางเดียวทั้งเดือน ⇒ ข้ามแต่ละระดับได้ครั้งเดียว) · เอกสารที่กฎหมายบังคับยังออกได้หลังเต็ม — การแจ้งคือทางที่เจ้าของรู้ก่อนบิล overage</summary>
    public static int CrossedWarnLevel(int usedBefore, int limitBefore, int usedAfter, int limitAfter)
    {
        var after = WarnLevel(usedAfter, limitAfter);
        return after > WarnLevel(usedBefore, limitBefore) ? after : 0;
    }

    /// <summary>ฐานนับโควตา (ใช้ไป, เพดานแพ็กเกจ) — ฝ่ายค้านรอบ 205 ข้อ 1 · <b>คำตัดสิน</b>: บริษัทใต้แพ็กเกจกลุ่ม (AccountSubscription)
    /// ใช้ "ยอดรวมทั้งกลุ่ม + เพดานกลุ่ม" ทั้งตอนตรวจโควตาและตอนหักโบนัส (โบนัสยังเป็นของบริษัทที่ซื้อ แต่ถูกใช้เมื่อ<b>กลุ่ม</b>เกินเพดาน —
    /// เพราะเพดานที่บังคับจริงคือเพดานกลุ่ม) · บริษัทเดี่ยวใช้ตัวนับ/เพดานของตัวเอง · เดิมตรวจด้วยยอดกลุ่มแต่หักโบนัสเทียบตัวนับบริษัทเดียว
    /// ⇒ โบนัสไม่ถูกหักเลยจนกว่าบริษัทนั้นเองจะเกินเพดานบริษัท</summary>
    public static (int Used, int PlanLimit) Basis(bool pooled, int companyUsed, int companyLimit, int poolUsed, int poolLimit)
        => pooled ? (poolUsed, poolLimit) : (companyUsed, companyLimit);

    /// <summary>เพดานที่แสดงต่อผู้ใช้ (ฝ่ายค้านรอบ 205 ข้อ 4) — หลังโบนัสหมด <see cref="EffectiveLimit"/> คืนเพดานแพ็กเกจ ⇒ ข้อความ "150/100"
    /// · เพดานที่ใช้จริงเดือนนี้ = ใบที่นับแล้ว − ใบที่คิดเป็นส่วนเกิน (ใบที่อยู่ในโควตาทุกใบ = แพ็กเกจ + โบนัสที่ใช้ไป)</summary>
    public static int DisplayLimit(int used, int limit, int overageThisMonth)
        => limit <= 0 ? limit : Math.Max(limit, used - Math.Max(0, overageThisMonth));

    /// <summary>ข้อความแจ้งเจ้าของเมื่อข้ามระดับ — บอกว่า "เอกสารที่กฎหมายบังคับยังออกได้" + ทางไปต่อเสมอ</summary>
    public static string CrossedMessage(int level, int used, int limit) => level >= 2
        ? $"เดือนนี้ใช้โควตาเอกสารครบแล้ว ({used}/{DisplayLimit(used, limit, 0)} ฉบับ) — ใบกำกับภาษี/ใบเสร็จ (รวมที่ออกอัตโนมัติจากการจอง/ชำระออนไลน์) ยังออกได้ตามปกติ "
          + "โดยคิดเป็นส่วนเกิน · ใบเสนอราคา/ใบสั่งซื้อจะถูกระงับ · ซื้อโควตาเพิ่มหรืออัปเกรดที่หน้า “ส่วนเสริมของฉัน”"
        : $"เดือนนี้ใช้โควตาเอกสารไปแล้ว {used}/{DisplayLimit(used, limit, 0)} ฉบับ (≥ {WarnPercent}%) — ซื้อโควตาเพิ่มหรืออัปเกรดที่หน้า “ส่วนเสริมของฉัน” ก่อนเต็ม";

    /// <summary>ระดับการเตือน — 0 = ปกติ, 1 = ใกล้เต็ม (≥ WarnPercent), 2 = เต็มแล้ว</summary>
    public const int WarnPercent = 80;

    public static int WarnLevel(int used, int limit)
    {
        if (limit <= 0) return 0;
        if (used >= limit) return 2;
        return used * 100 >= limit * WarnPercent ? 1 : 0;
    }

    /// <summary>คาดว่าจะเต็มวันที่เท่าไรของเดือน (run-rate) — null = ยังไม่พอประเมิน
    /// หรือใช้ช้ากว่าโควตา. ใช้บอกล่วงหน้าแทนที่จะรอให้เต็มแล้วค่อยแจ้ง</summary>
    public static int? ForecastExhaustionDay(int used, int limit, int dayOfMonth, int daysInMonth)
    {
        if (limit <= 0 || used <= 0 || dayOfMonth <= 0) return null;
        var perDay = (double)used / dayOfMonth;
        if (perDay <= 0) return null;
        var day = (int)Math.Ceiling(limit / perDay);
        if (day <= dayOfMonth) return dayOfMonth;      // เต็มแล้ว/กำลังจะเต็มวันนี้
        return day > daysInMonth ? null : day;         // ไม่เต็มภายในเดือนนี้
    }
}
