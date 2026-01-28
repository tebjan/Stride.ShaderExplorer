using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Stride.Core.Mathematics;
using Stride.Rendering;
using Stride.Graphics;
using Stride.Core.IO;
using System.IO;
using Stride.Shaders.Compiler;
using Stride.Core.Shaders.Ast;
using Stride.Shaders.Parser;
using Stride.Core.Diagnostics;
using Stride.Shaders;
using ShaderMacro = Stride.Core.Shaders.Parser.ShaderMacro;
using System.Reflection;
using System.Diagnostics;
using Stride.Core;
using Stride.Shaders.Parser.Mixins;
using StrideShaderExplorer;

namespace Stride.ShaderParser
{
    static class EffectUtils
    {
        public static string GetPathOfSdslShader(string effectName, IVirtualFileProvider fileProvider, IVirtualFileProvider dbFileProvider = null)
        {
            var path = EffectCompilerBase.GetStoragePathFromShaderType(effectName);
            if (fileProvider.TryGetFileLocation(path, out var filePath, out _, out _))
            {
                if (File.Exists(filePath))
                    return filePath;
            }

            var pathUrl = path + "/path";
            if (fileProvider.FileExists(pathUrl))
            {
                using (var pathStream = fileProvider.OpenStream(pathUrl, VirtualFileMode.Open, VirtualFileAccess.Read))
                using (var reader = new StreamReader(pathStream))
                {
                    var dbPath = reader.ReadToEnd();
                    if (File.Exists(dbPath))
                        return dbPath;
                }
            }

            if (dbFileProvider != null)
                return GetPathOfSdslShader(effectName, dbFileProvider);

            //find locally
            if (LocalShaderFilePaths.TryGetValue(effectName, out var fp))
                return fp;

            return null;
        }

        //get shader source from data base, is there a more direct way?
        public static string GetShaderSourceCode(string effectName, IVirtualFileProvider fileProvider, ShaderSourceManager shaderSourceManager)
        {
            var path = GetPathOfSdslShader(effectName, fileProvider);

            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    return File.ReadAllText(path);
                }
                catch (Exception)
                {

                    //fall through
                }
            } 

            return shaderSourceManager?.LoadShaderSource(effectName).Source;
        }

        static readonly Dictionary<string, string> LocalShaderFilePaths = GetShaders();

        private static Dictionary<string, string> GetShaders()
        {
            var packsFolder = Path.Combine(PlatformFolders.ApplicationBinaryDirectory, "packs");
            if (Directory.Exists(packsFolder))
            {
                return Directory.EnumerateDirectories(packsFolder, @"*Assets", SearchOption.AllDirectories)
                    .Where(p => p.Contains(@"\stride\Assets"))
                    .SelectMany(d => Directory.EnumerateFiles(d, "*.sdsl", SearchOption.AllDirectories))
                    .ToDictionary(fp => Path.GetFileNameWithoutExtension(fp));
            }
            else
            {
                return new Dictionary<string, string>();
            }
        }

        static readonly Regex FCamelCasePattern = new Regex("[a-z][A-Z0-9]", RegexOptions.Compiled);


        public static string GetPinName(this ParameterKey key, HashSet<string> usedNames)
        {
            var variableName = key.GetVariableName();
            var shaderName = key.GetShaderName();
            var camelCasedName = FCamelCasePattern.Replace(variableName, match => $"{match.Value[0]} {match.Value[1]}");
            var result = char.ToUpper(camelCasedName[0]) + camelCasedName.Substring(1);
            if (usedNames.Add(result))
                return result;
            return $"{shaderName} {result}";
        }

        public static string GetShaderName(this ParameterKey key)
        {
            var name = key.Name;
            var dotIndex = name.IndexOf('.');
            if (dotIndex > 0)
                return name.Substring(0, dotIndex);
            return string.Empty;
        }

        public static string GetVariableName(this ParameterKey key)
        {
            var name = key.Name;
            var dotIndex = name.IndexOf('.');
            if (dotIndex >= 0)
                return name.Substring(dotIndex + 1);
            return name;
        }

        public static bool TryParseEffect(string shaderName, IReadOnlyDictionary<string, ShaderViewModel> shaders, out ParsedShader result)
        {
            result = null;

            var resultRef = new ParsedShaderRef();
            var success = TryParseEffect(shaderName, shaders, resultRef);
            Debug.Assert(resultRef.ParentShaders.Count == 0);
            if (success)
                result = resultRef.ParsedShader;
            return success;
        }

        static object parserCacheLock = new object();
        internal static Dictionary<string, ParsedShader> parserCache = new Dictionary<string, ParsedShader>();
        
        public static void ResetParserCache(string shaderName = null)
        {
            lock (parserCacheLock)
            {
                if (!string.IsNullOrWhiteSpace(shaderName))
                {
                    parserCache.Remove(shaderName);
                }
                else
                {
                    parserCache.Clear();
                }
            }
        }

        private static bool TryParseEffect(string shaderName, IReadOnlyDictionary<string, ShaderViewModel> shaders, ParsedShaderRef resultRef)
        {
            lock (parserCacheLock)
            {
                if (parserCache.TryGetValue(shaderName, out var localResult))
                {
                    if (resultRef.ParsedShader == null)
                    {
                        resultRef.ParsedShader = localResult;
                    }
                    else
                    {
                        foreach (var parentShader in resultRef.ParentShaders)
                        {
                            parentShader.AddBaseShader(localResult);

                            // also add all base shaders of this base shader
                            foreach (var baseShader in localResult.BaseShaders)
                            {
                                parentShader.AddBaseShader(baseShader);
                            } 
                        }
                    }

                    return true;
                }

                try
                {

                    // SDSL
                    var macros = new[]
                    {
                            new ShaderMacro("class", "shader")
                    };

                    // get source code
                    var code = File.ReadAllText(shaders[shaderName].Path);
                    var inputFileName = shaderName + ".sdsl";

                    var parsingResult = StrideShaderParser.TryPreProcessAndParse(code, inputFileName, macros);

                    if (parsingResult.HasErrors || parsingResult.Shader.GetFirstClassDecl() == null)
                    {
                        return false;
                    }
                    else //success
                    {
                        localResult = new ParsedShader(parsingResult.Shader);

                        foreach (var parentShader in resultRef.ParentShaders)
                        {
                            parentShader.AddBaseShader(localResult);
                        }

                        // original shader
                        if (resultRef.ParsedShader == null)
                            resultRef.ParsedShader = localResult;

                        resultRef.ParentShaders.Push(localResult);
                        try
                        {
                            // base shaders
                            var baseShaders = localResult.ShaderClass?.BaseClasses ?? Enumerable.Empty<TypeName>();
                            foreach (var baseClass in baseShaders)
                            {
                                var baseShaderName = baseClass.Name.Text;
                                TryParseEffect(baseShaderName, shaders, resultRef);
                            }
                        }
                        finally
                        {
                            resultRef.ParentShaders.Pop();
                        }
                        
                        parserCache[shaderName] = localResult;
                        return true;
                    }
                }
                catch (Exception)
                {
                    return false;
                } 
            }
        }


        static Lazy<EffectCompilerParameters> effectCompilerParameters = new Lazy<EffectCompilerParameters>(() =>
        {
            return new EffectCompilerParameters
            {
                Platform = GraphicsPlatform.Direct3D11,
                Profile = GraphicsProfile.Level_11_0,
                Debug = true,
                OptimizationLevel = 0,
            };
        });
    }
}
