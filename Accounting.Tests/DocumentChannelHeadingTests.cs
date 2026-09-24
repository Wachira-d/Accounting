using System;
using System.Collections.Generic;
using DocumentHeading = Accounting.Models.DTOs.DocumentTemplate.DocumentHeading;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ภาษาและชื่อเอกสารในอีเมล/LINE/อีเมลตั้งเวลา ต้องตรงกับ PDF ที่แนบ** (ผลตรวจ S-12 · รอบ 193)
///
/// <para>═══ บั๊กที่ล็อกไว้ ═══ <c>DocumentEmailService</c> · <c>DocumentLineDeliveryService</c> · <c>EmailScheduleService</c>
/// หาภาษาเองด้วย <c>doc.DocumentLanguage ?? settings.DocumentLanguage</c> (ข้ามภาษาของเทมเพลต) และมีตารางชื่อชนิด
/// เอกสาร 6 ชนิดของตัวเอง (ไม่รู้จัก <c>DocumentTitleOverridesJson</c> · <c>CustomTitle</c> · หัวรวม) ⇒ อีเมล "ใบแจ้งหนี้"
/// ครอบ PDF หัว "ใบแจ้งหนี้/ใบวางบิล" หรือ PDF ภาษาอังกฤษ</para>
///
/// <para>heading ในเทสต์มาจาก <c>PdfGenerationService.ComputeDocumentHeading</c> = ส่วน pure ตัวเดียวกับที่
/// <c>ResolveDocumentHeadingAsync</c> ใช้ (ส่วนที่แตะ DB — เลือกเทมเพลต/ServedAsReceipt — เป็นขั้นเดียวกับ
/// <c>GenerateDocumentPdfAsync</c> ซึ่งเทสต์ของมันเองล็อกไว้)</para>
///
/// <para>ครึ่งแรก = เคสที่เคยพัง (เทมเพลตกำหนดภาษา · หัวที่เจ้าของตั้งเอง) · ครึ่งหลัง = ใบไทยธรรมดาได้หัวเดิมทุกตัวอักษร</para>
/// </summary>
public class DocumentChannelHeadingTests
{
    private static DocumentTemplate Tpl(DocumentType t, string? language = null, string? customTitle = null) => new()
    {
        Id = Guid.NewGuid(), DocumentType = t, IsDefault = true, IsActive = true,
        Language = language, CustomTitle = customTitle,
    };

    private static Document Doc(DocumentType t, string? docLang = null, string? contactName = "บริษัท ลูกค้า จำกัด") => new()
    {
        DocumentType = t, DocumentNumber = "INV-2026-0001", DocumentLanguage = docLang,
        DocumentDate = new DateTime(2026, 9, 24), TotalAmount = 1070m,
        Contact = contactName == null ? null : new Contact { Name = contactName },
    };

    private static DocumentHeading Heading(Document d, DocumentTemplate t, CompanySettings? s)
        => PdfGenerationService.ComputeDocumentHeading(d, t, s, companyMayIssueAbbreviated: true);

    // ════════ ครึ่งแรก: เคสที่เคยพัง ════════

    [Fact]
    public void เทมเพลตกำหนดภาษาอังกฤษ_อีเมลต้องเป็นอังกฤษตามPDF_ไม่ใช่ไทยตามค่าบริษัท()
    {
        var doc = Doc(DocumentType.Quotation);
        var settings = new CompanySettings { DocumentLanguage = "th" };
        var h = Heading(doc, Tpl(DocumentType.Quotation, language: "en"), settings);

        Assert.Equal("en", h.Language);   // สูตรเดิม doc ?? settings = "th" (ผิด)
        Assert.Equal(PdfGenerationService.ResolveDocumentLanguage(null, doc, Tpl(DocumentType.Quotation, "en"), settings), h.Language);

        var mail = DocumentEmailService.ComposeDefaultTemplate(doc, h, isEtaxByEmail: false);
        Assert.StartsWith(h.Title + " No. INV-2026-0001", mail.Subject);
        Assert.Contains("Dear ", mail.HtmlBody);
        Assert.DoesNotContain("เรียน", mail.HtmlBody);
    }

    [Fact]
    public void หัวที่เจ้าของตั้งเองในDocumentTitleOverridesJson_อยู่ในหัวอีเมล()
    {
        var doc = Doc(DocumentType.Invoice);
        var settings = new CompanySettings { DocumentTitleOverridesJson = """{"Invoice":"ใบแจ้งหนี้/ใบวางบิล"}""" };
        var h = Heading(doc, Tpl(DocumentType.Invoice), settings);

        Assert.Equal("ใบแจ้งหนี้/ใบวางบิล", h.Title);     // = หัวบน PDF (ComputeDocumentTitle)
        var mail = DocumentEmailService.ComposeDefaultTemplate(doc, h, isEtaxByEmail: false);
        Assert.Equal("ใบแจ้งหนี้/ใบวางบิล เลขที่ INV-2026-0001", mail.Subject);
        Assert.Contains("ใบแจ้งหนี้/ใบวางบิล", mail.HtmlBody);
    }

    [Fact]
    public void CustomTitleของเทมเพลต_อยู่ในหัวอีเมลeTax()
    {
        var doc = Doc(DocumentType.Invoice);
        var h = Heading(doc, Tpl(DocumentType.Invoice, customTitle: "INVOICE / ใบวางบิล"), null);
        var mail = DocumentEmailService.ComposeDefaultTemplate(doc, h, isEtaxByEmail: true);
        Assert.Equal("[e-Tax] INVOICE / ใบวางบิล เลขที่ INV-2026-0001", mail.Subject);
    }

    [Fact]
    public void อีเมลตั้งเวลา_หัวเรื่องdefaultใช้หัวเอกสารตามPDF_ไม่ใช่คำว่าเอกสาร()
    {
        var doc = Doc(DocumentType.Invoice);
        var settings = new CompanySettings { DocumentTitleOverridesJson = """{"Invoice":"ใบแจ้งหนี้/ใบวางบิล"}""" };
        var h = Heading(doc, Tpl(DocumentType.Invoice), settings);
        var ctx = new Dictionary<string, string?> { ["DocNumber"] = doc.DocumentNumber, ["DocTitle"] = h.Title };
        var subject = EmailScheduleService.Render(EmailScheduleService.DefaultDocSubject("DocumentOverdue", h.IsEnglish), ctx);
        Assert.Equal("เกินกำหนดชำระ: ใบแจ้งหนี้/ใบวางบิล เลขที่ INV-2026-0001", subject);
    }

    [Fact]
    public void ชื่อลูกค้าที่มีแท็ก_ถูกหนีในเนื้ออีเมล_ทั้งสองบริการ()
    {
        var doc = Doc(DocumentType.Invoice, contactName: "<script>alert(1)</script>");
        var h = Heading(doc, Tpl(DocumentType.Invoice), null);
        var mail = DocumentEmailService.ComposeDefaultTemplate(doc, h, isEtaxByEmail: false);
        Assert.DoesNotContain("<script>", mail.HtmlBody);
        Assert.Contains("&lt;script&gt;", mail.HtmlBody);

        var ctx = new Dictionary<string, string?> { ["ContactName"] = "<b>x</b>" };
        Assert.Equal("เรียน &lt;b&gt;x&lt;/b&gt;", EmailScheduleService.Render("เรียน {ContactName}", ctx, htmlEncodeValues: true));
        Assert.Equal("เรียน <b>x</b>", EmailScheduleService.Render("เรียน {ContactName}", ctx));   // หัวเรื่อง = ข้อความล้วน
    }

    // ════════ ครึ่งหลัง (ทิศตรงข้าม): ใบไทยธรรมดาไม่ถูกแตะ ════════

    [Fact]
    public void ใบแจ้งหนี้ไทยธรรมดา_หัวอีเมลเหมือนเดิมทุกตัวอักษร()
    {
        var doc = Doc(DocumentType.Invoice);
        var h = Heading(doc, Tpl(DocumentType.Invoice), new CompanySettings());
        Assert.Equal("th", h.Language);
        var mail = DocumentEmailService.ComposeDefaultTemplate(doc, h, isEtaxByEmail: false);
        Assert.Equal("ใบแจ้งหนี้ เลขที่ INV-2026-0001", mail.Subject);   // ค่าเดิมก่อนแก้
        Assert.Contains("เรียน คุณบริษัท ลูกค้า จำกัด", mail.HtmlBody);
    }

    [Fact]
    public void ภาษาที่ตรึงกับใบ_ยังชนะเทมเพลต_ลำดับชั้นเดิมไม่เปลี่ยน()
    {
        var doc = Doc(DocumentType.Invoice, docLang: "th");
        var h = Heading(doc, Tpl(DocumentType.Invoice, language: "en"), new CompanySettings { DocumentLanguage = "en" });
        Assert.Equal("th", h.Language);
        Assert.False(h.IsEnglish);
    }
}
