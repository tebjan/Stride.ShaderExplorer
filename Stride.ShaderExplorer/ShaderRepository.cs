using System.Collections.Generic;
using System.Linq;

namespace StrideShaderExplorer
{
    /// <summary>
    /// Repository for shader data and member lookup.
    /// </summary>
    public class ShaderRepository
    {
        private readonly Dictionary<string, ShaderViewModel> _shaders = new();
        private readonly Dictionary<string, Dictionary<ShaderViewModel, MemberList>> _members = new();

        public IReadOnlyDictionary<string, ShaderViewModel> Shaders => _shaders;
        public int Count => _shaders.Count;

        public void Clear()
        {
            _shaders.Clear();
            _members.Clear();
        }

        public void AddShader(string name, ShaderViewModel shader)
        {
            _shaders[name] = shader;
        }

        public bool TryGetShader(string name, out ShaderViewModel shader)
        {
            return _shaders.TryGetValue(name, out shader);
        }

        public bool ContainsShader(string name)
        {
            return _shaders.ContainsKey(name);
        }

        public void RegisterMember(string memberName, ShaderViewModel shader, MemberViewModel member)
        {
            if (!_members.TryGetValue(memberName, out var candidates))
            {
                candidates = new Dictionary<ShaderViewModel, MemberList>();
                _members[memberName] = candidates;
            }

            if (!candidates.TryGetValue(shader, out var memberList))
            {
                memberList = new MemberList();
                candidates[shader] = memberList;
            }

            memberList.Add(member);
        }

        public bool FindMember(string name, ShaderViewModel shader,
            out MemberList mems, out List<ShaderViewModel> scopedShaders)
        {
            mems = null;
            scopedShaders = null;

            if (!_members.TryGetValue(name, out var memberCandidates))
                return false;

            // Defined locally?
            memberCandidates.TryGetValue(shader, out mems);

            // Find base shaders that define the member
            scopedShaders = new List<ShaderViewModel>();
            var definingShader = shader;

            foreach (var baseShader in shader.BaseShaders)
            {
                if (memberCandidates.TryGetValue(baseShader, out var ms))
                {
                    if (definingShader.BaseShaders.Contains(baseShader))
                        mems = ms;
                    scopedShaders.Add(baseShader);
                }
            }

            // Also add derived shaders with overrides
            foreach (var derivedShader in shader.DerivedShaders)
            {
                if (memberCandidates.ContainsKey(derivedShader))
                    scopedShaders.Add(derivedShader);
            }

            return true;
        }
    }
}
