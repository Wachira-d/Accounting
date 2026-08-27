using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตัวตน "ผู้ออกเอกสาร" ที่จะพิมพ์บนหัวกระดาษ — ผลลัพธ์ของ
/// <see cref="DocumentIssuerIdentity.Resolve"/>
/// </summary>
/// <param name="PrimaryName">ชื่อตัวใหญ่บนหัวเอกสาร (แบรนด์ หรือ ชื่อนิติบุคคล)</param>
/// <param name="SecondaryName">บรรทัดรองใต้ชื่อหลัก (ชื่ออังกฤษ / ชื่อแบรนด์เมื่อ
/// นิติบุคคลเป็นตัวหลัก) — null = ไม่พิมพ์</param>
/// <param name="TagLine">สโลแกนของแบรนด์ (ถ้ามีและแบรนด์เป็นตัวหลัก)</param>
/// <param name="LogoPath">ไฟล์โลโก้ที่ควรใช้ (แบรนด์ก่อน แล้วค่อยของบริษัท)</param>
/// <param name="LogoUrl">URL โลโก้สำรองเมื่ออ่านไฟล์ไม่ได้</param>
/// <param name="Address">ที่อยู่ที่ควรพิมพ์ (ของแบรนด์ถ้ามี ไม่งั้นของบริษัท)</param>
/// <param name="Phone">เบอร์ที่ควรพิมพ์</param>
/// <param name="Email">อีเมลที่ควรพิมพ์</param>
/// <param name="Website">เว็บไซต์ของแบรนด์ (null = ไม่พิมพ์)</param>
/// <param name="PrimaryColor">สีหัวเอกสาร/หัวตาราง (แบรนด์ก่อน แล้วบริษัท)</param>
/// <param name="LegalLine">บรรทัด "ชื่อนิติบุคคลตัวเล็ก" — ไม่เคยเป็น null เมื่อ
/// <paramref name="BrandIsPrimary"/> เป็น true</param>
/// <param name="LegalLineInHeader">พิมพ์ LegalLine ใต้ชื่อหลักตรงหัว</param>
/// <param name="LegalLineInFooter">พิมพ์ LegalLine ท้ายกระดาษ</param>
/// <param name="BrandIsPrimary">แบรนด์ได้ขึ้นเป็นชื่อหลักจริงหรือไม่</param>
public sealed record IssuerIdentity(
    string PrimaryName,
    string? SecondaryName,
    string? TagLine,
    string? LogoPath,
    string? LogoUrl,
    string? Address,
    string? Phone,
    string? Email,
    string? Website,
    string? PrimaryColor,
    string? LegalLine,
    bool LegalLineInHeader,
    bool LegalLineInFooter,
    bool BrandIsPrimary,
    /// <summary>true = SecondaryName คือ "ชื่อแบรนด์" (บนเอกสารภาษีที่นิติบุคคล
    /// เป็นตัวหลัก) — renderer ต้องพิมพ์**เสมอ** ห้าม gate ด้วย
    /// template.ShowCompanyNameEn (flag นั้น default=false ⇒ สายเอกสาร
    /// "ใบแจ้งหนี้ชื่อร้าน → ใบกำกับชื่อบริษัท" จะไม่มีอะไรเชื่อมกันเลย —
    /// ผลตรวจทีมนักบัญชี ก-3)</summary>
    bool SecondaryIsBrand = false,
    /// <summary>ข้อความท้ายเอกสารของแบรนด์ — null = ใช้ของเทมเพลต/บริษัทตามเดิม
    /// (เดิมช่องนี้ในหน้าตั้งค่ากรอกได้แต่ไม่มี renderer อ่าน = field ตาย)</summary>
    string? FooterNotes = null,
    /// <summary>ถ้อยคำสาขาที่ต้องพิมพ์ต่อท้ายเลขผู้เสียภาษี — "สำนักงานใหญ่" /
    /// "สาขาที่ 3 (เชียงใหม่)" · null = ไม่พิมพ์ (กิจการที่ไม่มีเลขผู้เสียภาษี).
    /// **renderer ต้องอ่านจากตรงนี้ ห้ามคำนวณจาก `company.BranchCode` เอง** —
    /// ใบที่ออกจากสาขาย่อยจะพิมพ์รหัสของสำนักงานใหญ่ผิดทันที (§86/4)</summary>
    string? BranchLabel = null);

/// <summary>
/// **ตัวตัดสินเดียว** ว่าหัวเอกสารจะขึ้นชื่ออะไร โลโก้ไหน และต้องแฝงชื่อ
/// นิติบุคคลไว้ตรงไหน — ใช้ร่วมทั้ง HTML renderer (<c>PdfGenerationService</c>)
/// และ QuestPDF (<c>PdfGenerationService.DocumentRenderer</c>)
///
/// ทำไมต้องรวมไว้ที่เดียว: กฎเหล็ก #4 A ของโปรเจกต์ — "สอง renderer ห้าม drift"
/// เดิมทั้งสองไฟล์คำนวณ <c>coPrimaryName</c> เองคนละบรรทัด (ลอจิกเดียวกันเขียน
/// ซ้ำ) ซึ่งเป็นต้นเหตุคลาสสิกของเอกสารพิมพ์ออกมาไม่ตรงกันระหว่างพรีวิวกับ PDF
///
/// ═══ ด่านกฎหมาย ═══
/// ชื่อทางการค้าขึ้นเป็น "ตัวหลัก" ได้เฉพาะเอกสารที่**ไม่ใช่หลักฐานทางภาษี**
/// เพราะ ป.รัษฎากร §86/4(2) บังคับให้ใบกำกับภาษีแสดง "ชื่อ...ของผู้ประกอบการ
/// จดทะเบียน" (เช่นเดียวกับอย่างย่อ §86/6, ใบเพิ่มหนี้ §86/9, ใบลดหนี้ §86/10)
/// และใบรับเงินตาม ม.105 ต้องระบุผู้รับเงินที่แท้จริง
///
/// ถึงแบรนด์เป็นตัวหลัก ก็ยังพิมพ์บรรทัดนิติบุคคล (ชื่อ + เลขผู้เสียภาษี 13 หลัก
/// + สาขา) เสมอ — **ไม่มีทางปิด** เพราะเอกสารที่ไม่บอกว่าใครเป็นคู่สัญญาจริง
/// ใช้เป็นหลักฐานประกอบการลงบัญชีตาม พ.ร.บ.การบัญชี ม.7 ไม่ได้
/// </summary>
public static class DocumentIssuerIdentity
{
    /// <summary>ชนิดเอกสารที่ยอมให้ชื่อทางการค้าขึ้นเป็นตัวหลัก
    /// (ที่เหลือ = หลักฐานทางภาษี/การรับ-จ่ายเงิน → บังคับชื่อนิติบุคคล)</summary>
    private static readonly HashSet<DocumentType> BrandPrimaryAllowed = new()
    {
        DocumentType.Quotation,           // ใบเสนอราคา
        DocumentType.Invoice,             // ใบแจ้งหนี้ (ยังไม่ใช่ใบกำกับ)
        DocumentType.BillingNote,         // ใบวางบิล
        DocumentType.DeliveryNote,        // ใบส่งของ
        DocumentType.PurchaseRequisition, // ใบขอซื้อ
        DocumentType.PurchaseOrder,       // ใบสั่งซื้อ
    };

    /// <summary>คำที่บ่งว่ากระดาษใบนี้ทำหน้าที่เป็นใบกำกับภาษี — เจอเมื่อไร
    /// บังคับชื่อนิติบุคคลทันที ไม่ว่าชนิดเอกสารจะอยู่ในลิสต์ข้างบนหรือไม่
    /// (หัวเอกสารรวมหลายหน้าที่ เช่น "ใบแจ้งหนี้/ใบกำกับภาษี" ออกด้วยชนิด
    /// Invoice/TaxInvoice ได้ทั้งคู่ — เช็คที่หัวจริงจึงแม่นกว่าเช็คที่ enum)</summary>
    private static readonly string[] TaxInvoiceMarkers =
        { "ใบกำกับภาษี", "tax invoice", "ใบเพิ่มหนี้", "ใบลดหนี้", "debit note", "credit note" };

    /// <summary>true = เอกสารชนิดนี้ (ที่มีหัวแบบนี้) ให้ชื่อทางการค้าขึ้นเป็น
    /// ตัวหลักได้. ส่ง <paramref name="renderedTitle"/> เป็น null ได้เมื่อยังไม่รู้หัว
    /// (เช่นตอนหน้าเว็บถามว่า "ชนิดนี้เลือกแบรนด์ได้ไหม") — จะตัดสินจากชนิดล้วน</summary>
    public static bool CanBrandBePrimary(DocumentType type, string? renderedTitle = null)
    {
        if (!BrandPrimaryAllowed.Contains(type)) return false;
        if (string.IsNullOrWhiteSpace(renderedTitle)) return true;
        var t = NormalizeTitle(renderedTitle);
        return !TaxInvoiceMarkers.Any(m => t.Contains(NormalizeTitle(m), StringComparison.Ordinal));
    }

    /// <summary>normalize หัวเอกสารก่อนเทียบ marker — CustomTitle เป็นค่าที่ผู้ใช้
    /// พิมพ์เอง "ใบกำกับ ภาษี" (เว้นวรรค) / "TAX-INVOICE" / สระอำแบบแยก
    /// (นิคหิต ํ + สระอา) ตาเห็นเป็นคำเดียวกันแต่ Contains ตรง ๆ มองไม่เห็น
    /// ⇒ แบรนด์ขึ้นหัวบนกระดาษที่อ่านว่า "ใบกำกับภาษี" ได้ (ผลตรวจ ข-1)</summary>
    private static string NormalizeTitle(string s)
    {
        var t = s.Normalize(System.Text.NormalizationForm.FormC)
            .Replace("\u0E4D\u0E32", "\u0E33")   // นิคหิต+สระอา → สระอำ (NFC ไม่รวมให้)
            .ToLowerInvariant();
        return new string(t.Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '_' or '.' )).ToArray());
    }

    private static string? Pick(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>ประกอบบรรทัดนิติบุคคลตัวเล็ก — "ดำเนินการโดย {ชื่อ} เลขประจำตัว
    /// ผู้เสียภาษี {13 หลัก} ({สาขา})" · ภาษาอังกฤษใช้ "Operated by"</summary>
    public static string BuildLegalLine(
        string companyName, string? companyTaxId, string? branchLabel, bool isEnglish,
        string? registeredAddress = null)
    {
        var parts = new List<string> { isEnglish ? $"Operated by {companyName}" : $"ดำเนินการโดย {companyName}" };
        if (!string.IsNullOrWhiteSpace(companyTaxId))
            parts.Add(isEnglish ? $"Tax ID {companyTaxId}" : $"เลขประจำตัวผู้เสียภาษี {companyTaxId}");
        if (!string.IsNullOrWhiteSpace(branchLabel))
            parts.Add(branchLabel!);
        // ที่อยู่จดทะเบียน — บนใบที่แบรนด์ขึ้นหัว ที่อยู่ที่พิมพ์ตรงหัวคือของหน้าร้าน
        // ถ้าต้องส่งคำบอกกล่าว/ทวงหนี้ ลูกค้าต้องหาภูมิลำเนานิติบุคคล (ป.พ.พ. ม.68-69)
        // ได้จากใบ (ผลตรวจทีมนักบัญชี ข-4)
        if (!string.IsNullOrWhiteSpace(registeredAddress))
            parts.Add(registeredAddress!);
        return string.Join(" · ", parts);
    }

    /// <summary>ตัวตนที่จะพิมพ์บนหัวเอกสารใบนี้</summary>
    /// <param name="brand">แบรนด์ที่ผู้ใช้เลือก — null/ปิดใช้งาน = ใช้ชื่อบริษัทตามเดิม</param>
    /// <param name="renderedTitle">หัวเอกสารที่คำนวณแล้ว (จาก ComputeDocumentTitle)</param>
    public static IssuerIdentity Resolve(
        DocumentType type,
        string? renderedTitle,
        bool isEnglish,
        string companyName,
        string? companyNameEn,
        string? companyTaxId,
        string? branchLabel,
        string? companyAddress,
        string? companyPhone,
        string? companyEmail,
        string? companyLogoPath,
        string? companyLogoUrl,
        string? companyPrimaryColor,
        DocumentBrandView? brand)
    {
        // ชื่อนิติบุคคลตามภาษาเอกสาร (ลอจิกเดิมที่เคยซ้ำอยู่ใน renderer ทั้งสองตัว)
        var legalPrimary = isEnglish && !string.IsNullOrWhiteSpace(companyNameEn)
            ? companyNameEn!
            : companyName;
        var legalSecondary = !string.IsNullOrWhiteSpace(companyNameEn) && legalPrimary != companyNameEn
            ? companyNameEn
            : null;

        // ⚠️ ตั้งใจ**ไม่**เช็ค IsActive ที่นี่ — ใบเก่าที่ตรึง BrandId ไว้ต้องพิมพ์
        // หน้าตาเดิมเสมอแม้แบรนด์ถูกปิดใช้งานภายหลัง (ปุ่มลบสัญญากับผู้ใช้ไว้ว่า
        // "เอกสารเก่ายังพิมพ์ได้เหมือนเดิม") IsActive คุมแค่รายการให้เลือกตอน
        // ออกใบใหม่ (ผลตรวจทีมเส้นทางข้อมูล ข้อ 3)
        var useBrand = brand != null && !string.IsNullOrWhiteSpace(brand.Name);
        if (!useBrand)
        {
            return new IssuerIdentity(
                PrimaryName: legalPrimary,
                SecondaryName: legalSecondary,
                TagLine: null,
                LogoPath: companyLogoPath,
                LogoUrl: companyLogoUrl,
                Address: companyAddress,
                Phone: companyPhone,
                Email: companyEmail,
                Website: null,
                PrimaryColor: companyPrimaryColor,
                LegalLine: null,
                LegalLineInHeader: false,
                LegalLineInFooter: false,
                BrandIsPrimary: false,
                BranchLabel: branchLabel);
        }

        var b = brand!;
        var brandPrimary = isEnglish && !string.IsNullOrWhiteSpace(b.NameEn) ? b.NameEn! : b.Name;
        var brandCanLead = CanBrandBePrimary(type, renderedTitle);

        // โลโก้/สี/เบอร์ติดต่อของแบรนด์ใช้ได้ทุกเอกสาร — สิ่งที่กฎหมายคุมคือ
        // "ชื่อ" กับ "ที่อยู่" ไม่ใช่ภาพ (โลโก้บนใบกำกับภาษีเป็นเรื่องปกติมานาน)
        var logoPath = Pick(b.LogoPath, companyLogoPath);
        var logoUrl  = Pick(b.LogoUrl, companyLogoUrl);
        var address  = Pick(isEnglish ? b.AddressEn : b.Address, b.Address, companyAddress);
        var phone    = Pick(b.Phone, companyPhone);
        var email    = Pick(b.Email, companyEmail);
        var color    = Pick(b.PrimaryColor, companyPrimaryColor);

        if (!brandCanLead)
        {
            // เอกสารภาษี — ชื่อนิติบุคคลเป็นตัวหลักตาม §86/4(2) และ **ที่อยู่ต้อง
            // เป็นที่ตั้งสถานประกอบการตามที่จดทะเบียน** (ป.86/2542) — ที่อยู่หน้าร้าน
            // ของแบรนด์ทับไม่ได้ ไม่งั้นใบกำกับรายการไม่ถูกต้อง ผู้ซื้อเสี่ยงภาษีซื้อ
            // ต้องห้าม §82/5(1) (ผลตรวจทีมนักบัญชี ก-2). แบรนด์ยังได้โลโก้ + สี +
            // ชื่อร้านเป็นบรรทัดรอง ⇒ ใบยังดู "เป็นแบรนด์" อยู่
            return new IssuerIdentity(
                PrimaryName: legalPrimary,
                SecondaryName: Pick(brandPrimary, legalSecondary),
                TagLine: null,
                LogoPath: logoPath,
                LogoUrl: logoUrl,
                Address: companyAddress,
                Phone: phone,
                Email: email,
                Website: b.Website,
                PrimaryColor: color,
                LegalLine: null,
                LegalLineInHeader: false,
                LegalLineInFooter: false,
                BrandIsPrimary: false,
                SecondaryIsBrand: true,
                FooterNotes: Pick(isEnglish ? b.FooterNotesEn : b.FooterNotes, b.FooterNotes),
                BranchLabel: branchLabel);
        }

        // แบรนด์เป็นตัวหลัก — บรรทัดนิติบุคคลตัวเล็กต้องมีเสมอ (ปิดไม่ได้)
        var placement = (b.LegalNamePlacement ?? "Footer").Trim();
        var inHeader = placement.Equals("Header", StringComparison.OrdinalIgnoreCase)
                    || placement.Equals("Both", StringComparison.OrdinalIgnoreCase);
        var inFooter = !inHeader
                    || placement.Equals("Both", StringComparison.OrdinalIgnoreCase);
        return new IssuerIdentity(
            PrimaryName: brandPrimary,
            SecondaryName: isEnglish || string.IsNullOrWhiteSpace(b.NameEn) || brandPrimary == b.NameEn
                ? null : b.NameEn,
            TagLine: Pick(isEnglish ? b.TagLineEn : b.TagLine, b.TagLine),
            LogoPath: logoPath,
            LogoUrl: logoUrl,
            Address: address,
            Phone: phone,
            Email: email,
            Website: b.Website,
            PrimaryColor: color,
            LegalLine: BuildLegalLine(companyName, companyTaxId, branchLabel, isEnglish, companyAddress),
            LegalLineInHeader: inHeader,
            LegalLineInFooter: inFooter,
            BrandIsPrimary: true,
            SecondaryIsBrand: !isEnglish && !string.IsNullOrWhiteSpace(b.NameEn) && brandPrimary != b.NameEn,
            FooterNotes: Pick(isEnglish ? b.FooterNotesEn : b.FooterNotes, b.FooterNotes),
            BranchLabel: branchLabel);
    }
}

/// <summary>ข้อมูลแบรนด์เท่าที่ resolver ต้องใช้ — แยกจาก entity เพื่อให้
/// เทสต์เรียกได้โดยไม่ต้องแตะ EF และให้ layer อื่น (พรีวิวหน้าเว็บ) ส่งค่ามาเองได้</summary>
public sealed record DocumentBrandView(
    string Name,
    string? NameEn = null,
    string? TagLine = null,
    string? TagLineEn = null,
    string? LogoPath = null,
    string? LogoUrl = null,
    string? Address = null,
    string? AddressEn = null,
    string? Phone = null,
    string? Email = null,
    string? Website = null,
    string? PrimaryColor = null,
    string? LegalNamePlacement = "Footer",
    bool IsActive = true,
    string? FooterNotes = null,
    string? FooterNotesEn = null);
