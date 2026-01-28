using Stride.Core.Shaders.Ast;
using Stride.ShaderParser;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace StrideShaderExplorer
{
    /// <summary>
    /// Builds shader inheritance tree from .sdsl files.
    /// </summary>
    public class ShaderTreeBuilder
    {
        private readonly ShaderRepository _repository;

        public bool DirectParentsOnly { get; set; } = true;

        public ShaderTreeBuilder(ShaderRepository repository)
        {
            _repository = repository;
        }

        public List<ShaderViewModel> BuildTree(IEnumerable<string> paths)
        {
            _repository.Clear();
            var rootShaders = new List<ShaderViewModel>();

            // 1. Discover and create shader instances
            var files = paths
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .SelectMany(path => Directory.GetFiles(path, "*.sdsl", SearchOption.AllDirectories));

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!_repository.ContainsShader(name))
                    _repository.AddShader(name, new ShaderViewModel { Path = file, Name = name });
            }

            // 2. Parse and build relationships
            foreach (var shader in _repository.Shaders.Values)
            {
                if (!EffectUtils.TryParseEffect(shader.Name, _repository.Shaders, out var parsedShader))
                    continue;

                shader.ParsedShader = parsedShader;

                // Register members
                foreach (var member in parsedShader.ShaderClass.Members.OfType<IDeclaration>())
                {
                    var memberName = member.Name?.Text;
                    if (!string.IsNullOrWhiteSpace(memberName))
                        _repository.RegisterMember(memberName, shader, new MemberViewModel(memberName, member));
                }

                // Build inheritance relationships
                var baseShaderNames = parsedShader.BaseShaders
                    .Select(s => s.ShaderClass.Name.Text)
                    .ToList();

                if (baseShaderNames.Count == 0)
                {
                    rootShaders.Add(shader);
                    continue;
                }

                foreach (var baseName in baseShaderNames)
                {
                    if (!_repository.TryGetShader(baseName, out var baseShader))
                        continue;

                    shader.BaseShaders.Add(baseShader);
                    baseShader.DerivedShaders.Add(shader);

                    // Build tree view hierarchy
                    if (DirectParentsOnly)
                    {
                        if (parsedShader.ShaderClass.BaseClasses.Any(bc => bc.Name.Text == baseShader.Name))
                            baseShader.TreeViewChildren.Add(shader);
                    }
                    else
                    {
                        baseShader.TreeViewChildren.Add(shader);
                    }
                }
            }

            Debug.WriteLine($"Found {_repository.Count} shaders");
            return rootShaders.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
