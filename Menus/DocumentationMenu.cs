using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GenericModDocumentationFramework.Models;
using GenericModDocumentationFramework.Models.Entries;
using GenericModDocumentationFramework.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace GenericModDocumentationFramework.Menus
{

    public class DocumentationMenu : IClickableMenu, IKeyboardSubscriber
    {
        private const float MenuScale         = 0.9f;
        private const int   SidebarWidth      = 300;
        private const int   Padding           = 16;
        private const int   TabHeight         = 36;
        private const int   SidebarItemHeight = 52;
        private const int   ScrollButtonSize  = 44;
        private const int   BorderThickness   = 8;
        private const float SectionTitleScale = 0.8f;
        private const int   ListItemGap       = 4;
        private const int   SearchBarHeight   = 36;

        private const float SmallFontNaturalPx    = 16f;
        private const float DialogueFontNaturalPx = 20f;

        private static readonly Color HoverColor             = new(253, 182, 84);
        private static readonly Color DividerColor           = new(180, 140, 80);
        private static readonly Color SectionTitleColor      = new(177, 78,  5);
        private static readonly Color KeyColor               = new(60,  60,  120);
        private static readonly Color CaptionColor           = new(80,  80,  80);
        private static readonly Color SpoilerHeaderColor     = new(100, 70,  40);
        private static readonly Color LinkColor              = new(50,  80,  200);
        private static readonly Color LinkHoverColor         = new(80,  120, 255);
        private static readonly Color InternalLinkColor      = new(30,  130,  60);
        private static readonly Color InternalLinkHoverColor = new(50,  190,  90);
        private static readonly Color SearchHighlightColor   = new(255, 220, 60);

        private Color _accentColor;
        private Color _contentBorderColor;
        private Color _scrollBarColor;

        private static readonly RasterizerState ScissorRasterizer =
            new() { ScissorTestEnable = true };

        // Strip inline item tokens [128] / [(O)128] and emote tokens {heart}
        private static readonly Regex InlineTokenPattern =
            new(@"\[(\([A-Za-z]+\))?\d+\]|\{[A-Za-z_][A-Za-z0-9_]*\}", RegexOptions.Compiled);

        private readonly IReadOnlyList<ModDocumentation> _mods;
        private readonly ITranslationHelper              _i18n;
        private ModDocumentation?  _selectedMod;
        private DocumentationPage? _selectedPage;
        private int _selectedModIndex;

        private int _sidebarScrollOffset;
        private int _contentScrollOffset;
        private int _totalContentHeight;

        private IReadOnlyList<DocumentationPage> _pages = Array.Empty<DocumentationPage>();
        private Rectangle[] _tabBounds = Array.Empty<Rectangle>();

        private int   _headerImageHeight;
        private int[] _entryHeights = Array.Empty<int>();

        private int _smallFontLineH;
        private int _titleFontLineH;

        private Rectangle _sidebarBounds;
        private Rectangle _contentBounds;
        private Rectangle _tabsBounds;
        private Rectangle _scrollUpBounds;
        private Rectangle _scrollDownBounds;

        private ClickableTextureComponent _scrollUpButton   = null!;
        private ClickableTextureComponent _scrollDownButton = null!;

        private string? _tabHoverText;

        // ── Tab strip scrolling ──────────────────────────────────────────────────
        private int _tabScrollOffset = 0;   // horizontal scroll in pixels
        private int _tabsTotalWidth  = 0;   // sum of all tab widths + gaps

        // ── Sidebar search bar ───────────────────────────────────────────────────
        private string  _searchQuery      = string.Empty;
        private double  _cursorBlinkTimer = 0.0;
        private bool    _cursorVisible    = true;
        private Rectangle _searchBarBounds;

        // ── Content search bar (top-right) ───────────────────────────────────────
        private string    _contentSearchQuery      = string.Empty;
        private double    _contentCursorBlinkTimer = 0.0;
        private bool      _contentCursorVisible    = true;
        private Rectangle _contentSearchBarBounds;
        private Rectangle _prevMatchButtonBounds;
        private Rectangle _nextMatchButtonBounds;

        // ── Match state ──────────────────────────────────────────────────────────
        private readonly struct SearchMatch
        {
            /// <summary>Index into <see cref="_pages"/>.</summary>
            public readonly int PageIndex;
            /// <summary>Top-level entry index within the page.</summary>
            public readonly int EntryIndex;
            /// <summary>
            /// Path of child indices for nested entries (Spoiler / Row-left / Row-right / IndentBlock).
            /// Empty for top-level matches.
            /// </summary>
            public readonly IReadOnlyList<int> ChildPath;

            public SearchMatch(int pageIndex, int entryIndex, IReadOnlyList<int> childPath)
            {
                PageIndex  = pageIndex;
                EntryIndex = entryIndex;
                ChildPath  = childPath;
            }
        }

        private List<SearchMatch> _matches        = new();
        private int               _matchIndex     = -1;
        private bool              _matchesDirty   = false;  // set true when spoiler state changes during active search

        /// <summary>
        /// Which of the two search bars currently has keyboard focus:
        ///   0 = neither, 1 = sidebar, 2 = content.
        /// </summary>
        private int _activeSearchBar = 0;

        public bool Selected
        {
            get => _activeSearchBar != 0;
            set
            {
                // IKeyboardSubscriber.Selected is called by KeyboardDispatcher.set_Subscriber
                // when the dispatcher swaps subscribers. We must NOT re-touch the dispatcher
                // from here — doing so causes infinite recursion (set_Subscriber -> set_Selected
                // -> set_Subscriber -> ...). Just update our internal state.
                if (!value)
                    _activeSearchBar = 0;
            }
        }

        private void ActivateSearchBar(int which) // 1 = sidebar, 2 = content
        {
            _activeSearchBar = which;
            Game1.keyboardDispatcher.Subscriber = this;
            if (which == 1) { _cursorBlinkTimer        = 0; _cursorVisible        = true; }
            else            { _contentCursorBlinkTimer = 0; _contentCursorVisible = true; }
        }

        /// <summary>The subset of <see cref="_mods"/> that match the sidebar search query.</summary>
        private List<ModDocumentation> _filteredMods = new();

        private readonly bool _fontSettingsActive;
        private readonly IMonitor? _monitor;

        public DocumentationMenu(IReadOnlyList<ModDocumentation> mods, ITranslationHelper i18n, ModConfig config, bool fontSettingsActive, IMonitor? monitor = null)
            : base(
                x:      (int)(Game1.uiViewport.Width  * (1f - MenuScale) / 2f),
                y:      (int)(Game1.uiViewport.Height * (1f - MenuScale) / 2f),
                width:  (int)(Game1.uiViewport.Width  * MenuScale),
                height: (int)(Game1.uiViewport.Height * MenuScale),
                showUpperRightCloseButton: true)
        {
            _mods               = mods;
            _i18n               = i18n;
            _fontSettingsActive = fontSettingsActive;
            _monitor            = monitor;

            _accentColor        = ColorHelper.Parse(config.AccentColor,        new Color(177, 78,  5));
            _contentBorderColor = ColorHelper.Parse(config.ContentBorderColor, new Color(180, 140, 80));
            _scrollBarColor     = ColorHelper.Parse(config.ScrollBarColor,     new Color(180, 140, 80));

            CalculateBounds();

            _smallFontLineH = (int)Game1.smallFont.MeasureString("A").Y;
            _titleFontLineH = (int)(Game1.dialogueFont.MeasureString("A").Y * SectionTitleScale);

            CreateScrollButtons();
            RebuildFilter();
        }

        /// <summary>
        /// Called when the game window is resized. Re-computes the menu's position, all internal
        /// layout rectangles, scroll buttons, and content measurements so the UI fits the new viewport.
        /// </summary>
        public void Reinitialize()
        {
            // Re-anchor the IClickableMenu base position to the new viewport
            xPositionOnScreen = (int)(Game1.uiViewport.Width  * (1f - MenuScale) / 2f);
            yPositionOnScreen = (int)(Game1.uiViewport.Height * (1f - MenuScale) / 2f);
            width             = (int)(Game1.uiViewport.Width  * MenuScale);
            height            = (int)(Game1.uiViewport.Height * MenuScale);

            // Reposition the close button that IClickableMenu places in the top-right
            if (upperRightCloseButton != null)
            {
                upperRightCloseButton.bounds.X = xPositionOnScreen + width - 36;
                upperRightCloseButton.bounds.Y = yPositionOnScreen - 8;
            }

            // Rebuild every derived rectangle (sidebar, tabs, content, search bars, scroll buttons)
            CalculateBounds();
            CreateScrollButtons();

            // Remeasure content for the new content-area width
            RebuildPageState();

            // Clamp scroll offsets that may now be out of range
            int maxContent = Math.Max(0, _totalContentHeight - _contentBounds.Height + Padding);
            _contentScrollOffset = Math.Clamp(_contentScrollOffset, 0, maxContent);

            int maxTab = Math.Max(0, _tabsTotalWidth - _tabsBounds.Width);
            _tabScrollOffset = Math.Clamp(_tabScrollOffset, 0, maxTab);

            int maxSidebar = Math.Max(0, _filteredMods.Count * SidebarItemHeight - _sidebarBounds.Height);
            _sidebarScrollOffset = Math.Clamp(_sidebarScrollOffset, 0, maxSidebar);
        }

        // ── Font helpers ──────────────────────────────────────────────────────────

        private (float drawScale, int lineH) SmallFontParams(int? fontSizeOverride, int defaultPx)
        {
            float targetPx = fontSizeOverride ?? defaultPx;
            float scale    = targetPx / SmallFontNaturalPx;
            int   lineH    = (int)Math.Round(_smallFontLineH * scale) + 2;
            return (scale, lineH);
        }

        private (float drawScale, int lineH) TitleFontParams(int? fontSizeOverride)
        {
            float targetPx = fontSizeOverride ?? DialogueFontNaturalPx;
            float scale    = (targetPx / DialogueFontNaturalPx) * SectionTitleScale;
            int   lineH    = (int)Math.Round(_titleFontLineH * (targetPx / DialogueFontNaturalPx)) + 4;
            return (scale, lineH);
        }

        private static int ScaledWrapWidth(int maxWidth, float drawScale)
            => (int)Math.Floor(maxWidth / drawScale);

        // ── Bounds ────────────────────────────────────────────────────────────────

        private void CalculateBounds()
        {
            // Sidebar search bar (top-left column)
            _searchBarBounds = new Rectangle(
                xPositionOnScreen + Padding,
                yPositionOnScreen + Padding + 60,
                SidebarWidth,
                SearchBarHeight);

            _sidebarBounds = new Rectangle(
                xPositionOnScreen + Padding,
                _searchBarBounds.Bottom + Padding / 2,
                SidebarWidth,
                height - Padding * 2 - 60 - SearchBarHeight - Padding / 2);

            int contentX     = xPositionOnScreen + Padding + SidebarWidth + Padding;
            int contentWidth = width - SidebarWidth - Padding * 3;

            // Content search bar lives at the right of the tab row.
            // Prev / Next buttons: two small squares to the right of the bar.
            const int NavBtnSize    = SearchBarHeight;
            const int ContentBarW   = 220;
            const int CounterW      = 64;   // "99/99" label width
            int       navGroupW     = ContentBarW + 4 + CounterW + 4 + NavBtnSize + 4 + NavBtnSize;
            int       barRight      = contentX + contentWidth - ScrollButtonSize - 8; // respect scroll-button column
            int       navGroupX     = barRight - navGroupW;

            _contentSearchBarBounds = new Rectangle(
                navGroupX,
                yPositionOnScreen + Padding + 60,
                ContentBarW,
                SearchBarHeight);

            // Counter label placeholder rect (used for hit-test exclusion only)
            int counterX = _contentSearchBarBounds.Right + 4;

            _prevMatchButtonBounds = new Rectangle(
                counterX + CounterW + 4,
                yPositionOnScreen + Padding + 60,
                NavBtnSize,
                NavBtnSize);

            _nextMatchButtonBounds = new Rectangle(
                _prevMatchButtonBounds.Right + 4,
                yPositionOnScreen + Padding + 60,
                NavBtnSize,
                NavBtnSize);

            // Tabs row: only use the left portion (up to just before the nav group)
            int tabsWidth = navGroupX - contentX - Padding;
            _tabsBounds = new Rectangle(contentX, yPositionOnScreen + Padding + 60, tabsWidth, TabHeight);

            _contentBounds = new Rectangle(
                contentX, _tabsBounds.Bottom + 4,
                contentWidth - ScrollButtonSize - 8,
                height - Padding * 2 - 60 - TabHeight - 4);

            _scrollUpBounds   = new Rectangle(_contentBounds.Right + 8, _contentBounds.Top,               ScrollButtonSize, ScrollButtonSize);
            _scrollDownBounds = new Rectangle(_contentBounds.Right + 8, _contentBounds.Bottom - ScrollButtonSize, ScrollButtonSize, ScrollButtonSize);
        }

        private void CreateScrollButtons()
        {
            _scrollUpButton = new ClickableTextureComponent(
                _scrollUpBounds, Game1.mouseCursors, new Rectangle(421, 459, 11, 12), (float)ScrollButtonSize / 11);
            _scrollDownButton = new ClickableTextureComponent(
                _scrollDownBounds, Game1.mouseCursors, new Rectangle(421, 472, 11, 12), (float)ScrollButtonSize / 11);
        }

        // ── Mod / page selection ──────────────────────────────────────────────────

        private void SelectMod(int filteredIndex)
        {
            if (filteredIndex < 0 || filteredIndex >= _filteredMods.Count) return;
            _selectedModIndex    = filteredIndex;
            _selectedMod         = _filteredMods[filteredIndex];
            _contentScrollOffset = 0;
            _selectedPage        = null;
            ClearContentSearch();
            RebuildPageState();
        }

        private void SelectPage(DocumentationPage page)
        {
            _selectedPage        = page;
            _contentScrollOffset = 0;
            MeasureContentHeight();
            // Ensure the selected tab is visible in the scrollable strip
            int pageIndex = -1;
            for (int i = 0; i < _pages.Count; i++)
                if (_pages[i] == page) { pageIndex = i; break; }
            if (pageIndex >= 0) ScrollTabToVisible(pageIndex);
        }

        /// <summary>Adjust <see cref="_tabScrollOffset"/> so the tab at <paramref name="index"/> is fully visible.</summary>
        private void ScrollTabToVisible(int index)
        {
            if (index < 0 || index >= _tabBounds.Length) return;
            int maxTabScroll = Math.Max(0, _tabsTotalWidth - _tabsBounds.Width);
            int tabL = _tabBounds[index].X;                          // relative left edge
            int tabR = _tabBounds[index].X + _tabBounds[index].Width; // relative right edge
            // Scroll left if tab is cut off on the left
            if (tabL < _tabScrollOffset)
                _tabScrollOffset = Math.Max(0, tabL);
            // Scroll right if tab is cut off on the right
            else if (tabR > _tabScrollOffset + _tabsBounds.Width)
                _tabScrollOffset = Math.Min(maxTabScroll, tabR - _tabsBounds.Width);
        }

        private void NavigateTo(string resolvedModId, string? targetPageId, string? targetAnchor)
        {
            ModDocumentation? targetMod = null;
            foreach (var m in _mods)
                if (string.Equals(m.UniqueId, resolvedModId, StringComparison.OrdinalIgnoreCase))
                    { targetMod = m; break; }

            if (targetMod == null) { Game1.playSound("cancel"); return; }

            int filteredIndex = -1;
            for (int i = 0; i < _filteredMods.Count; i++)
                if (string.Equals(_filteredMods[i].UniqueId, resolvedModId, StringComparison.OrdinalIgnoreCase))
                    { filteredIndex = i; break; }

            if (filteredIndex < 0)
            {
                _searchQuery = string.Empty;
                RebuildFilter();
                for (int i = 0; i < _filteredMods.Count; i++)
                    if (string.Equals(_filteredMods[i].UniqueId, resolvedModId, StringComparison.OrdinalIgnoreCase))
                        { filteredIndex = i; break; }
            }

            if (filteredIndex < 0) { Game1.playSound("cancel"); return; }

            if (filteredIndex != _selectedModIndex) SelectMod(filteredIndex);

            if (targetPageId != null)
            {
                var targetDoc  = _filteredMods[filteredIndex];
                var targetPage = targetDoc.GetPage(targetPageId);
                if (targetPage != null && targetPage != _selectedPage)
                    SelectPage(targetPage);
            }

            if (targetAnchor != null && _selectedPage != null)
            {
                int offset    = ComputeAnchorScrollOffset(targetAnchor);
                int maxScroll = Math.Max(0, _totalContentHeight - _contentBounds.Height + Padding);
                _contentScrollOffset = Math.Clamp(offset, 0, maxScroll);
            }

            Game1.playSound("smallSelect");
        }

        private int ComputeAnchorScrollOffset(string anchor)
        {
            if (_selectedPage == null || _entryHeights.Length == 0) return 0;
            int y       = _headerImageHeight > 0 ? _headerImageHeight + Padding / 2 : 0;
            var entries = _selectedPage.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] is IAnchorable anchorable &&
                    string.Equals(anchorable.Anchor, anchor, StringComparison.OrdinalIgnoreCase))
                    return y;
                y += _entryHeights[i] + Padding / 2;
            }
            return 0;
        }

        // ── Sidebar filter ────────────────────────────────────────────────────────

        private void RebuildFilter()
        {
            var previouslySelected = _selectedMod;
            _filteredMods.Clear();
            _sidebarScrollOffset = 0;

            if (string.IsNullOrWhiteSpace(_searchQuery))
                foreach (var m in _mods) _filteredMods.Add(m);
            else
            {
                string q = _searchQuery.Trim();
                foreach (var m in _mods)
                    if (m.GetName().Contains(q, StringComparison.OrdinalIgnoreCase))
                        _filteredMods.Add(m);
            }

            if (previouslySelected != null)
            {
                for (int i = 0; i < _filteredMods.Count; i++)
                {
                    if (string.Equals(_filteredMods[i].UniqueId, previouslySelected.UniqueId, StringComparison.OrdinalIgnoreCase))
                    {
                        _selectedModIndex = i;
                        _selectedMod      = _filteredMods[i];
                        return;
                    }
                }
            }

            _selectedMod  = null;
            _selectedPage = null;
            if (_filteredMods.Count > 0) SelectMod(0);
            else _selectedModIndex = -1;
        }

        private void RebuildPageState()
        {
            _pages        = _selectedMod?.GetAllPages() ?? Array.Empty<DocumentationPage>();
            _selectedPage = _pages.Count > 0 ? _pages[0] : null;

            _tabBounds = new Rectangle[_pages.Count];
            _tabScrollOffset = 0;
            int   tabX     = 0;   // stored relative to strip origin; offset applied at draw/hit-test
            float tabScale = _fontSettingsActive ? 1.025f : 1f;

            for (int i = 0; i < _pages.Count; i++)
            {
                int naturalW = (int)((Game1.smallFont.MeasureString(_pages[i].GetPageName()).X + Padding * 2) * tabScale);
                int maxTabW  = (int)(_tabsBounds.Width * 0.4f * tabScale);
                int w        = Math.Min(naturalW, maxTabW);
                _tabBounds[i] = new Rectangle(tabX, _tabsBounds.Y, w, TabHeight);
                tabX += w + 4;
            }
            _tabsTotalWidth = tabX > 0 ? tabX - 4 : 0;  // strip minus trailing gap

            MeasureContentHeight();
        }

        private void MeasureContentHeight()
        {
            if (_selectedPage == null)
            {
                _totalContentHeight = 0;
                _headerImageHeight  = 0;
                _entryHeights       = Array.Empty<int>();
                return;
            }

            int maxWidth = _contentBounds.Width - Padding * 2;
            int y        = 0;

            _headerImageHeight = _selectedPage.HeaderImage != null
                ? MeasureHeaderImageHeight(_selectedPage.HeaderImage) : 0;
            if (_headerImageHeight > 0) y += _headerImageHeight + Padding / 2;

            var entries = _selectedPage.Entries;
            _entryHeights = new int[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                int h = MeasureEntryHeight(entries[i], maxWidth);
                _entryHeights[i] = h;
                y += h + Padding / 2;
            }
            _totalContentHeight = y;
        }

        // ── Content search ────────────────────────────────────────────────────────

        private void ClearContentSearch()
        {
            _contentSearchQuery = string.Empty;
            _matches.Clear();
            _matchIndex = -1;
        }

        /// <summary>Strip inline item/emote tokens and return plain text for searching.</summary>
        private static string ExtractPlainText(string raw)
            => InlineTokenPattern.Replace(raw, string.Empty);

        /// <summary>
        /// Extract searchable plain text from a single entry.
        /// Returns null for entries with no text (Image, Gif, Divider, Spacer, HeaderImage).
        /// Does NOT recurse into children — call recursively where needed.
        /// </summary>
        private static string? GetEntryText(IDocumentationEntry entry) => entry.Type switch
        {
            EntryType.SectionTitle  => ExtractPlainText(((SectionTitleEntry)entry).GetText()),
            EntryType.Paragraph     => ExtractPlainText(((ParagraphEntry)entry).GetText()),
            EntryType.Caption       => ExtractPlainText(((CaptionEntry)entry).GetText()),
            EntryType.KeyValuePair  => ExtractPlainText(((KeyValuePairEntry)entry).GetKey() + " " + ((KeyValuePairEntry)entry).GetValue()),
            EntryType.Link          => ExtractPlainText(((LinkEntry)entry).GetLabel()),
            EntryType.InternalLink  => ExtractPlainText(((InternalLinkEntry)entry).GetLabel()),
            EntryType.List          => ExtractPlainText(string.Join(" ", ((ListEntry)entry).Items.Select(f => f()))),
            EntryType.Spoiler       => ExtractPlainText(((SpoilerEntry)entry).GetLabel()),   // children handled by caller
            _                       => null
        };

        /// <summary>
        /// Rebuild <see cref="_matches"/> by scanning all pages of the selected mod.
        /// </summary>
        private void CollectMatches()
        {
            _matches.Clear();
            _matchIndex = -1;

            if (_selectedMod == null || string.IsNullOrWhiteSpace(_contentSearchQuery)) return;

            string q = _contentSearchQuery.Trim();
            if (q.Length == 0) return;

            for (int pi = 0; pi < _pages.Count; pi++)
            {
                var page    = _pages[pi];
                var entries = page.Entries;
                for (int ei = 0; ei < entries.Count; ei++)
                    CollectFromEntry(entries[ei], q, pi, ei, new List<int>());
            }

            if (_matches.Count > 0) _matchIndex = 0;
        }

        private void CollectFromEntry(IDocumentationEntry entry, string q, int pi, int ei, List<int> childPath)
        {
            // Check this entry's own text
            string? text = GetEntryText(entry);
            if (text != null && text.Contains(q, StringComparison.OrdinalIgnoreCase))
                _matches.Add(new SearchMatch(pi, ei, new List<int>(childPath)));

            // Recurse into container entries
            switch (entry.Type)
            {
                case EntryType.Spoiler:
                {
                    var children = ((SpoilerEntry)entry).Children;
                    for (int ci = 0; ci < children.Count; ci++)
                    {
                        var cp = new List<int>(childPath) { ci };
                        CollectFromEntry(children[ci], q, pi, ei, cp);
                    }
                    break;
                }
                case EntryType.Row:
                {
                    var row = (RowEntry)entry;
                    for (int ci = 0; ci < row.LeftEntries.Count; ci++)
                    {
                        var cp = new List<int>(childPath) { ci };
                        CollectFromEntry(row.LeftEntries[ci], q, pi, ei, cp);
                    }
                    for (int ci = 0; ci < row.RightEntries.Count; ci++)
                    {
                        var cp = new List<int>(childPath) { -(ci + 1) }; // negative = right column
                        CollectFromEntry(row.RightEntries[ci], q, pi, ei, cp);
                    }
                    break;
                }
                case EntryType.IndentBlock:
                {
                    var children = ((IndentBlockEntry)entry).Children;
                    for (int ci = 0; ci < children.Count; ci++)
                    {
                        var cp = new List<int>(childPath) { ci };
                        CollectFromEntry(children[ci], q, pi, ei, cp);
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// Jump the viewer to the match at <see cref="_matchIndex"/>.
        /// Switches pages and auto-expands Spoilers as needed.
        /// </summary>
        private void JumpToCurrentMatch()
        {
            if (_matches.Count == 0 || _matchIndex < 0) return;
            var match = _matches[_matchIndex];

            // Switch page if needed
            if (match.PageIndex < _pages.Count && _pages[match.PageIndex] != _selectedPage)
                SelectPage(_pages[match.PageIndex]);

            // Auto-expand any Spoiler on the path
            if (_selectedPage != null && match.ChildPath.Count > 0)
            {
                var entries = _selectedPage.Entries;
                if (match.EntryIndex < entries.Count && entries[match.EntryIndex] is SpoilerEntry spoiler)
                {
                    if (!spoiler.IsRevealed)
                    {
                        spoiler.IsRevealed = true;
                        MeasureContentHeight();
                    }
                }
            }

            // Compute scroll offset to the matching entry
            int targetY = ComputeEntryScrollOffset(match.EntryIndex);
            int maxScroll = Math.Max(0, _totalContentHeight - _contentBounds.Height + Padding);
            _contentScrollOffset = Math.Clamp(targetY, 0, maxScroll);
        }

        /// <summary>Returns the Y pixel offset (from top of content area) for entry at <paramref name="entryIndex"/>.</summary>
        private int ComputeEntryScrollOffset(int entryIndex)
        {
            int y = _headerImageHeight > 0 ? _headerImageHeight + Padding / 2 : 0;
            for (int i = 0; i < entryIndex && i < _entryHeights.Length; i++)
                y += _entryHeights[i] + Padding / 2;
            return y;
        }

        private void StepMatch(int delta)
        {
            if (_matches.Count == 0) return;
            _matchIndex = (_matchIndex + delta + _matches.Count) % _matches.Count;
            JumpToCurrentMatch();
            Game1.playSound("smallSelect");
        }

        // ── update ────────────────────────────────────────────────────────────────

        public override void update(GameTime time)
        {
            base.update(time);

            double dt = time.ElapsedGameTime.TotalSeconds;

            if (_activeSearchBar == 1)
            {
                _cursorBlinkTimer += dt;
                if (_cursorBlinkTimer >= 0.53) { _cursorBlinkTimer = 0; _cursorVisible = !_cursorVisible; }
            }
            if (_activeSearchBar == 2)
            {
                _contentCursorBlinkTimer += dt;
                if (_contentCursorBlinkTimer >= 0.53) { _contentCursorBlinkTimer = 0; _contentCursorVisible = !_contentCursorVisible; }
            }

            // Re-collect matches deferred from a spoiler toggle so we're outside the draw/click stack
            if (_matchesDirty && _contentSearchQuery.Length > 0)
            {
                _matchesDirty = false;
                int savedIndex = _matchIndex;
                CollectMatches();
                _matchIndex = _matches.Count > 0
                    ? Math.Clamp(savedIndex, 0, _matches.Count - 1)
                    : -1;
            }

            if (_selectedPage == null) return;
            TickGifEntries(_selectedPage.Entries, dt);
        }

        private static void TickGifEntries(IReadOnlyList<IDocumentationEntry> entries, double dt)
        {
            foreach (var entry in entries)
            {
                if (entry is GifEntry gif)
                {
                    gif.ElapsedSeconds += dt;
                    while (gif.ElapsedSeconds >= gif.FrameDuration)
                    {
                        gif.ElapsedSeconds -= gif.FrameDuration;
                        gif.CurrentFrame    = (gif.CurrentFrame + 1) % gif.FrameCount;
                    }
                }
                else if (entry is RowEntry row)
                {
                    TickGifEntries(row.LeftEntries,  dt);
                    TickGifEntries(row.RightEntries, dt);
                }
                else if (entry is IndentBlockEntry indent)
                {
                    TickGifEntries(indent.Children, dt);
                }
            }
        }

        // ── Measure helpers ───────────────────────────────────────────────────────

        private int MeasureHeaderImageHeight(HeaderImageEntry entry)
        {
            var tex = entry.TryGetTexture();
            if (tex == null) return 0;
            var rect = entry.SourceRect ?? new Rectangle(0, 0, tex.Width, tex.Height);
            if (rect.Width == 0) return 0;
            return (int)(_contentBounds.Width * ((float)rect.Height / rect.Width));
        }

        private int MeasureEntryHeight(IDocumentationEntry entry, int maxWidth) => entry.Type switch
        {
            EntryType.SectionTitle  => MeasureSectionTitleHeight((SectionTitleEntry)entry, maxWidth),
            EntryType.Paragraph     => MeasureScaledTextHeight(((ParagraphEntry)entry).GetText(),   maxWidth, ((ParagraphEntry)entry).FontSize,   (int)SmallFontNaturalPx),
            EntryType.Caption       => MeasureScaledTextHeight(((CaptionEntry)entry).GetText(),     maxWidth, ((CaptionEntry)entry).FontSize,     (int)SmallFontNaturalPx),
            EntryType.Image         => MeasureImageHeight((ImageEntry)entry, maxWidth),
            EntryType.KeyValuePair  => MeasureKeyValueHeight((KeyValuePairEntry)entry, maxWidth),
            EntryType.Divider       => MeasureDividerHeight((DividerEntry)entry),
            EntryType.Spacer        => ((SpacerEntry)entry).Height,
            EntryType.List          => MeasureListHeight((ListEntry)entry, maxWidth),
            EntryType.Spoiler       => MeasureSpoilerHeight((SpoilerEntry)entry, maxWidth),
            EntryType.Row           => MeasureRowHeight((RowEntry)entry, maxWidth),
            EntryType.Link          => _smallFontLineH + 2,
            EntryType.InternalLink  => _smallFontLineH + 2,
            EntryType.Gif           => MeasureGifHeight((GifEntry)entry),
            EntryType.IndentBlock   => MeasureIndentBlockHeight((IndentBlockEntry)entry, maxWidth),
            _                       => 0
        };

        private int MeasureSectionTitleHeight(SectionTitleEntry entry, int maxWidth)
        {
            var (drawScale, lineH) = TitleFontParams(entry.FontSize);
            int wrapWidth = ScaledWrapWidth(maxWidth, drawScale);
            int lineCount = InlineParser.WrapRich(entry.GetText(), Game1.dialogueFont, wrapWidth, _titleFontLineH).Count;
            return Math.Max(1, lineCount) * lineH;
        }

        private int MeasureScaledTextHeight(string text, int maxWidth, int? fontSizeOverride, int defaultPx)
        {
            var (drawScale, lineH) = SmallFontParams(fontSizeOverride, defaultPx);
            int wrapWidth = ScaledWrapWidth(maxWidth, drawScale);
            var lines     = InlineParser.WrapRich(text, Game1.smallFont, wrapWidth, _smallFontLineH);
            return lines.Count * lineH;
        }

        private int MeasureImageHeight(ImageEntry entry, int maxWidth)
        {
            var tex = entry.TryGetTexture();
            if (tex == null) return 0;
            var src = entry.SourceRect ?? new Rectangle(0, 0, tex.Width, tex.Height);
            if (src.Width == 0) return 0;

            if (!entry.HasFloatLayout)
            {
                int scaledW = (int)Math.Round(src.Width  * entry.Scale);
                int scaledH = (int)Math.Round(src.Height * entry.Scale);
                if (scaledW > maxWidth)
                    scaledH = (int)Math.Round(scaledH * ((double)maxWidth / scaledW));
                return Math.Max(1, scaledH);
            }

            int imgH     = (int)Math.Round(src.Height * entry.Scale);
            int imgW     = (int)Math.Round(src.Width  * entry.Scale);
            int listW    = maxWidth - imgW - ImageEntry.Gutter;
            int lineH    = _smallFontLineH + 2;
            int indented = Math.Max(1, listW - ImageEntry.BulletIndent);
            int listH    = 0;
            for (int i = 0; i < entry.Items!.Count; i++)
            {
                int lc = WrapRich(entry.Items[i](), Game1.smallFont, indented).Count;
                listH += Math.Max(1, lc) * lineH;
                if (i < entry.Items.Count - 1) listH += ListItemGap;
            }
            return Math.Max(imgH, listH);
        }

        private int MeasureGifHeight(GifEntry entry)
        {
            var tex = entry.TryGetTexture();
            return tex == null ? 0 : entry.GetScaledSize().h;
        }

        private static int MeasureDividerHeight(DividerEntry entry) => entry.Style switch
        {
            DividerStyle.Double => 16,
            _                  => 12
        };

        private int MeasureListHeight(ListEntry entry, int maxWidth)
        {
            var (drawScale, lineH) = SmallFontParams(entry.FontSize, (int)SmallFontNaturalPx);
            int indented  = maxWidth - ListEntry.BulletIndent;
            int wrapWidth = ScaledWrapWidth(indented, drawScale);
            int total     = 0;
            for (int i = 0; i < entry.Items.Count; i++)
            {
                int lineCount = InlineParser.WrapRich(entry.Items[i](), Game1.smallFont, wrapWidth, _smallFontLineH).Count;
                total += Math.Max(1, lineCount) * lineH;
                if (i < entry.Items.Count - 1) total += ListItemGap;
            }
            return total;
        }

        private int MeasureKeyValueHeight(KeyValuePairEntry entry, int maxWidth)
        {
            var (drawScale, lineH) = SmallFontParams(entry.FontSize, (int)SmallFontNaturalPx);
            float arrowW   = Game1.smallFont.MeasureString(":").X * drawScale;
            float keyW     = Game1.smallFont.MeasureString(entry.GetKey()).X * drawScale;
            float arrowGap = 8f * drawScale;
            float valX     = keyW + arrowGap + arrowW + arrowGap;
            int   valWidth = Math.Max(1, (int)(maxWidth - valX));
            int   wrapWidth = ScaledWrapWidth(valWidth, drawScale);
            int   lineCount = InlineParser.WrapRich(entry.GetValue(), Game1.smallFont, wrapWidth, _smallFontLineH).Count;
            return Math.Max(1, lineCount) * lineH;
        }

        private int MeasureSpoilerHeight(SpoilerEntry entry, int maxWidth)
        {
            int h = SpoilerEntry.HeaderHeight;
            if (entry.IsRevealed && entry.Children.Count > 0)
            {
                int innerW = maxWidth - SpoilerEntry.ChildPadding * 2;
                h += SpoilerEntry.ChildPadding + MeasureColumnHeight(entry.Children, Math.Max(1, innerW)) + SpoilerEntry.ChildPadding;
            }
            return h;
        }

        private int MeasureRowHeight(RowEntry entry, int maxWidth)
        {
            int leftW  = (int)Math.Round(maxWidth * entry.LeftFraction) - RowEntry.ColumnGap / 2;
            int rightW = maxWidth - leftW - RowEntry.ColumnGap;
            return Math.Max(
                MeasureColumnHeight(entry.LeftEntries,  Math.Max(1, leftW)),
                MeasureColumnHeight(entry.RightEntries, Math.Max(1, rightW)));
        }

        private int MeasureIndentBlockHeight(IndentBlockEntry entry, int maxWidth)
            => MeasureColumnHeight(entry.Children, Math.Max(1, maxWidth - entry.IndentAmount));

        private int MeasureColumnHeight(IReadOnlyList<IDocumentationEntry> entries, int maxWidth)
        {
            int total = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                total += MeasureEntryHeight(entries[i], maxWidth);
                if (i < entries.Count - 1) total += Padding / 2;
            }
            return total;
        }

        // ── Draw ──────────────────────────────────────────────────────────────────

        public override void draw(SpriteBatch b)
        {
            try
            {
                _tabHoverText = null;

                drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                    xPositionOnScreen, yPositionOnScreen, width, height, Color.White, drawShadow: true);

                DrawTitle(b);
                DrawSearchBar(b);

                if (_selectedMod != null)
                    DrawContentSearchBar(b);

                DrawSidebar(b);

                b.Draw(Game1.fadeToBlackRect,
                    new Rectangle(_sidebarBounds.Right + Padding / 2, _searchBarBounds.Top, 2,
                        _searchBarBounds.Height + Padding / 2 + _sidebarBounds.Height),
                    DividerColor * 0.5f);

                if (_selectedMod != null)
                {
                    DrawPageTabs(b);
                    DrawContent(b);
                    DrawScrollButtons(b);
                }
                else
                {
                    DrawEmptyState(b);
                }

                upperRightCloseButton?.draw(b);

                if (_tabHoverText != null)
                    drawHoverText(b, _tabHoverText, Game1.smallFont);

                drawMouse(b);
            }
            catch (Exception ex)
            {
                _monitor?.Log($"draw() failed: matches={_matches.Count} matchIndex={_matchIndex} query=[{_contentSearchQuery}] selectedMod=[{_selectedMod?.UniqueId}] selectedPage=[{_selectedPage?.PageId}] entryHeightsLen={_entryHeights.Length}\n{ex}", LogLevel.Error);
                throw;
            }
        }

        private void DrawTitle(SpriteBatch b)
        {
            var   font   = Game1.dialogueFont;
            float titleY = yPositionOnScreen + Padding + (60 - font.MeasureString("A").Y) / 2f;
            int   maxWidth = width - Padding * 4;
            string baseTitle = _i18n.Get("ui.title");

            if (_selectedMod == null)
            {
                float baseW  = font.MeasureString(baseTitle).X;
                float titleX = xPositionOnScreen + (width - baseW) / 2f;
                Utility.drawTextWithShadow(b, baseTitle, font, new Vector2(titleX, titleY), Game1.textColor);
                return;
            }

            const string separator = "  /  ";
            float baseW2   = font.MeasureString(baseTitle).X;
            float sepW     = font.MeasureString(separator).X;
            float modBudget = maxWidth - baseW2 - sepW;
            string modName = TruncateWithEllipsis(_selectedMod.UniqueId, font, Math.Max(1, (int)modBudget));
            float modW     = font.MeasureString(modName).X;
            float totalW   = baseW2 + sepW + modW;
            float startX   = xPositionOnScreen + (width - totalW) / 2f;

            Utility.drawTextWithShadow(b, baseTitle, font, new Vector2(startX,                 titleY), Game1.textColor);
            Utility.drawTextWithShadow(b, separator, font, new Vector2(startX + baseW2,        titleY), Game1.textColor * 0.45f);
            Utility.drawTextWithShadow(b, modName,   font, new Vector2(startX + baseW2 + sepW, titleY), Game1.textColor * 0.75f);
        }

        // Sidebar search bar
        private void DrawSearchBar(SpriteBatch b)
        {
            var  r       = _searchBarBounds;
            bool active  = _activeSearchBar == 1;

            Color bgColor     = active ? new Color(245, 235, 210) * 0.95f : new Color(220, 200, 160) * 0.55f;
            Color borderColor = active ? _accentColor : DividerColor * 0.6f;
            b.Draw(Game1.fadeToBlackRect, r, bgColor);
            DrawBorder(b, r, 2, borderColor);

            const int iconSize = 20;
            var       iconSrc  = new Rectangle(80, 0, 13, 13);
            float     iconScale = iconSize / 13f;
            int       iconX    = r.X + 8;
            int       iconY    = r.Y + (r.Height - iconSize) / 2;
            b.Draw(Game1.mouseCursors, new Rectangle(iconX, iconY, iconSize, iconSize), iconSrc, Color.White * 0.7f);

            int   textX    = iconX + iconSize + 6;
            int   textMaxW = r.Right - textX - Padding;
            float textY    = r.Y + (r.Height - _smallFontLineH) / 2f;

            if (_searchQuery.Length == 0 && !active)
            {
                string placeholder = _i18n.Get("ui.search-placeholder");
                string clipped     = TruncateWithEllipsis(placeholder, Game1.smallFont, textMaxW);
                b.DrawString(Game1.smallFont, clipped, new Vector2(textX, textY), Game1.textColor * 0.4f);
            }
            else
            {
                string display = TruncateWithEllipsis(_searchQuery, Game1.smallFont, textMaxW - (active ? 10 : 0));
                b.DrawString(Game1.smallFont, display, new Vector2(textX, textY), Game1.textColor);
                if (active && _cursorVisible)
                {
                    float cursorX = textX + Game1.smallFont.MeasureString(display).X + 1;
                    b.Draw(Game1.fadeToBlackRect,
                        new Rectangle((int)cursorX, (int)textY + 2, 2, _smallFontLineH - 4),
                        Game1.textColor * 0.85f);
                }
            }

            if (_searchQuery.Length > 0)
            {
                const int clearSize = 16;
                int       clearX    = r.Right - clearSize - 6;
                int       clearY    = r.Y + (r.Height - clearSize) / 2;
                var       clearRect = new Rectangle(clearX, clearY, clearSize, clearSize);
                bool      clearHov  = clearRect.Contains(Game1.getMouseX(), Game1.getMouseY());
                b.Draw(Game1.fadeToBlackRect, clearRect, clearHov ? HoverColor * 0.6f : Color.Transparent);
                b.DrawString(Game1.smallFont, "×",
                    new Vector2(clearX + 2, clearY + (clearSize - _smallFontLineH) / 2f),
                    clearHov ? Color.White : Game1.textColor * 0.55f);
            }
        }

        // Content search bar + prev/next + counter
        private void DrawContentSearchBar(SpriteBatch b)
        {
            var  r      = _contentSearchBarBounds;
            bool active = _activeSearchBar == 2;

            Color bgColor     = active ? new Color(245, 235, 210) * 0.95f : new Color(220, 200, 160) * 0.55f;
            Color borderColor = active ? _accentColor : DividerColor * 0.6f;
            b.Draw(Game1.fadeToBlackRect, r, bgColor);
            DrawBorder(b, r, 2, borderColor);

            const int iconSize = 20;
            var       iconSrc  = new Rectangle(80, 0, 13, 13);
            int       iconX    = r.X + 8;
            int       iconY    = r.Y + (r.Height - iconSize) / 2;
            b.Draw(Game1.mouseCursors, new Rectangle(iconX, iconY, iconSize, iconSize), iconSrc, Color.White * 0.7f);

            int   textX    = iconX + iconSize + 6;
            int   textMaxW = r.Right - textX - Padding;
            float textY    = r.Y + (r.Height - _smallFontLineH) / 2f;

            if (_contentSearchQuery.Length == 0 && !active)
            {
                string placeholder = _i18n.Get("ui.content-search-placeholder");
                string clipped     = TruncateWithEllipsis(placeholder, Game1.smallFont, textMaxW);
                b.DrawString(Game1.smallFont, clipped, new Vector2(textX, textY), Game1.textColor * 0.4f);
            }
            else
            {
                string display = TruncateWithEllipsis(_contentSearchQuery, Game1.smallFont, textMaxW - (active ? 10 : 0));
                b.DrawString(Game1.smallFont, display, new Vector2(textX, textY), Game1.textColor);
                if (active && _contentCursorVisible)
                {
                    float cursorX = textX + Game1.smallFont.MeasureString(display).X + 1;
                    b.Draw(Game1.fadeToBlackRect,
                        new Rectangle((int)cursorX, (int)textY + 2, 2, _smallFontLineH - 4),
                        Game1.textColor * 0.85f);
                }
            }

            if (_contentSearchQuery.Length > 0)
            {
                const int clearSize = 16;
                int       clearX    = r.Right - clearSize - 6;
                int       clearY    = r.Y + (r.Height - clearSize) / 2;
                var       clearRect = new Rectangle(clearX, clearY, clearSize, clearSize);
                bool      clearHov  = clearRect.Contains(Game1.getMouseX(), Game1.getMouseY());
                b.Draw(Game1.fadeToBlackRect, clearRect, clearHov ? HoverColor * 0.6f : Color.Transparent);
                b.DrawString(Game1.smallFont, "×",
                    new Vector2(clearX + 2, clearY + (clearSize - _smallFontLineH) / 2f),
                    clearHov ? Color.White : Game1.textColor * 0.55f);
            }

            // ── Counter label ("3/11") ───────────────────────────────────────────
            {
                int  counterX = r.Right + 4;
                int  counterW = _prevMatchButtonBounds.X - counterX - 4;
                float counterY = r.Y + (r.Height - _smallFontLineH) / 2f;
                string label = _matches.Count == 0
                    ? (_contentSearchQuery.Length > 0 ? "0/0" : string.Empty)
                    : $"{_matchIndex + 1}/{_matches.Count}";
                if (label.Length > 0)
                {
                    float lw = Game1.smallFont.MeasureString(label).X;
                    float lx = counterX + Math.Max(0, (counterW - lw) / 2f);
                    Color lc = _matches.Count == 0 && _contentSearchQuery.Length > 0
                        ? new Color(180, 60, 40)
                        : Game1.textColor * 0.75f;
                    b.DrawString(Game1.smallFont, label, new Vector2(lx, counterY), lc);
                }
            }

            // ── Prev button (▲) ──────────────────────────────────────────────────
            DrawNavButton(b, _prevMatchButtonBounds, upArrow: true,  enabled: _matches.Count > 1);

            // ── Next button (▼) ──────────────────────────────────────────────────
            DrawNavButton(b, _nextMatchButtonBounds, upArrow: false, enabled: _matches.Count > 1);
        }

        private void DrawNavButton(SpriteBatch b, Rectangle rect, bool upArrow, bool enabled)
        {
            bool hov = enabled && rect.Contains(Game1.getMouseX(), Game1.getMouseY());
            Color bg = hov ? HoverColor * 0.45f : new Color(220, 200, 160) * 0.35f;
            b.Draw(Game1.fadeToBlackRect, rect, bg);
            DrawBorder(b, rect, 1, DividerColor * (enabled ? 0.5f : 0.2f));

            // Use Stardew's scroll arrow sprites
            var arrowSrc = upArrow
                ? new Rectangle(421, 459, 11, 12)
                : new Rectangle(421, 472, 11, 12);
            const int aw = 22, ah = 24;
            int ax = rect.X + (rect.Width  - aw) / 2;
            int ay = rect.Y + (rect.Height - ah) / 2;
            b.Draw(Game1.mouseCursors, new Rectangle(ax, ay, aw, ah), arrowSrc,
                enabled ? Color.White : Color.White * 0.3f);
        }

        private static void DrawBorder(SpriteBatch b, Rectangle r, int t, Color col)
        {
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X,          r.Y,           r.Width, t),        col);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X,          r.Bottom - t,  r.Width, t),        col);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X,          r.Y,           t,        r.Height), col);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.Right - t,  r.Y,           t,        r.Height), col);
        }

        private void DrawSidebar(SpriteBatch b)
        {
            if (_mods.Count == 0)
            {
                Utility.drawTextWithShadow(b, _i18n.Get("ui.no-mods-sidebar"), Game1.smallFont,
                    new Vector2(_sidebarBounds.X + Padding, _sidebarBounds.Y + Padding),
                    Game1.textColor * 0.6f);
                return;
            }

            if (_filteredMods.Count == 0)
            {
                string noResults = _i18n.Get("ui.search-no-results");
                Utility.drawTextWithShadow(b, noResults, Game1.smallFont,
                    new Vector2(_sidebarBounds.X + Padding, _sidebarBounds.Y + Padding),
                    Game1.textColor * 0.5f);
                return;
            }

            var savedScissor    = b.GraphicsDevice.ScissorRectangle;
            var savedRasterizer = b.GraphicsDevice.RasterizerState;

            b.End();
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, ScissorRasterizer);
            b.GraphicsDevice.ScissorRectangle = _sidebarBounds;

            int y = _sidebarBounds.Y - _sidebarScrollOffset;
            for (int i = 0; i < _filteredMods.Count; i++)
            {
                var mod        = _filteredMods[i];
                var itemBounds = new Rectangle(_sidebarBounds.X, y, _sidebarBounds.Width, SidebarItemHeight);

                if (itemBounds.Bottom > _sidebarBounds.Y && itemBounds.Top < _sidebarBounds.Bottom)
                {
                    bool selected = i == _selectedModIndex;
                    bool hovered  = itemBounds.Contains(Game1.getMouseX(), Game1.getMouseY());

                    if (selected)     b.Draw(Game1.fadeToBlackRect, itemBounds, _accentColor);
                    else if (hovered) b.Draw(Game1.fadeToBlackRect, itemBounds, HoverColor * 0.3f);

                    // Show match count badge when searching content
                    string displayName = TruncateWithEllipsis(mod.GetName(), Game1.smallFont, itemBounds.Width - 24);
                    var namePos = new Vector2(itemBounds.X + 12, itemBounds.Y + 8);
                    if (selected) b.DrawString(Game1.smallFont, displayName, namePos, Color.White);
                    else          Utility.drawTextWithShadow(b, displayName, Game1.smallFont, namePos, Game1.textColor);

                    b.Draw(Game1.fadeToBlackRect,
                        new Rectangle(itemBounds.X + 8, itemBounds.Bottom - 1, itemBounds.Width - 16, 1),
                        DividerColor * 0.3f);
                }
                y += SidebarItemHeight;
            }

            b.End();
            b.GraphicsDevice.ScissorRectangle = savedScissor;
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, savedRasterizer);
        }

        private void DrawPageTabs(SpriteBatch b)
        {
            // ── Scissor the tab row so overflowing tabs are hidden ────────────────
            var savedScissor    = b.GraphicsDevice.ScissorRectangle;
            var savedRasterizer = b.GraphicsDevice.RasterizerState;

            b.End();
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, ScissorRasterizer);
            b.GraphicsDevice.ScissorRectangle = _tabsBounds;

            for (int i = 0; i < _pages.Count; i++)
            {
                var page = _pages[i];
                // Apply scroll offset: _tabBounds[i].X is relative to strip origin
                var tabBounds = new Rectangle(
                    _tabsBounds.X + _tabBounds[i].X - _tabScrollOffset,
                    _tabBounds[i].Y,
                    _tabBounds[i].Width,
                    _tabBounds[i].Height);

                // Skip tabs that are entirely outside the visible strip
                if (tabBounds.Right <= _tabsBounds.X || tabBounds.X >= _tabsBounds.Right)
                    continue;

                bool selected = page == _selectedPage;
                bool hovered  = tabBounds.Contains(Game1.getMouseX(), Game1.getMouseY())
                                && _tabsBounds.Contains(Game1.getMouseX(), Game1.getMouseY());

                if (selected)     b.Draw(Game1.fadeToBlackRect, tabBounds, _accentColor);
                else if (hovered) b.Draw(Game1.fadeToBlackRect, tabBounds, HoverColor * 0.3f);

                // Dot indicator when this page has matches
                bool hasMatches = false;
                if (_matches.Count > 0)
                    foreach (var m in _matches)
                        if (m.PageIndex == i) { hasMatches = true; break; }

                if (hasMatches)
                {
                    const int dotR = 4;
                    b.Draw(Game1.fadeToBlackRect,
                        new Rectangle(tabBounds.X + 4, tabBounds.Y + 4, dotR * 2, dotR * 2),
                        selected ? Color.White * 0.8f : SearchHighlightColor);
                }

                int    innerW   = tabBounds.Width - Padding * 2;
                string fullName = page.GetPageName();
                string tabLabel = TruncateWithEllipsis(fullName, Game1.smallFont, innerW);
                float  nameH    = Game1.smallFont.MeasureString(tabLabel).Y;
                Color  txtColor = selected ? Color.White : Game1.textColor;
                b.DrawString(Game1.smallFont, tabLabel,
                    new Vector2(tabBounds.X + Padding, tabBounds.Y + (TabHeight - nameH) / 2f),
                    txtColor);

                if (hovered && tabLabel != fullName)
                    _tabHoverText = fullName;
            }

            // ── Draw scroll-overflow fade indicators ─────────────────────────────
            bool canScrollLeft  = _tabScrollOffset > 0;
            bool canScrollRight = _tabsTotalWidth > _tabScrollOffset + _tabsBounds.Width;

            if (canScrollLeft)
            {
                // Left fade / arrow
                var fadeRect = new Rectangle(_tabsBounds.X, _tabsBounds.Y, 24, _tabsBounds.Height);
                b.Draw(Game1.fadeToBlackRect, fadeRect, new Color(50, 30, 10) * 0.35f);
                // Left arrow (◄) using Stardew scroll cursor
                b.Draw(Game1.mouseCursors,
                    new Rectangle(_tabsBounds.X + 2, _tabsBounds.Y + (_tabsBounds.Height - 16) / 2, 8, 16),
                    new Rectangle(421, 459, 11, 12),   // up-arrow sprite, rotated 90° via effect
                    Color.White * 0.85f, MathF.PI * -0.5f,
                    new Vector2(11f / 2f, 12f / 2f), SpriteEffects.None, 0f);
            }
            if (canScrollRight)
            {
                // Right fade / arrow
                var fadeRect = new Rectangle(_tabsBounds.Right - 24, _tabsBounds.Y, 24, _tabsBounds.Height);
                b.Draw(Game1.fadeToBlackRect, fadeRect, new Color(50, 30, 10) * 0.35f);
                b.Draw(Game1.mouseCursors,
                    new Rectangle(_tabsBounds.Right - 10, _tabsBounds.Y + (_tabsBounds.Height - 16) / 2, 8, 16),
                    new Rectangle(421, 459, 11, 12),
                    Color.White * 0.85f, MathF.PI * 0.5f,
                    new Vector2(11f / 2f, 12f / 2f), SpriteEffects.None, 0f);
            }

            b.End();
            b.GraphicsDevice.ScissorRectangle = savedScissor;
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, savedRasterizer);
        }

        private void DrawContent(SpriteBatch b)
        {
            if (_selectedPage == null) return;

            var savedScissor    = b.GraphicsDevice.ScissorRectangle;
            var savedRasterizer = b.GraphicsDevice.RasterizerState;

            b.End();
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, ScissorRasterizer);
            b.GraphicsDevice.ScissorRectangle = _contentBounds;

            int  y    = _contentBounds.Y + Padding - _contentScrollOffset;
            var  font = Game1.smallFont;

            if (_selectedPage.HeaderImage != null && _headerImageHeight > 0)
            {
                if (y + _headerImageHeight > _contentBounds.Y && y < _contentBounds.Bottom)
                {
                    var hi  = _selectedPage.HeaderImage;
                    var tex = hi.TryGetTexture();
                    if (tex != null)
                    {
                        var src = hi.SourceRect ?? new Rectangle(0, 0, tex.Width, tex.Height);
                        b.Draw(tex, new Rectangle(_contentBounds.X, y, _contentBounds.Width, _headerImageHeight), src, Color.White);
                    }
                }
                y += _headerImageHeight + Padding / 2;
            }

            // Determine which page index is currently displayed
            int currentPageIndex = -1;
            for (int pi = 0; pi < _pages.Count; pi++)
                if (_pages[pi] == _selectedPage) { currentPageIndex = pi; break; }

            var entries = _selectedPage.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                int  entryHeight = _entryHeights[i];
                bool visible     = (y + entryHeight) > _contentBounds.Y && y < _contentBounds.Bottom;

                // Is this entry the current jump target?
                bool isCurrentMatch = _matches.Count > 0
                    && _matchIndex >= 0
                    && _matches[_matchIndex].PageIndex  == currentPageIndex
                    && _matches[_matchIndex].EntryIndex == i
                    && _matches[_matchIndex].ChildPath.Count == 0;

                string? highlightQuery = (_contentSearchQuery.Length > 0) ? _contentSearchQuery : null;

                if (visible)
                {
                    // Draw a subtle background highlight behind the active match entry
                    if (isCurrentMatch)
                        b.Draw(Game1.fadeToBlackRect,
                            new Rectangle(_contentBounds.X, y, _contentBounds.Width, entryHeight),
                            SearchHighlightColor * 0.18f);

                    DrawEntry(b, entries[i], i, _contentBounds.X + Padding, ref y,
                        _contentBounds.Width - Padding * 2, font, highlightQuery);
                }
                else
                {
                    y += entryHeight;
                }

                y += Padding / 2;
            }

            b.End();
            b.GraphicsDevice.ScissorRectangle = savedScissor;
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, savedRasterizer);

            DrawContentBorder(b);

            if (_totalContentHeight > _contentBounds.Height)
            {
                float scrollPercent = Math.Clamp(
                    (float)_contentScrollOffset / (_totalContentHeight - _contentBounds.Height), 0f, 1f);
                int trackTop    = _scrollUpBounds.Bottom + 4;
                int trackBottom = _scrollDownBounds.Top  - 4;
                int trackHeight = trackBottom - trackTop;
                int thumbHeight = Math.Max(20, (int)(trackHeight * ((float)_contentBounds.Height / _totalContentHeight)));
                int thumbY      = trackTop + (int)((trackHeight - thumbHeight) * scrollPercent);
                b.Draw(Game1.fadeToBlackRect,
                    new Rectangle(_scrollUpBounds.X + 10, thumbY, _scrollUpBounds.Width - 20, thumbHeight),
                    _scrollBarColor);
            }
        }

        private void DrawContentBorder(SpriteBatch b)
        {
            var r = _contentBounds;
            int t = BorderThickness;
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X,         r.Y,          r.Width, t),        _contentBorderColor);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X,         r.Bottom - t, r.Width, t),        _contentBorderColor);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X,         r.Y,          t,        r.Height), _contentBorderColor);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.Right - t, r.Y,          t,        r.Height), _contentBorderColor);
        }

        private void DrawEntry(SpriteBatch b, IDocumentationEntry entry, int entryIndex,
            int x, ref int y, int maxWidth, SpriteFont font, string? highlightQuery = null)
        {
            switch (entry.Type)
            {
                case EntryType.SectionTitle:
                {
                    var e = (SectionTitleEntry)entry;
                    var (drawScale, lineH) = TitleFontParams(e.FontSize);
                    int wrapWidth = ScaledWrapWidth(maxWidth, drawScale);
                    var lines     = InlineParser.WrapRich(e.GetText(), Game1.dialogueFont, wrapWidth, _titleFontLineH);
                    foreach (var line in lines)
                    {
                        float lineW = InlineSegment.MeasureLineWidth(line, Game1.dialogueFont, _titleFontLineH) * drawScale;
                        float drawX = ComputeAlignedX(x, maxWidth, lineW, e.Alignment);
                        DrawRichLine(b, line, Game1.dialogueFont, (int)drawX, y, SectionTitleColor, drawScale, lineH, highlightQuery);
                        y += lineH;
                    }
                    break;
                }

                case EntryType.Paragraph:
                {
                    var e = (ParagraphEntry)entry;
                    var (drawScale, lineH) = SmallFontParams(e.FontSize, (int)SmallFontNaturalPx);
                    int wrapWidth = ScaledWrapWidth(maxWidth, drawScale);
                    var lines     = InlineParser.WrapRich(e.GetText(), font, wrapWidth, _smallFontLineH);
                    DrawRichLines(b, lines, font, x, ref y, maxWidth, Game1.textColor, e.Alignment, drawScale, lineH, highlightQuery);
                    break;
                }

                case EntryType.Caption:
                {
                    var e = (CaptionEntry)entry;
                    var (drawScale, lineH) = SmallFontParams(e.FontSize, (int)SmallFontNaturalPx);
                    int wrapWidth = ScaledWrapWidth(maxWidth, drawScale);
                    var lines     = InlineParser.WrapRich(e.GetText(), font, wrapWidth, _smallFontLineH);
                    DrawRichLines(b, lines, font, x, ref y, maxWidth, CaptionColor * 0.75f, e.Alignment, drawScale, lineH, highlightQuery);
                    break;
                }

                case EntryType.Image:
                {
                    var e       = (ImageEntry)entry;
                    int cachedH = entryIndex >= 0 ? _entryHeights[entryIndex] : MeasureImageHeight(e, maxWidth);
                    if (e.HasFloatLayout)
                        DrawFloatImage(b, e, x, ref y, maxWidth, font, cachedH, highlightQuery);
                    else
                    {
                        var tex = e.TryGetTexture();
                        if (tex != null)
                        {
                            var src  = e.SourceRect ?? new Rectangle(0, 0, tex.Width, tex.Height);
                            if (src.Width > 0)
                            {
                                int dstW = (int)Math.Round(src.Width  * e.Scale);
                                int dstH = (int)Math.Round(src.Height * e.Scale);
                                if (dstW > maxWidth) { dstH = (int)Math.Round(dstH * ((double)maxWidth / dstW)); dstW = maxWidth; }
                                float drawX = ComputeAlignedX(x, maxWidth, dstW, e.Alignment);
                                b.Draw(tex, new Rectangle((int)drawX, y, dstW, Math.Max(1, dstH)), src, Color.White);
                            }
                        }
                        y += cachedH;
                    }
                    break;
                }

                case EntryType.KeyValuePair:
                {
                    var e = (KeyValuePairEntry)entry;
                    var (drawScale, lineH) = SmallFontParams(e.FontSize, (int)SmallFontNaturalPx);
                    string arrow    = ":";
                    float  arrowW   = font.MeasureString(arrow).X * drawScale;
                    float  keyW     = font.MeasureString(e.GetKey()).X * drawScale;
                    float  arrowGap = 8f * drawScale;
                    float  valX     = x + keyW + arrowGap + arrowW + arrowGap;
                    int    valWidth  = Math.Max(1, (int)(x + maxWidth - valX));
                    int    wrapWidth = ScaledWrapWidth(valWidth, drawScale);

                    DrawHighlightedText(b, font, e.GetKey(), new Vector2(x, y), KeyColor, drawScale, highlightQuery);
                    b.DrawString(font, arrow, new Vector2(x + keyW + arrowGap, y), Game1.textColor * 0.5f,
                        0f, Vector2.Zero, drawScale, SpriteEffects.None, 0f);

                    var valLines = InlineParser.WrapRich(e.GetValue(), font, wrapWidth, _smallFontLineH);
                    int valY     = y;
                    DrawRichLines(b, valLines, font, (int)valX, ref valY, valWidth, Game1.textColor,
                        Alignment.Left, drawScale, lineH, highlightQuery);
                    y += MeasureKeyValueHeight(e, maxWidth);
                    break;
                }

                case EntryType.Divider:
                {
                    var e = (DividerEntry)entry;
                    DrawDivider(b, e.Style, x, y, maxWidth);
                    y += MeasureDividerHeight(e);
                    break;
                }

                case EntryType.Spacer:
                    y += ((SpacerEntry)entry).Height;
                    break;

                case EntryType.List:
                {
                    var e = (ListEntry)entry;
                    var (drawScale, lineH) = SmallFontParams(e.FontSize, (int)SmallFontNaturalPx);
                    int indented  = maxWidth - ListEntry.BulletIndent;
                    int wrapWidth = ScaledWrapWidth(indented, drawScale);

                    for (int i = 0; i < e.Items.Count; i++)
                    {
                        string prefix = e.IsOrdered ? $"{i + 1}." : "•";
                        var    lines  = InlineParser.WrapRich(e.Items[i](), font, wrapWidth, _smallFontLineH);

                        if (e.Alignment == Alignment.Left)
                        {
                            b.DrawString(font, prefix, new Vector2(x, y), Game1.textColor,
                                0f, Vector2.Zero, drawScale, SpriteEffects.None, 0f);
                            int bx = x + ListEntry.BulletIndent;
                            DrawRichLines(b, lines, font, bx, ref y, indented, Game1.textColor, Alignment.Left, drawScale, lineH, highlightQuery);
                        }
                        else
                        {
                            float firstLineW = lines.Count > 0 ? InlineSegment.MeasureLineWidth(lines[0], font, _smallFontLineH) * drawScale : 0;
                            float totalW     = ListEntry.BulletIndent * drawScale + firstLineW;
                            float baseX      = ComputeAlignedX(x, maxWidth, totalW, e.Alignment);
                            b.DrawString(font, prefix, new Vector2(baseX, y), Game1.textColor,
                                0f, Vector2.Zero, drawScale, SpriteEffects.None, 0f);
                            int bx = (int)(baseX + ListEntry.BulletIndent * drawScale);
                            DrawRichLines(b, lines, font, bx, ref y, indented, Game1.textColor, Alignment.Left, drawScale, lineH, highlightQuery);
                        }
                        if (i < e.Items.Count - 1) y += ListItemGap;
                    }
                    break;
                }

                case EntryType.Spoiler:
                    DrawSpoiler(b, (SpoilerEntry)entry, x, ref y, maxWidth, font,
                        entryIndex >= 0 ? _entryHeights[entryIndex] : MeasureSpoilerHeight((SpoilerEntry)entry, maxWidth),
                        highlightQuery);
                    break;

                case EntryType.Row:
                    DrawRow(b, (RowEntry)entry, x, ref y, maxWidth, font, highlightQuery);
                    break;

                case EntryType.Link:
                {
                    var e       = (LinkEntry)entry;
                    string label = e.GetLabel();
                    float  tw    = font.MeasureString(label).X;
                    float  drawX = ComputeAlignedX(x, maxWidth, tw, e.Alignment);
                    var    rect  = new Rectangle((int)drawX, y, (int)tw, _smallFontLineH);
                    bool   hov   = rect.Contains(Game1.getMouseX(), Game1.getMouseY());
                    Color  col   = e.IsUrlSafe() ? (hov ? LinkHoverColor : LinkColor) : Color.Gray * 0.6f;
                    DrawHighlightedText(b, font, label, new Vector2(drawX, y), col, 1f, highlightQuery);
                    b.Draw(Game1.fadeToBlackRect, new Rectangle((int)drawX, y + _smallFontLineH - 1, (int)tw, 1), col);
                    y += _smallFontLineH + 2;
                    break;
                }

                case EntryType.InternalLink:
                {
                    var    e     = (InternalLinkEntry)entry;
                    string label = e.GetLabel();
                    float  tw    = font.MeasureString(label).X;
                    float  drawX = ComputeAlignedX(x, maxWidth, tw, e.Alignment);
                    var    rect  = new Rectangle((int)drawX, y, (int)tw, _smallFontLineH);
                    bool   hov   = rect.Contains(Game1.getMouseX(), Game1.getMouseY());

                    bool exists = false;
                    foreach (var m in _mods)
                        if (string.Equals(m.UniqueId, e.ResolvedModId, StringComparison.OrdinalIgnoreCase))
                            { exists = true; break; }

                    Color col = exists ? (hov ? InternalLinkHoverColor : InternalLinkColor) : Color.Gray * 0.6f;
                    DrawHighlightedText(b, font, label, new Vector2(drawX, y), col, 1f, highlightQuery);
                    b.Draw(Game1.fadeToBlackRect, new Rectangle((int)drawX, y + _smallFontLineH - 1, (int)tw, 1), col);
                    if (hov && exists) _tabHoverText = BuildInternalLinkTooltip(e);
                    y += _smallFontLineH + 2;
                    break;
                }

                case EntryType.Gif:
                {
                    var e   = (GifEntry)entry;
                    var tex = e.TryGetTexture();
                    if (tex != null)
                    {
                        var src          = e.GetFrameRect(e.CurrentFrame);
                        var (dstW, dstH) = e.GetScaledSize();
                        float drawX      = ComputeAlignedX(x, maxWidth, dstW, e.Alignment);
                        b.Draw(tex, new Rectangle((int)drawX, y, dstW, dstH), src, Color.White);
                    }
                    y += entryIndex >= 0 ? _entryHeights[entryIndex] : MeasureGifHeight(e);
                    break;
                }

                case EntryType.IndentBlock:
                {
                    var e          = (IndentBlockEntry)entry;
                    int childX     = x + e.IndentAmount;
                    int childWidth = maxWidth - e.IndentAmount;
                    if (e.ShowRule)
                        b.Draw(Game1.fadeToBlackRect,
                            new Rectangle(x + e.IndentAmount / 2 - 1, y, 2,
                                entryIndex >= 0 ? _entryHeights[entryIndex] : MeasureIndentBlockHeight(e, maxWidth)),
                            DividerColor * 0.35f);
                    for (int i = 0; i < e.Children.Count; i++)
                    {
                        DrawEntry(b, e.Children[i], -1, childX, ref y, childWidth, font, highlightQuery);
                        if (i < e.Children.Count - 1) y += IndentBlockEntry.ChildGap;
                    }
                    break;
                }
            }
        }

        private void DrawRow(SpriteBatch b, RowEntry e, int x, ref int y, int maxWidth, SpriteFont font, string? highlightQuery)
        {
            int leftW  = (int)Math.Round(maxWidth * e.LeftFraction) - RowEntry.ColumnGap / 2;
            int rightW = maxWidth - leftW - RowEntry.ColumnGap;
            int rightX = x + leftW + RowEntry.ColumnGap;
            int startY = y;
            int leftY  = startY;
            int rightY = startY;

            for (int i = 0; i < e.LeftEntries.Count; i++)
            {
                DrawEntry(b, e.LeftEntries[i], -1, x, ref leftY, leftW, font, highlightQuery);
                if (i < e.LeftEntries.Count - 1) leftY += Padding / 2;
            }
            for (int i = 0; i < e.RightEntries.Count; i++)
            {
                DrawEntry(b, e.RightEntries[i], -1, rightX, ref rightY, rightW, font, highlightQuery);
                if (i < e.RightEntries.Count - 1) rightY += Padding / 2;
            }
            y = startY + Math.Max(leftY - startY, rightY - startY);
        }

        private void DrawFloatImage(SpriteBatch b, ImageEntry e, int x, ref int y, int maxWidth,
            SpriteFont font, int cachedH, string? highlightQuery)
        {
            var tex = e.TryGetTexture();
            if (tex != null)
            {
                var src  = e.SourceRect ?? new Rectangle(0, 0, tex.Width, tex.Height);
                int imgW = (int)Math.Round(src.Width  * e.Scale);
                int imgH = (int)Math.Round(src.Height * e.Scale);
                int lineH    = _smallFontLineH + 2;
                int listW    = maxWidth - imgW - ImageEntry.Gutter;
                int indented = Math.Max(1, listW - ImageEntry.BulletIndent);

                int imgX, listX;
                if (e.Alignment == Alignment.Left) { imgX = x; listX = x + imgW + ImageEntry.Gutter; }
                else                               { imgX = x + maxWidth - imgW; listX = x; }

                b.Draw(tex, new Rectangle(imgX, y, imgW, imgH), src, Color.White);

                int listY = y;
                for (int i = 0; i < e.Items!.Count; i++)
                {
                    Utility.drawTextWithShadow(b, "•", font, new Vector2(listX, listY), Game1.textColor);
                    int bx    = listX + ImageEntry.BulletIndent;
                    var rlines = WrapRich(e.Items[i](), font, indented);
                    DrawRichLines(b, rlines, font, bx, ref listY, indented, Game1.textColor,
                        highlightQuery: highlightQuery);
                    if (i < e.Items.Count - 1) listY += ListItemGap;
                }
            }
            y += cachedH;
        }

        private void DrawSpoiler(SpriteBatch b, SpoilerEntry e, int x, ref int y, int maxWidth,
            SpriteFont font, int cachedH, string? highlightQuery)
        {
            Color spoilerHover = new(
                Math.Min(255, SpoilerHeaderColor.R + 40),
                Math.Min(255, SpoilerHeaderColor.G + 30),
                Math.Min(255, SpoilerHeaderColor.B + 15));

            var  headerRect = new Rectangle(x, y, maxWidth, SpoilerEntry.HeaderHeight);
            bool hovered    = headerRect.Contains(Game1.getMouseX(), Game1.getMouseY());
            b.Draw(Game1.fadeToBlackRect, headerRect, hovered ? spoilerHover : SpoilerHeaderColor);

            string label = e.GetLabel();
            float  textY = y + (SpoilerEntry.HeaderHeight - _smallFontLineH) / 2f;
            DrawHighlightedText(b, font, label, new Vector2(x + 8, textY), Color.White, 1f, highlightQuery);

            const int ArrowW = 11, ArrowH = 12, ArrowScale = 2;
            var arrowSrc = e.IsRevealed
                ? new Rectangle(421, 459, ArrowW, ArrowH)
                : new Rectangle(421, 472, ArrowW, ArrowH);
            int arrowDrawW = ArrowW * ArrowScale;
            int arrowDrawH = ArrowH * ArrowScale;
            b.Draw(Game1.mouseCursors,
                new Rectangle(x + maxWidth - arrowDrawW - 8, y + (SpoilerEntry.HeaderHeight - arrowDrawH) / 2,
                    arrowDrawW, arrowDrawH),
                arrowSrc, Color.White);

            y += SpoilerEntry.HeaderHeight;

            if (e.IsRevealed && e.Children.Count > 0)
            {
                int contentH = cachedH - SpoilerEntry.HeaderHeight;
                if (contentH <= 0) { y += cachedH; return; }
                b.Draw(Game1.fadeToBlackRect,
                    new Rectangle(x, y, maxWidth, contentH),
                    new Color(245, 230, 200) * 0.6f);

                int innerX = x + SpoilerEntry.ChildPadding;
                int innerW = maxWidth - SpoilerEntry.ChildPadding * 2;
                int childY = y + SpoilerEntry.ChildPadding;
                for (int i = 0; i < e.Children.Count; i++)
                {
                    DrawEntry(b, e.Children[i], -1, innerX, ref childY, innerW, font, highlightQuery);
                    if (i < e.Children.Count - 1) childY += SpoilerEntry.ChildGap;
                }
                y += contentH;
            }
        }

        // ── Highlight-aware text rendering ────────────────────────────────────────

        /// <summary>
        /// Draw a single plain string, painting a highlight rect behind any occurrence of
        /// <paramref name="highlightQuery"/> before drawing the text on top.
        /// </summary>
        private void DrawHighlightedText(SpriteBatch b, SpriteFont font, string text,
            Vector2 pos, Color textColor, float scale, string? highlightQuery)
        {
            try
            {
                if (!string.IsNullOrEmpty(highlightQuery) && !string.IsNullOrEmpty(text))
                {
                    int startSearch = 0;
                    while (startSearch < text.Length)
                    {
                        int idx = text.IndexOf(highlightQuery, startSearch, StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) break;

                        // Guard against case-insensitive length mismatches — clamp to text length.
                        int matchLen = Math.Min(highlightQuery.Length, text.Length - idx);
                        if (matchLen <= 0) break;

                        float preW   = idx > 0 ? font.MeasureString(text.Substring(0, idx)).X * scale : 0f;
                        float matchW = font.MeasureString(text.Substring(idx, matchLen)).X * scale;
                        int   hH     = Math.Max(1, (int)(_smallFontLineH * scale));
                        int   matchPx = Math.Max(1, (int)matchW);

                        b.Draw(Game1.fadeToBlackRect,
                            new Rectangle((int)(pos.X + preW), (int)pos.Y, matchPx, hH),
                            SearchHighlightColor * 0.55f);

                        startSearch = idx + matchLen;
                    }
                }
            }
            catch (Exception ex)
            {
                _monitor?.Log($"DrawHighlightedText failed for text=[{text}] query=[{highlightQuery}]: {ex}", LogLevel.Error);
                // Fall through and draw text without highlight.
            }

            b.DrawString(font, text, pos, textColor, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }

        /// <summary>
        /// Draw a single line of <see cref="InlineSegment"/>s, painting highlight rects
        /// behind text atoms that contain the query before drawing the segments.
        /// </summary>
        private void DrawRichLine(SpriteBatch b, List<InlineSegment> line, SpriteFont font,
            int x, int y, Color textColor, float scale, int lineH, string? highlightQuery)
        {
            float cx = x;
            foreach (var seg in line)
            {
                if (seg.IsSprite)
                {
                    var tex = seg.ItemData!.GetTexture();
                    var src = seg.ItemData.GetSourceRect();
                    int sz  = (int)Math.Round(_smallFontLineH * scale);
                    b.Draw(tex, new Rectangle((int)cx, y, sz, sz), src, Color.White);
                    cx += sz + 2;
                }
                else if (seg.IsEmote)
                {
                    var tex = EmoteRegistry.TryGet(seg.EmoteName!);
                    int sz  = (int)Math.Round(_smallFontLineH * scale);
                    if (tex != null) b.Draw(tex, new Rectangle((int)cx, y, sz, sz), Color.White);
                    cx += sz + 2;
                }
                else if (!string.IsNullOrEmpty(seg.Text))
                {
                    DrawHighlightedText(b, font, seg.Text, new Vector2(cx, y), textColor, scale, highlightQuery);
                    cx += font.MeasureString(seg.Text).X * scale;
                }
            }
        }

        private void DrawRichLines(
            SpriteBatch               b,
            List<List<InlineSegment>> lines,
            SpriteFont                font,
            int                       x,
            ref int                   y,
            int                       maxWidth,
            Color                     textColor,
            Alignment                 alignment     = Alignment.Left,
            float                     scale         = 1f,
            int                       lineHOverride = 0,
            string?                   highlightQuery = null)
        {
            int lineH = lineHOverride > 0 ? lineHOverride : _smallFontLineH + 2;
            foreach (var line in lines)
            {
                float lineW = InlineSegment.MeasureLineWidth(line, font, _smallFontLineH) * scale;
                float drawX = ComputeAlignedX(x, maxWidth, lineW, alignment);
                DrawRichLine(b, line, font, (int)drawX, y, textColor, scale, lineH, highlightQuery);
                y += lineH;
            }
        }

        // ── Dividers, scrollbar, empty state ─────────────────────────────────────

        private static float ComputeAlignedX(int areaX, int areaWidth, float elementWidth, Alignment alignment) =>
            alignment switch
            {
                Alignment.Center => areaX + (areaWidth - elementWidth) / 2f,
                Alignment.Right  => areaX + areaWidth - elementWidth,
                _                => areaX
            };

        private void DrawDivider(SpriteBatch b, DividerStyle style, int x, int y, int maxWidth)
        {
            int centerY = y + 6;
            switch (style)
            {
                default:
                case DividerStyle.Single:
                    b.Draw(Game1.fadeToBlackRect, new Rectangle(x, centerY, maxWidth, 2), DividerColor * 0.4f);
                    break;
                case DividerStyle.Double:
                    b.Draw(Game1.fadeToBlackRect, new Rectangle(x, centerY - 2, maxWidth, 2), DividerColor * 0.4f);
                    b.Draw(Game1.fadeToBlackRect, new Rectangle(x, centerY + 3, maxWidth, 2), DividerColor * 0.4f);
                    break;
                case DividerStyle.Dotted:
                {
                    const int dotSize = 3, dotStep = 9;
                    int dotX = x;
                    while (dotX + dotSize <= x + maxWidth)
                    {
                        b.Draw(Game1.fadeToBlackRect, new Rectangle(dotX, centerY - 1, dotSize, dotSize), DividerColor * 0.45f);
                        dotX += dotStep;
                    }
                    break;
                }
                case DividerStyle.IconCentered:
                {
                    const int iconSize = 8, iconGap = 14;
                    int halfWidth  = maxWidth / 2;
                    int leftEnd    = x + halfWidth - iconGap;
                    int rightStart = x + halfWidth + iconGap;
                    if (leftEnd > x)
                        b.Draw(Game1.fadeToBlackRect, new Rectangle(x, centerY, leftEnd - x, 2), DividerColor * 0.4f);
                    if (rightStart < x + maxWidth)
                        b.Draw(Game1.fadeToBlackRect, new Rectangle(rightStart, centerY, x + maxWidth - rightStart, 2), DividerColor * 0.4f);
                    b.Draw(Game1.fadeToBlackRect, new Vector2(x + halfWidth, centerY + 1), null,
                        DividerColor * 0.65f, MathF.PI / 4f, new Vector2(0.5f, 0.5f), iconSize, SpriteEffects.None, 0f);
                    break;
                }
            }
        }

        private void DrawScrollButtons(SpriteBatch b)
        {
            if (_totalContentHeight <= _contentBounds.Height) return;
            _scrollUpButton.draw(b);
            _scrollDownButton.draw(b);
        }

        private void DrawEmptyState(SpriteBatch b)
        {
            string msg  = _i18n.Get("ui.no-mods-content");
            var    size = Game1.smallFont.MeasureString(msg);
            float  cx   = _contentBounds.X + (_contentBounds.Width  - size.X) / 2f;
            float  cy   = _contentBounds.Y + (_contentBounds.Height - size.Y) / 2f;
            Utility.drawTextWithShadow(b, msg, Game1.smallFont, new Vector2(cx, cy), Game1.textColor * 0.5f);
        }

        // ── Input handling ────────────────────────────────────────────────────────

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            try { receiveLeftClickInternal(x, y, playSound); }
            catch (Exception ex)
            {
                _monitor?.Log($"receiveLeftClick failed at ({x},{y}): matches={_matches.Count} matchIndex={_matchIndex} query=[{_contentSearchQuery}] selectedMod=[{_selectedMod?.UniqueId}] selectedPage=[{_selectedPage?.PageId}] entryHeightsLen={_entryHeights.Length}\n{ex}", LogLevel.Error);
                throw;
            }
        }

        private void receiveLeftClickInternal(int x, int y, bool playSound = true)
        {
            if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }

            if (_scrollUpButton.containsPoint(x, y))  { ScrollContent(-120); Game1.playSound("shiny4"); return; }
            if (_scrollDownButton.containsPoint(x, y)) { ScrollContent( 120); Game1.playSound("shiny4"); return; }

            // ── Prev / Next match buttons ────────────────────────────────────────
            if (_selectedMod != null && _prevMatchButtonBounds.Contains(x, y))
            {
                StepMatch(-1);
                return;
            }
            if (_selectedMod != null && _nextMatchButtonBounds.Contains(x, y))
            {
                StepMatch(+1);
                return;
            }

            // ── Content search bar ───────────────────────────────────────────────
            if (_selectedMod != null && _contentSearchBarBounds.Contains(x, y))
            {
                // Clear button (inside the bar)
                if (_contentSearchQuery.Length > 0)
                {
                    const int clearSize = 16;
                    int       clearX    = _contentSearchBarBounds.Right - clearSize - 6;
                    int       clearY    = _contentSearchBarBounds.Y + (_contentSearchBarBounds.Height - clearSize) / 2;
                    if (new Rectangle(clearX, clearY, clearSize, clearSize).Contains(x, y))
                    {
                        ClearContentSearch();
                        Game1.playSound("drumkit6");
                        return;
                    }
                }
                ActivateSearchBar(2);
                return;
            }

            // ── Sidebar search bar ───────────────────────────────────────────────
            if (_searchBarBounds.Contains(x, y))
            {
                if (_searchQuery.Length > 0)
                {
                    const int clearSize = 16;
                    int       clearX    = _searchBarBounds.Right - clearSize - 6;
                    int       clearY    = _searchBarBounds.Y + (_searchBarBounds.Height - clearSize) / 2;
                    if (new Rectangle(clearX, clearY, clearSize, clearSize).Contains(x, y))
                    {
                        _searchQuery = string.Empty;
                        RebuildFilter();
                        Game1.playSound("drumkit6");
                        return;
                    }
                }
                ActivateSearchBar(1);
                return;
            }

            // Clicking anywhere else deactivates search bar focus
            _activeSearchBar = 0;
            if (Game1.keyboardDispatcher.Subscriber == this)
                Game1.keyboardDispatcher.Subscriber = null;

            if (_sidebarBounds.Contains(x, y))
            {
                int relY  = y - _sidebarBounds.Y + _sidebarScrollOffset;
                int index = relY / SidebarItemHeight;
                if (index >= 0 && index < _filteredMods.Count && index != _selectedModIndex)
                {
                    Game1.playSound("smallSelect");
                    SelectMod(index);
                }
                return;
            }

            if (_selectedMod != null && _tabsBounds.Contains(x, y))
            {
                for (int i = 0; i < _tabBounds.Length; i++)
                {
                    // Reconstruct the scrolled screen-space rect for this tab
                    var screenTab = new Rectangle(
                        _tabsBounds.X + _tabBounds[i].X - _tabScrollOffset,
                        _tabBounds[i].Y,
                        _tabBounds[i].Width,
                        _tabBounds[i].Height);
                    if (screenTab.Contains(x, y))
                    {
                        if (_pages[i] != _selectedPage)
                        {
                            Game1.playSound("smallSelect");
                            SelectPage(_pages[i]);
                            // Re-run match collection on new page context (matches persist across pages)
                            if (_contentSearchQuery.Length > 0)
                                JumpToCurrentMatch();
                        }
                        return;
                    }
                }
            }

            if (_selectedPage != null && _contentBounds.Contains(x, y))
            {
                int cy    = _contentBounds.Y + Padding - _contentScrollOffset;
                int mxPad = _contentBounds.X + Padding;
                int mw    = _contentBounds.Width - Padding * 2;

                if (_selectedPage.HeaderImage != null && _headerImageHeight > 0)
                    cy += _headerImageHeight + Padding / 2;

                var entries = _selectedPage.Entries;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (HitTestEntry(entries[i], x, y, mxPad, cy, mw, _entryHeights[i]))
                        return;
                    cy += _entryHeights[i] + Padding / 2;
                }
            }
        }

        private bool HitTestEntry(IDocumentationEntry entry, int x, int y, int ex, int ey, int ew, int eh)
        {
            if (entry is SpoilerEntry spoiler)
            {
                var headerRect = new Rectangle(ex, ey, ew, SpoilerEntry.HeaderHeight);
                if (headerRect.Contains(x, y))
                {
                    spoiler.IsRevealed = !spoiler.IsRevealed;
                    Game1.playSound("smallSelect");
                    MeasureContentHeight();
                    // Re-clamp scroll so nothing is out of range after height change
                    int maxScroll = Math.Max(0, _totalContentHeight - _contentBounds.Height + Padding);
                    _contentScrollOffset = Math.Clamp(_contentScrollOffset, 0, maxScroll);
                    // Defer match re-collection to update() — safe to do outside the click stack
                    if (_contentSearchQuery.Length > 0)
                        _matchesDirty = true;
                    return true;
                }
                if (spoiler.IsRevealed && spoiler.Children.Count > 0)
                {
                    int innerX = ex + SpoilerEntry.ChildPadding;
                    int innerW = ew  - SpoilerEntry.ChildPadding * 2;
                    int colY   = ey + SpoilerEntry.HeaderHeight + SpoilerEntry.ChildPadding;
                    for (int i = 0; i < spoiler.Children.Count; i++)
                    {
                        int subH = MeasureEntryHeight(spoiler.Children[i], innerW);
                        if (HitTestEntry(spoiler.Children[i], x, y, innerX, colY, innerW, subH)) return true;
                        colY += subH + SpoilerEntry.ChildGap;
                    }
                }
            }
            else if (entry is LinkEntry link)
            {
                string label = link.GetLabel();
                float  tw    = Game1.smallFont.MeasureString(label).X;
                float  lx    = ComputeAlignedX(ex, ew, tw, link.Alignment);
                var    rect  = new Rectangle((int)lx, ey, (int)tw, _smallFontLineH);
                if (rect.Contains(x, y))
                {
                    if (!link.IsUrlSafe()) { Game1.playSound("cancel"); return true; }
                    Game1.playSound("smallSelect");
                    string message = _i18n.Get("ui.link-confirm", new { url = link.Url });
                    Game1.activeClickableMenu = new ConfirmationDialog(message,
                        _ => { link.Open(); Game1.activeClickableMenu = this; },
                        _ => { Game1.activeClickableMenu = this; });
                    return true;
                }
            }
            else if (entry is InternalLinkEntry internalLink)
            {
                string label = internalLink.GetLabel();
                float  tw    = Game1.smallFont.MeasureString(label).X;
                float  lx    = ComputeAlignedX(ex, ew, tw, internalLink.Alignment);
                var    rect  = new Rectangle((int)lx, ey, (int)tw, _smallFontLineH);
                if (rect.Contains(x, y)) { NavigateTo(internalLink.ResolvedModId, internalLink.TargetPageId, internalLink.TargetAnchor); return true; }
            }
            else if (entry is RowEntry row)
            {
                int leftW  = (int)Math.Round(ew * row.LeftFraction) - RowEntry.ColumnGap / 2;
                int rightW = ew - leftW - RowEntry.ColumnGap;
                int rightX = ex + leftW + RowEntry.ColumnGap;
                int colY   = ey;
                for (int i = 0; i < row.LeftEntries.Count; i++)
                {
                    int subH = MeasureEntryHeight(row.LeftEntries[i], leftW);
                    if (HitTestEntry(row.LeftEntries[i], x, y, ex, colY, leftW, subH)) return true;
                    colY += subH + Padding / 2;
                }
                colY = ey;
                for (int i = 0; i < row.RightEntries.Count; i++)
                {
                    int subH = MeasureEntryHeight(row.RightEntries[i], rightW);
                    if (HitTestEntry(row.RightEntries[i], x, y, rightX, colY, rightW, subH)) return true;
                    colY += subH + Padding / 2;
                }
            }
            else if (entry is IndentBlockEntry indentBlock)
            {
                int childX = ex + indentBlock.IndentAmount;
                int childW = ew  - indentBlock.IndentAmount;
                int colY   = ey;
                for (int i = 0; i < indentBlock.Children.Count; i++)
                {
                    int subH = MeasureEntryHeight(indentBlock.Children[i], childW);
                    if (HitTestEntry(indentBlock.Children[i], x, y, childX, colY, childW, subH)) return true;
                    colY += subH + IndentBlockEntry.ChildGap;
                }
            }
            return false;
        }

        public override void receiveKeyPress(Keys key)
        {
            if (_activeSearchBar != 0) return;  // eat all keys while either bar is focused
            base.receiveKeyPress(key);
        }

        // ── IKeyboardSubscriber ───────────────────────────────────────────────────

        public void RecieveTextInput(char inputChar)
        {
            if (_activeSearchBar == 0 || char.IsControl(inputChar)) return;

            if (_activeSearchBar == 1)
            {
                _searchQuery += inputChar;
                RebuildFilter();
                _cursorBlinkTimer = 0; _cursorVisible = true;
            }
            else
            {
                _contentSearchQuery += inputChar;
                CollectMatches();
                if (_matches.Count > 0) JumpToCurrentMatch();
                _contentCursorBlinkTimer = 0; _contentCursorVisible = true;
            }
        }

        public void RecieveTextInput(string text)
        {
            if (_activeSearchBar == 0 || string.IsNullOrEmpty(text)) return;
            foreach (char c in text)
                if (!char.IsControl(c))
                {
                    if (_activeSearchBar == 1) _searchQuery += c;
                    else                       _contentSearchQuery += c;
                }

            if (_activeSearchBar == 1) RebuildFilter();
            else { CollectMatches(); if (_matches.Count > 0) JumpToCurrentMatch(); }
        }

        public void RecieveCommandInput(char command)
        {
            if (_activeSearchBar == 0) return;

            if (_activeSearchBar == 1)
            {
                switch (command)
                {
                    case '\b':
                        if (_searchQuery.Length > 0)
                        {
                            _searchQuery = _searchQuery[..^1];
                            RebuildFilter();
                            _cursorBlinkTimer = 0; _cursorVisible = true;
                        }
                        break;
                    case '\r':
                        _activeSearchBar = 0;
                        if (Game1.keyboardDispatcher.Subscriber == this)
                            Game1.keyboardDispatcher.Subscriber = null;
                        break;
                    case '\x1b':
                        if (_searchQuery.Length > 0)
                        {
                            _searchQuery = string.Empty;
                            RebuildFilter();
                            Game1.playSound("drumkit6");
                        }
                        else
                        {
                            _activeSearchBar = 0;
                            if (Game1.keyboardDispatcher.Subscriber == this)
                                Game1.keyboardDispatcher.Subscriber = null;
                        }
                        break;
                }
            }
            else // content search bar
            {
                switch (command)
                {
                    case '\b':
                        if (_contentSearchQuery.Length > 0)
                        {
                            _contentSearchQuery = _contentSearchQuery[..^1];
                            CollectMatches();
                            if (_matches.Count > 0) JumpToCurrentMatch();
                            _contentCursorBlinkTimer = 0; _contentCursorVisible = true;
                        }
                        break;
                    case '\r':
                        // Enter steps to next match
                        StepMatch(+1);
                        break;
                    case '\x1b':
                        if (_contentSearchQuery.Length > 0)
                        {
                            ClearContentSearch();
                            Game1.playSound("drumkit6");
                        }
                        else
                        {
                            _activeSearchBar = 0;
                            if (Game1.keyboardDispatcher.Subscriber == this)
                                Game1.keyboardDispatcher.Subscriber = null;
                        }
                        break;
                }
            }
        }

        public void RecieveSpecialInput(Keys key) { }

        public override void receiveScrollWheelAction(int direction)
        {
            int mx = Game1.getMouseX(), my = Game1.getMouseY();
            if (_sidebarBounds.Contains(mx, my))
            {
                int maxSidebarScroll = Math.Max(0, _filteredMods.Count * SidebarItemHeight - _sidebarBounds.Height);
                _sidebarScrollOffset = Math.Clamp(_sidebarScrollOffset - direction / 3, 0, maxSidebarScroll);
            }
            else if (_tabsBounds.Contains(mx, my))
            {
                // Horizontal scroll of the tab strip (scroll wheel scrolls left/right)
                int maxTabScroll = Math.Max(0, _tabsTotalWidth - _tabsBounds.Width);
                _tabScrollOffset = Math.Clamp(_tabScrollOffset - direction / 3, 0, maxTabScroll);
            }
            else if (_contentBounds.Contains(mx, my))
            {
                ScrollContent(-direction / 3);
            }
        }

        private void ScrollContent(int delta)
        {
            int maxScroll = Math.Max(0, _totalContentHeight - _contentBounds.Height + Padding);
            _contentScrollOffset = Math.Clamp(_contentScrollOffset + delta, 0, maxScroll);
        }

        // ── Utility ───────────────────────────────────────────────────────────────

        private List<List<InlineSegment>> WrapRich(string text, SpriteFont font, int maxWidth)
            => InlineParser.WrapRich(text, font, maxWidth, _smallFontLineH);

        private static string TruncateWithEllipsis(string text, SpriteFont font, int maxPixelWidth)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (font.MeasureString(text).X <= maxPixelWidth) return text;
            const string ellipsis  = "…";
            float        ellipsisW = font.MeasureString(ellipsis).X;
            float        budget    = maxPixelWidth - ellipsisW;
            int lo = 0, hi = text.Length - 1, best = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (font.MeasureString(text.Substring(0, mid + 1)).X <= budget) { best = mid + 1; lo = mid + 1; }
                else hi = mid - 1;
            }
            return text.Substring(0, best) + ellipsis;
        }

        private string BuildInternalLinkTooltip(InternalLinkEntry e)
        {
            string modDisplay = e.ResolvedModId;
            foreach (var m in _mods)
                if (string.Equals(m.UniqueId, e.ResolvedModId, StringComparison.OrdinalIgnoreCase))
                    { modDisplay = m.GetName(); break; }

            if (e.TargetPageId == null && e.TargetAnchor == null) return modDisplay;
            if (e.TargetPageId != null && e.TargetAnchor == null) return $"{modDisplay}  ›  {e.TargetPageId}";
            if (e.TargetPageId == null) return $"{modDisplay}  ›  #{e.TargetAnchor}";
            return $"{modDisplay}  ›  {e.TargetPageId}  ›  #{e.TargetAnchor}";
        }
    }
}
