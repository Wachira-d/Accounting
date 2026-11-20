using System.Text.Json;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 278 (คำตัดสิน 278-1…278-8) — ตัวตัดสินเดียว "ช่องลายเซ็นฝั่งบริษัทสองช่อง (ช่อง 0 ผู้จัดทำ/ผู้เสนอราคา/ผู้รับเงิน ·
/// ช่อง 1 ผู้มีอำนาจลงนาม) เป็นคนเดียวกันไหม" + แผนรวม + กติกาชื่อเล่น + กติกาปุ่ม "ปรับผู้ลงนามตามข้อมูลปัจจุบัน"
///
/// <para>ที่มา (เจ้าของ · QT-20261119-0001 ที่ระบบภายนอกสร้างผ่าน API key): ช่อง 0 = <c>PreparerName</c> "K.นัท" + รูปจาก payload ·
/// ช่อง 1 = ผู้ออก API key ที่ไม่ได้อัปโหลดลายเซ็นในโปรไฟล์ ⇒ ช่องว่าง ทั้งที่เป็นคนเดียวกัน</para>
///
/// <para><b>ตัดสินด้วยกุญแจเท่านั้น — ห้ามเทียบความคล้ายชื่อ</b> · กุญแจ: (1) <c>Document.PreparerUserId</c> (ผูกได้เฉพาะผู้เรียกเอง หรือผู้ใช้ที่เปิด
/// "อนุญาตให้ระบบภายนอกระบุฉันเป็นผู้จัดทำ") · (2) ชื่อเล่นต่อบริษัทที่<b>เจ้าของบริษัทอนุมัติแล้ว</b> (<see cref="SignerAlias"/>) ตรงทั้งคำกับ
/// <c>PreparerName</c> · กุญแจถูก <b>แช่แข็งตอนเอกสารออก</b> (<see cref="Snapshot"/> ใน <c>Document.SignerSnapshotJson</c>) — เปลี่ยนชื่อเล่น/โปรไฟล์ทีหลัง
/// ไม่แก้เอกสารที่ออกแล้ว · ไม่มี snapshot (ใบร่าง · ใบที่ออกก่อนรอบนี้) ⇒ ไม่รวม</para>
///
/// <para><b>รูป (คำตัดสิน 278-9 — คำสั่งเจ้าของ · แทน 278-5)</b>: รวมได้เฉพาะเมื่อผูกด้วยกุญแจที่ยืนยันแล้ว (ผูกไม่ได้ = ไม่แตะเลย) ·
/// ทั้งสองช่องใช้รูปเดียวกัน — รูปในโปรไฟล์เป็นหลัก · โปรไฟล์ไม่มีรูป ⇒ รูปที่ระบบภายนอกส่งมา · แหล่งรูปแช่แข็งใน snapshot</para>
/// </summary>
public static class SignatureSlotIdentity
{
    public const string RuleCode = "SIGNER-SAME-PERSON";
    public const string RuleCodeRefresh = "SIGNER-REFRESH";
    public const int MaxAliases = 10;
    public const int MaxAliasLength = 100;
    public const string KindPreparerUserId = "PreparerUserId";
    public const string KindApprovedAlias = "ApprovedAlias";

    // ===================== snapshot (แช่แข็งตอนออกเอกสาร) =====================

    /// <summary>ผู้ใช้ที่ช่อง 0 ผูกด้วยกุญแจ ณ วันออกเอกสาร + ชื่อ/ตำแหน่งตามโปรไฟล์วันนั้น (ชื่อที่พิมพ์ทั้งสองช่องเมื่อรวม) + แหล่งรูปที่ใช้ทั้งสองช่อง
    /// (<see cref="ImageProfile"/> · <see cref="ImagePayload"/> · <see cref="ImageNone"/> · null = snapshot รุ่นก่อนคำตัดสิน 278-9 ⇒ ตัดสินด้วยกติกาเดียวกันตอนพิมพ์)</summary>
    public sealed record Candidate(Guid UserId, string Kind, string? Name, string? Title, string? ImageSource = null);

    /// <summary><c>C</c> ว่าง = แช่แข็งแบบ "ไม่รวม" (เช่น ใบที่ออกก่อนรอบ 278 — Round278Migration)</summary>
    public sealed record Snapshot(int V, List<Candidate> C, string Rule = RuleCode);

    public const string EmptySnapshotJson = "{\"V\":1,\"C\":[],\"Rule\":\"SIGNER-SAME-PERSON\"}";
    public const string ImageProfile = "profile";
    public const string ImagePayload = "payload";
    public const string ImageNone = "none";

    public static string Serialize(Snapshot s) => JsonSerializer.Serialize(s);

    public static Snapshot? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Snapshot>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary><b>คำตัดสิน 278-9 (คำสั่งเจ้าของ · แทน 278-5 เรื่องรูป)</b>: เมื่อผูกด้วยกุญแจที่ยืนยันแล้ว ทั้งสองช่องใช้<b>รูปเดียวกัน</b> —
    /// รูปในโปรไฟล์ของเรา (ยืนยันแล้ว) เป็นหลัก · โปรไฟล์ไม่มีรูป ⇒ รูปที่ระบบภายนอกส่งมา · ไม่มีทั้งคู่ ⇒ ไม่มีรูป</summary>
    private static string ChooseImageSource(bool profileHasImage, bool payloadHasImage)
        => profileHasImage ? ImageProfile : payloadHasImage ? ImagePayload : ImageNone;

    /// <summary>ผู้สมัครตอนออกเอกสาร: PreparerUserId ⇒ คนนั้นคนเดียว (ชื่อเล่นไม่ถูกใช้) · ไม่มี ⇒ เจ้าของชื่อเล่นที่อนุมัติแล้วซึ่งตรง <c>PreparerName</c>
    /// ทั้งคำ — <b>ตรงมากกว่าหนึ่งคน = กำกวม ⇒ ไม่มีผู้สมัคร</b> (ไม่เดา) · แหล่งรูปแช่แข็งด้วย <see cref="ChooseImageSource"/></summary>
    public static Snapshot BuildSnapshot(Guid? preparerUserId, (string? Name, string? Title, bool ProfileHasImage)? preparerProfile,
        IEnumerable<(Guid UserId, string? Name, string? Title, bool ProfileHasImage)> approvedAliasOwners, bool payloadHasImage = false)
    {
        if (preparerUserId is { } p && p != Guid.Empty)
            return new Snapshot(1, preparerProfile is { } pp
                ? new() { new Candidate(p, KindPreparerUserId, pp.Name, pp.Title, ChooseImageSource(pp.ProfileHasImage, payloadHasImage)) } : new());
        var owners = approvedAliasOwners.GroupBy(o => o.UserId).Select(g => g.First()).ToList();
        return new Snapshot(1, owners.Count == 1
            ? new() { new Candidate(owners[0].UserId, KindApprovedAlias, owners[0].Name, owners[0].Title, ChooseImageSource(owners[0].ProfileHasImage, payloadHasImage)) }
            : new());
    }

    // ===================== ตัดสินตอนพิมพ์ =====================

    /// <summary>ผู้ใช้ช่อง 1 (null = ไม่ใช่ผู้ใช้ เช่น ผู้มีอำนาจลงนามที่บริษัทตั้ง) ตรงผู้สมัครใน snapshot ⇒ คนเดียวกัน · ช่องลูกค้า/ไม่มี snapshot ⇒ ไม่รวม</summary>
    public static Candidate? Link(Snapshot? snap, Guid? slot1UserId, bool slot0IsCustomer = false, bool slot1IsCustomer = false)
    {
        if (snap == null || slot0IsCustomer || slot1IsCustomer || slot1UserId is not { } u || u == Guid.Empty) return null;
        return snap.C.FirstOrDefault(c => c.UserId == u);
    }

    public readonly record struct SlotFace(string? Name, string? Title, bool HasImage);

    /// <summary>ผลรวม — ชื่อ/ตำแหน่งของแต่ละช่อง + แหล่งรูปเดียวที่ใช้ทั้งสองช่อง</summary>
    public readonly record struct MergePlan(string? Name0, string? Name1, string? Title0, string? Title1, string ImageSource);

    /// <summary>ชื่อแสดงตามโปรไฟล์ (ตัวเดียวกับที่ช่องลายเซ็นใช้มาตลอด): ชื่อใต้ลายเซ็น → ชื่อ-สกุลบัญชี</summary>
    public static string? ProfileDisplayName(string? signatureName, string? fullName)
        => !string.IsNullOrWhiteSpace(signatureName) ? signatureName!.Trim()
         : !string.IsNullOrWhiteSpace(fullName) ? fullName!.Trim() : null;

    /// <summary>คำตัดสิน 278-2/278-9: ชื่อ "ครบกว่า" = ชื่อตามโปรไฟล์ที่แช่แข็งไว้ (ไม่ใช่ความยาวสตริง) ทั้งสองช่อง · ไม่มี ⇒ คงชื่อเดิมของแต่ละช่อง ·
    /// ตำแหน่งคงของช่องตัวเอง (ว่าง ⇒ ตำแหน่งในโปรไฟล์) · รูป: แหล่งที่แช่แข็งไว้ (<paramref name="frozenImageSource"/>) · snapshot รุ่นก่อน (null) ⇒
    /// ตัดสินด้วย <see cref="ChooseImageSource"/> จากสิ่งที่มีตอนพิมพ์ (<paramref name="profileHasImageNow"/> · รูปช่อง 0 = payload)</summary>
    public static MergePlan Merge(SlotFace s0, SlotFace s1, string? frozenName, string? frozenTitle, string? frozenImageSource, bool profileHasImageNow)
    {
        var p = Blank(frozenName);
        var t = Blank(frozenTitle);
        var src = frozenImageSource is ImageProfile or ImagePayload or ImageNone ? frozenImageSource : ChooseImageSource(profileHasImageNow, s0.HasImage);
        return new MergePlan(p ?? Blank(s0.Name) ?? Blank(s1.Name), p ?? Blank(s1.Name) ?? Blank(s0.Name),
            Blank(s0.Title) ?? t, Blank(s1.Title) ?? t, src);
    }

    // ===================== ชื่อเล่นต่อบริษัท (ต้องเจ้าของอนุมัติ) =====================

    /// <summary>คำกลางที่เป็นชื่อเล่นไม่ได้ (ใช้ร่วมกันหลายคน ⇒ กุญแจปลอม) · ขึ้นต้นด้วย "ฝ่าย"/"แผนก" ถือเป็นคำกลางทั้งหมด</summary>
    public static readonly string[] GenericWords =
    {
        "บัญชี", "การเงิน", "แอดมิน", "admin", "administrator", "staff", "sales", "account", "accounting", "finance", "system", "ระบบ",
        "ผู้จัดการ", "ผู้จัดทำ", "ผู้อนุมัติ", "เจ้าหน้าที่", "พนักงาน", "ผู้รับเงิน", "ผู้จ่ายเงิน", "ผู้มีอำนาจลงนาม", "กรรมการ", "เจ้าของ", "owner", "manager",
        "หัวหน้า", "แคชเชียร์", "cashier", "api", "bot", "test", "ทดสอบ",
    };
    private static readonly string[] GenericPrefixes = { "ฝ่าย", "แผนก", "ทีม" };

    /// <summary>ตรวจชื่อเล่นหนึ่งชื่อ (ฝั่งเขียนตัวเดียว · ตอนยื่นและตอนอนุมัติ): ว่าง/ยาวเกิน · คำกลาง · ซ้ำชื่อ-สกุล/ชื่อใต้ลายเซ็น/ชื่อเล่นของ<b>สมาชิกคนอื่น
    /// ในทุกบริษัทที่ผู้ขอเป็นสมาชิก</b> (<paramref name="otherMembersNames"/>) ⇒ ข้อความเหตุ · ผ่าน ⇒ null</summary>
    public static string? AliasProblem(string alias, IEnumerable<string?> otherMembersNames)
    {
        var n = NormalizeName(alias);
        if (n == null) return "ชื่อเล่นว่าง";
        if (alias.Trim().Length > MaxAliasLength) return $"ชื่อเล่นยาวเกิน {MaxAliasLength} ตัวอักษร";
        if (GenericWords.Any(w => NormalizeName(w) == n) || GenericPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
            return $"\"{alias.Trim()}\" เป็นคำกลางที่หลายคนใช้ร่วมกัน — ใช้เป็นกุญแจผูกลายเซ็นไม่ได้";
        if (otherMembersNames.Any(o => NormalizeName(o) == n))
            return $"\"{alias.Trim()}\" ตรงกับชื่อหรือชื่อเล่นของสมาชิกคนอื่น — ใช้ไม่ได้ (กันใช้ลายเซ็นแทนคนอื่น)";
        return null;
    }

    /// <summary>อ่านรายการที่ผู้ใช้ยื่น (คั่นขึ้นบรรทัด/จุลภาค/อัฒภาค) · ตัดซ้ำตามรูปที่เทียบ · เกินเพดาน ⇒ <see cref="BusinessRuleException"/></summary>
    public static IReadOnlyList<string> ParseRequested(string? input)
    {
        var seen = new HashSet<string>();
        var kept = new List<string>();
        if (!string.IsNullOrWhiteSpace(input))
            foreach (var raw in input.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var display = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (display.Length == 0) continue;
                if (seen.Add(NormalizeName(display)!)) kept.Add(display);
            }
        if (kept.Count > MaxAliases)
            throw new BusinessRuleException($"ตั้งชื่อเล่นสำหรับช่องลายเซ็นได้ไม่เกิน {MaxAliases} ชื่อต่อบริษัท", RuleCode);
        return kept;
    }

    /// <summary>ตัดช่องว่างหัวท้าย · ยุบช่องว่างกลาง · ตัวพิมพ์เล็ก — เทียบ "ตรงทั้งคำ" เท่านั้น (ไม่วัดความคล้าย) · ว่าง ⇒ null</summary>
    public static string? NormalizeName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    // ===================== ปุ่ม "ปรับผู้ลงนามตามข้อมูลปัจจุบัน" =====================

    /// <summary>ชนิดที่ห้ามปรับผู้ลงนามหลังออก (หลักฐานภาษี/การรับ-จ่ายเงิน)</summary>
    public static readonly DocumentType[] RefreshForbiddenTypes =
        { DocumentType.TaxInvoice, DocumentType.Receipt, DocumentType.CreditNote, DocumentType.DebitNote, DocumentType.PaymentVoucher,
          DocumentType.ReceiptVoucher, DocumentType.CertificateInLieu };

    /// <summary>เหตุที่ปรับผู้ลงนามไม่ได้จากข้อมูลบนใบ (ผู้เรียกตรวจลายเซ็นลูกค้าในขั้นอนุมัติเพิ่ม) · null = ปรับได้</summary>
    public static string? RefreshBlockedReason(Document d)
    {
        if (!DocumentStatusRules.IsIssued(d.Status)) return "ใบที่ยังไม่ออกใช้ข้อมูลปัจจุบันอยู่แล้ว";
        if (d.Status == DocumentStatus.Voided) return "ใบที่ยกเลิกแล้วปรับไม่ได้";
        if (RefreshForbiddenTypes.Contains(d.DocumentType) || d.IssuedAsCashReceipt || d.IsSettlementReceipt)
            return "ใบกำกับภาษี/ใบเสร็จ/ใบลด-เพิ่มหนี้/ใบสำคัญจ่าย ห้ามเปลี่ยนผู้ลงนามหลังออก";
        if (d.QuotationAcceptedAt != null || !string.IsNullOrWhiteSpace(d.DeliverySignatureBase64))
            return "ลูกค้าเซ็นยอมรับใบนี้แล้ว — คงผู้ลงนามตามที่ลูกค้าเห็นตอนเซ็น";
        return null;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
}
