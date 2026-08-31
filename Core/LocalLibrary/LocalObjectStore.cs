namespace KronosScreenRemote;

using System.IO;
using System.Security.Cryptography;

// Content-addressed blob store for full object bodies, at {root}/blobs/<hh>/<sha1>.bin
// (two-level fan-out so no single directory holds thousands of files). Every unique body
// is written once; Baseline and Current simply point at the same file for any untouched
// object, so dedup is free and there is no compaction/GC subsystem for v1 (an unreferenced
// blob left behind by a discard is a harmless orphan file, not a correctness problem).
static class LocalObjectStore
{
    // SHA-1 deliberately, and it is NOT a security boundary: this hash only answers "are these
    // two local bodies the same bytes" for dedup and dirty-detection, against content the user
    // themselves pulled or loaded. Nothing here defends against a chosen-prefix collision an
    // attacker would first have to get into the library by hand. Don't "harden" it to SHA-256 -
    // every existing blob filename, index.json entry and op-log target is a 40-char hex hash, so
    // a change is a store migration, not a one-line swap.
    public static string ComputeHash(byte[] body)
    {
        using var sha1 = SHA1.Create();
        return Convert.ToHexString(sha1.ComputeHash(body)).ToLowerInvariant();
    }

    static string PathFor(string root, string hash) =>
        Path.Combine(root, "blobs", hash[..2], hash + ".bin");

    // Idempotent - writing the same content twice is a no-op the second time.
    public static string Put(string root, byte[] body)
    {
        string hash = ComputeHash(body);
        string path = PathFor(root, hash);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, body);
        }
        return hash;
    }

    public static byte[]? TryGet(string root, string hash)
    {
        // LocalLibraryIndex.NoBaselineSentinel ("") means "no real hardware baseline exists
        // yet" (a brand-new local-only object) - not a real SHA-1 hash, so PathFor's hash[..2]
        // would throw on it. ChangesetBuilder's own Step 4 comment already documents the
        // expected behavior here: "null = ... nothing to back up."
        if (string.IsNullOrEmpty(hash)) return null;
        string path = PathFor(root, hash);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
