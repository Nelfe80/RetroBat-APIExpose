using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace RetroBat.Api.Replay.Overlay;

/// <summary>
/// Planche d'icônes de réactions (reacts-icons.png, embarquée dans l'exe) : 9 lignes (familles) × 6 colonnes.
/// Colonnes 0-2 = design gauche, 3-5 = design « lapin » (2 mascottes × 3 niveaux). Pour chaque
/// cellule on RETIRE le fond sombre (flood-fill depuis les bords), on ISOLE l'icône principale
/// (composante connexe depuis le centre → pas de débord voisin) et on RECADRE pile dessus
/// (centrage automatique, aspect propre). Si la planche manque, <see cref="Ok"/> = false.
/// </summary>
public sealed class ReplayReactionSprites : IDisposable
{
    public const int Cols = 6;
    private static readonly string[] Rows =
        { "hype", "wow", "respect", "laugh", "tension", "ouch", "love", "rage", "celebrate" };

    private readonly Bitmap?[,] _cells = new Bitmap?[9, Cols];
    private readonly ImageAttributes _ia = new();   // réutilisé (pas d'alloc par sprite/frame)
    private readonly ColorMatrix _cm = new();
    private readonly object _drawLock = new();       // la planche est partagée entre 2 threads UI (barre + HUD)
    public bool Ok { get; }

    /// <summary>Le nom de la planche dans l'exe (voir RetroBat.Api.csproj).</summary>
    internal const string Ressource = "RetroBat.Api.reacts-icons.png";

    public ReplayReactionSprites(ILogger logger)
    {
        try
        {
            // Lue dans media/ jusqu'a la 1.9.43, la planche n'existait que sur la borne qui fabrique
            // la release : ce dossier ne part ni dans la mise a jour ni dans l'installeur.
            using var flux = typeof(ReplayReactionSprites).Assembly.GetManifestResourceStream(Ressource);
            if (flux is null) { logger.LogWarning("Replay HUD : planche d'icônes absente de l'exe ({Ressource}), fallback glyphes.", Ressource); return; }
            using var sheet = new Bitmap(flux);
            int Bound(int i, int n, int total) => (int)Math.Round(i * (double)total / n); // bornes arrondies (pas de dérive)
            const int ox = 20, oy = 30; // chevauchement pour capter le débord de l'icône hors de sa cellule
            for (var r = 0; r < 9; r++)
            {
                int y0 = Bound(r, 9, sheet.Height), y1 = Bound(r + 1, 9, sheet.Height);
                for (var c = 0; c < Cols; c++)
                {
                    int x0 = Bound(c, Cols, sheet.Width), x1 = Bound(c + 1, Cols, sheet.Width);
                    int rx0 = Math.Max(0, x0 - ox), ry0 = Math.Max(0, y0 - oy);
                    int rx1 = Math.Min(sheet.Width, x1 + ox), ry1 = Math.Min(sheet.Height, y1 + oy);
                    _cells[r, c] = ExtractIcon(sheet, rx0, ry0, rx1 - rx0, ry1 - ry0, x0 - rx0, y0 - ry0, x1 - x0, y1 - y0);
                }
            }
            Ok = true;
            logger.LogInformation("Replay HUD : planche d'icônes chargée ({W}x{H}, {Cols}x9, recadrage auto).", sheet.Width, sheet.Height, Cols);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Replay HUD : chargement planche d'icônes échoué (fallback glyphes)."); }
    }

    public static int RowIndex(string family) => Array.IndexOf(Rows, family);

    /// <summary>Dessine la cellule (famille, colonne) centrée, hauteur = size, largeur au ratio.</summary>
    public void Draw(Graphics g, string family, int col, float cx, float cy, float size, float alpha)
    {
        var r = RowIndex(family);
        if (r < 0) return;
        col = Math.Clamp(col, 0, Cols - 1);
        var bmp = _cells[r, col];
        if (bmp is null || alpha <= 0.02f) return;

        var aspect = bmp.Width / (float)bmp.Height;
        float h = size, w = size * aspect;
        var dest = new Rectangle((int)(cx - w / 2f), (int)(cy - h / 2f), (int)w, (int)h);
        // Sérialisé : la planche (bitmaps + ImageAttributes) est partagée entre les 2 threads UI.
        lock (_drawLock)
        {
            if (alpha >= 0.99f) { g.DrawImage(bmp, dest, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel); return; }
            _cm.Matrix33 = Math.Clamp(alpha, 0f, 1f);
            _ia.SetColorMatrix(_cm);
            g.DrawImage(bmp, dest, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, _ia);
        }
    }

    // Détourage par SATURATION + LUMINOSITÉ (le fond est sombre ET désaturé ; les icônes sont
    // saturées OU claires — donc rouges/violets/blancs préservés). Puis on isole la composante
    // connexe contenant le CENTRE (pas de débord voisin ni de fragment détaché) et on recadre.
    private static Bitmap ExtractIcon(Bitmap sheet, int sx, int sy, int w, int h, int cx0, int cy0, int cw0, int ch0)
    {
        var src = new int[w * h];
        var sr = sheet.LockBits(new Rectangle(sx, sy, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { var ln = new int[w]; for (var y = 0; y < h; y++) { Marshal.Copy(sr.Scan0 + y * sr.Stride, ln, 0, w); Array.Copy(ln, 0, src, y * w, w); } }
        finally { sheet.UnlockBits(sr); }

        // alpha par pixel : rampe sur max(saturation, luminosité) -> fond sombre/gris = 0, icône = 255.
        var a = new int[w * h];
        for (var i = 0; i < w * h; i++)
        {
            var p = src[i];
            int r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
            var sat = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            var lu = (r * 77 + g * 150 + b * 29) >> 8;
            var av = Math.Max((sat - 28) * 255 / 40, (lu - 92) * 255 / 60);
            a[i] = Math.Clamp(av, 0, 255) * ((p >> 24) & 255) / 255;
        }

        // PLUS GRANDE composante connexe (a>50) = le corps de l'icône (pas les sparkles détachés,
        // même si l'icône est décentrée dans sa cellule).
        var inC = new bool[w * h];
        var label = new int[w * h];
        var stack = new Stack<int>();
        int cur = 0, bestLabel = 0, bestCentral = 0;
        for (var s = 0; s < w * h; s++)
        {
            if (a[s] <= 50 || label[s] != 0) continue;
            cur++; var centralCount = 0;
            label[s] = cur; stack.Push(s);
            while (stack.Count > 0)
            {
                var i = stack.Pop(); int x = i % w, y = i / w;
                if (x >= cx0 && x < cx0 + cw0 && y >= cy0 && y < cy0 + ch0) centralCount++;
                void Nb(int nx, int ny) { if (nx < 0 || ny < 0 || nx >= w || ny >= h) return; var j = ny * w + nx; if (a[j] > 50 && label[j] == 0) { label[j] = cur; stack.Push(j); } }
                Nb(x - 1, y); Nb(x + 1, y); Nb(x, y - 1); Nb(x, y + 1);
            }
            // L'icône de CETTE case = la composante la plus présente DANS la cellule centrale
            // (un voisin qui déborde n'a que peu de pixels au centre).
            if (centralCount > bestCentral) { bestCentral = centralCount; bestLabel = cur; }
        }
        if (bestLabel != 0) for (var i = 0; i < w * h; i++) inC[i] = label[i] == bestLabel;

        // boîte englobante + sortie (alpha doux pour la composante + 1 px de plume).
        int minx = w, miny = h, maxx = -1, maxy = -1;
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) if (inC[y * w + x]) { if (x < minx) minx = x; if (x > maxx) maxx = x; if (y < miny) miny = y; if (y > maxy) maxy = y; }
        if (maxx < 0) return new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        minx = Math.Max(0, minx - 2); miny = Math.Max(0, miny - 2);
        maxx = Math.Min(w - 1, maxx + 2); maxy = Math.Min(h - 1, maxy + 2);
        int cw = maxx - minx + 1, ch = maxy - miny + 1;

        bool NearC(int x, int y)
        {
            for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++) { int nx = x + dx, ny = y + dy; if (nx >= 0 && ny >= 0 && nx < w && ny < h && inC[ny * w + nx]) return true; }
            return false;
        }

        var dst = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
        var dr = dst.LockBits(new Rectangle(0, 0, cw, ch), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var ln = new int[cw];
            for (var y = 0; y < ch; y++)
            {
                for (var x = 0; x < cw; x++)
                {
                    int gx = minx + x, gy = miny + y, i = gy * w + gx;
                    var keep = inC[i] || (a[i] > 12 && NearC(gx, gy)); // plume 1 px
                    ln[x] = keep ? (a[i] << 24) | (src[i] & 0xFFFFFF) : 0;
                }
                Marshal.Copy(ln, 0, dr.Scan0 + y * dr.Stride, cw);
            }
        }
        finally { dst.UnlockBits(dr); }
        return dst;
    }

    public void Dispose()
    {
        foreach (var b in _cells) b?.Dispose();
        _ia.Dispose();
    }
}
