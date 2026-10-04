using System.Collections.Generic;

namespace U3D.Editor
{
    public interface IToolCategory
    {
        string CategoryName { get; }
        List<CreatorTool> GetTools();
        void DrawCategory();

        // Add navigation support
        System.Action<int> OnRequestTabSwitch { get; set; }
    }
}