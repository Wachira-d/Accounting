using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสินเดียว "ผังรายได้รับล่วงหน้า" ของตารางรับรู้รายได้ตามสัญญา (ทีม W6 · R-40 / COURSE_CREDIT_DESIGN B6) — pure
///
/// <para><b>ที่มา:</b> <c>RevenueRecognitionService.RecognizeRevenueAsync</c> เดิมหา <c>21810</c> (ไม่มีในผังมาตรฐาน) แล้ว fallback
/// <c>StartsWith("218")</c> = กลุ่ม <b>"ค่าใช้จ่ายบุคลากรค้างจ่าย"</b> (21811 เงินเดือนค้างจ่าย …) ⇒ JE รับรู้รายได้ไป Dr เงินเดือนค้างจ่ายเงียบ ๆ ·
/// ผังที่ถูกคือกลุ่ม <b>217 "รายได้รับล่วงหน้า"</b> (21713 ค่าบริการรับล่วงหน้า · 21712 ค่าสินค้ารับล่วงหน้า · 21711 ค่าเช่ารับล่วงหน้า) ·
/// ผังที่ไม่มี/กำกวม ⇒ <b>ไม่รู้ = บอกไม่รู้</b> (คืน null ให้ผู้เรียกล้มดัง) ไม่ใช่ลงผังที่ความหมายผิด (F2 #3 · G3)</para>
///
/// <para><b>กติกา:</b> (1) มี 21713 ⇒ ใช้ · (2) ไม่มี ⇒ ผัง 217xx (หนี้สิน · ไม่ใช่บัญชีคุม) ที่<b>ไม่ใช่</b> 21714 "ดอกเบี้ยค้างจ่าย" (อยู่ผิดกลุ่มในผังมาตรฐาน)
/// มี<b>ผังเดียว</b> ⇒ ใช้ · หลายผัง/ไม่มี ⇒ null · ห้ามมี fallback ด้วย prefix "218" อีก</para>
/// </summary>
public static class DeferredRevenueAccount
{
    public const string RuleCode = "TFRS-NPAE-6-DEFERRED-ACCOUNT";
    public const string PreferredCode = "21713";
    public const string GroupPrefix = "217";
    /// <summary>21714 "ดอกเบี้ยค้างจ่าย" อยู่ใต้กลุ่ม 217 ในผังมาตรฐานแต่ไม่ใช่รายได้รับล่วงหน้า — ห้ามเลือก</summary>
    public const string ExcludedCode = "21714";

    public sealed record Candidate(Guid Id, string Code, AccountType Type);

    /// <summary>เลือกผังรายได้รับล่วงหน้าจากผังที่ลงบัญชีได้ของบริษัท (ผู้เรียกกรองบัญชีคุม/ลบ/ปิดใช้ออกก่อน) · null = ไม่พบ/กำกวม</summary>
    public static Guid? Pick(IReadOnlyCollection<Candidate> postable)
    {
        var liabilities = postable.Where(c => c.Type == AccountType.Liability).ToList();
        var exact = liabilities.FirstOrDefault(c => c.Code == PreferredCode);
        if (exact != null) return exact.Id;
        var group = liabilities
            .Where(c => c.Code.StartsWith(GroupPrefix, StringComparison.Ordinal) && c.Code != GroupPrefix && c.Code != ExcludedCode)
            .ToList();
        return group.Count == 1 ? group[0].Id : null;
    }

    /// <summary>ข้อความล้มดังเมื่อหาผังไม่ได้ — บอกสิ่งที่ผู้ใช้ทำได้แทน (เพิ่ม/เปิดใช้ 21713)</summary>
    public static string MissingMessage(int groupCandidates)
        => groupCandidates > 1
            ? $"ผังรายได้รับล่วงหน้ามีหลายรหัสในกลุ่ม {GroupPrefix} และไม่มี {PreferredCode} — เพิ่ม/เปิดใช้ผัง {PreferredCode} \"ค่าบริการรับล่วงหน้า\" แล้วรับรู้รายได้ใหม่ (ระบบไม่เลือกแทน)"
            : $"ไม่พบผังรายได้รับล่วงหน้า {PreferredCode} \"ค่าบริการรับล่วงหน้า\" (กลุ่ม {GroupPrefix}) ในผังบัญชีของบริษัท — เพิ่ม/เปิดใช้ผังก่อน แล้วรับรู้รายได้ใหม่ "
              + "(ระบบไม่ลงผังกลุ่ม 218 ค่าใช้จ่ายบุคลากรค้างจ่ายแทนอีกต่อไป)";

    /// <summary>จำนวนผู้สมัครในกลุ่ม 217 (ไว้เลือกข้อความ) — ใช้คู่กับ <see cref="Pick"/> ที่คืน null</summary>
    public static int GroupCandidateCount(IReadOnlyCollection<Candidate> postable)
        => postable.Count(c => c.Type == AccountType.Liability && c.Code.StartsWith(GroupPrefix, StringComparison.Ordinal)
                               && c.Code != GroupPrefix && c.Code != ExcludedCode);

    /// <summary>JE รับรู้รายได้ตามสัญญาเดิมที่ Dr ผังกลุ่ม 218 (ลงผิดกลุ่ม) — ตัวตัดสินของรายงานตรวจข้อมูล (อ่านอย่างเดียว)</summary>
    public static bool IsMisposted(string accountCode)
        => accountCode.StartsWith("218", StringComparison.Ordinal);
}
