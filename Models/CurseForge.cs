#nullable disable
namespace CraftDeck.Models.CurseForge;

public class CFList<T>
{
    public List<T> data { get; set; }
}

public class CFData<T>
{
    public T data { get; set; }
}

public class CFMod
{
    public long id { get; set; }
    public string name { get; set; }
    public string summary { get; set; }
    public List<CFAuthor> authors { get; set; }
    public CFLogo logo { get; set; }
    public long downloadCount { get; set; }
    public long favoriteCount { get; set; }
    public string dateModified { get; set; }
    public List<CFFile> latestFiles { get; set; }
}

public class CFAuthor
{
    public long id { get; set; }
    public string name { get; set; }
}

public class CFLogo
{
    public long id { get; set; }
    public string url { get; set; }
    public string thumbnailUrl { get; set; }
}

public class CFFile
{
    public long id { get; set; }
    public long modId { get; set; }
    public string displayName { get; set; }
    public string fileName { get; set; }
    public string downloadUrl { get; set; }
    public long fileLength { get; set; }
    public string fileDate { get; set; }
    public List<string> gameVersions { get; set; }
}

public class CFManifest
{
    public string manifestType { get; set; }
    public string name { get; set; }
    public CFManifestMinecraft minecraft { get; set; }
    public List<CFManifestFile> files { get; set; }
    public string overrides { get; set; }
}

public class CFManifestMinecraft
{
    public string version { get; set; }
    public List<CFManifestLoader> modLoaders { get; set; }
}

public class CFManifestLoader
{
    public string id { get; set; }
    public bool primary { get; set; }
}

public class CFManifestFile
{
    public long projectID { get; set; }
    public long fileID { get; set; }
    public bool required { get; set; }
}
