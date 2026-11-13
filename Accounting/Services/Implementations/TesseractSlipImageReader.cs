using Accounting.Helpers;
using Accounting.Services.Implementations.Ocr;

namespace Accounting.Services.Implementations;

/// <summary>รอบ 260: อ่านรูปสลิปเป็นยอด/วันที่/เลขอ้างอิง — ทางเดียวในเครื่อง (ไม่มี AI · ไม่มี distillation ที่ต้องทำ — กฎเหล็ก #1 ไม่เข้าเงื่อนไข)</summary>
public interface ISlipImageReader
{
    /// <summary>เครื่องนี้อ่านข้อความจากรูปได้ไหม (มี tessdata) — แถวความพร้อมแท็บ LINE</summary>
    bool IsAvailable { get; }

    /// <summary>ผลอ่าน · อ่านไม่ได้/ไม่มีข้อความ = <see cref="OcrSlipText.Reading.Empty"/> (ไม่โยน)</summary>
    Task<OcrSlipText.Reading> ReadAsync(byte[] bytes, string contentType, CancellationToken ct = default);
}

/// <summary>Tesseract ในเครื่อง (<see cref="EmbeddedTesseractOcrService"/>) + ตัวแยกข้อความสลิปตัวเดียว <see cref="OcrSlipText"/> ·
/// รอบ 264 P2 (คำตัดสิน 264-4-1): ถอด QR ตรวจสลิปในเครื่องด้วย <see cref="SlipQrImageDecoder"/> ⇒ ธนาคารผู้โอน + เลขอ้างอิงของธนาคาร (QR <b>ไม่มี</b>ยอด/เลขบัญชี —
/// การตรวจรายการกับธนาคารยังเป็น backlog 260-1-2) · ไม่มี tessdata แต่ถอด QR ได้ ⇒ ผลอ่านมีแต่ QR (ไม่มียอด ⇒ ผู้เรียกส่งเข้าคิวพนักงานตามเดิม)</summary>
public sealed class TesseractSlipImageReader : ISlipImageReader
{
    private readonly EmbeddedTesseractOcrService _ocr;
    private readonly ILogger<TesseractSlipImageReader> _logger;

    public TesseractSlipImageReader(EmbeddedTesseractOcrService ocr, ILogger<TesseractSlipImageReader> logger)
    {
        _ocr = ocr; _logger = logger;
    }

    public bool IsAvailable => _ocr.IsAvailable;

    public async Task<OcrSlipText.Reading> ReadAsync(byte[] bytes, string contentType, CancellationToken ct = default)
    {
        if (bytes.Length == 0) return OcrSlipText.Reading.Empty;
        // QR ก่อน (ถูก · ไม่พึ่ง tessdata) — ถอดไม่ได้ = null ⇒ ผลอ่านเท่าเดิมทุกประการ
        var qr = SlipQrImageDecoder.TryDecode(bytes);
        if (!_ocr.IsAvailable) return OcrSlipText.Parse(null, null, qr);
        try
        {
            var r = await _ocr.ExtractTextAsync(bytes, contentType);
            return OcrSlipText.Parse(r.Success ? r.Text : null, null, qr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // อ่านไม่ได้ = สลิปเข้าคิวพนักงานพร้อมเหตุ "อ่านยอดไม่ได้" (ผู้เรียกบันทึกเหตุในแถว — ไม่ใช่ความเงียบ)
            _logger.LogError(ex, "อ่านรูปสลิปด้วย OCR ในเครื่องล้ม");
            return OcrSlipText.Parse(null, null, qr);
        }
    }
}
