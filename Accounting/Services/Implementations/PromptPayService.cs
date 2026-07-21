using System.Globalization;
using System.Text;
using Accounting.Services.Interfaces;
using QRCoder;

namespace Accounting.Services.Implementations;

/// <summary>
/// PromptPay QR Code Generator ตามมาตรฐาน EMVCo QR Code Specification
/// รองรับทั้ง:
///   - เลขบัตรประชาชน 13 หลัก (บุคคลธรรมดา)
///   - เลขทะเบียนนิติบุคคล 13 หลัก (นิติบุคคล)
///   - เบอร์โทรศัพท์ 10 หลัก (ขึ้นต้น 0)
///   - e-Wallet ID
///   - Biller ID (for Bill Payment with ref1/ref2)
///
/// Spec: EMVCo QR Code Specification for Payment Systems (EMV QRCPS) Merchant-Presented Mode
/// Thai BOT PromptPay uses Application ID: A000000677010111 (PromptPay Credit Transfer)
/// </summary>
public class PromptPayService : IPromptPayService
{
    private const string PayloadFormatIndicator = "01";
    private const string PointOfInitiation_Static = "11";
    private const string PointOfInitiation_Dynamic = "12";
    private const string MerchantAccountPromptPay = "29";
    private const string PromptPayAid = "A000000677010111";
    private const string PromptPayBillPaymentAid = "A000000677010112";
    private const string TransactionCurrency_THB = "764";
    private const string CountryCode_TH = "TH";

    public string GenerateQrPayload(string promptPayId, decimal amount, string? ref1 = null, string? ref2 = null)
    {
        var sanitized = SanitizeId(promptPayId);
        var isBillPayment = ref1 != null;

        var sb = new StringBuilder();

        // ID 00: Payload Format Indicator
        sb.Append(TlvField("00", PayloadFormatIndicator));

        // ID 01: Point of Initiation Method (12 = dynamic/one-time, 11 = static/reusable)
        sb.Append(TlvField("01", amount > 0 ? PointOfInitiation_Dynamic : PointOfInitiation_Static));

        // ID 29 or 30: Merchant Account Information — PromptPay
        if (isBillPayment)
        {
            // Bill Payment uses tag 30 with AID A000000677010112
            var billSub = new StringBuilder();
            billSub.Append(TlvField("00", PromptPayBillPaymentAid));
            billSub.Append(TlvField("01", sanitized)); // Biller ID
            billSub.Append(TlvField("02", ref1!));     // Reference 1
            if (!string.IsNullOrEmpty(ref2))
                billSub.Append(TlvField("03", ref2));  // Reference 2
            sb.Append(TlvField("30", billSub.ToString()));
        }
        else
        {
            // Credit Transfer uses tag 29 with AID A000000677010111
            var subData = new StringBuilder();
            subData.Append(TlvField("00", PromptPayAid));

            if (sanitized.Length == 13)
            {
                // Tax ID or National ID — sub-tag 02
                subData.Append(TlvField("02", sanitized));
            }
            else if (sanitized.Length >= 10)
            {
                // Phone number — convert 0xxxxxxxxx to 0066xxxxxxxxx
                var intlPhone = "0066" + sanitized[1..];
                subData.Append(TlvField("01", intlPhone));
            }
            else
            {
                // e-Wallet ID — sub-tag 03
                subData.Append(TlvField("03", sanitized));
            }

            sb.Append(TlvField(MerchantAccountPromptPay, subData.ToString()));
        }

        // ID 52: Merchant Category Code (0000 = not applicable)
        sb.Append(TlvField("52", "0000"));

        // ID 53: Transaction Currency (764 = THB)
        sb.Append(TlvField("53", TransactionCurrency_THB));

        // ID 54: Transaction Amount (if specified)
        if (amount > 0)
        {
            var amountStr = amount.ToString("F2", CultureInfo.InvariantCulture);
            sb.Append(TlvField("54", amountStr));
        }

        // ID 58: Country Code
        sb.Append(TlvField("58", CountryCode_TH));

        // ID 63: CRC (placeholder — will be calculated)
        sb.Append("6304");

        // Calculate CRC16-CCITT
        var crc = CalculateCrc16(sb.ToString());
        sb.Append(crc.ToString("X4"));

        // Remove the placeholder "6304" and rebuild with actual CRC
        var payload = sb.ToString();
        var withoutCrc = payload[..^8]; // Remove "6304XXXX"
        var finalCrc = CalculateCrc16(withoutCrc + "6304");
        return withoutCrc + "6304" + finalCrc.ToString("X4");
    }

    public byte[] GenerateQrImage(string promptPayId, decimal amount, int size = 300, string? ref1 = null, string? ref2 = null)
    {
        var payload = GenerateQrPayload(promptPayId, amount, ref1, ref2);

        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(qrCodeData);

        return qrCode.GetGraphic(size / 33); // ~33 modules per QR
    }

    public bool ValidatePromptPayId(string promptPayId)
    {
        var sanitized = SanitizeId(promptPayId);

        // Phone number: 10 digits starting with 0
        if (sanitized.Length == 10 && sanitized.StartsWith('0'))
            return sanitized.All(char.IsDigit);

        // National ID / Tax ID: 13 digits
        if (sanitized.Length == 13 && sanitized.All(char.IsDigit))
            return ValidateThaiId(sanitized);

        // e-Wallet: 15 digits
        if (sanitized.Length == 15 && sanitized.All(char.IsDigit))
            return true;

        return false;
    }

    private static string SanitizeId(string id) =>
        new string(id.Where(c => char.IsDigit(c)).ToArray());

    private static string TlvField(string id, string value) =>
        $"{id}{value.Length:D2}{value}";

    /// <summary>
    /// CRC16-CCITT (0xFFFF) — used by EMVCo QR Code standard
    /// Polynomial: x^16 + x^12 + x^5 + 1 (0x1021)
    /// </summary>
    private static ushort CalculateCrc16(string input)
    {
        var bytes = Encoding.ASCII.GetBytes(input);
        ushort crc = 0xFFFF;

        foreach (var b in bytes)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 0x8000) != 0)
                    crc = (ushort)((crc << 1) ^ 0x1021);
                else
                    crc = (ushort)(crc << 1);
            }
        }

        return crc;
    }

    /// <summary>Validate Thai National ID / Tax ID using digit-13 checksum</summary>
    private static bool ValidateThaiId(string id)
    {
        if (id.Length != 13) return false;
        int sum = 0;
        for (int i = 0; i < 12; i++)
            sum += (13 - i) * (id[i] - '0');
        int check = (11 - (sum % 11)) % 10;
        return check == (id[12] - '0');
    }
}
