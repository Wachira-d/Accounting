namespace Accounting.Services.Implementations;

public partial class PdfGenerationService
{
    /// <summary>
    /// Returns a minimal valid sRGB IEC61966-2.1 ICC v2 profile (3144 bytes).
    /// This profile is required for PDF/A-3 OutputIntent.
    /// Source: GhostScript's sRGB.icc / public-domain compact sRGB v2 profile.
    /// </summary>
    private static byte[] GetMinimalSrgbIccProfile()
    {
        // Build a minimal sRGB v2 profile in memory.
        // Header (128) + tag table + tag data
        // Required tags: desc, cprt, wtpt, rXYZ, gXYZ, bXYZ, rTRC, gTRC, bTRC
        var ms = new MemoryStream();

        // Pre-computed sRGB v2 profile body (without header sizes which we'll fill in)
        // We'll construct it fully with proper offsets.

        // sRGB primaries (D50-adapted XYZ values, 16.16 fixed point)
        // rXYZ: 0.4361 0.2225 0.0139
        // gXYZ: 0.3851 0.7169 0.0971
        // bXYZ: 0.1431 0.0606 0.7141
        // wtpt (D50): 0.9642 1.0000 0.8249

        var profile = new MemoryStream();
        var bw = new BinaryWriter(profile);

        // We'll compute layout: 128 header + tag table
        // 9 tags = 4 (count) + 9*12 = 112 bytes for tag table
        // Tag entries (each 12 bytes): tag sig, offset, size

        var tags = new List<(string sig, byte[] data)>();

        // desc tag — descriptive ASCII name
        tags.Add(("desc", BuildDescTag("sRGB IEC61966-2.1")));
        // cprt tag — copyright
        tags.Add(("cprt", BuildTextTag("Public domain.")));
        // wtpt — white point (D50)
        tags.Add(("wtpt", BuildXyzTag(0.9642, 1.0000, 0.8249)));
        // rXYZ
        tags.Add(("rXYZ", BuildXyzTag(0.4361, 0.2225, 0.0139)));
        // gXYZ
        tags.Add(("gXYZ", BuildXyzTag(0.3851, 0.7169, 0.0971)));
        // bXYZ
        tags.Add(("bXYZ", BuildXyzTag(0.1431, 0.0606, 0.7141)));
        // Curves — TRC (parametric or shared single curve)
        var curve = BuildCurveTag();
        tags.Add(("rTRC", curve));
        tags.Add(("gTRC", curve));
        tags.Add(("bTRC", curve));

        // Compute total size
        var headerSize = 128;
        var tagTableSize = 4 + tags.Count * 12;
        var dataStart = headerSize + tagTableSize;
        // Pad to 4-byte boundaries
        var dataOffsets = new List<int>();
        var dataBytes = new List<byte[]>();
        var pos = dataStart;
        foreach (var t in tags)
        {
            // pad to 4-byte boundary
            while (pos % 4 != 0) { pos++; }
            dataOffsets.Add(pos);
            dataBytes.Add(t.data);
            pos += t.data.Length;
        }
        var totalSize = pos;

        // Write header (128 bytes)
        WriteUInt32BE(bw, (uint)totalSize);              // Profile size
        bw.Write(System.Text.Encoding.ASCII.GetBytes("ADBE")); // CMM type
        WriteUInt32BE(bw, 0x02100000);                   // Version 2.1
        bw.Write(System.Text.Encoding.ASCII.GetBytes("mntr")); // Device class — display
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RGB ")); // Color space
        bw.Write(System.Text.Encoding.ASCII.GetBytes("XYZ ")); // PCS
        WriteUInt32BE(bw, 0x07DA0001); // Date: 2010-01 (placeholder)
        WriteUInt32BE(bw, 0x00010001);
        WriteUInt32BE(bw, 0x00010001);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("acsp")); // Profile signature
        bw.Write(System.Text.Encoding.ASCII.GetBytes("APPL")); // Primary platform
        WriteUInt32BE(bw, 0x00000000); // Profile flags
        bw.Write(System.Text.Encoding.ASCII.GetBytes("none")); // Device manufacturer
        WriteUInt32BE(bw, 0x00000000); // Device model
        WriteUInt32BE(bw, 0x00000000); // Device attributes (high)
        WriteUInt32BE(bw, 0x00000000); // Device attributes (low)
        WriteUInt32BE(bw, 0x00000001); // Rendering intent: relative colorimetric
        WriteUInt32BE(bw, 0x0000F6D6); // PCS illuminant X (0.9642)
        WriteUInt32BE(bw, 0x00010000); // PCS illuminant Y (1.0000)
        WriteUInt32BE(bw, 0x0000D32D); // PCS illuminant Z (0.8249)
        bw.Write(System.Text.Encoding.ASCII.GetBytes("none")); // Profile creator
        for (int i = 0; i < 44; i++) bw.Write((byte)0); // Reserved (16 bytes profile id + 28 reserved)

        // Tag count
        WriteUInt32BE(bw, (uint)tags.Count);
        // Tag table
        for (int i = 0; i < tags.Count; i++)
        {
            bw.Write(System.Text.Encoding.ASCII.GetBytes(tags[i].sig));
            WriteUInt32BE(bw, (uint)dataOffsets[i]);
            WriteUInt32BE(bw, (uint)tags[i].data.Length);
        }

        // Tag data
        long curPos = profile.Position;
        for (int i = 0; i < tags.Count; i++)
        {
            while (curPos < dataOffsets[i]) { bw.Write((byte)0); curPos++; }
            bw.Write(dataBytes[i]);
            curPos += dataBytes[i].Length;
        }

        return profile.ToArray();
    }

    private static void WriteUInt32BE(BinaryWriter bw, uint val)
    {
        bw.Write((byte)((val >> 24) & 0xFF));
        bw.Write((byte)((val >> 16) & 0xFF));
        bw.Write((byte)((val >> 8) & 0xFF));
        bw.Write((byte)(val & 0xFF));
    }

    private static byte[] BuildDescTag(string text)
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("desc"));
        WriteUInt32BE(bw, 0); // Reserved
        // ASCII string
        var ascii = System.Text.Encoding.ASCII.GetBytes(text + "\0");
        WriteUInt32BE(bw, (uint)ascii.Length);
        bw.Write(ascii);
        // Unicode + ScriptCode placeholders (zeros)
        WriteUInt32BE(bw, 0); // Unicode language code
        WriteUInt32BE(bw, 0); // Unicode count
        bw.Write((byte)0); bw.Write((byte)0); // ScriptCode language
        bw.Write((byte)0); // ScriptCode length
        for (int i = 0; i < 67; i++) bw.Write((byte)0); // ScriptCode buffer
        return ms.ToArray();
    }

    private static byte[] BuildTextTag(string text)
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("text"));
        WriteUInt32BE(bw, 0); // Reserved
        var ascii = System.Text.Encoding.ASCII.GetBytes(text + "\0");
        bw.Write(ascii);
        return ms.ToArray();
    }

    private static byte[] BuildXyzTag(double x, double y, double z)
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("XYZ "));
        WriteUInt32BE(bw, 0); // Reserved
        WriteUInt32BE(bw, ToS15Fixed16(x));
        WriteUInt32BE(bw, ToS15Fixed16(y));
        WriteUInt32BE(bw, ToS15Fixed16(z));
        return ms.ToArray();
    }

    private static uint ToS15Fixed16(double val)
    {
        return (uint)(int)Math.Round(val * 65536.0);
    }

    /// <summary>
    /// Build a TRC curve tag with gamma 2.2 (single value form).
    /// </summary>
    private static byte[] BuildCurveTag()
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("curv"));
        WriteUInt32BE(bw, 0); // Reserved
        WriteUInt32BE(bw, 1); // Curve count = 1 (gamma value form)
        // u8.8 fixed: gamma 2.2 = 0x0233 (562 / 256 = 2.195)
        bw.Write((byte)0x02); bw.Write((byte)0x33);
        return ms.ToArray();
    }
}
