using GenericModDocumentationFramework.Loaders;
using GenericModDocumentationFramework.Menus;
using GenericModDocumentationFramework.Registry;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;

namespace GenericModDocumentationFramework
{
    public class ModEntry : Mod
    {
        private DocumentationRegistry _registry           = null!;
        private ModConfig             _config             = null!;

        /// <summary>True when the Mobile Phone mod is present and GMDF has registered as a phone app.
        /// In this mode the HUD button is suppressed entirely.</summary>
        private bool _mobilePhoneActive = false;

        private const int   IconSrcW      = 47;
        private const int   IconSrcH      = 33;
        private const int   ButtonHeight  = 40;
        private const int   ButtonWidth   = (int)(IconSrcW * (ButtonHeight / (float)IconSrcH));
        private const float CustomScale   = ButtonHeight / (float)IconSrcH;

        private static readonly Rectangle CustomSourceRect      = new(0, 0, IconSrcW, IconSrcH);
        private static readonly Rectangle PlaceholderSourceRect = new(420, 489, 9, 9);
        private const float               PlaceholderScale      = ButtonHeight / 9f;

        private Texture2D?                _buttonTexture;
        private Rectangle                 _buttonSourceRect;
        private float                     _buttonScale;
        private ClickableTextureComponent _hudButton = null!;

        public override void Entry(IModHelper helper)
        {
            _registry = new DocumentationRegistry();
            _config   = helper.ReadConfig<ModConfig>();

            helper.Events.Input.ButtonsChanged  += OnButtonsChanged;
            helper.Events.Input.ButtonPressed   += OnButtonPressed;
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded   += OnSaveLoaded;
            helper.Events.Display.RenderedHud   += OnRenderedHud;
            helper.Events.Display.WindowResized += OnWindowResized;
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            RegisterWithGmcm();
            LoadButtonTexture();

            // Mobile Phone integration: register as a phone app instead of using the HUD button.
            // Must be called after LoadButtonTexture() so the texture is ready.
            RegisterWithMobilePhone();

            JsonDocumentationLoader.DiscoverAndLoad(_registry, Helper, Monitor);
        }

        private void RegisterWithMobilePhone()
        {
            var mobileApi = Helper.ModRegistry.GetApi<IMobilePhoneApi>("aedenthorn.MobilePhone");
            if (mobileApi is null) return;

            // Load the dedicated 48x48 phone app icon.
            // Falls back to the HUD button texture (or the mouse-cursor placeholder) if it is missing.
            Texture2D appIcon;
            try
            {
                appIcon = Helper.ModContent.Load<Texture2D>("assets/appIcon.png");
                Monitor.Log("Loaded phone app icon from assets/appIcon.png.", LogLevel.Debug);
            }
            catch
            {
                appIcon = _buttonTexture ?? Game1.mouseCursors;
                Monitor.Log("assets/appIcon.png not found — falling back to HUD button icon for phone app.", LogLevel.Warn);
            }

            bool success = mobileApi.AddApp(
                id:     ModManifest.UniqueID,
                name:   Helper.Translation.Get("hud-button.tooltip"),
                action: OpenDocumentationMenu,
                icon:   appIcon
            );

            if (success)
            {
                _mobilePhoneActive = true;
                Monitor.Log("Registered as a Mobile Phone app. HUD button suppressed.", LogLevel.Info);
            }
            else
            {
                Monitor.Log("Mobile Phone mod found but AddApp() returned false. HUD button will be used instead.", LogLevel.Warn);
            }
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            RebuildHudButton();
        }

        private void OnWindowResized(object? sender, WindowResizedEventArgs e)
        {
            RebuildHudButton();
        }

        private void LoadButtonTexture()
        {
            try
            {
                _buttonTexture    = Helper.ModContent.Load<Texture2D>("assets/GMDFIcon.png");
                _buttonSourceRect = CustomSourceRect;
                _buttonScale      = CustomScale;
                Monitor.Log("Loaded HUD button icon from assets/GMDFIcon.png.", LogLevel.Debug);
            }
            catch
            {
                _buttonTexture    = null;
                _buttonSourceRect = PlaceholderSourceRect;
                _buttonScale      = PlaceholderScale;
                Monitor.Log("assets/GMDFIcon.png not found — using placeholder icon.", LogLevel.Warn);
            }
        }

        private void RebuildHudButton()
        {
            int drawnW = _buttonTexture != null ? ButtonWidth  : (int)(9 * PlaceholderScale);
            int drawnH = _buttonTexture != null ? ButtonHeight : (int)(9 * PlaceholderScale);

            int buttonX = GetHudButtonX(drawnW);

            _hudButton = new ClickableTextureComponent(
                bounds:     new Rectangle(buttonX, 16, drawnW, drawnH),
                texture:    _buttonTexture ?? Game1.mouseCursors,
                sourceRect: _buttonSourceRect,
                scale:      _buttonScale
            )
            {
                hoverText = Helper.Translation.Get("hud-button.tooltip")
            };
        }

        private int GetHudButtonX(int buttonWidth)
        {
            if (Game1.currentLocation is MineShaft mine)
            {
                int level = mine.mineLevel;
                string levelStr = level.ToString();
                int textWidth = (int)Game1.smallFont.MeasureString(levelStr).X;

                return 8 + 8 + textWidth + 8 + 32;
            }

            return 16;
        }

        private void RegisterWithGmcm()
        {
            var gmcm = Helper.ModRegistry.GetApi<IGmcmApi>("spacechase0.GenericModConfigMenu");
            if (gmcm is null) return;

            gmcm.Register(
                mod:   ModManifest,
                reset: () => _config = new ModConfig(),
                save:  () => Helper.WriteConfig(_config)
            );

            gmcm.AddKeybindList(
                mod:      ModManifest,
                getValue: () => _config.OpenMenuKey,
                setValue: value => _config.OpenMenuKey = value,
                name:     () => Helper.Translation.Get("gmcm.keybind-name"),
                tooltip:  () => Helper.Translation.Get("gmcm.keybind-tooltip")
            );

            gmcm.AddBoolOption(
                mod:      ModManifest,
                getValue: () => _config.ShowHudButton,
                setValue: value => _config.ShowHudButton = value,
                name:     () => Helper.Translation.Get("gmcm.show-button-name"),
                tooltip:  () => Helper.Translation.Get("gmcm.show-button-tooltip")
            );

            // ── Appearance ───────────────────────────────────────────────────────
            gmcm.AddSectionTitle(
                mod:  ModManifest,
                text: () => Helper.Translation.Get("gmcm.theme-section")
            );

            gmcm.AddTextOption(
                mod:      ModManifest,
                getValue: () => _config.AccentColor,
                setValue: v  => _config.AccentColor = v,
                name:     () => Helper.Translation.Get("gmcm.accent-color-name"),
                tooltip:  () => Helper.Translation.Get("gmcm.accent-color-tooltip")
            );

            gmcm.AddTextOption(
                mod:      ModManifest,
                getValue: () => _config.ContentBorderColor,
                setValue: v  => _config.ContentBorderColor = v,
                name:     () => Helper.Translation.Get("gmcm.border-color-name"),
                tooltip:  () => Helper.Translation.Get("gmcm.border-color-tooltip")
            );

            gmcm.AddTextOption(
                mod:      ModManifest,
                getValue: () => _config.ScrollBarColor,
                setValue: v  => _config.ScrollBarColor = v,
                name:     () => Helper.Translation.Get("gmcm.scrollbar-color-name"),
                tooltip:  () => Helper.Translation.Get("gmcm.scrollbar-color-tooltip")
            );
        }

        private void OnButtonsChanged(object? sender, ButtonsChangedEventArgs e)
        {
            if (!Context.IsPlayerFree) return;
            if (_config.OpenMenuKey.JustPressed())
                OpenDocumentationMenu();
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!IsHudButtonVisible()) return;
            if (e.Button != SButton.MouseLeft) return;

            var mousePos = Game1.getMousePosition(ui_scale: true);
            if (_hudButton.containsPoint(mousePos.X, mousePos.Y))
            {
                Helper.Input.Suppress(SButton.MouseLeft);
                OpenDocumentationMenu();
            }
        }

        private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
        {
            if (!IsHudButtonVisible()) return;
            if (_hudButton is null) return;

            int drawnW = _hudButton.bounds.Width;
            int buttonX = GetHudButtonX(drawnW);
            _hudButton.bounds = new Rectangle(buttonX, _hudButton.bounds.Y, drawnW, _hudButton.bounds.Height);

            var mousePos = Game1.getMousePosition(ui_scale: true);
            bool hovered = _hudButton.containsPoint(mousePos.X, mousePos.Y);

            if (hovered)
            {
                e.SpriteBatch.Draw(
                    Game1.fadeToBlackRect,
                    new Rectangle(
                        _hudButton.bounds.X - 4, _hudButton.bounds.Y - 4,
                        _hudButton.bounds.Width + 8, _hudButton.bounds.Height + 8),
                    Color.White * 0.25f);
            }

            _hudButton.draw(e.SpriteBatch);

            if (hovered && !string.IsNullOrEmpty(_hudButton.hoverText))
                IClickableMenu.drawHoverText(e.SpriteBatch, _hudButton.hoverText, Game1.smallFont);
        }

        private bool IsHudButtonVisible()
        {
            // If we're registered as a Mobile Phone app, never show the HUD button.
            if (_mobilePhoneActive) return false;

            return _config.ShowHudButton
                && Context.IsPlayerFree
                && !Game1.eventUp
                && Game1.currentMinigame == null
                && !Game1.freezeControls
                && Game1.activeClickableMenu == null;
        }

        private void OpenDocumentationMenu()
        {
            if (!_registry.HasAnyMods)
                Monitor.Log("No mods have registered documentation yet.", LogLevel.Info);

            var mods = _registry.GetAllMods();
            Game1.activeClickableMenu = new DocumentationMenu(mods, Helper.Translation, _config);
            Game1.playSound("bigSelect");
        }
    }
}
