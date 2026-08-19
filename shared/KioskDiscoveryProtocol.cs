using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MulletHop.KioskDiscovery;

internal static class KioskDiscoveryProtocol
{
    public const int Version = 1;
    public const int ControllerPort = 47832;
    public const string ControllerBasePath = "/mullethop/";
    public const string AnnouncementPath = "api/discovery/announce";
    private const string ManualSetupPrefix = "MHK1:";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string CreateManualSetupCode(
        string controllerAddress,
        string pairingKey,
        string controllerName)
    {
        var payload = new KioskManualSetupPayload
        {
            ControllerAddress = NormalizeControllerAddress(controllerAddress),
            PairingKey = ValidatePairingKey(pairingKey),
            ControllerName = CleanControllerName(controllerName)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return ManualSetupPrefix + Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static KioskManualSetupPayload ParseManualSetupCode(string value)
    {
        value = value?.Trim() ?? string.Empty;
        if (!value.StartsWith(ManualSetupPrefix, StringComparison.OrdinalIgnoreCase) ||
            value.Length is < 20 or > 4_096)
        {
            throw new InvalidDataException(
                "The manual setup code is not a valid Mullet Hop kiosk code.");
        }

        try
        {
            var encoded = value[ManualSetupPrefix.Length..]
                .Replace('-', '+')
                .Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var bytes = Convert.FromBase64String(encoded);
            var payload = JsonSerializer.Deserialize<KioskManualSetupPayload>(bytes, JsonOptions)
                          ?? throw new InvalidDataException("The manual setup code was empty.");
            payload.ControllerAddress = NormalizeControllerAddress(payload.ControllerAddress);
            payload.PairingKey = ValidatePairingKey(payload.PairingKey);
            payload.ControllerName = CleanControllerName(payload.ControllerName);
            return payload;
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The manual setup code is damaged or incomplete.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The manual setup code could not be read.", ex);
        }
    }

    private static string NormalizeControllerAddress(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != ControllerPort ||
            !string.Equals(
                uri.AbsolutePath.TrimEnd('/'),
                ControllerBasePath.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("The controller address in the setup code is invalid.");
        }

        return new UriBuilder(uri)
        {
            Path = ControllerBasePath,
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri.AbsoluteUri;
    }

    private static string ValidatePairingKey(string value)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length is < 16 or > 1_000)
            throw new InvalidDataException("The controller pairing key is missing or incomplete.");
        return value;
    }

    private static string CleanControllerName(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "Kiosk Controller" : value.Trim();
        if (value.Length > 200)
            throw new InvalidDataException("The controller computer name is too long.");
        return value;
    }

    public static ECDiffieHellman CreateKioskKey() =>
        ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static string ExportPublicKey(ECDiffieHellman key) =>
        Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    public static bool IsValidPublicKey(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length is < 32 or > 512)
                return false;
            using var key = ECDiffieHellman.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var bytesRead);
            return bytesRead == bytes.Length;
        }
        catch
        {
            return false;
        }
    }

    public static KioskPairingOffer EncryptPairingOffer(
        string kioskPublicKey,
        KioskPairingPayload payload)
    {
        using var controllerKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var kioskKey = ImportPublicKey(kioskPublicKey);
        var key = DeriveEncryptionKey(controllerKey, kioskKey.PublicKey, payload.RequestId);
        try
        {
            var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(
                nonce,
                plaintext,
                ciphertext,
                tag,
                Encoding.UTF8.GetBytes(payload.RequestId));
            return new KioskPairingOffer
            {
                RequestId = payload.RequestId,
                ControllerPublicKey = ExportPublicKey(controllerKey),
                Nonce = Convert.ToBase64String(nonce),
                Ciphertext = Convert.ToBase64String(ciphertext),
                AuthenticationTag = Convert.ToBase64String(tag)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static KioskPairingPayload DecryptPairingOffer(
        ECDiffieHellman kioskKey,
        KioskPairingOffer offer)
    {
        if (string.IsNullOrWhiteSpace(offer.RequestId) || offer.RequestId.Length > 80)
            throw new InvalidDataException("The pairing request ID is invalid.");

        using var controllerKey = ImportPublicKey(offer.ControllerPublicKey);
        var key = DeriveEncryptionKey(kioskKey, controllerKey.PublicKey, offer.RequestId);
        try
        {
            var nonce = Convert.FromBase64String(offer.Nonce);
            var ciphertext = Convert.FromBase64String(offer.Ciphertext);
            var tag = Convert.FromBase64String(offer.AuthenticationTag);
            if (nonce.Length != 12 || tag.Length != 16 || ciphertext.Length is 0 or > 16_384)
                throw new InvalidDataException("The encrypted pairing request is invalid.");

            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(
                nonce,
                ciphertext,
                tag,
                plaintext,
                Encoding.UTF8.GetBytes(offer.RequestId));
            var payload = JsonSerializer.Deserialize<KioskPairingPayload>(plaintext, JsonOptions)
                          ?? throw new InvalidDataException("The pairing request was empty.");
            if (!string.Equals(payload.RequestId, offer.RequestId, StringComparison.Ordinal))
                throw new InvalidDataException("The pairing request ID did not match.");
            return payload;
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The encrypted pairing request was not valid.", ex);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("The pairing request could not be authenticated.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static ECDiffieHellman ImportPublicKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1_000)
            throw new InvalidDataException("The discovery public key is invalid.");
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length is < 32 or > 512)
                throw new InvalidDataException("The discovery public key is invalid.");
            var key = ECDiffieHellman.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var bytesRead);
            if (bytesRead != bytes.Length)
            {
                key.Dispose();
                throw new InvalidDataException("The discovery public key is invalid.");
            }
            return key;
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The discovery public key is invalid.", ex);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("The discovery public key is invalid.", ex);
        }
    }

    private static byte[] DeriveEncryptionKey(
        ECDiffieHellman localKey,
        ECDiffieHellmanPublicKey remoteKey,
        string requestId)
    {
        var sharedSecret = localKey.DeriveKeyMaterial(remoteKey);
        try
        {
            var context = Encoding.UTF8.GetBytes("MulletHop-Kiosk-Pairing-v1\n" + requestId);
            var input = new byte[sharedSecret.Length + context.Length];
            Buffer.BlockCopy(sharedSecret, 0, input, 0, sharedSecret.Length);
            Buffer.BlockCopy(context, 0, input, sharedSecret.Length, context.Length);
            var key = SHA256.HashData(input);
            CryptographicOperations.ZeroMemory(input);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }
}

internal sealed class KioskManualSetupPayload
{
    public string ControllerAddress { get; set; } = string.Empty;
    public string PairingKey { get; set; } = string.Empty;
    public string ControllerName { get; set; } = string.Empty;
}

internal sealed class KioskDiscoveryAnnouncement
{
    public int ProtocolVersion { get; set; } = KioskDiscoveryProtocol.Version;
    public string StationId { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string KioskPublicKey { get; set; } = string.Empty;
    public bool IsManaged { get; set; }
    public string CurrentController { get; set; } = string.Empty;
    public KioskPairingResult? PairingResult { get; set; }
}

internal sealed class KioskDiscoveryResponse
{
    public int ProtocolVersion { get; set; } = KioskDiscoveryProtocol.Version;
    public string ControllerName { get; set; } = string.Empty;
    public string ControllerAddress { get; set; } = string.Empty;
    public KioskPairingOffer? PairingOffer { get; set; }
    public string AcknowledgedPairingRequestId { get; set; } = string.Empty;
}

internal sealed class KioskPairingOffer
{
    public string RequestId { get; set; } = string.Empty;
    public string ControllerPublicKey { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public string Ciphertext { get; set; } = string.Empty;
    public string AuthenticationTag { get; set; } = string.Empty;
}

internal sealed class KioskPairingPayload
{
    public string RequestId { get; set; } = string.Empty;
    public string ControllerName { get; set; } = string.Empty;
    public string ControllerAddress { get; set; } = string.Empty;
    public string PairingKey { get; set; } = string.Empty;
    public DateTime ExpiresUtc { get; set; }
}

internal sealed class KioskPairingResult
{
    public string RequestId { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public string Message { get; set; } = string.Empty;
}
