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

namespace GenericModDocumentationFramework.Loaders
{

    public static class JsonDocumentationLoader
    {
        private const string FileName       = "documentation.json";
        private const int    CurrentFormat  = 1;

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

                string jsonPath = Path.Combine(modDir, FileName);
                if (!File.Exists(jsonPath))
                    jsonPath = Path.Combine(modDir, "assets", FileName);
                if (!File.Exists(jsonPath)) continue;

                try
                {
                    string json = File.ReadAllText(jsonPath);
                    var data = JsonSerializer.Deserialize<DocumentationData>(json, JsonOptions);

                    if (data == null)
                    {
                        monitor.Log($"[GMDF] '{modInfo.Manifest.UniqueID}': {FileName} deserialized as null — skipping.", LogLevel.Warn);
                        continue;
                    }

                    if (data.Format != CurrentFormat)
                    {
                        monitor.Log($"[GMDF] '{modInfo.Manifest.UniqueID}': {FileName} has format {data.Format} (expected {CurrentFormat}) — skipping.", LogLevel.Warn);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(data.ModName))
                    {
                        monitor.Log($"[GMDF] '{modInfo.Manifest.UniqueID}': {FileName} is missing 'modName' — skipping.", LogLevel.Warn);
                        continue;
                    }

                    ITranslationHelper? modI18n = GetModTranslations(modInfo, modDir, frameworkHelper, monitor);

                    var doc = BuildDocumentation(data, modDir, modI18n, modInfo.Manifest.UniqueID, monitor);

                    registry.RegisterFromJson(modInfo.Manifest.UniqueID, doc);
                    loaded++;

                    monitor.Log($"[GMDF] Loaded documentation for '{modInfo.Manifest.UniqueID}' ({data.Pages?.Count ?? 0} pages).", LogLevel.Debug);
                }
                catch (JsonException ex)
                {
                    monitor.Log($"[GMDF] '{modInfo.Manifest.UniqueID}': Failed to parse {FileName}: {ex.Message}", LogLevel.Error);
                }
                catch (Exception ex)
                {
                    monitor.Log($"[GMDF] '{modInfo.Manifest.UniqueID}': Unexpected error loading {FileName}: {ex}", LogLevel.Error);
                }
            }

            monitor.Log($"[GMDF] Discovery complete — {loaded} mod(s) with documentation loaded.", LogLevel.Info);
        }

        private static ModDocumentation BuildDocumentation(
            DocumentationData   data,
            string              modDir,
            ITranslationHelper? modI18n,
            string              modId,
            IMonitor            monitor)
        {
            Func<string> getName = () => ResolveI18n(data.ModName, modI18n);
            var doc = new ModDocumentation(getName);

            if (data.Pages == null || data.Pages.Count == 0)
                return doc;

            foreach (var pageData in data.Pages)
            {
                string pageId = pageData.Id ?? pageData.Name.ToLowerInvariant().Replace(' ', '-');
                Func<string> getPageName = () => ResolveI18n(pageData.Name, modI18n);

                DocumentationPage page;
                if (data.Pages.Count == 1 && (pageData.Id == null || pageData.Id == "overview"))
                {
                    page = doc.DefaultPage;
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
                        () => LoadTextureFromDisk(texPath, monitor, modId),
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

            return doc;
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
                    return new SectionTitleEntry(() => ResolveI18n(data.Text!, modI18n), align, data.FontSize);

                case "paragraph":
                    if (string.IsNullOrWhiteSpace(data.Text)) return Warn("paragraph missing 'text'");
                    return new ParagraphEntry(() => ResolveI18n(data.Text!, modI18n), align, data.FontSize);

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
                        () => LoadTextureFromDisk(texPath, monitor, modId),
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
                    if (string.IsNullOrWhiteSpace(data.Label)) return Warn("spoiler missing 'label'");
                    if (string.IsNullOrWhiteSpace(data.Text))  return Warn("spoiler missing 'text'");
                    return new SpoilerEntry(
                        () => ResolveI18n(data.Label!, modI18n),
                        () => ResolveI18n(data.Text!,  modI18n)
                    );

                case "link":
                    if (string.IsNullOrWhiteSpace(data.Url))  return Warn("link missing 'url'");
                    if (string.IsNullOrWhiteSpace(data.Text)) return Warn("link missing 'text'");
                    return new LinkEntry(
                        () => ResolveI18n(data.Text!, modI18n),
                        data.Url!,
                        align
                    );

                case "gif":
                {
                    if (string.IsNullOrWhiteSpace(data.Texture)) return Warn("gif missing 'texture'");
                    if (data.FrameCount < 1)                      return Warn("gif 'frameCount' must be >= 1");
                    string texPath = Path.Combine(modDir, data.Texture!);

                    return new GifEntry(
                        () => LoadTextureFromDisk(texPath, monitor, modId),
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
            if (i18n == null) return text;

            return I18nTokenPattern.Replace(text, match =>
            {
                string raw = match.Groups[1].Value;
                string key = raw.StartsWith("i18n:", StringComparison.OrdinalIgnoreCase)
                    ? raw.Substring(5)
                    : raw;
                var translation = i18n.Get(key.Trim());
                return translation.HasValue() ? translation.ToString() : match.Value;
            });
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
            try
            {
                using var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read);
                return Texture2D.FromStream(StardewValley.Game1.graphics.GraphicsDevice, stream);
            }
            catch (Exception ex)
            {
                monitor.Log($"[GMDF] '{modId}': Failed to load texture '{absolutePath}': {ex.Message}", LogLevel.Error);
                throw;
            }
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
            // Fast path: C# SMAPI mods expose their ITranslationHelper directly.
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

            // Fallback for non-C# mods (e.g. Content Patcher): create a temporary
            // content pack pointed at the mod's directory so SMAPI loads its i18n/
            // folder the normal way and gives us an ITranslationHelper.
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
