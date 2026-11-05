using System.Buffers.Binary;
using System.IO.Compression;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวตรวจรูปลายเซ็นที่คนนอกระบบวาดส่งมา — ตัวเดียวของทุกทางเข้าสาธารณะ** (รอบ 239 · คำตัดสิน 239-3)
///
/// <para>═══ ที่มา ═══ หน้าเซ็นรับสินค้า (<c>delivery-sign.html</c>) รับ base64 อะไรก็ได้ ≤ 700KB แล้วเก็บ/พิมพ์ลงเอกสารตรง ๆ
/// (ไม่ตรวจว่าเป็นรูปจริง · ไม่ตรวจว่าว่างเปล่า) · รอบ 239 เพิ่มลายเซ็นยอมรับใบเสนอราคาออนไลน์ ⇒ ทั้งสองเส้นต้องตรวจด้วยกติกาเดียว</para>
///
/// <para>═══ กติกา ═══ (1) รับ <c>data:image/png;base64,…</c> หรือ base64 ล้วน — ชนิดอื่นปฏิเสธ (canvas ส่ง PNG เสมอ) ·
/// (2) ≤ <see cref="MaxBytes"/> ไบต์หลังถอด base64 (ตรวจความยาวข้อความก่อนถอด — ไม่ถอดของบวม) · (3) magic bytes PNG + IHDR เป็น chunk แรก ·
/// (4) กว้าง/สูงอยู่ในกรอบ · (5) ถอด IDAT จริง (8 บิต · ไม่ interlace · ชนิดสี เทา/RGB/เทา+อัลฟา/RGBA) แล้วนับ "หมึก" —
/// น้อยกว่า <see cref="MinInkPixels"/> จุด หรือกรอบของหมึกเล็กกว่า <see cref="MinInkExtent"/> พิกเซลทั้งสองด้าน = ลายเซ็นว่าง</para>
///
/// <para>"หมึก" = พิกเซลที่เข้ม (ความสว่าง &lt; 200) — รูปที่มีอัลฟาต้องทึบพอด้วย (อัลฟา &gt; 32 · ฝ่ายค้านรอบ 239 S1: RGBA ขาวทึบเดิมผ่าน)
/// · ถอดแบบจำกัดขนาด (ไม่เกิน ความสูง × (1 + กว้าง × ไบต์ต่อพิกเซล)) กัน zip bomb</para>
/// </summary>
public static class SignatureImageValidator
{
    /// <summary>เพดานขนาดรูปหลังถอด base64 (200KB — ภาพ canvas ลายเซ็นปกติ 5–40KB)</summary>
    public const int MaxBytes = 200 * 1024;
    public const int MinWidth = 40;
    public const int MinHeight = 20;
    public const int MaxWidth = 2000;
    public const int MaxHeight = 1000;
    /// <summary>จำนวนจุดหมึกขั้นต่ำ — จุดเดียว/แตะหน้าจอพลาดไม่นับเป็นลายเซ็น</summary>
    public const int MinInkPixels = 40;
    /// <summary>กรอบของหมึกต้องกว้างหรือสูงอย่างน้อยเท่านี้ (พิกเซล)</summary>
    public const int MinInkExtent = 15;

    public enum Code { Ok, Empty, NotPng, TooLarge, BadStructure, Unsupported, BadDimensions, Blank }

    /// <summary>ผลตรวจ — <see cref="Png"/> = ไบต์ที่ผ่านแล้ว (ใช้เก็บ/พิมพ์ตัวนี้เท่านั้น ห้ามใช้ข้อความดิบจากคำขอ)</summary>
    public sealed record Result(Code Code, string? Message, byte[]? Png, int Width, int Height, int InkPixels)
    {
        public bool Ok => Code == Code.Ok;
        public string? DataUri => Png == null ? null : "data:image/png;base64," + Convert.ToBase64String(Png);
    }

    private static Result Fail(Code c, string msg) => new(c, msg, null, 0, 0, 0);

    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>ตรวจข้อความจากคำขอ (data URI หรือ base64)</summary>
    public static Result Validate(string? input)
    {
        var s = input?.Trim();
        if (string.IsNullOrEmpty(s)) return Fail(Code.Empty, "กรุณาวาดลายเซ็น");
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            const string prefix = "data:image/png;base64,";
            if (!s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Fail(Code.NotPng, "ลายเซ็นต้องเป็นรูป PNG จากช่องวาดลายเซ็น");
            s = s[prefix.Length..];
        }
        // base64 ยาว 4/3 ของไบต์ — ปฏิเสธก่อนถอด (ไม่จองหน่วยความจำให้ของบวม)
        if (s.Length > (MaxBytes / 3 + 1) * 4 + 8) return Fail(Code.TooLarge, $"ภาพลายเซ็นใหญ่เกิน {MaxBytes / 1024}KB");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(s); }
        catch (FormatException) { return Fail(Code.NotPng, "ข้อมูลลายเซ็นไม่ใช่รูปภาพที่อ่านได้"); }
        return ValidateBytes(bytes);
    }

    /// <summary>ตรวจไบต์ของรูปเต็มรูปแบบ (ทางเข้าเดียวคือ <see cref="Validate"/> · ตอนอ่านไฟล์ที่เก็บแล้วใช้ <see cref="LooksLikeStoredPng"/>)</summary>
    private static Result ValidateBytes(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return Fail(Code.Empty, "กรุณาวาดลายเซ็น");
        if (bytes.Length > MaxBytes) return Fail(Code.TooLarge, $"ภาพลายเซ็นใหญ่เกิน {MaxBytes / 1024}KB");
        if (bytes.Length < 8 + 25 || !bytes.AsSpan(0, 8).SequenceEqual(Signature))
            return Fail(Code.NotPng, "ลายเซ็นต้องเป็นรูป PNG จากช่องวาดลายเซ็น");

        int width = 0, height = 0, bitDepth = 0, colorType = -1, interlace = 0;
        var idat = new MemoryStream();
        var pos = 8;
        var sawIhdr = false;
        var sawIend = false;
        while (pos + 12 <= bytes.Length)
        {
            var len = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos, 4));
            if (len > int.MaxValue || pos + 12 + (long)len > bytes.Length)
                return Fail(Code.BadStructure, "ไฟล์ลายเซ็นเสียหาย");
            var type = System.Text.Encoding.ASCII.GetString(bytes, pos + 4, 4);
            var data = bytes.AsSpan(pos + 8, (int)len);
            if (!sawIhdr)
            {
                if (type != "IHDR" || len != 13) return Fail(Code.BadStructure, "ไฟล์ลายเซ็นเสียหาย");
                width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[..4]), (uint)int.MaxValue);
                height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)), (uint)int.MaxValue);
                bitDepth = data[8]; colorType = data[9]; interlace = data[12];
                sawIhdr = true;
            }
            else if (type == "IDAT") idat.Write(data);
            else if (type == "IEND") { sawIend = true; break; }
            pos += 12 + (int)len;
        }
        if (!sawIhdr || !sawIend || idat.Length == 0) return Fail(Code.BadStructure, "ไฟล์ลายเซ็นเสียหาย");
        if (width < MinWidth || height < MinHeight || width > MaxWidth || height > MaxHeight)
            return Fail(Code.BadDimensions, $"ขนาดภาพลายเซ็นต้องอยู่ระหว่าง {MinWidth}×{MinHeight} ถึง {MaxWidth}×{MaxHeight} พิกเซล");
        var channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 0 };
        if (bitDepth != 8 || channels == 0 || interlace != 0)
            return Fail(Code.Unsupported, "รูปแบบ PNG นี้ไม่รองรับ — กรุณาวาดลายเซ็นในช่องของหน้านี้");

        var stride = width * channels;
        var expected = (long)height * (stride + 1);
        byte[] raw;
        try
        {
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            raw = new byte[expected];
            var read = 0;
            while (read < raw.Length)
            {
                var n = z.Read(raw, read, raw.Length - read);
                if (n == 0) break;
                read += n;
            }
            if (read != raw.Length) return Fail(Code.BadStructure, "ไฟล์ลายเซ็นเสียหาย");
        }
        catch (InvalidDataException) { return Fail(Code.BadStructure, "ไฟล์ลายเซ็นเสียหาย"); }

        var ink = CountInk(raw, width, height, channels, out var minX, out var minY, out var maxX, out var maxY);
        if (ink < 0) return Fail(Code.BadStructure, "ไฟล์ลายเซ็นเสียหาย");
        var extentOk = ink > 0 && (maxX - minX + 1 >= MinInkExtent || maxY - minY + 1 >= MinInkExtent);
        if (ink < MinInkPixels || !extentOk)
            return new Result(Code.Blank, "ยังไม่ได้วาดลายเซ็น (ช่องลายเซ็นว่าง) — กรุณาเซ็นในช่องก่อนกดยืนยัน", null, width, height, Math.Max(ink, 0));
        return new Result(Code.Ok, null, bytes, width, height, ink);
    }

    /// <summary>ตรวจเบาตอน<b>อ่านไฟล์ที่เก็บไว้กลับมาแสดง</b> (ไบต์นั้นผ่าน <see cref="ValidateBytes"/> ตอนรับแล้ว) — magic PNG + ขนาด ≤ <see cref="MaxBytes"/> ·
    /// ไม่ถอด IDAT ซ้ำทุกครั้งที่มีคนเปิดลิงก์ (ฝ่ายค้านรอบ 239 N)</summary>
    public static bool LooksLikeStoredPng([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] byte[]? bytes)
        => bytes != null && bytes.Length >= 8 + 25 && bytes.Length <= MaxBytes && bytes.AsSpan(0, 8).SequenceEqual(Signature);

    /// <summary>ถอด filter ทีละแถว (PNG filter 0–4) แล้วนับหมึก · -1 = filter ไม่รู้จัก</summary>
    private static int CountInk(byte[] raw, int width, int height, int channels,
        out int minX, out int minY, out int maxX, out int maxY)
    {
        minX = int.MaxValue; minY = int.MaxValue; maxX = -1; maxY = -1;
        var stride = width * channels;
        var prev = new byte[stride];
        var cur = new byte[stride];
        var ink = 0;
        for (var y = 0; y < height; y++)
        {
            var off = y * (stride + 1);
            var filter = raw[off];
            Buffer.BlockCopy(raw, off + 1, cur, 0, stride);
            for (var i = 0; i < stride; i++)
            {
                int a = i >= channels ? cur[i - channels] : 0;
                int b = prev[i];
                int c = i >= channels ? prev[i - channels] : 0;
                int add = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => -1,
                };
                if (add < 0) return -1;
                cur[i] = (byte)(cur[i] + add);
            }
            for (var x = 0; x < width; x++)
            {
                var p = x * channels;
                bool isInk = channels switch
                {
                    // ฝ่ายค้าน S1: มีอัลฟา ⇒ หมึก = ทึบพอ **และ** เข้ม (RGBA ขาวทึบทั้งภาพเดิมนับเป็นหมึกทุกจุด = "ลายเซ็น" ว่างผ่าน)
                    4 => cur[p + 3] > 32 && (cur[p] * 299 + cur[p + 1] * 587 + cur[p + 2] * 114) / 1000 < 200,
                    2 => cur[p + 1] > 32 && cur[p] < 200,
                    3 => (cur[p] * 299 + cur[p + 1] * 587 + cur[p + 2] * 114) / 1000 < 200,
                    _ => cur[p] < 200,
                };
                if (!isInk) continue;
                ink++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            (prev, cur) = (cur, prev);
        }
        return ink;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
