using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml;

namespace TuPack
{
    class Program
    {
        static XmlDocument doc = new XmlDocument();

        static bool TryGetVersion(string path, out int version)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir == null)
            {
                version = 0;
                return false;
            }
            var manifest = Path.Join(dir, "tizen-manifest.xml");
            if (File.Exists(manifest))
            {
                doc.Load(manifest);
                var appId = doc.SelectSingleNode("//@api-version")?.Value;
                if (int.TryParse(appId, out version))
                {
                    return true;
                }
            }

            return TryGetVersion(dir, out version);
        }

        static bool TryFindTizenFX(int version, out string path)
        {
            path = "";
            var settings = NuGet.Configuration.Settings.LoadDefaultSettings(null);
            var nugetPath = NuGet.Configuration.SettingsUtility.GetGlobalPackagesFolder(settings);
            var packagePath = Path.Join(nugetPath, $"tizen.net.api{version}");

            if (!Directory.Exists(packagePath))
            {
                return false;
            }

            var versions = from file in Directory.GetDirectories(packagePath) select Version.Parse(new DirectoryInfo(file).Name);
            var latest = versions.OrderByDescending(v => v).First();

            var latestPackage = Path.Combine(packagePath, latest.ToString(), "ref", "netstandard2.0");

            if (!Directory.Exists(latestPackage))
            {
                return false;
            }

            path = latestPackage;
            return true;
        }

        public static IEnumerable<string> Glob(string glob)
        {
            foreach (string path in Glob(PathHead(glob) + DirSep, PathTail(glob)))
                yield return path;
        }
        public static IEnumerable<string> Glob(string head, string tail)
        {
            if (PathTail(tail) == tail)
                foreach (string path in Directory.GetFiles(head, tail).OrderBy(s => s))
                    yield return path;
            else
            {
                foreach (string dir in Directory.GetDirectories(head, PathHead(tail)).OrderBy(s => s))
                {
                    foreach (string path in Glob(Path.Combine(head, dir), PathTail(tail)))
                    {
                        yield return path;
                    }
                }
            }
        }
        static char DirSep = Path.DirectorySeparatorChar;
        static string PathHead(string path)
        {
            if (path.StartsWith("" + DirSep + DirSep))
                return path.Substring(0, 2) + path.Substring(2).Split(DirSep)[0] + DirSep + path.Substring(2).Split(DirSep)[1];

            return path.Split(DirSep)[0];
        }
        static string PathTail(string path)
        {
            if (!path.Contains(DirSep))
                return path;

            return path.Substring(1 + PathHead(path).Length);
        }

        static bool TryGetTpkRoot(string path, out string dir)
        {
            var bindir = Path.GetDirectoryName(path);
            var root = Directory.GetParent(bindir);

            var files = root.GetFiles();
            var authorSig = files.Any(f => f.Name == "author-signature.xml");
            var sig = files.Any(f => f.Name == "signature1.xml");
            var manifest = files.Any(f => f.Name == "tizen-manifest.xml");

            if (authorSig && sig && manifest)
            {
                dir = root.FullName;
                return true;
            }
            else
            {
                dir = "";
            }
            return false;
        }

        static async Task CallPack(string tpkroot)
        {
            var abs = Path.GetFullPath(tpkroot);
            var root = new DirectoryInfo(Directory.GetDirectoryRoot(abs));
            var dir = new DirectoryInfo(abs);
            var projRoot = dir;
            FileInfo csproj = null;
            while (projRoot != root)
            {
                csproj = projRoot.GetFiles().FirstOrDefault(f => Path.GetExtension(f.Name)?.ToLowerInvariant() == ".csproj");
                if (csproj == null)
                {
                    projRoot = projRoot.Parent;
                }
                else
                {
                    break;
                }
            }
            if (csproj == null)
            {
                Console.WriteLine($"Can not find csproj in top of tpkroot.");
                return;
            }
            doc.Load(csproj.ToString());
            var sdk = doc.SelectSingleNode("//@Sdk")?.Value;
            if (!sdk.StartsWith("Tizen.NET.Sdk/"))
            {
                Console.WriteLine($"project is not Tizen.NET.Sdk <Project Sdk=\"Tizen.NET.Sdk/x.y.z\">");
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var success = await Process.ExecuteAsync("dotnet",
                    $"msbuild /t:TizenPackage /p:TizenPackageDirName={dir.Name}",
                    projRoot.FullName,
                    line => Console.WriteLine(line),
                    err => Console.WriteLine($"error: {err}"));
            }
            else
            {
                Console.WriteLine($"Not Support yet.");
            }
        }


        /// <summary>
        /// Application for implant Tizen Trace to DLL, use ";" for sperator of list
        /// </summary>
        /// /// <param name="args">paths of DLL</param>
        /// <param name="namespaces">target namespace list</param>
        /// <param name="types">target type list</param>
        /// <param name="methods">target method list</param>
        /// <param name="api">Tizen.NET API version</param>
        /// <param name="repack">Repackaging if a directory of first dll is child of tpk root</param>
        static void Main(string[] args, string namespaces = "", string types = "", string methods = "", int api = 0, bool repack=true)
        {
            if (args == null || args.Length < 1)
            {
                Console.WriteLine($"There are no dll path...");
                return;
            }

            var nsList = from ns in namespaces.Split(';').Distinct() where !string.IsNullOrEmpty(ns) select ns;
            var typeList = from t in types.Split(';').Distinct() where !string.IsNullOrEmpty(t) select t;
            var methodList = from md in methods.Split(';').Distinct() where !string.IsNullOrEmpty(md) select md;

            var dllPaths = from path in args where File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() == ".dll" select Path.GetFullPath(path);
            var globs = from glob in args where glob.IndexOf('*') >= 0 select Glob(Path.GetFullPath(glob));

            var paths = new HashSet<string>();
            foreach (var path in dllPaths)
            {
                if (!paths.Contains(path))
                {
                    paths.Add(path);
                }
            }
            foreach (var path in globs.SelectMany(x => x))
            {
                if (!paths.Contains(path))
                {
                    paths.Add(path);
                }
            }

            if (paths.Count < 1)
            {
                Console.WriteLine($"There are no dll path...");
                return;
            }

            if (api == 0)
            {
                foreach (var path in paths)
                {
                    if (TryGetVersion(path, out api))
                    {
                        break;
                    }
                }
            }

            if (!TryFindTizenFX(api, out string tizenFXPath))
            {
                Console.WriteLine($"Can not find Tizen.NET.API{api}");
                return;
            }

            var implanter = new Implanter(tizenFXPath, nsList.ToList(), typeList.ToList(), methodList.ToList());

            foreach (var arg in dllPaths)
            {
                implanter.Implant(arg);
            }

            if (repack)
            {
                if (TryGetTpkRoot(paths.FirstOrDefault(), out var dir))
                {
                    if (!repack)
                    {
                        return;
                    }
                    CallPack(dir).Wait();
                }
                else
                {
                    Console.WriteLine($"dll is not placed inside of tpk root.");
                }
            }
        }
    }
}
