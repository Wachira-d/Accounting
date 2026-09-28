using System.Text.Json;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>เครื่องหมายของ <c>SettlementLine.Amount</c> ที่ประเภทหนึ่งยอมรับ (มุม wallet: บวก = ค้างเราเพิ่ม · ลบ = ถูกหัก)</summary>
public enum SettlementAmountSign
{
    Positive = 1,
    Negative = 2,
    Either = 3,
}

/// <summary>บรรทัดประเภทนี้ไปลงบัญชีทางไหน (ตัดสินใน <see cref="SettlementBatchMath.Plan"/>)</summary>
public enum SettlementPostingKind
{
    /// <summary>ห้ามลงบัญชี (Unclassified)</summary>
    None = 0,
    /// <summary>องค์ประกอบของยอดขาย — รวมกับบรรทัดขายอื่นของใบขายเดียวกันเป็นการรับชำระ 1 ครั้ง หรือเข้าใบขายสรุปรายวัน</summary>
    SaleComponent = 1,
    /// <summary>คืนเงินผู้ซื้อ — ใบลดหนี้อ้างใบขายเดิม + จ่ายคืนจากบัญชีพัก</summary>
    Refund = 2,
    /// <summary>ค่าธรรมเนียม → เอกสารซื้อ 1 ใบ/batch/กลุ่มภาษี (ภ.พ.30/36 · 50 ทวิ · §65 ตรี เดินเส้นเดิม)</summary>
    FeeDocument = 3,
    /// <summary>ลงใน JE รอบโอนตรง (reserve · chargeback · ภาษีถูกหัก · FX · ปรับปรุง · ค่าขนส่งที่แพลตฟอร์มช่วย)</summary>
    DirectJournal = 4,
}

/// <summary>กติกาของประเภทบรรทัดหนึ่ง — แถวเดียวในตาราง <see cref="SettlementLineTypeRules.All"/></summary>
/// <param name="AccountRole">บทบาทผัง (<see cref="SettlementAccountRoles"/>) — คีย์ของ <c>SettlementChannel.FeeAccountMapJson</c> สำหรับค่าธรรมเนียม ·
/// null = ผังมาจากเอกสาร (ขาย/คืนเงิน) หรือผู้ใช้ต้องเลือก (Adjustment)</param>
/// <param name="IsFee">เป็นค่าธรรมเนียมที่ต้องออกเป็นเอกสารซื้อ</param>
/// <param name="VatApplicable">ค่าธรรมเนียมประเภทนี้มี VAT ได้ (ตามโหมด VAT ของช่องทาง)</param>
/// <param name="WhtIncomeCode">รหัสประเภทเงินได้ใน <see cref="ThaiWhtRateTable"/> เมื่อช่องทางเปิดหัก ณ ที่จ่าย · null = ไม่หัก</param>
/// <param name="RequiresSaleMatch">ต้องจับคู่ใบขาย (จับไม่ได้ ⇒ ใบขายสรุปรายวัน สำหรับยอดขาย · บล็อกสำหรับคืนเงิน)</param>
/// <param name="RequiresReason">บังคับเหตุผล + ผังที่ผู้ใช้เลือก (Adjustment)</param>
public sealed record SettlementLineTypeRule(
    SettlementLineType Type,
    string LabelTh,
    SettlementAmountSign AllowedSign,
    SettlementPostingKind Posting,
    string? AccountRole,
    bool IsFee,
    bool VatApplicable,
    string? WhtIncomeCode,
    bool RequiresSaleMatch,
    bool RequiresReason)
{
    /// <summary>ลงบัญชีได้ไหม — Unclassified ห้ามเสมอ</summary>
    public bool Postable => Posting != SettlementPostingKind.None;

    /// <summary>ยอดนี้เครื่องหมายถูกตามประเภทไหม (0 ถือว่าผ่าน — แถว 0 ไม่ใช่หลักฐาน ตัวคิดแผนข้ามเอง)</summary>
    public bool SignAllowed(decimal amount) => amount == 0m || AllowedSign switch
    {
        SettlementAmountSign.Positive => amount > 0m,
        SettlementAmountSign.Negative => amount < 0m,
        _ => true,
    };
}

/// <summary>บทบาทผังที่แผนลงบัญชีใช้ — ผู้ลงบัญชี (ทีม C) แปลงเป็น AccountId ตามลำดับ:
/// <c>SettlementLine.OverrideAccountId</c> → คอลัมน์ของช่องทาง (clearing/reserve/dispute) → <c>FeeAccountMapJson</c> → ผังมาตรฐาน
/// (<see cref="DefaultCode"/>) · หาไม่เจอ = ล้มดังพร้อมชื่อบทบาท ห้ามตกผังอื่นเงียบ</summary>
public static class SettlementAccountRoles
{
    public const string Bank = "bank";
    public const string Clearing = "clearing";
    public const string Reserve = "reserve";
    public const string Dispute = "dispute";
    public const string Commission = "commission";
    public const string PaymentFee = "payment_fee";
    public const string Shipping = "shipping";
    public const string Ads = "ads";
    public const string ServiceFee = "service_fee";
    public const string WithdrawalFee = "withdrawal_fee";
    public const string WhtCredit = "wht_credit";
    public const string FxGain = "fx_gain";
    public const string FxLoss = "fx_loss";
    public const string Adjustment = "adjustment";
    public const string ChargebackLoss = "chargeback_loss";
    public const string WhtPayable = "wht_payable";
    public const string WhtReimbursable = "wht_reimbursable";
    public const string InputVatPending = "input_vat_pending";
    public const string Pp36InputVat = "pp36_input_vat";
    public const string Pp36Payable = "pp36_payable";

    /// <summary>บทบาทที่ผู้ใช้ตั้งผังเองได้ใน <c>FeeAccountMapJson</c> (ที่เหลือมาจากคอลัมน์ของช่องทาง/บัญชีธนาคาร/ผังภาษีที่ตายตัว)</summary>
    public static readonly IReadOnlyList<string> Mappable = new[]
    {
        Commission, PaymentFee, Shipping, Ads, ServiceFee, WithdrawalFee, WhtCredit, FxGain, FxLoss, ChargebackLoss,
    };

    /// <summary>ผังมาตรฐานของบทบาท — null = ไม่มีค่าเริ่มต้น (clearing/bank มาจากช่องทาง/รอบโอน · adjustment ผู้ใช้เลือก)
    /// <para>ผังภาษี ภ.พ.36 ใช้ตัวตั้งของ <see cref="ForeignServiceVat"/> (ห้ามพิมพ์ซ้ำ)</para></summary>
    public static string? DefaultCode(string role) => role switch
    {
        Reserve => SettlementChartSeed.ReserveAccountCode,
        Dispute => "11320",            // ลูกหนี้อื่น — พัก chargeback ระหว่างรอผล (report-S1 G5)
        Commission => "53140",         // ค่านายหน้าการขาย
        PaymentFee => SettlementChartSeed.PaymentFeeAccountCode,
        Shipping => "53130",           // ค่าขนส่ง (ฝ่ายขาย)
        Ads => "53120",                // ค่าโฆษณาและส่งเสริมการขาย
        ServiceFee => "53150",         // ค่าใช้จ่ายในการขายอื่นๆ
        WithdrawalFee => "54710",      // ค่าธรรมเนียมธนาคาร
        WhtCredit => "11910",          // ภาษีถูกหัก ณ ที่จ่าย
        FxGain => "43050",             // รายได้จากอัตราแลกเปลี่ยน
        FxLoss => "54950",             // ขาดทุนจากอัตราแลกเปลี่ยน
        ChargebackLoss => SettlementChartSeed.ChargebackLossAccountCode,
        WhtPayable => "21917",         // ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 53 (แพลตฟอร์มเป็นนิติบุคคล)
        WhtReimbursable => "11320",    // ลูกหนี้อื่น — ภาษีที่หักไว้รอแพลตฟอร์มคืน (W2)
        InputVatPending => "11630",    // ภาษีซื้อรอเครดิต — จนได้ใบกำกับรายเดือน (report-S1 G3/M3)
        Pp36InputVat => ForeignServiceVat.Pp36InputVatCode,
        Pp36Payable => ForeignServiceVat.Pp36PayableCode,
        _ => null,
    };
}

/// <summary>
/// **กติกาของประเภทบรรทัด settlement — ตารางเดียวทั้งระบบ** (รอบ 198 · report-S1 §7 · report-S2 §3.2)
///
/// <para>ทุกทางเข้า (adapter นำเข้า · ตัวจัดประเภท · ตัวคิดแผนลงบัญชี · หน้าจอ) ถามที่นี่ — ห้ามเขียน switch ของ
/// <see cref="SettlementLineType"/> เองที่อื่น · enum ทุกค่าต้องมีแถว (<c>tools/settlement_line_type_rules_check.py</c> + เทสต์)</para>
///
/// <para>═══ ตัดสินใจที่ตั้งใจ ═══
/// <list type="bullet">
/// <item><b>Unclassified ห้ามลงบัญชี</b> — ไม่รู้ = บอกว่าไม่รู้ ห้ามตก "ค่าใช้จ่ายอื่น" เงียบ (report-S1 §7 ข้อ 2)</item>
/// <item><b>ส่วนลดร้าน (SellerVoucher) ลดยอดขาย</b> ไม่ใช่ค่าใช้จ่าย และไม่อยู่ในฐาน VAT · <b>โค้ดแพลตฟอร์ม (PlatformVoucherSubsidy)
/// เป็นส่วนของยอดขาย</b> อยู่ในฐาน VAT (report-S1 §3 — ความมั่นใจกลาง)</item>
/// <item>ค่าธรรมเนียมยอมทั้งสองเครื่องหมาย (แพลตฟอร์มคืนค่าคอมได้) — ตัวคิดแผนรวมสุทธิต่อกลุ่มภาษี ถ้าสุทธิเป็นยอดคืน ⇒ ปัญหาพร้อมทางไปต่อ</item>
/// <item>ค่าธรรมเนียมถอนเงินไม่หัก ณ ที่จ่าย (ค่าธรรมเนียมธนาคาร · report-S1 D11 ยังไม่ตัดสิน MDR) · อัตรามาจาก <see cref="ThaiWhtRateTable"/> เท่านั้น</item>
/// </list></para>
/// </summary>
public static class SettlementLineTypeRules
{
    /// <summary>ทุกประเภท — ลำดับตามค่า enum</summary>
    public static readonly IReadOnlyList<SettlementLineTypeRule> All = new[]
    {
        new SettlementLineTypeRule(SettlementLineType.Unclassified, "รอจัดประเภท", SettlementAmountSign.Either,
            SettlementPostingKind.None, null, false, false, null, false, false),
        new SettlementLineTypeRule(SettlementLineType.Sale, "ยอดขาย", SettlementAmountSign.Positive,
            SettlementPostingKind.SaleComponent, null, false, false, null, true, false),
        new SettlementLineTypeRule(SettlementLineType.Refund, "คืนเงินผู้ซื้อ", SettlementAmountSign.Negative,
            SettlementPostingKind.Refund, null, false, false, null, true, false),
        new SettlementLineTypeRule(SettlementLineType.Chargeback, "ถูกปฏิเสธรายการ (chargeback)", SettlementAmountSign.Negative,
            SettlementPostingKind.DirectJournal, SettlementAccountRoles.Dispute, false, false, null, false, false),
        new SettlementLineTypeRule(SettlementLineType.ChargebackReversal, "ชนะ chargeback ได้เงินคืน", SettlementAmountSign.Positive,
            SettlementPostingKind.DirectJournal, SettlementAccountRoles.Dispute, false, false, null, false, false),
        new SettlementLineTypeRule(SettlementLineType.Commission, "ค่าคอมมิชชัน", SettlementAmountSign.Either,
            SettlementPostingKind.FeeDocument, SettlementAccountRoles.Commission, true, true, "2", false, false),
        new SettlementLineTypeRule(SettlementLineType.PaymentFee, "ค่าธรรมเนียมรับชำระเงิน", SettlementAmountSign.Either,
            SettlementPostingKind.FeeDocument, SettlementAccountRoles.PaymentFee, true, true, "8", false, false),
        new SettlementLineTypeRule(SettlementLineType.ShippingFeeCharged, "ค่าขนส่งที่แพลตฟอร์มเรียกเก็บ", SettlementAmountSign.Either,
            SettlementPostingKind.FeeDocument, SettlementAccountRoles.Shipping, true, true, "8tr", false, false),
        new SettlementLineTypeRule(SettlementLineType.ShippingSubsidy, "แพลตฟอร์มช่วยค่าขนส่ง", SettlementAmountSign.Positive,
            SettlementPostingKind.DirectJournal, SettlementAccountRoles.Shipping, false, false, null, false, false),
        new SettlementLineTypeRule(SettlementLineType.SellerVoucher, "ส่วนลดที่ร้านออกเอง", SettlementAmountSign.Negative,
            SettlementPostingKind.SaleComponent, null, false, false, null, true, false),
        new SettlementLineTypeRule(SettlementLineType.PlatformVoucherSubsidy, "โค้ดส่วนลดที่แพลตฟอร์มออกเงิน", SettlementAmountSign.Positive,
            SettlementPostingKind.SaleComponent, null, false, false, null, true, false),
        new SettlementLineTypeRule(SettlementLineType.AdsFee, "ค่าโฆษณา", SettlementAmountSign.Either,
            SettlementPostingKind.FeeDocument, SettlementAccountRoles.Ads, true, true, "8ad", false, false),
        new SettlementLineTypeRule(SettlementLineType.ServiceFee, "ค่าบริการแพลตฟอร์ม", SettlementAmountSign.Either,
            SettlementPostingKind.FeeDocument, SettlementAccountRoles.ServiceFee, true, true, "8", false, false),
        new SettlementLineTypeRule(SettlementLineType.WithdrawalFee, "ค่าธรรมเนียมถอนเงิน", SettlementAmountSign.Negative,
            SettlementPostingKind.FeeDocument, SettlementAccountRoles.WithdrawalFee, true, true, null, false, false),
        new SettlementLineTypeRule(SettlementLineType.ReserveHold, "แพลตฟอร์มกันเงินไว้", SettlementAmountSign.Negative,
            SettlementPostingKind.DirectJournal, SettlementAccountRoles.Reserve, false, false, null, false, false),
        new SettlementLineTypeRule(SettlementLineType.ReserveRelease, "ปล่อยเงินที่กันไว้", SettlementAmountSign.Positive,
            SettlementPostingKind.DirectJournal, SettlementAccountRoles.Reserve, false, false, null, false, false),
        new SettlementLineTypeRule(SettlementLineType.TaxWithheldByPlatform, "ภาษีที่แพลตฟอร์มหัก ณ ที่จ่ายจากเรา", SettlementAmountSign.Negative,
            SettlementPostingKind.DirectJournal, SettlementAccountRoles.WhtCredit, false, false, null, false, false),
        new SettlementLineTypeRule(SettlementLineType.FxDifference, "ผลต่างอัตราแลกเปลี่ยน", SettlementAmountSign.Either,
            SettlementPostingKind.DirectJournal, null, false, false, null, false, false),
        new SettlementLineTypeRule(SettlementLineType.Adjustment, "ปรับปรุงอื่น (ระบุเหตุผล)", SettlementAmountSign.Either,
            SettlementPostingKind.DirectJournal, SettlementAccountRoles.Adjustment, false, false, null, false, true),
    };

    private static readonly Dictionary<SettlementLineType, SettlementLineTypeRule> ByType = All.ToDictionary(r => r.Type);

    /// <summary>กติกาของประเภทนี้ — ค่าที่ไม่มีในตาราง (ข้อมูลเสีย/ค่าจากอนาคต) ⇒ กติกาของ Unclassified (ห้ามลงบัญชี) ไม่ใช่ throw</summary>
    public static SettlementLineTypeRule For(SettlementLineType type)
        => ByType.TryGetValue(type, out var r) ? r : ByType[SettlementLineType.Unclassified];

    /// <summary>แปลงคำตอบของตัวจัดประเภท (student/ครู — ชื่อ enum) เป็นประเภท <b>เฉพาะเมื่ออยู่ในชุดที่ลงบัญชีได้</b> —
    /// ด่านกันคำตอบแต่ง (กฎเหล็ก #1 anti-hallucination) · ตัวเลข/ชื่อที่ไม่รู้จัก/Unclassified ⇒ null (= คงรอจัดประเภท)</summary>
    public static SettlementLineType? ParseClassifierAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var a = answer.Trim();
        if (a.Length == 0 || char.IsDigit(a[0]) || a[0] == '-') return null;    // ห้ามรับตัวเลข — Enum.TryParse รับ "99" เป็นค่าไม่มีชื่อ
        // R-B14: ต้องเป็นชื่อเดียวตรงตัว — Enum.TryParse รับ "Sale, Refund" แล้ว OR ค่าเป็นประเภทที่สาม (= Chargeback) ทั้งที่ enum ไม่ใช่ [Flags]
        if (!Enum.GetNames<SettlementLineType>().Contains(a, StringComparer.OrdinalIgnoreCase)) return null;
        if (!Enum.TryParse<SettlementLineType>(a, ignoreCase: true, out var t)) return null;
        if (!ByType.ContainsKey(t) || !For(t).Postable) return null;
        return t;
    }

    /// <summary>อ่าน <c>SettlementChannel.FeeAccountMapJson</c> — ตัวอ่านตัวเดียว (หน้าตั้งค่า · ผู้ลงบัญชี)
    /// <para>รับเฉพาะคีย์ใน <see cref="SettlementAccountRoles.Mappable"/> ที่ค่าเป็น Guid · คีย์อื่น/ค่าเสีย ⇒ อยู่ใน <c>Rejected</c>
    /// (ผู้เรียกแสดงให้ผู้ใช้แก้ — ห้ามทิ้งเงียบ) · JSON พัง ⇒ ว่าง + Rejected = ["(json)"] · ผู้เรียกต้องตรวจ AccountId เป็นของบริษัทเอง</para></summary>
    public static (IReadOnlyDictionary<string, Guid> Map, IReadOnlyList<string> Rejected) ParseFeeAccountMap(string? json)
    {
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var rejected = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return (map, rejected);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                rejected.Add("(json)");
                return (map, rejected);
            }
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (SettlementAccountRoles.Mappable.Contains(p.Name, StringComparer.Ordinal)
                    && p.Value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(p.Value.GetString(), out var id) && id != Guid.Empty)
                    map[p.Name] = id;
                else
                    rejected.Add(p.Name);
            }
        }
        catch (JsonException)
        {
            rejected.Add("(json)");
        }
        return (map, rejected);
    }
}
