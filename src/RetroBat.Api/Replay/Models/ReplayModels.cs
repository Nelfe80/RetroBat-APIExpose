using System.Security.Cryptography;

namespace RetroBat.Api.Replay.Models;

// ─────────────────────────────────────────────────────────────────────────────
// Contrats Replay (CDC_DEV_NELFE_REPLAY v1.0). Le ReplayManifest est IMMUABLE
// après finalisation ; les données mutables vivent dans ReplayLocalMetadata.
// Sérialisation : System.Text.Json avec SnakeCaseLower (PascalCase -> snake_case).
// R1 : les empreintes runtime (rom/core/options) sont partielles (playback = R2).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Identité du jeu. <paramref name="SystemFolder"/> = le dossier système FRONTEND (ex. « megadrive »),
/// ajouté pour la PORTABILITÉ : ce n'est pas un chemin local (règle CDC) mais un identifiant
/// standardisé, identique sur toute install RetroBat — il permet à un peer de retrouver la ROM
/// dans `roms/&lt;dossier&gt;` sans hint. Optionnel : les manifestes antérieurs valent null.
/// (Le SystemId vient de RetroArch — « mega_drive » — et ne se déduit PAS du dossier.)
/// </summary>
public sealed record ReplayGame(string GameId, string SystemId, string? RomGroup, string? Ruleset, string? Crc32,
    string? SystemFolder = null);

public sealed record ReplayRuntime(
    string RuntimeId,
    string RetroarchVersion,
    string? RomSha256,
    string? CoreSha256,
    string? BiosSha256,
    string? CoreOptionsDigest,
    string ReplayFormat,
    // Le NOM du core libretro (« genesis_plus_gx »), portable : l'empreinte dit la version, le
    // nom dit lequel. Sans lui, une empreinte fausse laissait la lecture deviner (2048 pour un
    // replay de Sonic, 2026-09-17). Absent des manifestes anterieurs.
    string? CoreName = null);

public sealed record ReplayObjectRef(string Sha256, long Size);

/// <summary>
/// Repères de frames du replay. <paramref name="NominalFps"/> est la cadence DU CORE (base de
/// temps émulée), pas celle de l'écran : c'est elle qui convertit frames ↔ secondes (seek,
/// timeline, placement des réactions). Elle vaut 50 en PAL et prend des valeurs propres à chaque
/// carte en arcade (Neo-Geo 59,19 ; CPS 59,63 ; Pac-Man 60,10) — d'où R3.2, qui a remplacé la
/// constante 60 codée en dur. <paramref name="FpsSource"/> dit d'où elle vient : "core" (annoncée
/// par le core lui-même, exacte), "measured" (déduite de la cadence observée), "default" (repli
/// 60, valeur non vérifiée) ; absent = manifeste d'avant R3.2, à traiter comme "default".
/// </summary>
public sealed record ReplayFrames(long Start, long? RunStart, long? RunEnd, long ReplayEnd, double NominalFps,
    string? FpsSource = null);

public sealed record ReplayScoreLink(string? SubmissionHash, long? ScoreValueSnapshot);

public sealed record ReplayRecovery(bool RecoveredAfterCrash);

/// <summary>Manifeste technique immuable (schema nelfe.replay.v2).</summary>
public sealed record ReplayManifest(
    string Schema,
    string ReplayId,
    string SessionId,
    ReplayGame Game,
    DateTime CreatedAt,
    string Origin,
    ReplayRuntime Runtime,
    ReplayObjectRef Object,
    ReplayFrames Frames,
    ReplayScoreLink? ScoreLink,
    ReplayRecovery Recovery)
{
    public const string SchemaId = "nelfe.replay.v2";
}

/// <summary>
/// Indices de lancement LOCAUX (chemins ROM/core sur CETTE machine). Jamais dans le
/// manifeste (règle CDC : le manifeste ne contient aucun chemin local) — vivent dans la
/// meta locale. Capturés à l'enregistrement depuis saves/&lt;sys&gt;/libretro.&lt;core&gt;/&lt;jeu&gt;.replayN.
/// </summary>
public sealed record ReplayLaunchHint(string SystemFolder, string Core, string CoreDll, string RomPath);

/// <summary>Métadonnées locales MUTABLES (ne change jamais le manifeste technique).</summary>
public sealed record ReplayLocalMetadata(
    string Schema,
    string ReplayId,
    string Visibility,
    bool Pinned,
    string? ScoreRef,
    string? LeaderboardId,
    string PublicationState,
    DateTime LastAccessAt,
    bool CreatedByThisDevice,
    ReplayLaunchHint? Launch,
    // Identité du record pour la carte du player, estampillée au scellement par le
    // reporter (#2) quand le score est publié. Null tant que non renseigné.
    string? Player = null,
    long? ScoreValue = null,
    int? Rank = null)
{
    public const string SchemaId = "nelfe.replay.local-meta.v1";

    public static ReplayLocalMetadata Fresh(string replayId, ReplayLaunchHint? launch = null) => new(
        SchemaId, replayId, "private", false, null, null, "local", DateTime.UtcNow, true, launch);
}

/// <summary>Entrée d'index (vue dérivée, reconstructible depuis les manifests).</summary>
public sealed record ReplayIndexEntry(string ReplayId, string GameId, DateTime CreatedAt, string ObjectSha256);

/// <summary>
/// Réaction émise pendant une lecture (audience) — R4. Famille = hype/wow/respect/laugh/
/// tension/ouch/love/rage/celebrate ; niveau 1-3 (intensité = maintien, ou nb de boutons pour
/// l'accord « celebrate »). Frame = position dans le replay ; Ts = horloge murale (ms). Lang =
/// langue de l'auteur (affichage). Stockées en JSONL pour être rejouées (affichage = R4.2/R5).
/// </summary>
public sealed record ReplayReaction(
    string ReplayId, string Reaction, int Level, long Frame, long TsMs, string Lang, bool Chord,
    string? Author = null, // Author = nom d'affichage, jamais une identité de compte
    /// <summary>Jeton OPAQUE du spectateur qui a lancé la lecture (CDC DEV §101.6). La borne ne
    /// manipule jamais une identité de compte : elle transporte ce jeton, et c'est la plateforme
    /// qui le résout. Null = personne d'identifié, donc aucune réaction n'est retenue.</summary>
    string? ViewerToken = null,
    /// <summary>Numéro de la SÉANCE de visionnage (CDC §58 : « compteur signé par acteur+cible
    /// afin que l'état le plus récent soit déterminable »). Rien n'est jamais effacé : en
    /// regardant à nouveau, le spectateur refait ses cinq réactions, et seule la séance la plus
    /// récente est retenue à l'agrégat. L'ancienne reste, elle ne compte plus.</summary>
    long SessionSeq = 0);

public enum ReplayRecordingState { Idle, Starting, Recording, Stopping, Finalizing, Ready, Error }

/// <summary>États de lecture. <c>Replicating</c> = l'objet n'est pas encore ici et on va le
/// chercher : c'est un état de PROGRESSION, pas une erreur, et il existe pour que la demande de
/// lecture n'attende JAMAIS le réseau (CDC §86, qui définit déjà ce mot pour un objet).</summary>
public enum ReplayPlaybackState { Idle, Resolving, Replicating, Verifying, Preparing, Launching, Playing, Paused, Stopping, Finished, Error }

public enum ReplayErrorCode
{
    None,
    ReplayNotFound,
    ReplayObjectUnavailable,
    ReplayObjectCorrupt,   // R6 : taille/SHA-256 de l'objet ≠ manifeste (corruption ou altération peer)
    ReplayAlreadyRunning,
    GameAlreadyRunning,
    ReplayRecordStartFailed,
    ReplayFileNotStable,
    ReplayManifestInvalid,
    ReplayLaunchTimeout,
    RomNotFound,
    CoreNotFound,
    RuntimeIncompatible,
    RetroArchUnavailable,
    InternalError,
}

/// <summary>Générateur d'identifiant ULID (Crockford base32, 26 chars, triable par temps).</summary>
public static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford (sans I L O U)

    public static string NewReplayId() => "rp_" + New();
    public static string NewSessionId() => "sess_" + New();

    public static string New()
    {
        Span<byte> b = stackalloc byte[16];
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 5; i >= 0; i--) { b[i] = (byte)(ts & 0xFF); ts >>= 8; } // 48-bit temps (big-endian)
        RandomNumberGenerator.Fill(b[6..]);                                  // 80 bits d'aléa
        var chars = new char[26];
        for (int i = 0; i < 26; i++)
        {
            int val = 0;
            for (int j = 0; j < 5; j++)
            {
                int pos = i * 5 + j; // 0..129 (128 bits + 2 de padding)
                int bit = pos < 128 ? (b[pos / 8] >> (7 - (pos % 8))) & 1 : 0;
                val = (val << 1) | bit;
            }
            chars[i] = Alphabet[val];
        }
        return new string(chars);
    }
}
