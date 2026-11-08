using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoDeck.Models;

namespace RoDeck.Services;

/// <summary>What goes into a backup file: the accounts (with their sign-ins) plus favourites, presets and layouts.</summary>
public sealed class BackupData
{
    public int Version { get; set; } = 1;
    public DateTime ExportedAt { get; set; } = DateTime.Now;
    public string AppVersion { get; set; } = "";
    public List<Account> Accounts { get; set; } = new();
    public List<Favourite> Favourites { get; set; } = new();
    public List<LaunchPreset> Presets { get; set; } = new();
    public List<LayoutProfile> Layouts { get; set; } = new();
}

/// <summary>
/// Encrypted, portable backup (.rodeck). Unlike vault.dat it is NOT tied to this Windows user, only to the password
/// chosen when exporting, so it can be imported on another PC or after a reinstall.
/// Layout: "ADB1" | iterations (4, little endian) | salt (16) | nonce (12) | tag (16) | AES-256-GCM ciphertext of the JSON.
/// </summary>
public static class BackupService
{
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("ADB1");
    const int SaltSize = 16, NonceSize = 12, TagSize = 16, KeySize = 32, Iterations = 600_000;
    const int HeaderSize = 4 + 4 + SaltSize + NonceSize + TagSize;

    public const string Extension = ".rodeck";
    public const string FileFilter = "RoDeck backup (*.rodeck)|*.rodeck";
    /// <summary>For opening: backups made before the rename end in .altdeck (same format).</summary>
    public const string OpenFilter = "RoDeck backup (*.rodeck, *.altdeck)|*.rodeck;*.altdeck";

    public static void Export(string path, string password, BackupData data)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(data);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = Derive(password, salt, Iterations);
        try
        {
            var cipher = new byte[plain.Length];
            var tag = new byte[TagSize];
            using (var gcm = new AesGcm(key, TagSize)) gcm.Encrypt(nonce, plain, cipher, tag);

            var file = new byte[HeaderSize + cipher.Length];
            Magic.CopyTo(file, 0);
            BitConverter.GetBytes(Iterations).CopyTo(file, 4);
            salt.CopyTo(file, 8);
            nonce.CopyTo(file, 8 + SaltSize);
            tag.CopyTo(file, 8 + SaltSize + NonceSize);
            cipher.CopyTo(file, HeaderSize);

            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, file);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Returns the backup, or null with a message (wrong password, not a backup, damaged).</summary>
    public static BackupData? Import(string path, string password, out string? error)
    {
        error = null;
        byte[] file;
        try { file = File.ReadAllBytes(path); }
        catch (Exception ex) { error = "Couldn't read the file: " + ex.Message; return null; }

        if (file.Length < HeaderSize || !file.AsSpan(0, 4).SequenceEqual(Magic))
        {
            error = "That isn't an RoDeck backup file.";
            return null;
        }
        var iterations = BitConverter.ToInt32(file, 4);
        if (iterations < 10_000 || iterations > 10_000_000) { error = "The backup file is damaged."; return null; }

        var salt = file.AsSpan(8, SaltSize).ToArray();
        var nonce = file.AsSpan(8 + SaltSize, NonceSize);
        var tag = file.AsSpan(8 + SaltSize + NonceSize, TagSize);
        var cipher = file.AsSpan(HeaderSize);
        var plain = new byte[cipher.Length];
        var key = Derive(password, salt, iterations);
        try
        {
            using (var gcm = new AesGcm(key, TagSize)) gcm.Decrypt(nonce, cipher, tag, plain);
            return JsonSerializer.Deserialize<BackupData>(plain) ?? new BackupData();
        }
        catch (CryptographicException) { error = "That password isn't right (or the file was changed)."; return null; }
        catch (JsonException) { error = "The backup opened, but its contents couldn't be read."; return null; }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);
}
