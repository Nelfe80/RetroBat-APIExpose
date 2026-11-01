using System.Drawing;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// LA PAGE DE PREPARATION D'UNE EPREUVE (CDC du panneau, §8.1) : l'ecran de lancement VS, avec la fiche de l'epreuve a la place
/// des joueurs. Le fond, le mot central, les etapes et les boutons sont ceux du VS.
/// </summary>
public sealed partial class LeaderboardOverlayService
{
    private sealed partial class Panneau
    {
        private void DessinerLaFicheDeLEpreuve(Graphics g, string epreuve, IReadOnlyList<ChampDeFiche> fiche, FontFamily famille, Font secours, double t)
        {
            var (dx, opacite) = Arrivee(t, true, 0);
            if (opacite <= 0.01) return;

            const int colonnes = 2;
            const float marge = 44;
            var rangees = (fiche.Count + colonnes - 1) / colonnes;
            var carte = new RectangleF(900 + dx, 150, 956, 200 + rangees * 130);
            DessinerLeFondDUneCarteDeLancement(g, carte, null, opacite, 20);

            var titre = epreuve.ToUpperInvariant();
            var taille = TraitsDesEcrans.TailleQuiTient(titre, famille, 72, carte.Width - 2 * marge);
            var boite = TraitsDesEcrans.BoiteDuTexte(titre, famille, taille);
            TraitsDesEcrans.TexteEnRelief(g, titre, famille, taille, new PointF(carte.X + marge - boite.X, carte.Y + 44 - boite.Y), Color.White, Nuit, 0,
                [(4, 4, _accent)], opacite);

            using var etiquette = TraitsDesEcrans.Libelles(22, secours);
            using var valeur = TraitsDesEcrans.LibellesFins(34, secours);
            using var encre = new SolidBrush(TraitsDesEcrans.Alpha(Color.White, opacite));
            var largeur = (carte.Width - 2 * marge) / colonnes;
            for (var i = 0; i < fiche.Count; i++)
            {
                var x = carte.X + marge + i % colonnes * largeur;
                var y = carte.Y + 186 + i / colonnes * 130;
                TraitsDesEcrans.TexteEspace(g, fiche[i].Etiquette.ToUpperInvariant(), etiquette, TraitsDesEcrans.Alpha(_accent, opacite), x, y, 4);
                g.DrawString(fiche[i].Valeur, valeur, encre, new RectangleF(x, y + 38, largeur - 24, 84), TraitsDesEcrans.Typo);
            }
        }

        /// <summary>Sous la ligne du joueur : le record et les voisins de sa place, sa ligne dans la couleur du titre.</summary>
        private void DessinerLeClassementDuBilan(Graphics g, IReadOnlyList<LigneDeDefi> lignes, Font secours, double t)
        {
            var (dy, opacite) = Apparition(t, 0.25);
            if (opacite <= 0.01) return;

            const float gauche = 840;
            const float largeur = 1020;
            const float hauteur = 72;
            using var police = TraitsDesEcrans.Chiffres(36, secours, italique: true);
            var y = 410 + dy;
            foreach (var ligne in lignes)
            {
                var moi = ligne.Marque == "moi";
                var zone = new RectangleF(gauche, y, largeur, hauteur - 10);
                using (var forme = FormesDesEcrans.Arrondi(zone, 12))
                using (var fond = new SolidBrush(TraitsDesEcrans.Alpha(moi ? _accent : Nuit, opacite * (moi ? 0.9 : 0.6))))
                {
                    g.FillPath(fond, forme);
                }

                using var encre = new SolidBrush(TraitsDesEcrans.Alpha(moi ? Nuit : Color.White, opacite));
                var hautDuTexte = zone.Y + (zone.Height - police.GetHeight(g)) / 2f;
                g.DrawString(ligne.Nom.ToUpperInvariant(), police, encre, zone.X + 28, hautDuTexte, TraitsDesEcrans.Typo);
                var temps = TraitsDesEcrans.Mesure(g, ligne.Colonne2, police).Width;
                g.DrawString(ligne.Colonne2, police, encre, zone.Right - 28 - temps, hautDuTexte, TraitsDesEcrans.Typo);
                y += hauteur;
            }
        }
    }
}
