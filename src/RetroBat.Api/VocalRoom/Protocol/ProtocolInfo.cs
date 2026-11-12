// COPIE VERSIONNEE du contrat IPC de NelfePlay-VocalRoom (depot prive Nelfe80/NelfePlay-VocalRoom,
// src/NelfePlay.VocalRoom.Protocol, commit f3f0095, protocole 1). Ne pas modifier ici : changer le depot VocalRoom,
// puis recopier (voir VocalRoomProtocolSource et OUTILS_COMMUNS.md, « Vocal Discord »).
namespace NelfePlay.VocalRoom.Protocol;

/// <summary>
/// Le contrat IPC local, version 1 (docs/PROTOCOL.md). Contrat interne NelfePlay, PAS une API Discord.
/// APIExpose cree le named pipe et pilote ; NelfePlay-VocalRoom.exe s'y connecte et obeit.
/// </summary>
public static class ProtocolInfo
{
    /// <summary>Version courante du protocole.</summary>
    public const int Version = 1;

    /// <summary>Plus ancienne version que cet exe sait parler.</summary>
    public const int MinVersion = 1;

    /// <summary>Plus recente version que cet exe sait parler.</summary>
    public const int MaxVersion = 1;

    /// <summary>Taille maximale d'une trame (charge JSON UTF-8, sans les 4 octets de longueur).</summary>
    public const int MaxFrameBytes = 64 * 1024;

    /// <summary>Prefixe impose au nom du named pipe ; le reste est aleatoire, choisi par APIExpose.</summary>
    public const string PipePrefix = "nelfeplay-vocalroom-";

    /// <summary>Longueur du secret d'appairage, en octets (transmis en hexadecimal sur stdin, jamais en argv).</summary>
    public const int SecretBytes = 32;

    /// <summary>Longueur des nonces de la poignee de main, en octets.</summary>
    public const int NonceBytes = 16;
}

/// <summary>Commandes APIExpose → VocalRoom (§6.2 du CDC).</summary>
public static class Commands
{
    public const string Hello = "hello";
    public const string Health = "health";
    public const string Version = "version";
    public const string Status = "status";
    public const string Diagnostics = "diagnostics";
    public const string Shutdown = "shutdown";

    public const string AuthBegin = "auth.begin";
    public const string AuthApplyToken = "auth.apply_token";
    public const string AuthLogout = "auth.logout";

    public const string SessionAttach = "session.attach";
    public const string SessionDetach = "session.detach";

    public const string VoiceMute = "voice.mute";
    public const string VoiceDeafen = "voice.deafen";

    public const string AudioDevices = "audio.devices";
    public const string AudioSelect = "audio.select";
    public const string AudioMode = "audio.mode";

    // Phase 2 seulement (Lot H) : refusees en V1.
    public const string PresenceSet = "presence.set";
    public const string PresenceClear = "presence.clear";
    public const string SocialInviteSend = "social.invite.send";
    public const string SocialInviteAccept = "social.invite.accept";

    /// <summary>Commandes de la phase 2, refusees avec <see cref="ErrorCodes.Phase2Disabled"/>.</summary>
    public static readonly IReadOnlySet<string> Phase2 = new HashSet<string>(StringComparer.Ordinal)
    {
        PresenceSet, PresenceClear, SocialInviteSend, SocialInviteAccept,
    };

    /// <summary>
    /// Ce qu'un mode autonome ferait (§0 du CDC) : creer une conversation, un lobby, rejoindre par un secret.
    /// Refuse avec <see cref="ErrorCodes.Forbidden"/>, meme pour un pilote authentifie : le lobby se cree cote serveur.
    /// </summary>
    public static readonly IReadOnlySet<string> Forbidden = new HashSet<string>(StringComparer.Ordinal)
    {
        "lobby.create", "lobby.join", "lobby.join_secret", "lobby.leave", "lobby.members.add", "lobby.members.remove",
        "conversation.create", "conversation.join", "session.create", "call.start_free", "standalone",
    };
}

/// <summary>Evenements VocalRoom → APIExpose.</summary>
public static class Events
{
    /// <summary>Premiere trame envoyee par l'exe a la connexion : son nonce et les versions qu'il parle.</summary>
    public const string HandshakeChallenge = "handshake.challenge";

    public const string VoiceState = "voice.state";
    public const string VoiceMember = "voice.member";
    public const string VoiceError = "voice.error";
    public const string AudioLevel = "audio.level";
}

/// <summary>Codes d'erreur stables du protocole (champ <c>error.code</c> d'une reponse).</summary>
public static class ErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string UnsupportedVersion = "unsupported_version";
    public const string NotAuthenticated = "not_authenticated";
    public const string Unauthorized = "unauthorized";
    public const string UnknownCommand = "unknown_command";
    public const string Forbidden = "forbidden";
    public const string Phase2Disabled = "phase2_disabled";
    public const string NotImplemented = "not_implemented";
    public const string RequestIdReused = "request_id_reused";
    public const string InvalidPayload = "invalid_payload";
    public const string StaleGeneration = "stale_generation";
    public const string WrongRoom = "wrong_room";
    public const string SessionBusy = "session_busy";
    public const string NoSession = "no_session";
    public const string SdkUnavailable = "sdk_unavailable";
    public const string ShuttingDown = "shutting_down";
    public const string Internal = "internal";
}
