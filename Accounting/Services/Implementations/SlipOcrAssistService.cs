using System.Globalization;
using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Services.Implementations.Ocr;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>WP-C3: อ่านสลิปโอน (local Tesseract) + parse แบบ rule-based.
/// advisory — ไม่ auto-approve. ไม่เรียก AI ภายนอก จึงไม่เข้าเงื่อนไข distillation.</summary>
public class SlipOcrAssistService : ISlipOcrAssistService
{
    private readonly EmbeddedTesseractOcrService _ocr;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SlipOcrAssistService> _logger;

    public SlipOcrAssistService(EmbeddedTesseractOcrService ocr,
        IServiceScopeFactory scopeFactory, ILogger<SlipOcrAssistService> logger)
    {
        _ocr = ocr; _scopeFactory = scopeFactory; _logger = logger;
    }

    // ยอดเงินรูปแบบสลิปไทย: 1,234.56 (มีทศนิยม 2 ตำแหน่งเสมอ — เลขบัญชีไม่มี)
    private static readonly Regex AmountRx = new(@"\d{1,3}(?:,\d{3})*\.\d{2}", RegexOptions.Compiled);
    // เลขอ้างอิง/รายการ: ชุดตัวเลข/ตัวอักษรยาว 10–25 (transaction id)
    private static readonly Regex RefRx = new(@"[A-Z0-9]{10,25}", RegexOptions.Compiled);
    // วันที่: 01/02/2024, 01-02-24, 1 ก.พ. 2567 ฯลฯ (จับแบบ dd/mm[/yy] ก่อน)
    private static readonly Regex DateRx = new(@"\b(\d{1,2})[\/\-.](\d{1,2})[\/\-.](\d{2,4})\b", RegexOptions.Compiled);

    public async Task ParseAndStoreAsync(Guid paymentId, byte[] imageBytes, string contentType)
    {
        try
        {
            if (!_ocr.IsAvailable || imageBytes.Length == 0) return;

            var ocr = await _ocr.ExtractTextAsync(imageBytes, contentType);
            if (!ocr.Success || string.IsNullOrWhiteSpace(ocr.Text)) return;
            var text = ocr.Text;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var payment = await db.SubscriptionPayments.FirstOrDefaultAsync(p => p.Id == paymentId);
            if (payment == null) return;

            var expected = payment.Amount;
            var amounts = AmountRx.Matches(text)
                .Select(m => decimal.TryParse(m.Value.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null)
                .Where(d => d.HasValue).Select(d => d!.Value).Distinct().ToList();

            decimal? chosen = null;
            bool? matches = null;
            if (amounts.Count > 0)
            {
                // ให้ค่าที่ "ตรงกับยอดที่แจ้ง" มาก่อน (ปลอดภัยสุดสำหรับ badge)
                if (amounts.Any(a => Math.Abs(a - expected) < 0.01m)) { chosen = expected; matches = true; }
                else { chosen = amounts.Max(); matches = false; } // ยอดสูงสุด = มักเป็นยอดโอน
            }

            payment.SlipOcrAmount = chosen;
            payment.SlipOcrAmountMatches = matches;
            payment.SlipOcrReference = ExtractRef(text);
            payment.SlipOcrDate = ExtractDate(text);
            payment.SlipOcrParsedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "อ่านสลิป OCR ไม่สำเร็จ payment {PaymentId}", paymentId);
        }
    }

    private static string? ExtractRef(string text)
    {
        // เลือกชุดตัวเลขล้วนยาวสุด (transaction id) ตัดพวกที่เป็นเงิน/วันที่ออก
        var cand = RefRx.Matches(text).Select(m => m.Value)
            .Where(v => v.Any(char.IsDigit) && v.Length >= 10)
            .OrderByDescending(v => v.Length).FirstOrDefault();
        return cand;
    }

    private static DateTime? ExtractDate(string text)
    {
        var m = DateRx.Match(text);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, out var d) || !int.TryParse(m.Groups[2].Value, out var mo)
            || !int.TryParse(m.Groups[3].Value, out var y)) return null;
        if (y < 100) y += 2000;           // yy → 20yy
        if (y > 2500) y -= 543;           // พ.ศ. → ค.ศ.
        if (d is < 1 or > 31 || mo is < 1 or > 12 || y is < 2000 or > 2100) return null;
        try { return new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Utc); }
        catch { return null; }
    }
}
