namespace KronosScreenRemote;

// Settings > Sample Editor > "Create Zone Preferences" - controls where
// SampleEditorViewModel.AddPlaceholderZone places a new zone relative to the current
// last zone.
public enum SampleZoneCreatePosition
{
    // New zone is appended ABOVE the current last zone's own Top Key, claiming
    // previously-unassigned range - the existing last zone is left completely
    // unchanged. Only falls back to carving space from the existing last zone's own
    // top (shrinking its Top Key) when it's already at 127, the top of the MIDI
    // range, with no unassigned range left above it to append into.
    Right,
    // New zone takes the BOTTOM (lower-key) portion of the current last zone's own
    // range instead; the existing last zone keeps the top portion of its own former
    // range (its Top Key is unchanged, its effective low end moves up).
    Left,
}

public enum SampleZoneOriginalKeyPosition
{
    Bottom,
    Center,
    Top,
}
