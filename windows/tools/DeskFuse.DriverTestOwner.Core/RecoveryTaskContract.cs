using System.Xml;
using System.Xml.Linq;

namespace DeskFuse.DriverTestOwner.Core;

/// <summary>Exact scheduled-task action and policy, with no arbitrary command or executable supplied by a caller.</summary>
public sealed record RecoveryTaskContract(string Name, string Image, string WorkingDirectory, OwnerCommand Command, bool AtBoot)
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    public string Arguments => string.Join(" ", Command.Arguments());

    public string ToXml()
    {
        XElement Node(string name, object value) => new(Ns + name, value);
        return new XDocument(new XElement(Ns + "Task", new XAttribute("version", "1.4"),
            Node("RegistrationInfo", Node("Description", "DeskFuse exact-run driver-test recovery")),
            new XElement(Ns + "Triggers", AtBoot ? new XElement(Ns + "BootTrigger", Node("Enabled", "true")) : null),
            new XElement(Ns + "Principals", new XElement(Ns + "Principal", new XAttribute("id", "System"),
                // ServiceAccount is TASK_LOGON_TYPE=5 at registration, not a
                // valid XML logonType value in Microsoft's scheduler schema.
                Node("UserId", "S-1-5-18"), Node("RunLevel", "HighestAvailable"))),
            new XElement(Ns + "Settings", Node("MultipleInstancesPolicy", "IgnoreNew"),
                Node("DisallowStartIfOnBatteries", "false"), Node("StopIfGoingOnBatteries", "false"),
                Node("AllowHardTerminate", "true"), Node("StartWhenAvailable", "false"),
                Node("RunOnlyIfNetworkAvailable", "false"), Node("AllowStartOnDemand", "true"),
                Node("Enabled", "true"), Node("Hidden", "true"), Node("WakeToRun", "false"), Node("ExecutionTimeLimit", "PT30M")),
            new XElement(Ns + "Actions", new XAttribute("Context", "System"), new XElement(Ns + "Exec",
                Node("Command", Image), Node("Arguments", Arguments), Node("WorkingDirectory", WorkingDirectory)))))
            .ToString(SaveOptions.DisableFormatting);
    }

    public void RequireMatches(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 131072 });
        var root = XDocument.Load(reader).Root;
        string Value(XElement parent, string path) => parent.Element(Ns + path)?.Value ?? "";
        if (root?.Name != Ns + "Task") throw new InvalidDataException("Unexpected recovery task definition.");
        var principal = root.Element(Ns + "Principals")?.Elements().SingleOrDefault();
        var action = root.Element(Ns + "Actions")?.Elements().SingleOrDefault();
        var triggers = root.Element(Ns + "Triggers")?.Elements().ToArray() ?? [];
        var settings = root.Element(Ns + "Settings");
        if (principal?.Name != Ns + "Principal" || action?.Name != Ns + "Exec" || settings is null ||
            Value(principal, "UserId") is not ("S-1-5-18" or "SYSTEM") || principal.Element(Ns + "LogonType") is not null ||
            principal.Element(Ns + "GroupId") is not null ||
            principal.Attribute("id")?.Value != root.Element(Ns + "Actions")?.Attribute("Context")?.Value ||
            Value(principal, "RunLevel") != "HighestAvailable" ||
            !string.Equals(Value(action, "Command"), Image, StringComparison.OrdinalIgnoreCase) ||
            Value(action, "Arguments") != Arguments ||
            !string.Equals(Value(action, "WorkingDirectory"), WorkingDirectory, StringComparison.OrdinalIgnoreCase) ||
            (AtBoot ? triggers.Length != 1 || triggers[0].Name != Ns + "BootTrigger" || Value(triggers[0], "Enabled") != "true" ||
                triggers[0].Elements().Any(e => e.Name != Ns + "Enabled") : triggers.Length != 0) ||
            Value(settings, "MultipleInstancesPolicy") != "IgnoreNew" || Value(settings, "ExecutionTimeLimit") != "PT30M" ||
            Value(settings, "DisallowStartIfOnBatteries") != "false" || Value(settings, "StopIfGoingOnBatteries") != "false" ||
            Value(settings, "StartWhenAvailable") != "false" || Value(settings, "RunOnlyIfNetworkAvailable") != "false" ||
            Value(settings, "AllowStartOnDemand") != "true" || Value(settings, "Enabled") != "true" ||
            Value(settings, "WakeToRun") != "false" || Value(settings, "AllowHardTerminate") != "true" ||
            Value(settings, "RunOnlyIfIdle") is not ("" or "false") || settings.Element(Ns + "RestartOnFailure") is not null)
            throw new InvalidDataException("Recovery task does not match the prepared exact-run definition.");
    }
}
