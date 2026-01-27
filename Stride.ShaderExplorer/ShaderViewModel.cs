using CommunityToolkit.Mvvm.ComponentModel;
using Stride.ShaderParser;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace StrideShaderExplorer
{
    public class ShaderViewModel : ObservableObject
    {
        private bool _isExpanded = false;
        private bool _isVisible = true;
        private MemberList _selectedMember;

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set { SetProperty(ref _isExpanded, value); }
        }

        public bool IsVisible
        {
            get { return _isVisible; }
            set { SetProperty(ref _isVisible, value); }
        }

        public string Name { get; set; }

        public string Path { get; set; }

        string text;
        public string Text
        {
            get
            {
                if (text == null)
                {
                    text = File.ReadAllText(Path);
                }

                return text;
            }
        }

        public ParsedShader ParsedShader { get; set; }

        public List<ShaderViewModel> DerivedShaders { get; } = new List<ShaderViewModel>();

        public List<ShaderViewModel> BaseShaders { get; } = new List<ShaderViewModel>();

        public List<ShaderViewModel> TreeViewChildren { get; } = new List<ShaderViewModel>();

        public MemberList SelectedMember
        {
            get { return _selectedMember; }
            set
            {
                if (SetProperty(_selectedMember, value, v => _selectedMember = v))
                {
                }
            }
        }

        List<ShaderViewModel> _scopedShaders;
        public List<ShaderViewModel> ScopedShaders
        {
            get { return _scopedShaders; }
            set
            {
                if (SetProperty(_scopedShaders, value, v => _scopedShaders = v))
                {
                }
            }
        }

        public override string ToString() => ToString(0);

        public string ToString(int depth)
        {
            return Name + string.Join("", BaseShaders
                .Select(o => "\r\n" + "".PadLeft(depth * 4) + o.ToString(depth + 1)));
        }
    }
}
