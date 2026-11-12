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
            // รอบ 260: ตัวอ่านข้อความสลิปตัวเดียว (Helpers/OcrSlipText) — ใช้ร่วมกับสลิปจากแชต LINE
            var reading = Accounting.Helpers.OcrSlipText.Parse(text);
            var amounts = reading.Amounts;

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
            payment.SlipOcrReference = reading.Ref;
            payment.SlipOcrDate = reading.Date;
            payment.SlipOcrParsedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "อ่านสลิป OCR ไม่สำเร็จ payment {PaymentId}", paymentId);
        }
    }
}
