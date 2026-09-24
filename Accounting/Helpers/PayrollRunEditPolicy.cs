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

    /// <summary>แก้ยอด/แหล่งจ่ายรายคนได้ไหม — ได้เฉพาะรอบที่ยังไม่ลง GL.
    /// คืน (false, เหตุผล) พร้อม**ทางแก้** เสมอ ห้ามคืนเหตุผลว่าง</summary>
    public static (bool Can, string? Reason) CanEditAmounts(string? status) => status switch
    {
        Calculated or Approved => (true, null),
        Draft => (false, "รอบนี้ยังไม่ได้คำนวณ — กด \"คำนวณ\" ก่อนจึงจะมียอดรายคนให้แก้"),
        Paid => (false, "รอบนี้จ่ายและลงบัญชีไปแล้ว — กด \"กลับรายการจ่าย\" เพื่อกลับรายการ "
                        + "JE แล้วแก้ยอด จากนั้นกด \"จ่าย\" ใหม่"),
        Voided => (false, "รอบนี้ถูกยกเลิกแล้ว — แก้ไม่ได้ ต้องสร้างรอบใหม่"),
        _ => (false, $"สถานะ \"{status}\" ไม่รองรับการแก้ยอด"),
    };

    /// <summary>กด "คำนวณ/คำนวณใหม่" ทั้งรอบได้ไหม (คำตัดสินเจ้าของ #35 รอบ 193)
    ///
    /// <para>เดิมคำนวณได้เฉพาะ <c>Draft</c> ⇒ รอบที่คำนวณ/อนุมัติไปแล้ว**ก่อน**มีการแก้
    /// สูตร (เช่น D-02 ฐาน ปกส. ไม่หักลาไม่รับค่าจ้าง) ติดตัวเลขผิดไว้ถาวร ทางเดียวคือ
    /// แก้มือทีละคน · คำตัดสิน: "คำนวณใหม่เฉพาะรอบที่ยังไม่จ่าย · รอบที่จ่าย/ยื่นแล้ว
    /// ห้ามแก้" ⇒ ให้ Draft/Calculated/Approved คำนวณซ้ำได้ ส่วนที่เหลือปฏิเสธพร้อม
    /// เหตุผลและทางไปต่อ</para>
    ///
    /// <para>ปฏิเสธ 4 ทรง: <b>Paid</b> (ยอดออกไปแล้ว — ลง GL · 50 ทวิ · ภ.ง.ด.1/สปส.1-10
    /// อาจยื่นแล้ว) · <b>Voided</b> · <b>เคยจ่ายแล้วถูกกลับรายการ</b> (<paramref name="reopenedAt"/>
    /// มีค่า — ยอดชุดเดิมอาจอยู่ในแบบที่ยื่นไปแล้ว ⇒ แก้รายคนผ่าน "✏️ แก้ยอด" ที่มีร่องรอย
    /// ไม่ใช่ล้างทั้งรอบ) · <b>นำเข้าจากระบบนอก</b> (ยอดเป็นของระบบต้นทาง คำนวณทับด้วยสูตรเรา
    /// = ตัวเลขสองแหล่งที่ไม่มีใครรู้ว่าอันไหนจริง)</para>
    ///
    /// <para><b>รอบ 193 (ฝ่ายค้าน M2)</b> — "ยื่นแล้วห้ามแก้" ต้องครอบรอบ <b>Approved</b> ด้วย:
    /// รอบ Approved นับเข้าแบบยื่นแล้ว (<see cref="PayrollRunFilingScope.FilingStatuses"/> =
    /// {Approved, Paid} · ไฟล์ ภ.ง.ด.1/สปส.1-10 ดาวน์โหลดได้ · 50 ทวิประจำปี · ภ.ง.ด.91) และ
    /// ปันต้นทุนแรงงานเข้าโครงการได้แล้ว ⇒ ต้องดูหลักฐานใน <paramref name="evidence"/> ที่ service
    /// หามาให้ (ตัวนี้ pure — ไม่แตะฐานข้อมูล) · พารามิเตอร์นี้<b>บังคับ</b> ไม่มีค่าเริ่มต้น
    /// เพื่อไม่ให้ผู้เรียกลืมหาหลักฐานแล้วได้ "ผ่าน" จากการไม่รู้ (DOCTRINE §1: เงื่อนไขที่เป็นเท็จ
    /// เพราะไม่มีข้อมูลห้ามตกเป็นผ่าน)</para></summary>
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
        if (!string.IsNullOrWhiteSpace(externalSystem))
            return (false, $"รอบนี้นำเข้ายอดสำเร็จรูปจาก {externalSystem} — คำนวณใหม่ด้วยสูตรของระบบ "
                + "จะทับตัวเลขต้นทาง · แก้ที่ระบบต้นทางแล้วนำเข้าใหม่ หรือแก้รายคนด้วย \"✏️ แก้ยอด\"");
        if (reopenedAt.HasValue)
            return (false, "รอบนี้เคยจ่ายแล้วและถูกกลับรายการเมื่อ "
                + reopenedAt.Value.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)
                + " — ยอดชุดเดิมอาจถูกยื่น ภ.ง.ด.1/สปส.1-10 ไปแล้ว จึงห้ามคำนวณใหม่ทั้งรอบ "
                + "· แก้รายคนด้วย \"✏️ แก้ยอด\"");
        // ── ยื่น/นำส่งแล้ว — เฉพาะรอบที่อยู่ในแบบยื่นแล้ว (Approved) ──
        // Draft/Calculated ไม่เคยอยู่ในไฟล์ยื่น (FilingStatuses) ⇒ คำนวณใหม่ไม่เปลี่ยนสิ่งที่ยื่นไป
        if (PayrollRunFilingScope.CountsTowardFiling(status))
        {
            if (evidence.FiledForms.Count > 0)
                return (false, $"งวดของรอบนี้ถูกบันทึกว่ายื่น {string.Join(" / ", evidence.FiledForms)} แล้ว — "
                    + "ยอดที่ยื่นแล้วห้ามคำนวณใหม่ (ไฟล์ที่ยื่นกับตัวเลขในระบบจะไม่ตรงกัน) · "
                    + "ถ้าต้องแก้จริง: ยื่นแบบเพิ่มเติม/ยื่นแก้ไขกับหน่วยงานก่อน แล้วเปลี่ยนสถานะการยื่นของงวดนี้ "
                    + "(หน้าปฏิทินภาษี/รายงานภาษี) กลับเป็นยังไม่ยื่น จึงจะคำนวณใหม่ได้");
            if (evidence.RemittedForms.Count > 0)
                return (false, $"งวดของรอบนี้นำส่ง {string.Join(" / ", evidence.RemittedForms)} ไปแล้ว — "
                    + "คำนวณใหม่จะทำให้ยอดที่นำส่งกับยอดในรอบไม่ตรงกัน · "
                    + "ต้องกลับรายการนำส่งที่หน้านำส่งภาษี/ประกันสังคมก่อน (และยื่นแบบแก้ไขถ้ายื่นไปแล้ว) "
                    + "จึงจะคำนวณใหม่ได้");
        }
        if (evidence.ProjectCostAllocatedRows > 0)
            return (false, $"ต้นทุนแรงงานของรอบนี้ถูกปันเข้าโครงการแล้ว ({evidence.ProjectCostAllocatedRows} แถวเวลาทำงาน) — "
                + "คำนวณใหม่จะทำให้ต้นทุนโครงการไม่ตรงกับเงินเดือน · ระบบยังไม่มีปุ่มยกเลิกการปันต้นทุน "
                + "⇒ แก้รายคนด้วย \"✏️ แก้ยอด\" แล้วปรับต้นทุนโครงการด้วยใบสำคัญปรับปรุง");
        return (true, null);
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

/// <summary>หลักฐานว่ารอบเงินเดือน "ออกไปนอกระบบแล้ว" — service หามาแล้วส่งให้
/// <see cref="PayrollRunEditPolicy.CanRecalculate"/> (ตัวนั้น pure)
///
/// <para>ที่มาของหลักฐาน (ตรวจแล้วรอบ 193 · M2 — ดู <c>PayrollService.LoadRecalculateLockEvidenceAsync</c>):
/// <list type="bullet">
/// <item><b>ยื่นแล้ว</b> — <c>ComplianceFiling</c> (PND1 / SSO1-10 สถานะ Filed/Accepted) · รายงานภาษีเก่า
///   <c>TaxReport</c> ชนิด ภ.ง.ด.1/ประกันสังคมที่ประกาศว่ายื่นหรือถูกล็อก (สร้างใหม่ไม่ได้แล้ว แต่ข้อมูลเก่ายังอยู่) ·
///   <c>EFilingExport</c> แบบ PND.1 ของงวด (ไฟล์ที่ระบบสร้างเพื่ออัปโหลด)</item>
/// <item><b>นำส่งแล้ว</b> — <c>StatutoryRemittance</c> ชนิด SsoSps110/WhtPnd1 ของงวด · <c>PayrollRun.SsoSettledAt</c></item>
/// <item><b>ปันต้นทุนแล้ว</b> — <c>EmployeeProjectTime.AllocatedPayrollRunId</c> = รอบนี้</item>
/// </list>
/// ⚠️ การ<b>ดาวน์โหลด</b>ไฟล์ ภ.ง.ด.1/สปส.1-10 จาก <c>TaxFilingExportService</c> ไม่ทิ้งร่องรอยใด ๆ ⇒
/// ไม่ใช่หลักฐาน (และ "ดาวน์โหลด ≠ ยื่น" — ห้ามอนุมาน) · ผู้ใช้ที่ยื่นแล้วต้องบันทึกการยื่นในระบบ</para></summary>
/// <param name="FiledForms">ป้ายแบบที่ถูกบันทึกว่ายื่นแล้วในงวดของรอบ (ว่าง = ไม่พบ)</param>
/// <param name="RemittedForms">ป้ายแบบที่นำส่งเงินแล้วในงวดของรอบ</param>
/// <param name="ProjectCostAllocatedRows">จำนวนแถวเวลาทำงานที่ปันต้นทุนจากรอบนี้แล้ว</param>
public sealed record PayrollRunLockEvidence(
    IReadOnlyList<string> FiledForms,
    IReadOnlyList<string> RemittedForms,
    int ProjectCostAllocatedRows)
{
    public const string Pnd1Label = "ภ.ง.ด.1";
    public const string SsoLabel = "สปส.1-10";

    /// <summary>ตรวจแล้ว ไม่พบหลักฐานใด (ไม่ใช่ "ยังไม่ได้ตรวจ" — ผู้เรียกต้องตรวจก่อนใช้ค่านี้)</summary>
    public static readonly PayrollRunLockEvidence None =
        new(Array.Empty<string>(), Array.Empty<string>(), 0);

    /// <summary>ประกอบหลักฐานของรอบหนึ่งจากข้อเท็จจริงดิบ — ลำดับป้ายคงที่ (ภ.ง.ด.1 ก่อน สปส.)
    /// เพื่อให้ข้อความเหมือนกันทุกครั้ง ไม่ขึ้นกับลำดับแถวที่ query คืน</summary>
    public static PayrollRunLockEvidence From(
        bool pnd1Filed, bool ssoFiled, bool pnd1Remitted, bool ssoRemitted, int allocatedRows)
    {
        var filed = new List<string>();
        if (pnd1Filed) filed.Add(Pnd1Label);
        if (ssoFiled) filed.Add(SsoLabel);
        var remitted = new List<string>();
        if (pnd1Remitted) remitted.Add(Pnd1Label);
        if (ssoRemitted) remitted.Add(SsoLabel);
        return new PayrollRunLockEvidence(filed, remitted, Math.Max(0, allocatedRows));
    }
}
