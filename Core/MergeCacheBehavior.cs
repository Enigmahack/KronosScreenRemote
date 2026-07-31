namespace KronosScreenRemote;

// The Librarian's Merge Window staging cache - settings-tab choice between never touching
// disk (cleared on restart) and surviving a crash/reboot (a plain snapshot file, rewritten on
// every mutation; see Core/LocalLibrary/MergeCachePersistence.cs).
public enum MergeCacheBehavior
{
    TemporaryMemory,
    LocalStorage,
}
