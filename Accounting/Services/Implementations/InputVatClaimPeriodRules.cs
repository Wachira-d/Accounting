using Accounting.Models.Enums;

namespace Accounting.Services.Implementations;

/// <summary>ผลการตัดสิน "ตั้ง/ย้ายงวดเคลมภาษีซื้อ (ภ.พ.30)"</summary>
internal enum InputVatClaimPeriodOutcome
{
    /// <summary>ไม่มีอะไรเปลี่ยน — ห้ามยิง guard ใด ๆ (no-op ต้องเป็น no-op เสมอ)</summary>
    NoChange,
    /// <summary>เปลี่ยนไม่ได้ — <see cref="InputVatClaimPeriodDecision.BlockReason"/> บอกเหตุผลถึงผู้ใช้</summary>
    Blocked,
    /// <summary>เปลี่ยนได้ — เขียน <see cref="InputVatClaimPeriodDecision.Period"/> ลงเอกสาร
    /// (null = กลับไปเคลมตามเดือนภาษีของเอกสาร)</summary>
    Apply,
}

internal readonly record struct InputVatClaimPeriodDecision(
    InputVatClaimPeriodOutcome Outcome,
    DateTime? Period,
    string? BlockReason)
{
    public static InputVatClaimPeriodDecision NoChange()
        => new(InputVatClaimPeriodOutcome.NoChange, null, null);
    public static InputVatClaimPeriodDecision Blocked(string reason)
        => new(InputVatClaimPeriodOutcome.Blocked, null, reason);
    public static InputVatClaimPeriodDecision Apply(DateTime? period)
        => new(InputVatClaimPeriodOutcome.Apply, period, null);
}

/// <summary>
/// กติกา "ใบนี้จะเคลมภาษีซื้อในงวด ภ.พ.30 ไหน" — <b>ตัวตัดสินกลางตัวเดียวของทั้งระบบ</b>
///
/// <para>มีทางเข้าที่ผู้ใช้ตั้งงวดได้ 3 ทาง (สร้างเอกสาร · แก้ไขเอกสาร ·
/// แผงภาษีซื้อในหน้าดูเอกสาร) — ทั้งสามต้องได้ผลลัพธ์เดียวกันเป๊ะสำหรับ input
/// เดียวกัน ไม่งั้นผู้ใช้จะเจอ "ทางนี้ทำได้ ทางนั้นไม่ได้" บนใบใบเดียวกัน.
/// แยกออกมาเป็น pure function เพราะเป็นตรรกะภาษี (กฎเหล็ก #4 ข้อ G) — เทสต์ได้
/// โดยไม่ต้องมี DB และเป็นหลักฐานว่าทุกทางใช้ตารางตัดสินใจชุดเดียวกันจริง</para>
///
/// <para>แบ่ง 2 ขั้นตามข้อมูลที่ต้องใช้: ขั้นแรกตัดสินจากตัวเอกสารล้วน ๆ
/// (ไม่ต้อง query) — ส่วนใหญ่จบตั้งแต่ขั้นนี้; ขั้นสองต้องรู้ว่าใบอยู่ในรายงาน
/// งวดไหนแล้ว จึงค่อย query ต่อเมื่อผ่านขั้นแรก</para>
/// </summary>
internal static class InputVatClaimPeriodRules
{
    /// <summary>ตั้งงวดเคลมได้เฉพาะเอกสารฝั่งซื้อ — ฝั่งขายไม่มีภาษีซื้อให้เคลม</summary>
    internal static bool IsPurchaseDocType(DocumentType t)
        => t is DocumentType.PurchaseInvoice or DocumentType.Expense
            or DocumentType.PaymentVoucher or DocumentType.CertificateInLieu;

    /// <summary>แปลงค่าที่ผู้ใช้ส่งมาเป็นงวด (วันที่ 1 ของเดือน, UTC).
    /// <c>""</c>/ช่องว่าง = ล้างกลับไปใช้เดือนภาษีของเอกสาร → คืน null + ok=true.
    /// รับปี พ.ศ. ด้วย (ผู้ใช้ไทยพิมพ์ <c>2569-09</c> ได้) — ปี &gt; 2400 = พ.ศ.</summary>
    internal static (bool Ok, DateTime? Period, string? Error) ParsePeriod(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (true, null, null);

        var parts = raw.Trim().Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var y) || !int.TryParse(parts[1], out var m)
            || m < 1 || m > 12)
            return (false, null, "รูปแบบงวดไม่ถูกต้อง — ต้องเป็น yyyy-MM เช่น 2026-09");

        // ⚠️ แปลง พ.ศ. → ค.ศ. **ก่อน** ตรวจช่วงปี — เดิมตรวจ y > 2200 ก่อน
        // ⇒ ปี พ.ศ. ทุกค่า (2569 ฯลฯ) ถูกปฏิเสธตั้งแต่บรรทัดแรก บรรทัดแปลง
        // ข้างล่างไม่มีวันถูกเรียกถึงเลย = "รองรับ พ.ศ." ที่ไม่เคยทำงานจริง
        if (y > 2400) y -= 543;
        if (y < 2000 || y > 2200)
            return (false, null, "รูปแบบงวดไม่ถูกต้อง — ต้องเป็น yyyy-MM เช่น 2026-09");

        return (true, new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc), null);
    }

    /// <summary>ขั้นที่ 1 — ตัดสินจากตัวเอกสาร (ไม่ต้องแตะฐานข้อมูล)
    ///
    /// <para><b>ลำดับสำคัญมาก</b>: แปลงค่า + เทียบของเดิม <b>ก่อน</b> guard ทุกตัว.
    /// ที่มา (บั๊กจริง): UI ส่ง <c>""</c> มาเสมอตอนแก้ไขเพื่อรองรับการล้างค่า —
    /// เวอร์ชันแรกเช็ค "ต้องเป็นเอกสารฝั่งซื้อ" ก่อนดูว่าค่าเปลี่ยนจริงไหม ⇒ แก้
    /// ใบเสนอราคาที่ไม่เกี่ยวอะไรเลยก็ throw ("clone ใบเสนอราคาแล้วบันทึกไม่ได้")
    /// และ throw ใส่ใบพัก 11640 ที่ผู้ใช้ไม่ได้แตะช่องนี้ด้วย.
    /// <b>no-op ต้องเป็น no-op เสมอ — guard มีไว้กันการ "เปลี่ยน" เท่านั้น</b></para>
    ///
    /// <param name="raw">null = ไม่ได้ส่งมา (ไม่แตะ) · "" = ล้าง · "yyyy-MM" = งวด</param>
    /// <param name="currentBecameClaimableAt">ค่าปัจจุบันบนเอกสาร (เทียบระดับเดือน)</param>
    /// <param name="postedAsUndue">เคยลงภาษีซื้อเป็น "ยังไม่ถึงกำหนด" (11640) หรือไม่</param>
    /// </summary>
    internal static InputVatClaimPeriodDecision DecideFromDocument(
        string? raw, DocumentType docType,
        DateTime? currentBecameClaimableAt, bool postedAsUndue)
    {
        if (raw == null) return InputVatClaimPeriodDecision.NoChange();   // ไม่ได้ส่งมา = ไม่แตะ

        var (ok, period, error) = ParsePeriod(raw);
        if (!ok) return InputVatClaimPeriodDecision.Blocked(error!);

        var current = currentBecameClaimableAt.HasValue
            ? new DateTime(currentBecameClaimableAt.Value.Year, currentBecameClaimableAt.Value.Month, 1)
            : (DateTime?)null;
        if (period == current) return InputVatClaimPeriodDecision.NoChange();

        if (!IsPurchaseDocType(docType))
            return InputVatClaimPeriodDecision.Blocked("งวดเคลมภาษีซื้อตั้งได้เฉพาะเอกสารฝั่งซื้อ");

        // กติกา 2 — flow 11640 ชนะเจตนา: ใบที่ยังพักภาษีซื้ออยู่ (ใบกำกับไม่ครบ
        // §86/4) งวดเคลมถูกกำหนดตอน "เติมใบกำกับครบ" เท่านั้น ตั้งเองไม่ได้
        // (ตั้งได้ = รายงานมองว่าถึงกำหนดแล้วทั้งที่ยังไม่มีสิทธิ์เคลม)
        if (postedAsUndue && currentBecameClaimableAt == null)
            return InputVatClaimPeriodDecision.Blocked(
                "ใบนี้พักภาษีซื้อไว้ (ใบกำกับยังไม่ครบ §86/4) — งวดเคลมจะถูกกำหนด"
                + "อัตโนมัติเมื่อกด \"เติมใบกำกับครบ\" ไม่สามารถเลือกงวดเองที่นี่ได้");

        // กติกา 2.5 — ใบ undue ที่ย้ายเข้า 11610 แล้ว "ล้างงวด" ไม่ได้ (ย้ายได้
        // ล้างไม่ได้): BecameClaimableAt ของใบพวกนี้คือหลักฐานว่า reclassify
        // เกิดแล้ว ถ้าตั้งกลับเป็น null รายงานจะเห็นเป็น "ยังพัก 11640" ทั้งที่
        // GL ย้ายออกไปแล้ว → ภ.พ.30 กับ GL แยกทางกันเงียบ ๆ
        if (period == null && postedAsUndue)
            return InputVatClaimPeriodDecision.Blocked(
                "ใบนี้เคยพักภาษีซื้อ (11640) แล้วย้ายเข้า ภ.พ.30 — ล้างงวดกลับเป็น"
                + "ค่าปกติไม่ได้ (เลือกงวดใหม่ได้ แต่ต้องระบุงวดเสมอ)");

        return InputVatClaimPeriodDecision.Apply(period);
    }

    /// <summary>ขั้นที่ 2 — ตัดสินหลังรู้ว่าใบอยู่ในรายงาน ภ.พ.30 งวดไหนแล้ว
    ///
    /// <para>เรียกเฉพาะเมื่อขั้นที่ 1 คืน <see cref="InputVatClaimPeriodOutcome.Apply"/>
    /// ลำดับในนี้ก็สำคัญ: เช็ค "งวดที่ยื่นแล้ว" <b>ก่อน</b> §82/3 — ใบที่อยู่ใน
    /// งวดที่ยื่นแล้วต้องได้ข้อความ "ต้องยื่นเพิ่มเติม" เสมอ ไม่ใช่ข้อความเรื่อง
    /// กรอบ 6 เดือนซึ่งชี้ทางแก้ผิด</para>
    ///
    /// <param name="claimBasisDate">ฐานนับกรอบ §82/3 = วันที่ใบกำกับผู้ขาย ?? วันที่เอกสาร</param>
    /// <param name="filedReportPeriod">งวดที่ <b>ยื่น/นำส่งแล้ว</b> ซึ่งใบนี้ถูกใช้อยู่ (null = ไม่มี)</param>
    /// </summary>
    internal static InputVatClaimPeriodDecision DecideAgainstReports(
        DateTime? period, DateTime claimBasisDate, (int Month, int Year)? filedReportPeriod)
    {
        if (filedReportPeriod is { } filed)
            return InputVatClaimPeriodDecision.Blocked(
                $"ใบนี้อยู่ในรายงานภาษีซื้องวด {filed.Month:D2}/{filed.Year} "
                + "ที่**ยื่นแล้ว** — ย้ายงวดไม่ได้ ต้องยื่นแบบเพิ่มเติมกับสรรพากร");

        if (period.HasValue)
        {
            // §82/3 — ตัวตัดสินกลางเดียวกับปุ่ม "ดึงเอกสาร" ในหน้ารายงาน:
            // สิ่งที่ "ตั้งได้" ที่นี่ = สิ่งที่ "ดึงได้" ที่นั่น ไม่มีวันขัดกัน
            var (ok, reason) = TaxService.EvaluateClaimPeriod(
                claimBasisDate, isInput: true, period.Value.Year, period.Value.Month);
            if (!ok) return InputVatClaimPeriodDecision.Blocked(reason!);
        }

        return InputVatClaimPeriodDecision.Apply(period);
    }

    /// <summary>ข้อความร่องรอยที่ต่อหน้าบรรทัดในรายงานงวดเดิมตอนถูกติ๊กออก —
    /// นักบัญชีเปิดรายงานเก่าแล้วต้องรู้ทันทีว่าใบหลุดไปไหน</summary>
    internal static string MoveAuditReason(DateTime? period)
        => period.HasValue
            ? $"ย้ายงวดเคลม → {period.Value.Month:D2}/{period.Value.Year}"
            : "ย้ายกลับไปเคลมตามเดือนเอกสาร";
}
