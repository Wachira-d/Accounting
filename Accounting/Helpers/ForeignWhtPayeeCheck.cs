using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผู้รับเงินต่างประเทศรายนี้อยู่ใต้ ม.70 ไหม — ตัดสินจากข้อมูลบนผู้ติดต่อเท่านั้น (ไม่เดา)</summary>
public enum ForeignPayeeWhtScope
{
    /// <summary>นิติบุคคลต่างประเทศที่ไม่มีเลขผู้เสียภาษีไทย ⇒ ไม่ได้ลงทะเบียนประกอบกิจการในไทย ⇒ ม.70 (ภ.ง.ด.54)</summary>
    Section70 = 1,
    /// <summary>บุคคลธรรมดาต่างประเทศ — ม.70 ครอบเฉพาะนิติบุคคล ⇒ ระบบไม่ตัดสินอัตรา ม.70 ให้ (ห้ามเตือน "ต้องหัก 15%")</summary>
    Individual = 2,
    /// <summary>มีเลขผู้เสียภาษีนิติบุคคลไทย — อาจมีสถานประกอบการถาวร/สาขาในไทย (หัก 3% ภ.ง.ด.53 ถูก) หรือแค่จด VAT e-Service (ยังเป็น ม.70) ⇒ <b>ไม่รู้</b></summary>
    ThaiRegisteredUnknown = 3,
    /// <summary>ไม่รู้ว่าเป็นนิติบุคคลหรือบุคคลธรรมดา (ประเภทผู้ติดต่อยังไม่ระบุ + ไม่มีสัญญาณจากเลข/ชื่อ) ⇒ <b>ไม่รู้</b></summary>
    KindUnknown = 4,
}

/// <summary>
/// **คำเตือนอัตรา WHT จ่ายต่างประเทศตอนอนุมัติ/ออก 50 ทวิ — ตัวตัดสินขอบเขตผู้รับตัวเดียว** (รอบ 200 ทีม WF · ฝ่ายค้าน W-5/W-7)
///
/// <para>ตัวตัดสินอัตรา (<see cref="ForeignWhtRateResolver"/>) สมมติว่าผู้รับเป็น "นิติบุคคลต่างประเทศที่มิได้ประกอบกิจการในไทย" เสมอ ⇒ ผู้เรียกที่เอา
/// คำตัดสินไปเตือนตรง ๆ จะฟ้อง "หักขาด §54" กับ (ก) บุคคลธรรมดาต่างประเทศ (ไม่อยู่ใต้ ม.70) และ (ข) บริษัทต่างประเทศที่มีสาขาในไทย
/// (หัก 3% ภ.ง.ด.53 ถูกต้อง) — คำเตือนที่ฟ้องใบถูก (F2 ข้อ 8) · ตัวนี้แยกขอบเขตก่อน แล้ว:</para>
/// <list type="bullet">
/// <item><see cref="ForeignPayeeWhtScope.Section70"/> ⇒ ข้อความของ <see cref="ForeignWhtRateResolver.RateWarning"/> ตรง ๆ (หักขาด §54 ดัง · <b>รวมอัตรา 0 = ไม่หักเลย</b>)</item>
/// <item><see cref="ForeignPayeeWhtScope.Individual"/> ⇒ เงียบ (ระบบไม่มีอัตราให้เทียบ — ไม่ใช่ ม.70)</item>
/// <item>ไม่รู้ (<see cref="ForeignPayeeWhtScope.ThaiRegisteredUnknown"/> · <see cref="ForeignPayeeWhtScope.KindUnknown"/>) + อัตราต่ำกว่า ม.70 ⇒
/// เตือนแบบ <b>บอกว่าไม่รู้</b> พร้อมเงื่อนไขที่ทำให้ถูก/ผิด (ไม่ประกาศว่าหักขาด)</item>
/// </list>
/// </summary>
public static class ForeignWhtPayeeCheck
{
    /// <summary>รหัสกติกาของคำเตือน "ผู้รับต่างประเทศ · ไม่หัก · ไม่จำแนกเงินได้" (คำตัดสินข้อ 130)</summary>
    public const string UnclassifiedRuleCode = "RD-70-UNCLASSIFIED";

    /// <summary>
    /// **คำเตือน (ไม่บล็อก) ของใบสำคัญจ่าย/ใบซื้อ/ค่าใช้จ่ายที่ผู้รับเป็นต่างประเทศ แต่ทั้งใบไม่หัก ณ ที่จ่ายและไม่จำแนกประเภทเงินได้เลย**
    /// (รอบ PP36 ทีม F2 · คำตัดสินข้อ 130 · PP36_REVIEW T-4b) — null = ไม่ต้องเตือน
    /// <para>═══ ที่มา ═══ ใบ Booking.com ค่าคอมมิชชั่น 5,908 คีย์มือ: ไม่มีบรรทัดไหนหักหรือมีรหัสเงินได้ ⇒ ตัวตรวจรายบรรทัด
    /// (<see cref="Warning"/>) ข้ามทุกบรรทัด ⇒ <b>เงียบ</b> ขณะที่ทางเข้ารอบโอน OTA ตั้ง 40(2) 15% ให้ (คำตัดสินข้อ 41) ⇒ สองทางเข้าคนละด่าน (R5)</para>
    /// <para>═══ ทำไมเตือนไม่บล็อก ═══ อนุสัญญาภาษีซ้อน (เช่น ไทย–เนเธอร์แลนด์ "กำไรธุรกิจ" เมื่อไม่มีสถานประกอบการถาวร) อาจทำให้ไม่ต้องหักจริง
    /// แต่ตาราง DTA ยังว่างจนที่ปรึกษาภาษียืนยัน (คำตัดสินข้อ 13) ⇒ ให้คนตัดสินพร้อมเอกสาร · อัตรามาจาก <see cref="ForeignWhtRateResolver"/> ตัวเดียว (ไม่พิมพ์ 15% ซ้ำ)</para>
    /// <para>บุคคลธรรมดาต่างประเทศ (ไม่อยู่ใต้ ม.70) ⇒ เงียบ (ทางเดียวกับ <see cref="Warning"/>) · มีบรรทัดหักหรือจำแนกแล้ว ⇒ เงียบ (ตัวตรวจรายบรรทัดรับช่วง) ·
    /// มีหนังสือรับรอง 50 ทวิ ของใบนี้แล้ว ⇒ เงียบ (ภาษีจัดการนอกบรรทัด — เช่นรอบโอน OTA)</para>
    /// </summary>
    public static string? UnclassifiedNoWithholdingWarning(ForeignPayeeWhtScope scope, string? payeeCountryIso2, DateTime paymentDate,
        bool anyLineWithheld, bool anyLineClassified, bool hasWhtCertificate)
    {
        if (anyLineWithheld || anyLineClassified || hasWhtCertificate) return null;
        if (scope == ForeignPayeeWhtScope.Individual) return null;
        var d = ForeignWhtRateResolver.Resolve(ForeignIncomeCategory.FeesCommission, payeeCountryIso2, ResidenceCertificate.None, paymentDate);
        var rate = d.RatePercent ?? ForeignWhtRateResolver.Section70GeneralRate;
        var scopeNote = scope switch
        {
            ForeignPayeeWhtScope.ThaiRegisteredUnknown =>
                " (ผู้รับมีเลขผู้เสียภาษีนิติบุคคลไทย — ถ้ามีสาขา/สถานประกอบการถาวรในไทย ให้หักอัตราในประเทศ ภ.ง.ด.53 แทน)",
            ForeignPayeeWhtScope.KindUnknown =>
                " (ระบบไม่รู้ว่าผู้รับเป็นนิติบุคคลหรือบุคคลธรรมดา — ม.70 ใช้กับนิติบุคคลต่างประเทศ · ระบุประเภทผู้ติดต่อที่หน้าผู้ติดต่อ)",
            _ => "",
        };
        return $"[{UnclassifiedRuleCode} · {d.LegalReference}] ผู้รับเงินต่างประเทศ — ใบนี้ไม่หัก ณ ที่จ่ายและยังไม่จำแนกประเภทเงินได้ · "
            + $"ถ้าเป็นเงินได้ 40(2) (ค่าธรรมเนียม/ค่านายหน้า/ค่าคอมมิชชั่น) ต้องหัก {rate:0.##}% ตาม ม.70 ยื่น ภ.ง.ด.54 "
            + "เว้นแต่มีอนุสัญญาภาษีซ้อน + หนังสือรับรองถิ่นที่อยู่ของผู้รับ (ส่วนที่ควรหักแต่ไม่หัก ผู้จ่ายรับผิดเอง §54)"
            + scopeNote
            + " · ทางไปต่อ: ระบุประเภทเงินได้/อัตราหัก ณ ที่จ่ายในบรรทัด (ระบบตรวจอัตราให้) หรือถ้าไม่ต้องหักจริง เก็บหลักฐานอนุสัญญาไว้แล้วกดรับทราบ";
    }

    /// <summary>ขอบเขตของผู้รับจากข้อมูลบนผู้ติดต่อ (pure)</summary>
    public static ForeignPayeeWhtScope ScopeOf(string? taxId, ContactType contactType, string? name)
    {
        if (ThaiTaxId.IsJuristic(taxId)) return ForeignPayeeWhtScope.ThaiRegisteredUnknown;
        return WhtPayeeKind.Detect(taxId, contactType, name) switch
        {
            true => ForeignPayeeWhtScope.Section70,
            false => ForeignPayeeWhtScope.Individual,
            null => ForeignPayeeWhtScope.KindUnknown,
        };
    }

    /// <summary>คำเตือนของหนังสือรับรอง 50 ทวิ แบบ ภ.ง.ด.54 ที่ออกด้วยมือ (ฝ่ายค้าน W-5) — ทุกแถวเทียบกับตัวตัดสิน ม.70 · ขึ้นต้นด้วยลำดับแถว</summary>
    /// <param name="lines">(รหัสประเภทเงินได้ · อัตราที่ใช้ · วันที่จ่าย) ต่อแถว</param>
    public static IReadOnlyList<string> CertificateWarnings(ForeignPayeeWhtScope scope, string? payeeCountryIso2,
        IReadOnlyList<(string? IncomeTypeCode, decimal TaxRate, DateTime PaymentDate)> lines)
    {
        var result = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var (code, rate, paid) = lines[i];
            var decision = ForeignWhtRateResolver.ResolveForIncomeCode(code, payeeCountryIso2, ResidenceCertificate.None, paid);
            if (Warning(scope, decision, rate) is string w)
                result.Add($"แถวที่ {i + 1}: {w}");
        }
        return result;
    }

    /// <summary>
    /// ข้อความเตือนของบรรทัด/หนังสือรับรอง 1 แถว — null = ไม่ต้องเตือน
    /// </summary>
    /// <param name="usedRatePercent">อัตราที่ใช้จริง (%) · 0 = ไม่ได้หัก (ยังตรวจ — "ไม่หักเลย" คือการหักขาดที่พบบ่อยที่สุด)</param>
    public static string? Warning(ForeignPayeeWhtScope scope, ForeignWhtDecision decision, decimal usedRatePercent)
    {
        switch (scope)
        {
            case ForeignPayeeWhtScope.Section70:
                return ForeignWhtRateResolver.RateWarning(decision, usedRatePercent);
            case ForeignPayeeWhtScope.Individual:
                return null;
            default:
                if (decision.RatePercent is not decimal expected || usedRatePercent >= expected) return null;
                var why = scope == ForeignPayeeWhtScope.ThaiRegisteredUnknown
                    ? "ผู้รับมีเลขผู้เสียภาษีนิติบุคคลไทย — ถ้ามีสาขา/สถานประกอบการถาวรในไทย การหักอัตราในประเทศลง ภ.ง.ด.53 ถูกต้อง · "
                      + "ถ้าแค่จด VAT e-Service (ไม่ได้ประกอบกิจการในไทย) ต้องหักตาม ม.70"
                    : "ระบบไม่รู้ว่าผู้รับเป็นนิติบุคคลหรือบุคคลธรรมดา (ระบุประเภทผู้ติดต่อที่หน้าผู้ติดต่อ) — ม.70 ใช้กับนิติบุคคลต่างประเทศเท่านั้น";
                return $"[{decision.RuleCode} · {decision.LegalReference}] ระบบตัดสินไม่ได้ว่าอัตรา {usedRatePercent:0.##}% ถูกไหม: {why} "
                    + $"(ถ้าอยู่ใต้ ม.70 ต้องหัก {expected:0.##}% — ส่วนที่ขาดผู้จ่ายรับผิดเอง §54)";
        }
    }
}
