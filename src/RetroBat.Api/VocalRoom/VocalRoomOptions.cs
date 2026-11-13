using System.Text.RegularExpressions;

namespace RetroBat.Api.VocalRoom;

/// <summary>
/// LE VOCAL DISCORD DES PARTIES MULTIPLAYER (CDC NelfeVoice v1.3, lot B, 12/11/2026) : ce que la borne regle elle-meme.
/// Le reste (liaison Discord, consentement, preference, politique de la partie) appartient au site (§9 bis) : la borne
/// ne s'auto-promeut jamais a « auto ».
/// </summary>
public sealed class VocalRoomOptions
{
    /// <summary>
    /// Le vocal sur cette borne. A false, rien ne se lance, rien ne se demande au site. Allume par defaut : sans liaison
    /// Discord ni consentement sur nelfeplay.com/account, il ne se passe rien de toute facon.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Relances de l'exe apres un plantage, par session vocale (§9.3 : redemarrage borne).</summary>
    public int MaxRestarts { get; set; } = 3;
}

/// <summary>
/// Les fichiers du lot VocalRoom, livres ENSEMBLE a la racine d'APIExpose (comme NelfePlay.LiveStreaming.exe) : l'outil
/// de diagnostic n'accepte qu'un nom sans chemin dans executables.manifest.json. Krisp (reduction de bruit) est livre
/// sur decision du user du 12/11 ; ses modeles changent de nom avec le SDK, d'ou un motif plutot qu'une liste.
/// </summary>
public static class VocalRoomFichiers
{
    public const string Exe = "NelfePlay-VocalRoom.exe";

    /// <summary>Le nom du processus (sans .exe), pour le trouver ou l'arreter.</summary>
    public const string Processus = "NelfePlay-VocalRoom";

    public const string Sdk = "discord_partner_sdk.dll";

    public const string Notices = "Discord-Social-SDK-License-Notices.txt";

    public const string Krisp = "discord_krisp.dll";

    /// <summary>Le lot obligatoire : sans l'un d'eux, le vocal ne se lance pas.</summary>
    public static readonly IReadOnlyList<string> Obligatoires = [Exe, Sdk, Notices];

    private static readonly Regex ModeleKrisp = new(@"^krisp-[a-z0-9][a-z0-9-]{0,60}\.kef$", RegexOptions.CultureInvariant);

    /// <summary>Un chemin d'outils.json est-il un fichier du lot VocalRoom ? Rend son nom canonique, ou null.</summary>
    public static string? FichierDuVocal(string? chemin)
    {
        var c = (chemin ?? string.Empty).Trim();
        if (c.Length == 0 || c.Contains('/') || c.Contains('\\') || c.Contains("..", StringComparison.Ordinal)) return null;
        foreach (var connu in new[] { Exe, Sdk, Notices, Krisp })
        {
            if (string.Equals(c, connu, StringComparison.OrdinalIgnoreCase)) return connu;
        }

        return ModeleKrisp.IsMatch(c) ? c : null;
    }

    public static bool EstDuVocal(string fichier) => FichierDuVocal(fichier) is not null;

    /// <summary>L'exe, s'il est la ; null sinon.</summary>
    public static string? CheminDeLExe(string racine)
    {
        var chemin = Path.Combine(racine, Exe);
        return File.Exists(chemin) ? chemin : null;
    }

    /// <summary>Le lot obligatoire est-il complet ? (l'exe seul ne suffit pas : sans la DLL du SDK, pas de vocal).</summary>
    public static bool LotComplet(string racine) => Obligatoires.All(f => File.Exists(Path.Combine(racine, f)));
}

/// <summary>D'ou vient la copie du contrat IPC (<c>VocalRoom/Protocol</c>), et ce que l'API exige de l'exe.</summary>
public static class VocalRoomProtocolSource
{
    /// <summary>Le commit du depot NelfePlay-VocalRoom dont <c>src/NelfePlay.VocalRoom.Protocol</c> est recopie ici.</summary>
    public const string Commit = "f3f0095";

    /// <summary>La plus ancienne version de l'exe qui recoit le jeton du joueur (auth.apply_token).</summary>
    public static readonly Version VersionMinimale = new(0, 2, 0);

    /// <summary>Les commandes sans lesquelles l'API ne pilote pas l'exe (annoncees dans la reponse a hello).</summary>
    public static readonly IReadOnlyList<string> CapacitesRequises =
    [
        NelfePlay.VocalRoom.Protocol.Commands.SessionAttach,
        NelfePlay.VocalRoom.Protocol.Commands.SessionDetach,
        NelfePlay.VocalRoom.Protocol.Commands.VoiceMute,
        NelfePlay.VocalRoom.Protocol.Commands.VoiceDeafen,
        NelfePlay.VocalRoom.Protocol.Commands.AuthApplyToken,
    ];
}
