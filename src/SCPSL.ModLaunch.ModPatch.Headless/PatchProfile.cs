using Newtonsoft.Json;

namespace SCPSL.ModLaunch.ModPatch.Headless;

// Data contract derived from SCPSL-ModPatch PatchUtils/PatchInfo.cs (MIT).
public sealed class PatchInfo
{
    public List<VersionProfile> VersionRanges { get; set; } = [];
    public static PatchInfo Read(string path) => JsonConvert.DeserializeObject<PatchInfo>(File.ReadAllText(path),
        new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Error,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            TypeNameHandling = TypeNameHandling.None,
            MaxDepth = 64
        }) ?? throw new InvalidDataException("Empty patch profile.");
}

public sealed class VersionProfile
{
    public string VersionRange { get; set; } = "";
    public string VersionFrom { get; set; } = "";
    public string VersionTo { get; set; } = "";
    public string CleanLauncherPath { get; set; } = "";
    public string CleanLauncherUrl { get; set; } = "";
    public int MetadataVersion { get; set; }
    public ProfileMethods Methods { get; set; } = new();
    public bool Contains(Version version)
    {
        if (!Version.TryParse(VersionFrom, out var lower)) throw new InvalidDataException("Invalid versionFrom.");
        var upper = VersionTo == "Current" ? new Version(255, 255, 255) : Version.Parse(VersionTo);
        return version >= lower && version <= upper;
    }
}

public sealed class ProfileMethods
{
    public List<MethodPatch> PatchMethods { get; set; } = [];
    public VersionMethod? GameVersionMethod { get; set; }
}

public sealed class MethodPatch
{
    public string Name { get; set; } = "";
    public int PatchOffset { get; set; }
    public string PatchData { get; set; } = "";
    public int? PatchSize { get; set; }
    public bool Nowarning { get; set; }
    public string? Patchifnotexists { get; set; }
}

public sealed class VersionMethod
{
    public string Name { get; set; } = "GameCore.Version$$.cctor";
    public bool AutoFindOffsets { get; set; }
    public int MajorOffset { get; set; }
    public int MinorOffset { get; set; }
    public int RevisionOffset { get; set; }
    public int TypeOffset { get; set; }
}
