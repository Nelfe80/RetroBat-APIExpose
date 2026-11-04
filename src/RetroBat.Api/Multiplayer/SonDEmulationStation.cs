using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Multiplayer;

/// <summary>Une session audio du melangeur de Windows : son identifiant d'instance et son etat muet.</summary>
public sealed record SessionDuMelangeur(string Id, bool Muette);

/// <summary>Les sessions audio d'un processus, et leur bouton muet (le melangeur de Windows, ou un faux en test).</summary>
public interface ISessionsDuMelangeur
{
    IReadOnlyList<SessionDuMelangeur> De(string processus);

    void Muette(string processus, string id, bool muette);
}

/// <summary>
/// LE SON D'ES PENDANT UNE PARTIE MULTIPLAYER (retour du user du 26/10) : l'API lance le jeu elle-meme, ES ne sait donc pas
/// qu'une partie tourne et garde sa musique et son mode attract, que l'on entend sous le jeu. Quand ES lance un jeu, il coupe
/// son audio lui-meme (FileData.cpp) ; ici, la session audio d'emulationstation.exe est coupee dans le melangeur de Windows
/// pendant la partie, puis rendue au premier appui apres la partie (cet appui sort aussi ES de son ecran de veille, dont la
/// video retrouverait sinon le son sous l'ecran de fin), ou au plus tard quelques secondes apres.
///
/// LA MUSIQUE REVIENT TOUJOURS (decision du user du 31/10 : un joueur restait sans musique d'une partie a l'autre, et Windows
/// garde le bouton muet d'une application d'un lancement a l'autre). Hors d'une partie MULTIPLAYER, ES n'est jamais laisse
/// muet : a son demarrage, au retour d'un jeu et a chaque activite dans ES, toute session muette retrouve le son, y compris
/// une session coupee a la main dans le melangeur. Couper la musique, c'est le reglage d'ES.
///
/// LES EPREUVES ET LES COURSES MASS RUN (04/11) : le panneau des defis lance aussi le jeu lui-meme. Il rendait le son des le
/// lancement, et la musique d'ES revenait au premier appui en jeu, ou 20 s apres. Le son se tient coupe tant qu'un jeu tourne
/// (emulatorLauncher ou RetroArch) ; parti depuis quelques secondes, le son revient au premier appui, comme apres une partie
/// MULTIPLAYER. Un lancement qui ne donne aucun jeu ne laisse pas ES muet.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SonDEmulationStation : IDisposable
{
    public const string Processus = "emulationstation";

    /// <summary>Au demarrage, combien de temps attendre qu'ES ouvre sa session pour lui rendre le son.</summary>
    private static readonly TimeSpan PatienceAuDemarrage = TimeSpan.FromMinutes(3);

    /// <summary>Au retour d'un jeu lance par ES, combien de temps attendre qu'il rouvre sa session.</summary>
    private static readonly TimeSpan PatienceAuRetour = TimeSpan.FromSeconds(30);

    /// <summary>Le melangeur n'est pas relu plus souvent que ca, quelle que soit l'activite dans ES.</summary>
    private static readonly TimeSpan EntreDeuxControles = TimeSpan.FromSeconds(2);

    /// <summary>Les processus d'un jeu en cours : le lanceur vit tant que l'emulateur tourne.</summary>
    private static readonly string[] ProcessusDuJeu = ["emulatorLauncher", "retroarch"];

    private readonly ISessionsDuMelangeur _melangeur;
    private readonly TimeSpan _apresLAppui;
    private readonly TimeSpan _periode;
    private readonly TimeSpan _auPlusTard;
    private readonly Func<bool> _jeuEnCours;
    private readonly TimeSpan _absenceDuJeu;
    private readonly TimeSpan _sansJeuAuDepart;
    private readonly ILogger<SonDEmulationStation>? _journal;
    private readonly IDisposable? _abonnement;
    private readonly System.Threading.Timer _minuterie;
    private readonly object _verrou = new();
    private readonly HashSet<string> _coupees = new(StringComparer.Ordinal);
    private Etat _etat = Etat.Libre;
    private DateTime _rendreAvant;
    private DateTime _dernierControle;
    private DateTime _coupeA;
    private bool _jeuVu;
    private DateTime? _absentDepuis;

    private enum Etat
    {
        Libre,
        Coupe,
        AuProchainAppui,
        ARendre,
    }

    public SonDEmulationStation(IEventBus? bus = null, ILogger<SonDEmulationStation>? journal = null)
        : this(new MelangeurDeWindows(), TimeSpan.FromMilliseconds(600), bus, journal)
    {
    }

    internal SonDEmulationStation(ISessionsDuMelangeur melangeur, TimeSpan apresLAppui, IEventBus? bus = null,
        ILogger<SonDEmulationStation>? journal = null, TimeSpan? periode = null, TimeSpan? auPlusTard = null,
        Func<bool>? jeuEnCours = null, TimeSpan? absenceDuJeu = null, TimeSpan? sansJeuAuDepart = null)
    {
        _melangeur = melangeur;
        _apresLAppui = apresLAppui;
        _periode = periode ?? TimeSpan.FromSeconds(2);
        _auPlusTard = auPlusTard ?? TimeSpan.FromSeconds(20);
        _jeuEnCours = jeuEnCours ?? UnJeuTourne;
        // Plus que l'ecart entre deux lancements (une course MASS RUN qui suit la precedente) ; un lanceur part en moins d'une
        // seconde, 90 s sans jeu veulent dire que le lancement a echoue.
        _absenceDuJeu = absenceDuJeu ?? TimeSpan.FromSeconds(6);
        _sansJeuAuDepart = sansJeuAuDepart ?? TimeSpan.FromSeconds(90);
        _journal = journal;
        _minuterie = new System.Threading.Timer(_ => Tenir(), null, Timeout.Infinite, Timeout.Infinite);
        _abonnement = bus?.Subscribe<EventEnvelope>(e => SurEvenement(e.Type));

        // Au demarrage, quelle que soit l'histoire : Windows a pu garder ES muet depuis son dernier lancement.
        lock (_verrou)
        {
            _etat = Etat.ARendre;
            _rendreAvant = DateTime.UtcNow + PatienceAuDemarrage;
        }

        Tenir();
        ArmerSiBesoin();
    }

    /// <summary>ES est-il tenu muet en ce moment (partie en cours, ou fin de partie avant le premier appui) ?</summary>
    public bool Coupe
    {
        get
        {
            lock (_verrou)
            {
                return _etat is Etat.Coupe or Etat.AuProchainAppui;
            }
        }
    }

    /// <summary>Un jeu lance par l'API part (partie MULTIPLAYER, epreuve, course MASS RUN) : ES se tait jusqu'a la fin.</summary>
    public void Couper()
    {
        lock (_verrou)
        {
            _etat = Etat.Coupe;
            _coupeA = DateTime.UtcNow;
            _jeuVu = false;
            _absentDepuis = null;
        }

        DecrireAvantLaPartie();
        Tenir();
        Armer();
    }

    /// <summary>La partie est finie : le son revient au premier appui du joueur, ou au plus tard apres quelques secondes.</summary>
    public void RendreAuProchainAppui()
    {
        lock (_verrou)
        {
            if (_etat == Etat.Coupe)
            {
                _etat = Etat.AuProchainAppui;
                _rendreAvant = DateTime.UtcNow + _auPlusTard;
            }
        }
    }

    /// <summary>La partie n'a pas pu partir : le son revient tout de suite.</summary>
    public void Rendre()
    {
        lock (_verrou)
        {
            if (_etat is not (Etat.Coupe or Etat.AuProchainAppui))
            {
                return;
            }

            _etat = Etat.Libre;
        }

        Desarmer();
        RendreToutes();
    }

    private void SurEvenement(string? type)
    {
        switch (type)
        {
            case "panel.input.pressed":
                AppuiRecu();
                ActiviteDansEs();
                break;
            case "ui.system.selected.raw":
            case "ui.game.selected.raw":
                ActiviteDansEs();
                break;
            case "ui.game.ended.raw":
                RetourDUnJeu();
                break;
        }
    }

    private void AppuiRecu()
    {
        lock (_verrou)
        {
            if (_etat != Etat.AuProchainAppui)
            {
                return;
            }
        }

        // Le temps qu'ES traite le meme appui et quitte son ecran de veille : sa video ne reprend pas le son.
        _ = Task.Delay(_apresLAppui).ContinueWith(_ =>
        {
            lock (_verrou)
            {
                if (_etat != Etat.AuProchainAppui)
                {
                    return;
                }

                _etat = Etat.Libre;
            }

            Desarmer();
            RendreToutes();
        }, TaskScheduler.Default);
    }

    /// <summary>Le joueur est dans ES, sans partie MULTIPLAYER : ES ne reste pas muet.</summary>
    private void ActiviteDansEs()
    {
        lock (_verrou)
        {
            if (_etat != Etat.Libre || DateTime.UtcNow - _dernierControle < EntreDeuxControles)
            {
                return;
            }

            _dernierControle = DateTime.UtcNow;
        }

        _ = Task.Run(RendreToutes);
    }

    /// <summary>ES reprend la main apres un jeu qu'il a lance : le son revient des qu'il rouvre sa session.</summary>
    private void RetourDUnJeu()
    {
        lock (_verrou)
        {
            if (_etat != Etat.Libre)
            {
                return;
            }

            _etat = Etat.ARendre;
            _rendreAvant = DateTime.UtcNow + PatienceAuRetour;
        }

        Armer();
    }

    /// <summary>
    /// A chaque tour : pendant la partie, une session d'ES ouverte entre-temps (la video d'un ecran de veille) se tait aussi ;
    /// apres la partie, le son revient a l'echeance meme sans appui ; au demarrage, il revient des que la session d'ES existe.
    /// </summary>
    private void Tenir()
    {
        try
        {
            Etat etat;
            DateTime echeance;
            lock (_verrou)
            {
                etat = _etat;
                echeance = _rendreAvant;
            }

            if (etat == Etat.ARendre)
            {
                RendreQuandEsEstLa();
                return;
            }

            if (etat == Etat.AuProchainAppui && DateTime.UtcNow >= echeance)
            {
                lock (_verrou)
                {
                    if (_etat != Etat.AuProchainAppui)
                    {
                        return;
                    }

                    _etat = Etat.Libre;
                }

                Desarmer();
                RendreToutes();
                return;
            }

            if (etat is not (Etat.Coupe or Etat.AuProchainAppui))
            {
                return;
            }

            if (etat == Etat.Coupe && LeJeuEstParti())
            {
                _journal?.LogInformation("Son d'EmulationStation : plus de jeu en cours, le son revient au premier appui");
                RendreAuProchainAppui();
            }

            foreach (var session in _melangeur.De(Processus))
            {
                if (session.Muette)
                {
                    continue;
                }

                bool nouvelle;
                lock (_verrou)
                {
                    if (_etat is not (Etat.Coupe or Etat.AuProchainAppui))
                    {
                        return;
                    }

                    nouvelle = _coupees.Add(session.Id);
                }

                _melangeur.Muette(Processus, session.Id, true);
                if (nouvelle)
                {
                    _journal?.LogInformation("MULTIPLAYER : son d'EmulationStation coupe pendant la partie");
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _journal?.LogDebug(ex, "MULTIPLAYER : melangeur de Windows injoignable");
        }
    }

    /// <summary>
    /// Le jeu lance par l'API est-il parti ? Absent depuis <see cref="_absenceDuJeu"/>, apres avoir tourne, ou sans jamais
    /// etre apparu depuis la coupure (lancement rate). Une absence plus courte (une relance) ne compte pas.
    /// </summary>
    private bool LeJeuEstParti()
    {
        var enCours = _jeuEnCours();
        var maintenant = DateTime.UtcNow;
        lock (_verrou)
        {
            if (enCours)
            {
                _jeuVu = true;
                _absentDepuis = null;
                return false;
            }

            _absentDepuis ??= maintenant;
            return maintenant - _absentDepuis.Value >= _absenceDuJeu && (_jeuVu || maintenant - _coupeA >= _sansJeuAuDepart);
        }
    }

    private static bool UnJeuTourne()
    {
        foreach (var nom in ProcessusDuJeu)
        {
            var processus = Process.GetProcessesByName(nom);
            foreach (var p in processus)
            {
                p.Dispose();
            }

            if (processus.Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private void RendreQuandEsEstLa()
    {
        var sessions = _melangeur.De(Processus);
        var muettes = sessions.Where(s => s.Muette).ToList();
        foreach (var session in muettes)
        {
            _melangeur.Muette(Processus, session.Id, false);
        }

        if (muettes.Count > 0)
        {
            _journal?.LogInformation("MULTIPLAYER : son d'EmulationStation rendu ({Muettes} session(s) muette(s) a son arrivee)", muettes.Count);
        }

        bool fini;
        lock (_verrou)
        {
            fini = _etat == Etat.ARendre && (sessions.Count > 0 || DateTime.UtcNow >= _rendreAvant);
            if (fini)
            {
                _etat = Etat.Libre;
            }
        }

        if (fini)
        {
            Desarmer();
        }
    }

    /// <summary>Toute session muette d'ES retrouve le son, celle que nous avons coupee comme une autre.</summary>
    private void RendreToutes()
    {
        lock (_verrou)
        {
            _coupees.Clear();
        }

        try
        {
            var muettes = _melangeur.De(Processus).Where(s => s.Muette).ToList();
            foreach (var session in muettes)
            {
                _melangeur.Muette(Processus, session.Id, false);
            }

            if (muettes.Count > 0)
            {
                _journal?.LogInformation("MULTIPLAYER : son d'EmulationStation rendu");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _journal?.LogWarning(ex, "MULTIPLAYER : son d'EmulationStation non rendu");
        }
    }

    /// <summary>L'etat du son d'ES au depart d'une partie : aucune session, c'est qu'ES a ferme son audio ; muette, qu'il l'etait deja.</summary>
    private void DecrireAvantLaPartie()
    {
        try
        {
            var sessions = _melangeur.De(Processus);
            _journal?.LogInformation("MULTIPLAYER : son d'EmulationStation avant la partie : {Sessions} session(s), {Muettes} muette(s)",
                sessions.Count, sessions.Count(s => s.Muette));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _journal?.LogDebug(ex, "MULTIPLAYER : melangeur de Windows injoignable");
        }
    }

    private void Armer() => _minuterie.Change(_periode, _periode);

    private void ArmerSiBesoin()
    {
        lock (_verrou)
        {
            if (_etat == Etat.ARendre)
            {
                Armer();
            }
        }
    }

    private void Desarmer() => _minuterie.Change(Timeout.Infinite, Timeout.Infinite);

    public void Dispose()
    {
        _abonnement?.Dispose();
        _minuterie.Dispose();
    }
}

/// <summary>
/// Le melangeur de Windows (Core Audio) : les sessions audio d'un processus sur chaque sortie active, et leur bouton muet,
/// celui que montre le melangeur de volume.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MelangeurDeWindows : ISessionsDuMelangeur
{
    public IReadOnlyList<SessionDuMelangeur> De(string processus)
    {
        var resultat = new List<SessionDuMelangeur>();
        Parcourir(processus, (id, volume) =>
        {
            Marshal.ThrowExceptionForHR(volume.GetMute(out var muette));
            resultat.Add(new SessionDuMelangeur(id, muette));
            return false;
        });
        return resultat;
    }

    public void Muette(string processus, string id, bool muette)
    {
        Parcourir(processus, (ici, volume) =>
        {
            if (!string.Equals(ici, id, StringComparison.Ordinal))
            {
                return false;
            }

            var contexte = Guid.Empty;
            Marshal.ThrowExceptionForHR(volume.SetMute(muette, ref contexte));
            return true;
        });
    }

    /// <summary>Chaque session du processus sur chaque sortie active ; <paramref name="action"/> rend vrai pour s'arreter.</summary>
    private static void Parcourir(string processus, Func<string, ISimpleAudioVolume, bool> action)
    {
        var pids = Process.GetProcessesByName(processus).Select(p =>
        {
            using (p)
            {
                return (uint)p.Id;
            }
        }).ToHashSet();
        if (pids.Count == 0)
        {
            return;
        }

        var enumerateur = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            Marshal.ThrowExceptionForHR(enumerateur.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out var sorties));
            try
            {
                Marshal.ThrowExceptionForHR(sorties.GetCount(out var nombre));
                for (var i = 0; i < nombre; i++)
                {
                    Marshal.ThrowExceptionForHR(sorties.Item(i, out var sortie));
                    try
                    {
                        if (ParcourirLaSortie(sortie, pids, action))
                        {
                            return;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sortie);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sorties);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerateur);
        }
    }

    private static bool ParcourirLaSortie(IMMDevice sortie, HashSet<uint> pids, Func<string, ISimpleAudioVolume, bool> action)
    {
        var iid = typeof(IAudioSessionManager2).GUID;
        Marshal.ThrowExceptionForHR(sortie.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var objet));
        var gestionnaire = (IAudioSessionManager2)objet;
        try
        {
            Marshal.ThrowExceptionForHR(gestionnaire.GetSessionEnumerator(out var sessions));
            try
            {
                Marshal.ThrowExceptionForHR(sessions.GetCount(out var nombre));
                for (var i = 0; i < nombre; i++)
                {
                    Marshal.ThrowExceptionForHR(sessions.GetSession(i, out var controle));
                    try
                    {
                        var controle2 = (IAudioSessionControl2)controle;
                        if (controle2.GetProcessId(out var pid) != 0 || !pids.Contains(pid)
                            || controle2.GetSessionInstanceIdentifier(out var pointeur) != 0)
                        {
                            continue;
                        }

                        var id = Marshal.PtrToStringUni(pointeur) ?? string.Empty;
                        Marshal.FreeCoTaskMem(pointeur);
                        if (action(id, (ISimpleAudioVolume)controle))
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(controle);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sessions);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(gestionnaire);
        }

        return false;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flux, int etats, out IMMDeviceCollection sorties);

        [PreserveSig] int GetDefaultAudioEndpoint(int flux, int role, out IMMDevice sortie);

        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice sortie);

        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int nombre);

        [PreserveSig] int Item(int index, out IMMDevice sortie);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int contexte, IntPtr parametres, [MarshalAs(UnmanagedType.IUnknown)] out object objet);

        [PreserveSig] int OpenPropertyStore(int acces, out IntPtr proprietes);

        [PreserveSig] int GetId(out IntPtr id);

        [PreserveSig] int GetState(out int etat);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr session, int drapeaux, out IntPtr controle);

        [PreserveSig] int GetSimpleAudioVolume(IntPtr session, int drapeaux, out IntPtr volume);

        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);

        [PreserveSig] int RegisterSessionNotification(IntPtr client);

        [PreserveSig] int UnregisterSessionNotification(IntPtr client);

        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string session, IntPtr client);

        [PreserveSig] int UnregisterDuckNotification(IntPtr client);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int nombre);

        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int etat);

        [PreserveSig] int GetDisplayName(out IntPtr nom);

        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string nom, ref Guid contexte);

        [PreserveSig] int GetIconPath(out IntPtr chemin);

        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string chemin, ref Guid contexte);

        [PreserveSig] int GetGroupingParam(out Guid groupe);

        [PreserveSig] int SetGroupingParam(ref Guid groupe, ref Guid contexte);

        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);

        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int etat);

        [PreserveSig] int GetDisplayName(out IntPtr nom);

        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string nom, ref Guid contexte);

        [PreserveSig] int GetIconPath(out IntPtr chemin);

        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string chemin, ref Guid contexte);

        [PreserveSig] int GetGroupingParam(out Guid groupe);

        [PreserveSig] int SetGroupingParam(ref Guid groupe, ref Guid contexte);

        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);

        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);

        [PreserveSig] int GetSessionIdentifier(out IntPtr id);

        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);

        [PreserveSig] int GetProcessId(out uint pid);

        [PreserveSig] int IsSystemSoundsSession();

        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool sansAttenuation);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float niveau, ref Guid contexte);

        [PreserveSig] int GetMasterVolume(out float niveau);

        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muette, ref Guid contexte);

        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muette);
    }
}
