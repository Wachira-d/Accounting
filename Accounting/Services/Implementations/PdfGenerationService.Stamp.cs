using System.Globalization;
using System.Net;
using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ตราประทับ/ลายเซ็น — จุดต่อสายเดียวระหว่างตัวตัดสิน <see cref="DocumentStampPolicy"/> กับ renderer ทุกตัว (ทีม J 2026-10-22)
///
/// <para>เอกสารขาย/ซื้อ: HTML <c>BuildDocumentHtml</c> + QuestPDF <c>BuildBranding</c>/<c>RenderDocumentPdfNative</c> เรียก
/// <see cref="StampDecisionFor(Document, CompanySettings?)"/> + <see cref="ResolveStampBytes"/> ตัวเดียวกัน ·
/// 50 ทวิ: HTML <c>BuildWithholdingTaxCertHtml</c> + QuestPDF <c>BuildWhtCertPdf</c> + หน้าพิมพ์ <c>wht.html</c>
/// (ผ่าน <see cref="GetWithholdingTaxCertPrintDecorationAsync"/>) ใช้ <see cref="WhtCertStamp"/> ชุดเดียว</para></summary>
public partial class PdfGenerationService
{
    /// <summary>ตรา 50 ทวิ ที่ตัดสินแล้ว — รูป (null = ไม่ประทับ) + ขนาด/ระยะเลื่อน (มม.) จากวงกลมของแบบ</summary>
    internal sealed record WhtCertStamp(byte[] Image, decimal WidthMm, decimal HeightMm, decimal OffsetXMm, decimal OffsetYMm);

    private static DocumentStampPolicy.Legacy StampLegacyOf(CompanySettings? s)
        => new(s?.StampWidthMm ?? 32m, s?.StampHeightMm ?? 32m, s?.StampAlign);

    /// <summary>กฎตรา/ลายเซ็นของเอกสารใบนี้ (ประเภท × สถานะ × ค่าตั้งบริษัท)</summary>
    internal static DocumentStampPolicy.Decision StampDecisionFor(Document doc, CompanySettings? s)
        => DocumentStampPolicy.Resolve(DocumentStampPolicy.KeyOf(doc.DocumentType),
            DocumentStampPolicy.StageOf(doc.Status), s?.DocumentStampRulesJson, StampLegacyOf(s));

    /// <summary>กฎตรา/ลายเซ็นของ 50 ทวิ ใบนี้</summary>
    internal static DocumentStampPolicy.Decision StampDecisionFor(WithholdingTaxCert cert, CompanySettings? s)
        => DocumentStampPolicy.Resolve(DocumentStampPolicy.WhtCertKey,
            DocumentStampPolicy.StageOf(cert.Status), s?.DocumentStampRulesJson, StampLegacyOf(s));

    /// <summary>รูปตราของบริษัท — ไฟล์ <c>StampPath</c> → <c>StampUrl</c> (path เว็บ) → ตราในเทมเพลต (ของเดิม) ·
    /// ใช้ร่วม HTML + QuestPDF (เดิม HTML ไม่ fallback ตราในเทมเพลต · QuestPDF ไม่ลอง StampUrl ⇒ สอง renderer ประทับคนละรูปได้)</summary>
    internal static byte[]? ResolveStampBytes(CompanySettings? s, DocumentTemplate? template)
    {
        var b = TryReadImage(s?.StampPath) ?? TryReadImage(s?.StampUrl) ?? TryReadImage(template?.StampImagePath);
        return LooksLikeImage(b) ? b : null;
    }

    /// <summary>bytes ของรูป → data URI (ชนิดจาก magic bytes) · ไม่ใช่รูป ⇒ null</summary>
    internal static string? ImageDataUri(byte[]? b)
    {
        if (!LooksLikeImage(b)) return null;
        var mime = b![0] switch
        {
            0x89 => "image/png",
            0xFF => "image/jpeg",
            0x47 => "image/gif",
            _ => "image/webp",
        };
        return $"data:{mime};base64,{Convert.ToBase64String(b)}";
    }

    private static string StampMm(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>บล็อกตราเหนือช่องลงนามของเอกสารขาย/ซื้อ (HTML) — ตำแหน่ง/ขนาด/ระยะเลื่อนจากกฎ · คู่กับ QuestPDF
    /// <c>BuildSignatureArea</c> ที่อ่าน <see cref="PdfBranding"/> ชุดเดียวกัน</summary>
    internal static string DocumentStampBlockHtml(DocumentStampPolicy.Decision d, string src)
    {
        var align = d.Align switch { "Left" => "left", "Center" => "center", _ => "right" };
        var shift = d.OffsetXMm != 0 || d.OffsetYMm != 0
            ? $";position:relative;left:{StampMm(d.OffsetXMm)}mm;top:{StampMm(d.OffsetYMm)}mm" : "";
        return $"<div class='doc-stamp' style='text-align:{align};margin-top:8px'>"
            + $"<img src='{src}' alt='ตราประทับ' style='width:{StampMm(d.WidthMm)}mm;height:{StampMm(d.HeightMm)}mm;object-fit:contain;display:inline-block{shift}'/></div>";
    }

    /// <summary>ตรา 50 ทวิ ของใบนี้ — null เมื่อกฎไม่ประทับขั้นนี้ หรือบริษัทยังไม่อัปโหลดตรา</summary>
    internal static WhtCertStamp? ResolveWhtCertStamp(WithholdingTaxCert cert, CompanySettings? s)
    {
        var d = StampDecisionFor(cert, s);
        if (!d.ShowStamp) return null;
        var img = ResolveStampBytes(s, null);
        return img == null ? null : new WhtCertStamp(img, d.WidthMm, d.HeightMm, d.OffsetXMm, d.OffsetYMm);
    }

    /// <summary>ช่อง "ประทับตรานิติบุคคล (ถ้ามี)" ของ 50 ทวิ (HTML) — ไม่มีตรา = วงกลม + ข้อความเดิมทุกไบต์ ·
    /// มีตรา = วงกลมเปล่า + รูปตรากึ่งกลางวง (เลื่อนตามกฎ) · markup/คลาสเดียวกับหน้าพิมพ์ wht.html</summary>
    internal static string WhtStampAreaHtml(WhtCertStamp? stamp)
    {
        if (stamp == null)
            return $"<div class='stamp-area'>{string.Join("<br>", WhtCertFormText.StampLines)}</div>";
        var src = HtmlImageSource.Attribute(ImageDataUri(stamp.Image));
        if (src == null)
            return $"<div class='stamp-area'>{string.Join("<br>", WhtCertFormText.StampLines)}</div>";
        return "<div class='stamp-area has-stamp'>"
            + $"<img class='stamp-img' src='{src}' alt='ตราประทับ' style='width:{StampMm(stamp.WidthMm)}mm;height:{StampMm(stamp.HeightMm)}mm;"
            + $"transform:translate(calc(-50% + {StampMm(stamp.OffsetXMm)}mm), calc(-50% + {StampMm(stamp.OffsetYMm)}mm))'/></div>";
    }

    /// <summary>บทบาทของผู้ใช้ในบริษัทนี้ (ชื่อ <c>UserRole</c>) — ใช้ตัดสิน "ผู้ลงนามที่มีสิทธิ์"</summary>
    private async Task<string?> SignerRoleAsync(Guid companyId, Guid userId)
    {
        var role = await _db.Set<CompanyUser>().AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.UserId == userId)
            .Select(cu => (Models.Enums.UserRole?)cu.Role)
            .FirstOrDefaultAsync();
        return role?.ToString();
    }

    /// <summary>ลบรูปลายเซ็นฝั่งบริษัทที่กฎไม่ให้พิมพ์ (คงชื่อไว้ใต้เส้น) — ใช้ใน <c>ResolveSignersAsync</c> ·
    /// <paramref name="slotUsers"/>[i] = ผู้ใช้ในระบบเจ้าของลายเซ็นช่อง i (null = ไม่ใช่ผู้ใช้ เช่น ผู้มีอำนาจลงนามที่บริษัทตั้ง/คู่ค้าส่งมา ⇒
    /// ตรวจแค่ "ขั้นนี้พิมพ์ลายเซ็นไหม") · <paramref name="customerSlots"/>[i] = ลายเซ็นของลูกค้า (เซ็นรับของ/อนุมัติออนไลน์) ⇒ ไม่แตะเลย ·
    /// ช่องที่เกินความยาวอาร์เรย์ (ช่องลูกค้าลำดับ 2) ไม่ถูกแตะ</summary>
    private async Task ApplySignerPolicyAsync(Guid companyId, DocumentStampPolicy.Decision d,
        List<DocumentSigner> signers, Guid?[] slotUsers, bool[] customerSlots)
    {
        for (var i = 0; i < signers.Count && i < slotUsers.Length; i++)
        {
            var s = signers[i];
            if (i < customerSlots.Length && customerSlots[i]) continue;
            if (s.SignatureImageBytes == null && s.SignatureImageDataUri == null) continue;
            var allowed = d.ShowSignature
                && (slotUsers[i] is not { } uid || d.SignerAllowed(uid, await SignerRoleAsync(companyId, uid)));
            if (!allowed) signers[i] = s with { SignatureImageBytes = null, SignatureImageDataUri = null };
        }
    }

    /// <summary>ลายเซ็นผู้จ่ายเงินบน 50 ทวิ — สายเดิมของ <c>GenerateWithholdingTaxCertPdfAsync</c> (ผู้สร้างใบ → เจ้าของเมื่อไม่รู้ผู้สร้าง)
    /// + ด่านกฎ (ปิดลายเซ็นในขั้นนี้ · ผู้ลงนามไม่อยู่ในบทบาท/รายชื่อที่อนุญาต ⇒ ไม่พิมพ์รูป) · ใช้ร่วม PDF ทั้งสอง + หน้าพิมพ์</summary>
    private async Task<(byte[]? Bytes, string? Name)> ResolveWhtPayerSignatureAsync(
        Guid companyId, string? createdBy, DocumentStampPolicy.Decision d)
    {
        Guid? signerId = Guid.TryParse(createdBy, out var cb) ? cb : null;
        signerId ??= await _db.Set<CompanyUser>().AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.Role == Models.Enums.UserRole.Owner)
            .Select(cu => (Guid?)cu.UserId).FirstOrDefaultAsync();
        if (!signerId.HasValue) return (null, null);
        var u = await _db.Users.AsNoTracking()
            .Where(x => x.Id == signerId.Value)
            .Select(x => new { x.FullName, x.SignatureImageBase64, x.SignatureName })
            .FirstOrDefaultAsync();
        if (u == null) return (null, null);
        var name = !string.IsNullOrWhiteSpace(u.SignatureName) ? u.SignatureName : u.FullName;
        byte[]? bytes = null;
        if (!string.IsNullOrWhiteSpace(u.SignatureImageBase64)
            && d.ShowSignature && d.SignerAllowed(signerId, await SignerRoleAsync(companyId, signerId.Value)))
            bytes = TryDecodeBase64Image(u.SignatureImageBase64.Trim());
        return (bytes, name);
    }

    /// <summary>ตรา + ลายเซ็นสำหรับหน้าพิมพ์ 50 ทวิ (wht.html) — ตัวตัดสินเดียวกับ PDF ทั้งสอง</summary>
    public async Task<WhtCertPrintDecoration> GetWithholdingTaxCertPrintDecorationAsync(Guid companyId, Guid certId)
    {
        var cert = await _db.WithholdingTaxCerts.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหนังสือรับรองหัก ณ ที่จ่าย");
        var settings = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId);
        var d = StampDecisionFor(cert, settings);
        var stamp = ResolveWhtCertStamp(cert, settings);
        var (sig, _) = await ResolveWhtPayerSignatureAsync(companyId, cert.CreatedBy, d);
        return new WhtCertPrintDecoration(
            ShowStamp: stamp != null,
            StampImage: stamp == null ? null : ImageDataUri(stamp.Image),
            StampWidthMm: d.WidthMm, StampHeightMm: d.HeightMm,
            StampOffsetXMm: d.OffsetXMm, StampOffsetYMm: d.OffsetYMm,
            SignatureImage: ImageDataUri(sig),
            RuleCode: d.RuleCode);
    }
}
