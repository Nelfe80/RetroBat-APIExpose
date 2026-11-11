using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Storage;
using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'ESSAI REEL DU RENDU D'UN FILM (09/11), hors suite : il ne tourne que si <c>NELFE_ESSAI_RENDU</c> est pose, sur une borne
/// libre (aucun RetroArch). Le rendu du worktree, sur le vrai magasin des replays et le vrai RetroArch, sans envoi.
/// <c>NELFE_ESSAI_RENDU</c> = « replay|debut|fin|fin_du_run:score,... » ; <c>NELFE_ESSAI_RACINE</c> = le dossier d'APIExpose ;
/// <c>NELFE_ESSAI_SORTIE</c> = le dossier du compte rendu (JSON).
/// </summary>
public sealed class EssaiReelDuRenduTests
{
    [Fact]
    public async Task Rendre_un_replay_comme_la_borne_du_joueur()
    {
        var essai = Environment.GetEnvironmentVariable("NELFE_ESSAI_RENDU");
        if (string.IsNullOrWhiteSpace(essai)) return;
        Directory.SetCurrentDirectory(Environment.GetEnvironmentVariable("NELFE_ESSAI_RACINE") ?? @"E:\RetroBat\plugins\APIExpose");
        var champs = essai.Split('|');
        var replayId = champs[0];
        long? debut = champs.Length > 1 && long.TryParse(champs[1], out var d) ? d : null;
        long? fin = champs.Length > 2 && long.TryParse(champs[2], out var f) ? f : null;
        var controles = champs.Length > 3 && champs[3].Length > 0
            ? champs[3].Split(',').Select(p => p.Split(':')).Select(p => new PointDuScore(long.Parse(p[0]), long.Parse(p[1]))).ToList()
            : null;

        // L'epreuve (11/11) : NELFE_ESSAI_EPREUVE = « defi|version|images[|fin_du_film] », reglements de la copie locale de l'index.
        IReadOnlyList<EpreuveCertifiee>? epreuves = null;
        var catalogue = new RetroBat.Api.Speedrun.CatalogueDesDefis();
        if (Environment.GetEnvironmentVariable("NELFE_ESSAI_EPREUVE") is { Length: > 0 } ep)
        {
            var e = ep.Split('|');
            epreuves = [new EpreuveCertifiee(e[0], int.Parse(e[1]), long.Parse(e[2]), e.Length > 3 && long.TryParse(e[3], out var ff) ? ff : null)];
            new RetroBat.Api.Speedrun.IndexDesDefisService(null!, catalogue).ChargerDuDisque();
        }

        using var journaux = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
        var magasin = new ReplayStore(NullLogger<ReplayStore>.Instance);
        var resolveur = new ReplayRuntimeResolver(new EsSystemsRomPaths(NullLogger<EsSystemsRomPaths>.Instance), magasin, magasin,
            journaux.CreateLogger<ReplayRuntimeResolver>());
        // NELFE_RENDU_CFG = « cle=valeur;cle=valeur » : reglages ajoutes au profil de rendu, pour mesurer une piste a la fois.
        ReplayVideoRenderer.ReglagesDEssai = Environment.GetEnvironmentVariable("NELFE_RENDU_CFG") is { Length: > 0 } cfg
            ? string.Concat(cfg.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(kv => kv.Split('=', 2))
                .Select(kv => kv[0].Trim() + " = \"" + kv[1].Trim() + "\"\n"))
            : null;
        var rendu = new ReplayVideoRenderer(magasin, magasin, magasin, resolveur, () => "idle", journaux.CreateLogger<ReplayVideoRenderer>(), catalogue);
        var r = await rendu.RendreAsync(replayId, debut, fin, CancellationToken.None, controles, epreuves);

        var sortie = Environment.GetEnvironmentVariable("NELFE_ESSAI_SORTIE") ?? Path.GetTempPath();
        Directory.CreateDirectory(sortie);
        long? taille = r.Fichier is not null && File.Exists(r.Fichier) ? new FileInfo(r.Fichier).Length : null;
        string? copie = null;
        if (r.Fichier is not null && File.Exists(r.Fichier))
        {
            copie = Path.Combine(sortie, replayId + ".mkv");
            File.Copy(r.Fichier, copie, overwrite: true);
        }
        if (r.TraceDEpreuve is { } trace) File.WriteAllText(Path.Combine(sortie, replayId + ".trace"), trace);
        File.WriteAllText(Path.Combine(sortie, replayId + ".json"), JsonSerializer.Serialize(new
        {
            replay = replayId, ok = r.Ok, raison = r.Raison, debut = r.Debut, fin = r.Fin, frame = r.FrameAtteinte,
            secondes = Math.Round(r.Secondes, 1), fidelite = r.Fidelite, detail = r.DetailFidelite, film = copie, taille,
            releves = r.Releves?.Select(x => new[] { x.Frame, x.Score }).ToList(),
            trace_epreuve_lignes = r.TraceDEpreuve?.Length,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
