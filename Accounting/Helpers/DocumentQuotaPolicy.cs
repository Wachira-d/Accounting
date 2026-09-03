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
    /// <paramref name="fromLodging"/> = เอกสารที่โมดูลที่พักออกให้อัตโนมัติ —
    /// **ไม่นับเข้าโควตาเอกสาร** เพราะมิเตอร์ของที่พักคือ `lodging.stay`
    /// (นับตอนปิดการเข้าพัก) ถ้านับทั้งสองทางลูกค้าจะโดนคิดสองเด้งจากงานเดียว
    /// แต่ยังคง **ห้ามบล็อก** ตามกฎหมายเหมือนเดิม</summary>
    public static QuotaEnforcement Classify(DocumentType type, bool isDeposit, bool fromLodging)
    {
        var mustAllow = MustAlwaysIssue(type);
        if (fromLodging) return mustAllow ? QuotaEnforcement.CountedMustAllow : QuotaEnforcement.NotCounted;
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
    /// จะกลายเป็นจำกัดเท่าโบนัสทันทีที่มีใครได้โบนัส</summary>
    public static int EffectiveLimit(int planLimit, int bonusQuota, DateTime? bonusExpiresAt, DateTime nowUtc)
    {
        if (planLimit <= 0 || bonusQuota <= 0) return planLimit;
        if (bonusExpiresAt.HasValue && bonusExpiresAt.Value <= nowUtc) return planLimit;
        return planLimit + bonusQuota;
    }

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
