using Rhino.PlugIns;
using Rhino.UI;

namespace RhinoCC
{
    public class RhinoCCPlugin : PlugIn
    {
        public static RhinoCCPlugin Instance { get; private set; }

        public RhinoCCPlugin()
        {
            Instance = this;
        }

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            using (var bmp = new System.Drawing.Bitmap(32, 32))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.Clear(System.Drawing.Color.FromArgb(40, 40, 40));
                    g.DrawString("CC", new System.Drawing.Font("Arial", 12, System.Drawing.FontStyle.Bold), System.Drawing.Brushes.White, 2, 7);
                }
                var icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
                Panels.RegisterPanel(this, typeof(Chat.ChatPanel), "RhinoCC Agent", icon);
            }
            return LoadReturnCode.Success;
        }
    }
}
