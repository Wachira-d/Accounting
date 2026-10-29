using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 209 แพ็ก P6 — จุดเชื่อม CMS ↔ ERP (ตัวหาผู้ติดต่อเดียว · สาขา/คลังของเว็บ) · PDPA (ยินยอม · อีเมลกู้ตะกร้า · DSR ลบข้อมูลเว็บ) ·
/// ตัวเลือกย่อยสินค้า (variant) — ตัวตัดสิน pure ทุกตัว · ทุกกลุ่มล็อกทั้ง "เคสที่เปลี่ยน" และ "เคสเดิมไม่ถูกแตะ" (กฎเหล็ก #4 H)
/// </summary>
public class CmsRound209P6Tests
{
    private const string ValidJuristicTaxId = "0105550123451";   // ผ่าน mod-11
    private static readonly Guid Co = Guid.NewGuid();

    private static Contact C(string name, string? email = null, string? phone = null, string? taxId = null, string? branch = null,
        bool deleted = false, bool active = true, bool walkIn = false, int ageDays = 10)
        => new()
        {
            Id = Guid.NewGuid(), CompanyId = Co, Name = name, Email = email, Phone = phone, TaxId = taxId, BranchCode = branch,
            IsDeleted = deleted, IsActive = active, IsWalkInCustomer = walkIn, CreatedAt = DateTime.UtcNow.AddDays(-ageDays),
        };

    private static CmsContactInput In(string? email = null, string? phone = null, string? taxId = null, string? branch = null, Guid? linked = null)
        => new(linked, taxId, branch, email, phone, "ลูกค้า", null, null, "test");

    // ═════════ D4: ตัวหาผู้ติดต่อเดียว (เลขภาษี → อีเมล → เบอร์ · ไม่ fuzzy บนตัวเลข · กรองแถวลบ/ปิด) ═════════

    [Fact]
    public void Contact_tax_key_beats_email()
    {
        var byEmail = C("A", email: "a@x.com");
        var byTax = C("B", taxId: ValidJuristicTaxId, branch: "00000");
        var d = CmsContactResolver.Decide(new[] { byEmail, byTax }, In(email: "a@x.com", taxId: ValidJuristicTaxId));
        Assert.Equal(CmsContactMatchKind.TaxKey, d.Kind);
        Assert.Same(byTax, d.Match);
    }

    [Fact]
    public void Contact_email_matches_case_and_space_insensitively()
    {
        var row = C("A", email: "Somchai@Shop.co.th");
        var d = CmsContactResolver.Decide(new[] { row }, In(email: "  somchai@shop.co.th "));
        Assert.Equal(CmsContactMatchKind.Email, d.Kind);
        Assert.Same(row, d.Match);
    }

    [Fact]
    public void Contact_phone_normalized_matches_exact_number()
    {
        var row = C("A", phone: "081-234-5678");
        var d = CmsContactResolver.Decide(new[] { row }, In(phone: "+66 81 234 5678"));
        Assert.Equal(CmsContactMatchKind.Phone, d.Kind);
        Assert.Same(row, d.Match);
    }

    [Theory]   // ตัวเลขล้วนไม่มี "การสะกดผิด" — ต่างหลักเดียว = คนละคน · เบอร์สั้นไม่ใช้จับคู่
    [InlineData("0812345679")]
    [InlineData("812345678")]
    [InlineData("5678")]
    public void Contact_phone_never_fuzzy(string phone)
    {
        var row = C("A", phone: "0812345678");
        Assert.Null(CmsContactResolver.Decide(new[] { row }, In(phone: phone)).Match);
    }

    [Fact]
    public void Contact_deleted_or_inactive_rows_ignored()
    {
        // เดิม ResolveErpContactForOrderAsync / booking ไม่กรอง IsDeleted ⇒ ใบใหม่ผูกผู้ติดต่อที่ลบแล้ว
        var d = CmsContactResolver.Decide(new[] { C("A", email: "a@x.com", deleted: true), C("B", email: "a@x.com", active: false) },
            In(email: "a@x.com"));
        Assert.Null(d.Match);
    }

    [Fact]
    public void Contact_linked_contact_wins()
    {
        var linked = C("L", email: "other@x.com");
        var d = CmsContactResolver.Decide(new[] { C("A", email: "a@x.com"), linked }, In(email: "a@x.com", linked: linked.Id));
        Assert.Equal(CmsContactMatchKind.Linked, d.Kind);
        Assert.Same(linked, d.Match);
    }

    [Fact]
    public void Contact_tax_id_owned_by_other_branch_does_not_fall_back_to_email()
    {
        // เลขนี้มีเจ้าของแล้ว (สาขา 00008) แต่ผู้ซื้อขอสำนักงานใหญ่ ⇒ ห้ามถอยไปอีเมล (ได้แถวสาขาอื่นของนิติบุคคลเดียวกัน)
        var branch8 = C("B8", email: "buyer@x.com", taxId: ValidJuristicTaxId, branch: "00008");
        var d = CmsContactResolver.Decide(new[] { branch8 }, In(email: "buyer@x.com", taxId: ValidJuristicTaxId, branch: "00000"));
        Assert.Null(d.Match);
    }

    [Fact]
    public void Contact_walk_in_row_rejected_when_buyer_has_real_tax_id()
    {
        var walkIn = C("ลูกค้าทั่วไป", email: "x@x.com", walkIn: true);
        Assert.Null(CmsContactResolver.Decide(new[] { walkIn }, In(email: "x@x.com", taxId: ValidJuristicTaxId)).Match);
        // ทิศตรงข้าม: ไม่มีเลข ⇒ ใช้แถวเดิมได้ (พฤติกรรมเดิมของอีเมล)
        Assert.Same(walkIn, CmsContactResolver.Decide(new[] { walkIn }, In(email: "x@x.com")).Match);
    }

    [Fact]
    public void Contact_email_match_adopts_tax_id_of_row_without_one()
    {
        var row = C("A", email: "a@x.com");
        var d = CmsContactResolver.Decide(new[] { row }, In(email: "a@x.com", taxId: ValidJuristicTaxId));
        Assert.Same(row, d.Match);
        Assert.Equal(ValidJuristicTaxId, row.TaxId);
    }

    [Fact]
    public void Contact_deterministic_oldest_first()
    {
        var older = C("old", email: "a@x.com", ageDays: 30);
        var newer = C("new", email: "a@x.com", ageDays: 1);
        Assert.Same(older, CmsContactResolver.Decide(new[] { newer, older }, In(email: "a@x.com")).Match);
        Assert.Same(older, CmsContactResolver.Decide(new[] { older, newer }, In(email: "a@x.com")).Match);
    }

    [Fact]
    public void New_contact_prefers_company_name_and_normalizes_tax_id()
    {
        var c = CmsContactResolver.NewContact(Co, new CmsContactInput(null, "0-1055-50123-45-1", "00000", "a@x.com", "081", "สมชาย",
            "บริษัท เอ จำกัด", "กทม.", "storefront-order"));
        Assert.Equal("บริษัท เอ จำกัด", c.Name);
        Assert.Equal(ValidJuristicTaxId, c.TaxId);
        Assert.Equal("00000", c.BranchCode);
        Assert.True(c.IsCustomer);
        // ไม่มีชื่อเลย ⇒ ชื่อสำรองของผู้เรียก
        Assert.Equal("Online Guest", CmsContactResolver.NewContact(Co,
            new CmsContactInput(null, null, null, null, null, null, null, null, "x", "Online Guest")).Name);
    }

    // ═════════ ฐ/D3: สาขาของเอกสาร + คลังที่ตัดสต็อก ═════════

    [Fact]
    public void Document_branch_from_site()
    {
        var b = Guid.NewGuid();
        Assert.Equal(b, CmsDocumentBranch.FromSiteBranch(b));
        Assert.Null(CmsDocumentBranch.FromSiteBranch(null));           // เว็บไม่ผูกสาขา = สำนักงานใหญ่เดิม
        Assert.Null(CmsDocumentBranch.FromSiteBranch(Guid.Empty));
        Assert.Null(CmsDocumentBranch.FromSiteBranch(b, branchUsable: false));   // สาขาถูกลบ ⇒ ไม่ล้มการรับเงิน
    }

    [Fact]
    public void Warehouse_default_is_company_default()
    {
        var w = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.Equal(CmsStockSource.CompanyDefault, CmsSiteWarehouse.Decide(true, w, b));   // UseGlobalInventory (ค่าเริ่มต้น) = เดิม
        Assert.Equal(CmsStockSource.CompanyDefault, CmsSiteWarehouse.Decide(false, null, null));
    }

    [Fact]
    public void Warehouse_site_then_branch()
    {
        var w = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.Equal(CmsStockSource.SiteWarehouse, CmsSiteWarehouse.Decide(false, w, b));
        Assert.Equal(CmsStockSource.BranchWarehouse, CmsSiteWarehouse.Decide(false, null, b));
        Assert.Equal(CmsStockSource.BranchWarehouse, CmsSiteWarehouse.Decide(false, Guid.Empty, b));
    }

    // ═════════ ฑ: ความยินยอม PDPA ═════════

    [Fact]
    public void Consent_required_blocks_guest_without_tick()
        => Assert.Equal(CmsDataConsent.MissingMessage, CmsDataConsent.Problem(true, false, false, false));

    [Theory]   // ทิศตรงข้าม: ติ๊กแล้ว · พนักงานบันทึกแทน · ลูกค้าที่ยินยอมไว้แล้ว · เว็บไม่บังคับ (ค่าเริ่มต้น = เดิม)
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    public void Consent_passes(bool required, bool given, bool staff, bool customerConsented)
        => Assert.Null(CmsDataConsent.Problem(required, given, staff, customerConsented));

    [Fact]
    public void Consent_recorded_only_when_ticked()
    {
        Assert.True(CmsDataConsent.ShouldRecord(true));
        Assert.False(CmsDataConsent.ShouldRecord(false));   // ไม่แต่งหลักฐานให้คนที่ไม่ได้ติ๊ก (พนักงาน/ไม่บังคับ)
    }

    [Fact]
    public void Consent_version_tracks_shown_text()
    {
        var a = CmsDataConsent.ResolveText(null, "ร้านเค้ก");
        Assert.Contains("ร้านเค้ก", a);
        Assert.Equal(CmsDataConsent.Version(a), CmsDataConsent.Version(CmsDataConsent.ResolveText("", "ร้านเค้ก")));
        Assert.NotEqual(CmsDataConsent.Version(a), CmsDataConsent.Version(CmsDataConsent.ResolveText("ข้อความใหม่", "ร้านเค้ก")));
        Assert.StartsWith("site-", CmsDataConsent.Version(a));
    }

    [Fact]
    public void Consent_evidence_hash_round_trips()
    {
        var at = new DateTime(2026, 10, 29, 3, 0, 0, DateTimeKind.Utc);
        var v = CmsDataConsent.Version("x");
        var h = PdpaConsentEvidence.ComputeHash("a@x.com", CmsDataConsent.OrderPurpose, v, at, "web-form:WEB-1", null, null);
        Assert.True(PdpaConsentEvidence.Verify(h, "a@x.com", CmsDataConsent.OrderPurpose, v, at, "web-form:WEB-1", null, null));
        Assert.False(PdpaConsentEvidence.Verify(h, "a@x.com", CmsDataConsent.OrderPurpose, CmsDataConsent.Version("y"), at, "web-form:WEB-1", null, null));
    }

    // ═════════ ช: อีเมลกู้ตะกร้า ═════════

    [Fact]
    public void Abandoned_cart_sends_only_with_marketing_consent()
    {
        Assert.True(AbandonedCartEligibility.Decide(true, true, "a@x.com", acceptMarketing: true, consentWithdrawn: false).Send);
        Assert.False(AbandonedCartEligibility.Decide(true, true, "a@x.com", acceptMarketing: false, consentWithdrawn: false).Send);   // เดิมส่ง
    }

    [Theory]
    [InlineData(false, true, "a@x.com", true, false)]   // ร้านปิด
    [InlineData(true, false, null, true, false)]        // แขกไม่มีบัญชี
    [InlineData(true, true, "not-an-email", true, false)]
    [InlineData(true, true, "a@x.com", true, true)]     // ถอนความยินยอมแล้ว
    public void Abandoned_cart_blocked(bool enabled, bool hasCustomer, string? email, bool marketing, bool withdrawn)
        => Assert.False(AbandonedCartEligibility.Decide(enabled, hasCustomer, email, marketing, withdrawn).Send);

    [Theory]
    [InlineData(null, 3)]
    [InlineData(0, 3)]
    [InlineData(24, 24)]
    [InlineData(10000, 3)]
    public void Abandoned_cart_hours_from_site(int? configured, int expected)
        => Assert.Equal(expected, AbandonedCartEligibility.Hours(configured));

    // ═════════ E5/D9: ตัวเลือกย่อย (variant) ═════════

    private static SiteProductVariant V(decimal? price = null, Guid? erp = null, bool active = true, string sku = "M-BLK", string name = "M ดำ")
        => new() { Id = Guid.NewGuid(), Sku = sku, Name = name, Price = price, ErpProductId = erp, IsActive = active };

    [Fact]
    public void Variant_price_overrides_base()
    {
        Assert.Equal(390m, CmsVariantPricing.UnitPrice(350m, 390m));
        Assert.Equal(350m, CmsVariantPricing.UnitPrice(350m, null));   // ไม่ตั้งราคา ⇒ ราคาสินค้า
    }

    [Fact]
    public void Variant_stock_from_erp_product_else_parent()
    {
        var parent = Guid.NewGuid(); var child = Guid.NewGuid();
        Assert.Equal(child, CmsVariantPricing.StockProductId(parent, child));
        Assert.Equal(parent, CmsVariantPricing.StockProductId(parent, null));   // ไม่มี variant/ไม่ผูก ⇒ สินค้าแม่ (เดิม)
        Assert.Equal(parent, CmsVariantPricing.StockProductId(parent, Guid.Empty));
    }

    [Fact]
    public void Variant_sku_and_line_name()
    {
        var v = V();
        Assert.Equal("M-BLK", CmsVariantPricing.Sku("SHIRT", v));
        Assert.Equal("เสื้อ - M ดำ [SKU M-BLK]", CmsVariantPricing.LineName("เสื้อ", v));
        // ทิศตรงข้าม: สินค้าไม่มี variant ⇒ ชื่อ/SKU เดิมทุกตัวอักษร (บรรทัดเอกสาร ERP ไม่เปลี่ยน)
        Assert.Equal("SHIRT", CmsVariantPricing.Sku("SHIRT", null));
        Assert.Equal("เสื้อ", CmsVariantPricing.LineName("เสื้อ", null));
    }

    [Fact]
    public void Variant_selection_required_when_product_has_variants()
    {
        Assert.NotNull(CmsVariantPricing.SelectionProblem(true, null, null));
        var v = V();
        Assert.Null(CmsVariantPricing.SelectionProblem(true, v.Id, v));
        Assert.NotNull(CmsVariantPricing.SelectionProblem(true, Guid.NewGuid(), null));          // id ของสินค้าอื่น/ไม่พบ
        var off = V(active: false);
        Assert.NotNull(CmsVariantPricing.SelectionProblem(true, off.Id, off));                   // ปิดขาย
    }

    [Fact]
    public void Variant_absent_product_unchanged()
    {
        Assert.Null(CmsVariantPricing.SelectionProblem(false, null, null));                       // สินค้าไม่มี variant ⇒ เส้นเดิม
        Assert.NotNull(CmsVariantPricing.SelectionProblem(false, Guid.NewGuid(), null));          // ส่ง variant มากับสินค้าที่ไม่มี ⇒ ไม่ทิ้งเงียบ
    }

    [Fact]
    public void Variant_in_stock_rule()
    {
        Assert.False(CmsVariantPricing.InStock(0m, inStockOnly: true));
        Assert.True(CmsVariantPricing.InStock(0m, inStockOnly: false));   // สั่งจอง/ไม่ติดตามสต็อก
        Assert.True(CmsVariantPricing.InStock(2m, inStockOnly: true));
    }

    // ═════════ D6: DSR ลบข้อมูลเว็บ (legal hold คงช่องผู้ซื้อ) ═════════

    private static SiteOrder Order() => new()
    {
        ShippingName = "สมชาย", ShippingAddress = "1 ถนน", ShippingPhone = "0812345678", ShippingEmail = "a@x.com",
        ShippingPostalCode = "10110", CustomerNotes = "ฝากหน้าบ้าน", BillingName = "บริษัท เอ", BillingAddress = "2 ถนน",
        BillingTaxId = ValidJuristicTaxId, BillingBranchCode = "00000", OrderNumber = "WEB-1", TotalAmount = 500m,
    };

    [Fact]
    public void Erase_order_under_legal_hold_keeps_buyer_fields()
    {
        var o = Order();
        Assert.True(CmsPdpaErasure.AnonymizeOrder(o, legalHold: true) > 0);
        Assert.Null(o.ShippingEmail); Assert.Null(o.ShippingPhone); Assert.Null(o.ShippingAddress); Assert.Null(o.CustomerNotes);
        Assert.Equal("บริษัท เอ", o.BillingName);              // §86/4 ผู้ซื้อบนใบกำกับ — พ.ร.บ.การบัญชี ม.10 ถือ 5 ปี
        Assert.Equal(ValidJuristicTaxId, o.BillingTaxId);
        Assert.Equal("2 ถนน", o.BillingAddress);
        Assert.Equal(500m, o.TotalAmount);                       // ยอดเงินไม่ใช่ข้อมูลส่วนบุคคล
    }

    [Fact]
    public void Erase_order_without_hold_clears_everything_personal()
    {
        var o = Order();
        CmsPdpaErasure.AnonymizeOrder(o, legalHold: false);
        Assert.Null(o.BillingName); Assert.Null(o.BillingTaxId); Assert.Null(o.ShippingName); Assert.Null(o.ShippingEmail);
        Assert.Equal("WEB-1", o.OrderNumber);
    }

    [Fact]
    public void Erase_hold_keeps_shipping_name_only_when_no_billing_name()
    {
        var o = Order(); o.BillingName = null;
        CmsPdpaErasure.AnonymizeOrder(o, legalHold: true);
        Assert.Equal("สมชาย", o.ShippingName);   // ชื่อผู้ซื้อบนเอกสารมาจากชื่อผู้รับ
    }

    [Fact]
    public void Legal_hold_window_is_five_years()
    {
        var now = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(CmsPdpaErasure.IsUnderLegalHold(now.AddYears(-4), now));
        Assert.False(CmsPdpaErasure.IsUnderLegalHold(now.AddYears(-5).AddDays(-1), now));
        Assert.False(CmsPdpaErasure.IsUnderLegalHold(null, now));   // ไม่ผูกเอกสาร ⇒ ลบได้ทั้งหมด
    }

    [Fact]
    public void Erase_lead_and_booking_respect_hold()
    {
        var l = new CmsLead { LeadNumber = "L-1", CustomerName = "ก", CustomerEmail = "a@x.com", CustomerPhone = "0812345678",
            CustomerTaxId = ValidJuristicTaxId, Message = "ขอราคา", DataJson = "{\"budget\":1}" };
        CmsPdpaErasure.AnonymizeLead(l, legalHold: true);
        Assert.Equal(ValidJuristicTaxId, l.CustomerTaxId); Assert.Equal("ก", l.CustomerName);
        Assert.Null(l.CustomerEmail); Assert.Null(l.Message); Assert.Null(l.DataJson);

        var b = new SiteBooking { GuestName = "ข", GuestEmail = "b@x.com", GuestPhone = "0899999999", CustomerNotes = "แพ้ยา" };
        CmsPdpaErasure.AnonymizeBooking(b, legalHold: false);
        Assert.Null(b.GuestName); Assert.Null(b.GuestEmail); Assert.Null(b.CustomerNotes);
    }

    [Fact]
    public void Erase_customer_closes_every_entry_point()
    {
        var c = new SiteCustomer { Id = Guid.NewGuid(), Email = "a@x.com", PasswordHash = "h", AuthProvider = "google", AuthProviderId = "1",
            PasswordResetToken = "t", EmailVerificationToken = "v", AcceptMarketing = true, IsActive = true, Phone = "081" };
        CmsPdpaErasure.AnonymizeCustomer(c, DateTime.UtcNow);
        Assert.EndsWith("@anonymized.local", c.Email);
        Assert.Null(c.PasswordHash); Assert.Null(c.AuthProviderId); Assert.Null(c.PasswordResetToken); Assert.Null(c.EmailVerificationToken);
        Assert.False(c.AcceptMarketing); Assert.False(c.IsActive); Assert.True(c.RightToBeforgotten);
    }

    // ═════════ D7: ฟอร์ม → lead + อีเมลแจ้ง ═════════

    [Fact]
    public void Form_fields_mapped_to_lead_columns()
    {
        var f = CmsFormLeadMapper.Map("{\"Name\":\"สมชาย\",\"email\":\"a@x.com\",\"tel\":\"081\",\"budget\":\"5000\"}");
        Assert.Equal("สมชาย", f.Name); Assert.Equal("a@x.com", f.Email); Assert.Equal("081", f.Phone);
        Assert.Equal("5000", f.Extras["budget"]);
        Assert.Null(CmsFormLeadMapper.Map("not json").Email);   // คำตอบเสีย ⇒ ว่าง (ไม่โยน)
    }

    [Fact]
    public void Form_lead_type_and_recipients()
    {
        Assert.Equal(LeadType.Rfq, CmsFormLeadMapper.LeadTypeFor(SiteFormType.Rfq, null));
        Assert.Equal(LeadType.Quote, CmsFormLeadMapper.LeadTypeFor(SiteFormType.Contact, DocumentType.Quotation));
        Assert.Equal(LeadType.Contact, CmsFormLeadMapper.LeadTypeFor(SiteFormType.Contact, null));
        Assert.Equal(new[] { "a@x.com", "b@x.com" }, CmsFormLeadMapper.Recipients("A@x.com, b@x.com;bad; a@x.com").ToArray());
    }

    [Fact]
    public void Form_notify_html_encodes_user_input()
    {
        var html = CmsFormLeadMapper.NotifyHtml("ติดต่อ", "ร้าน", CmsFormLeadMapper.Map("{\"name\":\"<script>alert(1)</script>\"}"));
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}
