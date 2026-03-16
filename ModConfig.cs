using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace GenericModDocumentationFramework
{
    public class ModConfig
    {

        public KeybindList OpenMenuKey { get; set; } = KeybindList.Parse("F2");


        public bool ShowHudButton { get; set; } = true;
    }
}
