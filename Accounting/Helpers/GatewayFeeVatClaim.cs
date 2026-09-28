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

    /// <summary>ตรวจคำขอรับใบกำกับค่าธรรมเนียม (ก่อนลงใบสำคัญ Dr 11610 / Cr 11630)</summary>
    public static GatewayFeeVatClaimCheck Check(decimal vatAmount, decimal outstanding,
        string? invoiceNo, DateTime invoiceDate, DateTime claimDate,
        string? supplierName, string? supplierTaxId, string? supplierBranchCode, string? lateReason)
    {
        var vat = R(vatAmount);
        var branch = string.IsNullOrWhiteSpace(supplierBranchCode) ? "00000" : supplierBranchCode.Trim();
        GatewayFeeVatClaimCheck Fail(string m) => new(false, m, vat, R(outstanding), false, branch);

        if (vat <= 0m)
            return Fail("ยอด VAT บนใบกำกับต้องมากกว่า 0");
        if (vat > R(outstanding) + GatewayRefundMath.Tolerance)
            return Fail($"VAT บนใบกำกับ ({vat:N2}) มากกว่า VAT ค่าธรรมเนียมที่พักไว้ใน 11630 ({R(outstanding):N2}) — "
                + "ใบนี้อาจครอบค่าธรรมเนียมของรอบโอนที่ยังไม่ได้บันทึก (บันทึกรอบโอนที่ค้างก่อน) หรือ \"VAT ของค่าธรรมเนียม\" "
                + "ตั้งไม่ตรงกับที่ผู้ให้บริการคิด · ระบบไม่ดัน 11630 ให้ติดลบ");
        if (string.IsNullOrWhiteSpace(invoiceNo))
            return Fail("กรุณากรอกเลขที่ใบกำกับภาษีของผู้ให้บริการ (§86/4)");
        if (string.IsNullOrWhiteSpace(supplierName))
            return Fail("กรุณากรอกชื่อผู้ออกใบกำกับ (ผู้ให้บริการ) ตามที่พิมพ์บนใบ (§86/4)");
        if (!ThaiTaxId.IsValid(supplierTaxId))
            return Fail("เลขประจำตัวผู้เสียภาษีของผู้ออกใบกำกับต้องเป็นตัวเลข 13 หลักที่ checksum ถูกต้อง — "
                + "ภาษีซื้อที่ไม่มีเลขผู้ขายเคลมไม่ได้ (§82/5(1))");
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
