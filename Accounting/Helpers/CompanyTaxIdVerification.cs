using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 285 — <b>ตัวตัดสินเดียว</b>ของ "บริษัทยืนยันเลขผู้เสียภาษีแล้ว" (คำตัดสิน 285-1…285-6 · ที่มา: 280-10 ฝ่ายค้าน MED-1 —
/// ใครก็ตั้ง <c>Company.TaxId</c> เป็นเลขของคนอื่นได้)
///
/// <para><b>หลักฐาน (285-1):</b> ชื่อในทะเบียนเป็นข้อมูลสาธารณะ ⇒ "ชื่อตรงทะเบียน" พิสูจน์ความเป็นเจ้าของ<b>ไม่ได้</b> ใช้เป็นด่านตัดของที่ผิดชัด
/// (ชื่อไม่ตรง = ยื่นไม่ได้) เท่านั้น · ตัวพิสูจน์ = เอกสาร ภ.พ.20 หรือหนังสือรับรองการจดทะเบียน ที่<b>คน</b> (ผู้ดูแลแพลตฟอร์ม · SystemAdmin)
/// เปิดดูแล้วอนุมัติ · การยืนยันทางอีเมลโดเมนไม่ใช้ (โดเมนไม่ผูกกับเลขภาษี) · เทียบชื่อ = ตรงตัวหลังตัดคำนำหน้า/ต่อท้ายด้วย
/// <see cref="DbdIdentityGuard.Normalize"/> — ห้าม fuzzy</para>
///
/// <para><b>ผล (285-3):</b> ยืนยันแล้ว ⇒ เปิดคำขอเชื่อมคู่ค้าที่ส่งถึงเลขนี้<b>ก่อน</b>ตั้งเลขได้ (<see cref="IncomingRequestCutoff"/>) ·
/// ยังไม่ยืนยัน ⇒ เฉพาะคำขอหลัง <c>TaxIdSetAt</c> (พฤติกรรมรอบ 280)</para>
/// <para><b>หลุดสถานะ (285-4):</b> เปลี่ยนเลข ⇒ <see cref="ClearOnTaxIdChange"/> (เรียกจาก SaveChanges ทุกทางเข้า) · และ
/// <see cref="IsVerified"/> เทียบเลขที่ยืนยันกับเลขปัจจุบันอีกชั้น</para>
/// </summary>
public static class CompanyTaxIdVerification
{
    public const string RuleCode = "R285-TAXID-VERIFY";
    public const string LegalReference = "ภ.พ.20 (ใบทะเบียนภาษีมูลค่าเพิ่ม) / หนังสือรับรองการจดทะเบียนนิติบุคคล — หลักฐานเจ้าของเลขประจำตัวผู้เสียภาษี";
    /// <summary>ชนิดไฟล์แนบของหลักฐาน (<c>FileAttachments.EntityType</c> · EntityId = id ของคำขอ)</summary>
    public const string AttachmentEntityType = "CompanyTaxIdEvidence";

    public const string EvidencePp20 = "Pp20";
    public const string EvidenceCompanyCertificate = "CompanyCertificate";
    public static readonly IReadOnlyList<string> EvidenceKinds = new[] { EvidencePp20, EvidenceCompanyCertificate };

    public const string StatusPending = "Pending";
    public const string StatusApproved = "Approved";
    public const string StatusRejected = "Rejected";
    /// <summary>อนุมัติแล้วแต่ผู้ดูแลถอนภายหลัง</summary>
    public const string StatusRevoked = "Revoked";

    public const string RegistryMatch = "Match";
    public const string RegistryMismatch = "Mismatch";
    public const string RegistryUnknown = "Unknown";

    public const int MaxEvidenceBytes = 10 * 1024 * 1024;
    public const int MaxNoteLength = 1000;

    /// <summary>ป้ายชนิดหลักฐาน (หน้าแสดงตรงตัว)</summary>
    public static string EvidenceLabel(string? kind) => kind switch
    {
        EvidencePp20 => "ภ.พ.20 (ใบทะเบียนภาษีมูลค่าเพิ่ม)",
        EvidenceCompanyCertificate => "หนังสือรับรองการจดทะเบียนนิติบุคคล",
        _ => "ไม่ทราบชนิด",
    };

    public static string StatusLabel(string? status) => status switch
    {
        StatusPending => "รอผู้ดูแลระบบตรวจ",
        StatusApproved => "ยืนยันแล้ว",
        StatusRejected => "ถูกปฏิเสธ",
        StatusRevoked => "ถูกถอนการยืนยัน",
        _ => "ยังไม่ได้ยื่น",
    };

    public static string RegistryLabel(string? result, string? registryName) => result switch
    {
        RegistryMatch => $"ชื่อตรงทะเบียน ({registryName})",
        RegistryMismatch => $"ชื่อไม่ตรงทะเบียน — ทะเบียน: {registryName}",
        _ => "ตรวจกับทะเบียนไม่ได้ (ทะเบียนไม่ตอบ/ไม่ใช่นิติบุคคล) — ผู้ดูแลตัดสินจากเอกสาร",
    };

    /// <summary><b>ตัวตัดสินเดียว</b>ว่าบริษัทยืนยันเลขปัจจุบันแล้วหรือไม่ — ต้องมีเวลาอนุมัติ + เลขที่ยืนยันตรงเลขปัจจุบัน + เลขถูกต้อง (checksum)</summary>
    public static bool IsVerified(string? currentTaxId, DateTime? verifiedAt, string? verifiedFor)
    {
        if (verifiedAt == null || string.IsNullOrEmpty(verifiedFor)) return false;
        if (!ThaiTaxId.IsValid(currentTaxId)) return false;
        return ThaiTaxId.Normalize(currentTaxId) == ThaiTaxId.Normalize(verifiedFor);
    }

    public static bool IsVerified(Company c) => IsVerified(c.TaxId, c.TaxIdVerifiedAt, c.TaxIdVerifiedFor);

    /// <summary>ล้างสถานะยืนยันเมื่อเลขเปลี่ยน (ผู้เรียก: <c>AccountingDbContext.StampTaxIdSetAt</c> เท่านั้น)</summary>
    public static void ClearOnTaxIdChange(Company c)
    {
        c.TaxIdVerifiedAt = null;
        c.TaxIdVerifiedFor = null;
        c.TaxIdVerificationRequestId = null;
    }

    /// <summary>คำขอเชื่อมคู่ค้าที่ส่งถึงเลขของเรา "ตั้งแต่เมื่อไร" ที่เปิดให้ได้ · ยืนยันแล้ว = ทุกคำขอที่ยังไม่หมดอายุ (<see cref="DateTime.MinValue"/>) ·
    /// ยังไม่ยืนยัน = หลังตั้งเลข · ไม่รู้เวลาตั้งเลข = null (ไม่เปิดเลย)</summary>
    public static DateTime? IncomingRequestCutoff(bool verified, DateTime? taxIdSetAt) => verified ? DateTime.MinValue : taxIdSetAt;

    /// <summary>เทียบชื่อบริษัทกับชื่อทะเบียน — ตรงตัวหลังตัดคำนำหน้า/ต่อท้าย/ช่องว่าง/เครื่องหมาย (ไม่ fuzzy) · ทะเบียนว่าง = ไม่ทราบ</summary>
    public static string CompareRegistryName(string? companyName, string? registryName)
    {
        var reg = DbdIdentityGuard.Normalize(registryName);
        if (reg.Length == 0) return RegistryUnknown;
        var mine = DbdIdentityGuard.Normalize(companyName);
        if (mine.Length == 0) return RegistryMismatch;
        return mine == reg ? RegistryMatch : RegistryMismatch;
    }

    /// <summary>ยื่นได้ไหม — null = ได้ · อื่น ๆ = เหตุ + ทางไปต่อ (ข้อความไทยถึงผู้ใช้)</summary>
    public static string? SubmitProblem(string? taxId, string? evidenceKind, int fileBytes, bool alreadyVerified, bool hasPending,
        string registryResult, string? registryName)
    {
        if (!ThaiTaxId.IsValid(taxId))
            return "เลขผู้เสียภาษีของบริษัทยังไม่ถูกต้อง (ต้องเป็นเลข 13 หลักที่ผ่าน checksum) — แก้ที่ช่องเลขผู้เสียภาษีแล้วบันทึกก่อนยื่นหลักฐาน";
        if (evidenceKind == null || !EvidenceKinds.Contains(evidenceKind))
            return "เลือกชนิดหลักฐาน: ภ.พ.20 หรือหนังสือรับรองการจดทะเบียน";
        if (fileBytes <= 0) return "แนบไฟล์หลักฐาน (PDF หรือรูปภาพ)";
        if (fileBytes > MaxEvidenceBytes) return "ไฟล์หลักฐานใหญ่เกิน 10 MB";
        if (alreadyVerified) return "เลขผู้เสียภาษีนี้ยืนยันแล้ว";
        if (hasPending) return "มีคำขอยืนยันที่รอผู้ดูแลระบบตรวจอยู่แล้ว — รอผลก่อนยื่นใหม่";
        if (registryResult == RegistryMismatch)
            return $"ชื่อบริษัทในระบบไม่ตรงกับทะเบียนของเลขนี้ (ทะเบียน: {registryName}) — ตรวจว่าเลขผู้เสียภาษีถูกต้อง แล้วแก้ชื่อบริษัทให้ตรงทะเบียนก่อนยื่น";
        return null;
    }

    /// <summary>อนุมัติได้ไหม — คำขอต้องรอตรวจ · เลขในคำขอต้องตรงเลขปัจจุบันของบริษัท (บริษัทเปลี่ยนเลขหลังยื่น = อนุมัติไม่ได้) · ชื่อไม่ตรงทะเบียน = ไม่ได้</summary>
    public static string? ApproveProblem(string? status, string? requestTaxId, string? currentTaxId, string? registryResult, bool hasEvidence)
    {
        if (status != StatusPending) return "คำขอนี้ไม่ได้อยู่ในสถานะรอตรวจ";
        if (!ThaiTaxId.IsValid(currentTaxId) || ThaiTaxId.Normalize(currentTaxId) != ThaiTaxId.Normalize(requestTaxId))
            return "บริษัทเปลี่ยนเลขผู้เสียภาษีหลังยื่นคำขอ — อนุมัติไม่ได้ (ให้บริษัทยื่นหลักฐานของเลขปัจจุบันใหม่)";
        if (registryResult == RegistryMismatch) return "ชื่อบริษัทไม่ตรงทะเบียน — อนุมัติไม่ได้";
        if (!hasEvidence) return "ไม่พบไฟล์หลักฐาน — อนุมัติไม่ได้";
        return null;
    }

    /// <summary>ปฏิเสธ/ถอน ต้องมีเหตุ (บริษัทเห็นเหตุนี้)</summary>
    public static string? ReasonProblem(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "ระบุเหตุผล (บริษัทจะเห็นข้อความนี้)";
        if (reason.Trim().Length > MaxNoteLength) return "เหตุผลยาวเกิน 1,000 ตัวอักษร";
        return null;
    }

    /// <summary>ป้ายสถานะเลขภาษีของ<b>คู่ค้า</b>บนการ์ดคำขอ (เซิร์ฟเวอร์ตัดสิน · หน้าแสดงตรงตัว)</summary>
    public static string PartnerTaxIdLabel(bool verified) => verified
        ? "เลขผู้เสียภาษีนี้ผ่านการยืนยันเอกสารกับ NextAcc แล้ว"
        : "เลขผู้เสียภาษีนี้ผู้ขอตั้งเอง — ยังไม่ได้ยืนยันเอกสารกับ NextAcc";
}
