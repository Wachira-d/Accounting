namespace Accounting.Models.DTOs.Line;

/// <summary>รอบ 260: แหล่งแชตของสลิป — <paramref name="Kind"/> "user" (Id = userId) หรือ "group" (Id = groupId)</summary>
public sealed record LineChatSource(string Kind, string Id)
{
    public const string KindUser = "user";
    public const string KindGroup = "group";
    public bool IsGroup => Kind == KindGroup;
    public static LineChatSource User(string userId) => new(KindUser, userId);
    public static LineChatSource Group(string groupId) => new(KindGroup, groupId);
}

/// <summary>ใบที่เสนอให้พนักงานผูก (เอกสารค้างของผู้ติดต่อที่ผูก LINE) — ยอดจากเซิร์ฟเวอร์ · <paramref name="NetDue"/> = ยอดที่ลูกค้าต้องโอน ·
/// <paramref name="GrossDue"/> = ยอดก่อนหัก ณ ที่จ่าย · <paramref name="AmountMatches"/> = ยอดที่อ่านได้เท่ายอดที่ต้องโอน</summary>
public sealed record LineChatSlipDocOption(Guid DocumentId, string DocumentNumber, DateTime DocumentDate, decimal NetDue, decimal GrossDue, bool AmountMatches);

/// <summary>สลิปจากแชตที่ยังไม่ผูกเอกสาร (คิวพนักงาน) — ป้าย/เหตุ/ยอดจากเซิร์ฟเวอร์ หน้าเว็บแสดงตรงตัว</summary>
public sealed record LineChatSlipRow(
    Guid Id, DateTime ReceivedAt, string Status, string SourceLabel, Guid? ContactId, string? ContactName,
    string ReasonLabel, string? ReasonDetail, decimal? Amount, string? AmountsText, DateTime? SlipDate, string? SlipRef,
    IReadOnlyList<LineChatSlipDocOption> Options);

public sealed record LineChatSlipListResponse(int Count, IReadOnlyList<LineChatSlipRow> Items);

/// <summary>พนักงานผูกสลิปกับเอกสาร — <paramref name="DocumentId"/> หรือ <paramref name="DocumentNumber"/> (บริษัทเดียวกัน) · <paramref name="Amount"/> null = ยอดที่อ่านได้ ⇒ ยอดสุทธิของใบ</summary>
public sealed record AssignLineChatSlipRequest(Guid? DocumentId, string? DocumentNumber, decimal? Amount);

public sealed record DismissLineChatSlipRequest(string? Reason);

/// <summary>ผลการผูก — รายการรอยืนยันที่สร้าง (คิวเดียวกับสลิปพอร์ทัล)</summary>
/// <param name="CrossContact">ฝ่ายค้าน L6: ใบเป็นของผู้ติดต่ออื่นที่ไม่ใช่เจ้าของแชต (ลง audit · หน้าเว็บแสดงคำเตือนจากข้อความเซิร์ฟเวอร์)</param>
public sealed record LineChatSlipAssignResult(Guid SlipId, Guid DocumentId, string DocumentNumber, Guid PaymentIntentId, decimal Amount, bool CrossContact = false);
