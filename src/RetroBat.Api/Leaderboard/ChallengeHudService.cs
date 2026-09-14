using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// Le cartouche du DEFI, en haut a droite de l'ecran de jeu : le score a battre, le joueur qui
/// le detient, et en petit au-dessus la tete du classement.
///
/// UNE REGLE QUI NE SE NEGOCIE PAS (decision du 2026-09-09, confirmee le 2026-09-14) : pendant un
/// run certifie, rien d'ANIME devant le jeu. Le recompositing continu par-dessus RetroArch plein
/// ecran faisait saccader l'image et hacher le son sur la borne de reference - c'est mesure. Ce
/// cartouche est donc peint une fois, puis SEULEMENT quand la cible change : pas de minuteur de
/// dessin, pas de fondu, pas de clignotement. Depasser un joueur ne declenche aucune animation ;
/// la cible passe au joueur suivant et une fleche verte, immobile, dit qu'on a gagne un rang.
///
/// La cible initiale est le joueur juste AU-DESSUS de notre meilleur score ; sans score a nous,
/// c'est le dernier du top, la place a prendre pour y entrer. Nos propres lignes ne sont jamais
/// une cible : on ne se bat pas contre soi-meme.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ChallengeHudService : IHostedService, IDisposable
{
    /// <summary>La taille du top a integrer quand on n'y figure pas encore.</summary>
    public const int TailleDuTop = 10;

    private readonly IEventBus _bus;
    private readonly ILogger<ChallengeHudService> _journal;
    private readonly object _verrou = new();

    private IDisposable? _abonnement;
    private Thread? _fil;
    private Cartouche? _forme;

    private IReadOnlyList<LeaderboardClient.Ligne> _classement = Array.Empty<LeaderboardClient.Ligne>();
    private LeaderboardClient.Ligne? _tete;
    private LeaderboardClient.Ligne? _cible;
    private bool _rangGagne;
    private bool _arme;
    private bool _affiche;
    private long _score;
    private EsMenuStyle _style = new();

    public ChallengeHudService(IEventBus bus, ILogger<ChallengeHudService> journal)
    {
        _bus = bus;
        _journal = journal;
    }

    /// <summary>Ce que le cartouche montre, calcule d'un bloc.</summary>
    public sealed record Etat(LeaderboardClient.Ligne? Tete, LeaderboardClient.Ligne? Cible, bool RangGagne, long Score);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        Cacher();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Arme le defi avant le lancement : le classement du monde, deja charge par le panneau.
    /// Le cartouche n'apparaitra qu'au demarrage du jeu.
    /// </summary>
    public void Armer(IReadOnlyList<LeaderboardClient.Ligne> classement, EsMenuStyle style)
    {
        lock (_verrou)
        {
            _classement = classement.OrderBy(l => l.Rang).ToList();
            _style = style;
            _tete = _classement.FirstOrDefault();
            _cible = CibleInitiale(_classement);
            _rangGagne = false;
            _score = 0;
            _arme = true;
        }
        _journal.LogInformation("Defi : arme, cible {Cible}.", _cible is null ? "(deja en tete)" : $"#{_cible.Rang} {_cible.Joueur} {_cible.Valeur}");
    }

    /// <summary>
    /// La premiere cible : le joueur juste au-dessus de notre meilleur score, sinon le dernier du
    /// top (la place a prendre pour y entrer). Null si nous sommes deja en tete.
    /// </summary>
    public static LeaderboardClient.Ligne? CibleInitiale(IReadOnlyList<LeaderboardClient.Ligne> classement)
    {
        var tries = classement.OrderBy(l => l.Rang).ToList();
        var moi = tries.FirstOrDefault(l => l.CestMoi);
        if (moi is not null)
        {
            return tries.LastOrDefault(l => l.Rang < moi.Rang && !l.CestMoi);
        }
        var autres = tries.Where(l => !l.CestMoi).ToList();
        if (autres.Count == 0) return null;
        return autres.Count >= TailleDuTop ? autres[TailleDuTop - 1] : autres[^1];
    }

    /// <summary>
    /// Le score courant a franchi la cible : la cible devient le joueur suivant au-dessus. Rend
    /// vrai si au moins un rang a ete gagne. Plusieurs joueurs peuvent tomber d'un coup.
    /// </summary>
    public static bool Avancer(IReadOnlyList<LeaderboardClient.Ligne> classement, long score, ref LeaderboardClient.Ligne? cible)
    {
        // Un contre-la-montre ne se depasse pas EN COURS de partie : le chronometre du joueur
        // monte, et etre sous le temps d'un autre a la 30e seconde ne veut rien dire - seul le
        // temps FINAL compte. Comparer en direct ferait « depasser » tout le classement des le
        // depart, a zero. La cible reste donc fixe ; le verdict dira le rang.
        if (classement.Count > 0 && classement[0].PlusBasEstMieux) return false;
        var gagne = false;
        while (cible is not null && score > cible.Valeur)
        {
            var rang = cible.Rang;
            cible = classement.OrderBy(l => l.Rang).LastOrDefault(l => l.Rang < rang && !l.CestMoi);
            gagne = true;
        }
        return gagne;
    }

    private void SurEvenement(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "ui.game.started":
                    bool montrer;
                    lock (_verrou) montrer = _arme;
                    if (montrer) _ = MontrerQuandLeJeuEstLaAsync();
                    break;

                case "score.live.changed":
                    var score = LireScore(e.Payload);
                    if (score is null) return;
                    bool redessiner;
                    lock (_verrou)
                    {
                        if (!_affiche) return;
                        _score = score.Value;
                        var cible = _cible;
                        redessiner = Avancer(_classement, _score, ref cible);
                        if (redessiner)
                        {
                            _journal.LogInformation("Defi : rang gagne a {Score}, nouvelle cible {Cible}.", _score, cible is null ? "(en tete)" : $"#{cible.Rang} {cible.Joueur}");
                            _cible = cible;
                            _rangGagne = true;
                        }
                    }
                    // On ne repeint QUE si la cible a change : pas a chaque point marque.
                    if (redessiner) _forme?.Rafraichir();
                    break;

                case "ui.game.ended":
                    lock (_verrou) _arme = false;
                    Cacher();
                    break;
            }
        }
        catch (Exception ex)
        {
            _journal.LogDebug(ex, "Defi : evenement {Type} ignore.", e.Type);
        }
    }

    /// <summary>Le score d'un evenement. Meme prudence que partout : un non-nombre ne leve pas.</summary>
    private static long? LireScore(object? payload)
    {
        if (payload is null) return null;
        var el = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
        if (el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty("Score", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : null;
    }

    private async Task MontrerQuandLeJeuEstLaAsync()
    {
        // La fenetre de l'emulateur suit son processus de quelques instants : on attend qu'elle
        // existe pour se poser sur SON ecran (la borne affiche le jeu sur un ecran different).
        var limite = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        IntPtr fenetre = IntPtr.Zero;
        while (DateTime.UtcNow < limite && (fenetre = FenetreDuJeu()) == IntPtr.Zero)
        {
            await Task.Delay(400).ConfigureAwait(false);
        }
        await Task.Delay(1500).ConfigureAwait(false);
        lock (_verrou)
        {
            if (!_arme) return;
            _affiche = true;
        }
        var ecran = fenetre != IntPtr.Zero ? Screen.FromHandle(fenetre).Bounds : (Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080));
        if (_fil is null)
        {
            _fil = new Thread(() =>
            {
                try
                {
                    _forme = new Cartouche(this, ecran);
                    Application.Run(_forme);
                }
                catch (Exception ex) { _journal.LogWarning(ex, "Defi : le cartouche s'est arrete."); }
            }) { IsBackground = true, Name = "challenge-hud" };
            _fil.SetApartmentState(ApartmentState.STA);
            _fil.Start();
        }
        else
        {
            _forme?.Placer(ecran);
            _forme?.Paraitre();
        }
        _journal.LogInformation("Defi : cartouche affiche.");
    }

    private void Cacher()
    {
        lock (_verrou) _affiche = false;
        _forme?.Disparaitre();
    }

    private static IntPtr FenetreDuJeu()
    {
        foreach (var nom in new[] { "retroarch", "mame", "fbneo" })
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.ProcessName.StartsWith(nom, StringComparison.OrdinalIgnoreCase) && p.MainWindowHandle != IntPtr.Zero)
                    {
                        return p.MainWindowHandle;
                    }
                }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>La cible du defi arme, pour annoncer l'objectif avant le lancement.</summary>
    public Etat Objectif() => Lire();

    internal Etat Lire()
    {
        lock (_verrou) return new Etat(_tete, _cible, _rangGagne, _score);
    }

    internal EsMenuStyle Style()
    {
        lock (_verrou) return _style;
    }

    public void Dispose()
    {
        _abonnement?.Dispose();
        try { _forme?.Disparaitre(); } catch { }
    }

    // ── La fenetre ───────────────────────────────────────────────────────────

    private sealed class Cartouche : Form
    {
        private readonly ChallengeHudService _service;
        private readonly PrivateFontCollection _polices = new();
        private readonly Dictionary<string, FontFamily> _familles = new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Windows.Forms.Timer _gardien;
        private Image? _sceau;
        private int _hauteurEcran = 1080;

        public Cartouche(ChallengeHudService service, Rectangle ecran)
        {
            _service = service;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = Couleur(service.Style().BackgroundColor);
            Placer(ecran);
            ChargerLesPolices(service.Style());
            try
            {
                var sceau = service.Style().Icon("nelfe-verified");
                if (sceau.Length > 0)
                {
                    var glyphes = new EsGlyphCache(service.Style());
                    glyphes.Preparer("nelfe-verified", (int) (_hauteurEcran * 0.05), null);
                    _sceau = glyphes.Glyphe("nelfe-verified", (int) (_hauteurEcran * 0.05));
                }
            }
            catch (Exception) { _sceau = null; }

            // Le gardien ne DESSINE rien : il reaffirme seulement la place au-dessus du jeu,
            // sans deplacer ni retailler la fenetre. Aucun recompositing du contenu.
            _gardien = new System.Windows.Forms.Timer { Interval = 2000 };
            _gardien.Tick += (_, _) => SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
            _gardien.Start();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080, WsExTopmost = 0x00000008;
                var p = base.CreateParams;
                p.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTopmost;
                return p;
            }
        }

        public void Placer(Rectangle ecran)
        {
            if (IsHandleCreated && InvokeRequired) { BeginInvoke(() => Placer(ecran)); return; }
            _hauteurEcran = ecran.Height;
            // Discret : il accompagne la partie, il ne la couvre pas.
            var largeur = (int) (ecran.Width * 0.165);
            var hauteur = (int) (ecran.Height * 0.072);
            var marge = (int) (ecran.Height * 0.018);
            Bounds = new Rectangle(ecran.Right - largeur - marge, ecran.Top + marge, largeur, hauteur);
            // Coins arrondis par une REGION : decoupe statique, aucune transparence a composer.
            using var chemin = Arrondi(new RectangleF(0, 0, largeur, hauteur), hauteur * 0.14f);
            Region = new Region(chemin);
        }

        public void Rafraichir()
        {
            if (IsHandleCreated) BeginInvoke(Invalidate);
        }

        public void Paraitre()
        {
            if (IsHandleCreated) BeginInvoke(() => { if (!Visible) Show(); Invalidate(); });
        }

        public void Disparaitre()
        {
            if (IsHandleCreated) BeginInvoke(() => { if (Visible) Hide(); });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var s = _service.Style();
            var etat = _service.Lire();
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            // Le fond : le gris du menu d'ES, et un liseré de la couleur de selection.
            using (var fond = new SolidBrush(Couleur(s.BackgroundColor)))
            {
                g.FillRectangle(fond, 0, 0, Width, Height);
            }
            using (var lisere = new Pen(Color.FromArgb(255, 60, 60, 60), 1f))
            using (var chemin = Arrondi(new RectangleF(1, 1, Width - 3, Height - 3), Height * 0.14f))
            {
                g.DrawPath(lisere, chemin);
            }

            var marge = Height * 0.12f;
            var petite = Math.Max(9f, _hauteurEcran * 0.012f);
            var grande = Math.Max(12f, _hauteurEcran * 0.022f);
            using var policePetite = Police(s.TextFontPath, petite, FontStyle.Regular);
            using var policeNom = Police(s.TitleFontPath, grande * 0.72f, FontStyle.Bold);
            using var policeScore = Police(s.GroupFontPath.Length > 0 ? s.GroupFontPath : s.TitleFontPath, grande, FontStyle.Bold);
            using var gauche = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using var droite = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };

            // En petit, la tete du classement.
            var hautLigne = Height * 0.28f;
            if (etat.Tete is { } tete)
            {
                using var gris = new SolidBrush(Couleur(s.TextColor));
                g.DrawString($"#1 {tete.Joueur.ToUpperInvariant()}", policePetite, gris, new RectangleF(marge, marge * 0.6f, Width * 0.62f, hautLigne), gauche);
                g.DrawString(tete.Valeur.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), policePetite, gris, new RectangleF(0, marge * 0.6f, Width - marge, hautLigne), droite);
            }

            // La cible : sceau certifie, rang, joueur, et le score a battre.
            var yCible = marge * 0.6f + hautLigne;
            var hCible = Height - yCible - marge * 0.6f;
            var x = marge;
            if (_sceau is not null)
            {
                var cote = Math.Min(hCible * 0.8f, _sceau.Height);
                g.DrawImage(_sceau, x, yCible + (hCible - cote) / 2f, cote, cote);
                x += cote + marge * 0.6f;
            }

            if (etat.RangGagne)
            {
                // La fleche verte, IMMOBILE : on a gagne au moins un rang.
                var h = hCible * 0.42f;
                var y0 = yCible + (hCible - h) / 2f;
                using var vert = new SolidBrush(Color.FromArgb(255, 60, 203, 90));
                g.FillPolygon(vert, new[] { new PointF(x + h / 2f, y0), new PointF(x + h, y0 + h), new PointF(x, y0 + h) });
                x += h + marge * 0.5f;
            }

            // Le nom en gris du menu, le score seul en blanc : l'oeil ne va qu'a ce qui compte.
            using var blanc = new SolidBrush(Couleur(s.TitleColor));
            using var grisNom = new SolidBrush(Couleur(s.TextColor));
            if (etat.Cible is { } cible)
            {
                var valeur = cible.Valeur.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
                var largeurScore = g.MeasureString(valeur, policeScore, PointF.Empty, StringFormat.GenericTypographic).Width;
                g.DrawString($"#{cible.Rang} {cible.Joueur.ToUpperInvariant()}", policeNom, grisNom,
                    new RectangleF(x, yCible, Math.Max(10, Width - x - largeurScore - marge * 1.5f), hCible), gauche);
                g.DrawString(valeur, policeScore, blanc, new RectangleF(0, yCible, Width - marge, hCible), droite);
            }
            else
            {
                // Plus personne au-dessus : le joueur est en tete.
                g.DrawString("#1", policeScore, blanc, new RectangleF(x, yCible, Width - x - marge, hCible), gauche);
            }
        }

        private void ChargerLesPolices(EsMenuStyle style)
        {
            foreach (var chemin in new[] { style.TitleFontPath, style.TextFontPath, style.GroupFontPath })
            {
                if (chemin.Length == 0 || _familles.ContainsKey(chemin)) continue;
                try
                {
                    var avant = _polices.Families.Length;
                    _polices.AddFontFile(chemin);
                    var famille = _polices.Families.Skip(avant).FirstOrDefault() ?? _polices.Families.LastOrDefault();
                    if (famille is not null) _familles[chemin] = famille;
                }
                catch (Exception) { }
            }
        }

        private Font Police(string chemin, float taille, FontStyle variante)
        {
            if (chemin.Length > 0 && _familles.TryGetValue(chemin, out var famille))
            {
                foreach (var essai in new[] { variante, FontStyle.Regular, FontStyle.Bold })
                {
                    if (famille.IsStyleAvailable(essai)) return new Font(famille, taille, essai, GraphicsUnit.Pixel);
                }
            }
            return new Font("Segoe UI", taille, variante, GraphicsUnit.Pixel);
        }

        private static Color Couleur(string hex)
        {
            var (r, v, b, _) = EsMenuStyle.Couleur(hex);
            return Color.FromArgb(255, r, v, b);
        }

        private static GraphicsPath Arrondi(RectangleF r, float rayon)
        {
            var d = rayon * 2f;
            var chemin = new GraphicsPath();
            chemin.AddArc(r.X, r.Y, d, d, 180, 90);
            chemin.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            chemin.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            chemin.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            chemin.CloseFigure();
            return chemin;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _gardien.Dispose();
                _polices.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr apres, int x, int y, int cx, int cy, uint flags);
        private static readonly IntPtr HwndTopmost = new(-1);
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoSize = 0x0001;
    }
}
