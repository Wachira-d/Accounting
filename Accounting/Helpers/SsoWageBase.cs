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
        var expected = Contribution(Clamp(wageOnFile, ceiling), rate, maxContribution);
        return Math.Abs(expected - contribution) <= 1m;
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
        // (ด่านตอนนำส่งจะบล็อกให้เองอยู่แล้ว — เงินไม่ออกไปผิด)
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
