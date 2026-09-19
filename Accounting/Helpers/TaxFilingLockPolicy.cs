using System;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ระดับ "หลักฐาน" ที่ระบบมีอยู่จริงว่ารายงานภาษีงวดนี้ถูกยื่นแล้ว —
/// เรียงตามระยะห่างจากของจริง (DECISION_DOCTRINE §1 G3/G5)</summary>
public enum TaxFilingEvidence
{
    /// <summary>ยังไม่มีใครบอกว่ายื่น (Draft)</summary>
    NotFiled = 0,

    /// <summary>**มนุษย์ประกาศว่ายื่นแล้ว** แต่ระบบยังไม่เห็นเลขรับของกรมสรรพากร
    /// — เป็นคำบอกเล่า ไม่ใช่การยืนยันจากปลายทาง ⇒ ห้ามล็อกงวด</summary>
    DeclaredByUser = 1,

    /// <summary>มีเลขรับ/เลขอ้างอิงจากกรมสรรพากรอยู่ในมือ — ของจริงยืนยันแล้ว
    /// ⇒ ล็อกงวด (เอกสาร/JE ของงวดนั้นห้ามขยับ)</summary>
    ConfirmedByFilingNumber = 2,
}

/// <summary>ผลการตัดสินของ <see cref="TaxFilingLockPolicy"/> — เซิร์ฟเวอร์คิด
/// หน้าเว็บแสดงอย่างเดียว (หลักการ 10 ข้อ #5)</summary>
/// <param name="Status">สถานะที่ต้องเขียนลงรายงาน</param>
/// <param name="Evidence">หลักฐานที่มีจริง</param>
/// <param name="LockPeriod">ล็อกเอกสาร/JE ทั้งงวดหรือไม่</param>
/// <param name="Label">ป้ายสั้นบนจอ</param>
/// <param name="Detail">ประโยคที่บอก "สิ่งที่ระบบรู้จริง" — ห้ามเกินความจริง</param>
/// <param name="NextStep">ทางไปต่อของผู้ใช้ (null = ไม่มีอะไรต้องทำต่อ)</param>
/// <param name="NeedsFilingNumber">ยังค้างเลขรับที่ต้องตามเก็บ</param>
public sealed record TaxFilingJudgement(
    TaxReportStatus Status,
    TaxFilingEvidence Evidence,
    bool LockPeriod,
    string Label,
    string Detail,
    string? NextStep,
    bool NeedsFilingNumber);

/// <summary>
/// ตัวตัดสิน**ตัวเดียว**ของคำถาม "รายงานภาษีงวดนี้ถือว่ายื่นแล้วระดับไหน และ
/// ล็อกงวดได้หรือยัง"
///
/// <para>═══ ทำไมต้องมี (DECISION_AUDIT_2026-09-18 · D2-B1a · ราก R1) ═══
/// เดิม <c>FileTaxReportAsync</c> ประทับ <c>Status = Filed</c> + <c>FiledDate</c> +
/// <b><c>FilingLockedAt</c></b> จาก**การกดปุ่มอย่างเดียว** แล้ว controller ตอบว่า
/// "ยื่นรายงานภาษีสำเร็จ" ⇒ ล็อกเอกสาร/JE ทั้งงวดด้วยเหตุการณ์ที่ระบบไม่รู้ว่า
/// เกิดขึ้นจริง และโกหกผู้ใช้ไปพร้อมกัน (หลักการ 10 ข้อ #3: "สถานะปลายทางที่
/// ระบบประทับเองอันตรายกว่าการไม่ตอบ")</para>
///
/// <para>═══ กติกา ═══
/// • <b>ไม่มีเลขรับ</b> → <see cref="TaxReportStatus.Submitted"/> = "ผู้ใช้ประกาศ
///   ว่ายื่นแล้ว" · <c>FilingLockedAt</c> ต้อง<b>คง null</b> — งวดยังแก้ได้ เพราะ
///   ระบบยังไม่มีหลักฐานว่ามีอะไรไปถึงกรมสรรพากรจริง<br/>
/// • <b>มีเลขรับ</b> (เลขรับใบเสร็จ/เลขอ้างอิง e-Filing) → <see cref="TaxReportStatus.Filed"/>
///   + ล็อกงวด — ของจริงยืนยันแล้ว การแก้ย้อนหลังทำให้ GL ไม่ตรงแบบที่ยื่น<br/>
/// • ผู้ยื่นกระดาษที่ยังไม่มีเลขรับ **มีทางไปต่อเสมอ**: ประกาศไว้ก่อน (Submitted)
///   แล้วกลับมากด "บันทึกเลขรับ" เมื่อได้รับ ⇒ ระบบอัปเกรดเป็น Filed + ล็อกให้เอง</para>
///
/// <para>⚠️ ผู้อ่าน <c>Status == Filed</c> ในเรพ **ไม่ได้ถูกแก้ทุกจุด** (หลายจุด
/// อยู่ใน <c>DocumentService</c>/<c>IntegrationService</c> ที่รอบนี้ห้ามแตะ) —
/// จุดที่เขียนว่า <c>Status != Draft</c> จะนับ <c>Submitted</c> เป็น "ยื่นแล้ว"
/// โดยอัตโนมัติ (ทิศเข้มกว่า = ปลอดภัย) ส่วนจุดที่เขียน <c>== Filed</c> จะไม่นับ
/// ⇒ ใช้ <see cref="DeclaredOrFiled"/> แทนการเทียบเองทุกครั้งที่แก้จุดใหม่</para>
/// </summary>
public static class TaxFilingLockPolicy
{
    /// <summary>ค่าที่เขียนลง <c>TaxReport.RdSubmissionStatus</c> เมื่อมีแต่คำ
    /// ประกาศของผู้ใช้ — ไม่ใช่คำตอบจากกรมสรรพากร</summary>
    public const string SubmissionDeclared = "Declared";

    /// <summary>ค่าที่เขียนเมื่อมีเลขรับจากกรมสรรพากรแล้ว</summary>
    public const string SubmissionAcknowledged = "Acknowledged";

    /// <summary>ชุดสถานะที่แปลว่า "งวดนี้ถูกประกาศว่ายื่นแล้ว" (ประกาศด้วยมือ
    /// หรือยืนยันด้วยเลขรับก็ตาม) — ใช้แทนการเทียบ <c>== Filed</c> เองทุกจุดที่
    /// ถามว่า "ยังเป็นร่างอยู่ไหม"</summary>
    public static bool DeclaredOrFiled(TaxReportStatus status)
        => status is TaxReportStatus.Filed or TaxReportStatus.Submitted;

    /// <summary>ชุดเดียวกับ <see cref="DeclaredOrFiled"/> แต่เป็น array เพื่อให้
    /// EF แปล <c>DeclaredOrFiledStatuses.Contains(t.Status)</c> เป็น <c>IN (...)</c>
    /// ได้ตรง ๆ (แพตเทิร์นเดียวกับ <see cref="WhtCertFilingScope.Filed"/>) —
    /// ห้ามพิมพ์ <c>== Filed || == Submitted</c> เองในคิวรีใหม่</summary>
    public static readonly TaxReportStatus[] DeclaredOrFiledStatuses =
    {
        TaxReportStatus.Filed,
        TaxReportStatus.Submitted,
    };

    /// <summary>มีเลขรับอยู่ในมือหรือยัง — ช่องว่าง/ช่องว่างล้วน = ยังไม่มี</summary>
    public static bool HasFilingNumber(string? filingNumber)
        => !string.IsNullOrWhiteSpace(filingNumber);

    /// <summary>ตัดสินว่าการ "ยื่น" ครั้งนี้ได้สถานะอะไรและล็อกงวดไหม —
    /// เรียกตอนผู้ใช้กดยื่น/บันทึกเลขรับ</summary>
    public static TaxFilingJudgement Judge(string? filingNumber)
        => HasFilingNumber(filingNumber)
            ? new TaxFilingJudgement(
                TaxReportStatus.Filed,
                TaxFilingEvidence.ConfirmedByFilingNumber,
                LockPeriod: true,
                Label: "ยื่นแล้ว",
                Detail: $"ยืนยันด้วยเลขรับจากกรมสรรพากร {filingNumber!.Trim()} — งวดนี้ถูกล็อก เอกสาร/รายการบัญชีในงวดแก้ไม่ได้",
                NextStep: null,
                NeedsFilingNumber: false)
            : new TaxFilingJudgement(
                TaxReportStatus.Submitted,
                TaxFilingEvidence.DeclaredByUser,
                LockPeriod: false,
                Label: "บันทึกว่ายื่นแล้ว (รอเลขรับ)",
                Detail: "ระบบบันทึกตามที่ผู้ใช้แจ้งเท่านั้น — ยังไม่มีเลขรับจากกรมสรรพากร จึงยังไม่ล็อกงวดนี้",
                NextStep: "เมื่อได้ใบเสร็จ/เลขอ้างอิงจากกรมสรรพากร กด \"บันทึกเลขรับ\" เพื่อยืนยันและล็อกงวด",
                NeedsFilingNumber: true);

    /// <summary>อธิบายสถานะของรายงานที่เก็บไว้แล้ว — ใช้เติม DTO ให้หน้าเว็บ
    /// แสดงป้ายที่ตรงกับสิ่งที่เกิดจริง (ห้ามให้ JS ตัดสินเอง)</summary>
    /// <param name="status">สถานะที่เก็บอยู่</param>
    /// <param name="filingNumber">เลขรับที่เก็บไว้ (<c>TaxReport.RdAckNumber</c>)</param>
    public static TaxFilingJudgement Describe(TaxReportStatus status, string? filingNumber)
    {
        if (status == TaxReportStatus.Draft)
            return new TaxFilingJudgement(
                TaxReportStatus.Draft, TaxFilingEvidence.NotFiled, LockPeriod: false,
                Label: "ร่าง",
                Detail: "ยังไม่ได้ยื่น",
                NextStep: null, NeedsFilingNumber: false);

        if (status == TaxReportStatus.Submitted)
            return Judge(null) with
            {
                // ผู้ใช้ประกาศไว้ แต่ถ้าเผลอมีเลขรับติดมาแล้วสถานะยังไม่อัปเกรด
                // ให้ป้ายบอกตามเลขที่มีจริง (ข้อมูลชนะสถานะที่ค้าง)
                Detail = HasFilingNumber(filingNumber)
                    ? $"มีเลขรับ {filingNumber!.Trim()} บันทึกไว้แล้ว แต่สถานะยังไม่ถูกอัปเกรด — กด \"บันทึกเลขรับ\" อีกครั้งเพื่อล็อกงวด"
                    : "ระบบบันทึกตามที่ผู้ใช้แจ้งเท่านั้น — ยังไม่มีเลขรับจากกรมสรรพากร จึงยังไม่ล็อกงวดนี้",
            };

        // Filed — ของเดิมในฐานอาจเป็น Filed โดยไม่มีเลขรับ (ประทับจากปุ่มก่อน
        // รอบนี้). **ห้ามลดชั้นย้อนหลัง** ของที่ประกาศว่ายื่นไปแล้ว — ติดธง
        // NeedsFilingNumber ให้ตามเก็บแทน
        return HasFilingNumber(filingNumber)
            ? Judge(filingNumber)
            : new TaxFilingJudgement(
                TaxReportStatus.Filed, TaxFilingEvidence.DeclaredByUser, LockPeriod: true,
                Label: "ยื่นแล้ว (ไม่มีเลขรับในระบบ)",
                Detail: "รายงานนี้ถูกประทับว่ายื่นแล้วโดยไม่มีเลขรับบันทึกไว้ — งวดถูกล็อกอยู่",
                NextStep: "กด \"บันทึกเลขรับ\" เพื่อเก็บเลขรับ/เลขอ้างอิงของกรมสรรพากรไว้เป็นหลักฐาน",
                NeedsFilingNumber: true);
    }
}
