using System.Runtime.Versioning;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.MassRun;

namespace RetroBat.Api.Controllers;

/// <summary>MASS RUN (CDC MASS RUN, lot L2) : une salle d'essai sur la borne, coureurs simules, sans reseau.</summary>
[ApiController]
[Route("api/v1/massrun")]
[SupportedOSPlatform("windows")]
public sealed class MassRunController : ControllerBase
{
    private readonly MassRunService _massrun;

    public MassRunController(MassRunService massrun) => _massrun = massrun;

    public sealed class DemandeDEssai
    {
        /// <example>1-1</example>
        public string Niveau { get; set; } = "1-1";

        /// <summary>La salle d'attente, en secondes, avant le decompte.</summary>
        public int Attente { get; set; } = 60;

        /// <summary>Les coureurs simules (pistes enregistrees sur la borne), 64 au plus.</summary>
        public int Coureurs { get; set; } = 20;
    }

    /// <summary>Lance une salle d'essai : Super Mario Bros., le niveau demande, sur le coeur hote.</summary>
    [HttpPost("essai")]
    public IActionResult Essai([FromBody] DemandeDEssai? demande)
    {
        demande ??= new DemandeDEssai();
        try
        {
            var salle = _massrun.Lancer(demande.Niveau.Trim(), Math.Clamp(demande.Attente, 10, 300), Math.Clamp(demande.Coureurs, 0, MassRunCanal.MaxCoureurs));
            return Accepted(Vue(salle));
        }
        catch (InvalidOperationException e)
        {
            return Conflict(new { message = e.Message });
        }
    }

    /// <summary>La salle en cours ou la derniere : mode du coeur, image de course, arrivee.</summary>
    [HttpGet("etat")]
    public IActionResult Etat() => _massrun.Salle is { } salle ? Ok(Vue(salle)) : NotFound(new { message = "aucune salle" });

    private static object Vue(MassRunService.SalleLocale s) => new
    {
        niveau = s.Niveau,
        go = s.GoUtc,
        pseudo = s.Pseudo,
        couleur = s.Couleur,
        coureurs = s.Coureurs,
        pistes = s.Pistes,
        mode = s.Mode switch { 1 => "preparation", 2 => "attente", 3 => "decompte", 4 => "course", 5 => "arrivee", _ => "chargement" },
        imageCourse = s.ImageCourse,
        arrivee = s.Arrivee,
        finie = s.Finie,
    };
}
