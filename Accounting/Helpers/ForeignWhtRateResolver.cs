namespace Accounting.Helpers;

/// <summary>หลักฐาน "หนังสือรับรองถิ่นที่อยู่ทางภาษี" (Certificate of Residence · CoR) ของผู้รับเงินต่างประเทศ — เงื่อนไขเดียวที่ให้ใช้อัตราตามอนุสัญญา</summary>
/// <param name="Held">มีหนังสือรับรองฉบับจริงเก็บไว้</param>
/// <param name="ValidFrom">ครอบตั้งแต่วันที่ (null = ไม่ระบุ)</param>
/// <param name="ValidTo">ครอบถึงวันที่ (null = ไม่ระบุ)</param>
public readonly record struct ResidenceCertificate(bool Held, DateTime? ValidFrom, DateTime? ValidTo)
{
    /// <summary>ไม่มีหลักฐาน — ค่าที่ผู้เรียกทุกตัวส่งวันนี้ (ระบบยังไม่มีช่องเก็บ CoR · ดู <see cref="DtaTreatyRates"/>)</summary>
    public static ResidenceCertificate None => default;

    /// <summary>ครอบวันจ่ายนี้ไหม — ไม่มีหลักฐาน = ไม่ครอบ</summary>
    public bool Covers(DateTime paymentDate)
        => Held
           && (ValidFrom is not DateTime f || paymentDate.Date >= f.Date)
           && (ValidTo is not DateTime t || paymentDate.Date <= t.Date);
}

/// <summary>ผลการตัดสินอัตรา</summary>
public enum ForeignWhtOutcome
{
    /// <summary>อัตรา ม.70 ตามกฎหมาย (15% · เงินปันผล 10%) — รวมกรณี "มีอนุสัญญาแต่ไม่มี CoR"</summary>
    Section70 = 1,
    /// <summary>อัตราตามอนุสัญญา (มีแถวที่ยืนยันแล้ว + CoR ครอบวันจ่าย)</summary>
    TreatyRate = 2,
    /// <summary>ประเภทเงินได้อยู่นอก ม.70 (40(1)/(7)/(8)) — ไม่มีอัตรา ม.70 · ต้องให้ผู้ทำบัญชีจำแนก</summary>
    NotSection70 = 3,
    /// <summary>ไม่รู้ประเภทเงินได้ — ไม่มีอัตราให้ (ห้ามเดา)</summary>
    UnknownIncomeType = 4,
}

/// <summary>คำตัดสิน 1 ครั้ง — ทุกผลมี <c>RuleCode</c> + <c>LegalReference</c> (CLAUDE.md กฎเหล็ก #2 M "Legal reference logging")</summary>
/// <param name="RatePercent">อัตราที่ต้องหัก (%) · null = ไม่มีอัตรา (<see cref="ForeignWhtOutcome.NotSection70"/> · <see cref="ForeignWhtOutcome.UnknownIncomeType"/>)</param>
/// <param name="Explanation">ข้อความไทยพร้อมแสดงให้ผู้ใช้</param>
public sealed record ForeignWhtDecision(
    ForeignWhtOutcome Outcome,
    ForeignIncomeCategory Category,
    decimal? RatePercent,
    string RuleCode,
    string LegalReference,
    string Explanation)
{
    /// <summary>มีอัตราให้ใช้ (ม.70 หรืออนุสัญญา)</summary>
    public bool HasRate => RatePercent is not null;
}

/// <summary>
/// **อัตราหัก ณ ที่จ่ายเมื่อจ่ายเงินได้ไปต่างประเทศ (ภ.ง.ด.54) — ตัวตัดสินตัวเดียวของระบบ** (รอบ 200 ทีม W · คำตัดสินข้อ 13)
///
/// <para>═══ ลำดับ ═══ (1) จำแนกประเภทเงินได้จากรหัสใน <see cref="ThaiWhtRateTable"/> (ตารางประเภทเงินได้ตัวเดียว) → (2) อยู่นอก ม.70 / ไม่รู้ ⇒
/// <b>ไม่มีอัตรา</b> ให้ผู้เรียกบล็อก/เตือน (ห้ามตกไปอัตราในประเทศ — review198-A R-A5) → (3) มีแถวใน <see cref="DtaTreatyRates"/> <b>และ</b>
/// CoR ครอบวันจ่าย <b>และ</b> อัตราอนุสัญญาต่ำกว่า ⇒ อัตราอนุสัญญา → (4) ไม่งั้น อัตรา ม.70</para>
///
/// <para>═══ ฐานกฎหมาย ═══ ป.รัษฎากร ม.70: บริษัท/ห้างหุ้นส่วนนิติบุคคลที่ตั้งขึ้นตามกฎหมายต่างประเทศและมิได้ประกอบกิจการในไทย ได้รับเงินได้
/// ตาม ม.40(2)(3)(4)(5)(6) ที่จ่ายจากหรือในประเทศไทย ⇒ ผู้จ่ายหักภาษีร้อยละ 15 (เงินปันผลร้อยละ 10) ยื่น ภ.ง.ด.54 ภายใน 7 วันนับแต่วันสิ้นเดือนที่จ่าย
/// (กำหนดยื่นจาก <see cref="TaxFilingDeadline"/> ตัวเดียว) · <b>ไม่มีเกณฑ์ขั้นต่ำ 1,000 บาท</b> (เกณฑ์นั้นเป็นของ ท.ป.4/2528 ซึ่งออกตาม §3 เตรส ไม่ใช่ ม.70)</para>
///
/// <para>ห้ามพิมพ์ "15%"/"10%" ของ ม.70 ซ้ำที่อื่น — อ่าน <see cref="Section70GeneralRate"/>/<see cref="Section70DividendRate"/></para>
/// </summary>
public static class ForeignWhtRateResolver
{
    /// <summary>ม.70 อัตราทั่วไป (%)</summary>
    public const decimal Section70GeneralRate = 15m;
    /// <summary>ม.70 เงินปันผล (%)</summary>
    public const decimal Section70DividendRate = 10m;
    /// <summary>ตัวบทที่อ้างใน audit/ข้อความ</summary>
    public const string Section70Reference = "ป.รัษฎากร ม.70";

    /// <summary>จำแนกประเภทเงินได้จากรหัสใน <see cref="ThaiWhtRateTable"/> (รับทั้งรหัส "2"/"4a" และมาตรา "40(2)")</summary>
    internal static ForeignIncomeCategory CategoryOf(string? incomeCodeOrSection)
    {
        var section = (ThaiWhtRateTable.Find(incomeCodeOrSection)?.TaxSection ?? (incomeCodeOrSection ?? "").Trim()).Replace(" ", "");
        if (section.StartsWith("40(2)", StringComparison.Ordinal)) return ForeignIncomeCategory.FeesCommission;
        if (section.StartsWith("40(3)", StringComparison.Ordinal)) return ForeignIncomeCategory.Royalty;
        if (section.StartsWith("40(4)", StringComparison.Ordinal))
        {
            if (section.Contains("(ข)", StringComparison.Ordinal) || section.EndsWith("(b)", StringComparison.OrdinalIgnoreCase))
                return ForeignIncomeCategory.Dividend;
            if (section.Contains("(ก)", StringComparison.Ordinal) || section.EndsWith("(a)", StringComparison.OrdinalIgnoreCase))
                return ForeignIncomeCategory.Interest;
            return ForeignIncomeCategory.Unknown;   // 40(4) เฉย ๆ ชี้ขาดไม่ได้ว่าดอกเบี้ย (15%) หรือปันผล (10%) — ห้ามเดา
        }
        if (section.StartsWith("40(5)", StringComparison.Ordinal)) return ForeignIncomeCategory.Rent;
        if (section.StartsWith("40(6)", StringComparison.Ordinal)) return ForeignIncomeCategory.Professional;
        if (section.StartsWith("40(1)", StringComparison.Ordinal) || section.StartsWith("40(7)", StringComparison.Ordinal)
            || section.StartsWith("40(8)", StringComparison.Ordinal))
            return ForeignIncomeCategory.OutsideSection70;
        return ForeignIncomeCategory.Unknown;
    }

    /// <summary>ตัดสินอัตรา — ไม่ throw · ไม่มีอัตรา = <see cref="ForeignWhtDecision.RatePercent"/> null พร้อมคำอธิบาย</summary>
    /// <param name="payeeCountryIso2">ประเทศถิ่นที่อยู่ของผู้รับ (ISO alpha-2) · null = ไม่รู้ ⇒ หาแถวอนุสัญญาไม่ได้ ⇒ ม.70</param>
    /// <param name="cor">หนังสือรับรองถิ่นที่อยู่ — วันนี้ผู้เรียกทุกตัวส่ง <see cref="ResidenceCertificate.None"/></param>
    /// <param name="paymentDate">วันที่จ่าย (เลือกฉบับอนุสัญญา + ตรวจว่า CoR ครอบ)</param>
    public static ForeignWhtDecision Resolve(ForeignIncomeCategory category, string? payeeCountryIso2, ResidenceCertificate cor,
        DateTime paymentDate)
        => Resolve(category, payeeCountryIso2, cor, paymentDate, DtaTreatyRates.Rows);

    /// <summary>ตัวตัดสินบนตารางที่ส่งเข้ามา (เทสต์ใช้พิสูจน์ทิศอนุสัญญา — ตารางจริงยังว่าง)</summary>
    internal static ForeignWhtDecision Resolve(ForeignIncomeCategory category, string? payeeCountryIso2, ResidenceCertificate cor,
        DateTime paymentDate, IReadOnlyList<DtaTreatyRate> treatyTable)
    {
        if (category == ForeignIncomeCategory.Unknown)
            return new ForeignWhtDecision(ForeignWhtOutcome.UnknownIncomeType, category, null, "RD-70-UNKNOWN", Section70Reference,
                "ไม่ทราบประเภทเงินได้ (ม.40 วงเล็บไหน) — ระบบไม่เลือกอัตราให้ · ระบุประเภทเงินได้ก่อน "
                + $"(ม.70: 40(2)(3)(4)(5)(6) หัก {Section70GeneralRate:0.##}% · เงินปันผล {Section70DividendRate:0.##}%)");
        if (category == ForeignIncomeCategory.OutsideSection70)
            return new ForeignWhtDecision(ForeignWhtOutcome.NotSection70, category, null, "RD-70-NA", Section70Reference,
                "เงินได้ประเภท 40(1)/(7)/(8) ไม่อยู่ใน ม.70 (ครอบเฉพาะ 40(2)(3)(4)(5)(6)) — ถ้าเป็นค่าบริการ/ค่าโฆษณาที่จ่ายต่างประเทศ "
                + "ให้ผู้ทำบัญชีจำแนกก่อนว่าเป็น 40(8) จริง หรือเป็นค่าธรรมเนียม 40(2)/ค่าสิทธิ 40(3) (ซึ่งต้องหัก "
                + $"{Section70GeneralRate:0.##}%) — ระบบไม่เดาให้");

        var statutory = category == ForeignIncomeCategory.Dividend ? Section70DividendRate : Section70GeneralRate;
        var statutoryCode = category == ForeignIncomeCategory.Dividend ? "RD-70-DIV" : "RD-70";
        var row = DtaTreatyRates.Find(treatyTable, payeeCountryIso2, category, paymentDate);
        if (row != null && row.RatePercent < statutory)
        {
            if (cor.Covers(paymentDate))
                return new ForeignWhtDecision(ForeignWhtOutcome.TreatyRate, category, row.RatePercent,
                    $"DTA-{row.CountryIso2}", row.LegalReference,
                    $"อัตราตามอนุสัญญา {row.RatePercent:0.##}% ({row.LegalReference}) — {row.Condition} · มีหนังสือรับรองถิ่นที่อยู่ครอบวันจ่าย");
            return new ForeignWhtDecision(ForeignWhtOutcome.Section70, category, statutory, statutoryCode, Section70Reference,
                $"มีอนุสัญญากับ {row.CountryIso2} ({row.LegalReference} · {row.RatePercent:0.##}%) แต่ไม่มีหนังสือรับรองถิ่นที่อยู่ที่ครอบวันจ่าย "
                + $"⇒ หักตาม ม.70 {statutory:0.##}% · ขอหนังสือรับรองจากผู้รับก่อนจ่ายครั้งถัดไปเพื่อใช้อัตราอนุสัญญา");
        }
        return new ForeignWhtDecision(ForeignWhtOutcome.Section70, category, statutory, statutoryCode, Section70Reference,
            $"หักตาม ม.70 {statutory:0.##}% ยื่น ภ.ง.ด.54"
            + (row == null ? " (ระบบยังไม่มีแถวอนุสัญญาที่ยืนยันแล้วสำหรับผู้รับรายนี้ — ถ้ามีอนุสัญญา ให้ผู้ทำบัญชียืนยันตัวบทแล้วเติมตาราง)" : ""));
    }

    /// <summary>ทางลัด: ตัดสินจากรหัสประเภทเงินได้ของบรรทัด/หนังสือรับรอง</summary>
    public static ForeignWhtDecision ResolveForIncomeCode(string? incomeCodeOrSection, string? payeeCountryIso2, ResidenceCertificate cor,
        DateTime paymentDate)
        => Resolve(CategoryOf(incomeCodeOrSection), payeeCountryIso2, cor, paymentDate);

    /// <summary>
    /// อัตราที่ใบใช้จริงเทียบกับคำตัดสิน — ข้อความเตือน (ไทย · มี RuleCode + มาตรา) หรือ null เมื่อตรง
    /// <para>หักขาด = §54 ผู้จ่ายรับผิดในส่วนที่ขาด (มองไม่เห็นจนถูกประเมิน) · หักเกิน = ผู้รับทวง/ต้องคืน · ไม่มีอัตรา = ให้คนจำแนก</para>
    /// </summary>
    /// <param name="usedRatePercent">อัตราที่ใบ/บรรทัดใช้ (%) · ≤ 0 = ไม่ได้หัก</param>
    public static string? RateWarning(ForeignWhtDecision decision, decimal usedRatePercent)
    {
        var tag = $"[{decision.RuleCode} · {decision.LegalReference}]";
        if (decision.RatePercent is not decimal expected)
            return usedRatePercent > 0m ? $"{tag} {decision.Explanation} — ใบนี้ใช้ {usedRatePercent:0.##}%" : null;
        if (usedRatePercent == expected) return null;
        if (usedRatePercent < expected)
            return $"{tag} จ่ายต่างประเทศหัก {usedRatePercent:0.##}% ต่ำกว่าที่ต้องหัก {expected:0.##}% — ส่วนที่หักขาดผู้จ่ายรับผิดเอง (ป.รัษฎากร §54) · "
                + decision.Explanation;
        return $"{tag} จ่ายต่างประเทศหัก {usedRatePercent:0.##}% สูงกว่าที่ต้องหัก {expected:0.##}% — ผู้รับจะทวงส่วนที่หักเกิน · " + decision.Explanation;
    }
}
