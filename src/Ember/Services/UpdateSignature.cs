using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Ember.Services;

/// <summary>
/// Update signatures: ECDSA P-256 / SHA-256 over a short statement naming the file and its hash:
/// <code>ember-update-v1\n&lt;file name&gt;\n&lt;sha256 hex&gt;\n</code>
/// The file name carries the version (Ember-Setup-1.2.0.exe), so a signed old installer can't be
/// passed off as a newer one. Shared by the app (verify) and tools/EmberSign (sign).
/// </summary>
public static class UpdateSignature
{
    /// <summary>
    /// Public keys trusted to sign updates (SubjectPublicKeyInfo, base64). More than one allows the
    /// key to be replaced later: ship a release trusting both, then sign with the new one.
    /// </summary>
    public static readonly string[] TrustedKeys =
    {
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEe6NoMdbj1SufwABizQM2oi6vCwF9xwe42yiCDS9xxI0bc/Lly38DXUVI8vXkkcxzkODJckzxYcaJc1AMVWx0WQ==",
    };

    public static byte[] Statement(string fileName, byte[] sha256) =>
        Encoding.UTF8.GetBytes($"ember-update-v1\n{fileName}\n{Convert.ToHexString(sha256).ToLowerInvariant()}\n");

    public static byte[] Hash(Stream s)
    {
        s.Position = 0;
        using var sha = SHA256.Create();
        var h = sha.ComputeHash(s);
        s.Position = 0;
        return h;
    }

    public static string Sign(ECDsa key, string fileName, byte[] sha256) =>
        Convert.ToBase64String(key.SignData(Statement(fileName, sha256), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <summary>True when <paramref name="signatureBase64"/> is a valid signature by one of <paramref name="keys"/>.</summary>
    public static bool Verify(string fileName, byte[] sha256, string signatureBase64, string[]? keys = null)
    {
        byte[] sig;
        try { sig = Convert.FromBase64String(signatureBase64.Trim()); }
        catch (FormatException) { return false; }
        var statement = Statement(fileName, sha256);
        foreach (var k in keys ?? TrustedKeys)
        {
            try
            {
                using var ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(k), out _);
                if (ec.KeySize == 256 &&
                    ec.VerifyData(statement, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    return true;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException) { }
        }
        return false;
    }
}
