using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class TwoFactorStoreTests : IDisposable
{
    private static readonly Guid Ann = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("b0000000-0000-0000-0000-000000000002");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-2fa-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly IDataProtectionProvider _dp = new EphemeralDataProtectionProvider();

    public TwoFactorStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string StateFile => Path.Combine(_dir, "twofactor.json");

    private TwoFactorStore New() => new(StateFile, _dp, _time);

    private string CodeNow(byte[] secret, int stepOffset = 0) => Totp.Code(secret, Totp.StepAt(_time.GetUtcNow()) + stepOffset);

    private (TwoFactorStore Store, byte[] Secret, IReadOnlyList<string> Backup) Enrolled(Guid who, string name = "Ann")
    {
        var store = New();
        var (secret, _) = store.BeginEnrol(who, name, "DreamGrid");
        var backup = store.ConfirmEnrol(who, CodeNow(secret));
        Assert.NotNull(backup);
        return (store, secret, backup);
    }

    [Fact]
    public void Enrol_confirm_then_verify()
    {
        var store = New();
        Assert.Equal(TwoFactorState.Off, store.StateOf(Ann));

        var (secret, uri) = store.BeginEnrol(Ann, "Ann Admin", "DreamGrid");
        Assert.StartsWith("otpauth://totp/DreamGrid:Ann%20Admin?secret=" + Totp.Base32(secret), uri);
        Assert.Equal(TwoFactorState.Off, store.StateOf(Ann)); // not on until confirmed

        var backup = store.ConfirmEnrol(Ann, CodeNow(secret));
        Assert.NotNull(backup);
        Assert.Equal(10, backup.Count);
        Assert.Equal(TwoFactorState.On, store.StateOf(Ann));
        Assert.Equal(TwoFactorState.Off, store.StateOf(Bob));

        // The confirming code's step is used up; the next step's code works; a new store (a restart) sees the same.
        Assert.False(store.VerifyCode(Ann, CodeNow(secret), out _));
        var later = New();
        Assert.Equal(TwoFactorState.On, later.StateOf(Ann));
        Assert.True(later.VerifyCode(Ann, CodeNow(secret, 1), out var usedBackup));
        Assert.False(usedBackup);
        Assert.False(later.VerifyCode(Ann, CodeNow(secret, 1), out _)); // once
        Assert.Equal([(Ann, "Ann Admin")], later.Enrolled());
    }

    [Fact]
    public void Wrong_confirm_code_keeps_off()
    {
        var store = New();
        var (secret, _) = store.BeginEnrol(Ann, "Ann", "DreamGrid");
        var wrong = CodeNow(secret) == "123456" ? "654321" : "123456";
        Assert.Null(store.ConfirmEnrol(Ann, wrong));
        Assert.Equal(TwoFactorState.Off, store.StateOf(Ann));
        Assert.Empty(store.Enrolled());
        Assert.False(store.VerifyCode(Ann, CodeNow(secret), out _)); // an unconfirmed secret signs nobody in
        // Confirming with the right code afterwards still works.
        Assert.NotNull(store.ConfirmEnrol(Ann, CodeNow(secret)));
        Assert.Equal(TwoFactorState.On, store.StateOf(Ann));
    }

    [Fact]
    public void Confirm_without_begin_is_null()
    {
        Assert.Null(New().ConfirmEnrol(Ann, "123456"));
    }

    [Fact]
    public void Backup_code_works_once()
    {
        var (store, secret, backup) = Enrolled(Ann);
        var code = backup[3];
        Assert.True(store.VerifyCode(Ann, code.ToUpperInvariant(), out var usedBackup));
        Assert.True(usedBackup);
        Assert.False(store.VerifyCode(Ann, code, out _));
        Assert.False(New().VerifyCode(Ann, code, out _)); // gone from the file too
        Assert.True(New().VerifyCode(Ann, backup[4].Replace("-", ""), out _)); // dash optional
        Assert.False(store.VerifyCode(Bob, backup[5], out _)); // someone else's code, or no account
        Assert.True(store.VerifyCode(Ann, CodeNow(secret, 1), out var app));
        Assert.False(app);
    }

    [Fact]
    public void Secret_and_backup_codes_not_in_file_in_clear()
    {
        var store = New();
        var (secret, _) = store.BeginEnrol(Ann, "Ann", "DreamGrid");
        var pending = File.ReadAllText(StateFile);
        Assert.DoesNotContain(Totp.Base32(secret), pending);
        Assert.DoesNotContain(Convert.ToBase64String(secret), pending);

        var backup = store.ConfirmEnrol(Ann, CodeNow(secret))!;
        var text = File.ReadAllText(StateFile);
        Assert.DoesNotContain(Totp.Base32(secret), text);
        Assert.DoesNotContain(Convert.ToBase64String(secret), text);
        Assert.DoesNotContain(Convert.ToHexStringLower(secret), text);
        foreach (var c in backup)
        {
            Assert.DoesNotContain(c, text);
            Assert.DoesNotContain(c.Replace("-", ""), text);
            Assert.Contains(BackupCodes.Hash(c), text);
        }
    }

    [Fact]
    public void Damaged_file_is_unknown_state()
    {
        var (store, secret, backup) = Enrolled(Ann);
        File.WriteAllText(StateFile, "{ this is not json");
        Assert.Equal(TwoFactorState.Unknown, store.StateOf(Ann));
        Assert.Equal(TwoFactorState.Unknown, store.StateOf(Bob));
        Assert.False(store.VerifyCode(Ann, CodeNow(secret, 1), out _));
        Assert.False(store.VerifyCode(Ann, backup[0], out _));
        Assert.Empty(store.Enrolled());
        // Nothing overwrites the damaged file.
        Assert.Throws<InvalidOperationException>(() => store.BeginEnrol(Bob, "Bob", "DreamGrid"));
        Assert.Throws<InvalidOperationException>(() => store.Disable(Ann));
        Assert.Equal("{ this is not json", File.ReadAllText(StateFile));
    }

    [Fact]
    public void Valid_json_of_the_wrong_shape_is_unknown_state()
    {
        File.WriteAllText(StateFile, "[1,2,3]");
        Assert.Equal(TwoFactorState.Unknown, New().StateOf(Ann));
        File.WriteAllText(StateFile, "null");
        Assert.Equal(TwoFactorState.Unknown, New().StateOf(Ann));
    }

    [Fact]
    public void MoveAsideIfDamaged_then_off()
    {
        File.WriteAllText(StateFile, "garbage");
        TwoFactorStore.MoveAsideIfDamaged(StateFile);
        Assert.False(File.Exists(StateFile));
        var moved = Directory.GetFiles(_dir, "twofactor.json.damaged-*");
        Assert.Single(moved);
        Assert.Matches(@"twofactor\.json\.damaged-\d{14}$", moved[0]);
        Assert.Equal("garbage", File.ReadAllText(moved[0]));

        var store = New();
        Assert.Equal(TwoFactorState.Off, store.StateOf(Ann));
        Assert.NotNull(store.BeginEnrol(Ann, "Ann", "DreamGrid").Secret);
    }

    [Fact]
    public void MoveAsideIfDamaged_leaves_a_good_or_missing_file()
    {
        TwoFactorStore.MoveAsideIfDamaged(StateFile); // missing: nothing happens
        var (store, _, _) = Enrolled(Ann);
        TwoFactorStore.MoveAsideIfDamaged(StateFile);
        Assert.True(File.Exists(StateFile));
        Assert.Empty(Directory.GetFiles(_dir, "*.damaged-*"));
        Assert.Equal(TwoFactorState.On, store.StateOf(Ann));
    }

    [Fact]
    public void Disable_turns_off_and_forgets()
    {
        var (store, secret, backup) = Enrolled(Ann);
        var bob = store.BeginEnrol(Bob, "Bob", "DreamGrid");
        store.ConfirmEnrol(Bob, CodeNow(bob.Secret));
        store.Disable(Ann);
        Assert.Equal(TwoFactorState.Off, store.StateOf(Ann));
        Assert.Equal(TwoFactorState.On, store.StateOf(Bob));
        Assert.False(store.VerifyCode(Ann, backup[0], out _));
        Assert.False(store.VerifyCode(Ann, CodeNow(secret, 1), out _));
        Assert.Equal([(Bob, "Bob")], store.Enrolled());
        store.Disable(Ann); // already off: no error
    }

    [Fact]
    public void Re_enrolling_keeps_the_old_secret_until_confirmed()
    {
        var (store, oldSecret, _) = Enrolled(Ann);
        var (newSecret, _) = store.BeginEnrol(Ann, "Ann", "DreamGrid");
        Assert.Equal(TwoFactorState.On, store.StateOf(Ann));
        Assert.True(store.VerifyCode(Ann, CodeNow(oldSecret, 1), out _));
        Assert.NotNull(store.ConfirmEnrol(Ann, CodeNow(newSecret, 1)));
        Assert.False(store.VerifyCode(Ann, CodeNow(oldSecret, 1), out _));
    }

    [Fact]
    public void Another_instance_sees_changes_at_once()
    {
        var a = New();
        var b = New();
        Assert.Equal(TwoFactorState.Off, b.StateOf(Ann));
        var (secret, _) = a.BeginEnrol(Ann, "Ann", "DreamGrid");
        a.ConfirmEnrol(Ann, CodeNow(secret));
        Assert.Equal(TwoFactorState.On, b.StateOf(Ann));
        a.Disable(Ann);
        Assert.Equal(TwoFactorState.Off, b.StateOf(Ann));
    }

    [Fact]
    public void New_backup_codes_replace_all_and_need_an_app_code()
    {
        var (store, secret, old) = Enrolled(Ann);
        Assert.Null(store.RegenerateBackupCodes(Ann, old[0])); // a backup code is not enough
        Assert.True(store.VerifyCode(Ann, old[0], out _)); // and was not used up by trying
        var wrong = CodeNow(secret, 1) == "123456" ? "654321" : "123456";
        Assert.Null(store.RegenerateBackupCodes(Ann, wrong));
        Assert.Null(store.RegenerateBackupCodes(Bob, CodeNow(secret, 1))); // no two-factor

        var fresh = store.RegenerateBackupCodes(Ann, CodeNow(secret, 1));

        Assert.NotNull(fresh);
        Assert.Equal(BackupCodes.Count, fresh.Count);
        Assert.False(store.VerifyCode(Ann, old[1], out _)); // every old one is gone
        Assert.True(store.VerifyCode(Ann, fresh[0], out var usedBackup));
        Assert.True(usedBackup);
        Assert.False(store.VerifyCode(Ann, CodeNow(secret, 1), out _)); // the step it used is spent
        Assert.Null(store.RegenerateBackupCodes(Ann, CodeNow(secret, 1)));
        Assert.Equal(TwoFactorState.On, New().StateOf(Ann));
    }

    [Fact]
    public void New_backup_codes_on_a_damaged_file_throw()
    {
        var (store, secret, _) = Enrolled(Ann);
        File.WriteAllText(StateFile, "{ this is not json");
        Assert.Throws<InvalidOperationException>(() => store.RegenerateBackupCodes(Ann, CodeNow(secret, 1)));
        Assert.Equal("{ this is not json", File.ReadAllText(StateFile));
    }

    [Fact]
    public void Confirm_needs_the_secret_this_browser_was_shown()
    {
        var store = New();
        var (shown, _) = store.BeginEnrol(Ann, "Ann", "DreamGrid");
        Assert.True(store.IsPending(Ann, shown));
        // Setup started again elsewhere: the secret shown here is no longer the one waiting.
        var (other, _) = store.BeginEnrol(Ann, "Ann", "DreamGrid");
        Assert.False(store.IsPending(Ann, shown));
        Assert.True(store.IsPending(Ann, other));
        Assert.False(store.IsPending(Bob, other));

        Assert.Null(store.ConfirmEnrol(Ann, CodeNow(other), shown));
        Assert.Equal(TwoFactorState.Off, store.StateOf(Ann));
        Assert.NotNull(store.ConfirmEnrol(Ann, CodeNow(other), other));
        Assert.False(store.IsPending(Ann, other)); // confirmed: nothing waits
    }

    [Fact]
    public void A_different_data_protection_key_cannot_verify()
    {
        var (_, secret, backup) = Enrolled(Ann);
        var other = new TwoFactorStore(StateFile, new EphemeralDataProtectionProvider(), _time);
        Assert.Equal(TwoFactorState.On, other.StateOf(Ann)); // still on: fail closed
        Assert.False(other.VerifyCode(Ann, CodeNow(secret, 1), out _));
        Assert.True(other.VerifyCode(Ann, backup[0], out _)); // backup codes are hashes, no key needed
    }
}
