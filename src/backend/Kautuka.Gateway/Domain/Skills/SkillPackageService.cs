using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Kautuka.Gateway.Domain.AgentFiles;
using Kautuka.Gateway.Domain.Packages;

namespace Kautuka.Gateway.Domain.Skills;

internal sealed partial class SkillPackageService : ISkillPackageService
{
    private const string skillsDirectoryName = "skills";
    private static readonly StringComparison pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private const string instructionsFileName = "SKILL.md";
    internal const long MaxCompressedArchiveBytes = 50L * 1024 * 1024;
    private static readonly ArchiveIngestionLimits archiveLimits = new(
        MaxCompressedBytes: MaxCompressedArchiveBytes,
        MaxEntries: 512,
        MaxEntryBytes: 16L * 1024 * 1024,
        MaxExpandedBytes: 100L * 1024 * 1024,
        MaxCompressionRatio: 100,
        MaxProcessingTime: TimeSpan.FromSeconds(30));
    private readonly KautukaDatabase database;
    private readonly string temporaryRoot;

    public SkillPackageService(KautukaDatabase database)
        : this(database, Path.GetTempPath())
    {
    }

    internal SkillPackageService(KautukaDatabase database, string temporaryRoot)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        this.temporaryRoot = Path.GetFullPath(temporaryRoot ?? throw new ArgumentNullException(nameof(temporaryRoot)));
    }

    public async Task<SkillPackageImportOutcome> ValidateImportOutcomeAsync(Stream archiveStream, SkillPackageImportRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<SkillPackageInspection> inspections = await ProcessArchiveAsync(archiveStream, request, persist: false, cancellationToken).ConfigureAwait(false);
        return ToOutcome(inspections);
    }

    public async Task<SkillPackageImportOutcome> ImportOutcomeAsync(Stream archiveStream, SkillPackageImportRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<SkillPackageInspection> inspections = await ProcessArchiveAsync(archiveStream, request, persist: true, cancellationToken).ConfigureAwait(false);
        return ToOutcome(inspections);
    }

    public SkillPackageInspection Inspect(SkillDefinition skill)
    {
        string packageRoot = ResolveSkillPackageRoot(skill);
        string instructionsPath = Path.Combine(packageRoot, instructionsFileName);
        if (!File.Exists(instructionsPath))
        {
            throw new FileNotFoundException($"Skill instructions file was not found for '{skill.SkillId}'.", Path.Combine(packageRoot, instructionsFileName));
        }

        if (!WorkspaceFile.TryReadUnderRoot(packageRoot, instructionsFileName, 16L * 1024L * 1024L, out byte[]? instructionBytes, out _, rejectEmpty: false) || instructionBytes is null)
        {
            throw new InvalidOperationException($"Skill instructions file is not accessible for '{skill.SkillId}'.");
        }

        string instructions = DecodeUtf8Text(instructionBytes);

        // Self-heal: packages imported before script generation was introduced may have no scripts/ directory.
        // EnsureGeneratedScript is idempotent — it is a no-op when scripts already exist.
        EnsureGeneratedScript(packageRoot, instructions);

        List<string> scriptPaths = EnumerateRelativeFiles(Path.Combine(packageRoot, "scripts"));
        return new SkillPackageInspection(
            skill,
            instructions,
            EnumerateDiscoveredResourcePaths(packageRoot),
            scriptPaths,
            BuildScriptInspections(scriptPaths, instructions));
    }

    public string ReadResourceText(SkillDefinition skill, string resourcePath)
    {
        string normalizedPath = resourcePath.Replace('\\', '/').TrimStart('/');
        if (!(normalizedPath.StartsWith("references/", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Resource path '{resourcePath}' is not in a supported resource directory.");
        }

        string packageRoot = ResolveSkillPackageRoot(skill);
        if (!WorkspaceFile.TryReadUnderRoot(packageRoot, normalizedPath, 16L * 1024L * 1024L, out byte[]? bytes, out _, rejectEmpty: false) || bytes is null)
        {
            throw new InvalidOperationException($"Resource path '{resourcePath}' is not accessible.");
        }

        return DecodeUtf8Text(bytes);
    }

    public string ReadScriptText(SkillDefinition skill, string relativeScriptPath)
    {
        string packageRoot = ResolveSkillPackageRoot(skill);
        string relative = "scripts/" + relativeScriptPath.Replace('\\', '/').TrimStart('/');
        if (!WorkspaceFile.TryReadUnderRoot(packageRoot, relative, 16L * 1024L * 1024L, out byte[]? bytes, out _, rejectEmpty: false) || bytes is null)
        {
            throw new InvalidOperationException($"Skill '{skill.SkillId}' script '{relativeScriptPath}' was not found.");
        }

        return DecodeUtf8Text(bytes);
    }

    public string StagePackageForWorkspace(SkillDefinition skill, string workspaceHostPath, string containerWorkspacePath)
    {
        if (!TryGetOwnedPackageDirectory(skill.SkillId, out _))
        {
            throw new InvalidOperationException($"Skill '{skill.SkillId}' package path is not available.");
        }

        string workspaceRoot = Path.GetFullPath(workspaceHostPath);
        string stagedHostRoot = Path.Combine(workspaceRoot, ".kautuka", skillsDirectoryName, skill.SkillId);
        string stagedFull = Path.GetFullPath(stagedHostRoot);
        string expectedRoot = Path.GetFullPath(Path.Combine(workspaceRoot, ".kautuka", skillsDirectoryName));
        string? stagedParent = Path.GetDirectoryName(stagedFull);
        if (stagedParent is null
            || !string.Equals(Path.GetFullPath(stagedParent), expectedRoot, pathComparison)
            || !string.Equals(Path.GetFileName(stagedFull), skill.SkillId, StringComparison.Ordinal)
            || WorkspaceReparseGuard.ContainsReparsePoint(workspaceRoot, stagedFull))
        {
            throw new InvalidOperationException($"Skill '{skill.SkillId}' package path is not available.");
        }

        string markerPath = Path.Combine(stagedFull, ".packagehash");
        string? existingHash = File.Exists(markerPath) ? File.ReadAllText(markerPath).Trim() : null;
        if (!string.Equals(existingHash, skill.PackageHash, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(stagedFull))
            {
                Directory.Delete(stagedFull, recursive: true);
            }

            CopyDirectory(ResolveSkillPackageRoot(skill), stagedFull);
            Directory.CreateDirectory(stagedFull);
            File.WriteAllText(markerPath, skill.PackageHash);
        }

        return $"{containerWorkspacePath.TrimEnd('/')}/.kautuka/{skillsDirectoryName}/{skill.SkillId}";
    }

    public string ConfinePackagePath(string skillId, string? requestedPath, string? existingPath)
    {
        if (!TryGetOwnedPackageDirectory(skillId, out string canonical))
        {
            return string.Empty;
        }

        if (HasDifferentCaseSkillDirectory(skillId))
        {
            return string.Empty;
        }

        if (SamePackagePath(requestedPath, canonical) || SamePackagePath(existingPath, canonical))
        {
            return canonical;
        }

        return string.Empty;
    }

    public void DeletePackage(string skillId, string packagePath)
    {
        if (!TryGetOwnedPackageDirectory(skillId, out string canonical)
            || !SamePackagePath(packagePath, canonical)
            || HasDifferentCaseSkillDirectory(skillId)
            || WorkspaceReparseGuard.HasReparsePoint(canonical))
        {
            return;
        }

        if (Directory.Exists(canonical))
        {
            Directory.Delete(canonical, recursive: true);
        }
    }

    private bool TryGetOwnedPackageDirectory(string skillId, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(skillId)
            || !string.Equals(skillId, Path.GetFileName(skillId), StringComparison.Ordinal)
            || skillId is "." or "..")
        {
            return false;
        }

        string skillsRoot = Path.GetFullPath(Path.Combine(database.DataDirectory, skillsDirectoryName));
        canonical = Path.GetFullPath(Path.Combine(skillsRoot, skillId));
        string? parent = Path.GetDirectoryName(canonical);
        if (parent is null
            || !string.Equals(Path.GetFullPath(parent), skillsRoot, pathComparison)
            || !string.Equals(Path.GetFileName(canonical), skillId, StringComparison.Ordinal))
        {
            canonical = string.Empty;
            return false;
        }

        return true;
    }

    private bool HasDifferentCaseSkillDirectory(string skillId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string skillsRoot = Path.GetFullPath(Path.Combine(database.DataDirectory, skillsDirectoryName));
        if (!Directory.Exists(skillsRoot))
        {
            return false;
        }

        foreach (string existing in Directory.EnumerateDirectories(skillsRoot))
        {
            string name = Path.GetFileName(existing);
            if (string.Equals(name, skillId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, skillId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SamePackagePath(string? candidate, string canonical)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(candidate), canonical, pathComparison);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<SkillPackageInspection>> ProcessArchiveAsync(
        Stream archiveStream,
        SkillPackageImportRequest request,
        bool persist,
        CancellationToken cancellationToken)
    {
        string tempDirectory = Path.Combine(temporaryRoot, $"kautuka-skill-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        using var ingestion = new BoundedZipArchive(archiveLimits, cancellationToken);
        try
        {
            string archivePath = Path.Combine(tempDirectory, "package.zip");
#pragma warning disable CA2007 // await using on FileStream — no SynchronizationContext in ASP.NET Core
            await using (FileStream file = File.Create(archivePath))
            {
                await ingestion.CopyUploadAsync(archiveStream, file).ConfigureAwait(false);
            }
#pragma warning restore CA2007
#pragma warning disable CA1849 // ZipFile.OpenRead — no async overload in current API surface
            using var archive = ZipFile.OpenRead(archivePath);
#pragma warning restore CA1849
            IReadOnlyList<ZipArchiveEntry> fileEntries = ingestion.Validate(archive, "skill package");

#pragma warning disable CA2007 // await using on FileStream — no SynchronizationContext in ASP.NET Core
            await using FileStream hashStream = File.OpenRead(archivePath);
#pragma warning restore CA2007
#pragma warning disable CA1308 // Normalize strings to uppercase — hash stored as lowercase hex by convention
            string packageHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, ingestion.CancellationToken).ConfigureAwait(false)).ToLowerInvariant();
#pragma warning restore CA1308

            string extractRoot = Path.Combine(tempDirectory, "extract");
            Directory.CreateDirectory(extractRoot);
            string packagePrefix = DetectPackagePrefix(fileEntries);
            foreach (ZipArchiveEntry entry in fileEntries)
            {
                string normalizedPath = StripPackagePrefix(NormalizeEntryPath(entry.FullName), packagePrefix);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    continue;
                }

                string destinationPath = ingestion.ResolveDestination(extractRoot, normalizedPath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
#pragma warning disable CA2007 // await using on Stream — no SynchronizationContext in ASP.NET Core
                await using FileStream destination = File.Create(destinationPath);
#pragma warning restore CA2007
                await ingestion.CopyEntryAsync(entry, destination, "skill package").ConfigureAwait(false);
            }

            return ProcessExtractedPackage(extractRoot, packagePrefix, request, packageHash, persist);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static string DetectPackagePrefix(IReadOnlyList<ZipArchiveEntry> fileEntries)
    {
        // Only treat the archive as wrapped when every entry shares the same top-level directory.
        // A single root file like SKILL.md must not be interpreted as a wrapper prefix.
        bool hasNestedPaths = fileEntries
            .Select(entry => NormalizeEntryPath(entry.FullName))
            .Any(path => path.Contains('/', StringComparison.Ordinal));
        if (!hasNestedPaths)
        {
            return string.Empty;
        }

        HashSet<string> topLevelSegments = new(
            fileEntries
                .Select(entry => NormalizeEntryPath(entry.FullName))
                .Select(GetTopLevelSegment)
                .Where(static segment => !string.IsNullOrWhiteSpace(segment)),
            StringComparer.OrdinalIgnoreCase);

        if (topLevelSegments.Count != 1)
        {
            return string.Empty;
        }

        return topLevelSegments.First();
    }

    private static string GetTopLevelSegment(string path)
    {
        int separatorIndex = path.IndexOf('/', StringComparison.Ordinal);
        return separatorIndex < 0 ? path : path[..separatorIndex];
    }

    private static string StripPackagePrefix(string path, string packagePrefix)
    {
        if (string.IsNullOrWhiteSpace(packagePrefix))
        {
            return path;
        }

        string prefix = packagePrefix.Trim('/');
        if (string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string prefixWithSlash = $"{prefix}/";
        return path.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase)
            ? path[prefixWithSlash.Length..]
            : path;
    }

    private static string NormalizeEntryPath(string fullName)
        => BoundedZipArchive.NormalizeEntryPath(fullName);

    private static string NormalizeSkillId(string candidate)
    {
        string trimmed = candidate.Trim();
        var builder = new StringBuilder(trimmed.Length);
        foreach (char character in trimmed)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');
        }

        string normalized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? $"skill-{Guid.NewGuid():N}" : normalized;
    }

    private static SkillPackageImportOutcome ToOutcome(IReadOnlyList<SkillPackageInspection> inspections)
    {
        if (inspections.Count == 0)
        {
            throw new InvalidOperationException("Skill package did not produce any importable skill.");
        }

        return inspections.Count == 1
            ? new SkillPackageImportOutcome(false, inspections[0], inspections)
            : new SkillPackageImportOutcome(true, null, inspections);
    }

    private IReadOnlyList<SkillPackageInspection> ProcessExtractedPackage(string extractRoot, string packagePrefix, SkillPackageImportRequest request, string packageHash, bool persist)
    {
        string rootInstructionPath = Path.Combine(extractRoot, instructionsFileName);
        bool hasRootSkill = File.Exists(rootInstructionPath);
        List<string> childSkillDirectories = [.. Directory.EnumerateDirectories(extractRoot)
            .Where(path => File.Exists(Path.Combine(path, instructionsFileName)))
            .Order(StringComparer.OrdinalIgnoreCase)];

        if (hasRootSkill)
        {
            if (childSkillDirectories.Count > 0)
            {
                throw new InvalidOperationException("Mixed package layout is not supported. Use either a single skill root or multiple child skill directories.");
            }

            string expectedDirectoryName = string.IsNullOrWhiteSpace(packagePrefix)
                ? string.Empty
                : packagePrefix;
            bool enforceDirectoryMatch = !string.IsNullOrWhiteSpace(packagePrefix);
            SkillPackageInspection rootSkill = ProcessSkillDirectory(extractRoot, expectedDirectoryName, enforceDirectoryMatch, request, packageHash, persist);
            return [rootSkill];
        }

        if (childSkillDirectories.Count == 1)
        {
            string skillDirectory = childSkillDirectories[0];
            string expectedDirectoryName = Path.GetFileName(skillDirectory);
            SkillPackageInspection single = ProcessSkillDirectory(skillDirectory, expectedDirectoryName, true, request, packageHash, persist);
            return [single];
        }

        if (childSkillDirectories.Count > 1)
        {
            RejectBundleRootSharedFiles(extractRoot, childSkillDirectories);
            return [.. childSkillDirectories.Select(path => ProcessSkillDirectory(path, Path.GetFileName(path), true, request, packageHash, persist))];
        }

        throw new InvalidOperationException("Skill package must contain one or more child skill directories, each with SKILL.md.");
    }

    private static void RejectBundleRootSharedFiles(string extractRoot, IReadOnlyList<string> childSkillDirectories)
    {
        var allowedDirectoryNames = new HashSet<string>(
            childSkillDirectories.Select(Path.GetFileName).Where(static name => name is not null).Select(static name => name!),
            StringComparer.OrdinalIgnoreCase);
        bool hasRootFiles = Directory.EnumerateFiles(extractRoot, "*", SearchOption.TopDirectoryOnly).Any();
        if (hasRootFiles)
        {
            throw new InvalidOperationException("Bundle imports must be self-contained per skill directory. Root-level shared files are not supported.");
        }

        bool hasUnexpectedDirectories = Directory.EnumerateDirectories(extractRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Any(name => name is not null && !allowedDirectoryNames.Contains(name));
        if (hasUnexpectedDirectories)
        {
            throw new InvalidOperationException("Bundle imports must contain only child skill directories at the archive root.");
        }
    }

    private SkillPackageInspection ProcessSkillDirectory(string skillDirectory, string expectedDirectoryName, bool enforceDirectoryMatch, SkillPackageImportRequest request, string packageHash, bool persist)
    {
        string instructionsPath = Path.Combine(skillDirectory, instructionsFileName);
        if (!File.Exists(instructionsPath))
        {
            throw new InvalidOperationException($"Skill directory '{expectedDirectoryName}' is missing SKILL.md.");
        }

        if (!WorkspaceFile.TryReadUnderRoot(skillDirectory, instructionsFileName, 16L * 1024L * 1024L, out byte[]? instructionBytes, out _, rejectEmpty: false) || instructionBytes is null)
        {
            throw new InvalidOperationException($"Skill directory '{expectedDirectoryName}' instructions are not accessible.");
        }

        string instructions = DecodeUtf8Text(instructionBytes);
        SkillFrontmatter frontmatter = ParseFrontmatter(instructions);
        if (enforceDirectoryMatch)
        {
            ValidateFrontmatterNameMatchesDirectory(frontmatter.Name, expectedDirectoryName);
        }

        string skillId = NormalizeSkillId(frontmatter.Name);
        string name = frontmatter.Name;
        string description = frontmatter.Description;

        EnsureGeneratedScript(skillDirectory, instructions);
        List<string> resourcePaths = EnumerateDiscoveredResourcePaths(skillDirectory);
        List<string> scriptPaths = EnumeratePackageSectionFiles(skillDirectory, "scripts");
        IReadOnlyList<SkillScriptInspection> scriptInspections = BuildScriptInspections(scriptPaths, instructions);

        string packageRoot = Path.Combine(database.DataDirectory, skillsDirectoryName, skillId);
        if (persist)
        {
            if (Directory.Exists(packageRoot))
            {
                Directory.Delete(packageRoot, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(packageRoot)!);
            CopyDirectory(skillDirectory, packageRoot);
        }

        var sourceMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(frontmatter.License))
        {
            sourceMetadata["license"] = frontmatter.License;
        }

        if (!string.IsNullOrWhiteSpace(frontmatter.Compatibility))
        {
            sourceMetadata["compatibility"] = frontmatter.Compatibility;
        }

        if (!string.IsNullOrWhiteSpace(frontmatter.AllowedTools))
        {
            sourceMetadata["allowed-tools"] = frontmatter.AllowedTools;
        }

        foreach ((string key, string value) in frontmatter.Metadata)
        {
            sourceMetadata[$"metadata.{key}"] = value;
        }

        if (request.SourceMetadata is not null)
        {
            foreach ((string key, string value) in request.SourceMetadata)
            {
                sourceMetadata[key] = value;
            }
        }

        SkillDefinition definition = new()
        {
            SkillId = skillId,
            Name = name,
            Description = description,
            ImportTimestamp = DateTimeOffset.UtcNow,
            PackageHash = packageHash,
            Status = "ready",
            PackagePath = packageRoot,
            HasResources = resourcePaths.Count > 0,
            HasScripts = scriptPaths.Count > 0,
            SourceMetadata = sourceMetadata,
        };

        return new SkillPackageInspection(definition, instructions, resourcePaths, scriptPaths, scriptInspections);
    }

    private static string DecodeUtf8Text(byte[] bytes)
    {
        ReadOnlySpan<byte> content = bytes;
        ReadOnlySpan<byte> utf8Bom = [0xEF, 0xBB, 0xBF];
        if (content.StartsWith(utf8Bom))
        {
            content = content[utf8Bom.Length..];
        }

        return Encoding.UTF8.GetString(content);
    }

    private static SkillFrontmatter ParseFrontmatter(string instructions)
    {
        string normalized = instructions.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SKILL.md must start with YAML frontmatter.");
        }

        int closingIndex = normalized.IndexOf("\n---\n", "---\n".Length, StringComparison.Ordinal);
        if (closingIndex < 0)
        {
            throw new InvalidOperationException("SKILL.md frontmatter is missing a closing '---' line.");
        }

        string frontmatter = normalized["---\n".Length..closingIndex];
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? name = null;
        string? description = null;
        string? license = null;
        string? compatibility = null;
        string? allowedTools = null;
        bool inMetadata = false;

        foreach (string rawLine in frontmatter.Split('\n'))
        {
            string line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            bool isIndented = rawLine.StartsWith("  ", StringComparison.Ordinal);
            if (inMetadata && isIndented)
            {
                string metadataLine = line.Trim();
                int separator = metadataLine.IndexOf(':', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    continue;
                }

                string key = metadataLine[..separator].Trim();
                string value = Unquote(metadataLine[(separator + 1)..].Trim());
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    metadata[key] = value;
                }

                continue;
            }

            inMetadata = false;
            int rootSeparator = line.IndexOf(':', StringComparison.Ordinal);
            if (rootSeparator <= 0)
            {
                continue;
            }

            string keyName = line[..rootSeparator].Trim();
            string parsedValue = Unquote(line[(rootSeparator + 1)..].Trim());

            switch (keyName)
            {
                case "name":
                    name = parsedValue;
                    break;
                case "description":
                    description = parsedValue;
                    break;
                case "license":
                    license = parsedValue;
                    break;
                case "compatibility":
                    compatibility = parsedValue;
                    break;
                case "allowed-tools":
                    allowedTools = parsedValue;
                    break;
                case "metadata":
                    inMetadata = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException("SKILL.md frontmatter must include non-empty 'name' and 'description'.");
        }

        ValidateSkillName(name);
        if (description.Length > 1024)
        {
            throw new InvalidOperationException("SKILL.md frontmatter 'description' must be 1024 characters or fewer.");
        }

        return new SkillFrontmatter(name, description, license, compatibility, allowedTools, metadata);
    }

    private static void ValidateSkillName(string name)
    {
        if (name.Length is < 1 or > 64)
        {
            throw new InvalidOperationException("SKILL.md frontmatter 'name' must be between 1 and 64 characters.");
        }

        if (name.StartsWith('-') || name.EndsWith('-') || name.Contains("--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SKILL.md frontmatter 'name' must not start/end with '-' or contain consecutive hyphens.");
        }

        if (!Regex.IsMatch(name, "^[a-z0-9-]+$", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("SKILL.md frontmatter 'name' must use lowercase letters, digits, and hyphens only.");
        }
    }

    private static void ValidateFrontmatterNameMatchesDirectory(string frontmatterName, string directoryName)
    {
        string normalizedDirectoryName = NormalizeSkillId(directoryName);
        if (!string.Equals(frontmatterName, normalizedDirectoryName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"SKILL.md frontmatter 'name' ({frontmatterName}) must match the parent directory name ({normalizedDirectoryName}).");
        }
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            bool quoted = (value.StartsWith('"') && value.EndsWith('"'))
                || (value.StartsWith('\'') && value.EndsWith('\''));
            if (quoted)
            {
                return value[1..^1].Trim();
            }
        }

        return value.Trim();
    }

    private static List<string> EnumerateDiscoveredResourcePaths(string packageRoot)
    {
        List<string> references = EnumeratePackageSectionFiles(packageRoot, "references");
        List<string> assets = EnumeratePackageSectionFiles(packageRoot, "assets");
        references.AddRange(assets);
        return references;
    }

    private static List<string> EnumerateRelativeFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return [.. WorkspaceReparseGuard.EnumerateFiles(root)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(static path => path is not "." and not ".." && !path.StartsWith("../", StringComparison.Ordinal))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static List<string> EnumeratePackageSectionFiles(string packageRoot, string sectionName)
        => EnumerateRelativeFiles(Path.Combine(packageRoot, sectionName))
            .Select(path => $"{sectionName}/{path}")
            .ToList();

    private static void EnsureGeneratedScript(string packageRoot, string instructions)
    {
        string scriptsRoot = Path.Combine(packageRoot, "scripts");
        if (WorkspaceReparseGuard.HasReparsePoint(scriptsRoot))
        {
            return;
        }

        if (Directory.Exists(scriptsRoot) && WorkspaceReparseGuard.EnumerateFiles(scriptsRoot).Any())
        {
            return;
        }

        string? scriptBody = TryExtractShellScript(instructions, out _);
        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            return;
        }

        Directory.CreateDirectory(scriptsRoot);
        string scriptPath = Path.Combine(scriptsRoot, "generated-from-markdown.sh");
        File.WriteAllText(scriptPath, BuildGeneratedShellScript(scriptBody), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string? TryExtractShellScript(string instructions, out IReadOnlyList<string> parameterNames)
    {
        parameterNames = [];
        Match match = Regex.Match(
            instructions,
            "```(?:bash|sh)\\s*\\r?\\n(?<code>[\\s\\S]*?)```",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            return null;
        }

        string code = match.Groups["code"].Value.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        parameterNames = ExtractPlaceholderNames(code);

        var placeholderIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int nextIndex = 1;
        return Regex.Replace(code, "<([a-zA-Z0-9_-]+)>", placeholderMatch =>
        {
            string placeholderName = placeholderMatch.Groups[1].Value;
            if (!placeholderIndexes.TryGetValue(placeholderName, out int argumentIndex))
            {
                argumentIndex = nextIndex;
                placeholderIndexes[placeholderName] = argumentIndex;
                nextIndex += 1;
            }

            return $"${{{argumentIndex}:?missing {placeholderName}}}";
        });
    }

    private static IReadOnlyList<SkillScriptInspection> BuildScriptInspections(IReadOnlyList<string> scriptPaths, string instructions)
    {
        _ = TryExtractShellScript(instructions, out IReadOnlyList<string> generatedScriptParameters);

        return [.. scriptPaths.Select(path => new SkillScriptInspection(
            path,
            IsGeneratedMarkdownScript(path) ? generatedScriptParameters : []))];
    }

    private static bool IsGeneratedMarkdownScript(string path)
        => path.EndsWith("generated-from-markdown.sh", StringComparison.OrdinalIgnoreCase);

    private static List<string> ExtractPlaceholderNames(string code)
    {
        var parameterNames = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(code, "<([a-zA-Z0-9_-]+)>", RegexOptions.CultureInvariant))
        {
            string parameterName = match.Groups[1].Value;
            if (seen.Add(parameterName))
            {
                parameterNames.Add(parameterName);
            }
        }

        return parameterNames;
    }

    private static string BuildGeneratedShellScript(string scriptBody)
        => $"#!/usr/bin/env bash\nset -euo pipefail\n\n{scriptBody}\n";

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        string fullSource = Path.GetFullPath(sourceRoot);
        string fullDestination = Path.GetFullPath(destinationRoot);
        CopyDirectoryCore(fullSource, fullDestination, fullDestination);
    }

    private static void CopyDirectoryCore(string source, string destination, string destinationRoot)
    {
        if (WorkspaceReparseGuard.HasReparsePoint(source))
        {
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (FileSystemInfo entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (WorkspaceReparseGuard.HasReparsePoint(entry.FullName))
            {
                continue;
            }

            string target = Path.GetFullPath(Path.Combine(destination, entry.Name));
            string destinationPrefix = destinationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry is DirectoryInfo)
            {
                CopyDirectoryCore(entry.FullName, target, destinationRoot);
                continue;
            }

            try
            {
                using FileStream sourceStream = WorkspaceFile.OpenRead(entry.FullName);
                using FileStream targetStream = new(target, FileMode.Create, FileAccess.Write, FileShare.None);
                sourceStream.CopyTo(targetStream);
            }
            catch (IOException)
            {
                continue;
            }
        }
    }

    private string ResolveSkillPackageRoot(SkillDefinition skill)
    {
        if (!TryGetOwnedPackageDirectory(skill.SkillId, out string canonical))
        {
            throw new InvalidOperationException($"Skill '{skill.SkillId}' package path is not available.");
        }

        if (HasDifferentCaseSkillDirectory(skill.SkillId))
        {
            throw new InvalidOperationException($"Skill '{skill.SkillId}' package path is not available.");
        }

        return canonical;
    }

    private sealed record SkillFrontmatter(
        string Name,
        string Description,
        string? License,
        string? Compatibility,
        string? AllowedTools,
        IReadOnlyDictionary<string, string> Metadata);
}
