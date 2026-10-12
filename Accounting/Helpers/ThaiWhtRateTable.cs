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
    /// <param name="CertificateRowText">ข้อความ "ระบุ" ที่พิมพ์ต่อท้ายแถว 5/6 ของ 50 ทวิ ให้หนังสือรับรองบอกว่า
    /// <b>จ่ายค่าอะไร</b> (แถว 5 ของแบบเป็นรายการ "เช่น …" รวมสิบกว่าประเภท — ไม่ระบุ = ผู้รับ/สรรพากรอ่านไม่ออก) ·
    /// null = ไม่พิมพ์ (แถว 1–4 ชื่อแถวเฉพาะเจาะจงอยู่แล้ว · รหัสรวม "8" เป็นค่าตั้งต้นของเอกสารที่ไม่ได้ระบุประเภท
    /// ⇒ ห้ามพิมพ์ "ค่าจ้างทำของ" ลงใบที่อาจเป็นค่าเบี้ยประกัน — ค่าที่แต่งขึ้นอันตรายกว่าการไม่ระบุ · หลักการ #3)</param>
    /// <param name="PndFileCode">รหัสช่อง Col7 ของไฟล์นำเข้า ภ.ง.ด. (ผู้อ่านเดียว <see cref="PndIncomeTypeCode.ForFile"/>)</param>
    /// <param name="LegalReference">ตัวบทที่ให้อัตรานี้ — ใช้ในข้อความเตือนอัตราไม่ตรงประเภท</param>
    public sealed record IncomeType(
        string Code,
        string Name,
        string TaxSection,
        decimal? IndividualRate,
        decimal? JuristicRate,
        IReadOnlyList<string> ApplicableForms,
        string? Note = null,
        string? CertificateRowText = null,
        string PndFileCode = "6",
        string LegalReference = "ม.3 เตรส · ท.ป.4/2528");

    /// <summary>ทุกประเภทที่ระบบรองรับ เรียงตามลำดับมาตรา
    ///
    /// <para>⚠️ <b>ลำดับมีความหมาย</b>: <see cref="Find"/> ด้วยเลขมาตรา ("40(8)") คืน<b>แถวแรก</b>ของมาตรานั้น
    /// ⇒ แถวรวมของแต่ละมาตรา ("8" ค่าจ้างทำของ/ค่าบริการ) ต้องมาก่อนประเภทย่อยเสมอ (ข้อมูลเก่าที่เก็บเป็นเลขมาตรา
    /// ต้องได้คำตอบเดิม)</para>
    ///
    /// <para>รอบ 203 (คำถามเจ้าของ "หักค่าเบี้ยประกันภัย 1% ลงถูกช่องไหม"): เพิ่มประเภทตามคำสั่ง ม.3 เตรส ที่แบบ 50 ทวิ
    /// แถว 5 ระบุชื่อไว้เองแต่ตารางไม่มี — ค่าเบี้ยประกันวินาศภัย · รางวัลส่งเสริมการขาย · รางวัลประกวด/ชิงโชค ·
    /// ค่าแสดงของนักแสดงสาธารณะ + เงินได้ที่หน่วยงานรัฐหัก 1% (ม.69 ทวิ · ฝั่งเครดิตภาษีที่ถูกหัก) —
    /// ข้อที่ยังต้องยืนยันกับนักบัญชีอยู่ใน erp-review/2026-09-29/DECISIONS.md (รอเจ้าของ/นักบัญชียืนยัน)</para></summary>
    public static readonly IReadOnlyList<IncomeType> All = new[]
    {
        new IncomeType("1", "เงินเดือน ค่าจ้าง บำนาญ", "40(1)",
            IndividualRate: null, JuristicRate: null,
            new[] { "ภ.ง.ด.1" },
            Note: "อัตราขั้นบันไดตามฐานภาษีเงินได้บุคคลธรรมดา — ไม่มีอัตราคงที่",
            PndFileCode: "1", LegalReference: "ม.50(1) (อัตราก้าวหน้า)"),
        new IncomeType("2", "ค่านายหน้า/ค่าบริการ", "40(2)",
            IndividualRate: 3m, JuristicRate: 3m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" },
            PndFileCode: "2"),
        new IncomeType("3", "ค่าแห่งลิขสิทธิ์/ค่าสิทธิ", "40(3)",
            IndividualRate: 3m, JuristicRate: 3m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" },
            PndFileCode: "3"),
        new IncomeType("4a", "ดอกเบี้ย", "40(4)(ก)",
            IndividualRate: 15m, JuristicRate: 1m,
            new[] { "ภ.ง.ด.2", "ภ.ง.ด.53" },
            Note: "บุคคลธรรมดา 15% · นิติบุคคลไทย 1%",
            PndFileCode: "4A", LegalReference: "ม.50(2) (บุคคล 15%) · ท.ป.4/2528 (นิติบุคคล 1%)"),
        new IncomeType("4b", "เงินปันผล", "40(4)(ข)",
            IndividualRate: 10m, JuristicRate: 10m,
            new[] { "ภ.ง.ด.2", "ภ.ง.ด.53" },
            PndFileCode: "4B", LegalReference: "ม.50(2) (บุคคล) · ท.ป.4/2528 (นิติบุคคล)"),
        new IncomeType("5", "ค่าเช่าทรัพย์สิน", "40(5)",
            IndividualRate: 5m, JuristicRate: 5m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" },
            CertificateRowText: "ค่าเช่าทรัพย์สิน", PndFileCode: "5"),
        new IncomeType("6", "ค่าวิชาชีพอิสระ", "40(6)",
            IndividualRate: 3m, JuristicRate: 3m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" },
            CertificateRowText: "ค่าวิชาชีพอิสระ", PndFileCode: "6"),
        new IncomeType("7", "ค่ารับเหมา", "40(7)",
            IndividualRate: 3m, JuristicRate: 3m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" },
            CertificateRowText: "ค่ารับเหมา", PndFileCode: "6"),       // §3 เตรส ค่าจ้างทำของ (ดู PndIncomeTypeCode)
        new IncomeType("8", "ค่าจ้างทำของ/ค่าบริการอื่น", "40(8)",
            IndividualRate: 3m, JuristicRate: 3m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" },
            // CertificateRowText = null โดยตั้งใจ: "8" คือค่าตั้งต้นของใบที่ไม่ได้ระบุประเภท (WithholdingTaxCertService
            // AutoGenerateFromDocumentAsync) ⇒ พิมพ์ "ค่าจ้างทำของ" ลงใบที่อาจเป็นค่าเบี้ยประกัน = ข้อความที่แต่งขึ้น
            PndFileCode: "6"),
        new IncomeType("8ad", "ค่าโฆษณา", "40(8)",
            IndividualRate: 2m, JuristicRate: 2m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" },
            CertificateRowText: "ค่าโฆษณา", PndFileCode: "6"),
        new IncomeType("8tr", "ค่าขนส่ง (ไม่ใช่ขนส่งสาธารณะ)", "40(8)",
            IndividualRate: 1m, JuristicRate: 1m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" },
            CertificateRowText: "ค่าขนส่ง", PndFileCode: "6"),
        // ── รอบ 203: ประเภทที่แบบ 50 ทวิ แถว 5 ระบุชื่อเองแต่ตารางเดิมไม่มี ──
        new IncomeType("8ins", "ค่าเบี้ยประกันวินาศภัย", "40(8)",
            // ผู้รับต้องเป็นบริษัทประกันวินาศภัย (นิติบุคคล) — บุคคลธรรมดาไม่มีอัตรา (null ⇒ ให้คนกรอก ห้ามยืมอัตรานิติบุคคล)
            IndividualRate: null, JuristicRate: 1m,
            new[] { "ภ.ง.ด.53" },
            Note: "เฉพาะประกันวินาศภัย (รถยนต์ อัคคีภัย ทรัพย์สิน ฯลฯ) — เบี้ยประกันชีวิตไม่อยู่ในประเภทนี้",
            CertificateRowText: "ค่าเบี้ยประกันวินาศภัย", PndFileCode: "6"),
        new IncomeType("8pr", "รางวัล/ส่วนลด/ประโยชน์จากการส่งเสริมการขาย", "40(8)",
            IndividualRate: 3m, JuristicRate: 3m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" },
            CertificateRowText: "รางวัล ส่วนลดหรือประโยชน์ใด ๆ เนื่องจากการส่งเสริมการขาย", PndFileCode: "6"),
        new IncomeType("8cp", "รางวัลในการประกวด การแข่งขัน การชิงโชค", "40(8)",
            IndividualRate: 5m, JuristicRate: 5m,
            new[] { "ภ.ง.ด.3", "ภ.ง.ด.53" },
            CertificateRowText: "รางวัลในการประกวด การแข่งขัน การชิงโชค", PndFileCode: "6"),
        new IncomeType("8ent", "ค่าแสดงของนักแสดงสาธารณะ (ผู้มีถิ่นที่อยู่ในประเทศไทย)", "40(8)",
            // นักแสดงเป็นบุคคลธรรมดา — จ่ายผ่านบริษัทจัดหา = ค่าบริการ "8" ไม่ใช่ประเภทนี้
            IndividualRate: 5m, JuristicRate: null,
            new[] { "ภ.ง.ด.3" },
            Note: "นักแสดงที่ไม่มีถิ่นที่อยู่ในประเทศไทยใช้เกณฑ์อื่น — ต้องให้ผู้ทำบัญชีกำหนด",
            CertificateRowText: "ค่าแสดงของนักแสดงสาธารณะ", PndFileCode: "6"),
        new IncomeType("gov1", "เงินได้ที่ส่วนราชการ/องค์การของรัฐจ่าย (หัก 1%)", "ม.69 ทวิ",
            // ฝั่ง "เราถูกหัก" (ทะเบียนเครดิตภาษี) — ผู้จ่ายเป็นหน่วยงานรัฐ · บริษัทเอกชนห้ามใช้ประเภทนี้ออก 50 ทวิ ของตัวเอง
            IndividualRate: null, JuristicRate: 1m,
            new[] { "ภ.ง.ด.53" },
            Note: "ใช้เมื่อผู้จ่ายเป็นส่วนราชการ/องค์การของรัฐ/เทศบาล เท่านั้น — บริษัทเอกชนห้ามเลือกประเภทนี้",
            CertificateRowText: "เงินได้ที่หน่วยงานของรัฐจ่าย (หักร้อยละ 1 ตาม ม.69 ทวิ)", PndFileCode: "6",
            LegalReference: "ป.รัษฎากร ม.69 ทวิ"),
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

    /// <summary>
    /// หาประเภทจาก<b>ค่าที่เก็บไว้ทุกรูปที่พบจริงในเรพ</b> — รหัส ("8ad") · เลขมาตรา ("40(8)") ·
    /// ละตินของมาตรา ("40(4)(a)" / "40(4)a") · และรูป "มาตรา + ชื่อ" ที่ตัวอ่าน 50 ทวิ ที่ได้รับ
    /// (<see cref="WhtCertIncomeType.Infer"/>) เขียนลง <c>WhtCreditReceived.IncomeTypeCode</c>
    /// ("40(8) ค่าโฆษณา") — คืน null เมื่อชี้ขาดไม่ได้ (ห้ามเดา)
    ///
    /// <para>ที่มา (รอบ 203): <c>WhtCreditService.CheckRate</c> เคยถือตารางอัตราชุดที่สองจับคำเอง และหน้า
    /// wht-credit.html เก็บค่า <b>เลขมาตรา</b> ⇒ เลือก "ค่าโฆษณา 2%" แล้วเก็บเป็น "40(8)" ⇒ ตัวตรวจคาด 3% ⇒
    /// เตือนใบที่ถูก · ทุกผู้อ่านค่าที่เก็บไว้ต้องผ่านตัวนี้ตัวเดียว</para>
    /// </summary>
    public static IncomeType? Resolve(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        var direct = Find(stored);
        if (direct != null) return direct;

        var compact = stored.Trim();
        var m = System.Text.RegularExpressions.Regex.Match(compact,
            @"^\s*(?<sec>40\s*\(\s*\d\s*\)(?:\s*\(\s*[กขab]\s*\)|[ab](?![A-Za-z]))?)\s*(?<rest>.*)$");
        if (!m.Success)
            return null;
        var sec = m.Groups["sec"].Value.Replace(" ", "");
        // ละติน → ไทย (ตารางเก็บ (ก)/(ข))
        sec = sec.Replace("(a)", "(ก)").Replace("(b)", "(ข)");
        if (sec.EndsWith(")a", StringComparison.Ordinal)) sec = sec[..^1] + "(ก)";
        if (sec.EndsWith(")b", StringComparison.Ordinal)) sec = sec[..^1] + "(ข)";
        var rest = m.Groups["rest"].Value.Trim();

        var sameSection = All.Where(t => string.Equals(t.TaxSection, sec, StringComparison.Ordinal)).ToList();
        if (sameSection.Count == 0) return null;
        if (rest.Length >= 4)
        {
            // ประเภทย่อยก่อน (เจาะจงกว่า) — "ค่าโฆษณา" ต้องได้ 8ad ไม่ใช่แถวรวม 8 · ข้ามแถวแรก (แถวรวมของมาตรา)
            foreach (var t in sameSection.Skip(1))
            {
                if ((t.CertificateRowText != null && rest.Contains(t.CertificateRowText, StringComparison.Ordinal))
                    || t.Name.Contains(rest, StringComparison.Ordinal))
                    return t;
            }
        }
        return sameSection[0];
    }

    /// <summary>แถว 5/6 ของแบบ 50 ทวิ ที่ต้อง "ระบุ" ว่าจ่ายอะไร</summary>
    private static bool CertificateRowTakesSpecification(string row) => row is "5" or "6";

    /// <summary>แถวบนแบบ 50 ทวิ ที่มีช่อง "ระบุ" — ใช้สร้าง <c>CertificateRowSpecs</c></summary>
    public static readonly IReadOnlyList<string> CertificateRowsWithSpecification = new[] { "5", "6" };

    /// <summary>ความยาวสูงสุดของข้อความ "ระบุ" บนแถว — ช่องบนแบบมีที่บรรทัดเดียว</summary>
    public const int CertificateSpecificationMaxLength = 120;

    /// <summary>
    /// ข้อความ "ระบุ" ของ<b>ประเภทเงินได้หนึ่งรหัส</b> บนแถว 5/6 ของ 50 ทวิ — API ของ renderer (HTML/QuestPDF) ·
    /// รับค่าที่เก็บไว้ทุกรูป (ผ่าน <see cref="Resolve"/>)
    ///
    /// <para>⚠️ <b>null มีความหมาย = "อย่าพิมพ์ชื่อประเภท"</b> ห้ามผู้เรียกเติมด้วย <see cref="IncomeType.Name"/> เอง:
    /// รหัสรวม "8" คือค่าตั้งต้นของใบที่ไม่ได้ระบุประเภท (อาจเป็นค่าเบี้ยประกัน) และรหัสที่ไม่รู้จักต้องใช้คำอธิบายบรรทัด
    /// (ดู <see cref="CertificateRowSpecification"/>) — การเติมชื่อเองคือการพิมพ์ข้อความที่แต่งขึ้นลงเอกสารราชการ</para>
    /// </summary>
    public static string? CertificateRowText(string? code) => Resolve(code)?.CertificateRowText;

    /// <summary>
    /// ข้อความ "ระบุ" ของ<b>ทั้งแถว</b> <paramref name="row"/> บน 50 ทวิ จากบรรทัดของหนังสือรับรอง (หลายบรรทัดในแถวเดียว ⇒
    /// รวมชื่อไม่ซ้ำ) — ส่งออกทาง <c>WithholdingTaxCertResponse.CertificateRowSpecs</c> ให้หน้าพรีวิวแสดงอย่างเดียว
    ///
    /// <para>แถว 5: ชื่อประเภทจาก <see cref="IncomeType.CertificateRowText"/> (ไม่พิมพ์ถ้าเป็นรหัสรวม "8") ·
    /// แถว 6 "อื่น ๆ (ระบุ)": ชื่อประเภท หรือ<b>คำอธิบายบรรทัด</b>เมื่อรหัสไม่รู้จัก — แถว 6 ว่างคือแบบที่กรอกไม่ครบ ·
    /// แถว 1–4 คืน null (ชื่อแถวเจาะจงอยู่แล้ว)</para>
    /// </summary>
    public static string? CertificateRowSpecification(string row,
        IEnumerable<(string? Code, string? Description)> lines)
    {
        if (!CertificateRowTakesSpecification(row)) return null;
        var parts = new List<string>();
        foreach (var (code, description) in lines)
        {
            if (!string.Equals(CertificateRow(code), row, StringComparison.Ordinal)) continue;
            var text = CertificateRowText(code);
            if (string.IsNullOrWhiteSpace(text) && row == "6")
                text = description;
            text = text?.Trim();
            if (!string.IsNullOrEmpty(text) && !parts.Contains(text, StringComparer.Ordinal))
                parts.Add(text);
        }
        if (parts.Count == 0) return null;
        var joined = string.Join(", ", parts);
        return joined.Length <= CertificateSpecificationMaxLength
            ? joined
            : joined[..(CertificateSpecificationMaxLength - 1)] + "…";
    }

    /// <summary>
    /// <b>อัตราที่ใช้ตรงกับประเภทเงินได้ + ชนิดผู้รับไหม</b> — ตัวตัดสินเดียวของคำเตือน "อัตราไม่ตรงประเภท"
    /// (อนุมัติเอกสาร · ออก/แก้ 50 ทวิ ด้วยมือ · ทะเบียนภาษีถูกหัก) · คืนข้อความไทย หรือ null = ไม่ต้องเตือน
    ///
    /// <para>เตือนเท่านั้น ไม่บล็อก (ใบจริงอาจมีเหตุ เช่น อนุสัญญาภาษีซ้อน/คำสั่งเฉพาะ) · ไม่รู้ชนิดผู้รับ ⇒ ยอมรับอัตราของ
    /// ชนิดใดชนิดหนึ่ง (G3: ไม่รู้ ≠ ผิด) · อัตราลดชั่วคราวที่จ่าย<b>ในช่วง</b>ประกาศ ⇒ เงียบ (ตัวตัดสิน
    /// <see cref="ClassifyRate"/> ตัวเดิม) · ประเภทที่กฎหมายไม่กำหนดอัตราคงที่ (เงินเดือน) ⇒ เงียบ</para>
    /// </summary>
    /// <param name="code">รหัส/มาตราที่เก็บไว้ (ผ่าน <see cref="Resolve"/>)</param>
    /// <param name="ratePercent">อัตราที่ใช้ (%) — ≤ 0 = ไม่ได้หัก ⇒ เงียบ</param>
    /// <param name="payeeIsJuristic">ชนิดผู้รับ — null = ไม่รู้</param>
    /// <param name="paymentDate">วันที่จ่าย (สำหรับอัตราลดชั่วคราว)</param>
    public static string? CheckRateForType(string? code, decimal ratePercent, bool? payeeIsJuristic, DateTime paymentDate)
    {
        if (ratePercent <= 0m) return null;
        var t = Resolve(code);
        if (t == null) return null;
        var expected = new List<decimal>();
        if (payeeIsJuristic != false && t.JuristicRate is decimal j) expected.Add(j);
        if (payeeIsJuristic != true && t.IndividualRate is decimal i) expected.Add(i);
        if (expected.Count == 0) return null;
        if (expected.Any(e => Math.Abs(e - ratePercent) < 0.005m)) return null;
        if (ClassifyRate(ratePercent, paymentDate).Standing == RateStanding.TemporaryInForce) return null;

        var who = payeeIsJuristic switch
        {
            true => "ผู้รับเป็นนิติบุคคล",
            false => "ผู้รับเป็นบุคคลธรรมดา",
            _ => "ผู้รับ",
        };
        return $"อัตรา {ratePercent:0.##}% ไม่ตรงกับ \"{t.Name}\" ({t.TaxSection}) — {who} ควรหัก "
            + string.Join(" หรือ ", expected.Distinct().Select(e => $"{e:0.##}%"))
            + $" ({t.LegalReference}) · ตรวจประเภทเงินได้หรืออัตราอีกครั้ง (หักขาด ผู้จ่ายรับผิดในส่วนที่ขาด ม.54)";
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
    /// เงินได้ที่เก็บไว้ (รหัส/มาตรา ทุกรูปที่ <see cref="Resolve"/> รับ) เป็น<b>เงินได้ ม.40(4)</b> (ดอกเบี้ย/เงินปันผล …) ไหม —
    /// ผู้อ่านเดียวคือตัวเลือกแบบ ภ.ง.ด.2 (<c>WhtPayeeKind.ResolveForm</c>)
    ///
    /// <para>ม.50(2) + ม.59: ผู้จ่ายเงินได้ ม.40(4) ให้<b>บุคคลธรรมดา</b>หักแล้วยื่น <b>ภ.ง.ด.2</b> · ผู้รับนิติบุคคลไทยอยู่ ภ.ง.ด.53
    /// (ท.ป.4/2528) · ผู้รับต่างประเทศอยู่ ภ.ง.ด.54 (ม.70) — คำตัดสินเจ้าของ DECISIONS ข้อ 148</para>
    ///
    /// <para>รับ <c>"40(4)"</c> เปล่า ๆ (ไม่ระบุ (ก)/(ข)) ด้วย — ชี้ขาดไม่ได้ว่าดอกเบี้ยหรือปันผล (คนละอัตรา) แต่<b>แบบ</b>ชี้ขาดได้แล้วว่าเป็น ม.40(4) ·
    /// null/ว่าง/ไม่รู้จัก ⇒ false (ไม่รู้ ≠ ดอกเบี้ย — ใบเดิมไม่ถูกย้ายแบบเพราะเดา · หลักการ #3)</para>
    /// <para>⚠️ ม.40(3) ค่าสิทธิจ่ายบุคคลธรรมดา: แบบ ภ.ง.ด.2 ของกรมสรรพากรมีแถว ม.40(3) ด้วย (ม.50(2) อัตราก้าวหน้า) แต่คำตัดสินข้อ 148
    /// ครอบเฉพาะ ม.40(4) ⇒ ตารางยังให้ ม.40(3) อยู่ ภ.ง.ด.3 อัตรา 3% ตามเดิม (ข้อเสนอข้อ 149 รอเจ้าของ/นักบัญชี) — ห้ามขยายที่นี่เงียบ ๆ</para>
    /// </summary>
    public static bool IsSection40_4(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return false;
        var t = Resolve(stored);
        var section = (t?.TaxSection ?? stored).Replace(" ", "");
        return section.StartsWith("40(4)", StringComparison.Ordinal);
    }

    /// <summary>รหัสเงินได้ ม.40(4) จาก<b>อัตราที่หักจริง</b> — ใช้เฉพาะแถวที่ไม่มีเอกสารบอกประเภท (JV ลงผัง 21915 ของรายงาน ภ.ง.ด.2) ·
    /// อัตราบุคคลธรรมดาตามตารางนี้: ดอกเบี้ย <c>4a</c> 15% · เงินปันผล <c>4b</c> 10% (คลาดได้ ±0.05 จากการปัดเศษ) · อัตราอื่น ⇒ <c>"40(4)"</c>
    /// (บอกแบบได้ แต่ไม่เดาวงเล็บ — หลักการ #3)</summary>
    public static string Section40_4CodeFromRate(decimal ratePercent)
    {
        foreach (var t in All.Where(x => x.TaxSection.StartsWith("40(4)", StringComparison.Ordinal)))
            if (t.IndividualRate is decimal r && Math.Abs(r - ratePercent) <= 0.05m)
                return t.Code;
        return "40(4)";
    }

    /// <summary>ส่วนผสมของประเภทเงินได้ในบรรทัดที่<b>หักภาษีจริง</b>ของเอกสาร/หนังสือรับรองหนึ่งใบ (ผล <see cref="ClassifySection40_4"/>)</summary>
    public enum Section40_4Mix
    {
        /// <summary>ไม่มีบรรทัดที่หัก (หรือ WHT ระดับหัวเอกสารอย่างเดียว) — ไม่รู้ประเภท ⇒ แบบเดิม (ภ.ง.ด.3/53)</summary>
        Unknown = 0,
        /// <summary>ทุกบรรทัดที่หักเป็น ม.40(4)</summary>
        AllSection40_4 = 1,
        /// <summary>ไม่มีบรรทัด ม.40(4) เลย</summary>
        NoSection40_4 = 2,
        /// <summary>ปน ม.40(4) กับเงินได้อื่น (บรรทัดที่ไม่ระบุประเภทนับเป็น "อื่น") — คนละแบบยื่น ⇒ ต้องแยกใบ</summary>
        Mixed = 3,
    }

    /// <summary>จำแนกรหัสเงินได้ของบรรทัดที่หักภาษี — ตัวตัดสินเดียวของ "ใบนี้เป็น ภ.ง.ด.2 ทั้งใบไหม"</summary>
    /// <param name="withheldIncomeTypeCodes">รหัสของบรรทัดที่ <c>WithholdingTaxAmount &gt; 0</c> เท่านั้น (null ในลิสต์ = บรรทัดหักแต่ไม่ระบุประเภท)</param>
    public static Section40_4Mix ClassifySection40_4(IEnumerable<string?>? withheldIncomeTypeCodes)
    {
        var codes = withheldIncomeTypeCodes?.ToList();
        if (codes == null || codes.Count == 0) return Section40_4Mix.Unknown;
        var hits = codes.Count(IsSection40_4);
        if (hits == 0) return Section40_4Mix.NoSection40_4;
        return hits == codes.Count ? Section40_4Mix.AllSection40_4 : Section40_4Mix.Mixed;
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
