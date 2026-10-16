namespace Accounting.Helpers;

/// <summary>ผลการตัดสินของ <see cref="YearEndReopenRule.Evaluate"/> — เรียงตามลำดับที่ด่านตรวจ</summary>
public enum YearEndReopenOutcome
{
    /// <summary>ยกเลิกการปิดปีได้</summary>
    Allowed = 0,
    /// <summary>ปีนี้ยังไม่ได้ปิด (หรือถูกยกเลิกไปแล้ว — สองคนกดพร้อมกัน คนที่สองเห็นผลนี้)</summary>
    NotClosed = 1,
    /// <summary>มีปีหลังจากนี้ที่ปิดแล้ว — ยอดยกมาของปีถัดไปคิดจากปีนี้ ⇒ ต้องเปิดปีล่าสุดก่อน</summary>
    LaterYearClosed = 2,
    /// <summary>การปิดปีนี้ (โค้ดรุ่นเก่า) ยกยอดไป ม.ค. ที่ยังไม่ซ่อม — ยกเลิกแล้วจะไม่มีปีที่ปิดให้ปุ่มซ่อมใช้</summary>
    LegacyRollUnrepaired = 3,
    /// <summary>ไม่ระบุเหตุผล</summary>
    ReasonRequired = 4,
    /// <summary>ยื่น ภ.ง.ด.50 / งบการเงิน DBD ของปีนี้แล้ว — ต้องรับทราบว่าต้องยื่นเพิ่มเติม/งบการเงินฉบับแก้ไข</summary>
    FiledNeedsAcknowledgement = 5,
}

/// <summary>แบบที่ยื่นแล้วของปีที่จะยกเลิกการปิด (<c>ComplianceFiling</c> ที่ <see cref="YearEndReopenRule.IsFiledReturn"/>) — อ้างใน audit + หน้าจอ</summary>
public sealed record YearEndFiledReturn(
    Guid FilingId, string FilingType, string FormCode, int Year, int? Month, string Status,
    DateTime? FiledDate, string? SubmissionReference, string? ConfirmationNumber);

/// <summary>ข้อเท็จจริงที่ผู้เรียก (บริการ) โหลดมาให้ตัวตัดสิน — ตัวตัดสินไม่แตะฐานข้อมูล</summary>
public sealed record YearEndReopenFacts(
    int FiscalYear,
    bool IsClosed,
    IReadOnlyCollection<int> LaterClosedYears,
    int UnrepairedLegacyRolledRows,
    IReadOnlyList<YearEndFiledReturn> FiledReturns,
    string? Reason,
    bool AcknowledgeFiled);

public sealed record YearEndReopenDecision(YearEndReopenOutcome Outcome, string? Message, IReadOnlyList<YearEndFiledReturn> FiledReturns)
{
    public bool Allowed => Outcome == YearEndReopenOutcome.Allowed;
}

/// <summary>
/// **ยกเลิกการปิดปี — ตัวตัดสินตัวเดียว + ข้อความไทยทุกข้อ** (คำสั่งเจ้าของ 2026-10-16 · DECISIONS ข้อ 152)
///
/// <para><b>ที่มา:</b> ปิดปีแล้วพบรายการผิด (ก่อนยื่นงบ/ภ.ง.ด.50) ไม่มีทางเปิดปีกลับมาแก้ — ใบปิดปีถูกล็อก (<see cref="ClosingEntryLock"/>) และงวดทั้งปีถูกล็อก
/// (<c>ReopenPeriodAsync</c> ปฏิเสธงวด Locked) ⇒ ทางเดียวคือใบปรับปรุงกำไรสะสมในปีถัดไป ซึ่งทำให้งบของปีที่ยังไม่ยื่นไม่ถูกแก้</para>
///
/// <para><b>กติกา (คำตอบเจ้าของ 4 ข้อ):</b>
/// <list type="number">
/// <item>ยื่น ภ.ง.ด.50 หรืองบการเงิน DBD ของปีนั้นแล้ว (<c>ComplianceFiling</c> <c>RD_CIT</c>/<c>DBD_FinancialStatement</c> สถานะ Filed/Accepted) ⇒
///   ยกเลิกได้เฉพาะเมื่อรับทราบ — ไม่รับทราบ = บล็อกพร้อมข้อความว่าต้องยื่นแบบเพิ่มเติม/งบการเงินฉบับแก้ไข · เลขอ้างอิงแบบที่ยื่นลง audit</item>
/// <item>ไม่มีช่องวันประชุมผู้ถือหุ้น</item>
/// <item>หลังยกเลิก ทุกงวดของปีนั้นกลับเป็น "เปิด"</item>
/// <item>เจ้าของบริษัทเท่านั้น (ด่าน <c>EnsureOwnerAccessAsync</c> ที่ controller)</item>
/// </list>
/// และ: ต้องเป็นปีล่าสุดที่ปิด (ปีหลังจากนั้นปิดแล้ว ⇒ "เปิดปีล่าสุดก่อน") · การปิดปีนี้ยกยอดรุ่นเก่าไป ม.ค. ที่ยังไม่ซ่อม ⇒ "ซ่อมยอดยกมารุ่นเก่าก่อน"
/// (ยกเลิกแล้วปีนี้ไม่ปิด ⇒ ปุ่มซ่อมของปีถัดไปใช้ไม่ได้ · แถว ม.ค. ค้างเป็นฐานของงบทดลองต่อ) · เหตุผลห้ามว่าง</para>
///
/// <para>pure · ไม่มี I/O — บริการ <c>AccountingService.ReopenYearEndAsync</c> โหลดข้อเท็จจริงใต้ล็อก <see cref="OpeningRoll.LockKey"/> แล้วเรียกตัวนี้ก่อนเขียนอะไร</para>
/// </summary>
public static class YearEndReopenRule
{
    public const string RuleCode = "GL-YEAR-END-REOPEN";

    public const string LegalReference =
        "พ.ร.บ.การบัญชี 2543 ม.11 · TFRS for NPAEs บทที่ 5 (การแก้ไขข้อผิดพลาด) · ป.รัษฎากร ม.65 (ภ.ง.ด.50)";

    /// <summary>action ของ audit ทุกแถวที่การยกเลิกการปิดปีเขียน</summary>
    public const string AuditAction = "year-end-reopen";

    /// <summary>ป้ายของ "ใบปรับปรุงกำไรสะสม" ที่ระบบแนะนำเมื่อแก้ปีที่ปิดโดยไม่เปิดปี (Option C — ป้ายอย่างเดียว ไม่มีงบแสดงการเปลี่ยนแปลงส่วนของเจ้าของ)</summary>
    public const string PriorPeriodErrorLabel = "แก้ไขข้อผิดพลาดงวดก่อน (TFRS for NPAEs บทที่ 5)";

    /// <summary>ชนิดแบบที่ "ยื่นแล้ว" ทำให้ต้องรับทราบ — ภ.ง.ด.50/51 (สรรพากร) และงบการเงิน (DBD)</summary>
    public static readonly IReadOnlyList<string> FilingTypes = new[] { "RD_CIT", "DBD_FinancialStatement" };

    /// <summary>แถว <c>ComplianceFiling</c> นี้คือแบบของปีที่ยื่นแล้ว (Filed/Accepted) และเกี่ยวกับงบปีนั้น</summary>
    public static bool IsFiledReturn(string? filingType, string? status)
        => filingType != null && FilingTypes.Contains(filingType) && status is "Filed" or "Accepted";

    /// <summary>เหตุผลหลังตัดช่องว่าง (ว่าง = null)</summary>
    public static string? NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

    public static YearEndReopenDecision Evaluate(YearEndReopenFacts f)
    {
        var filed = f.FiledReturns ?? Array.Empty<YearEndFiledReturn>();
        if (!f.IsClosed)
            return new(YearEndReopenOutcome.NotClosed, NotClosedMessage(f.FiscalYear), filed);
        var later = (f.LaterClosedYears ?? Array.Empty<int>()).Where(y => y > f.FiscalYear).OrderBy(y => y).ToList();
        if (later.Count > 0)
            return new(YearEndReopenOutcome.LaterYearClosed, LaterYearClosedMessage(f.FiscalYear, later), filed);
        if (f.UnrepairedLegacyRolledRows > 0)
            return new(YearEndReopenOutcome.LegacyRollUnrepaired, LegacyRollMessage(f.FiscalYear, f.UnrepairedLegacyRolledRows), filed);
        if (NormalizeReason(f.Reason) == null)
            return new(YearEndReopenOutcome.ReasonRequired, ReasonRequiredMessage(f.FiscalYear), filed);
        if (filed.Count > 0 && !f.AcknowledgeFiled)
            return new(YearEndReopenOutcome.FiledNeedsAcknowledgement, FiledNeedsAcknowledgementMessage(f.FiscalYear, filed), filed);
        return new(YearEndReopenOutcome.Allowed, null, filed);
    }

    /// <summary>HTTP status ของแต่ละผล — ต้องรับทราบ = 409 (หน้าเว็บถามแล้วส่งใหม่) · ไม่พบปีที่ปิด = 404 · ที่เหลือ = 400</summary>
    internal static int StatusCodeFor(YearEndReopenOutcome outcome) => outcome switch
    {
        YearEndReopenOutcome.FiledNeedsAcknowledgement => 409,
        YearEndReopenOutcome.NotClosed => 404,
        _ => 400,
    };

    private static string Be(int year) => (year + 543).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>ชื่อแบบที่ผู้ใช้อ่านออก (FormCode ของระบบ → ชื่อไทย)</summary>
    public static string FilingLabel(YearEndFiledReturn r) => r.FilingType switch
    {
        "RD_CIT" when r.FormCode is "PND51" or "ภ.ง.ด.51" => "ภ.ง.ด.51",
        "RD_CIT" => "ภ.ง.ด.50",
        "DBD_FinancialStatement" => "งบการเงินยื่นกรมพัฒนาธุรกิจการค้า (ส.บช.3)",
        _ => string.IsNullOrWhiteSpace(r.FormCode) ? r.FilingType : r.FormCode,
    };

    /// <summary>เลขอ้างอิงของแบบ (เลขรับ/เลขยืนยัน) — ว่าง = "ไม่มีเลขอ้างอิง"</summary>
    public static string FilingRef(YearEndFiledReturn r)
        => !string.IsNullOrWhiteSpace(r.ConfirmationNumber) ? r.ConfirmationNumber!.Trim()
            : !string.IsNullOrWhiteSpace(r.SubmissionReference) ? r.SubmissionReference!.Trim()
            : "ไม่มีเลขอ้างอิง";

    private static string FiledList(IEnumerable<YearEndFiledReturn> filed)
        => string.Join(" · ", filed.Select(r => $"{FilingLabel(r)} ({r.Status}"
            + (r.FiledDate is DateTime d ? $" {ThaiDate.ToThaiDisplayString(d)}" : "") + $" · เลขอ้างอิง {FilingRef(r)})"));

    private static string NotClosedMessage(int fiscalYear)
        => $"ปีบัญชี {Be(fiscalYear)} ยังไม่ได้ปิด (หรือถูกยกเลิกการปิดไปแล้ว) — ไม่มีอะไรต้องยกเลิก · ยังไม่มีอะไรถูกบันทึก · ทางไปต่อ: โหลดหน้างวดบัญชีใหม่เพื่อดูสถานะล่าสุด";

    public static string LaterYearClosedMessage(int fiscalYear, IReadOnlyCollection<int> laterClosedYears)
        => $"ยกเลิกการปิดปี {Be(fiscalYear)} ไม่ได้: ปีหลังจากนั้นปิดแล้ว ({string.Join(", ", laterClosedYears.OrderBy(y => y).Select(Be))}) — "
            + "ยอดยกมาของปีถัดไปคิดจากปีนี้ ถ้าเปิดปีนี้ก่อน ยอดยกมาของปีที่ปิดแล้วจะไม่ตรงกับงบของปีนี้ · ยังไม่มีอะไรถูกบันทึก · "
            + $"ทางไปต่อ: เปิดปีล่าสุดก่อน (ยกเลิกการปิดปี {Be(laterClosedYears.Max())} แล้วไล่ย้อนลงมาทีละปี)";

    private static string LegacyRollMessage(int fiscalYear, int rows)
        => $"ยกเลิกการปิดปี {Be(fiscalYear)} ไม่ได้: การปิดปีนี้ (ระบบรุ่นเก่า) ยกยอดยกมาไปงวด ม.ค. ทั้งที่รอบบัญชีไม่ใช่ปีปฏิทิน ({rows} แถว) และยังไม่ได้ซ่อม — "
            + "ถ้ายกเลิกตอนนี้ ปุ่มซ่อมของปีถัดไปจะใช้ไม่ได้ (ต้องมีปีที่ปิด) และแถว ม.ค. จะค้างเป็นฐานของงบทดลอง · ยังไม่มีอะไรถูกบันทึก · "
            + $"ทางไปต่อ: ซ่อมยอดยกมารุ่นเก่าก่อน (หน้า \"ตรวจข้อมูลจากบั๊กที่แก้แล้ว\" หมวดยอดยกมาจากการปิดปี กด \"แก้\" ปี {Be(fiscalYear + 1)}) แล้วยกเลิกการปิดปีอีกครั้ง";

    private static string ReasonRequiredMessage(int fiscalYear)
        => $"กรุณาระบุเหตุผลที่ยกเลิกการปิดปี {Be(fiscalYear)} (บันทึกในประวัติการแก้ไข — ผู้สอบบัญชี/สรรพากรตรวจย้อนได้) · ยังไม่มีอะไรถูกบันทึก";

    private static string FiledNeedsAcknowledgementMessage(int fiscalYear, IReadOnlyCollection<YearEndFiledReturn> filed)
        => $"ปีบัญชี {Be(fiscalYear)} ยื่นแบบแล้ว: {FiledList(filed)} — ถ้ายกเลิกการปิดปีแล้วแก้ตัวเลข ต้องยื่นแบบ ภ.ง.ด.50 เพิ่มเติม (ป.รัษฎากร ม.65) "
            + "และ/หรือยื่นงบการเงินฉบับแก้ไขต่อกรมพัฒนาธุรกิจการค้า (ผู้สอบบัญชีต้องลงความเห็นใหม่) · ยังไม่มีอะไรถูกบันทึก · "
            + "ทางไปต่อ: ถ้าต้องการแก้จริง ติ๊ก \"รับทราบว่าต้องยื่นแบบเพิ่มเติม/งบการเงินฉบับแก้ไข\" แล้วกดยืนยันอีกครั้ง (เลขอ้างอิงแบบที่ยื่นจะถูกบันทึกในประวัติ) · "
            + $"ถ้าแค่พบข้อผิดพลาดหลังยื่น: บันทึกใบสำคัญปรับปรุงกำไรสะสม — {PriorPeriodErrorLabel} — ในงวดที่ยังเปิดของปีถัดไป โดยไม่ต้องเปิดปี";

    /// <summary>ทางไปต่อเมื่อแก้ปีที่ปิดแล้ว (ใช้ร่วม: ด่านเปิดงวดเดี่ยว · ด่านใบปิดปี <see cref="ClosingEntryLock"/>)</summary>
    public static string WayForward
        => "ทางไปต่อ: ถ้ายังไม่ได้ยื่นงบ/ภ.ง.ด.50 ของปีนั้น — เจ้าของบริษัทกด \"ยกเลิกการปิดปี\" ที่หน้างวดบัญชี (แถวปีที่ปิดแล้ว · ทุกงวดของปีกลับเป็นเปิด แก้แล้วปิดปีใหม่) · "
            + $"ถ้ายื่นแล้วและแค่พบข้อผิดพลาด: บันทึกใบสำคัญปรับปรุงกำไรสะสม — {PriorPeriodErrorLabel} — ในงวดที่ยังเปิดของปีถัดไป";

    /// <summary>ข้อความของ "เปิดงวดเดี่ยวที่ถูกล็อกจากการปิดปี" (<c>ReopenPeriodAsync</c>)</summary>
    public static string LockedPeriodMessage(string periodName)
        => $"งวด {periodName} ถูกล็อกจากการปิดปี — เปิดงวดเดี่ยวไม่ได้ (ใบปิดปีโอนกำไรของทั้งปีเข้ากำไรสะสมแล้ว) · ยังไม่มีอะไรถูกบันทึก · {WayForward}";

    /// <summary>ผลของการยกเลิกการปิดปี (ข้อความที่หน้ายืนยันแสดง — เซิร์ฟเวอร์เป็นเจ้าของ)</summary>
    public static IReadOnlyList<string> Effects(int fiscalYear)
        => new[]
        {
            $"กลับรายการใบปิดบัญชีสิ้นปี {Be(fiscalYear)} ด้วยใบกลับรายการลงวันเดียวกัน (ใบเดิมและใบกลับยังอยู่ — ลบ/กลับรายการซ้ำไม่ได้) ⇒ รายได้/ค่าใช้จ่ายของปีกลับมาเป็นยอดคงเหลือ",
            $"ยกเลิกยอดยกมาที่การปิดปีเขียนไว้ในงวดแรกของปี {Be(fiscalYear + 1)} (ยอดยกมาที่นำเข้าจากระบบเดิมไม่ถูกแตะ)",
            $"ทุกงวดของปี {Be(fiscalYear)} กลับเป็น \"เปิด\" (รวมงวดที่ปิดชั่วคราวไว้ก่อนปิดปี) — แบบภาษีรายเดือนที่ยื่นแล้ว (ภ.พ.30/ภ.ง.ด.) ยังล็อกเอกสารของเดือนนั้นตามเดิม",
            "แก้รายการแล้วปิดปีอีกครั้งได้ตามปกติ — ระบบสร้างใบปิดปีและยกยอดใหม่จากตัวเลขที่แก้แล้ว",
            "บันทึกประวัติทุกแถวที่เปลี่ยน พร้อมเหตุผลและเลขอ้างอิงแบบที่ยื่นแล้ว (ถ้ามี) · ถ้านี่เป็นปีเดียวที่ปิด จะเปลี่ยนเดือนเริ่มรอบบัญชีได้อีกครั้ง",
        };
}

/// <summary>ด่านยกเลิกการปิดปีไม่ผ่าน — ผล + แบบที่ยื่นแล้ว (controller ตอบ 409 พร้อม <c>filedReturns</c> ให้หน้าเว็บขอรับทราบ)</summary>
public sealed class YearEndReopenException : BusinessRuleException
{
    public YearEndReopenOutcome Outcome { get; }
    public IReadOnlyList<YearEndFiledReturn> FiledReturns { get; }

    public YearEndReopenException(YearEndReopenDecision decision)
        : base(decision.Message ?? "ยกเลิกการปิดปีไม่ได้", YearEndReopenRule.RuleCode, YearEndReopenRule.StatusCodeFor(decision.Outcome))
    {
        Outcome = decision.Outcome;
        FiledReturns = decision.FiledReturns;
    }
}
