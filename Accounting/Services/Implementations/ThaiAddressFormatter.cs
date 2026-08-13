using System.Text.RegularExpressions;

namespace Accounting.Services.Implementations;

/// <summary>
/// <b>ตัวประกอบที่อยู่ไทยตัวเดียวของทั้งระบบ</b> — ทุกที่ที่ต้องแสดง/บันทึกที่อยู่
/// เป็นข้อความต้องเรียกที่นี่ ห้ามเขียน <c>string.Join(" ", ...)</c> เองอีก
///
/// เหตุผล (บั๊กจริงที่เคยเกิด): ก่อนหน้านี้มีตัวประกอบแยกกัน 5 ตัว —
/// PdfGenerationService, WithholdingTaxCertService, DocumentService (contact),
/// CompanyService, EtaxInvoiceService — สี่ตัวหลัง join ด้วยช่องว่างเปล่า ๆ
/// ทำให้ที่อยู่บนเอกสารออกมาเป็น "44 หมู่ 9 หนองเหียง พนัสนิคม ชลบุรี 20140"
/// (ไม่มี ต./อ./จ.) ซึ่ง <b>ผิดข้อกำหนดเอกสารราชการ</b>: หนังสือรับรองหัก ณ ที่จ่าย
/// (50 ทวิ) และใบกำกับภาษีเต็มรูป §86/4 ต้องระบุ "ตำบล/แขวง อำเภอ/เขต จังหวัด"
/// ให้อ่านออกว่าส่วนไหนคืออะไร — เจ้าหน้าที่สรรพากรอ่านที่อยู่ไม่มีคำนำหน้าไม่ได้
///
/// หน้าที่ของตัวนี้:
///   • เติมคำนำหน้า ต./อ./จ. — และสลับเป็น แขวง/เขต อัตโนมัติเมื่อเป็น กทม.
///     (กทม. ใช้ "กรุงเทพมหานคร" ล้วน ไม่มี "จ." นำ)
///   • parse free-text ที่ไม่มีคำนำหน้าให้กลับเป็น structured ก่อน render
///   • ไม่พิมพ์ตำบล/อำเภอ/จังหวัดซ้ำสองรอบ เมื่อ free-text มีอยู่แล้ว
///   • ยุบการเขียน กทม. ซ้ำ ("กทม กรุงเทพมหานคร" → "กรุงเทพมหานคร")
/// </summary>
public static class ThaiAddressFormatter
{
    private static readonly string[] AreaPrefixes =
        { "แขวง", "เขต", "ตำบล", "อำเภอ", "จังหวัด", "ต.", "อ.", "จ." };

    /// <summary>ประกอบที่อยู่ฉบับพิมพ์ลงเอกสาร จาก free-text + structured fields
    /// (ทั้งคู่ optional). structured ชนะ free-text เสมอ; ช่องที่ structured ว่าง
    /// จะ parse เติมจาก free-text ให้</summary>
    public static string Format(
        string? freeText, string? buildingNumber, string? buildingName, string? moo, string? street,
        string? subDistrict, string? district, string? province, string? postalCode)
    {
        var sub = subDistrict?.Trim();
        var dist = district?.Trim();
        var prov = province?.Trim();
        var post = postalCode?.Trim();

        // Parse free-text เมื่อ structured locality **ไม่ครบ** (ไม่ใช่แค่ตอนว่างหมด)
        // — เคสจริงที่หลุด: บริษัทกรอกแต่ "จังหวัด" ไว้ใน structured ส่วนตำบล/อำเภอ
        // อยู่ใน free-text แบบไม่มีคำนำหน้า → เงื่อนไขเดิม (ต้องว่างทั้ง 3 ช่อง)
        // ไม่ทำงาน → พิมพ์ออกมาไม่มี ต./อ. เลย.
        // structured ที่ผู้ใช้กรอกเองชนะเสมอ — parse ใช้ "เติมช่องที่ยังว่าง" เท่านั้น
        var localityIncomplete = string.IsNullOrWhiteSpace(sub)
            || string.IsNullOrWhiteSpace(dist)
            || string.IsNullOrWhiteSpace(prov);
        if (localityIncomplete && !string.IsNullOrWhiteSpace(freeText))
        {
            var p = ThaiAddressParser.Parse(freeText);
            if (!string.IsNullOrWhiteSpace(p.Province)
                || !string.IsNullOrWhiteSpace(p.SubDistrict)
                || !string.IsNullOrWhiteSpace(p.District))
            {
                if (string.IsNullOrWhiteSpace(sub)) sub = p.SubDistrict?.Trim();
                if (string.IsNullOrWhiteSpace(dist)) dist = p.District?.Trim();
                if (string.IsNullOrWhiteSpace(prov)) prov = p.Province?.Trim();
                if (string.IsNullOrWhiteSpace(post)) post = p.PostalCode?.Trim();
                buildingNumber ??= p.BuildingNumber;
                moo ??= p.Moo;
                // รักษาส่วนหัวเต็ม (ห้อง/ชั้น/อาคาร/ซอย/ถนน) ไม่ใช่แค่ชื่อถนนสั้น ๆ
                // จาก parser — ผู้ใช้เห็นที่อยู่ครบเหมือนเดิม แค่แก้ ตำบล/อำเภอ →
                // แขวง/เขต ให้ถูกต้องสำหรับ กทม.
                street ??= ThaiAddressParser.ExtractStreetHead(freeText, p.BuildingNumber, p.Moo);
            }
        }

        // ไม่มี locality เลยจริง ๆ (parse ก็ไม่ได้) → คืน free-text เท่าที่มี
        // แต่ยังยุบ "กทม กรุงเทพมหานคร" ที่ผู้ใช้พิมพ์ซ้ำเองให้
        if (string.IsNullOrWhiteSpace(sub) && string.IsNullOrWhiteSpace(dist) && string.IsNullOrWhiteSpace(prov))
            return CollapseBangkok((freeText ?? "").Trim());

        var isBkk = !string.IsNullOrWhiteSpace(prov)
            && (prov.Contains("กรุงเทพ") || prov.Contains("กทม"));

        // Street/house part: prefer the explicit structured fields, else the
        // free text. ชื่ออาคาร (buildingName) ต้องอยู่ในบรรทัดนี้ด้วย — เดิม
        // ตกหล่นทำให้ที่อยู่บนเอกสารไม่มีชื่ออาคารทั้งที่ contact บันทึกไว้.
        // กันซ้ำ: ถ้า street (เช่น head ที่ดึงจาก free-text) มีชื่ออาคารอยู่แล้ว
        // ไม่ต้องเติมซ้ำอีกรอบ.
        var bName = buildingName?.Trim();
        if (!string.IsNullOrWhiteSpace(bName)
            && (street?.Contains(bName, StringComparison.Ordinal) == true))
            bName = null;
        var structuredStreet = string.Join(" ", new[]
        {
            buildingNumber?.Trim(),
            bName,
            string.IsNullOrWhiteSpace(moo) ? null : $"หมู่ {moo!.Trim()}",
            street?.Trim(),
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        // ใช้ structured street เมื่อมี "จุดยึด" จริง (เลขที่/หมู่/ถนน) — ถ้ามี
        // แค่ชื่ออาคารโดด ๆ ให้ตกไปใช้ free-text ที่มักครบกว่า (พฤติกรรมเดิม)
        // เว้นแต่ไม่มี free-text เลยจึงใช้ชื่ออาคารเท่าที่มี.
        var hasStreetAnchor = !string.IsNullOrWhiteSpace(buildingNumber)
            || !string.IsNullOrWhiteSpace(moo) || !string.IsNullOrWhiteSpace(street);
        string streetPart;
        if (!string.IsNullOrWhiteSpace(structuredStreet)
            && (hasStreetAnchor || string.IsNullOrWhiteSpace(freeText)))
        {
            streetPart = structuredStreet;
        }
        else
        {
            // ตกไปใช้ free-text (เลขที่/ถนน อยู่ใน free-text ไม่ใช่ structured) —
            // แต่ยังต้องเติมชื่ออาคารเข้าไปถ้า free-text ยังไม่มี ไม่งั้นชื่ออาคาร
            // จะหายอีกครั้งในเคสนี้ (บั๊กที่ผู้ใช้รายงาน หาก contact เก็บที่อยู่แบบนี้)
            streetPart = freeText ?? "";
            if (!string.IsNullOrWhiteSpace(bName)
                && !streetPart.Contains(bName!, StringComparison.Ordinal))
                streetPart = (bName + " " + streetPart).Trim();
        }

        // The street line must NEVER echo the locality we're about to print as
        // its own fields. Drop locality echoes **token by token** — NOT via
        // substring Replace, which mangled "บางนาตราด" → "ตราด" when the
        // sub-district was "บางนา" (substring of the road name). A token is
        // dropped when it equals a locality value, a bare prefix, a glued
        // prefix+value ("ตำบลบางนา"), a Bangkok synonym, or the postal code.
        // This still kills "8/36 แขวงดอกไม้ เขตประเวศ กทม กรุงเทพมหานคร 10250"
        // without eating real road names.
        var localityVals = new[] { sub, dist, prov, post }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .ToHashSet();
        bool DropStreetToken(string raw)
        {
            var x = raw.Trim().Trim(',').Trim();
            if (string.IsNullOrEmpty(x)) return true;
            if (localityVals.Contains(x)) return true;
            if (AreaPrefixes.Contains(x)) return true;
            if (IsBangkokToken(x)) return true;
            foreach (var pfx in AreaPrefixes)
                if (x.StartsWith(pfx, StringComparison.Ordinal)
                    && localityVals.Contains(x[pfx.Length..].Trim())) return true;
            return false;
        }
        streetPart = string.Join(" ",
            streetPart.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                      .Where(t => !DropStreetToken(t)));
        streetPart = Regex.Replace(streetPart, @"\s{2,}", " ").Trim().Trim(',').Trim();

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(streetPart)) parts.Add(streetPart);
        // Bangkok mis-entry guard: users frequently type a Bangkok spelling
        // ("กทม") into the sub-district / district field too. Without this,
        // District="กทม" + Province="กรุงเทพมหานคร" printed "เขตกทม
        // กรุงเทพมหานคร" — the doubled "กทม กรุงเทพมหานคร" the user reported.
        // Drop any sub/dist value that is itself a Bangkok variant so the
        // canonical province name is printed exactly once.
        // StripAreaPrefix กันเคสผู้ใช้พิมพ์ "ต.หนองเหียง" ลงช่องตำบลเอง →
        // ไม่งั้นได้ "ต.ต.หนองเหียง"
        if (!string.IsNullOrWhiteSpace(sub) && !IsBangkokToken(sub))
            parts.Add((isBkk ? "แขวง" : "ต.") + StripAreaPrefix(sub!));
        if (!string.IsNullOrWhiteSpace(dist) && !IsBangkokToken(dist))
            parts.Add((isBkk ? "เขต" : "อ.") + StripAreaPrefix(dist!));
        // Bangkok prints its full canonical name (กรุงเทพมหานคร) once, no จ.;
        // other provinces get the จ. prefix (after stripping any stray
        // prefix the user may have typed into the field).
        if (!string.IsNullOrWhiteSpace(prov))
            parts.Add(isBkk ? "กรุงเทพมหานคร" : "จ." + StripAreaPrefix(prov!));
        if (!string.IsNullOrWhiteSpace(post)) parts.Add(post);
        // Final safety net: collapse any Bangkok doubling that slipped through
        // the structured assembly (e.g. a Bangkok spelling left inside the
        // free-text street part that the token strip missed due to spacing).
        return CollapseBangkok(string.Join(" ", parts));
    }

    /// <summary>ตัดคำนำหน้าเขตปกครองที่ผู้ใช้อาจพิมพ์ติดมากับ "ค่า" ในช่อง
    /// structured (เช่นพิมพ์ "ต.หนองเหียง" ลงช่องตำบล) — กันคำนำหน้าซ้อน</summary>
    public static string StripAreaPrefix(string value)
    {
        var x = (value ?? "").Trim();
        foreach (var p in AreaPrefixes)
            if (x.StartsWith(p, StringComparison.Ordinal) && x.Length > p.Length)
                return x[p.Length..].Trim();
        return x;
    }

    /// <summary>True when the token — after dropping any แขวง/เขต/ต./อ./จ.
    /// prefix — is any spelling of Bangkok (full name or abbreviation).
    /// Lets the formatter recognise a Bangkok value mis-entered into the
    /// sub-district / district field and drop it so the province isn't
    /// printed twice ("เขตกทม กรุงเทพมหานคร").</summary>
    public static bool IsBangkokToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var x = token.Trim();
        foreach (var p in AreaPrefixes)
            if (x.StartsWith(p, StringComparison.Ordinal)) { x = x[p.Length..].Trim(); break; }
        return x is "กทม" or "กทม." or "กทมฯ" or "กรุงเทพ" or "กรุงเทพฯ" or "กรุงเทพมหานคร";
    }

    /// <summary>Collapse redundant Bangkok spellings down to a single canonical
    /// "กรุงเทพมหานคร". Handles three doubling patterns:
    ///   1. abbreviation next to full name ("กทม กรุงเทพมหานคร")
    ///   2. the full name repeated ("กรุงเทพมหานคร กรุงเทพมหานคร")
    ///   3. "เขต/แขวง" + Bangkok mis-entered ("เขตกทม กรุงเทพมหานคร")
    /// Run on the FINAL assembled address in every path (not just the
    /// free-text-only one) so no rendering route can leak a doubled province.</summary>
    public static string CollapseBangkok(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        // 1. Drop abbreviations when the full name is also present.
        if (s.Contains("กรุงเทพมหานคร"))
            foreach (var abbr in new[] { "เขตกทม.", "เขตกทม", "แขวงกทม", "กทม.", "กทมฯ", "กทม", "กรุงเทพฯ", "กรุงเทพมหานครฯ" })
                s = s.Replace(abbr, " ");
        // 2. Squash the full name repeated consecutively (only whitespace
        // between the copies — never swallow real content in between).
        s = Regex.Replace(s, @"กรุงเทพมหานคร(\s+กรุงเทพมหานคร)+", "กรุงเทพมหานคร");
        return Regex.Replace(s, @"\s{2,}", " ").Trim();
    }
}
