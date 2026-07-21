using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Detect recurring expense patterns per vendor — monthly bills (rent,
/// electricity, internet) tend to arrive on similar calendar dates with
/// similar amounts. When the system spots a recurring vendor, the
/// auto-create confidence threshold can be lowered: we already know what
/// to expect, so an OCR that "looks right" is more trustworthy than an
/// equivalent one from a one-off vendor.
///
/// Algorithm — analyze the past N documents per vendor and classify the
/// inter-arrival gap distribution:
///   • Monthly: gaps cluster around 28–32 days
///   • Quarterly: gaps cluster around 88–95 days
///   • Yearly: gaps cluster around 360–370 days
///   • Irregular: anything else
///
/// We require at least 3 historical documents before declaring a
/// recurring pattern, and that 60%+ of gaps fall in the same bucket.
/// </summary>
public class RecurringExpenseDetector
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<RecurringExpenseDetector> _logger;

    public RecurringExpenseDetector(AccountingDbContext db, ILogger<RecurringExpenseDetector> logger)
    {
        _db = db;
        _logger = logger;
    }

    public record RecurrenceResult(
        string Cadence,                 // "Monthly" | "Quarterly" | "Yearly" | "Irregular"
        decimal Confidence,             // 0.0 – 1.0
        int SampleSize,
        decimal? TypicalAmount,
        DateTime? NextExpectedDate);

    public async Task<RecurrenceResult?> DetectAsync(Guid companyId, Guid? contactId)
    {
        if (!contactId.HasValue) return null;
        var rows = await _db.Documents.AsNoTracking()
            .Where(d => !d.IsDeleted && d.CompanyId == companyId && d.ContactId == contactId.Value
                && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.Paid)
                && PurchaseSideTypes.Contains(d.DocumentType))
            .OrderBy(d => d.DocumentDate)
            .Select(d => new { d.DocumentDate, d.TotalAmount })
            .ToListAsync();
        if (rows.Count < 3) return null;

        // Inter-arrival gaps in days
        var gaps = new List<int>(rows.Count - 1);
        for (int i = 1; i < rows.Count; i++)
            gaps.Add((int)(rows[i].DocumentDate - rows[i - 1].DocumentDate).TotalDays);

        // Classify each gap
        int monthly = gaps.Count(g => g >= 25 && g <= 35);
        int quarterly = gaps.Count(g => g >= 85 && g <= 100);
        int yearly = gaps.Count(g => g >= 350 && g <= 380);

        var total = (decimal)gaps.Count;
        var cadence = "Irregular";
        decimal confidence = 0m;
        int? typicalDays = null;
        if (monthly / total >= 0.6m) { cadence = "Monthly"; confidence = monthly / total; typicalDays = 30; }
        else if (quarterly / total >= 0.6m) { cadence = "Quarterly"; confidence = quarterly / total; typicalDays = 90; }
        else if (yearly / total >= 0.6m) { cadence = "Yearly"; confidence = yearly / total; typicalDays = 365; }
        else return null;  // no useful pattern

        // Typical amount = median of past totals (robust to outliers)
        var totals = rows.Select(r => r.TotalAmount).OrderBy(a => a).ToList();
        var medianAmt = totals.Count % 2 == 0
            ? (totals[totals.Count / 2 - 1] + totals[totals.Count / 2]) / 2
            : totals[totals.Count / 2];
        var nextExpected = typicalDays.HasValue
            ? rows[^1].DocumentDate.AddDays(typicalDays.Value)
            : (DateTime?)null;

        return new RecurrenceResult(cadence, confidence, rows.Count, medianAmt, nextExpected);
    }

    private static readonly DocumentType[] PurchaseSideTypes = {
        DocumentType.PurchaseInvoice,
        DocumentType.Expense,
        DocumentType.CertificateInLieu,
        DocumentType.PurchaseOrder,
        DocumentType.PaymentVoucher,
    };
}
