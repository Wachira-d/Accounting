using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **"ผู้ใช้ตัดสิน · ระบบแนะนำ/อำนวย" ของงาน 50 ทวิ — ตัวตั้งข้อความ/รหัสกฎตัวเดียว** (คำสั่งเจ้าของ 2026-10-13: ระบบห้ามล็อกตาย ·
/// บล็อกเฉพาะที่กฎหมาย/ความถูกต้องของข้อมูลบังคับ และต้องมีทางไปต่อ · รอบ 204 ทีม D)
///
/// <para>รวมสามเรื่องที่เดิมเป็น "ล็อกตาย" หรือ "เปลี่ยนเงียบ": (1) งวดที่ยื่น/นำส่งแล้ว — ผู้มีสิทธิ์ยื่นภาษีรับทราบแล้วดำเนินการต่อได้
/// (<see cref="WhtFiledPeriodAck"/>) · (2) แบบ ภ.ง.ด. ที่ระบบแก้ให้ — บอกผู้ใช้ (<see cref="WhtCertFormCorrection"/>) ·
/// (3) ผู้ถูกหักไม่มีเลขผู้เสียภาษีตอนกด "สร้างใบ" เอง — ออกเป็นร่าง + คำเตือน (<see cref="WhtCertPayeeTaxIdDraft"/>)</para>
/// </summary>
public static class WhtFiledPeriodAck
{
    /// <summary>รหัสกฎของคำตอบ 409 "เปลี่ยนผู้ถูกหักแตะงวดที่ยื่น/นำส่งแล้ว — รับทราบแล้วไปต่อได้" (หน้าเว็บอ่านรหัสนี้แล้วเสนอช่องรับทราบ)</summary>
    public const string PayeeChangeNeedsAckRuleCode = "RD-50BIS-PAYEE-CHANGE-FILED";
    /// <summary>รหัสกฎของ audit "เปลี่ยนผู้ถูกหักทั้งที่งวดยื่นแล้ว (รับทราบ)"</summary>
    public const string PayeeChangeAckRuleCode = "RD-50BIS-PAYEE-CHANGE-FILED-ACK";
    /// <summary>รหัสกฎเดิมของ 409 "50 ทวิ อยู่ในแบบที่ยื่นแล้ว" (<c>WithholdingTaxCertService.VoidAsync</c>) — หน้าเว็บอ่านรหัสนี้แล้วเสนอช่องรับทราบ</summary>
    public const string VoidNeedsAckRuleCode = "RD-50TWI-FILED";
    /// <summary>รหัสกฎของ audit "ยกเลิก 50 ทวิ ที่ยื่นแล้ว (รับทราบ — ยื่นแบบเพิ่มเติม)"</summary>
    public const string VoidAckRuleCode = "RD-50TWI-FILED-ACK";
    public const string LegalReference = "ท.ป.4/2528 · ม.58/59 · การยื่นแบบเพิ่มเติม";

    /// <summary>ปัญหาของคำขอ "รับทราบ" (null = ใช้ได้): ต้องมีเหตุผล และผู้ขอต้องถือสิทธิ์ยื่นภาษี (Tax.File) — การยื่นแบบเพิ่มเติมเป็นงานของผู้ยื่น ·
    /// ไม่ได้ขอรับทราบ ⇒ null (ผู้เรียกบล็อกด้วยข้อความเดิม + <see cref="WayForward"/>)</summary>
    public static string? AckProblem(bool acknowledged, string? reason, bool holdsTaxFile)
    {
        if (!acknowledged) return null;
        if (!holdsTaxFile)
            return "การดำเนินการต่อในงวดที่ยื่น/นำส่งแล้วต้องให้ผู้มีสิทธิ์ยื่นภาษี (Tax.File) เป็นผู้รับทราบ — ให้ผู้มีสิทธิ์ยื่นภาษีทำรายการนี้";
        if (string.IsNullOrWhiteSpace(reason))
            return "ระบุเหตุผลที่ดำเนินการต่อในงวดที่ยื่นแล้ว (บันทึกลงประวัติ — ใช้อ้างอิงตอนยื่นแบบเพิ่มเติม)";
        return null;
    }

    /// <summary>ทางไปต่อที่ต่อท้ายข้อความ 409 เมื่อยังไม่ได้รับทราบ — ผู้ใช้ตัดสินได้เอง (ไม่ล็อกตาย)</summary>
    public const string WayForward =
        " · ถ้าต้องการดำเนินการต่อและจะยื่นแบบเพิ่มเติมเอง: ผู้มีสิทธิ์ยื่นภาษี (Tax.File) ติ๊ก \"รับทราบ — จะยื่นแบบเพิ่มเติม\" พร้อมเหตุผล "
        + "แล้วทำรายการอีกครั้ง (ระบบติดป้ายงวดที่ต้องยื่นเพิ่มเติมให้)";

    /// <summary>ป้ายงวดที่ต้องยื่นแบบเพิ่มเติม — "ต้องยื่น ภ.ง.ด.X เพิ่มเติม งวด MM/YYYY(พ.ศ.)"</summary>
    public static string SupplementaryFilingLabel(TaxType form, int year, int month)
        => $"ต้องยื่น {WhtUnissuedCertGate.FormLabel(form)} เพิ่มเติม งวด {month:00}/{year + 543}";

    /// <summary>ป้ายรวมหลายงวด (ไม่ซ้ำ · เรียงตามเวลา แล้วตามแบบ) — ว่าง ⇒ null</summary>
    public static string? SupplementaryFilingLabels(IEnumerable<(TaxType Form, int Year, int Month)> periods)
    {
        var labels = periods.Distinct().OrderBy(p => p.Year).ThenBy(p => p.Month).ThenBy(p => (int)p.Form)
            .Select(p => SupplementaryFilingLabel(p.Form, p.Year, p.Month)).ToList();
        return labels.Count == 0 ? null : string.Join(" · ", labels);
    }
}

/// <summary>แบบ ภ.ง.ด. ที่ระบบแก้จากที่ผู้ใช้เลือก (<c>WithholdingTaxCertService.ResolveWhtFormType</c>) — ข้อความที่ส่งกลับให้หน้าเว็บแจ้ง
/// (รอบ 204 ทีม D #21 · เดิมแก้เงียบแล้ว LogWarning อย่างเดียว) · แบบยังตัดสินจากผู้ถูกหัก+ประเภทเงินได้ตามเดิม (ภ.ง.ด.2 บังคับเฉพาะ ม.40(4))</summary>
public static class WhtCertFormCorrection
{
    public static string? Notice(TaxType? requested, TaxType resolved, string? reason)
    {
        if (requested is not TaxType from || from == resolved) return null;
        // ต้นทาง (ResolveWhtFormType) เป็นภาษาไทยแล้ว — ตัวแปลงนี้กันเหตุผลรุ่นเก่าที่ยังมีคำ "payee" ·
        // ภาษาไทยไม่เว้นวรรคระหว่างคำ: "payee เป็นนิติบุคคล" → "ผู้ถูกหักเป็นนิติบุคคล" (เดิมได้ "ผู้ถูกหัก เป็นนิติบุคคล")
        var why = string.IsNullOrWhiteSpace(reason) ? "" : " — " + reason
            .Replace("payee ", "ผู้ถูกหัก", StringComparison.Ordinal)
            .Replace("payee", "ผู้ถูกหัก", StringComparison.Ordinal).Trim();
        return $"ระบบเปลี่ยนแบบยื่นจาก {WhtUnissuedCertGate.FormLabel(from)} เป็น {WhtUnissuedCertGate.FormLabel(resolved)}{why} "
               + "(แบบ ภ.ง.ด. ตัดสินจากผู้ถูกหักและประเภทเงินได้) · ถ้าไม่ถูก ให้แก้เลขผู้เสียภาษี/ประเภทของผู้ติดต่อ หรือประเภทเงินได้ แล้วบันทึกใบนี้อีกครั้ง";
    }
}

/// <summary>ผู้ถูกหักไม่มีเลขผู้เสียภาษี (<see cref="WhtCertIssuePlan.PayeeTaxIdProblem"/>) ตอนผู้ใช้กด "สร้างใบ" เอง — ออกเป็นฉบับร่าง
/// (ไม่เข้าแบบยื่น) + คำเตือน + audit แทนการปฏิเสธ (รอบ 204 ทีม D #20) · ทางอัตโนมัติยังล้มดังเข้าลิสต์รอออกใบตามเดิม</summary>
public static class WhtCertPayeeTaxIdDraft
{
    public const string Warning = "ใบร่าง — เติมเลขผู้เสียภาษีก่อนออก/ยื่น";

    /// <summary>ออกเป็นร่างแทนการปฏิเสธไหม — เฉพาะคำขอของคนที่ไม่ได้ขอให้ออกทันที (ออกทันที = ใบเข้าแบบยื่น ต้องมีเลข)</summary>
    public static bool DraftInstead(string? payeeProblem, bool manualRequest, bool autoIssue)
        => payeeProblem != null && manualRequest && !autoIssue;

    public static string WarningFor(string payeeProblem) => $"{Warning}: {payeeProblem}";

    // ═══ ฝ่ายค้านรอบ 204 L5 — "ออก" ร่างที่ไม่มีเลขผู้เสียภาษีภายหลัง (WithholdingTaxCertService.IssueAsync) ═══
    // เดิมไม่ตรวจซ้ำ ⇒ ร่างจาก #20 (หรือใบที่สร้างเองโดยไม่มีเลข) กด "ออก" แล้วเข้าแบบยื่นทั้งที่ไม่มีเลข ⇒ ยื่น ภ.ง.ด. อิเล็กทรอนิกส์ไม่ผ่าน ·
    // คำสั่งเจ้าของ (ข้อ 150) ⇒ ไม่ล็อกตาย: เตือน + ผู้ใช้รับทราบแล้วออกได้ (audit) · ไม่รับทราบ ⇒ 409 พร้อมทางไปต่อ

    /// <summary>รหัสกฎของ 409 "ออกใบโดยผู้ถูกหักไม่มีเลขผู้เสียภาษี — ต้องรับทราบ" (หน้าเว็บอ่านรหัสนี้แล้วถามรับทราบ)</summary>
    public const string IssueNeedsAckRuleCode = "RD-50BIS-ISSUE-NO-TAXID";
    /// <summary>รหัสกฎของ audit "ออกใบโดยไม่มีเลขผู้เสียภาษี (รับทราบ)"</summary>
    public const string IssueAckRuleCode = "RD-50BIS-ISSUE-NO-TAXID-ACK";
    public const string IssueLegalReference = "ท.ป.4/2528 · แบบ ภ.ง.ด. ต้องมีเลขประจำตัวผู้เสียภาษีของผู้ถูกหัก";

    /// <summary>ทางไปต่อที่ต่อท้ายข้อความ 409</summary>
    public const string IssueWayForward =
        " · ทางไปต่อ (แนะนำ): เติมเลขผู้เสียภาษีที่ข้อมูลผู้ติดต่อ แล้วกด \"ออก\" อีกครั้ง · ถ้าต้องออกใบให้ผู้ถูกหักก่อน: ติ๊ก \"รับทราบ — ออกโดยไม่มีเลขผู้เสียภาษี\" "
        + "(ใบนี้จะเข้าแบบ ภ.ง.ด. ของงวด — การยื่นทางอิเล็กทรอนิกส์จะไม่ผ่านจนกว่าจะเติมเลข)";

    /// <summary>คำเตือนในคำตอบเมื่อออกแบบรับทราบ</summary>
    public static string IssuedWithoutTaxIdWarning(string payeeProblem)
        => $"ออกแล้วโดยผู้ใช้รับทราบว่าไม่มีเลขผู้เสียภาษี: {payeeProblem} — เติมเลขก่อนยื่นแบบ ภ.ง.ด.";
}
