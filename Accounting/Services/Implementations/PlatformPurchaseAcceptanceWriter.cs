using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Entities;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 219 เฟส 2 (คำตัดสินเจ้าของข้อ 178 ⑧) — <b>ผู้เขียนหลักฐานการยอมรับเงื่อนไขการซื้อตัวเดียว</b> (ศูนย์บิล · ลิงก์ชำระ · ซื้อส่วนเสริม):
/// ตรวจด้วย <see cref="PlatformPurchaseTerms.Refusal"/> ก่อน แล้วเพิ่มแถว append-only (ยังไม่ SaveChanges — อยู่ในธุรกรรมเดียวกับรายการซื้อ) ·
/// hash = <c>PdpaConsentEvidence.ComputeHash</c> (canonical เดียวของระบบ) บนเวลาที่ normalize แล้ว (ค่าเดียวกับที่เก็บ ⇒ ตรวจกลับได้)
/// </summary>
public static class PlatformPurchaseAcceptanceWriter
{
    public const int MaxUserAgent = 500;

    /// <summary>โยน <see cref="BusinessRuleException"/> เมื่อไม่ติ๊ก/ฉบับไม่ตรง — เรียกก่อนสร้างรายการซื้อเสมอ</summary>
    public static void EnsureAccepted(PlatformPurchaseAcceptanceInput input)
    {
        if (PlatformPurchaseTerms.Refusal(input.Accepted, input.VersionShown) is { } refusal) throw refusal;
    }

    public static PlatformPurchaseAcceptance Add(AccountingDbContext db, Guid buyerCompanyId, string kind, Guid? subjectId,
        PlatformPurchaseAcceptanceInput input, DateTime nowUtc)
    {
        EnsureAccepted(input);
        var at = PdpaConsentEvidence.NormalizeTimestamp(nowUtc);
        var ua = input.UserAgent is { Length: > MaxUserAgent } u ? u[..MaxUserAgent] : input.UserAgent;
        var subject = (input.Subject ?? "").Trim();
        if (subject.Length > 256) subject = subject[..256];
        var row = new PlatformPurchaseAcceptance
        {
            BuyerCompanyId = buyerCompanyId,
            SubjectKind = kind,
            SubjectId = subjectId,
            Subject = subject,
            PolicyVersion = PlatformPurchaseTerms.Version,
            AcceptedAt = at,
            Channel = input.Channel,
            IpAddress = input.IpAddress,
            UserAgent = ua,
            EvidenceHash = PdpaConsentEvidence.ComputeHash(subject, PlatformPurchaseTerms.Purpose, PlatformPurchaseTerms.Version,
                at, input.Channel, input.IpAddress, ua),
            CreatedBy = subject,
        };
        db.PlatformPurchaseAcceptances.Add(row);
        return row;
    }
}
