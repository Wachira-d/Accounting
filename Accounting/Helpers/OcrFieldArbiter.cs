namespace Accounting.Helpers;

/// <summary>ใครเป็นคนให้ค่านี้ — เรียงจาก<b>น่าเชื่อน้อยสุดไปมากสุด</b>
///
/// <para>ลำดับความน่าเชื่อเป็น <b>ข้อมูล</b> (ค่าของ enum + <see cref="OcrFieldArbiter.Precedence"/>)
/// ไม่ใช่ "ลำดับบรรทัดในเมธอด" — นี่คือหัวใจของการเลิก "ใครมาหลังชนะ"</para></summary>
public enum OcrFieldSource
{
    /// <summary>ไม่รู้ที่มา (ค่าที่ค้างมาจากรุ่นก่อนมีระบบนี้)</summary>
    Unknown = 0,
    /// <summary>เดาแบบมีเหตุผลเมื่อไม่มีอะไรเลย — น่าเชื่อน้อยที่สุด</summary>
    Guess = 10,
    /// <summary>AI ภายนอกตอบ (ครู)</summary>
    Ai = 20,
    /// <summary>กติกา/ค่าตั้งต้นของระบบ (VAT 7% · เครดิตเทอมของบริษัท)</summary>
    Rule = 30,
    /// <summary>ประวัติของผู้ขายรายนี้ / ทะเบียนราชการ — ถูกเฉพาะเมื่อ<b>คีย์ที่ใช้ค้นถูก</b></summary>
    VendorHistory = 40,
    /// <summary>ตารางกฎหมาย (อัตรา ท.ป.4/2528 · VAT 7/107 · เพดานตามกฎกระทรวง)
    /// ที่ผูกกับ<b>สิ่งที่อ่านได้จากกระดาษใบนี้</b> เช่น หมวดรายจ่าย/ประเภทเงินได้ ม.40
    ///
    /// <para>ต่างจาก <see cref="Rule"/> ซึ่งเป็น "ค่าตั้งต้นของระบบ" (VAT 7% · เครดิตเทอม
    /// ของบริษัท) ที่ใช้ได้เพราะ<b>ไม่มีอะไรดีกว่า</b> — ชั้นนี้ตัวเลขมาจากกฎหมายจึงผิดไม่ได้
    /// จุดอ่อนอยู่ที่ "หมวดที่อ่านมาจากกระดาษ" เท่านั้น ⇒ อยู่<b>เหนือ</b>นิสัยของผู้ขาย
    /// (<see cref="VendorHistory"/>) แต่<b>ใต้</b>สิ่งที่อ่านมาตรง ๆ จากใบ</para>
    ///
    /// <para>⚠️ เพิ่มในรอบ 184 (D-4 ขั้นที่ 1) แบบ<b>เติมอย่างเดียว</b>: ยังไม่มีผู้เสนอราย
    /// เดิมรายไหนใช้ค่านี้ ⇒ อันดับของทุกช่องที่มีอยู่<b>ไม่ขยับแม้แต่ช่องเดียว</b>
    /// (กฎเหล็ก #4 H: สลับลำดับชั้นต้องมี golden ก่อน/หลัง · การ<b>เติมชั้นใหม่</b>
    /// ที่ไม่มีใครเคยเสนอ ไม่ใช่การสลับ)</para></summary>
    Statute = 45,
    /// <summary>นักเรียน (local distillation model) ตอบ</summary>
    Student = 50,
    /// <summary>แพตเทิร์นตำแหน่งที่เรียนไว้จากใบของผู้ขายรายนี้</summary>
    LearnedPattern = 60,
    /// <summary>engine อ่านมาตรง ๆ (Tesseract / python / PDF text-layer)</summary>
    Engine = 70,
    /// <summary>อ่านจาก<b>ป้ายกำกับบนกระดาษ</b> ("เลขประจำตัวผู้เสียภาษี" นำหน้าเลข)</summary>
    PaperLabel = 80,
    /// <summary>Azure DI ที่รายงานความมั่นใจของ<b>ช่องนั้น</b> ≥ 0.85</summary>
    AzureHighConfidence = 90,
    /// <summary>e-Tax XML ที่มีลายเซ็นดิจิทัล — ความจริงตามกฎหมาย ไม่ใช่ผลอ่าน</summary>
    EtaxXml = 100,
    /// <summary><b>ผู้ใช้ยืนยัน/แก้เอง</b> — ชั้นบนสุด: คนที่ถือกระดาษอยู่ตรงหน้าเป็น
    /// ผู้ตัดสินสุดท้าย ไม่มีผู้เสนออัตโนมัติรายใดทับได้ (**one-way governor** · D-7)
    ///
    /// <para>⚠️ ก่อนรอบ 184 ลำดับชั้นฝั่งเซิร์ฟเวอร์<b>ไม่มีที่ยืนให้ "ผู้ใช้" เลย</b>
    /// ขณะที่ฝั่งหน้าเว็บมีกติกา <c>dataset.userTouched</c> = "ค่าผู้ใช้ชนะเสมอ"
    /// (<c>documents.html</c> · <c>_canAutoFill</c>) ⇒ สองฝั่งพูดคนละภาษา และเส้นที่
    /// อยากบันทึกว่า "ค่านี้คนยืนยันแล้ว" ต้องไป<b>ยืม</b> <see cref="PaperLabel"/>
    /// ซึ่งแปลว่าคนละเรื่อง (G3b)</para>
    ///
    /// <para>เพิ่มแบบ<b>เติมอย่างเดียว</b> เช่นเดียวกับ <see cref="Statute"/>:
    /// ยังไม่มีผู้เสนอรายเดิมรายใดใช้ค่านี้ ⇒ อันดับของทุกช่องที่มีอยู่ไม่ขยับ</para></summary>
    UserConfirmed = 110,
}

/// <summary>ค่าที่แหล่งหนึ่งเสนอสำหรับช่องหนึ่ง</summary>
/// <param name="Field">ชื่อช่องตาม <see cref="OcrFieldKeys"/></param>
/// <param name="Value">ค่าที่เสนอ (แปลงเป็นข้อความแล้ว)</param>
/// <param name="Source">ใครเสนอ</param>
/// <param name="Confidence">ความมั่นใจของแหล่งนั้น 0–1</param>
/// <param name="Evidence">หลักฐาน — ข้อความรอบ ๆ ที่ตัดมา / ชื่อ pattern / เลขบรรทัด</param>
public sealed record OcrFieldCandidate(
    string Field, string? Value, OcrFieldSource Source, decimal Confidence, string? Evidence = null);

/// <summary>ผลตัดสินของช่องหนึ่ง + ตัวเลือกที่แพ้ (ไว้ให้ผู้ใช้สลับได้/ไว้ไล่ย้อน)</summary>
public sealed record OcrFieldDecision(
    string Field, string? Value, OcrFieldSource Source, decimal Confidence, string? Evidence,
    IReadOnlyList<OcrFieldCandidate> Alternatives);

/// <summary>
/// **ตัวตัดสินค่าต่อช่องของผลสแกน** (pure, ไม่มี I/O) — สถาปัตยกรรมเป้าหมาย D1
///
/// ═══ ปัญหาที่แก้ ═══
/// วันนี้มี 6+ แหล่งเขียนทับ <c>extractedData.X</c> ตามลำดับที่เขียนไว้ในเมธอดเดียว
/// (engine → known-good → smart extractor → learned pattern → zone → learner → AI → DBD)
/// ⇒ ผลลัพธ์ขึ้นกับ<b>ลำดับบรรทัด</b> ไม่ใช่คุณภาพของหลักฐาน และไล่ย้อนไม่ได้ว่าใครใส่
/// — บั๊กจริงที่เกิดจากคลาสนี้มีอย่างน้อยสามตัวในไฟล์ CLAUDE.md: ทะเบียน DBD ทับชื่อ
/// ที่อ่านถูกแล้วเพราะคีย์ผิด · known-good ทับเลขที่เอกสารของใบใหม่ด้วยของใบเก่า ·
/// สามค่าที่ "ลงตัว" ทับยอดที่ engine อ่านถูก
///
/// ═══ กติกาการตัดสิน (ตามลำดับ) ═══
/// <list type="number">
/// <item><b>แหล่งที่น่าเชื่อกว่าชนะเสมอ</b> — ต่อให้แหล่งที่ต่ำกว่ามั่นใจกว่าก็ตาม
///   (นี่คือจุดที่ต่างจาก "ใครมั่นใจกว่าชนะ" ซึ่งทำให้ค่าที่แต่งขึ้นพร้อม confidence 0.95
///   ชนะค่าที่อ่านมาได้จริง)</item>
/// <item>แหล่งเดียวกัน → <b>มั่นใจกว่าชนะ</b></item>
/// <item>เท่ากันทุกอย่าง → <b>ตัวที่เสนอมาก่อนชนะ</b> (เสถียร ไม่ขึ้นกับการเรียงใหม่)</item>
/// </list>
///
/// <para><b>ไม่แต่งค่า</b>: ผู้เสนอที่ส่งค่าว่างมาถูกตัดทิ้ง — ไม่มีผู้เสนอเลย = ไม่มีคำตอบ
/// (คืน <c>null</c>) ไม่ใช่คืนค่าเปล่า</para>
/// </summary>
public static class OcrFieldArbiter
{
    /// <summary>ลำดับความน่าเชื่อจากมากไปน้อย — <b>ประกาศไว้เป็นข้อมูล</b>
    /// เปลี่ยนลำดับที่นี่ที่เดียว ไม่ต้องไปสลับบรรทัดในไปป์ไลน์</summary>
    public static readonly IReadOnlyList<OcrFieldSource> Precedence = new[]
    {
        OcrFieldSource.UserConfirmed,
        OcrFieldSource.EtaxXml,
        OcrFieldSource.AzureHighConfidence,
        OcrFieldSource.PaperLabel,
        OcrFieldSource.Engine,
        OcrFieldSource.LearnedPattern,
        OcrFieldSource.Student,
        OcrFieldSource.Statute,
        OcrFieldSource.VendorHistory,
        OcrFieldSource.Rule,
        OcrFieldSource.Ai,
        OcrFieldSource.Guess,
        OcrFieldSource.Unknown,
    };

    /// <summary>อันดับความน่าเชื่อ (สูง = น่าเชื่อกว่า) — <b>คำนวณจาก
    /// <see cref="Precedence"/> เท่านั้น</b> ไม่ใช่จากค่าตัวเลขของ enum
    ///
    /// <para>⚠️ ถ้าปล่อยให้เรียงด้วย <c>(int)source</c> ตรง ๆ ตาราง <c>Precedence</c>
    /// จะกลายเป็น "ของที่ประกาศไว้แต่ไม่มีใครใช้" ทันทีที่ใครเพิ่มแหล่งใหม่ด้วยเลข
    /// ที่ไม่ตรงลำดับ — defect class ที่เรพนี้เจอบ่อยที่สุด. แหล่งที่ไม่อยู่ในตาราง
    /// ได้อันดับต่ำสุด (ไม่ใช่ throw — สแกนต้องได้คำตอบเสมอตามกฎเหล็ก #3)</para></summary>
    public static int Rank(OcrFieldSource source)
    {
        var idx = -1;
        for (var i = 0; i < Precedence.Count; i++)
            if (Precedence[i] == source) { idx = i; break; }
        return idx < 0 ? int.MinValue : Precedence.Count - idx;
    }

    /// <summary>ตัดสินช่องเดียว · คืน <c>null</c> เมื่อไม่มีผู้เสนอที่มีค่า</summary>
    public static OcrFieldDecision? Decide(string field, IEnumerable<OcrFieldCandidate> candidates)
    {
        var usable = candidates
            .Where(c => string.Equals(c.Field, field, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(c.Value))
            .ToList();
        if (usable.Count == 0) return null;

        // เรียงด้วย Rank() ที่อ่านจากตาราง Precedence ⇒ เปลี่ยนลำดับความน่าเชื่อ
        // ทำที่ตารางที่เดียว ไม่ต้องแตะโค้ดตรงนี้และไม่ต้องระวังค่าตัวเลขของ enum
        var ranked = usable
            .Select((c, i) => (c, i))
            .OrderByDescending(x => Rank(x.c.Source))
            .ThenByDescending(x => x.c.Confidence)
            .ThenBy(x => x.i)                    // เสถียร: เสนอก่อนชนะ
            .ToList();

        var win = ranked[0].c;
        return new OcrFieldDecision(field, win.Value!.Trim(), win.Source, win.Confidence, win.Evidence,
            ranked.Skip(1).Select(x => x.c).ToList());
    }

    /// <summary>ตัดสินทุกช่องที่มีผู้เสนอ — เรียงตามชื่อช่องเพื่อให้ผลคงที่</summary>
    public static IReadOnlyList<OcrFieldDecision> DecideAll(IEnumerable<OcrFieldCandidate> candidates)
    {
        var all = candidates.Where(c => !string.IsNullOrWhiteSpace(c.Value)).ToList();
        return all.Select(c => c.Field).Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => Decide(f, all))
            .Where(d => d != null)
            .Select(d => d!)
            .ToList();
    }

    /// <summary>คำอธิบายภาษาไทยของแหล่ง — ใช้ทั้งหน้า review และ ProcessingNotes
    /// (ห้ามให้แต่ละหน้าจอแต่งคำเอง = สำเนามือชุดที่สอง)</summary>
    public static string SourceLabel(OcrFieldSource source) => source switch
    {
        OcrFieldSource.UserConfirmed => "ผู้ใช้ยืนยัน/แก้เอง",
        OcrFieldSource.EtaxXml => "e-Tax XML (มีลายเซ็นดิจิทัล)",
        OcrFieldSource.AzureHighConfidence => "Azure DI (มั่นใจสูง)",
        OcrFieldSource.PaperLabel => "ป้ายกำกับบนกระดาษ",
        OcrFieldSource.Engine => "ผลอ่านของ engine",
        OcrFieldSource.LearnedPattern => "แพตเทิร์นที่เรียนจากใบของผู้ขายรายนี้",
        OcrFieldSource.Student => "ระบบเรียนรู้แล้ว (local model)",
        OcrFieldSource.Statute => "ตารางกฎหมาย (อัตรา/เพดานตามประมวลรัษฎากร)",
        OcrFieldSource.VendorHistory => "ประวัติผู้ขาย/ทะเบียนราชการ",
        OcrFieldSource.Rule => "กติกาของระบบ",
        OcrFieldSource.Ai => "AI แนะนำ",
        OcrFieldSource.Guess => "ค่าเดา — ต้องตรวจสอบ",
        _ => "ไม่ทราบที่มา",
    };
}
