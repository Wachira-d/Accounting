using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 214 ต่อ (คำตัดสินข้อ 173 ฎ) — กติกาเดียวของหลักฐาน "ยอมรับข้อกำหนด + นโยบายแพลตฟอร์ม" (<see cref="PlatformPolicyAcceptance"/>):
/// <list type="bullet">
/// <item><b>เขียน</b> (<see cref="Create"/>): เวลา normalize (<see cref="PdpaConsentEvidence.NormalizeTimestamp"/>) · UA/IP ตัดความยาวก่อน hash ·
/// เก็บทุกช่องที่อยู่ใน canonical (อีเมล · วัตถุประสงค์ · เวอร์ชันที่หน้าเว็บแสดง · เวลา · ช่องทาง · IP · UA) ⇒ ตรวจกลับจากแถวได้ (<see cref="Check"/>)</item>
/// <item><b>ลบตามคำขอ ม.33</b> (<see cref="Pseudonymise"/>): หลักฐานความยินยอมเป็นภาระพิสูจน์ของผู้ควบคุมข้อมูล (ม.19 วรรคท้าย) และใช้ต่อสู้สิทธิเรียกร้อง
/// (ม.33 วรรคหนึ่ง (5)) ⇒ <b>legal hold</b>: คงแถว (วัตถุประสงค์ · ฉบับ · เวลา · ช่องทาง · hash) แต่ทำอีเมล/IP/UA ไม่ให้ระบุตัว — hash เดิมพิสูจน์
/// "เคยมีการยินยอม ณ เวลานั้น" ได้ แต่ตรวจซ้ำไม่ได้แล้วโดยเจตนา (ต้องใช้ PII ที่ลบไป)</item>
/// <item><b>เก็บนานเท่าไร</b> (<see cref="PurgeCutoff"/>): บัญชียังอยู่ = ตลอดอายุบัญชี (นโยบายฉบับ 1.0 ข้อ 7) · ลบตามคำขอแล้ว = อีก
/// <see cref="RetentionYearsAfterErasure"/> ปี (อายุความทั่วไป ป.พ.พ. ม.193/30) แล้วลบจริง (งาน <c>PdpaRetentionPurgeJob</c>) — MAX(retention) ตามกฎ J</item>
/// </list>
/// </summary>
public static class PlatformPolicyAcceptanceRules
{
    public const int MaxUserAgent = 500;
    public const int MaxIp = 45;
    public const int RetentionYearsAfterErasure = 10;

    public static PlatformPolicyAcceptance Create(Guid userId, string email, string purpose, string policyVersion,
        string? versionShown, DateTime nowUtc, string channel, string? ip, string? userAgent)
    {
        var at = PdpaConsentEvidence.NormalizeTimestamp(nowUtc);
        var ipT = string.IsNullOrWhiteSpace(ip) ? null : (ip.Length > MaxIp ? ip[..MaxIp] : ip);
        var uaT = string.IsNullOrEmpty(userAgent) ? null : (userAgent.Length > MaxUserAgent ? userAgent[..MaxUserAgent] : userAgent);
        var shown = string.IsNullOrWhiteSpace(versionShown) ? null : versionShown.Trim();
        if (shown is { Length: > 50 }) shown = shown[..50];
        return new PlatformPolicyAcceptance
        {
            UserId = userId,
            Email = email,
            Purpose = purpose,
            PolicyVersion = policyVersion,
            VersionShown = shown,
            AcceptedAt = at,
            Channel = channel,
            IpAddress = ipT,
            UserAgent = uaT,
            EvidenceHash = PdpaConsentEvidence.ComputeHash(email, purpose, shown, at, channel, ipT, uaT),
        };
    }

    /// <summary>ตรวจหลักฐานจากค่าในแถว (round-trip ผ่านฐาน) — แถวที่ลบตามคำขอแล้ว = <c>LegacyUnverifiable</c> (ไม่ใช่ Invalid · ไม่ใช่ผ่าน)</summary>
    public static PdpaConsentEvidence.EvidenceCheck Check(PlatformPolicyAcceptance r)
        => r.PseudonymisedAt != null
            ? PdpaConsentEvidence.EvidenceCheck.LegacyUnverifiable
            : PdpaConsentEvidence.Check(r.EvidenceHash, r.Email, r.Purpose, r.VersionShown, r.AcceptedAt, r.Channel, r.IpAddress, r.UserAgent);

    /// <summary>ลบตามคำขอ (legal hold) — อีเมลแทนด้วยอีเมลนิรนามของผู้ใช้ (ตัวเดียวกับที่ PdpaService ตั้งบน User) · IP/UA ล้าง</summary>
    public static void Pseudonymise(PlatformPolicyAcceptance r, string anonymisedEmail, DateTime nowUtc)
    {
        r.Email = anonymisedEmail;
        r.IpAddress = null;
        r.UserAgent = null;
        r.PseudonymisedAt ??= nowUtc;
    }

    /// <summary>ฝ่ายค้าน S1: แถวยินยอมเดิมใน <c>PdpaConsentRecords</c> (ระดับแพลตฟอร์ม CompanyId ศูนย์ — ต้นฉบับที่ migration คัดลอกมา)
    /// ลบตามคำขอแบบเดียวกัน — อีเมล/IP ทำไม่ให้ระบุตัว · คงแถว · วันลบครั้งแรกไม่เลื่อน</summary>
    public static void Pseudonymise(PdpaConsentRecord r, string anonymisedEmail, DateTime nowUtc)
    {
        r.SubjectContact = anonymisedEmail;
        r.IpAddress = null;
        r.PseudonymisedAt ??= nowUtc;
    }

    public const string StaffEnteredLabel = "บันทึกโดยเจ้าหน้าที่ (ไม่ใช่ hash ของระบบ)";

    /// <summary>ฝ่ายค้าน S4/N2: ผลตรวจหลักฐานของแถวความยินยอมบนเว็บร้าน/ที่เจ้าหน้าที่บันทึก — ป้ายเดียวกับแถวยอมรับนโยบาย ·
    /// hash ที่เจ้าหน้าที่กรอกเอง (ของเอกสารภายนอก) ตรวจด้วย canonical ไม่ได้ ⇒ บอกที่มา ไม่ใช่ "ไม่ตรง"</summary>
    public static string CheckLabel(PdpaConsentRecord c)
    {
        if (c.EvidenceSource == PdpaConsentEvidence.SourceStaff) return StaffEnteredLabel;
        if (c.PseudonymisedAt != null) return CheckLabel(PdpaConsentEvidence.EvidenceCheck.LegacyUnverifiable);
        return CheckLabel(PdpaConsentEvidence.Check(c.EvidenceHash, c.SubjectContact, c.Purpose, c.PolicyVersion,
            c.GrantedAt, c.Channel, c.IpAddress, null));
    }

    /// <summary>แถวที่ลบตามคำขอก่อนเวลานี้ = พ้น legal hold ลบจริงได้</summary>
    public static DateTime PurgeCutoff(DateTime nowUtc) => nowUtc.AddYears(-RetentionYearsAfterErasure);

    /// <summary>ป้ายผลตรวจภาษาไทยสำหรับรายงาน DSR</summary>
    public static string CheckLabel(PdpaConsentEvidence.EvidenceCheck c) => c switch
    {
        PdpaConsentEvidence.EvidenceCheck.Valid => "ตรวจแล้ว ตรงกับหลักฐาน",
        PdpaConsentEvidence.EvidenceCheck.Invalid => "ไม่ตรง — ข้อมูลถูกแก้หลังบันทึก",
        PdpaConsentEvidence.EvidenceCheck.Missing => "ไม่มีหลักฐาน hash",
        _ => "หลักฐานรุ่นเก่า/ทำให้ไม่ระบุตัวแล้ว — ตรวจซ้ำไม่ได้",
    };
}
