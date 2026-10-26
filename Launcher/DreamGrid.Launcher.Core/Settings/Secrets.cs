using System.Security.Cryptography;

namespace DreamGrid.Launcher.Core.Settings;

public static class Secrets
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>Cryptographically random, [A-Za-z0-9] only.</summary>
    public static string NewPassword(int length = 32) => RandomNumberGenerator.GetString(Alphabet, length);

    public static string NewMachineId() => Guid.NewGuid().ToString("D");
}
