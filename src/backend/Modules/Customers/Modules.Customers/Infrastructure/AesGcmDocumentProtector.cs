using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Customers.Application;
using TravelBooking.Modules.Customers.Domain;

namespace TravelBooking.Modules.Customers.Infrastructure;

/// <summary>
/// <c>Customers:DocumentEncryption</c> (ADR 0020). Key-encryption keys (256-bit, base64) by id, from user-secrets locally
/// and Key Vault in Azure, never from appsettings or code. <c>ActiveKeyId</c> wraps new documents; older keys stay
/// listed to read documents wrapped with them (rotation). No default: until a key is set, documents are refused.
/// </summary>
internal sealed class DocumentEncryptionOptions
{
    public const string SectionName = "Customers:DocumentEncryption";

    public string? ActiveKeyId { get; set; }

    public Dictionary<string, string> Keys { get; set; } = [];
}

/// <summary>
/// Envelope encryption with AES-256-GCM (ADR 0020). Each document gets a fresh data key; the document is encrypted with
/// it, bound to its id (associated data), and the data key is encrypted (wrapped) with the key-encryption key, bound to
/// the document id and key id. Deleting the wrapped key destroys the document (crypto-shredding). Layout of each blob:
/// nonce (12 bytes), tag (16), ciphertext.
/// </summary>
internal sealed class AesGcmDocumentProtector(IOptionsMonitor<DocumentEncryptionOptions> options) : IDocumentProtector
{
    private const int _keySize = 32;
    private const int _nonceSize = 12;
    private const int _tagSize = 16;
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public Result<ProtectedDocument, DocumentProtectionError> Protect(Guid documentId, TravelDocumentDetails details)
    {
        if (options.CurrentValue.ActiveKeyId is not { Length: > 0 } keyId || KeyFor(keyId) is not { } kek)
        {
            return Result<ProtectedDocument, DocumentProtectionError>.Failure(DocumentProtectionError.NotConfigured);
        }

        var dataKey = RandomNumberGenerator.GetBytes(_keySize);
        try
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(details, _json);
            var ciphertext = Seal(dataKey, plaintext, documentId.ToByteArray());
            CryptographicOperations.ZeroMemory(plaintext);
            var wrapped = Seal(kek, dataKey, WrapContext(documentId, keyId));
            return Result<ProtectedDocument, DocumentProtectionError>.Success(new ProtectedDocument(keyId, wrapped, ciphertext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public Result<TravelDocumentDetails, DocumentProtectionError> Unprotect(Guid documentId, ProtectedDocument document)
    {
        if (KeyFor(document.KeyId) is not { } kek)
        {
            return Result<TravelDocumentDetails, DocumentProtectionError>.Failure(DocumentProtectionError.NotConfigured);
        }

        byte[]? dataKey = null;
        byte[]? plaintext = null;
        try
        {
            dataKey = Open(kek, document.WrappedKey, WrapContext(documentId, document.KeyId));
            plaintext = Open(dataKey, document.Ciphertext, documentId.ToByteArray());
            return JsonSerializer.Deserialize<TravelDocumentDetails>(plaintext, _json) is { } details
                ? Result<TravelDocumentDetails, DocumentProtectionError>.Success(details)
                : Result<TravelDocumentDetails, DocumentProtectionError>.Failure(DocumentProtectionError.Unreadable);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException)
        {
            return Result<TravelDocumentDetails, DocumentProtectionError>.Failure(DocumentProtectionError.Unreadable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            if (dataKey is not null)
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    /// <summary>
    /// The startup check: every configured key is 256 bits of base64, and an active key id names one of them. Nothing
    /// configured is valid (documents are refused, 503), so hosts without documents need no key; a malformed key is not.
    /// </summary>
    internal static bool IsWellFormed(DocumentEncryptionOptions settings) =>
        settings.Keys.Values.All(key => Decode(key) is { Length: _keySize })
        && (settings.ActiveKeyId is not { Length: > 0 } id || settings.Keys.ContainsKey(id));

    private byte[]? KeyFor(string keyId) =>
        options.CurrentValue.Keys.TryGetValue(keyId, out var encoded) && Decode(encoded) is { Length: _keySize } key ? key : null;

    private static byte[]? Decode(string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[] WrapContext(Guid documentId, string keyId) => [.. documentId.ToByteArray(), .. Encoding.UTF8.GetBytes(keyId)];

    private static byte[] Seal(byte[] key, byte[] plaintext, byte[] associatedData)
    {
        var output = new byte[_nonceSize + _tagSize + plaintext.Length];
        var nonce = output.AsSpan(0, _nonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, _tagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(_nonceSize + _tagSize), output.AsSpan(_nonceSize, _tagSize), associatedData);
        return output;
    }

    private static byte[] Open(byte[] key, byte[] sealedData, byte[] associatedData)
    {
        if (sealedData.Length < _nonceSize + _tagSize)
        {
            throw new CryptographicException("Too short to be a sealed document.");
        }

        var plaintext = new byte[sealedData.Length - _nonceSize - _tagSize];
        using var aes = new AesGcm(key, _tagSize);
        aes.Decrypt(sealedData.AsSpan(0, _nonceSize), sealedData.AsSpan(_nonceSize + _tagSize), sealedData.AsSpan(_nonceSize, _tagSize), plaintext, associatedData);
        return plaintext;
    }
}
