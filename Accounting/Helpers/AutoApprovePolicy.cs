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

    /// <summary>ฝ่ายค้านรอบ 248 (HIGH/MED-2): ลายนิ้วมือของ "สิ่งที่ผู้ตั้งอนุมัติให้" ตอนตั้ง (<see cref="BindingHash"/> — ผู้ติดต่อ · ชนิดเอกสาร ·
    /// ยอด/บรรทัดเชิงเงิน · อนุมัติอัตโนมัติ · ส่งอีเมลอัตโนมัติ) — ผู้แก้รายการ (ไม่ต้องมีสิทธิ์อนุมัติ) เปลี่ยนค่าเหล่านี้ ⇒ ไม่ตรง ⇒ นโยบายหยุดมีผล ·
    /// null = ไม่ผูก (นโยบายระดับผู้ติดต่อ — มีผลกับรายการที่ผู้มีสิทธิ์ "รับรอง" ด้วย <see cref="InheritContact"/> เท่านั้น)</summary>
    [JsonPropertyName("bindingHash")]
    public string? BindingHashValue { get; init; }

    /// <summary>ใบรับรองระดับรายการ: "รายการนี้ใช้นโยบายของผู้ติดต่อ" (ผู้มีสิทธิ์อนุมัติรับรองค่าปัจจุบันของรายการ) — นโยบายของผู้ติดต่อไม่มีผลกับรายการที่ไม่มีใบรับรอง
    /// (กันสมาชิกไม่มีสิทธิ์สร้างรายการใหม่ AutoApprove ให้ลูกค้าที่มีนโยบาย)</summary>
    [JsonPropertyName("inheritContact")]
    public bool InheritContact { get; init; }

    /// <summary>ระดับที่มาของนโยบายที่มีผล ("Recurring" · "Contact") — ไม่เก็บลง JSON (ผู้ resolve ใส่)</summary>
    [JsonIgnore]
    public string? Scope { get; init; }

    public const string RuleCode = "APPROVE-UNATTENDED-POLICY";
    public const string ScopeRecurring = "Recurring";
    public const string ScopeContact = "Contact";
    /// <summary>รอบ 256 (คำตัดสินเจ้าของ 2026-11-09: "ใบเก่ายังไม่อนุมัติ/ค้างชำระ ไม่ควรกันใบงวดใหม่ของรายการประจำ") — ค่าตั้งต้นของรายการประจำที่ไม่ได้ตั้งนโยบาย</summary>
    public const string ScopeDefault = "Default";

    /// <summary>รอบ 256 — ค่าตั้งต้นของรายการประจำที่อนุมัติอัตโนมัติแต่ไม่ได้ตั้งนโยบาย: ปล่อย "ลูกค้าค้างชำระ" (AR-OVERDUE) ต่อ + หมายเหตุบนใบ + audit ·
    /// เดิม (รอบ 248 · Y1) ค่าตั้งต้น = หยุดรอคน ⇒ ใบงวดใหม่ค้างเป็นร่างทุกงวดเมื่อลูกค้ามีใบค้าง ทั้งที่เป็นค่าบริการตามสัญญา (เจ้าของกลับคำตัดสิน) ·
    /// ผู้รับผิดชอบ = ผู้สร้างรายการ (ต้องยังมีสิทธิ์อนุมัติ — ตรวจที่ <c>RecurringApprovePolicyLoader</c>) · อยากให้หยุด ⇒ ตั้งนโยบายของรายการเป็น "หยุดรอคน"</summary>
    public static AutoApprovePolicy RecurringDefault(string? createdBy)
        => new() { PassCodes = new[] { ArOverdueApprovalWarning.Code }, SetBy = createdBy, Scope = ScopeDefault };

    /// <summary>คำเตือนที่ปล่อยได้ — รหัส · ชื่อ · ตัวจับ (ป้าย) · คำอธิบาย (หน้าแสดงจากตรงนี้ — ไม่มีตารางใน JS)</summary>
    public sealed record Releasable(string Code, string Title, Func<string, bool> Matches, string Why);

    /// <summary>คำเตือนที่ <b>ปล่อยไม่ได้เสมอ</b> — แสดงในหน้าพร้อมเหตุผล (รหัสเหล่านี้ส่งมาในนโยบาย = ปฏิเสธพร้อมเหตุ)</summary>
    public sealed record NeverReleasable(string Code, string Title, string Why);

    public static readonly IReadOnlyList<Releasable> Catalogue = new[]
    {
        new Releasable(ArOverdueApprovalWarning.Code, "ลูกค้าค้างชำระเกิน 30 วัน", ArOverdueApprovalWarning.IsApprovalWarning,
            "เป็นการควบคุมสินเชื่อของบริษัทเอง (ไม่ใช่ข้อบังคับภาษี) — ปล่อยแล้วระบบอนุมัติใบงวดนี้ต่อ ลงหมายเหตุบนใบ + บันทึกตรวจสอบ (audit) ทุกครั้ง"),
    };

    /// <summary>รอบ 250 (WP-02 §2.4 ข้อ 12 · E L-4): รหัส "ข้อมูลผู้ซื้อไม่ครบ §86/4" (ปล่อยไม่ได้เสมอ) — เทสต์อ้างค่าคงที่นี้ ไม่พิมพ์ literal ซ้ำ</summary>
    public const string NeverBuyerTaxCode = "BUYER-86/4";

    public static readonly IReadOnlyList<NeverReleasable> NeverList = new[]
    {
        new NeverReleasable(NeverBuyerTaxCode, "ข้อมูลผู้ซื้อไม่ครบตาม ป.รัษฎากร §86/4",
            "ใบกำกับภาษีที่ขาดรายการ ผู้ซื้อใช้ขอคืนภาษีซื้อไม่ได้ (§82/5(1)) — ต้องแก้ข้อมูลผู้ติดต่อก่อนเสมอ"),
        new NeverReleasable("Σ-GAP", "ยอดไม่ตรงกระดาษ (เอกสารจากสแกน)", "ต้องมีคนตรวจกับกระดาษ (คำตัดสินเจ้าของข้อ 12)"),
        new NeverReleasable("VAT-NOT-ON-PAPER", "VAT ไม่ได้พิมพ์บนกระดาษ/ตรวจกับกระดาษไม่ได้", "ภาษีซื้อที่ระบบแต่งขึ้นเป็นภาษีซื้อต้องห้าม §82/5(1) (คำตัดสินรอบ 198 ข้อ 6)"),
        new NeverReleasable("PERIOD-CLOSED", "งวดบัญชีปิดแล้ว", "เป็นด่านปิดงวด ไม่ใช่คำเตือน"),
        new NeverReleasable("APPROVAL-LIMIT", "ด่านควบคุมภายในที่ไม่ใช่คำเตือน (เช่น งบประมาณแบบบล็อก · วางบิลเกิน · วงเงินเครดิตที่บล็อก)",
            "ด่านพวกนี้หยุดการอนุมัติด้วยข้อผิดพลาด ไม่ใช่คำเตือน — นโยบายนี้ไม่ override ด่านรอง (ใบประจำอนุมัติแบบไม่มีคนรับทราบ)"),
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
        string? bindingHash = null, bool inheritContact = false)
        => new()
        {
            BindingHashValue = bindingHash,
            InheritContact = inheritContact,
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

    /// <summary>ชั้นของนโยบาย (ฝ่ายค้าน HIGH/MED-2): รายการไม่มีใบรับรอง = null (ไม่ปล่อย — แม้ผู้ติดต่อมีนโยบาย) · รายการตั้งรหัสเอง (รวมตั้งว่าง = หยุดรอคน) =
    /// ของรายการ · รายการรับรอง "ใช้นโยบายของผู้ติดต่อ" = ของผู้ติดต่อ (null ถ้าผู้ติดต่อไม่ได้ตั้ง) · ลายนิ้วมือ/ผู้ตั้งตรวจที่ <c>RecurringApprovePolicyLoader</c></summary>
    public static AutoApprovePolicy? Effective(string? recurringJson, string? contactJson)
    {
        var rec = Parse(recurringJson, ScopeRecurring);
        if (rec == null) return null;
        return rec.InheritContact ? Parse(contactJson, ScopeContact) : rec;
    }

    /// <summary>ลายนิ้วมือของค่าที่ผู้ตั้งอนุมัติให้ (SHA-256 · canonical ตัวเดียวทั้งฝั่งเขียน/ฝั่งตรวจ — กฎ #4 C) · เฉพาะช่องที่กระทบเงิน/การส่ง —
    /// คำอธิบายบรรทัดไม่นับ (ตัวแปร &lt;&lt;month&gt;&gt; เปลี่ยนทุกงวดอยู่แล้ว) · เทมเพลตอ่านไม่ได้ = ค่าเฉพาะที่ไม่ตรงกับอะไร</summary>
    public static string BindingHash(Guid? contactId, string? documentType, string? templateData, bool autoApprove, bool autoSendEmail)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("v1|c=").Append(contactId?.ToString("D") ?? "-").Append("|t=").Append(documentType ?? "-")
          .Append("|aa=").Append(autoApprove ? 1 : 0).Append("|ae=").Append(autoSendEmail ? 1 : 0);
        static string N(JsonElement o, string k)
        {
            if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(k, out var e)) return "-";
            return e.ValueKind switch
            {
                JsonValueKind.Number => e.TryGetDecimal(out var d) ? d.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture) : e.GetRawText(),
                JsonValueKind.String => decimal.TryParse(e.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var ds)
                    ? ds.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture) : (e.GetString() ?? "-").Trim(),
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                _ => "-",
            };
        }
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(templateData) ? "{}" : templateData);
            var root = doc.RootElement;
            foreach (var k in new[] { "pricesIncludeVat", "billDiscountPercent", "billDiscountAmount", "combinedInvoiceTaxInvoice", "currency", "whtCertType" })
                sb.Append('|').Append(k).Append('=').Append(N(root, k));
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                foreach (var l in lines.EnumerateArray())
                {
                    sb.Append("|L");
                    foreach (var k in new[] { "quantity", "unitPrice", "vatRate", "withholdingTaxRate", "discountPercent", "discountAmount", "accountId", "productCode" })
                        sb.Append(',').Append(N(l, k));
                }
        }
        catch (JsonException) { sb.Append("|unreadable"); }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>ใบรับรองยังตรงกับค่าปัจจุบันของรายการไหม — ไม่มีลายนิ้วมือ (นโยบายที่ตั้งก่อนมีการผูก) = ไม่ตรง (ระวังไว้ก่อน)</summary>
    public static bool MatchesBinding(AutoApprovePolicy recurringPolicy, string currentHash)
        => recurringPolicy.BindingHashValue != null && string.Equals(recurringPolicy.BindingHashValue, currentHash, StringComparison.Ordinal);

    /// <summary>หมายเหตุบนเอกสาร (ต่อท้ายหมายเหตุอนุมัติ) — บอกว่าปล่อยตามนโยบายของใคร ระดับไหน</summary>
    public static string NoteLine(AutoApprovePolicy policy, IReadOnlyList<string> released)
        => policy.Scope == ScopeDefault
            ? $"อนุมัติต่อตามค่าตั้งต้นของรายการประจำ (ผู้สร้างรายการ {policy.SetBy ?? "-"} · ตั้งเป็น \"หยุดรอคน\" ได้ที่รายการประจำ): "
              + string.Join(", ", released.Select(w => CodeOf(w) ?? "?").Distinct())
            : $"ปล่อยตามนโยบายอนุมัติ ({(policy.Scope == ScopeContact ? "ของผู้ติดต่อ" : "ของรายการประจำ")}"
           + $" · ตั้งโดย {policy.SetBy ?? "-"}"
           + (policy.SetAt is { } at ? $" เมื่อ {at.AddHours(7):yyyy-MM-dd HH:mm} น." : "")
           + $"): {string.Join(", ", released.Select(w => CodeOf(w) ?? "?").Distinct())}";
}
