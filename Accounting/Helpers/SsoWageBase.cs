namespace Accounting.Helpers;

/// <summary>
/// ฐานค่าจ้างประกันสังคม (ม.33) — ตัวกลางเดียวของระบบ
///
/// ═══ ปัญหาที่แก้ (บั๊กจริง) ═══
/// ไฟล์ สปส.1-10 ประกาศคู่ <c>(ค่าจ้าง, เงินสมทบ)</c> ที่ขัดกันเอง: ค่าจ้าง
/// 14,094 คู่กับเงินสมทบ 683 ทั้งที่ 5% ของ 14,094 = 705 — เพราะ exporter เอา
/// <c>GrossIncome</c> มาใส่ช่องค่าจ้าง แต่เอา <c>SocialSecurityEmployee</c>
/// ที่เก็บไว้มาใส่ช่องเงินสมทบ (คนละแหล่ง ไม่มีใครตรวจว่าตรงกันไหม)
/// ⇒ ผู้ใช้แก้ยอดสมทบรายคนเมื่อไร ไฟล์เพี้ยนทันทีโดยไม่มีอะไรเตือน
///
/// และเมื่อผู้ใช้แก้ **ฝั่งลูกจ้างอย่างเดียว** ฝั่งนายจ้างยังค้างค่าเดิม ⇒
/// ยอดนำส่งสองฝั่งไม่เท่ากัน (4,381 vs 4,403) ทั้งที่ ม.33 ใช้ฐานเดียวกัน
///
/// ═══ กติกา ═══
/// **ฐานเป็นตัวตั้ง เงินสมทบเป็นผลลัพธ์** — ทั้งสองฝั่งคำนวณจากฐานเดียวกันเสมอ
/// ฐานถูกเก็บลง <c>PayrollDetail.SocialSecurityBase</c> (ไม่ใช่คำนวณสดตอน export)
/// เพื่อให้ตัวเลขบนไฟล์ที่ยื่นไปแล้วกับในระบบเล่าเรื่องเดียวกันตลอดไป
/// </summary>
public static class SsoWageBase
{
    /// <summary>ฐานขั้นต่ำตาม ม.33 — เงินเดือนต่ำกว่านี้ยังต้องสมทบจากฐาน 1,650</summary>
    public const decimal MinBase = 1_650m;

    /// <summary>ผลต่างที่ยอมรับได้ระหว่างยอดนายจ้างที่ควรเป็น กับที่บันทึกไว้จริง
    ///
    /// 1 บาท — กว้างพอสำหรับการปัดเศษของระบบต้นทาง (ยอดที่ import มามักปัดเป็น
    /// บาทถ้วน เช่น 647.65 → 648) แต่แคบพอที่จะจับเคสจริงที่ต่างกัน 22 บาท
    /// (4,381 vs 4,403 — นายจ้างคิดจากค่าจ้างเต็ม ลูกจ้างคิดจากฐานที่หักจริง)</summary>
    public const decimal PairTolerance = 1m;

    /// <summary>บีบฐานให้อยู่ในกรอบกฎหมาย [1,650, เพดานของปีนั้น]</summary>
    public static decimal Clamp(decimal wage, decimal ceiling)
        => Math.Max(MinBase, Math.Min(wage, ceiling));

    // ══════════════════════════════════════════════════════════════════════
    //  "ค่าจ้าง" ตาม ม.5 — รายการไหนเข้าฐานเงินสมทบบ้าง (คำตัดสิน Q1 รอบ 183)
    //
    //  ม.5 นิยาม "ค่าจ้าง" = เงินที่นายจ้างจ่ายให้ลูกจ้าง **เป็นค่าตอบแทนการ
    //  ทำงาน** ⇒ ไม่ใช่เงินเดือนพื้นฐานอย่างเดียว:
    //    · เบี้ยขยัน · ค่าตำแหน่ง · ค่าครองชีพ ที่จ่ายประจำทุกเดือนไม่ว่าจะมา
    //      ทำงานหรือไม่  → **เป็นค่าจ้าง** (แนวคำพิพากษาฎีกา)
    //    · ค่าเดินทาง/ค่าที่พัก **ตามที่จ่ายจริง** (ชดเชยค่าใช้จ่าย)
    //      → **ไม่ใช่ค่าจ้าง**
    //  เส้นแบ่งนี้ขึ้นกับ "ข้อตกลงการจ้างของบริษัทนั้น" ซึ่งระบบไม่มีทางรู้เอง
    //  จากชื่อรายการ ⇒ เป็น **ธงต่อรายการที่ HR ติ๊กเอง**
    //
    //  ทำไม `bool?` ไม่ใช่ `bool` (G3 "ไม่รู้ต้องเป็นค่าของมันเอง"):
    //    · `null` = **ยังไม่มีใครตัดสิน** ⇒ ไม่รวม = พฤติกรรมเดิมเป๊ะ ⇒ ไม่มี
    //      ลูกค้ารายไหนยอดสมทบขยับจนกว่าจะติ๊กเอง (ตัวเลขที่ยื่น สปส. ไปแล้ว
    //      ต้องไม่เปลี่ยนเพราะเราอัปเดตระบบ)
    //    · `false` = **ตัดสินแล้วว่าไม่ใช่ค่าจ้าง** — ต่างจาก `null` ตรงที่
    //      ไม่ต้องขึ้นคำเตือนอีก (G3b "ไม่รู้" ≠ "ยังไม่ได้ตรวจ")
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>รายการเงินได้นี้นับเป็น "ค่าจ้าง" ตาม ม.5 หรือไม่ —
    /// เฉพาะ <c>true</c> เท่านั้นที่เข้าฐานเงินสมทบ (<c>null</c> = ยังไม่ตัดสิน)</summary>
    public static bool CountsAsWage(bool? countsForSsoBase) => countsForSsoBase == true;

    /// <summary>รายการนี้ยังรอให้ HR ตัดสินอยู่ไหม — ใช้สร้าง**คำเตือน** บนหน้า
    /// รายการเงินเดือน/ผลการคำนวณ
    ///
    /// <para>ทิศของความเสียหาย (G5): นำส่งขาดเพราะไม่รวมเบี้ยเลี้ยงที่จริง ๆ
    /// เป็นค่าจ้าง = สปส. ประเมินย้อนหลัง + เงินเพิ่ม 2%/เดือน (ม.49) ซึ่ง
    /// **มองไม่เห็นจนถึงวันตรวจ** ⇒ ความเงียบต้องถูกทำให้ดัง แม้ค่าตั้งต้นจะ
    /// เป็น "ไม่รวม" ก็ตาม</para></summary>
    public static bool NeedsWageDecision(bool? countsForSsoBase) => countsForSsoBase is null;

    /// <summary>แปลงค่าที่ฟอร์มส่งมา (<c>"true"</c>/<c>"false"</c>/<c>"unset"</c>)
    /// เป็นธงสามสถานะ — คืน <c>false</c> แปลว่า "ไม่ได้ส่งมา ⇒ ห้ามแตะของเดิม"
    ///
    /// <para>ทำไมไม่รับ <c>bool?</c> ตรง ๆ: ใน JSON ค่า <c>null</c> ที่ "ตั้งใจส่ง"
    /// กับ "ไม่ได้ส่ง" หน้าตาเหมือนกันทุกประการ ⇒ ผู้ใช้ที่ติ๊กผิดแล้วอยากกลับไป
    /// "ยังไม่ระบุ" จะกดบันทึกแล้ว**ไม่มีอะไรเกิดขึ้น** (silent no-op — กฎเหล็ก
    /// #4 A) · สามสถานะจึงต้องมีสามคำ</para></summary>
    public static bool TryParseWageDecision(string? raw, out bool? value)
    {
        value = null;
        switch ((raw ?? "").Trim().ToLowerInvariant())
        {
            case "true": value = true; return true;
            case "false": value = false; return true;
            case "unset": case "null": value = null; return true;
            default: return false;
        }
    }

    /// <summary>คำอธิบายผลของธงนี้สำหรับหน้าตั้งค่า — **เซิร์ฟเวอร์เป็นเจ้าของ
    /// ถ้อยคำ** หน้าเว็บห้ามเขียนตารางกติกาเอง (หลักการ 10 ข้อ #5)</summary>
    public static string DescribeWageDecision(bool? countsForSsoBase) => countsForSsoBase switch
    {
        true => "นับเป็นค่าจ้างตาม ม.5 — รวมในฐานคำนวณเงินสมทบประกันสังคม "
              + "(ลูกจ้าง 5% + นายจ้าง 5% ภายในเพดานของปีนั้น) และฐานกองทุนเงินทดแทน",
        false => "ไม่ใช่ค่าจ้างตาม ม.5 (เงินชดเชยค่าใช้จ่ายที่จ่ายตามจริง เช่น "
               + "ค่าเดินทาง/ค่าที่พัก) — ไม่เข้าฐานเงินสมทบ",
        _ => "⚠️ ยังไม่ระบุ — ระบบ**ไม่รวม**ในฐานเงินสมทบ (เท่าพฤติกรรมเดิม) "
           + "แต่ถ้ารายการนี้จ่ายประจำทุกเดือนไม่ว่าจะมาทำงานหรือไม่ (เบี้ยขยัน · "
           + "ค่าตำแหน่ง · ค่าครองชีพ) ตาม ม.5 ถือเป็นค่าจ้างที่ต้องนำส่ง — "
           + "นำส่งขาดจะถูก สปส. ประเมินย้อนหลังพร้อมเงินเพิ่ม 2%/เดือน (ม.49)",
    };

    /// <summary>ฐานค่าจ้างก่อน clamp ของงวดหนึ่ง = เงินเดือนที่จ่ายจริงในงวดนี้
    /// (เฉลี่ยตามวันที่เป็นลูกจ้างแล้ว) + เบี้ยเลี้ยงที่ติ๊กว่าเป็นค่าจ้าง
    ///
    /// <para>แยกเป็นฟังก์ชันเพื่อให้ "ฐานของ ปกส." กับ "ฐานของกองทุนเงินทดแทน"
    /// อ่านนิยาม ม.5 ตัวเดียวกัน (เพดานคนละตัวจึง clamp คนละที่)</para></summary>
    public static decimal GrossWage(decimal proratedBaseSalary, decimal wageAllowances)
        => Math.Max(0m, proratedBaseSalary + wageAllowances);

    // ══════════════════════════════════════════════════════════════════════
    //  D-02 (คำตัดสินเจ้าของ #35 รอบ 193) — "ลาไม่รับค่าจ้าง" ต้องออกจากฐานด้วย
    //
    //  บั๊กจริง: เงินเดือน 20,000 ลาไม่รับค่าจ้างทั้งเดือน (หรือลาคลอดวันที่
    //  46–98 ที่นายจ้างไม่จ่าย) → แถวในรอบแสดง **รายได้ 0 · หัก ปกส. 875 ·
    //  สุทธิ −875** และไฟล์ สปส.1-10 ประกาศค่าจ้าง 17,500 ในเดือนที่ไม่ได้จ่าย
    //  ค่าจ้างเลย · ฐานภาษีหัก leaveDeduction แล้ว แต่ฐาน ปกส./กองทุนเงินทดแทน
    //  ไม่หัก — "แก้ครึ่งเดียว" ของ D-S3 (เฉลี่ยตามวันเข้า/ออก กับลาไม่รับค่าจ้าง
    //  คือคำถามเดียวกัน: "เดือนนี้จ่ายค่าจ้างจริงเท่าไร" ตาม ม.5)
    //
    //  สองกติกาที่ต้องมาคู่กัน:
    //   1) เงินเดือนที่จ่ายจริง = เงินเดือนเฉลี่ยตามวัน − หักลาไม่รับค่าจ้าง (≥ 0)
    //   2) **ไม่ได้จ่ายอะไรเลย = ฐาน 0 ไม่ใช่ 1,650** — ขั้นต่ำ 1,650 ของ ม.33 ใช้กับ
    //      ค่าจ้างที่จ่ายจริงแต่ต่ำกว่าเกณฑ์ ไม่ใช่เสกค่าจ้างให้เดือนที่ไม่ได้จ่าย
    //      (เงินสมทบ "หักจากค่าจ้างทุกครั้งที่จ่าย" ม.47 — ไม่จ่าย = ไม่มีอะไรให้หัก)
    //      ถ้าลืมข้อ 2 ข้อ 1 จะเปลี่ยนแค่ 875 → 82.50 และสุทธิยังติดลบอยู่ดี
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>เงินเดือนที่จ่ายจริงในงวด = เงินเดือนที่เฉลี่ยตามวันที่เป็นลูกจ้าง
    /// − ยอดหักลาไม่รับค่าจ้าง · ไม่ติดลบ (ลาเกินวันที่เป็นลูกจ้างต้องไม่ไป
    /// "กิน" เบี้ยเลี้ยงที่เป็นค่าจ้าง)</summary>
    public static decimal SalaryPaidThisPeriod(decimal proratedBaseSalary, decimal unpaidLeaveDeduction)
        => Math.Max(0m, proratedBaseSalary - Math.Max(0m, unpaidLeaveDeduction));

    /// <summary>ฐานเงินสมทบของงวด — **ไม่ได้จ่ายอะไรเลยในงวดนี้** (ค่าจ้าง ≤ 0 และรายได้
    /// รวมที่จ่าย ≤ 0) = ฐาน 0 · นอกนั้น = บีบเข้ากรอบ [1,650, เพดาน] ตาม <see cref="Clamp"/>
    /// เหมือนเดิมทุกบาท
    ///
    /// <para>ทำไมต้องดู <paramref name="totalPaidThisPeriod"/> ด้วย ไม่ใช่ค่าจ้างอย่างเดียว:
    /// ฐานค่าจ้างของระบบนับแค่เงินเดือน + เบี้ยเลี้ยงที่ติ๊กว่าเป็นค่าจ้าง — พนักงานที่
    /// เงินเดือนฐาน 0 แต่ได้คอมมิชชัน/OT จริงมี "ค่าจ้าง" ตาม ม.5 ที่ระบบยังไม่นับ ⇒
    /// ถ้าตัดเป็น 0 ตามค่าจ้างที่ระบบเห็น จะนำส่ง<b>ขาด</b>เงียบ ๆ (ทิศที่มองไม่เห็นจนวันตรวจ
    /// — DOCTRINE G5) · คงขั้นต่ำ 1,650 ไว้เหมือนเดิมเมื่อมีเงินจ่ายออกไปจริง</para></summary>
    /// <param name="totalPaidThisPeriod">รายได้รวมที่จ่ายในงวด (GrossIncome ของแถว)</param>
    public static decimal PeriodBase(decimal statutoryWage, decimal ceiling, decimal totalPaidThisPeriod)
        => statutoryWage <= 0m && totalPaidThisPeriod <= 0m ? 0m : Clamp(statutoryWage, ceiling);

    /// <summary>เงินสมทบจากฐาน — ปัด 2 ตำแหน่งแบบ AwayFromZero (ไม่ใช่ banker's
    /// rounding) และไม่เกินเพดานสมทบของปีนั้น</summary>
    public static decimal Contribution(decimal baseWage, decimal rate, decimal maxContribution)
        => Math.Min(Math.Round(baseWage * rate, 2, MidpointRounding.AwayFromZero), maxContribution);

    /// <summary>ฐานที่ควรใช้กับแถวนี้ตอนอ่าน (export/รายงาน)
    ///
    /// <para><paramref name="storedBase"/> &gt; 0 = ผู้ใช้/engine ตั้งไว้แล้ว → ใช้เลย
    /// (เป็นความจริงที่ถูกตรึงพร้อมยอดสมทบ)</para>
    ///
    /// <para>= 0 คือแถวเก่าที่เกิดก่อนมีช่องนี้ — **อนุมานจากเงินสมทบที่หักจริง**
    /// (<c>สมทบ ÷ อัตรา</c>) ไม่ใช่จาก <c>GrossIncome</c> เพราะยอดสมทบคือสิ่งที่
    /// นำส่งจริงและเป็นตัวเลขที่ต้องตรงกับช่องค่าจ้างบนไฟล์. ถ้าสมทบชนเพดาน
    /// (สมทบ = เพดานสมทบ) การหารกลับจะได้แค่เพดานค่าจ้าง ซึ่งเป็นคำตอบที่ถูก
    /// อยู่แล้วสำหรับ สปส. — แต่คืน <paramref name="grossIncome"/> ที่ยังไม่ cap
    /// เพราะแบบฟอร์ม Excel ของ e-Service ให้กรอก "ค่าจ้างจริง" ไม่ cap</para>
    ///
    /// <para>สมทบ = 0 (ไม่ได้อยู่ในระบบ ปกส.) → คืน 0 ไม่เดา</para></summary>
    public static decimal Resolve(decimal storedBase, decimal employeeContribution,
        decimal grossIncome, decimal rate, decimal maxContribution)
    {
        if (storedBase > 0) return storedBase;
        if (employeeContribution <= 0 || rate <= 0) return 0m;
        // ชนเพดาน → ค่าจ้างจริงสูงกว่าเพดาน หารกลับไม่ได้ความจริง ใช้ยอดจริง
        if (employeeContribution >= maxContribution - 0.005m) return grossIncome;
        // **รายได้รวมเข้ากันได้อยู่แล้ว → ใช้ตัวจริง อย่าไปแต่งใหม่**
        // แถวส่วนใหญ่คิดสมทบจาก gross ตรง ๆ (เช่น 12,953 → 648 ซึ่ง 5% ลงตัวใน
        // ระยะปัดเศษ) การหารกลับจะได้ 12,960 = เปลี่ยนค่าจ้างที่ประกาศโดยไม่จำเป็น
        // ⇒ ซ่อมเฉพาะแถวที่คู่ตัวเลข "ขัดกันจริง" เท่านั้น
        var ceilingFromMax = rate > 0 ? maxContribution / rate : grossIncome;
        if (grossIncome > 0
            && IsConsistent(grossIncome, employeeContribution, rate, ceilingFromMax, maxContribution))
            return grossIncome;
        return Math.Round(employeeContribution / rate, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>เงินสมทบ**ฝั่งนายจ้าง** ที่คู่กับฝั่งลูกจ้าง — คิดจากยอดลูกจ้าง
    /// ตามสัดส่วนอัตรา ไม่ใช่คำนวณจากฐานใหม่อีกรอบ
    ///
    /// <para>เหตุผล: ยอดที่ระบบนอกส่งมามักถูก**ปัดเป็นบาทถ้วน**แล้ว (เช่นฐาน
    /// 12,953 → 5% = 647.65 แต่หักจริง 648) ถ้าฝั่งนายจ้างไปคำนวณจากฐานใหม่จะได้
    /// 647.65 ⇒ สองฝั่งต่างกัน 0.35 บาททั้งที่ควรเท่ากัน. คิดจากยอดลูกจ้างตรง ๆ
    /// ทำให้ **อัตราเท่ากัน = ยอดเท่ากันเป๊ะ** เสมอ (กรณีปกติของ ม.33) และยังรองรับ
    /// ช่วงที่กฎหมายกำหนดอัตราสองฝั่งไม่เท่ากัน (เช่นประกาศลดชั่วคราว)</para></summary>
    public static decimal EmployerFrom(decimal employeeContribution, decimal rate,
        decimal employerRate, decimal employerMaxContribution)
    {
        if (employeeContribution <= 0 || rate <= 0) return 0m;
        if (employerRate == rate) return Math.Min(employeeContribution, employerMaxContribution);
        return Math.Min(
            Math.Round(employeeContribution * (employerRate / rate), 2, MidpointRounding.AwayFromZero),
            employerMaxContribution);
    }

    /// <summary>คู่ (ค่าจ้าง, เงินสมทบ) บนไฟล์ตรงกันไหม — เผื่อปัดเศษ ±1 บาท
    ///
    /// <para>ใช้เป็น **ด่านก่อนยื่น**: สปส. e-Service คำนวณ 5% จากค่าจ้างที่กรอก
    /// แล้วเทียบกับเงินสมทบ ไม่ตรง = ตีกลับทั้งแถว. เตือนที่ระบบเราก่อนดีกว่า
    /// ให้ผู้ใช้ไปเจอตอนอัปโหลด</para></summary>
    public static bool IsConsistent(decimal wageOnFile, decimal contribution,
        decimal rate, decimal ceiling, decimal maxContribution)
    {
        // ⚠️ ต้องใช้ค่าคงที่ตัวเดียวกับ Normalize — ตัวเลขคู่เดียวกันถูกตัดสินสอง
        // จังหวะ (ตอนเขียนยอด กับตอนตรวจก่อนยื่น) ถ้าเกณฑ์ต่างกันแม้นิดเดียว
        // จะเกิดช่วงที่ "ผ่านตอนเขียน แต่ตกตอนยื่น" โดยไม่มีใครแก้ให้ได้
        var expected = Contribution(Clamp(wageOnFile, ceiling), rate, maxContribution);
        return Math.Abs(expected - contribution) <= PairTolerance;
    }

    /// <summary>ทำให้ "ฐาน · ลูกจ้าง · นายจ้าง" ของแถวหนึ่งสอดคล้องกัน — **ตัวตัดสิน
    /// ตัวเดียว** ที่ทุกจุดเขียนยอดประกันสังคมต้องเรียก
    ///
    /// <para>ที่มา (บั๊กจริง): ยอดนายจ้างถูกเขียนจาก 4 ทางแต่มีแค่ 3 ทางที่ผูกกับ
    /// ฝั่งลูกจ้าง — ทางที่ 4 คือ <b>การนำเข้ารอบเงินเดือนจากระบบนอก (TakeTime)</b>
    /// ซึ่งคัดค่ามาดิบ ๆ ไม่เคยตรวจ ⇒ TakeTime คิดฝั่งนายจ้างจากค่าจ้างเต็ม
    /// ส่วนฝั่งลูกจ้างคิดจากฐานที่หักจริง ⇒ ต่างกัน 22 บาท (4,381 vs 4,403)
    /// ติดมากับรอบตั้งแต่วินาทีแรกและไม่มีอะไรซ่อมให้เลย</para>
    ///
    /// <para><b>ฝั่งลูกจ้างเป็นความจริง</b> (เป็นยอดที่หักจากเงินเดือนพนักงานไป
    /// จริงและตรงกับสลิปที่จ่ายไปแล้ว) — ฐานและฝั่งนายจ้างเป็นผลลัพธ์ที่คิดตาม
    /// ห้ามทำกลับทาง</para>
    ///
    /// <para>ไม่แตะแถวที่ไม่ได้อยู่ในระบบประกันสังคม (สองฝั่งเป็น 0)</para>
    /// </summary>
    /// <returns>ค่าที่ควรเป็น + ธงแยกว่าอะไรเปลี่ยน + <see cref="SsoPairResult.Conflict"/>
    /// เมื่อระบบ**ตัดสินแทนไม่ได้** (ผู้เรียกต้องคงค่าเดิมไว้แล้วบอกผู้ใช้)</returns>
    public static SsoPairResult Normalize(
        decimal storedBase, decimal employeeContribution, decimal grossIncome,
        decimal employerOnFile, decimal rate, decimal maxContribution,
        decimal employerRate, decimal employerMaxContribution)
    {
        // ไม่อยู่ในระบบประกันสังคม (สองฝั่งเป็น 0) — ไม่แตะ
        if (employeeContribution <= 0 && employerOnFile <= 0)
            return new SsoPairResult(storedBase, employerOnFile, false, false, null);

        // ⚠️ ฝั่งลูกจ้าง = 0 แต่ฝั่งนายจ้างมียอด — **ห้ามล้างฝั่งนายจ้างเป็น 0**
        // (เวอร์ชันแรกทำแบบนั้นเพราะสูตรคิดจากฝั่งลูกจ้างล้วน ⇒ หนี้สินเงินสมทบ
        // หายไปจาก JE เงียบ ๆ และไฟล์ สปส.1-10 ก็กรองแถวนี้ออก ⇒ นำส่งขาด
        // + เงินเพิ่ม §49) ระบบไม่มีทางรู้ว่าที่ถูกคือ "เติมฝั่งลูกจ้าง" หรือ
        // "ลบฝั่งนายจ้าง" ⇒ คงของเดิมไว้แล้วให้คนตัดสิน
        // ด่านที่บล็อกจริงอยู่ที่ `StatutoryRemittanceService.RemitAsync`
        // (RuleCode `SSO-PAIR-CONFLICT`) — **เพิ่งต่อสายเมื่อ 2026-09-16**: ก่อนหน้านี้
        // คอมเมนต์บรรทัดนี้เขียนว่า "ด่านตอนนำส่งจะบล็อกให้เองอยู่แล้ว" ทั้งที่
        // `grep` ทั้ง RemitAsync ไม่มีคำว่า Conflict/IsConsistent/SsoWageBase เลย
        // ⇒ เป็น doc-comment ที่โกหกคนอ่านมาตลอด (defect class ของ CLAUDE.md:
        // "ด่านที่ doc บอกว่ามี ≠ ด่านที่ถูกเรียก") — ถ้าย้าย/ลบด่านนั้น ต้องแก้
        // บรรทัดนี้ในคอมมิตเดียวกัน
        if (employeeContribution <= 0)
            return new SsoPairResult(storedBase, employerOnFile, false, false,
                $"ฝั่งลูกจ้างเป็น 0 แต่ฝั่งนายจ้างมี {employerOnFile:N2} บาท — "
                + "ม.33 ให้สมทบจากฐานค่าจ้างเดียวกันทั้งสองฝั่ง กรุณาตรวจว่าลืมกรอก"
                + "ยอดฝั่งลูกจ้าง หรือพนักงานคนนี้ไม่ต้องสมทบ (แก้ที่ \"แก้ยอด\" รายคน)");

        // ⚠️ ฝั่งลูกจ้างเองต้องอยู่ในกรอบกฎหมายก่อน จึงจะใช้เป็น "ความจริง" ได้
        // — ไม่งั้นความผิดพลาดของการหัก (หักขาด/หักเกิน ม.47) จะถูกแปลงเป็น
        // "ฐานค่าจ้างที่ประกาศต่อ สปส." ที่ไม่ตรงความจริงแทน
        var minContribution = Contribution(MinBase, rate, maxContribution);
        if (employeeContribution > maxContribution + 0.005m)
            return new SsoPairResult(storedBase, employerOnFile, false, false,
                $"ยอดสมทบฝั่งลูกจ้าง {employeeContribution:N2} เกินเพดาน {maxContribution:N2} "
                + "บาท/เดือน (ม.46) — หักเกินต้องคืนลูกจ้าง ไม่ใช่ปรับฐานตาม");
        if (employeeContribution < minContribution - 0.005m)
            return new SsoPairResult(storedBase, employerOnFile, false, false,
                $"ยอดสมทบฝั่งลูกจ้าง {employeeContribution:N2} ต่ำกว่าขั้นต่ำ {minContribution:N2} "
                + $"บาท/เดือน (ฐานขั้นต่ำ {MinBase:N0} ตาม ม.33) — กรุณาตรวจยอดที่หักจริง");

        var resolvedBase = Resolve(storedBase, employeeContribution, grossIncome, rate, maxContribution);
        var expectedEmployer = EmployerFrom(employeeContribution, rate, employerRate, employerMaxContribution);

        // แยกสองธง: การเติมฐานย้อนหลังให้แถวเก่า **ต้องไม่ไปขยับยอดนายจ้างที่
        // ลงตัวอยู่แล้ว** (ญาติของบทเรียน "ซ่อมเฉพาะแถวที่พังจริง" — การหารกลับ
        // หาฐานแล้วเขียนทับทุกแถว จะเปลี่ยนค่าจ้างที่ประกาศของแถวที่ถูกอยู่แล้ว)
        // และผู้เรียกต้องแยกสองเรื่องนี้ตอนบอกผู้ใช้ ไม่งั้นข้อความจะบอกว่า
        // "ปรับยอดนายจ้าง N คน" ทั้งที่ไม่มีบาทเดียวเปลี่ยน (ป้ายไม่ซื่อสัตย์)
        var baseChanged = resolvedBase > 0 && storedBase != resolvedBase;
        var employerChanged = Math.Abs(expectedEmployer - employerOnFile) > PairTolerance;

        return new SsoPairResult(
            resolvedBase > 0 ? resolvedBase : storedBase,
            employerChanged ? expectedEmployer : employerOnFile,
            baseChanged, employerChanged, null);
    }
}

/// <summary>ผลการทำให้คู่ (ฐาน · ลูกจ้าง · นายจ้าง) สอดคล้องกัน</summary>
/// <param name="Base">ฐานค่าจ้างที่ควรบันทึก</param>
/// <param name="Employer">ยอดฝั่งนายจ้างที่ควรบันทึก</param>
/// <param name="BaseFilled">เติม/แก้ฐานย้อนหลังให้แถวเก่า (ยอดเงินไม่เปลี่ยน)</param>
/// <param name="EmployerAdjusted">**ยอดเงิน**ฝั่งนายจ้างถูกปรับจริง</param>
/// <param name="Conflict">ไม่ null = ระบบตัดสินแทนไม่ได้ ผู้เรียกต้องคงค่าเดิม
/// ไว้ทั้งหมดแล้วแจ้งข้อความนี้ให้ผู้ใช้ (ห้ามเดาแทน — "ค่าที่แต่งขึ้นอันตราย
/// กว่าการไม่ตอบ")</param>
public sealed record SsoPairResult(
    decimal Base, decimal Employer, bool BaseFilled, bool EmployerAdjusted, string? Conflict)
{
    /// <summary>มีอะไรต้องเขียนลงแถวไหม</summary>
    public bool Changed => BaseFilled || EmployerAdjusted;
}
