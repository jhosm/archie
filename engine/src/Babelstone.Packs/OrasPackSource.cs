using System.Formats.Tar;

namespace Babelstone.Packs;

/// <summary>
/// Pulls a pack by digest with <c>oras</c> (ADR-PC-007 §P2), at load time only. Mirrors the
/// command packs/pack.sh uses: <c>oras pull [--oci-layout] &lt;ref&gt;@&lt;digest&gt; -o &lt;tmp&gt;</c>,
/// then reads the single <c>pack.tar</c> layer and extracts its members by relative path.
/// </summary>
/// <param name="useOciLayout">
/// true for the fully-offline OCI-layout form (dev/CI — <see cref="ociRef"/> is a layout dir);
/// false for a real registry reference (production). Digest semantics are identical either way.
/// </param>
/// <param name="orasExecutable">The oras binary; defaults to PATH lookup (the mise-pinned oras 1.3.2 in dev/CI).</param>
public sealed class OrasPackSource(bool useOciLayout = false, string orasExecutable = "oras") : IPackSource
{
    public async Task<IReadOnlyDictionary<string, byte[]>> PullByDigestAsync(
        string ociRef, string digest, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(ociRef);
        ArgumentException.ThrowIfNullOrEmpty(digest);

        var tempDir = Directory.CreateTempSubdirectory("babelstone-pack-");
        try
        {
            List<string> arguments = ["pull"];
            if (useOciLayout)
            {
                arguments.Add("--oci-layout");
            }

            arguments.Add($"{ociRef}@{digest}");
            arguments.AddRange(["-o", tempDir.FullName]);

            var result = await ProcessRunner.RunAsync(orasExecutable, arguments, ct);
            if (result.ExitCode != 0)
            {
                throw new PackLoadException(null, digest,
                    $"oras pull failed (exit {result.ExitCode}): {ProcessRunner.Tail(result.StdErr)}");
            }

            var tarPath = Path.Combine(tempDir.FullName, "pack.tar");
            if (!File.Exists(tarPath))
            {
                throw new PackLoadException(null, digest,
                    $"oras pull succeeded but produced no 'pack.tar' layer in the output directory.");
            }

            return ExtractTar(tarPath, digest);
        }
        finally
        {
            try
            {
                tempDir.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp dir; a leftover temp file must not fail a load.
            }
        }
    }

    private static IReadOnlyDictionary<string, byte[]> ExtractTar(string tarPath, string digest)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var stream = File.OpenRead(tarPath);
        using var reader = new TarReader(stream);

        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
            {
                continue;
            }

            // tar entries may be "./pack.yaml" or "pack.yaml" depending on how the layer was
            // packed; normalise to the bare relative path the parser keys on.
            var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
            using var buffer = new MemoryStream();
            entry.DataStream.CopyTo(buffer);
            files[name] = buffer.ToArray();
        }

        if (files.Count == 0)
        {
            throw new PackLoadException(null, digest, "the pulled pack.tar layer contained no files.");
        }

        return files;
    }
}
