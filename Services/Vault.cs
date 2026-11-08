using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoDeck.Models;

namespace RoDeck.Services;

/// <summary>Thrown when the vault is protected by a master password that has not been entered yet.</summary>
public sealed class VaultLockedException : Exception
{
    public VaultLockedException() : base("The vault is locked. Enter the master password first.") { }
}

/// <summary>
/// The whole account list (cookies included) in %AppData%\RoDeck\vault.dat. Two formats:
///  - default: encrypted with Windows DPAPI (current user), like before;
///  - master password: "ADV2" header, then PBKDF2-SHA256 salt, AES-256-GCM nonce, tag and ciphertext.
/// The key is derived once when the password is entered and kept in memory; every save uses a fresh nonce.
/// </summary>
public sealed class Vault
{
    public static Vault Shared { get; } = new();

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RoDeck");
    static readonly string FilePath = Path.Combine(Dir, "vault.dat");
    // keeps the old name on purpose: vaults saved by AltDeck (RoDeck's old name) were encrypted with it
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AltDeck.vault.v1");

    static readonly byte[] Magic = Encoding.ASCII.GetBytes("ADV2");
    const int SaltSize = 16, NonceSize = 12, TagSize = 16, KeySize = 32;
    const int DefaultIterations = 600_000;   // OWASP's 2023 figure for PBKDF2-HMAC-SHA256

    byte[]? _key;       // set once the password was entered (or chosen)
    byte[]? _salt;
    int _iterations;

    /// <summary>True when vault.dat on disk is locked with a master password.</summary>
    public bool IsPasswordProtected
    {
        get
        {
            try
            {
                if (!File.Exists(FilePath)) return _key != null;
                using var fs = File.OpenRead(FilePath);
                var head = new byte[Magic.Length];
                return fs.Read(head, 0, head.Length) == head.Length && head.AsSpan().SequenceEqual(Magic);
            }
            catch { return false; }
        }
    }

    /// <summary>True while a master password protects the vault and it has not been entered yet.</summary>
    public bool IsLocked => IsPasswordProtected && _key == null;

    // ------------------------------------------------------------------ load

    public List<Account> Load()
    {
        if (!File.Exists(FilePath)) return new List<Account>();
        var bytes = File.ReadAllBytes(FilePath);

        if (StartsWithMagic(bytes))
        {
            if (_key == null) throw new VaultLockedException();
            var plainPw = Decrypt(bytes, _key);   // can't fail for the right key; throws if the file was tampered with
            return JsonSerializer.Deserialize<List<Account>>(plainPw) ?? new List<Account>();
        }

        try
        {
            var plain = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<Account>>(plain) ?? new List<Account>();
        }
        catch (Exception ex) when (ex is CryptographicException || ex is JsonException)
        {
            // Unreadable (other Windows user / corrupt). Keep a backup instead of overwriting it later.
            try { File.Move(FilePath, FilePath + ".bak", overwrite: true); } catch { }
            return new List<Account>();
        }
    }

    /// <summary>Tries the master password. On success the vault stays unlocked for this run.</summary>
    public bool TryUnlock(string password)
    {
        try
        {
            if (!File.Exists(FilePath)) return false;
            var bytes = File.ReadAllBytes(FilePath);
            if (!StartsWithMagic(bytes) || bytes.Length < Magic.Length + 4 + SaltSize + NonceSize + TagSize) return false;

            ReadHeader(bytes, out var iterations, out var salt);
            var key = Derive(password, salt, iterations);
            try { Decrypt(bytes, key); }
            catch (CryptographicException) { CryptographicOperations.ZeroMemory(key); return false; }

            Forget();
            _key = key; _salt = salt; _iterations = iterations;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Checks a password against the unlocked vault (for "change" and "turn off").</summary>
    public bool VerifyPassword(string password)
    {
        if (_key == null || _salt == null) return false;
        var test = Derive(password, _salt, _iterations);
        try { return CryptographicOperations.FixedTimeEquals(test, _key); }
        finally { CryptographicOperations.ZeroMemory(test); }
    }

    // ------------------------------------------------------------------ save

    public void Save(IEnumerable<Account> accounts)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(accounts.ToList());
        try
        {
            byte[] data;
            if (_key != null && _salt != null) data = Encrypt(plain, _key, _salt, _iterations);
            else if (IsPasswordProtected) throw new VaultLockedException();   // never overwrite a locked vault
            else data = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            WriteAtomic(data);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>Locks the vault with a new master password (the accounts are re-saved right away).</summary>
    public void EnablePassword(string password, IEnumerable<Account> accounts)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Derive(password, salt, DefaultIterations);
        Forget();
        _key = key; _salt = salt; _iterations = DefaultIterations;
        Save(accounts);
    }

    /// <summary>Same as EnablePassword, for an already locked vault (new salt, new key).</summary>
    public void ChangePassword(string newPassword, IEnumerable<Account> accounts) => EnablePassword(newPassword, accounts);

    /// <summary>Goes back to DPAPI-only protection.</summary>
    public void DisablePassword(IEnumerable<Account> accounts)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(accounts.ToList());
        try
        {
            WriteAtomic(ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
            Forget();
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    // ------------------------------------------------------------------ crypto

    static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);

    // layout: magic(4) | iterations(4, little endian) | salt(16) | nonce(12) | tag(16) | ciphertext
    static byte[] Encrypt(byte[] plain, byte[] key, byte[] salt, int iterations)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var gcm = new AesGcm(key, TagSize))
            gcm.Encrypt(nonce, plain, cipher, tag);

        var header = Magic.Length + 4 + SaltSize;
        var data = new byte[header + NonceSize + TagSize + cipher.Length];
        Magic.CopyTo(data, 0);
        BitConverter.GetBytes(iterations).CopyTo(data, Magic.Length);
        salt.CopyTo(data, Magic.Length + 4);
        nonce.CopyTo(data, header);
        tag.CopyTo(data, header + NonceSize);
        cipher.CopyTo(data, header + NonceSize + TagSize);
        return data;
    }

    static byte[] Decrypt(byte[] data, byte[] key)
    {
        var header = Magic.Length + 4 + SaltSize;
        var nonce = data.AsSpan(header, NonceSize);
        var tag = data.AsSpan(header + NonceSize, TagSize);
        var cipher = data.AsSpan(header + NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, cipher, tag, plain);   // CryptographicException when the key or the data is wrong
        return plain;
    }

    static bool StartsWithMagic(byte[] bytes) => bytes.Length >= Magic.Length && bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    static void ReadHeader(byte[] bytes, out int iterations, out byte[] salt)
    {
        iterations = BitConverter.ToInt32(bytes, Magic.Length);
        if (iterations < 10_000 || iterations > 10_000_000) throw new CryptographicException("Bad vault header.");
        salt = bytes.AsSpan(Magic.Length + 4, SaltSize).ToArray();
    }

    void Forget()
    {
        if (_key != null) CryptographicOperations.ZeroMemory(_key);
        _key = null; _salt = null; _iterations = 0;
    }

    static void WriteAtomic(byte[] data)
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, FilePath, overwrite: true);
    }
}
