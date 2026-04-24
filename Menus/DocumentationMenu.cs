using System;
using System.Collections.Generic;
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
        
        private static readonly Color HoverColor        = new(253, 182, 84);
        private static readonly Color DividerColor      = new(180, 140, 80);
        private static readonly Color SectionTitleColor = new(177, 78,  5);
        private static readonly Color KeyColor          = new(60,  60,  120);
        private static readonly Color CaptionColor      = new(80,  80,  80);
        private static readonly Color SpoilerHeaderColor= new(100, 70,  40);
        private static readonly Color LinkColor         = new(50,  80,  200);
        private static readonly Color LinkHoverColor    = new(80,  120, 255);
        private static readonly Color InternalLinkColor      = new(30,  130,  60);
        private static readonly Color InternalLinkHoverColor = new(50,  190,  90);
        
        private Color _accentColor;
        private Color _contentBorderColor;
        private Color _scrollBarColor;

        private static readonly RasterizerState ScissorRasterizer =
            new() { ScissorTestEnable = true };

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

        // ── Search bar ──────────────────────────────────────────────────────────
        private string  _searchQuery      = string.Empty;
        private double  _cursorBlinkTimer = 0.0;
        private bool    _cursorVisible    = true;
        private Rectangle _searchBarBounds;

        /// <summary>
        /// IKeyboardSubscriber.Selected — true while the search bar has keyboard focus.
        /// Setting to true registers this menu with the game's keyboard dispatcher so
        /// that RecieveTextInput / RecieveCommandInput are called by the engine.
        /// </summary>
        public bool Selected
        {
            get => _searchBarActive;
            set
            {
                if (_searchBarActive == value) return;
                _searchBarActive = value;
                if (value)
                    Game1.keyboardDispatcher.Subscriber = this;
                else if (Game1.keyboardDispatcher.Subscriber == this)
                    Game1.keyboardDispatcher.Subscriber = null;
            }
        }
        private bool _searchBarActive;

        /// <summary>The subset of <see cref="_mods"/> that match the current search query, in display order.</summary>
        private List<ModDocumentation> _filteredMods = new();


        private readonly bool _fontSettingsActive;

        public DocumentationMenu(IReadOnlyList<ModDocumentation> mods, ITranslationHelper i18n, ModConfig config, bool fontSettingsActive)
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

            _accentColor        = ColorHelper.Parse(config.AccentColor,        new Color(177, 78,  5));
            _contentBorderColor = ColorHelper.Parse(config.ContentBorderColor, new Color(180, 140, 80));
            _scrollBarColor     = ColorHelper.Parse(config.ScrollBarColor,      new Color(180, 140, 80));

            CalculateBounds();

            _smallFontLineH = (int)Game1.smallFont.MeasureString("A").Y;
            _titleFontLineH = (int)(Game1.dialogueFont.MeasureString("A").Y * SectionTitleScale);

            CreateScrollButtons();

            RebuildFilter();
        }

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

        public override void update(GameTime time)
        {
            base.update(time);

            // Blink the search bar cursor.
            if (Selected)
            {
                _cursorBlinkTimer += time.ElapsedGameTime.TotalSeconds;
                if (_cursorBlinkTimer >= 0.53)
                {
                    _cursorBlinkTimer = 0.0;
                    _cursorVisible    = !_cursorVisible;
                }
            }

            if (_selectedPage == null) return;

            double dt = time.ElapsedGameTime.TotalSeconds;
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

        private void CalculateBounds()
        {
            // The search bar sits at the very top of the sidebar column.
            _searchBarBounds = new Rectangle(
                xPositionOnScreen + Padding,
                yPositionOnScreen + Padding + 60,
                SidebarWidth,
                SearchBarHeight
            );

            // The scrollable mod list begins below the search bar.
            _sidebarBounds = new Rectangle(
                xPositionOnScreen + Padding,
                _searchBarBounds.Bottom + Padding / 2,
                SidebarWidth,
                height - Padding * 2 - 60 - SearchBarHeight - Padding / 2
            );

            int contentX     = xPositionOnScreen + Padding + SidebarWidth + Padding;
            int contentWidth = width - SidebarWidth - Padding * 3;

            _tabsBounds = new Rectangle(contentX, yPositionOnScreen + Padding + 60, contentWidth, TabHeight);

            _contentBounds = new Rectangle(
                contentX, _tabsBounds.Bottom + 4,
                contentWidth - ScrollButtonSize - 8,
                height - Padding * 2 - 60 - TabHeight - 4
            );

            _scrollUpBounds   = new Rectangle(_contentBounds.Right + 8, _contentBounds.Top, ScrollButtonSize, ScrollButtonSize);
            _scrollDownBounds = new Rectangle(_contentBounds.Right + 8, _contentBounds.Bottom - ScrollButtonSize, ScrollButtonSize, ScrollButtonSize);
        }

        private void CreateScrollButtons()
        {
            _scrollUpButton = new ClickableTextureComponent(
                _scrollUpBounds, Game1.mouseCursors, new Rectangle(421, 459, 11, 12), (float)ScrollButtonSize / 11);
            _scrollDownButton = new ClickableTextureComponent(
                _scrollDownBounds, Game1.mouseCursors, new Rectangle(421, 472, 11, 12), (float)ScrollButtonSize / 11);
        }

        private void SelectMod(int filteredIndex)
        {
            if (filteredIndex < 0 || filteredIndex >= _filteredMods.Count) return;
            _selectedModIndex    = filteredIndex;
            _selectedMod         = _filteredMods[filteredIndex];
            _contentScrollOffset = 0;
            _selectedPage        = null;
            RebuildPageState();
        }

        private void SelectPage(DocumentationPage page)
        {
            _selectedPage        = page;
            _contentScrollOffset = 0;
            MeasureContentHeight();
        }

        /// <summary>
        /// Navigates the viewer to the specified mod, page, and optional anchor.
        /// Any of the parameters may be null, in which case sensible defaults apply.
        /// </summary>
        private void NavigateTo(string resolvedModId, string? targetPageId, string? targetAnchor)
        {
            // Find the mod in the full list first
            ModDocumentation? targetMod = null;
            foreach (var m in _mods)
            {
                if (string.Equals(m.UniqueId, resolvedModId, StringComparison.OrdinalIgnoreCase))
                {
                    targetMod = m;
                    break;
                }
            }

            if (targetMod == null)
            {
                // Target mod has no registered documentation — play a cancel sound and bail
                Game1.playSound("cancel");
                return;
            }

            // If the target mod is filtered out, clear the search so it becomes visible
            int filteredIndex = -1;
            for (int i = 0; i < _filteredMods.Count; i++)
            {
                if (string.Equals(_filteredMods[i].UniqueId, resolvedModId, StringComparison.OrdinalIgnoreCase))
                {
                    filteredIndex = i;
                    break;
                }
            }

            if (filteredIndex < 0)
            {
                // Clear search so the target mod is visible, then rebuild
                _searchQuery = string.Empty;
                RebuildFilter();
                for (int i = 0; i < _filteredMods.Count; i++)
                {
                    if (string.Equals(_filteredMods[i].UniqueId, resolvedModId, StringComparison.OrdinalIgnoreCase))
                    {
                        filteredIndex = i;
                        break;
                    }
                }
            }

            if (filteredIndex < 0)
            {
                Game1.playSound("cancel");
                return;
            }

            // Switch mod if necessary
            if (filteredIndex != _selectedModIndex)
                SelectMod(filteredIndex);

            // Switch page if a specific page was requested
            if (targetPageId != null)
            {
                var targetDoc  = _filteredMods[filteredIndex];
                var targetPage = targetDoc.GetPage(targetPageId);
                if (targetPage != null && targetPage != _selectedPage)
                    SelectPage(targetPage);
            }

            // Scroll to anchor (MeasureContentHeight has already run inside SelectMod/SelectPage)
            if (targetAnchor != null && _selectedPage != null)
            {
                int offset = ComputeAnchorScrollOffset(targetAnchor);
                int maxScroll = Math.Max(0, _totalContentHeight - _contentBounds.Height + Padding);
                _contentScrollOffset = Math.Clamp(offset, 0, maxScroll);
            }

            Game1.playSound("smallSelect");
        }

        /// <summary>
        /// Returns the pixel Y offset (from the top of the content area) of the first entry
        /// on the current page whose <see cref="IAnchorable.Anchor"/> matches <paramref name="anchor"/>.
        /// Returns 0 if no match is found.
        /// </summary>
        private int ComputeAnchorScrollOffset(string anchor)
        {
            if (_selectedPage == null || _entryHeights.Length == 0) return 0;

            int y = _headerImageHeight > 0 ? _headerImageHeight + Padding / 2 : 0;

            var entries = _selectedPage.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] is IAnchorable anchorable &&
                    string.Equals(anchorable.Anchor, anchor, StringComparison.OrdinalIgnoreCase))
                    return y;

                y += _entryHeights[i] + Padding / 2;
            }

            return 0; // anchor not found — scroll to top
        }

        /// <summary>
        /// Rebuilds <see cref="_filteredMods"/> from the full mod list using the current
        /// <see cref="_searchQuery"/>, then keeps the selected mod selected if it still
        /// appears in the results, or auto-selects the first result otherwise.
        /// </summary>
        private void RebuildFilter()
        {
            // Remember which mod was selected before the filter changed.
            var previouslySelected = _selectedMod;

            _filteredMods.Clear();
            _sidebarScrollOffset = 0;

            if (string.IsNullOrWhiteSpace(_searchQuery))
            {
                // No filter — show everything.
                foreach (var m in _mods)
                    _filteredMods.Add(m);
            }
            else
            {
                string q = _searchQuery.Trim();
                foreach (var m in _mods)
                {
                    if (m.GetName().Contains(q, StringComparison.OrdinalIgnoreCase))
                        _filteredMods.Add(m);
                }
            }

            // Try to keep the previously-selected mod selected.
            if (previouslySelected != null)
            {
                for (int i = 0; i < _filteredMods.Count; i++)
                {
                    if (string.Equals(_filteredMods[i].UniqueId, previouslySelected.UniqueId, StringComparison.OrdinalIgnoreCase))
                    {
                        _selectedModIndex = i;
                        _selectedMod      = _filteredMods[i];
                        // Page state is already correct — no need to rebuild.
                        return;
                    }
                }
            }

            // Previously-selected mod is not in the filtered results — select first result.
            _selectedMod  = null;
            _selectedPage = null;
            if (_filteredMods.Count > 0)
                SelectMod(0);
            else
                _selectedModIndex = -1;
        }

        private void RebuildPageState()
        {
            _pages        = _selectedMod?.GetAllPages() ?? Array.Empty<DocumentationPage>();
            _selectedPage = _pages.Count > 0 ? _pages[0] : null;

            _tabBounds = new Rectangle[_pages.Count];
            int tabX = _tabsBounds.X;
            float tabScale = _fontSettingsActive ? 1.025f : 1f;

            for (int i = 0; i < _pages.Count; i++)
            {
                int naturalW = (int)((Game1.smallFont.MeasureString(_pages[i].GetPageName()).X + Padding * 2) * tabScale);
                int maxTabW  = (int)(_tabsBounds.Width * 0.4f * tabScale);
                int w        = Math.Min(naturalW, maxTabW);
                _tabBounds[i] = new Rectangle(tabX, _tabsBounds.Y, w, TabHeight);
                tabX += w + 4;
            }

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
                ? MeasureHeaderImageHeight(_selectedPage.HeaderImage)
                : 0;

            if (_headerImageHeight > 0)
                y += _headerImageHeight + Padding / 2;

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

        private int MeasureHeaderImageHeight(HeaderImageEntry entry)
        {
            var tex = entry.TryGetTexture();
            if (tex == null) return 0;
            var rect = entry.SourceRect ?? new Rectangle(0, 0, tex.Width, tex.Height);
            if (rect.Width == 0) return 0;
            return (int)(_contentBounds.Width * ((float)rect.Height / rect.Width));
        }

        private int MeasureEntryHeight(IDocumentationEntry entry, int maxWidth)
        {
            return entry.Type switch
            {
                EntryType.SectionTitle => MeasureSectionTitleHeight((SectionTitleEntry)entry, maxWidth),
                EntryType.Paragraph    => MeasureScaledTextHeight(((ParagraphEntry)entry).GetText(),    maxWidth, ((ParagraphEntry)entry).FontSize,    (int)SmallFontNaturalPx),
                EntryType.Caption      => MeasureScaledTextHeight(((CaptionEntry)entry).GetText(),      maxWidth, ((CaptionEntry)entry).FontSize,      (int)SmallFontNaturalPx),
                EntryType.Image        => MeasureImageHeight((ImageEntry)entry, maxWidth),
                EntryType.KeyValuePair => MeasureKeyValueHeight((KeyValuePairEntry)entry, maxWidth),
                EntryType.Divider      => MeasureDividerHeight((DividerEntry)entry),
                EntryType.Spacer       => ((SpacerEntry)entry).Height,
                EntryType.List         => MeasureListHeight((ListEntry)entry, maxWidth),
                EntryType.Spoiler      => MeasureSpoilerHeight((SpoilerEntry)entry, maxWidth),
                EntryType.Row          => MeasureRowHeight((RowEntry)entry, maxWidth),
                EntryType.Link         => _smallFontLineH + 2,
                EntryType.InternalLink => _smallFontLineH + 2,
                EntryType.Gif          => MeasureGifHeight((GifEntry)entry),
                EntryType.IndentBlock  => MeasureIndentBlockHeight((IndentBlockEntry)entry, maxWidth),
                _                      => 0
            };
        }

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

            var src  = entry.SourceRect ?? new Rectangle(0, 0, tex.Width, tex.Height);
            int imgH = (int)Math.Round(src.Height * entry.Scale);

            if (!entry.HasFloatLayout)
                return imgH;

            int imgW     = (int)Math.Round(src.Width * entry.Scale);
            int listW    = maxWidth - imgW - ImageEntry.Gutter;
            int lineH    = _smallFontLineH + 2;
            int indented = Math.Max(1, listW - ImageEntry.BulletIndent);

            int listH = 0;
            for (int i = 0; i < entry.Items!.Count; i++)
            {
                int lc = WrapRich(entry.Items[i](), Game1.smallFont, indented).Count;
                listH += Math.Max(1, lc) * lineH;
                if (i < entry.Items.Count - 1)
                    listH += ListItemGap;
            }

            return Math.Max(imgH, listH);
        }

        private int MeasureGifHeight(GifEntry entry)
        {
            var tex = entry.TryGetTexture();
            if (tex == null) return 0;
            return entry.GetScaledSize().h;
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
                if (i < entry.Items.Count - 1)
                    total += ListItemGap;
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
            if (entry.IsRevealed)
                h += 4 + MeasureScaledTextHeight(entry.GetContent(), maxWidth - Padding, null, (int)SmallFontNaturalPx);
            return h;
        }

        private int MeasureRowHeight(RowEntry entry, int maxWidth)
        {
            int leftW  = (int)Math.Round(maxWidth * entry.LeftFraction) - RowEntry.ColumnGap / 2;
            int rightW = maxWidth - leftW - RowEntry.ColumnGap;
            int leftH  = MeasureColumnHeight(entry.LeftEntries,  Math.Max(1, leftW));
            int rightH = MeasureColumnHeight(entry.RightEntries, Math.Max(1, rightW));
            return Math.Max(leftH, rightH);
        }

        private int MeasureIndentBlockHeight(IndentBlockEntry entry, int maxWidth)
        {
            int childWidth = Math.Max(1, maxWidth - entry.IndentAmount);
            return MeasureColumnHeight(entry.Children, childWidth);
        }

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

        public override void draw(SpriteBatch b)
        {
            _tabHoverText = null;

            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White, drawShadow: true);

            DrawTitle(b);
            DrawSearchBar(b);
            DrawSidebar(b);

            b.Draw(Game1.fadeToBlackRect,
                new Rectangle(_sidebarBounds.Right + Padding / 2, _searchBarBounds.Top, 2, _searchBarBounds.Height + Padding / 2 + _sidebarBounds.Height),
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

        private void DrawTitle(SpriteBatch b)
        {
            var   font      = Game1.dialogueFont;
            float titleY    = yPositionOnScreen + Padding + (60 - font.MeasureString("A").Y) / 2f;
            int   maxWidth  = width - Padding * 4;

            string baseTitle = _i18n.Get("ui.title");

            if (_selectedMod == null)
            {
                float baseW  = font.MeasureString(baseTitle).X;
                float titleX = xPositionOnScreen + (width - baseW) / 2f;
                Utility.drawTextWithShadow(b, baseTitle, font, new Vector2(titleX, titleY), Game1.textColor);
                return;
            }

            const string separator = "  /  ";
            float baseW2    = font.MeasureString(baseTitle).X;
            float sepW      = font.MeasureString(separator).X;
            float modBudget = maxWidth - baseW2 - sepW;
            string modName  = TruncateWithEllipsis(_selectedMod.UniqueId, font, Math.Max(1, (int)modBudget));
            float modW      = font.MeasureString(modName).X;

            float totalW    = baseW2 + sepW + modW;
            float startX    = xPositionOnScreen + (width - totalW) / 2f;

            Utility.drawTextWithShadow(b, baseTitle, font, new Vector2(startX,                 titleY), Game1.textColor);
            Utility.drawTextWithShadow(b, separator, font, new Vector2(startX + baseW2,        titleY), Game1.textColor * 0.45f);
            Utility.drawTextWithShadow(b, modName,   font, new Vector2(startX + baseW2 + sepW, titleY), Game1.textColor * 0.75f);
        }

        private void DrawSearchBar(SpriteBatch b)
        {
            var  r       = _searchBarBounds;
            bool hovered = r.Contains(Game1.getMouseX(), Game1.getMouseY());

            // Background — slightly lighter when active, subtle when idle.
            Color bgColor = Selected
                ? new Color(245, 235, 210) * 0.95f
                : new Color(220, 200, 160) * 0.55f;
            b.Draw(Game1.fadeToBlackRect, r, bgColor);

            // Border — accent-tinted when active, muted otherwise.
            Color borderColor = Selected ? _accentColor : DividerColor * 0.6f;
            int   bt          = 2;
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X,          r.Y,           r.Width, bt),         borderColor);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X,          r.Bottom - bt, r.Width, bt),         borderColor);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X,          r.Y,           bt,       r.Height),  borderColor);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.Right - bt, r.Y,           bt,       r.Height),  borderColor);

            // Magnifier icon (using the Stardew cursors sprite).
            const int   iconSize   = 20;
            var         iconSrc    = new Rectangle(80, 0, 13, 13); // magnifier in mouseCursors
            float       iconScale  = iconSize / 13f;
            int         iconX      = r.X + 8;
            int         iconY      = r.Y + (r.Height - iconSize) / 2;
            b.Draw(Game1.mouseCursors, new Rectangle(iconX, iconY, iconSize, iconSize), iconSrc, Color.White * 0.7f);

            // Text / placeholder.
            int   textX    = iconX + iconSize + 6;
            int   textMaxW = r.Right - textX - Padding;
            float textY    = r.Y + (r.Height - _smallFontLineH) / 2f;

            if (_searchQuery.Length == 0 && !Selected)
            {
                // Placeholder
                string placeholder = _i18n.Get("ui.search-placeholder");
                string clipped     = TruncateWithEllipsis(placeholder, Game1.smallFont, textMaxW);
                b.DrawString(Game1.smallFont, clipped, new Vector2(textX, textY), Game1.textColor * 0.4f);
            }
            else
            {
                // Typed query + blinking cursor
                string display = TruncateWithEllipsis(_searchQuery, Game1.smallFont, textMaxW - (Selected ? 10 : 0));
                b.DrawString(Game1.smallFont, display, new Vector2(textX, textY), Game1.textColor);

                if (Selected && _cursorVisible)
                {
                    float cursorX = textX + Game1.smallFont.MeasureString(display).X + 1;
                    b.Draw(Game1.fadeToBlackRect,
                        new Rectangle((int)cursorX, (int)textY + 2, 2, _smallFontLineH - 4),
                        Game1.textColor * 0.85f);
                }
            }

            // Clear button — only shown when there is text.
            if (_searchQuery.Length > 0)
            {
                const int clearSize = 16;
                int       clearX    = r.Right - clearSize - 6;
                int       clearY    = r.Y + (r.Height - clearSize) / 2;
                var       clearRect = new Rectangle(clearX, clearY, clearSize, clearSize);
                bool      clearHov  = clearRect.Contains(Game1.getMouseX(), Game1.getMouseY());
                b.Draw(Game1.fadeToBlackRect, clearRect, clearHov ? HoverColor * 0.6f : Color.Transparent);
                // Draw an × using the small font.
                b.DrawString(Game1.smallFont, "×",
                    new Vector2(clearX + 2, clearY + (clearSize - _smallFontLineH) / 2f),
                    clearHov ? Color.White : Game1.textColor * 0.55f);
            }
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

            // "No results" message when the search query matches nothing.
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

                    string displayName = TruncateWithEllipsis(mod.GetName(), Game1.smallFont, itemBounds.Width - 24);
                    var namePos = new Vector2(itemBounds.X + 12, itemBounds.Y + 8);
                    if (selected)
                        b.DrawString(Game1.smallFont, displayName, namePos, Color.White);
                    else
                        Utility.drawTextWithShadow(b, displayName, Game1.smallFont, namePos, Game1.textColor);

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
            for (int i = 0; i < _pages.Count; i++)
            {
                var    page      = _pages[i];
                var    tabBounds = _tabBounds[i];

                bool selected = page == _selectedPage;
                bool hovered  = tabBounds.Contains(Game1.getMouseX(), Game1.getMouseY());

                if (selected)     b.Draw(Game1.fadeToBlackRect, tabBounds, _accentColor);
                else if (hovered) b.Draw(Game1.fadeToBlackRect, tabBounds, HoverColor * 0.3f);
                
                int    innerW    = tabBounds.Width - Padding * 2;
                string fullName  = page.GetPageName();
                string tabLabel  = TruncateWithEllipsis(fullName, Game1.smallFont, innerW);
                float  nameH     = Game1.smallFont.MeasureString(tabLabel).Y;
                Color  txtColor  = selected ? Color.White : Game1.textColor;
                b.DrawString(Game1.smallFont, tabLabel,
                    new Vector2(tabBounds.X + Padding, tabBounds.Y + (TabHeight - nameH) / 2f),
                    txtColor);

                if (hovered && tabLabel != fullName)
                    _tabHoverText = fullName;
            }
        }

        private void DrawContent(SpriteBatch b)
        {
            if (_selectedPage == null) return;

            var savedScissor    = b.GraphicsDevice.ScissorRectangle;
            var savedRasterizer = b.GraphicsDevice.RasterizerState;

            b.End();
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, ScissorRasterizer);
            b.GraphicsDevice.ScissorRectangle = _contentBounds;

            int y    = _contentBounds.Y + Padding - _contentScrollOffset;
            var font = Game1.smallFont;

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

            var entries = _selectedPage.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                int  entryHeight = _entryHeights[i];
                bool visible     = (y + entryHeight) > _contentBounds.Y && y < _contentBounds.Bottom;

                if (visible)
                    DrawEntry(b, entries[i], i, _contentBounds.X + Padding, ref y, _contentBounds.Width - Padding * 2, font);
                else
                    y += entryHeight;

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
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X,         r.Y,          r.Width, t),         _contentBorderColor);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X,         r.Bottom - t, r.Width, t),         _contentBorderColor);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X,         r.Y,          t,        r.Height), _contentBorderColor);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.Right - t, r.Y,          t,        r.Height), _contentBorderColor);
        }

        private void DrawEntry(SpriteBatch b, IDocumentationEntry entry, int entryIndex, int x, ref int y, int maxWidth, SpriteFont font)
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
                        float cx    = drawX;
                        foreach (var seg in line)
                        {
                            if (seg.IsSprite)
                            {
                                var tex = seg.ItemData!.GetTexture();
                                var src = seg.ItemData.GetSourceRect();
                                b.Draw(tex, new Rectangle((int)cx, y, lineH, lineH), src, Color.White);
                                cx += lineH + 2;
                            }
                            else if (!string.IsNullOrEmpty(seg.Text))
                            {
                                b.DrawString(Game1.dialogueFont, seg.Text, new Vector2(cx, y), SectionTitleColor,
                                    rotation: 0f, origin: Vector2.Zero, scale: drawScale,
                                    effects: SpriteEffects.None, layerDepth: 0f);
                                cx += Game1.dialogueFont.MeasureString(seg.Text).X * drawScale;
                            }
                        }
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
                    DrawRichLines(b, lines, font, x, ref y, maxWidth, Game1.textColor, e.Alignment, drawScale, lineH);
                    break;
                }

                case EntryType.Caption:
                {
                    var e = (CaptionEntry)entry;
                    var (drawScale, lineH) = SmallFontParams(e.FontSize, (int)SmallFontNaturalPx);
                    int wrapWidth = ScaledWrapWidth(maxWidth, drawScale);
                    var lines     = InlineParser.WrapRich(e.GetText(), font, wrapWidth, _smallFontLineH);
                    DrawRichLines(b, lines, font, x, ref y, maxWidth, CaptionColor * 0.75f, e.Alignment, drawScale, lineH);
                    break;
                }

                case EntryType.Image:
                {
                    var e       = (ImageEntry)entry;
                    int cachedH = entryIndex >= 0 ? _entryHeights[entryIndex] : MeasureImageHeight(e, maxWidth);

                    if (e.HasFloatLayout)
                        DrawFloatImage(b, e, x, ref y, maxWidth, font, cachedH);
                    else
                    {
                        var tex = e.TryGetTexture();
                        if (tex != null)
                        {
                            var src  = e.SourceRect ?? new Rectangle(0, 0, tex.Width, tex.Height);
                            int dstW = (int)Math.Round(src.Width  * e.Scale);
                            int dstH = (int)Math.Round(src.Height * e.Scale);
                            float drawX = ComputeAlignedX(x, maxWidth, dstW, e.Alignment);
                            b.Draw(tex, new Rectangle((int)drawX, y, dstW, dstH), src, Color.White);
                        }
                        y += cachedH;
                    }
                    break;
                }

                case EntryType.KeyValuePair:
                {
                    var e = (KeyValuePairEntry)entry;
                    var (drawScale, lineH) = SmallFontParams(e.FontSize, (int)SmallFontNaturalPx);

                    string arrow     = ":";
                    float  arrowW    = font.MeasureString(arrow).X * drawScale;
                    float  keyW      = font.MeasureString(e.GetKey()).X * drawScale;
                    float  arrowGap  = 8f * drawScale;
                    float  valX      = x + keyW + arrowGap + arrowW + arrowGap;
                    int    valWidth  = Math.Max(1, (int)(x + maxWidth - valX));
                    int    wrapWidth = ScaledWrapWidth(valWidth, drawScale);

                    b.DrawString(font, e.GetKey(), new Vector2(x, y), KeyColor,
                        0f, Vector2.Zero, drawScale, SpriteEffects.None, 0f);

                    b.DrawString(font, arrow, new Vector2(x + keyW + arrowGap, y), Game1.textColor * 0.5f,
                        0f, Vector2.Zero, drawScale, SpriteEffects.None, 0f);

                    var valLines = InlineParser.WrapRich(e.GetValue(), font, wrapWidth, _smallFontLineH);
                    int valY = y;
                    DrawRichLines(b, valLines, font, (int)valX, ref valY, valWidth, Game1.textColor, Alignment.Left, drawScale, lineH);

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
                            DrawRichLines(b, lines, font, bx, ref y, indented, Game1.textColor, Alignment.Left, drawScale, lineH);
                        }
                        else
                        {
                            float firstLineW = lines.Count > 0 ? InlineSegment.MeasureLineWidth(lines[0], font, _smallFontLineH) * drawScale : 0;
                            float totalW     = ListEntry.BulletIndent * drawScale + firstLineW;
                            float baseX      = ComputeAlignedX(x, maxWidth, totalW, e.Alignment);
                            b.DrawString(font, prefix, new Vector2(baseX, y), Game1.textColor,
                                0f, Vector2.Zero, drawScale, SpriteEffects.None, 0f);
                            int bx = (int)(baseX + ListEntry.BulletIndent * drawScale);
                            DrawRichLines(b, lines, font, bx, ref y, indented, Game1.textColor, Alignment.Left, drawScale, lineH);
                        }

                        if (i < e.Items.Count - 1)
                            y += ListItemGap;
                    }
                    break;
                }

                case EntryType.Spoiler:
                    DrawSpoiler(b, (SpoilerEntry)entry, x, ref y, maxWidth, font);
                    break;

                case EntryType.Row:
                    DrawRow(b, (RowEntry)entry, x, ref y, maxWidth, font);
                    break;

                case EntryType.Link:
                {
                    var e        = (LinkEntry)entry;
                    string label = e.GetLabel();
                    float  tw    = font.MeasureString(label).X;
                    float  drawX = ComputeAlignedX(x, maxWidth, tw, e.Alignment);
                    var    rect  = new Rectangle((int)drawX, y, (int)tw, _smallFontLineH);
                    bool   hov   = rect.Contains(Game1.getMouseX(), Game1.getMouseY());
                    Color  col   = e.IsUrlSafe()
                        ? (hov ? LinkHoverColor : LinkColor)
                        : Color.Gray * 0.6f;
                    Utility.drawTextWithShadow(b, label, font, new Vector2(drawX, y), col);
                    b.Draw(Game1.fadeToBlackRect,
                        new Rectangle((int)drawX, y + _smallFontLineH - 1, (int)tw, 1), col);
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

                    // Grey out if the target mod has no registered documentation
                    bool   exists = false;
                    foreach (var m in _mods)
                        if (string.Equals(m.UniqueId, e.ResolvedModId, StringComparison.OrdinalIgnoreCase))
                            { exists = true; break; }

                    Color col = exists
                        ? (hov ? InternalLinkHoverColor : InternalLinkColor)
                        : Color.Gray * 0.6f;

                    Utility.drawTextWithShadow(b, label, font, new Vector2(drawX, y), col);
                    b.Draw(Game1.fadeToBlackRect,
                        new Rectangle((int)drawX, y + _smallFontLineH - 1, (int)tw, 1), col);

                    if (hov && exists)
                        _tabHoverText = BuildInternalLinkTooltip(e);

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
                            new Rectangle(x + e.IndentAmount / 2 - 1, y, 2, entryIndex >= 0 ? _entryHeights[entryIndex] : MeasureIndentBlockHeight(e, maxWidth)),
                            DividerColor * 0.35f);

                    for (int i = 0; i < e.Children.Count; i++)
                    {
                        DrawEntry(b, e.Children[i], -1, childX, ref y, childWidth, font);
                        if (i < e.Children.Count - 1) y += IndentBlockEntry.ChildGap;
                    }
                    break;
                }
            }
        }

        private void DrawRow(SpriteBatch b, RowEntry e, int x, ref int y, int maxWidth, SpriteFont font)
        {
            int leftW  = (int)Math.Round(maxWidth * e.LeftFraction) - RowEntry.ColumnGap / 2;
            int rightW = maxWidth - leftW - RowEntry.ColumnGap;
            int rightX = x + leftW + RowEntry.ColumnGap;

            int startY = y;
            int leftY  = startY;
            int rightY = startY;

            for (int i = 0; i < e.LeftEntries.Count; i++)
            {
                DrawEntry(b, e.LeftEntries[i], -1, x, ref leftY, leftW, font);
                if (i < e.LeftEntries.Count - 1) leftY += Padding / 2;
            }

            for (int i = 0; i < e.RightEntries.Count; i++)
            {
                DrawEntry(b, e.RightEntries[i], -1, rightX, ref rightY, rightW, font);
                if (i < e.RightEntries.Count - 1) rightY += Padding / 2;
            }

            y = startY + Math.Max(leftY - startY, rightY - startY);
        }

        private void DrawFloatImage(SpriteBatch b, ImageEntry e, int x, ref int y, int maxWidth, SpriteFont font, int cachedH)
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

                if (e.Alignment == Alignment.Left)
                {
                    imgX  = x;
                    listX = x + imgW + ImageEntry.Gutter;
                }
                else
                {
                    imgX  = x + maxWidth - imgW;
                    listX = x;
                }

                b.Draw(tex, new Rectangle(imgX, y, imgW, imgH), src, Color.White);

                int listY = y;
                for (int i = 0; i < e.Items!.Count; i++)
                {
                    Utility.drawTextWithShadow(b, "•", font, new Vector2(listX, listY), Game1.textColor);
                    int bx     = listX + ImageEntry.BulletIndent;
                    var rlines = WrapRich(e.Items[i](), font, indented);
                    DrawRichLines(b, rlines, font, bx, ref listY, indented, Game1.textColor);

                    if (i < e.Items.Count - 1)
                        listY += ListItemGap;
                }
            }

            y += cachedH;
        }

        private void DrawSpoiler(SpriteBatch b, SpoilerEntry e, int x, ref int y, int maxWidth, SpriteFont font)
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
            Utility.drawTextWithShadow(b, label, font, new Vector2(x + 8, textY), Color.White);

            y += SpoilerEntry.HeaderHeight;

            if (e.IsRevealed)
            {
                int innerW   = maxWidth - Padding * 2;
                var lines    = WrapRich(e.GetContent(), font, innerW);
                int lineH    = _smallFontLineH + 2;
                int contentH = 4 + lines.Count * lineH;

                b.Draw(Game1.fadeToBlackRect,
                    new Rectangle(x, y, maxWidth, contentH),
                    new Color(245, 230, 200) * 0.6f);

                int ty = y + 4;
                DrawRichLines(b, lines, font, x + Padding, ref ty, innerW, Game1.textColor);

                y += contentH;
            }
        }

        private static float ComputeAlignedX(int areaX, int areaWidth, float elementWidth, Alignment alignment)
        {
            return alignment switch
            {
                Alignment.Center => areaX + (areaWidth - elementWidth) / 2f,
                Alignment.Right  => areaX + areaWidth - elementWidth,
                _                => areaX
            };
        }

        private void DrawDivider(SpriteBatch b, DividerStyle style, int x, int y, int maxWidth)
        {
            int centerY = y + 6;

            switch (style)
            {
                case DividerStyle.Single:
                default:
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

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }

            if (_scrollUpButton.containsPoint(x, y))  { ScrollContent(-120); Game1.playSound("shiny4"); return; }
            if (_scrollDownButton.containsPoint(x, y)) { ScrollContent( 120); Game1.playSound("shiny4"); return; }

            // ── Search bar ──────────────────────────────────────────────────────
            if (_searchBarBounds.Contains(x, y))
            {
                // Check clear button first (it sits inside _searchBarBounds).
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

                Selected          = true;
                _cursorBlinkTimer = 0.0;
                _cursorVisible    = true;
                Game1.playSound("smallSelect");
                return;
            }

            // Clicking anywhere outside the search bar deactivates it.
            Selected = false;

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
                    if (_tabBounds[i].Contains(x, y))
                    {
                        if (_pages[i] != _selectedPage)
                        {
                            Game1.playSound("smallSelect");
                            SelectPage(_pages[i]);
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
                    return true;
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
                    if (!link.IsUrlSafe())
                    {
                        Game1.playSound("cancel");
                        return true;
                    }

                    Game1.playSound("smallSelect");
                    string message = _i18n.Get("ui.link-confirm", new { url = link.Url });
                    Game1.activeClickableMenu = new ConfirmationDialog(message, _ =>
                    {
                        link.Open();
                        Game1.activeClickableMenu = this;
                    }, _ =>
                    {
                        Game1.activeClickableMenu = this;
                    });
                    return true;
                }
            }
            else if (entry is InternalLinkEntry internalLink)
            {
                string label = internalLink.GetLabel();
                float  tw    = Game1.smallFont.MeasureString(label).X;
                float  lx    = ComputeAlignedX(ex, ew, tw, internalLink.Alignment);
                var    rect  = new Rectangle((int)lx, ey, (int)tw, _smallFontLineH);
                if (rect.Contains(x, y))
                {
                    NavigateTo(internalLink.ResolvedModId, internalLink.TargetPageId, internalLink.TargetAnchor);
                    return true;
                }
            }
            else if (entry is RowEntry row)
            {
                int leftW  = (int)Math.Round(ew * row.LeftFraction) - RowEntry.ColumnGap / 2;
                int rightW = ew - leftW - RowEntry.ColumnGap;
                int rightX = ex + leftW + RowEntry.ColumnGap;

                int colY = ey;
                for (int i = 0; i < row.LeftEntries.Count; i++)
                {
                    int subH = MeasureEntryHeight(row.LeftEntries[i], leftW);
                    if (HitTestEntry(row.LeftEntries[i], x, y, ex, colY, leftW, subH))
                        return true;
                    colY += subH + Padding / 2;
                }

                colY = ey;
                for (int i = 0; i < row.RightEntries.Count; i++)
                {
                    int subH = MeasureEntryHeight(row.RightEntries[i], rightW);
                    if (HitTestEntry(row.RightEntries[i], x, y, rightX, colY, rightW, subH))
                        return true;
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
                    if (HitTestEntry(indentBlock.Children[i], x, y, childX, colY, childW, subH))
                        return true;
                    colY += subH + IndentBlockEntry.ChildGap;
                }
            }

            return false;
        }

        public override void receiveKeyPress(Keys key)
        {
            // While the search bar is active, suppress all key handling so SMAPI
            // hotkeys and the default Escape-closes-menu behaviour don't fire.
            // Actual text input arrives through the IKeyboardSubscriber methods.
            if (Selected) return;

            base.receiveKeyPress(key);
        }

        // ── IKeyboardSubscriber ───────────────────────────────────────────────

        /// <summary>Called by the engine for printable characters typed while Selected is true.</summary>
        public void RecieveTextInput(char inputChar)
        {
            if (!Selected || char.IsControl(inputChar)) return;
            _searchQuery += inputChar;
            RebuildFilter();
            _cursorBlinkTimer = 0.0;
            _cursorVisible    = true;
        }

        /// <summary>Called by the engine for pasted strings.</summary>
        public void RecieveTextInput(string text)
        {
            if (!Selected || string.IsNullOrEmpty(text)) return;
            foreach (char c in text)
                if (!char.IsControl(c))
                    _searchQuery += c;
            RebuildFilter();
            _cursorBlinkTimer = 0.0;
            _cursorVisible    = true;
        }

        /// <summary>
        /// Called by the engine for control characters: '\b' (backspace), '\r' (enter), '\t' (tab).
        /// </summary>
        public void RecieveCommandInput(char command)
        {
            if (!Selected) return;
            switch (command)
            {
                case '\b': // Backspace
                    if (_searchQuery.Length > 0)
                    {
                        _searchQuery = _searchQuery[..^1];
                        RebuildFilter();
                        _cursorBlinkTimer = 0.0;
                        _cursorVisible    = true;
                    }
                    break;

                case '\r': // Enter — dismiss the bar
                    Selected = false;
                    break;

                case '\x1b': // Escape — clear query first, then dismiss
                    if (_searchQuery.Length > 0)
                    {
                        _searchQuery = string.Empty;
                        RebuildFilter();
                        Game1.playSound("drumkit6");
                    }
                    else
                    {
                        Selected = false;
                    }
                    break;
            }
        }

        /// <summary>Called by the engine for special keys (arrow keys, etc.) — unused.</summary>
        public void RecieveSpecialInput(Keys key) { }

        public override void receiveScrollWheelAction(int direction)
        {
            int mx = Game1.getMouseX(), my = Game1.getMouseY();

            if (_sidebarBounds.Contains(mx, my))
            {
                int maxSidebarScroll = Math.Max(0, _filteredMods.Count * SidebarItemHeight - _sidebarBounds.Height);
                _sidebarScrollOffset = Math.Clamp(_sidebarScrollOffset - direction / 3, 0, maxSidebarScroll);
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

        private List<List<InlineSegment>> WrapRich(string text, SpriteFont font, int maxWidth)
            => InlineParser.WrapRich(text, font, maxWidth, _smallFontLineH);

        private static string TruncateWithEllipsis(string text, SpriteFont font, int maxPixelWidth)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (font.MeasureString(text).X <= maxPixelWidth) return text;

            const string ellipsis    = "…";
            float        ellipsisW   = font.MeasureString(ellipsis).X;
            float        budget      = maxPixelWidth - ellipsisW;

            int lo = 0, hi = text.Length - 1, best = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (font.MeasureString(text.Substring(0, mid + 1)).X <= budget)
                {
                    best = mid + 1;
                    lo   = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return text.Substring(0, best) + ellipsis;
        }

        private string BuildInternalLinkTooltip(InternalLinkEntry e)
        {
            // Find the display name of the target mod (fall back to UniqueID if not in registry)
            string modDisplay = e.ResolvedModId;
            foreach (var m in _mods)
                if (string.Equals(m.UniqueId, e.ResolvedModId, StringComparison.OrdinalIgnoreCase))
                    { modDisplay = m.GetName(); break; }

            // Build a human-readable destination string
            if (e.TargetPageId == null && e.TargetAnchor == null)
                return modDisplay;

            if (e.TargetPageId != null && e.TargetAnchor == null)
                return $"{modDisplay}  ›  {e.TargetPageId}";

            if (e.TargetPageId == null)
                return $"{modDisplay}  ›  #{e.TargetAnchor}";

            return $"{modDisplay}  ›  {e.TargetPageId}  ›  #{e.TargetAnchor}";
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
            int                       lineHOverride = 0)
        {
            int lineH = lineHOverride > 0 ? lineHOverride : _smallFontLineH + 2;

            foreach (var line in lines)
            {
                float lineW = InlineSegment.MeasureLineWidth(line, font, _smallFontLineH) * scale;
                float drawX = ComputeAlignedX(x, maxWidth, lineW, alignment);
                float cx    = drawX;

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
                    else if (!string.IsNullOrEmpty(seg.Text))
                    {
                        b.DrawString(font, seg.Text, new Vector2(cx, y), textColor,
                            0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
                        cx += font.MeasureString(seg.Text).X * scale;
                    }
                }
                y += lineH;
            }
        }

        private static List<string> WrapText(string text, SpriteFont font, int maxWidth)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;

            foreach (var rawLine in text.Split('\n'))
            {
                if (string.IsNullOrEmpty(rawLine)) { lines.Add(""); continue; }

                string[] words   = rawLine.Split(' ');
                string   current = "";

                foreach (var word in words)
                {
                    string candidate = current.Length == 0 ? word : current + " " + word;
                    if (font.MeasureString(candidate).X > maxWidth && current.Length > 0)
                    {
                        lines.Add(current);
                        current = word;
                    }
                    else
                    {
                        current = candidate;
                    }
                }

                if (current.Length > 0)
                    lines.Add(current);
            }

            return lines;
        }
    }
}
