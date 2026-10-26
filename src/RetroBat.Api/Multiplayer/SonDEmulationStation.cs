using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

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
/// pendant la partie, comme le ferait le joueur, puis rendue au premier appui apres la partie : cet appui sort aussi ES de son
/// ecran de veille, dont la video retrouverait sinon le son sous l'ecran de fin. Aucune option d'ES n'est touchee, et une
/// session que le joueur avait deja coupee le reste.
///
/// Une marque sur le disque dit qu'ES a ete coupe : si l'API tombe en pleine partie, elle lui rend le son a son redemarrage,
/// des que la session d'ES existe (ES peut demarrer apres l'API).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SonDEmulationStation : IDisposable
{
    public const string Processus = "emulationstation";

    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(2);

    /// <summary>Au redemarrage, combien de temps attendre qu'ES ouvre sa session pour lui rendre le son.</summary>
    private static readonly TimeSpan PatienceAuDemarrage = TimeSpan.FromMinutes(3);

    private readonly ISessionsDuMelangeur _melangeur;
    private readonly string _marque;
    private readonly TimeSpan _apresLAppui;
    private readonly ILogger<SonDEmulationStation>? _journal;
    private readonly IDisposable? _abonnement;
    private readonly System.Threading.Timer _minuterie;
    private readonly object _verrou = new();
    private readonly HashSet<string> _coupees = new(StringComparer.Ordinal);
    private Etat _etat = Etat.Libre;
    private DateTime _rendreAvant;

    private enum Etat
    {
        Libre,
        Coupe,
        AuProchainAppui,
        ARendreAuDemarrage,
    }

    public SonDEmulationStation(IEventBus? bus = null, ILogger<SonDEmulationStation>? journal = null)
        : this(new MelangeurDeWindows(), DefaultMarque, TimeSpan.FromMilliseconds(600), bus, journal)
    {
    }

    internal SonDEmulationStation(ISessionsDuMelangeur melangeur, string marque, TimeSpan apresLAppui, IEventBus? bus = null,
        ILogger<SonDEmulationStation>? journal = null)
    {
        _melangeur = melangeur;
        _marque = marque;
        _apresLAppui = apresLAppui;
        _journal = journal;
        _minuterie = new System.Threading.Timer(_ => Tenir(), null, Timeout.Infinite, Timeout.Infinite);
        // Tout appui sur la borne : apres une partie, il rend le son.
        _abonnement = bus?.Subscribe<EventEnvelope>(e =>
        {
            if (string.Equals(e.Type, "panel.input.pressed", StringComparison.Ordinal))
            {
                AppuiRecu();
            }
        });

        if (File.Exists(_marque))
        {
            lock (_verrou)
            {
                _etat = Etat.ARendreAuDemarrage;
                _rendreAvant = DateTime.UtcNow + PatienceAuDemarrage;
            }

            Tenir();
            Armer();
        }
    }

    public static string DefaultMarque => Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfeplay", "es-son-coupe.json");

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

    /// <summary>Une partie MULTIPLAYER part : ES se tait jusqu'a la fin.</summary>
    public void Couper()
    {
        lock (_verrou)
        {
            _etat = Etat.Coupe;
        }

        EcrireLaMarque();
        Tenir();
        Armer();
    }

    /// <summary>La partie est finie : le son revient au premier appui du joueur.</summary>
    public void RendreAuProchainAppui()
    {
        lock (_verrou)
        {
            if (_etat == Etat.Coupe)
            {
                _etat = Etat.AuProchainAppui;
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
        RendreLesNotres();
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
            RendreLesNotres();
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// A chaque tour : pendant la partie, une session d'ES ouverte entre-temps (la video d'un ecran de veille) se tait aussi ;
    /// au redemarrage, le son revient des que la session d'ES existe.
    /// </summary>
    private void Tenir()
    {
        try
        {
            Etat etat;
            lock (_verrou)
            {
                etat = _etat;
            }

            if (etat == Etat.ARendreAuDemarrage)
            {
                RendreAuDemarrage();
                return;
            }

            if (etat is not (Etat.Coupe or Etat.AuProchainAppui))
            {
                return;
            }

            foreach (var session in _melangeur.De(Processus))
            {
                if (session.Muette)
                {
                    continue;
                }

                _melangeur.Muette(Processus, session.Id, true);
                bool nouvelle;
                lock (_verrou)
                {
                    nouvelle = _coupees.Add(session.Id);
                }

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

    private void RendreAuDemarrage()
    {
        var sessions = _melangeur.De(Processus);
        // La marque dit que c'est l'API qui a coupe ES : toute session d'ES muette retrouve le son.
        foreach (var session in sessions.Where(s => s.Muette))
        {
            _melangeur.Muette(Processus, session.Id, false);
        }

        bool fini;
        lock (_verrou)
        {
            fini = sessions.Count > 0 || DateTime.UtcNow >= _rendreAvant;
            if (fini)
            {
                _etat = Etat.Libre;
            }
        }

        if (fini)
        {
            if (sessions.Count > 0)
            {
                _journal?.LogInformation("MULTIPLAYER : son d'EmulationStation rendu apres un redemarrage de l'API en pleine partie");
            }

            Desarmer();
            EffacerLaMarque();
        }
    }

    private void RendreLesNotres()
    {
        string[] ids;
        lock (_verrou)
        {
            ids = [.. _coupees];
            _coupees.Clear();
        }

        try
        {
            var presentes = _melangeur.De(Processus);
            foreach (var session in presentes.Where(s => s.Muette && ids.Contains(s.Id, StringComparer.Ordinal)))
            {
                _melangeur.Muette(Processus, session.Id, false);
            }

            if (ids.Length > 0)
            {
                _journal?.LogInformation("MULTIPLAYER : son d'EmulationStation rendu");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _journal?.LogWarning(ex, "MULTIPLAYER : son d'EmulationStation non rendu");
            return;
        }

        EffacerLaMarque();
    }

    private void Armer() => _minuterie.Change(Periode, Periode);

    private void Desarmer() => _minuterie.Change(Timeout.Infinite, Timeout.Infinite);

    private void EcrireLaMarque()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_marque)!);
            File.WriteAllText(_marque, JsonSerializer.Serialize(new { coupe = DateTime.UtcNow }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _journal?.LogDebug(ex, "MULTIPLAYER : marque du son d'ES non ecrite");
        }
    }

    private void EffacerLaMarque()
    {
        try
        {
            File.Delete(_marque);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _journal?.LogDebug(ex, "MULTIPLAYER : marque du son d'ES non effacee");
        }
    }

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
