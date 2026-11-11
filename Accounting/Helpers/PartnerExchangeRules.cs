using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวตัดสินของ "คู่ค้า NextAcc ↔ NextAcc" รอบ 255 WP-12B** (AUDIT_249 §WP-12 ข้อ 6–8 · AUTOPILOT_BILLING_DESIGN §6 · คำตัดสินเจ้าของ §3.3 Y5/N3) · pure ·
/// ต่อยอด <c>TradingPartnership</c>/<c>CrossTenantDocumentLink</c> เดิม (ไม่สร้างกลไกใหม่)
/// <list type="bullet">
/// <item>เพดานจำนวนคู่ต่อแพ็กเกจ (<c>PlanTemplate.MaxTradingPartners</c> · null = ไม่จำกัด — Y5 ทุกแพ็กเกจไม่มีเพดาน)</item>
/// <item>สถานะการเชื่อมที่บอกผู้ใช้ได้โดยไม่รั่วสมาชิกภาพ (P-1)</item>
/// <item>ใบขายที่ส่งถึงคู่ค้า ⇒ ใบซื้อ<b>ร่าง</b>ในบริษัทผู้รับ (N3 · ค่าตั้งต้นปิด · เพดานยอดต่อคู่)</item>
/// <item>50 ทวิ ที่ผู้จ่ายออก ⇒ ส่งเข้ากล่องรับของผู้ขายผ่านคู่ค้า (ค่าตั้งต้นปิด · เฉพาะใบที่ออกหลังเปิด)</item>
/// </list>
/// </summary>
public static class PartnerExchangeRules
{
    public const string RuleCode = "PARTNER-EXCHANGE";
    public const string LegalReference = "ป.รัษฎากร ม.82/5(1) · ม.86/4 · ม.50 ทวิ · พ.ร.บ.คุ้มครองข้อมูลส่วนบุคคล ม.40 (ข้อแลกเปลี่ยนข้ามผู้เช่า)";

    // ═══════════ เพดานจำนวนคู่ค้า (Y5) ═══════════

    /// <summary>บริษัทที่มีคู่ที่เชื่อมแล้ว <paramref name="acceptedCount"/> ราย รับคู่เพิ่มได้ไหม · null = ได้ · <paramref name="maxPartners"/> null/ติดลบ = ไม่จำกัด</summary>
    public static string? CapProblem(int acceptedCount, int? maxPartners, string companyLabel)
        => maxPartners is int max && max >= 0 && acceptedCount >= max
            ? $"แพ็กเกจของ{companyLabel}เชื่อมคู่ค้า NextAcc ได้สูงสุด {max} ราย (เชื่อมแล้ว {acceptedCount}) — ระงับคู่ที่ไม่ใช้แล้ว หรือติดต่อผู้ดูแลเพื่อขยายเพดาน"
            : null;

    // ═══════════ สถานะการเชื่อม (ไม่รั่วสมาชิกภาพ · P-1) ═══════════

    public enum LinkState { NotLinked, Linked, PendingOurs, Suspended }

    /// <summary>สถานะที่บอกผู้ถามได้ — คำเชิญที่<b>เราเชิญเอง</b>และปลายทางยังไม่ตอบ/ปฏิเสธ = <see cref="LinkState.NotLinked"/> (เหมือนไม่มีแถว — กันไล่เลขภาษี)</summary>
    public static LinkState StateOf(TradingPartnershipStatus? status, bool invitedByUs) => status switch
    {
        TradingPartnershipStatus.Accepted => LinkState.Linked,
        TradingPartnershipStatus.Suspended => LinkState.Suspended,
        TradingPartnershipStatus.Pending when !invitedByUs => LinkState.PendingOurs,
        _ => LinkState.NotLinked,
    };

    public static string StateLabel(LinkState s) => s switch
    {
        LinkState.Linked => "เชื่อมระบบแล้ว",
        LinkState.Suspended => "ระงับการเชื่อมชั่วคราว",
        LinkState.PendingOurs => "คู่ค้าส่งคำขอเชื่อมมา — รอเรายอมรับ",
        _ => "ยังไม่เชื่อม",
    };

    /// <summary>บอก "ใช้ NextAcc (เปิดให้คู่ค้าค้นเจอ)" ได้เฉพาะเมื่อยังไม่เชื่อมและปลายทาง<b>เปิดเอง</b> (<c>Company.PartnerDiscoverable</c> · ค่าตั้งต้นปิด)</summary>
    public static bool ShowDiscoverable(LinkState state, bool targetDiscoverable) => state == LinkState.NotLinked && targetDiscoverable;

    // ═══════════ ใบซื้อร่างจากใบขายของคู่ค้า (N3) ═══════════

    /// <summary>เพดานยอดของ "ฝั่งที่รับ" ในแถวคู่ (แถวเดียวต่อคู่ เรียง A &lt; B) — ผู้รับ = A ⇒ <paramref name="limitForA"/></summary>
    public static decimal? ReceiverLimit(Guid receiverId, Guid companyAId, decimal? limitForA, decimal? limitForB)
        => receiverId == companyAId ? limitForA : limitForB;

    /// <summary>ชนิดใบขายที่กลายเป็นใบซื้อร่างฝั่งผู้รับ (ใบแจ้งหนี้/ใบกำกับ) — ใบเสร็จ/ใบเสนอราคา/ใบสั่งซื้อไม่ใช่</summary>
    private static bool IsBillSource(DocumentType t) => t is DocumentType.Invoice or DocumentType.TaxInvoice;

    /// <summary>สร้างใบซื้อร่างไหม · null = สร้าง · ข้อความ = ไม่สร้าง (บันทึกบนแถวกล่องเข้าให้ผู้รับเห็นเหตุ — ไม่เงียบ)</summary>
    public static string? DraftProblem(bool receiverEnabled, DocumentType sourceType, DocumentStatus sourceStatus, decimal total, decimal? receiverLimit,
        string? currency)
    {
        if (!receiverEnabled) return "ไม่ได้สร้างใบซื้อร่าง: บริษัทผู้รับยังไม่เปิด \"สร้างใบซื้อร่างจากเอกสารของคู่ค้า\"";
        if (!IsBillSource(sourceType)) return "ไม่ได้สร้างใบซื้อร่าง: เอกสารชนิดนี้ไม่ใช่ใบแจ้งหนี้/ใบกำกับภาษี";
        if (sourceStatus is DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Voided or DocumentStatus.Rejected)
            return "ไม่ได้สร้างใบซื้อร่าง: เอกสารต้นทางยังไม่ออก/ถูกยกเลิก";
        if (!string.IsNullOrWhiteSpace(currency) && !string.Equals(currency, "THB", StringComparison.OrdinalIgnoreCase))
            return "ไม่ได้สร้างใบซื้อร่าง: เอกสารสกุลเงินต่างประเทศ — บันทึกเองพร้อมอัตราแลกเปลี่ยน";
        if (total <= 0m) return "ไม่ได้สร้างใบซื้อร่าง: ยอดเอกสารเป็นศูนย์";
        if (receiverLimit is decimal cap && cap >= 0m && total > cap)
            return $"ไม่ได้สร้างใบซื้อร่าง: ยอด {total:#,##0.00} เกินเพดานที่ตั้งไว้สำหรับคู่ค้ารายนี้ ({cap:#,##0.00}) — บันทึกเอง หรือปรับเพดานที่หน้าคู่ค้า";
        return null;
    }

    // ═══════════ ข้อแลกเปลี่ยน 50 ทวิ ผ่านคู่ค้า ═══════════

    /// <summary>ใบ 50 ทวิ ของผู้จ่ายใบนี้ส่งเข้ากล่องรับของผู้ขายได้ไหม — ออกแล้ว (ไม่ใช่ร่าง/ยกเลิก) · ออกหลังเปิดค่าตั้ง (watermark — ไม่ส่งย้อนหลัง) ·
    /// ยังไม่เคยส่ง · ลองไม่เกินเพดาน</summary>
    public static bool WhtCertDeliverable(int certStatus, DateTime? issuedAt, DateTime? enabledSince, DateTime? deliveredAt, int attempts)
        => (certStatus == 1 || certStatus == 3) && issuedAt is DateTime i && enabledSince is DateTime since && i >= since
           && deliveredAt == null && attempts < MaxWhtDeliveryAttempts;

    /// <summary>เพดานการลองส่งใบ 50 ทวิ ต่อใบ (ล้มเกินนี้ = ค้างพร้อมเหตุบนใบ ให้คนส่งเอง)</summary>
    public const int MaxWhtDeliveryAttempts = 8;

    /// <summary>รอบ 257 ชุด 2 (B-L4): กล่องรับของผู้ขายตอบว่า "ตีกลับ" (เช่น ผู้ถูกหักบนใบไม่ใช่ผู้ขาย) ⇒ <b>ไม่ใช่ "ส่งแล้ว"</b> — เหตุของการตีกลับบนใบ ·
    /// ไม่ลองส่งซ้ำ (ผลเชิงกำหนด — ส่งซ้ำได้ใบตีกลับซ้ำ) จนกว่าผู้จ่ายแก้แล้วออกใบใหม่ · อื่น ๆ (รับแล้ว/รอตรวจ/ซ้ำ) = ส่งแล้ว</summary>
    public static bool IntakeRejected(string? intakeStatus) => intakeStatus == nameof(WhtCertIntakeRecordStatus.Rejected);

    /// <summary>B-L4: เหตุบนใบของผู้จ่ายเมื่อกล่องรับของผู้ขายตีกลับ (ข้อความจากกล่องรับ · ไม่มี ⇒ ข้อความกลาง)</summary>
    public static string IntakeRejectedReason(string? intakeMessage)
        => "กล่องรับ 50 ทวิ ของผู้ขายตีกลับใบนี้: " + (string.IsNullOrWhiteSpace(intakeMessage) ? "ข้อมูลบนใบไม่ตรงกับผู้ขาย" : intakeMessage.Trim())
           + " — แก้ข้อมูลผู้ถูกหักแล้วออกใบใหม่ หรือส่งหนังสือรับรองให้ผู้ขายทางเดิม";

    /// <summary>B-L4: ป้ายสถานะการส่งผ่านคู่ค้าบนรายการ 50 ทวิ ของผู้จ่าย (หน้าแสดงอย่างเดียว) · null = ไม่ใช่งานของเส้นนี้ (ยังไม่ส่ง/ไม่ใช่คู่ค้า)</summary>
    public static string? WhtDeliveryLabel(DateTime? deliveredAt, string? deliveryError)
        => deliveredAt != null ? "ส่งเข้ากล่องรับของคู่ค้าแล้ว"
            : !string.IsNullOrWhiteSpace(deliveryError) ? "ส่งผ่านคู่ค้าไม่สำเร็จ: " + deliveryError.Trim()
            : null;

    /// <summary>อัตราบนใบ (ถ้าทุกบรรทัดอัตราเดียว ใช้อัตรานั้น · ไม่งั้นคิดจากยอดรวม ปัด 2 ตำแหน่ง)</summary>
    public static decimal CertRate(IReadOnlyCollection<decimal> lineRates, decimal income, decimal tax)
    {
        var distinct = lineRates.Distinct().ToList();
        if (distinct.Count == 1) return distinct[0];
        return income > 0m ? Math.Round(tax / income * 100m, 2, MidpointRounding.AwayFromZero) : 0m;
    }

    /// <summary>รอบ 255 ฝ่ายค้าน LOW: วันที่ของ 50 ทวิ ที่ส่งผ่านคู่ค้า = วันจ่ายเงินล่าสุดของบรรทัด (default(DateTime) = ไม่ได้ตั้ง ⇒ ข้าม) · ไม่มี ⇒ วันที่ออกใบ</summary>
    public static DateTime? WhtCertificateDate(IEnumerable<DateTime> linePaymentDates, DateTime? issuedDate)
    {
        var set = linePaymentDates.Where(d => d != default).ToList();
        return set.Count > 0 ? set.Max() : issuedDate;
    }

    // ═══════════ รอบ 257 ชุด 3 (D-H5): ระงับการเชื่อม ═══════════

    /// <summary>ระงับการเชื่อมได้ไหม — เฉพาะคู่ที่เชื่อมอยู่ (Accepted) · null = ได้ · ทางกลับ = เชิญใหม่ (เส้นเชิญเปิดแถว Suspended เป็นคำเชิญสด)</summary>
    public static string? SuspendProblem(TradingPartnershipStatus status) => status switch
    {
        TradingPartnershipStatus.Accepted => null,
        TradingPartnershipStatus.Suspended => "การเชื่อมนี้ถูกระงับอยู่แล้ว — ถ้าต้องการเชื่อมใหม่ ให้ส่งคำเชิญถึงคู่ค้าอีกครั้ง",
        TradingPartnershipStatus.Pending => "คำขอนี้ยังไม่ได้เชื่อม — ใช้ปุ่ม \"ปฏิเสธ\" แทน",
        _ => "การเชื่อมนี้ไม่ได้ใช้งานอยู่ — ไม่มีอะไรให้ระงับ",
    };

    /// <summary>ฝ่ายค้าน LOW (รอบ 257 ชุด 3): เชิญซ้ำได้ไหม — ถูกปฏิเสธ/ถูกระงับ ต้องพ้นช่วงพัก <paramref name="cooldownDays"/> วันก่อน (กันสแปม §6.3 · เดิมระงับแล้วเชิญซ้ำได้ทันที) ·
    /// ไม่รู้เวลา (แถวเก่าก่อนมีคอลัมน์) = ได้ · สถานะอื่น = ไม่ใช่เรื่องของตัวนี้ (false)</summary>
    public static bool ReinviteAllowed(TradingPartnershipStatus status, DateTime? rejectedAt, DateTime? suspendedAt, DateTime nowUtc, int cooldownDays) => status switch
    {
        TradingPartnershipStatus.Rejected => rejectedAt == null || rejectedAt <= nowUtc.AddDays(-cooldownDays),
        TradingPartnershipStatus.Suspended => suspendedAt == null || suspendedAt <= nowUtc.AddDays(-cooldownDays),
        _ => false,
    };

    /// <summary>ต้นทางหนึ่งใบในสายของใบซื้อ (ฝ่ายค้าน LOW: ใบแทนหลังต้นทางถูกยกเลิก)</summary>
    public readonly record struct SourceLiveness(Guid Id, bool Voided, Guid? Replaces);

    /// <summary>มีต้นทางที่ถูกยกเลิก<b>และยังไม่มีใบแทนที่มีผล</b>ในชุดไหม — ใบแทน = ต้นทางที่มีผลซึ่งสาย <c>ReplacesDocumentId</c> (≤ 6 ชั้น ภายในชุด) ถึงใบที่ถูกยกเลิก ·
    /// true ⇒ ใบซื้อติด SourceVoided · false ⇒ ไม่มีต้นทางยกเลิก หรือทุกใบที่ยกเลิกมีใบแทนแล้ว (ตัวตัดสินเดียวของเส้นแนบหลักฐาน + ตรวจต้นทางสด)</summary>
    public static bool AnyUnreplacedVoided(IReadOnlyCollection<SourceLiveness> sources)
    {
        var byId = sources.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        var replaced = new HashSet<Guid>();
        foreach (var live in sources.Where(x => !x.Voided))
        {
            var cur = live.Replaces;
            for (var depth = 0; depth < 6 && cur is Guid id; depth++)
            {
                replaced.Add(id);
                cur = byId.TryGetValue(id, out var next) ? next.Replaces : null;
            }
        }
        return sources.Any(x => x.Voided && !replaced.Contains(x.Id));
    }

    /// <summary>ข้อความหลังระงับ (ผู้ใช้เห็น) — บอกผลและทางกลับ</summary>
    public const string SuspendedMessage =
        "ระงับการเชื่อมแล้ว — หยุดส่งเอกสาร/สร้างใบซื้อร่าง/ส่ง 50 ทวิ เข้าระบบของกันและกัน · เอกสารที่ส่งไปแล้วยังอยู่ · เชื่อมใหม่ได้ด้วยการส่งคำเชิญอีกครั้ง";

    // ═══════════ รอบ 257 ชุด 3 (D-H4 · LOW): ใบที่ทำหน้าที่ใบกำกับภาษี ═══════════

    /// <summary>เอกสารของผู้ขายใบนี้ "ทำหน้าที่ใบกำกับภาษี" ไหม (ตัวตัดสินเดียวของเส้นคู่ค้า) — ใบกำกับภาษี (เว้นที่ตรึงไว้ว่าไม่ใช่ เช่น VAT 0 ทั้งใบ) ·
    /// ใบเสร็จที่ตรึงตอนออกว่าเป็นใบกำกับ (<c>IsTaxInvoiceByLaw == true</c>) และมี VAT (ใบเสร็จรับเงิน/ใบกำกับภาษีของการขายที่ออกใบแจ้งหนี้ก่อน — §78/1) ·
    /// ใบเสร็จที่ยังไม่ตรึง (null) = ไม่รู้ ⇒ <b>ไม่นับ</b> (ไม่เดาว่าเป็นใบกำกับ — F2 ข้อ 3) · ใบแจ้งหนี้/อื่น ๆ = ไม่ใช่</summary>
    /// <param name="receiptForm">รอบ 258-3 (ฝ่ายค้าน L6 · §82/5(2)): ใบเสร็จรับเงิน/ใบกำกับภาษี<b>อย่างย่อ</b> ⇒ ไม่ใช่หลักฐานภาษีซื้อ (false) · null = ไม่ทราบรูปแบบ (ตามเกณฑ์เดิม)</param>
    public static bool IsTaxInvoiceCarrier(DocumentType type, bool? isTaxInvoiceByLaw, decimal vatAmount, ReceiptForm? receiptForm = null)
        => receiptForm != ReceiptForm.ReceiptTaxInvoiceAbbreviated && type switch
    {
        DocumentType.TaxInvoice => isTaxInvoiceByLaw != false,
        DocumentType.Receipt => isTaxInvoiceByLaw == true && vatAmount > 0.005m,
        _ => false,
    };

    /// <summary>คำตัดสิน 257-3-5: ใบเสร็จที่เป็นใบกำกับ<b>ไม่สร้างใบซื้อร่างใบใหม่</b> — แนบหลักฐานเข้าใบซื้อเดิมของการขายครั้งเดียวกันเท่านั้น ·
    /// ไม่มีใบซื้อเดิม (ขายเงินสด) ⇒ เหตุบนแถวกล่องเข้า (ไม่เงียบ)</summary>
    public const string ReceiptNoChainMessage =
        "ไม่ได้สร้างใบซื้อร่าง: ใบเสร็จรับเงิน/ใบกำกับภาษีที่ไม่ได้มาจากใบแจ้งหนี้ที่ส่งถึงท่านผ่าน NextAcc — บันทึกเองจากกล่องเอกสารเข้า";

    // ═══════════ รอบ 257 ชุด 3 (D-M2): คัดลอกใบขายเป็นใบซื้อร่าง ═══════════

    /// <summary>บรรทัดหนึ่งของใบต้นทางที่ตัวตรวจการคัดลอกใช้ (รอบ 257 ชุด 3 ฝ่ายค้าน M4: เพิ่มจำนวน/ราคา/ส่วนลด/VAT เพื่อตรวจราคารวม VAT)</summary>
    public readonly record struct CopyLine(decimal Qty, decimal UnitPrice, decimal Discount, decimal Amount, decimal Vat, decimal Wht);

    /// <summary>คัดลอกใบของคู่ค้าเป็นเอกสารปลายทาง "ครบเท่าต้นฉบับ" ได้ไหม (ใบซื้อร่าง · ใบสั่งซื้อ/ใบแจ้งหนี้ปลายทาง — ตัวตรวจเดียว) · null = ได้
    /// (คัดลอกส่วนลดหัวใบ/บรรทัด · หน่วย · ภาษีหัก ณ ที่จ่าย · ผลต่างปัดเศษ · รูปแบบราคารวม VAT) ·
    /// ข้อความ = ไม่สร้าง (เหตุบนแถวกล่องเข้า) — ช่องที่ต้องผูกกับข้อมูลของผู้รับเอง (ใบมัดจำ · ผังส่วนลดหลัง VAT · ผู้จ่ายออกภาษีแทน) ไม่เดา ·
    /// ยอดบรรทัดไม่รวมเท่าหัวใบ (สัญญา <c>SubTotal = Σ บรรทัด + ผลต่างปัดเศษ</c> · <c>Total = ฐาน + VAT − ภาษีหัก</c>) = ไม่คัดลอกใบที่เพี้ยน ·
    /// ฝ่ายค้าน M4: ใบราคารวม VAT (ไม่มีส่วนลดท้ายบิล) ⇒ จำนวน × ราคา − ส่วนลด ต้องเท่า ฐาน + VAT ของบรรทัด (เปิดแก้แล้วบันทึกที่ฝั่งผู้รับจะได้ยอดเดิม)</summary>
    public static string? CopyProblem(decimal subTotal, decimal vat, decimal wht, decimal total, decimal rounding,
        decimal depositBaseDeducted, decimal whtPayerBorne, decimal postVatDeduction, IReadOnlyCollection<CopyLine> lines,
        bool pricesIncludeVat = false, decimal billDiscountAmount = 0m)
    {
        const string P = "ไม่ได้สร้างเอกสารจากคู่ค้า: ";
        if (depositBaseDeducted != 0m) return P + "ใบของคู่ค้าหักมัดจำที่ออกใบกำกับแล้ว — บันทึกเองพร้อมอ้างใบมัดจำของท่าน";
        if (whtPayerBorne != 0m) return P + "คู่ค้าระบุให้ผู้จ่ายออกภาษีหัก ณ ที่จ่ายแทน — บันทึกเองพร้อมเลือกประเภท 50 ทวิ";
        if (postVatDeduction != 0m) return P + "ใบของคู่ค้ามีส่วนลดหลังคิด VAT — บันทึกเองพร้อมผังส่วนลด";
        if (lines.Count == 0) return P + "ใบของคู่ค้าไม่มีรายการสินค้า/บริการ";
        var sumLines = lines.Sum(l => l.Amount);
        if (Math.Abs(sumLines + rounding - subTotal) > 0.005m)
            return P + $"ยอดรายการรวม {sumLines:#,##0.00} ไม่เท่ายอดก่อนภาษีบนหัวใบ {subTotal:#,##0.00} — บันทึกเองจากต้นฉบับ";
        var sumWht = lines.Sum(l => l.Wht);
        if (Math.Abs(sumWht - wht) > 0.005m)
            return P + $"ภาษีหัก ณ ที่จ่ายรายบรรทัดรวม {sumWht:#,##0.00} ไม่เท่าบนหัวใบ {wht:#,##0.00} — บันทึกเองจากต้นฉบับ";
        if (Math.Abs(WhtGrossUp.PayeeTotal(subTotal, vat, wht, 0m) - total) > 0.005m)
            return P + $"ยอดรวม {total:#,##0.00} ไม่เท่า ฐาน + VAT − ภาษีหัก ณ ที่จ่าย — บันทึกเองจากต้นฉบับ";
        if (pricesIncludeVat && billDiscountAmount == 0m
            && lines.Any(l => Math.Abs(Math.Round(l.Qty * l.UnitPrice, 2, MidpointRounding.AwayFromZero) - l.Discount - (l.Amount + l.Vat)) > 0.02m))
            return P + "ใบราคารวม VAT ที่ราคาต่อหน่วยไม่ตรงยอดบรรทัด (จำนวน × ราคา − ส่วนลด ≠ ฐาน + VAT) — บันทึกเองจากต้นฉบับ";
        return null;
    }

    /// <summary>รอบ 255 ฝ่ายค้าน M4: ยอมรับคำขอเชื่อมได้เฉพาะสถานะรอยอมรับ · null = ได้ · ถูกปฏิเสธ/ระงับ ⇒ ต้องเชิญใหม่ (ทางไปต่อ)</summary>
    public static string? AcceptProblem(TradingPartnershipStatus status) => status switch
    {
        TradingPartnershipStatus.Pending => null,
        TradingPartnershipStatus.Rejected => "คำขอนี้ถูกปฏิเสธแล้ว — ให้คู่ค้าส่งคำเชิญใหม่ (หรือเชิญเขาจากหน้าคู่ค้า)",
        TradingPartnershipStatus.Suspended => "การเชื่อมนี้ถูกระงับ — ให้คู่ค้าส่งคำเชิญใหม่ (หรือเชิญเขาจากหน้าคู่ค้า)",
        _ => "สถานะนี้ยอมรับไม่ได้ — ให้คู่ค้าส่งคำเชิญใหม่",
    };
}

/// <summary>
/// **หลักฐานภาษีซื้อของใบซื้อที่มาจากคู่ค้า NextAcc** (คำตัดสินเจ้าของ §3.3 N3 · ป.รัษฎากร ม.82/5(1) + ม.86/4 · ขมธอ.3-2560) · pure
/// <para>ใบที่ส่งผ่านแพลตฟอร์มเข้าเป็นใบซื้อ <b>ร่าง</b> เสมอ · ระบบตั้งได้แค่ค่าที่ยังติดด่าน (ต้นทางไม่ใช่ใบกำกับ ⇒ <see cref="AwaitingTaxInvoice"/> · รอบ 257): e-Tax ที่ลงลายมือชื่อ<b>และส่งไฟล์ถึงผู้รับแล้ว</b> ⇒ <see cref="AwaitingTimestamp"/> ·
/// อื่น ๆ (PDF ที่แพลตฟอร์มเรนเดอร์ไม่ใช่ต้นฉบับ) ⇒ <see cref="PaperRequired"/> ·
/// <b>ฝ่ายค้าน H2 (รอบ 255):</b> ระบบ<b>ไม่เคยตั้ง</b> <see cref="EtaxVerified"/>/<see cref="PaperConfirmed"/> เอง — เวลาประทับบนแถว e-Tax ของผู้ขายเป็นค่าที่ผู้ขายบันทึก ไม่ใช่การตรวจกับ ETDA ·
/// สองค่านั้นมาจาก "ได้รับต้นฉบับแล้ว" ของผู้รับเท่านั้น (<see cref="Confirmed"/>) · ผู้ขายยกเลิกต้นทาง ⇒ <see cref="SourceVoided"/> (ห้ามลงภาษีซื้อ · ยืนยันไม่ได้)</para>
/// <para><b>ไม่ลงภาษีซื้อเอง</b>: <see cref="BlocksClaim"/> = true ⇒ ตัวตรวจ §86/4 ตัวเดียว (<c>TaxInvoiceCompletenessChecker.Evaluate</c>) นับว่าใบยังไม่ครบ ⇒
/// อนุมัติแล้ว VAT พัก 11640 จนผู้ใช้ยืนยัน (ทางเดียวกับใบกำกับที่ไม่ครบอื่น ๆ)</para>
/// </summary>
public static class PartnerInputVatEvidence
{
    public const string EtaxVerified = "EtaxVerified";
    public const string AwaitingTimestamp = "AwaitingTimestamp";
    public const string PaperRequired = "PaperRequired";
    public const string PaperConfirmed = "PaperConfirmed";
    public const string SourceVoided = "SourceVoided";
    /// <summary>รอบ 257 ชุด 3 (D-H4): ต้นทางยังไม่ใช่ใบกำกับภาษี (ใบแจ้งหนี้) — ไม่มีต้นฉบับใบกำกับให้ยืนยัน · ห้ามลงภาษีซื้อ ·
    /// ปลดได้ทางเดียว = ผู้ขายออกใบกำกับ/ใบเสร็จที่เป็นใบกำกับของการขายเดิม ⇒ ระบบตั้งหลักฐานใหม่ด้วย <see cref="Of"/> (แล้วผู้รับยืนยันตามปกติ)</summary>
    public const string AwaitingTaxInvoice = "AwaitingTaxInvoice";

    public const string AwaitingTaxInvoiceMessage =
        "ต้นทางยังไม่ใช่ใบกำกับภาษี (หรือ VAT ไม่ตรงใบกำกับ) — ยืนยันต้นฉบับใบกำกับแบบปกติไม่ได้ · เมื่อผู้ขายออกใบกำกับภาษี/ใบเสร็จที่เป็นใบกำกับผ่าน NextAcc "
        + "ระบบจะแนบเข้าใบนี้ให้เอง · ถ้าได้ใบกำกับจากผู้ขายนอกระบบ กด \"ได้รับใบกำกับภาษีจากผู้ขายนอกระบบ\" พร้อมเลขที่/วันที่ใบกำกับ (ม.82/5(1) · ม.86/4)";

    /// <summary>ฝ่ายค้าน M3: VAT ของใบกำกับใหม่กับ VAT บนใบซื้อของผู้รับ ต่างกันไม่เกิน 0.005</summary>
    public static bool VatMatches(decimal sourceVat, decimal purchaseVat) => Math.Abs(sourceVat - purchaseVat) <= 0.005m;

    public const string VatMismatchMessage =
        "VAT บนใบกำกับของผู้ขายไม่เท่า VAT บนใบซื้อนี้ — ยังลงภาษีซื้อไม่ได้ · แก้ยอด/VAT ให้ตรงใบกำกับ แล้วกด \"ได้รับใบกำกับภาษีจากผู้ขายนอกระบบ\" พร้อมเลขที่/วันที่";

    // ═══════════ ฝ่ายค้าน M1: ทางออกของคน — "ได้รับใบกำกับภาษีจากผู้ขายนอกระบบ" ═══════════

    /// <summary>ชื่อเหตุการณ์ใน audit ของทาง "ได้ใบกำกับนอกระบบ" — ตัวเดียวของผู้เขียน (<c>ConfirmExternalTaxInvoiceAsync</c>) และผู้อ่าน
    /// (ตอนใบกำกับผ่านระบบมาถึงทีหลัง · งานซ่อม <see cref="PartnerEvidenceRepair"/>)</summary>
    public const string ExternalConfirmedEvent = "PartnerExternalTaxInvoiceConfirmed";

    /// <summary>รอบ 257 ปิดท้าย MED-3: สายนี้มีใบกำกับของผู้ขายผ่านระบบอยู่แล้ว (ใบล่าสุดที่ยังมีผล · VAT = <paramref name="inSystemTaxInvoiceVat"/>) ⇒ ทาง
    /// "ได้ใบกำกับนอกระบบ" ใช้ได้เฉพาะเมื่อ VAT บนใบซื้อตรงกับใบกำกับนั้น (<see cref="VatMatches"/> — กติกาเดียวกับตอนแนบ) · ไม่ตรง ⇒ ข้อความพร้อมทางไปต่อ ·
    /// ไม่มีใบกำกับผ่านระบบ (null) ⇒ ไม่ตรวจ (ทิศตรงข้าม: ผู้ขายออกใบกำกับนอกระบบจริง) · null = ได้ · pure</summary>
    public static string? ExternalVatProblem(decimal? inSystemTaxInvoiceVat, decimal purchaseVat, bool approved)
    {
        if (inSystemTaxInvoiceVat is not decimal v || VatMatches(v, purchaseVat)) return null;
        return $"ผู้ขายส่งใบกำกับภาษีผ่านระบบแล้ว (VAT {v:#,##0.00}) แต่ VAT บนใบซื้อนี้คือ {purchaseVat:#,##0.00} — บันทึก \"ได้รับใบกำกับ\" บนยอดที่ไม่ตรงใบกำกับไม่ได้ (ม.82/5(1) · ม.86/4) · "
            + (approved
                ? "ยกเลิกใบซื้อนี้แล้วบันทึกใหม่ด้วย VAT ตามใบกำกับ"
                : "แก้ยอด/VAT ของใบร่างให้ตรงใบกำกับแล้วกดอีกครั้ง (หรือยกเลิกแล้วบันทึกใหม่)")
            + " · ถ้าใบกำกับของผู้ขายผิด ให้ผู้ขายยกเลิกแล้วออกใบแทนที่ถูกต้อง";
    }

    /// <summary>เลขที่ใบกำกับเดียวกันไหม — ตัดช่องว่างหัวท้าย · ไม่สนตัวพิมพ์ · <b>ไม่ fuzzy</b> (เลขเอกสารไม่มีการสะกดผิด — F2 ข้อ 3) · ว่าง = ไม่ตรง</summary>
    internal static bool SameTaxInvoiceNumber(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>รอบ 257 ปิดท้าย MED-4: คำยืนยันผ่านทาง "ได้ใบกำกับนอกระบบ" ยังใช้ได้ไหมเมื่อใบกำกับผ่านระบบมาถึงทีหลัง — ใบยังอยู่ในสถานะที่คนยืนยัน +
    /// มี audit ของทางนั้น + เลขที่ใบกำกับที่คนบันทึกตรงกับเลขของใบที่มาถึง (VAT ตรวจแยกที่ <see cref="OnNewSource"/>) · pure</summary>
    public static bool ExternalConfirmationCovers(string? current, bool confirmedThroughExternalPath, string? recordedTaxInvoiceNumber, string? arrivingTaxInvoiceNumber)
        => (current == EtaxVerified || current == PaperConfirmed) && confirmedThroughExternalPath
           && SameTaxInvoiceNumber(recordedTaxInvoiceNumber, arrivingTaxInvoiceNumber);

    /// <summary>ใช้ทาง "ได้ใบกำกับนอกระบบ" ได้ไหม — เฉพาะใบที่รอใบกำกับ (<see cref="AwaitingTaxInvoice"/>) · ต้องมีเลขที่ (≤ 100 ตัว) + วันที่
    /// (ไม่ก่อน ค.ศ. 2000 · ไม่เกินพรุ่งนี้) · null = ได้</summary>
    public static string? ExternalTaxInvoiceProblem(string? evidence, string? number, DateTime? date, DateTime todayUtc)
    {
        if (evidence == SourceVoided) return SourceVoidedMessage;
        if (evidence != AwaitingTaxInvoice)
            return ConfirmProblem(evidence) == null
                ? "ใบนี้มีใบกำกับของผู้ขายผ่านระบบแล้ว — ใช้ปุ่ม \"ได้รับต้นฉบับแล้ว\" แทน"
                : ConfirmProblem(evidence);
        if (string.IsNullOrWhiteSpace(number)) return "กรอกเลขที่ใบกำกับภาษีของผู้ขาย";
        if (number.Trim().Length > 100) return "เลขที่ใบกำกับยาวเกิน 100 ตัวอักษร";
        if (date is not DateTime d) return "กรอกวันที่บนใบกำกับภาษีของผู้ขาย";
        if (d.Date < new DateTime(2000, 1, 1) || d.Date > todayUtc.Date.AddDays(1)) return "วันที่ใบกำกับไม่ถูกต้อง (ต้องไม่อยู่ในอนาคต)";
        return null;
    }

    /// <summary>ผู้รับใช้ทาง "ได้ใบกำกับนอกระบบ" ได้ไหม (ปุ่มบนหน้าคู่ค้า — server computes)</summary>
    public static bool CanConfirmExternalTaxInvoice(string? evidence) => evidence == AwaitingTaxInvoice;

    /// <summary>ฝ่ายค้าน LOW: ยืนยันแบบ "ได้ e-Tax ที่ประทับเวลา" ผ่านทางปกติได้เฉพาะเมื่อไฟล์ e-Tax ที่ลงลายมือชื่อของต้นทางถึงผู้รับแล้ว · null = ได้</summary>
    public static string? StampedEtaxProblem(string? evidence)
        => CanConfirmStampedEtax(evidence) ? null
            : "ต้นทางไม่ใช่ e-Tax ที่ลงลายมือชื่อและส่งไฟล์ถึงท่าน — ยืนยันแบบ \"ได้ e-Tax ที่ประทับเวลา\" ไม่ได้ (เลือก \"ได้รับต้นฉบับกระดาษแล้ว\")";

    /// <summary>ยืนยันแล้ว — ทางไปต่อเมื่อภาษีซื้อยังพัก (รอบ 257 ชุด 3: เดิมตอบ "ไม่ได้รอต้นฉบับ" เฉย ๆ = ทางตัน)</summary>
    public const string AlreadyConfirmedMessage =
        "ยืนยันต้นฉบับใบนี้แล้ว — ถ้าภาษีซื้อยังพักอยู่ ให้เปิดเอกสารแล้วเติมเลขที่/วันที่ใบกำกับของผู้ขาย (เส้นเติมใบกำกับภาษีซื้อ)";

    public const string SourceVoidedMessage =
        "ผู้ขายยกเลิกเอกสารต้นทาง (หรือยกเลิก e-Tax ของใบนั้น) แล้ว — ลงภาษีซื้อจากใบนี้ไม่ได้ (ม.82/5(1)) · ขอใบใหม่จากผู้ขาย แล้วยกเลิก/กลับรายการใบซื้อนี้";

    public const string EditedNote = "แก้ยอด/VAT/บรรทัดของใบที่มาจากคู่ค้า — หลักฐานภาษีซื้อกลับเป็น \"ต้องได้ต้นฉบับ\" (ใบไม่ตรงต้นฉบับของผู้ขายแล้ว)";

    /// <param name="sourceIsTaxInvoice">ต้นทางเป็นใบกำกับภาษี (ใบแจ้งหนี้ที่ไม่ใช่ใบกำกับ ⇒ ไม่มีต้นฉบับใบกำกับให้ลงภาษีซื้อ)</param>
    /// <param name="signed">e-Tax ของต้นทางลงลายมือชื่อแล้ว (มีลายเซ็น + เลขใบรับรอง + เวลาลงนาม)</param>
    /// <param name="signedFileDelivered">ไฟล์ XML ที่ลงลายมือชื่อถูกส่งถึงผู้รับแล้ว — ไม่ส่ง = ผู้รับไม่มีต้นฉบับ</param>
    /// <returns>AwaitingTimestamp · PaperRequired · AwaitingTaxInvoice (รอบ 257) เท่านั้น — ไม่มีทางได้ค่าที่ปลดด่าน (H2)</returns>
    /// <remarks>รอบ 257 ชุด 3 (D-H4): ต้นทางที่ไม่ใช่ใบกำกับ ⇒ <see cref="AwaitingTaxInvoice"/> (เดิม PaperRequired ⇒ ผู้รับกด "ได้ต้นฉบับ" ของใบแจ้งหนี้ได้ แล้วคำยืนยันนั้น
    /// ติดไปกับใบกำกับที่แปลงมาทีหลังโดยไม่มีใครยืนยันต้นฉบับใบกำกับ)</remarks>
    public static string Of(bool sourceIsTaxInvoice, bool signed, bool signedFileDelivered)
        => !sourceIsTaxInvoice ? AwaitingTaxInvoice : signed && signedFileDelivered ? AwaitingTimestamp : PaperRequired;

    /// <summary>ลำดับของค่าที่ยังติดด่าน (สูงกว่า = ใกล้ลงภาษีซื้อได้กว่า) — ใช้เลือกค่าที่ดีกว่าตอนหลักฐานใหม่ของการขายเดิมมาถึง</summary>
    private static int PendingRank(string? v) => v switch
    {
        AwaitingTaxInvoice => 1,
        PaperRequired => 2,
        AwaitingTimestamp => 3,
        _ => 0,
    };

    /// <summary>ค่าที่ผู้รับยืนยัน: ได้ไฟล์ e-Tax ที่ประทับเวลาแล้ว ⇒ EtaxVerified · ได้ต้นฉบับกระดาษ ⇒ PaperConfirmed</summary>
    public static string Confirmed(bool stampedEtax) => stampedEtax ? EtaxVerified : PaperConfirmed;

    /// <summary>ฝ่ายค้าน H3: หลักฐานใหม่ของการขายเดิมมาถึงใบซื้อที่มีอยู่ — อัปเกรดเฉพาะใบที่ยังติดด่าน (PaperRequired ⇒ AwaitingTimestamp) ·
    /// ค่าที่ผู้รับยืนยันแล้ว/ต้นทางถูกยกเลิก คงเดิม</summary>
    public static string Merge(string? current, string offered)
    {
        if (current is EtaxVerified or PaperConfirmed or SourceVoided) return current!;
        return current != null && PendingRank(current) >= PendingRank(offered) ? current : offered;
    }

    /// <summary>รอบ 257 ชุด 3 (D-H4): หลักฐานของใบใหม่ในสายการขายเดิมมาถึงใบซื้อที่มีอยู่ — ใบใหม่ทำหน้าที่ใบกำกับ ขณะที่<b>ทุกใบที่ผูกไว้ก่อนหน้าไม่ใช่</b>
    /// ⇒ คำยืนยัน/หลักฐานเดิมเป็นของใบแจ้งหนี้ ไม่ใช่ต้นฉบับใบกำกับ ⇒ ตั้งใหม่เป็น <paramref name="offered"/> (ผลของ <see cref="Of"/>) ให้ผู้รับยืนยันต้นฉบับใบกำกับอีกครั้ง ·
    /// ต้นทางถูกยกเลิก คงเดิม · อื่น ๆ = <see cref="Merge"/> (ทิศตรงข้าม: ยืนยันใบกำกับไว้แล้ว ใบกำกับที่ออกแทนมาถึง ⇒ คงค่าที่ยืนยัน)</summary>
    /// <param name="vatMatches">ฝ่ายค้าน M3: VAT บนใบกำกับใหม่เท่ากับ VAT บนใบซื้อ (±0.005 — <see cref="VatMatches"/>) · ไม่เท่า ⇒ <see cref="AwaitingTaxInvoice"/>
    /// (ห้ามตั้งหลักฐานที่ปลดภาษีซื้อได้บนยอดที่ไม่ตรงใบกำกับ — ผู้รับแก้ VAT ให้ตรงแล้วใช้ทาง "ได้รับใบกำกับภาษีจากผู้ขายนอกระบบ")</param>
    /// <param name="externalConfirmationCovers">รอบ 257 ปิดท้าย MED-4 (<see cref="ExternalConfirmationCovers"/>): ผู้รับยืนยันใบกำกับใบเดียวกันนี้ไว้แล้วผ่านทาง
    /// "ได้ใบกำกับนอกระบบ" (เลขที่ตรง) ⇒ คำยืนยันเป็นของใบกำกับจริง ไม่ใช่ของใบแจ้งหนี้ ⇒ <b>คงไว้</b> (ไม่ตั้งกลับ PaperRequired) · VAT ไม่ตรง ⇒ ยังตั้งกลับเหมือนเดิม</param>
    public static string OnNewSource(string? current, string offered, bool newSourceIsTaxInvoice, bool priorSourceWasTaxInvoice, bool vatMatches = true,
        bool externalConfirmationCovers = false)
    {
        if (current == SourceVoided) return SourceVoided;
        if (newSourceIsTaxInvoice && !vatMatches) return AwaitingTaxInvoice;
        if (newSourceIsTaxInvoice && !priorSourceWasTaxInvoice)
            return externalConfirmationCovers && (current == EtaxVerified || current == PaperConfirmed) ? current! : offered;
        return Merge(current, offered);
    }

    /// <summary>ฝ่ายค้าน M1: ผู้รับแก้ยอด/VAT/บรรทัดของใบจากคู่ค้า ⇒ ใบไม่ตรงต้นฉบับแล้ว — กลับเป็น PaperRequired (ยกเว้นต้นทางถูกยกเลิก — คงไว้) ·
    /// null (ใบซื้อทั่วไป) / ยอดไม่เปลี่ยน ⇒ คงเดิม</summary>
    public static string? AfterEdit(string? current, bool amountsChanged)
    {
        if (current == null || !amountsChanged || current is SourceVoided or AwaitingTaxInvoice) return current;
        return PaperRequired;
    }

    /// <summary>ลายนิ้วมือยอดของใบ (ยอดก่อน VAT · VAT · ยอดรวม · บรรทัด) — เทียบก่อน/หลังแก้ไข (M1)</summary>
    public static string AmountFingerprint(decimal subTotal, decimal vat, decimal total, IEnumerable<(decimal qty, decimal price, decimal amount, decimal vatAmount)> lines)
        => string.Join("|", new[] { subTotal, vat, total }.Select(x => x.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))
           + "#" + string.Join(";", lines.Select(l => string.Join(",", new[] { l.qty, l.price, l.amount, l.vatAmount }
               .Select(x => x.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)))));

    /// <summary>ห้ามลงภาษีซื้อ (นับเป็น §86/4 ไม่ครบ) — null/ค่าอื่น = ไม่ใช่ใบจากคู่ค้า ⇒ ไม่แตะ (ทิศตรงข้าม: ใบซื้อทั่วไปไม่ถูกกระทบ)</summary>
    public static bool BlocksClaim(string? evidence)
    {
        // ตัวเมธอดแบบบล็อก (ไม่ใช่ =>) ให้ tools/required_call_site_check.py ล็อกชุดค่าได้ — ถอด AwaitingTaxInvoice ออก = ใบที่ยังไม่มีใบกำกับลงภาษีซื้อได้
        return evidence is AwaitingTimestamp or PaperRequired or SourceVoided or AwaitingTaxInvoice;
    }

    /// <summary>ข้อความในรายการช่องที่ขาด (ตัวตรวจ §86/4) — บอกทางไปต่อ</summary>
    public static string MissingLabel(string? evidence) => evidence switch
    {
        SourceVoided => "ต้นทางถูกผู้ขายยกเลิก — ขอใบกำกับใบใหม่จากผู้ขาย (ใบนี้ลงภาษีซื้อไม่ได้)",
        AwaitingTaxInvoice => "ใบกำกับภาษีของผู้ขาย (ต้นทางเป็นใบแจ้งหนี้ — รอผู้ขายออกใบกำกับ/ใบเสร็จที่เป็นใบกำกับของการขายนี้)",
        AwaitingTimestamp => "ต้นฉบับใบกำกับ (ได้ไฟล์ e-Tax ที่ลงลายมือชื่อของคู่ค้าแล้ว — ตรวจการประทับเวลา ETDA แล้วกด \"ได้รับต้นฉบับแล้ว\")",
        _ => "ต้นฉบับใบกำกับ (ใบจากคู่ค้า NextAcc — กด \"ได้รับต้นฉบับแล้ว\" เมื่อได้ใบกำกับตัวจริง/ไฟล์ e-Tax)",
    };

    public static string Label(string? evidence) => evidence switch
    {
        EtaxVerified => "ผู้รับยืนยันว่าได้ไฟล์ e-Tax ที่ประทับเวลาแล้ว (ลงภาษีซื้อได้เมื่ออนุมัติ)",
        AwaitingTimestamp => "ได้ไฟล์ e-Tax ของคู่ค้าแล้ว — รอผู้รับตรวจการประทับเวลา (ยังลงภาษีซื้อไม่ได้)",
        PaperRequired => "ต้องได้ต้นฉบับใบกำกับก่อน (ยังลงภาษีซื้อไม่ได้)",
        PaperConfirmed => "ผู้ใช้ยืนยันว่าได้ต้นฉบับแล้ว",
        SourceVoided => "ผู้ขายยกเลิกต้นทางแล้ว (ลงภาษีซื้อไม่ได้)",
        AwaitingTaxInvoice => "ต้นทางเป็นใบแจ้งหนี้ — รอใบกำกับภาษีจากผู้ขาย (ยังลงภาษีซื้อไม่ได้)",
        _ => "",
    };

    /// <summary>ยืนยัน "ได้ต้นฉบับแล้ว" ได้ไหม — เฉพาะใบที่ยังรอต้นฉบับ<b>ใบกำกับ</b> (ต้นทางถูกยกเลิก/ต้นทางยังเป็นใบแจ้งหนี้ = ยืนยันไม่ได้) · null = ได้ ·
    /// ตัวตัดสินเดียวของทั้งเซิร์ฟเวอร์ (<c>ConfirmPartnerOriginalAsync</c>) และปุ่มบนหน้าเว็บ (<c>NeedsOriginal</c> · <c>PartnerNeedsOriginal</c>)</summary>
    public static string? ConfirmProblem(string? evidence) => evidence switch
    {
        AwaitingTimestamp or PaperRequired => null,
        SourceVoided => SourceVoidedMessage,
        AwaitingTaxInvoice => AwaitingTaxInvoiceMessage,
        EtaxVerified or PaperConfirmed => AlreadyConfirmedMessage,
        _ => "เอกสารนี้ไม่ได้มาจากคู่ค้า NextAcc — ไม่มีต้นฉบับให้ยืนยันที่นี่",
    };

    /// <summary>ยืนยันแบบ "ได้ไฟล์ e-Tax ที่ประทับเวลาแล้ว" ได้ไหม — เฉพาะใบที่ไฟล์ XML ที่ลงลายมือชื่อถึงผู้รับแล้ว (server computes · หน้าเว็บไม่เทียบสตริงเอง)</summary>
    public static bool CanConfirmStampedEtax(string? evidence) => evidence == AwaitingTimestamp;
}
