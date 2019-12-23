using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TuPack
{
    class Implanter
    {
        MyResolver Resolver;
        ICollection<string> Namespaces;
        ICollection<string> Types;
        ICollection<string> Methods;

        internal static MethodDefinition TTBeginDef;
        internal static MethodDefinition TTEndDef;
        internal static MethodDefinition TTBeginAsyncDef;
        internal static MethodDefinition TTEndAsyncDef;


        public Implanter(string tfxDir, ICollection<string> nss = null, ICollection<string> types = null, ICollection<string> methods = null)
        {
            Namespaces = nss;
            Types = types;
            Methods = methods;

            Resolver = new MyResolver();
            if (!string.IsNullOrEmpty(tfxDir))
            {
                Resolver.AddSearchDirectory(tfxDir);
            }

            var tizenTracer = Path.Join(tfxDir, "Tizen.Tracer.dll");
            using (var asmDef = AssemblyDefinition.ReadAssembly(tizenTracer))
            {
                var ttType = asmDef.MainModule.Types.FirstOrDefault(t => t.FullName == "Tizen.Tracer");
                if (ttType == null)
                {
                    throw new Exception("Can't find Tizen.Tracer type");
                }

                if (TTBeginDef == null)
                    TTBeginDef = ttType.Methods.FirstOrDefault(md => md.Name == "Begin" && md.Parameters.Count == 1 && md.Parameters[0].ParameterType.MetadataType == MetadataType.String);
                if (TTEndDef == null)
                    TTEndDef = ttType.Methods.FirstOrDefault(md => md.Name == "End");
                if (TTBeginAsyncDef == null)
                    TTBeginAsyncDef = ttType.Methods.FirstOrDefault(md => md.Name == "AsyncBegin"
                                                                    && md.Parameters.Count == 2
                                                                    && md.Parameters[0].ParameterType.MetadataType == MetadataType.Int32
                                                                    && md.Parameters[1].ParameterType.MetadataType == MetadataType.String);
                if (TTEndAsyncDef == null)
                    TTEndAsyncDef = ttType.Methods.FirstOrDefault(md => md.Name == "AsyncEnd"
                                                                  && md.Parameters.Count == 2
                                                                  && md.Parameters[0].ParameterType.MetadataType == MetadataType.Int32
                                                                  && md.Parameters[1].ParameterType.MetadataType == MetadataType.String);

                if (TTBeginDef == null)
                {
                    throw new Exception($"Can't find Tizen.Tracer.Begin(string)");
                }
                if (TTEndDef == null)
                {
                    throw new Exception($"Can't find Tizen.Tracer.End()");
                }
                if (TTBeginAsyncDef == null)
                {
                    throw new Exception($"Can't find Tizen.Tracer.AsyncBegin(string)");
                }
                if (TTEndAsyncDef == null)
                {
                    throw new Exception($"Can't find Tizen.Tracer.AsyncEnd()");
                }
            }
        }

        public bool FilterNamespace(string ns) => Namespaces == null || Namespaces.Count == 0 || Namespaces.Contains(ns);
        public bool FilterType(string cls) => Types == null || Types.Count == 0 || Types.Contains(cls);
        public bool FilterMethod(string md) => Methods == null || Methods.Count == 0 || Methods.Contains(md);
        public void Implant(string path)
        {
            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            var pdb = Path.Join(dir, $"{name}.pdb");
            var mdb = Path.Join(dir, $"{name}.mdb");
            var IsDebug = File.Exists(pdb) || File.Exists(mdb);

            Resolver.AddSearchDirectory(dir);

            var rParam = new ReaderParameters
            {
                AssemblyResolver = Resolver,
                ReadWrite = true,
                ReadSymbols = IsDebug
            };
            using (var asmDef = AssemblyDefinition.ReadAssembly(path, rParam))
            {
                var funcs = new TracerFunctions
                {
                    Begin = asmDef.MainModule.ImportReference(TTBeginDef),
                    End = asmDef.MainModule.ImportReference(TTEndDef),
                    BeginAsync = asmDef.MainModule.ImportReference(TTBeginAsyncDef),
                    EndAsync = asmDef.MainModule.ImportReference(TTEndAsyncDef)
                };
                foreach (var module in asmDef.Modules)
                {
                    foreach (var type in module.Types)
                    {
                        if (!FilterNamespace(type.Namespace) || !FilterType(type.Name))
                        {
                            continue;
                        }
                        foreach (var method in type.ConcreteMethods())
                        {
                            if (!FilterMethod(method.Name))
                            {
                                continue;
                            }

                            Implant(method, funcs);
                        }
                    }
                }

                asmDef.Write(new WriterParameters { WriteSymbols = IsDebug });
            }
        }

        void Implant(MethodDefinition md, TracerFunctions funcs)
        {
            Console.WriteLine($"Process.... [{md.Module.Assembly.Name.Name}] {md.DeclaringType.Namespace}::{md.DeclaringType.Name}.{md.Name}");

            if (md.IsYield())
            {
                Console.WriteLine($"           Skipping '{md.FullName}' since methods that yield are not supported.");
            }

            if (md.IsAsync())
            {
                new AsyncMethodProcessor(md, funcs).Process();

            }
            else
            {
                new MethodProcessor(md, funcs).Process();
            }
        }

        class MyResolver : DefaultAssemblyResolver
        {
            HashSet<string> searchDirectories = new HashSet<string>();
            public new void AddSearchDirectory(string dir)
            {
                if (!searchDirectories.Contains(dir))
                {
                    base.AddSearchDirectory(dir);
                }
            }
            public void AddAssembly(string asm) => RegisterAssembly(AssemblyDefinition.ReadAssembly(asm, new ReaderParameters { AssemblyResolver = this }));
            public override AssemblyDefinition Resolve(AssemblyNameReference name)
            {
                if (TryResolve(name, out var asm)) return asm;
                if (IsMscorlib(name) && (TryResolve("mscorlib", out asm) || TryResolve("netstandard", out asm) || TryResolve("System.Runtime", out asm))) return asm;
                throw new AssemblyResolutionException(name);
            }

            bool TryResolve(string an, out AssemblyDefinition asm) => TryResolve(AssemblyNameReference.Parse(an), out asm);

            bool TryResolve(AssemblyNameReference anr, out AssemblyDefinition asm)
            {
                try
                {
                    asm = base.Resolve(anr);
                    return true;
                }
                catch (AssemblyResolutionException)
                {
                    asm = null;
                    return false;
                }
            }

            static bool IsMscorlib(AssemblyNameReference name) => name.Name == "mscorlib" || name.Name == "System.Runtime" || name.Name == "netstandard";
        }
    }
}
