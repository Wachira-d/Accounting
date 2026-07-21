namespace Accounting.Services.Interfaces;

/// <summary>
/// สร้าง PromptPay QR Code ตามมาตรฐาน EMVCo
/// ใช้สำหรับรับชำระเงินผ่าน PromptPay (พร้อมเพย์)
/// </summary>
public interface IPromptPayService
{
    string GenerateQrPayload(string promptPayId, decimal amount, string? ref1 = null, string? ref2 = null);
    byte[] GenerateQrImage(string promptPayId, decimal amount, int size = 300, string? ref1 = null, string? ref2 = null);
    bool ValidatePromptPayId(string promptPayId);
}
