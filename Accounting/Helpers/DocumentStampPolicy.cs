using System.Text.Json;
using System.Text.Json.Serialization;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ตัวตัดสินเดียว "ตราประทับ/ลายเซ็นอิเล็กทรอนิกส์ บนเอกสารที่พิมพ์" (Document Stamp Rules · ทีม J 2026-10-22)
///
/// ═══ ที่มา ═══
/// (1) ใบ 50 ทวิ ไม่แสดงตราประทับเลยทั้งดูบนเว็บ · ดาวน์โหลด PDF · พิมพ์ — ทางออก 50 ทวิทั้งสาม (HTML <c>BuildWithholdingTaxCertHtml</c> ·
/// QuestPDF <c>BuildWhtCertPdf</c> · หน้าพิมพ์ <c>wht.html printCert</c>) ไม่เคยโหลด <c>CompanySettings.StampPath</c> — วาดแค่วงกลมว่าง
/// "ประทับตรานิติบุคคล (ถ้ามี)" · ขณะที่เอกสารขายประทับได้เพราะ <c>BuildDocumentHtml</c>/<c>BuildBranding</c> อ่านตรา
/// (2) เงื่อนไข "ประทับเมื่อไร" เขียน inline 3 ที่ (HTML · QuestPDF · ResolveSigners) — ไม่มีที่ให้บริษัทตั้งแยกตามประเภท/สถานะ
///
/// ═══ กติกา ═══
/// คีย์กฎ = ชื่อ enum <see cref="DocumentType"/> (Quotation · Invoice · TaxInvoice · Receipt · …) + <see cref="WhtCertKey"/> ·
/// สถานะยุบเป็น 5 ขั้น <see cref="Stage"/> (ร่าง/รออนุมัติ/อนุมัติแล้ว/ชำระแล้ว/ยกเลิก) ·
/// <b>ยังไม่ตั้ง (ไม่มีคีย์) = พฤติกรรมเดิมทุกไบต์</b>: ตราประทับเฉพาะ อนุมัติแล้ว/ชำระแล้ว/ยกเลิก (ร่าง·รออนุมัติ·ถูกปฏิเสธ ไม่ประทับ) ·
/// ลายเซ็นอิเล็กทรอนิกส์ทุกขั้น · ขนาด/ตำแหน่งจาก <c>CompanySettings.StampWidthMm/HeightMm/StampAlign</c> · ผู้ลงนามได้ทุกคน ·
/// 50 ทวิ ค่าตั้งต้นขนาด <see cref="WhtDefaultSizeMm"/> มม. วางทับวงกลม "ประทับตรานิติบุคคล" ของแบบ (ตรา 32 มม. ล้นกรอบแบบ) ·
/// ทั้ง HTML + QuestPDF + หน้าพิมพ์ 50 ทวิ เรียก <see cref="Resolve(string, Stage, string?, Legacy)"/> ตัวเดียว ห้ามคิดเงื่อนไขเอง
/// (กฎ #4 A "Resolver กลาง" · <c>tools/required_call_site_check.py</c>)</summary>
public static class DocumentStampPolicy
{
    /// <summary>คีย์กฎของหนังสือรับรองการหักภาษี ณ ที่จ่าย (50 ทวิ) — ไม่ใช่ <see cref="DocumentType"/></summary>
    public const string WhtCertKey = "WithholdingTaxCert";

    /// <summary>ขนาดตั้งต้นของตราบน 50 ทวิ (มม.) — พอดีวงกลม 70px ของแบบ + ขอบ</summary>
    public const decimal WhtDefaultSizeMm = 24m;

    /// <summary>ระยะเลื่อนสูงสุด (มม.) ทั้งแกน X/Y — กันตราหลุดกระดาษ</summary>
    public const decimal MaxOffsetMm = 60m;

    public enum Stage { Draft, Pending, Approved, Paid, Voided }

    /// <summary>ขั้นที่ "ประทับตรา" เมื่อยังไม่ตั้ง — ร่าง/รออนุมัติไม่ประทับ (ผู้มีอำนาจอนุมัติ = ประทับตรา · พฤติกรรมเดิม)</summary>
    public static readonly IReadOnlyList<Stage> DefaultStampStages = new[] { Stage.Approved, Stage.Paid, Stage.Voided };

    /// <summary>ขั้นที่พิมพ์ลายเซ็นอิเล็กทรอนิกส์เมื่อยังไม่ตั้ง — ทุกขั้น (พฤติกรรมเดิม: ผู้จัดทำเซ็นตั้งแต่ร่าง ·
    /// ช่องผู้อนุมัติว่างเองจนกว่าจะอนุมัติจริง ซึ่งเป็น "ข้อเท็จจริง" ไม่ใช่ค่าตั้ง)</summary>
    public static readonly IReadOnlyList<Stage> DefaultSignatureStages =
        new[] { Stage.Draft, Stage.Pending, Stage.Approved, Stage.Paid, Stage.Voided };

    /// <summary>ป้ายภาษาไทยของแต่ละขั้น (หน้าตั้งค่าแสดงอย่างเดียว)</summary>
    public static string StageLabel(Stage s) => s switch
    {
        Stage.Draft => "ร่าง / ถูกปฏิเสธ",
        Stage.Pending => "รออนุมัติ",
        Stage.Approved => "อนุมัติแล้ว / ส่งแล้ว",
        Stage.Paid => "ชำระแล้ว",
        Stage.Voided => "ยกเลิก",
        _ => s.ToString(),
    };

    /// <summary>รายการประเภทเอกสารที่ตั้งกฎได้ (ลำดับบนหน้าตั้งค่า) — คีย์ + ป้ายไทย</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Kinds = new (string, string)[]
    {
        (nameof(DocumentType.Quotation), "ใบเสนอราคา"),
        (nameof(DocumentType.Invoice), "ใบแจ้งหนี้"),
        (nameof(DocumentType.BillingNote), "ใบวางบิล"),
        (nameof(DocumentType.TaxInvoice), "ใบกำกับภาษี"),
        (nameof(DocumentType.Receipt), "ใบเสร็จรับเงิน"),
        (nameof(DocumentType.DebitNote), "ใบเพิ่มหนี้"),
        (nameof(DocumentType.CreditNote), "ใบลดหนี้"),
        (nameof(DocumentType.DeliveryNote), "ใบส่งของ"),
        (nameof(DocumentType.ReceiptVoucher), "ใบสำคัญรับ"),
        (nameof(DocumentType.PurchaseRequisition), "ใบขอซื้อ"),
        (nameof(DocumentType.PurchaseOrder), "ใบสั่งซื้อ"),
        (nameof(DocumentType.GoodsReceiptNote), "ใบรับสินค้า"),
        (nameof(DocumentType.PurchaseInvoice), "ใบแจ้งหนี้ซื้อ"),
        (nameof(DocumentType.Expense), "ใบบันทึกค่าใช้จ่าย"),
        (nameof(DocumentType.PaymentVoucher), "ใบสำคัญจ่าย"),
        (nameof(DocumentType.CertificateInLieu), "ใบรับรองแทนใบเสร็จ"),
        (WhtCertKey, "หนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ)"),
    };

    private static readonly HashSet<string> KnownKeys = new(Kinds.Select(k => k.Key), StringComparer.Ordinal);
    private static readonly HashSet<string> KnownRoles = new(
        Enum.GetNames<UserRole>().Where(n => n != nameof(UserRole.SystemAdmin)), StringComparer.OrdinalIgnoreCase);

    public static string KeyOf(DocumentType t) => t.ToString();

    /// <summary>สถานะเอกสาร → ขั้นของกฎ · ถูกปฏิเสธ = กลับไปเป็นร่าง (ยังไม่มีผู้อนุมัติ) · ชำระบางส่วน/เกินกำหนด/ส่งแล้ว = อนุมัติแล้ว</summary>
    public static Stage StageOf(DocumentStatus s) => s switch
    {
        DocumentStatus.Draft or DocumentStatus.Rejected => Stage.Draft,
        DocumentStatus.WaitingApproval => Stage.Pending,
        DocumentStatus.Paid => Stage.Paid,
        DocumentStatus.Voided => Stage.Voided,
        _ => Stage.Approved,
    };

    /// <summary>สถานะ 50 ทวิ → ขั้น · ออกแล้ว/พิมพ์แล้ว = อนุมัติแล้ว (ใบ 50 ทวิไม่มีขั้นรออนุมัติ/ชำระ)</summary>
    public static Stage StageOf(WithholdingTaxCertStatus s) => s switch
    {
        WithholdingTaxCertStatus.Draft => Stage.Draft,
        WithholdingTaxCertStatus.Voided => Stage.Voided,
        _ => Stage.Approved,
    };

    /// <summary>ค่าตั้งเดิมของบริษัท (ก่อนมีกฎ) — <c>CompanySettings.StampWidthMm/StampHeightMm/StampAlign</c></summary>
    public sealed record Legacy(decimal WidthMm = 32m, decimal HeightMm = 32m, string? Align = "Right");

    /// <summary>กฎหนึ่งประเภทเอกสาร (รูปที่เก็บเป็น JSON ใน <c>CompanySettings.DocumentStampRulesJson</c>) · ช่องที่เป็น null = ใช้ค่าตั้งต้น</summary>
    public sealed class Rule
    {
        /// <summary>ขั้นที่ประทับตรา (ชื่อ <see cref="Stage"/>) · null = ค่าตั้งต้น <see cref="DefaultStampStages"/> · [] = ไม่ประทับเลย</summary>
        public List<string>? Stamp { get; set; }
        /// <summary>ขั้นที่พิมพ์ลายเซ็นอิเล็กทรอนิกส์ · null = ทุกขั้น · [] = ไม่พิมพ์เลย</summary>
        public List<string>? Signature { get; set; }
        /// <summary>Left / Center / Right · null = <c>CompanySettings.StampAlign</c> (50 ทวิ: ยึดวงกลมของแบบ ใช้เฉพาะ offset)</summary>
        public string? Align { get; set; }
        public decimal? OffsetXMm { get; set; }
        public decimal? OffsetYMm { get; set; }
        public decimal? WidthMm { get; set; }
        public decimal? HeightMm { get; set; }
        /// <summary>บทบาท (<see cref="UserRole"/>) ที่ลายเซ็นพิมพ์ได้ · ว่างทั้งคู่ = ทุกคน</summary>
        public List<string>? SignerRoles { get; set; }
        public List<Guid>? SignerUserIds { get; set; }
    }

    /// <summary>ผลการตัดสินต่อใบ — renderer ทุกตัวใช้ค่าชุดนี้ตรงตัว</summary>
    public sealed record Decision(
        string Key, Stage Stage, bool ShowStamp, bool ShowSignature, string Align,
        decimal OffsetXMm, decimal OffsetYMm, decimal WidthMm, decimal HeightMm,
        IReadOnlyList<string> SignerRoles, IReadOnlyList<Guid> SignerUserIds, bool IsCustom)
    {
        /// <summary>RuleCode สำหรับ log/tooltip — เช่น <c>STAMP-TaxInvoice-Draft-off</c></summary>
        public string RuleCode => $"STAMP-{Key}-{Stage}-{(ShowStamp ? "on" : "off")}{(IsCustom ? "" : "-default")}";

        /// <summary>ผู้ใช้คนนี้ (บทบาทในบริษัทนี้) พิมพ์ลายเซ็นบนเอกสารประเภทนี้ได้ไหม · ไม่จำกัด = ได้ทุกคน ·
        /// ลายเซ็นที่ไม่ใช่ผู้ใช้ในระบบ (ลูกค้าเซ็นรับ · คู่ค้าส่งมาทาง API · ผู้มีอำนาจลงนามที่บริษัทตั้งไว้) ไม่ผ่านด่านนี้</summary>
        public bool SignerAllowed(Guid? userId, string? role)
        {
            if (SignerRoles.Count == 0 && SignerUserIds.Count == 0) return true;
            if (userId.HasValue && SignerUserIds.Contains(userId.Value)) return true;
            return !string.IsNullOrWhiteSpace(role)
                && SignerRoles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>อ่าน JSON ที่เก็บไว้ — JSON เสีย/ว่าง ⇒ ไม่มีกฎ (= พฤติกรรมเดิม · ห้ามทำให้ PDF ล้ม) · คีย์ที่ไม่รู้จักถูกตัดทิ้ง</summary>
    public static Dictionary<string, Rule> Parse(string? json)
    {
        var result = new Dictionary<string, Rule>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, Rule>>(json, Json);
            if (raw == null) return result;
            foreach (var (k, v) in raw)
                if (v != null && KnownKeys.Contains(k)) result[k] = v;
        }
        catch (JsonException) { /* ค่าเสีย = ไม่มีกฎ (พฤติกรรมเดิม) — หน้าตั้งค่าจะเห็นว่าไม่มีกฎแล้วตั้งใหม่ได้ */ }
        return result;
    }

    /// <summary>ตรวจกฎก่อนบันทึก — คืนรายการข้อผิดพลาด (ภาษาไทย) · ว่าง = บันทึกได้</summary>
    public static List<string> Validate(IReadOnlyDictionary<string, Rule>? rules)
    {
        var errors = new List<string>();
        if (rules == null) return errors;
        foreach (var (key, r) in rules)
        {
            if (!KnownKeys.Contains(key)) { errors.Add($"ไม่รู้จักประเภทเอกสาร \"{key}\""); continue; }
            if (r == null) continue;
            foreach (var s in (r.Stamp ?? new()).Concat(r.Signature ?? new()))
                if (!TryStage(s, out _))
                    errors.Add($"{key}: ไม่รู้จักสถานะ \"{s}\"");
            if (r.Align != null && !IsAlign(r.Align))
                errors.Add($"{key}: ตำแหน่งต้องเป็น Left / Center / Right");
            if (r.OffsetXMm is { } x && Math.Abs(x) > MaxOffsetMm) errors.Add($"{key}: เลื่อนแนวนอนได้ไม่เกิน ±{MaxOffsetMm} มม.");
            if (r.OffsetYMm is { } y && Math.Abs(y) > MaxOffsetMm) errors.Add($"{key}: เลื่อนแนวตั้งได้ไม่เกิน ±{MaxOffsetMm} มม.");
            if (r.WidthMm is { } w && (w < 5m || w > 120m)) errors.Add($"{key}: ความกว้างตราต้องอยู่ระหว่าง 5–120 มม.");
            if (r.HeightMm is { } h && (h < 5m || h > 120m)) errors.Add($"{key}: ความสูงตราต้องอยู่ระหว่าง 5–120 มม.");
            foreach (var role in r.SignerRoles ?? new())
                if (!KnownRoles.Contains(role)) errors.Add($"{key}: ไม่รู้จักบทบาท \"{role}\"");
        }
        return errors;
    }

    /// <summary>รูป JSON มาตรฐานที่เก็บลงฐาน (ผ่าน <see cref="Validate"/> แล้ว) · ไม่มีกฎ ⇒ null (= พฤติกรรมเดิม)</summary>
    public static string? Serialize(IReadOnlyDictionary<string, Rule>? rules)
    {
        if (rules == null || rules.Count == 0) return null;
        var clean = new SortedDictionary<string, Rule>(StringComparer.Ordinal);
        foreach (var (k, r) in rules)
        {
            if (r == null || !KnownKeys.Contains(k)) continue;
            clean[k] = new Rule
            {
                Stamp = NormalizeStages(r.Stamp),
                Signature = NormalizeStages(r.Signature),
                Align = r.Align == null ? null : CanonAlign(r.Align),
                OffsetXMm = r.OffsetXMm, OffsetYMm = r.OffsetYMm,
                WidthMm = r.WidthMm, HeightMm = r.HeightMm,
                SignerRoles = r.SignerRoles?.Where(KnownRoles.Contains)
                    .Select(x => Enum.Parse<UserRole>(x, true).ToString()).Distinct().ToList(),
                SignerUserIds = r.SignerUserIds?.Where(g => g != Guid.Empty).Distinct().ToList(),
            };
        }
        return clean.Count == 0 ? null : JsonSerializer.Serialize(clean, Json);
    }

    /// <summary>ตัดสินต่อใบ — จาก JSON ที่เก็บไว้ (ทางที่ renderer เรียก)</summary>
    public static Decision Resolve(string key, Stage stage, string? rulesJson, Legacy legacy)
        => Resolve(key, stage, Parse(rulesJson), legacy);

    public static Decision Resolve(string key, Stage stage, IReadOnlyDictionary<string, Rule> rules, Legacy legacy)
    {
        rules.TryGetValue(key, out var r);
        var isWht = key == WhtCertKey;

        var stampStages = ParseStages(r?.Stamp) ?? DefaultStampStages;
        var sigStages = ParseStages(r?.Signature) ?? DefaultSignatureStages;

        // ขนาด: กฎ → (50 ทวิ: ค่าตั้งต้นของแบบ) → ค่าตั้งเดิมของบริษัท · กว้าง ≤ 0 = auto 32 (สัญญาเดิมของทั้งสอง renderer)
        decimal width, height;
        if (r?.WidthMm is { } rw) width = rw;
        else if (isWht) width = WhtDefaultSizeMm;
        else width = legacy.WidthMm <= 0 ? 32m : legacy.WidthMm;
        if (r?.HeightMm is { } rh) height = rh;
        else if (isWht) height = WhtDefaultSizeMm;
        else height = legacy.HeightMm <= 0 ? 32m : legacy.HeightMm;
        width = Math.Clamp(width, 5m, 120m);
        height = Math.Clamp(height, 5m, 120m);

        var align = CanonAlign(r?.Align ?? legacy.Align);
        var offX = Math.Clamp(r?.OffsetXMm ?? 0m, -MaxOffsetMm, MaxOffsetMm);
        var offY = Math.Clamp(r?.OffsetYMm ?? 0m, -MaxOffsetMm, MaxOffsetMm);

        return new Decision(key, stage,
            ShowStamp: stampStages.Contains(stage),
            ShowSignature: sigStages.Contains(stage),
            Align: align, OffsetXMm: offX, OffsetYMm: offY, WidthMm: width, HeightMm: height,
            SignerRoles: (IReadOnlyList<string>?)r?.SignerRoles?.ToList() ?? Array.Empty<string>(),
            SignerUserIds: (IReadOnlyList<Guid>?)r?.SignerUserIds?.ToList() ?? Array.Empty<Guid>(),
            IsCustom: r != null);
    }

    private static bool IsAlign(string a) =>
        a.Trim().Equals("Left", StringComparison.OrdinalIgnoreCase)
        || a.Trim().Equals("Center", StringComparison.OrdinalIgnoreCase)
        || a.Trim().Equals("Right", StringComparison.OrdinalIgnoreCase);

    private static string CanonAlign(string? a) => (a ?? "").Trim().ToLowerInvariant() switch
    {
        "left" => "Left",
        "center" => "Center",
        _ => "Right",
    };

    private static IReadOnlyList<Stage>? ParseStages(List<string>? names)
    {
        if (names == null) return null;
        var list = new List<Stage>();
        foreach (var n in names)
            if (TryStage(n, out var s) && !list.Contains(s)) list.Add(s);
        return list;
    }

    /// <summary>รับเฉพาะ "ชื่อ" ขั้น (ไม่รับตัวเลข "1" ที่ Enum.TryParse ยอมแปลงให้เงียบ ๆ)</summary>
    private static bool TryStage(string? name, out Stage stage)
    {
        stage = default;
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n) || !char.IsLetter(n[0])) return false;
        return Enum.TryParse(n, true, out stage) && Enum.IsDefined(stage);
    }

    private static List<string>? NormalizeStages(List<string>? names)
        => ParseStages(names)?.OrderBy(s => s).Select(s => s.ToString()).ToList();
}
