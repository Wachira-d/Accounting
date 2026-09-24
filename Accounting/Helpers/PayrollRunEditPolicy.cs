namespace Accounting.Helpers;

/// <summary>
/// กติกาเดียวของระบบว่า "รอบเงินเดือนนี้แก้ยอดได้ไหม / กลับรายการจ่ายได้ไหม"
///
/// ═══ ทำไมต้องมีตัวกลาง ═══
/// เดิมเงื่อนไข <c>Status != "Calculated" &amp;&amp; Status != "Approved"</c> ถูกเขียน
/// ซ้ำ 2 ที่ใน <c>PayrollService</c> (แก้ยอดรายคน / แก้แหล่งจ่าย) และ **เขียน
/// ใหม่อีกชุดฝั่ง JS** (<c>payEditable</c> ใน payroll.html) — สำเนามือ 3 ชุดที่
/// ไม่มีใครทำให้ตรงกัน = defect class "รายการที่คัดลอกมาด้วยมือ = drift แน่นอน
/// แค่รอเวลา" (กฎเหล็ก #4 A) ที่เรพนี้เจอซ้ำที่สุด
///
/// ที่แย่กว่า drift คือ **ฝั่ง JS ซ่อนปุ่มเฉย ๆ โดยไม่บอกเหตุผล** — ผู้ใช้เปิด
/// รอบที่จ่ายแล้วจะเห็นตารางที่ "แก้อะไรไม่ได้เลย" โดยไม่มีคำอธิบายและไม่มีทาง
/// ไปต่อ (defect class "ห้าม silent no-op" — ถ้าเซิร์ฟเวอร์ไม่รับ ต้องล็อกช่อง
/// **พร้อมบอกเหตุผลและทางแก้**) ฟังก์ชันในไฟล์นี้จึงคืน "เหตุผลเป็นภาษาไทยที่
/// เอาไปโชว์ได้เลย" ไม่ใช่แค่ bool แล้วให้แต่ละที่ไปแต่งข้อความเอง
/// </summary>
public static class PayrollRunEditPolicy
{
    public const string Draft = "Draft";
    public const string Calculated = "Calculated";
    public const string Approved = "Approved";
    public const string Paid = "Paid";
    public const string Voided = "Voided";

    /// <summary>แก้ยอด/แหล่งจ่ายรายคนได้ไหม — ได้เฉพาะรอบที่ยังไม่ลง GL <b>และยังไม่ถูกบันทึกว่ายื่น/นำส่ง</b>.
    /// คืน (false, เหตุผล) พร้อม**ทางแก้** เสมอ ห้ามคืนเหตุผลว่าง
    ///
    /// <para>รอบ 193 (ฝ่ายค้าน C3): คำตัดสิน #35 "รอบที่จ่าย/<b>ยื่นแล้ว</b>ห้ามแก้" ครอบการแก้รายคนด้วย — เดิมดูแค่สถานะ
    /// ⇒ รอบ Approved ที่ยื่น ภ.ง.ด.1/สปส.1-10 ไปแล้วยังแก้เงินเดือน/ภาษี/ปกส. รายคนได้ (และข้อความล็อกของ
    /// "คำนวณใหม่" ชี้มาทางนี้เอง) · ใช้หลักฐานชุดเดียวกับ <see cref="CanRecalculate"/> ผ่าน <see cref="FiledOrSettledBlock"/>
    /// ตัวเดียว · <paramref name="evidence"/> บังคับ (ไม่รู้ ≠ ผ่าน)</para></summary>
    public static (bool Can, string? Reason) CanEditAmounts(string? status, PayrollRunLockEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var basic = CanSetPaymentAccount(status);
        if (!basic.Can) return basic;
        var blocked = FiledOrSettledBlock(status, evidence, "แก้ยอดรายคน");
        return blocked == null ? basic : (false, blocked);
    }

    /// <summary>เปลี่ยน "แหล่งจ่าย" รายคนได้ไหม — ดูแค่สถานะ (แหล่งจ่ายไม่อยู่ในแบบ ภ.ง.ด.1/สปส.1-10
    /// จึงไม่ถูกล็อกด้วยหลักฐานการยื่น) · กติกาสถานะชุดเดียวกับ <see cref="CanEditAmounts"/></summary>
    public static (bool Can, string? Reason) CanSetPaymentAccount(string? status)
    {
        (bool Can, string? Reason) basic = status switch
        {
            Calculated or Approved => (true, null),
            Draft => (false, "รอบนี้ยังไม่ได้คำนวณ — กด \"คำนวณ\" ก่อนจึงจะมียอดรายคนให้แก้"),
            Paid => (false, "รอบนี้จ่ายและลงบัญชีไปแล้ว — กด \"กลับรายการจ่าย\" เพื่อกลับรายการ "
                            + "JE แล้วแก้ยอด จากนั้นกด \"จ่าย\" ใหม่"),
            Voided => (false, "รอบนี้ถูกยกเลิกแล้ว — แก้ไม่ได้ ต้องสร้างรอบใหม่"),
            _ => (false, $"สถานะ \"{status}\" ไม่รองรับการแก้ยอด"),
        };
        return basic;
    }

    /// <summary>กด "คำนวณ/คำนวณใหม่" ทั้งรอบได้ไหม (คำตัดสินเจ้าของ #35 รอบ 193)
    ///
    /// <para>เดิมคำนวณได้เฉพาะ <c>Draft</c> ⇒ รอบที่คำนวณ/อนุมัติไปแล้ว**ก่อน**มีการแก้
    /// สูตร (เช่น D-02 ฐาน ปกส. ไม่หักลาไม่รับค่าจ้าง) ติดตัวเลขผิดไว้ถาวร ทางเดียวคือ
    /// แก้มือทีละคน · คำตัดสิน: "คำนวณใหม่เฉพาะรอบที่ยังไม่จ่าย · รอบที่จ่าย/ยื่นแล้ว
    /// ห้ามแก้" ⇒ ให้ Draft/Calculated/Approved คำนวณซ้ำได้ ส่วนที่เหลือปฏิเสธพร้อม
    /// เหตุผลและทางไปต่อ</para>
    ///
    /// <para>ลำดับด่าน (รอบ 193 หลังฝ่ายค้าน): สถานะ → <b>ยื่น/นำส่งแล้ว</b> (<see cref="FiledOrSettledBlock"/> ตัวเดียวกับ
    /// ✏️ แก้ยอด) → นำเข้าจากระบบนอก → เคยจ่ายแล้วกลับรายการ → ปันต้นทุนโครงการ · ด่านยื่น/นำส่งมา<b>ก่อน</b>ด่านที่แนะนำ
    /// "✏️ แก้ยอด" เสมอ ⇒ ข้อความที่แนะนำ ✏️ ออกเฉพาะเมื่อ ✏️ ไม่ถูกล็อกด้วยหลักฐานชุดเดียวกัน
    /// (ข้อความล็อกห้ามชี้ไปที่ปุ่มที่ถูกล็อก)</para>
    ///
    /// <para><paramref name="evidence"/> บังคับ ไม่มีค่าเริ่มต้น (DOCTRINE §1: เงื่อนไขที่เป็นเท็จเพราะไม่มีข้อมูลห้ามตกเป็นผ่าน)</para></summary>
    public static (bool Can, string? Reason) CanRecalculate(
        string? status, string? externalSystem, DateTime? reopenedAt, PayrollRunLockEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (status == Paid)
            return (false, "รอบนี้จ่ายและลงบัญชีไปแล้ว — ยอดที่จ่าย/ยื่นแล้วห้ามคำนวณใหม่ "
                + "ถ้าต้องแก้ให้กด \"กลับรายการจ่าย\" แล้วแก้ยอดรายคนด้วย \"✏️ แก้ยอด\"");
        if (status == Voided)
            return (false, "รอบนี้ถูกยกเลิกแล้ว — คำนวณไม่ได้ ต้องสร้างรอบใหม่");
        if (status is not (Draft or Calculated or Approved))
            return (false, $"สถานะ \"{status}\" คำนวณไม่ได้");
        var blocked = FiledOrSettledBlock(status, evidence, "คำนวณใหม่");
        if (blocked != null)
            return (false, blocked);
        if (!string.IsNullOrWhiteSpace(externalSystem))
            return (false, $"รอบนี้นำเข้ายอดสำเร็จรูปจาก {externalSystem} — คำนวณใหม่ด้วยสูตรของระบบ "
                + "จะทับตัวเลขต้นทาง · แก้ที่ระบบต้นทางแล้วนำเข้าใหม่ หรือแก้รายคนด้วย \"✏️ แก้ยอด\"");
        if (reopenedAt.HasValue)
            return (false, "รอบนี้เคยจ่ายแล้วและถูกกลับรายการเมื่อ "
                + reopenedAt.Value.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)
                + " — ยอดชุดเดิมอาจถูกยื่น ภ.ง.ด.1/สปส.1-10 ไปแล้ว จึงห้ามคำนวณใหม่ทั้งรอบ "
                + "· แก้รายคนด้วย \"✏️ แก้ยอด\"");
        if (evidence.ProjectCostAllocatedRows > 0)
            return (false, $"ต้นทุนแรงงานของรอบนี้ถูกปันเข้าโครงการแล้ว ({evidence.ProjectCostAllocatedRows} แถวเวลาทำงาน) — "
                + "คำนวณใหม่จะทำให้ต้นทุนโครงการไม่ตรงกับเงินเดือน · ระบบยังไม่มีปุ่มยกเลิกการปันต้นทุน "
                + "⇒ แก้รายคนด้วย \"✏️ แก้ยอด\" แล้วปรับต้นทุนโครงการด้วยใบสำคัญปรับปรุง");
        return (true, null);
    }

    /// <summary>คำเตือน (ไม่ล็อก) ก่อนคำนวณใหม่/แก้ยอด — ไฟล์ e-Filing ที่ระบบ<b>สร้าง</b>ไว้แล้วของงวดนี้ ·
    /// "สร้างไฟล์ ≠ ยื่น" ⇒ ไม่ใช่เหตุล็อก แต่ต้องบอกผู้ใช้ (null = ไม่มีอะไรต้องเตือน)</summary>
    public static string? RecalculateWarning(PayrollRunLockEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return evidence.FileGeneratedForms.Count == 0 ? null
            : $"ระบบเคยสร้างไฟล์ยื่น {string.Join(" / ", evidence.FileGeneratedForms)} ของงวดนี้ไว้แล้ว — "
              + "ถ้าไฟล์นั้นถูกอัปโหลดไปแล้ว ตัวเลขหลังแก้จะไม่ตรงกับที่ยื่น (บันทึกการยื่นที่ปฏิทินภาษีเพื่อให้ระบบล็อก หรือยื่นแบบเพิ่มเติม)";
    }

    /// <summary>ด่าน "ยื่น/นำส่งแล้ว" ตัวเดียวของทั้ง <see cref="CanRecalculate"/> และ <see cref="CanEditAmounts"/>
    ///
    /// <para><b>ยื่นแล้ว</b> — ผูกกับ<b>งวด</b> (แบบยื่นเป็นรายเดือน) และใช้เฉพาะรอบที่อยู่ในไฟล์ยื่นแล้ว
    /// (<see cref="PayrollRunFilingScope.CountsTowardFiling"/> — Draft/Calculated ไม่เคยอยู่ในไฟล์) ·
    /// ทางไปต่อระบุตาม<b>แหล่ง</b>ที่บันทึกไว้จริง (ปฏิทินภาษี / ปฏิทิน compliance / รายงานภาษีเก่า) — ปลดที่ไหนก็บอกที่นั่น</para>
    ///
    /// <para><b>นำส่งแล้ว</b> — ผูกกับ<b>รอบ</b> (<c>SsoSettledAt</c> ของรอบนั้น) ไม่ใช่เดือน: ยอดนำส่งมาจากรอบ Paid
    /// เท่านั้น ⇒ รอบโบนัสที่ยัง Approved ไม่ได้อยู่ในเงินที่นำส่ง แม้รอบเงินเดือนของเดือนเดียวกันจะนำส่งแล้ว
    /// (ฝ่ายค้าน C2) · ทางไปต่อ = ปุ่ม "กลับรายการนำส่ง สปส." ของรอบนั้นที่หน้าเงินเดือน (มีจริง)</para></summary>
    private static string? FiledOrSettledBlock(string? status, PayrollRunLockEvidence evidence, string action)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (PayrollRunFilingScope.CountsTowardFiling(status) && evidence.FiledMarks.Count > 0)
        {
            var forms = evidence.FiledMarks.Select(m => m.Form).Distinct().ToList();
            var ways = evidence.FiledMarks.Select(m => m.Source).Distinct().OrderBy(x => x)
                .Select(PayrollFilingMark.UndoHint).ToList();
            return $"งวดของรอบนี้ถูกบันทึกว่ายื่น {string.Join(" / ", forms)} แล้ว — ยอดที่ยื่นแล้วห้าม{action} "
                + "(ตัวเลขในระบบจะไม่ตรงกับแบบที่ยื่น) · ถ้าต้องแก้จริง: ยื่นแบบเพิ่มเติม/ยื่นแก้ไขกับหน่วยงานก่อน แล้ว"
                + string.Join(" และ ", ways) + $" จึงจะ{action}ได้";
        }
        if (evidence.RunSsoSettled)
            return $"รอบนี้นำส่งเงินสมทบประกันสังคมไปแล้ว — ห้าม{action} (ยอดที่นำส่งกับยอดในรอบจะไม่ตรงกัน) · "
                + "กดปุ่ม \"↩️ กลับรายการนำส่ง\" (นำส่ง สปส.) ของรอบนี้ที่หน้าเงินเดือนก่อน (และยื่น สปส.1-10 แก้ไขถ้ายื่นไปแล้ว)";
        return null;
    }

    /// <summary>กลับรายการจ่าย (Paid → Approved) ได้ไหม.
    ///
    /// ⚠️ ตรวจได้แค่สิ่งที่อยู่บนตัว run — **งวดบัญชีปิดหรือยัง** ตรวจที่
    /// <c>PayrollService.ReopenPaidRunAsync</c> เพราะต้อง query FiscalPeriods
    /// (ใส่ตรงนี้จะกลายเป็น N+1 บนหน้ารายการรอบ). ฝั่ง UI จึงอาจโชว์ปุ่มแล้ว
    /// เซิร์ฟเวอร์ปฏิเสธพร้อมเหตุผล — ยอมได้ เพราะข้อความบอกทางแก้ชัด</summary>
    /// <summary>ยกเลิกทั้งรอบ (→ Voided) ได้ไหม — กติกา สปส. **เดียวกับ CanReopen**:
    /// นำส่งแล้ว = มี JE ก้อนที่สอง (Dr 21815 / Cr Bank) ที่ Void ไม่แตะ ⇒ ถ้ายอมให้ยกเลิก
    /// จะกลับแค่ JE จ่าย เหลือ 21815 **ติดลบ** ถาวร + แถวนำส่งยังบอกว่านำส่งแล้ว ⇒ รอบใหม่
    /// นำส่งซ้ำงวด. เดิม VoidPayrollAsync ตรวจแค่ "Voided ซ้ำ" — ด่านครอบทางเดียว
    /// (ERP_REVIEW_2026-09-05 H-01)</summary>
    public static (bool Can, string? Reason) CanVoid(string? status, DateTime? ssoSettledAt)
    {
        if (status == Voided)
            return (false, "รอบจ่ายเงินเดือนนี้ถูกยกเลิกแล้ว");
        if (ssoSettledAt.HasValue)
            return (false,
                $"รอบนี้นำส่งประกันสังคมไปแล้วเมื่อ {ssoSettledAt.Value.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)} — "
                + "ต้องกลับรายการนำส่ง สปส. ก่อน (และถ้ายื่น สปส.1-10 ไปแล้วต้องยื่นแก้ไขด้วย) "
                + "จึงจะยกเลิกรอบได้");
        return (true, null);
    }

    public static (bool Can, string? Reason) CanReopen(string? status, DateTime? ssoSettledAt)
    {
        if (status != Paid)
            return (false, status switch
            {
                Draft or Calculated or Approved => "รอบนี้ยังไม่ได้จ่าย — แก้ยอดได้เลยโดยไม่ต้องกลับรายการ",
                Voided => "รอบนี้ถูกยกเลิกไปแล้ว — กลับรายการไม่ได้ ต้องสร้างรอบใหม่",
                _ => $"สถานะ \"{status}\" กลับรายการจ่ายไม่ได้",
            });

        // นำส่ง สปส. แล้ว = มี JE ก้อนที่สอง (Dr 21815 / Cr Bank) + เลขรับจาก
        // สปส. บนกระดาษ. กลับรายการจ่ายโดยไม่แตะ JE ก้อนนั้น จะเหลือหนี้สิน
        // 21815 ที่ถูกล้างไปแล้วทั้งที่ต้นทางหายไป → งบไม่ตรง และยอดที่ยื่นจริง
        // กับยอดในระบบต่างกันโดยไม่มีใครรู้
        if (ssoSettledAt.HasValue)
            // InvariantCulture: ถ้าเครื่อง/คอนเทนเนอร์ตั้ง culture เป็น th-TH
            // ปฏิทินเริ่มต้นคือพุทธศักราช ⇒ "15/09/2026" กลายเป็น "15/09/2569"
            // เงียบ ๆ. ในระบบเก็บ/แสดง ค.ศ. — พ.ศ. ใช้เฉพาะแบบยื่นภาษีเท่านั้น
            return (false,
                $"รอบนี้นำส่งประกันสังคมไปแล้วเมื่อ {ssoSettledAt.Value.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)} — "
                + "ต้องกลับรายการนำส่ง สปส. ก่อน (และถ้ายื่น สปส.1-10 ไปแล้วต้องยื่นแก้ไขด้วย) "
                + "จึงจะกลับรายการจ่ายได้");

        return (true, null);
    }
}

/// <summary>แหล่งที่ผู้ใช้/ระบบบันทึกว่า "ยื่นแล้ว" — ลำดับ = ลำดับในข้อความทางไปต่อ</summary>
public enum PayrollFilingSource
{
    /// <summary>หน้าปฏิทินภาษี (<c>TaxCalendarEvent.Status = "Filed"</c>) — ทางเดียวบนจอที่บันทึกการยื่น ภ.ง.ด.1/สปส.1-10</summary>
    TaxCalendar = 0,
    /// <summary>ปฏิทิน compliance (<c>ComplianceFiling</c> Filed/Accepted) — มีแต่ API ไม่มีหน้าจอ</summary>
    ComplianceFiling = 1,
    /// <summary>รายงานภาษีชนิด ภ.ง.ด.1/ประกันสังคมรุ่นเก่า (สร้างใหม่ไม่ได้แล้ว) ที่ประกาศว่ายื่นหรือถูกล็อก</summary>
    LegacyTaxReport = 2,
}

/// <summary>เครื่องหมาย "ยื่นแล้ว" หนึ่งรายการ — แบบ + แหล่ง (แหล่งกำหนดว่าผู้ใช้ปลดได้ที่ไหน)</summary>
public sealed record PayrollFilingMark(string Form, PayrollFilingSource Source)
{
    /// <summary>ทางปลดของแต่ละแหล่ง — ต้องเป็นทางที่มีจริง (ฝ่ายค้าน C1: ข้อความเดิมชี้ไปปฏิทินภาษีที่ไม่ปลดอะไร)</summary>
    internal static string UndoHint(PayrollFilingSource s) => s switch
    {
        PayrollFilingSource.TaxCalendar => "เปลี่ยนสถานะของแบบนั้นที่หน้า \"ปฏิทินภาษี\" กลับเป็น \"รอยื่น\"",
        PayrollFilingSource.ComplianceFiling =>
            "เปลี่ยนสถานะในปฏิทิน compliance (บันทึกผ่าน API ไม่มีหน้าจอ — ให้ผู้ดูแลระบบแก้ทาง PUT /compliance/filings/{id})",
        _ => "กด \"ปลดล็อก/กลับเป็นร่าง\" ที่รายงานภาษีของงวดนั้น (หน้ารายงานภาษี)",
    };
}

/// <summary>หลักฐานว่ารอบเงินเดือน "ออกไปนอกระบบแล้ว" — service หามาแล้วส่งให้
/// <see cref="PayrollRunEditPolicy.CanRecalculate"/> / <see cref="PayrollRunEditPolicy.CanEditAmounts"/> (pure ทั้งคู่)
///
/// <para>ที่มาของหลักฐาน (รอบ 193 หลังฝ่ายค้าน C1/C2 — ดู <c>PayrollService.LoadRecalculateLockEvidenceAsync</c>):
/// <list type="bullet">
/// <item><b>ยื่นแล้ว (ล็อก · ต่องวด)</b> — ปฏิทินภาษี <c>TaxCalendarEvent</c> (ภ.ง.ด.1/สปส.1-10 · Filed) ·
///   <c>ComplianceFiling</c> (PND1/SSO1-10 · Filed/Accepted) · <c>TaxReport</c> เก่าที่ประกาศว่ายื่น/ถูกล็อก</item>
/// <item><b>นำส่งแล้ว (ล็อก · ต่อรอบ)</b> — <c>PayrollRun.SsoSettledAt</c> ของรอบนั้น · <b>ไม่</b>ใช้แถว <c>StatutoryRemittance</c>
///   รายเดือน (ยอดนำส่งมาจากรอบ Paid เท่านั้น — ผูกเดือนทำให้รอบโบนัส Approved ถูกล็อกผิด และ
///   <c>ReverseSsoSettlementAsync</c> ไม่ล้างแถวนั้น ⇒ ล็อกถาวร)</item>
/// <item><b>ปันต้นทุนแล้ว (ล็อกคำนวณใหม่ · ต่อรอบ)</b> — <c>EmployeeProjectTime.AllocatedPayrollRunId</c></item>
/// <item><b>สร้างไฟล์ยื่นแล้ว (เตือนเท่านั้น)</b> — <c>EFilingExport</c> PND.1 ของงวด · "สร้างไฟล์ ≠ ยื่น"</item>
/// </list>
/// ⚠️ การ<b>ดาวน์โหลด</b>ไฟล์ ภ.ง.ด.1/สปส.1-10 จาก <c>TaxFilingExportService</c> ไม่ทิ้งร่องรอยใด ๆ ⇒ ไม่ใช่หลักฐาน</para></summary>
public sealed record PayrollRunLockEvidence(
    IReadOnlyList<PayrollFilingMark> FiledMarks,
    bool RunSsoSettled,
    int ProjectCostAllocatedRows,
    IReadOnlyList<string> FileGeneratedForms)
{
    public const string Pnd1Label = "ภ.ง.ด.1";
    public const string SsoLabel = "สปส.1-10";

    /// <summary>ตรวจแล้ว ไม่พบหลักฐานใด (ไม่ใช่ "ยังไม่ได้ตรวจ" — ผู้เรียกต้องตรวจก่อนใช้ค่านี้)</summary>
    public static readonly PayrollRunLockEvidence None =
        new(Array.Empty<PayrollFilingMark>(), false, 0, Array.Empty<string>());

    /// <summary>ประกอบหลักฐานของรอบหนึ่ง — เรียงป้ายคงที่ (ภ.ง.ด.1 ก่อน สปส. · แหล่งตาม enum) ไม่ขึ้นกับลำดับแถวที่ query คืน</summary>
    public static PayrollRunLockEvidence From(
        IEnumerable<PayrollFilingMark> filedMarks, bool runSsoSettled, int allocatedRows,
        bool pnd1FileGenerated = false)
    {
        var marks = filedMarks.Distinct()
            .OrderBy(m => m.Form == Pnd1Label ? 0 : m.Form == SsoLabel ? 1 : 2).ThenBy(m => m.Source)
            .ToList();
        var generated = pnd1FileGenerated ? new[] { Pnd1Label } : Array.Empty<string>();
        return new PayrollRunLockEvidence(marks, runSsoSettled, Math.Max(0, allocatedRows), generated);
    }
}
