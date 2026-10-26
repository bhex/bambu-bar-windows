using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BambuBar.Core.Protocol;

/// <summary>
/// Decides whether a printer's TLS certificate is genuine.
///
/// Bambu printers present a leaf named after their serial number, issued by a per-model "BBL Device CA"
/// that chains to Bambu's own roots ("BBL CA", "BBL CA2 RSA", "BBL CA2 ECC"). Those roots — together with
/// Bambu's cross-certificates and the device CAs — ship with the app. The hostname is not checked,
/// because the certificate names the serial, not the IP address.
///
/// Security model (mirrors the macOS original):
///  * the Windows certificate store is never consulted, so a rogue CA installed on this machine or on a
///    shared network cannot impersonate the printer and collect the access code;
///  * the only acceptable trust anchors are the bundled, self-signed Bambu roots;
///  * the leaf must be issued to the exact serial number we are talking to, so another device on the LAN
///    cannot collect this printer's code;
///  * every signature link in the chain is verified with the issuer's bundled/public key.
/// </summary>
public static class BambuTrust
{
    /// <summary>Every certificate that ships with the app (roots, cross-certs and device CAs).</summary>
    public static readonly X509Certificate2[] Bundled = LoadBundled();

    /// <summary>Bambu's self-signed roots — the only acceptable trust anchors.</summary>
    public static readonly X509Certificate2[] Anchors = Bundled
        .Where(c => IsSelfSigned(c))
        .ToArray();

    public sealed record Result(Verdict Verdict, string Reason)
    {
        public static Result Trusted => new(Verdict.Trusted, "");
        public static Result Rejected(string reason) => new(Verdict.Rejected, reason);
    }

    public enum Verdict
    {
        /// <summary>Chains to a bundled Bambu root and is named after the expected printer.</summary>
        Trusted,
        Rejected,
    }

    /// <summary>The most recent verdict, for <c>--probe</c> diagnostics.</summary>
    public static Result LastVerdict { get; private set; } = new(Verdict.Rejected, "Not evaluated yet.");

    /// <summary>Evaluates the certificate the printer presented (leaf plus any intermediates it sent).</summary>
    public static Result Evaluate(X509Certificate2? leaf, X509Certificate2[]? presented, string serial)
    {
        var verdict = Check(leaf, presented ?? Array.Empty<X509Certificate2>(), serial);
        LastVerdict = verdict;
        return verdict;
    }

    private static Result Check(X509Certificate2? leaf, X509Certificate2[] presented, string serial)
    {
        if (leaf is null || leaf.SubjectName.Name.Length == 0)
            return Result.Rejected("The printer sent no certificate.");

        // 1) The leaf must name the serial we expect — another Bambu printer on the network must not be
        //    able to collect this access code.
        var commonName = ExtractCommonName(leaf);
        if (commonName is null || !string.Equals(commonName, serial, StringComparison.OrdinalIgnoreCase))
            return Result.Rejected(commonName is null
                ? "The certificate has no name."
                : $"The certificate belongs to another device ({commonName}).");

        // 2) The leaf must be within its validity window.
        var now = DateTime.Now;
        if (leaf.NotBefore > now || leaf.NotAfter < now)
            return Result.Rejected("The certificate is outside its validity period.");

        // 3) Build the chain from the leaf. The pool is everything the server sent plus everything that
        //    ships with the app (Bambu's cross-certs and per-model device CAs, for firmwares that omit
        //    intermediates). A chain only counts if it reaches one of the bundled self-signed roots.
        var pool = new List<X509Certificate2>(presented);
        foreach (var cert in Bundled)
            if (!pool.Any(c => c.Thumbprint == cert.Thumbprint))
                pool.Add(cert);

        var chain = new List<X509Certificate2> { leaf };
        var current = leaf;
        for (var guard = 0; guard < 10; guard++)
        {
            var next = pool.FirstOrDefault(c =>
                c.Thumbprint != current.Thumbprint &&
                NameEquals(c.SubjectName, current.IssuerName) &&
                VerifySignature(current, c));
            if (next is null)
                return Result.Rejected("The certificate doesn't chain to a Bambu Lab certificate authority.");
            chain.Add(next);
            current = next;
            if (Anchors.Any(a => a.Thumbprint == current.Thumbprint))
                return Result.Trusted;
        }

        return Result.Rejected("The certificate doesn't chain to a Bambu Lab certificate authority.");
    }

    private static bool IsSelfSigned(X509Certificate2 cert)
    {
        try
        {
            return NameEquals(cert.SubjectName, cert.IssuerName);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Compares X.509 names by their DER encoding — the string form can differ in attribute order and
    /// punctuation, which would let a look-alike issuer slip through a string comparison.
    /// </summary>
    private static bool NameEquals(X500DistinguishedName a, X500DistinguishedName b)
    {
        if (a == b) return true;
        try
        {
            return a.RawData.SequenceEqual(b.RawData);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Verifies <c>child</c>'s signature with <c>issuer</c>'s public key (RSA or ECDSA).</summary>
    private static bool VerifySignature(X509Certificate2 child, X509Certificate2 issuer)
    {
        // The signature covers the TBSCertificate — the first field of the DER encoding — not the
        // whole certificate.
        var tbs = ExtractTbs(child.RawData);
        var signature = ExtractRawSignature(child.RawData);
        if (tbs.Length == 0 || signature.Length == 0) return false;
        var hash = HashFrom(child.SignatureAlgorithm);

        // Preferred: the PublicKey wrapper exposes the issuer's key object directly. This works even
        // on toolchains where X509Certificate2.GetPublicKey() returns a raw key blob instead of a
        // SubjectPublicKeyInfo (importing that throws).
        try
        {
            var pk = issuer.PublicKey;
            if (pk is not null)
            {
                try
                {
                    if (pk.GetRSAPublicKey() is RSA rsa)
                    {
                        using (rsa) return rsa.VerifyData(tbs, signature, hash, RSASignaturePadding.Pkcs1);
                    }
                }
                catch { /* not an RSA key */ }
                try
                {
                    if (pk.GetECDsaPublicKey() is ECDsa ecd)
                    {
                        using (ecd) return VerifyEcdsa(ecd, tbs, signature, hash);
                    }
                }
                catch { /* not an ECDSA key */ }
            }
        }
        catch { /* wrapper unavailable on this toolchain */ }

        // Fallback: import the key from SubjectPublicKeyInfo (the standard .NET behaviour).
        try
        {
            var spki = issuer.GetPublicKey();
            if (TryRsa(spki, tbs, signature, hash)) return true;
            if (TryEcdsa(spki, tbs, signature, hash)) return true;
        }
        catch { /* GetPublicKey unavailable */ }

        return false;
    }

    private static bool TryRsa(byte[] spki, byte[] data, byte[] signature, HashAlgorithmName hash)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(spki, out _);
            return rsa.VerifyData(data, signature, hash, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryEcdsa(byte[] spki, byte[] data, byte[] signature, HashAlgorithmName hash)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            return VerifyEcdsa(ecdsa, data, signature, hash);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// ECDSA signature-format adapter. X.509 carries the raw r||s form, but toolchains differ in
    /// what their VerifyData default expects — this runtime wants raw, standard .NET wants DER (r,s),
    /// and real-world certificates may carry either (Bambu's EC root is DER). Try the natural
    /// encoding first, then the converted one. A wrong encoding simply fails verification, so this
    /// can never produce a false acceptance.
    /// </summary>
    private static bool VerifyEcdsa(ECDsa ecd, byte[] tbs, byte[] signature, HashAlgorithmName hash)
    {
        if (VerifyEcdsaOnce(ecd, tbs, signature, hash)) return true;

        var coord = (ecd.KeySize + 7) / 8;
        if (coord <= 0) return false;

        if (signature.Length > 0 && signature[0] == 0x30)
        {
            // DER (r,s) presented where raw r||s is expected.
            if (DerToRaw(signature, coord) is { } raw && VerifyEcdsaOnce(ecd, tbs, raw, hash)) return true;
        }
        else if (signature.Length == 2 * coord)
        {
            // Raw r||s presented where DER (r,s) is expected.
            if (RawToDer(signature, coord) is { } der && VerifyEcdsaOnce(ecd, tbs, der, hash)) return true;
        }
        return false;
    }

    private static bool VerifyEcdsaOnce(ECDsa ecd, byte[] tbs, byte[] signature, HashAlgorithmName hash)
    {
        try
        {
            return ecd.VerifyData(tbs, signature, hash);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Converts a DER-encoded (r,s) ECDSA signature to fixed-size raw r||s.</summary>
    private static byte[]? DerToRaw(byte[] der, int coord)
    {
        try
        {
            if (der.Length < 6 || der[0] != 0x30) return null;
            var pos = ReadDerLength(der, 1, out var seqLen);
            if (pos + seqLen != der.Length) return null;

            if (der[pos] != 0x02) return null;
            pos = ReadDerLength(der, pos + 1, out var rLen);
            if (rLen == 0 || rLen > coord + 1) return null;
            var r = TrimLeadingZeros(der, pos, rLen, out var rStart, coord);
            pos += rLen;

            if (der[pos] != 0x02) return null;
            pos = ReadDerLength(der, pos + 1, out var sLen);
            if (sLen == 0 || sLen > coord + 1 || pos + sLen != der.Length) return null;
            var s = TrimLeadingZeros(der, pos, sLen, out var sStart, coord);

            var result = new byte[2 * coord];
            System.Array.Copy(der, rStart, result, coord - r.Length, r.Length);
            System.Array.Copy(der, sStart, result, 2 * coord - s.Length, s.Length);
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] TrimLeadingZeros(byte[] src, int offset, int length, out int start, int max)
    {
        start = offset;
        while (length > 1 && src[start] == 0) { start++; length--; }
        if (length > max) throw new CryptographicException("Bad ECDSA coordinate.");
        return new byte[length];
    }

    /// <summary>Converts fixed-size raw r||s to a DER-encoded (r,s) ECDSA signature.</summary>
    private static byte[]? RawToDer(byte[] raw, int coord)
    {
        try
        {
            if (raw.Length != 2 * coord) return null;
            byte[] ToInteger(byte[] v)
            {
                var start = 0;
                while (start < v.Length - 1 && v[start] == 0) start++;
                v = v[start..];
                var body = (v[0] & 0x80) != 0
                    ? new byte[] { 0 }.Concat(v).ToArray()
                    : v;
                var len = body.Length < 0x80
                    ? new byte[] { (byte)body.Length }
                    : new byte[] { (byte)(0x81), (byte)body.Length };
                return new byte[] { 0x02 }.Concat(len).Concat(body).ToArray();
            }
            var r = ToInteger(raw[..coord]);
            var s = ToInteger(raw[coord..]);
            var content = r.Concat(s).ToArray();
            var len = content.Length < 0x80
                ? new byte[] { (byte)content.Length }
                : new byte[] { (byte)(0x81), (byte)content.Length };
            return new byte[] { 0x30 }.Concat(len).Concat(content).ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The certificate's signature covers the TBSCertificate — the first field of the DER encoding:
    /// SEQUENCE { tbsCertificate, signatureAlgorithm, signatureValue }.
    /// </summary>
    private static byte[] ExtractTbs(byte[] certificateDer)
    {
        try
        {
            if (certificateDer.Length < 4 || certificateDer[0] != 0x30) return Array.Empty<byte>();
            var tbsStart = ReadDerLength(certificateDer, 1, out _);
            if (certificateDer[tbsStart] != 0x30) return Array.Empty<byte>();
            var contentStart = ReadDerLength(certificateDer, tbsStart + 1, out var tbsLength);
            if (contentStart + tbsLength > certificateDer.Length) return Array.Empty<byte>();
            return certificateDer[tbsStart..(contentStart + tbsLength)];
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>Derives the digest a signature uses from the signing-algorithm OID.</summary>
    private static HashAlgorithmName HashFrom(Oid? algorithm) =>
        algorithm?.Value switch
        {
            // ecdsa-with-SHA384 / SHA384withRSAEncryption
            "1.2.840.10045.4.3.3" or "1.2.840.113549.1.1.12" => HashAlgorithmName.SHA384,
            // ecdsa-with-SHA512 / SHA512withRSAEncryption
            "1.2.840.10045.4.3.4" or "1.2.840.113549.1.1.13" => HashAlgorithmName.SHA512,
            // ecdsa-with-SHA256, SHA256withRSAEncryption, or anything unexpected
            _ => HashAlgorithmName.SHA256,
        };

    private static string? ExtractCommonName(X509Certificate2 cert)
    {
        // RFC 2253 form, e.g. "CN=22E00ABC" — take the CN component regardless of attribute order.
        var name = cert.SubjectName?.Name ?? "";
        foreach (var part in name.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                return trimmed[3..];
        }
        return null;
    }

    /// <summary>
    /// The certificate's signature is the last field of its DER encoding: SEQUENCE { tbsCertificate,
    /// signatureAlgorithm, signatureValue (BIT STRING) }. This walks those two leading fields and
    /// returns the bit string's payload.
    /// </summary>
    private static byte[] ExtractRawSignature(byte[] certificateDer)
    {
        try
        {
            var pos = 0;
            if (certificateDer[pos] != 0x30) return Array.Empty<byte>();
            pos = ReadDerLength(certificateDer, pos + 1, out var outerLength);
            if (pos + outerLength > certificateDer.Length) return Array.Empty<byte>();

            // Skip tbsCertificate (SEQUENCE).
            if (certificateDer[pos] != 0x30) return Array.Empty<byte>();
            pos = ReadDerLength(certificateDer, pos + 1, out var tbsLength);
            pos += tbsLength;

            // Skip signatureAlgorithm (SEQUENCE).
            if (certificateDer[pos] != 0x30) return Array.Empty<byte>();
            pos = ReadDerLength(certificateDer, pos + 1, out var sigAlgLength);
            pos += sigAlgLength;

            // signatureValue (BIT STRING): one "unused bits" byte (must be 0) then the signature.
            if (certificateDer[pos] != 0x03) return Array.Empty<byte>();
            pos = ReadDerLength(certificateDer, pos + 1, out var bitLength);
            if (bitLength < 2 || certificateDer[pos] != 0) return Array.Empty<byte>();
            return certificateDer[(pos + 1)..(pos + bitLength)];
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private static int ReadDerLength(byte[] der, int pos, out int length)
    {
        var first = der[pos];
        if (first < 0x80)
        {
            length = first;
            return pos + 1;
        }
        var count = first & 0x7F;
        if (count > 4)
            throw new System.Security.Cryptography.CryptographicException("Bad DER length.");
        length = 0;
        for (var i = 0; i < count; i++)
            length = (length << 8) | der[pos + 1 + i];
        return pos + 1 + count;
    }

    private static X509Certificate2[] LoadBundled()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "certificates"),
            Path.Combine(AppContext.BaseDirectory, "certificates"),
        };
        var dir = candidates.FirstOrDefault(Directory.Exists);
        if (dir is null) return Array.Empty<X509Certificate2>();

        var certs = new List<X509Certificate2>();
        foreach (var file in Directory.GetFiles(dir, "*.pem"))
        {
            try
            {
                certs.AddRange(ParsePem(File.ReadAllText(file)));
            }
            catch
            {
                // A broken PEM should not take the whole trust set down.
            }
        }
        return certs.ToArray();
    }

    /// <summary>Extracts every certificate from a PEM file (mirrors the Swift original).</summary>
    public static List<X509Certificate2> ParsePem(string pem)
    {
        var certs = new List<X509Certificate2>();
        const string begin = "-----BEGIN CERTIFICATE-----";
        const string end = "-----END CERTIFICATE-----";
        var offset = 0;
        while (true)
        {
            var start = pem.IndexOf(begin, offset, StringComparison.Ordinal);
            if (start < 0) break;
            start += begin.Length;
            var stop = pem.IndexOf(end, start, StringComparison.Ordinal);
            if (stop < 0) break;
            offset = stop + end.Length;

            var base64 = new string(pem[start..stop])
                .Where(ch => !char.IsWhiteSpace(ch))
                .ToArray();
            try
            {
                certs.Add(new X509Certificate2(Convert.FromBase64String(new string(base64))));
            }
            catch
            {
                // Skip a malformed block.
            }
        }
        return certs;
    }
}
