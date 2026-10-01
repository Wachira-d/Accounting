namespace Accounting.Helpers;

/// <summary>VAT ค่าธรรมเนียมที่พักไว้ใน 11630 จากรอบโอนของเดือนหนึ่ง (ยอดเดบิตสุทธิของบรรทัด 11630 ในใบสำคัญรอบโอน)</summary>
public readonly record struct GatewayFeeVatMonth(DateTime MonthStartUtc, decimal Deferred);

/// <summary>ระดับความเสี่ยงของ VAT ค้าง 11630 ต่อกำหนด §82/3 (เคลมได้ภายใน 6 เดือนนับจากเดือนที่ออกใบกำกับ)</summary>
public enum GatewayFeeVatAgeLevel
{
    Ok = 0,
    /// <summary>ค้างมาแล้ว ≥ 5 เดือน — ใกล้ครบกำหนด §82/3 (ถ้าใบกำกับลงวันที่ในเดือนนั้น)</summary>
    NearDeadline = 1,
    /// <summary>ค้างเกิน 6 เดือน — อาจเลยกำหนด §82/3 แล้ว (ขึ้นกับวันที่บนใบกำกับจริง)</summary>
    PastWindow = 2,
}

/// <summary>ยอดค้างต่อเดือนหลังตัดยอดที่เคลมแล้ว (ตัดจากเดือนเก่าสุดก่อน)</summary>
public sealed record GatewayFeeVatBucket(
    DateTime MonthStartUtc, decimal Deferred, decimal Outstanding, int MonthsOld, GatewayFeeVatAgeLevel Level, string? Warning);

/// <summary>ภาพรวม VAT ค่าธรรมเนียมที่รอใบกำกับของผู้ให้บริการรายหนึ่ง</summary>
public sealed record GatewayFeeVatAging(
    decimal DeferredTotal, decimal ClaimedTotal, decimal Outstanding,
    IReadOnlyList<GatewayFeeVatBucket> Buckets, GatewayFeeVatAgeLevel WorstLevel, string? Warning);

/// <summary>ใบสำคัญ "รับใบกำกับค่าธรรมเนียม" ที่ลงไว้แล้ว — ใช้หาการเคลมซ้ำ (review198-E2 E2-4) · <c>SupplierTaxId</c> null = หาไม่เจอ (ใบเก่า)</summary>
public readonly record struct GatewayFeeVatPriorClaim(string EntryNumber, string? InvoiceNo, string? SupplierTaxId);

/// <summary>ผลตรวจ "ปรับปรุงเศษ VAT ค่าธรรมเนียมใน 11630" (รอบ 201 ทีม GW · A-GW8) — <c>Amount</c> มีเครื่องหมาย: บวก = 11630 ค้างเดบิต (Dr ค่าธรรมเนียม / Cr 11630) ·
/// ลบ = เคลมเกินที่พัก (Dr 11630 / Cr ค่าธรรมเนียม)</summary>
public readonly record struct GatewayFeeVatResidueCheck(bool Ok, string? Message, decimal Amount, decimal Threshold);

/// <summary>ผลตรวจคำขอ "รับใบกำกับค่าธรรมเนียม" (ย้าย 11630 → 11610)</summary>
public readonly record struct GatewayFeeVatClaimCheck(
    bool Ok, string? Message, decimal Vat, decimal OutstandingAfter, bool IsLate, string BranchCode);

/// <summary>
/// **VAT ค่าธรรมเนียมรับชำระเงินออนไลน์ที่พักไว้ใน 11630 — ทางไปถึง 11610 + เตือนอายุ §82/3** (ฝ่ายค้าน R-E3)
///
/// <para>═══ ที่มา ═══ รอบ 198 G-3 ลง VAT ของค่าธรรมเนียมเป็น <c>Dr 11630 ภาษีซื้อรอเครดิต</c> ตอนบันทึกรอบโอน และหน้าตั้งค่าบอกว่า
/// "ย้ายเข้า 11610 เมื่อได้ใบกำกับรายเดือน" — แต่<b>ไม่มีเส้นไหนย้าย</b>และไม่มีคำเตือนอายุ (defect class "มี ≠ ถูกเรียก") ⇒ ภาษีซื้อที่บอกว่า
/// "เคลมได้" ไม่เคยเข้า ภ.พ.30 · ผู้ใช้ที่บันทึกใบกำกับรายเดือนของผู้ให้บริการเป็นเอกสารซื้อ = ค่าใช้จ่ายซ้ำ (ลงไปแล้วตอนรอบโอน) +
/// ภาษีซื้อสองที่ (11610 จากเอกสาร + 11630 ค้าง)</para>
///
/// <para>═══ เส้นที่เลือก (เล็กที่สุดที่ถูก) ═══ "รับใบกำกับค่าธรรมเนียม" = JV <c>Dr 11610 / Cr 11630</c> เท่ายอด VAT บนใบกำกับ
/// <b>ไม่ลงค่าใช้จ่ายซ้ำ</b> · อ้างเลขที่/วันที่ใบกำกับ + ชื่อ/เลขผู้เสียภาษี/สาขาผู้ออกใบ (§86/4) ในใบสำคัญ ⇒ รายงานภาษีซื้อเส้นใบสำคัญ
/// (<c>TaxService</c> JE_INPUT) นับเป็นภาษีซื้อที่เคลมได้เมื่อมีเลขผู้เสียภาษี 13 หลัก · วันที่ใบสำคัญ = เดือนภาษีที่เคลม</para>
///
/// <para>═══ ด่าน ═══ VAT &gt; 0 และไม่เกินยอดค้าง (เกิน = ใบนี้ครอบค่าธรรมเนียมที่ยังไม่บันทึกรอบโอน หรือโหมด VAT ตั้งไม่ตรง — ต้องแก้ต้นทาง
/// ไม่ใช่ดัน 11630 ติดลบ) · เลขที่ใบกำกับ · ชื่อผู้ออก · เลขผู้เสียภาษี 13 หลัก + checksum · สาขา 5 หลัก · วันที่ใบกำกับ ≤ วันที่เคลม ·
/// <b>§82/3</b>: เดือนที่เคลม − เดือนใบกำกับ &gt; 6 = บล็อก · 1–6 เดือน = ต้องระบุเหตุผลที่เคลมช้า (CLAUDE.md กฎ #2 B)</para>
///
/// <para>═══ อายุ ═══ ใบกำกับรายเดือนของผู้ให้บริการออกตามเดือนของค่าธรรมเนียม ⇒ ใช้<b>เดือนของรอบโอน</b>เป็นตัวแทนเดือนใบกำกับ
/// (ตัวแทน ไม่ใช่ความจริง — ข้อความบอกตรง ๆ) · ≥ 5 เดือน = ใกล้ครบ · &gt; 6 เดือน = อาจเลยกำหนด · <b>ไม่โอนเป็นค่าใช้จ่ายเอง</b>
/// (CLAUDE.md B เขียนว่า "expired → auto reclassify" แต่วันที่ใบกำกับจริงระบบไม่รู้ ⇒ ถามเจ้าของ/นักบัญชีใน review198-A)</para>
///
/// <para>G6: pure · ไม่มี I/O</para>
/// </summary>
public static class GatewayFeeVatClaim
{
    /// <summary>§82/3 — เคลมภาษีซื้อได้ภายใน 6 เดือนนับจากเดือนที่ออกใบกำกับ</summary>
    public const int WindowMonths = 6;
    /// <summary>เริ่มเตือนเมื่อค้างมาแล้วเท่านี้เดือน (เหลือเดือนสุดท้าย)</summary>
    public const int WarnFromMonths = 5;

    /// <summary>คำนำของ tag ใบสำคัญ "รับใบกำกับค่าธรรมเนียม" (ทุกผู้ให้บริการ) — ใช้หาใบกำกับที่เคลมซ้ำข้ามผู้ให้บริการ (E2-4)</summary>
    public const string ClaimTagPrefix = "gateway-fee-vat:";

    /// <summary>tag ของใบสำคัญ "รับใบกำกับค่าธรรมเนียม" ของผู้ให้บริการหนึ่ง — ใช้นับยอดที่เคลมแล้ว (ต้องตรงกันทั้งฝั่งเขียนและอ่าน)</summary>
    public static string ClaimTag(string providerCode) => ClaimTagPrefix + providerCode;

    /// <summary>ใบสำคัญเคลมที่ลงไว้แล้ว (ยังไม่ถูกกลับรายการ) จากข้อมูลบนใบสำคัญ — เลขผู้เสียภาษีจากช่องโครงสร้าง (E2-5) ก่อน ·
    /// ใบสำคัญก่อนมีช่อง = เลข 13 หลักตัวแรกในคำอธิบาย (รูป "ภาษีซื้อ-{ชื่อ} {เลข} สาขา {รหัส}" ที่เส้นเคลมเขียนเสมอ)</summary>
    public static GatewayFeeVatPriorClaim PriorClaim(string entryNumber, string? reference, string? structuredTaxId, string? description)
    {
        var taxId = ThaiTaxId.Normalize(structuredTaxId);
        if (string.IsNullOrEmpty(taxId) && description != null)
        {
            var m = System.Text.RegularExpressions.Regex.Match(description, @"(?<!\d)\d{13}(?!\d)");
            if (m.Success) taxId = m.Value;
        }
        return new GatewayFeeVatPriorClaim(entryNumber, reference?.Trim(), string.IsNullOrEmpty(taxId) ? null : taxId);
    }

    /// <summary>ใบกำกับฉบับนี้ (เลขที่ + เลขผู้เสียภาษีผู้ออก) ถูกเคลมไปแล้วหรือยัง (review198-E2 E2-4) — null = ยัง
    ///
    /// <para>ใบกำกับฉบับเดียวเคลมภาษีซื้อได้ครั้งเดียว · เดิมด่านมีแค่ "ไม่เกินยอดพัก 11630" ⇒ กดซ้ำ/บันทึกซ้ำหนึ่งสัปดาห์ต่อมาผ่านได้ตราบที่ยอดพักยังพอ ·
    /// รายงานภาษีซื้อมีเลขที่ใบกำกับซ้ำสองบรรทัด และบรรทัดที่สองไม่มีใบกำกับรองรับ (§82/5(1))</para>
    /// <para>เทียบเลขที่แบบไม่สนตัวพิมพ์/ช่องว่างหัวท้าย · เลขผู้เสียภาษีเทียบเฉพาะตัวเลข · เลขที่เดียวกันจาก<b>ผู้ออกคนละราย</b> = คนละใบ (ผ่าน) ·
    /// ใบสำคัญเก่าที่หาเลขผู้เสียภาษีไม่เจอ = นับว่าตรง (ทิศปลอดภัย: บล็อกพร้อมบอกเลขใบสำคัญ ให้คนตรวจ — ไม่ปล่อยเคลมซ้ำเงียบ)</para></summary>
    public static GatewayFeeVatPriorClaim? FindDuplicate(IEnumerable<GatewayFeeVatPriorClaim> priors, string? invoiceNo, string? supplierTaxId)
    {
        var inv = (invoiceNo ?? "").Trim();
        var tid = ThaiTaxId.Normalize(supplierTaxId);
        if (inv.Length == 0) return null;
        foreach (var p in priors)
        {
            if (!string.Equals((p.InvoiceNo ?? "").Trim(), inv, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.SupplierTaxId == null || string.IsNullOrEmpty(tid) || p.SupplierTaxId == tid) return p;
        }
        return null;
    }

    /// <summary>ข้อความเมื่อใบกำกับถูกเคลมไปแล้ว — บอกเลขใบสำคัญเดิม + ทางไปต่อ</summary>
    public static string DuplicateMessage(GatewayFeeVatPriorClaim prior, string invoiceNo)
        => $"ใบกำกับเลขที่ {invoiceNo.Trim()} ของผู้ออกรายนี้ถูกเคลมภาษีซื้อไปแล้วในใบสำคัญ {prior.EntryNumber} — ใบกำกับฉบับเดียวเคลมได้ครั้งเดียว "
           + "(เคลมซ้ำ = ภาษีซื้อที่ไม่มีใบกำกับรองรับ §82/5(1)) · ถ้าใบสำคัญเดิมลงผิด ให้กลับรายการใบนั้นก่อนแล้วบันทึกใหม่";

    /// <summary>ข้อความเมื่อเดือนภาษีของวันที่เคลมยื่น/ประกาศยื่น ภ.พ.30 แล้ว (review198-E2 E2-6) — ใบสำคัญที่ลงย้อนเข้าเดือนนั้น
    /// ไม่ถูกหยิบเข้ารายงานที่ยื่นแล้ว ⇒ ภาษีซื้อหายเงียบ หรือถ้าสร้างรายงานใหม่ = แบบที่ยื่นแล้วเปลี่ยน</summary>
    public static string DeclaredVatMonthMessage(DateTime claimDate)
        => $"เดือนภาษี {ThaiDate.CalendarDateUtc(claimDate):MM/yyyy} ยื่น/ประกาศว่ายื่น ภ.พ.30 แล้ว — ลงภาษีซื้อย้อนเข้าเดือนนั้นไม่ได้ "
           + "(ไม่เข้ารายงานที่ยื่นแล้ว = ภาษีซื้อหายเงียบ) · ให้เคลมในเดือนภาษีที่ยังไม่ยื่น (ยังอยู่ในกำหนด §82/3 ถ้าไม่เกิน 6 เดือนนับจากเดือนของใบกำกับ)";

    /// <summary>วันที่เคลมอยู่ในอนาคต (วันไทย) — <c>null</c> = ไม่ใช่ (รอบ 200 ทีม G · review198-E2 E2-12)
    ///
    /// <para>ใบสำคัญเคลมลงวันที่ล่วงหน้า = ภาษีซื้อเข้า ภ.พ.30 ของเดือนที่ยังไม่ถึง และยอดพัก 11630 ของเดือนนี้ถูกหักไปก่อนรอบโอนจริงจะเกิด ·
    /// แยกจาก <see cref="Check"/> เพราะต้องรู้ "วันนี้" (ผู้เรียกส่ง <c>DateTime.UtcNow</c>) — service เรียกทั้งสองตัวเสมอ
    /// (ล็อกด้วย <c>tools/required_call_site_check.py</c>)</para></summary>
    public static string? FutureClaimDateMessage(DateTime claimDate, DateTime nowUtc)
    {
        var claim = ThaiDate.CalendarDateUtc(claimDate);
        var today = ThaiDate.CalendarDateUtc(nowUtc);
        return claim > today
            ? $"วันที่เคลม {claim:dd/MM/yyyy} อยู่ในอนาคต — ลงภาษีซื้อล่วงหน้าไม่ได้ (เข้า ภ.พ.30 เดือนที่ยังไม่ถึง) · เลือกวันที่ไม่เกินวันนี้"
            : null;
    }

    /// <summary>จำนวนเดือนปฏิทินระหว่างสองเดือน (to − from) · ติดลบได้</summary>
    private static int MonthsBetween(DateTime from, DateTime to)
        => (to.Year - from.Year) * 12 + (to.Month - from.Month);

    /// <summary>อายุยอดค้าง — ตัดยอดที่เคลมแล้วจากเดือนเก่าสุดก่อน (FIFO) แล้วติดระดับต่อเดือน</summary>
    public static GatewayFeeVatAging Aging(IEnumerable<GatewayFeeVatMonth> deferred, decimal claimedTotal, DateTime todayUtc)
    {
        var months = deferred
            .GroupBy(m => new DateTime(m.MonthStartUtc.Year, m.MonthStartUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc))
            .Select(g => new GatewayFeeVatMonth(g.Key, R(g.Sum(x => x.Deferred))))
            .Where(m => m.Deferred != 0m)
            .OrderBy(m => m.MonthStartUtc)
            .ToList();
        var deferredTotal = R(months.Sum(m => m.Deferred));
        var claimed = R(Math.Max(0m, claimedTotal));
        var toAllocate = claimed;
        var today = ThaiDate.CalendarDateUtc(todayUtc);

        var buckets = new List<GatewayFeeVatBucket>();
        foreach (var m in months)
        {
            var take = Math.Min(Math.Max(0m, m.Deferred), toAllocate);
            toAllocate -= take;
            var outstanding = R(m.Deferred - take);
            var age = MonthsBetween(m.MonthStartUtc, today);
            var level = outstanding <= 0m ? GatewayFeeVatAgeLevel.Ok
                : age > WindowMonths ? GatewayFeeVatAgeLevel.PastWindow
                : age >= WarnFromMonths ? GatewayFeeVatAgeLevel.NearDeadline
                : GatewayFeeVatAgeLevel.Ok;
            buckets.Add(new GatewayFeeVatBucket(m.MonthStartUtc, m.Deferred, outstanding, age, level, BucketWarning(level, m.MonthStartUtc, outstanding)));
        }

        var outstandingTotal = R(deferredTotal - claimed);
        var worst = buckets.Count == 0 ? GatewayFeeVatAgeLevel.Ok : buckets.Max(b => b.Level);
        string? warning = worst switch
        {
            GatewayFeeVatAgeLevel.PastWindow =>
                "มี VAT ค่าธรรมเนียมค้างใน 11630 เกิน 6 เดือน — ถ้าใบกำกับของผู้ให้บริการลงวันที่ในเดือนนั้น เลยกำหนดเคลม §82/3 แล้ว "
                + "(เคลมไม่ได้ ต้องโอนเป็นค่าใช้จ่าย — ให้นักบัญชีตัดสินและลงใบสำคัญ) · ถ้าใบกำกับลงวันที่หลังจากนั้น ยังเคลมได้ภายในกำหนด",
            GatewayFeeVatAgeLevel.NearDeadline =>
                "มี VAT ค่าธรรมเนียมค้างใน 11630 ใกล้ครบ 6 เดือน (§82/3) — ขอใบกำกับภาษีรายเดือนจากผู้ให้บริการ แล้วกด \"รับใบกำกับค่าธรรมเนียม\" ก่อนครบกำหนด",
            _ => null,
        };
        if (outstandingTotal < 0m)
            warning = (warning == null ? "" : warning + " · ")
                + $"ยอดที่เคลมแล้ว ({claimed:N2}) มากกว่า VAT ที่พักจากรอบโอน ({deferredTotal:N2}) — ตรวจใบสำคัญ \"รับใบกำกับค่าธรรมเนียม\" ที่ลงเกิน";
        return new GatewayFeeVatAging(deferredTotal, claimed, outstandingTotal, buckets, worst, warning);
    }

    private static string? BucketWarning(GatewayFeeVatAgeLevel level, DateTime month, decimal outstanding) => level switch
    {
        GatewayFeeVatAgeLevel.PastWindow =>
            $"ค้าง {outstanding:N2} จากรอบโอนเดือน {month:MM/yyyy} เกิน 6 เดือน — อาจเลยกำหนดเคลม §82/3 (ขึ้นกับวันที่บนใบกำกับ)",
        GatewayFeeVatAgeLevel.NearDeadline =>
            $"ค้าง {outstanding:N2} จากรอบโอนเดือน {month:MM/yyyy} — ใกล้ครบ 6 เดือน (§82/3)",
        _ => null,
    };

    /// <summary>ส่วนของใบกำกับรายเดือนที่อยู่<b>นอก</b>หน้านี้ (รอบ 201 ทีม GW · A-GW5 · review200-P2 X-9) — VAT ค่าธรรมเนียมของรายการที่รอบโอน settlement (batch)
    /// เป็นเจ้าของอยู่ใน<b>ใบสำคัญจ่ายค่าธรรมเนียม</b>ของรอบโอนนั้น (พักที่ 11640 "ยังไม่ถึงกำหนด" จนกรอกใบกำกับที่เอกสาร) ไม่ใช่ 11630 ⇒ เคลมรวมกับหน้านี้ไม่ได้
    /// (ใบสำคัญ Dr 11610 / Cr 11630 ของยอดนั้น = 11630 ติดลบ + ภาษีซื้อซ้ำ) · null = ไม่มีส่วนนอกหน้านี้</summary>
    public static string? BatchPortionNote(decimal batchUndueVat, int batchDocumentCount)
        => R(batchUndueVat) <= 0m || batchDocumentCount <= 0 ? null
            : $"VAT ค่าธรรมเนียมอีก {R(batchUndueVat):N2} บาท ({batchDocumentCount} ใบสำคัญจ่าย) มาจากรอบโอนที่บันทึกในหน้า \"รอบโอนเงินจากแพลตฟอร์ม\" — "
              + "ใบกำกับรายเดือนของผู้ให้บริการใบเดียวอาจครอบทั้งสองส่วน: เคลมที่หน้านี้เฉพาะส่วนที่ค้าง 11630 แล้วกรอกเลขที่/วันที่ใบกำกับเดียวกันที่ใบสำคัญจ่ายค่าธรรมเนียม"
              + "ของรอบโอนเหล่านั้น (ปุ่ม \"บันทึก + ย้ายเข้าภาษีซื้อ ภ.พ.30\" ที่หน้าเอกสาร) — ห้ามเคลมส่วนนั้นซ้ำที่นี่";

    // ══════════════════════════════════════════════════════════════════
    //  รอบ 201 ทีม GW (A-GW8 · review198-E2 E2-12f): เศษปัดรายใบกำกับค้าง 11630 — เครื่องมือปรับปรุงภายใต้เกณฑ์
    // ══════════════════════════════════════════════════════════════════

    /// <summary>คำนำของ tag ใบสำคัญ "ปรับปรุงเศษ VAT ค่าธรรมเนียม" — <b>ไม่</b>ขึ้นต้นด้วย <see cref="ClaimTagPrefix"/> (ไม่ใช่ใบกำกับ · ไม่เข้าการหาเคลมซ้ำ)</summary>
    public const string ResidueTagPrefix = "gateway-fee-vat-residue:";

    /// <summary>tag ของใบสำคัญปรับปรุงเศษของผู้ให้บริการหนึ่ง — ยอดในใบนี้นับเป็น "ล้าง 11630 แล้ว" คู่กับใบเคลม (ฝั่งอ่านกับฝั่งเขียนใช้ตัวนี้ตัวเดียว)</summary>
    public static string ResidueTag(string providerCode) => ResidueTagPrefix + providerCode;

    /// <summary>เกณฑ์เศษปัดที่ยอมให้ปรับปรุง ต่อใบกำกับที่เคลมแล้ว (ตั้งแต่การปรับปรุงครั้งก่อน) — VAT ปัดรายรายการ (ต่อ charge) กับ VAT บนใบกำกับรายเดือน
    /// (ปัดยอดรวม) ต่างกันได้ไม่เกินสตางค์ต่อรายการ · ต่อใบ 1 บาทครอบหลายร้อยรายการ · เกินนี้ = ไม่ใช่เศษ (ใบกำกับที่ยังไม่เคลม/โหมด VAT ไม่ตรง)</summary>
    public const decimal ResiduePerInvoiceBaht = 1.00m;

    /// <summary>จำนวนใบกำกับสูงสุดที่นับเข้าเกณฑ์ (ฝ่ายค้าน GWO-2) — เกณฑ์ไม่สะสมไม่จำกัด: ไม่ปรับปรุงนาน ๆ ใบเคลมสะสมจนเกณฑ์ใหญ่พอกลบ VAT ที่ยังไม่ได้เคลมทั้งเดือน ·
    /// 12 ใบ (ใบกำกับรายเดือน 1 ปี) ⇒ เกณฑ์สูงสุด 12 บาท · ปรับปรุงบ่อยกว่าปีละครั้งไม่ถูกกระทบ</summary>
    public const int ResidueMaxInvoicesCounted = 12;

    /// <summary>ตรวจคำขอปรับปรุงเศษ — ล้าง 11630 ของผู้ให้บริการนี้ทั้งยอดที่ค้าง (ไม่ให้เลือกยอด: เศษคือส่วนที่เหลือทั้งหมดหลังเคลมครบ)
    /// <para>ด่าน: (1) มีใบกำกับที่เคลมแล้วตั้งแต่การปรับปรุงครั้งก่อนอย่างน้อยหนึ่งใบ (เศษเกิดจากการเคลมเท่านั้น) (2) ยอดค้างไม่เป็นศูนย์
    /// (3) |ยอดค้าง| ≤ เกณฑ์ × จำนวนใบ (นับไม่เกิน <see cref="ResidueMaxInvoicesCounted"/> ใบ — GWO-2) (4) <b>ระดับวัน</b> (ฝ่ายค้าน GWO-2): ไม่มีบรรทัดพัก 11630
    /// ของรอบโอนที่<b>วันที่ลงบัญชีหลังวันที่ใบกำกับที่เคลมล่าสุด</b> — มี ⇒ ยอดค้างอาจเป็น VAT ที่รอใบ ไม่ใช่เศษ (ปรับปรุงทิ้ง = เสียสิทธิ์เคลม) ·
    /// เดิมเทียบระดับเดือน ⇒ ใบกำกับลงวันที่ 15 "ครอบ" รอบโอนวันที่ 28 ของเดือนเดียวกันได้</para></summary>
    public static GatewayFeeVatResidueCheck ResidueCheck(decimal outstanding, int claimsSinceLastResidue,
        DateTime? latestDeferredEntryDateUtc, DateTime? latestClaimedInvoiceDate)
    {
        var amount = R(outstanding);
        var counted = Math.Min(ResidueMaxInvoicesCounted, Math.Max(0, claimsSinceLastResidue));
        var threshold = R(ResiduePerInvoiceBaht * counted);
        GatewayFeeVatResidueCheck Fail(string m) => new(false, m, amount, threshold);
        if (claimsSinceLastResidue <= 0)
            return Fail("ยังไม่มีใบกำกับที่เคลมแล้วตั้งแต่การปรับปรุงครั้งก่อน — เศษปัดเกิดหลังเคลมใบกำกับเท่านั้น (ยอดค้างตอนนี้คือ VAT ที่รอใบกำกับ)");
        if (amount == 0m)
            return Fail("ไม่มียอดค้าง 11630 ของผู้ให้บริการนี้ให้ปรับปรุง");
        if (Math.Abs(amount) > threshold)
            return Fail($"ยอดค้าง {amount:N2} เกินเกณฑ์เศษปัด {threshold:N2} บาท ({ResiduePerInvoiceBaht:N2} บาท × ใบกำกับที่เคลม {counted} ใบ"
                + (claimsSinceLastResidue > counted ? $" — นับสูงสุด {ResidueMaxInvoicesCounted} ใบ" : "") + ") — "
                + "ไม่ใช่เศษปัด: มีใบกำกับที่ยังไม่ได้เคลม หรือ \"VAT ของค่าธรรมเนียม\" ตั้งไม่ตรงกับที่ผู้ให้บริการคิด · "
                + "ตรวจใบกำกับรายเดือนให้ครบก่อน (ถ้าเลยกำหนด §82/3 ให้นักบัญชีลงใบสำคัญโอนเป็นค่าใช้จ่ายเอง)");
        if (latestDeferredEntryDateUtc is DateTime lastDeferred
            && (latestClaimedInvoiceDate is not DateTime lastInv
                || ThaiDate.CalendarDateUtc(lastDeferred) > ThaiDate.CalendarDateUtc(lastInv)))
            return Fail($"ยังมี VAT จากรอบโอนวันที่ {ThaiDate.ToThaiDisplayString(lastDeferred)} ซึ่งหลังวันที่ใบกำกับที่เคลมล่าสุด"
                + (latestClaimedInvoiceDate is DateTime inv ? $" ({ThaiDate.ToThaiDisplayString(inv)})" : "")
                + " — ยอดค้างอาจเป็น VAT ที่รอใบกำกับ ไม่ใช่เศษ · รับใบกำกับที่ครอบรอบโอนนั้นก่อนแล้วค่อยปรับปรุงเศษ");
        return new GatewayFeeVatResidueCheck(true, null, amount, threshold);
    }

    /// <summary>ตรวจคำขอรับใบกำกับค่าธรรมเนียม (ก่อนลงใบสำคัญ Dr 11610 / Cr 11630)
    /// <para><paramref name="batchPortionNote"/> (A-GW5): ข้อความจาก <see cref="BatchPortionNote"/> — ต่อท้ายข้อความ "VAT มากกว่ายอดพัก" ให้รู้ว่าส่วนเกินอาจเป็นของรอบโอน batch</para></summary>
    public static GatewayFeeVatClaimCheck Check(decimal vatAmount, decimal outstanding,
        string? invoiceNo, DateTime invoiceDate, DateTime claimDate,
        string? supplierName, string? supplierTaxId, string? supplierBranchCode, string? lateReason,
        string? batchPortionNote = null)
    {
        var vat = R(vatAmount);
        // รอบ 200 ทีม G (review198-E2 E2-12): สาขาว่าง ≠ สำนักงานใหญ่ — §86/4 + ประกาศอธิบดีฯ 199 บังคับให้ใบกำกับมีสาขา ⇒ ต้องกรอกตามใบ
        // (เดิมเติม 00000 เงียบ ๆ = ค่าที่แต่งขึ้นลงรายงานภาษีซื้อ) · หน้าเว็บตั้งค่าเริ่มต้น 00000 ไว้ให้แล้ว ผู้ใช้ที่ลบทิ้งต้องเห็นว่าว่าง
        var branch = (supplierBranchCode ?? "").Trim();
        GatewayFeeVatClaimCheck Fail(string m) => new(false, m, vat, R(outstanding), false, branch);

        if (vat <= 0m)
            return Fail("ยอด VAT บนใบกำกับต้องมากกว่า 0");
        if (vat > R(outstanding) + GatewayRefundMath.Tolerance)
            return Fail($"VAT บนใบกำกับ ({vat:N2}) มากกว่า VAT ค่าธรรมเนียมที่พักไว้ใน 11630 ({R(outstanding):N2}) — "
                + "ใบนี้อาจครอบค่าธรรมเนียมของรอบโอนที่ยังไม่ได้บันทึก (บันทึกรอบโอนที่ค้างก่อน) หรือ \"VAT ของค่าธรรมเนียม\" "
                + "ตั้งไม่ตรงกับที่ผู้ให้บริการคิด · ระบบไม่ดัน 11630 ให้ติดลบ"
                + (string.IsNullOrWhiteSpace(batchPortionNote) ? "" : " · " + batchPortionNote));
        if (string.IsNullOrWhiteSpace(invoiceNo))
            return Fail("กรุณากรอกเลขที่ใบกำกับภาษีของผู้ให้บริการ (§86/4)");
        if (string.IsNullOrWhiteSpace(supplierName))
            return Fail("กรุณากรอกชื่อผู้ออกใบกำกับ (ผู้ให้บริการ) ตามที่พิมพ์บนใบ (§86/4)");
        if (!ThaiTaxId.IsValid(supplierTaxId))
            return Fail("เลขประจำตัวผู้เสียภาษีของผู้ออกใบกำกับต้องเป็นตัวเลข 13 หลักที่ checksum ถูกต้อง — "
                + "ภาษีซื้อที่ไม่มีเลขผู้ขายเคลมไม่ได้ (§82/5(1))");
        if (branch.Length == 0)
            return Fail("กรุณากรอกรหัสสาขาผู้ออกใบกำกับตามที่พิมพ์บนใบ (00000 = สำนักงานใหญ่ · §86/4)");
        if (branch.Length != 5 || !branch.All(char.IsDigit))
            return Fail("รหัสสาขาผู้ออกใบกำกับต้องเป็นตัวเลข 5 หลัก (00000 = สำนักงานใหญ่)");

        var inv = ThaiDate.CalendarDateUtc(invoiceDate);
        var claim = ThaiDate.CalendarDateUtc(claimDate);
        if (inv > claim)
            return Fail("วันที่เคลม (วันที่ใบสำคัญ) ต้องไม่ก่อนวันที่บนใบกำกับ — เคลมภาษีซื้อก่อนได้ใบกำกับไม่ได้");
        var late = MonthsBetween(inv, claim);
        if (late > WindowMonths)
            return Fail($"ใบกำกับลงวันที่ {inv:dd/MM/yyyy} เคลมในเดือน {claim:MM/yyyy} ช้า {late} เดือน — เกิน 6 เดือนตาม §82/3 "
                + "เคลมภาษีซื้อไม่ได้แล้ว · VAT ก้อนนี้ต้องโอนเป็นค่าใช้จ่าย (ให้นักบัญชีตัดสินและลงใบสำคัญ)");
        if (late >= 1 && string.IsNullOrWhiteSpace(lateReason))
            return Fail($"เคลมช้ากว่าเดือนของใบกำกับ {late} เดือน (ยังอยู่ในกำหนด §82/3) — กรุณาระบุเหตุผลที่เคลมช้า "
                + "(ผู้สอบบัญชี/สรรพากรถามเสมอ)");

        return new GatewayFeeVatClaimCheck(true, null, vat, R(outstanding - vat), late >= 1, branch);
    }

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
