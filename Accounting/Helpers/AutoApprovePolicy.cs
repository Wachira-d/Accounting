using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Accounting.Helpers;

/// <summary>
/// **นโยบายอนุมัติอัตโนมัติข้ามคำเตือน — ตัวตัดสินเดียว** (pure · รอบ 248 · AUTOPILOT_BILLING_DESIGN §4.3 · หลักการ #10 "ทุกการปล่อยผ่านที่เจ้าของเลือก = มีร่องรอย")
///
/// <para>เจ้าของเลือก "รหัสคำเตือน" ที่ยอมให้ระบบอนุมัติต่อได้ (ตอนนี้มีรหัสเดียว <see cref="ArOverdueApprovalWarning.Code"/>) · ใช้ได้กับแหล่ง
/// <see cref="ApprovalAckSource.UnattendedWithPolicy"/> เท่านั้น (ใบประจำ) · ตั้งได้ <b>ต่อรายการประจำ</b> และ <b>ต่อผู้ติดต่อ</b> — ไม่มีค่าระดับบริษัท
/// (คำถามเจ้าของ Q3 · ค่าตั้งต้นของทีม = ปิดทั้งคู่ · <b>รอเจ้าของยืนยัน</b>)</para>
///
/// <para>ปล่อยได้เฉพาะคำเตือนที่ (1) มีป้ายรหัสอยู่ใน <see cref="Catalogue"/> (2) รหัสนั้นอยู่ในนโยบาย (3) <b>ไม่</b>อยู่ในชุดห้ามปล่อย
/// (<see cref="NeverPassable"/>: [Σ-GAP] ยอดไม่ตรงกระดาษ · VAT ไม่ได้พิมพ์บนกระดาษ) · คำเตือนที่ไม่มีป้าย (เช่น ผู้ซื้อ §86/4 ไม่ครบ · งวดปิด)
/// ปล่อยไม่ได้โดยโครงสร้าง — ห้าม match ข้อความไทย</para>
/// </summary>
public sealed record AutoApprovePolicy
{
    /// <summary>รหัสคำเตือนที่ปล่อยได้ (ว่าง = "หยุดรอคนทุกข้อ" — นโยบายที่ตั้งไว้แต่ไม่ปล่อยอะไร · ใช้ทับค่าผู้ติดต่อ)</summary>
    [JsonPropertyName("passCodes")]
    public IReadOnlyList<string> PassCodes { get; init; } = Array.Empty<string>();

    /// <summary>ผู้ตั้ง (id ผู้ใช้) — ออกจากบริษัทแล้ว ⇒ นโยบายหยุดมีผล (ตรวจตอนรัน · <c>CompanyMembership</c>)</summary>
    [JsonPropertyName("setBy")]
    public string? SetBy { get; init; }

    [JsonPropertyName("setAt")]
    public DateTime? SetAt { get; init; }

    /// <summary>ผู้ติดต่อที่ผู้ตั้งอนุมัติให้ปล่อย (นโยบายระดับรายการประจำ) — ฝ่ายค้านรอบ 248: ผู้ไม่มีสิทธิ์อนุมัติเปลี่ยนผู้ติดต่อของรายการได้
    /// (แก้รายการไม่ต้องมีสิทธิ์อนุมัติ) ⇒ นโยบายที่ผู้มีสิทธิ์ตั้งให้ลูกค้า A ต้องไม่ปล่อยใบของลูกค้า B · null = ไม่ผูก (นโยบายระดับผู้ติดต่อ — ผูกกับแถวตัวเองอยู่แล้ว)</summary>
    [JsonPropertyName("contactId")]
    public Guid? BoundContactId { get; init; }

    /// <summary>ชนิดเอกสารตอนตั้ง (ชื่อ enum) — เปลี่ยนชนิดหลังตั้ง ⇒ นโยบายหยุดมีผล (ผู้ตั้งอนุมัติให้ชนิดนั้น) · null = ไม่ผูก</summary>
    [JsonPropertyName("documentType")]
    public string? BoundDocumentType { get; init; }

    /// <summary>ระดับที่มาของนโยบายที่มีผล ("Recurring" · "Contact") — ไม่เก็บลง JSON (ผู้ resolve ใส่)</summary>
    [JsonIgnore]
    public string? Scope { get; init; }

    public const string RuleCode = "APPROVE-UNATTENDED-POLICY";
    public const string ScopeRecurring = "Recurring";
    public const string ScopeContact = "Contact";

    /// <summary>คำเตือนที่ปล่อยได้ — รหัส · ชื่อ · ตัวจับ (ป้าย) · คำอธิบาย (หน้าแสดงจากตรงนี้ — ไม่มีตารางใน JS)</summary>
    public sealed record Releasable(string Code, string Title, Func<string, bool> Matches, string Why);

    /// <summary>คำเตือนที่ <b>ปล่อยไม่ได้เสมอ</b> — แสดงในหน้าพร้อมเหตุผล (รหัสเหล่านี้ส่งมาในนโยบาย = ปฏิเสธพร้อมเหตุ)</summary>
    public sealed record NeverReleasable(string Code, string Title, string Why);

    public static readonly IReadOnlyList<Releasable> Catalogue = new[]
    {
        new Releasable(ArOverdueApprovalWarning.Code, "ลูกค้าค้างชำระเกิน 30 วัน", ArOverdueApprovalWarning.IsApprovalWarning,
            "เป็นการควบคุมสินเชื่อของบริษัทเอง (ไม่ใช่ข้อบังคับภาษี) — ปล่อยแล้วระบบอนุมัติใบงวดนี้ต่อ ลงหมายเหตุบนใบ + บันทึกตรวจสอบ (audit) ทุกครั้ง"),
    };

    public static readonly IReadOnlyList<NeverReleasable> NeverList = new[]
    {
        new NeverReleasable("BUYER-86/4", "ข้อมูลผู้ซื้อไม่ครบตาม ป.รัษฎากร §86/4",
            "ใบกำกับภาษีที่ขาดรายการ ผู้ซื้อใช้ขอคืนภาษีซื้อไม่ได้ (§82/5(1)) — ต้องแก้ข้อมูลผู้ติดต่อก่อนเสมอ"),
        new NeverReleasable("Σ-GAP", "ยอดไม่ตรงกระดาษ (เอกสารจากสแกน)", "ต้องมีคนตรวจกับกระดาษ (คำตัดสินเจ้าของข้อ 12)"),
        new NeverReleasable("VAT-NOT-ON-PAPER", "VAT ไม่ได้พิมพ์บนกระดาษ/ตรวจกับกระดาษไม่ได้", "ภาษีซื้อที่ระบบแต่งขึ้นเป็นภาษีซื้อต้องห้าม §82/5(1) (คำตัดสินรอบ 198 ข้อ 6)"),
        new NeverReleasable("PERIOD-CLOSED", "งวดบัญชีปิดแล้ว", "เป็นด่านปิดงวด ไม่ใช่คำเตือน"),
        new NeverReleasable("APPROVAL-LIMIT", "เกินวงเงินที่ต้องให้ผู้มีอำนาจอนุมัติ", "เป็นการควบคุมภายในของบริษัท — นโยบายนี้ไม่ข้ามด่านวงเงินอนุมัติ"),
    };

    /// <summary>คำเตือนที่ไม่มีวันปล่อยด้วยนโยบาย (ตัวจับป้ายของตัวตั้งเดิม — ไม่อ่านข้อความเอง)</summary>
    public static bool NeverPassable(string? warning)
        => warning == null || OcrApprovalGapWarning.IsGapWarning(warning) || OcrApprovalGapWarning.IsVatDerivedWarning(warning);

    /// <summary>รหัสของคำเตือน (จาก <see cref="Catalogue"/>) — null = ไม่มีป้ายที่นโยบายรู้จัก (ปล่อยไม่ได้)</summary>
    public static string? CodeOf(string? warning)
        => warning == null || NeverPassable(warning) ? null : Catalogue.FirstOrDefault(c => c.Matches(warning))?.Code;

    /// <summary>นโยบายนี้ปล่อยคำเตือนข้อนี้ไหม</summary>
    public static bool Releases(AutoApprovePolicy? policy, string? warning)
        => policy is { PassCodes.Count: > 0 } && CodeOf(warning) is { } code
           && policy.PassCodes.Contains(code, StringComparer.OrdinalIgnoreCase);

    /// <summary>คำเตือนที่นโยบายปล่อยจริงในชุดนี้ (ลงร่องรอย)</summary>
    public static IReadOnlyList<string> Released(AutoApprovePolicy? policy, IEnumerable<string> warnings)
        => warnings.Where(w => Releases(policy, w)).ToList();

    /// <summary>ตรวจรหัสที่ผู้ใช้ส่งมา — null = ใช้ได้ · ข้อความ = ปฏิเสธ (บอกเหตุ + ทางไปต่อ) · ห้ามเก็บรหัสที่ไม่รู้จักเงียบ ๆ</summary>
    public static string? Validate(IEnumerable<string>? codes)
    {
        var list = (codes ?? Array.Empty<string>()).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
        foreach (var c in list)
        {
            var never = NeverList.FirstOrDefault(n => string.Equals(n.Code, c, StringComparison.OrdinalIgnoreCase));
            if (never != null)
                return $"ตั้งนโยบายให้ปล่อย \"{never.Title}\" ไม่ได้ — {never.Why}";
            if (!Catalogue.Any(k => string.Equals(k.Code, c, StringComparison.OrdinalIgnoreCase)))
                return $"ไม่รู้จักรหัสคำเตือน \"{c}\" — เลือกได้เฉพาะ: " + string.Join(", ", Catalogue.Select(k => $"{k.Code} ({k.Title})"));
        }
        return null;
    }

    /// <summary>นโยบายใหม่จากรหัสที่ผ่าน <see cref="Validate"/> แล้ว (รหัสเป็นรูปมาตรฐานของ <see cref="Catalogue"/> · ไม่ซ้ำ)</summary>
    public static AutoApprovePolicy Create(IEnumerable<string>? codes, string setBy, DateTime utcNow,
        Guid? boundContactId = null, string? boundDocumentType = null)
        => new()
        {
            BoundContactId = boundContactId,
            BoundDocumentType = boundDocumentType,
            PassCodes = (codes ?? Array.Empty<string>())
                .Select(c => Catalogue.FirstOrDefault(k => string.Equals(k.Code, c?.Trim(), StringComparison.OrdinalIgnoreCase))?.Code)
                .Where(c => c != null).Select(c => c!).Distinct().ToList(),
            SetBy = setBy,
            SetAt = utcNow,
        };

    /// <summary>JSON ↔ นโยบาย (ตัวเดียวทั้งฝั่งเขียน/อ่าน) · null/อ่านไม่ได้ = ไม่ได้ตั้ง (ตามชั้นถัดไป) · รหัสที่ไม่รู้จักถูกตัดทิ้ง (ไม่ปล่อย)</summary>
    public static AutoApprovePolicy? Parse(string? json, string? scope = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var p = JsonSerializer.Deserialize<AutoApprovePolicy>(json);
            if (p == null) return null;
            var codes = (p.PassCodes ?? Array.Empty<string>())
                .Where(c => Catalogue.Any(k => string.Equals(k.Code, c, StringComparison.OrdinalIgnoreCase))).ToList();
            return p with { PassCodes = codes, Scope = scope };
        }
        catch (JsonException) { return null; }
    }

    public static string Serialize(AutoApprovePolicy policy) => JsonSerializer.Serialize(policy);

    /// <summary>ชั้นของนโยบาย: รายการประจำ (ตั้งไว้ — รวมตั้งว่าง = หยุดรอคน) ชนะผู้ติดต่อ · ไม่ตั้งทั้งคู่ = null (พฤติกรรมเดิม — หยุดรอคน)</summary>
    public static AutoApprovePolicy? Effective(string? recurringJson, string? contactJson)
        => Parse(recurringJson, ScopeRecurring) ?? Parse(contactJson, ScopeContact);

    /// <summary>นโยบายยังตรงกับรายการตอนนี้ไหม (ผู้ติดต่อ/ชนิดเอกสารเดียวกับตอนตั้ง) — ไม่ตรง ⇒ หยุดมีผล (ผู้ตั้งไม่ได้อนุมัติให้ลูกค้า/ชนิดใหม่)</summary>
    public static bool MatchesBinding(AutoApprovePolicy policy, Guid? contactId, string? documentType)
        => (policy.BoundContactId == null || policy.BoundContactId == contactId)
           && (policy.BoundDocumentType == null || string.Equals(policy.BoundDocumentType, documentType, StringComparison.Ordinal));

    /// <summary>หมายเหตุบนเอกสาร (ต่อท้ายหมายเหตุอนุมัติ) — บอกว่าปล่อยตามนโยบายของใคร ระดับไหน</summary>
    public static string NoteLine(AutoApprovePolicy policy, IReadOnlyList<string> released)
        => $"ปล่อยตามนโยบายอนุมัติ ({(policy.Scope == ScopeContact ? "ของผู้ติดต่อ" : "ของรายการประจำ")}"
           + $" · ตั้งโดย {policy.SetBy ?? "-"}"
           + (policy.SetAt is { } at ? $" เมื่อ {at.AddHours(7):yyyy-MM-dd HH:mm} น." : "")
           + $"): {string.Join(", ", released.Select(w => CodeOf(w) ?? "?").Distinct())}";
}
