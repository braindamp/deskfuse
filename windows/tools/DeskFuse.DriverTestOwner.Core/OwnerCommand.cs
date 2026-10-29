namespace DeskFuse.DriverTestOwner.Core;

public enum OwnerRole { Status, Control, Provision, Watch, Recover }
public enum OwnerControlAction { Close, Acknowledge }

public sealed record OwnerCommand(OwnerRole Role, Guid NamespaceId, Guid RunId, Guid? Nonce = null,
    OwnerControlAction? Action = null, int? Step = null, bool Boot = false)
{
    public static OwnerCommand Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("status" or "control" or "provision" or "watch" or "recover"))
            throw new ArgumentException("Expected status, control, provision, watch or recover.");
        var role = args[0] switch
        {
            "status" => OwnerRole.Status, "control" => OwnerRole.Control, "provision" => OwnerRole.Provision,
            "watch" => OwnerRole.Watch, _ => OwnerRole.Recover
        };
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var boot = false;
        for (var i = 1; i < args.Length; i++)
        {
            var name = args[i];
            if (name == "--boot")
            {
                if (boot || role != OwnerRole.Recover) throw new ArgumentException("Invalid boot role.");
                boot = true; continue;
            }
            if (name is not ("--namespace" or "--run" or "--nonce" or "--action" or "--step") ||
                ++i >= args.Length || !options.TryAdd(name, args[i]))
                throw new ArgumentException("Unknown, duplicate or incomplete owner option.");
        }
        Guid RequiredGuid(string name)
        {
            if (!options.TryGetValue(name, out var value) || !Guid.TryParseExact(value, "N", out var id) || id == Guid.Empty)
                throw new ArgumentException($"{name} requires a nonempty 32-hex identity.");
            return id;
        }
        var namespaceId = RequiredGuid("--namespace");
        var runId = RequiredGuid("--run");
        Guid? nonce = null;
        OwnerControlAction? action = null;
        int? step = null;
        if (role is OwnerRole.Provision or OwnerRole.Watch or OwnerRole.Recover) nonce = RequiredGuid("--nonce");
        else if (options.ContainsKey("--nonce")) throw new ArgumentException("This role has no worker nonce.");
        if (role == OwnerRole.Control)
        {
            action = options.GetValueOrDefault("--action") switch
            {
                "close" => OwnerControlAction.Close, "ack" => OwnerControlAction.Acknowledge,
                _ => throw new ArgumentException("Control action must be close or ack.")
            };
            if (action == OwnerControlAction.Acknowledge)
            {
                if (!options.TryGetValue("--step", out var value) || value is not ("1" or "2" or "3"))
                    throw new ArgumentException("Acknowledgement requires step 1, 2 or 3.");
                step = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (options.ContainsKey("--step")) throw new ArgumentException("Close has no acknowledgement step.");
        }
        else if (options.ContainsKey("--action") || options.ContainsKey("--step"))
            throw new ArgumentException("Only control accepts an action.");
        return new(role, namespaceId, runId, nonce, action, step, boot);
    }

    public string[] Arguments()
    {
        List<string> values = [Role.ToString().ToLowerInvariant(), "--namespace", NamespaceId.ToString("N"), "--run", RunId.ToString("N")];
        if (Nonce is { } nonce) { values.Add("--nonce"); values.Add(nonce.ToString("N")); }
        if (Action is { } action) { values.Add("--action"); values.Add(action == OwnerControlAction.Close ? "close" : "ack"); }
        if (Step is { } step) { values.Add("--step"); values.Add(step.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        if (Boot) values.Add("--boot");
        return values.ToArray();
    }
}
