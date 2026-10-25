// Builds a story-mode add-on DLC pack (dlc.rpf) around the packed props, so GTA streams them.
//
// Layout inside dlc.rpf:
//   content.xml, setup2.xml
//   x64/<name>_props.rpf   (every .ydr, .ycd, .ytd and .ytyp from the input dir)
using System;
using System.IO;
using System.Linq;
using System.Text;
using CodeWalker.GameFiles;

static class DlcPack
{
    public static void Build(string gtaDir, string outDir, string name)
    {
        var packDir = Path.Combine(outDir, name);
        Directory.CreateDirectory(packDir);
        var rpfPath = Path.GetFullPath(Path.Combine(packDir, "dlc.rpf"));
        if (File.Exists(rpfPath)) File.Delete(rpfPath);

        var files = Directory.GetFiles(gtaDir).Where(f => f.EndsWith(".ydr") || f.EndsWith(".ycd") || f.EndsWith(".ytd") || f.EndsWith(".ytyp")).OrderBy(f => f).ToArray();
        var ytyps = files.Where(f => f.EndsWith(".ytyp")).Select(Path.GetFileName).ToArray();
        if (files.Length == 0) throw new InvalidDataException($"no .ydr/.ytyp files in {gtaDir}");

        // CodeWalker joins paths with '\' and uses a path with a ':' (a drive letter) as-is. Off Windows
        // there's no drive letter, so make it under a name with a ':' in the pack folder, then rename it.
        string made = rpfPath;
        if (!OperatingSystem.IsWindows())
        {
            made = Path.Combine(packDir, "new:dlc.rpf");
            if (File.Exists(made)) File.Delete(made);
        }
        var dlc = RpfFile.CreateNew("", made, RpfEncryption.OPEN);
        RpfFile.CreateFile(dlc.Root, "content.xml", Encoding.UTF8.GetBytes(Lf(ContentXml(name, ytyps))));
        RpfFile.CreateFile(dlc.Root, "setup2.xml", Encoding.UTF8.GetBytes(Lf(Setup2Xml(name))));
        var x64 = RpfFile.CreateDirectory(dlc.Root, "x64");
        var props = RpfFile.CreateNew(x64, name + "_props.rpf", RpfEncryption.OPEN);
        foreach (var f in files)
        {
            RpfFile.CreateFile(props.Root, Path.GetFileName(f), File.ReadAllBytes(f));
        }
        if (made != rpfPath) File.Move(made, rpfPath);
        Console.WriteLine($"wrote {rpfPath} ({files.Length} files in x64/{name}_props.rpf)");
        Verify(rpfPath);
    }

    // Reopens the pack from disk and lists every entry, nested rpfs included.
    static void Verify(string rpfPath)
    {
        var rpf = new RpfFile(rpfPath, Path.GetFileName(rpfPath));
        rpf.ScanStructure(null, e => throw new InvalidDataException(e));
        void List(RpfFile f, string indent)
        {
            foreach (var e in f.AllEntries.OfType<RpfFileEntry>())
                Console.WriteLine($"{indent}{e.Path.Substring(f.Path.Length).TrimStart('\\')} ({e.GetFileSize()} B)");
            foreach (var c in f.Children ?? new System.Collections.Generic.List<RpfFile>()) List(c, indent + "  ");
        }
        List(rpf, "  ");
    }

    // The same bytes on every machine: AppendLine and a checkout with CRLF line endings would otherwise
    // give Windows builds \r\n.
    static string Lf(string text) => text.Replace("\r\n", "\n");

    static string ContentXml(string name, string[] ytyps)
    {
        var rpf = $"dlc_{name}:/%PLATFORM%/{name}_props.rpf";
        var items = new[] { (rpf, "RPF_FILE") }
            .Concat(ytyps.Select(y => ($"{rpf}/{y}", "DLC_ITYP_REQUEST"))).ToArray();
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<CDataFileMgr__ContentsOfDataFileXml>");
        sb.AppendLine("  <disabledFiles />\n  <includedXmlFiles />\n  <includedDataFiles />\n  <dataFiles>");
        foreach (var (file, type) in items)
        {
            sb.AppendLine($"    <Item>\n      <filename>{file}</filename>\n      <fileType>{type}</fileType>");
            sb.AppendLine("      <overlay value=\"false\" />\n      <disabled value=\"true\" />\n      <persistent value=\"true\" />\n    </Item>");
        }
        sb.AppendLine("  </dataFiles>\n  <contentChangeSets>\n    <Item>");
        sb.AppendLine($"      <changeSetName>{name.ToUpperInvariant()}_AUTOGEN</changeSetName>\n      <filesToDisable />\n      <filesToEnable>");
        foreach (var (file, _) in items) sb.AppendLine($"        <Item>{file}</Item>");
        sb.AppendLine("      </filesToEnable>\n      <txdToLoad />\n      <txdToUnload />\n      <residentResources />\n      <unregisterResources />");
        sb.AppendLine("    </Item>\n  </contentChangeSets>\n  <patchFiles />\n</CDataFileMgr__ContentsOfDataFileXml>");
        return sb.ToString();
    }

    static string Setup2Xml(string name) => $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<SSetupData>
  <deviceName>dlc_{name}</deviceName>
  <datFile>content.xml</datFile>
  <timeStamp>01/01/2026 00:00:00</timeStamp>
  <nameHash>{name}</nameHash>
  <contentChangeSetGroups>
    <Item>
      <NameHash>GROUP_STARTUP</NameHash>
      <ContentChangeSets>
        <Item>{name.ToUpperInvariant()}_AUTOGEN</Item>
      </ContentChangeSets>
    </Item>
  </contentChangeSetGroups>
  <type>EXTRACONTENT_COMPAT_PACK</type>
  <order value=""999"" />
  <minorOrder value=""0"" />
  <isLevelPack value=""false"" />
  <dependencyPackHash />
  <requiredVersion />
  <subPackCount value=""0"" />
</SSetupData>
";
}
