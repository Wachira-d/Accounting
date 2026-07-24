namespace Accounting.Services.Interfaces;

/// <summary>WP-C3: อ่านสลิปโอนเงิน (local Tesseract OCR + rule-based parse)
/// ช่วย prefill/เทียบยอดตอน admin review — advisory เท่านั้น ไม่ auto-approve.
/// 100% local (ไม่เรียก LLM ภายนอก) → ไม่ต้องมี distillation loop ตามกฎเหล็ก #1.
/// best-effort: OCR ไม่พร้อม/อ่านไม่ออก → ไม่ทำอะไร ไม่กระทบ flow.</summary>
public interface ISlipOcrAssistService
{
    /// <summary>OCR สลิป → parse ยอด/วันที่/อ้างอิง → เทียบกับยอดที่ลูกค้าแจ้ง →
    /// เก็บผลลง SubscriptionPayment. ห้าม throw.</summary>
    Task ParseAndStoreAsync(Guid paymentId, byte[] imageBytes, string contentType);
}
