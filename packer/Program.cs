// Packs CodeWalker-format XML (written by the Python converter) into binary GTA V files.
//
//   MbgtaPacker <input dir> <output dir> [--check]
//   MbgtaPacker dlc <packed files dir> <output dir> [--name mbgta]
//   MbgtaPacker pose <ydr> <ycd> <clip> <seconds> <out.txt>
//
// Every *.ydr.xml / *.ytd.xml / *.ytyp.xml in the input dir is converted. A .ydr.xml's
// DDS textures are read from the folder of the same name next to it, the way
// Sollumz exports lay them out. --check reloads each written file and prints
// a short summary so a bad conversion fails here rather than in game.
using System;
using System.IO;
using System.Linq;
using System.Xml;
using CodeWalker.GameFiles;
using SharpDX;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "dlc")
        {
            var i = Array.IndexOf(args, "--name");
            DlcPack.Build(args[1], args[2], i > 0 && i + 1 < args.Length ? args[i + 1] : "mbgta");
            return 0;
        }
        if (args.Length == 2 && args[0] == "dump-ycd")
        {
            // The first clip's tracks as the game will read them, to check a converted .ycd.
            var ycd = RpfFile.GetResourceFile<YcdFile>(File.ReadAllBytes(args[1]));
            foreach (var entry in ycd.ClipDictionary.Animations.Animations.data_items.Where(e => e?.Animation != null).Take(1))
            {
                var a = entry.Animation;
                Console.WriteLine($"{a.Hash}: {a.Frames} frames, flags {a.Unknown_10h}");
                var seq = a.Sequences.data_items[0];
                for (int i = 0; i < a.BoneIds.data_items.Length; i++)
                {
                    var b = a.BoneIds.data_items[i];
                    var s = seq.Sequences[i];
                    Console.WriteLine($"  bone {b.BoneId} track {b.Track} format {b.Unk0}: " + string.Join(", ", s.Channels.Select(c => c.Type.ToString())) +
                        $"  frame 5 = {(b.Track == 0 ? s.EvaluateVector(5).ToString() : s.EvaluateQuaternion(5).ToString())}");
                }
            }
            return 0;
        }
        if (args.Length == 6 && args[0] == "pose")
        {
            Pose(args[1], args[2], args[3], float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture), args[5]);
            return 0;
        }
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: MbgtaPacker <input dir> <output dir> [--check]\n       MbgtaPacker dlc <packed files dir> <output dir> [--name mbgta]");
            return 2;
        }
        var inputDir = args[0];
        var outputDir = args[1];
        var check = args.Contains("--check");
        Directory.CreateDirectory(outputDir);

        var failures = 0;
        foreach (var xmlPath in Directory.GetFiles(inputDir, "*.xml").OrderBy(p => p))
        {
            var fileName = Path.GetFileName(xmlPath);
            var format = XmlMeta.GetXMLFormat(fileName.ToLowerInvariant(), out _);
            var outName = fileName.Substring(0, fileName.Length - 4);
            // "ball.ydr.xml" reads its textures from "ball/", matching Sollumz's export layout.
            var assetFolder = Path.Combine(inputDir, Path.GetFileNameWithoutExtension(outName));
            try
            {
                var doc = new XmlDocument();
                doc.Load(xmlPath);
                var data = XmlMeta.GetData(doc, format, assetFolder);
                if (data == null) throw new InvalidDataException("CodeWalker returned no data");
                var outPath = Path.Combine(outputDir, outName);
                File.WriteAllBytes(outPath, data);
                Console.WriteLine($"wrote {outName} ({data.Length} bytes)");
                if (check) Console.WriteLine("  " + Describe(outPath, data));
            }
            catch (Exception e)
            {
                failures++;
                Console.Error.WriteLine($"FAILED {fileName}: {e.Message}");
            }
        }
        return failures == 0 ? 0 : 1;
    }

    // Pose a packed drawable with one clip at a time, from the binary files, the way the game reads
    // them: tracks found by bone tag, bones posed parent first, skinned vertices blended through each
    // geometry's bone list, rigid models moved by their bone. Writes one triangle a line (x y z x 3),
    // the rest pose when the clip is "-".
    static void Pose(string ydrPath, string ycdPath, string clip, float t, string outPath)
    {
        var d = RpfFile.GetResourceFile<YdrFile>(File.ReadAllBytes(ydrPath)).Drawable;
        var skel = d.Skeleton;
        skel.BuildBonesMap();
        var bones = skel.BonesSorted;
        foreach (var b in bones) b.ResetAnimTransform();
        int found = 0, missing = 0;
        if (clip != "-")
        {
            var ycd = RpfFile.GetResourceFile<YcdFile>(File.ReadAllBytes(ycdPath));
            var hash = JenkHash.GenHash(clip);
            var anim = ycd.AnimMap[hash].Animation;
            var frame = anim.GetFramePosition(t);
            for (int i = 0; i < anim.BoneIds.data_items.Length; i++)
            {
                var id = anim.BoneIds.data_items[i];
                if (!skel.BonesMap.TryGetValue(id.BoneId, out var bone)) { missing++; continue; }
                found++;
                if (id.Track == 0) bone.AnimTranslation = new Vector3(anim.EvaluateVector4(frame, i, true).X, anim.EvaluateVector4(frame, i, true).Y, anim.EvaluateVector4(frame, i, true).Z);
                else if (id.Track == 1) bone.AnimRotation = anim.EvaluateQuaternion(frame, i, true);
            }
        }
        foreach (var b in bones) { b.UpdateAnimTransform(); b.UpdateSkinTransform(); }
        Console.WriteLine($"{clip} at {t}s: {found} tracks found by bone tag, {missing} not");
        var lines = new System.Collections.Generic.List<string>();
        foreach (var model in d.DrawableModels.High)
        {
            Console.WriteLine($"  model: skin {model.HasSkin}, bone {model.BoneIndex}, flags {model.Flags}, matrices {model.SkeletonBindUnk1}, {model.Geometries.Length} geometries");
            foreach (var g in model.Geometries)
            {
                var vd = g.VertexData;
                int stride = vd.Info.Stride;
                int pw = vd.Info.GetComponentOffset(1), pi = vd.Info.GetComponentOffset(2);
                var posed = new Vector3[vd.VertexCount];
                for (int v = 0; v < vd.VertexCount; v++)
                {
                    var p = vd.GetVector3(v, 0);
                    if (model.HasSkin == 1)
                    {
                        var sum = Vector3.Zero;
                        for (int k = 0; k < 4; k++)
                        {
                            float w = vd.VertexBytes[v * stride + pw + k] / 255f;
                            int idx = vd.VertexBytes[v * stride + pi + k];
                            if (w <= 0f) continue;
                            var bone = bones[g.BoneIds[idx]];
                            sum += Vector3.TransformCoordinate(p, bone.SkinTransform) * w;
                        }
                        posed[v] = sum;
                    }
                    else
                        posed[v] = Vector3.TransformCoordinate(p, bones[model.BoneIndex].AnimTransform);
                }
                var ind = g.IndexBuffer.Indices;
                for (int i = 0; i + 2 < ind.Length; i += 3)
                    lines.Add(string.Join(" ", new[] { posed[ind[i]], posed[ind[i + 1]], posed[ind[i + 2]] }
                        .SelectMany(q => new[] { q.X, q.Y, q.Z }).Select(f => f.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture))));
            }
        }
        File.WriteAllLines(outPath, lines);
    }

    static string Describe(string path, byte[] data)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".ydr"))
        {
            var d = RpfFile.GetResourceFile<YdrFile>(data).Drawable;
            var models = d.DrawableModels?.High ?? Array.Empty<DrawableModel>();
            var geoms = models.Sum(m => m.Geometries?.Length ?? 0);
            var verts = models.SelectMany(m => m.Geometries ?? Array.Empty<DrawableGeometry>()).Sum(g => g.VerticesCount);
            var texs = d.ShaderGroup?.TextureDictionary?.Textures?.data_items?.Length ?? 0;
            var bones = d.Skeleton?.Bones?.Items?.Length ?? 0;
            return $"drawable '{d.Name}': {geoms} geometries, {verts} vertices, {d.ShaderGroup?.Shaders?.data_items?.Length ?? 0} shaders, " +
                   $"{texs} embedded textures, {bones} bones, bounds {DescribeBound(d.Bound)}, radius {d.BoundingSphereRadius:0.###}";
        }
        if (name.EndsWith(".ycd"))
        {
            var ycd = RpfFile.GetResourceFile<YcdFile>(data);
            var anims = ycd.ClipDictionary?.Animations?.Animations?.data_items ?? Array.Empty<AnimationMapEntry>();
            var first = anims.FirstOrDefault()?.Animation;
            return $"clips: {ycd.ClipMapEntries?.Length ?? 0}, animations: {anims.Length}" +
                   (first != null ? $", first has {first.Frames} frames, {first.BoneIds?.data_items?.Length ?? 0} tracks" : "");
        }
        if (name.EndsWith(".ytyp"))
        {
            var ytyp = RpfFile.GetResourceFile<YtypFile>(data);
            return "archetypes: " + string.Join(", ", ytyp.AllArchetypes.Select(a => a.Name));
        }
        return "";
    }

    static string DescribeBound(Bounds b)
    {
        if (b == null) return "none";
        var s = b.Type.ToString();
        if (b is BoundGeometry g) s += $" ({g.Vertices?.Length ?? 0} vertices, {g.Polygons?.Length ?? 0} polygons)";
        if (b is BoundComposite c)
            s += " [" + string.Join(", ", (c.Children?.data_items ?? Array.Empty<Bounds>()).Select(x => DescribeBound(x) + " " + x.CompositeFlags1.Flags1)) + "]";
        return s;
    }
}
