// COPIE VERSIONNEE du contrat IPC de NelfePlay-VocalRoom (depot prive Nelfe80/NelfePlay-VocalRoom,
// src/NelfePlay.VocalRoom.Protocol, commit f3f0095, protocole 1). Ne pas modifier ici : changer le depot VocalRoom,
// puis recopier (voir VocalRoomProtocolSource et OUTILS_COMMUNS.md, « Vocal Discord »).
using System.Security.Cryptography;
using System.Text;

namespace NelfePlay.VocalRoom.Protocol;

/// <summary>
/// Appairage ephemere APIExpose ↔ VocalRoom (§6.1 et §12 du CDC).
///
/// APIExpose tire un secret de 32 octets a chaque lancement et l'ecrit sur l'entree standard de l'exe (jamais en argv
/// ni dans l'environnement). A la connexion, chacun prouve qu'il connait le secret par un HMAC-SHA256 sur les deux
/// nonces, avec une etiquette de role differente : une preuve du pilote ne peut pas etre rejouee comme preuve de l'exe.
/// </summary>
public static class Pairing
{
    private const string ServerLabel = "nelfeplay-vocalroom/v1/apiexpose";
    private const string ClientLabel = "nelfeplay-vocalroom/v1/vocalroom";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(ProtocolInfo.SecretBytes);

    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(ProtocolInfo.NonceBytes)).ToLowerInvariant();

    /// <summary>Nom de pipe aleatoire, prefixe impose.</summary>
    public static string NewPipeName() => ProtocolInfo.PipePrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    /// <summary>Preuve du pilote (APIExpose), envoyee dans <c>hello</c>.</summary>
    public static string ServerProof(byte[] secret, string clientNonce, string serverNonce) => Proof(secret, ServerLabel, clientNonce, serverNonce);

    /// <summary>Preuve de l'exe, rendue dans la reponse a <c>hello</c>.</summary>
    public static string ClientProof(byte[] secret, string clientNonce, string serverNonce) => Proof(secret, ClientLabel, clientNonce, serverNonce);

    /// <summary>Comparaison en temps constant de deux preuves hexadecimales.</summary>
    public static bool ProofEquals(string? expected, string? actual)
    {
        if (expected is null || actual is null) return false;
        byte[] a = Encoding.ASCII.GetBytes(expected.ToLowerInvariant());
        byte[] b = Encoding.ASCII.GetBytes(actual.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Secret → ligne hexadecimale (ce qu'APIExpose ecrit sur stdin).</summary>
    public static string EncodeSecret(byte[] secret) => Convert.ToHexString(secret).ToLowerInvariant();

    /// <summary>Ligne lue sur stdin → secret ; null si elle n'a pas la bonne forme.</summary>
    public static byte[]? DecodeSecret(string? line)
    {
        if (line is null) return null;
        string s = line.Trim();
        if (s.Length != ProtocolInfo.SecretBytes * 2) return null;
        try { return Convert.FromHexString(s); }
        catch (FormatException) { return null; }
    }

    /// <summary>Un nonce recu a-t-il la forme attendue (hexadecimal, 32 caracteres) ?</summary>
    public static bool IsValidNonce(string? nonce) =>
        nonce is { Length: ProtocolInfo.NonceBytes * 2 } && nonce.All(Uri.IsHexDigit);

    private static string Proof(byte[] secret, string label, string clientNonce, string serverNonce)
    {
        byte[] data = Encoding.UTF8.GetBytes($"{label}|{clientNonce}|{serverNonce}");
        return Convert.ToHexString(HMACSHA256.HashData(secret, data)).ToLowerInvariant();
    }
}
