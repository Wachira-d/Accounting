namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Lightweight bilingual term canonicalizer — practical alternative to
/// loading a multilingual sentence-embedding model for cross-script
/// matching ("กระดาษทิชชู่" ↔ "tissue paper", "คอมพิวเตอร์" ↔ "computer").
///
/// Each entry maps a family of equivalent wordings to a single
/// canonical token like "$tissue". <see cref="ProductMatcher.Normalize"/>
/// rewrites known aliases to their canonical token BEFORE the token-sort
/// + trigram pass, so OCR lines in Thai and English alike collapse to
/// the same normalized form when they describe the same physical thing.
///
/// Trade-offs vs. a real sentence-embedding model:
///   • + No model file to ship (~120 MB+ for multilingual MiniLM saved).
///   • + No inference latency — pure string replacement.
///   • + Reviewable + auditable — humans can read the dictionary.
///   • − Only catches term families we've curated.
///   • − Misses paraphrases that re-word the same concept differently.
///
/// Coverage focus: Thai SME OCR vocabulary from the 12 industries we
/// already ship template plans for (restaurant, retail, beauty,
/// healthcare, services, construction, real estate, technology,
/// education, transportation, hotel, manufacturing) — plus the cross-
/// industry "office consumables / IT / vehicles / cleaning" cluster.
///
/// Expansion: append entries to <see cref="Groups"/>. Each group is
/// (alias[], canonical). Aliases are case-insensitive sub-string
/// matched, so "กระดาษทิชชู่ขนาดใหญ่" still matches "กระดาษทิชชู่".
/// </summary>
public static class BilingualTerms
{
    public static readonly (string[] Aliases, string Canonical)[] Groups =
    {
        // ── Paper / tissue / office consumables ──
        (new[] {"กระดาษทิชชู่","กระดาษเช็ดมือ","ทิชชู่","tissue","tissues","tissue paper","facial tissue","wipes"}, "$tissue"),
        (new[] {"กระดาษถ่ายเอกสาร","กระดาษ a4","กระดาษเอ4","กระดาษเอสี่","กระดาษ a3","กระดาษ b5","กระดาษโน้ต","paper","copy paper","a4 paper","bond paper","printer paper"}, "$paper"),
        (new[] {"ปากกา","ลูกลื่น","ปากกาเมจิก","ปากกาไฮไลท์","pen","pens","ballpoint","ball pen","highlighter","marker","sharpie","permanent marker"}, "$pen"),
        (new[] {"ดินสอ","ดินสอกด","pencil","pencils","mechanical pencil"}, "$pencil"),
        (new[] {"แฟ้ม","แฟ้มเอกสาร","แฟ้มแขวน","แฟ้มโชว์เอกสาร","folder","binder","file folder","manila folder"}, "$folder"),
        (new[] {"ลิ้นชัก","ลวดเย็บ","แม็กเย็บกระดาษ","ที่เย็บกระดาษ","stapler","staple","staples"}, "$stapler"),
        (new[] {"ไม้บรรทัด","ruler","scale ruler"}, "$ruler"),
        (new[] {"ที่เจาะกระดาษ","ที่เจาะรู","hole punch","hole puncher"}, "$holepunch"),
        (new[] {"คลิป","คลิปดำ","คลิปหนีบกระดาษ","paper clip","binder clip","paperclip"}, "$paperclip"),
        (new[] {"เทป","เทปใส","สก๊อตเทป","scotch tape","tape","sticky tape","masking tape"}, "$tape"),

        // ── IT / computers / peripherals ──
        (new[] {"คอมพิวเตอร์","คอม","พีซี","computer","pc","desktop","desktop pc","desktop computer"}, "$computer"),
        (new[] {"โน๊ตบุ๊ค","โน้ตบุ๊ก","โน๊ตบุ้ค","laptop","notebook","macbook","macbook pro","macbook air"}, "$laptop"),
        (new[] {"จอ","จอภาพ","จอคอม","monitor","display","screen","lcd monitor"}, "$monitor"),
        (new[] {"คีย์บอร์ด","แป้นพิมพ์","keyboard","mechanical keyboard"}, "$keyboard"),
        (new[] {"เมาส์","เม้าส์","mouse","optical mouse","wireless mouse"}, "$mouse"),
        (new[] {"เครื่องพิมพ์","ปริ้นเตอร์","printer","inkjet printer","laser printer"}, "$printer"),
        (new[] {"เครื่องสแกน","สแกนเนอร์","scanner","document scanner","barcode scanner"}, "$scanner"),
        (new[] {"หมึก","หมึกพิมพ์","ตลับหมึก","ink","ink cartridge","toner","toner cartridge"}, "$ink"),
        (new[] {"เซิร์ฟเวอร์","server","tower server","rack server"}, "$server"),
        (new[] {"สาย lan","สาย hdmi","สาย usb","cable","lan cable","ethernet cable","hdmi cable","usb cable"}, "$cable"),
        (new[] {"เราเตอร์","router","wifi router","wireless router"}, "$router"),
        (new[] {"สวิทช์","switch","network switch"}, "$switch"),
        (new[] {"ฮาร์ดดิสก์","ฮาร์ดดิส","hdd","ssd","hard disk","hard drive","solid state"}, "$harddisk"),
        (new[] {"ยูพีเอส","ups","battery backup","uninterruptible power"}, "$ups"),

        // ── Furniture ──
        (new[] {"โต๊ะ","โต๊ะทำงาน","โต๊ะออฟฟิศ","desk","office desk","table","work desk"}, "$desk"),
        (new[] {"เก้าอี้","เก้าอี้สำนักงาน","เก้าอี้ออฟฟิศ","chair","office chair","ergonomic chair"}, "$chair"),
        (new[] {"ตู้","ตู้เอกสาร","ตู้เก็บของ","cabinet","filing cabinet","storage cabinet"}, "$cabinet"),
        (new[] {"ชั้น","ชั้นวาง","ชั้นวางของ","shelf","shelves","bookshelf","storage shelf"}, "$shelf"),
        (new[] {"โซฟา","sofa","couch"}, "$sofa"),

        // ── Vehicles / transportation ──
        (new[] {"รถยนต์","รถเก๋ง","car","sedan","passenger car"}, "$car"),
        (new[] {"รถกระบะ","กระบะ","ปิคอัพ","ปิกอัพ","pickup","pickup truck","truck"}, "$pickup"),
        (new[] {"รถบรรทุก","truck","lorry","heavy truck"}, "$heavytruck"),
        (new[] {"รถจักรยานยนต์","มอเตอร์ไซค์","motorcycle","motorbike","scooter"}, "$motorcycle"),
        (new[] {"น้ำมัน","น้ำมันรถ","gasoline","diesel","fuel","gas","petrol"}, "$fuel"),
        (new[] {"ยาง","ยางรถ","ยางรถยนต์","tire","tires","tyre"}, "$tire"),

        // ── Cleaning / hygiene ──
        (new[] {"น้ำยาทำความสะอาด","น้ำยาเช็ดพื้น","detergent","cleaner","floor cleaner","all-purpose cleaner"}, "$cleaner"),
        (new[] {"สบู่","สบู่เหลว","soap","liquid soap","hand soap","bar soap"}, "$soap"),
        (new[] {"แชมพู","shampoo"}, "$shampoo"),
        (new[] {"ครีมนวด","conditioner","hair conditioner"}, "$conditioner"),
        (new[] {"ผงซักฟอก","น้ำยาซักผ้า","laundry detergent","washing powder"}, "$laundry"),
        (new[] {"ถุงขยะ","ถุงดำ","trash bag","garbage bag","bin bag"}, "$trashbag"),
        (new[] {"ถุงมือ","gloves","rubber gloves","latex gloves","nitrile gloves"}, "$gloves"),
        (new[] {"หน้ากาก","หน้ากากอนามัย","mask","face mask","surgical mask","n95"}, "$mask"),

        // ── Beverages / food (cafe/restaurant) ──
        (new[] {"น้ำดื่ม","น้ำเปล่า","water","drinking water","bottled water"}, "$water"),
        (new[] {"กาแฟ","เมล็ดกาแฟ","coffee","coffee beans","espresso","arabica","robusta"}, "$coffee"),
        (new[] {"ชา","ใบชา","tea","tea leaves","green tea","black tea"}, "$tea"),
        (new[] {"นม","นมสด","milk","fresh milk","uht milk"}, "$milk"),
        (new[] {"น้ำตาล","sugar","cane sugar","brown sugar","white sugar"}, "$sugar"),
        (new[] {"ข้าว","ข้าวสาร","rice","white rice","jasmine rice"}, "$rice"),
        (new[] {"น้ำมันพืช","น้ำมันถั่วเหลือง","น้ำมันปาล์ม","cooking oil","vegetable oil","palm oil","soybean oil"}, "$cookingoil"),
        (new[] {"น้ำปลา","fish sauce"}, "$fishsauce"),
        (new[] {"ซอสปรุงรส","ซีอิ๊ว","soy sauce","seasoning sauce"}, "$soysauce"),

        // ── Construction / hardware ──
        (new[] {"ปูนซีเมนต์","ปูน","cement","portland cement"}, "$cement"),
        (new[] {"อิฐ","อิฐแดง","อิฐมวลเบา","brick","bricks","clay brick","autoclaved brick"}, "$brick"),
        (new[] {"เหล็ก","เหล็กเส้น","เหล็กรูปพรรณ","steel","rebar","steel bar","structural steel"}, "$steel"),
        (new[] {"สี","สีน้ำ","สีน้ำมัน","paint","interior paint","exterior paint","latex paint"}, "$paint"),
        (new[] {"กระเบื้อง","ไทล์","tile","tiles","ceramic tile","floor tile"}, "$tile"),
        (new[] {"สายไฟ","wire","electrical wire","cable wire"}, "$wire"),
        (new[] {"หลอดไฟ","หลอด led","bulb","light bulb","led bulb","fluorescent bulb"}, "$bulb"),

        // ── HVAC / appliances ──
        (new[] {"แอร์","เครื่องปรับอากาศ","air conditioner","air-con","aircon","ac unit","split type ac"}, "$aircon"),
        (new[] {"พัดลม","fan","ceiling fan","exhaust fan","desk fan"}, "$fan"),
        (new[] {"ตู้เย็น","refrigerator","fridge"}, "$fridge"),
        (new[] {"เครื่องซักผ้า","washing machine","washer"}, "$washingmachine"),
        (new[] {"ไมโครเวฟ","microwave","microwave oven"}, "$microwave"),
        (new[] {"เครื่องดูดฝุ่น","vacuum","vacuum cleaner"}, "$vacuum"),

        // ── Auto-detected brands (cross-script) ──
        (new[] {"โตโยต้า","toyota"}, "$toyota"),
        (new[] {"ฮอนด้า","honda"}, "$honda"),
        (new[] {"อีซูซุ","isuzu"}, "$isuzu"),
        (new[] {"นิสสัน","nissan"}, "$nissan"),
        (new[] {"มาสด้า","mazda"}, "$mazda"),
        (new[] {"มิตซูบิชิ","mitsubishi"}, "$mitsubishi"),
        (new[] {"เป๊ปซี่","pepsi"}, "$pepsi"),
        (new[] {"โค้ก","โคคาโคล่า","coke","coca-cola","coca cola","cocacola"}, "$coke"),
        (new[] {"สิงห์","singha"}, "$singha"),
        (new[] {"ลีโอ","leo"}, "$leo"),
        (new[] {"ช้าง","chang"}, "$chang"),
        (new[] {"ไมโล","milo"}, "$milo"),
        (new[] {"โอวัลติน","ovaltine"}, "$ovaltine"),
        (new[] {"แอปเปิ้ล","apple"}, "$apple"),
        (new[] {"ซัมซุง","samsung"}, "$samsung"),
        (new[] {"เลอโนโว","lenovo"}, "$lenovo"),
        (new[] {"ดีอีแอล","dell"}, "$dell"),
        (new[] {"เอชพี","hp","hewlett packard"}, "$hp"),
        (new[] {"แคนนอน","canon"}, "$canon"),
        (new[] {"นิคอน","nikon"}, "$nikon"),
        (new[] {"โซนี่","sony"}, "$sony"),
        (new[] {"แอลจี","lg","lg electronics"}, "$lg"),
        (new[] {"พานาโซนิค","panasonic"}, "$panasonic"),

        // ── Medical / healthcare ──
        (new[] {"ยา","ยาเม็ด","medicine","tablet","pill","drug","pharmaceutical"}, "$medicine"),
        (new[] {"วัคซีน","vaccine","vaccination"}, "$vaccine"),
        (new[] {"เข็มฉีดยา","syringe","needle","injection"}, "$syringe"),
        (new[] {"ผ้าพันแผล","gauze","bandage","sterile gauze"}, "$gauze"),
        (new[] {"แอลกอฮอล์","alcohol","rubbing alcohol","ethanol"}, "$alcohol"),

        // ── Generic services ──
        (new[] {"ค่าเช่า","ค่าเช่าสำนักงาน","ค่าเช่าที่","rent","rental","office rent","lease"}, "$rent"),
        (new[] {"ค่าน้ำ","ค่าน้ำประปา","water bill","water utility"}, "$waterbill"),
        (new[] {"ค่าไฟ","ค่าไฟฟ้า","electricity bill","electric bill","power bill"}, "$electricitybill"),
        (new[] {"อินเทอร์เน็ต","เน็ต","internet","internet bill","broadband"}, "$internet"),
        (new[] {"ค่าโทรศัพท์","ค่าโทร","phone bill","telephone bill","mobile bill"}, "$phonebill"),
        (new[] {"ค่าขนส่ง","ค่าจัดส่ง","shipping","delivery","freight","courier"}, "$shipping"),
        (new[] {"ค่าโฆษณา","advertising","ad","ads","facebook ads","google ads"}, "$advertising"),
        (new[] {"ค่าที่ปรึกษา","consulting","consulting fee","advisory fee"}, "$consulting"),
        (new[] {"ค่าทำบัญชี","accounting","bookkeeping","audit fee"}, "$accountingfee"),
        (new[] {"ค่าตรวจสอบ","auditing","audit"}, "$audit"),
    };

    /// <summary>APPEND each matched group's canonical token to the
    /// input text without replacing the original. Case-insensitive.
    /// Done in <see cref="ProductMatcher.Normalize"/> so the trigram /
    /// Levenshtein layer sees a shared cross-script token while the
    /// original wording stays intact (so keyword-based filters like
    /// FixedAssetDetector still see "computer" / "คอมพิวเตอร์" /
    /// "laptop" literally).
    /// </summary>
    public static string Canonicalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
        var s = text.ToLowerInvariant();
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (aliases, canon) in Groups)
        {
            if (added.Contains(canon)) continue;
            foreach (var a in aliases)
            {
                if (s.Contains(a))
                {
                    added.Add(canon);
                    break;
                }
            }
        }
        if (added.Count == 0) return s;
        // Append in a deterministic order so the same input always
        // produces the same normalized output (important for the alias
        // cache + trigram index keys).
        return s + " " + string.Join(" ", added.OrderBy(t => t, StringComparer.Ordinal));
    }
}
