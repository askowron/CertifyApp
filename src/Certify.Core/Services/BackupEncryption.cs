using System.Security.Cryptography;
using System.Text;
using Certify.Core.Localization;

namespace Certify.Core.Services;

/// <summary>
/// Szyfrowanie backupu haslem: PBKDF2-SHA256 (200k) -> AES-256-GCM.
/// Format .cbak: "CBK1" + salt(16) + nonce(12) + ciphertext + tag(16).
/// Zle haslo / naruszony plik = blad uwierzytelnienia tagu.
/// </summary>
public static class BackupEncryption
{
    public const string EncryptedExtension = ".cbak";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CBK1");
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Pbkdf2Iterations = 200_000;
    public const int MinPasswordLength = 8;

    public static bool IsEncrypted(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < Magic.Length) return false;
            Span<byte> buf = stackalloc byte[4];
            return fs.Read(buf) == 4 && buf.SequenceEqual(Magic);
        }
        catch { return false; }
    }

    public static string? ValidatePassword(string? password) =>
        string.IsNullOrEmpty(password) ? UIStrings.T("Pw_Err_Required")
        : password.Length < MinPasswordLength ? UIStrings.T("Pw_Err_Min")
        : null;

    public static async Task EncryptFileAsync(string inputPath, string outputPath, string password, CancellationToken ct = default)
    {
        var plain = await File.ReadAllBytesAsync(inputPath, ct);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
        try
        {
            var cipher = new byte[plain.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plain, cipher, tag);
            using var fs = File.Create(outputPath);
            await fs.WriteAsync(Magic, ct);
            await fs.WriteAsync(salt, ct);
            await fs.WriteAsync(nonce, ct);
            await fs.WriteAsync(cipher, ct);
            await fs.WriteAsync(tag, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static async Task DecryptFileAsync(string inputPath, string outputPath, string password, CancellationToken ct = default)
    {
        var all = await File.ReadAllBytesAsync(inputPath, ct);
        try
        {
            if (all.Length < Magic.Length + SaltSize + NonceSize + TagSize
                || !all.AsSpan(0, 4).SequenceEqual(Magic))
                throw new InvalidOperationException(UIStrings.T("Bk_Err_NotEncrypted"));
            var salt = all.AsSpan(4, SaltSize).ToArray();
            var nonce = all.AsSpan(4 + SaltSize, NonceSize).ToArray();
            var tag = all.AsSpan(all.Length - TagSize, TagSize).ToArray();
            var cipher = all.AsSpan(4 + SaltSize + NonceSize, all.Length - 4 - SaltSize - NonceSize - TagSize).ToArray();
            var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
            try
            {
                var plain = new byte[cipher.Length];
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(nonce, cipher, tag, plain);
                await File.WriteAllBytesAsync(outputPath, plain, ct);
                CryptographicOperations.ZeroMemory(plain);
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException(UIStrings.T("Bk_Err_BadPass"));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(all);
        }
    }
}
