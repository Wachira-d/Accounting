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
/// <para>ใบที่ส่งผ่านแพลตฟอร์มเข้าเป็นใบซื้อ <b>ร่าง</b> เสมอ · ระบบตั้งได้แค่สองค่า: e-Tax ที่ลงลายมือชื่อ<b>และส่งไฟล์ถึงผู้รับแล้ว</b> ⇒ <see cref="AwaitingTimestamp"/> ·
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

    public const string SourceVoidedMessage =
        "ผู้ขายยกเลิกเอกสารต้นทาง (หรือยกเลิก e-Tax ของใบนั้น) แล้ว — ลงภาษีซื้อจากใบนี้ไม่ได้ (ม.82/5(1)) · ขอใบใหม่จากผู้ขาย แล้วยกเลิก/กลับรายการใบซื้อนี้";

    public const string EditedNote = "แก้ยอด/VAT/บรรทัดของใบที่มาจากคู่ค้า — หลักฐานภาษีซื้อกลับเป็น \"ต้องได้ต้นฉบับ\" (ใบไม่ตรงต้นฉบับของผู้ขายแล้ว)";

    /// <param name="sourceIsTaxInvoice">ต้นทางเป็นใบกำกับภาษี (ใบแจ้งหนี้ที่ไม่ใช่ใบกำกับ ⇒ ไม่มีต้นฉบับใบกำกับให้ลงภาษีซื้อ)</param>
    /// <param name="signed">e-Tax ของต้นทางลงลายมือชื่อแล้ว (มีลายเซ็น + เลขใบรับรอง + เวลาลงนาม)</param>
    /// <param name="signedFileDelivered">ไฟล์ XML ที่ลงลายมือชื่อถูกส่งถึงผู้รับแล้ว — ไม่ส่ง = ผู้รับไม่มีต้นฉบับ</param>
    /// <returns>AwaitingTimestamp หรือ PaperRequired เท่านั้น — ไม่มีทางได้ค่าที่ปลดด่าน (H2)</returns>
    public static string Of(bool sourceIsTaxInvoice, bool signed, bool signedFileDelivered)
        => sourceIsTaxInvoice && signed && signedFileDelivered ? AwaitingTimestamp : PaperRequired;

    /// <summary>ค่าที่ผู้รับยืนยัน: ได้ไฟล์ e-Tax ที่ประทับเวลาแล้ว ⇒ EtaxVerified · ได้ต้นฉบับกระดาษ ⇒ PaperConfirmed</summary>
    public static string Confirmed(bool stampedEtax) => stampedEtax ? EtaxVerified : PaperConfirmed;

    /// <summary>ฝ่ายค้าน H3: หลักฐานใหม่ของการขายเดิมมาถึงใบซื้อที่มีอยู่ — อัปเกรดเฉพาะใบที่ยังติดด่าน (PaperRequired ⇒ AwaitingTimestamp) ·
    /// ค่าที่ผู้รับยืนยันแล้ว/ต้นทางถูกยกเลิก คงเดิม</summary>
    public static string Merge(string? current, string offered)
    {
        if (current is EtaxVerified or PaperConfirmed or SourceVoided) return current!;
        if (current == AwaitingTimestamp) return AwaitingTimestamp;
        return offered;
    }

    /// <summary>ฝ่ายค้าน M1: ผู้รับแก้ยอด/VAT/บรรทัดของใบจากคู่ค้า ⇒ ใบไม่ตรงต้นฉบับแล้ว — กลับเป็น PaperRequired (ยกเว้นต้นทางถูกยกเลิก — คงไว้) ·
    /// null (ใบซื้อทั่วไป) / ยอดไม่เปลี่ยน ⇒ คงเดิม</summary>
    public static string? AfterEdit(string? current, bool amountsChanged)
    {
        if (current == null || !amountsChanged || current == SourceVoided) return current;
        return PaperRequired;
    }

    /// <summary>ลายนิ้วมือยอดของใบ (ยอดก่อน VAT · VAT · ยอดรวม · บรรทัด) — เทียบก่อน/หลังแก้ไข (M1)</summary>
    public static string AmountFingerprint(decimal subTotal, decimal vat, decimal total, IEnumerable<(decimal qty, decimal price, decimal amount, decimal vatAmount)> lines)
        => string.Join("|", new[] { subTotal, vat, total }.Select(x => x.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))
           + "#" + string.Join(";", lines.Select(l => string.Join(",", new[] { l.qty, l.price, l.amount, l.vatAmount }
               .Select(x => x.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)))));

    /// <summary>ห้ามลงภาษีซื้อ (นับเป็น §86/4 ไม่ครบ) — null/ค่าอื่น = ไม่ใช่ใบจากคู่ค้า ⇒ ไม่แตะ (ทิศตรงข้าม: ใบซื้อทั่วไปไม่ถูกกระทบ)</summary>
    public static bool BlocksClaim(string? evidence) => evidence is AwaitingTimestamp or PaperRequired or SourceVoided;

    /// <summary>ข้อความในรายการช่องที่ขาด (ตัวตรวจ §86/4) — บอกทางไปต่อ</summary>
    public static string MissingLabel(string? evidence) => evidence switch
    {
        SourceVoided => "ต้นทางถูกผู้ขายยกเลิก — ขอใบกำกับใบใหม่จากผู้ขาย (ใบนี้ลงภาษีซื้อไม่ได้)",
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
        _ => "",
    };

    /// <summary>ยืนยัน "ได้ต้นฉบับแล้ว" ได้ไหม — เฉพาะใบที่ยังรอต้นฉบับ (ต้นทางถูกยกเลิก = ยืนยันไม่ได้) · null = ได้</summary>
    public static string? ConfirmProblem(string? evidence) => evidence switch
    {
        AwaitingTimestamp or PaperRequired => null,
        SourceVoided => SourceVoidedMessage,
        _ => "เอกสารนี้ไม่ได้รอต้นฉบับใบกำกับจากคู่ค้า",
    };
}
