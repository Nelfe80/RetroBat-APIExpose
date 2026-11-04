using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.Speedrun;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.MassRun;

/// <summary>
/// UNE SALLE MASS RUN LANCEE PAR L'API (CDC MASS RUN, lot L2). L'API pose la salle dans le canal (<see cref="MassRunCanal"/>) :
/// niveau, heure du GO!, pseudo et couleur d'avatar du joueur, etat de depart ; elle lance le jeu sur le coeur hote, puis, tant
/// que la partie tourne, ecrit les coureurs de chaque image et lit la partie du joueur.
///
/// En L2 les coureurs viennent des pistes .npiste enregistrees sur la borne (coureurs simules) ; au lot L3 ils viendront du
/// noeud de la salle, par le meme canal. Une partie sur le coeur hote n'est jamais classee (voir NelfePlayScoringReporter).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MassRunService : IDisposable
{
    /// <summary>Super Mario Bros. (World), le seul jeu du lot L2 : son contenu, sa ROM, ses etats d'epreuve.</summary>
    private const uint CrcSmb = 0x393a432f;
    private const string CoeurHote = "fceumm_ghosts";

    private readonly ApiContext _contexte;
    private readonly IEventBus _bus;
    private readonly NelfePlayAgentService? _agent;
    private readonly ILogger<MassRunService> _journal;
    private readonly object _verrou = new();
    private MassRunCanal? _canal;
    private Thread? _boucle;
    private volatile bool _arret;
    private SalleLocale? _salle;

    public MassRunService(ApiContext contexte, IEventBus bus, ILogger<MassRunService> journal, NelfePlayAgentService? agent = null)
    {
        _contexte = contexte;
        _bus = bus;
        _journal = journal;
        _agent = agent;
    }

    public sealed record SalleLocale(string Niveau, int Attente, string Pseudo, string Couleur, int Coureurs, int Pistes, string Rom)
    {
        public DateTime? GoUtc { get; set; }
        public uint Mode { get; set; }
        public uint ImageCourse { get; set; }
        public uint Arrivee { get; set; }
        public bool Finie { get; set; }
    }

    public SalleLocale? Salle => _salle;

    /// <summary>Lance une salle d'essai sur la borne. Leve <see cref="InvalidOperationException"/> si elle ne peut pas partir.</summary>
    public SalleLocale Lancer(string niveau, int attenteSecondes, int coureurs, int limiteSecondes = 300)
    {
        lock (_verrou)
        {
            if (Process.GetProcessesByName("retroarch").Length > 0)
            {
                throw new InvalidOperationException("un jeu tourne deja");
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(niveau, "^[1-8]-[1-4]$"))
            {
                throw new InvalidOperationException("niveau inconnu : " + niveau);
            }
            var rom = Path.Combine(RetroBatPaths.RetroBatRoot, "roms", "nes", "Super Mario Bros. (World).zip");
            if (!File.Exists(rom))
            {
                throw new InvalidOperationException("ROM absente : " + rom);
            }
            var cheminEtat = Path.Combine(EtatsDesDefis.Racine, "nes", "super-mario-bros", niveau + "-panneau.state");
            if (!File.Exists(cheminEtat))
            {
                throw new InvalidOperationException("etat de depart absent : " + cheminEtat);
            }
            var etat = EtatRetroArch.Brut(File.ReadAllBytes(cheminEtat));

            var statut = _agent?.Status;
            var pseudo = (statut?.Pseudo is { Length: > 0 } p ? p : "JOUEUR").ToUpperInvariant();
            var poignee = statut?.PlayerCode is { Length: > 0 } code ? code : pseudo;
            var couleur = MultiplayerPanelService.CouleurDe(poignee);

            var pistes = LirePistes(niveau);
            var simules = FormerCoureurs(pistes, coureurs);

            Arreter();
            _canal ??= new MassRunCanal();
            // Le GO! n'est fixe qu'une fois le jeu charge (le coeur passe en salle d'attente) : la salle d'attente dure alors ce
            // qui a ete demande, quel que soit le temps de chargement (12 s mesurees sur la borne le 04/11).
            _canal.PoserSalle(CrcSmb, null, limiteSecondes, Convert.ToUInt32(couleur, 16), pseudo.Length > 10 ? pseudo[..10] : pseudo, niveau, etat);

            var arguments = RetroBat.Api.Controllers.CommandsController.ArgumentsDuLanceur(
                RetroBat.Api.Netplay.EsManettes.PourLancer(_journal).Arguments, "nes", "libretro", CoeurHote, rom, null);
            if (arguments is null)
            {
                _canal.Fermer();
                throw new InvalidOperationException("arguments du lanceur refuses");
            }
            var salle = new SalleLocale(niveau, attenteSecondes, pseudo, couleur, simules.Count, pistes.Count, rom);
            _salle = salle;
            _arret = false;
            var canal = _canal;
            // La boucle d'abord : le lancement direct rend la main tard (30 s mesurees), le jeu tourne deja.
            _boucle = new Thread(() => Boucle(canal, salle, simules)) { IsBackground = true, Name = "MassRun", Priority = ThreadPriority.AboveNormal };
            _boucle.Start();
            _ = Task.Run(() =>
            {
                if (LancementDirect.Lancer(arguments, "nes", rom, _contexte, _bus, _journal) is null)
                {
                    _journal.LogWarning("MASS RUN : emulatorLauncher n'a pas demarre");
                    _arret = true;
                }
            });
            _journal.LogInformation("MASS RUN : salle {Niveau} lancee, {Attente} s d'attente, {Coureurs} coureurs simules ({Pistes} pistes), {Pseudo} #{Couleur}",
                niveau, attenteSecondes, simules.Count, pistes.Count, pseudo, couleur);
            return salle;
        }
    }

    private sealed record Simule(PisteMassRun Piste, int Ecart, uint Couleur, string Pseudo);

    private static List<PisteMassRun> LirePistes(string niveau)
    {
        var dossier = Path.Combine(RetroBatPaths.RetroBatRoot, "bios", "nelfeplay", "massrun", CrcSmb.ToString("x8"), "pistes", niveau);
        if (!Directory.Exists(dossier))
        {
            return [];
        }
        return Directory.EnumerateFiles(dossier, "*.npiste")
            .Select(f => PisteMassRun.Lire(f, CrcSmb))
            .OfType<PisteMassRun>()
            .Take(32)
            .ToList();
    }

    /// <summary>Les coureurs simules : chaque piste repetee avec un ecart, a la couleur d'avatar de son pseudo.</summary>
    private static List<Simule> FormerCoureurs(List<PisteMassRun> pistes, int voulus)
    {
        var liste = new List<Simule>();
        if (pistes.Count == 0)
        {
            return liste;
        }
        for (var i = 0; i < Math.Min(voulus, MassRunCanal.MaxCoureurs); i++)
        {
            var piste = pistes[i % pistes.Count];
            var pseudo = i < pistes.Count && piste.Pseudo.Length > 0 ? piste.Pseudo : $"BOT {i + 1:00}";
            liste.Add(new Simule(piste, (i / pistes.Count) * 19, Convert.ToUInt32(MultiplayerPanelService.CouleurDe(pseudo + i), 16),
                pseudo.Length > 10 ? pseudo[..10] : pseudo));
        }
        return liste;
    }

    /// <summary>
    /// A chaque image publiee par le coeur : les coureurs de la fenetre qui commence a cette image. Pendant la salle d'attente,
    /// chaque coureur boucle sur sa piste ; en course, il est a son image de course.
    /// </summary>
    private void Boucle(MassRunCanal canal, SalleLocale salle, List<Simule> simules)
    {
        uint dernierMode = 0, derniereImage = uint.MaxValue, derniereImageMode = uint.MaxValue;
        var arriveeNotee = false;
        var debut = DateTime.UtcNow;
        var vu = false;
        var controle = DateTime.MinValue;
        try
        {
            while (!_arret)
            {
                // La fin : le jeu a tourne puis RetroArch a disparu, ou il n'a jamais demarre en 3 minutes.
                if (DateTime.UtcNow - controle > TimeSpan.FromSeconds(1))
                {
                    controle = DateTime.UtcNow;
                    var tourne = Process.GetProcessesByName("retroarch").Length > 0;
                    if (tourne) vu = true;
                    else if (vu || DateTime.UtcNow - debut > TimeSpan.FromMinutes(3)) break;
                }
                var joueur = canal.LireJoueur();
                if (joueur is { } j)
                {
                    salle.Mode = j.Mode;
                    salle.ImageCourse = j.ImageCourse;
                    if (j.Mode != dernierMode)
                    {
                        _journal.LogInformation("MASS RUN : mode {Mode}", j.Mode);
                        dernierMode = j.Mode;
                        if (j.Mode == 2 && salle.GoUtc is null)
                        {
                            salle.GoUtc = DateTime.UtcNow.AddSeconds(salle.Attente + 5);
                            canal.PoserGo(salle.GoUtc.Value);
                            _journal.LogInformation("MASS RUN : jeu charge, GO! a {Go:HH:mm:ss}", salle.GoUtc.Value.ToLocalTime());
                        }
                    }
                    var attente = j.Mode == 2;
                    if (attente ? j.ImageMode != derniereImageMode : j.ImageCourse != derniereImage)
                    {
                        derniereImage = j.ImageCourse;
                        derniereImageMode = j.ImageMode;
                        var base_ = attente ? j.ImageMode : j.ImageCourse;
                        var coureurs = new List<MassRunCanal.Coureur>(simules.Count);
                        for (var i = 0; i < simules.Count; i++)
                        {
                            var s = simules[i];
                            var fenetre = new byte[]?[MassRunCanal.ImagesParFenetre];
                            for (var k = 0; k < fenetre.Length; k++)
                            {
                                fenetre[k] = attente
                                    ? s.Piste.Images[(int)((base_ + k + (uint)s.Ecart * 7u + (uint)i * 53u) % (uint)s.Piste.Images.Length)]
                                    : s.Piste.A((long)base_ + k - s.Ecart);
                            }
                            coureurs.Add(new MassRunCanal.Coureur(s.Couleur, s.Pseudo, false, fenetre));
                        }
                        canal.EcrireCoureurs(base_, coureurs);
                    }
                    if (j.Arrivee > 0 && !arriveeNotee)
                    {
                        arriveeNotee = true;
                        salle.Arrivee = j.Arrivee;
                        NoterLArrivee(salle);
                    }
                }
                Thread.Sleep(1);
            }
        }
        catch (Exception e)
        {
            _journal.LogWarning(e, "MASS RUN : la boucle de la salle s'arrete");
        }
        finally
        {
            salle.Finie = true;
            canal.Fermer();
            _journal.LogInformation("MASS RUN : salle {Niveau} terminee", salle.Niveau);
        }
    }

    private void NoterLArrivee(SalleLocale salle)
    {
        var secondes = salle.Arrivee / 60.0988;
        _journal.LogInformation("MASS RUN : arrivee en {Images} images ({Secondes:0.00} s)", salle.Arrivee, secondes);
        try
        {
            var dossier = Path.Combine(RetroBatPaths.PluginRoot, "state", "massrun");
            Directory.CreateDirectory(dossier);
            var ligne = JsonSerializer.Serialize(new
            {
                at = DateTime.UtcNow,
                niveau = salle.Niveau,
                pseudo = salle.Pseudo,
                images = salle.Arrivee,
                secondes = Math.Round(secondes, 2),
                coureurs = salle.Coureurs,
            });
            File.AppendAllText(Path.Combine(dossier, "arrivees.jsonl"), ligne + Environment.NewLine);
        }
        catch (IOException e)
        {
            _journal.LogWarning(e, "MASS RUN : arrivee non notee");
        }
    }

    private void Arreter()
    {
        _arret = true;
        _boucle?.Join(500);
        _boucle = null;
    }

    public void Dispose()
    {
        Arreter();
        _canal?.Fermer();
        _canal?.Dispose();
    }
}
