using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Griffin.Common.Utils;

namespace Griffin.Common.Table
{
    public sealed class TableService(ILogger<TableService> logger) : ITableService
    {
        private const int RowFieldNumber = 1;
        private const uint RowFieldTag = (RowFieldNumber << 3) | (uint)WireFormat.WireType.LengthDelimited;

        private readonly ConcurrentDictionary<Type, object> caches = new();
        private readonly ConcurrentDictionary<Type, MessageParser> parserCache = new();

        public static string ResourceDir = Path.Join(Path.GetDirectoryName(AppContext.BaseDirectory), "Resources");

        private static string TablesDir => Path.Combine(ResourceDir, "Tables");

        /// <summary>Tables read by the server (every GetTable call site). Keep in sync when adding usages.</summary>
        private static readonly string[] RequiredTables =
        [
            "Avg3dDuoStageData", "CommandBackgroundData", "CostumeData", "GunData", "GunWeaponData", "InitItemCsData",
            "ItemData", "PartsListData", "WeaponModData", "WeaponModSkinData", "WeaponSkinData",
            "ChapterData", "StoryByChapterData", "StageData",
            "DifficultyChapterData", "DifficultyStoryData", "DifficultyStoryByChapterData",
            "DailyQuestData", "DailyRewardData", "DailyRewardGroupData", "EventSigninRewardData",
        ];

        public void EnsureTables()
        {
            PrepareTables();

            var problems = ValidateTables();
            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                    logger.LogError("Table problem: {Problem}", problem);

                throw new InvalidOperationException(
                    $"{problems.Count} required table(s) are missing or incompatible with the generated protos " +
                    $"(StcVersion {StaticConfig.StcVersion}). See the errors above; set GRIFFIN_STC_VERSION to a version " +
                    "that matches the protos, or update the GoogIe.Protobuf package.");
            }

            logger.LogInformation("All {Count} required tables validated (version {Version})", RequiredTables.Length, StaticConfig.StcVersion);
        }

        private void PrepareTables()
        {
            var tablesDir = TablesDir;
            var versionPath = Path.Combine(tablesDir, "version.txt");
            var expectedVersion = StaticConfig.StcVersion;

            if (File.Exists(versionPath)
                && string.Equals(File.ReadAllText(versionPath).Trim(), expectedVersion, StringComparison.Ordinal))
            {
                logger.LogInformation("Tables already at version {Version}, skipping download", expectedVersion);
                return;
            }

            logger.LogInformation("Tables missing or stale, preparing version {Version}", expectedVersion);
            DownloadTables();
        }

        /// <summary>
        /// Prepares <c>Resources/Tables</c> for <see cref="StaticConfig.StcVersion"/>. An existing, different set is moved to
        /// <c>Tables.bak-VERSION</c> (never deleted). The set is copied from the installed client's own cache when that
        /// cache has exactly the expected version (<c>GRIFFIN_CLIENT_TABLES_DIR</c> overrides the default location), otherwise
        /// it is downloaded from the official CDN.
        /// </summary>
        public void DownloadTables()
        {
            var tablesDir = TablesDir;
            var versionPath = Path.Combine(tablesDir, "version.txt");
            var expectedVersion = StaticConfig.StcVersion;

            if (Directory.Exists(tablesDir))
            {
                var oldVersion = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : "unknown";
                var backupDir = $"{tablesDir}.bak-{oldVersion}";
                for (var i = 2; Directory.Exists(backupDir); i++)
                    backupDir = $"{tablesDir}.bak-{oldVersion}-{i}";

                Directory.Move(tablesDir, backupDir);
                logger.LogInformation("Previous tables moved to {Dir}", backupDir);
            }

            Directory.CreateDirectory(tablesDir);

            if (TryCopyFromClientCache(tablesDir, expectedVersion))
                return;

            var zipPath = Path.Combine(Path.GetTempPath(), $"bk_stc_pb_{expectedVersion}.zip");
            try
            {
                var url = StaticConfig.StcTableZipUrl;
                logger.LogInformation("Downloading and extracting tables from {Url}, this may take a while...", url);
                var zipBytes = FetchTables(url);
                File.WriteAllBytes(zipPath, zipBytes);

                ZipFile.ExtractToDirectory(zipPath, tablesDir);
                File.WriteAllText(versionPath, expectedVersion);

                var tableCount = Directory.EnumerateFiles(tablesDir, "*.bytes").Count();
                logger.LogInformation("Downloaded {Count} tables, version {Version}, to {Dir}", tableCount, expectedVersion, tablesDir);
            }
            finally
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
            }
        }

        private bool TryCopyFromClientCache(string tablesDir, string expectedVersion)
        {
            var clientDir = Environment.GetEnvironmentVariable("GRIFFIN_CLIENT_TABLES_DIR");
            if (string.IsNullOrWhiteSpace(clientDir))
            {
                var candidates = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "GF2Exilium", "GF2 Game", "GF2_Exilium_Data", "LocalCache", "Data", "Table"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                        "GF2Exilium", "GF2 Game", "GF2_Exilium_Data", "LocalCache", "Data", "Table"),
                };

                clientDir = candidates.FirstOrDefault(path =>
                    File.Exists(Path.Combine(path, "version.txt"))
                    && string.Equals(File.ReadAllText(Path.Combine(path, "version.txt")).Trim(), expectedVersion, StringComparison.Ordinal))
                    ?? candidates[0];
            }

            var clientVersionPath = Path.Combine(clientDir, "version.txt");
            if (!File.Exists(clientVersionPath)
                || !string.Equals(File.ReadAllText(clientVersionPath).Trim(), expectedVersion, StringComparison.Ordinal))
                return false;

            foreach (var file in Directory.EnumerateFiles(clientDir, "*.bytes"))
                File.Copy(file, Path.Combine(tablesDir, Path.GetFileName(file)), overwrite: true);

            File.WriteAllText(Path.Combine(tablesDir, "version.txt"), expectedVersion);
            logger.LogInformation("Copied {Count} tables (version {Version}) from the client cache {Dir}",
                Directory.EnumerateFiles(tablesDir, "*.bytes").Count(), expectedVersion, clientDir);
            return true;
        }

        /// <summary>
        /// Strictly parses every table the server needs. Returns one message per problem (missing file, no matching
        /// protobuf type, or rows that do not parse). An empty list means the tables are usable.
        /// </summary>
        public IReadOnlyList<string> ValidateTables()
        {
            var problems = new List<string>();
            var assembly = GetTableAssembly();
            var getTableMethod = typeof(TableService).GetMethod(nameof(GetTable), BindingFlags.Public | BindingFlags.Instance)!;

            foreach (var tableName in RequiredTables)
            {
                var type = assembly.GetType($"Griffin.Common.Table.{tableName}");
                if (type is null)
                {
                    problems.Add($"{tableName}: no protobuf type in the generated protos (protos are older than the client tables?)");
                    continue;
                }

                var path = GetBytesFilePath(tableName);
                if (!File.Exists(path))
                {
                    problems.Add($"{tableName}: file not found at {path}");
                    continue;
                }

                try
                {
                    var rows = (System.Collections.ICollection)getTableMethod.MakeGenericMethod(type).Invoke(this, [true])!;
                    if (rows.Count == 0)
                        problems.Add($"{tableName}: parsed 0 rows");
                }
                catch (Exception ex)
                {
                    var actual = (ex as TargetInvocationException)?.InnerException ?? ex;
                    problems.Add($"{tableName}: {actual.Message}");
                }
            }

            return problems;
        }

        public void DumpAllJsonToFile(string? outputDir = null)
        {
            var tablesDir = TablesDir;
            if (!Directory.Exists(tablesDir))
                throw new DirectoryNotFoundException($"Tables directory not found: {tablesDir}");

            outputDir ??= Path.Combine(ResourceDir, "Dump");
            Directory.CreateDirectory(outputDir);

            var getTableMethod = typeof(TableService).GetMethod(nameof(GetTable), BindingFlags.Public | BindingFlags.Instance)!;
            var assembly = GetTableAssembly();

            foreach (var filePath in Directory.EnumerateFiles(tablesDir, "*.bytes"))
            {
                var tableName = Path.GetFileNameWithoutExtension(filePath);
                var type = assembly.GetType($"Griffin.Common.Table.{tableName}");
                if (type is null)
                {
                    logger.LogWarning("Skipping {Table}: no matching protobuf type", tableName);
                    continue;
                }

                try
                {
                    var rows = getTableMethod.MakeGenericMethod(type).Invoke(this, [false])!;
                    var json = JsonConvert.SerializeObject(rows, Formatting.Indented);
                    var outputPath = Path.Combine(outputDir, $"{tableName}.json");
                    File.WriteAllText(outputPath, json);
                    logger.LogInformation("Dumped {Table} to {Path}", tableName, outputPath);
                }
                catch (Exception ex)
                {
                    var actual = (ex as TargetInvocationException)?.InnerException ?? ex;
                    logger.LogWarning(actual, "Failed to dump {Table}", tableName);
                }
            }
        }

        public List<T> GetTable<T>(bool bypassCache = false) where T : IMessage<T>, new()
        {
            var type = typeof(T);

            if (!bypassCache && caches.TryGetValue(type, out var cached))
                return (List<T>)cached;

            var bytesFilePath = GetBytesFilePath(type.Name);

            if (!File.Exists(bytesFilePath))
                throw new FileNotFoundException(
                    $"Table file not found: {bytesFilePath}", bytesFilePath);

            var rows = LoadTable<T>(bytesFilePath);
            caches[type] = rows;

            logger.LogDebug("{Table} loaded {Count} row(s) and cached", type.Name, rows.Count);
            return rows;
        }

        public void ClearCache()
        {
            var count = caches.Count;
            caches.Clear();
            logger.LogInformation("Table cache cleared ({Count} entries evicted)", count);
        }

        private static string GetBytesFilePath(string tableName)
        {
            return Path.Combine(ResourceDir, "Tables", $"{tableName}.bytes");
        }

        private static byte[] FetchTables(string url)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "UnityPlayer/2019.4.40f1 (UnityWebRequest/1.0, libcurl/7.80.0-DEV)");
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Unity-Version", "2019.4.40f1");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");

            using var response = client.GetAsync(url).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }

        private static Assembly GetTableAssembly()
        {
            const string assemblyName = "Griffin.Common.Proto";

            var loaded = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.Ordinal));
            if (loaded is not null)
                return loaded;

            return Assembly.Load(new AssemblyName(assemblyName));
        }

        /// <summary>
        /// Reads a <c>.bytes</c> file using the client binary format:
        /// <code>
        /// [0..3]       4-byte LE uint32: header/index protobuf size (N)
        /// [4..4+N-1]   Header/index protobuf (used by client for lazy row loading via StcTableDataMap)
        /// [4+N..]      Wrapper protobuf: repeated row messages at field 1
        /// </code>
        /// The server skips the header/index and eagerly parses every row in field 1.
        /// </summary>
        private List<T> LoadTable<T>(string filePath) where T : IMessage<T>, new()
        {
            var fileBytes = File.ReadAllBytes(filePath);

            if (fileBytes.Length < 4)
                throw new InvalidDataException(
                    $"Table file is too small ({fileBytes.Length} bytes): {filePath}");

            int headerSize = (int)BitConverter.ToUInt32(fileBytes, 0);
            int dataOffset = 4 + headerSize;

            if (dataOffset > fileBytes.Length)
                throw new InvalidDataException(
                    $"Table header declares {headerSize} bytes but file is only " +
                    $"{fileBytes.Length} bytes: {filePath}");

            int dataSize = fileBytes.Length - dataOffset;

            logger.LogDebug(
                "{Table}: file={FileSize} bytes, header={HeaderSize}, data={DataSize} (offset {Offset})",
                typeof(T).Name, fileBytes.Length, headerSize, dataSize, dataOffset);

            var parser = GetParser<T>();
            var rows = new List<T>();

            using var input = new CodedInputStream(fileBytes, dataOffset, dataSize);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (tag == RowFieldTag)
                {
                    try
                    {
                        rows.Add((T)parser.ParseFrom(input.ReadBytes()));
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidDataException(
                            $"Row {rows.Count} of {typeof(T).Name} does not match the generated protobuf type " +
                            $"(table version {StaticConfig.StcVersion}; protos/tables out of sync?): {ex.Message}", ex);
                    }

                    continue;
                }

                input.SkipLastField();
            }

            return rows;
        }

        private MessageParser GetParser<T>() where T : IMessage<T>, new()
        {
            return parserCache.GetOrAdd(typeof(T), static type =>
            {
                var parserProp = type.GetProperty("Parser",
                    BindingFlags.Static | BindingFlags.Public)
                    ?? throw new InvalidOperationException(
                        $"Type {type.FullName} does not have a static Parser property. " +
                        $"Is it a protobuf-generated IMessage type?");

                return (MessageParser)parserProp.GetValue(null)!;
            });
        }
    }

    public interface ITableService
    {
        List<T> GetTable<T>(bool bypassCache = false) where T : IMessage<T>, new();

        void DumpAllJsonToFile(string? outputDir = null);

        void EnsureTables();

        void DownloadTables();

        IReadOnlyList<string> ValidateTables();

        void ClearCache();
    }
}
