using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GenericModDocumentationFramework.Models;
using GenericModDocumentationFramework.Models.Entries;
using GenericModDocumentationFramework.Models.Json;
using GenericModDocumentationFramework.Registry;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley.TokenizableStrings;

namespace GenericModDocumentationFramework.Loaders
{

    public static class JsonDocumentationLoader
    {
        private const string FileName        = "documentation.json";
        private const string MultiFileFolder = "documentation";
        private const string PageFilePrefix  = "documentation.";
        private const int    CurrentFormat   = 1;

        private static readonly Regex I18nTokenPattern = new(@"\{\{(.+?)\}\}", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling         = JsonCommentHandling.Skip,
            AllowTrailingCommas         = true,
            DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
        };

        public static void DiscoverAndLoad(
            DocumentationRegistry registry,
            IModHelper            frameworkHelper,
            IMonitor              monitor)
        {
            int loaded = 0;

            foreach (var modInfo in frameworkHelper.ModRegistry.GetAll())
            {
                string? modDir = GetModDirectory(modInfo);
                if (modDir == null) continue;

                // ── Determine which loading mode to use ──────────────────────────
                // Multi-file mode takes priority when a documentation/ folder exists
                // that contains a documentation.json manifest inside it.
                string multiDir      = Path.Combine(modDir, MultiFileFolder);
                string multiManifest = Path.Combine(multiDir, FileName);
                bool   isMultiFile   = Directory.Exists(multiDir) && File.Exists(multiManifest);

                // Legacy single-file: root documentation.json or assets/documentation.json
                string singlePath = Path.Combine(modDir, FileName);
                if (!File.Exists(singlePath))
                    singlePath = Path.Combine(modDir, "assets", FileName);
                bool hasSingleFile = File.Exists(singlePath);

                if (!isMultiFile && !hasSingleFile) continue;

                // Warn if both layouts are present so the mod author knows which wins.
                if (isMultiFile && hasSingleFile)
                    monitor.Log($"[GMDF] '{modInfo.Manifest.UniqueID}': both a '{MultiFileFolder}/' folder and a root {FileName} were found — using the '{MultiFileFolder}/' folder.", LogLevel.Warn);

                ITranslationHelper? modI18n = GetModTranslations(modInfo, modDir, frameworkHelper, monitor);

                try
                {
                    ModDocumentation doc;
                    int pageCount;

                    if (isMultiFile)
                    {
                        (doc, pageCount) = LoadMultiFile(multiDir, multiManifest, modI18n, modInfo.Manifest.UniqueID, monitor);
                    }
                    else
                    {
                        (doc, pageCount) = LoadSingleFile(singlePath, modDir, modI18n, modInfo.Manifest.UniqueID, monitor);
                    }

                    if (doc == null) continue;

                    registry.RegisterFromJson(modInfo.Manifest.UniqueID, doc);
                    loaded++;

                    string mode = isMultiFile ? "multi-file" : "single-file";
                    monitor.Log($"[GMDF] Loaded documentation for '{modInfo.Manifest.UniqueID}' ({pageCount} page(s), {mode}).", LogLevel.Debug);
                }
                catch (JsonException ex)
                {
                    monitor.Log($"[GMDF] '{modInfo.Manifest.UniqueID}': Failed to parse documentation JSON: {ex.Message}", LogLevel.Error);
                }
                catch (Exception ex)
                {
                    monitor.Log($"[GMDF] '{modInfo.Manifest.UniqueID}': Unexpected error loading documentation: {ex}", LogLevel.Error);
                }
            }

            monitor.Log($"[GMDF] Discovery complete — {loaded} mod(s) with documentation loaded.", LogLevel.Info);
        }

        // ── Single-file loader (legacy) ───────────────────────────────────────────

        private static (ModDocumentation? doc, int pageCount) LoadSingleFile(
            string              jsonPath,
            string              modDir,
            ITranslationHelper? modI18n,
            string              modId,
            IMonitor            monitor)
        {
            string json = File.ReadAllText(jsonPath);
            var data = JsonSerializer.Deserialize<DocumentationData>(json, JsonOptions);

            if (data == null)
            {
                monitor.Log($"[GMDF] '{modId}': {FileName} deserialized as null — skipping.", LogLevel.Warn);
                return (null, 0);
            }

            if (!ValidateManifest(data, modId, FileName, monitor))
                return (null, 0);

            var doc = BuildDocumentation(data, modDir, modI18n, modId, monitor);
            return (doc, data.Pages?.Count ?? 0);
        }

        // ── Multi-file loader ─────────────────────────────────────────────────────

        private static (ModDocumentation? doc, int pageCount) LoadMultiFile(
            string              multiDir,
            string              manifestPath,
            ITranslationHelper? modI18n,
            string              modId,
            IMonitor            monitor)
        {
            // 1. Parse the manifest.
            string manifestJson = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<DocumentationData>(manifestJson, JsonOptions);

            if (manifest == null)
            {
                monitor.Log($"[GMDF] '{modId}': {MultiFileFolder}/{FileName} deserialized as null — skipping.", LogLevel.Warn);
                return (null, 0);
            }

            if (!ValidateManifest(manifest, modId, $"{MultiFileFolder}/{FileName}", monitor))
                return (null, 0);

            // Warn if the manifest already contains inline page data (pages with entries) —
            // in multi-file mode the pages array in the manifest is used only for pageOrder
            // metadata; entries defined there are ignored.
            if (manifest.Pages is { Count: > 0 } && manifest.Pages.Exists(p => p.Entries.Count > 0))
                monitor.Log($"[GMDF] '{modId}': {MultiFileFolder}/{FileName} contains inline page entries — these are ignored in multi-file mode. Use per-page files instead.", LogLevel.Warn);

            // 2. Find all per-page files: documentation.<anything>.json
            //    Exclude the manifest itself (documentation.json).
            var pageFiles = new List<string>();
            foreach (string file in Directory.GetFiles(multiDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, FileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith(PageFilePrefix, StringComparison.OrdinalIgnoreCase))
                    pageFiles.Add(file);
            }

            // 3. Parse each page file.
            //    Key = the page's resolved id (from the file's own id field, or derived from the file name).
            var parsedPages = new Dictionary<string, (PageData data, string filePath)>(StringComparer.OrdinalIgnoreCase);

            foreach (string filePath in pageFiles)
            {
                string fileName = Path.GetFileName(filePath);
                try
                {
                    string pageJson = File.ReadAllText(filePath);
                    var pageData = JsonSerializer.Deserialize<PageData>(pageJson, JsonOptions);

                    if (pageData == null)
                    {
                        monitor.Log($"[GMDF] '{modId}': Page file '{fileName}' deserialized as null — skipping.", LogLevel.Warn);
                        continue;
                    }

                    // Derive id: use explicit id field, else strip "documentation." prefix and ".json" suffix.
                    if (string.IsNullOrWhiteSpace(pageData.Id))
                    {
                        string stem = Path.GetFileNameWithoutExtension(fileName); // e.g. "documentation.npcs"
                        pageData.Id = stem.Substring(PageFilePrefix.Length);      // e.g. "npcs"
                    }

                    string pageId = pageData.Id!.ToLowerInvariant();

                    if (parsedPages.ContainsKey(pageId))
                    {
                        monitor.Log($"[GMDF] '{modId}': Duplicate page id '{pageId}' from file '{fileName}' — skipping.", LogLevel.Warn);
                        continue;
                    }

                    parsedPages[pageId] = (pageData, filePath);
                }
                catch (JsonException ex)
                {
                    monitor.Log($"[GMDF] '{modId}': Failed to parse page file '{fileName}': {ex.Message}", LogLevel.Error);
                }
            }

            if (parsedPages.Count == 0)
            {
                monitor.Log($"[GMDF] '{modId}': No valid page files found in '{MultiFileFolder}/' — loading with empty documentation.", LogLevel.Warn);
            }

            // 4. Order pages: pageOrder list first (in order), then remaining alphabetically by page id.
            var orderedPageIds = new List<string>();

            if (manifest.PageOrder is { Count: > 0 })
            {
                foreach (string id in manifest.PageOrder)
                {
                    string norm = id.ToLowerInvariant();
                    if (parsedPages.ContainsKey(norm))
                        orderedPageIds.Add(norm);
                    else
                        monitor.Log($"[GMDF] '{modId}': pageOrder references '{id}' but no matching page file was found — skipping.", LogLevel.Warn);
                }
            }

            // Append any pages not yet listed, sorted alphabetically.
            var listedSet = new HashSet<string>(orderedPageIds, StringComparer.OrdinalIgnoreCase);
            var unlisted  = new List<string>(parsedPages.Keys);
            unlisted.RemoveAll(k => listedSet.Contains(k));
            unlisted.Sort(StringComparer.OrdinalIgnoreCase);
            orderedPageIds.AddRange(unlisted);

            // 5. Build the ModDocumentation from the ordered page list.
            Func<string> getName = () => ResolveI18n(manifest.ModName, modI18n);
            var doc = new ModDocumentation(modId, getName);

            foreach (string pageId in orderedPageIds)
            {
                var (pageData, filePath) = parsedPages[pageId];
                // Use the directory of the multi-file folder as the base for asset resolution,
                // since texture paths in page files are relative to the mod root just like
                // they are in single-file mode.
                string modDir = Directory.GetParent(multiDir)!.FullName;
                ApplyPageToDoc(doc, pageData, orderedPageIds.Count, modDir, modI18n, modId, monitor);
            }

            return (doc, orderedPageIds.Count);
        }

        // ── Shared manifest validation ────────────────────────────────────────────

        private static bool ValidateManifest(DocumentationData data, string modId, string fileLabel, IMonitor monitor)
        {
            if (data.Format != CurrentFormat)
            {
                monitor.Log($"[GMDF] '{modId}': {fileLabel} has format {data.Format} (expected {CurrentFormat}) — skipping.", LogLevel.Warn);
                return false;
            }

            if (string.IsNullOrWhiteSpace(data.ModName))
            {
                monitor.Log($"[GMDF] '{modId}': {fileLabel} is missing 'modName' — skipping.", LogLevel.Warn);
                return false;
            }

            return true;
        }

        private static ModDocumentation BuildDocumentation(
            DocumentationData   data,
            string              modDir,
            ITranslationHelper? modI18n,
            string              modId,
            IMonitor            monitor)
        {
            Func<string> getName = () => ResolveI18n(data.ModName, modI18n);
            var doc = new ModDocumentation(modId, getName);

            if (data.Pages == null || data.Pages.Count == 0)
                return doc;

            foreach (var pageData in data.Pages)
                ApplyPageToDoc(doc, pageData, data.Pages.Count, modDir, modI18n, modId, monitor);

            return doc;
        }

        /// <summary>
        /// Resolves a <see cref="PageData"/> into a <see cref="DocumentationPage"/> and attaches it to
        /// <paramref name="doc"/>.  Used by both the single-file and multi-file loading paths.
        /// </summary>
        /// <param name="totalPageCount">
        /// Total number of pages being added to <paramref name="doc"/> in this batch.
        /// When 1 and the page id is null / "overview", entries go into the default page.
        /// </param>
        private static void ApplyPageToDoc(
            ModDocumentation    doc,
            PageData            pageData,
            int                 totalPageCount,
            string              modDir,
            ITranslationHelper? modI18n,
            string              modId,
            IMonitor            monitor)
        {
            string pageId = pageData.Id ?? pageData.Name.ToLowerInvariant().Replace(' ', '-');
            Func<string> getPageName = () => ResolveI18n(pageData.Name, modI18n);

            DocumentationPage page;
            if (totalPageCount == 1 && (pageData.Id == null || pageData.Id == "overview"))
            {
                page = doc.DefaultPage;
                page.GetPageName = getPageName;
            }
            else
            {
                doc.AddPage(pageId, getPageName);
                page = doc.GetPage(pageId)!;
            }

            if (pageData.HeaderImage != null && !string.IsNullOrWhiteSpace(pageData.HeaderImage.Texture))
            {
                var hiData = pageData.HeaderImage;
                string texPath = Path.Combine(modDir, hiData.Texture);
                Rectangle? srcRect = ToRect(hiData.SourceRect);

                page.HeaderImage = new HeaderImageEntry(
                    MakeTextureLoader(texPath, monitor, modId),
                    srcRect
                );
            }

            foreach (var entryData in pageData.Entries)
            {
                var entry = BuildEntry(entryData, modDir, modI18n, modId, monitor);
                if (entry != null)
                    page.Entries.Add(entry);
            }
        }

        private static IDocumentationEntry? BuildEntry(
            EntryData           data,
            string              modDir,
            ITranslationHelper? modI18n,
            string              modId,
            IMonitor            monitor)
        {
            var align = ParseAlignment(data.Align);

            switch (data.Type?.ToLowerInvariant())
            {
                case "sectiontitle":
                    if (string.IsNullOrWhiteSpace(data.Text)) return Warn("sectionTitle missing 'text'");
                    return new SectionTitleEntry(() => ResolveI18n(data.Text!, modI18n), align, data.FontSize, data.Anchor);

                case "paragraph":
                    if (string.IsNullOrWhiteSpace(data.Text)) return Warn("paragraph missing 'text'");
                    return new ParagraphEntry(() => ResolveI18n(data.Text!, modI18n), align, data.FontSize, data.Anchor);

                case "caption":
                    if (string.IsNullOrWhiteSpace(data.Text)) return Warn("caption missing 'text'");
                    var captionAlign = data.Align != null ? align : Alignment.Center;
                    return new CaptionEntry(() => ResolveI18n(data.Text!, modI18n), captionAlign, data.FontSize);

                case "image":
                {
                    if (string.IsNullOrWhiteSpace(data.Texture)) return Warn("image missing 'texture'");
                    string texPath = Path.Combine(modDir, data.Texture!);

                    IReadOnlyList<Func<string>>? itemFuncs = null;
                    if (data.Items is { Count: > 0 })
                    {
                        var funcs = new List<Func<string>>();
                        foreach (var item in data.Items)
                            funcs.Add(() => ResolveI18n(item, modI18n));
                        itemFuncs = funcs;
                    }

                    return new ImageEntry(
                        MakeTextureLoader(texPath, monitor, modId),
                        ToRect(data.SourceRect),
                        data.Scale,
                        align,
                        itemFuncs
                    );
                }

                case "list":
                {
                    if (data.Items == null || data.Items.Count == 0) return Warn("list missing 'items'");
                    var itemFuncs = new List<Func<string>>();
                    foreach (var item in data.Items)
                        itemFuncs.Add(() => ResolveI18n(item, modI18n));
                    return new ListEntry(itemFuncs, isOrdered: false, align, data.FontSize);
                }

                case "orderedlist":
                {
                    if (data.Items == null || data.Items.Count == 0) return Warn("orderedList missing 'items'");
                    var itemFuncs = new List<Func<string>>();
                    foreach (var item in data.Items)
                        itemFuncs.Add(() => ResolveI18n(item, modI18n));
                    return new ListEntry(itemFuncs, isOrdered: true, align, data.FontSize);
                }

                case "keyvalue":
                    if (string.IsNullOrWhiteSpace(data.Key))   return Warn("keyValue missing 'key'");
                    if (string.IsNullOrWhiteSpace(data.Value)) return Warn("keyValue missing 'value'");
                    return new KeyValuePairEntry(
                        () => ResolveI18n(data.Key!,   modI18n),
                        () => ResolveI18n(data.Value!, modI18n),
                        data.FontSize
                    );

                case "divider":
                    return new DividerEntry(ParseDividerStyle(data.Style));

                case "spacer":
                    return new SpacerEntry(Math.Max(1, data.Height));

                case "spoiler":
                {
                    if (string.IsNullOrWhiteSpace(data.Label)) return Warn("spoiler missing 'label'");

                    // Support both the legacy plain-text form (data.Text) and the new child-entries form (data.Entries).
                    // If both are present, data.Entries takes priority.
                    var children = new List<IDocumentationEntry>();

                    if (data.Entries != null && data.Entries.Count > 0)
                    {
                        foreach (var sub in data.Entries)
                        {
                            var built = BuildEntry(sub, modDir, modI18n, modId, monitor);
                            if (built != null) children.Add(built);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(data.Text))
                    {
                        // Legacy: wrap the plain text in a ParagraphEntry so old JSON keeps working.
                        string capturedText = data.Text!;
                        children.Add(new ParagraphEntry(() => ResolveI18n(capturedText, modI18n)));
                    }
                    else
                    {
                        return Warn("spoiler must have either 'entries' or 'text'");
                    }

                    return new SpoilerEntry(
                        () => ResolveI18n(data.Label!, modI18n),
                        children
                    );
                }

                case "link":
                    if (string.IsNullOrWhiteSpace(data.Url))  return Warn("link missing 'url'");
                    if (string.IsNullOrWhiteSpace(data.Text)) return Warn("link missing 'text'");
                    return new LinkEntry(
                        () => ResolveI18n(data.Text!, modI18n),
                        data.Url!,
                        align
                    );

                case "internallink":
                {
                    if (string.IsNullOrWhiteSpace(data.Text)) return Warn("internalLink missing 'text'");
                    // At least one of mod, page, or anchor must be provided to be useful
                    if (string.IsNullOrWhiteSpace(data.Mod) &&
                        string.IsNullOrWhiteSpace(data.Page) &&
                        string.IsNullOrWhiteSpace(data.Anchor))
                        return Warn("internalLink must specify at least one of 'mod', 'page', or 'anchor'");

                    return new InternalLinkEntry(
                        () => ResolveI18n(data.Text!, modI18n),
                        modId,
                        string.IsNullOrWhiteSpace(data.Mod)    ? null : data.Mod,
                        string.IsNullOrWhiteSpace(data.Page)   ? null : data.Page,
                        string.IsNullOrWhiteSpace(data.Anchor) ? null : data.Anchor,
                        align
                    );
                }

                case "gif":
                {
                    if (string.IsNullOrWhiteSpace(data.Texture)) return Warn("gif missing 'texture'");
                    if (data.FrameCount < 1)                      return Warn("gif 'frameCount' must be >= 1");
                    string texPath = Path.Combine(modDir, data.Texture!);

                    return new GifEntry(
                        MakeTextureLoader(texPath, monitor, modId),
                        data.FrameCount,
                        data.FrameDuration,
                        data.Scale,
                        align,
                        data.Columns,
                        data.Rows
                    );
                }

                case "row":
                {
                    if ((data.Left == null || data.Left.Count == 0) && (data.Right == null || data.Right.Count == 0))
                        return Warn("row: both 'left' and 'right' are empty");

                    var leftEntries  = new List<IDocumentationEntry>();
                    var rightEntries = new List<IDocumentationEntry>();

                    foreach (var sub in data.Left ?? new List<EntryData>())
                    {
                        var built = BuildEntry(sub, modDir, modI18n, modId, monitor);
                        if (built != null) leftEntries.Add(built);
                    }
                    foreach (var sub in data.Right ?? new List<EntryData>())
                    {
                        var built = BuildEntry(sub, modDir, modI18n, modId, monitor);
                        if (built != null) rightEntries.Add(built);
                    }

                    double frac = data.LeftFraction > 0 ? data.LeftFraction : 0.5;
                    return new RowEntry(leftEntries, rightEntries, frac);
                }

                case "indentblock":
                {
                    if (data.Entries == null || data.Entries.Count == 0)
                        return Warn("indentBlock: 'entries' is empty or missing");

                    var children = new List<IDocumentationEntry>();
                    foreach (var sub in data.Entries)
                    {
                        var built = BuildEntry(sub, modDir, modI18n, modId, monitor);
                        if (built != null) children.Add(built);
                    }

                    int indent = data.Indent > 0 ? data.Indent : 32;
                    return new IndentBlockEntry(children, indent, data.ShowRule);
                }

                default:
                    monitor.Log($"[GMDF] '{modId}': Unknown entry type '{data.Type}' — skipping.", LogLevel.Warn);
                    return null;
            }

            IDocumentationEntry? Warn(string msg)
            {
                monitor.Log($"[GMDF] '{modId}': {msg} — skipping entry.", LogLevel.Warn);
                return null;
            }
        }

        private static string ResolveI18n(string text, ITranslationHelper? i18n)
        {
            // Step 1 — resolve {{i18n:key}} / {{key}} SMAPI translation tokens.
            if (i18n != null)
            {
                text = I18nTokenPattern.Replace(text, match =>
                {
                    string raw = match.Groups[1].Value;
                    string key = raw.StartsWith("i18n:", StringComparison.OrdinalIgnoreCase)
                        ? raw.Substring(5)
                        : raw;
                    var translation = i18n.Get(key.Trim());
                    return translation.HasValue() ? translation.ToString() : match.Value;
                });
            }

            // Step 2 — resolve Stardew tokenizable strings, e.g.
            //   [FarmName]  [PlayerName]  [LocalizedText Strings\File:Key]  [ItemName (O)128]
            // These are only meaningful once a save is loaded and Game1.player exists.
            // Because every text field is wrapped in a Func<string> invoked at render time,
            // this executes after save-load — exactly when TokenParser needs its context.
            if (text.Contains('[') && StardewValley.Game1.player != null)
            {
                try
                {
                    text = TokenParser.ParseText(
                        text,
                        random:       null,
                        customParser: null,
                        player:       StardewValley.Game1.player);
                }
                catch
                {
                    // Malformed token string — leave the text unchanged.
                }
            }

            return text;
        }

        private static Alignment ParseAlignment(string? align) => align?.ToLowerInvariant() switch
        {
            "center" => Alignment.Center,
            "right"  => Alignment.Right,
            _        => Alignment.Left
        };

        private static DividerStyle ParseDividerStyle(string? style) => style?.ToLowerInvariant() switch
        {
            "double"       => DividerStyle.Double,
            "dotted"       => DividerStyle.Dotted,
            "iconcentered" => DividerStyle.IconCentered,
            _              => DividerStyle.Single
        };

        private static Rectangle? ToRect(RectData? data)
        {
            if (data == null) return null;
            return new Rectangle(data.X, data.Y, data.Width, data.Height);
        }

        private static Texture2D LoadTextureFromDisk(string absolutePath, IMonitor monitor, string modId)
        {
            using var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read);
            return Texture2D.FromStream(StardewValley.Game1.graphics.GraphicsDevice, stream);
        }

        private static Func<Texture2D> MakeTextureLoader(string absolutePath, IMonitor monitor, string modId)
        {
            bool logged = false;
            return () =>
            {
                try
                {
                    return LoadTextureFromDisk(absolutePath, monitor, modId);
                }
                catch (Exception ex)
                {
                    if (!logged)
                    {
                        monitor.Log($"[GMDF] '{modId}': Failed to load texture '{absolutePath}': {ex.Message}", LogLevel.Error);
                        logged = true;
                    }
                    throw;
                }
            };
        }

        private static string? GetModDirectory(IModInfo modInfo)
        {
            try
            {
                var prop = modInfo.GetType().GetProperty("DirectoryPath");
                if (prop != null)
                    return prop.GetValue(modInfo) as string;

                var modProp = modInfo.GetType().GetProperty("Mod");
                var mod = modProp?.GetValue(modInfo);
                if (mod != null)
                {
                    var helperProp = mod.GetType().GetProperty("Helper");
                    var helper = helperProp?.GetValue(mod);
                    if (helper != null)
                    {
                        var dirProp = helper.GetType().GetProperty("DirectoryPath");
                        return dirProp?.GetValue(helper) as string;
                    }
                }
            }
            catch { }

            return null;
        }

        private static ITranslationHelper? GetModTranslations(
            IModInfo    modInfo,
            string?     modDir,
            IModHelper  frameworkHelper,
            IMonitor    monitor)
        {
            try
            {
                if (modInfo is IMod imod)
                    return imod.Helper.Translation;

                var modProp = modInfo.GetType().GetProperty("Mod");
                if (modProp?.GetValue(modInfo) is IMod mod)
                    return mod.Helper.Translation;

                var modObj = modProp?.GetValue(modInfo);
                if (modObj != null)
                {
                    var helperProp = typeof(IMod).GetProperty("Helper");
                    var helper = helperProp?.GetValue(modObj) as IModHelper;
                    if (helper != null) return helper.Translation;
                }
            }
            catch { }
            
            if (modDir != null && Directory.Exists(Path.Combine(modDir, "i18n")))
            {
                try
                {
                    var pack = frameworkHelper.ContentPacks.CreateTemporary(
                        directoryPath: modDir,
                        id:            $"gmdf.i18n.{modInfo.Manifest.UniqueID}",
                        name:          modInfo.Manifest.Name,
                        description:   "GMDF i18n proxy",
                        author:        modInfo.Manifest.Author,
                        version:       modInfo.Manifest.Version);

                    monitor.Log($"[GMDF] '{modInfo.Manifest.UniqueID}': using temporary content pack for i18n.", LogLevel.Debug);
                    return pack.Translation;
                }
                catch (Exception ex)
                {
                    monitor.Log($"[GMDF] '{modInfo.Manifest.UniqueID}': failed to create i18n proxy: {ex.Message}", LogLevel.Warn);
                }
            }

            monitor.Log($"[GMDF] '{modInfo.Manifest.UniqueID}': no i18n available — {{{{key}}}} tokens will not resolve.", LogLevel.Debug);
            return null;
        }
    }
}
