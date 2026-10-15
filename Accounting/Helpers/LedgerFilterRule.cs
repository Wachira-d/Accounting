namespace Accounting.Helpers;

/// <summary>
/// **รายงานบัญชีแบบกรองสาขา/โครงการ/มิติ — กติกาตัวเดียว** (ฝ่ายค้านรอบส่งออก 2026-10-15 รอบ 2 P2-5 · erp-review/2026-10-14/EXPORT_AUDIT.md)
///
/// <para><b>บั๊กที่มา:</b> (1) บรรทัดที่ติดสาขา A แต่ปันส่วนเข้ามิติของสาขา B ถูกนับเต็มในทั้งสองสาขา · (2) ปันส่วน 30/70 แสดง 100% ทั้งสองมิติ
/// (ตัวกรองดูแค่ "มีแถวปันส่วนไหม" ไม่ดูสัดส่วน — ขัดกับรายงานกำไรขาดทุนตามมิติ <c>GetDimensionPnLAsync</c>) · (3) งบทดลอง/บัญชีแยกประเภทแบบกรอง
/// นับตั้งแต่เปิดบริษัท และใบปิดปีไม่มีสาขา ⇒ รายได้/ค่าใช้จ่ายของสาขาสะสมข้ามปีไม่มีวันเป็นศูนย์ · (4) โหลดทั้งสมุดบัญชีเข้าหน่วยความจำ</para>
///
/// <para><b>กติกา (เจ้าของ: <c>AccountingService.ApplyLedgerLineFilters</c> + <c>LoadAllocationSharesAsync</c> ใช้ตัวคำนวณในไฟล์นี้):</b>
/// <list type="number">
/// <item><b>สาขาของบรรทัด</b> = สาขาบนบรรทัด ถ้าไม่มี = สาขาบนหัวใบ · บรรทัดที่มีสาขาแล้วนับใน<b>สาขานั้นสาขาเดียว เต็มจำนวน</b>
///   (การปันส่วนมิติไม่ย้ายบรรทัดข้ามสาขา)</item>
/// <item>บรรทัดที่<b>ไม่มีสาขา</b> + กรองสาขาที่ผูกมิติ: มีแถวปันส่วน ⇒ นับ<b>ตามสัดส่วน</b>ที่ปันเข้ามิติของสาขา (<see cref="AllocationShare"/>) ·
///   ไม่มีแถวปันส่วน ⇒ มิติบนบรรทัด/หัวใบตรงกับมิติของสาขา = เต็มจำนวน</item>
/// <item><b>กรองมิติ</b>: บรรทัดที่มีแถวปันส่วน ⇒ ตามสัดส่วนที่ปันเข้ามิตินั้น (แถวปันส่วนเป็นตัวกำหนดมิติของบรรทัด — มิติบนบรรทัด/หัวใบไม่นับซ้ำ) ·
///   ไม่มีแถวปันส่วน ⇒ มิติบนบรรทัด/หัวใบ = เต็มจำนวน · กรองทั้งมิติและสาขา ⇒ มิติกำหนดยอด สาขาเป็นตัวคัดเข้า</item>
/// <item>สัดส่วน = <c>AllocatedAmount</c> (จำนวนเงินบนฝั่งของบรรทัด) ก่อน → <c>AllocatedPercent</c> (ปัด 2 ตำแหน่ง AwayFromZero) → เต็มบรรทัด ·
///   รายงานกำไรขาดทุนตามมิติ (<c>GetDimensionPnLAsync</c>) ใช้ตัวเดียวกัน ⇒ ตัวเลขเท่ากัน</item>
/// <item><b>งบทดลอง/บัญชีแยกประเภทแบบกรอง</b>: รายได้/ค่าใช้จ่ายเริ่มที่<b>ต้นรอบบัญชี</b>ของวันที่รายงาน (<see cref="PnlWindowStart"/>) ·
///   สินทรัพย์/หนี้สิน/ทุนนับตั้งแต่เปิดบริษัท (ยอดนำเข้าไม่มีสาขา จึงไม่บวก — เดิม) · กำไรขาดทุนของรอบก่อน ๆ ของตัวกรองแสดงเป็นแถวทุน
///   <see cref="PriorPnlRowName"/> (ไม่มีผัง) ⇒ งบทดลองแบบกรองลงตัวเมื่อรายการของตัวกรองลงตัว</item>
/// <item>ไม่กรอง ⇒ ผลเดิมทุกตัวเลข (ฐานยอดยกมา + รายการตั้งแต่วันเริ่มฐาน) · รวมยอดในฐานข้อมูล (ไม่ดึงทุกบรรทัด)</item>
/// </list></para>
/// </summary>
public static class LedgerFilterRule
{
    public const string RuleCode = "GL-FILTER";

    /// <summary>ชื่อแถวทุนของกำไร(ขาดทุน)ก่อนรอบบัญชีนี้ในงบทดลองแบบกรอง (ไม่มีรหัสผัง)</summary>
    public const string PriorPnlRowName = "กำไร(ขาดทุน)ก่อนรอบบัญชีนี้ของตัวกรองที่เลือก";

    /// <summary>แถวนี้คือแถวทุน <see cref="PriorPnlRowName"/> ของงบทดลองแบบกรอง (ไม่มีรหัสผัง) — รอบ 3 P2-1: งบทดลองแบบช่วงเวลา
    /// (<c>AccountingReportSheets.BuildTrialBalanceWorksheet</c>) ถือยอดของแถวนี้เป็น<b>ยอดยกมาทั้งจำนวน</b> (กำไรขาดทุนของรอบก่อน ๆ ไม่เคลื่อนไหวในรอบนี้ ·
    /// บัญชีแยกประเภทไม่มีแถวคู่) — เดิมยกมา 0 ⇒ ผลต่างความต่อเนื่องเท่ากำไรขาดทุนรอบก่อนทั้งก้อน ⇒ ไฟล์กรองสาขา/โครงการ/มิติทุกไฟล์หลังปิดปีแรกขึ้นคำเตือนเท็จ</summary>
    public static bool IsPriorPnlRow(string accountCode, string accountName)
        => accountCode.Length == 0 && accountName == PriorPnlRowName;

    /// <summary>ส่วนของบรรทัดที่ปันเข้ามิติหนึ่ง (pure): จำนวนเงินที่ปัน (ฝั่งเดียวกับบรรทัด) → ร้อยละ (ปัด 2 ตำแหน่ง AwayFromZero) → เต็มบรรทัด</summary>
    public static (decimal Debit, decimal Credit) AllocationShare(decimal lineDebit, decimal lineCredit, decimal? allocatedAmount, decimal? allocatedPercent)
    {
        if (allocatedAmount is decimal amount)
        {
            var magnitude = Math.Abs(amount);
            return lineDebit >= lineCredit ? (magnitude, 0m) : (0m, magnitude);
        }
        if (allocatedPercent is decimal pct)
            return (Math.Round(lineDebit * pct / 100m, 2, MidpointRounding.AwayFromZero),
                    Math.Round(lineCredit * pct / 100m, 2, MidpointRounding.AwayFromZero));
        return (lineDebit, lineCredit);
    }

    /// <summary>วันเริ่มนับรายได้/ค่าใช้จ่ายของงบทดลอง/บัญชีแยกประเภทแบบกรอง = ต้นรอบบัญชีที่ <paramref name="asOf"/> อยู่ (<see cref="FiscalYear"/>)</summary>
    public static DateTime PnlWindowStart(DateTime asOf, int fiscalYearStartMonth)
        => FiscalYear.RangeFor(FiscalYear.FiscalYearOf(asOf.Date, fiscalYearStartMonth), fiscalYearStartMonth).Start;

    /// <summary>ข้อความเมื่อบัญชีแยกประเภทแบบกรองครอบช่วงข้ามต้นรอบบัญชี — งบทดลองแบบกรองเริ่มรายได้/ค่าใช้จ่ายใหม่ที่ต้นรอบ</summary>
    public static string CrossesFiscalYearNote(DateTime fiscalYearStart)
        => $"⚠ ช่วงที่เลือกข้ามต้นรอบบัญชี ({ThaiDate.ToThaiDisplayString(fiscalYearStart)}) — งบทดลองแบบกรองเริ่มนับรายได้/ค่าใช้จ่ายใหม่ที่ต้นรอบ "
            + "แต่บัญชีแยกประเภทช่วงนี้นับรายได้/ค่าใช้จ่ายต่อเนื่อง ⇒ ยอดยกไปของบัญชีรายได้/ค่าใช้จ่ายอาจไม่เท่างบทดลอง · แยกดูทีละรอบบัญชีเพื่อให้ตรงกัน";
}
