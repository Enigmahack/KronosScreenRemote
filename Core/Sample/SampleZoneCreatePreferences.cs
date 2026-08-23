namespace KronosScreenRemote;

// Settings > Sample Editor > "Create Zone Preferences" - controls how
// SampleEditorViewModel.AddPlaceholderZone carves a new zone's key range out of the
// current last zone's own range.
public enum SampleZoneCreatePosition
{
    // New zone takes the TOP (higher-key) portion of the carved range; the existing
    // last zone keeps the bottom portion and its OWN Top Key shrinks. This was the
    // only behavior before this setting existed.
    Right,
    // New zone takes the BOTTOM (lower-key) portion instead; the existing last zone
    // keeps the top portion of its own former range (its Top Key is unchanged, its
    // effective low end moves up).
    Left,
}

public enum SampleZoneOriginalKeyPosition
{
    Bottom,
    Center,
    Top,
}
