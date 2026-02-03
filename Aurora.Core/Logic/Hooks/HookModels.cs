namespace Aurora.Core.Logic.Hooks;

public enum HookWhen { PreTransaction, PostTransaction }
public enum TriggerType { Package, File }
public enum TriggerOperation { Install, Upgrade, Remove }

public class HookTrigger
{
    public TriggerOperation Operation { get; set; }
    public TriggerType Type { get; set; }
    public string Target { get; set; } = string.Empty;
}

public class AlpmHook
{
    public string Name { get; set; } = string.Empty; // Filename
    public string Path { get; set; } = string.Empty; // Full Path
    
    // Triggers
    public List<HookTrigger> Triggers { get; set; } = new();
    
    // Action
    public string Exec { get; set; } = string.Empty;
    public HookWhen When { get; set; } = HookWhen.PostTransaction;
    public bool NeedsTargets { get; set; }
    public bool AbortOnFail { get; set; }
    public string Description { get; set; } = string.Empty;
}