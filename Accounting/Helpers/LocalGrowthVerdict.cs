namespace Accounting.Helpers;

/// <summary>คำตัดสินว่า "นักเรียนโตจริง" หรือ "ระบบแค่เงียบลง"</summary>
public enum LocalGrowthState
{
    /// <summary>ข้อมูลไม่พอจะตัดสิน — <b>ไม่ใช่</b> "ปกติ" (G3: ไม่รู้ต้องเป็นค่าใน enum)</summary>
    NotEnoughData = 0,

    /// <summary><b>ตาบอด</b> — ไม่มี label ที่มนุษย์ตั้งใจให้เลยใน 30 วัน ⇒ ตัวเลข
    /// ความแม่นทุกตัวบนแถวนี้<b>ยืนยันไม่ได้</b> ต่อให้เป็น 100%</summary>
    Blind = 1,

    /// <summary><b>โตจริง</b> — นักเรียนตอบได้ครอบคลุม + แม่น + ยังมีคนยืนยันแบบตั้งใจ</summary>
    Growing = 2,

    /// <summary><b>เงียบลง</b> — เรียกครูน้อยลงแต่นักเรียนก็ไม่ได้ตอบแทน
    /// (ครอบคลุมต่ำ) ⇒ ระบบหยุดตอบ ไม่ใช่โตขึ้น — <b>อาการที่อันตรายที่สุด</b>
    /// เพราะกราฟ <c>UsedAi</c> ลดลงเหมือนกันเป๊ะกับตอนโตจริง</summary>
    GoingQuiet = 3,

    /// <summary><b>ถอยหลัง</b> — นักเรียนตอบเยอะแต่ตอบผิดบ่อยกว่าเกณฑ์</summary>
    Regressing = 4,

    /// <summary>ทรงตัว — ยังพึ่งครูอยู่มาก ยังไม่โตและยังไม่เงียบ</summary>
    Stalled = 5,
}

/// <summary>ผลตัดสิน + เหตุผลภาษาไทยที่เอาไปแสดงบนหน้าแอดมินได้ตรง ๆ</summary>
public readonly record struct LocalGrowthJudgement(LocalGrowthState State, string Reason);

/// <summary>
/// **ตัวชี้วัดคู่: แยก "local โตจริง" ออกจาก "ระบบเงียบลง"** (pure, ไม่มี I/O)
///
/// ═══ ปัญหาที่แก้ ═══
/// <para><c>CLAUDE.md</c> กฎเหล็ก #1 ข้อ 6 ให้ใช้ <c>&lt;Feature&gt;UsedAi</c> ที่ลดลงเป็น
/// ตัวชี้วัดความพร้อม — แต่ตัวเลขนั้น<b>ลดลงได้สองสาเหตุที่ตรงข้ามกัน</b>:</para>
/// <list type="number">
/// <item>นักเรียนเก่งขึ้นจน short-circuit ก่อนถึงครู (<b>ดี</b>)</item>
/// <item>ระบบเลิกถาม — ด่านปิด · งบหมด · provider ถูกปิด · ไม่มีใครใช้ feature นั้น
///   (<b>แย่</b> และเงียบสนิท)</item>
/// </list>
/// <para>ตัวเลขเดียวแยกสองอย่างนี้ไม่ได้ ⇒ ต้องอ่าน <b>3 ตัวพร้อมกัน</b>
/// (<c>DECISION_DOCTRINE</c> §3.2): <b>ความครอบคลุมของนักเรียน</b> ·
/// <b>ความแม่นของนักเรียน</b> · <b>จำนวน label ที่มนุษย์ตั้งใจให้</b></para>
///
/// <para><b>ทำไมต้องมีชั้น <see cref="LocalGrowthState.Blind"/></b> — ความแม่นคำนวณจาก
/// "คำตอบที่ผู้ใช้เลือก" · ถ้าไม่มี label ที่ตั้งใจเลย (มีแต่การกดผ่าน) ตัวหารยังโตได้
/// และความแม่นยังขึ้นได้ ทั้งที่<b>ไม่มีใครตรวจอะไรเลย</b> ⇒ สถานะ "Healthy" ที่เชื่อไม่ได้
/// (ราก "คลังเอียง" §3.1) · เกณฑ์นี้ต้องมาก่อนเกณฑ์ความแม่นเสมอ</para>
///
/// <para>ตัวตัดสินนี้<b>ไม่เปลี่ยน</b> <c>LocalModelHealthStatus</c> เดิม (ซึ่งตอบคำถาม
/// "นักเรียนสู้ครูได้ไหม") — คนละคำถามกัน จึงเป็นคนละค่า ไม่ใช่ค่าเดียวที่ถูกยืม</para>
/// </summary>
public static class LocalGrowthVerdict
{
    /// <summary>ต่ำกว่านี้ = ยังไม่มีอะไรให้ตัดสิน (ตัวเดียวกับเกณฑ์ <c>InsufficientData</c>
    /// ของ <c>AiFeedbackTrainingJob</c> — ห้ามตั้งเลขคนละชุดสองที่)</summary>
    public const int MinSamples = 30;

    /// <summary>ความครอบคลุมที่ถือว่า "นักเรียนตอบแทนได้จริง" — เลขเดียวกับเกณฑ์ที่
    /// job ใช้แนะให้ลด sampling อยู่แล้ว (0.80) เพื่อไม่ให้มีสองความหมายของคำว่า "พอ"</summary>
    public const decimal GrowingCoverage = 0.80m;

    /// <summary>ต่ำกว่านี้ = นักเรียนแทบไม่ได้ตอบ ⇒ ถ้าครูก็ไม่ถูกเรียกด้วย
    /// แปลว่าระบบเงียบ ไม่ใช่โต</summary>
    public const decimal QuietCoverage = 0.30m;

    /// <summary>สัดส่วนครั้งที่ยิงครูจริง ต่อแถวทั้งหมด — ต่ำกว่านี้ถือว่า "แทบไม่ถามครูแล้ว"</summary>
    public const decimal QuietAiShare = 0.10m;

    /// <summary>ความแม่นขั้นต่ำของนักเรียนก่อนจะเรียกว่าโต · ต่ำกว่า
    /// <see cref="RegressingAccuracy"/> = ถอยหลัง</summary>
    public const decimal GrowingAccuracy = 0.85m;

    /// <summary>ต่ำกว่านี้ทั้งที่ตอบเยอะ = ถอยหลัง (ยิ่งตอบยิ่งผิด)</summary>
    public const decimal RegressingAccuracy = 0.70m;

    /// <param name="samples30d">แถว feedback ที่มี label ของ 30 วัน</param>
    /// <param name="localSamples30d">แถวที่<b>นักเรียนตอบได้</b></param>
    /// <param name="aiSamples30d">แถวที่<b>ยิง provider จริง</b> (<c>ProviderUsed != None</c>)</param>
    /// <param name="localAccuracy30d">ความแม่นของนักเรียน (หารด้วย <paramref name="localSamples30d"/>)</param>
    /// <param name="explicitLabels30d">label ที่มนุษย์ตั้งใจให้ (ไม่นับการกดผ่าน)</param>
    public static LocalGrowthJudgement Judge(
        int samples30d, int localSamples30d, int aiSamples30d,
        decimal localAccuracy30d, int explicitLabels30d)
    {
        if (samples30d < MinSamples)
            return new(LocalGrowthState.NotEnoughData,
                $"ตัวอย่างที่มี label เพียง {samples30d} แถว (ต้องมี ≥ {MinSamples}) — ยังตัดสินไม่ได้");

        // ต้องมาก่อนทุกเกณฑ์ความแม่น: ไม่มีคนตรวจ = ตัวเลขความแม่นยืนยันไม่ได้
        if (explicitLabels30d <= 0)
            return new(LocalGrowthState.Blind,
                $"ไม่มีการยืนยันแบบตั้งใจเลยใน 30 วัน ({samples30d} แถวเป็นการกดผ่าน/คำตอบของระบบเอง) "
                + "— ความแม่นที่วัดได้ยืนยันไม่ได้ ต้องสุ่มให้คนตรวจก่อน");

        var coverage = samples30d > 0 ? (decimal)localSamples30d / samples30d : 0m;
        var aiShare = samples30d > 0 ? (decimal)aiSamples30d / samples30d : 0m;

        if (aiShare < QuietAiShare && coverage < QuietCoverage)
            return new(LocalGrowthState.GoingQuiet,
                $"เรียกครูเพียง {aiShare:P0} ของแถว แต่นักเรียนก็ตอบได้แค่ {coverage:P0} "
                + "— ระบบหยุดตอบ ไม่ใช่โตขึ้น (ตรวจด่าน/งบ/สถานะ provider)");

        if (localSamples30d >= MinSamples && localAccuracy30d < RegressingAccuracy)
            return new(LocalGrowthState.Regressing,
                $"นักเรียนตอบ {localSamples30d} แถวแต่แม่นเพียง {localAccuracy30d:P0} "
                + $"(ต่ำกว่า {RegressingAccuracy:P0}) — ยิ่งตอบยิ่งพาไปผิด");

        if (coverage >= GrowingCoverage && localAccuracy30d >= GrowingAccuracy)
            return new(LocalGrowthState.Growing,
                $"นักเรียนตอบได้ {coverage:P0} ของเคส แม่น {localAccuracy30d:P0} "
                + $"และยังมีคนยืนยันแบบตั้งใจ {explicitLabels30d} ครั้ง — โตจริง");

        return new(LocalGrowthState.Stalled,
            $"นักเรียนตอบได้ {coverage:P0} (แม่น {localAccuracy30d:P0}) · ยังเรียกครู {aiShare:P0} "
            + "— ทรงตัว ยังพึ่งครูอยู่");
    }

    /// <summary>ป้ายสั้นภาษาไทยสำหรับหน้าแอดมิน — ห้ามให้หน้าเว็บแต่งเอง (สำเนาที่สอง)</summary>
    public static string Label(LocalGrowthState state) => state switch
    {
        LocalGrowthState.NotEnoughData => "ข้อมูลไม่พอ",
        LocalGrowthState.Blind => "ตาบอด — ไม่มีคนตรวจ",
        LocalGrowthState.Growing => "โตจริง",
        LocalGrowthState.GoingQuiet => "เงียบลง (ไม่ใช่โต)",
        LocalGrowthState.Regressing => "ถอยหลัง",
        LocalGrowthState.Stalled => "ทรงตัว",
        _ => "ไม่ทราบ",
    };
}
