namespace Accounting.Helpers;

/// <summary>
/// ตารางประเภทเงินได้ + อัตราหัก ณ ที่จ่าย ตาม ท.ป.4/2528 (+ §3 เตรส)
/// — <b>ตัวอ้างอิงกลางตัวเดียวของระบบ</b>
///
/// ═══ ทำไมต้องมี ═══
/// ผลตรวจพบว่าคำถามเดียวกันถูกตอบด้วยตารางคนละชุด <b>2 ที่</b> และไม่ตรงกัน
/// ทั้งคู่ยังไม่ตรงกับกฎหมายด้วย:
/// <list type="bullet">
/// <item><c>WithholdingTaxCertController.GetIncomeTypes</c> — 40(3) ค่าสิทธิ = <b>5%</b>
///   (กฎหมายคือ 3%) และ 40(1) เงินเดือน = <b>3% แบบคงที่</b> ทั้งที่กฎหมายใช้
///   <b>อัตราขั้นบันได</b></item>
/// <item><c>wht-credit.html</c> dropdown — 40(4) รวม "ดอกเบี้ย/เงินปันผล" เป็นตัวเลือกเดียว
///   ที่ <b>1%</b> ทั้งที่ดอกเบี้ยบุคคล = 15% · ดอกเบี้ยนิติบุคคล = 1% · ปันผล = 10%</item>
/// </list>
/// dropdown เป็นคำแนะนำเดียวที่ผู้ใช้เห็นตอนกรอกอัตรา ⇒ ป้ายผิด = ยอดหักผิด
/// ตั้งแต่ต้นทาง แล้วไหลไป ภ.ง.ด.3/53 และเครดิต CIT ทั้งสาย
///
/// ═══ กติกา ═══
/// อัตราของผู้รับที่เป็น <b>บุคคลธรรมดา</b> กับ <b>นิติบุคคล</b> ต่างกันได้ จึงเก็บ
/// แยกสองช่อง. ประเภทที่กฎหมายไม่ได้กำหนดอัตราคงที่ (เงินเดือน = ขั้นบันได)
/// เก็บ <c>null</c> — <b>ห้ามใส่ตัวเลขปลอมเพื่อให้ช่องไม่ว่าง</b>
/// </summary>
public static class ThaiWhtRateTable
{
    /// <param name="IndividualRate">อัตราเมื่อผู้รับเป็นบุคคลธรรมดา (%) — null = ไม่มีอัตราคงที่</param>
    /// <param name="JuristicRate">อัตราเมื่อผู้รับเป็นนิติบุคคลไทย (%) — null = ไม่ใช้กับนิติบุคคล</param>
    public sealed record IncomeType(
        string Code,
        string Name,
        string TaxSection,
        decimal? IndividualRate,
        decimal? JuristicRate,
        IReadOnlyList<string> ApplicableForms,
        string? Note = null);

    /// <summary>ทุกประเภทที่ระบบรองรับ เรียงตามลำดับมาตรา</summary>
    public static readonly IReadOnlyList<IncomeType> All = new[]
    {
        new IncomeType("1", "เงินเดือน ค่าจ้าง บำนาญ", "40(1)",
            IndividualRate: null, JuristicRate: null,
            new[] { "ภ.ง.ด.1" },
            Note: "อัตราขั้นบันไดตามฐานภาษีเงินได้บุคคลธรรมดา — ไม่มีอัตราคงที่"),
        new IncomeType("2", "ค่านายหน้า/ค่าบริการ", "40(2)",
            IndividualRate: 3m, JuristicRate: 3m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" }),
        new IncomeType("3", "ค่าแห่งลิขสิทธิ์/ค่าสิทธิ", "40(3)",
            IndividualRate: 3m, JuristicRate: 3m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" }),
        new IncomeType("4a", "ดอกเบี้ย", "40(4)(ก)",
            IndividualRate: 15m, JuristicRate: 1m,
            new[] { "ภ.ง.ด.2", "ภ.ง.ด.53" },
            Note: "บุคคลธรรมดา 15% · นิติบุคคลไทย 1%"),
        new IncomeType("4b", "เงินปันผล", "40(4)(ข)",
            IndividualRate: 10m, JuristicRate: 10m,
            new[] { "ภ.ง.ด.2", "ภ.ง.ด.53" }),
        new IncomeType("5", "ค่าเช่าทรัพย์สิน", "40(5)",
            IndividualRate: 5m, JuristicRate: 5m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" }),
        new IncomeType("6", "ค่าวิชาชีพอิสระ", "40(6)",
            IndividualRate: 3m, JuristicRate: 3m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" }),
        new IncomeType("7", "ค่ารับเหมา", "40(7)",
            IndividualRate: 3m, JuristicRate: 3m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" }),
        new IncomeType("8", "ค่าจ้างทำของ/ค่าบริการอื่น", "40(8)",
            IndividualRate: 3m, JuristicRate: 3m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" }),
        new IncomeType("8ad", "ค่าโฆษณา", "40(8)",
            IndividualRate: 2m, JuristicRate: 2m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" }),
        new IncomeType("8tr", "ค่าขนส่ง (ไม่ใช่ขนส่งสาธารณะ)", "40(8)",
            IndividualRate: 1m, JuristicRate: 1m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" }),
    };

    /// <summary>
    /// แถวบนแบบ **หนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ)** ที่ประเภทเงินได้นี้ต้องไปอยู่
    /// — <c>"1" "2" "3" "4a" "4b" "5" "6"</c>
    ///
    /// ═══ ที่มา (ผลตรวจทีม D/F · SYSTEM_AUDIT_2026-09-07.md D-03 · F-02) ═══
    /// <para>ทั้ง renderer ฝั่ง PDF และฝั่ง JS ต่างถือ <b>allow-list ของรหัสที่พิมพ์
    /// มือ</b> ซึ่งตกรหัสที่ระบบเองสร้าง (<c>8ad</c> ค่าโฆษณา 2% · <c>8tr</c>
    /// ค่าขนส่ง 1%) และแถว "อื่น ๆ" ก็เป็น allow-list (<c>9/99/other</c>) จึงไม่ใช่
    /// ตาข่ายรับ ⇒ บรรทัดนั้น<b>หายจากทุกแถว</b> แต่ยอดรวมท้ายตารางยังเต็ม ⇒
    /// ผู้รับเงินเอาไปยื่นเครดิตภาษีไม่ได้ (เอกสารที่กฎหมายบังคับออก 2 ฉบับ)</para>
    ///
    /// <para><b>ทิศของความผิดพลาดต้องเป็น "ไปโผล่แถวอื่น ๆ" ไม่ใช่ "หายเงียบ"</b>
    /// — รหัสที่ไม่รู้จักจึงตกแถว 6 เสมอ · <c>"40(4)"</c> ที่ไม่ระบุวงเล็บ (ก)/(ข)
    /// ก็ตกแถว 6 เพราะชี้ขาดไม่ได้ว่าเป็นดอกเบี้ยหรือปันผล (คนละอัตรา 15% vs 10%)
    /// — ห้ามเดา</para>
    /// </summary>
    public static string CertificateRow(string? codeOrSection)
    {
        var section = Find(codeOrSection)?.TaxSection
            ?? (codeOrSection ?? "").Trim();
        section = section.Replace(" ", "");
        if (section.StartsWith("40(1)", StringComparison.Ordinal)) return "1";
        if (section.StartsWith("40(2)", StringComparison.Ordinal)) return "2";
        if (section.StartsWith("40(3)", StringComparison.Ordinal)) return "3";
        if (section.StartsWith("40(4)", StringComparison.Ordinal))
        {
            // รองรับทั้งอักษรไทย (ก)/(ข) และละติน (a)/(b)
            if (section.Contains("(ข)", StringComparison.Ordinal)
                || section.EndsWith("(b)", StringComparison.OrdinalIgnoreCase)) return "4b";
            if (section.Contains("(ก)", StringComparison.Ordinal)
                || section.EndsWith("(a)", StringComparison.OrdinalIgnoreCase)) return "4a";
            return "6";
        }
        if (section.StartsWith("40(5)", StringComparison.Ordinal)
            || section.StartsWith("40(6)", StringComparison.Ordinal)
            || section.StartsWith("40(7)", StringComparison.Ordinal)
            || section.StartsWith("40(8)", StringComparison.Ordinal)) return "5";
        return "6";
    }

    /// <summary>หาตามรหัส หรือตามมาตรา ("40(5)") — คืน null เมื่อไม่รู้จัก</summary>
    /// <summary>อัตราหัก ณ ที่จ่ายที่กฎหมายกำหนด (ท.ป.4/2528 + §3 เตรส) —
    /// ใช้ "snap" อัตราที่อนุมานจากยอดบนกระดาษเข้าหาค่าที่เป็นไปได้จริง
    ///
    /// <para>อยู่ที่นี่ที่เดียวเพื่อไม่ให้มีลิสต์อัตราชุดที่สอง (ตารางกฎหมาย
    /// ที่คัดลอกไปเขียนใหม่ = เตือน/คิดผิดตลอดไป — บทเรียนใน CLAUDE.md)</para></summary>
    public static readonly decimal[] StatutoryRates = { 1m, 2m, 3m, 5m, 10m, 15m };

    // ══════════════════════════════════════════════════════════════════════
    //  อัตราลดชั่วคราว — ตารางกฎหมายที่มี "อายุ"
    // ══════════════════════════════════════════════════════════════════════
    //
    // ═══ ที่มา (คำถามค้าง §10.5 ข้อ 2 รอบ 183) ═══
    // เอกสารรอบก่อนตั้งคำถามว่า "อัตรา 1.5% (e-withholding) ไม่อยู่ในตารางกลาง
    // ⇒ ใบที่ใช้ 1.5% จะขึ้นคำเตือน" และเสนอให้เติมเข้า `StatutoryRates`
    //
    // **ข้อเท็จจริงที่ตรวจแล้วต่างจากคำถาม**: 1.5% ไม่ใช่อัตรา e-withholding —
    // เป็นอัตราลด**ทั่วไป**ช่วงโควิด ใช้กับการจ่ายระหว่าง 1 เม.ย. – 30 ก.ย. 2563
    // เท่านั้น. อัตราของ e-withholding คือ 2% (1 ต.ค. 2563 – 31 ธ.ค. 2564) แล้ว
    // 1% (1 ม.ค. 2566 – 31 ธ.ค. 2568) ซึ่ง **อยู่ใน `StatutoryRates` อยู่แล้ว**
    // (2% = ค่าโฆษณา · 1% = ค่าขนส่ง) ⇒ ใบ e-withholding **ไม่เคยติดคำเตือนเลย**
    //
    // ⇒ การเติม 1.5% เข้า `StatutoryRates` แบบไม่มีวันหมดอายุจะทำให้อัตราที่
    // หมดอายุไปแล้ว 6 ปีกลายเป็น "ถูกกฎหมาย" ถาวร — ใบปีนี้ที่ใช้ 1.5% (หัก**ขาด**
    // ครึ่งหนึ่งจาก 3%) จะเงียบสนิท และ §54 ให้**ผู้จ่าย**รับผิดในภาษีที่หักขาด
    // = ความเสียหายที่มองไม่เห็นจนกว่าจะถูกประเมิน (ผิดทิศตาม G5)
    //
    // ⇒ ทางที่เลือก: ให้ตาราง**มีมิติเวลา** — อัตราที่เคยใช้ได้จริงถูกบันทึกไว้
    // พร้อมช่วงเวลาและเลขที่ประกาศ. ใบที่ลงวันที่ **ในช่วง** ⇒ เงียบ (เลิกฟ้อง
    // ใบที่ถูก — F2 ข้อ 8) · ใบที่ลงวันที่ **นอกช่วง** ⇒ เตือนโดย**อ้างช่วงเวลา
    // และอัตราที่ควรใช้แทน** ไม่ใช่แค่ "ไม่ใช่อัตราตามกฎหมาย"
    //
    // ⚠️ ห้ามใส่แถวที่ยังไม่มีประกาศจริงรองรับ — แถวที่แต่งขึ้นจะทำให้ระบบเงียบ
    // กับใบที่หักขาด ซึ่งอันตรายกว่าการเตือนเกิน (หลักการ 10 ข้อ #3)

    /// <param name="Rate">อัตรา (%)</param>
    /// <param name="From">วันแรกที่ใช้ได้ (วันที่<b>จ่าย</b> ไม่ใช่วันที่ในใบกำกับ)</param>
    /// <param name="ToInclusive">วันสุดท้ายที่ใช้ได้ (รวมวันนี้)</param>
    /// <param name="LegalReference">เลขที่ประกาศ/กฎหมายที่ให้อัตรานี้</param>
    /// <param name="AppliesTo">ขอบเขตประเภทเงินได้ที่ประกาศนั้นครอบ (ข้อความอธิบาย)</param>
    public sealed record TemporaryRate(
        decimal Rate, DateTime From, DateTime ToInclusive, string LegalReference, string AppliesTo);

    /// <summary>อัตราลดชั่วคราวที่เคยมีผลจริง — เรียงตามวันเริ่ม</summary>
    public static readonly IReadOnlyList<TemporaryRate> TemporaryReducedRates = new[]
    {
        new TemporaryRate(1.5m,
            new DateTime(2020, 4, 1), new DateTime(2020, 9, 30),
            "ท.ป.310/2563",
            "ลดจาก 3% เป็น 1.5% สำหรับเงินได้ ม.40(2)(3)(6)(7)(8) — มาตรการโควิด-19"),
    };

    /// <summary>สถานะของอัตราหนึ่ง ณ วันที่จ่ายหนึ่ง</summary>
    public enum RateStanding
    {
        /// <summary>อัตราถาวรตาม ท.ป.4/2528 / §3 เตรส</summary>
        Statutory = 0,
        /// <summary>อัตราลดชั่วคราว และวันที่จ่าย<b>อยู่ใน</b>ช่วงที่ประกาศให้</summary>
        TemporaryInForce = 1,
        /// <summary>อัตราลดชั่วคราว แต่วันที่จ่าย<b>อยู่นอก</b>ช่วง</summary>
        TemporaryOutOfWindow = 2,
        /// <summary>ไม่ใช่อัตราของไทยเลย</summary>
        NotRecognised = 3,
    }

    /// <param name="Standing">สถานะ</param>
    /// <param name="Warning">ข้อความเตือน (null = ไม่ต้องเตือน) — ภาษาไทย เอาไปโชว์ได้ตรง ๆ</param>
    public readonly record struct RateVerdict(RateStanding Standing, string? Warning)
    {
        /// <summary>ต้องเตือนผู้ใช้ไหม</summary>
        public bool NeedsAttention => Warning != null;
    }

    /// <summary>
    /// อัตรานี้ใช้ได้ตามกฎหมายไหม เมื่อ<b>จ่ายวันนั้น</b> — ตัวตัดสินตัวเดียว
    /// ของทั้งด่านตอนอนุมัติเอกสารและตัวสร้างไฟล์ยื่น ภ.ง.ด.
    ///
    /// <para>ไม่ throw · อัตรา ≤ 0 = "ไม่ได้หัก" ⇒ ไม่มีอะไรต้องเตือน</para>
    /// </summary>
    /// <param name="ratePercent">อัตราที่ใบ/บรรทัดใช้ (%)</param>
    /// <param name="paymentDate">วันที่จ่าย (ท.ป.4/2528 ผูกกับวันจ่าย ไม่ใช่วันที่ในใบ)</param>
    public static RateVerdict ClassifyRate(decimal ratePercent, DateTime paymentDate)
    {
        if (ratePercent <= 0m) return new RateVerdict(RateStanding.Statutory, null);
        if (StatutoryRates.Contains(ratePercent))
            return new RateVerdict(RateStanding.Statutory, null);

        var temp = TemporaryReducedRates.FirstOrDefault(t => t.Rate == ratePercent);
        if (temp != null)
        {
            var d = paymentDate.Date;
            if (d >= temp.From.Date && d <= temp.ToInclusive.Date)
                return new RateVerdict(RateStanding.TemporaryInForce, null);

            return new RateVerdict(RateStanding.TemporaryOutOfWindow,
                $"อัตรา {ratePercent:0.##}% เป็นอัตรา**ลดชั่วคราว** ({temp.LegalReference}: {temp.AppliesTo}) "
                + $"ใช้ได้เฉพาะการจ่ายระหว่าง {ThaiDate(temp.From)}–{ThaiDate(temp.ToInclusive)} "
                + $"แต่ใบนี้จ่าย {ThaiDate(paymentDate)} — นอกช่วง ⇒ ต้องใช้อัตราปกติ ("
                + string.Join(" / ", StatutoryRates.Select(r => $"{r:0.##}")) + "%). "
                + "ถ้าหักน้อยกว่าที่กฎหมายกำหนด **ผู้จ่ายรับผิดในส่วนที่ขาด (§54)** — "
                + "แก้อัตราที่บรรทัด หรือยืนยันประเภทเงินได้อีกครั้ง");
        }

        return new RateVerdict(RateStanding.NotRecognised,
            $"อัตรา {ratePercent:0.##}% ไม่ใช่อัตราตามกฎหมาย ("
            + string.Join(" / ", StatutoryRates.Select(r => $"{r:0.##}"))
            + "%) ตรวจประเภทเงินได้ (Income Type Code) อีกครั้ง");
    }

    /// <summary>วันที่แบบไทย (พ.ศ.) สำหรับข้อความที่ผู้ใช้อ่าน — ใช้ในไฟล์นี้เท่านั้น</summary>
    private static string ThaiDate(DateTime d) => $"{d:dd/MM/}{d.Year + 543}";

    /// <summary>ดึงอัตราตามกฎหมายที่ใกล้ที่สุดเมื่อห่างไม่เกิน
    /// <paramref name="tolerance"/> — ไกลกว่านั้นคืน <c>null</c> (ไม่ใช่อัตราของไทย
    /// = อย่าเดา ปล่อยให้ด่านตรวจเตือนแทน)</summary>
    public static decimal? SnapToStatutory(decimal? rate, decimal tolerance = 0.15m)
    {
        if (rate is not decimal r || r <= 0m) return null;
        var nearest = StatutoryRates.OrderBy(v => Math.Abs(v - r)).First();
        return Math.Abs(nearest - r) <= tolerance ? nearest : null;
    }

    public static IncomeType? Find(string? codeOrSection)
    {
        if (string.IsNullOrWhiteSpace(codeOrSection)) return null;
        var key = codeOrSection.Trim();
        return All.FirstOrDefault(t =>
                   string.Equals(t.Code, key, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(t =>
                   string.Equals(t.TaxSection, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>อัตราที่ควรใช้กับผู้รับรายนี้ — null = ไม่มีอัตราคงที่ (ต้องให้คนกรอก)</summary>
    public static decimal? RateFor(string? codeOrSection, bool payeeIsJuristic)
    {
        var t = Find(codeOrSection);
        if (t == null) return null;
        return payeeIsJuristic ? t.JuristicRate : t.IndividualRate;
    }

    /// <summary>ประเภทเงินได้นี้ **อัตราขึ้นกับชนิดผู้รับ** หรือไม่
    ///
    /// <para>วันนี้มีตัวเดียวคือ <b>ดอกเบี้ย 40(4)(ก)</b> — บุคคลธรรมดา 15% · นิติบุคคลไทย 1%
    /// (ต่างกัน 15 เท่า). ตัวช่วยนี้มีไว้ให้ผู้เรียกที่<b>ยังไม่รู้ชนิดผู้รับ</b> ตอบว่า
    /// "ไม่รู้" แทนการเดาข้างใดข้างหนึ่ง — เดาเป็นนิติบุคคล = หักขาด (§54 ผู้จ่ายรับผิด)
    /// · เดาเป็นบุคคล = หักเกิน (ผู้รับต้องไปขอคืนเอง) ⇒ ทั้งสองทางผิดจริง</para>
    ///
    /// <para>⚠️ ห้ามเขียนรายการรหัสที่กำกวมเป็นลิสต์มือที่อื่น — ให้ถามฟังก์ชันนี้
    /// เพื่อให้เพิ่มแถวในตารางแล้วทุกด่านรู้พร้อมกัน (หลักการ "ตัวตั้งตัวเดียว")</para></summary>
    public static bool RateDependsOnPayeeKind(string? codeOrSection)
    {
        var t = Find(codeOrSection);
        return t != null && t.IndividualRate != t.JuristicRate;
    }

    /// <summary>
    /// ด่าน ฿1,000 (ท.ป.4/2528 ข้อ 12) — <b>สะสมต่อคู่สัญญา ไม่ใช่ต่อบรรทัด</b>
    ///
    /// <para>จ่ายงวดละ 800 สามงวดในสัญญาเดียวกัน ต้องหักตั้งแต่งวดที่ยอดสะสม
    /// ถึง 1,000 — ระบบที่ดูยอดต่อบรรทัดอย่างเดียวจะไม่หักเลยทั้งสามงวด</para>
    /// </summary>
    public const decimal MinimumThresholdBaht = 1000m;

    /// <summary>ต้องหักภาษีสำหรับการจ่ายครั้งนี้หรือไม่</summary>
    /// <param name="thisPaymentAmount">ยอดจ่ายงวดนี้</param>
    /// <param name="alreadyPaidUnderSameContract">ยอดที่จ่ายไปแล้วภายใต้สัญญา/คู่สัญญาเดียวกันในปีภาษีนี้</param>
    /// <param name="contractTotalKnown">รู้ยอดสัญญาทั้งก้อนแล้วและยอดนั้น ≥ 1,000</param>
    public static bool ShouldWithhold(
        decimal thisPaymentAmount,
        decimal alreadyPaidUnderSameContract = 0m,
        bool contractTotalKnown = false)
    {
        if (contractTotalKnown) return true;
        return thisPaymentAmount + alreadyPaidUnderSameContract >= MinimumThresholdBaht;
    }
}
