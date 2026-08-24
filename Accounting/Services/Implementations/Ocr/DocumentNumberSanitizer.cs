using System.Text.RegularExpressions;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// ด่านตรวจ "เลขที่เอกสาร" ที่ vision model สกัดมา — anti-hallucination guard
/// ตามกฎเหล็ก #1 (validate คำตอบ AI กับข้อมูลจริงก่อน apply)
///
/// <para>ที่มา (บั๊กจริง — ผู้ใช้ส่งภาพมา): บิลค่าไฟ กฟภ. มีเลข 2 ชุดใกล้กัน
/// "เลขที่ (No.) XH0712608004488" กับ "เลขที่ใบแจ้งหนี้ (Invoice No.)
/// 510504578045" — vision model <b>เอาสองเลขมาต่อกัน</b>เป็น
/// "XH0712608004488510504578045" ทั้งที่เอกสารไม่มีเลขนี้อยู่เลยสักบรรทัด.
/// เลขที่เอกสารผิด = ตามใบไม่เจอ + ตัวกันสแกนซ้ำ (dedup ด้วยเลขที่+ยอด)
/// ไม่มีวันจับใบเดิมได้</para>
///
/// <para>หลัก: เลขที่เอกสารที่ถูกต้อง<b>ต้องปรากฏบนเอกสารจริง</b> — ตรวจกับ
/// token ที่ OCR อ่านได้ ถ้าไม่เจอทั้งก้อนแต่ "ผ่าออกเป็นสองเลขที่ต่างก็อยู่บน
/// เอกสารจริง" ได้ = จับได้ว่าถูกต่อกัน → คืนเลขตัวที่เป็นเลขที่เอกสารหลัก
/// (ตัวที่อยู่ใกล้ป้าย "เลขที่/No." ที่ไม่ใช่ "เลขที่ใบแจ้งหนี้/Invoice No.")</para>
/// </summary>
internal static class DocumentNumberSanitizer
{
    /// <summary>token เอกสาร: อักษร/ตัวเลข ต่อด้วยได้ทั้งขีด/ทับ ยาว ≥ 4</summary>
    private static readonly Regex TokenRx = new(
        @"[A-Za-z0-9][A-Za-z0-9\-/]{3,}", RegexOptions.Compiled);

    /// <summary>ป้ายของ "เลขที่เอกสารหลัก" — เลขที่ใบกำกับ/ใบเสร็จ ไม่ใช่เลขอ้างอิงอื่น
    /// (ป้ายรองอย่าง "เลขที่ใบแจ้งหนี้" ก็ขึ้นต้นด้วย "เลขที่" — ผู้เรียกต้องเช็ค
    /// ป้ายรอง<b>ก่อน</b>เสมอ ป้ายหลักจึงตัดสินเฉพาะที่เหลือ)</summary>
    private static readonly Regex PrimaryAnchorRx = new(
        @"เลขที่\s*(?:\(?\s*No\.?\s*\)?)?\s*[:：]?|(?<![A-Za-z])No\.\s*[:：]?",
        RegexOptions.Compiled);

    /// <summary>ป้ายของเลขอ้างอิงรอง (ใบแจ้งหนี้/สัญญา/ผู้ใช้ไฟ ฯลฯ) — เลขพวกนี้
    /// <b>ไม่ใช่</b>เลขที่เอกสาร แต่ vision ชอบหยิบมาปน</summary>
    private static readonly Regex SecondaryAnchorRx = new(
        @"เลขที่ใบแจ้งหนี้|Invoice\s*No|เลขที่สัญญา|Contract\s*No|"
        + @"หมายเลขผู้ใช้|รหัสเครื่องวัด|CA\s*No|Meter",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>ผลการตรวจ: เลขที่หลังแก้ (หรือค่าเดิมถ้าไม่พบปัญหา) + บันทึกว่าแก้อะไร
    /// (null = ไม่ได้แก้) — ผู้เรียกเอา note ไปลง ReasoningTrace ให้ผู้ใช้เห็นได้</summary>
    internal static (string? DocumentNumber, string? CorrectionNote) Sanitize(
        string? extracted, string? rawText)
    {
        if (string.IsNullOrWhiteSpace(extracted)) return (extracted, null);
        var doc = extracted.Trim();

        // ไม่มี raw text ให้เทียบ (เช่น e-Tax XML ที่ field มาจาก XML ตรง ๆ) — ปล่อยผ่าน
        if (string.IsNullOrWhiteSpace(rawText)) return (doc, null);

        // token ทั้งหมดที่ OCR อ่านได้จริงจากกระดาษ
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in TokenRx.Matches(rawText)) tokens.Add(m.Value);

        // เลขที่ปรากฏบนเอกสารจริงทั้งก้อน = ผ่าน (เคสปกติ 99%)
        if (tokens.Contains(doc)) return (doc, null);

        // ── ตรวจ "สองเลขถูกต่อกัน" ──
        // ผ่าที่ทุกตำแหน่ง (สั้นสุดข้างละ 4) — ทั้งซ้ายและขวาต้องเป็น token ที่
        // อยู่บนเอกสารจริงทั้งคู่ถึงจะนับ (กันผ่ามั่วบนเลขที่แค่ OCR อ่านเพี้ยน)
        for (var i = 4; i <= doc.Length - 4; i++)
        {
            var left = doc[..i];
            var right = doc[i..];
            if (!tokens.Contains(left) || !tokens.Contains(right)) continue;

            var chosen = PickPrimary(left, right, rawText);
            return (chosen,
                $"เลขที่เอกสารที่สกัดมา \"{doc}\" ไม่มีอยู่บนเอกสาร — เป็นเลข 2 ชุด"
                + $" (\"{left}\" + \"{right}\") ถูกต่อกัน ระบบเลือกใช้ \"{chosen}\""
                + " ซึ่งเป็นเลขที่เอกสารหลัก");
        }

        // ไม่ใช่การต่อกัน — อาจเป็น OCR อ่านตัวอักษรเพี้ยน (O↔0, l↔1) ซึ่งเทียบ
        // ตรง ๆ ไม่เจอเป็นปกติ: ปล่อยผ่าน ไม่เดาแก้ (แก้มั่วแย่กว่าปล่อย —
        // ผู้ใช้ยังเห็นและแก้เองได้ในฟอร์ม)
        return (doc, null);
    }

    /// <summary>เลือกว่าซีก left/right ตัวไหนคือเลขที่เอกสารหลัก:
    /// ตัวที่อยู่หลังป้าย "เลขที่/No." ธรรมดาชนะตัวที่อยู่หลังป้ายรอง
    /// ("เลขที่ใบแจ้งหนี้/Invoice No./เลขที่สัญญา") — เสมอกันเอาตัวซ้าย
    /// (vision อ่านบน→ล่าง เลขที่หลักมาก่อนเสมอบนฟอร์มไทย)</summary>
    private static string PickPrimary(string left, string right, string rawText)
    {
        var leftScore = AnchorScore(left, rawText);
        var rightScore = AnchorScore(right, rawText);
        return rightScore > leftScore ? right : left;
    }

    /// <summary>คะแนนความเป็น "เลขที่เอกสารหลัก" ของ token ตามป้ายที่อยู่ข้างหน้า
    /// ภายใน 30 ตัวอักษร: ป้ายหลัก +2 · ไม่มีป้าย 0 · ป้ายรอง −2</summary>
    private static int AnchorScore(string token, string rawText)
    {
        // token โผล่ได้หลายตำแหน่ง (เช่นเลขซ้ำในตาราง) — เอาคะแนนดีสุด:
        // ขอแค่มีจุดเดียวที่อยู่หลังป้ายหลัก ก็ถือว่าเป็นเลขที่เอกสารหลักได้
        var best = int.MinValue;
        var idx = -1;
        while ((idx = rawText.IndexOf(token, idx + 1, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var start = Math.Max(0, idx - 30);
            var before = rawText[start..idx];
            // ป้ายรองเช็คก่อนเสมอ — "เลขที่ใบแจ้งหนี้" ก็ขึ้นต้นด้วย "เลขที่"
            // ถ้าเช็คป้ายหลักก่อน เลขใบแจ้งหนี้จะได้คะแนนบวกผิด ๆ
            var score = SecondaryAnchorRx.IsMatch(before) ? -2
                : PrimaryAnchorRx.IsMatch(before) ? 2 : 0;
            best = Math.Max(best, score);
            if (best == 2) break;
        }
        return best == int.MinValue ? 0 : best;
    }
}
