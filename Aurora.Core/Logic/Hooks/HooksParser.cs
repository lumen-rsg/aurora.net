using Aurora.Core.Logging;

namespace Aurora.Core.Logic.Hooks;

public static class HookParser
{
    public static AlpmHook? Parse(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        var hook = new AlpmHook 
        { 
            Path = filePath,
            Name = System.IO.Path.GetFileNameWithoutExtension(filePath) 
        };

        var lines = File.ReadAllLines(filePath);
        string currentSection = "";
        HookTrigger? currentTrigger = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                currentSection = line.Substring(1, line.Length - 2);
                if (currentSection == "Trigger")
                {
                    currentTrigger = new HookTrigger();
                    hook.Triggers.Add(currentTrigger);
                }
                continue;
            }

            var parts = line.Split('=', 2);
            if (parts.Length < 2) continue;

            var key = parts[0].Trim();
            var val = parts[1].Trim();

            if (currentSection == "Trigger" && currentTrigger != null)
            {
                switch (key)
                {
                    case "Operation":
                        if (Enum.TryParse<TriggerOperation>(val, true, out var op)) 
                            currentTrigger.Operation = op;
                        break;
                    case "Type":
                        if (Enum.TryParse<TriggerType>(val, true, out var type)) 
                            currentTrigger.Type = type;
                        break;
                    case "Target":
                        currentTrigger.Target = val;
                        break;
                }
            }
            else if (currentSection == "Action")
            {
                switch (key)
                {
                    case "Exec": hook.Exec = val; break;
                    case "When":
                        if (Enum.TryParse<HookWhen>(val, true, out var when)) 
                            hook.When = when;
                        break;
                    case "Description": hook.Description = val; break;
                    case "NeedsTargets": hook.NeedsTargets = true; break; // Usually key exists = true
                    case "AbortOnFail": hook.AbortOnFail = true; break;
                }
            }
        }

        // Validate minimal requirements
        if (string.IsNullOrEmpty(hook.Exec)) return null;
        if (hook.Triggers.Count == 0) return null;

        return hook;
    }
}