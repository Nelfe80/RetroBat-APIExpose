using RetroBat.Api.Netplay;

namespace RetroBat.Api.Replay.Recording;

/// <summary>
/// LE REPLAY EN NETPLAY (lot 3 de RetroArch NelfePlay, nelfeplay.2) : qui enregistre quoi, decision PURE.
///
/// Sous la 1.22.2 officielle et sous nelfeplay.1, le replay d'un pair netplay ne porte que les entrees du port 0 : relu, il
/// diverge (banc du 25/11 : 5 ecarts sur 5). Le lot 3 (retroportage de la PR amont 19870) y met les entrees CONFIRMEES de tous
/// les joueurs, chez l'hote comme chez l'invite. Tant que cet exe n'est pas en place, l'invite n'enregistre rien (comportement
/// d'avant) ; l'hote, lui, a toujours enregistre sa propre partie (aucune porte ne l'arretait).
/// Le spectateur n'enregistre jamais : il regarde la partie d'un autre.
/// </summary>
internal static class ReplayNetplay
{
    internal enum Porte
    {
        /// <summary>Cette borne joue sa propre partie (hote compris) : le flot habituel de l'enregistreur.</summary>
        Habituelle,
        /// <summary>Invitee en joueur, exe NelfePlay du lot 3 en place : le replay de l'invite s'enregistre.</summary>
        InviteEnregistre,
        /// <summary>Invitee en joueur, mais l'exe ne sait pas porter tous les joueurs : pas de replay (comme avant).</summary>
        InviteSansExe,
        /// <summary>Spectatrice : jamais de replay.</summary>
        Spectateur,
    }

    internal static Porte Decider(NetplayGuestService.Role role, bool exeDuLot3)
        => role switch
        {
            NetplayGuestService.Role.Joueur => exeDuLot3 ? Porte.InviteEnregistre : Porte.InviteSansExe,
            NetplayGuestService.Role.Spectateur => Porte.Spectateur,
            _ => Porte.Habituelle,
        };
}
