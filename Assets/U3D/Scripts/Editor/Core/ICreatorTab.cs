namespace U3D.Editor
{
    public interface ICreatorTab
    {
        string TabName { get; }
        bool IsComplete { get; }
        void Initialize();
        void DrawTab();

        // Add navigation callback
        System.Action<int> OnRequestTabSwitch { get; set; }
    }
}