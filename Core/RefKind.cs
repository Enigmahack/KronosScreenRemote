namespace KronosScreenRemote;

using System.Text.Json;
using System.Text.Json.Serialization;

// The four reference shapes a Librarian object can hold. Previously two independently-named
// string vocabularies for the same four things - ObjectReferenceWalker emitted "timbre 3" /
// "drum track" / "osc1 zone2" / "slot 5" while LibraryCatalog emitted "combi_timbre" /
// "drum_track" / "osc1_zone2" / "setlist_slot" - and LibRefs.ApplyResolvedRef had to accept
// BOTH, dispatching on prefix. A misspelt or newly-invented kind string silently fell to the
// dispatcher's else branch (the Set List encoder) and wrote at the wrong offsets; the enum
// makes that unrepresentable.
//
// WHICH osc/zone or WHICH timbre a site is stays in the accompanying Site index, exactly as it
// always did - the string never carried information Site didn't already have.
[JsonConverter(typeof(RefKindJsonConverter))]
enum RefKind
{
    CombiTimbre,    // Combi timbre -> Program
    DrumTrack,      // Program drum track -> Program (Site is -1: a Program has exactly one)
    OscZone,        // HD-1 oscillator zone -> Drum Kit / Wave Sequence
    SetListSlot,    // Set List slot -> Program / Combi
}

static class RefKinds
{
    // The one display vocabulary, replacing both old string forms. Site-aware, so "osc1 zone2"
    // and "timbre 3" still read exactly as before wherever they were shown.
    public static string Describe(RefKind kind, int site) => kind switch
    {
        RefKind.CombiTimbre => $"timbre {site + 1}",
        RefKind.DrumTrack   => "drum track",
        RefKind.OscZone     => $"osc{site / LibRefs.ZonesPerOsc + 1} zone{site % LibRefs.ZonesPerOsc + 1}",
        _                   => $"slot {site + 1}",
    };

    // Both legacy vocabularies, in one place, for one purpose: reading a MergeCache snapshot
    // written before the enum existed. Nothing else parses kind strings any more - producers
    // emit the enum directly.
    public static RefKind ParseLegacy(string s) =>
        s.StartsWith("timbre", StringComparison.Ordinal) || s == "combi_timbre" ? RefKind.CombiTimbre
        : s is "drum track" or "drum_track"                                     ? RefKind.DrumTrack
        : s.StartsWith("osc", StringComparison.Ordinal)                         ? RefKind.OscZone
        : RefKind.SetListSlot;
}

// MergeRefSite.RefKind is persisted (Core/LocalLibrary/MergeCachePersistence.cs). Writes the
// enum name; reads either that or a pre-enum snapshot's kind string, so upgrading does not
// throw away a user's staged Merge Window contents.
sealed class RefKindJsonConverter : JsonConverter<RefKind>
{
    public override RefKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return (RefKind)reader.GetInt32();
        string s = reader.GetString() ?? "";
        return Enum.TryParse<RefKind>(s, out var k) ? k : RefKinds.ParseLegacy(s);
    }

    public override void Write(Utf8JsonWriter writer, RefKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
