using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 274-10 — <b>เลขประจำตัวที่ถูกเข้ารหัสซ้อน</b> (ciphertext ถูกเข้ารหัสอีกชั้น) กลับมาอ่านได้โดยไม่ต้องกรอกใหม่
///
/// <para><b>ที่มา:</b> ก่อนรอบ 274 แถวที่เข้ารหัสด้วย dev key ถอดไม่ออก ⇒ API คืน <c>enc1:…</c> ดิบ ⇒ ทางเข้าที่ไม่มีด่าน (สร้างพนักงาน · HRIS sync ·
/// นำเข้าไฟล์ที่ส่งออกไว้) เขียนสตริงนั้นกลับ ⇒ converter เข้ารหัสซ้ำด้วยคีย์ปัจจุบัน · รายงานบอก "กรอกใหม่" ทั้งที่กู้ได้:
/// ถอดชั้นนอก (คีย์ปัจจุบัน) → ชั้นใน enc1 → ถอดด้วยชุดคีย์เดิม → ค่าจริง</para>
/// </summary>
public class PiiNestedCipherRound274_10Tests
{
    // ค่าทดสอบเท่านั้น
    private const string NewKey = "r274-10-new-key-9876543210";
    private const string ValidId = "1101700001237";      // ผ่าน mod-11
    private const string BadChecksum = "1101700001238";
    private static readonly string[] Prev = { EncryptionKeyResolver.DevKey };

    private static string Nest(string plain, string innerKey, string outerKey)
        => EncryptionHelper.Encrypt(EncryptionHelper.Encrypt(plain, innerKey), outerKey);

    [Fact]
    public void reproduce_ชั้นในdevkey_ชั้นนอกคีย์ปัจจุบัน_เดิมอ่านได้แค่enc1_และรายงานบอกกรอกใหม่()
    {
        var stored = Nest(ValidId, EncryptionKeyResolver.DevKey, NewKey);
        // รูปของบั๊ก: ถอดชั้นเดียวได้ ciphertext อีกก้อน (ตัวอ่านเลขเห็นเป็นถอดไม่ได้)
        var once = EncryptionHelper.Decrypt(stored, NewKey);
        Assert.True(EncryptionHelper.IsEncrypted(once));
        Assert.Equal(TaxIdProblem.Undecryptable, EmployeeTaxIdentity.InspectValue(once).Problem);
        // หลังแก้: ตัวตัดสินเดียวแกะชั้นซ้อน ⇒ เลขจริง 13 หลัก
        var r = EncryptedColumnConverter.Inspect(stored, NewKey, Prev, PiiValueKind.ThaiTaxId);
        Assert.Equal(PiiCipherState.Nested, r.State);
        Assert.Equal(ValidId, r.Plain);
        Assert.Equal(1, r.NestedLayers);
        Assert.False(r.NestedUnresolved);
        // รายงาน: ไม่ต้องกรอกใหม่ (รอย้ายคีย์ · ไม่กันการยื่นแบบ)
        Assert.Equal(EmployeeIdentityHealth.Issue.PendingRekey, EmployeeIdentityHealth.Classify(stored, NewKey, Prev)!.Value.Issue);
    }

    [Fact]
    public void ซ้อนสองชั้น_และชั้นนอกบนคีย์เดิม_แกะได้_ไม่เกินความลึกสูงสุด()
    {
        var two = EncryptionHelper.Encrypt(Nest(ValidId, EncryptionKeyResolver.DevKey, NewKey), NewKey);
        var r2 = EncryptedColumnConverter.Inspect(two, NewKey, Prev, PiiValueKind.ThaiTaxId);
        Assert.Equal((PiiCipherState.Nested, ValidId, 2), (r2.State, r2.Plain, r2.NestedLayers));

        var outerOld = Nest("123-4-56789-0", NewKey, EncryptionKeyResolver.DevKey);   // เลขบัญชี · ชั้นนอกคีย์เดิม
        var r3 = EncryptedColumnConverter.Inspect(outerOld, NewKey, Prev);
        Assert.Equal((PiiCipherState.Nested, "123-4-56789-0", 0), (r3.State, r3.Plain, r3.KeyIndex));

        // เกิน MaxNestedDepth ⇒ ไม่แกะ (ค่าในสุดยังเป็น ciphertext) — คงเป็นซ้อนที่ใช้ไม่ได้
        var deep = ValidId;
        for (var i = 0; i <= EncryptedColumnConverter.MaxNestedDepth + 1; i++) deep = EncryptionHelper.Encrypt(deep, NewKey);
        var rd = EncryptedColumnConverter.Inspect(deep, NewKey, Prev, PiiValueKind.ThaiTaxId);
        Assert.True(rd.NestedUnresolved);
        Assert.NotEqual(PiiCipherState.Nested, rd.State);
        Assert.True(EncryptedColumnConverter.IsUnreadable(rd.Plain));
    }

    [Fact]
    public void ทิศตรงข้าม_ชั้นในถอดไม่ออก_หรือเลขผิด_ยังรายงานให้กรอกใหม่_ไม่แต่งค่า()
    {
        var lostInner = Nest(ValidId, "r274-10-key-nobody-has", NewKey);
        var a = EncryptedColumnConverter.Inspect(lostInner, NewKey, Prev, PiiValueKind.ThaiTaxId);
        Assert.Equal(PiiCipherState.Current, a.State);
        Assert.True(a.NestedUnresolved);
        Assert.True(EncryptedColumnConverter.IsUnreadable(a.Plain));        // ผู้อ่านปลายทางเห็นถอดไม่ได้ — ไม่ตัดตัวเลข
        Assert.Equal(EmployeeIdentityHealth.Issue.NestedCipher, EmployeeIdentityHealth.Classify(lostInner, NewKey, Prev)!.Value.Issue);

        // ชั้นในถอดได้แต่ไม่ใช่เลข 13 หลักที่ผ่าน mod-11 ⇒ เลขประจำตัว: ไม่รับ
        foreach (var bad in new[] { BadChecksum, "12345", "abc1101700001237" })
        {
            var nested = Nest(bad, EncryptionKeyResolver.DevKey, NewKey);
            var r = EncryptedColumnConverter.Inspect(nested, NewKey, Prev, PiiValueKind.ThaiTaxId);
            Assert.True(r.NestedUnresolved, bad);
            Assert.NotEqual(bad, r.Plain);
            Assert.Equal(EmployeeIdentityHealth.Issue.NestedCipher, EmployeeIdentityHealth.Classify(nested, NewKey, Prev)!.Value.Issue);
        }
        // คอลัมน์ข้อความ (เลขบัญชี) — plaintext ที่ไม่ใช่ ciphertext รับได้
        Assert.Equal(PiiCipherState.Nested, EncryptedColumnConverter.Inspect(Nest("12345", EncryptionKeyResolver.DevKey, NewKey), NewKey, Prev).State);
    }

    [Fact]
    public void ทิศตรงข้าม_แถวปกติไม่ถูกแตะ()
    {
        var cur = EncryptionHelper.Encrypt(ValidId, NewKey);
        Assert.Equal((PiiCipherState.Current, ValidId, 0, false),
            EncryptedColumnConverter.Inspect(cur, NewKey, Prev, PiiValueKind.ThaiTaxId) is var r ? (r.State, r.Plain, r.NestedLayers, r.NestedUnresolved) : default);
        Assert.Equal(PiiCipherState.Previous, EncryptedColumnConverter.Inspect(EncryptionHelper.Encrypt(ValidId, EncryptionKeyResolver.DevKey), NewKey, Prev).State);
        Assert.Equal(PiiCipherState.Plaintext, EncryptedColumnConverter.Inspect(ValidId, NewKey, Prev).State);
        Assert.Null(EmployeeIdentityHealth.Classify(cur, NewKey, Prev));
    }

    [Fact]
    public void ciphertextที่ผู้ใช้ส่งมาหลังรอบนี้_ห่อพร้อมเครื่องหมาย_ฝั่งอ่านไม่แกะ_ปิด_oracle()
    {
        // ตัวอย่าง: ciphertext ของแถวอื่น (ถอดได้ด้วย dev key) ถูกส่งมาเป็นเลขบัญชีผ่านทางเข้าที่ข้ามด่าน ⇒ converter ห่อพร้อมเครื่องหมาย ⇒ อ่านกลับได้ ciphertext เดิม
        var victim = EncryptionHelper.Encrypt(ValidId, EncryptionKeyResolver.DevKey);
        var conv = new EncryptedColumnConverter(PiiValueKind.Text);
        var stored = (string)conv.ConvertToProvider(victim)!;
        Assert.Equal(victim, (string?)conv.ConvertFromProvider(stored));
        var (cur, prev) = EncryptedColumnConverter.Keys;
        var r = EncryptedColumnConverter.Inspect(stored, cur, prev.Concat(Prev).ToArray());
        Assert.True(r.NestedUnresolved);
        Assert.Equal(victim, r.Plain);
        // ทิศตรงข้าม: ค่าธรรมดาที่ไม่ใช่ ciphertext ไม่ถูกห่อพร้อมเครื่องหมาย (อ่าน/เขียนตามเดิม)
        var plain = (string)conv.ConvertToProvider(ValidId)!;
        Assert.False(EncryptionHelper.Decrypt(plain, cur).StartsWith(EncryptedColumnConverter.UserCipherMarker, StringComparison.Ordinal));
        Assert.Equal(ValidId, (string?)conv.ConvertFromProvider(plain));
    }

    [Fact]
    public void converter_ชนิดเลขประจำตัว_อ่านแถวซ้อนรุ่นเก่าได้เลขจริง()
    {
        var (cur, _) = EncryptedColumnConverter.Keys;
        var legacyNested = Nest(ValidId, cur, cur);   // ซ้อนรุ่นเก่า (ไม่มีเครื่องหมาย) — ทั้งสองชั้นถอดด้วยคีย์ของ converter ได้
        var conv = new EncryptedColumnConverter(PiiValueKind.ThaiTaxId);
        Assert.Equal(PiiValueKind.ThaiTaxId, conv.Kind);
        Assert.Equal(ValidId, (string?)conv.ConvertFromProvider(legacyNested));
        // ทิศตรงข้าม: ชั้นในเป็นเลขผิด ⇒ คอลัมน์เลขประจำตัวคืน ciphertext ชั้นใน (ถอดไม่ได้ในสายตาผู้อ่าน) · คอลัมน์ข้อความคืนค่าที่แกะได้
        var badNested = Nest(BadChecksum, cur, cur);
        Assert.True(EncryptedColumnConverter.IsUnreadable((string?)conv.ConvertFromProvider(badNested)));
        Assert.Equal(BadChecksum, (string?)new EncryptedColumnConverter().ConvertFromProvider(badNested));
    }

    // ───────── ด่านฝั่งเขียน (กันเกิดซ้ำ) ─────────

    [Fact]
    public void ฝั่งเขียน_ciphertextของแถวนี้เองส่งกลับมาเป๊ะ_คงค่าเดิม_ciphertextอื่นปฏิเสธพร้อมเหตุจริง()
    {
        var stuck = EncryptionHelper.Encrypt(ValidId, "r274-10-key-nobody-has");   // ค่าในแถวที่ถอดไม่ได้ (ฟอร์มแสดง enc1:…)
        var keep = EmployeeRecordEdit.ThaiIdNumber(stuck, stuck, "เลขบัตรประชาชน");
        Assert.False(keep.Changes);
        Assert.Null(keep.Error);                                                   // เดิม: ปฏิเสธทั้งคำขอ ⇒ แก้ช่องอื่นไม่ได้
        Assert.False(EmployeeRecordEdit.SsoInsuredNumber(stuck, stuck).Changes);
        Assert.Null(EmployeeRecordEdit.SsoInsuredNumber(stuck, stuck).Error);
        Assert.True(EmployeeRecordEdit.IsEchoOfStoredUnreadable(" " + stuck + " ", stuck));

        // ciphertext อื่น (ไม่ใช่ค่าของแถวนี้) ⇒ ปฏิเสธ · ข้อความบอกสาเหตุจริง (ไม่ใช่ "เลขที่ถูกปิดบัง")
        var other = EncryptionHelper.Encrypt("X" + ValidId, "k");
        var rej = EmployeeRecordEdit.ThaiIdNumber(other, stuck, "เลขบัตรประชาชน");
        Assert.Contains("enc1:", rej.Error);
        Assert.DoesNotContain("ปิดบัง", rej.Error);
        Assert.NotNull(EmployeeRecordEdit.SsoInsuredNumber(other, null).Error);
        // รูปรุ่นก่อน (ไม่มีคำนำหน้า) ก็ปฏิเสธ
        var legacy = other[EncryptionHelper.CipherPrefix.Length..];
        Assert.Contains("ciphertext", EmployeeRecordEdit.CipherInputProblem(legacy, "เลขบัญชีธนาคาร"));
        Assert.False(EmployeeRecordEdit.IsEchoOfStoredUnreadable(other, stuck));
        Assert.False(EmployeeRecordEdit.IsEchoOfStoredUnreadable(ValidId, ValidId));    // ค่าปกติไม่ใช่ "ciphertext echo"

        // ทิศตรงข้าม: ค่าจริงผ่าน · แถวที่ถอดไม่ได้แก้เป็นเลขจริงได้
        Assert.Null(EmployeeRecordEdit.CipherInputProblem("123-4-56789-0", "เลขบัญชีธนาคาร"));
        Assert.Null(EmployeeRecordEdit.CipherInputProblem(null, "เลขบัญชีธนาคาร"));
        var fix = EmployeeRecordEdit.ThaiIdNumber(ValidId, stuck, "เลขบัตรประชาชน");
        Assert.True(fix.Changes);
        Assert.Equal(ValidId, fix.Value);
    }
}
