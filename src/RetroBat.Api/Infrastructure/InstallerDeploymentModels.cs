namespace RetroBat.Api.Infrastructure;

public class InstallerDeployRequest
{
    public bool DryRun { get; set; }
}

public class InstallerDeploymentResult
{
    public string Action { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool DryRun { get; set; }
    public string InstallerRootPath { get; set; } = string.Empty;
    public string ThemesSourcePath { get; set; } = string.Empty;
    public string ScriptsSourcePath { get; set; } = string.Empty;
    public string GameInfosSourcePath { get; set; } = string.Empty;
    public string ThemesTargetPath { get; set; } = string.Empty;
    public string ScriptsTargetPath { get; set; } = string.Empty;
    public string GameInfosTargetPath { get; set; } = string.Empty;
    public string HashManifestPath { get; set; } = string.Empty;
    public int CheckedFiles { get; set; }
    public int MissingFiles { get; set; }
    public int ChangedFiles { get; set; }
    public int CopiedFiles { get; set; }
    public List<InstallerDeploymentItem> Items { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class InstallerDeploymentItem
{
    public string Kind { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public bool TargetExists { get; set; }
    public bool OverwriteAllowed { get; set; }
    public bool NeedsCopy { get; set; }
    public bool Applied { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
}
