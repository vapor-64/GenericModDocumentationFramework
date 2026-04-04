using System;
using Microsoft.Xna.Framework.Graphics;

namespace GenericModDocumentationFramework
{
    public interface IMobilePhoneApi
    {
        bool AddApp(string id, string name, Action action, Texture2D icon);

        bool GetPhoneOpened();
        bool GetAppRunning();
    }
}
