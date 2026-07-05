namespace RetroBat.Api.Infrastructure;

public class RetroArchWrapperDeployRequest
{
    public bool DryRun { get; set; }
}

public class RetroArchWrapperDeploymentResult
{
    public string Action { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool DryRun { get; set; }
    public bool RetroArchRunning { get; set; }
    public bool SkippedBecauseRetroArchRunning { get; set; }
    public string WrapperDllPath { get; set; } = string.Empty;
    public string CoresPath { get; set; } = string.Empty;
    public string RealCoresPath { get; set; } = string.Empty;
    public bool WrapperExists { get; set; }
    public bool WrapperHasSignature { get; set; }
    public int CheckedCores { get; set; }
    public int WrappedCores { get; set; }
    public int RealCores { get; set; }
    public int MissingRealCores { get; set; }
    public int PendingDeployments { get; set; }
    public int DeployedCores { get; set; }
    public List<RetroArchWrapperCoreStatus> Cores { get; set; } = new();
    public List<RetroArchWrapperDeploymentAction> Actions { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class RetroArchWrapperCoreStatus
{
    public string CoreName { get; set; } = string.Empty;
    public string CorePath { get; set; } = string.Empty;
    public string RealCorePath { get; set; } = string.Empty;
    public bool IsWrapper { get; set; }
    public bool HasRealCore { get; set; }
    public bool NeedsDeployment { get; set; }
    public long CoreBytes { get; set; }
    public long? RealCoreBytes { get; set; }
    public DateTime LastWriteTime { get; set; }
    public DateTime? RealLastWriteTime { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class RetroArchWrapperDeploymentAction
{
    public string CoreName { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string? BackupPath { get; set; }
    public bool Applied { get; set; }
}
