namespace RetroBat.Domain.Models;

public class RuleSet
{
    public string Id { get; set; } = string.Empty;
    public string ScopeSystemId { get; set; } = string.Empty;
    public string ScopeLayout { get; set; } = string.Empty;
    public List<Rule> Rules { get; set; } = new();
}

public class Rule
{
    public RuleTrigger When { get; set; } = new();
    public List<RuleAction> Then { get; set; } = new();
}

public class RuleTrigger
{
    public string Type { get; set; } = string.Empty;
    public string? Button { get; set; }
    public string? OutputKey { get; set; }
    public string? Event { get; set; }
}

public class RuleAction
{
    public string Type { get; set; } = string.Empty;
    public string? Panel { get; set; }
    public Dictionary<string, string>? Colors { get; set; }
    public string? Url { get; set; }
    public string? Target { get; set; }
    public object? SceneSpec { get; set; }
}

public class LayoutSchema
{
    public string RuleSetId { get; set; } = string.Empty;
    public Dictionary<string, string>? Assets { get; set; }
    public List<LayoutBinding>? Bindings { get; set; }
    public List<LayoutView>? Views { get; set; }
}

public class LayoutBinding
{
    public string OutputKey { get; set; } = string.Empty;
    public string Element { get; set; } = string.Empty;
    public Dictionary<string, string>? States { get; set; }
}

public class LayoutView
{
    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
}
