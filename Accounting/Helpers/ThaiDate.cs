namespace Accounting.Helpers;

/// <summary>Helper จัดการ "วันที่ตามปฏิทินไทย" (Asia/Bangkok) ให้ unambiguous
/// ทั่วระบบ. ปัญหาที่แก้: DocumentDate/PaymentDate ฯลฯ เป็น "calendar date"
/// (สนใจแค่ วัน/เดือน/ปี ไม่สนเวลา) แต่ถูกเก็บใน Postgres timestamptz เป็น UTC.
/// เมื่อ frontend ส่ง "2026-06-02" แล้ว parse เป็น local midnight → convert UTC
/// = "2026-06-01 17:00 UTC" (shift ถอยหลัง 1 วัน). ทำให้:
///   • เลขเอกสาร format UTC ดิบ = ผิดวัน
///   • ภพ.30 boundary: ใบ 01/06 ไทย เก็บ 31/05 17:00 UTC → ตกงวด มิ.ย.
///
/// วิธีแก้: normalize ทุก calendar date → "วันที่ไทย ณ 00:00 UTC" (anchor
/// midnight UTC ของวันตามปฏิทินไทย) ก่อนเก็บ. ทำให้ raw-UTC date = Bangkok
/// date = วันที่จริง — ทุก comparison/format ตรงกันไม่มี ambiguity.</summary>
public static class ThaiDate
{
    private static TimeZoneInfo Bkk()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "SE Asia Standard Time" : "Asia/Bangkok");
        }
        catch { return TimeZoneInfo.CreateCustomTimeZone("BKK", TimeSpan.FromHours(7), "BKK", "BKK"); }
    }

    /// <summary>คืนวันที่ตามปฏิทินไทยของ dt (ไม่ว่า dt จะ Kind ไหน) ในรูป
    /// DateTime ณ 00:00:00 Kind=Utc → เก็บลง timestamptz แล้ว round-trip
    /// กลับมาเป็นวันเดิมเสมอ + format/compare ตรงทุก layer.</summary>
    public static DateTime CalendarDateUtc(DateTime dt)
    {
        // Era guard — storage ต้องเป็น ค.ศ. เสมอ (ช่วงสมเหตุผล ~1900–2400).
        // ปีนอกช่วงแปลว่ามีการปน พ.ศ./ค.ศ. หลุดมา: 2569 (พ.ศ. ไม่ถูกแปลง) หรือ
        // 1483 (ค.ศ. ถูกลบ 543 เกิน) → ปรับกลับให้อยู่ในช่วง ค.ศ. กัน bug
        // ทุกทาง (create/update/import/OCR/auto-receipt) เขียนปีเพี้ยนลง DB
        // ซึ่งทำให้ aging/เลขเอกสาร (yyyyMMdd) ผิด ~543 ปี. AddYears กัน Feb29.
        if (dt.Year > 2400) dt = dt.AddYears(-543);
        else if (dt.Year < 1900) dt = dt.AddYears(543);
        // ตีความ dt ว่าเป็น instant UTC (Unspecified/Local → treat เป็น UTC
        // เพื่อไม่ double-shift; midnight Unspecified → Bangkok 07:00 = วันเดิม)
        var utc = dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        var bkk = TimeZoneInfo.ConvertTimeFromUtc(utc, Bkk());
        return new DateTime(bkk.Year, bkk.Month, bkk.Day, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>nullable overload.</summary>
    public static DateTime? CalendarDateUtc(DateTime? dt)
        => dt.HasValue ? CalendarDateUtc(dt.Value) : null;

    /// <summary>yyyyMMdd ของวันที่ไทย — ใช้กับเลขเอกสาร.</summary>
    public static string YyyyMmDd(DateTime dt)
        => CalendarDateUtc(dt).ToString("yyyyMMdd");

    /// <summary>วันที่ไทยแบบอ่านออก <c>dd/MM/พ.ศ.</c> — ใช้ในข้อความที่ผู้ใช้/
    /// ผู้สอบบัญชีอ่าน (หมายเหตุบนเอกสาร ฯลฯ)
    ///
    /// <para>ระบุ <see cref="System.Globalization.CultureInfo.InvariantCulture"/> เสมอ:
    /// ถ้า process ตั้ง culture th-TH ปฏิทินเริ่มต้นเป็นพุทธศักราชอยู่แล้ว ⇒
    /// <c>dd/MM/yyyy</c> จะได้ปี พ.ศ. มาเอง แล้วการ +543 ที่นี่จะกลายเป็น
    /// <b>บวกซ้ำ</b> (2026 → 3112) เงียบ ๆ</para></summary>
    public static string ToThaiDisplayString(DateTime dt)
    {
        var d = CalendarDateUtc(dt);
        return d.ToString("dd/MM/", System.Globalization.CultureInfo.InvariantCulture)
             + (d.Year + 543).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// แปลง "ปีที่อ่านได้จากกระดาษ" เป็นปี ค.ศ. — <b>ตัวแปลงกลางตัวเดียว</b>
    ///
    /// <para>รองรับ 4 รูปแบบที่พบบนเอกสารไทยจริง:
    /// <list type="bullet">
    /// <item><c>2569</c> → พ.ศ. 4 หลัก → 2026</item>
    /// <item><c>2026</c> → ค.ศ. 4 หลัก → คงเดิม</item>
    /// <item><c>69</c> → พ.ศ. ย่อ (2500+69 = 2569) → 2026</item>
    /// <item><c>26</c> → ค.ศ. ย่อ → 2026</item>
    /// </list></para>
    ///
    /// <para>⚠️ ที่มา: กติกาเดียวกันนี้ถูกเขียนซ้ำในเรพด้วย **เกณฑ์ที่ต่างกัน 4 แบบ**
    /// (<c>&gt; 2500</c> ใน ParseThaiDocument · <c>&gt;= 2400</c> ใน EnrichFromRawText ·
    /// <c>&gt; 2400</c> ใน CalendarDateUtc · <c>&gt; currentYear + 10</c> ในหน้าแอดมิน)
    /// ⇒ เอกสารใบเดียวกันที่เข้าคนละเส้นทาง OCR ลงคนละปีได้</para>
    ///
    /// <para>เส้นแบ่ง 2 หลัก: ค่า &gt;= <paramref name="shortBeFloor"/> ถือเป็น พ.ศ. ย่อ
    /// (ค่าเริ่มต้น 60 = พ.ศ. 2560/ค.ศ. 2017 ขึ้นไป) — เอกสารบัญชีที่สแกนเข้าระบบ
    /// ไม่ควรเก่ากว่านั้น</para>
    /// </summary>
    public static int NormalizeYear(int year, int shortBeFloor = 60) => year switch
    {
        >= 2400 => year - 543,        // พ.ศ. 4 หลัก
        >= 1900 => year,              // ค.ศ. 4 หลัก
        >= 100 => year,               // 3 หลัก — ผิดปกติ ปล่อยผ่านให้ผู้เรียกตรวจเอง
        _ when year >= shortBeFloor => 2500 + year - 543,   // พ.ศ. ย่อ (69 → 2026)
        _ => 2000 + year,             // ค.ศ. ย่อ (26 → 2026)
    };

    /// <summary>
    /// แปลงข้อความวันที่จาก OCR/engine เป็น <see cref="DateTime"/> โดย
    /// <b>ไม่ขึ้นกับ culture ของ process</b>
    ///
    /// <para>⚠️ ที่มา (ผลตรวจ OCR 2026-09-06 · T2-19): เส้น OCR เรียก
    /// <c>DateTime.TryParse(value, out var d)</c> เปล่า ๆ 4 จุด ⇒ ผลลัพธ์
    /// ขึ้นกับ culture ของคอนเทนเนอร์ที่รันจริง — ถ้าตั้ง th-TH ปฏิทินเริ่มต้น
    /// เป็น<b>พุทธศักราช</b> ⇒ ISO "2026-09-05" ถูกอ่านเป็น พ.ศ. 2026 =
    /// ค.ศ. 1483 ⇒ <c>ValidateAndNormalizeDate</c> เห็น <c>year &lt; 1990</c>
    /// แล้ว<b>ล้างค่าทิ้ง</b> ⇒ ทุกใบจาก python/Azure K-V ไม่มีวันที่ · กลับกัน
    /// ถ้าบังคับ InvariantCulture ล้วน ๆ วันที่ไทยแบบ <c>15/08/2569</c> จะถูก
    /// อ่านเป็น MM/dd แล้ว<b>พังทั้งใบ</b> (เดือน 15 ไม่มีจริง) — สองทิศนี้
    /// แก้พร้อมกันไม่ได้ด้วย culture เดียว จึงต้องไล่ตามลำดับ</para>
    ///
    /// <para>ลำดับ: (1) ISO 8601 (ต้องขึ้นต้น <c>yyyy-MM-dd</c> เท่านั้น) อ่านผ่าน
    /// <see cref="DateTimeOffset"/> + Invariant แล้วเอา<b>เวลาตามที่เอกสารเขียน</b>
    /// (2) รูปแบบบนกระดาษไทย <c>d/M/yyyy</c> · <c>d-M-yy</c> · <c>yyyy/M/d</c> ·
    /// <c>yyyyMMdd</c> — <b>แยกตัวเลขเองด้วย regex ไม่ผ่าน culture ใด ๆ</b>
    /// (3) ปีที่ได้ผ่าน <see cref="NormalizeYear"/> เสมอ — พ.ศ. บนกระดาษจึงกลายเป็น
    /// ค.ศ. ที่จุดเดียว ไม่ใช่ให้แต่ละผู้เรียกลบ 543 กันเอง (4) วัน/เดือนที่เกินจริง
    /// (32/13) คืน <c>false</c> — ห้ามปัดให้เป็นวันที่ที่ "ดูใช้ได้"</para>
    /// </summary>
    public static bool TryParseFlexible(string? text, out DateTime value)
    {
        value = default;
        var t = (text ?? "").Trim();
        if (t.Length == 0) return false;

        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // (1) machine-generated — ISO 8601 / roundtrip จาก Azure DI · python · e-Tax XML
        //
        // ★ ต้องบังคับว่า "ขึ้นต้นด้วยปี 4 หลัก แล้วตามด้วย -MM-dd" ก่อน ห้ามโยน
        //   TryParse ทั่วไปเข้ามาที่นี่: Invariant อ่าน `05/08/2026` เป็น **MM/dd**
        //   = 8 พ.ค. ทั้งที่กระดาษไทยหมายถึง 5 ส.ค. ⇒ ใบที่วันและเดือนต่างกันแต่
        //   ทั้งคู่ ≤ 12 จะเพี้ยนเงียบ ๆ (ไม่ error เพราะวันที่ยังสมเหตุสมผล)
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[0-9]{4}-[0-9]{2}-[0-9]{2}")
            && DateTimeOffset.TryParse(t, inv,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var iso))
            // ใช้ DateTimeOffset แล้วอ่าน .DateTime = **เวลาตามที่เอกสารเขียน**
            // ห้ามแปลงเป็นเวลาเครื่อง: "2026-09-05T00:30+07:00" บนเครื่องโซนลบ
            // จะถอยเป็น 4 ก.ย. เงียบ ๆ — วันที่บนกระดาษต้องไม่ขึ้นกับโซนของเซิร์ฟเวอร์
            return Build(iso.DateTime.Year, iso.DateTime.Month, iso.DateTime.Day, out value);

        // (2) รูปแบบบนกระดาษไทย — แยกตัวเลขเองทั้งหมด **ไม่ผ่าน culture ใด ๆ**
        //     (ใช้ TryParseExact กับรูปแบบ "yy" ไม่ได้ เพราะ .NET เติมศตวรรษให้
        //      ตาม TwoDigitYearMax ของปฏิทินก่อน ⇒ "69" กลายเป็น 1969 แล้ว
        //      NormalizeYear มองไม่เห็นว่ามันเป็น พ.ศ. ย่อ)
        var dmy = System.Text.RegularExpressions.Regex.Match(
            t, @"^([0-9]{1,2})[/\-.]([0-9]{1,2})[/\-.]([0-9]{2}|[0-9]{4})$");
        if (dmy.Success)
            return Build(NormalizeYear(int.Parse(dmy.Groups[3].Value, inv)),
                int.Parse(dmy.Groups[2].Value, inv),
                int.Parse(dmy.Groups[1].Value, inv), out value);

        var ymd = System.Text.RegularExpressions.Regex.Match(
            t, @"^([0-9]{4})[/\-.]([0-9]{1,2})[/\-.]([0-9]{1,2})$");
        if (ymd.Success)
            return Build(NormalizeYear(int.Parse(ymd.Groups[1].Value, inv)),
                int.Parse(ymd.Groups[2].Value, inv),
                int.Parse(ymd.Groups[3].Value, inv), out value);

        // (2ข) "15 สิงหาคม 2569" / "15 ส.ค. 69" — ชื่อเดือนไทยเต็ม/ย่อ ผ่าน
        //      ตัวแปลงกลาง Helpers/ThaiMonthName (ตัวเดียวกับที่ SmartFieldExtractor ใช้)
        //      เดิมรูปแบบนี้อ่านได้เฉพาะตอน process ตั้ง culture th-TH เท่านั้น
        //      — ซึ่งเป็นเงื่อนไขเดียวกับที่ทำให้ ISO พังทั้งระบบ
        var named = System.Text.RegularExpressions.Regex.Match(
            t, @"^([0-9]{1,2})[ \t]+([^ \t0-9]+)[ \t]+([0-9]{2}|[0-9]{4})$");
        if (named.Success && ThaiMonthName.TryParse(named.Groups[2].Value) is int mo)
            return Build(NormalizeYear(int.Parse(named.Groups[3].Value, inv)), mo,
                int.Parse(named.Groups[1].Value, inv), out value);

        var compact = System.Text.RegularExpressions.Regex.Match(t, @"^([0-9]{4})([0-9]{2})([0-9]{2})$");
        if (compact.Success)
            return Build(NormalizeYear(int.Parse(compact.Groups[1].Value, inv)),
                int.Parse(compact.Groups[2].Value, inv),
                int.Parse(compact.Groups[3].Value, inv), out value);

        return false;

        // สร้างวันที่แบบตรวจความถูกต้องเอง — วัน/เดือนเกินจริง (32/13) = อ่านผิด
        // ต้องคืน false ไม่ใช่ปัดให้เป็นวันที่ที่ "ดูใช้ได้" (ห้ามแต่งค่า)
        static bool Build(int year, int month, int day, out DateTime outValue)
        {
            outValue = default;
            if (year is < 1900 or > 2400 || month is < 1 or > 12 || day < 1) return false;
            if (day > DateTime.DaysInMonth(year, month)) return false;
            outValue = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
            return true;
        }
    }
}
