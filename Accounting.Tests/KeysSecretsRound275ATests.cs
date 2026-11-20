using Accounting.Helpers;
using Accounting.Services.Background;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 275 แพ็ก A (คีย์และความลับ · T4 H-1 · M-1 · M-2 · M-4 · M-5) — pure:
/// ห่วงโซ่คีย์ถอดตัวเดียว (PII = ความลับ) · คีย์เดิมหลายตัว · ลายนิ้วมือคีย์ · ด่านเปิดใช้ความลับ · DTO พนักงานไม่ส่ง ciphertext · ข้อความไม่ชี้ env/URL
/// </summary>
public class KeysSecretsRound275ATests
{
    private const string K0 = "r275a-key-K0";
    private const string K1 = "r275a-key-K1";
    private const string K2 = "r275a-key-K2";

    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
        => n => vars.FirstOrDefault(v => v.Name == n).Value;

    // ═══ H-1: ห่วงโซ่ตัวเดียว ═══

    [Fact]
    public void DecryptKeyChain_มี_PREVIOUS_ก่อน_แล้วเลขกำกับ_แล้วคีย์ตกทอด_ไม่มีคีย์ปัจจุบัน()
    {
        var chain = EncryptionKeyResolver.DecryptKeyChain(K2,
            Env(("ENCRYPTION_KEY", K2), (EncryptionKeyResolver.PreviousKeyEnv, K1), (EncryptionKeyResolver.PreviousKeyEnv + "_2", K0)), "cfg-key", null);
        Assert.Equal(new[] { K1, K0, "cfg-key", EncryptionKeyResolver.DevKey }, chain);
        Assert.DoesNotContain(K2, chain);
    }

    [Fact]
    public void DecryptKeyChain_PREVIOUS_หลายบรรทัด_แยกเป็นหลายคีย์_และ_config_ใช้เมื่อไม่มี_env()
    {
        var multi = EncryptionKeyResolver.DecryptKeyChain(K2, Env((EncryptionKeyResolver.PreviousKeyEnv, K1 + "\n" + K0 + "\n")), null, null);
        Assert.Contains(K1, multi);
        Assert.Contains(K0, multi);
        var fromConfig = EncryptionKeyResolver.DecryptKeyChain(K2, Env(), null, K1);
        Assert.Equal(K1, fromConfig[0]);
        // ทิศตรงข้าม: คีย์เดียวไม่มีบรรทัดใหม่ = รายการเดิมของรอบ 274 (ลำดับ/เนื้อหาไม่เปลี่ยน)
        Assert.Equal(EncryptionKeyResolver.PiiPreviousKeys(K2, K1, null, null),
            EncryptionKeyResolver.DecryptKeyChain(K2, Env((EncryptionKeyResolver.PreviousKeyEnv, K1)), null, null));
    }

    [Fact]
    public void H1_หมุนคีย์_env_ไป_env_ความลับที่เข้ารหัสด้วยคีย์เดิมยังใช้ได้และย้ายได้_เดิมเป็น_WrongKey()
    {
        var stored = new SecretProtector(K1).Protect("smtp-app-password");
        // บั๊กเดิม: SecretProtector ใช้ LegacyCandidates (ไม่มี PREVIOUS)
        var old = new SecretProtector(K2, null, EncryptionKeyResolver.LegacyCandidates(K2, K2, null));
        Assert.Equal(SecretState.WrongKey, old.Inspect(stored));
        Assert.Null(old.Reprotect(stored));

        var p = new SecretProtector(K2, null, EncryptionKeyResolver.DecryptKeyChain(K2, Env(("ENCRYPTION_KEY", K2), (EncryptionKeyResolver.PreviousKeyEnv, K1)), null, null));
        Assert.Equal(SecretState.LegacyKey, p.Inspect(stored));
        Assert.Equal("smtp-app-password", p.Unprotect(stored));
        var migrated = p.Reprotect(stored);
        Assert.NotNull(migrated);
        Assert.Equal("smtp-app-password", new SecretProtector(K2).Unprotect(migrated));   // ไม่ต้องพึ่งคีย์เดิมอีก
        // ค่าบนคีย์ปัจจุบันแล้ว = ไม่แตะ (ทิศตรงข้าม)
        Assert.Null(p.Reprotect(migrated));
    }

    // ═══ M-4: ลายนิ้วมือ ═══

    [Fact]
    public void Fingerprint_คงที่ต่อคีย์_ต่างกันเมื่อคีย์ต่าง_ไม่มีคีย์ในผล()
    {
        Assert.Equal(EncryptionKeyResolver.Fingerprint(K1), EncryptionKeyResolver.Fingerprint(K1));
        Assert.NotEqual(EncryptionKeyResolver.Fingerprint(K1), EncryptionKeyResolver.Fingerprint(K2));
        Assert.Equal(16, EncryptionKeyResolver.Fingerprint(K1).Length);
        Assert.DoesNotContain("K1", EncryptionKeyResolver.Fingerprint(K1));
        var p = new SecretProtector(K2, null, new[] { K1 });
        Assert.Equal(EncryptionKeyResolver.Fingerprint(K2), p.CurrentKeyFingerprint);
        Assert.Contains(EncryptionKeyResolver.Fingerprint(K1), p.PreviousKeyFingerprints);
    }

    [Theory]
    [InlineData(null, null, "c", KeyFingerprintVerdict.FirstSeen, true)]
    [InlineData("c", null, "c", KeyFingerprintVerdict.Match, true)]
    [InlineData("p", null, "c", KeyFingerprintVerdict.Rotated, true)]
    [InlineData("x", null, "c", KeyFingerprintVerdict.Mismatch, false)]
    [InlineData("x", "c", "c", KeyFingerprintVerdict.RotatedBack, false)]
    [InlineData("c", null, null, KeyFingerprintVerdict.NotChecked, true)]
    public void FingerprintRule_ตัดสินและสิทธิ์ย้ายคีย์(string? stored, string? storedPrev, string? current, KeyFingerprintVerdict expected, bool allows)
    {
        var v = EncryptionKeyFingerprintRule.Evaluate(stored, storedPrev, current, new[] { "p" });
        Assert.Equal(expected, v);
        Assert.Equal(allows, EncryptionKeyFingerprintRule.AllowsRekey(v));
        Assert.Equal(EncryptionKeyFingerprintRule.IsProblem(v), EncryptionKeyFingerprintRule.AdminMessage(v) != null);
        // ข้อความถึงผู้ใช้บริษัทไม่มีชื่อ env (M-1)
        Assert.DoesNotContain("ENCRYPTION_KEY", EncryptionKeyFingerprintRule.TenantMessage(v) ?? "");
    }

    // ═══ M-5: ด่านเปิดใช้ความลับ ═══

    [Fact]
    public void SecretUse_ว่าง_ไม่ได้ตั้ง_ถอดไม่ได้_ล้มพร้อมเหตุจริง_ใช้ได้_คืนค่า()
    {
        var p = new SecretProtector(K2);
        var lost = new SecretProtector(K1).Protect("whsec");

        var empty = SecretUse.Open(p, "รหัสลับ webhook", null);
        Assert.True(empty.NotConfigured);
        Assert.False(empty.Unusable);
        Assert.Null(SecretUse.Optional(p, "รหัสลับ webhook", ""));
        Assert.Equal(SecretUse.RuleCode, Assert.Throws<BusinessRuleException>(() => SecretUse.Require(p, "รหัสลับ webhook", null)).RuleCode);

        var bad = SecretUse.Open(p, "รหัสลับ webhook", lost);
        Assert.True(bad.Unusable);
        Assert.Null(bad.Value);
        Assert.Contains("คีย์เข้ารหัสของระบบถูกเปลี่ยน", bad.Problem);   // เหตุจริงจาก Inspect (WrongKey) ไม่ใช่ "ยังไม่ได้ตั้งค่า"
        Assert.DoesNotContain("ENCRYPTION_KEY", bad.Problem);
        Assert.Throws<BusinessRuleException>(() => SecretUse.Optional(p, "รหัสลับ webhook", lost));   // ห้ามทำเหมือนไม่ได้ตั้ง (ส่งไม่เซ็น)
        Assert.Contains("เสียหาย", SecretUse.Open(p, "x", "enc1:@@@").Problem);

        var good = SecretUse.Open(p, "รหัสลับ webhook", p.Protect("whsec_ok"));
        Assert.True(good.Ok);
        Assert.Equal("whsec_ok", good.Value);
        Assert.Equal("plain-legacy", SecretUse.Require(p, "x", "plain-legacy"));   // plaintext เก่ายังใช้ได้ตามเดิม
    }

    // ═══ M-2: DTO พนักงาน ═══

    [Fact]
    public void ForEmployeeDto_ciphertext_ได้ป้าย_ไม่ใช่ค่าดิบ_ทุกสิทธิ์_ค่าปกติไม่ถูกแตะ()
    {
        var cipher = new SecretProtector("r275a-other").Protect("1101700207030");
        Assert.Equal(EncryptedColumnConverter.UnreadableExportText, EmployeeRecordEdit.ForEmployeeDto(cipher, true, PiiMask.CitizenId));
        Assert.Equal(EncryptedColumnConverter.UnreadableExportText, EmployeeRecordEdit.ForEmployeeDto(cipher, false, PiiMask.CitizenId));
        Assert.Equal("1101700207030", EmployeeRecordEdit.ForEmployeeDto("1101700207030", true, PiiMask.CitizenId));
        Assert.Equal(PiiMask.CitizenId("1101700207030"), EmployeeRecordEdit.ForEmployeeDto("1101700207030", false, PiiMask.CitizenId));
        Assert.Null(EmployeeRecordEdit.ForEmployeeDto(null, true, PiiMask.CitizenId));
    }

    [Fact]
    public void ฟอร์มส่งป้ายกลับ_คงค่าเดิม_กรอกใหม่ทับได้_สร้างใหม่ด้วยป้ายถูกปฏิเสธ_echo_guard_เดิมยังอยู่()
    {
        var cipher = new SecretProtector("r275a-other").Protect("1101700207030");
        var label = EncryptedColumnConverter.UnreadableExportText;
        Assert.False(EmployeeRecordEdit.ThaiIdNumber(label, cipher, "เลขบัตรประชาชน").Changes);   // ไม่แตะช่อง = คงค่าเดิม
        Assert.Null(EmployeeRecordEdit.ThaiIdNumber(label, cipher, "เลขบัตรประชาชน").Error);
        Assert.False(EmployeeRecordEdit.ThaiIdNumber(cipher, cipher, "เลขบัตรประชาชน").Changes);  // echo guard รอบ 274-10 ยังอยู่
        var retyped = EmployeeRecordEdit.ThaiIdNumber("1101700207030", cipher, "เลขบัตรประชาชน");
        Assert.True(retyped.Changes);
        Assert.Equal("1101700207030", retyped.Value);
        Assert.NotNull(EmployeeRecordEdit.ThaiIdNumber(label, null, "เลขบัตรประชาชน").Error);   // ไม่มีค่าเดิม = ป้ายไม่ใช่เลข
        Assert.False(EmployeeRecordEdit.IsEchoOfStoredUnreadable(label, "1101700207030"));      // ค่าปกติ: ป้ายไม่ใช่ "ไม่ได้แก้"
        Assert.False(EmployeeRecordEdit.SsoInsuredNumber(label, cipher).Changes);
    }

    [Fact]
    public void PiiProblemText_บอกเหตุ_ไม่มีชื่อ_env_และ_Missing_ไม่ใช่ปัญหาบนจอ()
    {
        var cipher = new SecretProtector("r275a-other").Protect("1101700207030");
        var text = EmployeeRecordEdit.PiiProblemText(EmployeeTaxIdentity.Inspect(null, cipher), bankUnreadable: true);
        Assert.NotNull(text);
        Assert.Contains(EmployeeTaxIdentity.UndecryptableText, text);
        Assert.Contains("เลขบัญชี", text);
        Assert.DoesNotContain("ENCRYPTION_KEY", text);
        Assert.Null(EmployeeRecordEdit.PiiProblemText(EmployeeTaxIdentity.Inspect(null, null), false));
        Assert.Null(EmployeeRecordEdit.PiiProblemText(EmployeeTaxIdentity.Inspect(null, "1101700207030"), false));
    }

    // ═══ M-1: ข้อความถึงผู้ใช้ไม่ชี้ URL ดิบ/ชื่อ env ═══

    [Fact]
    public void ข้อความถึงผู้ใช้บริษัท_ไม่มี_URL_ดิบหรือชื่อ_env()
    {
        var undecryptable = EmployeeTaxIdentity.InspectValue(new SecretProtector("r275a-other").Protect("1101700207030")).Reason!;
        var blocked = Pnd1FilingData.BlockedMessage("ภ.ง.ด.1", "11/2569", Array.Empty<Pnd1FilingData.BlockedRow>());
        foreach (var t in new[] { undecryptable, blocked, LineFailureAlert.DecryptAdvice, EncryptionHealthText.TenantAdvice })
        {
            Assert.DoesNotContain("ENCRYPTION_KEY", t);
            Assert.DoesNotContain("GET payroll/", t);
        }
        Assert.Contains(EncryptionHealthText.PageName, blocked);
        Assert.Contains("ENCRYPTION_KEY_PREVIOUS", EncryptionHealthText.AdminKeyAdvice);   // ผู้ดูแลแพลตฟอร์มได้ชื่อ env
    }

    [Fact]
    public void Summary_watermark_บอกว่าถอดคีย์เดิมได้เมื่อไม่มีค่าค้าง_และบอกจำนวนที่ใช้ไม่ได้()
    {
        var none = new SecretReprotectPass.Result(3, 1, new Dictionary<string, int> { ["WebhookRegistrations.Secret"] = 1 });
        Assert.Contains("✓ ไม่มีความลับบนคีย์เดิมเหลือ", SecretReprotectJob.Summary(none));
        var some = none with { LegacyKeyRemaining = 2, Unusable = 1 };
        var s = SecretReprotectJob.Summary(some);
        Assert.Contains("ยังอยู่บนคีย์เดิม 2", s);
        Assert.Contains("⛔ ถอดไม่ออกทุกคีย์/เสียหาย 1", s);
        Assert.DoesNotContain("✓", s);
    }
}

/// <summary>รอบ 275 แพ็ก A ฝ่ายค้าน MED-2: เครื่องที่คีย์ไม่ตรงหยุดเขียนค่าเข้ารหัสใหม่ (ความลับ + PII) — อ่านไม่กระทบ · ประตูเป็นสถานะทั้ง process ⇒ รันไม่ขนาน</summary>
[Collection(Accounting.Tests.Db.EncryptionGlobalStateCollection.Name)]
public class EncryptionWriteGuardRound275ATests
{
    [Fact]
    public void Mismatch_ห้ามเขียนความลับและPIIใหม่_อ่านค่าเดิมได้ตามปกติ()
    {
        var p = new SecretProtector("r275a-guard-key");
        var existing = p.Protect("smtp-pass");
        var existingPii = (string?)new EncryptedColumnConverter().ConvertToProvider("1101700207030");
        try
        {
            EncryptionWriteGuard.Apply(KeyFingerprintVerdict.Mismatch);
            var ex = Assert.Throws<EncryptionKeyMismatchException>(() => p.Protect("new-pass"));
            Assert.Equal(503, ex.StatusCode);
            Assert.DoesNotContain("ENCRYPTION_KEY", ex.Message);
            Assert.Throws<EncryptionKeyMismatchException>(() => new EncryptedColumnConverter().ConvertToProvider("1101700207030"));
            // อ่านไม่กระทบ
            Assert.Equal("smtp-pass", p.Unprotect(existing));
            Assert.Equal("1101700207030", (string?)new EncryptedColumnConverter().ConvertFromProvider(existingPii));
            EncryptionWriteGuard.Apply(KeyFingerprintVerdict.RotatedBack);
            Assert.Throws<EncryptionKeyMismatchException>(() => p.Protect("x"));
        }
        finally { EncryptionWriteGuard.Clear(); }
    }

    [Theory]
    [InlineData(KeyFingerprintVerdict.Match)]
    [InlineData(KeyFingerprintVerdict.FirstSeen)]
    [InlineData(KeyFingerprintVerdict.Rotated)]
    [InlineData(KeyFingerprintVerdict.RotationPending)]
    [InlineData(KeyFingerprintVerdict.NotChecked)]
    public void ทิศตรงข้าม_คีย์ไม่มีปัญหา_เขียนได้ตามปกติ(KeyFingerprintVerdict v)
    {
        var p = new SecretProtector("r275a-guard-key");
        try
        {
            EncryptionWriteGuard.Apply(KeyFingerprintVerdict.Mismatch);
            EncryptionWriteGuard.Apply(v);   // ผลตรวจรอบถัดไปปกติ ⇒ เปิดประตูคืน
            Assert.Null(EncryptionWriteGuard.BlockedReason);
            Assert.Equal("ok", p.Unprotect(p.Protect("ok")));
            Assert.NotNull(new EncryptedColumnConverter().ConvertToProvider("1101700207030"));
        }
        finally { EncryptionWriteGuard.Clear(); }
    }

    [Fact]
    public void รอยืนยันการหมุน_ไม่ใช่ปัญหาคีย์_แต่ห้ามย้ายแถว_และหมุนมีผลเมื่อทุกเครื่องรายงานคีย์ใหม่()
    {
        Assert.False(EncryptionKeyFingerprintRule.AllowsRekey(KeyFingerprintVerdict.RotationPending));
        Assert.True(EncryptionKeyFingerprintRule.NeedsAdmin(KeyFingerprintVerdict.RotationPending));
        Assert.False(EncryptionKeyFingerprintRule.IsProblem(KeyFingerprintVerdict.RotationPending));
        Assert.True(EncryptionKeyFingerprintRule.AllInstancesOn("new", new[] { "new", "new" }));
        Assert.False(EncryptionKeyFingerprintRule.AllInstancesOn("new", new[] { "new", "old" }));
        Assert.False(EncryptionKeyFingerprintRule.AllInstancesOn("new", Array.Empty<string>()));
    }
}
