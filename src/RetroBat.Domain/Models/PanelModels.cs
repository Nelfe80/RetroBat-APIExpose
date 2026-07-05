namespace RetroBat.Domain.Models;

/// <summary>
/// Catalog entry describing a panel definition exposed by the API.
/// </summary>
public class PanelCatalogEntrySnapshot
{
    public string Source { get; set; } = "dynpanels";
    public string Scope { get; set; } = "none";
    public string SystemId { get; set; } = string.Empty;
    public string Rom { get; set; } = string.Empty;
    public string Core { get; set; } = string.Empty;
    public string ThemePanelPath { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string DefaultLayoutId { get; set; } = string.Empty;
    public int LayoutCount { get; set; }
    public List<string> LayoutIds { get; set; } = new();
}

/// <summary>
/// Resolved panel snapshot used by REST and theme XML generation.
/// </summary>
public class PanelThemeSnapshot
{
    public string Source { get; set; } = "dynpanels";
    public string SystemId { get; set; } = string.Empty;
    public string Rom { get; set; } = string.Empty;
    public string Core { get; set; } = string.Empty;
    public string Scope { get; set; } = "none";
    public string DefaultLayoutId { get; set; } = string.Empty;
    public string ActiveLayoutId { get; set; } = string.Empty;
    public string ActiveLayoutSource { get; set; } = "fallback";
    public string ThemePanelPath { get; set; } = string.Empty;
    public string SystemPanelFile { get; set; } = string.Empty;
    public string CorePanelFile { get; set; } = string.Empty;
    public string GamePanelFile { get; set; } = string.Empty;
    public List<PanelThemeLayoutSnapshot> Layouts { get; set; } = new();
}

public class PanelThemeLayoutSnapshot
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int PanelButtons { get; set; }
    public string Scope { get; set; } = string.Empty;
    public string ProfileKey { get; set; } = string.Empty;
    public List<PanelThemePlayerSnapshot> Players { get; set; } = new();
    public List<PanelThemeButtonSnapshot> SystemButtons { get; set; } = new();
}

public class PanelThemePlayerSnapshot
{
    public int Index { get; set; }
    public string JoystickType { get; set; } = string.Empty;
    public string JoystickColor { get; set; } = string.Empty;
    public List<PanelThemeButtonSnapshot> Buttons { get; set; } = new();
}

public class PanelThemeButtonSnapshot
{
    public string Id { get; set; } = string.Empty;
    public int? Slot { get; set; }
    public int Player { get; set; }
    public string Label { get; set; } = string.Empty;
    public string MachineButton { get; set; } = string.Empty;
    public string Function { get; set; } = string.Empty;
    public string Controller { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public int? RetropadId { get; set; }
    public string LibretroButton { get; set; } = string.Empty;
    public string FbneoButton { get; set; } = string.Empty;
    public string MameButton { get; set; } = string.Empty;
}
