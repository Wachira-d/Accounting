using Xunit;

namespace Accounting.Tests;

/// <summary>
/// กันสแกนใบเดิมซ้ำ (CheckDuplicateAsync) — ใบเดียวกันถูกสแกนสองครั้ง
/// (ส่งไลน์ซ้ำ / ถ่ายซ้ำ / คนละคนอัปโหลด) จะสร้างเอกสาร **คนละเลขของเรา**
/// ⇒ ค่าใช้จ่ายและภาษีซื้อเบิ้ลแบบเงียบ ๆ เพราะไม่มีอะไรชนกัน
///
/// สองระดับ: เลขใบกำกับผู้ขายตรงกัน = แน่นอน · คู่ค้า+ยอด+ช่วงวัน = น่าสงสัย
/// ทั้งคู่ **เตือนอย่างเดียว ห้ามบล็อก** — ผู้ขายขายของชุดเดิมซ้ำได้จริง
/// (false positive ที่บล็อกแรงกว่าปัญหาที่กัน)
/// </summary>
public class DuplicateScanCheckTests
{
    public sealed record Doc(
        Guid Id, Guid ContactId, string? SupplierInvoiceNumber,
        string DocumentType, decimal TotalAmount, DateTime DocumentDate,
        string Status = "Approved", bool IsDeleted = false);

    /// <summary>mirror ของ DocumentService.CheckDuplicateAsync</summary>
    private static (bool Strong, List<(Guid Id, string Reason)> Hits) Check(
        IEnumerable<Doc> all, Guid? contactId, string? supplierInvoiceNumber,
        string? docType, decimal amount, DateTime date, Guid? exclude = null)
    {
        var seen = new HashSet<Guid>();
        if (exclude.HasValue) seen.Add(exclude.Value);
        var hits = new List<(Guid, string)>();

        var live = all.Where(d => !d.IsDeleted
            && d.Status != "Voided" && d.Status != "Rejected").ToList();

        var sin = supplierInvoiceNumber?.Trim();
        if (!string.IsNullOrWhiteSpace(sin))
            foreach (var d in live.Where(d =>
                d.SupplierInvoiceNumber != null
                && string.Equals(d.SupplierInvoiceNumber, sin, StringComparison.OrdinalIgnoreCase)
                && (contactId == null || d.ContactId == contactId)))
                if (seen.Add(d.Id)) hits.Add((d.Id, "SupplierInvoiceNumber"));

        if (contactId.HasValue && amount > 0.01m)
        {
            var tol = amount * 0.005m;
            foreach (var d in live.Where(d => d.ContactId == contactId.Value
                && d.DocumentDate >= date.AddDays(-60) && d.DocumentDate <= date.AddDays(60)
                && (docType == null || d.DocumentType == docType)
                && d.TotalAmount >= amount - tol && d.TotalAmount <= amount + tol))
                if (seen.Add(d.Id)) hits.Add((d.Id, "SameContactAndAmount"));
        }

        return (hits.Any(h => h.Item2 == "SupplierInvoiceNumber"), hits);
    }

    private static readonly Guid Vendor = Guid.NewGuid();
    private static readonly Guid OtherVendor = Guid.NewGuid();
    private static readonly DateTime Today = new(2026, 8, 17);

    private static Doc Existing(string? sin, decimal amt, int dayOffset = 0,
        string status = "Approved", Guid? contact = null) =>
        new(Guid.NewGuid(), contact ?? Vendor, sin, "Expense", amt,
            Today.AddDays(dayOffset), status);

    [Fact]
    public void Same_supplier_invoice_number_is_a_strong_match()
    {
        var db = new[] { Existing("BASIE26070112200", 19142.30m, -5) };
        var (strong, hits) = Check(db, Vendor, "BASIE26070112200", "Expense", 19142.30m, Today);
        Assert.True(strong);
        Assert.Single(hits);
    }

    [Fact]
    public void Supplier_number_match_is_case_insensitive_and_trimmed()
    {
        var db = new[] { Existing("basie26070112200", 100m) };
        Assert.True(Check(db, Vendor, "  BASIE26070112200 ", "Expense", 100m, Today).Strong);
    }

    [Fact]
    public void Same_number_from_a_different_vendor_is_not_a_match()
    {
        // เลขใบกำกับซ้ำข้ามผู้ขายเกิดได้ปกติ (แต่ละเจ้ารันเลขของตัวเอง)
        var db = new[] { Existing("INV-001", 100m, contact: OtherVendor) };
        Assert.False(Check(db, Vendor, "INV-001", "Expense", 100m, Today).Strong);
    }

    [Fact]
    public void Same_contact_and_amount_is_a_weak_match_only()
    {
        var db = new[] { Existing(null, 19142.30m, -3) };
        var (strong, hits) = Check(db, Vendor, null, "Expense", 19142.30m, Today);
        Assert.False(strong);                       // ไม่ใช่ระดับ "แน่นอน"
        Assert.Single(hits);
        Assert.Equal("SameContactAndAmount", hits[0].Reason);
    }

    [Fact]
    public void Amount_tolerance_is_half_a_percent()
    {
        Assert.Single(Check(new[] { Existing(null, 10000m) }, Vendor, null, "Expense", 10040m, Today).Hits);
        Assert.Empty(Check(new[] { Existing(null, 10000m) }, Vendor, null, "Expense", 10060m, Today).Hits);
    }

    [Fact]
    public void Outside_the_sixty_day_window_is_not_flagged()
    {
        // ค่าบริการรายเดือนยอดเท่ากันทุกเดือน — ห่างเกิน 60 วันไม่ใช่ใบซ้ำ
        Assert.Empty(Check(new[] { Existing(null, 5000m, -90) }, Vendor, null, "Expense", 5000m, Today).Hits);
        Assert.Single(Check(new[] { Existing(null, 5000m, -30) }, Vendor, null, "Expense", 5000m, Today).Hits);
    }

    [Theory]
    [InlineData("Voided")]
    [InlineData("Rejected")]
    public void Voided_and_rejected_documents_are_ignored(string status)
    {
        var db = new[] { Existing("INV-001", 100m, status: status) };
        Assert.False(Check(db, Vendor, "INV-001", "Expense", 100m, Today).Strong);
    }

    [Fact]
    public void Deleted_documents_are_ignored()
    {
        var d = Existing("INV-001", 100m) with { IsDeleted = true };
        Assert.False(Check(new[] { d }, Vendor, "INV-001", "Expense", 100m, Today).Strong);
    }

    [Fact]
    public void The_document_being_edited_is_excluded()
    {
        // แก้ใบเดิม → ต้องไม่เตือนว่าตัวมันเองซ้ำกับตัวเอง
        var self = Existing("INV-001", 100m);
        Assert.Empty(Check(new[] { self }, Vendor, "INV-001", "Expense", 100m, Today, exclude: self.Id).Hits);
    }

    [Fact]
    public void A_document_matching_both_rules_is_reported_once()
    {
        var db = new[] { Existing("INV-001", 100m) };
        var (strong, hits) = Check(db, Vendor, "INV-001", "Expense", 100m, Today);
        Assert.True(strong);
        Assert.Single(hits);                        // ไม่ซ้ำสองรายการ
        Assert.Equal("SupplierInvoiceNumber", hits[0].Reason);   // เหตุผลแรงชนะ
    }

    [Fact]
    public void No_signal_at_all_returns_nothing()
    {
        var db = new[] { Existing("INV-001", 100m) };
        Assert.Empty(Check(db, contactId: null, supplierInvoiceNumber: null,
            "Expense", 0m, Today).Hits);
    }
}
