using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Identifiants RetroAchievements DE SESSION : poses par le hub au check-in
/// du joueur, retires au checkout (ou auto-checkout). Les valeurs d'origine
/// de retroarch.cfg sont sauvegardees avant le premier patch et restaurees a
/// la fin — le proprietaire de la borne retrouve toujours sa configuration.
/// RetroArch lit le cfg au lancement d'un jeu : la session etant ouverte
/// avant de jouer, les succes partent sur le compte RA du joueur.
/// </summary>
public sealed class CheevosSessionService
{
    private static readonly string[] Keys = ["cheevos_username", "cheevos_password", "cheevos_enable", "cheevos_token"];

    private readonly object _lock = new();
    private readonly string _backupPath = Path.Combine(AppContext.BaseDirectory, "state", "cheevos-session-backup.json");

    public sealed record SessionState(bool Active, string? Username);

    public SessionState GetState()
    {
        lock (_lock)
        {
            if (!File.Exists(_backupPath))
            {
                return new SessionState(false, null);
            }

            var current = ReadValues();
            return new SessionState(true, Mask(current.GetValueOrDefault("cheevos_username")));
        }
    }

    /// <summary>Pose les identifiants du joueur (sauvegarde l'existant au
    /// premier appel de la session).</summary>
    public void Apply(string username, string password)
    {
        lock (_lock)
        {
            if (!File.Exists(RetroBatPaths.RetroArchConfigPath))
            {
                throw new FileNotFoundException("retroarch.cfg introuvable", RetroBatPaths.RetroArchConfigPath);
            }

            if (!File.Exists(_backupPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
                File.WriteAllText(_backupPath, JsonSerializer.Serialize(ReadValues()));
            }

            WriteValues(new Dictionary<string, string?>
            {
                ["cheevos_username"] = username,
                ["cheevos_password"] = password,
                // Le token cache appartient au compte precedent : on le purge
                // pour forcer une connexion propre au compte du joueur.
                ["cheevos_token"] = string.Empty,
                ["cheevos_enable"] = "true"
            });
        }
    }

    /// <summary>Restaure la configuration d'origine de la borne.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (!File.Exists(_backupPath))
            {
                return;
            }

            var backup = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(_backupPath)) ?? [];
            WriteValues(backup);
            File.Delete(_backupPath);
        }
    }

    private Dictionary<string, string?> ReadValues()
    {
        var values = Keys.ToDictionary(key => key, _ => (string?)null);
        foreach (var line in File.ReadAllLines(RetroBatPaths.RetroArchConfigPath))
        {
            foreach (var key in Keys)
            {
                if (line.TrimStart().StartsWith(key + " ", StringComparison.Ordinal) ||
                    line.TrimStart().StartsWith(key + "=", StringComparison.Ordinal))
                {
                    var separator = line.IndexOf('=');
                    if (separator > 0)
                    {
                        values[key] = line[(separator + 1)..].Trim().Trim('"');
                    }
                }
            }
        }

        return values;
    }

    private void WriteValues(Dictionary<string, string?> values)
    {
        var lines = File.ReadAllLines(RetroBatPaths.RetroArchConfigPath).ToList();
        foreach (var key in Keys)
        {
            if (!values.TryGetValue(key, out var value))
            {
                continue;
            }

            var index = lines.FindIndex(line =>
                line.TrimStart().StartsWith(key + " ", StringComparison.Ordinal) ||
                line.TrimStart().StartsWith(key + "=", StringComparison.Ordinal));
            if (value is null)
            {
                if (index >= 0)
                {
                    lines.RemoveAt(index);
                }

                continue;
            }

            var newLine = $"{key} = \"{value}\"";
            if (index >= 0)
            {
                lines[index] = newLine;
            }
            else
            {
                lines.Add(newLine);
            }
        }

        File.WriteAllLines(RetroBatPaths.RetroArchConfigPath, lines);
    }

    private static string? Mask(string? username)
        => string.IsNullOrEmpty(username) ? username : username[..Math.Min(2, username.Length)] + "…";
}
