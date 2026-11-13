using Accounting.Helpers;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 264 แพ็ก P2 (คำตัดสิน 264-4-1) — <b>ถอด QR จากรูปสลิปในเครื่อง</b>: พิกเซลผ่าน SkiaSharp เดิม (<see cref="SkiaImageOps.DecodeUpright"/>) ⇒ ความสว่าง 8 บิต ⇒
/// ZXing.Net แพ็กเกจหลัก (managed · ไม่ใช้ <c>ZXing.Net.Bindings.SkiaSharp</c> ที่ผูก SkiaSharp รุ่นอื่น) · ไม่เรียกเครือข่าย · ไม่มี AI ·
/// ถอดไม่ได้/รูปเสีย ⇒ null (ไม่โยน — สลิปยังอ่านข้อความได้ตามเดิม) · คืน<b>ข้อความดิบ</b> — ผู้ตีความคือ <see cref="ThaiSlipQr.Parse"/> ตัวเดียว
/// </summary>
public static class SlipQrImageDecoder
{
    /// <summary>ด้านยาวสุดที่ลองถอด (รูปสลิปจริง 750–2400 px) — ใหญ่กว่านี้ย่อก่อน ลดเวลา · ลองขนาดเต็มก่อนแล้วย่อครึ่งเมื่อหาไม่พบ</summary>
    public const int MaxEdge = 2400;

    public static string? TryDecode(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using var decoded = SkiaImageOps.DecodeUpright(bytes);
            if (decoded == null) return null;
            var src = decoded.Bitmap;
            var owned = new List<SKBitmap>();
            try
            {
                if (Math.Max(src.Width, src.Height) > MaxEdge)
                {
                    src = SkiaImageOps.ResizeMax(src, MaxEdge);
                    owned.Add(src);
                }
                var text = DecodeBitmap(src);
                if (text == null && Math.Min(src.Width, src.Height) >= 600)
                {
                    var half = SkiaImageOps.Resize(src, src.Width / 2, src.Height / 2);
                    owned.Add(half);
                    text = DecodeBitmap(half);
                }
                return text;
            }
            finally
            {
                foreach (var b in owned) b.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>ความสว่าง BT.709 ต่อพิกเซล ⇒ ZXing (QR อย่างเดียว · TryHarder) · คืนข้อความของ QR แรกที่ถอดได้</summary>
    internal static string? DecodeBitmap(SKBitmap bmp)
    {
        var w = bmp.Width; var h = bmp.Height;
        var lum = new byte[w * h];
        var px = bmp.Pixels;
        for (var i = 0; i < px.Length && i < lum.Length; i++)
        {
            var c = px[i];
            // พื้นโปร่งใส ⇒ ขาว (QR บน PNG โปร่งใส)
            var a = c.Alpha / 255.0;
            var y = 0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue;
            lum[i] = (byte)Math.Clamp((int)Math.Round(y * a + 255 * (1 - a), MidpointRounding.AwayFromZero), 0, 255);
        }
        var source = new RGBLuminanceSource(lum, w, h, RGBLuminanceSource.BitmapFormat.Gray8);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE } },
        };
        var result = reader.Decode(source);
        return string.IsNullOrWhiteSpace(result?.Text) ? null : result!.Text;
    }
}
