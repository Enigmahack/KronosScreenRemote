using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KronosScreenRemote.ViewModels;

// Bindable tree node for the Sample Editor's Collection -> Multisample -> Zone
// hierarchy (Views/SampleEditorWindow.xaml's SampleTree). Deliberately lean compared
// to ObjectTreeNode - no bank/merge/dirty-conflict machinery, since a .KSC/.KMP/.KSF
// tree has none of that; disk state IS the truth, there's no local-vs-Kronos diff to
// render.
partial class SampleTreeNode : ObservableObject
{
    public string Label { get; }

    // Exactly one of these is non-null, identifying what kind of node this is and
    // what it points at. CollectionRef carries its own .KSC path (like MultisampleRef/
    // ZoneRef already do) so a caller can tell which of possibly SEVERAL open
    // collections a given root belongs to, now that opening a second .KSC adds another
    // root instead of replacing the first (see SampleEditorViewModel.RebuildTreeFromCollection).
    public (KscCollection Collection, string Path)? CollectionRef { get; }
    public (KmpMultisample Multisample, string Path)? MultisampleRef { get; }
    public (KmpZone Zone, string KmpPath)? ZoneRef { get; }

    [ObservableProperty] bool isExpanded;
    [ObservableProperty] bool isSelected;

    public ObservableCollection<SampleTreeNode> Children { get; } = [];

    public static SampleTreeNode ForCollection(string label, KscCollection collection, string path) =>
        new(label, collectionRef: (collection, path));

    public static SampleTreeNode ForMultisample(string label, KmpMultisample multisample, string path) =>
        new(label, multisampleRef: (multisample, path));

    public static SampleTreeNode ForZone(string label, KmpZone zone, string kmpPath) =>
        new(label, zoneRef: (zone, kmpPath));

    SampleTreeNode(string label, (KscCollection, string)? collectionRef = null,
        (KmpMultisample, string)? multisampleRef = null, (KmpZone, string)? zoneRef = null)
    {
        Label = label;
        CollectionRef = collectionRef;
        MultisampleRef = multisampleRef;
        ZoneRef = zoneRef;
    }
}
