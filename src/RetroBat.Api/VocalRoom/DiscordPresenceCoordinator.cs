using System.Text.Json.Nodes;
using NelfePlay.VocalRoom.Protocol;

namespace RetroBat.Api.VocalRoom;

/// <summary>Qui publie l'activite Discord de la partie : un seul editeur a la fois (CDC NelfeVoice §11 bis).</summary>
public enum ProprietaireDeLaPresence
{
    /// <summary>Personne : le joueur ne partage pas son activite.</summary>
    Personne,

    /// <summary>RetroArch, par son propre <c>discord_allow</c> (V1 : toujours, sans jamais toucher a sa configuration).</summary>
    RetroArchNatif,

    /// <summary>NelfePlay, par l'exe du vocal (Application ID de NelfePlay), RetroArch coupe pour CETTE execution seulement.</summary>
    NelfePlay,
}

/// <summary>
/// L'ARBITRE DE LA RICH PRESENCE DISCORD (CDC NelfeVoice v1.3 §11 bis ; lot H avance le 12/11 : le user veut la Rich
/// Presence et les amis Discord AVANT la production, pour l'approbation Comms Access).
///
/// SQUELETTE DESACTIVE. Tant que <see cref="Active"/> est faux, la decision est toujours
/// <see cref="ProprietaireDeLaPresence.RetroArchNatif"/> : RetroArch publie comme avant (son <c>discord_allow</c>, lu,
/// jamais ecrit), l'exe du vocal ne publie rien, et <see cref="AppliquerAsync"/> ne fait rien. Ce qui reste a faire pour
/// l'allumer :
/// <list type="number">
/// <item>l'exe : <c>presence.set</c>/<c>presence.clear</c> (aujourd'hui <c>phase2_disabled</c>), sur
/// <c>Client::UpdateRichPresence</c>/<c>ClearRichPresence</c> ;</item>
/// <item>le lanceur MULTIPLAYER : un <c>discord_allow = "false"</c> pour CETTE execution (fichier de surcharge passe a
/// RetroArch), jamais dans le retroarch.cfg du joueur ;</item>
/// <item>le site : la preference « afficher mon activite » (consentement de partage) dans le profil vocal ;</item>
/// <item>une verification sur un vrai Discord : une seule activite visible.</item>
/// </list>
/// </summary>
public sealed class DiscordPresenceCoordinator
{
    /// <summary>L'interrupteur du lot H. NE PAS ALLUMER sans les quatre points ci-dessus.</summary>
    public const bool Active = false;

    private readonly VocalRoomProcessService? _processus;
    private readonly ILogger<DiscordPresenceCoordinator>? _journal;

    public DiscordPresenceCoordinator(VocalRoomProcessService? processus = null, ILogger<DiscordPresenceCoordinator>? journal = null)
    {
        _processus = processus;
        _journal = journal;
    }

    /// <summary>La derniere decision (pour le diagnostic).</summary>
    public ProprietaireDeLaPresence Proprietaire { get; private set; } = ProprietaireDeLaPresence.RetroArchNatif;

    /// <summary>
    /// Qui publie ? Pur. Desactive : RetroArch, toujours (V1). Active : NelfePlay seulement pour une partie MULTIPLAYER dont
    /// le joueur partage son activite, avec l'exe du vocal pret et un RetroArch qu'on sait couper pour cette execution ;
    /// sinon RetroArch s'il publie, sinon personne. Jamais deux editeurs.
    /// </summary>
    public static ProprietaireDeLaPresence Decider(
        bool active,
        bool partageDActivite,
        bool partieMultiplayer,
        bool exePret,
        bool retroArchCoupablePourCetteExecution,
        bool? discordAllowDeRetroArch)
    {
        if (!active)
        {
            return ProprietaireDeLaPresence.RetroArchNatif;
        }

        if (!partageDActivite)
        {
            // Le mode prive du joueur passe avant tout : ni NelfePlay ni RetroArch... si l'on peut couper RetroArch.
            return retroArchCoupablePourCetteExecution || discordAllowDeRetroArch != true
                ? ProprietaireDeLaPresence.Personne
                : ProprietaireDeLaPresence.RetroArchNatif;
        }

        if (partieMultiplayer && exePret && (retroArchCoupablePourCetteExecution || discordAllowDeRetroArch != true))
        {
            return ProprietaireDeLaPresence.NelfePlay;
        }

        return discordAllowDeRetroArch == true ? ProprietaireDeLaPresence.RetroArchNatif : ProprietaireDeLaPresence.Personne;
    }

    /// <summary>
    /// L'activite que NelfePlay publierait : titre canonique du jeu, systeme, mode, nombre de joueurs, debut. JAMAIS de
    /// chemin de ROM, d'empreinte, de mot de passe netplay, de lobby ni d'adresse locale (§11 bis). Pur.
    /// </summary>
    public static JsonObject Composer(string titreDuJeu, string systeme, string mode, int joueurs, int places, DateTimeOffset debut)
        => new()
        {
            ["details"] = Borne(titreDuJeu, 128),
            ["state"] = Borne($"{mode} · {systeme}", 128),
            ["party_size"] = Math.Max(1, joueurs),
            ["party_max"] = Math.Max(Math.Max(1, joueurs), places),
            ["start"] = debut.ToUnixTimeSeconds(),
        };

    /// <summary>
    /// Lit <c>discord_allow</c> dans un retroarch.cfg, sans jamais l'ecrire. Null s'il est absent ou illisible.
    /// </summary>
    public static bool? LireDiscordAllow(string cheminDuCfg)
    {
        try
        {
            if (!File.Exists(cheminDuCfg)) return null;
            foreach (var ligne in File.ReadLines(cheminDuCfg))
            {
                var l = ligne.Trim();
                if (!l.StartsWith("discord_allow", StringComparison.Ordinal)) continue;
                var egal = l.IndexOf('=');
                if (egal < 0 || l[..egal].Trim() != "discord_allow") continue;
                var valeur = l[(egal + 1)..].Trim().Trim('"').ToLowerInvariant();
                return valeur is "true" or "1";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// Applique la decision. Desactive : ne fait rien (RetroArch garde sa presence, l'exe n'en publie aucune). Active : la
    /// commande <c>presence.set</c> ou <c>presence.clear</c> a l'exe (que l'exe 0.2.0 refuse encore : phase2_disabled).
    /// </summary>
    public async Task AppliquerAsync(ProprietaireDeLaPresence decision, JsonObject? activite)
    {
        Proprietaire = decision;
#pragma warning disable CS0162 // code inaccessible tant que le lot H n'est pas allume : voulu
        if (!Active || _processus is null)
        {
            return;
        }

        var reponse = decision == ProprietaireDeLaPresence.NelfePlay && activite is not null
            ? await _processus.CommanderAsync(Commands.PresenceSet, activite).ConfigureAwait(false)
            : await _processus.CommanderAsync(Commands.PresenceClear).ConfigureAwait(false);
        _journal?.LogInformation("VOCAL : presence Discord {Decision} ({Statut})", decision,
            Messages.Bool(reponse, "ok") == true ? "ok" : Messages.Str(Messages.Obj(reponse, "error"), "code") ?? "sans reponse");
#pragma warning restore CS0162
    }

    private static string Borne(string texte, int max) => texte.Length <= max ? texte : texte[..max];
}
